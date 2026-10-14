using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The basketball (comes with the Hoop): bounces higher than the tennis ball, rolls, rides window tops, bounces off
/// window sides, the rim and the backboard, and drops through the net. Grab it and flick it at the hoop (you shoot
/// too); a buddy that has it dribbles it beside it as it goes, and lifts it over its head to shoot. Three baskets in a
/// row by the same shooter (you too) and it's ON FIRE: flames trail it until that shooter misses or someone else
/// scores. Drawn in code.
/// </summary>
[DisallowMultipleComponent]
public class Basketball : MonoBehaviour, IGrabTarget
{
    public struct Shot { public bool swish, bank, dunk; }

    /// <summary>The ball on screen (null = none).</summary>
    public static Basketball Current { get; private set; }
    /// <summary>A basket: who shot it (null = you), and how.</summary>
    public static event Action<GummyBody, Shot> Scored;
    /// <summary>A shot missed: who shot it (null = you), and whether it was an airball (touched nothing).</summary>
    public static event Action<GummyBody, bool> Missed;
    /// <summary>You flicked it (at the hoop, hopefully).</summary>
    public static event Action UserShot;
    /// <summary>Three in a row: ON FIRE (null = you).</summary>
    public static event Action<GummyBody> CaughtFire;
    /// <summary>The fire's out (null = yours).</summary>
    public static event Action<GummyBody> FireOut;

    public float radius = 0.15f;
    public float gravity = 18f;
    [Range(0f, 1f)] public float bounce = 0.66f;
    /// <summary>Air drag (per second).</summary>
    public const float Drag = 0.15f;
    /// <summary>Baskets in a row for ON FIRE.</summary>
    public const int FireAfter = 3;

    public bool Hovered { get; set; }
    public bool Held { get; private set; }
    /// <summary>The buddy that has it (null = nobody).</summary>
    public GummyBody Carrier { get; private set; }
    public Vector2 Position => pos;
    public Vector2 Velocity => vel;
    public float SurfaceY => pos.y - radius;
    public bool Resting { get; private set; }
    /// <summary>On the floor or a window top right now.</summary>
    public bool Grounded { get; private set; }
    /// <summary>A shot in the air that hasn't gone in or missed yet.</summary>
    public bool ShotLive => shotLive;
    /// <summary>Who took that shot (null = you).</summary>
    public GummyBody Shooter => shooter;
    public bool UserShooting => shotLive && userShot;
    public float ShotAge => Time.time - shotAt;
    /// <summary>The last one to have it (null = you, or nobody yet).</summary>
    public GummyBody LastTouch => lastTouch;
    public bool OnFire => onFire;
    /// <summary>Whose fire it is (null with OnFire = yours).</summary>
    public GummyBody FireOwner => fireOwner;
    /// <summary>Held up over its head, about to shoot (BuddyHoops sets it).</summary>
    public bool Lift { get; set; }
    /// <summary>Baskets in a row (null = you).</summary>
    public int StreakOf(GummyBody b) => b ? (streak.TryGetValue(b, out int n) ? n : 0) : userStreak;

    Vector2 pos, vel, grabOff;
    float angle, spin, dribbleT, shotAt, nextFlame;
    bool shotLive, userShot, touchedRim, touchedBoard, dunk, onFire, fireUser, hidden, lastUser;
    GummyBody shooter, fireOwner, lastTouch;
    long onPlat, layerWin = -1;
    int platVersion = -1, userStreak;
    SpriteRenderer sr;
    readonly Dictionary<GummyBody, int> streak = new Dictionary<GummyBody, int>();
    const int Flames = 20;
    readonly SpriteRenderer[] flame = new SpriteRenderer[Flames];
    readonly Vector2[] flameV = new Vector2[Flames];
    readonly float[] flameLife = new float[Flames], flameMax = new float[Flames];
    static Sprite s_ball, s_flame;
    static readonly System.Random s_fx = new System.Random(41);

