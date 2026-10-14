using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// A fused monster (BuddyFusion squashes buddies into one): one big lumpy gummy made of everyone inside (MonsterLook).
/// The buddies inside are switched off just as they were until they come out again. Grab it and it's far too heavy to
/// carry: the bit you grabbed stretches out like taffy, and with a good pull the friend nearest your grip comes out with
/// a slurp (you're holding it now), dizzy but fine; pull the last two apart and the monster's gone. It lumbers about,
/// roars now and then, comes down with a STOMP (the window it lands on wobbles, buddies close by jump), eats any snack it
/// gets to in one chomp and now and then chases the others for fun (it never catches them: they run and hide,
/// BuddyFusion). With all six inside there's nobody left to scare, so it stalks your cursor (it never moves it). What it
/// earns is shared out when it splits. It's still a monster after a restart (monsters.txt).
/// </summary>
[DefaultExecutionOrder(8)] // after BuddyEater has picked a snack: the chase is only when there's none
[RequireComponent(typeof(GummyBody))]
[DisallowMultipleComponent]
public class BuddyMonster : MonoBehaviour
{
    public const int MaxMembers = 6;

    [Header("Sounds (from the buddies' BuddyFusion)")]
    public AudioClip fuseClip, splitClip, roarClip, stompClip, stretchLoop;
    [Range(0f, 1f)] public float volume = 0.7f;

    /// <summary>Who's inside (0 = the one the others were pushed into).</summary>
    public IReadOnlyList<GummyBody> Members => members;
    /// <summary>The buddy it's chasing for fun right now (or null).</summary>
    public GummyBody Chasing => now() < chaseUntil ? chasing : null;
    /// <summary>XP it earned so far (shared out when it splits).</summary>
    public int XpPool => xpPool;
    public string MonsterName => look ? look.MonsterName : "MONSTER";
    /// <summary>Tests: roars, stomps, chases, friends pulled out.</summary>
    public int Roars { get; private set; }
    public int Stomps { get; private set; }
    public int Chases { get; private set; }
    public static int Splits { get; private set; }
    public static int Fusions { get; private set; }

    static readonly List<BuddyMonster> s_all = new List<BuddyMonster>();
    /// <summary>Every monster there is.</summary>
    public static IReadOnlyList<BuddyMonster> All => s_all;

    readonly List<GummyBody> members = new List<GummyBody>();
    GummyBody body;
    GummyBrain brain;
    BuddySpeech speech;
    BuddyEater eater;
    WindowWobble wobble;
    MonsterLook look;
    AudioSource sfx, stretch;
    int xpPool;
    float awayX, awayUntil;
    float nextRoar, nextChase, chaseUntil, nextStalk, lastStomp = -10f, roarT = -1f, peelAfter, airMinVy, poseUntil = -1f;
    Vector2 pose = Vector2.one;
    bool wasAir, wasHeld;
    int pick = -1;
    GummyBody chasing;

    static float now() => Time.time;

    // ------------------------------------------------------------------ who's where

    /// <summary>Is this buddy inside a monster right now?</summary>
    public static bool IsInside(GummyBody b) => Of(b) != null;

    /// <summary>The monster this buddy is inside (or null).</summary>
    public static BuddyMonster Of(GummyBody b)
    {
        if (!b) return null;
        for (int i = 0; i < s_all.Count; i++) if (s_all[i] && s_all[i].members.Contains(b)) return s_all[i];
        return null;
    }

    /// <summary>The closest monster and the gap between it and this point (0 = touching / inside).</summary>
    public static BuddyMonster Nearest(GummyBody to, out float gap)
    {
        BuddyMonster best = null;
        gap = float.MaxValue;
        foreach (var m in s_all)
        {
            if (!m || !m.body || !m.isActiveAndEnabled) continue;
            Rect a = m.body.Extent, b = to.Extent;
            float dx = Mathf.Max(0f, Mathf.Max(a.xMin - b.xMax, b.xMin - a.xMax));
            float dy = Mathf.Max(0f, Mathf.Max(a.yMin - b.yMax, b.yMin - a.yMax));
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            if (d < gap) { gap = d; best = m; }
        }
        return best;
    }

    public bool Has(GummyBody b) => members.Contains(b);

    // ------------------------------------------------------------------ fusing

