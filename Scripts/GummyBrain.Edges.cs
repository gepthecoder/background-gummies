using UnityEngine;

// Edge life: when it's bored it goes and climbs a screen edge or a window side like a slime (over onto the window top,
// or up to the top of the screen, along it upside down, then it dangles and drops), or it squeezes half off-screen and
// peeks in at you, sometimes hiding completely until you come close.
public partial class GummyBrain
{
    [Header("Edges")]
    [Tooltip("When it's bored: chance to go and climb a nearby screen edge or window side.")]
    [Range(0f, 1f)] public float climbChance = 0.25f;
    [Tooltip("When it's bored: chance to go and peek in from just off-screen.")]
    [Range(0f, 1f)] public float peekChance = 0.15f;
    [Tooltip("Looks for edges this far away (world units).")]
    public float edgeRange = 4f;
    [Tooltip("Climbing speed (x size / 0.6).")]
    public float climbSpeed = 1.5f;

    enum EdgeKind { Climb, Peek, Visit, Swing, Glide, Zip, Float, Jet }

    /// <summary>Little moments other scripts can react to (speech bubbles): "climb", "top", "peek", "found", "arrived".</summary>
    public event System.Action<string> Moment;
    /// <summary>Not busy with anything: sitting around, awake, no snack in sight.</summary>
    public bool IsFree => Current == State.Idle && !Food.HasValue && !body.Held && !body.IsStuck && !StayPut;
    /// <summary>Staying where it is for now (sheltering from the rain, splashing in a puddle: BuddyRain): no strolls,
    /// adventures, games or toys; snacks it already went for, sleep and you still work.</summary>
    public bool StayPut { get; set; }
    /// <summary>0 squeezing out, 1 peeking, 2 hiding, 3 coming back; -1 when not peeking.</summary>
    public int PeekPhase => Current == State.Peek ? phase : -1;

    float visitX;

    /// <summary>Hop over to this x on the surface it's on (then sits: Moment "arrived"). False if it's busy.</summary>
    public bool Visit(float x)
    {
        if (!IsFree || !body.Grounded) return false;
        visitX = x;
        edgeKind = EdgeKind.Visit;
        edgeSide = x < body.Center.x ? -1 : 1;
        edge = new GummyBody.Edge { axis = 0, pos = x, sign = -edgeSide, from = -1e4f, to = 1e4f };
        edgeTarget = new Vector2(x, body.Extent.yMin);
        hop = Hop.Squash;
        hopT = 0f;
        Enter(State.Hopping);
        chasing = true;
        edgeWalk = true;
        leapPlanned = false;
        TryRideLeg(); // far: on the skateboard
        return true;
    }

    /// <summary>Lie down for a nap right now (e.g. next to a friend).</summary>
    public void NapNow()
    {
        if (Current == State.Asleep || body.Held) return;
        lingerUntil = -1f;
        Enter(State.Asleep);
    }
    bool edgeWalk;          // hopping over to an edge
    EdgeKind edgeKind;
    Vector2 edgeTarget;
    GummyBody.Edge edge;
    int edgeSide;           // where the wall is: -1 = to its left, +1 = to its right
    int phase;
    float phaseUntil, climbGoal, lastSmallHop;

