using UnityEngine;

/// <summary>
/// The duckling (apple level 25, BuddyFriends): a lost little duck waddles in from the edge of the screen going "QUACK?",
/// finds the apple and decides she's its mama ("MAMA?" "...MAMA?!"). The gummy body is its round fluffy self; this adds
/// what makes it a duck, drawn in code and riding the soft body: an orange bill (towards where it's going), a little tuft
/// on its head, stubby wings that flap when it's happy or in the air, orange webbed feet and a perky tail. It follows
/// its mama around: when she goes off somewhere it hops after her and stops right behind her, in a line. Snacks and the
/// toys still come first.
/// </summary>
[DefaultExecutionOrder(5)] // after BuddyEater has set (or cleared) its snack: mama only when there's no snack
[RequireComponent(typeof(GummyBody))]
[DisallowMultipleComponent]
public class BuddyDuck : MonoBehaviour
{
    [Tooltip("Sprite material (WhipIt > Setup assigns it).")]
    public Material material;
    [Tooltip("Its mama: the apple (WhipIt > Setup assigns it).")]
    public GummyBody mama;
    [Tooltip("When mama goes off somewhere: how often it follows her (else it potters about on its own for a bit).")]
    [Range(0f, 1f)] public float followChance = 0.85f;

    /// <summary>Hopping after its mama (or sitting right behind her).</summary>
    public bool Following => following;
    /// <summary>Still waddling in / meeting its mama for the first time.</summary>
    public bool Arriving => phase != Phase.None;
    /// <summary>Times it went after its mama, times it caught up (tests).</summary>
    public int Follows { get; private set; }
    public int CaughtUp { get; private set; }
    /// <summary>Which way it faces (-1 left .. 1 right).</summary>
    public float Facing => facing;
    /// <summary>Times it hopped over its mama to get out of her way (tests).</summary>
    public int HopsOver { get; private set; }

    enum Phase { None, Waddle, ToMama, Meet }

    GummyBody body;
    GummyBrain brain;
    BuddySpeech speech;
    BuddyEater eater;
    SpriteRenderer bill, tuft, wingL, wingR, footL, footR, tail;
    Sprite tuftSprite, wingSprite, tailSprite;
    Color bakedBody, bakedRim;
    bool baked, following, wasClose, overHop, bumps = true, hurried;
    float overStart, overUntil, overX, nextOver;
    int planFails;
    float facing = 1f, nextLook, restSince = -1f, giveUpUntil = -1f, nextPlan, lastLine = -100f, phaseT, wadDir = 1f, flapT, lastMamaX, wadCheck, wadX;
    Phase phase;
    Vector2 target;
    float targetY;
    static Sprite s_bill, s_foot;
    static bool s_billTried, s_footTried; // (baked once a session; a sprite that couldn't be made isn't tried every frame)

    float S => body.size / 0.6f;

    void Awake()
    {
        body = GetComponent<GummyBody>();
        brain = GetComponent<GummyBrain>();
        speech = GetComponent<BuddySpeech>();
        eater = GetComponent<BuddyEater>();
        int o = body.sortingOrder;
        tuft = MakeRenderer("Duck Tuft", o - 1);   // behind the body: it pokes out of the top of its head
        tail = MakeRenderer("Duck Tail", o - 1);
        wingL = MakeRenderer("Duck Wing L", o - 1); // little wings peeking out at its sides
        wingR = MakeRenderer("Duck Wing R", o - 1);
        footL = MakeRenderer("Duck Foot L", o + 1);
        footR = MakeRenderer("Duck Foot R", o + 1);
        bill = MakeRenderer("Duck Bill", o + 3);     // over its face (where the mouth would be)
    }

