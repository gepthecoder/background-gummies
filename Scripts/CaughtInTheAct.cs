using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Caught in the act: when you've been away a couple of minutes (no mouse or keyboard: only the idle time is read), the
/// buddies run wild. One shouts "THEY'RE GONE!", a cheeky tune starts and they party: a dance party, a conga line along
/// the floor, a pillow fight (feathers everywhere), one bouncing on the Recycle Bin like a trampoline. Rounds of it, with a
/// breather in between. The moment you touch the mouse: a record scratch and they all freeze, mid-move, wherever they
/// are (mid-air too). A beat later they drop and act innocent: whistling, eyes darting anywhere but at you, one tiptoeing
/// back to where it was when you left. At most once every 20 to 40 minutes. Chat "go wild" (or WhipIt > Buddy > Go Wild
/// Now) to see it sooner. Lives on the Interaction object (WhipIt > Setup).
/// </summary>
[DefaultExecutionOrder(8)] // after BuddyEater (0) and the brains: it steers them and sets their poses
[DisallowMultipleComponent]
public class CaughtInTheAct : MonoBehaviour
{
    public enum Phase { Waiting, Wild, Frozen, Innocent }
    public enum Role { Dance, Conga, Pillow, Trampoline }

    [Tooltip("You've been away this long (seconds without mouse or keyboard): party.")]
    public float awayAfter = 120f;
    [Tooltip("At most one party this often (minutes, x..y).")]
    public Vector2 cooldown = new Vector2(20f, 40f);
    [Tooltip("Still nobody back after this long (seconds): they get tired and stop.")]
    public float maxWild = 900f;
    [Tooltip("Frozen this long when you catch them (seconds), then acting innocent this long.")]
    public float freezeFor = 1.1f;
    public float innocentFor = 5f;
    [Tooltip("Sprite material (WhipIt > Setup assigns it).")]
    public Material material;
    [Header("Sounds (WhipIt > Setup assigns them)")]
    public AudioClip loopClip;
    public AudioClip scratchClip, pomfClip, boingClip;
    [Range(0f, 1f)] public float volume = 0.55f;
    [Range(0f, 1f)] public float musicVolume = 0.3f;

    /// <summary>The one in the scene (on the Interaction object).</summary>
    public static CaughtInTheAct Current { get; private set; }
    /// <summary>No party before this (Time.time; sims set it).</summary>
    public static float NextAllowed = -1f;

    public Phase State { get; private set; }
    public bool Wild => State == Phase.Wild;
    /// <summary>A party (or the caught moment after it) is on: other things that start by themselves wait.</summary>
    public static bool Partying => Current && Current.State != Phase.Waiting;
    /// <summary>Counts (tests): parties, times you caught them, pillow hits, trampoline bounces.</summary>
    public int Parties { get; private set; }
    public int Catches { get; private set; }
    public int Hits { get; private set; }
    public int Bounces { get; private set; }
    public Role RoleOf(GummyBody b) { foreach (var m in ms) if (m.b == b) return m.role; return Role.Dance; }
    public bool InIt(GummyBody b) { foreach (var m in ms) if (m.b == b) return true; return false; }

    const float Beat = 60f / 124f; // (the tune's tempo)

    class M
    {
        public GummyBody b;
        public GummyBrain br;
        public CuteFace face;
        public Role role;
        public Vector2 home;      // where it was when you left
        public float homeY;
        public int partner = -1, side;
        public bool hurried, attacker, landed, sneak;
        public float nextMove, nextLine, nextLook, swungAt = -10f;
        public Vector2 spot;
        public float spotY;
        public int lastBeat = -1;
    }
    readonly List<M> ms = new List<M>();
    float t, wildAt, roundUntil, restUntil, grace, awayNow = -1f;
    bool resting;
    long binId;
    float binX0, binX1, binY;

    // ------------------------------------------------------------------ life

    void Awake()
    {
        root = new GameObject("Caught In The Act").transform;
        root.SetParent(transform, false);
        music = gameObject.AddComponent<AudioSource>();
        music.playOnAwake = false;
        music.loop = true;
        music.spatialBlend = 0f;
        music.volume = 0f;
        sfx = gameObject.AddComponent<AudioSource>();
        sfx.playOnAwake = false;
        sfx.spatialBlend = 0f;
    }

    void OnEnable() { Current = this; }

    void OnDisable()
    {
        if (State != Phase.Waiting) End(false);
        if (music) music.Stop();
        if (Current == this) Current = null;
    }

