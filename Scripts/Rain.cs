using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Rain (Ctrl+Shift+R, chat "rain", WhipIt > Buddy > Make It Rain): grey rain clouds with sleepy faces roll in along the
/// top of the screen and it rains on the desktop for two minutes (Ctrl+Shift+R again stops it early). Every window is a
/// roof: the drops land on the taskbar and on window tops, never under a window, so under one it stays dry (BuddyRain
/// takes the buddies there). They land on the buddies too (wet: BuddyRain) and fill puddles on the taskbar and on window
/// tops; drag a window and its puddles spill off it. When it stops the clouds roll away, a rainbow fades in and the puddles
/// dry up slowly. Anything landing in a puddle splashes. Everything lives in the desktop: windows in front hide it. Only
/// that key combination is read. More than one screen: it rains on the screen most of the buddies are on.
/// </summary>
[DefaultExecutionOrder(25)] // after the buddies have moved this frame: it rains on where they are now
[DisallowMultipleComponent]
public class Rain : MonoBehaviour
{
    [Tooltip("How long a shower lasts (seconds) unless you stop it (Ctrl+Shift+R again).")]
    public float duration = 120f;
    [Tooltip("Raindrops per second for every unit of screen width.")]
    public float dropsPerUnit = 11f;
    [Tooltip("Sprite material (WhipIt > Setup assigns it).")]
    public Material material;

    [Header("Sounds (WhipIt > Setup assigns them)")]
    public AudioClip rainLoop;
    public AudioClip splashClip;
    public AudioClip chimeClip;
    [Range(0f, 1f)] public float volume = 0.5f;

    /// <summary>The one in the scene (on the Interaction object).</summary>
    public static Rain Current { get; private set; }
    /// <summary>Tests can replace the Ctrl+Shift+R check.</summary>
    public static Func<bool> KeysProbe;
    /// <summary>Showers so far (this session).</summary>
    public static int Showers;

    public enum Phase { Off, Coming, Raining, Leaving }
    public Phase State { get; private set; }
    /// <summary>The clouds are coming in or it's raining (the buddies take cover).</summary>
    public bool Raining => State == Phase.Coming || State == Phase.Raining;
    /// <summary>How hard it rains right now (0..1).</summary>
    public float Intensity { get; private set; }
    public bool RainbowUp => bow && rainbowT >= 0f;
    public IReadOnlyList<Puddle> Puddles => puddles;
    /// <summary>Counts (tests): drops that landed, drops that hit a buddy, splashes, spilled puddles.</summary>
    public int Landed { get; private set; }
    public int BuddyHits { get; private set; }
    public int Splashes { get; private set; }
    public int Spills { get; private set; }
    /// <summary>Time since the rain started / stopped.</summary>
    public float Since => Time.time - changedAt;

    /// <summary>A puddle on the taskbar (Surface 0) or on a window top (its id). X, Y = the middle of it on the surface.</summary>
    public class Puddle
    {
        public long Surface { get; internal set; }
        public float X { get; internal set; }
        public float Y { get; internal set; }
        public float Width => w;
        public float Volume => vol;
        public float Left => X - w * 0.5f;
        public float Right => X + w * 0.5f;
        /// <summary>Buddies splashing in it (BuddyRain).</summary>
        public int Claims;
        internal float rel, vol, w, h, born;
        internal Vector2 anchor;
        internal SpriteRenderer sr;
        internal bool dead;
    }

    // ------------------------------------------------------------------ the parts

    const int MaxDrops = 320, MaxDroplets = 200, MaxRipples = 40, MaxPuddles = 16, MaxMasks = 16;
    const float ArriveTime = 2.4f, LeaveTime = 2.2f, DropWater = 0.0018f, NewPuddleChance = 0.0016f, MaxWidth = 1.9f;
    /// <summary>Draw orders: the rainbow, the clouds, the drops, puddles on the taskbar and their rings behind everything
    /// (the desktop: windows in front hide them, sprite masks in the range -21..-7); puddles on windows -6, their rings -5;
    /// splashes, drips and spray just behind the buddies (4: water flying round them, not over their faces).</summary>
    public const int RainbowOrder = -20, CloudOrder = -13, DropOrder = -11, FloorPuddleOrder = -9, FloorRippleOrder = -8,
        WindowPuddleOrder = -6, WindowRippleOrder = -5, DropletOrder = 4;

    readonly Vector2[] dropPos = new Vector2[MaxDrops], dropVel = new Vector2[MaxDrops];
    readonly bool[] dropOn = new bool[MaxDrops];
    readonly SpriteRenderer[] dropSr = new SpriteRenderer[MaxDrops];

    struct Bead { public Vector2 pos, vel; public float life, size, carry; public long ignore; public bool on; }
    readonly Bead[] droplets = new Bead[MaxDroplets];
    readonly SpriteRenderer[] dropletSr = new SpriteRenderer[MaxDroplets];

    struct Ring { public Vector2 pos; public float t, size; public bool on, onWindow; }
    readonly Ring[] ripples = new Ring[MaxRipples];
    readonly SpriteRenderer[] rippleSr = new SpriteRenderer[MaxRipples];

    class Cloud { public SpriteRenderer sr; public int face; public float nextFace, phase; }
    readonly List<Cloud> clouds = new List<Cloud>();
    readonly List<Puddle> puddles = new List<Puddle>();
    readonly List<SpriteMask> masks = new List<SpriteMask>();
    readonly Dictionary<GummyBody, Vector2> lastFall = new Dictionary<GummyBody, Vector2>(); // x = was grounded (1/0), y = its fall speed
    SpriteRenderer rainbow;
    AudioSource loopSrc, fxSrc;
    Transform root;
    float t, acc, changedAt = -100f, rainbowT, rainedFor, lastSplashSound = -1f, nextDropSound;
    bool wasKeys, cheered, bow;
    int nextDrop, nextDroplet, nextRipple;

    void Awake()
    {
        root = new GameObject("Rain").transform;
        root.SetParent(transform, false);
        loopSrc = gameObject.AddComponent<AudioSource>();
        loopSrc.playOnAwake = false;
        loopSrc.loop = true;
        loopSrc.spatialBlend = 0f;
        loopSrc.volume = 0f;
        fxSrc = gameObject.AddComponent<AudioSource>();
        fxSrc.playOnAwake = false;
        fxSrc.spatialBlend = 0f;
    }