    SpriteRenderer MakeRenderer(string name, int order)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false); // (a child: hiding behind a window masks it too)
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sortingOrder = order;
        sr.color = Color.white;
        Material m = material ? material : body.spriteMaterial;
        if (m) sr.sharedMaterial = m;
        sr.enabled = false;
        return sr;
    }

    void OnDisable()
    {
        EndOver();
        Unhurry();
        foreach (var sr in new[] { bill, tuft, wingL, wingR, footL, footR, tail }) if (sr) sr.enabled = false;
        if (following && brain) brain.Food = null;
        following = false;
        if (phase == Phase.Waddle && brain && brain.Towering) brain.TowerEnd();
        if (phase == Phase.Waddle && body) body.HoldRoom = false;
        phase = Phase.None;
    }

    void OnDestroy()
    {
        foreach (var sp in new[] { tuftSprite, wingSprite, tailSprite }) if (sp) { Destroy(sp.texture); Destroy(sp); }
    }

    // ------------------------------------------------------------------ arriving

    /// <summary>
    /// It's just been let in at the edge of the screen (side: +1 = it comes in from the left, walking right; -1 from the
    /// right; 0 = it's already in, beside its mama): it waddles in, finds its mama and meets her.
    /// </summary>
    public void Arrive(GummyBody mamaBody, int side)
    {
        if (mamaBody) mama = mamaBody;
        phaseT = 0f;
        wadCheck = 0f;
        wadDir = side >= 0 ? 1f : -1f;
        facing = wadDir;
        phase = side != 0 ? Phase.Waddle : Phase.Meet;
        if (phase == Phase.Waddle && brain && !brain.TowerStart()) phase = Phase.ToMama;
    }

    void Arriving_(float now, float dt)
    {
        phaseT += dt;
        Rect r = ScreenBounds.WorldRect;
        switch (phase)
        {
            case Phase.Waddle:
            {
                // in from off the screen along the floor, a waddle in every step: all the way over to its mama if she's down
                // on the floor too (else just in, then it hops over); something in the way: it hops the rest
                if (phaseT > 0.4f && phaseT - dt <= 0.4f) Say("QUACK?", true);
                float fl = ScreenBounds.FloorNear(Mathf.Clamp(body.Center.x, r.xMin + 0.06f, r.xMax - 0.06f), body.Extent.yMin); // (the floor it's on)
                bool mamaDown = mama && mama.Grounded && mama.Extent.yMin - mama.SkateLift < fl + 0.3f;
                float inside = wadDir > 0 ? r.xMin + (mamaDown ? 9f : 1.6f) : r.xMax - (mamaDown ? 9f : 1.6f);
                if (phaseT >= wadCheck + 1f) { if (wadCheck > 0f && Mathf.Abs(body.Center.x - wadX) < 0.12f) inside = body.Center.x; wadCheck = phaseT; wadX = body.Center.x; }
                float y = body.Extent.yMin;
                float cOff = Mathf.Max(body.Center.y - body.Extent.yMin, body.size * 0.3f);
                Vector2 at = new Vector2(body.Center.x + wadDir * 0.5f, Mathf.Max(fl, y) + cOff);
                Vector2 v = new Vector2(wadDir * 1.1f * Mathf.Sqrt(S), (at.y - body.Center.y) * 8f);
                body.Glide(v, 30f * S);
                float w = 0.06f * Mathf.Sin(phaseT * 12f);
                body.Pose = new Vector2(1f + w, 1f - w);
                body.Lean = 6f * Mathf.Sin(phaseT * 6f);
                facing = wadDir;
                bool there = mamaDown && Near(1.2f) && body.Extent.xMin > r.xMin + 0.05f && body.Extent.xMax < r.xMax - 0.05f;
                if (there || (wadDir > 0 ? body.Extent.xMin > r.xMin + 0.05f && body.Center.x >= inside : body.Extent.xMax < r.xMax - 0.05f && body.Center.x <= inside) || phaseT > 14f || !brain.Towering)
                {
                    if (brain.Towering) brain.TowerEnd();
                    body.Pose = Vector2.one;
                    body.HoldRoom = false; // (the room beyond the edge shrinks away behind it)
                    phase = there ? Phase.Meet : Phase.ToMama;
                    phaseT = 0f;
                }
                return;
            }
            case Phase.ToMama:
                // hops over to her (snacks can wait); close enough, or it can't get there: "MAMA?"
                if (!mama || !mama.isActiveAndEnabled) { phase = Phase.None; return; }
                if (Near(1.5f) || phaseT > 25f || (phaseT > 1f && !GoTo(now))) { brain.Food = null; phase = Phase.Meet; phaseT = 0f; }
                return;
            case Phase.Meet:
            {
                brain.Food = null;
                brain.Linger(2f);
                var ms = mama ? mama.GetComponent<BuddySpeech>() : null;
                var mb = mama ? mama.GetComponent<GummyBrain>() : null;
                if (mb) mb.Linger(2f);
                if (mama) facing = mama.Center.x >= body.Center.x ? 1f : -1f;
                if (Step(phaseT, dt, 0.3f)) Say("MAMA?", true);
                if (Step(phaseT, dt, 1.6f) && ms) ms.Say("MAMA?!", 0f, true);
                if (Step(phaseT, dt, 2.9f))
                {
                    Say("MAMA!!", true);
                    if (body.Grounded) body.Hop(new Vector2(0f, GummyBody.JumpSpeed(0.35f * S, body.gravity, body.Drag)), 0.4f);
                    if (brain) brain.Treat(); // (a heart, a happy face)
                    flapT = 1.2f;
                }
                if (Step(phaseT, dt, 4.3f) && ms) ms.Say("I'M A MOM?!", 0f, true);
                if (Step(phaseT, dt, 5.6f) && mb) mb.Treat();
                if (phaseT > 6.2f) { phase = Phase.None; following = true; Follows++; restSince = -1f; }
                return;
            }
        }
    }

    static bool Step(float t, float dt, float at) => t >= at && t - dt < at;

    bool Near(float dist)
    {
        if (!mama) return false;
        float mFeet = mama.Extent.yMin - mama.SkateLift, myFeet = body.Extent.yMin - body.SkateLift;
        return Mathf.Abs(mFeet - myFeet) < 0.3f && Mathf.Abs(body.Center.x - mama.Center.x) < (mama.Extent.width + body.Extent.width) * 0.5f + dist * 0.5f;
    }

    // ------------------------------------------------------------------ following mama

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("BuddyDuck.Update", t0); }
    void UpdateTimed()
    {
        float now = Time.time, dt = Mathf.Min(Time.deltaTime, 0.05f);
        UpdateFacing(dt);
        if (!brain || body.Held) return;
        if (phase != Phase.None) { Arriving_(now, dt); return; }
        if (!mama || !mama.isActiveAndEnabled || !mama.gameObject.activeInHierarchy) { StopFollowing(); EndOver(); return; }
        if (overHop) { UpdateOver(now); return; }
        if (InMamasWay(now)) { HopOver(now); return; }                          // never in her way: over her head it goes
        if (eater && eater.Target) { following = false; Unhurry(); return; }   // snacks first (BuddyEater has its Food)
        if (BusyElsewhere()) { StopFollowing(); return; }                       // a toy, a game, a tower, the party...
        if (brain.Current == GummyBrain.State.Asleep) { following = false; Unhurry(); return; }

        bool close = Near(0.9f);
        if (!following)
        {
            // mama's gone off somewhere: after her (most of the time)
            if (close || now < giveUpUntil || now < nextLook || !brain.IsFree) return;
            nextLook = now + 1f;
            if (Random.value > followChance) { nextLook = now + 6f; return; } // (it potters about on its own a bit)
            following = true;
            Follows++;
            restSince = -1f;
            nextPlan = 0f;
        }
        // she's flying, held or bouncing about: wait for her to land
        if (mama.Held || !mama.Grounded) { brain.Food = null; return; }
        if (close)
        {
            // right behind her: sits, faces her; once she's settled for a while it's free to do its own thing
            brain.Food = null;
            Unhurry();
            if (!wasClose) { CaughtUp++; if (Random.value < 0.15f) Say(Pick("MAMA!", "QUACK!", "HI MAMA"), false); if (Random.value < 0.06f) { var ms = mama.GetComponent<BuddySpeech>(); if (ms) ms.Say(Pick("MY BABY!", "GOOD DUCK", "STAY CLOSE!"), 0f, false); } }
            wasClose = true;
            if (Mathf.Abs(mama.Center.x - lastMamaX) > 0.05f) restSince = now;
            lastMamaX = mama.Center.x;
            if (restSince < 0f) restSince = now;
            if (now - restSince > 6f) { following = false; nextLook = now + 2f; }
            return;
        }
        wasClose = false;
        if (!GoTo(now))
        {
            // can't get to her (she's up somewhere it can't reach yet): later
            StopFollowing();
            giveUpUntil = now + 12f;
            if (mama.Extent.yMin - mama.SkateLift > body.Extent.yMin + 0.5f && Random.value < 0.4f) Say(Pick("MAMA! WAIT!", "QUACK! WAIT UP!", "TOO HIGH, MAMA"), false);
        }
    }

    // ------------------------------------------------------------------ out of mama's way

    // she's coming its way and it's right in front of her, on the same floor
    bool InMamasWay(float now)
    {
        if (now < nextOver || !body.Grounded || body.Held || body.IsStuck || !mama.Grounded || mama.Held || BusyElsewhere()) return false;
        float heading = MamaHeading();
        if (heading == 0f || Mathf.Sign(body.Center.x - mama.Center.x) != heading) return false;
        float gap = heading > 0f ? body.Extent.xMin - mama.Extent.xMax : mama.Extent.xMin - body.Extent.xMax;
        if (gap > 0.25f + 0.2f * S) return false;
        float mFeet = mama.Extent.yMin - mama.SkateLift, myFeet = body.Extent.yMin - body.SkateLift;
        return Mathf.Abs(mFeet - myFeet) < 0.25f;
    }

    // which way she's going: how she moves, else where her brain wants to go (stuck behind it, she isn't moving)
    float MamaHeading()
    {
        float vx = mama.Velocity.x;
        if (Mathf.Abs(vx) > 0.5f) return Mathf.Sign(vx);
        var mb = mama.GetComponent<GummyBrain>();
        if (mb && mb.Food.HasValue && Mathf.Abs(mb.Food.Value.x - mama.Center.x) > 0.6f) return Mathf.Sign(mb.Food.Value.x - mama.Center.x);
        return 0f;
    }

    // a little hop right over her head (passing in front of her) to land just behind her
    void HopOver(float now)
    {
        Rect r = ScreenBounds.WorldRect;
        float heading = MamaHeading();
        float half = body.Extent.width * 0.5f;
        float land = mama.Center.x - heading * (mama.Extent.width * 0.5f + half + 0.2f);
        float h = mama.Extent.yMax - body.Extent.yMin + 0.12f;
        if (land < r.xMin + half + 0.05f || land > r.xMax - half - 0.05f || h > 3f) { nextOver = now + 2f; return; } // (no room: she'll manage)
        float g = body.gravity;
        float vy = GummyBody.JumpSpeed(h, g, body.Drag);
        float T = vy / g + Mathf.Sqrt(2f * h / g);
        float vx = (land - body.Center.x) / T * 1.06f;
        body.Hop(new Vector2(vx, vy) - body.Velocity, 0.5f);
        bumps = body.collideBuddies;
        body.collideBuddies = false; // (through her, not into her)
        overHop = true;
        overStart = now;
        overUntil = now + T + 0.8f;
        overX = land;
        flapT = T;
        brain.Food = null;
        HopsOver++;
        if (Random.value < 0.25f) Say(Pick("HOP!", "QUACK!", "SORRY MAMA!"), false);
    }

    void UpdateOver(float now)
    {
        brain.Food = null;
        bool landed = body.Grounded && now - overStart > 0.2f;
        if ((landed && Unblocked()) || now > overUntil) { EndOver(); nextOver = now + 1f; }
    }

    // not inside anyone any more (else it stays a ghost a moment longer)
    bool Unblocked()
    {
        foreach (var o in GummyBody.All)
        {
            if (!o || o == body || !o.isActiveAndEnabled) continue;
            Rect a = o.Extent, b = body.Extent;
            if (a.xMin < b.xMax && a.xMax > b.xMin && a.yMin < b.yMax && a.yMax > b.yMin) return false;
        }
        return true;
    }

    void EndOver()
    {
        if (!overHop) return;
        overHop = false;
        if (body) body.collideBuddies = bumps;
    }

    // the spot right behind mama on what she stands on (behind = away from where she's going); false = it can't get there
    bool GoTo(float now)
    {
        Rect r = ScreenBounds.WorldRect;
        float gap = (mama.Extent.width + body.Extent.width) * 0.5f + 0.12f;
        float side = Mathf.Abs(mama.Velocity.x) > 0.4f ? -Mathf.Sign(mama.Velocity.x) : (body.Center.x >= mama.Center.x ? 1f : -1f);
        float mFeet = mama.Extent.yMin - mama.SkateLift;
        float tx = mama.Center.x + side * gap;
        if (tx < r.xMin + 0.3f || tx > r.xMax - 0.3f) tx = mama.Center.x - side * gap; // (no room that side: the other)
        target = new Vector2(tx, mFeet + body.size * 0.3f);
        targetY = mFeet;
        // (mid-hop, or getting up after a landing, it can't plan: it keeps going; twice no way there: it can't)
        bool settled = body.Grounded && !body.Held && !body.IsStuck && (brain.Current == GummyBrain.State.Idle || brain.Current == GummyBrain.State.Hopping);
        if (now >= nextPlan && settled)
        {
            nextPlan = now + 1f;
            if (brain.PlanTo(target, targetY, out _)) planFails = 0;
            else if (++planFails >= 2) { planFails = 0; return false; }
        }
        brain.Food = target;
        brain.FoodSurface = targetY;
        // far behind: it hurries (quick little hops), close: its own pace
        float far = Mathf.Abs(tx - body.Center.x);
        if (far > 2f) { brain.Hurry = 1.6f; hurried = true; }
        else if (hurried && far < 1f) Unhurry();
        return true;
    }

    void Unhurry()
    {
        if (hurried && brain) brain.Hurry = 1f;
        hurried = false;
    }

    void StopFollowing()
    {
        Unhurry();
        if (following && brain) brain.Food = null;
        following = false;
        wasClose = false;
    }

    bool BusyElsewhere()
    {
        var h = GetComponent<BuddyHoops>();
        var p = GetComponent<BuddyPit>();
        var s = GetComponent<BuddySlide>();
        var l = GetComponent<BuddyLaser>();
        var rn = GetComponent<BuddyRain>();
        return (h && (h.Busy || h.Playing)) || (p && p.Busy) || (s && s.Busy) || (l && l.Playing) || (rn && rn.Busy) || (Rain.Current && Rain.Current.Raining)
            || BuddyTower.Has(body) || LevelUpParty.Current || BuddyHideSeek.Playing
            || brain.Towering || brain.InPit || FetchBall.Current && FetchBall.Current.InPlay;
    }

    void Say(string line, bool force)
    {
        if (!speech) return;
        if (!force && Time.time - lastLine < 20f) return;
        if (speech.Say(line, 0f, force)) lastLine = Time.time;
    }

    static string Pick(params string[] l) => l[Random.Range(0, l.Length)];

    // which way it faces: where it's going, else at its mama when she's close, else towards you
    void UpdateFacing(float dt)
    {
        Vector2 v = body.Velocity, cur = Grabber.CursorWorld;
        float want = facing;
        if (Mathf.Abs(v.x) > 0.6f && !body.Held) want = Mathf.Sign(v.x);
        else if (following && mama && Mathf.Abs(mama.Center.x - body.Center.x) > 0.2f) want = Mathf.Sign(mama.Center.x - body.Center.x);
        else if (!body.Held && (cur - body.Center).magnitude < 4f && Mathf.Abs(cur.x - body.Center.x) > 0.5f * S) want = Mathf.Sign(cur.x - body.Center.x);
        want = want >= 0f ? 1f : -1f;
        if (phase == Phase.Waddle) want = wadDir;
        facing = Mathf.MoveTowards(facing, want, dt * 4.5f);
    }

    // ------------------------------------------------------------------ its bill, tuft, wings, feet, tail

    void LateUpdate() { long t0 = WhipProf.T(); LateUpdateTimed(); WhipProf.Add("BuddyDuck.LateUpdate", t0); }
    void LateUpdateTimed()
    {
        if (!s_bill && !s_billTried) { s_bill = BakeSprite(BillW, BillH, new Vector2(0.5f, 0.5f), "Duck Bill", (x, y, tx) => BillPaint(x, y, tx, new Color(1f, 0.62f, 0.12f))); s_billTried = !s_bill; }
        if (!s_foot && !s_footTried) { s_foot = BakeSprite(FootW, FootH, new Vector2(0.4f, 0.8f), "Duck Foot", (x, y, tx) => FootPaint(x, y, tx, new Color(1f, 0.55f, 0.1f))); s_footTried = !s_foot; }
        if (!baked || !Same(body.color, bakedBody) || !Same(body.rimColor, bakedRim))
        {
            foreach (var sp in new[] { tuftSprite, wingSprite, tailSprite }) if (sp) { Destroy(sp.texture); Destroy(sp); }
            Color fill = body.color, edge = Color.Lerp(body.color, body.rimColor, 0.75f);
            tuftSprite = BakeSprite(TuftW, TuftH, new Vector2(0.5f, 0.1f), "Duck Tuft", (x, y, tx) => TuftPaint(x, y, tx, fill, edge));
            wingSprite = BakeSprite(WingW, WingH, new Vector2(0.2f, 0.85f), "Duck Wing", (x, y, tx) => WingPaint(x, y, tx, fill, edge));
            tailSprite = BakeSprite(TailW, TailH, new Vector2(0.1f, 0.3f), "Duck Tail", (x, y, tx) => TailPaint(x, y, tx, fill, edge));
            bakedBody = body.color; bakedRim = body.rimColor; baked = true;
        }
        float s = body.size, f = facing, t = Time.time, dt = Time.deltaTime;
        float ang = body.Angle;
        var rot = Quaternion.Euler(0f, 0f, ang);
        Vector2 up = rot * Vector2.up, right = rot * Vector2.right;
        Vector2 span = body.Span;
        float w = Mathf.Max(span.x, s * 0.5f), h = Mathf.Max(span.y, s * 0.5f);
        bool happy = brain && brain.Mood == CuteFace.Expression.Happy;
        bool air = !body.Grounded && !body.Held;
        flapT = Mathf.Max(0f, flapT - dt);
        float flap = air || flapT > 0f ? Mathf.Sin(t * 22f) * 30f : happy ? Mathf.Sin(t * 12f) * 14f : Mathf.Sin(t * 2.3f) * 4f;
        float fs = Mathf.Sign(f == 0f ? 1f : f), fa = Mathf.Lerp(0.55f, 1f, Mathf.Abs(f));

        // the bill: where its mouth would be (just under its eyes), nudged a little towards where it faces
        {
            Vector2 p = body.Center + right * (0.06f * f * w) + up * (-0.13f * h);
            Place(bill, s_bill, p, ang, new Vector2(fs * Mathf.Lerp(0.8f, 1f, Mathf.Abs(f)) * 0.4f * s, 0.4f * s));
        }
        // the tuft: a few curly feathers out of the top of its head
        Place(tuft, tuftSprite, body.TopPoint - up * (0.03f * s), ang + 6f * Mathf.Sin(t * 3f) - 8f * f, new Vector2(0.24f * s * fs, 0.24f * s));
        // the wings: stubby, at its sides a bit below the middle (they flap in the air and when it's happy)
        for (int side = -1; side <= 1; side += 2)
        {
            var sr = side < 0 ? wingL : wingR;
            Vector2 p = body.OutlinePoint(-side * 92f, out Vector2 n);
            p -= n * (0.09f * s);
            Place(sr, wingSprite, p, ang + side * (10f - flap), new Vector2(side * 0.3f * s, 0.3f * s));
        }
        // webbed feet at its bottom, toes towards where it faces
        for (int side = -1; side <= 1; side += 2)
        {
            var sr = side < 0 ? footL : footR;
            Vector2 p = body.OutlinePoint(180f + 25f * side, out _);
            p += right * (0.03f * f * w) + up * (0.02f * s);
            float toes = Mathf.Abs(f) > 0.3f ? Mathf.Sign(f) : side;
            Place(sr, s_foot, p, ang, new Vector2(toes * 0.24f * s, 0.24f * s));
        }
        // the tail: a perky little point at its lower back, away from where it faces (it wiggles)
        {
            Vector2 p = body.OutlinePoint(180f - 52f * f, out Vector2 n);
            p -= n * (0.05f * s);
            float wig = Mathf.Sin(t * (happy ? 14f : 4f)) * (happy ? 14f : 5f);
            Place(tail, tailSprite, p, ang + wig, new Vector2(-fs * 0.24f * s, 0.24f * s));
        }
    }

    static void Place(SpriteRenderer sr, Sprite sp, Vector2 at, float deg, Vector2 scale)
    {
        sr.sprite = sp;
        sr.enabled = true;
        sr.transform.position = new Vector3(at.x, at.y, 0f);
        sr.transform.rotation = Quaternion.Euler(0f, 0f, deg);
        sr.transform.localScale = new Vector3(scale.x, scale.y, 1f);
    }

    static bool Same(Color a, Color b) => Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) < 1e-3f;

    // ------------------------------------------------------------------ drawing (the bill and feet once, orange; the
    //                                                                     tuft, wings and tail in its own colours)

    const int BillW = 64, BillH = 40, FootW = 64, FootH = 32, TuftW = 48, TuftH = 48, WingW = 48, WingH = 48, TailW = 48, TailH = 40;

    static Sprite BakeSprite(int w, int h, Vector2 pivot, string name, System.Func<float, float, float, Color> paint)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = name };
        var px = new Color32[w * h];
        float texel = 1f / w;
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                Color c = paint((i + 0.5f) * texel, (j + 0.5f) * texel, texel); // x 0..1, y 0..h/w (same texel size)
                px[j * w + i] = new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), Mathf.Clamp01(c.a));
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return Sprite.Create(tex, new Rect(0, 0, w, h), pivot, w, 0, SpriteMeshType.FullRect); // 1 unit wide
    }

    static Color Clear => new Color(0f, 0f, 0f, 0f);
    static float Cover(float d, float texel) => Mathf.Clamp01(0.5f - d / texel);
    static float Ellipse(float x, float y, float cx, float cy, float rx, float ry) => (Mathf.Sqrt((x - cx) * (x - cx) / (rx * rx) + (y - cy) * (y - cy) / (ry * ry)) - 1f) * Mathf.Min(rx, ry);

    /// <summary>The bill, seen from the front (x 0..1, y 0..0.625; pivot in the middle): a wide rounded upper bill over a
    /// smaller lower one (a smiley line between them), two nostrils, a shine, a darker outline.</summary>
    internal static Color BillPaint(float x, float y, float texel, Color orange)
    {
        float top = Ellipse(x, y, 0.5f, 0.36f, 0.46f, 0.19f);
        float low = Ellipse(x, y, 0.5f, 0.22f, 0.32f, 0.13f);
        float d = Mathf.Min(top, low);
        float a = Cover(d, texel);
        if (a <= 0f) return Clear;
        Color edge = new Color(0.72f, 0.33f, 0.04f);
        Color c = top < low ? orange * (0.95f + 0.15f * Mathf.Clamp01((y - 0.3f) / 0.2f)) : orange * 0.82f;
        if (Mathf.Abs(top - low) < texel * 1.6f && y < 0.33f) c = edge;                                        // the mouth line
        if (Ellipse(x, y, 0.41f, 0.41f, 0.035f, 0.025f) < 0f || Ellipse(x, y, 0.59f, 0.41f, 0.035f, 0.025f) < 0f) c = edge; // nostrils
        if (Ellipse(x, y, 0.3f, 0.45f, 0.12f, 0.04f) < 0f) c = Color.Lerp(c, Color.white, 0.5f);                // shine
        if (d > -texel * 2f) c = Color.Lerp(c, edge, 0.85f);
        c.a = a;
        return c;
    }

    /// <summary>A webbed foot (x 0..1, y 0..0.5; the pivot at its heel, top): three rounded toes joined by webbing.</summary>
    internal static Color FootPaint(float x, float y, float texel, Color orange)
    {
        // a fan from the heel (0.4, 0.4) out to three toe tips along the bottom
        float d = 9f;
        foreach (float tx in new[] { 0.62f, 0.8f, 0.95f })
        {
            float seg = SegD(new Vector2(x, y), new Vector2(0.38f, 0.36f), new Vector2(tx, 0.08f)) - 0.07f;
            d = Mathf.Min(d, seg);
        }
        float web = Mathf.Max(Ellipse(x, y, 0.66f, 0.2f, 0.33f, 0.16f), -(y - 0.06f));
        d = Mathf.Min(d, web);
        d = Mathf.Min(d, Ellipse(x, y, 0.38f, 0.36f, 0.12f, 0.1f));
        float a = Cover(d, texel);
        if (a <= 0f) return Clear;
        Color edge = new Color(0.72f, 0.33f, 0.04f);
        Color c = orange * (0.9f + 0.15f * y * 2f);
        if (d > -texel * 2f) c = Color.Lerp(c, edge, 0.85f);
        c.a = a;
        return c;
    }

    static float SegD(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float k = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Vector2.Dot(ab, ab));
        return (p - a - ab * k).magnitude;
    }

    /// <summary>The tuft (x 0..1, y 0..1; pivot at the bottom middle): three soft curls of feather, the middle one tallest.</summary>
    internal static Color TuftPaint(float x, float y, float texel, Color fill, Color edge)
    {
        float d = 9f;
        for (int k = -1; k <= 1; k++)
        {
            float bx = 0.5f + k * 0.16f, tall = k == 0 ? 0.78f : 0.55f, lean = k * 0.12f;
            // a teardrop leaning out, curling at the tip
            float v = Mathf.Clamp01((y - 0.08f) / tall);
            float cx = bx + lean * v + 0.06f * Mathf.Sin(v * 3.2f) * (k == 0 ? 1f : k);
            float half = Mathf.Lerp(0.085f, 0.02f, v);
            float dd = y < 0.08f ? Ellipse(x, y, bx, 0.08f, 0.09f, 0.06f) : y > 0.08f + tall ? 9f : Mathf.Abs(x - cx) - half;
            d = Mathf.Min(d, dd);
        }
        float a = Cover(d, texel);
        if (a <= 0f) return Clear;
        Color c = Color.Lerp(fill, Color.white, 0.1f);
        if (d > -texel * 2f) c = edge;
        c.a = a;
        return c;
    }

    /// <summary>A stubby wing (x 0..1, y 0..1; drawn for the right side, the shoulder top left): a rounded paddle with two
    /// feather scallops at its tip.</summary>
    internal static Color WingPaint(float x, float y, float texel, Color fill, Color edge)
    {
        float d = Ellipse(x, y, 0.42f, 0.5f, 0.3f, 0.4f);
        d = Mathf.Min(d, Ellipse(x, y, 0.6f, 0.22f, 0.16f, 0.12f));
        d = Mathf.Min(d, Ellipse(x, y, 0.4f, 0.14f, 0.14f, 0.11f));
        float a = Cover(d, texel);
        if (a <= 0f) return Clear;
        Color c = Color.Lerp(fill, edge, 0.18f) * (0.95f + 0.1f * y);
        if (d > -texel * 2f) c = edge;
        c.a = a;
        return c;
    }

    /// <summary>The tail (x 0..1 from its base, y 0..0.83; pivot at the base): two short feathers flicked up.</summary>
    internal static Color TailPaint(float x, float y, float texel, Color fill, Color edge)
    {
        float d = 9f;
        for (int k = 0; k < 2; k++)
        {
            Vector2 a0 = new Vector2(0.08f, 0.25f), b0 = new Vector2(0.82f - 0.12f * k, 0.62f - 0.22f * k);
            float seg = SegD(new Vector2(x, y), a0, b0);
            float u = Mathf.Clamp01(Vector2.Dot(new Vector2(x, y) - a0, b0 - a0) / (b0 - a0).sqrMagnitude);
            d = Mathf.Min(d, seg - Mathf.Lerp(0.14f, 0.04f, u));
        }
        float al = Cover(d, texel);
        if (al <= 0f) return Clear;
        Color c = fill;
        if (d > -texel * 2f) c = edge;
        c.a = al;
        return c;
    }
}
