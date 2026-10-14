using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Buddy tower: a snack lying on a window that nobody can get up to alone. The one that wants it calls the others
/// ("TOWER TIME!"); they gather beside the end of that window (the route planner gets them there), the biggest one
/// stands at the bottom and the others climb onto each other's heads (2 or 3 high, wobbling), the ones below push
/// ("PUSH!") and the top one jumps up and over onto the window and eats the snack ("TEAMWORK!", +1 XP for each helper).
/// Now and then it sways too far and topples ("TIMBERRR"). Chat "tower" stacks up on the spot just to show off.
/// One tower at a time; runtime only (BuddyEater calls Want() when it can't get to a snack).
/// </summary>
[DefaultExecutionOrder(6)] // after BuddyEater / BuddyFetch have set where everyone's going: the tower's members go to the tower
public class BuddyTower : MonoBehaviour
{
    /// <summary>The tower going on (null = none).</summary>
    public static BuddyTower Current { get; private set; }
    /// <summary>This buddy is part of the tower going on.</summary>
    public static bool Has(GummyBody b) => Current && b && Current.Contains(b);
    /// <summary>Chance a tower 2 / 3 high topples before the top one jumps off.</summary>
    public static float toppleTwo = 0.12f, toppleThree = 0.25f;
    /// <summary>So far (this session): towers started, stacked all the way up, toppled, top one made it onto the window, snack eaten after.</summary>
    public static int Started, Stacked, Toppled, Reached, Teamwork;
    /// <summary>A tower made it to the top (the top one jumped off onto the window): everyone in it, one by one.</summary>
    public static event System.Action<GummyBody> Made;
    /// <summary>Why the last one stopped early (for testing).</summary>
    public static string LastReason = "";

    /// <summary>How many high (2 or 3), and the members bottom to top.</summary>
    public int Height => m.Count;
    public IReadOnlyList<GummyBody> Members => bodies;
    public bool Practice => practice;

    enum Phase { Gather, Build, Stand, Push, Jump, Down, Wait, Topple }

    class Member
    {
        public GummyBody body;
        public GummyBrain brain;
        public BuddySpeech speech;
        public float h, cOff, hw, spotX, climbT, climbDur;
        public bool joined, perched, climbing, released;
        public Vector2 from;
    }

    readonly List<Member> m = new List<Member>();             // bottom to top
    readonly List<GummyBody> bodies = new List<GummyBody>();
    Snack snack;
    Member caller;
    bool practice, topple, reached;
    int side, layout, downNext;
    float towerX, groundY, landX, topY, windowY, t, phaseT, swayT, swayAmp, swayDeg, stuckFor, nextDown;
    Phase phase;

    const float GatherTime = 20f, Push = 0.5f, Clear = 0.25f;
    static readonly Dictionary<Snack, float> s_retry = new Dictionary<Snack, float>();
    static float s_quietUntil = -100f;

    bool Contains(GummyBody b) => bodies.Contains(b);
    Member Top => m[m.Count - 1];
    Member Base => m[0];

    // ------------------------------------------------------------------ starting one

    /// <summary>
    /// This buddy can't get to that snack (BuddyEater): if a tower of friends could get it up there, build one. True if
    /// one's on its way.
    /// </summary>
    public static bool Want(GummyBody leader, Snack s)
    {
        float now = Time.time;
        if (Current || !leader || !s || !s.Resting || s.Held || s.Eaten || now < s_quietUntil || Busy()) return false;
        if (s_retry.TryGetValue(s, out float until) && now < until) return false;
        if (!Free(leader, true)) return false;
        foreach (var b in GummyBody.All)
        {
            var e = b ? b.GetComponent<BuddyEater>() : null;
            if (e && e.Target == s) return false; // someone's on its way already
        }
        if (s_retry.Count > 32) { var dead = new List<Snack>(); foreach (var kv in s_retry) if (!kv.Key || now > kv.Value) dead.Add(kv.Key); foreach (var d in dead) s_retry.Remove(d); }
        s_retry[s] = now + 4f; // (thinks it over again in a bit)
        var helpers = Helpers(leader, s.Position);
        if (helpers.Count == 0) return false;
        if (!PlanFor(leader, helpers, s, out var plan)) { s_retry[s] = now + 10f; return false; }
        var go = new GameObject("Buddy Tower");
        go.AddComponent<BuddyTower>().Begin(plan, s, false);
        return true;
    }

    /// <summary>Stack up right here just to show off (chat "tower"). False if there's nobody free to stack with, or no room.</summary>
    public static bool Show(GummyBody caller)
    {
        if (Current || !caller || Busy() || !Free(caller, true)) return false;
        var helpers = Helpers(caller, caller.Center);
        if (helpers.Count == 0) return false;
        if (!PlanShow(caller, helpers, out var plan)) return false;
        var go = new GameObject("Buddy Tower");
        go.AddComponent<BuddyTower>().Begin(plan, null, true);
        return true;
    }

