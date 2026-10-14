using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A little snack on the desktop (SnackDropper drops one whenever you copy something). It falls, bounces off window
/// sides, lands on the taskbar or on window tops and rides them when you move the window. You can grab and throw it; buddies eat it
/// (BuddyEater). Drawn in code: a cookie, a wrapped candy or a water drop; the candy cloud's sweets (CandyCloud: wrapped
/// sweets, a lollipop, a gumdrop, a jelly bean, a candy heart) are smaller, pile up and fade if nobody eats them. The
/// rare giant potion (GiantPotion) is a little glowing bottle with sparkles twinkling round it.
/// </summary>
[DisallowMultipleComponent]
public class Snack : MonoBehaviour, IGrabTarget
{
    public enum Kind { Cookie, Candy, Drop, Sweet, Potion }
    /// <summary>How many looks a Sweet has (variant 0..).</summary>
    public const int SweetLooks = 7;

    /// <summary>Every snack on screen that hasn't been eaten.</summary>
    public static readonly List<Snack> All = new List<Snack>();
    /// <summary>A snack touched down for the first time.</summary>
    public static event Action<Snack> Landed;

    public Kind kind;
    public float radius = 0.11f;
    public float gravity = 16f;
    [Range(0f, 1f)] public float bounce = 0.35f;
    public int sortingOrder = 8;
    /// <summary>Sweet: which look (0..SweetLooks-1).</summary>
    public int variant;
    /// <summary>XP for eating it (-1 = the buddy's usual snack XP).</summary>
    public int xp = -1;
    /// <summary>Fades away this long after it first lands, if nobody's eaten it (0 = stays).</summary>
    public float life;

    public bool Hovered { get; set; }
    public bool Held { get; private set; }
    /// <summary>Lying still on the floor or on a window.</summary>
    public bool Resting { get; private set; }
    public bool Eaten { get; private set; }
    public Vector2 Position => pos;
    public Vector2 Velocity => vel;
    /// <summary>Height of what it's lying on (a sweet on a pile: the floor or window top under the pile).</summary>
    public float SurfaceY => Stacked ? restY : pos.y - radius;
    /// <summary>Lying on other sweets (a pile) right now.</summary>
    public bool Stacked => kind == Kind.Sweet && stackFrame >= Time.frameCount - 1;
    /// <summary>Fading away (nobody ate it).</summary>
    public bool Fading => fadeT >= 0f;

    Vector2 pos, vel, grabOff, eatFrom;
    float angle, spin, eatT;
    long onPlat;
    long layerWin = -1; // Occlusion: in front while it drops in, then where it lands
    bool hidden;        // behind a window in front of where it is
    int platVersion = -1;
    bool landedOnce;
    float restY, sinceLanded, fadeT = -1f;
    int stackFrame = -10;
    SpriteRenderer sr;
    Func<Vector2> mouth;

    static Sprite[] s_sprites, s_sweets;

    /// <summary>Make a snack at a world position.</summary>
    public static Snack Spawn(Kind kind, Vector2 at, Vector2 velocity, Material material, int order) => Spawn(kind, 0, at, velocity, material, order);

    /// <summary>Make a snack at a world position (variant: the look of a Sweet).</summary>
    public static Snack Spawn(Kind kind, int variant, Vector2 at, Vector2 velocity, Material material, int order)
    {
        var go = new GameObject("Snack (" + kind + ")");
        var s = go.AddComponent<Snack>();
        s.kind = kind;
        s.variant = Mathf.Clamp(variant, 0, SweetLooks - 1);
        if (kind == Kind.Sweet) { s.radius = 0.085f; s.bounce = 0.42f; }
        if (kind == Kind.Potion) { s.radius = 0.13f; s.bounce = 0.3f; s.xp = 3; }
        s.restY = at.y - s.radius;
        s.sortingOrder = order;
        s.pos = at;
        s.vel = velocity;
        s.spin = UnityEngine.Random.Range(-200f, 200f);
        s.sr = go.AddComponent<SpriteRenderer>();
        s.sr.sprite = kind == Kind.Sweet ? SweetSprite(s.variant) : SpriteFor(kind);
        s.sr.sortingOrder = order;
        if (material) s.sr.sharedMaterial = material;
        float k = 2.3f * s.radius; // sprite is 1 unit wide, the shape fills ~87% of it
        go.transform.localScale = new Vector3(k, k, 1f);
        go.transform.position = new Vector3(at.x, at.y, 0f);
        if (kind == Kind.Potion) s.MakeShine(material, order);
        return s;
    }

