using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Loads a big RLE pattern (like a digital clock built entirely out of Life) and runs it on the GPU,
/// with zoom and pan.
///
/// Setup: empty GameObject → add LifeClock → press Play.
/// The shader loads itself from Resources, and the clock pattern downloads itself on first run.
/// </summary>
public class LifeClock : MonoBehaviour
{
    [Tooltip("Optional. If empty, LifeLarge.compute is loaded from a Resources folder.")]
    [SerializeField] ComputeShader shader;

    [Header("Pattern")]
    [SerializeField] string patternFile = "clockMini.rle";
    [Tooltip("Where to download the pattern from if it isn't on disk yet.")]
    [SerializeField] string patternUrl =
        "https://raw.githubusercontent.com/VladanMajerech/ConwayLifeDigitalClocks/master/clockMini.rle";
    [Tooltip("Empty cells added around the pattern.")]
    [SerializeField] int padding = 64;

    [Header("Speed")]
    [Tooltip("clockMini.rle advances one displayed minute every 2880 generations, so 48 gen/s runs it in real time.")]
    [SerializeField] float realTimeGensPerSecond = 48f;
    [Tooltip("Hard limit on generations per frame. The real limit adapts to keep the frame rate above ~30 FPS.")]
    [SerializeField] int maxStepsPerFrame = 256;

    [Header("Colors")]
    [SerializeField] Color aliveColor = new Color(0.98f, 0.82f, 0.35f);
    [SerializeField] Color deadColor = new Color(0.06f, 0.06f, 0.09f);
    [SerializeField] Color trailColor = new Color(0.45f, 0.18f, 0.40f);
    [SerializeField] Color outsideColor = new Color(0.02f, 0.02f, 0.03f);
    [SerializeField, Range(0f, 0.99f)] float trailDecay = 0.8f;

    RenderTexture a, b, display;
    int loadK, stepK, viewK;
    int width, height;
    bool ready;

    float gensPerSecond;
    float stepBudget;
    float adaptiveCap;
    bool running = true;
    long generation;

    // view
    Vector2 viewOrigin;    // grid coords under the bottom-left screen pixel
    float cellsPerPixel = 1f;
    Vector2 lastMouse;

    // stats
    float statTimer;
    long statGens;
    float measuredGps;

    string status;     // shown while loading
    string error;
    Texture2D hudBackground;

    // ------------------------------------------------------------------ setup

    IEnumerator Start()
    {
        gensPerSecond = realTimeGensPerSecond;
        adaptiveCap = maxStepsPerFrame;

        if (shader == null) shader = Resources.Load<ComputeShader>("LifeLarge");
        if (shader == null) { error = "Couldn't find LifeLarge.compute. It should be in a Resources folder, or assigned in the Inspector."; yield break; }
        if (!SystemInfo.supportsComputeShaders) { error = "This GPU / graphics API doesn't support compute shaders."; yield break; }

        // 1. Find the pattern text: StreamingAssets → downloaded copy → download it now.
        string text = null;
        string streaming = Path.Combine(Application.streamingAssetsPath, patternFile);
        string cached = Path.Combine(Application.persistentDataPath, patternFile);

        if (File.Exists(streaming)) text = File.ReadAllText(streaming);
        else if (File.Exists(cached)) text = File.ReadAllText(cached);
        else
        {
            using (var req = UnityWebRequest.Get(patternUrl))
            {
                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    status = $"Downloading {patternFile}… {req.downloadProgress * 100f:0}%";
                    yield return null;
                }

                if (req.result != UnityWebRequest.Result.Success)
                {
                    error = $"Couldn't download the pattern ({req.error}).\n\n" +
                            $"Download it yourself from:\n{patternUrl}\n\nand put it in Assets/StreamingAssets/{patternFile}";
                    yield break;
                }

                text = req.downloadHandler.text;
                try { File.WriteAllText(cached, text); }   // keep it for next time
                catch (System.Exception e) { Debug.LogWarning("Couldn't cache pattern: " + e.Message); }
            }
        }

        status = "Parsing pattern…";
        yield return null;

        // 2. Parse it.
        byte[] cells;
        int srcW, srcH;
        try
        {
            cells = RleLoader.Parse(text, out srcW, out srcH);
        }
        catch (System.Exception e)
        {
            error = "Couldn't read the pattern: " + e.Message;
            yield break;
        }

        width = srcW + padding * 2;
        height = srcH + padding * 2;

        int max = SystemInfo.maxTextureSize;
        if (width > max || height > max)
        {
            error = $"Pattern is {width}×{height}, but this GPU's max texture size is {max}.";
            yield break;
        }

        status = "Uploading to GPU…";
        yield return null;