    void OnEnable() { Current = this; }

    void OnDisable()
    {
        if (Current == this) Current = null;
        if (loopSrc) loopSrc.Stop();
    }

    void OnDestroy()
    {
        if (root) Destroy(root.gameObject);
    }

    // ------------------------------------------------------------------ you

    /// <summary>Ctrl+Shift+R: rain if it isn't, stop it if it is.</summary>
    public void Toggle()
    {
        if (Raining) End(); else Begin();
    }

    /// <summary>The clouds roll in and it rains (for duration seconds).</summary>
    public void Begin()
    {
        if (Raining) return;
        Showers++;
        float k = State == Phase.Leaving ? 1f - Smooth(t / LeaveTime) : 0f; // (coming back while it was leaving: from there)
        State = Phase.Coming;
        t = InvSmooth(k) * ArriveTime;
        rainedFor = 0f;
        changedAt = Time.time;
        if (bow) rainbowT = Mathf.Max(rainbowT, RainbowHold); // (the rainbow fades away)
        if (k <= 0f) { s_area = ScreenBounds.BusiestRect; s_areaVersion = ScreenBounds.Version; } // (more than one screen: over where most of them are)
    }

    static Rect s_area;
    static int s_areaVersion = -1;

    /// <summary>Where it rains: the screen (one screen: WorldRect; more: the one most of them were on when it started).</summary>
    public static Rect Area
    {
        get
        {
            if (!ScreenBounds.Multi) return ScreenBounds.WorldRect;
            if (s_areaVersion != ScreenBounds.Version) { s_areaVersion = ScreenBounds.Version; s_area = ScreenBounds.BusiestRect; } // (the screens changed)
            return s_area;
        }
    }

    /// <summary>It stops: the clouds roll away (and a rainbow comes out).</summary>
    public void End()
    {
        if (!Raining) return;
        float k = State == Phase.Coming ? Smooth(t / ArriveTime) : 1f;
        State = Phase.Leaving;
        t = (1f - InvSmooth(k)) * LeaveTime;
        changedAt = Time.time;
        if (rainedFor > 6f) { bow = true; rainbowT = -RainbowDelay; cheered = false; }
    }

    static bool KeysDown()
    {
        if (KeysProbe != null) return KeysProbe();
        return DesktopInput.ChordHeld(DesktopInput.VK_CONTROL, DesktopInput.VK_SHIFT, DesktopInput.VK_R);
    }

    /// <summary>The puddle under this buddy (it's standing in it), or null.</summary>
    public Puddle PuddleUnder(GummyBody b)
    {
        Rect e = b.Extent;
        float feet = e.yMin - b.SkateLift, x = b.Center.x;
        foreach (var p in puddles)
            if (!p.dead && Mathf.Abs(feet - p.Y) < 0.14f && x > p.Left - 0.05f && x < p.Right + 0.05f) return p;
        return null;
    }

    /// <summary>A splash where something landed in a puddle (speed = how hard): water flies, rings, sploosh.</summary>
    public void Splash(Vector2 at, float speed, Puddle p)
    {
        Splashes++;
        int n = Mathf.Clamp(Mathf.RoundToInt(6f + speed * 1.6f), 6, 18);
        float side = 0.8f + speed * 0.22f, up = 1.6f + speed * 0.38f;
        for (int i = 0; i < n; i++)
            AddDroplet(at + new Vector2(Rnd(-0.12f, 0.12f), 0.02f), new Vector2(Rnd(-1f, 1f) * side, up * Rnd(0.6f, 1.15f)), Rnd(0.03f, 0.055f), 0f, 0, 1.4f);
        if (p != null)
        {
            AddRipple(new Vector2(at.x - 0.08f, p.Y), p, 0.5f);
            AddRipple(new Vector2(at.x + 0.1f, p.Y), p, 0.35f);
            p.vol *= 0.985f;
        }
        if (Time.time - lastSplashSound > 0.08f)
        {
            lastSplashSound = Time.time;
            Play(splashClip, Rnd(0.9f, 1.15f) * Mathf.Lerp(1.15f, 0.85f, Mathf.InverseLerp(2f, 9f, speed)), Mathf.Clamp01(0.35f + speed * 0.08f));
        }
    }

    /// <summary>Little drops of water flying off something (a wet buddy dripping or shaking itself dry).</summary>
    public void Spray(Vector2 at, Vector2 vel, float size) => AddDroplet(at, vel, size, 0f, 0, 1.2f);

    /// <summary>Is the point under a roof (a window above it covers it: no rain gets there)?</summary>
    public static bool Roofed(float x, float top)
    {
        foreach (var w in DesktopPlatforms.Windows)
            if (!w.screen && x >= w.xMin && x <= w.xMax && w.yMin >= top - 0.02f) return true;
        return false;
    }