    void OnEnable() { All.Add(this); GrabTargets.Add(this); }
    void OnDisable() { All.Remove(this); GrabTargets.Remove(this); Held = false; ShowShine(false); }
    void OnDestroy() { if (shine) Destroy(shine.gameObject); if (twinkles != null) foreach (var tw in twinkles) if (tw) Destroy(tw.gameObject); }

    // ------------------------------------------------------------------ grab

    public bool Contains(Vector2 p) => !Eaten && !hidden && fadeT < 0f && (p - pos).sqrMagnitude < radius * radius * 2f;

    public void BeginGrab(Vector2 p)
    {
        Held = true;
        Resting = false;
        onPlat = 0;
        grabOff = pos - p;
        vel = Vector2.zero;
    }

    public void DragTo(Vector2 p)
    {
        Vector2 target = p + grabOff;
        float dt = Mathf.Max(Time.unscaledDeltaTime, 1e-4f); // (your hand runs on real time, bullet time or not)
        vel = Vector2.Lerp(vel, (target - pos) / dt, 0.5f);
        pos = target;
    }

    public void EndGrab()
    {
        Held = false;
        vel = Vector2.ClampMagnitude(vel, 14f);
    }

    /// <summary>Knocked by something (the apple's whip): flies off with this velocity.</summary>
    public void Knock(Vector2 velocity)
    {
        if (Held || Eaten) return;
        vel = velocity;
        onPlat = 0;
        Resting = false;
        spin = UnityEngine.Random.Range(-600f, 600f);
    }

    /// <summary>A sweet in a pile: shoved by its neighbours (fromBelow = one under it holds it up; supportY: what the pile stands on).</summary>
    public void Push(Vector2 offset, bool fromBelow, float supportY)
    {
        if (Held || Eaten) return;
        pos += offset;
        if (!fromBelow) return;
        stackFrame = Time.frameCount;
        restY = supportY;
        if (vel.y < 0f) vel.y = 0f;
    }

    /// <summary>Start fading away now (too many sweets lying around).</summary>
    public void FadeNow()
    {
        if (Eaten || fadeT >= 0f) return;
        fadeT = 0f;
        Held = false;
        All.Remove(this);
        GrabTargets.Remove(this);
    }

    /// <summary>Gets eaten: flies into the mouth and disappears.</summary>
    public void Eat(Func<Vector2> mouthPosition)
    {
        if (Eaten) return;
        Eaten = true;
        Held = false;
        mouth = mouthPosition;
        eatFrom = pos;
        eatT = 0f;
        All.Remove(this);
        GrabTargets.Remove(this);
    }

