using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

/// <summary>A walkable surface in world units: y = height, x0..x1 = extent. dx/dy = how far it moved since the last poll.</summary>
public struct Platform
{
    public long id;
    public float x0, x1, y, dx, dy;
}

/// <summary>A window's outline in world units. movable = not maximized, so buddies may nudge it (and it's solid).
/// screen = not a window: a bit of the world on none of your screens (ScreenBounds.Solids), always solid, never moves.</summary>
public struct WindowBox
{
    public long id;
    public float xMin, yMin, xMax, yMax;
    public bool movable, screen;
    public bool Contains(Vector2 p) => p.x >= xMin && p.x <= xMax && p.y >= yMin && p.y <= yMax;
}

/// <summary>A desktop icon you can see (no window over it), world units: the icon picture. bin = the Recycle Bin.</summary>
public struct IconBox
{
    public long id;
    public float xMin, yMin, xMax, yMax;
    public bool bin;
    public Vector2 Center => new Vector2(0.5f * (xMin + xMax), 0.5f * (yMin + yMax));
}

/// <summary>
/// Turns the visible top edges of your open windows into platforms the buddies can land, sit and hop on.
/// Polls Windows ~30x a second (EnumWindows + DWM frame bounds), keeps only the parts of each top edge that aren't
/// covered by windows in front of it, and reports how far each window moved so things standing on it ride along.
/// In the Editor there are no real windows, so a test window is drawn in the Game view instead.
/// It can also nudge a window a few pixels and put it back (BeginMove / MoveTo / EndMove, used by WindowWobble).
/// Desktop icons you can see (DesktopIcons: positions only) are little perches on top of the icon and hooks for the rope
/// (Icons); the Recycle Bin is a trampoline (WindowWobble).
/// With more than one screen, the bits of the world on no screen (ScreenBounds.Solids) go at the back of Windows (flagged
/// screen, ids from ScreenIdBase) and the floors on top of them into Current, so everything that bumps into windows and
/// lands on them bumps into those and lands on those too. Things that are about real windows skip them (IsScreen).
/// </summary>
[DisallowMultipleComponent]
public class DesktopPlatforms : MonoBehaviour
{
    /// <summary>Current platforms (world units). Read by GummyBody every physics step.</summary>
    public static readonly List<Platform> Current = new List<Platform>();
    /// <summary>Increments every time Current is refreshed.</summary>
    public static int Version { get; private set; }
    /// <summary>Window outlines (world units), front-most first. Refreshed with Current.</summary>
    public static readonly List<WindowBox> Windows = new List<WindowBox>();
    /// <summary>Desktop icons you can see (not behind a window). Their perches are in Current too (ids from IconIdBase).</summary>
    public static readonly List<IconBox> Icons = new List<IconBox>();
    public const long IconIdBase = 1L << 48;
    public static bool IsIcon(long id) => id >= IconIdBase;
    /// <summary>Ids of the bits of the world on no screen (ScreenBounds.Solids) and the floors on top of them.</summary>
    public const long ScreenIdBase = 1L << 47;
    public static bool IsScreen(long id) => id >= ScreenIdBase && id < IconIdBase;
    /// <summary>A real window or icon (not a bit of the world on no screen, not the floor).</summary>
    public static bool IsReal(long id) => id != 0 && !IsScreen(id);
    public static bool IsBin(long id)
    {
        foreach (var ic in Icons) if (ic.id == id) return ic.bin;
        return false;
    }
    /// <summary>The icon whose picture contains this point (false if none).</summary>
    public static bool IconAt(Vector2 p, out IconBox icon)
    {
        foreach (var ic in Icons)
            if (p.x >= ic.xMin && p.x <= ic.xMax && p.y >= ic.yMin && p.y <= ic.yMax) { icon = ic; return true; }
        icon = default;
        return false;
    }