    // ------------------------------------------------------------------ each frame

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("Cloud.Update", t0); }
    void UpdateTimed()
    {
        float dt = Mathf.Min(Time.deltaTime, 0.05f);
        bool keys = KeysDown();
        if (keys && !wasKeys) Toggle();
        wasKeys = keys;
        Rect r = Area;

        Weather(dt);
        PlaceClouds(r);
        Spawn(dt, r);
        StepDrops(dt, r);
        StepDroplets(dt, r);
        StepPuddles(dt, r);
        StepRipples(dt);
        WatchLandings();
        StepRainbow(dt, r);
        UpdateMasks();
        Sound(dt);
    }

    void Weather(float dt)
    {
        switch (State)
        {
            case Phase.Coming:
                t += dt;
                rainedFor += dt;
                Intensity = Smooth(t / ArriveTime);
                if (t >= ArriveTime) { State = Phase.Raining; t = 0f; }
                break;
            case Phase.Raining:
                t += dt;
                rainedFor += dt;
                Intensity = 0.85f + 0.15f * Mathf.Sin(Time.time * 0.7f) * Mathf.Sin(Time.time * 0.31f + 1f); // (gusts)
                if (t >= duration) End();
                break;
            case Phase.Leaving:
                t += dt;
                Intensity = 1f - Smooth(t * 1.6f / LeaveTime);
                if (t >= LeaveTime) { State = Phase.Off; Intensity = 0f; }
                break;
            default:
                Intensity = 0f;
                break;
        }
    }

    // ------------------------------------------------------------------ the clouds: a row along the top of the screen,
    //                                                                     sliding in from both sides

    void PlaceClouds(Rect r)
    {
        float k = State == Phase.Coming ? Smooth(t / ArriveTime) : State == Phase.Raining ? 1f : State == Phase.Leaving ? 1f - Smooth(t / LeaveTime) : 0f;
        int n = k > 0f ? Mathf.Max(3, Mathf.CeilToInt(r.width / 2.2f) + 1) : 0;
        while (clouds.Count < n)
        {
            var c = new Cloud { sr = MakeSprite("Rain Cloud", CloudSprite(0), CloudOrder, true), face = Rand.Next(2), phase = Rnd(0f, 6.3f) };
            c.nextFace = Time.time + Rnd(2f, 7f);
            clouds.Add(c);
        }
        float spacing = n > 1 ? r.width / (n - 1) : r.width, W = spacing * 1.3f, H = W * CloudH / CloudW;
        float now = Time.time;
        for (int i = 0; i < clouds.Count; i++)
        {
            var c = clouds[i];
            bool on = i < n;
            if (c.sr.enabled != on) c.sr.enabled = on;
            if (!on) continue;
            float home = r.xMin + i * spacing, side = home < r.center.x ? -1f : 1f;
            float x = home + side * (1f - k) * (r.width * 0.5f + W);
            float bob = 0.04f * Mathf.Sin(now * 1.3f + c.phase);
            float belly = r.yMax - 0.75f;                                       // its belly here: the top part is off the screen
            float y = belly + (0.5f - CloudBelly) * H + bob;
            if (now >= c.nextFace) { c.face = 1 - c.face; c.nextFace = now + Rnd(3f, 8f); c.sr.sprite = CloudSprite(c.face); }
            if (!c.sr.sprite) c.sr.sprite = CloudSprite(c.face);
            c.sr.transform.position = new Vector3(x, y, 0f);
            c.sr.transform.localScale = new Vector3(W * (1f + 0.02f * Mathf.Sin(now * 2.1f + c.phase)), W, 1f);
        }
    }

    // ------------------------------------------------------------------ the drops

    void Spawn(float dt, Rect r)
    {
        if (Intensity <= 0f) { acc = 0f; return; }
        acc += dropsPerUnit * r.width * Intensity * dt;
        while (acc >= 1f)
        {
            acc -= 1f;
            Vector2 at = new Vector2(Rnd(r.xMin, r.xMax), r.yMax - 0.62f - Rnd(0f, 0.25f));
            if (InWindow(at)) continue; // (a window up there: it rains on it, not under it)
            int i = Free(dropOn, ref nextDrop);
            if (i < 0) break;
            dropOn[i] = true;
            dropPos[i] = at;
            dropVel[i] = new Vector2(0f, -Rnd(10.5f, 12.5f));
            if (!dropSr[i]) dropSr[i] = MakeSprite("Raindrop", DropSprite(), DropOrder, true);
            dropSr[i].enabled = true;
        }
    }

    void StepDrops(float dt, Rect r)
    {
        for (int i = 0; i < MaxDrops; i++)
        {
            if (!dropOn[i]) continue;
            Vector2 prev = dropPos[i], p = prev + dropVel[i] * dt;
            dropPos[i] = p;
            bool gone = false;
            if (HitBuddy(p, out GummyBody b))
            {
                gone = true;
                BuddyHits++;
                if (Rand.NextDouble() < 0.5) AddDroplet(p, new Vector2(Rnd(-0.8f, 0.8f), Rnd(0.6f, 1.4f)), 0.022f, 0f, 0, 0.3f);
                var br = b.GetComponent<BuddyRain>();
                if (br) br.Hit();
            }
            else if (HitWindow(prev, p, 0, out long id, out float top, out bool roof))
            {
                gone = true;
                if (roof) Land(new Vector2(p.x, top), id, DropWater, true);
            }
            else if (p.y <= r.yMin)
            {
                gone = true;
                Land(new Vector2(p.x, r.yMin), 0, DropWater, true);
            }
            else if (p.x < r.xMin - 1f || p.x > r.xMax + 1f) gone = true;
            if (gone) { dropOn[i] = false; dropSr[i].enabled = false; continue; }
            var tr = dropSr[i].transform;
            tr.position = new Vector3(p.x, p.y + DropLen * 0.5f, 0f); // (the streak trails up behind it)
            tr.localScale = new Vector3(0.03f, DropLen * DropW / DropH, 1f); // (the picture is 1 wide, DropH/DropW tall)
        }
    }

    const float DropLen = 0.28f;

    // the drop's on a buddy (inside its round outline)
    static bool HitBuddy(Vector2 p, out GummyBody hit)
    {
        hit = null;
        foreach (var b in GummyBody.All)
        {
            if (!b || !b.isActiveAndEnabled || b.Hiding || b.InRoom) continue;
            Rect e = b.Extent;
            if (e.width <= 0f || p.x < e.xMin || p.x > e.xMax || p.y < e.yMin || p.y > e.yMax) continue;
            float u = (p.x - e.center.x) / (e.width * 0.5f), v = (p.y - e.center.y) / (e.height * 0.5f);
            if (u * u + v * v > 1f) continue;
            hit = b;
            return true;
        }
        return false;
    }

    // it came down onto (or into) a window: roof = onto the part of its top you can see (it lands there)
    static bool HitWindow(Vector2 prev, Vector2 p, long ignore, out long id, out float top, out bool roof)
    {
        id = 0; top = 0f; roof = false;
        var wins = DesktopPlatforms.Windows;
        for (int k = 0; k < wins.Count; k++)
        {
            WindowBox w = wins[k];
            if (w.id == ignore || p.x < w.xMin || p.x > w.xMax) continue;
            bool crossed = prev.y >= w.yMax && p.y < w.yMax;
            if (!crossed && !w.Contains(p)) continue;
            id = w.id;
            top = w.yMax;
            roof = crossed && TopVisible(w.id, p.x);
            return true;
        }
        return false;
    }

    static bool InWindow(Vector2 p)
    {
        foreach (var w in DesktopPlatforms.Windows) if (w.Contains(p)) return true;
        return false;
    }

    static bool TopVisible(long id, float x)
    {
        foreach (var pl in DesktopPlatforms.Current)
            if (pl.id == id && x >= pl.x0 && x <= pl.x1) return true;
        return false;
    }

    // a drop (or a spilled drop of water) landed on a surface: into a puddle there, or now and then a new one
    void Land(Vector2 at, long surface, float water, bool rain)
    {
        if (rain) Landed++;
        var p = PuddleAt(at.x, surface, 0.12f);
        if (p != null)
        {
            p.vol += water;
            if (Rand.NextDouble() < 0.45) AddRipple(at, p, 0.25f);
        }
        else
        {
            if (!rain || Rand.NextDouble() < NewPuddleChance) NewPuddle(at, surface, water + 0.015f);
            if (rain && Rand.NextDouble() < 0.55)
                AddDroplet(at + Vector2.up * 0.01f, new Vector2(Rnd(-0.7f, 0.7f), Rnd(0.8f, 1.6f)), 0.02f, 0f, 0, 0.3f);
        }
    }

    // ------------------------------------------------------------------ droplets (splashes, drips, spray, spills)

    void AddDroplet(Vector2 at, Vector2 vel, float size, float carry, long ignore, float life)
    {
        int i = Free(droplets, ref nextDroplet);
        if (i < 0) return;
        droplets[i] = new Bead { pos = at, vel = vel, life = life, size = size, carry = carry, ignore = ignore, on = true };
        if (!dropletSr[i]) dropletSr[i] = MakeSprite("Droplet", DropletSprite(), DropletOrder, false);
        dropletSr[i].enabled = true;
    }

    void StepDroplets(float dt, Rect r)
    {
        for (int i = 0; i < MaxDroplets; i++)
        {
            ref Bead d = ref droplets[i];
            if (!d.on) continue;
            Vector2 prev = d.pos;
            d.vel.y -= 20f * dt;
            d.pos += d.vel * dt;
            d.life -= dt;
            bool gone = d.life <= 0f;
            if (!gone && d.vel.y < 0f)
            {
                if (HitWindow(prev, d.pos, d.ignore, out long id, out float top, out bool roof))
                {
                    gone = true;
                    if (roof && d.carry > 0f) Land(new Vector2(d.pos.x, top), id, d.carry, false);
                }
                else if (d.pos.y <= r.yMin)
                {
                    gone = true;
                    if (d.carry > 0f) Land(new Vector2(d.pos.x, r.yMin), 0, d.carry, false);
                }
            }
            if (gone || d.pos.x < r.xMin - 2f || d.pos.x > r.xMax + 2f) { d.on = false; dropletSr[i].enabled = false; continue; }
            var tr = dropletSr[i].transform;
            tr.position = new Vector3(d.pos.x, d.pos.y, 0f);
            float stretch = 1f + Mathf.Min(d.vel.magnitude * 0.06f, 0.6f);
            tr.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.vel.y, d.vel.x) * Mathf.Rad2Deg + 90f);
            tr.localScale = new Vector3(d.size, d.size * stretch, 1f);
        }
    }

    // ------------------------------------------------------------------ puddles

    Puddle PuddleAt(float x, long surface, float margin)
    {
        foreach (var p in puddles) if (!p.dead && p.Surface == surface && x > p.Left - margin && x < p.Right + margin) return p;
        return null;
    }

    // where a puddle on this surface can be: x0..x1 (false = no room, or the window's gone)
    static bool Span(long surface, Rect r, out float x0, out float x1, out float y, out WindowBox win)
    {
        win = default;
        if (surface == 0) { x0 = r.xMin + 0.1f; x1 = r.xMax - 0.1f; y = r.yMin; return true; }
        x0 = x1 = y = 0f;
        foreach (var w in DesktopPlatforms.Windows)
            if (w.id == surface) { win = w; x0 = w.xMin + 0.06f; x1 = w.xMax - 0.06f; y = w.yMax; return x1 - x0 > 0.5f; }
        return false;
    }

    void NewPuddle(Vector2 at, long surface, float vol)
    {
        Rect r = Area;
        if (!Span(surface, r, out float x0, out float x1, out float y, out WindowBox win)) return;
        int here = 0;
        foreach (var q in puddles) if (!q.dead && q.Surface == surface) here++;
        int cap = surface == 0 ? Mathf.Max(3, Mathf.FloorToInt(r.width / 2.6f)) : Mathf.Max(1, Mathf.FloorToInt((x1 - x0) / 2.4f));
        if (here >= cap || puddles.Count >= MaxPuddles)
        {
            // no room for another one: the water runs into the nearest one there
            Puddle best = null;
            float bd = 3f;
            foreach (var q in puddles) if (!q.dead && q.Surface == surface && Mathf.Abs(q.X - at.x) < bd) { bd = Mathf.Abs(q.X - at.x); best = q; }
            if (best != null) best.vol += vol;
            return;
        }
        var p = new Puddle { Surface = surface, vol = vol, w = 0.3f, born = Time.time };
        float x = Mathf.Clamp(at.x, x0 + 0.15f, x1 - 0.15f);
        p.rel = surface == 0 ? x : x - win.xMin;
        p.X = x;
        p.Y = y;
        p.anchor = new Vector2(win.xMin, win.yMax);
        p.sr = MakeSprite("Puddle", PuddleSprite(), surface == 0 ? FloorPuddleOrder : WindowPuddleOrder, surface == 0);
        p.sr.enabled = true;
        puddles.Add(p);
    }

    void StepPuddles(float dt, Rect r)
    {
        for (int i = puddles.Count - 1; i >= 0; i--)
        {
            var p = puddles[i];
            if (!p.dead && p.Surface != 0)
            {
                // on a window: it rides along a little (a wobble), moved away or gone = it spills off
                bool found = Span(p.Surface, r, out float wx0, out float wx1, out float wy, out WindowBox win);
                if (!found || (new Vector2(win.xMin, win.yMax) - p.anchor).magnitude > 0.2f || p.w > wx1 - wx0) { Spill(p, found ? win.xMin - p.anchor.x : 0f, found ? (win.xMin + win.xMax) * 0.5f : p.X); }
                else
                {
                    p.X = Mathf.Clamp(win.xMin + p.rel, wx0 + p.w * 0.5f, wx1 - p.w * 0.5f);
                    p.rel = p.X - win.xMin;
                    p.Y = wy;
                }
            }
            else if (!p.dead)
            {
                p.Y = r.yMin;
                p.X = Mathf.Clamp(p.rel, r.xMin + 0.1f + p.w * 0.5f, r.xMax - 0.1f - p.w * 0.5f);
            }
            if (!p.dead && !Raining) p.vol -= (0.008f + 0.012f * p.vol) * dt; // dries up (a big one in a minute or two)
            p.vol = Mathf.Min(p.vol, 2f);                                       // (as big as it gets: the rest runs off)
            if (p.dead || p.vol <= 0.012f)
            {
                // shrinking away
                p.dead = true;
                p.w = Mathf.MoveTowards(p.w, 0f, dt * 1.2f);
                if (p.w <= 0.02f) { if (p.sr) Destroy(p.sr.gameObject); puddles.RemoveAt(i); continue; }
            }
            else
            {
                float want = Mathf.Clamp(0.3f + 1.25f * Mathf.Sqrt(p.vol), 0.3f, MaxWidth);
                if (p.Surface == 0) want = Mathf.Min(want, r.width - 0.4f);
                else if (Span(p.Surface, r, out float a0, out float a1, out _, out _)) want = Mathf.Min(want, a1 - a0);
                p.w = Mathf.MoveTowards(p.w, want, dt * 0.5f);
                p.h = 0.06f + 0.06f * Mathf.Clamp01(p.vol / 0.8f);
            }
            Show(p);
        }
        Merge();
    }

    void Show(Puddle p)
    {
        var tr = p.sr.transform;
        tr.position = new Vector3(p.X, p.Y - 0.008f, 0f);
        tr.localScale = new Vector3(Mathf.Max(p.w, 0.001f), p.h * PuddleW / PuddleH, 1f); // (the sprite is 1 wide, PuddleH/PuddleW tall)
        bool hidden = p.Surface != 0 && Occlusion.Hidden(new Vector2(p.X, p.Y + 0.03f), Occlusion.LayerOf(p.Surface));
        if (p.sr.enabled == hidden) p.sr.enabled = !hidden;
    }

    // two puddles on the same surface ran into each other: one puddle
    void Merge()
    {
        for (int i = 0; i < puddles.Count; i++)
            for (int j = i + 1; j < puddles.Count; j++)
            {
                Puddle a = puddles[i], b = puddles[j];
                if (a.dead || b.dead || a.Surface != b.Surface || a.Right < b.Left - 0.02f || b.Right < a.Left - 0.02f) continue;
                float v = a.vol + b.vol;
                float x = (a.X * a.vol + b.X * b.vol) / Mathf.Max(v, 1e-4f);
                a.rel += x - a.X;
                a.X = x;
                a.vol = v;
                a.w = Mathf.Max(a.w, b.w);
                a.Claims += b.Claims;
                b.vol = 0f;
                b.dead = true;
                b.w = 0f;
            }
    }

    // its window moved away (dx = how far sideways) or went: the water slides off and falls
    void Spill(Puddle p, float dx, float winMid)
    {
        Spills++;
        int n = Mathf.Clamp(Mathf.RoundToInt(6f + p.vol * 24f), 6, 28);
        float carry = p.vol / n;
        for (int k = 0; k < n; k++)
        {
            float x = p.X + Rnd(-0.5f, 0.5f) * p.w;
            float dir = Mathf.Abs(dx) > 0.05f ? -Mathf.Sign(dx) : (x >= winMid ? 1f : -1f); // (it stays behind as the window goes)
            AddDroplet(new Vector2(x, p.Y + 0.03f), new Vector2(dir * Rnd(0.8f, 2.4f), Rnd(0.2f, 1.2f)), Rnd(0.03f, 0.05f), carry, p.Surface, 4f);
        }
        p.vol = 0f;
        p.dead = true;
        p.w = 0f;
    }

    // ------------------------------------------------------------------ rings on the water

    void AddRipple(Vector2 at, Puddle p, float size)
    {
        int i = Free(ripples, ref nextRipple);
        if (i < 0) return;
        bool win = p.Surface != 0;
        float x = Mathf.Clamp(at.x, p.Left + 0.06f, p.Right - 0.06f);
        ripples[i] = new Ring { pos = new Vector2(x, p.Y + p.h * 0.85f), t = 0f, size = Mathf.Min(size, p.w * 0.6f), on = true, onWindow = win };
        if (!rippleSr[i]) rippleSr[i] = MakeSprite("Ripple", RippleSprite(), FloorRippleOrder, true);
        rippleSr[i].sortingOrder = win ? WindowRippleOrder : FloorRippleOrder;
        rippleSr[i].maskInteraction = win ? SpriteMaskInteraction.None : SpriteMaskInteraction.VisibleOutsideMask;
        rippleSr[i].enabled = true;
    }

    void StepRipples(float dt)
    {
        for (int i = 0; i < MaxRipples; i++)
        {
            ref Ring q = ref ripples[i];
            if (!q.on) continue;
            q.t += dt;
            if (q.t >= 0.5f) { q.on = false; rippleSr[i].enabled = false; continue; }
            float u = q.t / 0.5f, w = q.size * (0.2f + 0.8f * Mathf.Sqrt(u));
            var tr = rippleSr[i].transform;
            tr.position = new Vector3(q.pos.x, q.pos.y, 0f);
            tr.localScale = new Vector3(w, w, 1f);
            rippleSr[i].color = new Color(1f, 1f, 1f, 0.8f * (1f - u));
        }
    }

    // ------------------------------------------------------------------ anything landing in a puddle splashes

    readonly List<GummyBody> gone = new List<GummyBody>();

    void WatchLandings()
    {
        if (puddles.Count == 0) { lastFall.Clear(); return; }
        foreach (var b in GummyBody.All)
        {
            if (!b || !b.isActiveAndEnabled || b.InRoom) continue;
            bool g = b.Grounded, had = lastFall.TryGetValue(b, out Vector2 was), air = had && was.x < 0.5f;
            float vy = b.Velocity.y;
            if (g && air && was.y < -2.2f)
            {
                var p = PuddleUnder(b);
                if (p != null) Splash(new Vector2(b.Center.x, p.Y + p.h), -was.y, p);
            }
            lastFall[b] = g ? new Vector2(1f, 0f) : new Vector2(0f, air ? Mathf.Min(was.y, vy) : vy); // (airborne: the fastest it fell)
        }
        gone.Clear();
        foreach (var kv in lastFall) if (!kv.Key || !kv.Key.isActiveAndEnabled) gone.Add(kv.Key);
        foreach (var b in gone) lastFall.Remove(b);
    }

    // ------------------------------------------------------------------ the rainbow

    const float RainbowDelay = 1.2f, RainbowIn = 2.5f, RainbowHold = 11f, RainbowOut = 3.5f;

    void StepRainbow(float dt, Rect r)
    {
        if (!bow) { if (rainbow && rainbow.enabled) rainbow.enabled = false; return; }
        rainbowT += dt;
        if (rainbowT < 0f) return; // (a moment after the rain stops)
        if (!rainbow) rainbow = MakeSprite("Rainbow", RainbowSprite(), RainbowOrder, true);
        float a = rainbowT < RainbowIn ? Smooth(rainbowT / RainbowIn) : rainbowT < RainbowHold ? 1f : 1f - Smooth((rainbowT - RainbowHold) / RainbowOut);
        if (rainbowT >= RainbowHold + RainbowOut) { bow = false; rainbow.enabled = false; return; }
        float R = Mathf.Min(r.width * 0.42f, r.height * 0.85f);
        rainbow.enabled = true;
        rainbow.transform.position = new Vector3(r.center.x, r.yMin - 0.6f, 0f);
        rainbow.transform.localScale = new Vector3(R * 2f, R * 2f, 1f);
        rainbow.color = new Color(1f, 1f, 1f, a);
        if (!cheered && a > 0.6f)
        {
            cheered = true;
            Play(chimeClip, 1f, 0.6f);
            BuddyRain.CheerRainbow(new Vector2(r.center.x, r.yMin - 0.6f + R));
        }
    }

    // ------------------------------------------------------------------ windows in front hide it (sprite masks over the
    //                                                                     desktop-layer parts: orders -21..-7)

    void UpdateMasks()
    {
        bool any = Occlusion.Enabled && (State != Phase.Off || puddles.Count > 0 || bow);
        var wins = DesktopPlatforms.Windows;
        int real = 0;
        while (real < wins.Count && !wins[real].screen) real++; // (the bits of the world on no screen are at the back: no masks)
        int n = any ? Mathf.Min(real, MaxMasks) : 0;
        while (masks.Count < n) masks.Add(MakeMask());
        for (int i = 0; i < masks.Count; i++)
        {
            bool on = i < n;
            if (masks[i].enabled != on) masks[i].enabled = on;
            if (!on) continue;
            WindowBox w = wins[i];
            masks[i].transform.position = new Vector3((w.xMin + w.xMax) * 0.5f, (w.yMin + w.yMax) * 0.5f, 0f);
            masks[i].transform.localScale = new Vector3(w.xMax - w.xMin, w.yMax - w.yMin, 1f);
        }
    }

    SpriteMask MakeMask()
    {
        var go = new GameObject("Rain Mask");
        go.transform.SetParent(root, false);
        var m = go.AddComponent<SpriteMask>();
        m.sprite = WhiteSprite();
        m.isCustomRangeActive = true;
        m.backSortingOrder = RainbowOrder - 2; // only the rain's own back parts (-21 .. -7)
        m.frontSortingOrder = FloorRippleOrder + 1;
        return m;
    }

    // ------------------------------------------------------------------ sound

    void Sound(float dt)
    {
        if (!loopSrc) return;
        float want = Intensity * volume * 0.6f;
        if (want > 0.001f && rainLoop && !loopSrc.isPlaying) { loopSrc.clip = rainLoop; loopSrc.volume = 0f; loopSrc.Play(); }
        loopSrc.volume = Mathf.MoveTowards(loopSrc.volume, want, dt * 0.5f);
        if (loopSrc.isPlaying && want <= 0.001f && loopSrc.volume <= 0.001f) loopSrc.Stop();
    }

    void Play(AudioClip clip, float pitch, float vol)
    {
        if (!clip || !fxSrc) return;
        fxSrc.pitch = pitch;
        fxSrc.PlayOneShot(clip, volume * vol);
    }

    // ------------------------------------------------------------------ helpers

    SpriteRenderer MakeSprite(string name, Sprite s, int order, bool desktop)
    {
        var go = new GameObject(name);
        go.transform.SetParent(root, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = s;
        sr.sortingOrder = order;
        sr.color = Color.white;
        if (material) sr.sharedMaterial = material;
        sr.maskInteraction = desktop ? SpriteMaskInteraction.VisibleOutsideMask : SpriteMaskInteraction.None;
        sr.enabled = false;
        return sr;
    }

    static int Free(bool[] on, ref int next)
    {
        for (int k = 0; k < on.Length; k++) { int i = (next + k) % on.Length; if (!on[i]) { next = (i + 1) % on.Length; return i; } }
        return -1;
    }

    static int Free(Bead[] a, ref int next)
    {
        for (int k = 0; k < a.Length; k++) { int i = (next + k) % a.Length; if (!a[i].on) { next = (i + 1) % a.Length; return i; } }
        return -1;
    }

    static int Free(Ring[] a, ref int next)
    {
        for (int k = 0; k < a.Length; k++) { int i = (next + k) % a.Length; if (!a[i].on) { next = (i + 1) % a.Length; return i; } }
        return -1;
    }

    static float Smooth(float u) { u = Mathf.Clamp01(u); return u * u * (3f - 2f * u); }

    // u for Smooth(u) = k (so a shower turning round mid-way carries on from where the clouds are)
    static float InvSmooth(float k)
    {
        k = Mathf.Clamp01(k);
        return 0.5f - Mathf.Sin(Mathf.Asin(1f - 2f * k) / 3f);
    }

    // its own dice (where drops fall, splashes): the buddies' dice stay as they were
    static readonly System.Random Rand = new System.Random(29);
    static float Rnd(float a, float b) => a + (float)Rand.NextDouble() * (b - a);

    // ------------------------------------------------------------------ drawing (baked once, in code)

    const int CloudW = 160, CloudH = 104, DropW = 4, DropH = 40, DropletN = 16, PuddleW = 128, PuddleH = 32, RippleW = 64, RippleH = 16, BowW = 256, BowH = 128;
    const float CloudBelly = 0.07f / 0.65f; // where its belly is, from the bottom of the picture
    static readonly Sprite[] s_cloud = new Sprite[2];
    static Sprite s_drop, s_droplet, s_puddle, s_ripple, s_bow, s_white;

    static Sprite Bake(int w, int h, Vector2 pivot, string name, Func<float, float, float, Color> paint)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = name };
        var px = new Color32[w * h];
        float texel = 1f / w;
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                Color c = paint((i + 0.5f) * texel, (j + 0.5f) * texel, texel); // x 0..1, y 0..h/w
                px[j * w + i] = new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), Mathf.Clamp01(c.a));
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return Sprite.Create(tex, new Rect(0, 0, w, h), pivot, w, 0, SpriteMeshType.FullRect); // 1 unit wide
    }

    static Sprite CloudSprite(int face) => Cached(ref s_cloud[face], ref s_tried[0 + face], () => Bake(CloudW, CloudH, new Vector2(0.5f, 0.5f), "Rain Cloud " + face, (x, y, tx) => CloudPaint(face, x, y, tx)));
    static Sprite DropSprite() => Cached(ref s_drop, ref s_tried[2], () => Bake(DropW, DropH, new Vector2(0.5f, 0.5f), "Raindrop", DropPaint));
    static Sprite DropletSprite() => Cached(ref s_droplet, ref s_tried[3], () => Bake(DropletN, DropletN, new Vector2(0.5f, 0.5f), "Droplet", DropletPaint));
    static Sprite PuddleSprite() => Cached(ref s_puddle, ref s_tried[4], () => Bake(PuddleW, PuddleH, new Vector2(0.5f, 0f), "Puddle", PuddlePaint));
    static Sprite RippleSprite() => Cached(ref s_ripple, ref s_tried[5], () => Bake(RippleW, RippleH, new Vector2(0.5f, 0.5f), "Ripple", RipplePaint));
    static Sprite RainbowSprite() => Cached(ref s_bow, ref s_tried[6], () => Bake(BowW, BowH, new Vector2(0.5f, 0f), "Rainbow", RainbowPaint));
    static Sprite WhiteSprite() => Cached(ref s_white, ref s_tried[7], () => Bake(4, 4, new Vector2(0.5f, 0.5f), "Rain Occluder", (x, y, tx) => Color.white));

    // baked once (and again only if it's gone); a sprite that can't be made isn't tried every frame
    static Sprite Cached(ref Sprite s, ref bool tried, Func<Sprite> make)
    {
        if (s || tried) return s;
        s = make();
        tried = !s;
        return s;
    }
    static readonly bool[] s_tried = new bool[8];

    static float Circle(float x, float y, float cx, float cy, float r) => Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - r;
    static Color Clear => new Color(0f, 0f, 0f, 0f);

    /// <summary>A rain cloud (x 0..1, y 0..0.65): puffy, slate blue-grey, lighter on top, a darker belly with a wavy
    /// bottom, a soft rim, and a face low on its belly: 0 = sleepy (closed eyes, a little smile), 1 = eyes open, "o".</summary>
    internal static Color CloudPaint(int face, float x, float y, float texel)
    {
        float d = Circle(x, y, 0.5f, 0.38f, 0.21f);
        d = Mathf.Min(d, Circle(x, y, 0.29f, 0.3f, 0.16f));
        d = Mathf.Min(d, Circle(x, y, 0.71f, 0.31f, 0.17f));
        d = Mathf.Min(d, Circle(x, y, 0.13f, 0.2f, 0.11f));
        d = Mathf.Min(d, Circle(x, y, 0.87f, 0.2f, 0.11f));
        d = Mathf.Min(d, Circle(x, y, 0.39f, 0.5f, 0.13f));
        d = Mathf.Min(d, Circle(x, y, 0.62f, 0.5f, 0.11f));
        float box = Mathf.Max(Mathf.Max(0.12f - x, x - 0.88f), Mathf.Max(0.08f - y, y - 0.3f)) - 0.02f;
        d = Mathf.Min(d, box);
        d = Mathf.Max(d, 0.07f + 0.01f * Mathf.Sin(x * 55f) - y);                 // the wavy belly
        float a = Mathf.Clamp01(0.5f - d / texel);
        if (a <= 0f) return Clear;
        float h = Mathf.Clamp01((y - 0.07f) / 0.5f);
        Color c = Color.Lerp(new Color(0.50f, 0.56f, 0.68f), new Color(0.80f, 0.84f, 0.91f), h);
        float hl = Mathf.Min(Mathf.Min(Circle(x, y, 0.47f, 0.47f, 0.09f), Circle(x, y, 0.27f, 0.37f, 0.06f)), Mathf.Min(Circle(x, y, 0.68f, 0.39f, 0.065f), Circle(x, y, 0.37f, 0.57f, 0.05f)));
        c = Color.Lerp(c, new Color(0.95f, 0.97f, 1f), Mathf.Clamp01(-hl / 0.05f) * 0.6f);
        float rim = Mathf.Clamp01(1f + d / (texel * 3f));
        c = Color.Lerp(c, new Color(0.38f, 0.44f, 0.58f), rim * 0.9f);
        // the face, low on its belly
        float cx = 0.5f, ey = 0.17f, ex = 0.08f;
        Color ink = new Color(0.20f, 0.22f, 0.32f);
        for (int s = -1; s <= 1; s += 2)
        {
            float ck = Mathf.Sqrt(Mathf.Pow((x - (cx + s * 0.15f)) / 1.4f, 2f) + Mathf.Pow(y - 0.125f, 2f)) - 0.026f;
            c = Color.Lerp(c, new Color(0.78f, 0.70f, 0.86f), Mathf.Clamp01(0.5f - ck / texel) * 0.6f);   // cheeks
            float exc = cx + s * ex;
            if (face == 1)
            {
                float ed = Circle(x, y, exc, ey, 0.022f);
                c = Color.Lerp(c, ink, Mathf.Clamp01(0.5f - ed / texel));
                float gl = Circle(x, y, exc - 0.007f, ey + 0.008f, 0.007f);
                c = Color.Lerp(c, Color.white, Mathf.Clamp01(0.5f - gl / texel));
            }
            else
            {
                float r0 = Circle(x, y, exc, ey + 0.012f, 0.024f);                                       // sleepy: u-shaped lids
                float ed = Mathf.Max(Mathf.Abs(r0) - 0.006f, y - (ey + 0.012f));
                c = Color.Lerp(c, ink, Mathf.Clamp01(0.5f - ed / texel));
            }
        }
        float md = face == 1 ? Mathf.Abs(Circle(x, y, cx, 0.11f, 0.014f)) - 0.006f
                             : Mathf.Max(Mathf.Abs(Circle(x, y, cx, 0.13f, 0.024f)) - 0.006f, y - 0.122f);
        c = Color.Lerp(c, ink, Mathf.Clamp01(0.5f - md / texel));
        c.a = a;
        return c;
    }

    /// <summary>A raindrop streak (x 0..1 across, y 0..10 along): light blue, rounder and brighter at the bottom, fading
    /// out towards the top.</summary>
    internal static Color DropPaint(float x, float y, float texel)
    {
        float v = y / ((float)DropH / DropW);                // 0 bottom .. 1 top
        float across = 1f - Mathf.Abs(x - 0.5f) * 2f;        // 1 in the middle
        float a = Mathf.Clamp01(across * 1.6f) * Mathf.Lerp(1f, 0.1f, v) * (v < 0.06f ? v / 0.06f : 1f);
        Color c = Color.Lerp(new Color(0.48f, 0.62f, 0.88f), new Color(0.86f, 0.94f, 1f), Mathf.Clamp01(across * 1.3f));
        c.a = a * 0.9f;
        return c;
    }

    /// <summary>A drop of water (x, y 0..1): round, light blue with a darker rim and a white shine.</summary>
    internal static Color DropletPaint(float x, float y, float texel)
    {
        float d = Circle(x, y, 0.5f, 0.5f, 0.42f);
        float a = Mathf.Clamp01(0.5f - d / texel);
        if (a <= 0f) return Clear;
        Color c = new Color(0.66f, 0.84f, 1f);
        if (d > -texel * 2.2f) c = new Color(0.32f, 0.52f, 0.84f);
        if (Circle(x, y, 0.38f, 0.62f, 0.1f) < 0f) c = Color.white;
        c.a = a * 0.95f;
        return c;
    }

    /// <summary>A puddle seen from the side (x 0..1, y 0..0.25; the bottom = the surface it lies on): a flat lens of
    /// water, a bright line along its top, darker below, a couple of reflections.</summary>
    internal static Color PuddlePaint(float x, float y, float texel)
    {
        float u = x * 2f - 1f, v = y / 0.25f;                // -1..1 along, 0..1 up
        float top = Mathf.Sqrt(Mathf.Max(0f, 1f - u * u * u * u));
        float d = (v - top) * 0.25f;                         // (in x units, roughly)
        float a = Mathf.Clamp01(0.5f - d / texel);
        if (a <= 0f || top <= 0f) return Clear;
        float k = Mathf.Clamp01(v / Mathf.Max(top, 1e-3f));
        Color c = Color.Lerp(new Color(0.30f, 0.50f, 0.84f), new Color(0.58f, 0.78f, 0.98f), k);
        if (d > -texel * 2.2f) c = Color.Lerp(c, new Color(0.88f, 0.96f, 1f), 0.85f);                          // the bright top line
        if (Mathf.Abs(v - 0.5f * top) < 0.12f && ((u > -0.55f && u < -0.25f) || (u > 0.15f && u < 0.28f))) c = Color.Lerp(c, Color.white, 0.4f); // reflections
        if (v < 0.12f) c = Color.Lerp(c, new Color(0.22f, 0.40f, 0.72f), 0.5f);                                  // darker underneath
        c.a = a * 0.8f;
        return c;
    }

    /// <summary>A ring on the water (x 0..1, y 0..0.25): a flat white ellipse outline.</summary>
    internal static Color RipplePaint(float x, float y, float texel)
    {
        float u = (x - 0.5f) / 0.46f, v = (y - 0.125f) / 0.1f;
        float r = Mathf.Sqrt(u * u + v * v);
        float d = Mathf.Abs(r - 0.85f) * 0.1f - texel * 0.7f;
        float a = Mathf.Clamp01(0.5f - d / texel);
        return new Color(1f, 1f, 1f, a * 0.9f);
    }

    /// <summary>The rainbow (x 0..1, y 0..0.5; pivot at the bottom middle = the middle of its circle): soft bands from
    /// violet inside to red outside, fading out towards its feet.</summary>
    internal static Color RainbowPaint(float x, float y, float texel)
    {
        float u = x * 2f - 1f, v = y * 2f;                   // -1..1, 0..1 (one unit = its outer radius)
        float rr = Mathf.Sqrt(u * u + v * v);
        float band = (rr - 0.72f) / 0.28f;                   // 0 inside .. 1 outside
        if (band < 0f || band > 1f) return Clear;
        Color[] stops = { new Color(0.58f, 0.38f, 0.86f), new Color(0.32f, 0.52f, 0.96f), new Color(0.30f, 0.80f, 0.92f), new Color(0.42f, 0.86f, 0.42f), new Color(1f, 0.90f, 0.32f), new Color(1f, 0.62f, 0.26f), new Color(0.96f, 0.32f, 0.32f) };
        float f = band * (stops.Length - 1);
        int i = Mathf.Min((int)f, stops.Length - 2);
        Color c = Color.Lerp(stops[i], stops[i + 1], f - i);
        float edge = Mathf.Clamp01(band / 0.08f) * Mathf.Clamp01((1f - band) / 0.08f);
        float feet = Mathf.Clamp01(v / 0.35f);
        c.a = 0.55f * edge * feet * feet;
        return c;
    }
}
