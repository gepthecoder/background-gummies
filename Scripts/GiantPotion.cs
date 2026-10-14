using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The giant potion: about one in 25 snacks you copy is a little glowing bottle instead (chat "potion", or WhipIt > Buddy >
/// Drop A Giant Potion, drops one over a buddy). Whoever gets to it first drinks it: GULP, it shivers and sparkles and
/// grows to about three times its size for 15 seconds. A giant stomps about: every landing goes BOOM, dust flies, the
/// windows next to it wobble, little ones close by bounce and snacks hop. The others want a ride: up to three come over
/// and climb onto its head, and it carries them around. Then POP: back to its size in a shower of sparkles, a bit dizzy,
/// and the riders hop off (+1 XP each). One giant at a time; a fused monster leaves potions alone. Nothing else changes
/// while it's big: it levels up and eats as usual (and shrinks back to its new size). Lives on the Interaction object.
/// </summary>
[DefaultExecutionOrder(8)] // after BuddyEater (0): it steers the giant and its riders
[DisallowMultipleComponent]
public class GiantPotion : MonoBehaviour
{
    public enum Phase { None, Drink, Grow, Big, Shrink }

    [Tooltip("About one in this many snacks you copy is a giant potion (0 = only when you ask for one).")]
    public int oneIn = 25;
    [Tooltip("How long it stays giant (seconds, from the gulp).")]
    public float giantFor = 15f;
    [Tooltip("How much bigger it gets (x its size), and the biggest a giant gets (world units wide).")]
    public float grow = 2.8f;
    public float maxSize = 1.75f;
    [Tooltip("At most this many ride on its head.")]
    public int maxRiders = 3;
    [Tooltip("Sprite material (WhipIt > Setup assigns it).")]
    public Material material;
    [Header("Sounds (WhipIt > Setup assigns them)")]
    public AudioClip gulpClip;
    public AudioClip growClip, stompClip, popClip;
    [Range(0f, 1f)] public float volume = 0.6f;

    /// <summary>The one in the scene (on the Interaction object).</summary>
    public static GiantPotion Current { get; private set; }
    /// <summary>A buddy drank one and is growing.</summary>
    public static event Action<GummyBody> WentGiant;

    public Phase State => phase;
    /// <summary>The giant (or the one drinking / shrinking back), null when there's none.</summary>
    public GummyBody Giant => phase == Phase.None ? null : giant;
    /// <summary>Counts (tests): giants, stomps, windows wobbled, rides (got up on its head), potions from copying.</summary>
    public int Giants { get; private set; }
    public int Stomps { get; private set; }
    public int Wobbles { get; private set; }
    public int Rides { get; private set; }
    public int Rolled { get; private set; }
    /// <summary>Up on its head right now.</summary>
    public bool Riding(GummyBody b) { foreach (var r in riders) if (r.body == b && r.stage == 2) return true; return false; }
    public int RiderCount { get { int n = 0; foreach (var r in riders) if (r.stage == 2) n++; return n; } }

    const float DrinkTime = 0.8f, GrowTime = 1.3f, ShrinkTime = 0.45f;

    GummyBody giant;
    GummyBrain gBrain;
    Phase phase;
    float t, baseSize, bigAt, nextLine, airVy, lastStompLine = -10f, hRatio = 1f, fitTo, nextFit;
    bool air, paused, saidHopOn;
    float walkX, walkY, walkUntil, pauseUntil;
    bool walking;
    int walkDir = 1;

    class R
    {
        public GummyBody body;
        public GummyBrain brain;
        public int stage;          // 0 on its way, 1 climbing up, 2 riding, -1 gave up
        public int side, fails;
        public bool hurried, rode;
        public float since, climbT, climbDur, cOff, nextLine;
        public Vector2 from;
    }
    readonly List<R> riders = new List<R>();

    // ------------------------------------------------------------------ life

    void Awake()
    {
        root = new GameObject("Giant Potion").transform;
        root.SetParent(transform, false);
        sfx = gameObject.AddComponent<AudioSource>();
        sfx.playOnAwake = false;
        sfx.spatialBlend = 0f;
    }

    void OnEnable()
    {
        Current = this;
        BuddyEater.AnyAte += OnAte;
    }

    void OnDisable()
    {
        BuddyEater.AnyAte -= OnAte;
        Abort();
        if (Current == this) Current = null;
    }

    void OnDestroy()
    {
        if (root) Destroy(root.gameObject);
    }

    // ------------------------------------------------------------------ for everyone else

    /// <summary>This one's a giant right now (drinking, big, or shrinking back).</summary>
    public static bool IsGiant(GummyBody b) => b && Current && Current.phase != Phase.None && Current.giant == b;

    /// <summary>BuddyProgress grew it (it levelled up): a giant stays giant (and shrinks back to this new size later).</summary>
    public static float Resize(GummyBody b, float size)
    {
        if (!IsGiant(b) || Current.phase == Phase.Shrink) return size;
        Current.baseSize = size;
        if (Current.phase == Phase.Drink) return size;
        return Current.fitTo = Current.Fit(size);
    }

    /// <summary>Can it eat this snack? (A potion: not a monster, and one giant at a time.)</summary>
    public static bool CanEat(GummyBody b, Snack s)
    {
        if (!s || s.kind != Snack.Kind.Potion) return true;
        if (!b || b.IsMonster) return false;
        return !Current || Current.phase == Phase.None;
    }

    /// <summary>The snack you just copied: a giant potion this time? (About one in oneIn; its own dice. Not while there's
    /// a giant or a potion lying around.)</summary>
    public static bool Roll()
    {
        var c = Current;
        if (!c || !c.isActiveAndEnabled || c.oneIn <= 0 || c.phase != Phase.None) return false;
        foreach (var s in Snack.All) if (s && s.kind == Snack.Kind.Potion) return false;
        if (Rand.Next(c.oneIn) != 0) return false;
        c.Rolled++;
        return true;
    }

    /// <summary>Why it can't drop one now (null: it can).</summary>
    public string Blocked()
    {
        if (phase != Phase.None) return "giant";
        foreach (var s in Snack.All) if (s && s.kind == Snack.Kind.Potion) return "one there";
        return null;
    }

    /// <summary>Drop a potion over this buddy (null: over your cursor). Returns it, or null.</summary>
    public Snack DropFor(GummyBody b)
    {
        if (Blocked() != null) return null;
        var d = FindFirstObjectByType<SnackDropper>();
        if (!d) return null;
        return b ? d.Drop(true, Snack.Kind.Potion, b.Center.x + (Rand.NextDouble() < 0.5 ? -0.5f : 0.5f) * b.size) : d.Drop(true, Snack.Kind.Potion);
    }

    /// <summary>Pop it now (chat "shrink"): back to its size.</summary>
    public void PopNow()
    {
        if (phase == Phase.Drink || phase == Phase.Grow || phase == Phase.Big) Pop();
    }

    float BigSize(float s) => Mathf.Max(s, Mathf.Min(s * grow, Mathf.Max(maxSize, s)));

    // as big as there's room for over its head (under a low window: only that big; it grows on once it's out)
    float Fit(float s) => Mathf.Clamp(Room() * 0.95f / hRatio, s, BigSize(s));

    float Room()
    {
        Rect e = giant.Extent;
        float feet = e.yMin, half = Mathf.Max(e.width, BigSize(baseSize) * 0.9f) * 0.5f, x0 = giant.Center.x - half, x1 = giant.Center.x + half;
        float room = ScreenBounds.CeilingAt(new Vector2(giant.Center.x, feet + 0.01f)) - feet;
        var wins = DesktopPlatforms.Windows;
        for (int i = 0; i < wins.Count; i++)
        {
            WindowBox w = wins[i];
            if (!w.movable || (!giant.solidWindows && !w.screen) || w.xMax < x0 || w.xMin > x1 || w.yMin < feet + 0.05f) continue;
            room = Mathf.Min(room, w.yMin - feet);
        }
        return room;
    }

    // room over its head for a rider
    bool HeadRoom(GummyBody rider) => Room() > giant.Extent.height + rider.Extent.height + 0.15f;

    // ------------------------------------------------------------------ the gulp

    void OnAte(BuddyEater e, Snack s)
    {
        if (!e || !s || s.kind != Snack.Kind.Potion || phase != Phase.None) return;
        var b = e.GetComponent<GummyBody>();
        if (!b || b.IsMonster) return;
        giant = b;
        gBrain = b.GetComponent<GummyBrain>();
        baseSize = b.TargetSize;
        hRatio = Mathf.Clamp(b.Extent.height / Mathf.Max(b.size, 0.05f), 0.6f, 1.4f); // (how tall it is for its width)
        phase = Phase.Drink;
        t = 0f;
        Giants++;
        riders.Clear();
        air = walking = saidHopOn = false;
        airVy = 0f;
        if (gBrain) { gBrain.WakeUp(); gBrain.Food = null; gBrain.StayPut = true; }
        Play(gulpClip, 0.9f, 0.9f);
        Say(b, "GULP!");
        Say(b, Line(b, "I FEEL... FUNNY", "WHAT WAS IN THAT?", "TOO MUCH SUGAR?", "MY BUN FEELS WEIRD", "RAWR...?", "QUACK...?", "WE FEEL... ODD"), 0.55f);
        for (int i = 0; i < 10; i++) Sparkle(b.Center + Rnd2(0.5f) * b.size, Rnd2(1f), Rnd(0.06f, 0.11f), Pink(), Rnd(0.5f, 0.9f));
        WentGiant?.Invoke(b);
    }

    // ------------------------------------------------------------------ each frame

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("R.Update", t0); }
    void UpdateTimed()
    {
        float dt = Mathf.Min(Time.deltaTime, 0.05f), now = Time.time;
        if (phase != Phase.None)
        {
            if (!giant || !giant.isActiveAndEnabled) Abort();
            else
            {
                t += dt;
                switch (phase)
                {
                    case Phase.Drink:
                        {
                            // a shiver, sparkles swirling in
                            float w = Mathf.Sin(t * 42f) * 0.07f * Mathf.Clamp01(t / 0.2f);
                            giant.Pose = new Vector2(1f + w, 1f / (1f + w));
                            if (gBrain) { gBrain.StayPut = true; gBrain.Food = null; gBrain.KeepAwake(); }
                            if (Rand.NextDouble() < dt * 25f) { float a = Rnd(0f, 6.283f); Vector2 o = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * giant.size * 1.1f; Sparkle(giant.Center + o, -o * 1.6f, Rnd(0.05f, 0.09f), Pink(), 0.45f); }
                            if (t >= DrinkTime) Grow();
                            break;
                        }
                    case Phase.Grow:
                        if (gBrain) { gBrain.StayPut = true; gBrain.Food = null; gBrain.KeepAwake(); }
                        if (Rand.NextDouble() < dt * 30f) Sparkle(giant.Center + Rnd2(0.6f) * giant.size, Rnd2(1.5f) + new Vector2(0f, 0.6f), Rnd(0.06f, 0.12f), Pink(), Rnd(0.5f, 0.8f));
                        if (t >= DrinkTime + GrowTime) { phase = Phase.Big; bigAt = now; nextLine = now + Rnd(3f, 5f); if (gBrain) gBrain.StayPut = false; Invite(now); }
                        break;
                    case Phase.Big:
                        Big(dt, now);
                        if (t >= giantFor) Pop();
                        break;
                    case Phase.Shrink:
                        if (t >= ShrinkTime + 0.3f) { phase = Phase.None; giant = null; gBrain = null; riders.Clear(); }
                        break;
                }
            }
        }
        StepSparkles(dt);
        StepPuffs(dt);
    }

    void Grow()
    {
        phase = Phase.Grow;
        giant.Pose = Vector2.one;
        fitTo = Fit(baseSize);
        nextFit = Time.time + GrowTime;
        giant.Morph(giant.shape, fitTo, GrowTime);
        Play(growClip, 1f, 1f);
        Say(giant, Line(giant, "I'M HUUUGE!", "BEEFSTEAK TOMATO!", "VENTI BEAN!", "DOUBLE DOUBLE!", "RAWR! KAIJU MODE!", "MEGA QUACK!", "WE ARE... MORE"));
        for (int i = 0; i < 16; i++) Sparkle(giant.Center + Rnd2(0.4f) * giant.size, Rnd2(2.5f), Rnd(0.07f, 0.13f), Pink(), Rnd(0.6f, 1f));
        // everyone nearby looks
        int k = 0;
        foreach (var b in GummyBody.All)
        {
            if (!b || b == giant || !b.isActiveAndEnabled || b.IsMonster || b.InRoom || k >= 2 || !ScreenBounds.SameScreen(b.Center, giant.Center)) continue;
            if (Rand.NextDouble() < 0.4) continue;
            k++;
            Say(b, Line(b, "WHOA!", "WHAT THE...", "THAT'S NO DECAF", "IT'S ENORMOUS!", "RAWR?! BIGGER THAN ME!", "QUAAACK!", "IMPRESSIVE."), 0.6f + 0.5f * k);
        }
    }

    // ------------------------------------------------------------------ big

    void Big(float dt, float now)
    {
        if (gBrain) gBrain.KeepAwake();
        bool busy = Busy();
        if (busy) { if (riders.Count > 0) ReleaseRiders(false); StopWalking(); }

        // as big as there's room for (walking out from under a window it grows on, under a low one it shrinks to fit)
        if (now >= nextFit && giant.Grounded && !giant.Held)
        {
            nextFit = now + 0.25f;
            float want = Fit(baseSize);
            if (Mathf.Abs(want - fitTo) > 0.08f) { fitTo = want; giant.Morph(giant.shape, want, 0.6f); }
        }

        // stomps: every landing from a hop (or a throw)
        if (!giant.Grounded) { air = true; airVy = Mathf.Min(airVy, giant.Velocity.y); }
        else if (air) { air = false; if (airVy < -2.2f && !giant.Held) Stomp(airVy, now); airVy = 0f; }

        // a sparkle now and then: still magic
        if (Rand.NextDouble() < dt * 4f) Sparkle(new Vector2(giant.Center.x + Rnd(-0.45f, 0.45f) * giant.Extent.width, giant.Center.y + Rnd(-0.2f, 0.45f) * giant.Extent.height), new Vector2(Rnd(-0.2f, 0.2f), Rnd(0.3f, 0.7f)), Rnd(0.05f, 0.09f), Pink(), Rnd(0.5f, 0.9f));

        if (busy) return;
        Riders(dt, now);
        Walk(now);
        if (now >= nextLine)
        {
            nextLine = now + Rnd(4.5f, 7.5f);
            if (Rand.NextDouble() < 0.55) Say(giant, Line(giant, Pick("FEE FI FO YUM!", "LOOK HOW TALL I AM!", "TINY WINDOWS!"), Pick("I'M A PUMPKIN NOW", "BIG RED!"), Pick("EXTRA LARGE!", "GRANDE IS AN UNDERSTATEMENT"), Pick("TRIPLE STACK!", "FAMILY SIZE!"), Pick("STOMP STOMP RAWR!", "FEAR THE DINO!"), Pick("QUAAACK!", "BIG BIRD... NO. BIG DUCK"), "WE ARE TALL."));
        }
    }

    // a stroll: a few hops one way, a look round, a few hops back (it waits while its riders climb on)
    void Walk(float now)
    {
        if (!gBrain || giant.Held) { StopWalking(); return; }
        bool waiting = false;
        foreach (var r in riders) if ((r.stage == 0 && r.body && HeadRoom(r.body)) || r.stage == 1) waiting = true; // (no room over its head: nobody to wait for)
        if (waiting && now - bigAt < 7f)
        {
            gBrain.StayPut = true;
            gBrain.Food = null;
            walking = false;
            if (!saidHopOn && now - bigAt > 0.6f) { saidHopOn = true; Say(giant, Line(giant, "HOP ON!", "ALL ABOARD!", "FREE RIDES!", "ALL ABOARD THE BUN!", "RAWR! CLIMB ON!", "QUACK! HOP ON!", "WE GIVE RIDES.")); }
            return;
        }
        if (!walking)
        {
            if (now < pauseUntil) { gBrain.StayPut = true; gBrain.Food = null; return; }
            if (!giant.Grounded) return;
            // somewhere a few steps along the bit of floor it's on
            Vector2 span = giant.SupportSpan;
            BuddyRoute.Build(Mathf.Max(giant.Extent.height, giant.size), giant.solidWindows);
            int si = BuddyRoute.SurfaceAt(giant.Center.x, giant.Extent.yMin, false);
            if (si >= 0) { var sf = BuddyRoute.Surfaces[si]; span = new Vector2(Mathf.Max(span.x, sf.x0), Mathf.Min(span.y, sf.x1)); }
            float hw = giant.Extent.width * 0.5f, lo = span.x + hw * 1.1f, hi = span.y - hw * 1.1f;
            if (hi - lo < 1f) { pauseUntil = now + 2f; return; } // (no room for a walk: it stands and looks big)
            float x = giant.Center.x + walkDir * Rnd(2f, 3.5f);
            if (x < lo || x > hi) { walkDir = -walkDir; x = giant.Center.x + walkDir * Rnd(2f, 3.5f); }
            walkX = Mathf.Clamp(x, lo, hi);
            walkY = giant.Extent.yMin;
            walkUntil = now + 6f;
            walking = true;
        }
        if (Mathf.Abs(giant.Center.x - walkX) < 0.45f || now > walkUntil)
        {
            walking = false;
            walkDir = -walkDir;
            pauseUntil = now + Rnd(0.8f, 1.6f);
            gBrain.Food = null;
            gBrain.StayPut = true;
            return;
        }
        gBrain.StayPut = false;
        gBrain.Food = new Vector2(walkX, walkY + giant.size * 0.3f);
        gBrain.FoodSurface = walkY;
    }

    void StopWalking()
    {
        if (!gBrain) return;
        if (walking || gBrain.StayPut) { gBrain.Food = null; gBrain.StayPut = false; }
        walking = false;
    }

    void Stomp(float vy, float now)
    {
        Stomps++;
        float k = Mathf.Clamp01(-vy / 8f), w = giant.Extent.width;
        Vector2 feet = new Vector2(giant.Center.x, giant.Extent.yMin);
        Play(stompClip, Rnd(0.6f, 0.7f), 0.55f + 0.45f * k);
        for (int i = 0; i < 10; i++)
        {
            float side = i % 2 == 0 ? -1f : 1f;
            Puff(feet + new Vector2(side * Rnd(0.25f, 0.5f) * w, 0.04f), new Vector2(side * Rnd(1f, 2.6f), Rnd(0.1f, 0.7f)), Rnd(0.14f, 0.26f) * (0.7f + 0.5f * k), new Color(0.82f, 0.78f, 0.72f), Rnd(0.45f, 0.75f));
        }
        // the windows: the one it's standing on gives, the ones standing next to it (on the same ground) shake
        var wob = giant.GetComponent<WindowWobble>();
        if (wob)
        {
            long under = giant.SupportWindow;
            int n = 0;
            if (under != 0 && !DesktopPlatforms.IsScreen(under) && !DesktopPlatforms.IsIcon(under)) { wob.Stomp(under, feet, 3f + 3f * k); Wobbles++; n++; }
            var wins = DesktopPlatforms.Windows;
            for (int i = 0; i < wins.Count && n < 4; i++)
            {
                WindowBox wb = wins[i];
                if (wb.screen || !wb.movable || wb.id == under || DesktopPlatforms.IsIcon(wb.id)) continue;
                float gap = Mathf.Max(0f, Mathf.Max(wb.xMin - feet.x, feet.x - wb.xMax) - w * 0.5f);
                if (gap > 2.5f || Mathf.Abs(wb.yMin - feet.y) > 0.6f) continue;
                wob.Stomp(wb.id, new Vector2(Mathf.Clamp(feet.x, wb.xMin, wb.xMax), wb.yMax), (1.5f + 2f * k) * (1f - gap / 3f));
                Wobbles++;
                n++;
            }
        }
        // little ones close by bounce, snacks hop
        int said = 0;
        foreach (var b in GummyBody.All)
        {
            if (!b || b == giant || !b.isActiveAndEnabled || b.Held || b.InRoom || !b.Grounded || IsRider(b)) continue;
            float d = Mathf.Abs(b.Center.x - feet.x) - w * 0.5f;
            if (d > 2.5f || Mathf.Abs(b.Extent.yMin - feet.y) > 0.3f) continue;
            float s = Mathf.Sqrt(b.size / 0.6f), f = 1f - Mathf.Clamp01(d / 2.5f);
            b.Hop(new Vector2(Mathf.Sign(b.Center.x - feet.x) * 0.6f * s * f, GummyBody.JumpSpeed((0.12f + 0.3f * f * k) * s, b.gravity, b.Drag)), 0.3f);
            if (said == 0 && Rand.NextDouble() < 0.35) { said++; Say(b, Line(b, "WHOA!", "EARTHQUAKE!", "SPILLED MY COFFEE!", "MY PICKLES!", "RAWR! SHAKY!", "QUACK!", "HM.")); }
        }
        foreach (var sn in Snack.All)
        {
            if (!sn || sn.Held || sn.Eaten || !sn.Resting) continue;
            float d = Mathf.Abs(sn.Position.x - feet.x) - w * 0.5f;
            if (d > 2.5f || Mathf.Abs(sn.SurfaceY - feet.y) > 0.3f) continue;
            float f = 1f - Mathf.Clamp01(d / 2.5f);
            sn.Knock(new Vector2(Rnd(-0.6f, 0.6f), 1.5f + 2.5f * f * k));
        }
        if (now - lastStompLine > 6f && Rand.NextDouble() < 0.3) { lastStompLine = now; Say(giant, Line(giant, "BOOM!", "STOMP!", "THUD!", "BOOM BOOM!", "RAWR! STOMP!", "QUACK. BOOM.", "BOOM.")); }
    }

    // ------------------------------------------------------------------ riders

    void Invite(float now)
    {
        if (Busy()) return;
        var cands = new List<(float d, GummyBody b)>();
        foreach (var b in GummyBody.All)
        {
            if (!b || b == giant || !b.isActiveAndEnabled || b.Held || b.IsMonster || b.InRoom || !ScreenBounds.SameScreen(b.Center, giant.Center)) continue;
            var br = b.GetComponent<GummyBrain>();
            if (!br || br.Towering || br.Sliding) continue;
            var fu = b.GetComponent<BuddyFusion>();
            if (fu && fu.Current != BuddyFusion.Mode.None) continue;
            float d = (b.Center - giant.Center).magnitude;
            if (d < 12f) cands.Add((d, b));
        }
        cands.Sort((a, c) => a.d.CompareTo(c.d));
        if (cands.Count > maxRiders) cands.RemoveRange(maxRiders, cands.Count - maxRiders);
        cands.Sort((a, c) => a.b.Center.x.CompareTo(c.b.Center.x)); // (seats left to right: nobody climbs across)
        for (int i = 0; i < cands.Count && riders.Count < maxRiders; i++)
        {
            var b = cands[i].b;
            var br = b.GetComponent<GummyBrain>();
            if (br.Current == GummyBrain.State.Asleep) br.WakeUp();
            int side = b.Center.x < giant.Center.x ? -1 : 1;
            riders.Add(new R { body = b, brain = br, stage = 0, side = side, since = now, nextLine = now + Rnd(4f, 7f) });
            Say(b, Line(b, "CAN I RIDE?!", "ME FIRST!", "PIGGYBACK!", "GIDDY UP TIME!", "RAWR! RIDE!", "QUACK! UP UP!", "WE RIDE?"), 1.2f + 0.6f * i);
        }
    }

    bool IsRider(GummyBody b) { foreach (var r in riders) if (r.body == b && (r.stage == 1 || r.stage == 2)) return true; return false; }

    void Riders(float dt, float now)
    {
        for (int i = 0; i < riders.Count; i++)
        {
            var r = riders[i];
            var b = r.body;
            if (!b || !b.isActiveAndEnabled || r.stage < 0) continue;
            var br = r.brain;
            br.KeepAwake();
            if (b.Held)
            {
                // you took it: off the giant (you can put it back up there yourself)
                if (r.stage == 1 || r.stage == 2) { br.TowerEnd(); r.stage = 0; }
                r.since = now;
                continue;
            }
            float S = Mathf.Sqrt(b.size / 0.6f);
            switch (r.stage)
            {
                case 0:
                    {
                        if (!r.hurried) { r.hurried = true; br.Hurry = 1.6f; }
                        float gw = giant.Extent.width * 0.5f, rw = Mathf.Max(b.Extent.width, b.size * 0.8f) * 0.5f, gy = giant.Extent.yMin;
                        float spotX = giant.Center.x + r.side * (gw + rw + 0.1f);
                        bool level = Mathf.Abs(b.Extent.yMin - gy) < 0.3f;
                        bool close = Mathf.Abs(b.Center.x - giant.Center.x) < gw + rw + 0.9f;
                        bool turn = true; // (one climbs up at a time)
                        foreach (var o in riders) if (o != r && o.stage == 1) turn = false;
                        if (turn && b.Grounded && level && close && giant.Grounded && HeadRoom(b) && giant.Velocity.sqrMagnitude < 4f && !b.IsStuck && br.TowerStart())
                        {
                            r.stage = 1;
                            r.from = b.Center;
                            r.cOff = b.Center.y - b.Extent.yMin;
                            r.climbT = 0f;
                            r.climbDur = Mathf.Clamp(0.45f + 0.15f * (PerchAt(r) - r.from).magnitude, 0.6f, 1.2f);
                            br.Food = null;
                            Unhurry(r);
                            if (Rand.NextDouble() < 0.6) Say(b, Line(b, "HUP!", "UP I GO!", "HUP HUP", "CLIMB TIME!", "RAWR! UP!", "QUACK! HUP!", "UP."));
                            break;
                        }
                        br.Food = new Vector2(spotX, gy + b.size * 0.3f);
                        br.FoodSurface = gy;
                        if (now - r.since > 10f)
                        {
                            r.stage = -1;
                            br.Food = null;
                            Unhurry(r);
                            Say(b, Line(b, "TOO TALL...", "I CAN'T GET UP THERE", "NEXT TIME", "NO RIDE FOR ME", "RAWR... TOO BIG", "QUACK...", "WE WATCH."));
                        }
                        break;
                    }
                case 1:
                    {
                        if (!br.Towering) { r.stage = 0; r.since = now; break; }
                        r.climbT += dt;
                        float u = Smooth(r.climbT / r.climbDur);
                        Vector2 T = PerchAt(r);
                        Vector2 C = new Vector2(Mathf.Lerp(r.from.x, T.x, 0.3f), Mathf.Max(r.from.y, T.y) + 0.55f * b.size);
                        Vector2 D = (1f - u) * (1f - u) * r.from + 2f * u * (1f - u) * C + u * u * T;
                        Fly(b, D, 9f * S + giant.Velocity.magnitude, 16f, 70f);
                        b.Pose = new Vector2(0.94f, 1.06f);
                        if (u >= 1f && (b.Center - T).magnitude < 0.1f * b.size + 0.06f)
                        {
                            r.stage = 2;
                            r.rode = true;
                            Rides++;
                            b.Pose = Vector2.one;
                            r.nextLine = now + Rnd(3f, 6f);
                            Say(b, Line(b, "YEEHAW!", "GIDDY UP!", "SO HIGH!", "BEST SEAT IN THE HOUSE", "RAWR! FASTER!", "QUACK! TALL!", "WE SEE ALL."));
                        }
                        else if (r.climbT > r.climbDur + 1.5f) { Off(r, 0.8f); r.stage = ++r.fails >= 2 ? -1 : 0; r.since = now; } // (slipped off: one more try)
                        break;
                    }
                case 2:
                    {
                        if (!br.Towering) { r.stage = 0; r.since = now; break; }
                        if (giant.Grounded && !HeadRoom(b)) { Off(r, 1f); r.stage = 0; r.since = now; Say(b, Pick("DUCK!", "LOW WINDOW!")); break; } // (under a window: off it gets)
                        Vector2 T = PerchAt(r);
                        Fly(b, T, 6f * S + giant.Velocity.magnitude * 1.2f, 14f, 60f);
                        b.Lean = Mathf.Clamp(-giant.Velocity.x * 2.5f, -12f, 12f);
                        if (now >= r.nextLine)
                        {
                            r.nextLine = now + Rnd(5f, 9f);
                            if (Rand.NextDouble() < 0.5) Say(b, Line(b, Pick("I CAN SEE EVERYTHING!", "WHEEE!"), Pick("TALLEST TOMATO EVER", "LOOK AT ME!"), Pick("ESPRESSO ON TOP!", "VIEW'S GREAT!"), Pick("TOP BUN, LITERALLY", "BURGER WITH A VIEW"), Pick("RAWR! GO GO GO!", "DINO RODEO!"), Pick("QUACK! WEEE!", "DUCK EXPRESS!"), "WE RIDE."));
                        }
                        break;
                    }
            }
        }
    }

    // where rider r sits: side by side across the giant's head (round: the ones at the sides a bit lower), a third one
    // on top of the first if there's no room
    Vector2 PerchAt(R r)
    {
        int n = 0, me = 0;
        float total = 0f;
        foreach (var o in riders)
        {
            if (o.stage < 0 || !o.body) continue;
            if (o == r) me = n;
            total += Width(o.body) + 0.04f;
            n++;
        }
        float gW = giant.Extent.width, R0 = gW * 0.5f;
        if (n >= 3 && total > gW * 0.92f && me == 2)
        {
            // stacked on the one next to it
            R below = null;
            foreach (var o in riders) { if (o == r) break; if (o.stage >= 0 && o.body) below = o; }
            if (below != null) return new Vector2(below.body.Center.x, below.body.Extent.yMax + r.cOff + 0.02f);
        }
        int across = n >= 3 && total > gW * 0.92f ? 2 : n;
        float span = 0f;
        int k = 0;
        float x0 = 0f;
        foreach (var o in riders)
        {
            if (o.stage < 0 || !o.body) continue;
            if (k >= across) break;
            if (o == r) x0 = span + Width(o.body) * 0.5f;
            span += Width(o.body) + 0.04f;
            k++;
        }
        float dx = x0 - span * 0.5f;
        float drop = (R0 - Mathf.Sqrt(Mathf.Max(R0 * R0 - dx * dx, 0f))) * 0.85f;
        return new Vector2(giant.Center.x + dx, giant.Extent.yMax - drop + r.cOff + 0.02f);
    }

    static float Width(GummyBody b) => Mathf.Max(b.Extent.width, b.size * 0.8f);

    static void Fly(GummyBody b, Vector2 at, float maxV, float k, float accel)
    {
        Vector2 v = (at - b.Center) * k;
        if (v.magnitude > maxV) v = v.normalized * maxV;
        b.Glide(v, accel * Mathf.Sqrt(b.size / 0.6f));
    }

    void Off(R r, float k)
    {
        var b = r.body;
        if (!b) return;
        bool up = r.brain && r.brain.Towering;
        if (r.brain) { r.brain.TowerEnd(); if (r.stage == 0) r.brain.Food = null; }
        Unhurry(r);
        if (!up || !b.isActiveAndEnabled) return;
        float s = Mathf.Sqrt(b.size / 0.6f);
        int dir = b.Center.x < (giant ? giant.Center.x : b.Center.x + 1f) ? -1 : 1;
        // right off its head (one left up there gets flung to the ceiling when the giant jumps)
        float clear = giant ? giant.Extent.width * 0.5f + Width(b) * 0.5f + 0.15f - Mathf.Abs(b.Center.x - giant.Center.x) : 0f;
        float vx = Mathf.Max(Rnd(1.4f, 2.2f) * s * k, clear / 0.45f);
        b.Hop(new Vector2(dir * vx - b.Velocity.x, Rnd(2.4f, 3.2f) * s - b.Velocity.y), 0.4f);
    }

    void ReleaseRiders(bool pop)
    {
        foreach (var r in riders)
        {
            if (!r.body || r.stage < 0) continue;
            bool up = r.stage == 1 || r.stage == 2;
            Off(r, pop ? 1f : 0.7f);
            if (pop && r.rode)
            {
                var prog = r.body.GetComponent<BuddyProgress>();
                if (prog) prog.AddXp(1, r.body.Center);
                Say(r.body, Line(r.body, "WHEEE!", "AGAIN! AGAIN!", "WHAT A RIDE!", "BEST. RIDE. EVER.", "RAWR! AGAIN!", "QUACK! AGAIN!", "WE ENJOYED IT."), 0.15f);
            }
            else if (up) Say(r.body, Pick("WHOA!", "HEY!"));
        }
        riders.Clear();
    }

    void Unhurry(R r)
    {
        if (r.hurried && r.brain) r.brain.Hurry = 1f;
        r.hurried = false;
    }

    // ------------------------------------------------------------------ pop

    void Pop()
    {
        if (!giant) { phase = Phase.None; return; }
        phase = Phase.Shrink;
        t = 0f;
        ReleaseRiders(true);
        StopWalking();
        giant.Pose = Vector2.one;
        giant.Morph(giant.shape, baseSize, ShrinkTime);
        Play(popClip, 1f, 0.9f);
        for (int i = 0; i < 26; i++) Sparkle(giant.Center + Rnd2(0.5f) * giant.Extent.width, Rnd2(3.2f) + new Vector2(0f, 0.8f), Rnd(0.07f, 0.13f), Pink(), Rnd(0.6f, 1.1f));
        for (int i = 0; i < 8; i++) Puff(giant.Center + Rnd2(0.4f) * giant.Extent.width, Rnd2(1.2f), Rnd(0.25f, 0.45f), new Color(1f, 0.75f, 0.98f), Rnd(0.5f, 0.8f));
        if (gBrain) { gBrain.StayPut = false; gBrain.Food = null; gBrain.Hurry = 1f; gBrain.MakeDizzy(1.2f); }
        Say(giant, Line(giant, Pick("AWW... TINY AGAIN", "THAT WAS AWESOME!"), Pick("WHERE DID I GO?", "CHERRY TOMATO AGAIN"), Pick("BACK TO ESPRESSO SIZE", "DECAF FEELING"), Pick("SLIDER SIZE AGAIN", "AWW, KIDS MENU"), Pick("RAWR... SMOL", "BACK TO MINI DINO"), Pick("QUACK... SMALL", "DUCKLING SIZE"), "WE SHRANK."), 0.5f);
    }

    // something went: the giant's gone (or this is switched off): everyone let go, it back to its size if it's still there
    void Abort()
    {
        if (phase == Phase.None) return;
        ReleaseRiders(false);
        if (giant && giant.isActiveAndEnabled && phase != Phase.Shrink) { giant.Pose = Vector2.one; giant.Morph(giant.shape, baseSize, 0.3f); }
        if (gBrain) { gBrain.StayPut = false; gBrain.Food = null; }
        phase = Phase.None;
        giant = null;
        gBrain = null;
    }

    static bool Busy() => Bedtime.Sleeping || (Lava.Current && Lava.Current.On) || (Rain.Current && Rain.Current.Raining)
        || LevelUpParty.Current || BandJam.Current || PickupGame.Current || BuddyTower.Current || BuddyHideSeek.Playing;

    // ------------------------------------------------------------------ sparkles and puffs (pooled)

    const int MaxSparkles = 64, MaxPuffs = 32, SparkleOrder = 14, PuffOrder = 9;
    struct P { public Vector2 pos, vel; public float t, life, size; public Color color; public bool on; }
    readonly P[] sparkles = new P[MaxSparkles], puffs = new P[MaxPuffs];
    readonly SpriteRenderer[] sparkleSr = new SpriteRenderer[MaxSparkles], puffSr = new SpriteRenderer[MaxPuffs];
    int nextSparkle, nextPuff;
    Transform root;
    AudioSource sfx;

    static Color Pink()
    {
        double r = Rand.NextDouble();
        return r < 0.4 ? new Color(1f, 0.6f, 1f) : r < 0.75 ? new Color(1f, 0.95f, 1f) : new Color(1f, 0.88f, 0.45f);
    }

    void Sparkle(Vector2 at, Vector2 vel, float size, Color c, float life)
    {
        int i = Free(sparkles, ref nextSparkle);
        if (i < 0) return;
        sparkles[i] = new P { pos = at, vel = vel, size = size, color = c, life = life, on = true };
        if (!sparkleSr[i]) sparkleSr[i] = MakeSprite("Potion Sparkle", Snack.StarSprite(), SparkleOrder);
    }

    void Puff(Vector2 at, Vector2 vel, float size, Color c, float life)
    {
        int i = Free(puffs, ref nextPuff);
        if (i < 0) return;
        puffs[i] = new P { pos = at, vel = vel, size = size, color = c, life = life, on = true };
        if (!puffSr[i]) puffSr[i] = MakeSprite("Giant Dust", Snack.GlowSprite(), PuffOrder);
    }

    void StepSparkles(float dt)
    {
        for (int i = 0; i < MaxSparkles; i++)
        {
            ref P p = ref sparkles[i];
            var sr = sparkleSr[i];
            if (!p.on) { if (sr && sr.enabled) sr.enabled = false; continue; }
            p.t += dt;
            if (p.t >= p.life) { p.on = false; sr.enabled = false; continue; }
            float u = p.t / p.life;
            p.vel *= 1f - 2.2f * dt;
            p.pos += p.vel * dt;
            sr.enabled = true;
            float s = p.size * 2f * Mathf.Sin(Mathf.PI * Mathf.Min(1f, u * 1.2f)) * (0.8f + 0.2f * Mathf.Sin(p.t * 30f + i));
            sr.transform.position = new Vector3(p.pos.x, p.pos.y, 0f);
            sr.transform.localScale = new Vector3(s, s, 1f);
            Color c = p.color;
            c.a = 1f - u * u;
            sr.color = c;
        }
    }

    void StepPuffs(float dt)
    {
        for (int i = 0; i < MaxPuffs; i++)
        {
            ref P p = ref puffs[i];
            var sr = puffSr[i];
            if (!p.on) { if (sr && sr.enabled) sr.enabled = false; continue; }
            p.t += dt;
            if (p.t >= p.life) { p.on = false; sr.enabled = false; continue; }
            float u = p.t / p.life;
            p.vel *= 1f - 3f * dt;
            p.pos += p.vel * dt;
            sr.enabled = true;
            float s = p.size * (1f + 1.4f * u);
            sr.transform.position = new Vector3(p.pos.x, p.pos.y, 0f);
            sr.transform.localScale = new Vector3(s, s, 1f);
            Color c = p.color;
            c.a = 0.8f * Mathf.Min(1f, u * 8f) * (1f - u);
            sr.color = c;
        }
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
        sr.enabled = false;
        return sr;
    }

    static int Free(P[] a, ref int next) { for (int k = 0; k < a.Length; k++) { int i = (next + k) % a.Length; if (!a[i].on) { next = (i + 1) % a.Length; return i; } } return -1; }

    // ------------------------------------------------------------------ sound and words

    void Play(AudioClip clip, float pitch, float vol)
    {
        if (!clip || !sfx) return;
        sfx.pitch = pitch;
        sfx.PlayOneShot(clip, volume * vol);
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

    readonly List<(float at, GummyBody who, string text)> later = new List<(float, GummyBody, string)>();

    void Say(GummyBody b, string text, float delay = 0f)
    {
        if (!b || string.IsNullOrEmpty(text)) return;
        var sp = b.GetComponent<BuddySpeech>();
        if (!sp) return;
        if (delay <= 0f) { sp.Say(text, 0f, true); return; }
        later.Add((Time.time + delay, b, text));
    }

    void LateUpdate() { long t0 = WhipProf.T(); LateUpdateTimed(); WhipProf.Add("R.LateUpdate", t0); }
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

    static float Smooth(float u) { u = Mathf.Clamp01(u); return u * u * (3f - 2f * u); }

    // its own dice (the 1-in-25, sparkles, who says what): the buddies' dice stay as they were
    static readonly System.Random Rand = new System.Random(57);
    static float Rnd(float a, float b) => a + (float)Rand.NextDouble() * (b - a);
    static Vector2 Rnd2(float r) => new Vector2(Rnd(-r, r), Rnd(-r, r));
    static string Pick(params string[] l) => l[Rand.Next(l.Length)];
}