    bool TryEdgeAdventure()
    {
        if (!body.Grounded || body.IsStuck || body.Held) return false;
        if (rope && skills && skills.Has(BuddySkills.Skill.Swing) && Random.value < swingChance && PlanFunSwing()) return true; // (from window tops too)
        if (board && skills && skills.Has(BuddySkills.Skill.Skate) && Random.value < skateChance &&
            ((skills.Has(BuddySkills.Skill.Grind) && Random.value < 0.5f && PlanGrindLine()) || PlanFunRide(false))) return true; // (window tops too)
        if (plane && skills && skills.Has(BuddySkills.Skill.Glide) && body.OnPlatform && Random.value < glideChance && PlanFunGlide()) return true; // (from window tops)
        if (rope && skills && skills.Has(BuddySkills.Skill.Zip) && body.OnPlatform && Random.value < zipChance && PlanFunZip()) return true; // (from window tops)
        if (balloon && skills && skills.Has(BuddySkills.Skill.Balloon) && Random.value < floatChance && PlanFunFloat()) return true; // (up onto a higher window)
        if (jetpack && JetHeight > 0f && Random.value < jetChance && PlanFunJet()) return true; // (up onto a higher window, if it works)
        if (body.OnPlatform) return false;
        float r = Random.value;
        if (r < climbChance) return (!skills || skills.Has(BuddySkills.Skill.Climb)) && PlanClimb(); // climbing comes with a level
        if (r < climbChance + peekChance) return PlanPeek();
        return false;
    }

    bool PlanClimb()
    {
        Rect w = ScreenBounds.WorldRect, own = ScreenBounds.ScreenRectAt(body.Center);
        Rect e = body.Extent;
        float cx = body.Center.x, best = edgeRange;
        bool found = false;
        // the outside walls (more than one screen: where its own screen is at the edge)
        if (cx - w.xMin < best && own.xMin <= w.xMin + 1e-3f) { best = cx - w.xMin; found = true; edgeSide = -1; edge = new GummyBody.Edge { axis = 0, pos = w.xMin, sign = 1f, from = -1e4f, to = 1e4f }; }
        if (w.xMax - cx < best && own.xMax >= w.xMax - 1e-3f) { best = w.xMax - cx; found = true; edgeSide = 1; edge = new GummyBody.Edge { axis = 0, pos = w.xMax, sign = -1f, from = -1e4f, to = 1e4f }; }
        if (body.solidWindows || ScreenBounds.Multi)
        {
            var wins = DesktopPlatforms.Windows;
            for (int k = 0; k < wins.Count; k++)
            {
                WindowBox b = wins[k];
                if (!b.movable || (!body.solidWindows && !b.screen)) continue;
                if (b.yMin > e.yMin + 0.05f || b.yMax < e.yMin + e.height * 2f) continue; // must come down to its level and be tall enough
                if (b.xMin > cx && b.xMin - cx < best)
                { best = b.xMin - cx; found = true; edgeSide = 1; edge = new GummyBody.Edge { axis = 0, pos = b.xMin, sign = -1f, from = b.yMin, to = b.yMax, surface = b.id }; }
                else if (b.xMax < cx && cx - b.xMax < best)
                { best = cx - b.xMax; found = true; edgeSide = -1; edge = new GummyBody.Edge { axis = 0, pos = b.xMax, sign = 1f, from = b.yMin, to = b.yMax, surface = b.id }; }
            }
        }
        if (!found) return false;
        edgeKind = EdgeKind.Climb;
        GoToEdge();
        return true;
    }

    bool PlanPeek()
    {
        Rect w = ScreenBounds.WorldRect, own = ScreenBounds.ScreenRectAt(body.Center);
        float cx = body.Center.x;
        bool l = own.xMin <= w.xMin + 1e-3f, rt = own.xMax >= w.xMax - 1e-3f; // (more than one screen: an outside edge of its own)
        if (!l && !rt) return false;
        edgeSide = l && rt ? (cx - w.xMin < w.xMax - cx ? -1 : 1) : l ? -1 : 1;
        float wall = edgeSide < 0 ? w.xMin : w.xMax;
        if (Mathf.Abs(cx - wall) > edgeRange * 1.5f || WindowBetween(cx, wall)) return false;
        edge = new GummyBody.Edge { axis = 0, pos = wall, sign = -edgeSide, from = -1e4f, to = 1e4f };
        edgeKind = EdgeKind.Peek;
        GoToEdge();
        return true;
    }

    void GoToEdge()
    {
        edgeTarget = new Vector2(edge.pos - edgeSide * body.Extent.width * 0.5f, body.Extent.yMin); // where it touches the wall
        visitX = float.NaN;
        hop = Hop.Squash;
        hopT = 0f;
        Enter(State.Hopping);
        chasing = true;
        edgeWalk = true;
        leapPlanned = false;
    }