    static bool Busy() => LevelUpParty.Current || BuddyHideSeek.Playing || LaserPointer.On || PickupGame.Current;

    // awake, on its feet, not busy (the one that wants the snack: not doing anything else either)
    static bool Free(GummyBody b, bool leader)
    {
        if (!b || !b.isActiveAndEnabled || b.IsMonster || b.Held || b.IsStuck || !b.Grounded || b.OnRope || b.Gliding || b.Skating) return false;
        var brain = b.GetComponent<GummyBrain>();
        if (!brain || brain.Towering || brain.Current != GummyBrain.State.Idle) return false;
        var fetch = b.GetComponent<BuddyFetch>();
        if (fetch && fetch.Carrying) return false;
        if (leader) return true;
        var eater = b.GetComponent<BuddyEater>();
        return brain.IsFree && !(eater && eater.Target);
    }

    static List<GummyBody> Helpers(GummyBody leader, Vector2 near)
    {
        var l = new List<GummyBody>();
        foreach (var b in GummyBody.All)
            if (b != leader && Free(b, false) && (b.Center - near).magnitude < 18f) l.Add(b);
        return l;
    }

    static Member Make(GummyBody b)
    {
        var e = b.Extent;
        return new Member
        {
            body = b, brain = b.GetComponent<GummyBrain>(), speech = b.GetComponent<BuddySpeech>(),
            h = Mathf.Max(e.height, b.size * 0.7f), cOff = Mathf.Max(b.Center.y - e.yMin, b.size * 0.3f), hw = Mathf.Max(e.width, b.size * 0.8f) * 0.5f,
        };
    }

    // how high the top one gets off the top of the tower (its own best jump, and the ones below push)
    static float Jump(GummyBody b)
    {
        var sk = b.GetComponent<BuddySkills>();
        float s = b.size / 0.6f;
        float j = sk ? Mathf.Max(sk.HopUpHeight, Mathf.Max(sk.LeapHeight, sk.WallJumpHeight)) : 1f;
        return Mathf.Max(j, 0.75f * s) + 0.5f;
    }

    struct Plan
    {
        public List<Member> m;
        public GummyBody leader;
        public int side;
        public float towerX, groundY, landX, topY, windowY, cost;
    }

    // beside one end of the window the snack is on, on what's below there: the fewest buddies that get the top one up.
    // Who goes where: the one nearest the tower at the bottom, the one furthest out on top (nobody has to get past anyone
    // to get to their spot), unless only another order gets the top one high enough (then the one that called, on top).
    static bool PlanFor(GummyBody leader, List<GummyBody> helpers, Snack s, out Plan best)
    {
        best = default;
        best.cost = float.MaxValue;
        Rect r = ScreenBounds.WorldRect;
        var lead = Make(leader);
        bool solid = leader.solidWindows;
        BuddyRoute.Build(lead.h, solid);
        var surfs = BuddyRoute.Surfaces;
        int bi = BuddyRoute.SurfaceAt(s.Position.x, s.SurfaceY);
        if (bi < 0 || surfs[bi].win == 0) return false; // (on the floor: anyone gets there)
        BuddyRoute.Surface B = surfs[bi];
        var pool = new List<Member>();
        foreach (var h in helpers) pool.Add(Make(h));
        float maxHW = lead.hw;
        foreach (var x in pool) maxHW = Mathf.Max(maxHW, x.hw);
        for (int n = 2; n <= 3 && n - 1 <= pool.Count; n++)
        {
            for (int sd = -1; sd <= 1; sd += 2)
            {
                // sd +1: beside B's left end, jumping over to the right; -1: beside its right end
                float edgeX = sd > 0 ? B.x0 : B.x1;
                float tx = edgeX - sd * (maxHW + 0.15f);
                if (tx - maxHW < r.xMin + 0.05f || tx + maxHW > r.xMax - 0.05f) continue;
                BuddyRoute.Build(lead.h, solid);
                int ai = BuddyRoute.SurfaceBelow(tx, B.y - 0.3f);
                if (ai < 0) continue;
                BuddyRoute.Surface A = BuddyRoute.Surfaces[ai];
                if (tx - maxHW * 0.5f < A.x0 || tx + maxHW * 0.5f > A.x1) continue; // no room to stand there
                if (B.y - A.y < 0.4f) continue;
                // the helpers nearest the tower; then who goes where
                var near = new List<Member>(pool);
                near.Sort((a, b) => Away(a, tx, A).CompareTo(Away(b, tx, A)));
                var team = new List<Member> { lead };
                for (int k = 0; k < n - 1; k++) team.Add(near[k]);
                var orders = Orders(team);
                orders.Sort((a, b) => Rank(a, tx, sd, A, leader).CompareTo(Rank(b, tx, sd, A, leader)));
                foreach (var order in orders)
                {
                    if (!Fits(order, B, A, tx, sd, solid, r, out float lx, out float ty)) continue;
                    var ms = new List<Member>();
                    foreach (var x in order) ms.Add(Copy(x));
                    if (!Spots(ms, tx, sd, A, r, out float cost)) continue;
                    cost += 3f * Crossings(order, tx, sd, A);
                    if (cost < best.cost) best = new Plan { m = ms, leader = leader, side = sd, towerX = tx, groundY = A.y, landX = lx, topY = ty, windowY = B.y, cost = cost };
                    break; // (the best order for this side)
                }
            }
            if (best.m != null) return true; // (the fewest that do it)
        }
        return false;
    }