    // ------------------------------------------------------------------ motion

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("Snack.Update", t0); }
    void UpdateTimed()
    {
        float dt = Mathf.Min(Time.deltaTime, 1f / 30f);
        if (Eaten)
        {
            eatT += dt;
            float u = Mathf.Clamp01(eatT / 0.2f);
            pos = Vector2.Lerp(eatFrom, mouth != null ? mouth() : eatFrom, u * u);
            float k = 2.3f * radius * (1f - u);
            transform.localScale = new Vector3(k, k, 1f);
            transform.position = new Vector3(pos.x, pos.y, 0f);
            if (shine) Shine(dt, 1f - u);
            if (u >= 1f) { ShowShine(false); Destroy(gameObject); }
            return;
        }
        if (fadeT >= 0f)
        {
            // nobody ate it: it shrinks and fades
            fadeT += dt;
            float f = Mathf.Clamp01(1f - fadeT / 0.8f);
            if (sr) sr.color = new Color(1f, 1f, 1f, f);
            float k = 2.3f * radius * (0.6f + 0.4f * f);
            transform.localScale = new Vector3(k, k, 1f);
            if (f <= 0f) Destroy(gameObject);
        }
        else if (life > 0f && landedOnce && !Held)
        {
            sinceLanded += dt;
            if (sinceLanded > life) FadeNow();
        }
        if (!Held)
        {
            Ride();
            const int sub = 2;
            for (int i = 0; i < sub; i++) Step(dt / sub);
        }
        transform.position = new Vector3(pos.x, pos.y, 0f);
        transform.localRotation = Quaternion.Euler(0f, 0f, angle);
        hidden = Occlusion.ThingHidden(pos, Held, Resting, onPlat, ref layerWin);
        if (sr && sr.enabled == hidden) sr.enabled = !hidden;
        if (shine) Shine(dt, hidden ? 0f : fadeT >= 0f ? Mathf.Clamp01(1f - fadeT / 0.8f) : 1f);
    }

    // ------------------------------------------------------------------ the potion's glow and sparkles

    SpriteRenderer shine;
    SpriteRenderer[] twinkles;
    float shineT;
    const int Twinkles = 3;
    static Sprite s_glow, s_star;

    void MakeShine(Material material, int order)
    {
        shine = Extra("Potion Glow", GlowSprite(), order - 1, material);
        twinkles = new SpriteRenderer[Twinkles];
        for (int i = 0; i < Twinkles; i++) twinkles[i] = Extra("Potion Sparkle", StarSprite(), order + 1, material);
        shineT = UnityEngine.Random.value * 10f;
    }

    static SpriteRenderer Extra(string name, Sprite sprite, int order, Material material)
    {
        var go = new GameObject(name); // (not a child: it doesn't spin with the bottle; OnDestroy takes it along)
        var r = go.AddComponent<SpriteRenderer>();
        r.sprite = sprite;
        r.sortingOrder = order;
        if (material) r.sharedMaterial = material;
        r.enabled = false;
        return r;
    }

    void ShowShine(bool on)
    {
        if (shine && shine.enabled != on) shine.enabled = on;
        if (twinkles != null) foreach (var tw in twinkles) if (tw && tw.enabled != on) tw.enabled = on;
    }

    // a soft pink glow breathing round it, three little stars twinkling in and out around it (a = how much shows)
    void Shine(float dt, float a)
    {
        shineT += dt;
        ShowShine(a > 0.01f);
        if (a <= 0.01f) return;
        float pulse = 0.5f + 0.5f * Mathf.Sin(shineT * 3.2f);
        float g = radius * (5.2f + 0.9f * pulse) * Mathf.Max(a, 0.3f);
        shine.transform.position = new Vector3(pos.x, pos.y, 0f);
        shine.transform.localScale = new Vector3(g, g, 1f);
        shine.color = new Color(1f, 0.55f, 1f, (0.45f + 0.25f * pulse) * a);
        for (int i = 0; i < Twinkles; i++)
        {
            float ph = shineT * 0.9f + i / (float)Twinkles;
            float cyc = ph - Mathf.Floor(ph), life = Mathf.Sin(cyc * Mathf.PI); // (each one: in, out, somewhere else)
            int n = Mathf.FloorToInt(ph) * 7 + i * 13;
            float ang = Hash(n, 1f) * 2f * Mathf.PI, dist = radius * (1.4f + 1.1f * Hash(n, 2f));
            var tw = twinkles[i];
            tw.transform.position = new Vector3(pos.x + Mathf.Cos(ang) * dist, pos.y + Mathf.Sin(ang) * dist + 0.05f * cyc, 0f);
            float s = radius * 1.1f * life;
            tw.transform.localScale = new Vector3(s, s, 1f);
            tw.color = new Color(1f, 0.95f, 1f, life * a);
        }
    }

    internal static Sprite GlowSprite()
    {
        if (s_glow) return s_glow;
        const int res = 32;
        var tex = new Texture2D(res, res, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "Potion Glow" };
        var px = new Color32[res * res];
        for (int y = 0; y < res; y++)
            for (int x = 0; x < res; x++)
            {
                float u = (x + 0.5f) / res * 2f - 1f, v = (y + 0.5f) / res * 2f - 1f, r = Mathf.Clamp01(Mathf.Sqrt(u * u + v * v));
                px[y * res + x] = new Color(1f, 1f, 1f, (1f - r) * (1f - r));
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return s_glow = Sprite.Create(tex, new Rect(0, 0, res, res), new Vector2(0.5f, 0.5f), res); // 1 unit wide
    }

    internal static Sprite StarSprite()
    {
        if (s_star) return s_star;
        const int res = 16;
        var tex = new Texture2D(res, res, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "Potion Sparkle" };
        var px = new Color32[res * res];
        for (int y = 0; y < res; y++)
            for (int x = 0; x < res; x++)
                px[y * res + x] = StarPaint((x + 0.5f) / res * 2f - 1f, (y + 0.5f) / res * 2f - 1f);
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return s_star = Sprite.Create(tex, new Rect(0, 0, res, res), new Vector2(0.5f, 0.5f), res);
    }

    /// <summary>A four-pointed twinkle (u, v in -1..1): thin bright rays and a soft middle.</summary>
    internal static Color StarPaint(float u, float v)
    {
        float ax = Mathf.Abs(u), ay = Mathf.Abs(v);
        float ray = Mathf.Max(Mathf.Clamp01(1f - ax / 0.16f) * Mathf.Clamp01(1f - ay), Mathf.Clamp01(1f - ay / 0.16f) * Mathf.Clamp01(1f - ax));
        float core = Mathf.Clamp01(1f - Mathf.Sqrt(u * u + v * v) / 0.45f);
        return new Color(1f, 1f, 1f, Mathf.Clamp01(ray * 1.3f + core * core));
    }

    void Ride()
    {
        if (DesktopPlatforms.Version == platVersion) return;
        platVersion = DesktopPlatforms.Version;
        if (onPlat == 0) return;
        var all = DesktopPlatforms.Current;
        for (int k = 0; k < all.Count; k++)
        {
            if (all[k].id != onPlat) continue;
            pos += new Vector2(all[k].dx, all[k].dy);
            return;
        }
        onPlat = 0; // the window is gone: fall
    }

    void Step(float h)
    {
        Rect r = ScreenBounds.WorldRect;
        vel.y -= gravity * h;
        vel *= 1f - 0.2f * h;
        Vector2 prev = pos;
        pos += vel * h;
        angle += spin * h;

        bool grounded = false;
        if (pos.x < r.xMin + radius) { pos.x = r.xMin + radius; if (vel.x < 0f) vel.x = -vel.x * bounce; spin = -spin * 0.5f; }
        if (pos.x > r.xMax - radius) { pos.x = r.xMax - radius; if (vel.x > 0f) vel.x = -vel.x * bounce; spin = -spin * 0.5f; }
        if (pos.y > r.yMax - radius) { pos.y = r.yMax - radius; if (vel.y > 0f) vel.y = -vel.y * bounce; }
        if (pos.y < r.yMin + radius) { pos.y = r.yMin + radius; Touch(0); grounded = true; }

        // window tops: one-sided, only from above
        var all = DesktopPlatforms.Current;
        for (int k = 0; k < all.Count && !grounded; k++)
        {
            Platform pl = all[k];
            if (pos.x < pl.x0 || pos.x > pl.x1 || prev.y - radius < pl.y - 1e-4f || pos.y - radius >= pl.y) continue;
            pos.y = pl.y + radius;
            Touch(pl.id);
            grounded = true;
        }
        if (!grounded) onPlat = 0;
        if (!grounded && Stacked)
        {
            // on a pile of sweets: it stands on the ones below
            grounded = true;
            if (vel.y < 0f) vel.y = 0f;
            if (!landedOnce) { landedOnce = true; Landed?.Invoke(this); }
        }

        // window sides and bottoms (solid windows); a window opened on top of it pops it out on top
        var wins = DesktopPlatforms.Windows;
        for (int k = 0; k < wins.Count; k++)
        {
            WindowBox w = wins[k];
            if (!w.movable) continue;
            float x0 = w.xMin - radius, x1 = w.xMax + radius, y0 = w.yMin - radius;
            if (pos.x <= x0 || pos.x >= x1 || pos.y <= y0 || pos.y - radius >= w.yMax) continue;
            if (prev.y - radius >= w.yMax - 1e-4f) continue;                                   // from above: the top catches it
            if (prev.x <= x0 + 1e-4f) { pos.x = x0; if (vel.x > 0f) vel.x = -vel.x * bounce; }
            else if (prev.x >= x1 - 1e-4f) { pos.x = x1; if (vel.x < 0f) vel.x = -vel.x * bounce; }
            else if (prev.y <= y0 + 1e-4f) { pos.y = y0; if (vel.y > 0f) vel.y = -vel.y * bounce; }
            else if (w.yMax + 2f * radius < r.yMax) { pos.y = w.yMax + radius; vel = new Vector2(vel.x, 2f); onPlat = 0; grounded = false; }
        }

        if (grounded)
        {
            vel.x *= Mathf.Exp(-6f * h);                              // friction
            spin = Mathf.Lerp(spin, -vel.x / radius * Mathf.Rad2Deg, 0.3f); // roll
        }
        Resting = grounded && vel.sqrMagnitude < 0.01f;
    }

    void Touch(long platform)
    {
        onPlat = platform;
        restY = pos.y - radius;
        float impact = -vel.y;
        if (vel.y < 0f) vel.y = impact > 1.2f ? impact * bounce : 0f;
        if (!landedOnce) { landedOnce = true; Landed?.Invoke(this); }
    }

    // ------------------------------------------------------------------ drawing (baked once per kind)

    static Sprite SpriteFor(Kind kind)
    {
        if (s_sprites == null) s_sprites = new Sprite[5];
        int i = (int)kind;
        if (!s_sprites[i]) s_sprites[i] = Bake(kind);
        return s_sprites[i];
    }

    static Sprite Bake(Kind kind)
    {
        const int res = 64;
        const float texel = 2f / res, rimWidth = 0.09f;
        var tex = new Texture2D(res, res, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "Snack " + kind
        };
        var px = new Color32[res * res];
        for (int y = 0; y < res; y++)
            for (int x = 0; x < res; x++)
            {
                float u = (x + 0.5f) * texel - 1f, v = (y + 0.5f) * texel - 1f;
                Color fill, rim;
                float d = kind == Kind.Cookie ? Cookie(u, v, texel, out fill, out rim)
                        : kind == Kind.Candy ? Candy(u, v, out fill, out rim)
                        : kind == Kind.Potion ? Potion(u, v, texel, out fill, out rim)
                        : Drop(u, v, texel, out fill, out rim);
                float band = Mathf.Clamp01(0.5f + (d + rimWidth) / texel);
                Color c = Color.Lerp(fill, rim, band);
                c.r = Mathf.Clamp01(c.r); c.g = Mathf.Clamp01(c.g); c.b = Mathf.Clamp01(c.b);
                c.a = Mathf.Clamp01(0.5f - d / texel);
                px[y * res + x] = c;
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return Sprite.Create(tex, new Rect(0, 0, res, res), new Vector2(0.5f, 0.5f), res); // 1 unit wide
    }

    static float Cookie(float u, float v, float texel, out Color fill, out Color rim)
    {
        float r = Mathf.Sqrt(u * u + v * v), th = Mathf.Atan2(v, u);
        float d = r - (0.84f + 0.035f * Mathf.Sin(7f * th) + 0.02f * Mathf.Sin(13f * th + 1f));
        float shade = Mathf.Clamp01(0.5f + 0.35f * (-u * 0.6f + v * 0.8f) / 0.9f);
        fill = new Color(0.87f, 0.63f, 0.36f) * (0.85f + 0.3f * shade);
        float chip = 9f;
        chip = Mathf.Min(chip, Dist(u, v, -0.35f, 0.3f)); chip = Mathf.Min(chip, Dist(u, v, 0.3f, 0.38f));
        chip = Mathf.Min(chip, Dist(u, v, 0.05f, -0.05f)); chip = Mathf.Min(chip, Dist(u, v, -0.3f, -0.38f));
        chip = Mathf.Min(chip, Dist(u, v, 0.42f, -0.28f)); chip = Mathf.Min(chip, Dist(u, v, -0.02f, 0.62f));
        fill = Color.Lerp(fill, new Color(0.38f, 0.22f, 0.12f), Mathf.Clamp01(0.5f - (chip - 0.12f) / texel));
        rim = new Color(0.55f, 0.34f, 0.17f);
        return d;
    }

    static float Candy(float u, float v, out Color fill, out Color rim)
    {
        float dSweet = Mathf.Sqrt(u * u + v * v) - 0.5f;
        float ax = Mathf.Abs(u), t = Mathf.Clamp01((ax - 0.4f) / 0.58f);
        float dWrap = Mathf.Max(Mathf.Abs(v) - (0.08f + 0.28f * t), Mathf.Max(0.4f - ax, ax - 0.98f)); // twisted ends
        if (dSweet < dWrap)
        {
            float a = Mathf.Atan2(v, u) + Mathf.Sqrt(u * u + v * v) * 5f;
            float stripe = Mathf.Clamp01(0.5f + 3f * Mathf.Sin(a * 3f)) * 0.75f; // swirl
            fill = Color.Lerp(new Color(0.98f, 0.36f, 0.52f), Color.white, stripe);
        }
        else fill = new Color(1f, 0.72f, 0.82f) * (0.85f + 0.15f * Mathf.Sin(v * 40f)); // pleats
        rim = new Color(0.62f, 0.12f, 0.25f);
        return Mathf.Min(dSweet, dWrap);
    }

    static float Drop(float u, float v, float texel, out Color fill, out Color rim)
    {
        // a round bottom and a cone up to the tip
        const float cy = -0.22f, r = 0.58f, tip = 0.92f - cy;
        float a = Mathf.Asin(r / tip), px = Mathf.Abs(u), py = v - cy, d;
        if (py > tip) d = Mathf.Sqrt(px * px + (py - tip) * (py - tip));
        else if (py > r * Mathf.Sin(a)) d = px * Mathf.Cos(a) + (py - tip) * Mathf.Sin(a);
        else d = Mathf.Sqrt(px * px + py * py) - r;
        float shade = Mathf.Clamp01(0.5f + 0.5f * (-u * 0.5f - v * 0.5f));
        fill = new Color(0.48f, 0.78f, 1f) * (0.8f + 0.3f * shade);
        float hx = (u + 0.2f) / 0.12f, hy = (v + 0.05f) / 0.2f;
        fill = Color.Lerp(fill, Color.white, Mathf.Clamp01(0.5f - (Mathf.Sqrt(hx * hx + hy * hy) - 1f) * 0.12f / texel)); // shine
        rim = new Color(0.18f, 0.42f, 0.78f);
        return d;
    }

    /// <summary>The giant potion: a round glass bottle with a cork, glowing violet-pink potion in it (bubbles, a bright
    /// middle), the empty glass above it pale, a shine on the glass.</summary>
    internal static float Potion(float u, float v, float texel, out Color fill, out Color rim)
    {
        float dBulb = Dist(u, v, 0f, -0.3f) - 0.6f;
        float dNeck = Box(u, v - 0.2f, 0.2f, 0.36f, 0.06f);
        float dLip = Box(u, v - 0.55f, 0.27f, 0.06f, 0.05f);
        float dCork = Box(u, v - 0.75f, 0.17f, 0.17f, 0.06f);
        float dGlass = Mathf.Min(dBulb, Mathf.Min(dNeck, dLip));
        if (dCork < dGlass && v > 0.58f)
        {
            fill = new Color(0.78f, 0.56f, 0.34f) * (0.88f + 0.2f * Mathf.Clamp01(0.5f - u * 2f));
            if (Hash(Mathf.Floor(u * 14f) + 5f, Mathf.Floor(v * 14f)) > 0.8f) fill *= 0.82f; // (cork speckles)
            rim = new Color(0.42f, 0.26f, 0.13f);
            return dCork;
        }
        float level = -0.12f + 0.025f * Mathf.Sin(u * 9f);
        if (v < level)
        {
            float r = Dist(u, v, 0.05f, -0.36f);
            fill = Color.Lerp(new Color(1f, 0.62f, 1f), new Color(0.62f, 0.2f, 0.92f), Mathf.Clamp01(r / 0.62f)); // glowing middle
            float bub = Mathf.Min(Mathf.Min(Dist(u, v, -0.24f, -0.42f) - 0.07f, Dist(u, v, 0.18f, -0.2f) - 0.05f), Mathf.Min(Dist(u, v, 0.05f, -0.62f) - 0.045f, Dist(u, v, -0.08f, -0.22f) - 0.035f));
            if (bub < 0f) fill = Color.Lerp(fill, new Color(1f, 0.9f, 1f), 0.7f);
            if (level - v < texel * 1.5f) fill = Color.Lerp(fill, new Color(1f, 0.85f, 1f), 0.6f); // (the surface)
        }
        else fill = new Color(0.86f, 0.9f, 0.98f) * (0.92f + 0.08f * Mathf.Clamp01(0.5f + v)); // empty glass
        float sh = Dist(u * 1.6f, v, -0.5f, 0.0f) - 0.12f;
        if (sh < 0f && dBulb < -0.08f) fill = Color.Lerp(fill, Color.white, 0.85f); // the shine
        rim = new Color(0.36f, 0.12f, 0.46f);
        return dGlass;
    }

    // a rounded box at the origin (half sizes hx, hy, corner radius r)
    static float Box(float u, float v, float hx, float hy, float r)
    {
        float qx = Mathf.Abs(u) - hx + r, qy = Mathf.Abs(v) - hy + r;
        float ox = Mathf.Max(qx, 0f), oy = Mathf.Max(qy, 0f);
        return Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
    }

    static float Dist(float u, float v, float x, float y) => Mathf.Sqrt((u - x) * (u - x) + (v - y) * (v - y));

    // ------------------------------------------------------------------ the candy cloud's sweets (baked once per look)

    static Sprite SweetSprite(int look)
    {
        if (s_sweets == null) s_sweets = new Sprite[SweetLooks];
        look = Mathf.Clamp(look, 0, SweetLooks - 1);
        if (!s_sweets[look]) s_sweets[look] = BakeSweet(look);
        return s_sweets[look];
    }

    static Sprite BakeSweet(int look)
    {
        const int res = 64;
        const float texel = 2f / res;
        var tex = new Texture2D(res, res, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "Sweet " + look
        };
        var px = new Color32[res * res];
        for (int y = 0; y < res; y++)
            for (int x = 0; x < res; x++)
                px[y * res + x] = SweetPaint(look, (x + 0.5f) * texel - 1f, (y + 0.5f) * texel - 1f, texel);
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return Sprite.Create(tex, new Rect(0, 0, res, res), new Vector2(0.5f, 0.5f), res); // 1 unit wide
    }

    /// <summary>One look of a sweet at (u, v) in -1..1: 0-2 wrapped sweets (strawberry, mint, lemon), 3 a lollipop,
    /// 4 a sugared gumdrop, 5 a jelly bean, 6 a candy heart.</summary>
    internal static Color SweetPaint(int look, float u, float v, float texel)
    {
        const float rimWidth = 0.1f;
        Color fill, rim;
        float d;
        switch (look)
        {
            case 0: d = Wrapped(u, v, new Color(0.98f, 0.36f, 0.52f), new Color(1f, 0.74f, 0.84f), new Color(0.62f, 0.12f, 0.25f), out fill, out rim); break;
            case 1: d = Wrapped(u, v, new Color(0.22f, 0.80f, 0.66f), new Color(0.72f, 0.96f, 0.89f), new Color(0.06f, 0.42f, 0.34f), out fill, out rim); break;
            case 2: d = Wrapped(u, v, new Color(1f, 0.78f, 0.12f), new Color(1f, 0.94f, 0.62f), new Color(0.64f, 0.42f, 0.02f), out fill, out rim); break;
            case 3: d = Lollipop(u, v, texel, out fill, out rim); break;
            case 4: d = Gumdrop(u, v, texel, out fill, out rim); break;
            case 5: d = JellyBean(u, v, texel, out fill, out rim); break;
            default: d = CandyHeart(u, v, texel, out fill, out rim); break;
        }
        float band = Mathf.Clamp01(0.5f + (d + rimWidth) / texel);
        Color c = Color.Lerp(fill, rim, band);
        c.r = Mathf.Clamp01(c.r); c.g = Mathf.Clamp01(c.g); c.b = Mathf.Clamp01(c.b);
        c.a = Mathf.Clamp01(0.5f - d / texel);
        return c;
    }

    // a round sweet with a white swirl in a twisted wrapper
    static float Wrapped(float u, float v, Color sweet, Color wrap, Color rimC, out Color fill, out Color rim)
    {
        float r = Mathf.Sqrt(u * u + v * v), dSweet = r - 0.52f;
        float ax = Mathf.Abs(u), t = Mathf.Clamp01((ax - 0.42f) / 0.56f);
        float dWrap = Mathf.Max(Mathf.Abs(v) - (0.08f + 0.3f * t), Mathf.Max(0.42f - ax, ax - 0.98f));
        if (dSweet < dWrap)
        {
            float a = Mathf.Atan2(v, u) + r * 5f;
            float stripe = Mathf.Clamp01(0.5f + 3f * Mathf.Sin(a * 3f)) * 0.7f;
            fill = Color.Lerp(sweet, Color.white, stripe) * (0.88f + 0.2f * Mathf.Clamp01(0.5f + 0.5f * (v - u)));
            fill = Color.Lerp(fill, Color.white, Mathf.Clamp01(1f - Dist(u, v, -0.2f, 0.22f) / 0.13f) * 0.85f); // shine
        }
        else fill = wrap * (0.86f + 0.14f * Mathf.Sin(v * 40f)); // pleats
        rim = rimC;
        return Mathf.Min(dSweet, dWrap);
    }

    // a rainbow swirl lollipop on a white stick
    static float Lollipop(float u, float v, float texel, out Color fill, out Color rim)
    {
        float cx = 0f, cy = 0.2f, r = Dist(u, v, cx, cy);
        float dDisc = r - 0.6f;
        float dStick = Mathf.Max(Mathf.Abs(u) - 0.08f, Mathf.Max(v - 0.1f, -0.98f - v));
        if (dDisc <= dStick)
        {
            float a = Mathf.Atan2(v - cy, u - cx) / (2f * Mathf.PI) + r * 1.6f;
            int k = (int)Mathf.Repeat(Mathf.Floor(a * 4f), 4f);
            Color[] swirl = { new Color(1f, 0.38f, 0.62f), new Color(1f, 0.86f, 0.25f), new Color(0.35f, 0.82f, 0.98f), new Color(0.72f, 0.48f, 0.98f) };
            fill = swirl[k] * (0.86f + 0.2f * Mathf.Clamp01(0.5f + (v - cy - u) * 0.8f));
            fill = Color.Lerp(fill, Color.white, Mathf.Clamp01(1f - Dist(u, v, -0.22f, 0.45f) / 0.14f) * 0.9f); // shine
            rim = new Color(0.55f, 0.18f, 0.42f);
        }
        else
        {
            fill = new Color(0.98f, 0.97f, 0.93f) * (0.82f + 0.18f * Mathf.Clamp01(0.5f - u * 5f));
            rim = new Color(0.62f, 0.6f, 0.56f);
        }
        return Mathf.Min(dDisc, dStick);
    }

    // a sugar-coated gumdrop: a dome with a flat bottom, sparkly sugar crystals
    static float Gumdrop(float u, float v, float texel, out Color fill, out Color rim)
    {
        float d = Mathf.Max(Dist(u, v, 0f, -0.14f) - 0.8f, -0.6f - v);
        float shade = Mathf.Clamp01(0.55f + 0.45f * (v + 0.6f) / 1.3f - 0.25f * u);
        fill = new Color(0.64f, 0.30f, 0.88f) * (0.78f + 0.3f * shade);
        float h = Hash(Mathf.Floor(u * 16f), Mathf.Floor(v * 16f));
        if (h > 0.78f) fill = Color.Lerp(fill, Color.white, 0.55f); // sugar
        fill = Color.Lerp(fill, Color.white, Mathf.Clamp01(1f - Dist(u, v, -0.28f, 0.32f) / 0.16f) * 0.8f);
        rim = new Color(0.36f, 0.12f, 0.55f);
        return d;
    }

    // a kidney-shaped jelly bean with freckles and a glossy streak
    static float JellyBean(float u, float v, float texel, out Color fill, out Color rim)
    {
        const float a = 0.42f; // tilt (radians)
        float cs = Mathf.Cos(a), sn = Mathf.Sin(a);
        float x = cs * u + sn * v, y = -sn * u + cs * v;
        y += 0.2f * (x * x - 0.2f);                         // the bend
        float px = Mathf.Max(Mathf.Abs(x) - 0.45f, 0f);
        float d = Mathf.Sqrt(px * px + y * y) - 0.4f;
        fill = new Color(1f, 0.55f, 0.14f) * (0.84f + 0.24f * Mathf.Clamp01(0.5f + y * 1.2f));
        if (Hash(Mathf.Floor(u * 12f) + 3f, Mathf.Floor(v * 12f)) > 0.86f) fill *= 0.82f;   // freckles
        if (y > 0.12f && y < 0.24f && Mathf.Abs(x) < 0.42f) fill = Color.Lerp(fill, Color.white, 0.6f); // glossy streak
        rim = new Color(0.62f, 0.26f, 0.02f);
        return d;
    }

    // a red candy heart with a white glint
    static float CandyHeart(float u, float v, float texel, out Color fill, out Color rim)
    {
        const float k = 1.5f;
        float px = Mathf.Abs(u) / k, py = (v + 0.82f) / k, d;
        if (py + px > 1f) d = Mathf.Sqrt((px - 0.25f) * (px - 0.25f) + (py - 0.75f) * (py - 0.75f)) - Mathf.Sqrt(2f) / 4f;
        else
        {
            float m = 0.5f * Mathf.Max(px + py, 0f);
            float d1 = px * px + (py - 1f) * (py - 1f), d2 = (px - m) * (px - m) + (py - m) * (py - m);
            d = Mathf.Sqrt(Mathf.Min(d1, d2)) * Mathf.Sign(px - py);
        }
        d *= k;
        fill = new Color(0.96f, 0.2f, 0.34f) * (0.84f + 0.24f * Mathf.Clamp01(0.5f + 0.5f * (v - u * 0.5f)));
        fill = Color.Lerp(fill, Color.white, Mathf.Clamp01(1f - Dist(u, v, -0.38f, 0.36f) / 0.13f) * 0.9f);
        rim = new Color(0.58f, 0.05f, 0.16f);
        return d;
    }

    static float Hash(float a, float b)
    {
        float h = Mathf.Sin(a * 12.9898f + b * 78.233f) * 43758.5453f;
        return h - Mathf.Floor(h);
    }
}
