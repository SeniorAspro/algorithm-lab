using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// GPU version of the Game of Life. Same rules as GameOfLife.cs, but every cell
/// is computed in parallel by Life.compute, so the grid can be millions of cells.
///
/// Setup: put this on an empty GameObject and drag Life.compute into the Shader slot.
/// It draws itself over the screen, so no Quad or material is needed.
/// </summary>
public class GpuLife : MonoBehaviour
{
    [SerializeField] ComputeShader shader;

    [Header("Grid")]
    [SerializeField] int width = 1024;
    [SerializeField] int height = 576;
    [SerializeField] bool wrapEdges = true;

    [Header("Simulation")]
    [Tooltip("Generations computed per rendered frame.")]
    [SerializeField, Range(1, 32)] int stepsPerFrame = 1;
    [SerializeField, Range(0f, 1f)] float randomFill = 0.25f;

    [Header("Painting")]
    [SerializeField, Range(0, 30)] int brushRadius = 3;

    [Header("Colors")]
    [SerializeField] Color aliveColor = new Color(0.98f, 0.82f, 0.35f);
    [SerializeField] Color deadColor = new Color(0.06f, 0.06f, 0.09f);
    [SerializeField] Color trailColor = new Color(0.45f, 0.18f, 0.40f);
    [SerializeField, Range(0f, 0.99f)] float trailDecay = 0.9f;

    RenderTexture a, b, display;   // a = current state, b = scratch, display = colored output
    int initK, clearK, stepK, paintK, colorK;
    int groupsX, groupsY;

    bool running = true;
    long generation;

    // ------------------------------------------------------------------ setup

    void Start()
    {
        if (shader == null)
        {
            Debug.LogError("GpuLife: assign Life.compute to the Shader field.");
            enabled = false;
            return;
        }
        if (!SystemInfo.supportsComputeShaders)
        {
            Debug.LogError("GpuLife: this GPU / graphics API doesn't support compute shaders.");
            enabled = false;
            return;
        }

        a = MakeTexture();
        b = MakeTexture();
        display = MakeTexture();

        initK = shader.FindKernel("Init");
        clearK = shader.FindKernel("Clear");
        stepK = shader.FindKernel("Step");
        paintK = shader.FindKernel("Paint");
        colorK = shader.FindKernel("Colorize");

        groupsX = Mathf.CeilToInt(width / 8f);
        groupsY = Mathf.CeilToInt(height / 8f);

        shader.SetInt("Width", width);
        shader.SetInt("Height", height);

        Randomize();
    }

    RenderTexture MakeTexture()
    {
        var rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
        {
            enableRandomWrite = true,       // lets the compute shader write into it
            filterMode = FilterMode.Point,  // crisp cells
            wrapMode = TextureWrapMode.Clamp
        };
        rt.Create();
        return rt;
    }

    // ------------------------------------------------------------------ loop

    void Update()
    {
        HandleKeys();
        HandlePainting();

        if (running)
            for (int i = 0; i < stepsPerFrame; i++) Step();

        Colorize();
    }

    void Step()
    {
        shader.SetInt("Wrap", wrapEdges ? 1 : 0);
        shader.SetFloat("TrailDecay", trailDecay);
        shader.SetTexture(stepK, "Prev", a);
        shader.SetTexture(stepK, "Next", b);
        shader.Dispatch(stepK, groupsX, groupsY, 1);
        (a, b) = (b, a);   // ping-pong: the new state becomes the current one
        generation++;
    }

    void Colorize()
    {
        shader.SetVector("AliveColor", ToShader(aliveColor));
        shader.SetVector("DeadColor", ToShader(deadColor));
        shader.SetVector("TrailColor", ToShader(trailColor));
        shader.SetTexture(colorK, "Prev", a);
        shader.SetTexture(colorK, "Display", display);
        shader.Dispatch(colorK, groupsX, groupsY, 1);
    }

    // The display texture is linear, so colors need converting in a Linear-space project.
    static Vector4 ToShader(Color c) =>
        QualitySettings.activeColorSpace == ColorSpace.Linear ? c.linear : c;

    // ------------------------------------------------------------------ actions

    void Randomize()
    {
        shader.SetFloat("Seed", Random.value * 1000f);
        shader.SetFloat("Fill", randomFill);
        shader.SetTexture(initK, "Next", a);
        shader.Dispatch(initK, groupsX, groupsY, 1);
        generation = 0;
    }

    void Clear()
    {
        shader.SetTexture(clearK, "Next", a);
        shader.Dispatch(clearK, groupsX, groupsY, 1);
        generation = 0;
    }