    /// <summary>
    /// Squashes `incoming` into `into` (a buddy: a new monster of the two, where `into` is; a monster: one more inside).
    /// Returns the monster, or null when it can't (a seventh, a monster into a monster).
    /// </summary>
    public static BuddyMonster Fuse(GummyBody incoming, GummyBody into, bool quiet = false)
    {
        if (!incoming || !into || incoming == into || incoming.IsMonster || IsInside(incoming)) return null;
        var m = into.GetComponent<BuddyMonster>();
        if (m)
        {
            if (m.members.Count >= MaxMembers) return null;
            m.Add(incoming, quiet);
            return m;
        }
        if (into.IsMonster || IsInside(into)) return null;
        return Create(new List<GummyBody> { into, incoming }, into.Center, 0, quiet);
    }

    static BuddyMonster Create(List<GummyBody> list, Vector2 at, int xp, bool quiet)
    {
        GummyBody host = list[0];
        var looks = Looks(list);
        var go = new GameObject("Gummy Monster");
        go.SetActive(false);
        go.transform.position = new Vector3(at.x, at.y, host.transform.position.z);

        var b = go.AddComponent<GummyBody>();
        b.shader = host.shader;
        b.spriteMaterial = host.spriteMaterial;
        b.sortingOrder = host.sortingOrder; // (its range is free: the host is inside)
        b.shape = host.shape;
        b.size = host.size;
        b.color = host.color;
        b.rimColor = host.rimColor;
        b.topper = GummyBody.Topper.None;
        b.gloss = host.gloss;
        b.particles = 32; // a big lumpy outline
        b.uprightSpeed = 3f; // (rights itself gently: a big lump flung upright would roll off)
        b.IsMonster = true;
        b.Heavy = true;
        b.sticky = false; // (too heavy to stick to things: it lands, THUD)
        // its jelly: like the host's, but a big heavy lump holds its shape better and doesn't bounce
        b.edgeStiffness = host.edgeStiffness; b.areaStiffness = host.areaStiffness; b.iterations = host.iterations;
        b.gravity = host.gravity; b.damping = host.damping; b.friction = host.friction;
        b.stiffness = Mathf.Max(host.stiffness * 1.6f, 0.1f);
        b.restitution = 0.02f;
        b.impactWobble = host.impactWobble * 0.6f;
        b.wobbleDamping = Mathf.Max(host.wobbleDamping, 0.5f);
        var face = go.AddComponent<CuteFace>();
        face.material = host.spriteMaterial;
        face.Hidden = true;

        var hfx = host.GetComponent<GummyFx>();
        var fx = go.AddComponent<GummyFx>();
        if (hfx) { fx.splatClip = hfx.splatClip; fx.bumpClip = hfx.bumpClip; fx.popClip = hfx.popClip; fx.stretchLoop = hfx.stretchLoop; }

        var br = go.AddComponent<GummyBrain>();
        br.hopSpeed = 1.05f;            // lumbering: low heavy hops (x size / 0.6, and it's big)
        br.hopUp = 3.6f;
        br.squashPose = new Vector2(1.2f, 0.82f);
        br.launchPose = new Vector2(0.88f, 1.12f);
        br.pauseBetweenHops = new Vector2(0.6f, 1.1f);
        br.hopsPerWalk = new Vector2Int(2, 4);
        br.idleTime = new Vector2(1.5f, 4f);
        br.hopTowardsCursor = 0.3f;
        br.climbChance = 0f;           // too heavy to climb or peek
        br.peekChance = 0f;
        br.sleepAfter = 90f;
        br.leapHeight = 4f;

        var hs = host.GetComponent<BuddySpeech>();
        var sp = go.AddComponent<BuddySpeech>();
        sp.voice = BuddySpeech.Voice.Monster;
        sp.chattiness = 0.8f;
        sp.chatterEvery = new Vector2(30f, 70f);
        if (hs) sp.blipClip = hs.blipClip;

        var he = host.GetComponent<BuddyEater>();
        var ea = go.AddComponent<BuddyEater>();
        if (he) ea.crunchClip = he.crunchClip;

        var hw = host.GetComponent<WindowWobble>();
        var ww = go.AddComponent<WindowWobble>();
        if (hw) ww.boingClip = hw.boingClip;

        go.AddComponent<BuddyLayer>();
        var hb = host.GetComponent<BuddyBand>();
        var bd = go.AddComponent<BuddyBand>(); // (the tuba: the clips come from the band's kit)
        if (hb) { bd.volume = hb.volume; bd.hopVolume = hb.hopVolume; bd.jamVolume = hb.jamVolume; bd.material = hb.material; }
        var lk = go.AddComponent<MonsterLook>();
        lk.material = host.spriteMaterial;

        var m = go.AddComponent<BuddyMonster>();
        var hf = host.GetComponent<BuddyFusion>();
        if (hf) { m.fuseClip = hf.fuseClip; m.splitClip = hf.splitClip; m.roarClip = hf.roarClip; m.stompClip = hf.stompClip; m.volume = hf.volume; }
        if (hfx) m.stretchLoop = hfx.stretchLoop;
        m.members.AddRange(list);
        m.xpPool = xp;
        foreach (var x in list) Hide(x);

        go.SetActive(true);
        lk.Become(looks);
        Fusions++;
        if (!quiet) m.Fused(list[list.Count - 1], true);
        else m.nextRoar = Time.time + Random.Range(6f, 14f);
        Save();
        return m;
    }