        // 3. Upload once as an 8-bit texture, then copy into the simulation texture on the GPU.
        var src = new Texture2D(srcW, srcH, TextureFormat.R8, false) { filterMode = FilterMode.Point };
        src.SetPixelData(cells, 0);
        src.Apply(false, true);   // true = free the CPU copy

        a = MakeState();
        b = MakeState();

        loadK = shader.FindKernel("Load");
        stepK = shader.FindKernel("Step");
        viewK = shader.FindKernel("View");

        shader.SetInt("Width", width);
        shader.SetInt("Height", height);
        shader.SetInt("SrcWidth", srcW);
        shader.SetInt("SrcHeight", srcH);
        shader.SetInt("Pad", padding);

        shader.SetTexture(loadK, "Src", src);
        shader.SetTexture(loadK, "Next", a);
        Dispatch(loadK, width, height);

        Destroy(src);

        FitView();
        lastMouse = MousePosition;
        status = null;
        ready = true;
    }

    RenderTexture MakeState()
    {
        // RG16 = 2 bytes per cell. A 7000×7500 grid is ~105 MB per texture instead of ~210 MB with ARGB32.
        var rt = new RenderTexture(width, height, 0, RenderTextureFormat.RG16, RenderTextureReadWrite.Linear)
        {
            enableRandomWrite = true,
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };
        rt.Create();
        return rt;
    }

    void EnsureDisplay()
    {
        if (display != null && display.width == Screen.width && display.height == Screen.height) return;
        if (display != null) display.Release();
        display = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
        {
            enableRandomWrite = true,
            filterMode = FilterMode.Point
        };
        display.Create();
    }

    void Dispatch(int kernel, int w, int h) =>
        shader.Dispatch(kernel, Mathf.CeilToInt(w / 8f), Mathf.CeilToInt(h / 8f), 1);

    // ------------------------------------------------------------------ loop

    void Update()
    {
        if (!ready) return;

        HandleKeys();
        HandleView();

        if (running)
        {
            // If frames get slow, the GPU is the bottleneck: run fewer generations per frame.
            float dt = Time.unscaledDeltaTime;
            if (dt > 1f / 30f) adaptiveCap = Mathf.Max(1f, adaptiveCap * 0.8f);
            else if (dt < 1f / 50f) adaptiveCap = Mathf.Min(maxStepsPerFrame, adaptiveCap * 1.1f + 1f);

            stepBudget += gensPerSecond * Time.deltaTime;
            int steps = Mathf.Min((int)stepBudget, (int)adaptiveCap);
            stepBudget -= steps;
            if (stepBudget > adaptiveCap) stepBudget = 0;   // can't keep up: drop the backlog instead of lagging forever
            for (int i = 0; i < steps; i++) Step();
        }

        Render();
        UpdateStats();
    }

    void Step()
    {
        shader.SetFloat("TrailDecay", trailDecay);
        shader.SetTexture(stepK, "Prev", a);
        shader.SetTexture(stepK, "Next", b);
        Dispatch(stepK, width, height);
        (a, b) = (b, a);
        generation++;
        statGens++;
    }

    void Render()
    {
        EnsureDisplay();
        shader.SetVector("ViewOrigin", viewOrigin);
        shader.SetFloat("CellsPerPixel", cellsPerPixel);
        shader.SetInt("DisplayWidth", display.width);
        shader.SetInt("DisplayHeight", display.height);
        shader.SetVector("AliveColor", ToShader(aliveColor));
        shader.SetVector("DeadColor", ToShader(deadColor));
        shader.SetVector("TrailColor", ToShader(trailColor));
        shader.SetVector("OutsideColor", ToShader(outsideColor));
        shader.SetTexture(viewK, "Prev", a);
        shader.SetTexture(viewK, "Display", display);
        Dispatch(viewK, display.width, display.height);
    }

    static Vector4 ToShader(Color c) =>
        QualitySettings.activeColorSpace == ColorSpace.Linear ? c.linear : c;

    void UpdateStats()
    {
        statTimer += Time.unscaledDeltaTime;
        if (statTimer >= 0.5f)
        {
            measuredGps = statGens / statTimer;
            statGens = 0;
            statTimer = 0;
        }
    }

    // ------------------------------------------------------------------ view

    void FitView()
    {
        cellsPerPixel = Mathf.Max(width / (float)Screen.width, height / (float)Screen.height) * 1.02f;
        Vector2 center = new Vector2(width, height) / 2f;
        viewOrigin = center - new Vector2(Screen.width, Screen.height) / 2f * cellsPerPixel;
    }

    void HandleView()
    {
        Vector2 mouse = MousePosition;

        // Zoom toward the mouse: the cell under the cursor stays under the cursor.
        float scroll = ScrollDelta;
        if (scroll != 0)
        {
            Vector2 cellUnderMouse = viewOrigin + mouse * cellsPerPixel;
            float factor = scroll > 0 ? 1f / 1.25f : 1.25f;
            float maxCpp = Mathf.Max(width / (float)Screen.width, height / (float)Screen.height) * 2f;
            cellsPerPixel = Mathf.Clamp(cellsPerPixel * factor, 1f / 32f, maxCpp);
            viewOrigin = cellUnderMouse - mouse * cellsPerPixel;
        }

        // Drag to pan.
        if (DragHeld)
            viewOrigin -= (mouse - lastMouse) * cellsPerPixel;

        lastMouse = mouse;
    }

    // ------------------------------------------------------------------ input

    void HandleKeys()
    {
        if (PressedPlayPause) running = !running;
        if (PressedStep && !running) Step();
        if (PressedFaster) gensPerSecond = Mathf.Min(gensPerSecond * 2f, maxStepsPerFrame * 240f);
        if (PressedSlower) gensPerSecond = Mathf.Max(gensPerSecond / 2f, 1f);
        if (PressedRealTime) gensPerSecond = realTimeGensPerSecond;
        if (PressedFit) FitView();
    }

    // ------------------------------------------------------------------ drawing + HUD

    void OnGUI()
    {
        var style = new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true };
        style.normal.textColor = Color.white;

        if (error != null || status != null)
        {
            GUI.Label(new Rect(20, 20, Screen.width - 40, Screen.height - 40), error ?? status, style);
            return;
        }
        if (!ready) return;

        if (Event.current.type == EventType.Repaint && display != null)
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), display);

        if (hudBackground == null)
        {
            hudBackground = new Texture2D(1, 1);
            hudBackground.SetPixel(0, 0, new Color(0, 0, 0, 0.6f));
            hudBackground.Apply();
        }
        GUI.DrawTexture(new Rect(0, 0, Screen.width, 58), hudBackground);

        float speed = gensPerSecond / realTimeGensPerSecond;
        double clockMinutes = generation / (realTimeGensPerSecond * 60.0);
        float zoom = 1f / cellsPerPixel;
        string limited = measuredGps < gensPerSecond * 0.9f && running ? " — GPU-limited" : "";

        GUILayout.BeginArea(new Rect(12, 6, Screen.width - 24, 60));
        GUILayout.Label(
            $"{(running ? "▶" : "❚❚")}  Gen {generation:N0}  ·  clock time +{(int)clockMinutes} min  ·  " +
            $"speed ×{speed:0.##} ({measuredGps:N0} gen/s{limited})  ·  {width:N0}×{height:N0} cells  ·  " +
            $"zoom {(zoom >= 1 ? $"{zoom:0.#}×" : $"1/{cellsPerPixel:0.#}")}  ·  FPS {1f / Mathf.Max(Time.smoothDeltaTime, 0.0001f):0}", style);
        GUILayout.Label("Space pause · N step · +/- speed · R real time · F fit · scroll zoom · drag pan", style);
        GUILayout.EndArea();
    }

    void OnDestroy()
    {
        if (a != null) a.Release();
        if (b != null) b.Release();
        if (display != null) display.Release();
        if (hudBackground != null) Destroy(hudBackground);
    }

    // ------------------------------------------------------------------ input bindings
    // Works with both the new Input System (Unity 6 default) and the old Input Manager.

#if ENABLE_INPUT_SYSTEM
    static bool Down(Key k) => Keyboard.current != null && Keyboard.current[k].wasPressedThisFrame;
    bool PressedPlayPause => Down(Key.Space);
    bool PressedStep => Down(Key.N);
    bool PressedFaster => Down(Key.Equals) || Down(Key.NumpadPlus);
    bool PressedSlower => Down(Key.Minus) || Down(Key.NumpadMinus);
    bool PressedRealTime => Down(Key.R);
    bool PressedFit => Down(Key.F);
    bool DragHeld => Mouse.current != null && (Mouse.current.leftButton.isPressed || Mouse.current.middleButton.isPressed);
    float ScrollDelta
    {
        get
        {
            if (Mouse.current == null) return 0;
            float y = Mouse.current.scroll.ReadValue().y;
            return y > 0 ? 1 : y < 0 ? -1 : 0;
        }
    }
    Vector2 MousePosition => Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
#else
    bool PressedPlayPause => Input.GetKeyDown(KeyCode.Space);
    bool PressedStep => Input.GetKeyDown(KeyCode.N);
    bool PressedFaster => Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.KeypadPlus);
    bool PressedSlower => Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus);
    bool PressedRealTime => Input.GetKeyDown(KeyCode.R);
    bool PressedFit => Input.GetKeyDown(KeyCode.F);
    bool DragHeld => Input.GetMouseButton(0) || Input.GetMouseButton(2);
    float ScrollDelta => Input.mouseScrollDelta.y;
    Vector2 MousePosition => Input.mousePosition;
#endif
}