    public Camera cam;
    [Tooltip("Seconds between window scans.")]
    public float pollInterval = 1f / 30f;
    [Tooltip("Ignore windows narrower than this (pixels).")]
    public int minWindowWidth = 80;

    [Header("Editor testing (viewport fractions: x0, x1, y)")]
    public List<Vector3> editorPlatforms = new List<Vector3> { new Vector3(0.3f, 0.6f, 0.4f) };
    [Tooltip("Height of the Editor test windows (viewport fraction).")]
    public float editorWindowHeight = 0.2f;

    [Header("Desktop icons")]
    [Tooltip("Buddies stand on your desktop icons and hook their rope on them (only icon positions are read).")]
    public bool useDesktopIcons = true;
    [Tooltip("Editor test icons (viewport fractions: x, y of the middle; z = 1: the Recycle Bin).")]
    public List<Vector3> editorIcons = new List<Vector3>();
    [Tooltip("Size of the Editor test icons (viewport height fraction).")]
    public float editorIconSize = 0.045f;

    float nextPoll;
    readonly Dictionary<long, Vector2> lastPos = new Dictionary<long, Vector2>(); // window top-left (world) last poll
    static float s_pxPerUnit = 108f; // our screen pixels per world unit (for moving windows)

    void Awake()
    {
        if (!cam) cam = Camera.main;
#if UNITY_EDITOR
        BuildEditorLines();
#endif
        if (useDesktopIcons) DesktopIcons.Start();
    }

    void OnDisable()
    {
        Current.Clear();
        Windows.Clear();
        Icons.Clear();
        Version++;
    }

    void OnApplicationQuit() => DesktopIcons.Stop();

    // ------------------------------------------------------------------ desktop icons

    readonly List<DesktopIcons.Raw> rawIcons = new List<DesktopIcons.Raw>();

    /// <summary>What happened to the desktop icons in the last poll, for whipit_perf.txt (counts and positions only).</summary>
    public static string IconReport { get; private set; } = "";

