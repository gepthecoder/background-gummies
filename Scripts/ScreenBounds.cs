using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

/// <summary>
/// Where the buddies can be: your screens, exactly as Windows arranges them (left, right, above, portrait, different
/// resolutions). The overlay window covers all of them (TransparentWindow); this reads the layout from Windows and keeps
/// the world in step with it:
///  · Screens: every screen's area in world units (its work area: without the taskbar), the main one centred at (0, 0)
///    and 10 units tall, the others the same number of pixels per unit (a buddy is the same size in pixels everywhere).
///  · WorldRect: the box round all of them (the outer walls; its bottom is the lowest floor).
///  · Solids: what's inside that box but on no screen (beside a portrait screen, under a taskbar): solid, like a window
///    that's always there (DesktopPlatforms adds them to Windows, flagged screen, and their tops to Current as floors).
///    Where screens touch there's nothing in the way: buddies, snacks and the ball go straight across.
/// Every screen's floor is its own: its taskbar, or its bottom edge. Checked every half second (screens plugged in or
/// out, resolution, taskbar moved); whipit_screens.txt (next to the saves) describes what it found.
/// In the Editor: one screen, the Game view.
/// </summary>
[DisallowMultipleComponent]
public class ScreenBounds : MonoBehaviour
{
    public struct ScreenArea
    {
        /// <summary>World units: the whole screen, and the part without the taskbar (where they live).</summary>
        public Rect full, work;
        public bool primary;
        /// <summary>Windows scaling of that screen (1 = 100 %).</summary>
        public float scale;
        public string name;
    }

    /// <summary>The box round all the screens (world units). Its bottom is the lowest floor.</summary>
    public static Rect WorldRect { get; private set; } = new Rect(-8f, -5f, 16f, 10f);
    /// <summary>Your screens (world units).</summary>
    public static IReadOnlyList<ScreenArea> Screens => s_screens;
    /// <summary>Inside WorldRect but on no screen (or under a taskbar): solid.</summary>
    public static IReadOnlyList<Rect> Solids => s_solids;
    /// <summary>More than one screen.</summary>
    public static bool Multi => s_screens.Count > 1;
    /// <summary>Goes up whenever the layout changes.</summary>
    public static int Version { get; private set; }
    /// <summary>Screen pixels per world unit.</summary>
    public static float PixelsPerUnit { get; private set; } = 108f;
    /// <summary>
    /// Leave out screens driven by another graphics chip than the one drawing WhipIt (a laptop's own screen on the
    /// integrated graphics while the others are on the graphics card). Windows can't keep a see-through window right when
    /// it's on both: everything came out washed out and see-through (its colours added onto what's behind), and every
    /// frame had to be copied across. With all your screens on one chip nothing is left out.
    /// </summary>
    public static bool LeaveOutOtherChip = true;
    /// <summary>The screens left out (and on which chip), for the reports; empty: none.</summary>
    public static string LeftOut { get; private set; } = "";

    public Camera cam;
    [Tooltip("Wall thickness in world units. Thick walls stop fast throws from tunnelling through.")]
    public float thickness = 2f;

    static readonly List<ScreenArea> s_screens = new List<ScreenArea> { new ScreenArea { full = new Rect(-8f, -5f, 16f, 10f), work = new Rect(-8f, -5f, 16f, 10f), primary = true, scale = 1f, name = "screen" } };
    static readonly List<Rect> s_solids = new List<Rect>();
    static readonly List<RectInt> s_monitorsPx = new List<RectInt>(); // (virtual desktop pixels, y down) for DesktopPlatforms
    static RectInt s_clientPx;          // the overlay window (physical pixels, y down)
    static float s_unityPerPx = 1f;     // Unity's pixels per physical pixel (1 unless the player isn't DPI aware)
    static int s_mainPx;                // the main screen's height in Unity pixels

    BoxCollider2D left, right, top, floor;
    Rect built;
    float nextCheck;
    string lastLayout;