    /// <summary>A ball at a world position (replaces the one on screen), moving at velocity.</summary>
    public static Basketball Spawn(Vector2 at, Vector2 velocity, Material material)
    {
        if (Current) Destroy(Current.gameObject);
        var go = new GameObject("Basketball");
        var b = go.AddComponent<Basketball>();
        b.pos = at;
        b.vel = velocity;
        b.spin = -velocity.x / b.radius * Mathf.Rad2Deg * 0.5f;
        b.sr = go.AddComponent<SpriteRenderer>();
        b.sr.sprite = BallSprite();
        b.sr.sortingOrder = 66; // (between the back and the front of the rim: Hoop)
        b.sr.color = Color.white;
        if (material) b.sr.sharedMaterial = material;
        for (int i = 0; i < Flames; i++)
        {
            var g = new GameObject("Flame");
            g.transform.SetParent(go.transform, false);
            var f = g.AddComponent<SpriteRenderer>();
            f.sprite = FlameSprite();
            f.sortingOrder = 65;
            f.color = Color.white;
            if (material) f.sharedMaterial = material;
            f.enabled = false;
            b.flame[i] = f;
        }
        Current = b;
        b.Place();
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

    // ------------------------------------------------------------------ you

    public bool Contains(Vector2 p) => !hidden && (p - pos).sqrMagnitude < radius * radius * 2.2f;

    public void BeginGrab(Vector2 p)
    {
        Carrier = null; // (snatched out of its hands)
        Lift = false;
        if (shotLive) Miss();
        Held = true;
        Resting = false;
        onPlat = 0;
        grabOff = pos - p;
        vel = Vector2.zero;
        lastTouch = null;
        lastUser = true;
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
        if (vel.magnitude > 2.5f) { StartShot(null, false); UserShot?.Invoke(); } // a flick (just letting go isn't)
    }

    // ------------------------------------------------------------------ buddies

    /// <summary>Picked up (it's loose, or a shot it catches on the way down). False if someone has it.</summary>
    public bool PickUp(GummyBody b)
    {
        if (Held || Carrier || !b) return false;
        if (shotLive) Miss(); // (a rebound)
        Take(b);
        return true;
    }

    /// <summary>It's this buddy's now (a steal, or a pick-up).</summary>
    public void Take(GummyBody b)
    {
        Carrier = b;
        Lift = false;
        Resting = false;
        onPlat = 0;
        lastTouch = b;
        lastUser = false;
    }

    /// <summary>Let go of it (knocked loose, dropped, a pass): it flies off at this velocity.</summary>
    public void Release(Vector2 velocity)
    {
        Carrier = null;
        Lift = false;
        vel = velocity;
        spin = -vel.x / radius * Mathf.Rad2Deg * 0.4f;
        onPlat = 0;
        Resting = false;
    }

    /// <summary>A shot (from where it is, at this velocity) by this buddy.</summary>
    public void Shoot(Vector2 velocity, GummyBody by)
    {
        Release(velocity);
        spin = Mathf.Sign(-velocity.x) * 420f; // (backspin)
        lastTouch = by;
        StartShot(by, false);
    }

    /// <summary>A dunk: put down through the rim from just above it.</summary>
    public void Slam(Vector2 at, GummyBody by)
    {
        pos = at;
        Release(new Vector2(0f, -5f));
        lastTouch = by;
        StartShot(by, true);
    }

    /// <summary>Lying on a window top nobody can get up to: it rolls off the nearer end.</summary>
    public bool RollOff()
    {
        if (Carrier || Held || onPlat == 0 || DesktopPlatforms.IsIcon(onPlat)) return false;
        foreach (var p in DesktopPlatforms.Current)
        {
            if (p.id != onPlat) continue;
            float dir = pos.x - p.x0 < p.x1 - pos.x ? -1f : 1f;
            float dist = dir < 0f ? pos.x - p.x0 : p.x1 - pos.x;
            vel = new Vector2(dir * Mathf.Clamp(Mathf.Sqrt(2f * 3f * (dist + 0.3f)), 1.5f, 6f), 0.3f); // (enough to get there with the rolling friction)
            Resting = false;
            return true;
        }
        return false;
    }

    /// <summary>Knocked away in the air (a block).</summary>
    public void Deflect(Vector2 velocity)
    {
        if (Carrier || Held) return;
        vel = velocity;
        spin = UnityEngine.Random.Range(-600f, 600f);
        if (shotLive) Miss();
    }

    void StartShot(GummyBody by, bool isDunk)
    {
        shotLive = true;
        shooter = by;
        userShot = by == null;
        shotAt = Time.time;
        touchedRim = touchedBoard = false;
        dunk = isDunk;
    }

    // ------------------------------------------------------------------ baskets and misses

    void Score()
    {
        var h = Hoop.Current;
        if (h) h.Swish(touchedRim ? 0.6f : 1f);
        bool live = shotLive;
        GummyBody by = live ? shooter : lastTouch;
        bool you = live ? userShot : lastUser;
        var shot = new Shot { swish = live && !touchedRim && !touchedBoard, bank = live && touchedBoard, dunk = live && dunk };
        shotLive = false;
        // streaks and the fire: someone else scoring puts it out
        if (onFire && (fireUser != you || (!you && fireOwner != by))) { var was = fireUser ? null : fireOwner; onFire = false; FireOut?.Invoke(was); }
        int n = you ? ++userStreak : (streak[by] = StreakOf(by) + 1);
        if (!you && by) foreach (var k in new List<GummyBody>(streak.Keys)) if (k != by) streak[k] = 0;
        if (!you) userStreak = 0;
        else streak.Clear();
        if (n >= FireAfter && !onFire) { onFire = true; fireUser = you; fireOwner = you ? null : by; CaughtFire?.Invoke(fireOwner); }
        Scored?.Invoke(you ? null : by, shot);
    }

    void Miss()
    {
        if (!shotLive) return;
        shotLive = false;
        if (userShot) userStreak = 0;
        else if (shooter) streak[shooter] = 0;
        if (onFire && fireUser == userShot && (userShot || fireOwner == shooter)) { onFire = false; FireOut?.Invoke(fireOwner); }
        Missed?.Invoke(userShot ? null : shooter, !touchedRim && !touchedBoard);
    }

    // ------------------------------------------------------------------ motion

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("Basketball.Update", t0); }
    void UpdateTimed()
    {
        float dt = Mathf.Min(Time.deltaTime, 1f / 30f);
        if (Carrier)
        {
            if (!Carrier.isActiveAndEnabled || Carrier.Held) Release(Carrier ? Carrier.Velocity * 0.5f : Vector2.zero);
            else Carry(dt);
        }
        if (!Carrier && !Held)
        {
            Ride();
            const int sub = 3;
            for (int i = 0; i < sub; i++) Step(dt / sub);
            if (shotLive && (Grounded || ShotAge > 4.5f)) Miss();
        }
        Place();
        Fire(dt);
    }

