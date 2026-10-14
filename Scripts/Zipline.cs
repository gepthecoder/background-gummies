using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A zipline the apple strung (BuddySkills ZIPLINE): a twisted, glossy licorice rope tied with a gummy knot round a high
/// window's top corner and stretched down to a candy-cane stake above a lower window top (or the taskbar). It shoots out
/// with a little wave and lands with a thunk, sags a bit, dips and bounces when someone grabs on, and wobbles back when
/// they let go. Riders (any buddy) hang from a pulley: a peppermint wheel that rolls along it with a gummy handle on two
/// licorice laces, sugar sparkles flying off at speed. It comes down when nobody has used it for a while (it unties and
/// reels back up), or snaps when one of the windows it's tied to moves (a twang, both halves drop). It hangs in front of
/// the windows it's tied to; windows in front of those hide the parts behind them.
/// </summary>
public class Zipline : MonoBehaviour
{
    static readonly List<Zipline> s_all = new List<Zipline>();
    /// <summary>Every line that's up (shooting out or ready).</summary>
    public static IReadOnlyList<Zipline> All => s_all;

    /// <summary>The high end (tied to the window corner) and the low end.</summary>
    public Vector2 From { get; private set; }
    public Vector2 To { get; private set; }
    public float Length => (To - From).magnitude;
    /// <summary>Fully out (it can be ridden).</summary>
    public bool Ready => shot >= 1f && !gone;
    /// <summary>Who strung it.</summary>
    public GummyBody Owner { get; private set; }
    public int Riders { get; private set; }
    /// <summary>It snapped (a window it's tied to moved) rather than coming down.</summary>
    public bool Snapped { get; private set; }

    [Tooltip("Comes down after this long without anyone on it (seconds).")]
    public float keepFor = 25f;

    /// <summary>Draw orders: in front of every buddy (their own ranges go up to 50), under the bubbles.</summary>
    public const int LineOrder = 70;

    long fromWin, toWin;
    WindowBox fromBox, toBox;
    float shot, idleSince, fade = 1f, width, thick, landY, stakeT = -1f;
    float dip, dipV, dipAt = 0.5f, twang;
    bool gone, landed;
    Color color;
    Material material;
    AudioSource sfx;
    AudioClip thunkClip, snapClip;
    readonly Dictionary<GummyBody, float> riders = new Dictionary<GummyBody, float>(); // rider -> where it hangs (along the line)

    // the drawing: licorice beads along the curve, knots, the stake, a pulley per rider, sugar sparkles
    readonly List<SpriteRenderer> beads = new List<SpriteRenderer>();
    SpriteRenderer knotFrom, knotTo, stake;
    class Pulley { public SpriteRenderer wheel, strapL, strapR, handle; public float spin, lastS = -1f, sparkleT; }
    readonly Dictionary<GummyBody, Pulley> pulleys = new Dictionary<GummyBody, Pulley>();
    readonly List<Pulley> spare = new List<Pulley>();
    const int Sparkles = 14;
    readonly SpriteRenderer[] sparkle = new SpriteRenderer[Sparkles];
    readonly Vector2[] sparkleV = new Vector2[Sparkles];
    readonly float[] sparkleLife = new float[Sparkles];
    const int Samples = 40;
    readonly Vector2[] curve = new Vector2[Samples + 1];
    readonly float[] arc = new float[Samples + 1];

    /// <summary>String a new line (it shoots out from the high end over the next moment). landY: the surface under the
    /// low end (the stake stands on it; NaN = 0.4 below the end).</summary>
    public static Zipline String(GummyBody owner, Vector2 from, Vector2 to, long fromWindow, long toWindow, Color color, Material material, float width,
                                 float landY = float.NaN, AudioClip thunk = null, AudioClip snap = null)
    {
        var go = new GameObject("Zipline");
        var z = go.AddComponent<Zipline>();
        z.Owner = owner;
        z.From = from;
        z.To = to;
        z.fromWin = fromWindow;
        z.toWin = toWindow;
        z.fromBox = Box(fromWindow);
        z.toBox = Box(toWindow);
        z.width = width;
        z.thick = Mathf.Max(width * 3f, 0.05f); // a chunky licorice rope
        z.landY = float.IsNaN(landY) ? to.y - 0.4f : Mathf.Min(landY, to.y - 0.05f);
        z.color = color;
        z.material = material;
        z.thunkClip = thunk;
        z.snapClip = snap;
        z.idleSince = Time.time;
        z.sfx = go.AddComponent<AudioSource>();
        z.sfx.playOnAwake = false;
        z.sfx.spatialBlend = 0f;
        z.knotFrom = z.MakeSprite("Zipline Knot", LineOrder + 1);
        z.knotTo = z.MakeSprite("Zipline Knot", LineOrder + 1);
        z.stake = z.MakeSprite("Zipline Stake", LineOrder);
        for (int i = 0; i < Sparkles; i++) { z.sparkle[i] = z.MakeSprite("Sugar Sparkle", LineOrder + 3); z.sparkle[i].enabled = false; }
        return z;
    }

