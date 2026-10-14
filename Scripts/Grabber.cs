using UnityEngine;

/// <summary>
/// Smart click-through + grab & drag for every IGrabTarget (rigid Grabbable, GummyBody, ...).
/// Each frame: if the cursor is over a target (or one is being held) the overlay window catches the mouse,
/// otherwise clicks go through to Windows as before. The target decides how it follows the cursor.
/// </summary>
[DefaultExecutionOrder(-50)] // before Whip / bodies, so Busy and CursorWorld are current
[DisallowMultipleComponent]
public class Grabber : MonoBehaviour
{
    /// <summary>True while the cursor is over a target or one is held. The whip ignores clicks then.</summary>
    public static bool Busy { get; private set; }
    /// <summary>Cursor position in world units, updated every frame (eyes look at it).</summary>
    public static Vector2 CursorWorld { get; private set; }

    public Camera cam;

    IGrabTarget hovered, held;
    bool wasDown;
    static Grabber s_inst;

    void Awake()
    {
        if (!cam) cam = Camera.main;
    }

    void OnEnable() => s_inst = this;

    /// <summary>
    /// What you're holding is something else now (you pulled a buddy out of a fused monster: you're holding the buddy;
    /// to = null: a buddy you were holding went into a monster, you're holding nothing). Only swaps what the cursor
    /// drives: the caller ends the old grab and begins the new one.
    /// </summary>
    public static void Handoff(IGrabTarget from, IGrabTarget to)
    {
        if (!s_inst || s_inst.held != from) return;
        s_inst.held = to;
        if (to == null) s_inst.SetHovered(null);
    }

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("Grabber.Update", t0); }
    void UpdateTimed()
    {
        if (!cam) return;
        Vector2 m = DesktopInput.MousePosition;
        Vector3 wp = cam.ScreenToWorldPoint(new Vector3(m.x, m.y, -cam.transform.position.z));
        Vector2 p = new Vector2(wp.x, wp.y);
        CursorWorld = p;
        bool down = DesktopInput.LeftButtonHeld;

        if (!Alive(held)) held = null;
        if (!Alive(hovered)) hovered = null;

        if (held == null) SetHovered(Find(p));

        if (down && !wasDown && hovered != null) { held = hovered; held.BeginGrab(p); }
        else if (!down && held != null) { held.EndGrab(); held = null; }

        if (held != null) held.DragTo(p);
        wasDown = down;

        Busy = held != null || hovered != null;
        TransparentWindow.SetClickThrough(!Busy);
    }

    void OnDisable()
    {
        if (s_inst == this) s_inst = null;
        if (Alive(held)) held.EndGrab();
        held = null;
        SetHovered(null);
        Busy = false;
        TransparentWindow.SetClickThrough(true);
    }

    static IGrabTarget Find(Vector2 p)
    {
        var all = GrabTargets.All;
        for (int i = all.Count - 1; i >= 0; i--) // last registered = drawn on top, roughly
            if (Alive(all[i]) && all[i].Contains(p)) return all[i];
        return null;
    }

    void SetHovered(IGrabTarget t)
    {
        if (hovered == t) return;
        if (Alive(hovered)) hovered.Hovered = false;
        hovered = t;
        if (hovered != null) hovered.Hovered = true;
    }

    /// <summary>Null-safe for destroyed Unity objects behind the interface.</summary>
    static bool Alive(IGrabTarget t) => t is Object o ? (bool)o : t != null;
}