    // in its hands: dribbled beside it while it goes, held beside it standing, up over its head to shoot
    void Carry(float dt)
    {
        var c = Carrier;
        var h = Hoop.Current;
        float face = Lift && h ? Mathf.Sign(h.Rim.x - c.Center.x) : Mathf.Abs(c.Velocity.x) > 0.3f ? Mathf.Sign(c.Velocity.x) : h ? Mathf.Sign(h.Rim.x - c.Center.x) : (Grabber.CursorWorld.x >= c.Center.x ? 1f : -1f);
        if (face == 0f) face = 1f;
        Vector2 want;
        if (Lift) { want = c.Center + new Vector2(face * c.size * 0.12f, c.size * 0.5f + radius * 0.8f); dribbleT = 0f; }
        else
        {
            float side = face * (c.size * 0.45f + radius * 0.6f), hand = c.Center.y - c.size * 0.05f;
            if (c.Grounded && Mathf.Abs(c.Velocity.x) > 0.4f)
            {
                dribbleT += dt;
                float floor = c.Extent.yMin + radius, k = Mathf.Abs(Mathf.Cos(Mathf.PI * dribbleT / 0.42f));
                want = new Vector2(c.Center.x + side, floor + (hand - floor) * k);
            }
            else { want = new Vector2(c.Center.x + side, hand); dribbleT = 0f; }
        }
        Vector2 was = pos;
        pos = Vector2.Lerp(pos, want, 1f - Mathf.Exp(-40f * dt));
        vel = (pos - was) / Mathf.Max(dt, 1e-4f);
        angle += -c.Velocity.x / radius * Mathf.Rad2Deg * dt * 0.3f;
        Grounded = false;
        Resting = false;
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
        vel *= 1f - Drag * h;
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

        // solid window sides and bottoms
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

        // the hoop: off the board, off the rim's two ends, and down through the middle = a basket
        var hp = Hoop.Current;
        if (hp && hp.Playable)
        {
            Rect b = hp.Board;
            Vector2 q = new Vector2(Mathf.Clamp(pos.x, b.xMin, b.xMax), Mathf.Clamp(pos.y, b.yMin, b.yMax));
            Vector2 d = pos - q;
            if (d.sqrMagnitude < radius * radius)
            {
                Vector2 n = d.sqrMagnitude > 1e-8f ? d.normalized : new Vector2(hp.Side, 0f);
                pos = q + n * radius;
                float vn = Vector2.Dot(vel, n);
                if (vn < 0f) { vel -= (1f + 0.55f) * vn * n; touchedBoard = true; }
            }
            Rim(hp.RimBack);
            Rim(hp.RimFront);
            Vector2 rim = hp.Rim;
            if (prev.y >= rim.y && pos.y < rim.y && vel.y < 0f && Mathf.Abs(pos.x - rim.x) < Hoop.RimHalf - radius * 0.3f) Score();
        }

        if (grounded)
        {
            vel.x *= Mathf.Exp(-3f * h); // (rolls, but not across the whole screen)
            spin = Mathf.Lerp(spin, -vel.x / radius * Mathf.Rad2Deg, 0.3f);
        }
        Grounded = grounded;
        Resting = grounded && vel.sqrMagnitude < 0.02f;
    }

