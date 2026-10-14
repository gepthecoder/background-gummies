using System;
using System.Runtime.InteropServices;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Mouse + hotkeys that keep working while the overlay window is click-through and unfocused.
/// Windows player: polls Win32 directly (Unity input never sees a click-through window).
/// Editor / other platforms: Input System (or legacy Input), so you can test in the Game view.
/// </summary>
public static class DesktopInput
{
    public const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_SPACE = 0x20, VK_B = 0x42, VK_K = 0x4B, VK_L = 0x4C, VK_Q = 0x51, VK_R = 0x52, VK_W = 0x57;

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr hWnd, ref POINT p);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint param, ref RECT rect, uint winIni);
    const int VK_LBUTTON = 0x01;
    const uint SPI_GETWORKAREA = 0x0030;
    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize, dwTime; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("kernel32.dll")] static extern uint GetTickCount();

    static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
#endif

    /// <summary>Cursor in Unity screen pixels (origin bottom-left of this window, which covers all your screens).</summary>
    public static Vector2 MousePosition
    {
        get
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            // physical pixels on every screen (whatever its scaling), then into the window that covers them all
            IntPtr old = TransparentWindow.Physical();
            GetCursorPos(out POINT p);
            TransparentWindow.Restore(old);
            if (ScreenBounds.PixelToUnity(p.X, p.Y, out Vector2 u)) return u;
            GetCursorPos(out p);
            IntPtr h = TransparentWindow.Handle;
            if (h != IntPtr.Zero) ScreenToClient(h, ref p);
            return new Vector2(p.X, Screen.height - 1 - p.Y);
#elif ENABLE_INPUT_SYSTEM
            return Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
#else
            return Input.mousePosition;
#endif
        }
    }

    public static bool LeftButtonHeld
    {
        get
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            return Down(VK_LBUTTON);
#elif ENABLE_INPUT_SYSTEM
            return Mouse.current != null && Mouse.current.leftButton.isPressed;
#else
            return Input.GetMouseButton(0);
#endif
        }
    }

    /// <summary>
    /// Bottom of the Windows work area (= top edge of a bottom taskbar) in Unity screen pixels.
    /// Returns false in the Editor / on other platforms (use the screen bottom).
    /// </summary>
    public static bool TryGetWorkAreaBottom(out float screenY)
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        var r = new RECT();
        if (SystemParametersInfo(SPI_GETWORKAREA, 0, ref r, 0))
        {
            var p = new POINT { X = r.Left, Y = r.Bottom };
            IntPtr h = TransparentWindow.Handle;
            if (h != IntPtr.Zero) ScreenToClient(h, ref p);
            screenY = Screen.height - p.Y;
            return true;
        }
#endif
        screenY = 0f;
        return false;
    }

    /// <summary>True while all three virtual keys are held. Global in the Windows player; in the Editor only while the
    /// Game view has focus (Ctrl, Shift, Space, B, K, L, Q, R, W).</summary>
    public static bool ChordHeld(int vk1, int vk2, int vk3)
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        return Down(vk1) && Down(vk2) && Down(vk3);
#elif ENABLE_INPUT_SYSTEM
        return KeyHeld(vk1) && KeyHeld(vk2) && KeyHeld(vk3);
#else
        return false;
#endif
    }

#if !(UNITY_STANDALONE_WIN && !UNITY_EDITOR) && ENABLE_INPUT_SYSTEM
    static bool KeyHeld(int vk)
    {
        var k = Keyboard.current;
        if (k == null) return false;
        switch (vk)
        {
            case VK_CONTROL: return k.ctrlKey.isPressed;
            case VK_SHIFT: return k.shiftKey.isPressed;
            case VK_SPACE: return k.spaceKey.isPressed;
            case VK_K: return k.kKey.isPressed;
            case VK_L: return k.lKey.isPressed;
            case VK_B: return k.bKey.isPressed;
            case VK_Q: return k.qKey.isPressed;
            case VK_R: return k.rKey.isPressed;
            case VK_W: return k.wKey.isPressed;
            default: return false;
        }
    }
#endif

    /// <summary>
    /// Seconds since you last touched the mouse or keyboard anywhere (only the time, never what you pressed).
    /// Windows player: GetLastInputInfo. Editor: mouse movement and clicks over the Game view.
    /// </summary>
    public static float IdleSeconds
    {
        get
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            if (GetLastInputInfo(ref info)) return (GetTickCount() - info.dwTime) / 1000f;
#endif
            Vector2 m = MousePosition;
            bool b = LeftButtonHeld;
            if ((m - s_lastMouse).sqrMagnitude > 1f || b) { s_lastMouse = m; s_lastActive = Time.unscaledTime; }
            return Time.unscaledTime - s_lastActive;
        }
    }
    static Vector2 s_lastMouse;
    static float s_lastActive;
}