    void OnDestroy() { if (root) Destroy(root.gameObject); }

    // ------------------------------------------------------------------ you

    /// <summary>Chat "go wild": the next time you've been away a little (20 s), whatever the cooldown.</summary>
    public void Dare()
    {
        awayNow = 20f;
        NextAllowed = Mathf.Min(NextAllowed, Time.time);
    }

    /// <summary>Party now (WhipIt > Buddy > Go Wild Now): input doesn't count for the first few seconds.</summary>
    public bool StartNow(float graceSeconds = 6f)
    {
        if (State != Phase.Waiting || Busy() || Bodies().Count == 0) return false;
        Begin(Time.time, graceSeconds);
        return true;
    }

    static float Idle() => WorkClock.IdleProbe != null ? WorkClock.IdleProbe() : DesktopInput.IdleSeconds;

    static bool Busy() => Bedtime.Sleeping || (Lava.Current && Lava.Current.On) || (Rain.Current && Rain.Current.Raining)
        || LevelUpParty.Current || BandJam.Current || PickupGame.Current || BuddyTower.Current || BuddyHideSeek.Playing
        || (GiantPotion.Current && GiantPotion.Current.Giant) || (Trophies.Current && Trophies.Current.Showing) || BulletTime.Active;

    List<GummyBody> Bodies()
    {
        var list = new List<GummyBody>();
        foreach (var b in GummyBody.All) if (b && b.isActiveAndEnabled && !b.Held && !b.InRoom && b.GetComponent<GummyBrain>() && !b.GetComponent<GummyBrain>().Towering) list.Add(b);
        return list;
    }