    // off one end of the rim (a little tube)
    void Rim(Vector2 c)
    {
        Vector2 d = pos - c;
        float min = radius + Hoop.RimTube;
        if (d.sqrMagnitude >= min * min) return;
        Vector2 n = d.sqrMagnitude > 1e-8f ? d.normalized : Vector2.up;
        pos = c + n * min;
        float vn = Vector2.Dot(vel, n);
        if (vn < 0f)
        {
            Vector2 vt = vel - vn * n;
            vel = vt * 0.85f - 0.5f * vn * n;
            touchedRim = true;
            spin = -spin * 0.5f;
        }
    }

    void Touch(long platform)
    {
        onPlat = platform;
        float impact = -vel.y;
        if (vel.y < 0f) vel.y = impact > 0.9f ? impact * bounce : 0f;
    }

    void Place()
    {
        transform.position = new Vector3(pos.x, pos.y, 0f);
        transform.localRotation = Quaternion.Euler(0f, 0f, angle);
        float k = 2.3f * radius;
        transform.localScale = new Vector3(k, k, 1f);
        if (Carrier) { var bl = Carrier.GetComponent<BuddyLayer>(); hidden = bl && bl.Hides(pos); }
        else hidden = Occlusion.ThingHidden(pos, Held || shotLive, Resting, onPlat, ref layerWin);
        if (sr)
        {
            sr.enabled = !hidden;
            float glow = onFire ? 0.5f + 0.5f * Mathf.Sin(Time.time * 18f) : 0f;
            sr.color = onFire ? new Color(1f, 0.8f - 0.15f * glow, 0.55f - 0.25f * glow) : Color.white;
        }
    }

    // ON FIRE: flames stream off it
    void Fire(float dt)
    {
        if (onFire && !hidden && Time.time >= nextFlame)
        {
            nextFlame = Time.time + 0.022f;
            for (int i = 0; i < Flames; i++)
            {
                if (flameLife[i] > 0f) continue;
                flameMax[i] = flameLife[i] = 0.28f + 0.15f * (float)s_fx.NextDouble();
                flameV[i] = -vel * 0.12f + new Vector2(((float)s_fx.NextDouble() - 0.5f) * 0.8f, 0.9f + 0.6f * (float)s_fx.NextDouble());
                flame[i].transform.position = new Vector3(pos.x + ((float)s_fx.NextDouble() - 0.5f) * radius, pos.y + ((float)s_fx.NextDouble() - 0.5f) * radius, 0f);
                break;
            }
        }
        for (int i = 0; i < Flames; i++)
        {
            var f = flame[i];
            if (!f) continue;
            if (flameLife[i] <= 0f) { if (f.enabled) f.enabled = false; continue; }
            flameLife[i] -= dt;
            float u = 1f - flameLife[i] / flameMax[i];
            f.transform.position += (Vector3)(flameV[i] * dt);
            f.transform.localScale = Vector3.one * (radius * 2.4f * (1f - 0.7f * u));
            f.color = u < 0.35f ? Color.Lerp(new Color(1f, 0.95f, 0.55f, 0.95f), new Color(1f, 0.6f, 0.12f, 0.9f), u / 0.35f) : Color.Lerp(new Color(1f, 0.6f, 0.12f, 0.9f), new Color(0.85f, 0.15f, 0.05f, 0f), (u - 0.35f) / 0.65f);
            f.enabled = !hidden;
        }
    }

    // ------------------------------------------------------------------ aiming (BuddyHoops)