    void Paint(int x, int y, bool draw)
    {
        shader.SetInts("BrushPos", x, y);
        shader.SetInt("BrushRadius", brushRadius);
        shader.SetFloat("BrushValue", draw ? 1f : 0f);
        shader.SetTexture(paintK, "Next", a);
        shader.Dispatch(paintK, groupsX, groupsY, 1);
    }

    void HandleKeys()
    {
        if (PressedPlayPause) running = !running;
        if (PressedStep && !running) Step();
        if (PressedRandom) Randomize();
        if (PressedClear) Clear();
        if (PressedFaster) stepsPerFrame = Mathf.Min(32, stepsPerFrame * 2);
        if (PressedSlower) stepsPerFrame = Mathf.Max(1, stepsPerFrame / 2);
        if (PressedWrap) wrapEdges = !wrapEdges;
    }

    void HandlePainting()
    {
        bool draw = LeftHeld, erase = RightHeld;
        if (!draw && !erase) return;

        GetViewRect(out float left, out float bottom, out float scale);
        Vector2 m = MousePosition;   // screen coords, origin bottom-left
        int x = Mathf.FloorToInt((m.x - left) / scale);
        int y = Mathf.FloorToInt((m.y - bottom) / scale);
        if (x < 0 || x >= width || y < 0 || y >= height) return;

        Paint(x, y, draw);
    }

    // Fit the grid to the screen, keeping its aspect ratio, centered.
    void GetViewRect(out float left, out float bottom, out float scale)
    {
        scale = Mathf.Min(Screen.width / (float)width, Screen.height / (float)height);
        left = (Screen.width - width * scale) / 2f;
        bottom = (Screen.height - height * scale) / 2f;
    }

    // ------------------------------------------------------------------ drawing + HUD

    void OnGUI()
    {
        if (display == null) return;

        if (Event.current.type == EventType.Repaint)
        {
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.blackTexture);
            GetViewRect(out float left, out float bottom, out float scale);
            // GUI rects are measured from the top, but the view is centered so top margin == bottom margin.
            GUI.DrawTexture(new Rect(left, bottom, width * scale, height * scale), display);
        }

        var style = new GUIStyle(GUI.skin.label) { fontSize = 16 };
        style.normal.textColor = Color.white;

        GUILayout.BeginArea(new Rect(12, 8, 900, 200));
        GUILayout.Label($"GPU  {width}×{height} = {width * height:N0} cells   " +
                        $"{(running ? "▶" : "❚❚")}   Gen {generation:N0}   " +
                        $"{stepsPerFrame} step/frame   FPS {1f / Mathf.Max(Time.smoothDeltaTime, 0.0001f):0}   " +
                        $"Wrap {(wrapEdges ? "on" : "off")}", style);
        GUILayout.Label("Space play/pause · N step · R random · C clear · +/- speed · W wrap · LMB draw · RMB erase", style);
        GUILayout.EndArea();
    }

    void OnDestroy()
    {
        if (a != null) a.Release();
        if (b != null) b.Release();
        if (display != null) display.Release();
    }

    // ------------------------------------------------------------------ input
    // Works with both the new Input System (Unity 6 default) and the old Input Manager.

#if ENABLE_INPUT_SYSTEM
    static bool Down(Key k) => Keyboard.current != null && Keyboard.current[k].wasPressedThisFrame;
    bool PressedPlayPause => Down(Key.Space);
    bool PressedStep => Down(Key.N);
    bool PressedRandom => Down(Key.R);
    bool PressedClear => Down(Key.C);
    bool PressedFaster => Down(Key.Equals) || Down(Key.NumpadPlus);
    bool PressedSlower => Down(Key.Minus) || Down(Key.NumpadMinus);
    bool PressedWrap => Down(Key.W);
    bool LeftHeld => Mouse.current != null && Mouse.current.leftButton.isPressed;
    bool RightHeld => Mouse.current != null && Mouse.current.rightButton.isPressed;
    Vector2 MousePosition => Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
#else
    bool PressedPlayPause => Input.GetKeyDown(KeyCode.Space);
    bool PressedStep => Input.GetKeyDown(KeyCode.N);
    bool PressedRandom => Input.GetKeyDown(KeyCode.R);
    bool PressedClear => Input.GetKeyDown(KeyCode.C);
    bool PressedFaster => Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.KeypadPlus);
    bool PressedSlower => Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus);
    bool PressedWrap => Input.GetKeyDown(KeyCode.W);
    bool LeftHeld => Input.GetMouseButton(0);
    bool RightHeld => Input.GetMouseButton(1);
    Vector2 MousePosition => Input.mousePosition;
#endif
}
