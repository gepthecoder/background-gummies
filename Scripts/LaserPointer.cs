using System;
using UnityEngine;

/// <summary>
/// Laser pointer: Ctrl+Shift+L (or chat "laser") and a little red dot follows your cursor; the buddies chase it like cats
/// (BuddyLaser). Ctrl+Shift+L again (or chat "laser off") puts it away, and it goes off by itself when you leave the mouse
/// alone for a while. Only that one key combination is read, never anything else you type.
/// </summary>
[DefaultExecutionOrder(-40)] // after Grabber has the cursor, before the buddies look for the dot
[DisallowMultipleComponent]
public class LaserPointer : MonoBehaviour
{
    [Tooltip("Sprite material (WhipIt > Setup assigns it).")]
    public Material material;
    public Color color = new Color(1f, 0.12f, 0.10f);
    [Tooltip("The dot's size (world units).")]
    public float size = 0.2f;
    [Tooltip("Goes off by itself when the mouse hasn't moved for this long (seconds).")]
    public float offAfter = 30f;
    [Tooltip("Draw order (above the buddies).")]
    public int sortingOrder = 300;

    /// <summary>The one in the scene.</summary>
    public static LaserPointer Current { get; private set; }
    /// <summary>The dot is on.</summary>
    public static bool On => Current && Current.on;
    /// <summary>Where the dot is (world units).</summary>
    public static Vector2 Dot => Current ? Current.dot : Vector2.zero;
    /// <summary>How fast the dot is moving (world units / s, smoothed).</summary>
    public static Vector2 DotVelocity => Current ? Current.vel : Vector2.zero;
    /// <summary>It was switched on (true) or off (false).</summary>
    public static event Action<bool> Toggled;

    SpriteRenderer sr;
    bool on, wasHotkey;
    Vector2 dot, last, vel;
    float stillFor, t;
    Sprite dotSprite;
    bool baked;

    void Awake()
    {
        var go = new GameObject("Laser Dot");
        go.transform.SetParent(transform, false);
        sr = go.AddComponent<SpriteRenderer>();
        sr.sortingOrder = sortingOrder;
        if (material) sr.sharedMaterial = material;
        sr.enabled = false;
    }

    void OnEnable() { Current = this; }
    void OnDisable() { if (Current == this) Current = null; if (on) Turn(false); }

    /// <summary>Switch it on or off.</summary>
    public void Turn(bool value)
    {
        if (on == value) return;
        on = value;
        stillFor = 0f;
        dot = last = Grabber.CursorWorld;
        vel = Vector2.zero;
        if (!baked) { dotSprite = Bake(color); baked = true; }
        sr.sprite = dotSprite;
        sr.enabled = on;
        Toggled?.Invoke(on);
    }

    public void Toggle() => Turn(!on);

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("LaserPointer.Update", t0); }
    void UpdateTimed()
    {
        bool hk = DesktopInput.ChordHeld(DesktopInput.VK_CONTROL, DesktopInput.VK_SHIFT, DesktopInput.VK_L);
        if (hk && !wasHotkey) Toggle();
        wasHotkey = hk;
        if (!on) return;

        float dt = Mathf.Max(Time.deltaTime, 1e-4f);
        t += dt;
        Vector2 cur = Grabber.CursorWorld;
        Vector2 v = (cur - last) / dt;
        if (v.sqrMagnitude > 2500f) v = Vector2.zero; // (jumped: another screen)
        vel = Vector2.Lerp(vel, v, 1f - Mathf.Exp(-12f * dt));
        stillFor = (cur - last).sqrMagnitude > 1e-6f ? 0f : stillFor + dt;
        last = cur;
        // a real laser in a hand: it trembles a tiny bit
        Vector2 tremble = new Vector2(Mathf.Sin(t * 37f) + 0.6f * Mathf.Sin(t * 91f), Mathf.Sin(t * 43f + 1.3f) + 0.6f * Mathf.Sin(t * 83f)) * (0.006f * size / 0.2f);
        dot = cur + tremble;

        Rect r = ScreenBounds.WorldRect;
        bool offScreen = cur.x < r.xMin - 0.5f || cur.x > r.xMax + 0.5f || cur.y < r.yMin - 0.5f || cur.y > r.yMax + 0.5f;
        if ((offAfter > 0f && stillFor > offAfter) || offScreen) { Turn(false); return; }
    }

    void LateUpdate() { long t0 = WhipProf.T(); LateUpdateTimed(); WhipProf.Add("LaserPointer.LateUpdate", t0); }
    void LateUpdateTimed()
    {
        if (!on) return;
        float flick = 1f + 0.07f * Mathf.Sin(t * 53f) * Mathf.Sin(t * 17f);
        sr.transform.position = new Vector3(dot.x, dot.y, 0f);
        sr.transform.localScale = new Vector3(size * flick, size * flick, 1f);
    }

    void OnDestroy()
    {
        if (dotSprite) { Destroy(dotSprite.texture); Destroy(dotSprite); }
    }

    // a bright white-hot core, a red ring and a soft red glow
    static Sprite Bake(Color color)
    {
        const int R = 48;
        var tex = new Texture2D(R, R, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "Laser Dot" };
        var px = new Color32[R * R];
        for (int j = 0; j < R; j++)
            for (int i = 0; i < R; i++)
            {
                float x = (i + 0.5f) / R - 0.5f, y = (j + 0.5f) / R - 0.5f;
                Color c = DotPaint(Mathf.Sqrt(x * x + y * y) * 2f, color);
                px[j * R + i] = new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), Mathf.Clamp01(c.a));
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return Sprite.Create(tex, new Rect(0, 0, R, R), new Vector2(0.5f, 0.5f), R); // 1 unit wide
    }

    // r = 0 in the middle, 1 at the edge
    static Color DotPaint(float r, Color red)
    {
        float core = Mathf.Clamp01((0.2f - r) / 0.08f);    // white-hot middle
        float ring = Mathf.Clamp01((0.42f - r) / 0.08f);   // the dot
        float glow = Mathf.Clamp01(1f - r) * Mathf.Clamp01(1f - r) * 0.5f;
        Color c = Color.Lerp(red, new Color(1f, 0.93f, 0.9f), core * 0.9f);
        c.a = Mathf.Max(ring, glow);
        return c;
    }
}