    /// <summary>
    /// The launch velocity that takes the ball from p0 through the point `to`, coming down onto it, peaking `clear`
    /// above the higher of the two (with this ball's gravity and air drag: the drag acts on x and y separately, so the
    /// height alone sets how long it flies, and x just has to cover the distance in that time). False if it can't.
    /// </summary>
    public static bool Aim(Vector2 p0, Vector2 to, float clear, float g, out Vector2 v)
    {
        v = Vector2.zero;
        float apex = Mathf.Max(p0.y, to.y) + Mathf.Max(clear, 0.05f);
        float want = apex - p0.y;
        float vy = Mathf.Sqrt(2f * g * want);
        for (int it = 0; it < 4; it++) vy *= Mathf.Sqrt(want / Mathf.Max(ApexOf(vy, g), 0.01f)); // (drag: a bit more)
        if (!Descend(p0.y, vy, to.y, g, out float S)) return false;
        v = new Vector2((to.x - p0.x) / S, vy);
        return true;
    }

    const float AimStep = 1f / 180f; // (the ball moves in about these steps: 3 a frame)

    // how high it goes above where it starts
    static float ApexOf(float vy, float g)
    {
        float y = 0f;
        for (int i = 0; i < 2000 && vy > 0f; i++) { vy -= g * AimStep; vy *= 1f - Drag * AimStep; y += vy * AimStep; }
        return y;
    }

    // when it comes back down through height ty: S = how far 1 unit/s of sideways speed has taken it by then
    static bool Descend(float y, float vy, float ty, float g, out float S)
    {
        S = 0f;
        float f = 1f;
        for (int i = 0; i < 3000; i++)
        {
            vy -= g * AimStep;
            vy *= 1f - Drag * AimStep;
            f *= 1f - Drag * AimStep;
            float ny = y + vy * AimStep;
            if (vy < 0f && y >= ty && ny < ty)
            {
                S += f * AimStep * (y - ty) / Mathf.Max(y - ny, 1e-6f);
                return S > 0.01f;
            }
            S += f * AimStep;
            y = ny;
        }
        return false;
    }

    // ------------------------------------------------------------------ drawing

    static Sprite BallSprite()
    {
        if (s_ball) return s_ball;
        s_ball = Bake(64, BallPaint, "Basketball");
        return s_ball;
    }

    static Sprite FlameSprite()
    {
        if (s_flame) return s_flame;
        s_flame = Bake(16, (u, v, tx) => { float r = Mathf.Sqrt(u * u + v * v); return new Color(1f, 1f, 1f, Mathf.Clamp01((0.95f - r) / 0.5f)); }, "Flame");
        return s_flame;
    }

    static Sprite Bake(int res, Func<float, float, float, Color> paint, string name)
    {
        var tex = new Texture2D(res, res, TextureFormat.RGBA32, false) { name = name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        var px = new Color32[res * res];
        float texel = 2f / res;
        for (int y = 0; y < res; y++)
            for (int x = 0; x < res; x++)
            {
                Color c = paint((x + 0.5f) * texel - 1f, (y + 0.5f) * texel - 1f, texel);
                px[y * res + x] = new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), Mathf.Clamp01(c.a));
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return Sprite.Create(tex, new Rect(0, 0, res, res), new Vector2(0.5f, 0.5f), res); // 1 unit wide
    }

    // u, v in -1..1: an orange ball, pebbly, with black seams (one across, one down, two curves)
    internal static Color BallPaint(float u, float v, float texel)
    {
        float r = Mathf.Sqrt(u * u + v * v), R = 0.86f;
        if (r > R + texel) return new Color(0f, 0f, 0f, 0f);
        float light = Mathf.Clamp01(0.5f + 0.5f * (-u * 0.55f + v * 0.65f) / R);
        Color c = Color.Lerp(new Color(0.78f, 0.32f, 0.07f), new Color(1f, 0.6f, 0.25f), light);
        c *= 0.96f + 0.04f * Mathf.Sin(u * 90f) * Mathf.Sin(v * 90f); // pebbles
        float s1 = Mathf.Abs(u), s2 = Mathf.Abs(v), s3 = Mathf.Abs(Mathf.Sqrt((u - 1.05f) * (u - 1.05f) + v * v) - 0.75f), s4 = Mathf.Abs(Mathf.Sqrt((u + 1.05f) * (u + 1.05f) + v * v) - 0.75f);
        float seam = Mathf.Min(Mathf.Min(s1, s2), Mathf.Min(s3, s4));
        c = Color.Lerp(c, new Color(0.12f, 0.06f, 0.04f), Mathf.Clamp01(1f - (seam - 0.025f) / texel));
        c = Color.Lerp(c, new Color(0.45f, 0.16f, 0.04f), Mathf.Clamp01((r - (R - 0.08f)) / 0.08f) * 0.7f); // darker edge
        c.a = Mathf.Clamp01((R - r) / texel + 0.5f);
        return c;
    }
}