    void Add(GummyBody b, bool quiet)
    {
        members.Add(b);
        Hide(b);
        look.Become(Looks(members));
        Fusions++;
        if (!quiet) Fused(b, false);
        Save();
    }

    static List<MonsterLook.Member> Looks(List<GummyBody> list)
    {
        var l = new List<MonsterLook.Member>();
        foreach (var b in list) l.Add(MonsterLook.Member.Of(b));
        return l;
    }

    // switched off just as it is (you might be holding it: you're not any more)
    static void Hide(GummyBody b)
    {
        if (b.Held) { Grabber.Handoff(b, null); b.EndGrab(); }
        var sp = b.GetComponent<BuddySpeech>();
        if (sp) sp.Hush();
        b.gameObject.SetActive(false);
    }

    // the moment it happens: a squelchy gulp, it wobbles and grows, and roars its name
    void Fused(GummyBody newest, bool born)
    {
        Play(fuseClip, Random.Range(0.92f, 1.05f));
        if (brain) brain.NoDizzyUntil = Time.time + 2f; // (growing isn't a crash)
        PoseFor(new Vector2(1.22f, 0.8f), 0.3f);
        roarT = 0.45f; // (a roar right after)
        if (speech)
        {
            string n = MonsterName;
            speech.Say(born ? Pick("I AM " + n + "!!", n + "!! RAAAWR!", "WE ARE " + n + "!") : Pick("MORE! " + n + " GROWS!", "BIGGER! RAAAWR!", "WELCOME INSIDE, " + Kind(newest) + "!"), 0f, true);
        }
        nextRoar = Time.time + Random.Range(10f, 18f);
        nextChase = Time.time + Random.Range(6f, 12f);
    }

    // ------------------------------------------------------------------ lifecycle

    void Awake()
    {
        body = GetComponent<GummyBody>();
        brain = GetComponent<GummyBrain>();
        speech = GetComponent<BuddySpeech>();
        eater = GetComponent<BuddyEater>();
        wobble = GetComponent<WindowWobble>();
        look = GetComponent<MonsterLook>();
        sfx = gameObject.AddComponent<AudioSource>();
        sfx.playOnAwake = false;
        sfx.spatialBlend = 0f;
        stretch = gameObject.AddComponent<AudioSource>();
        stretch.playOnAwake = false;
        stretch.loop = true;
        stretch.spatialBlend = 0f;
        stretch.volume = 0f;
        nextRoar = Time.time + Random.Range(10f, 20f);
        nextChase = Time.time + Random.Range(8f, 16f);
        nextStalk = Time.time + Random.Range(6f, 12f);
    }

    void OnEnable()
    {
        if (!s_all.Contains(this)) s_all.Add(this);
        if (eater) eater.Ate += OnAte;
    }

    void OnDisable()
    {
        if (eater) eater.Ate -= OnAte;
        if (stretch) stretch.Stop();
    }

    void OnDestroy() => s_all.Remove(this);

    void OnAte(Snack s)
    {
        xpPool += s && s.xp >= 0 ? s.xp : 5;
        Save();
    }