    void Awake()
    {
        if (!cam) cam = Camera.main;
        left = MakeWall("Wall Left");
        right = MakeWall("Wall Right");
        top = MakeWall("Wall Top");
        floor = MakeWall("Floor");
        Rebuild();
    }

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("ScreenBounds.Update", t0); }
    void UpdateTimed()
    {
        if (Time.unscaledTime < nextCheck) return; // screen / taskbar changes are rare
        nextCheck = Time.unscaledTime + 0.5f;
        Rebuild();
    }

    void Rebuild()
    {
        if (!cam) return;
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        if (ReadScreens()) { PlaceWalls(); return; }
        if (lastLayout != null) return; // (a hiccup: keeps what it had)
#endif
        // one screen: the camera's view, the floor on the taskbar
        Vector3 min = cam.ViewportToWorldPoint(new Vector3(0f, 0f, 0f));
        Vector3 max = cam.ViewportToWorldPoint(new Vector3(1f, 1f, 0f));
        float bottom = min.y;
        if (DesktopInput.TryGetWorkAreaBottom(out float sy))
            bottom = cam.ScreenToWorldPoint(new Vector3(0f, sy, 0f)).y;
        var rect = Rect.MinMaxRect(min.x, bottom, max.x, max.y);
        if (rect != built || Multi) SetLayout(new[] { rect }, new[] { Rect.MinMaxRect(min.x, min.y, max.x, max.y) });
        PlaceWalls();
    }

    void PlaceWalls()
    {
        Rect rect = WorldRect;
        if (rect == built) return;
        built = rect;
        float t = thickness, w = rect.width + 2f * t, h = rect.height + 2f * t;
        Place(left, new Vector2(rect.xMin - t * 0.5f, rect.center.y), new Vector2(t, h));
        Place(right, new Vector2(rect.xMax + t * 0.5f, rect.center.y), new Vector2(t, h));
        Place(top, new Vector2(rect.center.x, rect.yMax + t * 0.5f), new Vector2(w, t));
        Place(floor, new Vector2(rect.center.x, rect.yMin - t * 0.5f), new Vector2(w, t));
    }

    // ------------------------------------------------------------------ the layout (shared, testable)

    /// <summary>
    /// Set the screens (world units): where they live (work areas) and the whole screens (optional; the first one is the
    /// main one unless primary says otherwise). Works out WorldRect and the Solids. Used by the Windows player, the
    /// Editor (one screen) and the sims.
    /// </summary>
    public static void SetLayout(IList<Rect> work, IList<Rect> full = null, int primary = 0, IList<float> scales = null, IList<string> names = null)
    {
        s_screens.Clear();
        s_solids.Clear();
        s_floors.Clear();
        Version++;
        if (work == null || work.Count == 0) return;
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        for (int i = 0; i < work.Count; i++)
        {
            Rect w = work[i];
            s_screens.Add(new ScreenArea { work = w, full = full != null && i < full.Count ? full[i] : w, primary = i == primary, scale = scales != null && i < scales.Count ? scales[i] : 1f, name = names != null && i < names.Count ? names[i] : "screen " + (i + 1) });
            x0 = Mathf.Min(x0, w.xMin); y0 = Mathf.Min(y0, w.yMin); x1 = Mathf.Max(x1, w.xMax); y1 = Mathf.Max(y1, w.yMax);
        }
        WorldRect = Rect.MinMaxRect(x0, y0, x1, y1);
        Solidify();
        FindFloors();
    }

    // the box minus the screens, as a few rectangles: cut along every screen edge, keep the cells on no screen, join them
    static void Solidify()
    {
        s_solids.Clear();
        if (s_screens.Count < 2 && (s_screens.Count == 0 || s_screens[0].work == WorldRect)) return;
        var xs = new List<float>();
        var ys = new List<float>();
        Rect box = WorldRect;
        xs.Add(box.xMin); xs.Add(box.xMax); ys.Add(box.yMin); ys.Add(box.yMax);
        foreach (var s in s_screens) { xs.Add(s.work.xMin); xs.Add(s.work.xMax); ys.Add(s.work.yMin); ys.Add(s.work.yMax); }
        Distinct(xs); Distinct(ys);
        int nx = xs.Count - 1, ny = ys.Count - 1;
        var solid = new bool[nx, ny];
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
            {
                var c = new Vector2(0.5f * (xs[i] + xs[i + 1]), 0.5f * (ys[j] + ys[j + 1]));
                bool on = false;
                foreach (var s in s_screens) if (s.work.Contains(c)) { on = true; break; }
                solid[i, j] = !on;
            }
        // runs along each row, then stack runs that line up
        var runs = new List<Rect>();
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                if (!solid[i, j]) continue;
                int k = i;
                while (k + 1 < nx && solid[k + 1, j]) k++;
                runs.Add(Rect.MinMaxRect(xs[i], ys[j], xs[k + 1], ys[j + 1]));
                i = k;
            }
        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int a = 0; a < runs.Count && !merged; a++)
                for (int b = 0; b < runs.Count && !merged; b++)
                {
                    if (a == b) continue;
                    Rect A = runs[a], B = runs[b];
                    if (Mathf.Abs(A.xMin - B.xMin) < 1e-4f && Mathf.Abs(A.xMax - B.xMax) < 1e-4f && Mathf.Abs(A.yMax - B.yMin) < 1e-4f)
                    {
                        runs[a] = Rect.MinMaxRect(A.xMin, A.yMin, A.xMax, B.yMax);
                        runs.RemoveAt(b);
                        merged = true;
                    }
                }
        }
        foreach (var r in runs) if (r.width > 0.01f && r.height > 0.01f) s_solids.Add(r);
    }

    static void Distinct(List<float> v)
    {
        v.Sort();
        for (int i = v.Count - 1; i > 0; i--) if (v[i] - v[i - 1] < 1e-4f) v.RemoveAt(i);
    }

    /// <summary>Every floor (x0, x1, y): one screen: WorldRect's bottom; more: each screen's own (the lowest one, solid tops
    /// under a screen, bottom edges over another screen). For things that cover the floor (the lava).</summary>
    public static void AllFloors(List<Vector3> into)
    {
        into.Clear();
        if (!Multi) { into.Add(new Vector3(WorldRect.xMin, WorldRect.xMax, WorldRect.yMin)); return; }
        foreach (var f in s_floors) into.Add(new Vector3(f.x0, f.x1, f.y));
    }

    /// <summary>The floors that aren't the lowest one: every screen's bottom edge (its taskbar, or its bottom) that isn't
    /// the bottom of the box: over a bit of the world on no screen it's that solid's top (k = the solid), over another
    /// screen it's a floor you can throw them up through but they stand on (k = -2 - the screen). Pieces that touch at
    /// the same height are one floor.</summary>
    public static void Floors(List<(float x0, float x1, float y, int k)> into)
    {
        into.Clear();
        foreach (var f in s_floors) if (f.k != -1) into.Add(f);
    }

    // every floor: the bits of the lowest one under a screen (k = -1), solid tops under a screen (k = the solid) and the
    // bottom edges of screens right above another screen (k = -2 - that screen)
    static readonly List<(float x0, float x1, float y, int k)> s_floors = new List<(float x0, float x1, float y, int k)>();

    static void FindFloors()
    {
        s_floors.Clear();
        if (s_screens.Count < 2) return;
        var raw = new List<(float x0, float x1, float y, int k)>();
        var covered = new List<Vector2>();
        for (int i = 0; i < s_screens.Count; i++)
        {
            Rect w = s_screens[i].work;
            if (Mathf.Abs(w.yMin - WorldRect.yMin) < 1e-3f) { raw.Add((w.xMin, w.xMax, WorldRect.yMin, -1)); continue; }
            covered.Clear();
            for (int k = 0; k < s_solids.Count; k++)
            {
                Rect s = s_solids[k];
                if (Mathf.Abs(w.yMin - s.yMax) > 1e-3f) continue;
                float a = Mathf.Max(w.xMin, s.xMin), b = Mathf.Min(w.xMax, s.xMax);
                if (b - a > 1e-3f) { raw.Add((a, b, s.yMax, k)); covered.Add(new Vector2(a, b)); }
            }
            // the rest of its bottom edge is right above another screen
            covered.Sort((p, q) => p.x.CompareTo(q.x));
            float x = w.xMin;
            foreach (var c in covered)
            {
                if (c.x - x > 0.05f) raw.Add((x, c.x, w.yMin, -2 - i));
                x = Mathf.Max(x, c.y);
            }
            if (w.xMax - x > 0.05f) raw.Add((x, w.xMax, w.yMin, -2 - i));
        }
        raw.Sort((p, q) => p.y != q.y ? p.y.CompareTo(q.y) : p.x0.CompareTo(q.x0));
        foreach (var f in raw)
        {
            int last = s_floors.Count - 1;
            if (last >= 0 && Mathf.Abs(s_floors[last].y - f.y) < 1e-3f && f.x0 <= s_floors[last].x1 + 1e-3f)
            {
                var m = s_floors[last];
                s_floors[last] = (m.x0, Mathf.Max(m.x1, f.x1), m.y, m.k >= 0 || f.k < 0 ? m.k : f.k); // (a solid's id if there's one under it)
            }
            else s_floors.Add(f);
        }
    }

    /// <summary>The screen this point is on (index into Screens); off every screen: the nearest one.</summary>
    public static int ScreenIndexAt(Vector2 p)
    {
        int best = 0;
        float bd = float.MaxValue;
        for (int i = 0; i < s_screens.Count; i++)
        {
            Rect w = s_screens[i].work;
            float dx = Mathf.Max(0f, Mathf.Max(w.xMin - p.x, p.x - w.xMax)), dy = Mathf.Max(0f, Mathf.Max(w.yMin - p.y, p.y - w.yMax));
            float d = dx * dx + dy * dy;
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }

    /// <summary>Where they live on the screen this point is on (the nearest screen's, if it's on none). One screen: WorldRect.</summary>
    public static Rect ScreenRectAt(Vector2 p) => Multi ? s_screens[ScreenIndexAt(p)].work : WorldRect;

    /// <summary>Is this point on a screen (not under a taskbar, not in a gap beside a screen)?</summary>
    public static bool OnScreen(Vector2 p)
    {
        if (!Multi) return p.x >= WorldRect.xMin && p.x <= WorldRect.xMax && p.y >= WorldRect.yMin && p.y <= WorldRect.yMax;
        foreach (var s in s_screens) if (s.work.Contains(p)) return true;
        return false;
    }

    /// <summary>Are these two points on the same screen? (One screen: always.)</summary>
    public static bool SameScreen(Vector2 a, Vector2 b) => !Multi || ScreenIndexAt(a) == ScreenIndexAt(b);

    /// <summary>The height of the floor under this point.</summary>
    public static float FloorAt(Vector2 p) => FloorUnder(p, out float y, out _) ? y : ScreenRectAt(p).yMin;

    /// <summary>The floor it stands on (feet at y, at x = p.x): the stretch it can walk along at that height, across screens
    /// where they touch. One screen (or none found): WorldRect's width.</summary>
    public static Vector2 FloorSpanAt(Vector2 p, float y)
    {
        if (!Multi) return new Vector2(WorldRect.xMin, WorldRect.xMax);
        foreach (var f in s_floors)
            if (Mathf.Abs(f.y - y) < 0.05f && p.x >= f.x0 - 0.05f && p.x <= f.x1 + 0.05f) return new Vector2(f.x0, f.x1);
        return new Vector2(WorldRect.xMin, WorldRect.xMax);
    }

    /// <summary>The floor under this point: the highest floor below it (its screen's floor, unless there's a hole down to a
    /// screen below), and the stretch of it. False if there's none (one screen: WorldRect's bottom).</summary>
    public static bool FloorUnder(Vector2 p, out float y, out Vector2 span)
    {
        y = WorldRect.yMin;
        span = new Vector2(WorldRect.xMin, WorldRect.xMax);
        if (!Multi) return true;
        bool found = false;
        foreach (var f in s_floors)
            if (p.x >= f.x0 && p.x <= f.x1 && f.y <= p.y + 1e-3f && (!found || f.y > y)) { y = f.y; span = new Vector2(f.x0, f.x1); found = true; }
        return found;
    }

    /// <summary>Where they live on the main screen (one screen: WorldRect).</summary>
    public static Rect MainRect
    {
        get
        {
            if (!Multi) return WorldRect;
            foreach (var s in s_screens) if (s.primary) return s.work;
            return s_screens[0].work;
        }
    }

    /// <summary>How high it can go straight up from here: the top of its screen, or of the one above it where they touch.
    /// One screen: WorldRect's top.</summary>
    public static float CeilingAt(Vector2 p)
    {
        if (!Multi) return WorldRect.yMax;
        float top = ScreenRectAt(p).yMax;
        for (int guard = 0; guard < 8; guard++)
        {
            bool up = false;
            foreach (var s in s_screens)
                if (Mathf.Abs(s.work.yMin - top) < 1e-3f && p.x >= s.work.xMin && p.x <= s.work.xMax && s.work.yMax > top + 1e-3f) { top = s.work.yMax; up = true; break; }
            if (!up) break;
        }
        return top;
    }

    /// <summary>The floor at x nearest to height y (where to put something down at x). One screen: WorldRect's bottom.</summary>
    public static float FloorNear(float x, float y)
    {
        if (!Multi) return WorldRect.yMin;
        float best = WorldRect.yMin, bd = float.MaxValue;
        foreach (var f in s_floors)
            if (x >= f.x0 - 0.05f && x <= f.x1 + 0.05f && Mathf.Abs(f.y - y) < bd) { bd = Mathf.Abs(f.y - y); best = f.y; }
        return bd < float.MaxValue ? best : ScreenRectAt(new Vector2(x, y)).yMin;
    }

    /// <summary>The screen most of them are on right now (where the pillow fort, the rain, the playground... go).</summary>
    public static Rect BusiestRect
    {
        get
        {
            if (!Multi) return WorldRect;
            var count = new int[s_screens.Count];
            foreach (var b in GummyBody.All) if (b && b.isActiveAndEnabled) count[ScreenIndexAt(b.Center)]++;
            int best = 0;
            for (int i = 0; i < count.Length; i++)
                if (count[i] > count[best] || (count[i] == count[best] && s_screens[i].primary && !s_screens[best].primary)) best = i;
            return s_screens[best].work;
        }
    }

    /// <summary>The main screen's height in Unity pixels (pixel fonts scale with it). One screen: Screen.height.</summary>
    public static int MainScreenPixels => Multi && s_mainPx > 0 ? s_mainPx : Screen.height;

    /// <summary>A physical screen pixel (virtual desktop, y down) in Unity's screen pixels (y up). False before the window is
    /// known (Windows player only).</summary>
    public static bool PixelToUnity(int px, int py, out Vector2 u)
    {
        u = default;
        if (s_clientPx.width <= 0 || s_clientPx.height <= 0) return false;
        u = new Vector2((px - s_clientPx.x) * (Screen.width / (float)s_clientPx.width), Screen.height - 1 - (py - s_clientPx.y) * (Screen.height / (float)s_clientPx.height));
        return true;
    }

    /// <summary>The screens changed (one unplugged, moved, the taskbar moved...): buddies that ended up off every screen
    /// come back onto the nearest one, just above its floor.</summary>
    public static void Rescue()
    {
        foreach (var b in GummyBody.All)
        {
            if (!b || !b.isActiveAndEnabled || OnScreen(b.Center)) continue;
            Rect r = ScreenRectAt(b.Center);
            float hw = Mathf.Min(b.size, r.width * 0.25f);
            var to = new Vector2(Mathf.Clamp(b.Center.x, r.xMin + hw, r.xMax - hw), Mathf.Clamp(b.Center.y, r.yMin + b.size, r.yMax - b.size));
            b.Teleport(to);
        }
    }

    /// <summary>How many screens Windows told us about (0 in the Editor, or before the window is up).</summary>
    public static int MonitorCount => s_monitorsPx.Count;

    /// <summary>The screen in pixels (virtual desktop, y down) that contains this pixel, for DesktopPlatforms (false: none).</summary>
    public static bool MonitorAt(int px, int py, out RectInt m)
    {
        foreach (var r in s_monitorsPx)
            if (px >= r.xMin && px < r.xMax && py >= r.yMin && py < r.yMax) { m = r; return true; }
        m = default;
        return false;
    }

    /// <summary>The screen (pixels) a top edge from x0 to x1 at height y is on (false: on none).</summary>
    public static bool MonitorAt(int x0, int x1, int y, out RectInt m)
    {
        foreach (var r in s_monitorsPx)
            if (y >= r.yMin && y < r.yMax && x1 > r.xMin && x0 < r.xMax) { m = r; return true; }
        m = default;
        return false;
    }

    // ------------------------------------------------------------------ Windows: your screens

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFOEX { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice; }
    delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr hMonitor, int type, out uint dpiX, out uint dpiY);

    struct Mon { public RECT full, work; public bool primary; public float scale; public string name; }
    static readonly List<Mon> s_found = new List<Mon>();

    [AOT.MonoPInvokeCallback(typeof(MonitorEnumProc))]
    static bool OnMonitor(IntPtr h, IntPtr hdc, ref RECT rect, IntPtr data)
    {
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(h, ref info)) return true;
        float scale = 1f;
        try { if (GetDpiForMonitor(h, 0, out uint dx, out _) == 0 && dx > 0) scale = dx / 96f; } catch (Exception) { }
        s_found.Add(new Mon { full = info.rcMonitor, work = info.rcWork, primary = (info.dwFlags & 1) != 0, scale = scale, name = info.szDevice });
        return true;
    }

    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT value, int size);
    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    static void FindMonitors()
    {
        s_found.Clear();
        IntPtr old = TransparentWindow.Physical();
        try { EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, OnMonitor, IntPtr.Zero); }
        finally { TransparentWindow.Restore(old); }
        LeaveOut();
    }

    // which chip drives each screen (\\.\DISPLAYn -> its name), read again every few seconds
    static readonly Dictionary<string, string> s_chipOf = new Dictionary<string, string>();
    static float s_chipsAt = -1f;

    static void LeaveOut()
    {
        if (!LeaveOutOtherChip || s_found.Count < 2) { LeftOut = ""; return; }
        if (s_chipsAt < 0f || Time.unscaledTime - s_chipsAt > 5f)
        {
            s_chipsAt = Time.unscaledTime;
            s_chipOf.Clear();
            try
            {
                var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                for (uint i = 0; i < 32 && EnumDisplayDevices(null, i, ref dd, 0); i++)
                {
                    if ((dd.StateFlags & 1) != 0 && !string.IsNullOrEmpty(dd.DeviceName)) s_chipOf[dd.DeviceName] = dd.DeviceString ?? "";
                    dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                }
            }
            catch (Exception) { s_chipOf.Clear(); }
        }
        string unity = SystemInfo.graphicsDeviceName ?? "";
        int same = 0;
        foreach (var m in s_found) if (s_chipOf.TryGetValue(m.name, out string c) && c == unity) same++;
        if (unity.Length == 0 || same == 0 || same == s_found.Count) { LeftOut = ""; return; } // (all on one chip, or can't tell)
        string left = "";
        for (int i = s_found.Count - 1; i >= 0; i--)
        {
            if (!s_chipOf.TryGetValue(s_found[i].name, out string c) || c == unity) continue; // (unknown: kept)
            left = s_found[i].name + " on " + c + (left.Length > 0 ? "; " + left : "");
            s_found.RemoveAt(i);
        }
        LeftOut = left;
    }

    /// <summary>All your screens together (physical pixels, y down): where the overlay window goes.</summary>
    public static bool VirtualDesktop(out RectInt v)
    {
        FindMonitors();
        v = default;
        if (s_found.Count == 0) return false;
        int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
        foreach (var m in s_found) { l = Math.Min(l, m.full.Left); t = Math.Min(t, m.full.Top); r = Math.Max(r, m.full.Right); b = Math.Max(b, m.full.Bottom); }
        v = new RectInt(l, t, r - l, b - t);
        return true;
    }

    int spans;
    float nextSpan;

    // your screens -> the world: the main screen centred at (0, 0) and 10 units tall, every screen the same pixels per unit;
    // the camera covers the whole window (all of them). Everything here in physical pixels.
    bool ReadScreens()
    {
        IntPtr hwnd = TransparentWindow.Handle;
        if (hwnd == IntPtr.Zero || Screen.width <= 0 || Screen.height <= 0) return false;
        FindMonitors();
        if (s_found.Count == 0) return false;
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT me, Marshal.SizeOf<RECT>()) != 0) return false;
        if (me.Right - me.Left <= 0 || me.Bottom - me.Top <= 0) return false;

        // the window should cover all of them (a screen plugged in, or Unity sized it back): put it back, now and then
        int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
        foreach (var m in s_found) { l = Math.Min(l, m.full.Left); t = Math.Min(t, m.full.Top); r = Math.Max(r, m.full.Right); b = Math.Max(b, m.full.Bottom); }
        if ((me.Left != l || me.Top != t || me.Right != r || me.Bottom != b) && Time.unscaledTime >= nextSpan)
        {
            nextSpan = Time.unscaledTime + (spans < 5 ? 1f : 10f);
            spans++;
            TransparentWindow.Span();
        }

        int main = 0;
        for (int i = 0; i < s_found.Count; i++) if (s_found[i].primary) main = i;
        RECT pm = s_found[main].full;
        float ppu = Mathf.Max(1f, (pm.Bottom - pm.Top) / 10f);
        float pcx = 0.5f * (pm.Left + pm.Right), pcy = 0.5f * (pm.Top + pm.Bottom);
        s_clientPx = new RectInt(me.Left, me.Top, me.Right - me.Left, me.Bottom - me.Top);
        s_unityPerPx = Screen.height / (float)(me.Bottom - me.Top); // 1 unless the player isn't DPI aware
        PixelsPerUnit = ppu * s_unityPerPx;
        s_mainPx = Mathf.RoundToInt((pm.Bottom - pm.Top) * s_unityPerPx);

        // the camera: its view is the window, the main screen's middle at (0, 0)
        float ortho = (me.Bottom - me.Top) / (2f * ppu);
        var pos = new Vector3((0.5f * (me.Left + me.Right) - pcx) / ppu, (pcy - 0.5f * (me.Top + me.Bottom)) / ppu, cam.transform.position.z);
        if (Mathf.Abs(cam.orthographicSize - ortho) > 1e-4f) cam.orthographicSize = ortho;
        if ((cam.transform.position - pos).sqrMagnitude > 1e-8f) cam.transform.position = pos;

        var work = new List<Rect>();
        var full = new List<Rect>();
        var scales = new List<float>();
        var names = new List<string>();
        s_monitorsPx.Clear();
        foreach (var m in s_found)
        {
            work.Add(Rect.MinMaxRect((m.work.Left - pcx) / ppu, (pcy - m.work.Bottom) / ppu, (m.work.Right - pcx) / ppu, (pcy - m.work.Top) / ppu));
            full.Add(Rect.MinMaxRect((m.full.Left - pcx) / ppu, (pcy - m.full.Bottom) / ppu, (m.full.Right - pcx) / ppu, (pcy - m.full.Top) / ppu));
            scales.Add(m.scale);
            names.Add(m.name);
            s_monitorsPx.Add(new RectInt(m.full.Left, m.full.Top, m.full.Right - m.full.Left, m.full.Bottom - m.full.Top));
        }
        string layout = Describe(me);
        if (layout == lastLayout) return true;
        bool first = lastLayout == null;
        lastLayout = layout;
        SetLayout(work, full, main, scales, names);
        WriteReport(me);
        if (!first) Rescue();
        return true;
    }

    string Describe(RECT me)
    {
        var sb = new StringBuilder();
        foreach (var m in s_found) sb.Append(m.full.Left).Append(',').Append(m.full.Top).Append(',').Append(m.full.Right).Append(',').Append(m.full.Bottom).Append('|').Append(m.work.Left).Append(',').Append(m.work.Top).Append(',').Append(m.work.Right).Append(',').Append(m.work.Bottom).Append(m.primary ? "*" : "").Append(';');
        sb.Append(me.Left).Append(',').Append(me.Top).Append(',').Append(me.Right).Append(',').Append(me.Bottom).Append(',').Append(Screen.width).Append('x').Append(Screen.height);
        return sb.ToString();
    }

    // what it found, for you (and me): whipit_screens.txt next to the saves
    void WriteReport(RECT me)
    {
        try
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("WhipIt screens - " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", inv));
            sb.AppendLine("Overlay window: " + R(me) + ", Unity sees " + Screen.width + " x " + Screen.height + " (" + TransparentWindow.SpanInfo + ")");
            sb.AppendLine("Pixels per world unit: " + PixelsPerUnit.ToString("0.###", inv) + ", camera size " + cam.orthographicSize.ToString("0.###", inv) + " at " + cam.transform.position.x.ToString("0.###", inv) + "," + cam.transform.position.y.ToString("0.###", inv));
            if (LeftOut.Length > 0) sb.AppendLine("Left out (on another graphics chip than WhipIt's; see ScreenBounds.LeaveOutOtherChip): " + LeftOut);
            sb.AppendLine("Drawn by: " + SystemInfo.graphicsDeviceName + " (" + SystemInfo.graphicsDeviceType + ", " + QualitySettings.activeColorSpace + " colour space)");
            var facts = DisplayFacts(inv);
            for (int i = 0; i < s_found.Count && i < s_screens.Count; i++)
            {
                var m = s_found[i];
                var s = s_screens[i];
                sb.AppendLine((m.primary ? "* " : "  ") + m.name + ": screen " + R(m.full) + ", work area " + R(m.work) + ", scaling " + Mathf.RoundToInt(m.scale * 100f) + " %");
                if (facts.TryGetValue(m.name, out string f)) sb.AppendLine("      " + f);
                sb.AppendLine("      world: screen " + W(s.full, inv) + ", where they live " + W(s.work, inv));
            }
            sb.AppendLine("World box: " + W(WorldRect, inv));
            sb.AppendLine("Solid (on no screen / under a taskbar): " + s_solids.Count);
            foreach (var r in s_solids) sb.AppendLine("  " + W(r, inv));
            sb.AppendLine("Floors: " + s_floors.Count);
            foreach (var f in s_floors) sb.AppendLine("  x " + f.x0.ToString("0.##", inv) + ".." + f.x1.ToString("0.##", inv) + " at y " + f.y.ToString("0.##", inv) + (f.k == -1 ? " (the lowest)" : f.k < -1 ? " (over the screen below)" : ""));
            Directory.CreateDirectory(Application.persistentDataPath);
            File.WriteAllText(Path.Combine(Application.persistentDataPath, "whipit_screens.txt"), sb.ToString());
        }
        catch (Exception e) { Debug.LogWarning("[WhipIt] Couldn't write whipit_screens.txt: " + e.Message); }
    }

    // which graphics chip drives each screen, its refresh rate and colour mode (only facts about the screens)
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string device, uint devNum, ref DISPLAY_DEVICE dd, uint flags);

    [StructLayout(LayoutKind.Sequential)] struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] struct DC_PATH_SOURCE { public LUID adapter; public uint id, modeIdx, status; }
    [StructLayout(LayoutKind.Sequential)] struct DC_PATH_TARGET { public LUID adapter; public uint id, modeIdx, tech, rotation, scaling, refreshNum, refreshDen, scanLine; public int available; public uint status; }
    [StructLayout(LayoutKind.Sequential)] struct DC_PATH { public DC_PATH_SOURCE source; public DC_PATH_TARGET target; public uint flags; }
    [StructLayout(LayoutKind.Sequential, Size = 64)] struct DC_MODE { public uint infoType, id; public LUID adapter; }
    [StructLayout(LayoutKind.Sequential)] struct DC_HEADER { public uint type, size; public LUID adapter; public uint id; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct DC_SOURCE_NAME { public DC_HEADER header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string gdiName; }
    [StructLayout(LayoutKind.Sequential)] struct DC_COLOR { public DC_HEADER header; public uint value, encoding, bits; }
    [StructLayout(LayoutKind.Sequential)] struct DC_COLOR2 { public DC_HEADER header; public uint value, encoding, bits, mode; }
    [StructLayout(LayoutKind.Sequential)] struct DC_WHITE { public DC_HEADER header; public uint level; }
    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] DC_PATH[] paths, ref uint numModes, [Out] DC_MODE[] modes, IntPtr topology);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] static extern int DcSourceName(ref DC_SOURCE_NAME info);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] static extern int DcColor(ref DC_COLOR info);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] static extern int DcColor2(ref DC_COLOR2 info);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] static extern int DcWhite(ref DC_WHITE info);

    static Dictionary<string, string> DisplayFacts(IFormatProvider inv)
    {
        var facts = new Dictionary<string, string>();
        try
        {
            string unity = SystemInfo.graphicsDeviceName ?? "";
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            for (uint i = 0; i < 32 && EnumDisplayDevices(null, i, ref dd, 0); i++)
            {
                if ((dd.StateFlags & 1) != 0) // (attached to the desktop)
                    facts[dd.DeviceName] = "on " + dd.DeviceString + (unity.Length > 0 && dd.DeviceString == unity ? " (the one drawing WhipIt)" : " (NOT the one drawing WhipIt: every frame gets copied over)");
                dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            }
        }
        catch (Exception e) { facts["?"] = e.Message; }
        try
        {
            if (GetDisplayConfigBufferSizes(2, out uint np, out uint nm) != 0) return facts; // (2: the active ones)
            var paths = new DC_PATH[np];
            var modes = new DC_MODE[nm];
            if (QueryDisplayConfig(2, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return facts;
            for (int i = 0; i < np; i++)
            {
                var p = paths[i];
                var sn = new DC_SOURCE_NAME { header = new DC_HEADER { type = 1, size = (uint)Marshal.SizeOf<DC_SOURCE_NAME>(), adapter = p.source.adapter, id = p.source.id } };
                if (DcSourceName(ref sn) != 0 || string.IsNullOrEmpty(sn.gdiName)) continue;
                string s = "";
                if (p.target.refreshDen != 0) s += ", " + (p.target.refreshNum / (double)p.target.refreshDen).ToString("0.#", inv) + " Hz";
                var c2 = new DC_COLOR2 { header = new DC_HEADER { type = 15, size = (uint)Marshal.SizeOf<DC_COLOR2>(), adapter = p.target.adapter, id = p.target.id } };
                bool advanced = false;
                if (DcColor2(ref c2) == 0)
                {
                    advanced = (c2.value & 2) != 0;
                    s += ", colour mode " + (c2.mode == 2 ? "HDR" : c2.mode == 1 ? "WCG (automatic colour management)" : "SDR") + ((c2.value & 32) != 0 ? ", HDR switched on" : "") + ((c2.value & 128) != 0 ? ", auto colour switched on" : "");
                }
                else
                {
                    var c = new DC_COLOR { header = new DC_HEADER { type = 9, size = (uint)Marshal.SizeOf<DC_COLOR>(), adapter = p.target.adapter, id = p.target.id } };
                    if (DcColor(ref c) == 0) { advanced = (c.value & 2) != 0; s += ", advanced colour " + (advanced ? "ON" : "off"); }
                }
                if (advanced)
                {
                    var w = new DC_WHITE { header = new DC_HEADER { type = 11, size = (uint)Marshal.SizeOf<DC_WHITE>(), adapter = p.target.adapter, id = p.target.id } };
                    if (DcWhite(ref w) == 0) s += ", SDR white " + Mathf.RoundToInt(w.level / 1000f * 80f) + " nits";
                }
                s += ", adapter " + p.target.adapter.High.ToString("x", inv) + ":" + p.target.adapter.Low.ToString("x", inv);
                facts[sn.gdiName] = (facts.TryGetValue(sn.gdiName, out string a) ? a : "?") + s;
            }
        }
        catch (Exception e) { facts["?"] = e.Message; }
        return facts;
    }

    static string R(RECT r) => r.Left + "," + r.Top + " .. " + r.Right + "," + r.Bottom + " (" + (r.Right - r.Left) + " x " + (r.Bottom - r.Top) + ")";
    static string W(Rect r, IFormatProvider inv) => "x " + r.xMin.ToString("0.##", inv) + ".." + r.xMax.ToString("0.##", inv) + ", y " + r.yMin.ToString("0.##", inv) + ".." + r.yMax.ToString("0.##", inv);
#endif

    BoxCollider2D MakeWall(string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        return go.AddComponent<BoxCollider2D>();
    }

    static void Place(BoxCollider2D c, Vector2 center, Vector2 size)
    {
        c.transform.position = new Vector3(center.x, center.y, 0f);
        c.size = size;
    }
}