    // how far it is from the tower spot (another surface counts extra)
    static float Away(Member x, float tx, BuddyRoute.Surface A) => Mathf.Abs(x.body.Center.x - tx) + 3f * Mathf.Abs(x.body.Extent.yMin - A.y);

    static List<List<Member>> Orders(List<Member> team)
    {
        var all = new List<List<Member>>();
        if (team.Count == 2) { all.Add(new List<Member> { team[0], team[1] }); all.Add(new List<Member> { team[1], team[0] }); return all; }
        int[][] p = { new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 }, new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 } };
        foreach (var q in p) all.Add(new List<Member> { team[q[0]], team[q[1]], team[q[2]] });
        return all;
    }

    // nobody has to get past anybody: the bottom one is the nearest on its side of the tower, and on each side the ones
    // that climb first wait nearer. Fewer is better, then the one that called on top
    static int Crossings(List<Member> order, float tx, int sd, BuddyRoute.Surface A)
    {
        int c = 0;
        var b0 = order[0];
        int s0 = Side(b0, tx, sd, A);
        for (int i = 1; i < order.Count; i++)
            if (Side(order[i], tx, sd, A) == s0 && Away(order[i], tx, A) < Away(b0, tx, A)) c++;
        for (int i = 1; i < order.Count; i++)
            for (int j = i + 1; j < order.Count; j++)
                if (Side(order[i], tx, sd, A) == Side(order[j], tx, sd, A) && Away(order[i], tx, A) > Away(order[j], tx, A) + 0.05f) c++;
        return c;
    }
    static int Rank(List<Member> order, float tx, int sd, BuddyRoute.Surface A, GummyBody leader) => Crossings(order, tx, sd, A) * 2 + (order[order.Count - 1].body == leader ? 0 : 1);

    // high enough (the ones below and the top one's jump), and room for the tower and for the top one's way up and over
    static bool Fits(List<Member> order, BuddyRoute.Surface B, BuddyRoute.Surface A, float tx, int sd, bool solid, Rect r, out float lx, out float ty)
    {
        var top = order[order.Count - 1];
        float stackH = 0f, maxHW = top.hw;
        for (int j = 0; j < order.Count - 1; j++) { stackH += order[j].h * 0.96f; maxHW = Mathf.Max(maxHW, order[j].hw); }
        float edgeX = sd > 0 ? B.x0 : B.x1;
        lx = Mathf.Clamp(edgeX + sd * top.hw * 1.6f, B.x0 + top.hw, B.x1 - top.hw);
        ty = B.y + Clear * top.body.size / 0.6f;
        if (B.y - A.y > stackH + Jump(top.body)) return false;
        if (ty + top.h + 0.1f > r.yMax) return false; // no room up there
        float colTop = Mathf.Max(A.y + stackH + top.h, ty + top.h);
        if (BuddyRoute.Blocked(new Vector2(tx, (A.y + 0.08f + colTop) * 0.5f), maxHW * 0.85f, (colTop - A.y - 0.08f) * 0.5f, solid)) return false;
        int steps = Mathf.Clamp(Mathf.CeilToInt(Mathf.Abs(lx - tx) / 0.25f), 2, 40);
        for (int k = 1; k <= steps; k++)
            if (BuddyRoute.Blocked(new Vector2(Mathf.Lerp(tx, lx, k / (float)steps), ty + top.h * 0.5f + 0.02f), top.hw * 0.85f, top.h * 0.5f - 0.04f, solid)) return false;
        return true;
    }

    // stacking up where it stands, the biggest at the bottom and the littlest on top, just to show off
    static bool PlanShow(GummyBody caller, List<GummyBody> helpers, out Plan p)
    {
        p = default;
        Rect r = ScreenBounds.WorldRect;
        var all = new List<Member> { Make(caller) };
        foreach (var h in helpers) all.Add(Make(h));
        all.Sort((a, b) => b.body.size.CompareTo(a.body.size)); // the biggest at the bottom, the littlest on top
        if (all.Count > 3) { var keep = all.GetRange(0, 2); keep.Add(all[all.Count - 1]); if (!keep.Exists(x => x.body == caller)) keep[1] = all.Find(x => x.body == caller); keep.Sort((a, b) => b.body.size.CompareTo(a.body.size)); all = keep; }
        bool solid = caller.solidWindows;
        float gy = caller.Extent.yMin - caller.SkateLift, tx = caller.Center.x, stackH = 0f, maxHW = 0f;
        var bse = all[0].body;
        if (Mathf.Abs(bse.Extent.yMin - bse.SkateLift - gy) < 0.2f && Mathf.Abs(bse.Center.x - tx) < 6f) tx = bse.Center.x; // (the bottom one's already standing on this surface: right there)
        foreach (var x in all) { stackH += x.h * 0.96f; maxHW = Mathf.Max(maxHW, x.hw); }
        if (gy + stackH + 0.2f > r.yMax) return false;
        if (BuddyRoute.Blocked(new Vector2(tx, gy + 0.08f + stackH * 0.5f), maxHW * 0.85f, stackH * 0.5f - 0.04f, solid)) return false;
        BuddyRoute.Build(all[0].h, solid);
        int ai = BuddyRoute.SurfaceBelow(tx, gy + 0.1f);
        if (ai < 0) return false;
        BuddyRoute.Surface A = BuddyRoute.Surfaces[ai];
        int sd = tx > r.center.x ? 1 : -1; // (the others wait on the side towards the middle of the screen)
        tx = Mathf.Clamp(tx, A.x0 + maxHW * 0.5f, A.x1 - maxHW * 0.5f);
        if (!Spots(all, tx, sd, A, r, out float cost)) return false;
        p = new Plan { m = all, leader = caller, side = sd, towerX = tx, groundY = A.y, landX = tx, topY = A.y, windowY = A.y, cost = cost };
        return true;
    }

    static Member Copy(Member x) => new Member { body = x.body, brain = x.brain, speech = x.speech, h = x.h, cOff = x.cOff, hw = x.hw };

    // where each one waits before it climbs (the bottom one right at the tower, the others in a row behind it), and
    // can everyone get there
    static bool Spots(List<Member> ms, float tx, int sd, BuddyRoute.Surface A, Rect r, out float cost)
    {
        cost = 0f;
        bool solid = ms[0].body.solidWindows;
        ms[0].spotX = tx;
        float offL = ms[0].hw + 0.1f, offR = offL; // (how far out each side's row of waiting buddies reaches)
        for (int j = 1; j < ms.Count; j++)
        {
            var x = ms[j];
            float gap = x.hw + 0.35f * x.body.size; // (room to spare: it stops a little short or long of its spot)
            float lo = Mathf.Max(A.x0, r.xMin) + x.hw * 0.6f, hi = Mathf.Min(A.x1, r.xMax) - x.hw * 0.6f;
            int want = Side(x, tx, sd, A);
            bool placed = false;
            for (int k = 0; k < 2 && !placed; k++)
            {
                int dir = k == 0 ? want : -want;
                float off = (dir < 0 ? offL : offR) + gap, at = tx + dir * off;
                if (lo >= hi || at < lo || at > hi) continue; // no room on that side
                if (BuddyRoute.Blocked(new Vector2(at, A.y + 0.05f + x.h * 0.5f), x.hw * 0.85f, x.h * 0.5f, solid)) continue; // (under a window that comes down too low)
                x.spotX = at;
                if (dir < 0) offL = off + x.hw + 0.1f; else offR = off + x.hw + 0.1f;
                placed = true;
            }
            if (!placed) return false;
        }
        foreach (var x in ms)
        {
            var b = x.body;
            bool there = Mathf.Abs(b.Extent.yMin - b.SkateLift - A.y) < 0.2f && b.Center.x > A.x0 - 0.1f && b.Center.x < A.x1 + 0.1f;
            if (there) { cost += Mathf.Abs(b.Center.x - x.spotX); continue; }
            if (!x.brain.PlanTo(new Vector2(x.spotX, A.y + 0.3f), A.y, out float c)) return false;
            cost += c;
        }
        return true;
    }

    // which side of the tower it waits on: the side it's on now (coming from somewhere else: the side away from the window)
    static int Side(Member x, float tx, int sd, BuddyRoute.Surface A)
    {
        bool here = Mathf.Abs(x.body.Extent.yMin - x.body.SkateLift - A.y) < 0.2f && x.body.Center.x > A.x0 - 0.1f && x.body.Center.x < A.x1 + 0.1f;
        if (!here || Mathf.Abs(x.body.Center.x - tx) < 0.05f) return -sd;
        return x.body.Center.x < tx ? -1 : 1;
    }

    // ------------------------------------------------------------------ the tower

    void OnDisable() { BuddyEater.AnyAte -= OnAte; Release(); if (Current == this) Current = null; }

    void Begin(Plan p, Snack s, bool show)
    {
        Current = this;
        Started++;
        BuddyEater.AnyAte += OnAte;
        m.AddRange(p.m);
        foreach (var x in m) bodies.Add(x.body);
        snack = s;
        practice = show;
        caller = m.Find(x => x.body == p.leader) ?? Top;
        side = p.side;
        towerX = p.towerX;
        groundY = p.groundY;
        landX = p.landX;
        topY = p.topY;
        windowY = p.windowY;
        layout = BuddyRoute.LayoutHash();
        topple = Random.value < (m.Count >= 3 ? toppleThree : toppleTwo);
        phase = Phase.Gather;
        Say(caller, 1f, show ? L(caller, "TOWER TIME!", "BEAN STACK!", "BURGER TOWER!", "DINO STACK!") : L(caller, "TOWER TIME!", "STACK-PRESSO!", "LIFT ME UP?", "STACK FOR SNACK!"), true);
        for (int j = 0; j < m.Count; j++)
        {
            if (m[j] == caller) continue;
            bool bottom = j == 0, top = j == m.Count - 1;
            Say(m[j], bottom ? 1f : 0.7f, bottom ? L(m[j], "I'LL BE THE BASE", "BASE BEAN!", "I'M THE BASE", "DINO BASE!") : top ? L(m[j], "I'LL GO UP!", "ME ON TOP!", "UP I GO", "TOP DINO!") : L(m[j], "ON IT!", "COMING! ZOOM", "OK OK", "RAWR! COMING"), bottom);
        }
    }

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("Member.Update", t0); }
    void UpdateTimed()
    {
        float dt = Mathf.Min(Time.deltaTime, 0.05f), now = Time.time;
        t += dt;
        phaseT += dt;
        BuddySocial.QuietUntil = Mathf.Max(BuddySocial.QuietUntil, now + 1f); // (no visits or chats while they're at it)
        if (phase == Phase.Wait) { if (phaseT > 15f || !snack || snack.Eaten) End(); return; }
        if (!Check()) return;
        Sway(dt);

        switch (phase)
        {
            case Phase.Gather: Gather(); break;
            case Phase.Build: Build(dt); break;
            case Phase.Stand:
                Hold();
                if (phaseT < 0.8f) break;
                if (topple) { Enter(Phase.Topple); Say(Top, 1f, L(Top, "WHOA WHOA", "WOBBLY!!", "UH OH", "WOBBLE WOBBLE"), true); break; }
                if (practice) { Say(Top, 1f, L(Top, "TA-DA!", "TA-DA-PRESSO!", "BEHOLD!", "RAWR! TA-DA!"), true); Enter(Phase.Push); break; }
                Enter(Phase.Push);
                for (int j = 0; j < m.Count - 1; j++) Say(m[j], j == 0 ? 1f : 0.5f, L(m[j], "PUSH!", "GO GO GO!", "HUP!", "UP YOU GO!"), j == 0);
                break;
            case Phase.Push:
                Hold();
                if (practice) { if (phaseT > 1.6f) { Enter(Phase.Down); nextDown = 0f; downNext = m.Count - 1; } break; }
                if (phaseT >= Push) { Enter(Phase.Jump); Say(Top, 0.9f, L(Top, "WHEEE!", "ZOOOM!", "HUP!", "RAWR!"), true); }
                break;
            case Phase.Jump: Hold(); JumpOff(dt); break;
            case Phase.Down: Hold(); Down(); break;
            case Phase.Topple: Hold(); if (phaseT > 1.4f) Fall(); break;
        }
    }

    void Enter(Phase p) { phase = p; phaseT = 0f; }

    // still a tower? (someone grabbed, the snack gone, a window moved, the laser...)
    bool Check()
    {
        bool joinedMoved = false;
        foreach (var x in m)
        {
            if (!x.body || !x.body.isActiveAndEnabled || !x.brain) { LastReason = "gone"; Collapse(null); return false; }
            if (x.joined && !x.released && !x.brain.Towering) joinedMoved = true; // (grabbed, or knocked out of it)
            if (!x.joined && (x.body.Held || x.brain.Current == GummyBrain.State.Asleep)) { LastReason = "held/asleep"; Collapse(L(Top, "NEVER MIND", "FINE. BYE", "NEVERMIND", "NEVER MIND")); return false; }
        }
        if (joinedMoved) { LastReason = "moved"; Collapse(L(Base, "HEY!", "HEY!!", "OOF. HEY", "RAWR! HEY")); return false; }
        if (!practice && phase <= Phase.Push && (!snack || snack.Eaten || snack.Held || !snack.isActiveAndEnabled || !snack.Resting || Mathf.Abs(snack.SurfaceY - windowY) > 0.3f))
        { LastReason = "snack"; Collapse(L(Top, "AWW", "AWW...", "AWW. SNACK GONE", "AWW")); return false; }
        if (BuddyRoute.LayoutHash() != layout && phase <= Phase.Jump) { LastReason = "layout"; Collapse(L(Top, "WHOA! THE WINDOW", "HEY! THE WINDOW", "WHO MOVED THAT", "RAWR?! WINDOW")); return false; }
        if (Busy()) { LastReason = "busy"; Collapse(null); return false; }
        if (phase == Phase.Gather && phaseT > GatherTime) { LastReason = "slow"; Collapse(L(Top, "NEVER MIND", "TOO SLOW!", "MEH. LATER", "NEVER MIND")); return false; }
        return true;
    }

    // everyone over to their spot (the route planner gets them there), then stands still
    void Gather()
    {
        bool all = true;
        foreach (var x in m)
        {
            if (x.joined) continue;
            var b = x.body;
            bool there = Mathf.Abs(b.Center.x - x.spotX) < Mathf.Max(0.35f * b.size, 0.15f) && Mathf.Abs(b.Extent.yMin - b.SkateLift - groundY) < 0.2f && b.Grounded && !b.Skating
                && !b.IsStuck && b.Velocity.sqrMagnitude < 0.25f; // (landed and settled: not still bouncing off its last hop)
            if (there && x.brain.TowerStart()) { x.joined = true; x.brain.Food = null; continue; }
            all = false;
            x.brain.Food = new Vector2(x.spotX, groundY + b.size * 0.3f);
            x.brain.FoodSurface = groundY;
        }
        if (all) { Enter(Phase.Build); m[0].perched = true; }
    }

    // one after the other, from the bottom up: a hop up and over onto the head of the one below
    void Build(float dt)
    {
        Hold();
        int j = 1;
        while (j < m.Count && m[j].perched) j++;
        if (j >= m.Count)
        {
            Stacked++;
            Enter(Phase.Stand);
            return;
        }
        var x = m[j];
        if (!x.climbing)
        {
            if (phaseT < 0.35f) return; // (a moment between climbers)
            x.climbing = true;
            x.from = x.body.Center;
            Vector2 to = PerchAt(j);
            x.climbDur = Mathf.Clamp(0.4f + 0.16f * (to - x.from).magnitude, 0.5f, 1.1f);
            x.climbT = 0f;
            Say(x, 0.6f, L(x, "HUP!", "UP I GO!", "HUP HUP", "CLIMB TIME!"));
        }
        x.climbT += dt;
        float u = Mathf.Clamp01(x.climbT / x.climbDur);
        u = u * u * (3f - 2f * u);
        Vector2 T = PerchAt(j);
        Vector2 C = new Vector2(Mathf.Lerp(x.from.x, T.x, 0.35f), Mathf.Max(x.from.y, T.y) + 0.55f * x.body.size);
        Vector2 D = (1f - u) * (1f - u) * x.from + 2f * u * (1f - u) * C + u * u * T;
        Fly(x.body, D, 9f * Mathf.Sqrt(x.body.size / 0.6f), 16f, 70f);
        x.body.Pose = new Vector2(0.94f, 1.06f);
        if (u >= 1f && (x.body.Center - T).magnitude < 0.08f * x.body.size + 0.04f)
        {
            x.perched = true;
            x.climbing = false;
            phaseT = 0f;
            Say(m[j - 1], j == 1 ? 0.8f : 0.5f, L(m[j - 1], "OOF!", "HEAVY!", "OOF. HEAVY", "OOF! RAWR"), true);
        }
        else if (x.climbT > x.climbDur + 1.5f) { LastReason = "climb"; Collapse(L(Base, "TOO WOBBLY", "NOPE. WOBBLY", "TOO WOBBLY", "NOPE")); }
    }

    // the ones up there stay on the heads below them (swaying), the bottom one squished
    void Hold()
    {
        int perched = 0;
        for (int j = 1; j < m.Count; j++)
        {
            var x = m[j];
            if (!x.perched || !x.brain.Towering || (j == m.Count - 1 && phase >= Phase.Jump)) continue;
            perched++;
            Fly(x.body, PerchAt(j), 4f, 12f, 45f);
            x.body.Lean = -swayDeg;
            float crouch = phase == Phase.Push && !practice ? Mathf.Clamp01(phaseT / Push) * 0.06f : 0f;
            float sx = 1f + 0.02f * perched + crouch;
            x.body.Pose = new Vector2(sx, 1f / sx);
        }
        if (Base.brain.Towering)
        {
            float load = 0f;
            for (int j = 1; j < m.Count; j++) if (m[j].perched && m[j].brain.Towering) load += m[j].body.size / Base.body.size;
            float push = phase == Phase.Push && !practice ? Mathf.Clamp01(phaseT / Push) * 0.08f : 0f;
            float sx = 1f + 0.045f * load + 0.012f * load * Mathf.Sin(t * 8f) + push;
            Base.body.Pose = new Vector2(sx, 1f / sx);
        }
    }

    // where climber j sits: on top of the one below it, leaning out with the tower's sway
    Vector2 PerchAt(int j)
    {
        var below = m[j - 1].body;
        float hgt = below.Extent.yMax - Base.body.Extent.yMin + m[j].cOff;
        return new Vector2(Base.body.Center.x + Mathf.Sin(swayDeg * Mathf.Deg2Rad) * hgt, below.Extent.yMax + m[j].cOff + 0.02f);
    }

    void Sway(float dt)
    {
        swayT += dt;
        int up = 0;
        for (int j = 1; j < m.Count; j++) if (m[j].perched) up++;
        float amp = phase == Phase.Topple ? Mathf.Lerp(2f, 16f, Mathf.Clamp01(phaseT / 1.4f) * Mathf.Clamp01(phaseT / 1.4f)) : 1.2f * up + (m.Count >= 3 ? 0.6f : 0f);
        swayAmp = Mathf.MoveTowards(swayAmp, amp, dt * (phase == Phase.Topple ? 40f : 6f));
        swayDeg = swayAmp * (Mathf.Sin(swayT * 2f * Mathf.PI * 0.75f) + 0.35f * Mathf.Sin(swayT * 2f * Mathf.PI * 1.8f + 1.1f));
    }

    static void Fly(GummyBody b, Vector2 at, float maxV, float k, float accel)
    {
        Vector2 v = (at - b.Center) * k;
        if (v.magnitude > maxV) v = v.normalized * maxV;
        b.Glide(v, accel * Mathf.Sqrt(b.size / 0.6f));
    }

    // the top one: straight up off the tower, across above the window, lets go over the spot
    void JumpOff(float dt)
    {
        var x = Top;
        var b = x.body;
        if (!x.brain.Towering) { x.released = true; Landed(); return; }
        float S = Mathf.Sqrt(b.size / 0.6f), feet = b.Extent.yMin, togo = topY - feet;
        Vector2 v;
        if (togo > 0.04f && Mathf.Abs(landX - b.Center.x) > 0.3f * b.size && phaseT < 4f)
        {
            v = new Vector2(Mathf.Clamp((towerX - b.Center.x) * 2f, -0.6f, 0.6f), 7f * S * Mathf.Clamp(togo * 1.5f + 0.35f, 0.35f, 1f));
            b.Pose = new Vector2(0.9f, 1.11f);
        }
        else
        {
            float dx = landX - b.Center.x;
            if (Mathf.Abs(dx) < 0.25f * b.size) { Off(x); Landed(); return; } // over the spot: drops on
            v = new Vector2(Mathf.Clamp(dx * 3f, -1f, 1f) * 4.5f * S, Mathf.Clamp((topY - feet) * 3f, -0.8f, 0.8f));
            b.Pose = Vector2.one;
        }
        b.Glide(v, 60f * S);
        b.Lean = Mathf.Clamp(-b.Velocity.x * 3f, -12f, 12f);
        stuckFor = b.Velocity.magnitude < 0.3f ? stuckFor + dt : 0f;
        if (stuckFor > 0.6f || phaseT > 6f) { Off(x); Landed(); } // (bumped into something: drops)
    }

    void Landed()
    {
        reached = Top.body.Extent.yMin > topY - Clear * Top.body.size / 0.6f - 0.4f;
        if (reached) { Reached++; foreach (var x in m) if (x.body) Made?.Invoke(x.body); }
        Enter(Phase.Down);
        nextDown = 0.35f;
        downNext = m.Count - 2;
    }

    // the rest climb down from the top, one after the other, away from the window; the bottom one last
    void Down()
    {
        if (phaseT < nextDown) return;
        nextDown = phaseT + 0.3f;
        for (int j = Mathf.Min(downNext, m.Count - 1); j >= 1; j--)
        {
            var x = m[j];
            if (!x.brain.Towering) continue;
            HopOff(x, -side, 1f);
            downNext = j - 1;
            return;
        }
        if (Base.brain.Towering)
        {
            Off(Base);
            Say(Base, reached ? 0.9f : 0.5f, reached ? L(Base, "TEAMWORK!", "TEAM BEAN!", "TEAMWORK. NICE", "TEAM RAWR!") : practice ? L(Base, "NAILED IT", "STACKED!", "STRONG BUN", "STRONG DINO") : L(Base, "PHEW", "PHEW!", "MY BACK", "PHEW. RAWR"));
        }
        if (reached && !practice) Enter(Phase.Wait);
        else End();
    }

    static void Off(Member x)
    {
        x.released = true;
        x.brain.TowerEnd();
    }

    static void HopOff(Member x, int dir, float k)
    {
        Off(x);
        float s = Mathf.Sqrt(x.body.size / 0.6f);
        x.body.Hop(new Vector2(dir * Random.Range(1.3f, 1.9f) * s * k - x.body.Velocity.x, Random.Range(2.2f, 3f) * s - x.body.Velocity.y), 0.4f);
    }

    // TIMBERRR: everyone above the bottom one goes flying off to the side it leaned
    void Fall()
    {
        Toppled++;
        int dir = swayDeg > 0f ? 1 : swayDeg < 0f ? -1 : -side;
        for (int j = m.Count - 1; j >= 1; j--)
        {
            var x = m[j];
            if (!x.brain.Towering) continue;
            float s = Mathf.Sqrt(x.body.size / 0.6f);
            x.released = true;
            x.brain.TowerFell(0f);
            x.body.Hop(new Vector2(dir * (2.6f + 1f * j) * s - x.body.Velocity.x, (2.4f + 0.5f * j) * s - x.body.Velocity.y), 0.4f);
            x.body.Spin(-dir * (4f + 2f * j));
        }
        Say(Top, 1f, L(Top, "TIMBERRR!", "TIMBERRR!!", "WHOAAA!", "TIMBERRR! RAWR"), true);
        if (m.Count > 2) Say(m[1], 0.7f, L(m[1], "NOOO!", "AAAH!", "NOT AGAIN", "RAWRRR!"));
        if (Base.brain.Towering) { Off(Base); Say(Base, 0.8f, L(Base, "OOF!", "OOF!!", "MY BUN!", "OOF!")); }
        if (snack) s_retry[snack] = Time.time + 45f; // (that one: not again for a while)
        End();
    }

    // it's off: anyone up there hops down, the others go back to what they were doing
    void Collapse(string line)
    {
        if (line != null && Top.speech) Say(Top, 1f, line);
        if (snack) s_retry[snack] = Time.time + 20f;
        Release();
        End();
    }

    void Release()
    {
        for (int j = m.Count - 1; j >= 0; j--)
        {
            var x = m[j];
            if (!x.body || !x.brain) continue;
            if (x.brain.Towering)
            {
                if (j > 0 && (x.perched || x.climbing) && x.body.isActiveAndEnabled) HopOff(x, -side, 0.7f);
                else Off(x);
            }
            else if (!x.joined && phase == Phase.Gather) x.brain.Food = null;
        }
    }

    void OnAte(BuddyEater e, Snack s)
    {
        if (phase != Phase.Wait || !e || s != snack || e.gameObject != Top.body.gameObject) return;
        Teamwork++;
        Say(Top, 1f, L(Top, "THANKS, TEAM!", "THANKS TEAM! ZOOM", "WORTH IT", "THANKS, TEAM! RAWR"), true);
        for (int j = 0; j < m.Count - 1; j++)
        {
            var x = m[j];
            if (!x.body) continue;
            var p = x.body.GetComponent<BuddyProgress>();
            if (p) p.AddXp(1, x.body.Center);
            if (j == 0) Say(x, 0.6f, L(x, "WE DID IT!", "TEAM BEAN!", "TEAMWORK!", "WE DID IT! RAWR"));
        }
        End();
    }

    void End()
    {
        BuddyEater.AnyAte -= OnAte;
        s_quietUntil = Time.time + 15f;
        if (Current == this) Current = null;
        enabled = false;
        Destroy(gameObject);
    }

    static void Say(Member x, float chance, string line, bool force = false)
    {
        if (x == null || !x.speech || !x.speech.isActiveAndEnabled || Random.value > chance) return;
        x.speech.Say(line, 0f, force);
    }

    static string L(Member x, string any, string bean, string burger, string dino)
    {
        if (x == null || !x.speech) return any;
        switch (x.speech.voice)
        {
            case BuddySpeech.Voice.Bean: return bean;
            case BuddySpeech.Voice.Burger: return burger;
            case BuddySpeech.Voice.Dino: return dino;
            default: return any;
        }
    }
}
