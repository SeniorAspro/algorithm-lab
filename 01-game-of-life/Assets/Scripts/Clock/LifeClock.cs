using System.IO;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Loads a big RLE pattern (like a digital clock built entirely out of Life) and runs it on the GPU,
/// with zoom and pan. The pattern file goes in Assets/StreamingAssets/.
///
/// Setup: empty GameObject → add LifeClock → drag LifeLarge.compute into the Shader field.
/// </summary>
public class LifeClock : MonoBehaviour
{
    [SerializeField] ComputeShader shader;
    [Tooltip("File name inside Assets/StreamingAssets/")]
    [SerializeField] string patternFile = "clockMini.rle";
    [Tooltip("Empty cells added around the pattern.")]
    [SerializeField] int padding = 64;

    [Header("Speed")]
    [Tooltip("clockMini.rle advances one displayed minute every 2880 generations, so 48 gen/s runs it in real time.")]
    [SerializeField] float realTimeGensPerSecond = 48f;
    [SerializeField] int maxStepsPerFrame = 512;

    [Header("Colors")]
    [SerializeField] Color aliveColor = new Color(0.98f, 0.82f, 0.35f);
    [SerializeField] Color deadColor = new Color(0.06f, 0.06f, 0.09f);
    [SerializeField] Color trailColor = new Color(0.45f, 0.18f, 0.40f);
    [SerializeField] Color outsideColor = new Color(0.02f, 0.02f, 0.03f);
    [SerializeField, Range(0f, 0.99f)] float trailDecay = 0.8f;

    RenderTexture a, b, display;
    int loadK, stepK, viewK;
    int width, height;

    float gensPerSecond;
    float stepBudget;
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

    string error;

    // ------------------------------------------------------------------ setup

    void Start()
    {
        gensPerSecond = realTimeGensPerSecond;

        if (shader == null) { error = "Assign LifeLarge.compute to the Shader field."; return; }
        if (!SystemInfo.supportsComputeShaders) { error = "This GPU / graphics API doesn't support compute shaders."; return; }

        string path = Path.Combine(Application.streamingAssetsPath, patternFile);
        if (!File.Exists(path))
        {
            error = $"Pattern not found:\n{path}\n\nDownload clockMini.rle (see this project's README) and put it in Assets/StreamingAssets/.";
            return;
        }

        byte[] cells;
        int srcW, srcH;
        try
        {
            cells = RleLoader.Parse(File.ReadAllText(path), out srcW, out srcH);
        }
        catch (System.Exception e)
        {
            error = "Couldn't read the pattern: " + e.Message;
            return;
        }

        width = srcW + padding * 2;
        height = srcH + padding * 2;

        int max = SystemInfo.maxTextureSize;
        if (width > max || height > max)
        {
            error = $"Pattern is {width}×{height}, but this GPU's max texture size is {max}.";
            return;
        }

        // Upload the pattern once as an 8-bit texture, then copy it into the simulation texture on the GPU.
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
        if (error != null) return;

        HandleKeys();
        HandleView();

        if (running)
        {
            stepBudget += gensPerSecond * Time.deltaTime;
            int steps = Mathf.Min((int)stepBudget, maxStepsPerFrame);
            stepBudget -= steps;
            if (stepBudget > maxStepsPerFrame) stepBudget = 0;   // GPU can't keep up: drop the backlog
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

        if (error != null)
        {
            GUI.Label(new Rect(20, 20, Screen.width - 40, Screen.height - 40), error, style);
            return;
        }

        if (Event.current.type == EventType.Repaint && display != null)
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), display);

        float speed = gensPerSecond / realTimeGensPerSecond;
        double clockMinutes = generation / (realTimeGensPerSecond * 60.0);
        float zoom = 1f / cellsPerPixel;

        var bg = new Texture2D(1, 1);
        bg.SetPixel(0, 0, new Color(0, 0, 0, 0.6f));
        bg.Apply();
        GUI.DrawTexture(new Rect(0, 0, Screen.width, 58), bg);
        Destroy(bg);

        GUILayout.BeginArea(new Rect(12, 6, Screen.width - 24, 60));
        GUILayout.Label(
            $"{(running ? "▶" : "❚❚")}  Gen {generation:N0}  ·  clock time +{(int)clockMinutes} min  ·  " +
            $"speed ×{speed:0.##} real time ({measuredGps:N0} gen/s)  ·  {width:N0}×{height:N0} cells  ·  " +
            $"zoom {(zoom >= 1 ? $"{zoom:0.#}×" : $"1/{cellsPerPixel:0.#}")}  ·  FPS {1f / Mathf.Max(Time.smoothDeltaTime, 0.0001f):0}", style);
        GUILayout.Label("Space pause · N step · +/- speed · R real time · F fit · scroll zoom · drag pan", style);
        GUILayout.EndArea();
    }

    void OnDestroy()
    {
        if (a != null) a.Release();
        if (b != null) b.Release();
        if (display != null) display.Release();
    }

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
