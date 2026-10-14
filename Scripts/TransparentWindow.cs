using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

/// <summary>
/// Turns the Windows player window into a transparent, click-through, always-on-top overlay over all your screens:
/// a borderless window exactly as big as all of them together, however Windows arranges them (ScreenBounds reads the
/// layout and puts the window back if it ever changes).
/// Requirements (applied by WhipIt > Setup): camera background RGBA 0,0,0,0 · URP HDR off ·
/// Direct3D11 · "Use DXGI flip model swapchain" off · Windowed, resizable.
/// Does nothing in the Editor. Global hotkey: Ctrl+Shift+Q quits.
/// </summary>
[DisallowMultipleComponent]
public class TransparentWindow : MonoBehaviour
{
    /// <summary>HWND of the player window (IntPtr.Zero in the Editor or before setup).</summary>
    public static IntPtr Handle { get; private set; }
    /// <summary>Where the window was last put (for whipit_screens.txt).</summary>
    public static string SpanInfo { get; private set; } = "not placed yet";

    [Tooltip("Mouse clicks go through to the apps underneath, except over grabbable objects (see Grabber).")]
    public bool clickThrough = true;
    public bool alwaysOnTop = true;
    [Tooltip("Clicking an object doesn't steal keyboard focus from the app you're working in.")]
    public bool noActivate = true;
    [Tooltip("Frames per second at most. The overlay is one window as big as all your screens together (with three screens " +
             "that can be 30+ million pixels); every frame Windows copies all of it, so drawing faster than this only costs.")]
    public int maxFps = 60;
    [Tooltip("Frames per second while you're away (no mouse or keyboard for a minute).")]
    public int idleFps = 30;

    static bool s_allowClickThrough = true;
    static bool s_clickThroughOn = true;
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    static bool s_noActivate = true;
    static IntPtr s_prevForeground;
#endif

    /// <summary>
    /// Take the keyboard (the chat box): lets this window be activated and brings it to the front, remembering which
    /// app had focus. Clicks still go through to the apps below. No-op in the Editor (click the Game view instead).
    /// </summary>
    public static void BeginTyping()
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        if (Handle == IntPtr.Zero) return;
        IntPtr fg = GetForegroundWindow();
        if (fg != Handle) s_prevForeground = fg;
        int ex = GetWindowLong(Handle, GWL_EXSTYLE) & ~WS_EX_NOACTIVATE;
        SetWindowLong(Handle, GWL_EXSTYLE, ex);
        // Windows only lets the foreground app hand out focus: borrow its input queue for a moment
        uint fgThread = GetWindowThreadProcessId(fg, out _), me = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(fgThread, me, true);
        SetForegroundWindow(Handle);
        SetFocus(Handle);
        if (attached) AttachThreadInput(fgThread, me, false);
#endif
    }

    /// <summary>Give the keyboard back to the app you were in, and go back to never taking focus.</summary>
    public static void EndTyping()
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        if (Handle == IntPtr.Zero) return;
        int ex = GetWindowLong(Handle, GWL_EXSTYLE);
        if (s_noActivate) ex |= WS_EX_NOACTIVATE;
        SetWindowLong(Handle, GWL_EXSTYLE, ex);
        if (s_prevForeground != IntPtr.Zero && IsWindow(s_prevForeground) && GetForegroundWindow() == Handle) SetForegroundWindow(s_prevForeground);
        s_prevForeground = IntPtr.Zero;
