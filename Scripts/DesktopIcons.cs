using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

/// <summary>
/// Where your desktop icons are (Windows app only), read on a background thread every ~1.5 s through the documented
/// shell interface Explorer offers (IShellWindows -> the desktop's IFolderView: item count, item position, spacing; the
/// icon list window itself is found by its class, for its corner and whether icons are shown).
/// Only positions are read: never icon names, files or anything inside them, and icons are never moved. The Recycle
/// Bin is told apart by comparing its shell ID (it's the trampoline). Desktop icons switched off = none.
/// DesktopPlatforms turns these into perches and rope hooks (DesktopPlatforms.Icons).
/// </summary>
public static class DesktopIcons
{
    /// <summary>One icon in physical screen pixels: x, y = top-left of its cell, w, h = cell size (spacing).</summary>
    public struct Raw { public int x, y, w, h; public bool bin; }

    static readonly object s_lock = new object();
    static Raw[] s_icons = new Raw[0];
    static int s_version;
    static Thread s_thread;
    static volatile bool s_run;
    const int MaxIcons = 200;
    const int PollMs = 1500;

    /// <summary>What the last reading found, for whipit_perf.txt: how many icons and where the first few are, or the step
    /// that failed (positions only, never names).</summary>
    public static string Status { get { lock (s_lock) return s_status; } }
    static string s_status = "not read yet";

    /// <summary>Increments every time a new reading is in.</summary>
    public static int Version { get { lock (s_lock) return s_version; } }

    /// <summary>The latest icons (copied into list). False if there's no reading (yet, or not on Windows).</summary>
    public static bool Get(List<Raw> list)
    {
        list.Clear();
        lock (s_lock)
        {
            list.AddRange(s_icons);
            return s_version > 0;
        }
    }