    bool EdgeArrived()
    {
        if (edgeKind == EdgeKind.Visit) return Mathf.Abs(body.Center.x - visitX) < 0.35f * body.size;
        if (edgeKind == EdgeKind.Swing || edgeKind == EdgeKind.Glide || edgeKind == EdgeKind.Zip || edgeKind == EdgeKind.Float || edgeKind == EdgeKind.Jet) return Mathf.Abs(body.Center.x - visitX) < 0.25f * body.size;
        return edgeSide < 0 ? body.Extent.xMin <= edge.pos + 0.12f * body.size : body.Extent.xMax >= edge.pos - 0.12f * body.size;
    }

    void ArriveAtEdge()
    {
        float now = Time.time;
        if (edgeKind == EdgeKind.Visit) { Enter(State.Idle); Moment?.Invoke("arrived"); return; }
        if (edgeKind == EdgeKind.Swing) { ArriveForSwing(); return; }
        if (edgeKind == EdgeKind.Glide) { ArriveForGlide(); return; }
        if (edgeKind == EdgeKind.Zip) { ArriveForZip(); return; }
        if (edgeKind == EdgeKind.Float) { ArriveForFloat(); return; }
        if (edgeKind == EdgeKind.Jet) { ArriveForJet(); return; }
        if (edgeKind == EdgeKind.Climb)
        {
            Rect w = ClimbRect();
            // up a window side: all the way (then over onto its top); up the screen edge: some way, sometimes to the very top
            climbGoal = edge.surface != 0 ? edge.to : body.Extent.yMin + Random.Range(1.5f, 12f);
            if (climbGoal > w.yMax - body.Extent.height) climbGoal = w.yMax; // = to the top
            body.Crawl(edge, climbSpeed * Mathf.Sqrt(S));
            phase = 0;
            phaseUntil = now + 16f;
            Enter(State.Climb);
            Moment?.Invoke("climb");
        }
        else
        {
            float room = 1.3f * body.size;
            if (edgeSide < 0) body.RoomLeft = room; else body.RoomRight = room;
            body.HoldRoom = true;
            phase = 0;
            phaseUntil = now + 6f;
            Enter(State.Peek);
        }
    }