#endif
    }

    /// <summary>
    /// Switch click-through on/off at runtime (off = this window swallows the click instead of the app below).
    /// Called every frame by Grabber; only touches the window style when the state changes. No-op in the Editor.
    /// </summary>
    public static void SetClickThrough(bool on)
    {
        on &= s_allowClickThrough;
        if (on == s_clickThroughOn) return;
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        if (Handle == IntPtr.Zero) return;
        int ex = GetWindowLong(Handle, GWL_EXSTYLE);
        ex = on ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        SetWindowLong(Handle, GWL_EXSTYLE, ex);
#endif
        s_clickThroughOn = on;
    }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    [StructLayout(LayoutKind.Sequential)] struct MARGINS { public int Left, Right, Top, Bottom; }
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
    [DllImport("user32.dll")] static extern IntPtr GetActiveWindow();
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int index, int value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS margins);
    [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);

    const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    const int WS_POPUP = unchecked((int)0x80000000), WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_SYSMENU = 0x00080000,
        WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000;
    const int WS_EX_TRANSPARENT = 0x00000020, WS_EX_LAYERED = 0x00080000, WS_EX_NOACTIVATE = 0x08000000;
    const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020, SWP_SHOWWINDOW = 0x0040;
    static readonly IntPtr HWND_TOPMOST = new IntPtr(-1), HWND_NOTOPMOST = new IntPtr(-2);

    static readonly IntPtr PerMonitorV2 = new IntPtr(-4);
    static bool s_noDpiApi;

    /// <summary>
    /// The Win32 calls until Restore see physical pixels (the same on every screen, whatever its scaling and whatever DPI
    /// mode the player runs in). Returns what to give Restore.
    /// </summary>
    public static IntPtr Physical()
    {
        if (s_noDpiApi) return IntPtr.Zero;
        try { return SetThreadDpiAwarenessContext(PerMonitorV2); }
        catch (Exception) { s_noDpiApi = true; return IntPtr.Zero; } // (older than Windows 10 1607)
    }

    public static void Restore(IntPtr old)
    {
        if (old == IntPtr.Zero || s_noDpiApi) return;
        try { SetThreadDpiAwarenessContext(old); } catch (Exception) { s_noDpiApi = true; }
    }

    static bool s_topmost = true;

    /// <summary>The window over all your screens: no frame, exactly as big as all of them together (physical pixels).</summary>
    public static void Span()
    {
        if (Handle == IntPtr.Zero || !ScreenBounds.VirtualDesktop(out RectInt v)) return;
        int style = GetWindowLong(Handle, GWL_STYLE);
        int want = (style & ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX)) | WS_POPUP;
        if (want != style) SetWindowLong(Handle, GWL_STYLE, want);
        IntPtr old = Physical();
        try
        {
            SetWindowPos(Handle, s_topmost ? HWND_TOPMOST : HWND_NOTOPMOST, v.x, v.y, v.width, v.height,
                SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        }
        finally { Restore(old); }
        string dpi = "?";
        try { dpi = new[] { "not DPI aware", "system DPI aware", "per-monitor DPI aware" }[Mathf.Clamp(GetAwarenessFromDpiAwarenessContext(GetWindowDpiAwarenessContext(Handle)), 0, 2)]; }
        catch (Exception) { }
        SpanInfo = "put at " + v.x + "," + v.y + " size " + v.width + " x " + v.height + ", " + dpi;
    }

    static uint s_pid;
    static IntPtr s_found;
    static readonly StringBuilder s_class = new StringBuilder(64);

    [AOT.MonoPInvokeCallback(typeof(EnumWindowsProc))]
    static bool FindPlayerWindow(IntPtr hWnd, IntPtr lParam)
    {
        GetWindowThreadProcessId(hWnd, out uint pid);
        if (pid != s_pid) return true;
        s_class.Length = 0;
        GetClassName(hWnd, s_class, s_class.Capacity);
        if (s_class.ToString() != "UnityWndClass") return true;
        s_found = hWnd;
        return false; // stop
    }

    static IntPtr FindWindowHandle()
    {
        s_pid = GetCurrentProcessId();
        s_found = IntPtr.Zero;
        EnumWindows(FindPlayerWindow, IntPtr.Zero);
        return s_found != IntPtr.Zero ? s_found : GetActiveWindow();
    }

    bool quitWasHeld;

    IEnumerator Start()
    {
        Application.runInBackground = true;
        HookPlayerLoop();
        // a normal window first (a full-screen one only covers one screen); Span makes it borderless and as big as all of them
        if (Screen.fullScreenMode != FullScreenMode.Windowed) Screen.fullScreenMode = FullScreenMode.Windowed;

        yield return null; // let the player finish creating/resizing its window
        yield return null;

        Handle = FindWindowHandle();
        if (Handle == IntPtr.Zero) { Debug.LogError("[WhipIt] Player window not found."); yield break; }
        s_topmost = alwaysOnTop;
        Span();

        var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(Handle, ref margins); // per-pixel alpha from the swapchain

        int ex = GetWindowLong(Handle, GWL_EXSTYLE) | WS_EX_LAYERED;
        if (clickThrough) ex |= WS_EX_TRANSPARENT;
        if (noActivate) ex |= WS_EX_NOACTIVATE;
        SetWindowLong(Handle, GWL_EXSTYLE, ex);
        s_allowClickThrough = clickThrough;
        s_clickThroughOn = clickThrough;
        s_noActivate = noActivate;

        SetWindowPos(Handle, alwaysOnTop ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
    }

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("TransparentWindow.Update", t0); }
    void UpdateTimed()
    {
        bool quit = DesktopInput.ChordHeld(DesktopInput.VK_CONTROL, DesktopInput.VK_SHIFT, DesktopInput.VK_Q);
        if (quit && !quitWasHeld) Application.Quit();
        quitWasHeld = quit;
        Pace();
        Perf();
    }

    // the phases of Unity's frame, timed for whipit_perf.txt (a marker at the start and the end of each): our scripts are in
    // Update / PreLateUpdate / FixedUpdate, the drawing in PostLateUpdate, the wait for the frame rate cap in TimeUpdate
    static long[] s_phaseT0;
    static void HookPlayerLoop()
    {
        try
        {
            var root = UnityEngine.LowLevel.PlayerLoop.GetCurrentPlayerLoop();
            var subs = root.subSystemList;
            if (subs == null || s_phaseT0 != null) return;
            s_phaseT0 = new long[subs.Length];
            for (int i = 0; i < subs.Length; i++)
            {
                int k = i;
                string key = "Frame." + (subs[i].type != null ? subs[i].type.Name : "phase" + i);
                var list = new List<UnityEngine.LowLevel.PlayerLoopSystem>();
                list.Add(new UnityEngine.LowLevel.PlayerLoopSystem { type = typeof(TransparentWindow), updateDelegate = () => s_phaseT0[k] = WhipProf.T() });
                if (subs[i].subSystemList != null) list.AddRange(subs[i].subSystemList);
                list.Add(new UnityEngine.LowLevel.PlayerLoopSystem { type = typeof(TransparentWindow), updateDelegate = () => WhipProf.Add(key, s_phaseT0[k]) });
                subs[i].subSystemList = list.ToArray();
            }
            root.subSystemList = subs;
            UnityEngine.LowLevel.PlayerLoop.SetPlayerLoop(root);
        }
        catch (Exception e) { Debug.LogWarning("[WhipIt] Couldn't time the frame: " + e.Message); }
    }

    // the frame rate: capped (no vsync: the window spans screens with different refresh rates), lower while you're away
    float nextPace;
    void Pace()
    {
        if (Time.unscaledTime < nextPace) return;
        nextPace = Time.unscaledTime + 1f;
        if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
        int fps = DesktopInput.IdleSeconds > 60f ? Mathf.Min(idleFps, maxFps) : maxFps;
        fps = fps > 0 ? Mathf.Max(fps, 15) : -1;
        if (Application.targetFrameRate != fps) Application.targetFrameRate = fps;
    }

    // how fast it runs, every 10 s, into whipit_perf.txt next to the saves (the last 60 lines): frames per second, the
    // slowest frame, and (with Frame Timing Stats on, WhipIt > Setup) the main thread, the render thread, the graphics
    // card and the wait for Windows to take the frame
    float perfT0 = -1f, perfWorst;
    int perfFrames, perfTimed;
    double perfMain, perfRender, perfGpu, perfWait;
    readonly FrameTiming[] timing = new FrameTiming[1];
    readonly List<string> perfLines = new List<string>();

    void Perf()
    {
        float now = Time.unscaledTime;
        if (perfT0 < 0f) { perfT0 = now; return; }
        perfFrames++;
        perfWorst = Mathf.Max(perfWorst, Time.unscaledDeltaTime);
        try
        {
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, timing) > 0)
            {
                perfMain += timing[0].cpuMainThreadFrameTime;
                perfRender += timing[0].cpuRenderThreadFrameTime;
                perfGpu += timing[0].gpuFrameTime;
                perfWait += timing[0].cpuMainThreadPresentWaitTime;
                perfTimed++;
            }
        }
        catch (Exception) { }
        if (now - perfT0 < 10f) return;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        float span = now - perfT0;
        string line = DateTime.Now.ToString("HH:mm:ss", inv) + "  " + (perfFrames / span).ToString("0.0", inv) + " fps, frame " +
            (1000f * span / Mathf.Max(1, perfFrames)).ToString("0.0", inv) + " ms (slowest " + (1000f * perfWorst).ToString("0", inv) + ")";
        if (perfTimed > 0)
            line += ", main thread " + (perfMain / perfTimed).ToString("0.0", inv) + " ms, render thread " + (perfRender / perfTimed).ToString("0.0", inv) +
                    ", GPU " + (perfGpu / perfTimed).ToString("0.0", inv) + ", waiting for Windows " + (perfWait / perfTimed).ToString("0.0", inv);
        else line += ", no frame timings (turn on Frame Timing Stats: WhipIt > Setup)";
        line += ", target " + Application.targetFrameRate + (DesktopInput.IdleSeconds > 60f ? " (away)" : "");
        line += "\n      Unity's frame (ms per frame): " + WhipProf.Top(perfFrames, 12, "Frame.", false);
        line += "\n      ours (ms per frame): " + WhipProf.Top(perfFrames, 16, "Frame.", true);
        line += "\n      desktop icons: " + DesktopIcons.Status + "; " + DesktopPlatforms.IconReport;
        WhipProf.Clear();
        perfLines.Add(line);
        if (perfLines.Count > 30) perfLines.RemoveAt(0);
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("WhipIt speed - window " + Screen.width + " x " + Screen.height + " (" + (Screen.width * (double)Screen.height / 1e6).ToString("0.0", inv) +
                          " million pixels), drawn by " + SystemInfo.graphicsDeviceName + " (" + SystemInfo.graphicsDeviceType + ")");
            if (ScreenBounds.LeftOut.Length > 0) sb.AppendLine("Screens left out (on another graphics chip): " + ScreenBounds.LeftOut);
            foreach (var l in perfLines) sb.AppendLine(l);
            System.IO.File.WriteAllText(System.IO.Path.Combine(Application.persistentDataPath, "whipit_perf.txt"), sb.ToString());
        }
        catch (Exception) { }
        perfT0 = now; perfFrames = 0; perfWorst = 0f; perfTimed = 0; perfMain = perfRender = perfGpu = perfWait = 0;
    }
#endif
}
