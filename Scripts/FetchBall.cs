using System;
using UnityEngine;

/// <summary>
/// A tennis ball to play fetch with (chat "fetch" / "ball", or WhipIt > Buddy > Throw The Ball). Grab it and throw it:
/// the buddies race for it (BuddyFetch), the first one there picks it up in its mouth and brings it back to your cursor,
/// and drops it at your feet for another throw. One ball at a time; it bounces, rolls, lands on window tops and rides
/// them like a snack does, and goes away after a couple of minutes nobody plays with it. Drawn in code.
/// </summary>
[DisallowMultipleComponent]
public class FetchBall : MonoBehaviour, IGrabTarget
{
    /// <summary>The ball on screen (null = none).</summary>
    public static FetchBall Current { get; private set; }
    /// <summary>You threw it (or it was just put in play): the race is on.</summary>
    public static event Action<FetchBall> Thrown;

    public float radius = 0.13f;
    public float gravity = 18f;
    [Range(0f, 1f)] public float bounce = 0.62f;
    [Tooltip("Goes away after this long with nobody playing (seconds).")]
    public float keepFor = 120f;

    public bool Hovered { get; set; }
    public bool Held { get; private set; }
    /// <summary>In a buddy's mouth.</summary>
    public GummyBody Carrier { get; private set; }
    /// <summary>Thrown and not brought back yet.</summary>
    public bool InPlay { get; private set; }
    public bool Resting { get; private set; }
    public Vector2 Position => pos;
    public Vector2 Velocity => vel;
    public float SurfaceY => pos.y - radius;
    /// <summary>When it was thrown (Time.time).</summary>
    public float ThrownAt { get; private set; }

    Vector2 pos, vel, grabOff;
    float angle, spin, lastPlay, fade = 1f;
    long onPlat;
    long layerWin = -1; // Occlusion: in front while it's thrown, then where it lands (or with whoever carries it)
    bool hidden;
    int platVersion = -1;
    bool going;
    SpriteRenderer sr;
    static Sprite s_sprite;

    /// <summary>A ball at a world position (replaces the one on screen), moving at velocity; inPlay = they go for it.</summary>
    public static FetchBall Spawn(Vector2 at, Vector2 velocity, Material material, int order, bool inPlay)
    {
        if (Current) Destroy(Current.gameObject);
        var go = new GameObject("Fetch Ball");
        var b = go.AddComponent<FetchBall>();
        b.pos = at;
        b.vel = velocity;
        b.spin = UnityEngine.Random.Range(-300f, 300f);
        b.sr = go.AddComponent<SpriteRenderer>();
        b.sr.sprite = Sprite();
        b.sr.sortingOrder = order;
        if (material) b.sr.sharedMaterial = material;
        float k = 2.3f * b.radius;
        go.transform.localScale = new Vector3(k, k, 1f);
        go.transform.position = new Vector3(at.x, at.y, 0f);
        b.lastPlay = Time.time;
        Current = b;
        if (inPlay) b.PutInPlay();
        return b;
    }

    void OnEnable() { GrabTargets.Add(this); if (!Current) Current = this; }
    void OnDisable()
    {
        GrabTargets.Remove(this);
        Held = false;
        Carrier = null;
        if (Current == this) Current = null;
    }

    void PutInPlay()
    {
        InPlay = true;
        ThrownAt = Time.time;
        lastPlay = Time.time;
        Thrown?.Invoke(this);
    }

    // ------------------------------------------------------------------ you

    public bool Contains(Vector2 p) => !going && !hidden && (p - pos).sqrMagnitude < radius * radius * 2.2f;

    public void BeginGrab(Vector2 p)
    {
        if (Carrier) Carrier = null; // (snatched out of its mouth)
        Held = true;
        InPlay = false;
        Resting = false;
        onPlat = 0;
        grabOff = pos - p;
        vel = Vector2.zero;
        lastPlay = Time.time;
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
        vel = Vector2.ClampMagnitude(vel, 16f);
        spin = -vel.x / radius * Mathf.Rad2Deg * 0.5f;
        if (vel.magnitude > 1.5f) PutInPlay(); // a throw (just letting go of it isn't)
    }