    // icons -> perches (the icon's top, about as wide as its cell) and Icons (for rope hooks); none behind a window.
    // Returns 0: added, 1: off our screens / behind the taskbar, 2: a window is over it (coverer: which one)
    int AddIcon(long id, Vector2 tl, Vector2 br, float cellW, bool bin) => AddIcon(id, tl, br, cellW, bin, out _);
    int AddIcon(long id, Vector2 tl, Vector2 br, float cellW, bool bin, out WindowBox coverer)
    {
        coverer = default;
        var ic = new IconBox { id = id, xMin = Mathf.Min(tl.x, br.x), xMax = Mathf.Max(tl.x, br.x), yMin = Mathf.Min(tl.y, br.y), yMax = Mathf.Max(tl.y, br.y), bin = bin };
        Vector2 c = ic.Center;
        Rect scr = ScreenBounds.ScreenRectAt(c);
        if (c.x < scr.xMin || c.x > scr.xMax || ic.yMin < scr.yMin + 0.05f || ic.yMax > scr.yMax - 0.02f) return 1; // off our screen / behind the taskbar
        foreach (var w in Windows) if (w.Contains(c)) { coverer = w; return 2; } // a window is over it
        Icons.Add(ic);
        float half = Mathf.Max(cellW * 0.5f, (ic.xMax - ic.xMin) * 0.5f); // (about as wide as the icon's cell: room to sit)
        Current.Add(new Platform { id = id, x0 = c.x - half, x1 = c.x + half, y = ic.yMax });
        return 0;
    }


    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("DesktopPlatforms.Update", t0); }
    void UpdateTimed()
    {
        if (!cam || Time.unscaledTime < nextPoll) return;
        nextPoll = Time.unscaledTime + pollInterval;
        if (Screen.height > 0 && cam.orthographicSize > 0f) s_pxPerUnit = Screen.height / (2f * cam.orthographicSize);
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        PollWindows();
#else
        PollEditor();
#endif
        Version++;
    }

    // ------------------------------------------------------------------ more than one screen

    static readonly List<(float x0, float x1, float y, int k)> s_floors = new List<(float x0, float x1, float y, int k)>();

    /// <summary>The bits of the world on no screen: solid, at the back of Windows; the floors of the screens (on top of
    /// those, or right above another screen): in Current.</summary>
    static void AddScreens()
    {
        var solids = ScreenBounds.Solids;
        if (solids.Count == 0) return;
        for (int k = 0; k < solids.Count; k++)
        {
            Rect r = solids[k];
            Windows.Add(new WindowBox { id = ScreenIdBase + k, xMin = r.xMin, yMin = r.yMin, xMax = r.xMax, yMax = r.yMax, movable = true, screen = true });
        }
        ScreenBounds.Floors(s_floors);
        foreach (var f in s_floors) Current.Add(new Platform { id = f.k >= 0 ? ScreenIdBase + f.k : ScreenIdBase + (1L << 20) - 2 - f.k, x0 = f.x0, x1 = f.x1, y = f.y });
    }

    // ------------------------------------------------------------------ geometry (shared, testable)

    public struct WinRect
    {
        public long id;
        public int left, top, right, bottom;
    }

    /// <summary>
    /// For windows sorted front-to-back, return the parts of each window's top edge that no window in front of it
    /// covers. Output: (index into wins, x0, x1) in the same pixel space.
    /// </summary>
    public static void VisibleTopEdges(List<WinRect> wins, List<(int win, int x0, int x1)> result)
    {
        result.Clear();
        var pieces = new List<(int a, int b)>();
        var next = new List<(int a, int b)>();
        for (int k = 0; k < wins.Count; k++)
        {
            WinRect w = wins[k];
            pieces.Clear();
            pieces.Add((w.left, w.right));
            for (int j = 0; j < k && pieces.Count > 0; j++)
            {
                WinRect c = wins[j];
                if (!(c.top <= w.top && c.bottom > w.top)) continue; // doesn't cover this edge's height
                next.Clear();
                foreach (var pc in pieces)
                {
                    if (c.right <= pc.a || c.left >= pc.b) { next.Add(pc); continue; }
                    if (c.left > pc.a) next.Add((pc.a, c.left));
                    if (c.right < pc.b) next.Add((c.right, pc.b));
                }
                pieces.Clear();
                pieces.AddRange(next);
            }
            foreach (var pc in pieces) if (pc.b - pc.a > 4) result.Add((k, pc.a, pc.b));
        }
    }

    /// <summary>Screen-pixel windows -> world platforms, with per-window movement since the last call.</summary>
    void Publish(List<WinRect> wins, List<(int win, int x0, int x1)> edges, Func<float, float, Vector2> toWorld)
    {
        Current.Clear();
        var seen = new HashSet<long>();
        foreach (var e in edges)
        {
            WinRect w = wins[e.win];
            Vector2 a = toWorld(e.x0, w.top), b = toWorld(e.x1, w.top), tl = toWorld(w.left, w.top);
            Vector2 d = lastPos.TryGetValue(w.id, out Vector2 prev) ? tl - prev : Vector2.zero;
            Current.Add(new Platform { id = w.id, x0 = Mathf.Min(a.x, b.x), x1 = Mathf.Max(a.x, b.x), y = a.y, dx = d.x, dy = d.y });
            seen.Add(w.id);
        }
        lastPos.Clear();
        foreach (WinRect w in wins) if (seen.Contains(w.id)) lastPos[w.id] = toWorld(w.left, w.top);
    }

    // ------------------------------------------------------------------ editor: fake windows

    static readonly Dictionary<long, Vector2> s_editorOffset = new Dictionary<long, Vector2>(); // nudged test windows
    static readonly Dictionary<long, Vector2> s_editorBase = new Dictionary<long, Vector2>();   // where each was when the nudge began
    readonly Dictionary<long, Vector2> editorLastOffset = new Dictionary<long, Vector2>();
    readonly List<LineRenderer> editorLines = new List<LineRenderer>();
    readonly Vector3[] outline = new Vector3[5];

    void PollEditor()
    {
        Current.Clear();
        Windows.Clear();
        for (int i = 0; i < editorPlatforms.Count; i++)
        {
            long id = i + 1;
            Vector3 f = editorPlatforms[i];
            Vector3 a = cam.ViewportToWorldPoint(new Vector3(f.x, f.z, 0f)), b = cam.ViewportToWorldPoint(new Vector3(f.y, f.z, 0f));
            Vector3 c = cam.ViewportToWorldPoint(new Vector3(f.x, f.z - editorWindowHeight, 0f));
            Vector2 off = s_editorOffset.TryGetValue(id, out Vector2 o) ? o : Vector2.zero;
            Vector2 d = editorLastOffset.TryGetValue(id, out Vector2 last) ? off - last : Vector2.zero;
            editorLastOffset[id] = off;
            float x0 = Mathf.Min(a.x, b.x) + off.x, x1 = Mathf.Max(a.x, b.x) + off.x, y = a.y + off.y, yb = c.y + off.y;
            Current.Add(new Platform { id = id, x0 = x0, x1 = x1, y = y, dx = d.x, dy = d.y });
            Windows.Add(new WindowBox { id = id, xMin = x0, xMax = x1, yMin = Mathf.Min(y, yb), yMax = Mathf.Max(y, yb), movable = true });
            if (i < editorLines.Count && editorLines[i])
            {
                outline[0] = new Vector3(x0, y, 0f); outline[1] = new Vector3(x1, y, 0f); outline[2] = new Vector3(x1, yb, 0f);
                outline[3] = new Vector3(x0, yb, 0f); outline[4] = outline[0];
                editorLines[i].SetPositions(outline);
            }
        }
        AddScreens();
        // test icons
        Icons.Clear();
        if (!useDesktopIcons) return;
        for (int i = 0; i < editorIcons.Count; i++)
        {
            Vector3 f = editorIcons[i];
            Vector3 c = cam.ViewportToWorldPoint(new Vector3(f.x, f.y, 0f)), e = cam.ViewportToWorldPoint(new Vector3(f.x, f.y + editorIconSize * 0.5f, 0f));
            float h = Mathf.Abs(e.y - c.y);
            AddIcon(IconIdBase + i, new Vector2(c.x - h, c.y + h), new Vector2(c.x + h, c.y - h), h * 3.1f, f.z > 0.5f);
            int li = editorPlatforms.Count + i;
            if (li < editorLines.Count && editorLines[li])
            {
                outline[0] = new Vector3(c.x - h, c.y + h, 0f); outline[1] = new Vector3(c.x + h, c.y + h, 0f); outline[2] = new Vector3(c.x + h, c.y - h, 0f);
                outline[3] = new Vector3(c.x - h, c.y - h, 0f); outline[4] = outline[0];
                editorLines[li].SetPositions(outline);
            }
        }
    }