    // ------------------------------------------------------------------ each frame

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("M.Update", t0); }
    void UpdateTimed()
    {
        if (BulletTime.Stopped) return; // (time stopped: the party holds still too, and you can't catch it)
        float dt = Mathf.Min(Time.deltaTime, 0.05f), now = Time.time;
        t += dt;
        switch (State)
        {
            case Phase.Waiting:
                if (NextAllowed < 0f) NextAllowed = now + 60f; // (not right at the start)
                if (now >= NextAllowed && Idle() >= (awayNow > 0f ? awayNow : awayAfter) && !Busy() && Bodies().Count > 0) Begin(now, 0f);
                break;
            case Phase.Wild:
                if (now - wildAt > grace && Idle() < 0.6f) { Catch(now); break; } // you're back!
                if (Busy()) { End(true); break; }
                if (now - wildAt > maxWild) { Tired(now); break; }
                Party(dt, now);
                break;
            case Phase.Frozen:
                if (t >= freezeFor) Innocent(now);
                break;
            case Phase.Innocent:
                Act(now);
                if (t >= innocentFor) End(true);
                break;
        }
        Music(dt);
        StepParticles(dt);
    }

    // ------------------------------------------------------------------ the party

    void Begin(float now, float graceSeconds)
    {
        ms.Clear();
        foreach (var b in Bodies())
        {
            var br = b.GetComponent<GummyBrain>();
            if (br.Current == GummyBrain.State.Asleep) br.WakeUp();
            ms.Add(new M { b = b, br = br, face = b.GetComponent<CuteFace>(), home = b.Center, homeY = b.Extent.yMin, nextLine = now + Rnd(4f, 9f) });
        }
        State = Phase.Wild;
        t = 0f;
        wildAt = now;
        grace = graceSeconds;
        awayNow = -1f;
        Parties++;
        FindBin();
        var first = ms[Rand.Next(ms.Count)].b;
        Say(first, Line(first, "THEY'RE GONE! PARTY!!", "COAST IS CLEAR!", "THEY LEFT! ESPRESSO PARTY!", "BUN VOYAGE! PARTY!", "RAWR! PARTY TIME!", "QUACK! THEY'RE GONE!", "THE HUMAN LEFT. PARTY."));
        int k = 0;
        foreach (var m in ms) if (m.b != first && k < 2 && Rand.NextDouble() < 0.6) { k++; Say(m.b, Pick("WOOO!", "YESSS!", "PARTY!!", "FINALLY!"), 0.7f + 0.5f * k); }
        Assign(now);
        if (music && loopClip && !BuddyBand.Muted) { music.clip = loopClip; music.volume = 0f; music.Play(); }
    }

    // who does what this round
    void Assign(float now)
    {
        foreach (var m in ms) Release(m);
        var free = new List<M>();
        foreach (var m in ms) if (m.b && m.b.isActiveAndEnabled) free.Add(m);
        // shuffled (its own dice)
        for (int i = free.Count - 1; i > 0; i--) { int j = Rand.Next(i + 1); var tmp = free[i]; free[i] = free[j]; free[j] = tmp; }
        foreach (var m in free) { m.role = Role.Dance; m.partner = -1; m.landed = false; m.sneak = false; }
        int at = 0;
        if (binId != 0 && free.Count > 0 && Rand.NextDouble() < 0.85)
        {
            // the smallest one on the bin's screen bounces on it (it's a little perch)
            int best = -1;
            for (int i = 0; i < free.Count; i++)
                if (ScreenBounds.SameScreen(free[i].b.Center, new Vector2(binX0, binY)) && (best < 0 || free[i].b.size < free[best].b.size)) best = i;
            if (best < 0) best = 0;
            free[best].role = Role.Trampoline;
            var sw = free[0]; free[0] = free[best]; free[best] = sw;
            at = 1;
        }
        if (free.Count - at >= 2 && Rand.NextDouble() < 0.7 && PairUp(free[at], free[at + 1])) at += 2;
        int left = free.Count - at;
        if (left >= 3 && Rand.NextDouble() < 0.6)
        {
            for (int i = at; i < free.Count; i++) free[i].role = Role.Conga;
            var lead = free[at];
            Say(lead.b, Pick("CONGA LINE!", "CONGA! FOLLOW ME!", "EVERYBODY CONGA!"), 0.3f);
        }
        roundUntil = now + Rnd(50f, 80f);
        resting = false;
    }

    bool PairUp(M a, M b)
    {
        if (!ScreenBounds.SameScreen(a.b.Center, b.b.Center)) return false;
        // they meet where the first one is: side by side on its floor
        float gap = 0.45f * (a.b.size + b.b.size) + 0.55f;
        Vector2 span = a.b.SupportSpan;
        float x = Mathf.Clamp(a.b.Center.x, span.x + gap, span.y - gap);
        if (span.y - span.x < gap * 2f + 0.4f) return false;
        a.role = b.role = Role.Pillow;
        int ia = ms.IndexOf(a), ib = ms.IndexOf(b);
        a.partner = ib; b.partner = ia;
        a.side = -1; b.side = 1;
        a.spot = new Vector2(x - gap * 0.5f, a.b.Extent.yMin); b.spot = new Vector2(x + gap * 0.5f, a.b.Extent.yMin);
        a.spotY = b.spotY = a.b.Extent.yMin;
        a.attacker = true; b.attacker = false;
        a.nextMove = b.nextMove = Time.time + 2.5f;
        Say(a.b, Pick("PILLOW FIGHT!", "EN GARDE!", "POMF ATTACK!"), 0.4f);
        return true;
    }

    void Party(float dt, float now)
    {
        if (resting)
        {
            foreach (var m in ms) if (m.b && m.br) { m.br.KeepAwake(); m.br.Food = null; m.br.StayPut = true; }
            if (now >= restUntil) Assign(now);
            return;
        }
        if (now >= roundUntil)
        {
            // a breather: they flop down and pant, then the next round
            resting = true;
            restUntil = now + Rnd(6f, 10f);
            foreach (var m in ms) { Release(m); if (m.br) { m.br.StayPut = true; m.br.Act(new Vector2(1.12f, 0.88f), 1.2f); } }
            var any = ms[Rand.Next(ms.Count)].b;
            if (any) Say(any, Pick("PHEW!", "BREAK!", "WATER BREAK!", "*PANT PANT*"));
            return;
        }
        int beat = Mathf.FloorToInt((now - wildAt) / Beat);
        float ph = (now - wildAt) / Beat - beat;
        M conga = null;
        foreach (var m in ms)
        {
            if (!m.b || !m.b.isActiveAndEnabled || !m.br) continue;
            m.br.KeepAwake();
            m.br.NoDizzyUntil = now + 1.5f; // (spinning for fun: not dizzy)
            if (m.b.Held) continue;
            switch (m.role)
            {
                case Role.Dance: Dance(m, beat, ph); break;
                case Role.Conga: Conga(m, ref conga, now, ph); break;
                case Role.Pillow: Pillow(m, now); break;
                case Role.Trampoline: Trampoline(m, now); break;
            }
            if (now >= m.nextLine)
            {
                m.nextLine = now + Rnd(7f, 14f);
                if (Rand.NextDouble() < 0.4) Say(m.b, m.role == Role.Trampoline ? Pick("WHEEE!", "BOING BOING!", "HIGHER!") : m.role == Role.Pillow ? Pick("TAKE THAT!", "POMF!", "HAHA!") : m.role == Role.Conga ? Pick("CONGA ♪", "DA DA DA DA DA HEY!", "LEFT, RIGHT ♪") : Line(m.b, "WOOO!", "DANCE DANCE!", "BEAN BOOGIE!", "SHAKE YOUR BUNS!", "RAWR-OCK!", "QUACK QUACK ♪", "WE DANCE."));
            }
        }
        if (Rand.NextDouble() < dt * 3f && ms.Count > 0) { var m = ms[Rand.Next(ms.Count)]; if (m.b) Spark(new Vector2(m.b.Center.x + Rnd(-0.4f, 0.4f) * m.b.size, m.b.Extent.yMax + 0.1f), new Vector2(Rnd(-0.3f, 0.3f), Rnd(0.5f, 1f)), Rnd(0.05f, 0.09f), PartyColor(), Rnd(0.6f, 1f), false); }
    }

    // on the spot: a bounce on every beat, a spin every fourth
    void Dance(M m, int beat, float ph)
    {
        m.br.Food = null;
        m.br.StayPut = true;
        float s = Mathf.Sqrt(m.b.size / 0.6f);
        if (beat != m.lastBeat && m.b.Grounded)
        {
            m.lastBeat = beat;
            bool big = beat % 4 == 3;
            Vector2 span = m.b.SupportSpan;
            float hw = m.b.Extent.width * 0.5f, vx = Rnd(-0.3f, 0.3f) * s;
            if (m.b.Center.x - hw < span.x + 0.35f) vx = Mathf.Abs(vx) + 0.2f;      // (near the edge of its window: inwards)
            else if (m.b.Center.x + hw > span.y - 0.35f) vx = -Mathf.Abs(vx) - 0.2f;
            m.b.Hop(new Vector2(vx, GummyBody.JumpSpeed((big ? 0.32f : 0.12f) * s, m.b.gravity, m.b.Drag)), 0.3f);
            if (big) m.side = Rand.NextDouble() < 0.5 ? -1 : 1; // (which way it wiggles this bar)
        }
        float k = ph < 0.15f ? 1f - ph / 0.15f : 0f; // (a squash right on the beat)
        m.b.Pose = new Vector2(1f + 0.14f * k, 1f / (1f + 0.14f * k));
        m.b.Lean = (beat % 4 == 3 ? 18f : 7f) * (m.side == 0 ? 1 : m.side) * Mathf.Sin(ph * Mathf.PI); // (a wiggle; a big one on the fourth beat)
    }

    // a line along the floor: the first one leads back and forth, the others follow, wiggling
    void Conga(M m, ref M ahead, float now, float ph)
    {
        float s = Mathf.Sqrt(m.b.size / 0.6f);
        m.br.StayPut = false;
        if (!m.hurried) { m.hurried = true; m.br.Hurry = 0.9f; }
        if (ahead == null)
        {
            // the leader
            if (m.nextMove == 0f || Mathf.Abs(m.b.Center.x - m.spot.x) < 0.4f || now > m.nextMove)
            {
                Vector2 span = m.b.SupportSpan;
                float lo = span.x + 0.6f, hi = span.y - 0.6f;
                m.side = m.side == 0 ? 1 : -m.side;
                float x = Mathf.Clamp(m.b.Center.x + m.side * Rnd(2.5f, 4.5f), lo, hi);
                if (Mathf.Abs(x - m.b.Center.x) < 1f) { m.side = -m.side; x = Mathf.Clamp(m.b.Center.x + m.side * Rnd(2.5f, 4.5f), lo, hi); }
                m.spot = new Vector2(x, m.b.Extent.yMin);
                m.nextMove = now + 8f;
            }
            m.br.Food = new Vector2(m.spot.x, m.spot.y + m.b.size * 0.3f);
            m.br.FoodSurface = m.spot.y;
        }
        else
        {
            float dir = Mathf.Sign(ahead.b.Velocity.x != 0f ? ahead.b.Velocity.x : (ahead.spot.x - ahead.b.Center.x));
            if (dir == 0f) dir = 1f;
            float x = ahead.b.Center.x - dir * (0.45f * (ahead.b.size + m.b.size) + 0.15f);
            m.br.Food = new Vector2(x, ahead.b.Extent.yMin + m.b.size * 0.3f);
            m.br.FoodSurface = ahead.b.Extent.yMin;
        }
        m.b.Lean = 10f * Mathf.Sin((ph + (ahead == null ? 0f : 0.5f)) * Mathf.PI * 2f) * s;
        ahead = m;
    }

    // two of them side by side, taking turns: a hop into the other one, POMF, feathers
    void Pillow(M m, float now)
    {
        if (m.partner < 0 || m.partner >= ms.Count) { m.role = Role.Dance; return; }
        var o = ms[m.partner];
        if (!o.b || !o.b.isActiveAndEnabled) { m.role = Role.Dance; return; }
        bool at = Mathf.Abs(m.b.Center.x - m.spot.x) < 0.35f && Mathf.Abs(m.b.Extent.yMin - m.spotY) < 0.25f;
        bool oAt = Mathf.Abs(o.b.Center.x - o.spot.x) < 0.45f && Mathf.Abs(o.b.Extent.yMin - o.spotY) < 0.25f;
        if (!at && m.b.Grounded && now - m.swungAt > 0.7f)
        {
            // (back to its corner)
            m.br.StayPut = false;
            m.br.Food = new Vector2(m.spot.x, m.spotY + m.b.size * 0.3f);
            m.br.FoodSurface = m.spotY;
            if (!m.landed && now > roundUntil - 40f) { m.role = o.role = Role.Dance; } // (can't get together: they dance instead)
            return;
        }
        m.br.Food = null;
        m.br.StayPut = true;
        if (at) m.landed = true;
        if (!m.attacker || now < m.nextMove || !m.b.Grounded || !o.landed || !oAt || !o.b.Grounded) return;
        // swing!
        float s = Mathf.Sqrt(m.b.size / 0.6f), dx = o.b.Center.x - m.b.Center.x;
        m.b.Hop(new Vector2(Mathf.Sign(dx) * 2.6f * s - m.b.Velocity.x, GummyBody.JumpSpeed(0.3f * s, m.b.gravity, m.b.Drag) - m.b.Velocity.y), 0.3f);
        m.b.Pose = new Vector2(0.86f, 1.16f);
        m.attacker = false; o.attacker = true;
        m.swungAt = now;
        m.nextMove = o.nextMove = now + Rnd(1.3f, 2f);
        pendingHit = (m, o, now + 0.18f);
    }

    (M a, M b, float at)? pendingHit;

    void Hit(M a, M b)
    {
        Hits++;
        Vector2 mid = 0.5f * (a.b.Center + b.b.Center);
        float s = Mathf.Sqrt(b.b.size / 0.6f);
        b.b.Hop(new Vector2(Mathf.Sign(b.b.Center.x - a.b.Center.x) * 1.2f * s, GummyBody.JumpSpeed(0.15f * s, b.b.gravity, b.b.Drag)), 0.3f);
        b.b.Spin(Mathf.Sign(b.b.Center.x - a.b.Center.x) * -5f);
        Play(pomfClip, Rnd(0.95f, 1.2f), 0.8f);
        for (int i = 0; i < 14; i++) Spark(mid + Rnd2(0.15f), new Vector2(Rnd(-1.6f, 1.6f), Rnd(0.3f, 1.8f)), Rnd(0.07f, 0.11f), new Color(1f, 0.99f, 0.96f), Rnd(1.2f, 2f), true);
        if (Rand.NextDouble() < 0.35) Say(b.b, Pick("OOF!", "POMF!", "HEY!", "NO FAIR!"));
    }

    // over to the Recycle Bin, then bounce, bounce, bounce
    void Trampoline(M m, float now)
    {
        if (binId == 0) { m.role = Role.Dance; return; }
        float s = Mathf.Sqrt(m.b.size / 0.6f);
        bool on = m.b.SupportWindow == binId;
        if (on && m.b.Grounded)
        {
            m.br.Food = null;
            m.br.StayPut = true;
            if (now >= m.nextMove)
            {
                m.nextMove = now + 0.25f;
                Bounces++;
                float cx = 0.5f * (binX0 + binX1);
                m.b.Hop(new Vector2(Mathf.Clamp((cx - m.b.Center.x) * 4f, -2f, 2f) - m.b.Velocity.x, GummyBody.JumpSpeed(Rnd(0.9f, 1.3f) * s, m.b.gravity, m.b.Drag) - m.b.Velocity.y), 0.4f);
                m.b.Lean = Rand.NextDouble() < 0.5 ? 15f : -15f;
                Play(boingClip, Rnd(1.2f, 1.45f), 0.55f);
            }
            return;
        }
        if (!m.b.Grounded || m.br.Pouncing) return;
        float bx = 0.5f * (binX0 + binX1);
        int side = m.b.Center.x < bx ? -1 : 1;
        float standX = bx + side * (0.5f * (binX1 - binX0) + 0.45f * m.b.size + 0.35f);
        Vector2 span = m.b.SupportSpan;
        if (standX < span.x + 0.3f || standX > span.y - 0.3f) standX = bx - side * (0.5f * (binX1 - binX0) + 0.45f * m.b.size + 0.35f); // (no room that side)
        if (Mathf.Abs(m.b.Center.x - standX) < 0.45f && Mathf.Abs(m.b.Extent.yMin - (binY - (binY - FloorUnderBin()))) < 0.3f)
        {
            // beside it: up!
            m.br.Food = null;
            m.br.StayPut = true;
            if (now >= m.nextMove && m.br.Pounce(new Vector2(bx, binY + m.b.size * 0.55f), 0.25f)) m.nextMove = now + 1.2f;
            return;
        }
        m.br.StayPut = false;
        if (!m.hurried) { m.hurried = true; m.br.Hurry = 1.4f; }
        float fy = FloorUnderBin();
        m.br.Food = new Vector2(standX, fy + m.b.size * 0.3f);
        m.br.FoodSurface = fy;
        if (now - wildAt > 25f && Bounces == 0) { m.role = Role.Dance; } // (couldn't get there: dances instead)
    }

    float FloorUnderBin() => ScreenBounds.FloorAt(new Vector2(0.5f * (binX0 + binX1), binY - 0.05f));

    void FindBin()
    {
        binId = 0;
        foreach (var ic in DesktopPlatforms.Icons)
        {
            if (!ic.bin) continue;
            foreach (var pl in DesktopPlatforms.Current)
                if (pl.id == ic.id) { binId = pl.id; binX0 = pl.x0; binX1 = pl.x1; binY = pl.y; return; }
        }
    }

    // ------------------------------------------------------------------ caught!

    void Catch(float now)
    {
        Catches++;
        State = Phase.Frozen;
        t = 0f;
        pendingHit = null;
        if (music) music.Stop();
        Play(scratchClip, 1f, 1f);
        foreach (var m in ms)
        {
            if (!m.b || !m.b.isActiveAndEnabled) continue;
            m.b.Frozen = true; // (right where it is: mid-hop, upside down, half way up the conga)
            if (m.face) m.face.LookAt = null; // (wide eyes, straight at you)
            var sp = m.b.GetComponent<BuddySpeech>();
            if (sp) sp.Say("!", freezeFor + 0.2f, true); // (busted)
        }
    }

    void Innocent(float now)
    {
        State = Phase.Innocent;
        t = 0f;
        // the one furthest from where it was tiptoes back
        M far = null;
        float most = 1.2f;
        foreach (var m in ms)
        {
            if (!m.b) continue;
            m.b.Frozen = false;
            Release(m);
            float d = Mathf.Abs(m.b.Center.x - m.home.x) + Mathf.Abs(m.b.Extent.yMin - m.homeY);
            if (d > most) { most = d; far = m; }
            m.nextLook = now + Rnd(0.1f, 0.5f);
        }
        if (far != null) far.sneak = true;
        // a couple of them say something very innocent
        int k = 0;
        foreach (var m in ms)
        {
            if (!m.b || k >= 3 || (k > 0 && Rand.NextDouble() < 0.4)) continue;
            Say(m.b, k == 0 ? Pick("♪ ~ ♪", "*WHISTLES*", "♪ HMM HMM ~") : Line(m.b, Pick("NOTHING HAPPENED", "WHAT PARTY?"), Pick("I WAS NAPPING", "WHO, ME?"), Pick("JUST... BREWING", "DECAF. TOTALLY."), Pick("JUST CHILLING", "NO BUNS WERE HARMED"), Pick("RAWR? ...I MEAN HI", "DINO WAS ASLEEP"), Pick("...QUACK?", "QUACK. (INNOCENTLY)"), "WE SAW NOTHING."), 0.25f + 0.9f * k);
            k++;
        }
    }

    // eyes anywhere but at you; the one that wandered off sneaks back
    void Act(float now)
    {
        foreach (var m in ms)
        {
            if (!m.b || !m.br) continue;
            if (m.face && now >= m.nextLook)
            {
                m.nextLook = now + Rnd(0.5f, 1.1f);
                Vector2 away = m.b.Center - Grabber.CursorWorld;
                if (away.sqrMagnitude < 0.01f) away = Vector2.right;
                away = away.normalized;
                Vector2 side = new Vector2(-away.y, away.x) * (Rand.NextDouble() < 0.5 ? 1f : -1f);
                m.face.LookAt = m.b.Center + (away * 0.6f + side * 0.8f + new Vector2(0f, Rnd(0.2f, 1f))) * 3f;
            }
            if (m.sneak)
            {
                m.br.StayPut = false;
                if (!m.hurried) { m.hurried = true; m.br.Hurry = 0.7f; }
                m.br.Food = new Vector2(m.home.x, m.homeY + m.b.size * 0.3f);
                m.br.FoodSurface = m.homeY;
                if (Mathf.Abs(m.b.Center.x - m.home.x) < 0.3f) { m.sneak = false; m.br.Food = null; m.br.StayPut = true; }
            }
            else { m.br.StayPut = true; m.br.Food = null; }
        }
    }

    // nobody came back for ages: they run out of steam
    void Tired(float now)
    {
        foreach (var m in ms) if (m.b && Rand.NextDouble() < 0.5) Say(m.b, Pick("SO TIRED...", "PARTIED OUT", "*YAWN*"), Rnd(0f, 1.5f));
        End(true);
    }

    void End(bool cooldownToo)
    {
        foreach (var m in ms)
        {
            if (!m.b) continue;
            m.b.Frozen = false;
            if (m.face) m.face.LookAt = null;
            Release(m);
            if (m.br) m.br.StayPut = false;
            m.b.Lean = 0f;
        }
        ms.Clear();
        pendingHit = null;
        State = Phase.Waiting;
        t = 0f;
        if (cooldownToo) NextAllowed = Time.time + Rnd(cooldown.x, cooldown.y) * 60f;
    }

    void Release(M m)
    {
        if (!m.br) return;
        m.br.Food = null;
        if (m.hurried) { m.br.Hurry = 1f; m.hurried = false; }
        m.b.Lean = 0f;
        m.spot = Vector2.zero;
        m.nextMove = 0f;
    }

    void LateUpdate() { long t0 = WhipProf.T(); LateUpdateTimed(); WhipProf.Add("M.LateUpdate", t0); }
    void LateUpdateTimed()
    {
        if (pendingHit.HasValue && Time.time >= pendingHit.Value.at)
        {
            var h = pendingHit.Value;
            pendingHit = null;
            if (State == Phase.Wild && h.a.b && h.b.b) Hit(h.a, h.b);
        }
        for (int i = later.Count - 1; i >= 0; i--)
        {
            if (Time.time < later[i].at) continue;
            var l = later[i];
            later.RemoveAt(i);
            if (State == Phase.Frozen) continue; // (frozen: not a word)
            var sp = l.who ? l.who.GetComponent<BuddySpeech>() : null;
            if (sp && l.who.isActiveAndEnabled) sp.Say(l.text, 0f, true);
        }
    }

    // ------------------------------------------------------------------ sound

    Transform root;
    AudioSource music, sfx;

    void Music(float dt)
    {
        if (!music) return;
        float want = State == Phase.Wild && !resting ? musicVolume * volume : State == Phase.Wild ? musicVolume * volume * 0.35f : 0f;
        if (BuddyBand.Muted) want = 0f;
        if (State == Phase.Wild && want > 0f && loopClip && !music.isPlaying) { music.clip = loopClip; music.Play(); }
        music.volume = Mathf.MoveTowards(music.volume, want, dt * 0.5f);
        if (music.isPlaying && State == Phase.Waiting && music.volume <= 0.001f) music.Stop();
    }

    void Play(AudioClip clip, float pitch, float vol)
    {
        if (!clip || !sfx) return;
        sfx.pitch = pitch;
        sfx.PlayOneShot(clip, volume * vol);
    }

    // ------------------------------------------------------------------ sparkles and feathers (pooled)

    const int MaxParticles = 64;
    struct P { public Vector2 pos, vel; public float t, life, size; public Color color; public bool on, feather; }
    readonly P[] ps = new P[MaxParticles];
    readonly SpriteRenderer[] psr = new SpriteRenderer[MaxParticles];
    int nextP;

    static Color PartyColor()
    {
        switch (Rand.Next(5))
        {
            case 0: return new Color(1f, 0.45f, 0.7f);
            case 1: return new Color(0.45f, 0.8f, 1f);
            case 2: return new Color(1f, 0.9f, 0.35f);
            case 3: return new Color(0.6f, 1f, 0.5f);
            default: return Color.white;
        }
    }

    void Spark(Vector2 at, Vector2 vel, float size, Color c, float life, bool feather)
    {
        int i = -1;
        for (int k = 0; k < MaxParticles; k++) { int j = (nextP + k) % MaxParticles; if (!ps[j].on) { i = j; nextP = (j + 1) % MaxParticles; break; } }
        if (i < 0) return;
        ps[i] = new P { pos = at, vel = vel, size = size, color = c, life = life, on = true, feather = feather };
        if (!psr[i])
        {
            var go = new GameObject("Party Sparkle");
            go.transform.SetParent(root, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sortingOrder = 12;
            if (material) sr.sharedMaterial = material;
            psr[i] = sr;
        }
        psr[i].sprite = feather ? FeatherSprite() : Snack.StarSprite();
    }

    static Sprite s_feather;

    // a little white feather (1 unit wide): a curved vane with a grey quill down the middle
    static Sprite FeatherSprite()
    {
        if (s_feather) return s_feather;
        const int w = 32, h = 16;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "Feather" };
        var px = new Color32[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float u = (x + 0.5f) / w * 2f - 1f, v = (y + 0.5f) / h * 2f - 1f;
                float bend = 0.25f * u * u - 0.12f, vv = v - bend;              // (a slight curve)
                float halfH = 0.75f * Mathf.Sqrt(Mathf.Max(0f, 1f - u * u)) * (u > 0f ? 1f - 0.4f * u : 1f);
                float d = Mathf.Abs(vv) - halfH;
                float a = Mathf.Clamp01(0.5f - d * h * 0.5f);
                Color c = Color.Lerp(new Color(1f, 1f, 1f), new Color(0.88f, 0.88f, 0.92f), Mathf.Abs(vv) / Mathf.Max(halfH, 0.01f) * 0.6f);
                if (Mathf.Abs(vv) < 0.09f && u > -0.95f) c = new Color(0.72f, 0.72f, 0.78f); // the quill
                if (Mathf.Sin(u * 22f + vv * 6f) > 0.85f) c *= 0.95f;                     // (barbs)
                c.a = a;
                px[y * w + x] = c;
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return s_feather = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), w);
    }

    void StepParticles(float dt)
    {
        for (int i = 0; i < MaxParticles; i++)
        {
            ref P p = ref ps[i];
            var sr = psr[i];
            if (!p.on) { if (sr && sr.enabled) sr.enabled = false; continue; }
            if (State == Phase.Frozen) { continue; } // (everything stops: the feathers too)
            p.t += dt;
            if (p.t >= p.life) { p.on = false; sr.enabled = false; continue; }
            float u = p.t / p.life;
            if (p.feather)
            {
                // drifts down, rocking side to side
                p.vel *= 1f - 2.5f * dt;
                p.vel.y -= 0.6f * dt;
                p.pos += (p.vel + new Vector2(Mathf.Sin(p.t * 6f + i) * 0.4f, 0f)) * dt;
            }
            else
            {
                p.vel *= 1f - 2f * dt;
                p.pos += p.vel * dt;
            }
            sr.enabled = true;
            float s = p.feather ? p.size * 2.2f : p.size * 2f * Mathf.Sin(Mathf.PI * Mathf.Min(1f, u * 1.2f));
            sr.transform.position = new Vector3(p.pos.x, p.pos.y, 0f);
            sr.transform.localScale = p.feather ? new Vector3(s, s, 1f) : new Vector3(s, s, 1f);
            sr.transform.localRotation = p.feather ? Quaternion.Euler(0f, 0f, 25f * Mathf.Sin(p.t * 5f + i)) : Quaternion.identity;
            Color c = p.color;
            c.a = p.feather ? 0.95f * (1f - u * u) : 1f - u * u;
            sr.color = c;
        }
    }

    // ------------------------------------------------------------------ words

    static string Line(GummyBody b, string apple, string tomato, string bean, string burger, string dino, string duck, string monster)
    {
        var sp = b ? b.GetComponent<BuddySpeech>() : null;
        switch (sp ? sp.voice : BuddySpeech.Voice.Apple)
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
        later.Add((Time.time + Mathf.Max(delay, 0f), b, text));
    }

    // its own dice: the buddies' dice stay as they were
    static readonly System.Random Rand = new System.Random(73);
    static float Rnd(float a, float b) => a + (float)Rand.NextDouble() * (b - a);
    static Vector2 Rnd2(float r) => new Vector2(Rnd(-r, r), Rnd(-r, r));
    static string Pick(params string[] l) => l[Rand.Next(l.Length)];
}