    // ------------------------------------------------------------------ buddies

    /// <summary>Picked up by this buddy (in its mouth). False if someone has it already.</summary>
    public bool PickUp(GummyBody b)
    {
        if (Carrier || Held || going) return false;
        Carrier = b;
        Resting = false;
        onPlat = 0;
        lastPlay = Time.time;
        return true;
    }

    /// <summary>Dropped / tossed (brought back: the round's over).</summary>
    public void Toss(Vector2 velocity, bool delivered)
    {
        Carrier = null;
        vel = velocity;
        onPlat = 0;
        Resting = false;
        if (delivered) InPlay = false;
        lastPlay = Time.time;
    }

    /// <summary>Nobody's bringing it back after all (gave up / took too long): the round's over.</summary>
    public void EndRound() => InPlay = false;

    // ------------------------------------------------------------------ motion

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("FetchBall.Update", t0); }
    void UpdateTimed()
    {
        float dt = Mathf.Min(Time.deltaTime, 1f / 30f);
        if (going)
        {
            fade -= dt / 0.4f;
            transform.localScale = Vector3.one * (2.3f * radius * Mathf.Max(fade, 0f));
            if (fade <= 0f) Destroy(gameObject);
            return;
        }
        if (Carrier)
        {
            if (!Carrier.isActiveAndEnabled || Carrier.Held) { Toss(Carrier ? Carrier.Velocity * 0.5f : Vector2.zero, false); }
            else
            {
                // in its mouth: just in front of it, the way it's heading
                float face = Mathf.Abs(Carrier.Velocity.x) > 0.3f ? Mathf.Sign(Carrier.Velocity.x) : (Grabber.CursorWorld.x >= Carrier.Center.x ? 1f : -1f);
                Vector2 want = Carrier.Center + new Vector2(face * Carrier.size * 0.42f, -Carrier.size * 0.08f);
                vel = (want - pos) / Mathf.Max(dt, 1e-4f);
                pos = Vector2.Lerp(pos, want, 1f - Mathf.Exp(-30f * dt));
                angle += -Carrier.Velocity.x / radius * Mathf.Rad2Deg * dt * 0.3f;
            }
        }
        else if (!Held)
        {
            Ride();
            const int sub = 2;
            for (int i = 0; i < sub; i++) Step(dt / sub);
        }
        transform.position = new Vector3(pos.x, pos.y, 0f);
        transform.localRotation = Quaternion.Euler(0f, 0f, angle);
        if (!Held && !Carrier && !InPlay && Time.time - lastPlay > keepFor) going = true;
        if (Carrier) { var bl = Carrier.GetComponent<BuddyLayer>(); hidden = bl && bl.Hides(pos); } // in its mouth: with it
        else hidden = Occlusion.ThingHidden(pos, Held, Resting, onPlat, ref layerWin);
        if (sr && sr.enabled == hidden) sr.enabled = !hidden;
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
        onPlat = 0;
    }

    void Step(float h)
    {
        Rect r = ScreenBounds.WorldRect;
        vel.y -= gravity * h;
        vel *= 1f - 0.15f * h;
        Vector2 prev = pos;
        pos += vel * h;
        angle += spin * h;

        bool grounded = false;
        if (pos.x < r.xMin + radius) { pos.x = r.xMin + radius; if (vel.x < 0f) vel.x = -vel.x * bounce; spin = -spin * 0.6f; }
        if (pos.x > r.xMax - radius) { pos.x = r.xMax - radius; if (vel.x > 0f) vel.x = -vel.x * bounce; spin = -spin * 0.6f; }
        if (pos.y > r.yMax - radius) { pos.y = r.yMax - radius; if (vel.y > 0f) vel.y = -vel.y * bounce; }
        if (pos.y < r.yMin + radius) { pos.y = r.yMin + radius; Touch(0); grounded = true; }

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

        // solid window sides and bottoms: it bounces off them
        var wins = DesktopPlatforms.Windows;
        for (int k = 0; k < wins.Count; k++)
        {
            WindowBox w = wins[k];
            if (!w.movable) continue;
            float x0 = w.xMin - radius, x1 = w.xMax + radius, y0 = w.yMin - radius;
            if (pos.x <= x0 || pos.x >= x1 || pos.y <= y0 || pos.y - radius >= w.yMax) continue;
            if (prev.y - radius >= w.yMax - 1e-4f) continue;
            if (prev.x <= x0 + 1e-4f) { pos.x = x0; if (vel.x > 0f) vel.x = -vel.x * bounce; }
            else if (prev.x >= x1 - 1e-4f) { pos.x = x1; if (vel.x < 0f) vel.x = -vel.x * bounce; }
            else if (prev.y <= y0 + 1e-4f) { pos.y = y0; if (vel.y > 0f) vel.y = -vel.y * bounce; }
            else if (w.yMax + 2f * radius < r.yMax) { pos.y = w.yMax + radius; vel = new Vector2(vel.x, 2f); onPlat = 0; grounded = false; }
        }

        if (grounded)
        {
            vel.x *= Mathf.Exp(-1.6f * h); // it rolls a long way
            spin = Mathf.Lerp(spin, -vel.x / radius * Mathf.Rad2Deg, 0.3f);
        }
        Resting = grounded && vel.sqrMagnitude < 0.02f;
    }

    void Touch(long platform)
    {
        onPlat = platform;
        float impact = -vel.y;
        if (vel.y < 0f) vel.y = impact > 0.8f ? impact * bounce : 0f;
    }

    // ------------------------------------------------------------------ drawing: a tennis ball

    static Sprite Sprite()
    {
        if (s_sprite) return s_sprite;
        const int res = 64;
        var tex = new Texture2D(res, res, TextureFormat.RGBA32, false) { name = "Fetch Ball", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        var px = new Color32[res * res];
        float texel = 2f / res;
        for (int y = 0; y < res; y++)
            for (int x = 0; x < res; x++)
            {
                float u = (x + 0.5f) * texel - 1f, v = (y + 0.5f) * texel - 1f;
                px[y * res + x] = BallPaint(u, v, texel);
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        s_sprite = UnityEngine.Sprite.Create(tex, new Rect(0, 0, res, res), new Vector2(0.5f, 0.5f), res); // 1 unit wide
        return s_sprite;
    }

    // u, v in -1..1: a fuzzy yellow-green ball with the white curved seams
    static Color BallPaint(float u, float v, float texel)
    {
        float r = Mathf.Sqrt(u * u + v * v), R = 0.86f;
        if (r > R + texel) return new Color(0f, 0f, 0f, 0f);
        float light = Mathf.Clamp01(0.55f + 0.45f * (-u * 0.5f + v * 0.7f) / R);
        Color c = Color.Lerp(new Color(0.62f, 0.74f, 0.12f), new Color(0.88f, 0.96f, 0.38f), light);
        // two seams curving round it (the classic S)
        float s1 = Mathf.Abs(Mathf.Sqrt((u - 0.95f) * (u - 0.95f) + v * v) - 0.62f), s2 = Mathf.Abs(Mathf.Sqrt((u + 0.95f) * (u + 0.95f) + v * v) - 0.62f);
        float seam = Mathf.Min(s1, s2);
        c = Color.Lerp(c, new Color(0.97f, 0.98f, 0.92f), Mathf.Clamp01(1f - (seam - 0.035f) / texel));
        c = Color.Lerp(c, new Color(0.42f, 0.52f, 0.08f), Mathf.Clamp01((r - (R - 0.07f)) / 0.07f) * 0.7f); // darker rim
        c.a = Mathf.Clamp01((R - r) / texel + 0.5f);
        return c;
    }
}