    void UpdateClimb(float now)
    {
        if (body.Held || (!body.Crawling && phase != 2)) { Enter(State.Recover); return; }
        if (Food.HasValue && phase != 2 && !foodClimb) { body.StopCrawl(); Enter(State.Recover); return; } // snack! lets go (unless it's climbing up to it)
        lastAttention = now;
        float sw = Mathf.Sin(stateTime * 2f * Mathf.PI / 0.55f); // inching along
        body.Pose = edge.axis == 0 ? new Vector2(1f - 0.05f * sw, 1f + 0.08f * sw) : new Vector2(1f + 0.08f * sw, 1f - 0.05f * sw);
        Rect w = ClimbRect(), e = body.Extent;
        if (phase == 0)
        {
            if (edge.surface != 0 && e.yMax > edge.to + e.height * 0.25f)
            {
                // top of a window: pull itself up (straight up, the side is in the way) ...
                body.StopCrawl();
                float up = GummyBody.JumpSpeed(Mathf.Max(edge.to - e.yMin, 0f) + 0.2f * S, body.gravity, body.Drag);
                body.Hop(new Vector2(0f, up), 0.8f);
                phase = 2;
                phaseUntil = now + 1.2f;
                return;
            }
            if (e.yMax >= w.yMax - 0.03f)
            {
                // top of the screen: along the top edge, upside down, away from the wall
                edge = new GummyBody.Edge { axis = 1, pos = w.yMax, sign = -1f, from = w.xMin, to = w.xMax };
                body.Crawl(edge, -edgeSide * climbSpeed * Mathf.Sqrt(S));
                Moment?.Invoke("top");
                phase = 1;
                phaseUntil = now + Random.Range(1.5f, 3.5f);
                return;
            }
            if (edge.surface == 0 && e.yMin >= climbGoal) { Enter(State.Recover); return; } // high enough: lets go, wheee
            if (now > phaseUntil + (foodClimb ? 10f : 0f)) { Enter(State.Recover); return; }
        }
        else if (phase == 2)
        {
            // ... and once it's above the top, over onto it
            body.Pose = Vector2.one;
            if (e.yMin > edge.to + 0.02f)
            {
                if (foodClimb)
                {
                    // well onto the top, towards the snack (not just over the edge, where it would slide back off)
                    float d = -edge.sign;
                    float target = Food.HasValue && Mathf.Abs(FoodSurface - edge.to) < 0.1f ? Food.Value.x : body.Center.x + d * body.size;
                    float inside = body.Center.x + d * e.width * 1.2f;
                    if ((target - inside) * d < 0f) target = inside;
                    body.Hop(new Vector2(Mathf.Clamp((target - body.Center.x) / 0.35f - body.Velocity.x, -6f, 6f), 0.5f), 0.6f);
                }
                else body.Hop(new Vector2(-edge.sign * 2.2f * Mathf.Sqrt(S), 1f), 0.6f);
                happyUntil = now + 1.5f;
                Enter(State.Recover);
                Moment?.Invoke("top");
            }
            else if (now > phaseUntil) Enter(State.Recover);
        }
        else if (now > phaseUntil || e.xMin <= w.xMin + 0.02f || e.xMax >= w.xMax - 0.02f)
        {
            if (Random.value < 0.6f) body.StickToEdge(Random.Range(1.5f, 3f)); // hangs on and dangles for a bit
            Enter(State.Recover);
        }
    }

    void UpdatePeek(float now)
    {
        if (body.Held) { Enter(State.Recover); return; }
        lastAttention = now;
        Rect w = ScreenBounds.WorldRect;
        float wall = edgeSide < 0 ? w.xMin : w.xMax;
        float outBy = (body.Center.x - wall) * edgeSide; // how far its middle is past the screen edge
        body.Pose = Breathe(0.012f, now);
        if (Food.HasValue && phase < 3) phase = 3;
        switch (phase)
        {
            case 0: // squeeze out until half of it is past the edge
                if (outBy >= 0f) { phase = 1; phaseUntil = now + Random.Range(2f, 4.5f); Moment?.Invoke("peek"); }
                else if (now > phaseUntil) phase = 3;
                else SmallHop(edgeSide, now, 0.8f);
                break;
            case 1: // half in, watching you
                if (now > phaseUntil)
                {
                    if (Random.value < 0.5f) { phase = 2; phaseUntil = now + Random.Range(2.5f, 6f); }
                    else phase = 3;
                }
                break;
            case 2: // all the way out: hiding. Comes back when you come close, or after a while
                if (outBy < 0.6f * body.size) SmallHop(edgeSide, now, 0.9f);
                bool youFoundMe = Mathf.Abs(Grabber.CursorWorld.x - wall) < 1.5f && outBy > 0.4f * body.size;
                if (now > phaseUntil || youFoundMe)
                {
                    phase = 3;
                    phaseUntil = now + 8f;
                    if (youFoundMe) { happyUntil = now + 1.5f; Moment?.Invoke("found"); }
                }
                break;
            default: // back in
                if (body.Extent.xMin > w.xMin + 0.02f && body.Extent.xMax < w.xMax - 0.02f) { Enter(State.Idle); return; }
                SmallHop(-edgeSide, now, 1.1f);
                if (now > phaseUntil + 4f) Enter(State.Recover); // the room follows it back in either way
                break;
        }
    }