#if UNITY_EDITOR
    void BuildEditorLines()
    {
        if (!cam) return;
        var mat = new Material(Shader.Find("Sprites/Default"));
        for (int i = 0; i < editorPlatforms.Count + editorIcons.Count; i++)
        {
            bool icon = i >= editorPlatforms.Count;
            var go = new GameObject(icon ? "Test Icon (Editor only)" : "Test Window (Editor only)");
            go.transform.SetParent(transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.widthMultiplier = 0.03f;
            lr.sharedMaterial = mat;
            lr.startColor = lr.endColor = icon ? (editorIcons[i - editorPlatforms.Count].z > 0.5f ? new Color(0.45f, 0.75f, 0.95f, 0.9f) : new Color(0.85f, 0.8f, 0.5f, 0.9f)) : new Color(0.6f, 0.6f, 0.6f, 0.9f);
            lr.positionCount = 5;
            editorLines.Add(lr);
        }
        PollEditor();
    }
#endif

    // ------------------------------------------------------------------ nudging windows (WindowWobble)

    /// <summary>Start nudging a window: remembers where it is now. False if it can't be moved (maximized, gone...).</summary>
    public static bool BeginMove(long id)
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        if (s_moving.ContainsKey(id)) return true;
        var h = new IntPtr(id);
        if (!IsWindow(h) || IsZoomed(h) || IsIconic(h) || !GetWindowRect(h, out RECT r)) return false;
        s_moving[id] = new Moving { x = r.Left, y = r.Top, ax = r.Left, ay = r.Top, bx = r.Left, by = r.Top, cx = r.Left, cy = r.Top };
        return true;
#else
        s_editorBase[id] = s_editorOffset.TryGetValue(id, out Vector2 o) ? o : Vector2.zero; // (a test window moved before: from there)
        if (!s_editorOffset.ContainsKey(id)) s_editorOffset[id] = Vector2.zero;
        return true;
#endif
    }

    /// <summary>
    /// Put the window at this offset (world units) from where BeginMove found it. False = stop: the window is gone,
    /// got maximized/minimized, or someone else moved it (then it's theirs and we leave it alone).
    /// </summary>
    public static bool MoveTo(long id, Vector2 offset)
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        if (!s_moving.TryGetValue(id, out Moving m)) return false;
        var h = new IntPtr(id);
        if (!IsWindow(h) || IsZoomed(h) || IsIconic(h) || !GetWindowRect(h, out RECT r) || !m.IsOurs(r.Left, r.Top))
        {
            s_moving.Remove(id);
            return false;
        }
        int nx = m.x + Mathf.RoundToInt(offset.x * s_pxPerUnit), ny = m.y - Mathf.RoundToInt(offset.y * s_pxPerUnit);
        if (nx == m.ax && ny == m.ay) return true;
        if (!SetWindowPos(h, IntPtr.Zero, nx, ny, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_ASYNCWINDOWPOS))
        {
            s_moving.Remove(id); // e.g. an admin window we're not allowed to touch
            return false;
        }
        m.cx = m.bx; m.cy = m.by; m.bx = m.ax; m.by = m.ay; m.ax = nx; m.ay = ny;
        s_moving[id] = m;
        return true;