    /// <summary>The line that's up between about these two points (null if none).</summary>
    public static Zipline Find(Vector2 from, Vector2 to)
    {
        Zipline best = null;
        float bd = 0.3f;
        foreach (var z in s_all)
        {
            if (z.gone) continue;
            float d = (z.From - from).magnitude + (z.To - to).magnitude;
            if (d < bd) { bd = d; best = z; }
        }
        return best;
    }

    /// <summary>The point at distance s from the high end.</summary>
    public Vector2 At(float s) => From + (To - From).normalized * Mathf.Clamp(s, 0f, Length);

    /// <summary>Where a rider at distance s hangs from: on the rope as it's drawn (its sag, and the dip under the riders).</summary>
    public Vector2 HangAt(float s)
    {
        float L = Length;
        if (L < 0.01f || shot < 1f) return At(s);
        float u = Mathf.Clamp01(s / L);
        return At(s) + Vector2.down * Drop(u);
    }

    // how far the rope hangs below the straight line at u (0..1 along): a little sag, the dip under the riders (none at the ends)
    float Drop(float u)
    {
        float sag = 0.012f * Length + 0.015f;
        float w = u <= dipAt ? (dipAt > 0f ? u / dipAt : 0f) : (dipAt < 1f ? (1f - u) / (1f - dipAt) : 0f);
        return sag * 4f * u * (1f - u) + dip * Mathf.Sin(w * Mathf.PI * 0.5f) * Mathf.Clamp01(4f * dipAt * (1f - dipAt) * 1.5f);
    }

    /// <summary>The nearest distance along the line to a point.</summary>
    public float Along(Vector2 p) => Mathf.Clamp(Vector2.Dot(p - From, (To - From).normalized), 0f, Length);

    /// <summary>A buddy hangs on at distance s (call every frame it rides).</summary>
    public void Ride(GummyBody b, float s)
    {
        if (!riders.ContainsKey(b)) dipV -= 1.2f * Mathf.Sqrt(b ? b.size / 0.6f : 1f); // grabbed on: the line gives
        riders[b] = s;
        Riders = riders.Count;
        idleSince = Time.time;
    }

    /// <summary>It let go.</summary>
    public void Off(GummyBody b)
    {
        if (riders.Remove(b)) dipV += 0.9f; // let go: it springs back up
        Riders = riders.Count;
        idleSince = Time.time;
    }

