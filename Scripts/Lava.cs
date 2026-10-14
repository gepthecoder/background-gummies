using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The floor is lava (chat "lava", WhipIt > Buddy > The Floor Is Lava, or now and then by itself while you're away):
/// one of them shouts "THE FLOOR IS LAVA!", the floor (the taskbar line; with more screens every screen's floor) glows
/// and bubbles up into lava, and they all scramble up onto window tops (or desktop icons, or a friend's head) before it's
/// hot. You can help: drag a window under one, or pick one up and put it somewhere safe. Touching the lava while it's
/// hot: it yelps, bounces up smoking, goes toasty brown for a while and sits the round out at the side, cheering the
/// others on. The last one standing wins (+XP; if the time runs out, everyone still standing wins); then the lava cools
/// to rock and steams away. Snacks that drop into it sizzle up. Everything lives in the desktop: windows in front hide
/// it. Lives on the Interaction object (WhipIt > Setup).
/// </summary>
[DefaultExecutionOrder(8)] // after BuddyEater (0), the rain shelter (6) and bedtime (7): during a round, the lava wins
[DisallowMultipleComponent]
public class Lava : MonoBehaviour
{
    public enum Phase { Off, Rising, Hot, Cooling }

    [Tooltip("From the shout to hot lava (seconds): their time to get up onto something.")]
    public float warning = 6f;
    [Tooltip("How long the lava stays hot (seconds) while more than one is still standing.")]
    public float roundLength = 40f;
    [Tooltip("Now and then by itself while you're away (minutes between rounds, x..y); 0 = never.")]
    public Vector2 autoEvery = new Vector2(45f, 80f);
    [Tooltip("Sprite material (WhipIt > Setup assigns it).")]
    public Material material;
    [Header("Sounds (WhipIt > Setup assigns them)")]
    public AudioClip loopClip;
    public AudioClip bloopClip, sizzleClip, alarmClip;
    [Range(0f, 1f)] public float volume = 0.5f;

    /// <summary>The one in the scene (on the Interaction object).</summary>
    public static Lava Current { get; private set; }
    /// <summary>When the next round starts by itself (Time.time; sims set it).</summary>
    public static float NextAuto = -1f;
    /// <summary>A buddy touched the hot lava.</summary>
    public static event Action<GummyBody> Burned;
    /// <summary>A buddy won a round (still standing at the end; true: the last one standing).</summary>
    public static event Action<GummyBody, bool> Won;

    public Phase State { get; private set; }
    /// <summary>A round is on: the shout's gone out, or the floor is hot.</summary>
    public bool On => State == Phase.Rising || State == Phase.Hot;
    public bool Hot => State == Phase.Hot;
    /// <summary>How far up the lava is (0..1).</summary>
    public float Level { get; private set; }
    /// <summary>Counts (tests): rounds, buddies burnt, snacks burnt.</summary>
    public int Rounds { get; private set; }
    public int Burns { get; private set; }
    public int SnacksBurnt { get; private set; }
    /// <summary>Last round's winners (still standing at the end).</summary>
    public IReadOnlyList<GummyBody> Winners => winners;
    /// <summary>Who's in the round, safe (up on something), out (burnt).</summary>
    public bool Playing(GummyBody b) => b && ps.TryGetValue(b, out P p) && p.playing;
    public bool IsSafe(GummyBody b) => b && ps.TryGetValue(b, out P p) && p.playing && p.safe && !p.burnt;
    public bool IsOut(GummyBody b) => b && ps.TryGetValue(b, out P p) && p.playing && p.burnt;

    // ------------------------------------------------------------------ the players

    class P
    {
        public bool playing, safe, burnt, saidSafe, saidHelp, atSide, hurried, steered;
        public float since, nextPlan, nextLine, burntAt, smokeAt;
        public Vector2 spot;
        public float spotY;
        public long spotWin;
        public int fails;
    }

    readonly Dictionary<GummyBody, P> ps = new Dictionary<GummyBody, P>();
    readonly List<GummyBody> winners = new List<GummyBody>();
    readonly Dictionary<long, float> banned = new Dictionary<long, float>(); // (window tops nobody could get to lately)
    static readonly Dictionary<GummyBody, float> s_toast = new Dictionary<GummyBody, float>();
    int startCount;
    float t, phaseAt, lastAt = -1f;

    const float Touch = 0.07f;     // feet this close to the floor while it's hot: burnt
    const float CoolTime = 3.5f;
    const float ToastFade = 120f;  // seconds to fade from crispy back to normal (after the round)

    // ------------------------------------------------------------------ life

    void Awake()
    {
        root = new GameObject("Lava").transform;
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
        if (On) Release();
        if (loopSrc) loopSrc.Stop();
    }

    void OnDestroy()
    {
        if (root) Destroy(root.gameObject);
    }

    // ------------------------------------------------------------------ you

    /// <summary>Why a round can't start now (null: it can).</summary>
    public static string Blocked()
    {
        if (!Current) return "no lava";
        if (Current.State != Phase.Off) return "already";
        if (Bedtime.Sleeping) return "bedtime";
        if (Rain.Current && Rain.Current.Raining) return "rain";
        if (LevelUpParty.Current || BandJam.Current || PickupGame.Current || BuddyTower.Current || BuddyHideSeek.Playing || CaughtInTheAct.Partying) return "busy";
        return null;
    }

    /// <summary>The floor is lava: caller (or one of them) shouts it, the round starts. False if it can't now.</summary>
    public bool Begin(GummyBody caller = null)
    {
        if (Blocked() != null) return false;
        var bs = Bodies();
        if (bs.Count == 0) return false;
        float now = Time.time;
        ps.Clear();
        winners.Clear();
        foreach (var b in bs)
        {
            ps[b] = new P { playing = true, since = now, nextLine = now + Rnd(5f, 9f) };
            var br = b.GetComponent<GummyBrain>();
            if (br && br.Current == GummyBrain.State.Asleep) br.WakeUp();
        }
        startCount = bs.Count;
        if (!caller || !ps.ContainsKey(caller)) caller = bs[Rand.Next(bs.Count)];
        Say(caller, Line(caller, "THE FLOOR IS LAVA!", "THE FLOOR IS LAVA!", "THE FLOOR IS LAVA!", "THE FLOOR IS LAVA!", "RAWR! FLOOR IS LAVA!", "QUACK! FLOOR IS LAVA!", "THE FLOOR. IS LAVA."));
        int k = 0;
        foreach (var b in bs)
        {
            if (b == caller || Rand.NextDouble() > 0.55 || k >= 2) continue;
            k++;
            Say(b, Line(b, "AAAAH!", "NOT AGAIN!", "HOT COFFEE!", "I'M ALREADY COOKED!", "RAWR! HOT!", "QUACK QUACK!", "WE DO NOT MELT"), 0.8f + 0.5f * k);
        }
        State = Phase.Rising;
        t = 0f;
        phaseAt = now;
        Rounds++;
        Play(alarmClip, 1f, 0.7f);
        return true;
    }

    /// <summary>Stop it now (chat "no lava", the menu again): it cools down, nobody wins.</summary>
    public void Stop()
    {
        if (!On) return;
        winners.Clear();
        Release();
        State = Phase.Cooling;
        t = 0f;
        phaseAt = Time.time;
        Play(sizzleClip, 0.6f, 0.6f);
    }

    // ------------------------------------------------------------------ each frame

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("P.Update", t0); }
    void UpdateTimed()
    {
        float dt = Mathf.Min(Time.deltaTime, 0.05f), now = Time.time;
        t += dt;
        switch (State)
        {
            case Phase.Off:
                Level = 0f;
                Auto(now);
                break;
            case Phase.Rising:
                Level = Smooth(t / Mathf.Max(warning, 0.1f));
                Steer(now);
                if (t >= warning) { State = Phase.Hot; t = 0f; phaseAt = now; Level = 1f; }
                break;
            case Phase.Hot:
                Level = 1f;
                Steer(now);
                BurnCheck(now);
                SnackCheck();
                Fountain(now);
                int left = Standing();
                // the last one standing: once it's clear it's still up there (not just in the air over the lava)
                bool last = left == 0 || (startCount >= 2 && left <= 1);
                if (!last) lastAt = -1f;
                else if (lastAt < 0f) lastAt = now;
                if (t >= roundLength || left == 0 || (last && now - lastAt >= 1.5f)) EndRound(now);
                break;
            case Phase.Cooling:
                Level = 1f;
                if (t >= CoolTime) { State = Phase.Off; Level = 0f; ps.Clear(); }
                break;
        }
        Toasts(dt);
        Draw(dt, now);
        Sound(dt);
    }

    int Standing()
    {
        int n = 0;
        foreach (var kv in ps) if (kv.Value.playing && !kv.Value.burnt && kv.Key && kv.Key.isActiveAndEnabled) n++;
        return n;
    }

    // now and then by itself: while you're away, nothing else going on, and there's a window to get up on
    void Auto(float now)
    {
        if (autoEvery.y <= 0f) return;
        if (NextAuto < 0f) { NextAuto = now + Rnd(15f, 30f) * 60f; return; }
        if (now < NextAuto) return;
        float idle = WorkClock.IdleProbe != null ? WorkClock.IdleProbe() : DesktopInput.IdleSeconds;
        if (Blocked() != null || idle < 20f || !AnyPerch() || Bodies().Count < 2) { NextAuto = now + 60f; return; } // (napping ones wake up for it)
        NextAuto = now + Rnd(autoEvery.x, autoEvery.y) * 60f;
        Begin();
    }

    static bool AnyPerch()
    {
        foreach (var p in DesktopPlatforms.Current) if (!DesktopPlatforms.IsScreen(p.id) && p.x1 - p.x0 > 0.6f) return true;
        return false;
    }

    // ------------------------------------------------------------------ getting up onto something

    void Steer(float now)
    {
        foreach (var kv in ps)
        {
            var b = kv.Key;
            var p = kv.Value;
            if (!p.playing || !b || !b.isActiveAndEnabled) continue;
            var br = b.GetComponent<GummyBrain>();
            if (!br) continue;
            br.KeepAwake(); // (no napping in a game)
            if (p.burnt) { ToTheSide(b, br, p, now); continue; }
            if (b.Held) { p.safe = true; p.steered = false; continue; } // (in your hand: safe; you put it down)
            bool up = b.Grounded && !OnLava(b);
            if (up)
            {
                // up on a window, an icon or a friend: stays put till it's over
                if (!p.safe && !p.saidSafe && Rand.NextDouble() < 0.6) { p.saidSafe = true; Say(b, Line(b, "SAFE!", "PHEW! MADE IT!", "STILL BREWING!", "BUN SAFE!", "DINO IS SAFE!", "QUACK! SAFE!", "WE ARE. ABOVE IT.")); }
                p.safe = true;
                br.Food = null;
                br.StayPut = true;
                Unhurry(br, p);
                if (State == Phase.Hot && now >= p.nextLine)
                {
                    p.nextLine = now + Rnd(9f, 16f);
                    br.Act(new Vector2(0.94f, 1.08f), 0.7f); // (on tiptoe, peering down)
                    if (Rand.NextDouble() < 0.45) Say(b, Line(b, Pick("DON'T MOVE MY WINDOW!", "SO HOT DOWN THERE..."), Pick("I'M NOT LOOKING DOWN", "MY LEAVES ARE CURLING"), Pick("SMELLS LIKE ROASTING", "STEAMY..."), Pick("I CAN SMELL MYSELF", "SIZZLE SIZZLE..."), Pick("RAWR! HOT FLOOR!", "DINO LIKES VOLCANOES"), Pick("QUACK... HOT", "NO SWIMMING TODAY"), "WE WAIT. WE WIN."));
                }
                continue;
            }
            if (!b.Grounded) { if (p.steered) br.Food = new Vector2(p.spot.x, p.spot.y + b.size * 0.3f); continue; } // (on its way: in the air)
            // on the floor: up onto something, quick
            p.safe = false;
            br.StayPut = false;
            if (!p.hurried) { p.hurried = true; br.Hurry = 1.8f; }
            if (now >= p.nextPlan || !p.steered)
            {
                p.nextPlan = now + 0.8f;
                if (!FindPerch(b, br, p, now))
                {
                    p.steered = false;
                    br.Food = null;
                    if (!p.saidHelp) { p.saidHelp = true; Say(b, Line(b, "HELP! A WINDOW!", "LIFT ME UP!", "GET ME OUTTA HERE!", "I'M GONNA GRILL!", "RAWR! HELP!", "QUAAACK! HELP!", "LIFT US. NOW.")); }
                    if (b.Grounded && br.Current == GummyBrain.State.Idle && Rand.NextDouble() < 0.5) br.Dodge(Rand.NextDouble() < 0.5 ? -1f : 1f, 0.6f * Mathf.Sqrt(b.size / 0.6f), 4f * Mathf.Sqrt(b.size / 0.6f)); // (hot feet!)
                    continue;
                }
            }
            if (p.steered) { br.Food = new Vector2(p.spot.x, p.spot.y + b.size * 0.3f); br.FoodSurface = p.spotY; }
        }
    }

    // the nearest window top (or icon) it can get to with room for it, not where a friend's already going
    bool FindPerch(GummyBody b, GummyBrain br, P p, float now)
    {
        float hw = Mathf.Max(b.Extent.width, b.size * 0.8f) * 0.5f, feet = b.Extent.yMin;
        var cands = new List<(float score, Vector2 at, long id)>();
        foreach (var pl in DesktopPlatforms.Current)
        {
            if (DesktopPlatforms.IsScreen(pl.id) || pl.x1 - pl.x0 < hw * 1.6f || pl.y < feet + 0.2f) continue;
            if (banned.TryGetValue(pl.id, out float until) && now < until) continue;
            float lo = pl.x0 + hw * 1.1f, hi = pl.x1 - hw * 1.1f;
            if (hi < lo) { lo = hi = 0.5f * (pl.x0 + pl.x1); }
            float x = Mathf.Clamp(b.Center.x, lo, hi);
            // room for it there (a friend's spot or a friend standing there: a bit further along)
            for (int tries = 0; tries < 6 && Crowded(b, x, pl.y, hw); tries++)
            {
                float step = hw * 2.3f * (1 + tries / 2) * (tries % 2 == 0 ? 1f : -1f);
                x = Mathf.Clamp(Mathf.Clamp(b.Center.x, lo, hi) + step, lo, hi);
            }
            if (Crowded(b, x, pl.y, hw)) continue;
            cands.Add((Mathf.Abs(x - b.Center.x) + 0.6f * Mathf.Abs(pl.y - feet), new Vector2(x, pl.y), pl.id));
        }
        cands.Sort((a, c) => a.score.CompareTo(c.score));
        float best = float.MaxValue;
        bool found = false;
        for (int i = 0; i < cands.Count && i < 8; i++)
        {
            var c = cands[i];
            if (c.score > best) break; // (no cheaper way can be further away)
            if (!br.PlanTo(new Vector2(c.at.x, c.at.y + b.size * 0.3f), c.at.y, out float cost)) continue;
            cost += c.score * 0.3f;
            if (cost < best) { best = cost; p.spot = c.at; p.spotY = c.at.y; p.spotWin = c.id; found = true; }
        }
        if (p.steered && found == false && p.spotWin != 0) banned[p.spotWin] = now + 8f;
        p.steered = found;
        return found;
    }

    bool Crowded(GummyBody me, float x, float y, float hw)
    {
        foreach (var kv in ps)
        {
            if (kv.Key == me || !kv.Key) continue;
            var o = kv.Value;
            if (o.steered && !o.burnt && Mathf.Abs(o.spotY - y) < 0.1f && Mathf.Abs(o.spot.x - x) < hw * 2.1f) return true;
            var ob = kv.Key;
            if (ob.Grounded && Mathf.Abs(ob.Extent.yMin - y) < 0.12f && Mathf.Abs(ob.Center.x - x) < hw + ob.Extent.width * 0.5f) return true;
        }
        return false;
    }

    // its feet in the lava: on (or down at) a floor anywhere under it (one foot on the floor's edge counts)
    bool OnLava(GummyBody b)
    {
        Floors();
        float feet = b.Extent.yMin - b.SkateLift, x0 = b.Extent.xMin + 0.02f, x1 = b.Extent.xMax - 0.02f;
        foreach (var f in floors)
            if (f.y > x0 && f.x < x1 && feet <= f.z + Touch && feet >= f.z - 0.4f) return true;
        return false;
    }

    void Floors()
    {
        if (floorsVersion == ScreenBounds.Version && floors.Count > 0) return;
        Layout();
    }

    // ------------------------------------------------------------------ hot!

    void BurnCheck(float now)
    {
        foreach (var kv in ps)
        {
            var b = kv.Key;
            var p = kv.Value;
            if (!p.playing || p.burnt || !b || !b.isActiveAndEnabled || b.Held) continue;
            if (!OnLava(b)) continue;
            Burn(b, p, now);
        }
    }

    void Burn(GummyBody b, P p, float now)
    {
        p.burnt = true;
        p.safe = p.steered = p.atSide = false;
        p.burntAt = now;
        p.smokeAt = now;
        Burns++;
        s_toast[b] = 1f;
        b.Toast = 1f;
        var br = b.GetComponent<GummyBrain>();
        if (br) { br.Food = null; br.StayPut = false; Unhurry(br, p); br.NoDizzyUntil = now + 0.5f; }
        float S = Mathf.Sqrt(b.size / 0.6f), side = Rand.NextDouble() < 0.5 ? -1f : 1f;
        b.Hop(new Vector2(side * 1.4f * S - b.Velocity.x, GummyBody.JumpSpeed(1.5f * b.size / 0.6f, b.gravity, b.Drag) - b.Velocity.y), 0.8f);
        b.Spin(-side * 7f);
        for (int i = 0; i < 6; i++) Puff(b.Center + new Vector2(Rnd(-0.3f, 0.3f), Rnd(-0.3f, 0.1f)) * b.size, new Vector2(Rnd(-0.4f, 0.4f), Rnd(0.6f, 1.2f)), Rnd(0.25f, 0.4f) * Mathf.Max(0.6f, b.size / 0.6f), new Color(0.3f, 0.28f, 0.27f), 1.2f);
        for (int i = 0; i < 8; i++) Ember(new Vector2(b.Center.x + Rnd(-0.3f, 0.3f), b.Extent.yMin + 0.05f), new Vector2(Rnd(-1.5f, 1.5f), Rnd(1.5f, 3.5f)));
        Play(sizzleClip, Rnd(0.95f, 1.1f), 0.8f);
        Say(b, Line(b, "HOT HOT HOT!", "MY FEET!", "OVER-ROASTED!", "WELL DONE!", "RAWR! OUCH!", "FRIED DUCK?!", "WE ARE... CRISPY"));
        Burned?.Invoke(b);
    }

    // out: over to the side of its floor, where it sits it out (in the lava: it's crispy already) and cheers
    void ToTheSide(GummyBody b, GummyBrain br, P p, float now)
    {
        if (b.Held) { br.Food = null; return; }
        if (now - p.burntAt < 1.2f || !b.Grounded) return; // (the bounce)
        if (!p.atSide)
        {
            Vector2 span = b.SupportSpan;
            BuddyRoute.Build(Mathf.Max(b.Extent.height, b.size), b.solidWindows); // (the bit of floor it's on: windows standing on it end it)
            int si = BuddyRoute.SurfaceAt(b.Center.x, b.Extent.yMin, false);
            if (si >= 0) { var sf = BuddyRoute.Surfaces[si]; span = new Vector2(Mathf.Max(span.x, sf.x0), Mathf.Min(span.y, sf.x1)); }
            float hw = Mathf.Max(b.Extent.width, b.size) * 0.5f, lo = span.x + hw * 1.2f, hi = span.y - hw * 1.2f;
            if (b.OnPlatform || hi <= lo) { p.atSide = true; } // (it landed on something: it stays there)
            else
            {
                // the nearer end, past where the others are
                float x = b.Center.x - lo < hi - b.Center.x ? lo : hi;
                foreach (var kv in ps) if (kv.Key != b && kv.Value.burnt && kv.Value.atSide && Mathf.Abs(kv.Value.spot.x - x) < hw * 2.2f) x += (x == lo ? 1f : -1f) * hw * 2.3f;
                p.spot = new Vector2(x, b.Extent.yMin);
                p.spotY = b.Extent.yMin;
                if (Mathf.Abs(b.Center.x - x) < 0.3f + 0.4f * b.size || now - p.burntAt > 12f) p.atSide = true;
                else { br.StayPut = false; br.Food = new Vector2(x, p.spotY + b.size * 0.3f); br.FoodSurface = p.spotY; }
            }
            if (p.atSide) { br.Food = null; br.StayPut = true; p.nextLine = now + Rnd(2f, 5f); }
            return;
        }
        br.Food = null;
        br.StayPut = true;
        if (State == Phase.Hot && now >= p.nextLine)
        {
            p.nextLine = now + Rnd(7f, 13f);
            GummyBody fav = null;
            foreach (var kv in ps) if (kv.Value.playing && !kv.Value.burnt && kv.Key && (!fav || Rand.NextDouble() < 0.5)) fav = kv.Key;
            if (fav && Rand.NextDouble() < 0.6) Say(b, Pick("GO " + NameOf(fav) + "!", "YOU GOT THIS, " + NameOf(fav) + "!", "DON'T FALL, " + NameOf(fav) + "!"));
            else if (Rand.NextDouble() < 0.4) Say(b, Line(b, "*SIZZLE*", "I'M A SUN-DRIED TOMATO", "DARK ROAST NOW...", "CHAR-GRILLED", "RAWR... TOASTY", "QUACK... CRISPY", "WE ARE. WELL DONE."));
        }
    }

    // snacks that drop into the hot lava sizzle up
    void SnackCheck()
    {
        if (Snack.All.Count == 0) return;
        Floors();
        foreach (var s in new List<Snack>(Snack.All)) // (one that sizzles up leaves the list)
        {
            if (!s || s.Held || s.Eaten || s.Fading) continue;
            bool hot = false;
            foreach (var f in floors) if (s.Position.x > f.x && s.Position.x < f.y && s.SurfaceY <= f.z + 0.05f && s.SurfaceY >= f.z - 0.4f) { hot = true; break; }
            if (!hot) continue;
            s.FadeNow();
            SnacksBurnt++;
            Play(sizzleClip, Rnd(1.3f, 1.6f), 0.4f);
            for (int i = 0; i < 3; i++) Puff(s.Position, new Vector2(Rnd(-0.3f, 0.3f), Rnd(0.5f, 1f)), Rnd(0.15f, 0.25f), new Color(0.35f, 0.33f, 0.32f), 1f);
        }
    }

    // now and then a big bubble bursts and throws up a fountain of glowing drops
    float nextFountain;

    void Fountain(float now)
    {
        if (t < 1f) { nextFountain = now + Rnd(3f, 6f); return; }
        if (now < nextFountain || floors.Count == 0) return;
        nextFountain = now + Rnd(5f, 9f);
        float total = 0f;
        foreach (var f in floors) total += f.y - f.x;
        Vector2 at = AlongFloors(Rnd(0f, total)) + new Vector2(0f, TileH * 0.7f);
        for (int i = 0; i < 16; i++) { float vy = Rnd(3f, 5.5f); Ember(at, new Vector2(Rnd(-1.2f, 1.2f), vy), true, 2f * vy / 9f + 0.05f); }
        Play(bloopClip, Rnd(0.6f, 0.75f), 0.6f);
        // the nearest one up on something goes "WHOA!"
        GummyBody near = null;
        float best = 4f;
        foreach (var kv in ps)
            if (kv.Key && kv.Value.safe && !kv.Value.burnt && Mathf.Abs(kv.Key.Center.x - at.x) < best) { best = Mathf.Abs(kv.Key.Center.x - at.x); near = kv.Key; }
        if (near && Rand.NextDouble() < 0.6) Say(near, Line(near, "WHOA!", "EEK!", "HOT SPLASH!", "WATCH THE BUN!", "RAWR! VOLCANO!", "QUACK!", "PRETTY."));
    }

    // ------------------------------------------------------------------ the end

    void EndRound(float now)
    {
        winners.Clear();
        foreach (var kv in ps) if (kv.Value.playing && !kv.Value.burnt && kv.Key && kv.Key.isActiveAndEnabled) winners.Add(kv.Key);
        bool last = winners.Count == 1 && startCount >= 2;
        foreach (var w in winners)
        {
            var prog = w.GetComponent<BuddyProgress>();
            if (prog) prog.AddXp(last ? 3 : 2, w.Center);
            var br = w.GetComponent<GummyBrain>();
            if (br) br.Treat();
        }
        foreach (var w in winners) Won?.Invoke(w, last);
        if (last) Say(winners[0], Line(winners[0], "KING OF THE WINDOW!", "LAST TOMATO STANDING!", "UNBURNT BEAN!", "RAW AND PROUD!", "DINO SURVIVES!", "QUACK! I WIN!", "WE WIN. OBVIOUSLY."));
        else if (winners.Count > 1) { Say(winners[0], Pick("WE MADE IT!", "STILL STANDING!")); Say(winners[1], Pick("NOT A SCRATCH!", "TOO EASY!"), 0.9f); }
        else
        {
            GummyBody any = null;
            foreach (var kv in ps) if (kv.Key && kv.Key.isActiveAndEnabled) { any = kv.Key; break; }
            if (any) Say(any, Pick("EVERYONE'S TOAST!", "WE ALL MELTED..."));
        }
        // the losers, at the side
        int k = 0;
        foreach (var kv in ps)
        {
            if (!kv.Value.burnt || !kv.Key || k >= 2 || Rand.NextDouble() < 0.4) continue;
            k++;
            Say(kv.Key, last ? Pick("GG, " + NameOf(winners[0]) + "!", "REMATCH!", "NEXT TIME...") : Pick("REMATCH!", "OUCH...", "STILL HOT..."), 1.4f + 0.6f * k);
        }
        Release();
        State = Phase.Cooling;
        t = 0f;
        phaseAt = now;
        Play(sizzleClip, 0.55f, 0.7f); // (the lava cooling to rock)
        if (NextAuto >= 0f) NextAuto = Mathf.Max(NextAuto, now + autoEvery.x * 60f);
    }

    // everyone back to normal (they keep their toast)
    void Release()
    {
        foreach (var kv in ps)
        {
            var b = kv.Key;
            if (!b) continue;
            var br = b.GetComponent<GummyBrain>();
            if (!br) continue;
            br.StayPut = false;
            if (kv.Value.steered || kv.Value.burnt) br.Food = null;
            Unhurry(br, kv.Value);
        }
    }

    static void Unhurry(GummyBrain br, P p)
    {
        if (p.hurried) br.Hurry = 1f;
        p.hurried = false;
    }

    // crispy, then slowly back (smoking for a bit while it's in the lava)
    void Toasts(float dt)
    {
        if (s_toast.Count == 0) return;
        var keys = new List<GummyBody>(s_toast.Keys);
        float now = Time.time;
        foreach (var b in keys)
        {
            if (!b) { s_toast.Remove(b); continue; }
            float v = s_toast[b];
            bool inRound = On && ps.TryGetValue(b, out P p) && p.burnt;
            if (!inRound) v -= dt / ToastFade;
            if (v <= 0f) { s_toast.Remove(b); b.Toast = 0f; continue; }
            s_toast[b] = v;
            b.Toast = v;
            if (inRound && b.isActiveAndEnabled)
            {
                var pp = ps[b];
                float every = now - pp.burntAt < 2.5f ? 0.18f : 1.4f;
                if (now >= pp.smokeAt) { pp.smokeAt = now + every * Rnd(0.8f, 1.2f); Puff(new Vector2(b.Center.x + Rnd(-0.25f, 0.25f) * b.size, b.Extent.yMax - 0.1f * b.size), new Vector2(Rnd(-0.2f, 0.2f), Rnd(0.5f, 0.9f)), Rnd(0.18f, 0.3f) * Mathf.Max(0.6f, b.size / 0.6f), new Color(0.32f, 0.3f, 0.3f), 1.5f); }
            }
        }
    }

    // ------------------------------------------------------------------ drawing

    /// <summary>Draw orders: all of it in the desktop, behind the buddies (windows in front hide it: sprite masks in the
    /// range -20..-13): the glow, the lava, its bubbles, embers, smoke, steam.</summary>
    public const int GlowOrder = -19, LavaOrder = -18, BubbleOrder = -17, EmberOrder = -16, SmokeOrder = -15, SteamOrder = -14;
    const float TileH = 0.375f;    // the lava strip (world units): 1 wide, this tall (its wavy top about 0.27 up)
    const int Frames = 8, MaxBubbles = 24, MaxEmbers = 60, MaxPuffs = 48, MaxMasks = 16;

    Transform root;
    AudioSource loopSrc, fxSrc;
    readonly List<SpriteRenderer> tiles = new List<SpriteRenderer>();
    readonly List<SpriteRenderer> glows = new List<SpriteRenderer>();
    readonly List<SpriteMask> masks = new List<SpriteMask>();
    readonly List<Vector3> floors = new List<Vector3>();
    int floorsVersion = -1, frame;
    float frameT, bubbleAcc, emberAcc, lastBloop = -1f;

    struct Bub { public Vector2 pos; public float t, life, size; public bool on; }
    readonly Bub[] bubs = new Bub[MaxBubbles];
    readonly SpriteRenderer[] bubSr = new SpriteRenderer[MaxBubbles];
    struct Spark { public Vector2 pos, vel; public float t, life, size; public bool on, heavy; }
    readonly Spark[] embers = new Spark[MaxEmbers];
    readonly SpriteRenderer[] emberSr = new SpriteRenderer[MaxEmbers];
    struct Puf { public Vector2 pos, vel; public float t, life, size; public Color color; public bool on, steam; }
    readonly Puf[] puffs = new Puf[MaxPuffs];
    readonly SpriteRenderer[] puffSr = new SpriteRenderer[MaxPuffs];
    int nextBub, nextEmber, nextPuff;

    void Draw(float dt, float now)
    {
        bool show = State != Phase.Off;
        if (show && floorsVersion != ScreenBounds.Version) Layout();
        float cool = State == Phase.Cooling ? Mathf.Clamp01(t / CoolTime) : 0f;
        // the strip: waves rolling along (8 frames), rising out of the floor, cooling to dark rock and fading away
        frameT += dt;
        if (frameT >= 0.11f) { frameT = 0f; frame = (frame + 1) % Frames; }
        Sprite tile = TileSprite(frame);
        float flicker = State == Phase.Rising && t < 1.2f ? 0.75f + 0.25f * Mathf.Sin(now * 25f) : 1f; // (the alarm: it flickers up)
        Color tint = cool > 0f ? Color.Lerp(Color.white, new Color(0.34f, 0.3f, 0.3f), Smooth(cool * 2f)) : new Color(flicker, flicker, flicker);
        tint.a = cool > 0.5f ? 1f - Smooth((cool - 0.5f) * 2f) : 1f;
        for (int i = 0; i < tiles.Count; i++)
        {
            var sr = tiles[i];
            if (sr.enabled != show) sr.enabled = show;
            if (!show) continue;
            sr.sprite = tile;
            Vector3 s = sr.transform.localScale;
            if (Mathf.Abs(s.y - Level) > 1e-4f) sr.transform.localScale = new Vector3(s.x, Mathf.Max(Level, 0.001f), 1f);
            sr.color = tint;
        }
        float glowA = Level * (1f - cool) * (0.85f + 0.15f * Mathf.Sin(now * 2.3f));
        for (int i = 0; i < glows.Count; i++)
        {
            var sr = glows[i];
            if (sr.enabled != show) sr.enabled = show;
            if (show) sr.color = new Color(1f, 1f, 1f, glowA);
        }
        // bubbles and embers while it's hot (and rising)
        if (show && cool <= 0f && floors.Count > 0)
        {
            float total = 0f;
            foreach (var f in floors) total += f.y - f.x;
            bubbleAcc += dt * total * 0.22f * Level;
            emberAcc += dt * total * 0.35f * Level;
            while (bubbleAcc >= 1f) { bubbleAcc -= 1f; SpawnBubble(total); }
            while (emberAcc >= 1f) { emberAcc -= 1f; Vector2 at = AlongFloors(Rnd(0f, total)); Ember(at + new Vector2(0f, TileH * 0.75f), new Vector2(Rnd(-0.2f, 0.2f), Rnd(0.4f, 1.1f))); }
        }
        if (cool > 0f && cool < 0.7f && floors.Count > 0 && Rand.NextDouble() < dt * 14f)
        {
            float total = 0f;
            foreach (var f in floors) total += f.y - f.x;
            Vector2 at = AlongFloors(Rnd(0f, total));
            Puff(at + new Vector2(0f, TileH * 0.6f), new Vector2(Rnd(-0.15f, 0.15f), Rnd(0.5f, 1f)), Rnd(0.3f, 0.5f), new Color(0.92f, 0.92f, 0.94f), 1.6f, true);
        }
        StepBubbles(dt, now);
        StepEmbers(dt);
        StepPuffs(dt);
        UpdateMasks(show || AnyParticles());
    }

    bool AnyParticles()
    {
        for (int i = 0; i < MaxPuffs; i++) if (puffs[i].on) return true;
        for (int i = 0; i < MaxEmbers; i++) if (embers[i].on) return true;
        return false;
    }

    // a tile per unit of floor (the last one on each floor squeezed to fit), and a glow over each floor
    void Layout()
    {
        floorsVersion = ScreenBounds.Version;
        ScreenBounds.AllFloors(floors);
        int n = 0, g = 0;
        foreach (var f in floors)
        {
            float x = f.x;
            while (x < f.y - 0.01f)
            {
                float w = Mathf.Min(1f, f.y - x);
                if (n >= tiles.Count) tiles.Add(MakeSprite("Lava", null, LavaOrder));
                var sr = tiles[n++];
                sr.transform.position = new Vector3(x, f.z, 0f);
                sr.transform.localScale = new Vector3(w, Mathf.Max(Level, 0.001f), 1f);
                x += w;
            }
            if (g >= glows.Count) glows.Add(MakeSprite("Lava Glow", GlowSprite(), GlowOrder));
            var gs = glows[g++];
            gs.transform.position = new Vector3(f.x, f.z, 0f);
            gs.transform.localScale = new Vector3((f.y - f.x) * 16f, 1.8f, 1f);
        }
        while (tiles.Count > n) { Destroy(tiles[tiles.Count - 1].gameObject); tiles.RemoveAt(tiles.Count - 1); }
        while (glows.Count > g) { Destroy(glows[glows.Count - 1].gameObject); glows.RemoveAt(glows.Count - 1); }
    }

    // a point along all the floors (u = 0..their total width), on the floor
    Vector2 AlongFloors(float u)
    {
        foreach (var f in floors)
        {
            float w = f.y - f.x;
            if (u <= w) return new Vector2(f.x + u, f.z);
            u -= w;
        }
        var l = floors[floors.Count - 1];
        return new Vector2(l.y, l.z);
    }

    void SpawnBubble(float total)
    {
        int i = Free(bubs, ref nextBub);
        if (i < 0) return;
        Vector2 at = AlongFloors(Rnd(0f, total));
        bubs[i] = new Bub { pos = at + new Vector2(0f, TileH * Rnd(0.6f, 0.76f) * Level), life = Rnd(0.5f, 1.1f), size = Rnd(0.08f, 0.18f), on = true };
        if (!bubSr[i]) bubSr[i] = MakeSprite("Lava Bubble", BubbleSprite(), BubbleOrder);
    }

    void StepBubbles(float dt, float now)
    {
        for (int i = 0; i < MaxBubbles; i++)
        {
            ref Bub b = ref bubs[i];
            if (!b.on) { if (bubSr[i] && bubSr[i].enabled) bubSr[i].enabled = false; continue; }
            b.t += dt;
            float u = b.t / b.life;
            if (u >= 1f || State == Phase.Cooling)
            {
                b.on = false;
                bubSr[i].enabled = false;
                if (State == Phase.Cooling) continue;
                for (int k = 0; k < 3; k++) Ember(b.pos + new Vector2(0f, b.size * 0.4f), new Vector2(Rnd(-0.9f, 0.9f), Rnd(1.2f, 2.4f)), true);
                if (now - lastBloop > 0.12f && Rand.NextDouble() < 0.5) { lastBloop = now; Play(bloopClip, Rnd(0.8f, 1.25f), 0.35f); }
                continue;
            }
            float s = b.size * Smooth(Mathf.Min(1f, u * 1.6f));
            b.pos.y += dt * 0.06f;
            var sr = bubSr[i];
            sr.enabled = true;
            sr.transform.position = new Vector3(b.pos.x, b.pos.y, 0f);
            sr.transform.localScale = new Vector3(s * 2f, s * 1.7f, 1f);
        }
    }

    // an ember drifting up, or (splash) a drop of lava flung up that falls back
    void Ember(Vector2 at, Vector2 vel, bool splash = false, float life = 0f)
    {
        int i = Free(embers, ref nextEmber);
        if (i < 0) return;
        embers[i] = new Spark { pos = at, vel = vel, life = life > 0f ? life : splash ? Rnd(0.4f, 0.7f) : Rnd(0.9f, 1.8f), size = splash ? Rnd(0.035f, 0.06f) : Rnd(0.025f, 0.05f), on = true, heavy = splash };
        if (!emberSr[i]) emberSr[i] = MakeSprite("Lava Ember", EmberSprite(), EmberOrder);
    }

    void StepEmbers(float dt)
    {
        for (int i = 0; i < MaxEmbers; i++)
        {
            ref Spark e = ref embers[i];
            if (!e.on) { if (emberSr[i] && emberSr[i].enabled) emberSr[i].enabled = false; continue; }
            e.t += dt;
            if (e.t >= e.life) { e.on = false; emberSr[i].enabled = false; continue; }
            e.vel.y -= (e.heavy ? 9f : 0.4f) * dt; // (splashes arc back down, embers drift up)
            e.vel.x += Mathf.Sin(e.t * 9f + i) * 0.6f * dt;
            e.pos += e.vel * dt;
            var sr = emberSr[i];
            sr.enabled = true;
            float a = 1f - e.t / e.life;
            sr.transform.position = new Vector3(e.pos.x, e.pos.y, 0f);
            sr.transform.localScale = new Vector3(e.size * 2f, e.size * 2f, 1f);
            sr.color = new Color(1f, 0.75f + 0.25f * a, 0.3f + 0.3f * a, a);
        }
    }

    void Puff(Vector2 at, Vector2 vel, float size, Color color, float life, bool steam = false)
    {
        int i = Free(puffs, ref nextPuff);
        if (i < 0) return;
        puffs[i] = new Puf { pos = at, vel = vel, size = size, color = color, life = life * Rnd(0.8f, 1.2f), on = true, steam = steam };
        if (!puffSr[i]) puffSr[i] = MakeSprite("Lava Smoke", PuffSprite(), SmokeOrder);
        puffSr[i].sortingOrder = steam ? SteamOrder : SmokeOrder;
    }

    void StepPuffs(float dt)
    {
        for (int i = 0; i < MaxPuffs; i++)
        {
            ref Puf p = ref puffs[i];
            if (!p.on) { if (puffSr[i] && puffSr[i].enabled) puffSr[i].enabled = false; continue; }
            p.t += dt;
            if (p.t >= p.life) { p.on = false; puffSr[i].enabled = false; continue; }
            float u = p.t / p.life;
            p.vel *= 1f - 1.2f * dt;
            p.vel.x += Mathf.Sin(p.t * 3f + i) * 0.3f * dt;
            p.pos += p.vel * dt;
            var sr = puffSr[i];
            sr.enabled = true;
            float s = p.size * (0.6f + 1.1f * u);
            sr.transform.position = new Vector3(p.pos.x, p.pos.y, 0f);
            sr.transform.localScale = new Vector3(s, s, 1f);
            Color c = p.color;
            c.a = (p.steam ? 0.55f : 0.7f) * Mathf.Min(1f, u * 6f) * (1f - u);
            sr.color = c;
        }
    }

    void UpdateMasks(bool any)
    {
        any &= Occlusion.Enabled;
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
        var go = new GameObject("Lava Mask");
        go.transform.SetParent(root, false);
        var m = go.AddComponent<SpriteMask>();
        m.sprite = WhiteSprite();
        m.isCustomRangeActive = true;
        m.backSortingOrder = GlowOrder - 1; // only the lava's own parts (-20 .. -13)
        m.frontSortingOrder = SteamOrder + 1;
        return m;
    }

    SpriteRenderer MakeSprite(string name, Sprite s, int order)
    {
        var go = new GameObject(name);
        go.transform.SetParent(root, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = s;
        sr.sortingOrder = order;
        sr.color = Color.white;
        if (material) sr.sharedMaterial = material;
        sr.maskInteraction = SpriteMaskInteraction.VisibleOutsideMask;
        sr.enabled = false;
        return sr;
    }

    // ------------------------------------------------------------------ sound

    void Sound(float dt)
    {
        if (!loopSrc) return;
        float want = (State == Phase.Rising || State == Phase.Hot ? Level : 0f) * volume * 0.55f;
        if (want > 0.001f && loopClip && !loopSrc.isPlaying) { loopSrc.clip = loopClip; loopSrc.volume = 0f; loopSrc.Play(); }
        loopSrc.volume = Mathf.MoveTowards(loopSrc.volume, want, dt * 0.6f);
        if (loopSrc.isPlaying && want <= 0.001f && loopSrc.volume <= 0.001f) loopSrc.Stop();
    }

    void Play(AudioClip clip, float pitch, float vol)
    {
        if (!clip || !fxSrc) return;
        fxSrc.pitch = pitch;
        fxSrc.PlayOneShot(clip, volume * vol);
    }

    // ------------------------------------------------------------------ helpers

    List<GummyBody> Bodies()
    {
        var list = new List<GummyBody>();
        foreach (var b in GummyBody.All) if (b && b.isActiveAndEnabled && !b.InRoom && b.GetComponent<GummyBrain>()) list.Add(b); // (not one inside a window)
        return list;
    }

    static BuddySpeech.Voice Voice(GummyBody b)
    {
        var sp = b ? b.GetComponent<BuddySpeech>() : null;
        return sp ? sp.voice : BuddySpeech.Voice.Apple;
    }

    static string Line(GummyBody b, string apple, string tomato, string bean, string burger, string dino, string duck, string monster)
    {
        switch (Voice(b))
        {
            case BuddySpeech.Voice.Tomato: return tomato;
            case BuddySpeech.Voice.Bean: return bean;
            case BuddySpeech.Voice.Burger: return burger;
            case BuddySpeech.Voice.Dino: return dino;
            case BuddySpeech.Voice.Duck: return duck;
            case BuddySpeech.Voice.Monster: return monster;
            default: return apple;
        }
    }

    static string NameOf(GummyBody b) => Line(b, "APPLE", "TOMATO", "BEAN", "BURGER", "DINO", "DUCKY", "MONSTER");

    readonly List<(float at, GummyBody who, string text)> later = new List<(float, GummyBody, string)>();

    void Say(GummyBody b, string text, float delay = 0f)
    {
        if (!b || string.IsNullOrEmpty(text)) return;
        var sp = b.GetComponent<BuddySpeech>();
        if (!sp) return;
        if (delay <= 0f) { sp.Say(text, 0f, true); return; }
        later.Add((Time.time + delay, b, text));
    }

    void LateUpdate() { long t0 = WhipProf.T(); LateUpdateTimed(); WhipProf.Add("P.LateUpdate", t0); }
    void LateUpdateTimed()
    {
        for (int i = later.Count - 1; i >= 0; i--)
        {
            if (Time.time < later[i].at) continue;
            var l = later[i];
            later.RemoveAt(i);
            var sp = l.who ? l.who.GetComponent<BuddySpeech>() : null;
            if (sp && l.who.isActiveAndEnabled) sp.Say(l.text, 0f, true);
        }
    }

    static int Free(Bub[] a, ref int next) { for (int k = 0; k < a.Length; k++) { int i = (next + k) % a.Length; if (!a[i].on) { next = (i + 1) % a.Length; return i; } } return -1; }
    static int Free(Spark[] a, ref int next) { for (int k = 0; k < a.Length; k++) { int i = (next + k) % a.Length; if (!a[i].on) { next = (i + 1) % a.Length; return i; } } return -1; }
    static int Free(Puf[] a, ref int next) { for (int k = 0; k < a.Length; k++) { int i = (next + k) % a.Length; if (!a[i].on) { next = (i + 1) % a.Length; return i; } } return -1; }

    static float Smooth(float u) { u = Mathf.Clamp01(u); return u * u * (3f - 2f * u); }

    // its own dice (who shouts, where bubbles pop): the buddies' dice stay as they were
    static readonly System.Random Rand = new System.Random(41);
    static float Rnd(float a, float b) => a + (float)Rand.NextDouble() * (b - a);
    static string Pick(params string[] l) => l[Rand.Next(l.Length)];

    // ------------------------------------------------------------------ pictures (baked once, in code)

    const int TileW = 64, TileHpx = 24, BubbleN = 24, EmberN = 8, PuffN = 32;
    static readonly Sprite[] s_tile = new Sprite[Frames];
    static Sprite s_bubble, s_ember, s_puff, s_glow, s_white;
    static readonly bool[] s_tried = new bool[Frames + 5];

    static Sprite TileSprite(int f) => Cached(ref s_tile[f], ref s_tried[f], () => Bake(TileW, TileHpx, new Vector2(0f, 0f), "Lava " + f, (x, y, tx) => LavaPaint(f, x, y, tx)));
    static Sprite BubbleSprite() => Cached(ref s_bubble, ref s_tried[Frames], () => Bake(BubbleN, BubbleN, new Vector2(0.5f, 0.5f), "Lava Bubble", BubblePaint));
    static Sprite EmberSprite() => Cached(ref s_ember, ref s_tried[Frames + 1], () => Bake(EmberN, EmberN, new Vector2(0.5f, 0.5f), "Lava Ember", EmberPaint));
    static Sprite PuffSprite() => Cached(ref s_puff, ref s_tried[Frames + 2], () => Bake(PuffN, PuffN, new Vector2(0.5f, 0.5f), "Lava Smoke", PuffPaint));
    static Sprite GlowSprite() => Cached(ref s_glow, ref s_tried[Frames + 3], () => Bake(4, 64, new Vector2(0f, 0f), "Lava Glow", GlowPaint, 64)); // (1/16 wide, 1 tall)
    static Sprite WhiteSprite() => Cached(ref s_white, ref s_tried[Frames + 4], () => Bake(4, 4, new Vector2(0.5f, 0.5f), "Lava Occluder", (x, y, tx) => Color.white));

    static Sprite Cached(ref Sprite s, ref bool tried, Func<Sprite> make)
    {
        if (s || tried) return s;
        s = make();
        tried = !s;
        return s;
    }

    // a w x h picture, x 0..1 across, y 0..h/w up; ppu = pixels per world unit (default: 1 unit wide)
    static Sprite Bake(int w, int h, Vector2 pivot, string name, Func<float, float, float, Color> paint, int ppu = 0)
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
        return Sprite.Create(tex, new Rect(0, 0, w, h), pivot, ppu > 0 ? ppu : w, 0, SpriteMeshType.FullRect);
    }

    static Color Clear => new Color(0f, 0f, 0f, 0f);
    const float Tau = 6.2831853f;

    /// <summary>The lava strip, frame f of 8 (x 0..1 along, y 0..0.375 up from the floor): a wavy glowing top edge (it
    /// rolls along as the frames go by, and tiles: every wave fits a whole number of times in one unit), yellow-hot near
    /// the surface, deep red below, dark crusty bits drifting in it with glowing cracks round them.</summary>
    internal static Color LavaPaint(int f, float x, float y, float texel)
    {
        float ph = f / (float)Frames;
        float s = 0.27f + 0.03f * Mathf.Sin(Tau * (2f * x + ph)) + 0.015f * Mathf.Sin(Tau * (5f * x - 2f * ph) + 1.3f);
        float a = Mathf.Clamp01((s - y) / texel + 0.5f);
        if (a <= 0f) return Clear;
        float depth = Mathf.Clamp01((s - y) / s);
        Color top = new Color(1f, 0.8f, 0.25f), mid = new Color(1f, 0.43f, 0.08f), deep = new Color(0.6f, 0.09f, 0.04f);
        Color c = depth < 0.18f ? Color.Lerp(top, mid, depth / 0.18f) : Color.Lerp(mid, deep, (depth - 0.18f) / 0.82f);
        // crust floating in it (it drifts along with the waves)
        float n = Mathf.Sin(Tau * (3f * x + ph)) * Mathf.Sin(Tau * (y * 8f) + Tau * (2f * x - ph) + 0.7f);
        if (n > 0.62f) c = Color.Lerp(c, new Color(0.36f, 0.07f, 0.03f), 0.75f);
        else if (n > 0.5f) c = Color.Lerp(c, new Color(1f, 0.85f, 0.35f), 0.6f);
        // the hot bright skin on top
        if (s - y < texel * 2.2f) c = Color.Lerp(c, new Color(1f, 0.95f, 0.6f), 0.85f);
        c.a = a;
        return c;
    }

    /// <summary>A lava bubble (x, y 0..1): an orange dome, lighter inside, a darker rim, a bright shine.</summary>
    internal static Color BubblePaint(float x, float y, float texel)
    {
        float d = Mathf.Sqrt((x - 0.5f) * (x - 0.5f) + (y - 0.5f) * (y - 0.5f)) - 0.42f;
        float a = Mathf.Clamp01(0.5f - d / texel);
        if (a <= 0f) return Clear;
        float r = Mathf.Clamp01((d + 0.42f) / 0.42f);
        Color c = Color.Lerp(new Color(1f, 0.78f, 0.3f), new Color(1f, 0.45f, 0.08f), r);
        if (d > -texel * 2.2f) c = new Color(0.72f, 0.16f, 0.05f);
        float sh = Mathf.Sqrt((x - 0.37f) * (x - 0.37f) + (y - 0.64f) * (y - 0.64f)) - 0.09f;
        if (sh < 0f) c = Color.Lerp(c, new Color(1f, 0.97f, 0.8f), 0.9f);
        c.a = a;
        return c;
    }

    /// <summary>An ember (x, y 0..1): a soft bright dot.</summary>
    internal static Color EmberPaint(float x, float y, float texel)
    {
        float r = Mathf.Sqrt((x - 0.5f) * (x - 0.5f) + (y - 0.5f) * (y - 0.5f)) / 0.5f;
        float a = Mathf.Clamp01(1f - r);
        return new Color(1f, 1f, 1f, a * a * (3f - 2f * a));
    }

    /// <summary>A puff (x, y 0..1): a soft round cloud, white (tinted grey for smoke, white for steam).</summary>
    internal static Color PuffPaint(float x, float y, float texel)
    {
        float r = Mathf.Sqrt((x - 0.5f) * (x - 0.5f) + (y - 0.5f) * (y - 0.5f)) / 0.48f;
        r += 0.06f * Mathf.Sin(Mathf.Atan2(y - 0.5f, x - 0.5f) * 5f);
        float a = Mathf.Clamp01((1f - r) * 2.2f);
        return new Color(1f, 1f, 1f, a * a * (3f - 2f * a));
    }

    /// <summary>The glow over the lava (x 0..1, y 0..16 up; stretched along the floor): warm orange, fading out upwards.</summary>
    internal static Color GlowPaint(float x, float y, float texel)
    {
        float v = Mathf.Clamp01(y / 16f);
        float a = (1f - v) * (1f - v) * 0.5f;
        return new Color(1f, 0.42f, 0.1f, a);
    }
}