    // the screen it climbs on: its walls and top (one screen: WorldRect; more: its own screen, up to the top of the one
    // above where they touch)
    Rect ClimbRect()
    {
        if (!ScreenBounds.Multi) return ScreenBounds.WorldRect;
        Rect r = ScreenBounds.ScreenRectAt(body.Center);
        r.yMax = ScreenBounds.CeilingAt(body.Center);
        return r;
    }

    void SmallHop(int d, float now, float speed)
    {
        if (!body.Grounded || now - lastSmallHop < 0.45f) return;
        lastSmallHop = now;
        body.Hop(new Vector2(d * speed * Mathf.Sqrt(S), 3.2f * Mathf.Sqrt(S)), 0.5f);
    }

    /// <summary>Is a solid window side right in front of it (about one hop away, at its level)?</summary>
    bool BlockedByWindow(int d)
    {
        if (!body.solidWindows && !ScreenBounds.Multi) return false;
        float reach = body.size * 1.1f;
        Rect e = body.Extent;
        var wins = DesktopPlatforms.Windows;
        for (int k = 0; k < wins.Count; k++)
        {
            WindowBox b = wins[k];
            if (!b.movable || (!body.solidWindows && !b.screen) || b.yMin > e.yMin + 0.05f || b.yMax < e.yMin + 0.05f) continue;
            if (d > 0 && b.xMin >= e.xMax - 0.02f && b.xMin < e.xMax + reach) return true;
            if (d < 0 && b.xMax <= e.xMin + 0.02f && b.xMax > e.xMin - reach) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ more than one screen

    // home: where you last put it down (where it was when the screens changed); it strolls back there
    void UpdateHome()
    {
        if (body.Held) { movedByYou = true; return; }
        if (!body.Grounded) return;
        if (!homeSet || homeVersion != ScreenBounds.Version || movedByYou) { home = body.Center; homeSet = true; homeVersion = ScreenBounds.Version; movedByYou = false; }
    }

    /// <summary>Where it lives (more than one screen): where you last put it down.</summary>
    public Vector2 Home => homeSet ? home : body.Center;

    // on over the edge of its screen that way: a floor there no more than a little drop down, or a hop up
    bool CanCross(int d, float edgeX)
    {
        Rect e = body.Extent;
        var probe = new Vector2(edgeX + d * 0.4f, e.yMin + 0.2f);
        if (!ScreenBounds.OnScreen(probe)) return ScreenStep(d) > 0f;
        return ScreenBounds.FloorUnder(probe, out float y, out _) && e.yMin - y <= 1.5f * S;
    }

    // a bit of the world on no screen right ahead, its top a little higher than its feet (the next screen's floor):
    // how much higher (0: none, or too high to hop)
    float ScreenStep(int d)
    {
        float reach = body.size * 1.1f, best = 0f;
        Rect e = body.Extent;
        var wins = DesktopPlatforms.Windows;
        for (int k = 0; k < wins.Count; k++)
        {
            WindowBox b = wins[k];
            if (!b.screen || b.yMin > e.yMin + 0.05f || b.yMax < e.yMin + 0.05f) continue;
            bool ahead = d > 0 ? b.xMin >= e.xMax - 0.02f && b.xMin < e.xMax + reach : b.xMax <= e.xMin + 0.02f && b.xMax > e.xMin - reach;
            if (!ahead) continue;
            float h = b.yMax - e.yMin;
            if (h > 1.2f * S) return 0f;
            best = Mathf.Max(best, h);
        }
        return best;
    }

    bool WindowBetween(float a, float b)
    {
        if (!body.solidWindows && !ScreenBounds.Multi) return false;
        float lo = Mathf.Min(a, b), hi = Mathf.Max(a, b), y = body.Extent.yMin + 0.05f;
        var wins = DesktopPlatforms.Windows;
        for (int k = 0; k < wins.Count; k++)
        {
            WindowBox w = wins[k];
            if (w.movable && (body.solidWindows || w.screen) && w.yMin <= y && w.yMax >= y && w.xMax > lo && w.xMin < hi) return true;
        }
        return false;
    }
}