    void OnEnable() { if (!s_all.Contains(this)) s_all.Add(this); }
    void OnDisable() { s_all.Remove(this); }

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("Pulley.Update", t0); }
    void UpdateTimed()
    {
        float dt = Time.deltaTime;
        if (shot < 1f) shot = Mathf.Min(1f, shot + dt / 0.3f);
        if (shot >= 1f && !landed)
        {
            landed = true;
            stakeT = 0f;
            dipV -= 0.8f; // thunk: a little bounce all along
            Play(thunkClip, Rnd(0.95f, 1.08f), 0.55f);
        }
        if (!gone)
        {
            // a window it's tied to moved (or went away): it snaps; nobody on it for a while: it comes down
            if (Moved(fromWin, fromBox) || Moved(toWin, toBox)) { gone = true; Snapped = true; Play(snapClip, Rnd(0.92f, 1.05f), 0.6f); }
            else if (Riders == 0 && Time.time - idleSince > keepFor) gone = true;
            if (Owner == null) gone = true;
        }
        if (gone)
        {
            riders.Clear();
            Riders = 0;
            fade -= dt / 0.4f;
            if (fade <= 0f) { s_all.Remove(this); Destroy(gameObject); return; }
        }
    }

    void Play(AudioClip clip, float pitch, float volume)
    {
        if (!clip || !sfx) return;
        sfx.pitch = pitch;
        sfx.PlayOneShot(clip, volume);
    }

    // ------------------------------------------------------------------ drawing

    void LateUpdate() { long t0 = WhipProf.T(); LateUpdateTimed(); WhipProf.Add("Pulley.LateUpdate", t0); }
    void LateUpdateTimed()
    {
        float dt = Mathf.Min(Time.deltaTime, 0.05f), now = Time.time;
        float L = Length;
        if (L < 0.01f) return;
        Vector2 dir = (To - From) / L, up = new Vector2(-dir.y, dir.x);
        if (up.y < 0f) up = -up;

        // the dip under the riders (sprung: it gives when someone grabs on, bounces, settles), a small sag, a twang when it snaps
        float want = 0f, size = 1f;
        foreach (var kv in riders) { dipAt = Mathf.Clamp01(kv.Value / L); size = kv.Key ? kv.Key.size / 0.6f : 1f; want = 0.07f * Mathf.Sqrt(size); break; }
        dipV += ((want - dip) * 140f - dipV * 9f) * dt;
        dip += dipV * dt;

        // shooting out / reeling back in / snapped: where the line ends and how it hangs
        float reach = 1f - (1f - shot) * (1f - shot);
        float snapDrop = 0f;
        if (gone && !Snapped) reach *= Mathf.Clamp01(fade); // unties and reels back up to the knot
        if (gone && Snapped) snapDrop = (1f - Mathf.Clamp01(fade)) * 0.5f * L; // both halves drop
        Vector2 end = Vector2.Lerp(From, To, reach);
        for (int i = 0; i <= Samples; i++)
        {
            float t = i / (float)Samples;
            Vector2 p = Vector2.Lerp(From, end, t);
            float u = t * reach; // where this is along the full line
            p += Vector2.down * (shot >= 1f ? Drop(u) : (0.012f * L + 0.015f) * 4f * u * (1f - u) * u);
            if (shot < 1f) p += up * (0.06f * L * 0.08f * Mathf.Sin(t * Mathf.PI * 3f - shot * 12f) * (1f - shot) * t); // the wave as it flies
            if (Snapped && gone)
            {
                float half = t < 0.5f ? t * 2f : (1f - t) * 2f; // 0 at the knot / stake, 1 where it broke
                p += Vector2.down * (snapDrop * half * half);
            }
            curve[i] = p;
            arc[i] = i == 0 ? 0f : arc[i - 1] + (curve[i] - curve[i - 1]).magnitude;
        }

        int layer = Mathf.Min(LayerFor(fromWin), LayerFor(toWin)); // in front of both windows it's tied to
        float alpha = Mathf.Clamp01(fade);
        Color tint = new Color(1f, 1f, 1f, alpha);

        // licorice beads along the curve (overlapping: a twisted rope), the break in the middle once it snapped
        float beadLen = thick * 2.2f, step = beadLen * 0.74f, total = arc[Samples];
        int n = Mathf.Max(1, Mathf.CeilToInt(total / step));
        Sprite bead = BeadSprite(color);
        while (beads.Count < n) beads.Add(MakeSprite("Licorice", LineOrder));
        int seg = 0;
        for (int k = 0; k < beads.Count; k++)
        {
            var sr = beads[k];
            if (k >= n) { if (sr.enabled) sr.enabled = false; continue; }
            float a = Mathf.Min((k + 0.5f) * step, total);
            while (seg < Samples - 1 && arc[seg + 1] < a) seg++;
            float f = arc[seg + 1] > arc[seg] ? (a - arc[seg]) / (arc[seg + 1] - arc[seg]) : 0f;
            Vector2 c = Vector2.Lerp(curve[seg], curve[seg + 1], f), d = curve[seg + 1] - curve[seg];
            bool broken = Snapped && gone && Mathf.Abs(a / Mathf.Max(total, 1e-4f) - 0.5f) < 0.03f;
            sr.sprite = bead;
            sr.color = tint;
            sr.enabled = !broken && !Occlusion.Hidden(c, layer);
            sr.transform.position = new Vector3(c.x, c.y, 0f);
            sr.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
            sr.transform.localScale = new Vector3(beadLen, beadLen, 1f);
        }

        // the knot round the window corner, the knot on the stake, the stake (it pops up when the line lands)
        float ks = thick * 2.2f;
        Place(knotFrom, KnotSprite(color), From + Vector2.down * (thick * 0.3f), 0f, ks, tint, layer);
        Vector2 tail = curve[Samples];
        Place(knotTo, KnotSprite(color), tail, 0f, ks * 0.9f, tint, layer);
        knotTo.enabled &= shot > 0.05f;
        if (stakeT >= 0f) stakeT += dt;
        float pop = stakeT < 0f ? 0f : stakeT < 0.25f ? Mathf.Sin(stakeT / 0.25f * Mathf.PI * 0.5f) * 1.12f : 1f + 0.12f * Mathf.Exp(-(stakeT - 0.25f) * 10f) * Mathf.Cos((stakeT - 0.25f) * 30f);
        float stakeH = Mathf.Max(To.y - landY + thick, 0.05f);
        stake.sprite = StakeSprite(color);
        stake.color = gone && !Snapped ? new Color(1f, 1f, 1f, alpha) : tint;
        stake.enabled = pop > 0.01f && !Occlusion.Hidden(new Vector2(To.x, landY + stakeH * 0.5f), layer);
        stake.transform.position = new Vector3(To.x, landY, 0f);
        stake.transform.rotation = Quaternion.identity;
        stake.transform.localScale = new Vector3(thick * 1.6f * pop, stakeH * pop / (StakeH / (float)StakeW), 1f); // (the sprite is 1 x 4 units)

        // a pulley for every rider: a peppermint wheel rolling on the line, a gummy handle on two laces, sparkles at speed
        foreach (var kv in riders)
        {
            if (!kv.Key) continue;
            if (!pulleys.TryGetValue(kv.Key, out Pulley pl)) { pl = spare.Count > 0 ? Take() : MakePulley(); pulleys[kv.Key] = pl; pl.lastS = kv.Value; }
            DrawPulley(pl, kv.Key, kv.Value, total, layer, tint, dt, now);
        }
        // pulleys of riders that let go: put away
        List<GummyBody> done = null;
        foreach (var kv in pulleys) if (!riders.ContainsKey(kv.Key)) { if (done == null) done = new List<GummyBody>(); done.Add(kv.Key); }
        if (done != null) foreach (var b in done) { var pl = pulleys[b]; pulleys.Remove(b); Show(pl, false); spare.Add(pl); }
        UpdateSparkles(dt, layer, alpha);
    }

    void DrawPulley(Pulley pl, GummyBody b, float s, float total, int layer, Color tint, float dt, float now)
    {
        // on the curve where it hangs (the dip included)
        float u = Mathf.Clamp01(s / Mathf.Max(Length, 1e-4f)) * total;
        int seg = 0;
        while (seg < Samples - 1 && arc[seg + 1] < u) seg++;
        float f = arc[seg + 1] > arc[seg] ? (u - arc[seg]) / (arc[seg + 1] - arc[seg]) : 0f;
        Vector2 p = Vector2.Lerp(curve[seg], curve[seg + 1], f), d = (curve[seg + 1] - curve[seg]).normalized;
        Vector2 n = new Vector2(-d.y, d.x);
        if (n.y < 0f) n = -n;
        float r = thick * 0.9f;
        Vector2 wheel = p + n * (r * 0.75f); // it sits on the rope
        float moved = s - pl.lastS;
        pl.lastS = s;
        pl.spin -= moved / Mathf.Max(r, 1e-3f) * Mathf.Rad2Deg;
        bool hid = Occlusion.Hidden(wheel, layer);

        pl.wheel.sprite = WheelSprite();
        pl.wheel.color = tint;
        pl.wheel.enabled = !hid;
        pl.wheel.transform.position = new Vector3(wheel.x, wheel.y, 0f);
        pl.wheel.transform.rotation = Quaternion.Euler(0f, 0f, pl.spin);
        pl.wheel.transform.localScale = new Vector3(r * 2.2f, r * 2.2f, 1f);

        // the handle at the top of the rider (where it holds on), across the way it hangs
        Vector2 grip = b.OnRope ? b.RopePoint : b.TopPoint;
        Vector2 hangDir = grip - wheel;
        if (hangDir.sqrMagnitude < 1e-6f) hangDir = Vector2.down;
        Vector2 across = new Vector2(-hangDir.y, hangDir.x).normalized;
        float hw = Mathf.Max(b.size * 0.3f, thick * 2f);
        pl.handle.sprite = HandleSprite();
        pl.handle.color = tint;
        pl.handle.enabled = !hid;
        pl.handle.transform.position = new Vector3(grip.x, grip.y, 0f);
        pl.handle.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(across.y, across.x) * Mathf.Rad2Deg);
        pl.handle.transform.localScale = new Vector3(hw * 2f, hw * 2f, 1f);
        Strap(pl.strapL, wheel, grip - across * (hw * 0.8f), tint, hid);
        Strap(pl.strapR, wheel, grip + across * (hw * 0.8f), tint, hid);

        // sugar sparkles off the wheel at speed
        float speed = dt > 0f ? Mathf.Abs(moved) / dt : 0f;
        if (speed > 2.5f && !hid)
        {
            pl.sparkleT -= dt;
            if (pl.sparkleT <= 0f) { pl.sparkleT = Mathf.Lerp(0.09f, 0.03f, Mathf.InverseLerp(2.5f, 7f, speed)); Spark(p, -d * Mathf.Sign(moved) * 1.2f + Vector2.up * Rnd(0.6f, 1.6f) + new Vector2(Rnd(-0.5f, 0.5f), Rnd(-0.5f, 0.5f))); }
        }
    }

    void Strap(SpriteRenderer sr, Vector2 a, Vector2 b, Color tint, bool hid)
    {
        Vector2 d = b - a;
        sr.sprite = StrapSprite();
        sr.color = new Color(color.r * 0.55f, color.g * 0.45f, color.b * 0.45f, tint.a);
        sr.enabled = !hid && d.sqrMagnitude > 1e-6f;
        Vector2 m = 0.5f * (a + b);
        sr.transform.position = new Vector3(m.x, m.y, 0f);
        sr.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
        sr.transform.localScale = new Vector3(d.magnitude + thick * 0.4f, thick * 0.4f / 0.5f, 1f); // (the sprite is 1 x 0.5 units)
    }

    void Spark(Vector2 at, Vector2 v)
    {
        for (int i = 0; i < Sparkles; i++)
        {
            if (sparkleLife[i] > 0f) continue;
            sparkleLife[i] = Rnd(0.3f, 0.5f);
            sparkleV[i] = v;
            var sr = sparkle[i];
            sr.sprite = SparkleSprite();
            sr.color = Rnd(0f, 1f) < 0.6f ? Color.white : new Color(1f, 0.82f, 0.9f);
            sr.transform.position = new Vector3(at.x, at.y, 0f);
            sr.transform.localScale = Vector3.one * (thick * Rnd(0.6f, 1f));
            return;
        }
    }

    void UpdateSparkles(float dt, int layer, float alpha)
    {
        for (int i = 0; i < Sparkles; i++)
        {
            var sr = sparkle[i];
            if (sparkleLife[i] <= 0f) { if (sr.enabled) sr.enabled = false; continue; }
            sparkleLife[i] -= dt;
            sparkleV[i] += Vector2.down * (9f * dt);
            Vector3 p = sr.transform.position + (Vector3)(sparkleV[i] * dt);
            sr.transform.position = p;
            Color c = sr.color;
            c.a = Mathf.Clamp01(sparkleLife[i] / 0.2f) * alpha;
            sr.color = c;
            sr.enabled = sparkleLife[i] > 0f && !Occlusion.Hidden(p, layer);
        }
    }

    void Place(SpriteRenderer sr, Sprite sp, Vector2 at, float angle, float size, Color tint, int layer)
    {
        sr.sprite = sp;
        sr.color = tint;
        sr.enabled = !Occlusion.Hidden(at, layer);
        sr.transform.position = new Vector3(at.x, at.y, 0f);
        sr.transform.rotation = Quaternion.Euler(0f, 0f, angle);
        sr.transform.localScale = new Vector3(size, size, 1f);
    }

    Pulley MakePulley() => new Pulley
    {
        wheel = MakeSprite("Pulley Wheel", LineOrder + 2),
        strapL = MakeSprite("Pulley Lace", LineOrder + 1),
        strapR = MakeSprite("Pulley Lace", LineOrder + 1),
        handle = MakeSprite("Pulley Handle", LineOrder + 2),
    };

    Pulley Take() { var pl = spare[spare.Count - 1]; spare.RemoveAt(spare.Count - 1); pl.spin = 0f; pl.sparkleT = 0f; return pl; }

    static void Show(Pulley pl, bool on) { pl.wheel.enabled = pl.strapL.enabled = pl.strapR.enabled = pl.handle.enabled = on; }

    SpriteRenderer MakeSprite(string name, int order)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sortingOrder = order;
        sr.color = Color.white;
        if (material) sr.sharedMaterial = material;
        sr.enabled = false;
        return sr;
    }

    static int LayerFor(long win) => win != 0 && Occlusion.Exists(win) ? Occlusion.LayerOf(win) : Occlusion.Back;

    static WindowBox Box(long id)
    {
        if (id == 0) return default;
        var wins = DesktopPlatforms.Windows;
        for (int k = 0; k < wins.Count; k++) if (wins[k].id == id) return wins[k];
        return default;
    }

    static bool Moved(long id, WindowBox was)
    {
        if (id == 0) return false;
        var wins = DesktopPlatforms.Windows;
        for (int k = 0; k < wins.Count; k++)
        {
            if (wins[k].id != id) continue;
            WindowBox w = wins[k];
            return Mathf.Abs(w.xMin - was.xMin) > 0.05f || Mathf.Abs(w.xMax - was.xMax) > 0.05f || Mathf.Abs(w.yMax - was.yMax) > 0.05f;
        }
        return true; // gone (closed / minimized)
    }

    // ------------------------------------------------------------------ sprites (baked once; the licorice ones per colour)

    const int BeadW = 48, BeadH = 24, KnotN = 40, StakeW = 16, StakeH = 64, WheelN = 40, HandleN = 40;
    static readonly Dictionary<int, Sprite> s_beads = new Dictionary<int, Sprite>(), s_knots = new Dictionary<int, Sprite>(), s_stakes = new Dictionary<int, Sprite>();
    static Sprite s_wheel, s_handle, s_strap, s_sparkle;
    static bool s_wheelBaked, s_handleBaked, s_strapBaked, s_sparkleBaked;

    static int Key(Color c) => ((int)(c.r * 255f) << 16) | ((int)(c.g * 255f) << 8) | (int)(c.b * 255f);

    static Sprite BeadSprite(Color c)
    {
        int k = Key(c);
        if (s_beads.TryGetValue(k, out Sprite s)) return s;
        s = Bake(BeadW, BeadH, new Vector2(0.5f, 0.5f), "Licorice Bead", (x, y, tx) => BeadPaint(x, y, tx, c));
        s_beads[k] = s;
        return s;
    }

    static Sprite KnotSprite(Color c)
    {
        int k = Key(c);
        if (s_knots.TryGetValue(k, out Sprite s)) return s;
        s = Bake(KnotN, KnotN, new Vector2(0.5f, 0.5f), "Licorice Knot", (x, y, tx) => KnotPaint(x, y, tx, c));
        s_knots[k] = s;
        return s;
    }

    static Sprite StakeSprite(Color c)
    {
        int k = Key(c);
        if (s_stakes.TryGetValue(k, out Sprite s)) return s;
        // 1 unit wide, StakeH/StakeW tall: scaled to (width, height) by the caller, so the pivot's at its foot
        s = Bake(StakeW, StakeH, new Vector2(0.5f, 0f), "Candy Stake", (x, y, tx) => StakePaint(x, y, tx, c)); // pivot at its foot
        s_stakes[k] = s;
        return s;
    }

    static Sprite WheelSprite() { if (!s_wheelBaked) { s_wheel = Bake(WheelN, WheelN, new Vector2(0.5f, 0.5f), "Peppermint Wheel", WheelPaint); s_wheelBaked = true; } return s_wheel; }
    static Sprite HandleSprite() { if (!s_handleBaked) { s_handle = Bake(HandleN, HandleN, new Vector2(0.5f, 0.5f), "Gummy Handle", HandlePaint); s_handleBaked = true; } return s_handle; }
    static Sprite StrapSprite() { if (!s_strapBaked) { s_strap = Bake(16, 8, new Vector2(0.5f, 0.5f), "Lace", LacePaint); s_strapBaked = true; } return s_strap; }
    static Sprite SparkleSprite() { if (!s_sparkleBaked) { s_sparkle = Bake(12, 12, new Vector2(0.5f, 0.5f), "Sugar", SparklePaint); s_sparkleBaked = true; } return s_sparkle; }

    // x 0..1 across the texture, y 0..h/w up it (same texel size); 1 unit wide
    static Sprite Bake(int w, int h, Vector2 pivot, string name, System.Func<float, float, float, Color> paint)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = name };
        var px = new Color32[w * h];
        float texel = 1f / w;
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                Color c = paint((i + 0.5f) * texel, (j + 0.5f) * texel, texel);
                px[j * w + i] = new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), Mathf.Clamp01(c.a));
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return Sprite.Create(tex, new Rect(0, 0, w, h), pivot, w);
    }

    static Color Clear => new Color(0f, 0f, 0f, 0f);
    static float Cover(float d, float texel) => Mathf.Clamp01(0.5f - d / texel);

    static Color Over(Color under, Color over, float a)
    {
        float oa = a + under.a * (1f - a);
        if (oa <= 1e-5f) return Clear;
        Color c = (over * a + under * (under.a * (1f - a))) / oa;
        c.a = oa;
        return c;
    }

    // one twist of licorice (x along, y across 0..0.5): a plump bead with a slanted groove, a glossy streak, sugar specks
    internal static Color BeadPaint(float x, float y, float texel, Color baseC)
    {
        const float H = BeadH / (float)BeadW;
        float cy = H * 0.5f, half = H * 0.38f + H * 0.06f * Mathf.Sin(Mathf.Clamp01(x) * Mathf.PI);
        float ex = Mathf.Max(Mathf.Abs(x - 0.5f) - (0.48f - half), 0f); // rounded ends
        float d = ex > 0f ? new Vector2(ex, y - cy).magnitude - half : Mathf.Abs(y - cy) - half;
        float a = Cover(d, texel);
        if (a <= 0f) return Clear;
        float k = (y - cy) / Mathf.Max(half, 1e-3f); // -1 bottom .. 1 top
        float shade = 0.55f + 0.45f * Mathf.Sqrt(Mathf.Max(0f, 1f - k * k)) + 0.12f * k;
        Color c = baseC * shade;
        // the slanted groove of the twist (where this strand tucks under the next)
        float g = Mathf.Abs((x - 0.5f) - 0.28f * k);
        if (g < 0.05f) c = Color.Lerp(c, baseC * 0.42f, 1f - g / 0.05f);
        // glossy streak along the top
        float hl = Mathf.Abs(k - 0.45f);
        if (hl < 0.18f && Mathf.Abs(x - 0.5f) < 0.36f) c = Color.Lerp(c, Color.Lerp(baseC, Color.white, 0.8f), (1f - hl / 0.18f) * 0.75f);
        // sugar specks
        float h = Hash(Mathf.Floor(x * 40f), Mathf.Floor(y * 40f));
        if (h > 0.93f) c = Color.Lerp(c, Color.white, 0.6f);
        if (d > -texel * 1.6f) c = Color.Lerp(c, baseC * 0.35f, 0.6f); // its edge
        c.a = a;
        return c;
    }

    // a fat round knot: two lobes of licorice crossing, a glossy top
    internal static Color KnotPaint(float x, float y, float texel, Color baseC)
    {
        Color outC = Clear;
        for (int lobe = 0; lobe < 2; lobe++)
        {
            Vector2 c0 = lobe == 0 ? new Vector2(0.42f, 0.46f) : new Vector2(0.58f, 0.54f);
            float r = 0.3f, d = (new Vector2(x, y) - c0).magnitude - r;
            float a = Cover(d, texel);
            if (a <= 0f) continue;
            Vector2 q = (new Vector2(x, y) - c0) / r;
            float shade = 0.6f + 0.4f * Mathf.Sqrt(Mathf.Max(0f, 1f - q.sqrMagnitude)) + 0.15f * q.y;
            Color c = baseC * shade;
            if ((q - new Vector2(-0.3f, 0.4f)).magnitude < 0.28f) c = Color.Lerp(c, Color.Lerp(baseC, Color.white, 0.8f), 0.6f);
            if (Mathf.Abs(q.x * 0.7f + q.y * 0.7f) < 0.08f) c = Color.Lerp(c, baseC * 0.45f, 0.7f); // a twist line
            if (d > -texel * 1.6f) c = Color.Lerp(c, baseC * 0.35f, 0.6f);
            c.a = 1f;
            outC = Over(outC, c, a);
        }
        return outC;
    }

    // a candy-cane stake: white with stripes in the licorice colour, a rounded top, a darker edge
    internal static Color StakePaint(float x, float y, float texel, Color stripe)
    {
        const float H = StakeH / (float)StakeW;
        float d = Mathf.Max(Mathf.Abs(x - 0.5f) - 0.42f, -y);
        if (y > H - 0.42f) d = Mathf.Max(new Vector2(x - 0.5f, y - (H - 0.42f)).magnitude - 0.42f, -y);
        float a = Cover(d, texel);
        if (a <= 0f) return Clear;
        bool red = Mathf.Repeat(y * 1.1f + x * 0.9f, 1f) < 0.45f;
        Color c = red ? stripe : new Color(0.98f, 0.97f, 0.94f);
        float shade = 0.82f + 0.25f * Mathf.Sqrt(Mathf.Max(0f, 1f - Mathf.Pow((x - 0.5f) / 0.42f, 2f)));
        c *= shade;
        if (Mathf.Abs(x - 0.38f) < 0.06f) c = Color.Lerp(c, Color.white, 0.5f); // shine
        if (d > -texel * 1.3f) c *= 0.7f;
        c.a = a;
        return c;
    }

    // a peppermint wheel: white with red swirls, a hub (it spins, so the swirl shows it rolling)
    internal static Color WheelPaint(float x, float y, float texel)
    {
        Vector2 q = new Vector2(x - 0.5f, y - 0.5f);
        float r = q.magnitude, d = r - 0.46f;
        float a = Cover(d, texel);
        if (a <= 0f) return Clear;
        float ang = Mathf.Atan2(q.y, q.x) + r * 5f;
        bool red = Mathf.Repeat(ang / (2f * Mathf.PI) * 6f, 1f) < 0.5f;
        Color c = red ? new Color(0.88f, 0.12f, 0.18f) : new Color(0.99f, 0.98f, 0.96f);
        c *= 0.85f + 0.2f * Mathf.Clamp01(0.5f - (q.x + q.y) * 0.9f);
        if (r < 0.1f) c = new Color(0.55f, 0.52f, 0.55f);         // the hub
        if (r < 0.05f) c = new Color(0.3f, 0.28f, 0.3f);
        if (d > -texel * 2f) c = new Color(0.6f, 0.15f, 0.2f);   // its rim
        c.a = a;
        return c;
    }

    // a gummy handle bar (across, x 0..1): sunny yellow jelly, glossy, rounded ends
    internal static Color HandlePaint(float x, float y, float texel)
    {
        float cy = 0.5f, half = 0.13f;
        float ex = Mathf.Max(Mathf.Abs(x - 0.5f) - (0.46f - half), 0f);
        float d = ex > 0f ? new Vector2(ex, y - cy).magnitude - half : Mathf.Abs(y - cy) - half;
        float a = Cover(d, texel);
        if (a <= 0f) return Clear;
        float k = (y - cy) / half;
        Color baseC = new Color(1f, 0.80f, 0.20f);
        Color c = baseC * (0.65f + 0.35f * Mathf.Sqrt(Mathf.Max(0f, 1f - k * k)) + 0.1f * k);
        if (k > 0.2f && k < 0.65f && Mathf.Abs(x - 0.5f) < 0.3f) c = Color.Lerp(c, Color.white, 0.55f);
        if (d > -texel * 1.6f) c = new Color(0.78f, 0.5f, 0.08f);
        c.a = a;
        return c;
    }

    internal static Color LacePaint(float x, float y, float texel)
    {
        float d = Mathf.Abs(y - 0.25f) - 0.2f;
        float a = Cover(d, texel);
        return new Color(1f, 1f, 1f, a);
    }

    internal static Color SparklePaint(float x, float y, float texel)
    {
        Vector2 q = new Vector2(x - 0.5f, y - 0.5f);
        float star = Mathf.Min(Mathf.Abs(q.x), Mathf.Abs(q.y)) * 3f + q.magnitude;
        float a = Mathf.Clamp01((0.42f - star) / 0.12f);
        return new Color(1f, 1f, 1f, a);
    }

    // its own dice for the looks (sparkles, pitches): the buddies' behaviour doesn't depend on how many sparkles flew
    static readonly System.Random s_fx = new System.Random(7);
    static float Rnd(float a, float b) => a + (float)s_fx.NextDouble() * (b - a);

    static float Hash(float a, float b)
    {
        float h = Mathf.Sin(a * 12.9898f + b * 78.233f) * 43758.5453f;
        return h - Mathf.Floor(h);
    }
}