#else
        if (!s_editorOffset.ContainsKey(id)) return false;
        s_editorOffset[id] = (s_editorBase.TryGetValue(id, out Vector2 b) ? b : Vector2.zero) + offset;
        return true;
#endif
    }

    /// <summary>Stop nudging; by default put the window back exactly where BeginMove found it (restore false: it stays
    /// where it is now: you moved it, e.g. by a buddy's window panel's top bar).</summary>
    public static void EndMove(long id, bool restore = true)
    {
        if (restore) MoveTo(id, Vector2.zero);
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        s_moving.Remove(id);
#else
        if (restore && (!s_editorBase.TryGetValue(id, out Vector2 b) || b.sqrMagnitude < 1e-12f)) s_editorOffset.Remove(id);
        s_editorBase.Remove(id);
#endif
    }

    // ------------------------------------------------------------------ Windows player: real windows

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);
    [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200, SWP_ASYNCWINDOWPOS = 0x4000;

    /// <summary>A window we're nudging: where it started and the last three positions we asked for (moves are async).</summary>
    struct Moving
    {
        public int x, y, ax, ay, bx, by, cx, cy;
        public bool IsOurs(int px, int py) =>
            Close(px, py, ax, ay) || Close(px, py, bx, by) || Close(px, py, cx, cy) || Close(px, py, x, y);
        static bool Close(int px, int py, int qx, int qy) => Math.Abs(px - qx) <= 2 && Math.Abs(py - qy) <= 2;
    }
    static readonly Dictionary<long, Moving> s_moving = new Dictionary<long, Moving>();

    const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_APPWINDOW = 0x00040000;
    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;

    static readonly List<IntPtr> s_found = new List<IntPtr>();
    static readonly StringBuilder s_class = new StringBuilder(64);
    static uint s_pid;

    readonly List<WinRect> wins = new List<WinRect>();
    readonly List<(int win, int x0, int x1)> edges = new List<(int win, int x0, int x1)>();

    [AOT.MonoPInvokeCallback(typeof(EnumWindowsProc))]
    static bool Collect(IntPtr hWnd, IntPtr lParam)
    {
        s_found.Add(hWnd); // top-most first
        return true;
    }

    static bool IsCandidate(IntPtr h)
    {
        if (!IsWindowVisible(h) || IsIconic(h)) return false;
        GetWindowThreadProcessId(h, out uint pid);
        if (pid == s_pid) return false; // our own overlay
        int ex = GetWindowLong(h, GWL_EXSTYLE);
        if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0) return false; // tooltips, palettes, popups
        if (GetWindowTextLength(h) == 0) return false;
        if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return false; // other desktops, hidden UWP
        s_class.Length = 0;
        GetClassName(h, s_class, s_class.Capacity);
        string cls = s_class.ToString();
        return cls != "Progman" && cls != "WorkerW" && cls != "Shell_TrayWnd" && cls != "Shell_SecondaryTrayWnd";
    }

    // a top edge (x0..x1 at y, pixels) on one of your screens: which (the overlay window if they're not known yet)
    static bool OnAScreen(int x0, int x1, int y, RECT me, out RectInt m)
    {
        if (ScreenBounds.MonitorCount == 0)
        {
            m = new RectInt(me.Left, me.Top, me.Right - me.Left, me.Bottom - me.Top);
            return y < me.Bottom && x1 > me.Left && x0 < me.Right;
        }
        return ScreenBounds.MonitorAt(x0, x1, y, out m);
    }

    // the screen a window is (mostly) on: the one its middle is on, else the one its top-left is on, else the overlay
    static RectInt ScreenOf(WinRect w, RECT me)
    {
        if (ScreenBounds.MonitorAt((w.left + w.right) / 2, (w.top + w.bottom) / 2, out RectInt m) || ScreenBounds.MonitorAt(w.left + 8, w.top + 8, out m)) return m;
        return new RectInt(me.Left, me.Top, me.Right - me.Left, me.Bottom - me.Top);
    }

    void PollWindows()
    {
        IntPtr self = TransparentWindow.Handle;
        if (self == IntPtr.Zero) return;
        if (DwmGetWindowAttribute(self, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT me, Marshal.SizeOf<RECT>()) != 0) return;
        float sx = Screen.width / (float)Mathf.Max(1, me.Right - me.Left);   // DWM bounds are physical pixels;
        float sy = Screen.height / (float)Mathf.Max(1, me.Bottom - me.Top);  // this also covers a non-DPI-aware player

        s_pid = GetCurrentProcessId();
        s_found.Clear();
        EnumWindows(Collect, IntPtr.Zero);

        wins.Clear();
        foreach (IntPtr h in s_found)
        {
            if (!IsCandidate(h)) continue;
            if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0) continue;
            if (r.Right - r.Left < minWindowWidth || r.Bottom - r.Top < 20) continue;
            wins.Add(new WinRect { id = h.ToInt64(), left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom });
        }

        VisibleTopEdges(wins, edges);
        // drop edges at the very top of a screen (maximized windows) or on none of them
        edges.RemoveAll(e => !OnAScreen(e.x0, e.x1, wins[e.win].top, me, out RectInt m) || wins[e.win].top <= m.yMin + 2);

        Camera c = cam;
        Func<float, float, Vector2> toWorld = (px, py) =>
        {
            float ux = (px - me.Left) * sx, uy = Screen.height - (py - me.Top) * sy;
            Vector3 w = c.ScreenToWorldPoint(new Vector3(ux, uy, -c.transform.position.z));
            return new Vector2(w.x, w.y);
        };
        Publish(wins, edges, toWorld);

        Windows.Clear();
        foreach (WinRect w in wins)
        {
            if (w.right <= me.Left || w.left >= me.Right || w.bottom <= me.Top || w.top >= me.Bottom) continue; // other screen
            Vector2 tl = toWorld(w.left, w.top), br = toWorld(w.right, w.bottom);
            RectInt m = ScreenOf(w, me); // (maximized: fills its own screen)
            bool maximized = w.top <= m.yMin + 2 && w.left <= m.xMin + 2 && w.right >= m.xMax - 2;
            Windows.Add(new WindowBox
            {
                id = w.id, xMin = Mathf.Min(tl.x, br.x), xMax = Mathf.Max(tl.x, br.x), yMin = Mathf.Min(tl.y, br.y), yMax = Mathf.Max(tl.y, br.y),
                movable = !maximized && w.top > m.yMin + 2
            });
        }
        AddScreens();

        // desktop icons (read on their own thread): the icon picture sits centred at the top of its cell
        Icons.Clear();
        if (!useDesktopIcons) { IconReport = "switched off (useDesktopIcons)"; return; }
        if (!DesktopIcons.Get(rawIcons)) { IconReport = "no reading yet"; return; }
        int others = 0, added = 0, offScreen = 0, covered = 0;
        string firstOff = "", coverers = "";
        for (int i = 0; i < rawIcons.Count; i++)
        {
            DesktopIcons.Raw ic = rawIcons[i];
            if (ic.x + ic.w <= me.Left || ic.x >= me.Right || ic.y + ic.h <= me.Top || ic.y >= me.Bottom) { others++; continue; } // other screen
            int size = Mathf.RoundToInt(Mathf.Min(ic.w, ic.h) * 0.64f), left = ic.x + (ic.w - size) / 2, top = ic.y + Mathf.Max(2, size / 20);
            Vector2 tl = toWorld(left, top), br = toWorld(left + size, top + size);
            float cell = Mathf.Abs(toWorld(ic.x + ic.w, ic.y).x - toWorld(ic.x, ic.y).x);
            int r = AddIcon(IconIdBase + i, tl, br, cell, ic.bin, out WindowBox by);
            if (r == 0) added++;
            else if (r == 1) { offScreen++; if (firstOff.Length == 0) firstOff = " (e.g. at " + ic.x + "," + ic.y + " px = world " + ((tl.x + br.x) * 0.5f).ToString("0.00") + "," + tl.y.ToString("0.00") + ")"; }
            else
            {
                covered++;
                string who = WindowClass(by.id) + " " + Mathf.RoundToInt((by.xMax - by.xMin) * s_pxPerUnit) + "x" + Mathf.RoundToInt((by.yMax - by.yMin) * s_pxPerUnit);
                if (!coverers.Contains(who) && coverers.Length < 200) coverers += (coverers.Length > 0 ? ", " : "") + who;
            }
        }
        IconReport = rawIcons.Count + " read, " + others + " on screens left out, " + added + " used, " + offScreen + " off the screens or under a taskbar" + firstOff +
                     ", " + covered + " under a window" + (coverers.Length > 0 ? " (" + coverers + ")" : "");
    }

    // a window's class (for the icon report: which window is lying over the icons), never its title or contents
    static string WindowClass(long id)
    {
        if (id >= ScreenIdBase) return "off-screen area";
        s_class.Length = 0;
        try { GetClassName(new IntPtr(id), s_class, s_class.Capacity); } catch (Exception) { }
        return s_class.Length > 0 ? s_class.ToString() : "?";
    }
#endif
}