    // ------------------------------------------------------------------ every frame

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("BuddyMonster.Update", t0); }
    void UpdateTimed()
    {
        float t = Time.time, dt = Time.deltaTime;
        if (members.Count < 2) { Dissolve(body.Center, null, default); return; }

        // pulled: the bit you hold stretches out of it; far enough and the friend nearest your grip comes out
        if (body.Held && !wasHeld) pick = Nearest(body.GrabPoint);
        wasHeld = body.Held;
        if (body.Held)
        {
            if (stretchLoop && !stretch.isPlaying) { stretch.clip = stretchLoop; stretch.Play(); }
            stretch.volume = Mathf.Clamp01(body.Taffy) * 0.5f * volume;
            stretch.pitch = 0.7f + 0.5f * Mathf.Clamp01(body.Taffy);
            if (body.Taffy >= 1f && t >= peelAfter) { Peel(); return; }
            chaseUntil = 0f;
            return;
        }
        if (stretch.isPlaying) stretch.Stop();

        // the roar it saved up (right after a fusion); a squash or a rear-up held for a moment
        if (roarT >= 0f) { roarT -= dt; if (roarT < 0f) Roar(true); }
        if (t < poseUntil && body.Grounded) body.Pose = Vector2.Lerp(pose, Vector2.one, Mathf.Clamp01(1f - (poseUntil - t) / 0.25f)); // (eases back)

        // stomp: every landing after a proper drop shakes things up
        bool air = !body.Grounded;
        if (air) airMinVy = Mathf.Min(airMinVy, body.Velocity.y);
        if (!air && wasAir) { if (airMinVy < -3.2f) Stomp(-airMinVy); airMinVy = 0f; }
        wasAir = air;

        if (!brain || brain.Current == GummyBrain.State.Asleep) return;
        if (Bedtime.Sleeping) { if (chasing) { chasing = null; chaseUntil = 0f; brain.Hurry = 1f; } return; } // (bedtime: off to the pillow fort, no roaring)
        // a jam: it plays the tuba with them (no chasing, stalking or roaring till it's over)
        if (BandJam.Current && BandJam.Current.Plays(body))
        {
            if (chasing) { chasing = null; chaseUntil = 0f; brain.Hurry = 1f; brain.Food = null; }
            nextChase = Mathf.Max(nextChase, t + 8f);
            nextStalk = Mathf.Max(nextStalk, t + 8f);
            nextRoar = Mathf.Max(nextRoar, t + 6f);
            return;
        }
        if (t < awayUntil && brain.IsFree && body.Grounded && Mathf.Abs(body.Center.x - awayX) > 0.5f) { brain.Visit(awayX); awayUntil = 0f; }

        // roars now and then
        if (t >= nextRoar && brain.Current == GummyBrain.State.Idle && body.Grounded) Roar(false);

        bool others = AnyoneToScare();
        // chases one of them for fun (never catches it: when it gets close the game's over)
        if (chasing) UpdateChase(t);
        else if (others && t >= nextChase && brain.IsFree && body.Grounded && !(eater && eater.Target)) StartChase(t);

        // all six inside: nobody left to scare, so it comes after your cursor
        brain.hopTowardsCursor = others ? 0.3f : 0.85f;
        if (!others && t >= nextStalk && brain.IsFree && body.Grounded)
        {
            nextStalk = t + Random.Range(6f, 12f);
            Vector2 c = Grabber.CursorWorld;
            Rect r = ScreenBounds.WorldRect;
            if (r.Contains(c) && ScreenBounds.SameScreen(c, body.Center) && Mathf.Abs(c.x - body.Center.x) > body.size) // (on its screen)
            {
                brain.Visit(Mathf.Clamp(c.x, r.xMin + body.size, r.xMax - body.size));
                if (Random.value < 0.6f) Say(Pick("WE SEE YOU", "NOWHERE TO HIDE, HUMAN", "PET US? ...RAWR", "HERE, CURSOR CURSOR", "YOU'RE NEXT. HEHE"));
            }
        }
    }

    bool AnyoneToScare()
    {
        foreach (var b in GummyBody.All)
            if (b && b.isActiveAndEnabled && !b.IsMonster && b.GetComponent<BuddyFusion>()) return true;
        return false;
    }

    void Roar(bool fused)
    {
        nextRoar = Time.time + Random.Range(14f, 28f);
        Roars++;
        Play(roarClip, Random.Range(0.9f, 1.08f) * Mathf.Lerp(1.05f, 0.8f, Mathf.InverseLerp(2f, 6f, members.Count)));
        PoseFor(new Vector2(0.88f, 1.14f), 0.7f); // rears up
        if (brain) { brain.Linger(1.2f); brain.NoDizzyUntil = Time.time + 1.5f; }
        if (!fused) Say(RoarLine(), true);
    }

    string RoarLine()
    {
        var l = new List<string> { "RAAAAWR!", "RAAAWR!!", "GRAAAH!" };
        foreach (var m in look.Members)
            switch (m.kind)
            {
                case MonsterLook.Kind.Apple: l.Add("CRUNCH-RAAAWR!"); break;
                case MonsterLook.Kind.Tomato: l.Add("SPLAT-RAAAWR!"); break;
                case MonsterLook.Kind.Bean: l.Add("ESPRESS-RAAAWR!"); break;
                case MonsterLook.Kind.Burger: l.Add("MEAT-RAAAWR!"); break;
                case MonsterLook.Kind.Dino: l.Add("RAWR RAWR RAAAWR!"); break;
                case MonsterLook.Kind.Duck: l.Add("QUACK-RAAAWR!"); break;
            }
        return l[Random.Range(0, l.Count)];
    }

    // every landing is a thud (its walk: a soft one); a proper drop is a STOMP
    void Stomp(float speed)
    {
        float t = Time.time;
        if (t - lastStomp < 0.4f) return;
        lastStomp = t;
        bool big = speed > 5.5f;
        if (big) Stomps++;
        Play(stompClip, Random.Range(0.85f, 0.95f) * (big ? 0.8f : 1f), big ? 1f : 0.3f);
        long under = body.OnPlatform ? body.SupportWindow : 0;
        Vector2 feet = new Vector2(body.Center.x, body.Extent.yMin);
        if (wobble && under != 0) wobble.Stomp(under, feet, big ? 6f : 2.5f);
        // buddies close by jump (and run: they're scared anyway)
        float reach = body.Extent.width * 0.5f + (big ? 2.2f : 0.9f);
        foreach (var o in GummyBody.All)
        {
            if (!o || o == body || o.IsMonster || !o.isActiveAndEnabled || !o.Grounded) continue;
            if (Mathf.Abs(o.Center.x - body.Center.x) > reach || Mathf.Abs(o.Extent.yMin - feet.y) > 0.4f) continue;
            var ob = o.GetComponent<GummyBrain>();
            if (ob) ob.Dodge(Mathf.Sign(o.Center.x - body.Center.x), 1.2f, 3.4f * Mathf.Sqrt(o.size / 0.6f));
        }
    }

    // ------------------------------------------------------------------ chasing for fun

    void StartChase(float t)
    {
        nextChase = t + Random.Range(18f, 35f);
        GummyBody best = null;
        float bd = 7f;
        float feet = body.Extent.yMin;
        foreach (var o in GummyBody.All)
        {
            var of = o ? o.GetComponent<BuddyFusion>() : null;
            if (!o || o.IsMonster || !o.isActiveAndEnabled || !o.Grounded || !of || of.Current == BuddyFusion.Mode.Mama) continue;
            if (Mathf.Abs(o.Extent.yMin - feet) > 0.6f) continue; // (same floor: it doesn't climb)
            float d = Mathf.Abs(o.Center.x - body.Center.x);
            if (d < bd) { bd = d; best = o; }
        }
        if (!best) return;
        chasing = best;
        chaseUntil = t + Random.Range(3.5f, 5f);
        Chases++;
        brain.Hurry = 1.35f;
        var sp = best.GetComponent<BuddySpeech>();
        Say(Pick("COME HERE, LITTLE " + (sp ? sp.Name : "ONE") + "!", "HUG TIME!! RAAAWR", "TAG! YOU'RE IT!", "WE ONLY WANT TO PLAY!"), true);
    }

    void PoseFor(Vector2 p, float seconds)
    {
        pose = p;
        poseUntil = Time.time + seconds;
    }

    void UpdateChase(float t)
    {
        GummyBody o = chasing;
        float gap = o ? Mathf.Abs(o.Center.x - body.Center.x) - (o.Extent.width + body.Extent.width) * 0.5f : 99f;
        var of = o ? o.GetComponent<BuddyFusion>() : null;
        bool cornered = of && of.Current == BuddyFusion.Mode.Cower;
        if (!o || !o.isActiveAndEnabled || gap < 0.5f || cornered || t >= chaseUntil || body.Held || Mathf.Abs(o.Extent.yMin - body.Extent.yMin) > 1f || (eater && eater.Target))
        {
            chaseUntil = 0f;
            chasing = null;
            brain.Hurry = 1f;
            brain.Food = null;
            if (Random.value < 0.6f) Say(Pick("HEHE. SCARED YOU", "...WE JUST WANTED A HUG", "TOO FAST!", "PHEW. MONSTERING IS TIRING"), false);
            // (and it lumbers off the other way: a cornered one gets out)
            if (o && (cornered || gap < 1f))
            {
                Rect r = ScreenBounds.WorldRect;
                float away = body.Center.x + Mathf.Sign(body.Center.x - o.Center.x + 0.001f) * (3f + body.size);
                awayX = Mathf.Clamp(away, r.xMin + body.size, r.xMax - body.size);
                awayUntil = t + 4f;
            }
            return;
        }
        brain.Food = new Vector2(o.Center.x, o.Extent.yMin + body.size * 0.3f);
        brain.FoodSurface = o.Extent.yMin;
    }

    // ------------------------------------------------------------------ coming apart

    // the friend nearest your grip (each friend is a patch; the one the others went into is the monster itself)
    int Nearest(Vector2 grip)
    {
        int best = members.Count - 1;
        float bd = float.MaxValue;
        for (int i = 1; i < members.Count; i++)
        {
            Vector4 pq = body.PatchNow(i - 1);
            if (pq.z <= 0f) continue;
            float d = (new Vector2(pq.x, pq.y) - grip).magnitude - pq.z;
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }

    void Peel()
    {
        Vector2 at = body.HeldAt;
        if (pick < 1 || pick >= members.Count) pick = Nearest(body.GrabPoint);
        GummyBody outB = members[pick];
        int share = xpPool / members.Count;
        xpPool -= share;
        members.RemoveAt(pick);
        body.EndGrab();
        peelAfter = Time.time + 0.4f;
        Splits++;
        Play(splitClip, Random.Range(0.95f, 1.1f));
        if (members.Count < 2) { Dissolve(body.Center, outB, at, share); return; }
        look.Become(Looks(members));
        if (brain) brain.NoDizzyUntil = Time.time + 2f;
        PoseFor(new Vector2(1.16f, 0.86f), 0.25f);
        Release(outB, at, true, share);
        Save();
        if (Random.value < 0.7f) Say(Pick("HEY! GIVE THAT BACK!", "OW! THAT WAS OUR " + Kind(outB) + "!", "FINE. SMALLER BUT SCARIER", "RAAWR... (SMALLER)"), true);
    }

    /// <summary>Every monster bursts back into its buddies at once (WhipIt > Buddy > Split The Monsters).</summary>
    public static void SplitAll()
    {
        foreach (var m in new List<BuddyMonster>(s_all)) if (m) m.Burst();
    }

    void Burst()
    {
        Vector2 c = body.Center;
        int n = members.Count, share = n > 0 ? xpPool / n : 0;
        var list = new List<GummyBody>(members);
        members.Clear();
        s_all.Remove(this);
        Play(splitClip, 0.9f);
        for (int i = 0; i < list.Count; i++)
        {
            float u = n > 1 ? i / (float)(n - 1) - 0.5f : 0f;
            Release(list[i], c + new Vector2(u * body.Extent.width, 0.1f), false, share);
        }
        if (body.Held) { Grabber.Handoff(body, null); body.EndGrab(); }
        Save();
        gameObject.SetActive(false);
        Destroy(gameObject);
    }

    // the last two pop apart: the monster's gone
    void Dissolve(Vector2 at, GummyBody pulled, Vector2 pulledAt, int share = 0)
    {
        GummyBody last = members.Count > 0 ? members[0] : null;
        int rest = xpPool;
        members.Clear();
        s_all.Remove(this);
        if (last) Release(last, at, false, rest);
        if (pulled) Release(pulled, pulledAt, true, share);
        Grabber.Handoff(body, null); // (if you were still holding the monster itself)
        Save();
        gameObject.SetActive(false);
        Destroy(gameObject);
    }

    // a friend back out (held by you if you pulled it out), dizzy, with its share of what the monster earned
    void Release(GummyBody b, Vector2 at, bool held, int xp)
    {
        b.gameObject.SetActive(true);
        Rect r = ScreenBounds.ScreenRectAt(at); // (on a screen)
        float half = b.size * 0.5f;
        at.x = Mathf.Clamp(at.x, r.xMin + half, r.xMax - half);
        at.y = Mathf.Clamp(at.y, r.yMin + half, r.yMax - half);
        b.Teleport(at);
        if (held)
        {
            Grabber.Handoff(body, b);
            b.BeginGrab(at);
        }
        var br = b.GetComponent<GummyBrain>();
        if (br) { br.Food = null; br.StayPut = false; br.Hurry = 1f; br.MakeDizzy(2.2f); }
        var pr = b.GetComponent<BuddyProgress>();
        if (pr && xp > 0) pr.AddXp(xp, b.Center);
        var sp = b.GetComponent<BuddySpeech>();
        if (sp && Random.value < 0.8f) sp.Say(Pick("@_@ I'M ME AGAIN!", "WHOA. ONE BODY", "THAT WAS WEIRD", "I SAW EVERYTHING. TWICE", "FREE!"), 0f, true);
    }

    // ------------------------------------------------------------------ remembering (monsters.txt: xp|save,save,...)

    public static string FilePath => Path.Combine(Application.persistentDataPath, "monsters.txt");
    static bool s_restoring;

    static void Save()
    {
        if (s_restoring) return;
        try
        {
            var sb = new System.Text.StringBuilder();
            foreach (var m in s_all)
            {
                if (!m || m.members.Count < 2) continue;
                sb.Append(m.xpPool).Append('|');
                for (int i = 0; i < m.members.Count; i++) { if (i > 0) sb.Append(','); sb.Append(SaveName(m.members[i])); }
                sb.Append('\n');
            }
            if (sb.Length == 0) { if (File.Exists(FilePath)) File.Delete(FilePath); }
            else File.WriteAllText(FilePath, sb.ToString());
        }
        catch (System.Exception e) { Debug.LogWarning("[WhipIt] Couldn't save the monsters: " + e.Message); }
    }

    static string SaveName(GummyBody b)
    {
        var p = b.GetComponent<BuddyProgress>();
        return p && !string.IsNullOrEmpty(p.saveName) ? p.saveName : b.name.ToLowerInvariant().Replace(' ', '_');
    }

    /// <summary>The monsters there were when the app closed, back as they were (the buddies are all there by now).</summary>
    public static void Restore()
    {
        if (s_all.Count > 0 || !File.Exists(FilePath)) return;
        string[] lines;
        try { lines = File.ReadAllLines(FilePath); }
        catch { return; }
        s_restoring = true;
        try
        {
            foreach (string line in lines)
            {
                int bar = line.IndexOf('|');
                if (bar < 0) continue;
                int.TryParse(line.Substring(0, bar), out int xp);
                var list = new List<GummyBody>();
                foreach (string nm in line.Substring(bar + 1).Split(','))
                    foreach (var b in GummyBody.All)
                        if (b && !b.IsMonster && b.isActiveAndEnabled && SaveName(b) == nm.Trim() && !list.Contains(b) && !IsInside(b)) { list.Add(b); break; }
                if (list.Count >= 2) Create(list, list[0].Center, xp, true);
            }
        }
        finally { s_restoring = false; }
        Save();
    }

    // ------------------------------------------------------------------ helpers

    void Say(string line, bool force = false)
    {
        if (speech) speech.Say(line, 0f, force);
    }

    void Play(AudioClip clip, float pitch, float vol = 1f)
    {
        if (!clip || !sfx) return;
        sfx.pitch = pitch;
        sfx.PlayOneShot(clip, volume * vol);
    }

    static string Kind(GummyBody b)
    {
        var sp = b ? b.GetComponent<BuddySpeech>() : null;
        return sp ? sp.Name : "FRIEND";
    }

    static string Pick(params string[] l) => l[Random.Range(0, l.Length)];
}