    /// <summary>Start reading (the Windows app; does nothing elsewhere).</summary>
    public static void Start()
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        if (s_thread != null) return;
        s_run = true;
        s_thread = new Thread(Loop) { IsBackground = true, Name = "WhipIt desktop icons" };
        s_thread.Start();
#endif
    }

    public static void Stop()
    {
        s_run = false;
        s_thread = null;
    }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    static bool s_warned;

    static void Loop()
    {
        // physical pixels on every monitor, like the window positions (DWM frame bounds)
        try { SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch (Exception) { } // (Windows 10 1607+)
        int init = CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
        while (s_run)
        {
            var list = new List<Raw>();
            bool ok = false;
            string status;
            s_step = "";
            try { ok = Read(list); status = ok ? Describe(list) : "failed at " + s_step; }
            catch (Exception e)
            {
                status = "error at " + s_step + ": " + e.GetType().Name + " " + e.Message;
                if (!s_warned) { s_warned = true; Debug.LogWarning("[WhipIt] Couldn't read the desktop icons (buddies just won't use them): " + e.Message); }
            }
            lock (s_lock)
            {
                s_icons = ok ? list.ToArray() : new Raw[0];
                s_status = status;
                s_version++;
            }
            Thread.Sleep(PollMs);
        }
        if (init >= 0) CoUninitialize();
    }

    static string s_step = "";
    static int s_skipped;

    static string Describe(List<Raw> list)
    {
        if (s_step.Length > 0) return s_step; // (read fine, but no icons to read: why)
        var sb = new System.Text.StringBuilder();
        sb.Append(list.Count).Append(" icons");
        if (s_skipped > 0) sb.Append(" (").Append(s_skipped).Append(" couldn't be read)");
        if (list.Count > 0) sb.Append(", cells ").Append(list[0].w).Append('x').Append(list[0].h).Append(" px, first at");
        for (int i = 0; i < list.Count && i < 4; i++) sb.Append(' ').Append(list[i].x).Append(',').Append(list[i].y).Append(list[i].bin ? " (bin)" : "");
        return sb.ToString();
    }

    // which step of the reading failed (and its HRESULT), for Status
    static bool Fail(string step, int hr)
    {
        s_step = step + (hr != 0 ? " (0x" + hr.ToString("X8") + ")" : "");
        return false;
    }

    static bool Read(List<Raw> list)
    {
        IntPtr windows = IntPtr.Zero, disp = IntPtr.Zero, sp = IntPtr.Zero, browser = IntPtr.Zero, view = IntPtr.Zero, fv = IntPtr.Zero, bin = IntPtr.Zero;
        try
        {
            Guid clsid = CLSID_ShellWindows, iid = IID_IShellWindows;
            s_step = "ShellWindows";
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_LOCAL_SERVER | CLSCTX_INPROC_SERVER, ref iid, out windows);
            if (hr < 0 || windows == IntPtr.Zero) return Fail("ShellWindows", hr);
            var loc = new Variant { vt = VT_I4 }; // CSIDL_DESKTOP (0)
            var root = new Variant();             // VT_EMPTY
            s_step = "FindWindowSW";
            hr = Fn<FindWindowSW>(windows, 15)(windows, ref loc, ref root, SWC_DESKTOP, out _, SWFO_NEEDDISPATCH, out disp);
            if (hr != 0 || disp == IntPtr.Zero) return Fail("FindWindowSW (the desktop)", hr);
            Guid iidSp = IID_IServiceProvider;
            s_step = "IServiceProvider";
            hr = Marshal.QueryInterface(disp, ref iidSp, out sp);
            if (hr < 0) return Fail("IServiceProvider", hr);
            Guid sid = SID_STopLevelBrowser, iidSb = IID_IShellBrowser;
            s_step = "QueryService";
            hr = Fn<QueryService>(sp, 3)(sp, ref sid, ref iidSb, out browser);
            if (hr < 0 || browser == IntPtr.Zero) return Fail("QueryService (top-level browser)", hr);
            s_step = "QueryActiveShellView";
            hr = Fn<OutPtr>(browser, 15)(browser, out view); // IShellBrowser::QueryActiveShellView
            if (hr < 0 || view == IntPtr.Zero) return Fail("QueryActiveShellView", hr);
            // the desktop's icon list window (for its visibility and where its corner is). Found by window class: asking the
            // view for it (IOleWindow::GetWindow, an input-synchronous call) fails from here with RPC_E_CANTCALLOUT_ININPUTSYNCCALL
            s_step = "the icon list window";
            IntPtr lv = DesktopListView();
            if (lv == IntPtr.Zero) { s_step = "no icon list in the desktop view"; return true; }
            if (!IsWindowVisible(lv)) { s_step = "desktop icons are switched off"; return true; } // (none)
            s_step = "";
            Guid iidFv = IID_IFolderView;
            hr = Marshal.QueryInterface(view, ref iidFv, out fv);
            if (hr < 0) return Fail("IFolderView", hr);
            hr = Fn<ItemCount>(fv, 7)(fv, SVGIO_ALLVIEW, out int count);
            if (hr < 0) return Fail("ItemCount", hr);
            hr = Fn<OutPoint>(fv, 12)(fv, out POINT spacing); // IFolderView::GetSpacing
            if (hr < 0 || spacing.x <= 0 || spacing.y <= 0) return Fail("GetSpacing " + spacing.x + "x" + spacing.y, hr);
            s_step = "";
            try { SHGetSpecialFolderLocation(IntPtr.Zero, CSIDL_BITBUCKET, out bin); } catch (Exception) { bin = IntPtr.Zero; }
            var item = Fn<Item>(fv, 6);
            var pos = Fn<ItemPosition>(fv, 11);
            count = Math.Min(count, MaxIcons);
            s_skipped = 0;
            for (int i = 0; i < count; i++)
            {
                if (item(fv, i, out IntPtr pidl) < 0 || pidl == IntPtr.Zero) { s_skipped++; continue; }
                try
                {
                    if (pos(fv, pidl, out POINT pt) < 0) { s_skipped++; continue; }
                    IntPtr dpi = TransparentWindow.Physical(); // (physical pixels, like the windows and the screens)
                    ClientToScreen(lv, ref pt);
                    TransparentWindow.Restore(dpi);
                    bool isBin = false;
                    if (bin != IntPtr.Zero) { try { isBin = ILIsEqual(pidl, bin); } catch (Exception) { } }
                    list.Add(new Raw { x = pt.x, y = pt.y, w = spacing.x, h = spacing.y, bin = isBin });
                }
                finally { Marshal.FreeCoTaskMem(pidl); }
            }
            return true;
        }
        finally
        {
            if (fv != IntPtr.Zero) Marshal.Release(fv);
            if (view != IntPtr.Zero) Marshal.Release(view);
            if (browser != IntPtr.Zero) Marshal.Release(browser);
            if (sp != IntPtr.Zero) Marshal.Release(sp);
            if (disp != IntPtr.Zero) Marshal.Release(disp);
            if (windows != IntPtr.Zero) Marshal.Release(windows);
            if (bin != IntPtr.Zero) Marshal.FreeCoTaskMem(bin);
        }
    }

    // Progman > SHELLDLL_DefView > SysListView32; with a slideshow or animated wallpaper the view sits in a WorkerW instead
    static IntPtr DesktopListView()
    {
        IntPtr progman = FindWindow("Progman", null);
        IntPtr defView = progman != IntPtr.Zero ? FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null) : IntPtr.Zero;
        for (IntPtr w = IntPtr.Zero; defView == IntPtr.Zero && (w = FindWindowEx(IntPtr.Zero, w, "WorkerW", null)) != IntPtr.Zero;)
            defView = FindWindowEx(w, IntPtr.Zero, "SHELLDLL_DefView", null);
        return defView == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
    }

    // a COM method by its slot in the object's vtable
    static T Fn<T>(IntPtr obj, int slot) where T : class
    {
        IntPtr vtbl = Marshal.ReadIntPtr(obj);
        IntPtr fn = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer(fn, typeof(T)) as T;
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct Variant { public ushort vt, r1, r2, r3; public IntPtr a, b; } // VARIANT (16 / 24 bytes)

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FindWindowSW(IntPtr self, ref Variant loc, ref Variant root, int swClass, out int hwnd, int options, out IntPtr disp);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int QueryService(IntPtr self, ref Guid service, ref Guid riid, out IntPtr obj);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int OutPtr(IntPtr self, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ItemCount(IntPtr self, uint flags, out int count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Item(IntPtr self, int index, out IntPtr pidl);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ItemPosition(IntPtr self, IntPtr pidl, out POINT pt);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int OutPoint(IntPtr self, out POINT pt);

    static readonly Guid CLSID_ShellWindows = new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    static readonly Guid IID_IShellWindows = new Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85");
    static readonly Guid IID_IServiceProvider = new Guid("6D5140C1-7436-11CE-8034-00AA006009FA");
    static readonly Guid SID_STopLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
    static readonly Guid IID_IShellBrowser = new Guid("000214E2-0000-0000-C000-000000000046");
    static readonly Guid IID_IFolderView = new Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE");
    const int CLSCTX_INPROC_SERVER = 0x1, CLSCTX_LOCAL_SERVER = 0x4, COINIT_APARTMENTTHREADED = 0x2;
    const ushort VT_I4 = 3;
    const int SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1, CSIDL_BITBUCKET = 0x000a;
    const uint SVGIO_ALLVIEW = 2;

    [DllImport("ole32.dll")] static extern int CoInitializeEx(IntPtr reserved, int coinit);
    [DllImport("ole32.dll")] static extern void CoUninitialize();
    [DllImport("ole32.dll")] static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int ctx, ref Guid iid, out IntPtr obj);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hWnd, ref POINT pt);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("shell32.dll")] static extern int SHGetSpecialFolderLocation(IntPtr hwnd, int csidl, out IntPtr pidl);
    [DllImport("shell32.dll")] static extern bool ILIsEqual(IntPtr a, IntPtr b);
#endif
}
