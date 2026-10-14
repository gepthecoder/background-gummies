using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The buddies live in the desktop, not on top of everything: the desktop (wallpaper, icons, the floor along the taskbar)
/// is at the back, every open window is a layer in front of it in the order Windows stacks them (DesktopPlatforms.Windows,
/// front-most first). A buddy, a snack, the ball... belongs to a layer: the desktop, the window it stands on or climbs,
/// or in front of everything (Front: you're holding it, it's flying after a throw). Windows in front of its layer hide it
/// there. The overlay itself stays on top of everything and just doesn't draw what a window in front would cover.
/// Ctrl+Shift+B (or chat "on top" / "background") switches this off and on: off = always in front, like before.
/// </summary>
public static class Occlusion
{
    /// <summary>In front of every window.</summary>
    public const int Front = -1;
    /// <summary>The desktop: behind every window.</summary>
    public static int Back => DesktopPlatforms.Windows.Count;

    static bool s_enabled = true;
    /// <summary>Background mode (the default): windows in front of things hide them. Off: always in front.</summary>
    public static bool Enabled
    {
        get => s_enabled;
        set { if (s_enabled == value) return; s_enabled = value; Toggled?.Invoke(value); }
    }
    /// <summary>Background mode was switched on (true) or off (false).</summary>
    public static event Action<bool> Toggled;

    static bool s_wasHotkey;
    static int s_polledFrame = -1;

    /// <summary>Ctrl+Shift+B switches background mode (called by every BuddyLayer; acts once a frame). Only that key
    /// combination is read.</summary>
    public static void PollHotkey()
    {
        if (Time.frameCount == s_polledFrame) return;
        s_polledFrame = Time.frameCount;
        bool hk = DesktopInput.ChordHeld(DesktopInput.VK_CONTROL, DesktopInput.VK_SHIFT, DesktopInput.VK_B);
        if (hk && !s_wasHotkey) Enabled = !Enabled;
        s_wasHotkey = hk;
    }

    /// <summary>The layer of a window: its place in the stack (0 = front-most). The desktop, an icon, another screen's floor
    /// or a window that's gone: Back.</summary>
    public static int LayerOf(long windowId)
    {
        if (windowId == 0 || DesktopPlatforms.IsIcon(windowId) || DesktopPlatforms.IsScreen(windowId)) return Back;
        var w = DesktopPlatforms.Windows;
        for (int i = 0; i < w.Count; i++) if (w[i].id == windowId) return i;
        return Back;
    }

    /// <summary>Is this window still open (in the stack)?</summary>
    public static bool Exists(long windowId)
    {
        var w = DesktopPlatforms.Windows;
        for (int i = 0; i < w.Count; i++) if (w[i].id == windowId) return true;
        return false;
    }

    /// <summary>Is this point covered by a window in front of this layer?</summary>
    public static bool Hidden(Vector2 p, int layer)
    {
        if (!s_enabled || layer <= 0) return false;
        var w = DesktopPlatforms.Windows;
        int n = Mathf.Min(layer, w.Count);
        for (int i = 0; i < n; i++) if (!w[i].screen && w[i].Contains(p)) return true;
        return false;
    }

    /// <summary>The windows in front of this layer that overlap the box (front-most first, at most max), as world rects.</summary>
    public static void Occluders(int layer, Rect box, List<Rect> into, int max)
    {
        if (!s_enabled || layer <= 0) return;
        var w = DesktopPlatforms.Windows;
        int n = Mathf.Min(layer, w.Count);
        for (int i = 0; i < n && into.Count < max; i++)
        {
            WindowBox b = w[i];
            if (b.screen) continue; // (not a window: a bit of the world on no screen)
            if (b.xMax <= box.xMin || b.xMin >= box.xMax || b.yMax <= box.yMin || b.yMin >= box.yMax) continue;
            into.Add(Rect.MinMaxRect(b.xMin, b.yMin, b.xMax, b.yMax));
        }
    }

    /// <summary>The layer of something lying on a platform (a window top, an icon, 0 = the floor).</summary>
    public static int LayerOfSurface(long platformId) => LayerOf(platformId);

    /// <summary>
    /// For a loose thing (a snack, the ball): held = in front; lying on a platform = that platform's layer (0 = the floor:
    /// the desktop); up in the air = where it was (lastWin: -1 = in front, it starts there when it drops in). Is it hidden
    /// at p right now?
    /// </summary>
    public static bool ThingHidden(Vector2 p, bool held, bool resting, long platform, ref long lastWin)
    {
        if (held) lastWin = -1;
        else if (resting) lastWin = DesktopPlatforms.IsIcon(platform) ? 0 : platform;
        if (lastWin > 0 && !Exists(lastWin)) lastWin = 0;
        int layer = lastWin < 0 ? Front : lastWin == 0 ? Back : LayerOf(lastWin);
        return Hidden(p, layer);
    }
}
