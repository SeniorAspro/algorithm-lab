using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Conway's Game of Life on a texture.
/// Drop this on an empty GameObject in a scene with an orthographic Main Camera and press Play.
///
/// Rules (each step, for every cell, based on its 8 neighbors):
///   - A live cell with 2 or 3 live neighbors survives.
///   - A dead cell with exactly 3 live neighbors becomes alive.
///   - Every other cell dies or stays dead.
/// </summary>
public class GameOfLife : MonoBehaviour
{
    [Header("Grid")]
    [SerializeField] int width = 160;
    [SerializeField] int height = 90;
    [Tooltip("If on, the left edge connects to the right and the top to the bottom (a torus).")]
    [SerializeField] bool wrapEdges = true;

    [Header("Simulation")]
    [SerializeField, Range(1f, 60f)] float stepsPerSecond = 12f;
    [SerializeField, Range(0f, 1f)] float randomFill = 0.25f;
    [SerializeField] bool startRandom = true;

    [Header("Painting")]
    [Tooltip("0 = single cell, 1 = 3x3, 2 = 5x5 ...")]
    [SerializeField, Range(0, 5)] int brushRadius = 0;

    [Header("Colors")]
    [SerializeField] Color aliveColor = new Color(0.98f, 0.82f, 0.35f);
    [SerializeField] Color deadColor = new Color(0.06f, 0.06f, 0.09f);
    [Tooltip("Color of cells that died recently. Fades toward the dead color.")]
    [SerializeField] Color trailColor = new Color(0.45f, 0.18f, 0.40f);
    [SerializeField, Range(0f, 0.99f)] float trailDecay = 0.85f;

    bool[,] cells;
    bool[,] next;
    float[,] trail;          // 1 = just died, fades to 0
    Color32[] pixels;
    Texture2D texture;
    Camera cam;

    bool running;
    float stepTimer;
    int generation;
    int population;
    bool dirty;

    // ------------------------------------------------------------------ setup

    void Start()
    {
        cells = new bool[width, height];
        next = new bool[width, height];
        trail = new float[width, height];
        pixels = new Color32[width * height];

        texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,   // crisp pixels, no blur
            wrapMode = TextureWrapMode.Clamp
        };

        // 1 pixel = 1 world unit, pivot in the center
        var sprite = Sprite.Create(texture, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 1f);
        var sr = gameObject.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;

        cam = Camera.main;
        FitCamera();

        if (startRandom) Randomize();
        dirty = true;
    }

    void FitCamera()
    {
        if (cam == null) return;
        cam.orthographic = true;
        cam.backgroundColor = deadColor;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.transform.position = new Vector3(transform.position.x, transform.position.y, -10f);

        float halfH = height / 2f;
        float halfWAsH = (width / 2f) / cam.aspect;
        cam.orthographicSize = Mathf.Max(halfH, halfWAsH) * 1.05f;
    }

    // ------------------------------------------------------------------ loop

    void Update()
    {
        HandleKeys();
        HandlePainting();

        if (running)
        {
            stepTimer += Time.deltaTime;
            float interval = 1f / stepsPerSecond;
            // Catch up if a frame was slow, but cap it so we never freeze.
            int safety = 0;
            while (stepTimer >= interval && safety++ < 10)
            {
                stepTimer -= interval;
                Step();
            }
        }

        if (dirty) Redraw();
    }

    void Step()
    {
        int pop = 0;

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                int n = CountNeighbors(x, y);
                bool alive = cells[x, y];
                bool nextAlive = alive ? (n == 2 || n == 3) : (n == 3);

                next[x, y] = nextAlive;

                if (alive && !nextAlive) trail[x, y] = 1f;   // just died → leave a ghost
                else trail[x, y] *= trailDecay;

                if (nextAlive) pop++;
            }
        }

        // Swap buffers instead of copying: we must read the OLD grid while writing the new one.
        (cells, next) = (next, cells);

        generation++;
        population = pop;
        dirty = true;
    }

    int CountNeighbors(int cx, int cy)
    {
        int count = 0;
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;

                int x = cx + dx;
                int y = cy + dy;

                if (wrapEdges)
                {
                    x = (x + width) % width;
                    y = (y + height) % height;
                }
                else if (x < 0 || x >= width || y < 0 || y >= height)
                {
                    continue;
                }

                if (cells[x, y]) count++;
            }
        }
        return count;
    }

    void Redraw()
    {
        Color32 alive = aliveColor;
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                pixels[y * width + x] = cells[x, y]
                    ? alive
                    : (Color32)Color.Lerp(deadColor, trailColor, trail[x, y]);
            }
        }
        texture.SetPixels32(pixels);
        texture.Apply(false);
        dirty = false;
    }

    // ------------------------------------------------------------------ actions

    void Randomize()
    {
        population = 0;
        for (int x = 0; x < width; x++)
            for (int y = 0; y < height; y++)
            {
                cells[x, y] = Random.value < randomFill;
                trail[x, y] = 0f;
                if (cells[x, y]) population++;
            }
        generation = 0;
        dirty = true;
    }

    void Clear()
    {
        System.Array.Clear(cells, 0, cells.Length);
        System.Array.Clear(trail, 0, trail.Length);
        generation = 0;
        population = 0;
        dirty = true;
    }

    void Paint(int cx, int cy, bool value)
    {
        for (int dx = -brushRadius; dx <= brushRadius; dx++)
            for (int dy = -brushRadius; dy <= brushRadius; dy++)
            {
                int x = cx + dx, y = cy + dy;
                if (x < 0 || x >= width || y < 0 || y >= height) continue;
                if (cells[x, y] != value) population += value ? 1 : -1;
                cells[x, y] = value;
            }
        dirty = true;
    }

    void HandleKeys()
    {
        if (PressedPlayPause) running = !running;
        if (PressedStep && !running) Step();
        if (PressedRandom) Randomize();
        if (PressedClear) Clear();
        if (PressedFaster) stepsPerSecond = Mathf.Min(60f, stepsPerSecond + 2f);
        if (PressedSlower) stepsPerSecond = Mathf.Max(1f, stepsPerSecond - 2f);
        if (PressedWrap) wrapEdges = !wrapEdges;
    }

    void HandlePainting()
    {
        if (cam == null) return;
        bool draw = LeftHeld, erase = RightHeld;
        if (!draw && !erase) return;

        Vector3 world = cam.ScreenToWorldPoint(MousePosition);
        Vector3 local = world - transform.position;
        int x = Mathf.FloorToInt(local.x + width / 2f);
        int y = Mathf.FloorToInt(local.y + height / 2f);
        if (x < 0 || x >= width || y < 0 || y >= height) return;

        Paint(x, y, draw);
    }

    // ------------------------------------------------------------------ HUD

    void OnGUI()
    {
        var style = new GUIStyle(GUI.skin.label) { fontSize = 16 };
        style.normal.textColor = Color.white;

        GUILayout.BeginArea(new Rect(12, 8, 600, 200));
        GUILayout.Label($"{(running ? "▶ Running" : "❚❚ Paused")}   Gen {generation}   Pop {population}   Speed {stepsPerSecond:0}/s   Wrap {(wrapEdges ? "on" : "off")}", style);
        GUILayout.Label("Space play/pause · N step · R random · C clear · +/- speed · W wrap · LMB draw · RMB erase", style);
        GUILayout.EndArea();
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
    Vector3 MousePosition => Mouse.current != null ? (Vector3)Mouse.current.position.ReadValue() : Vector3.zero;
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
    Vector3 MousePosition => Input.mousePosition;
#endif
}
