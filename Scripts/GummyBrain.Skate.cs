using UnityEngine;

// Skateboard (BuddySkills SKATEBOARD, with BuddySkate): a long way along the taskbar or a window top goes on the board
// (to a snack, a friend, the start of a swing), an ollie takes it off the end of a window and further than a hop, and when
// it's bored it goes for a ride (up and back, an ollie or a kickflip on the way once it's good at it). GRIND (level 15):
// riding a window top at speed is a grind (sparks), and a grind session hops from rail to rail: up onto a window top,
// along it, off the end onto the next one, and off the last one down to wherever it lands.
// Riding happens inside the Hopping state: once it's there it hops off and the chase (or trip) carries on.
public partial class GummyBrain
{
    [Header("Skateboard")]
    [Tooltip("When it's bored: chance to go for a ride on its skateboard (once it has one).")]
    [Range(0f, 1f)] public float skateChance = 0.12f;
    [Tooltip("Pushes off this hard (world units / s², x size / 0.6).")]
    public float skateAccel = 9f;
    [Tooltip("Brakes this hard (world units / s², x size / 0.6); harder when it has to stop short.")]
    public float skateBrake = 8f;

    BuddySkate board;
    enum Ride { Mount, Roll, Air }
    Ride ride;
    bool riding, rideOllie, rideFun, rideRunup, rideTrick, rideFlip;
    float rideT, rideTo, rideBack, rideRunX, rideSlow, rideAir, rideCheck;
    int rideLegs;
    enum GrindPhase { None, ToRail, OffEnd }
    GrindPhase grind;
    int railsLeft, grindSide;
    bool rideMantle; // popped straight up beside a window side: over onto the top once it's above it

    float SkateTop => skills ? skills.SkateSpeed : 0f;
    bool CanSkate => board && board.isActiveAndEnabled && SkateTop > 0f && body.Grounded && !body.Held && !body.IsStuck && !body.OnRope;
    /// <summary>On its skateboard right now.</summary>
    public bool Riding => riding;

    /// <summary>Go for a ride now (chat "skate"; trick = an ollie / kickflip on the way). False if it's busy or can't.</summary>
    public bool SkateNow(bool trick) => Current == State.Idle && PlanFunRide(trick);

    /// <summary>Go grinding now (chat "grind"): rail to rail over the window tops. False if it's busy or there's no rail near.</summary>
    public bool GrindNow() => Current == State.Idle && PlanGrindLine();

    // the next bit of the way is a long ride along this surface (or an ollie): on the board (true = riding now)
    bool TryRideLeg()
    {
        if (!riding && !CanSkate) return false;
        if (!NextLeg(false, out float to, out bool ollie) || !WorthRiding(to, ollie)) return false;
        if (!riding) Mount();
        SetLeg(to, ollie);
        return true;
    }

    // the ride stops at the end of the surface it's on: still a long way to go along here? (an ollie always is)
    bool WorthRiding(float to, bool ollie)
    {
        if (ollie) return true;
        Vector2 span = RideSpan();
        float m = body.size * 0.45f, end = Mathf.Clamp(to, span.x + m, span.y - m);
        return Mathf.Abs(end - body.Center.x) > BuddyRoute.SkateFrom(body.size) * 0.5f;
    }

    // where the board takes it next (any = also short rides, while it's on it already)
    bool NextLeg(bool any, out float to, out bool ollie)
    {
        to = 0f;
        ollie = false;
        float x = body.Center.x, far = BuddyRoute.SkateFrom(body.size);
        bool trip = edgeWalk && (!Food.HasValue || foodClimb);
        if (trip)
        {
            if ((edgeKind != EdgeKind.Visit && edgeKind != EdgeKind.Swing && edgeKind != EdgeKind.Glide && edgeKind != EdgeKind.Zip && edgeKind != EdgeKind.Float && edgeKind != EdgeKind.Jet) || float.IsNaN(visitX)) return false;
            to = visitX;
            return any || Mathf.Abs(to - x) > far;
        }
        if (!chasing || !Food.HasValue || !CanReachFood()) return false;
        if (step.move == BuddyRoute.Move.Ollie) { to = step.takeoffX; ollie = true; return true; }
        if (step.move == BuddyRoute.Move.Walk) to = step.landX;
        else if (step.wall) to = step.takeoffX;
        else to = step.takeoffX;
        return any || Mathf.Abs(to - x) > far;
    }

    void Mount()
    {
        riding = true;
        ride = Ride.Mount;
        rideT = rideSlow = rideAir = 0f;
        rideFun = rideOllie = rideTrick = rideFlip = rideRunup = false;
        rideLegs = 0;
        body.Skate(board.Lift);
        board.Show();
        hop = Hop.Pause; // (still Hopping: the chase / trip carries on once it's off the board)
        hopT = 0f;
        Moment?.Invoke("skate");
    }

    void SetLeg(float to, bool ollie)
    {
        Vector2 span = RideSpan();
        float m = body.size * 0.45f;
        rideOllie = ollie;
        rideRunup = false;
        rideT = rideSlow = 0f;
        rideCheck = 0.3f;
        if (!ollie) { rideTo = Mathf.Clamp(to, span.x + m, span.y - m); return; }
        // an ollie: rides at the end of the surface flat out; backs up first if it's too close to get up to speed
        rideTo = to;
        float runway = body.size * 2.2f;
        if (grind == GrindPhase.None && (to - body.Center.x) * step.side < runway)
        {
            rideRunup = true;
            rideRunX = Mathf.Clamp(to - step.side * runway * 1.4f, span.x + m, span.y - m);
            if ((to - rideRunX) * step.side < body.size) rideRunup = false; // no room to back up: goes anyway
        }
    }

    // where it can ride: the surface it's on (the floor between windows that stand on it, or a window top)
    Vector2 RideSpan()
    {
        Rect r = ScreenBounds.WorldRect;
        Vector2 sp = body.SupportSpan;
        float lo = Mathf.Max(sp.x, r.xMin), hi = Mathf.Min(sp.y, r.xMax);
        if (!body.OnPlatform)
        {
            BuddyRoute.Build(Me().height, body.solidWindows);
            int i = BuddyRoute.SurfaceAt(body.Center.x, body.Extent.yMin - body.SkateLift, false);
            if (i >= 0) { var s = BuddyRoute.Surfaces[i]; lo = Mathf.Max(lo, s.x0); hi = Mathf.Min(hi, s.x1); }
        }
        return new Vector2(lo, hi);
    }

    void UpdateRide(float dt)
    {
        rideT += dt;
        float x = body.Center.x, vx = body.Velocity.x, top = SkateTop * (rideFun ? 0.85f : 1f);
        switch (ride)
        {
            case Ride.Mount:
                body.Coast();
                body.Pose = Vector2.Lerp(new Vector2(1.08f, 0.93f), Vector2.one, Smooth(rideT / 0.25f));
                if (rideT > 0.25f) { ride = Ride.Roll; rideT = 0f; }
                return;

            case Ride.Air:
                hop = Hop.Air;
                board.Grinding = false;
                body.Coast();
                if (rideMantle && body.Extent.yMin - body.SkateLift > step.landY + 0.02f)
                {
                    float over = step.landX - body.Center.x;
                    body.Hop(new Vector2(Mathf.Clamp(over / 0.35f - body.Velocity.x, -6f, 6f), 0.5f), 0.6f);
                    rideMantle = false;
                }
                body.Lean = 0f;
                body.Pose = Vector2.Lerp(launchPose, new Vector2(1.04f, 0.96f), Smooth(rideT / 0.2f)); // tucked up on the board
                if (rideT > 0.08f && body.Grounded) { Landed(); return; }
                if (rideT > 3f) { EndRide(); Enter(State.Recover); }
                return;
        }

        // rolling
        if (!body.Grounded)
        {
            rideAir += dt; // rolled off something (or a window moved away under it)
            if (rideAir > 0.15f) { ride = Ride.Air; rideT = 0f; hop = Hop.Air; }
            return;
        }
        rideAir = 0f;
        hop = Hop.Pause;
        bool trip = edgeWalk && (!Food.HasValue || foodClimb);
        // along a window top at speed: that's a grind (once it knows how)
        bool grinding = skills && skills.Has(BuddySkills.Skill.Grind) && body.OnPlatform && Mathf.Abs(vx) > 2f * Mathf.Sqrt(S);
        if (grinding && !board.Grinding) Moment?.Invoke("grind");
        board.Grinding = grinding;

        // a snack turned up on a ride for fun: off after it (on the board if it's far)
        if (rideFun && Food.HasValue && !Stuck && CanReachFood())
        {
            rideFun = false;
            grind = GrindPhase.None;
            chasing = true;
            chaseFood = Food.Value;
            if (NextLeg(true, out float to0, out bool ol0)) SetLeg(to0, ol0);
            else { LegDone(true); return; }
        }
        // chasing: the snack's gone (a friend ate it): stops; it moved: rides there instead
        if (chasing && !trip)
        {
            if (!Food.HasValue) { chasing = false; rideOllie = false; rideTo = x + vx * 0.2f; }
            else if ((rideCheck -= dt) <= 0f)
            {
                rideCheck = 0.3f;
                if (NextLeg(true, out float to1, out bool ol1)) { if (ol1 != rideOllie || Mathf.Abs(to1 - rideTo) > 0.2f) SetLeg(to1, ol1); }
                else { LegDone(true); return; }
            }
        }

        float want, dx = rideTo - x;
        if (grind == GrindPhase.OffEnd)
        {
            // flat out off the end of the last rail (it drops down to wherever it lands)
            want = grindSide * top;
            body.Drive(want, skateAccel * S);
            body.Lean = -grindSide * 4f;
            rideSlow = Mathf.Abs(vx) < 0.5f ? rideSlow + dt : 0f; // (a window or the screen edge at the end)
            if (rideSlow > 1.2f || rideT > 8f) { grind = GrindPhase.None; LegDone(true); }
            return;
        }
        if (rideOllie && !rideRunup)
        {
            // flat out at the edge, pops at the take-off spot
            want = step.side * top;
            if ((rideTo - x) * step.side <= Mathf.Max(0.04f, Mathf.Abs(vx) * dt * 1.5f)) { Pop(); return; }
        }
        else
        {
            float goal = rideRunup ? rideRunX : rideTo;
            dx = goal - x;
            want = Mathf.Abs(dx) < 0.2f * body.size ? 0f : Mathf.Sign(dx) * Mathf.Min(top, Mathf.Sqrt(2f * skateBrake * S * Mathf.Max(Mathf.Abs(dx) - 0.05f, 0f)));
            bool there = (Mathf.Abs(dx) < 0.3f * body.size && Mathf.Abs(vx) < 1.2f * Mathf.Sqrt(S)) || (Mathf.Abs(dx) < 0.6f * body.size && Mathf.Abs(vx) < 0.3f); // (close enough: it hops the rest)
            if (rideRunup) { if (there || (Mathf.Abs(dx) < 0.4f * body.size && vx * dx <= 0f)) rideRunup = false; }
            else
            {
                // a trick on the way (for fun): an ollie / kickflip at speed, where there's room to come down and stop
                if (rideFun && rideTrick && Mathf.Abs(vx) > top * 0.6f && vx * dx > 0f)
                {
                    float vy = GummyBody.JumpSpeed(BuddyRoute.OlliePop * S * (rideFlip ? 1.15f : 0.9f), body.gravity, body.Drag);
                    float air = 2f * vy / Mathf.Max(body.gravity, 1f), need = Mathf.Abs(vx) * air + vx * vx / (2f * skateBrake * S) + 0.3f;
                    if (Mathf.Abs(dx) > need && Mathf.Abs(dx) < need + 1.5f) { FunPop(vy); return; }
                }
                if (there) { LegDone(false); return; }
            }
        }
        // push or brake (harder if it has to stop short)
        bool braking = want * vx < 0f || Mathf.Abs(want) < Mathf.Abs(vx);
        float acc = skateAccel * S;
        if (braking)
        {
            float stop = Mathf.Max(Mathf.Abs(dx) - 0.05f, 0.05f);
            acc = Mathf.Min(Mathf.Max(skateBrake * S, vx * vx / (2f * stop) * 1.15f), 40f);
        }
        body.Drive(want, acc);
        body.Lean = -Mathf.Clamp((want - vx) * 0.5f, -1f, 1f) * 6f; // into the push, back when it brakes
        float bob = braking ? 0f : 0.025f * Mathf.Sin(rideT * 10f) * Mathf.Clamp01(Mathf.Abs(want - vx));
        body.Pose = new Vector2(1.04f + bob, 0.96f - bob); // knees bent
        // not getting anywhere (a friend or a window in the way)
        rideSlow = Mathf.Abs(vx) < 0.5f && Mathf.Abs(dx) > 0.12f * body.size ? rideSlow + dt : 0f;
        if (rideSlow > 1.2f || rideT > 15f) LegDone(true);
    }

    // at the end of this bit: on to the next (fun: back again; chasing: another long ride / an ollie), or off the board
    void LegDone(bool blocked)
    {
        if (rideFun && !blocked && rideLegs > 0)
        {
            rideLegs--;
            float back = rideBack;
            rideBack = rideTo;
            rideTo = back;
            rideT = 0f;
            return;
        }
        bool trip = edgeWalk && (!Food.HasValue || foodClimb);
        if (!rideFun && !blocked && chasing)
        {
            if (!trip) { CheckLanding(); planAt = -10f; }
            if (NextLeg(false, out float to, out bool ollie) && WorthRiding(to, ollie)) { SetLeg(to, ollie); return; }
        }
        EndRide();
        hop = Hop.Pause;
        hopT = 0f;
        pauseFor = 0.12f;
    }

    // ollie off the end: the pop that comes down where the route said (worked out from where it really is)
    void Pop()
    {
        float dy = step.landY - (body.Extent.yMin - body.SkateLift);
        Vector2 v0;
        rideMantle = step.wall && step.move != BuddyRoute.Move.Ollie;
        bool ok;
        if (rideMantle)
        {
            // beside a window side: straight up (board and all), over onto the top once it's above it
            v0 = new Vector2(0f, GummyBody.JumpSpeed(dy + 0.9f * S, body.gravity, body.Drag));
            ok = body.Extent.yMax + dy + 0.9f * S < ScreenBounds.CeilingAt(body.Center);
        }
        else ok = step.move == BuddyRoute.Move.Ollie
            ? BuddyRoute.OllieLaunch(Me(), step.landX - body.Center.x, dy, SkateTop * 1.35f, out v0)
            : LeapVelocity(dy, step.landX - body.Center.x, Mathf.Max(dy, 0.5f), out v0, out _); // (up onto a rail: a big one, board and all)
        if (!ok)
        {
            banned[step.key] = Time.time + 120f; // too slow to make it: another way
            grind = GrindPhase.None;
            LegDone(true);
            return;
        }
        if (!rideFun) Expect();
        bool flip = skills && skills.Kickflips && Random.value < 0.4f;
        body.Hop(v0 - body.Velocity, 0.8f);
        board.Ollie(flip);
        ride = Ride.Air;
        rideT = 0f;
        rideOllie = false;
        hop = Hop.Air;
        Moment?.Invoke(flip ? "kickflip" : "ollie");
    }

    // a trick just for fun: straight up with the board, carries on rolling
    void FunPop(float vy)
    {
        body.Hop(new Vector2(0f, vy), 0.6f);
        board.Ollie(rideFlip);
        ride = Ride.Air;
        rideT = 0f;
        hop = Hop.Air;
        rideTrick = rideLegs > 0 && Random.value < 0.5f; // maybe another one on the way back
        Moment?.Invoke(rideFlip ? "kickflip" : "ollie");
    }

    void Landed()
    {
        ride = Ride.Roll;
        rideT = 0f;
        hop = Hop.Pause;
        if (grind == GrindPhase.ToRail)
        {
            // on the rail: along it, then onto the next one (or off the end)
            bool onRail = body.OnPlatform && Mathf.Abs(body.Extent.yMin - body.SkateLift - step.landY) < 0.25f;
            if (onRail)
            {
                grindSide = step.side;
                railsLeft--;
                rideOllie = false;
                if (railsLeft > 0 && PickRail(out BuddyRoute.Step next, grindSide)) { StartRailHop(next); return; }
                if (OpenEnd(grindSide)) { grind = GrindPhase.OffEnd; return; }
                // the end of this one is against the screen edge or a window: grinds to it and stops
                grind = GrindPhase.None;
                Vector2 sp = RideSpan();
                rideTo = grindSide > 0 ? sp.y - body.size * 0.6f : sp.x + body.size * 0.6f;
                rideLegs = 0;
                rideCheck = 0.5f;
                return;
            }
            grind = GrindPhase.None; // missed: rolls to a stop wherever it came down
            rideLegs = 0;
        }
        else if (grind == GrindPhase.OffEnd) { grind = GrindPhase.None; rideLegs = 0; }
        else if (rideFun) return; // rolls on
        // after an ollie on the way somewhere: rolls to a stop on what it landed on, then on with the chase
        Vector2 span = RideSpan();
        float m = body.size * 0.45f, vx = body.Velocity.x;
        rideTo = Mathf.Clamp(body.Center.x + vx * 0.3f, span.x + m, span.y - m);
        rideOllie = false;
        rideCheck = 0.5f;
    }

    void EndRide()
    {
        if (!riding) return;
        riding = false;
        rideFun = rideOllie = rideMantle = false;
        grind = GrindPhase.None;
        body.StopSkating();
        body.Coast();
        if (board) board.Hide();
        body.Pose = Vector2.one;
    }

    // ------------------------------------------------------------------ grinding: rail to rail

    bool PlanGrindLine()
    {
        if (!CanSkate || !skills || !skills.Has(BuddySkills.Skill.Grind)) return false;
        CheckLayout();
        if (!PickRail(out BuddyRoute.Step first, 0)) return false;
        Enter(State.Hopping);
        chasing = leapPlanned = edgeWalk = false;
        hopsLeft = 0;
        Mount();
        rideFun = true;
        rideTrick = false;
        rideLegs = 0;
        railsLeft = Random.Range(2, 4);
        StartRailHop(first);
        return true;
    }

    // a hop / leap / ollie from where it is onto a window top long enough to grind (side 0 = either way)
    bool PickRail(out BuddyRoute.Step pick, int side)
    {
        pick = default;
        float total = 0f, minLen = 2.5f * S;
        foreach (var kv in BuddyRoute.MovesFrom(Me(), Abilities()))
        {
            BuddyRoute.Step st = kv.Key;
            BuddyRoute.Surface to = kv.Value;
            if (to.win == 0 || to.x1 - to.x0 < minLen || IsBanned(st.key)) continue; // (beside a window side too: an arc up over its corner)
            if (st.move != BuddyRoute.Move.Ollie && st.move != BuddyRoute.Move.Hop && st.move != BuddyRoute.Move.Leap) continue;
            if (side != 0 && st.side != side) continue;
            if (!EndOpen(to, st.side)) continue; // it'd grind into the screen edge or a window
            total += 1f;
            if (Random.value * total < 1f) pick = st;
        }
        return total > 0f;
    }

    // the end of the rail it's on (this way) is free: off it, it comes down somewhere
    bool OpenEnd(int side)
    {
        Vector2 sp = RideSpan();
        var s = new BuddyRoute.Surface { x0 = sp.x, x1 = sp.y, y = body.Extent.yMin - body.SkateLift, win = 1 };
        return EndOpen(s, side);
    }

    bool EndOpen(BuddyRoute.Surface s, int side)
    {
        Rect r = ScreenBounds.WorldRect;
        float end = side > 0 ? s.x1 : s.x0, room = body.size * 1.6f;
        if (side > 0 ? end > r.xMax - room : end < r.xMin + room) return false;
        // a window standing just past the end, up to its height (it'd ride into its side)
        var wins = DesktopPlatforms.Windows;
        for (int k = 0; k < wins.Count; k++)
        {
            WindowBox w = wins[k];
            if (!w.movable || w.id == s.win) continue;
            float near = side > 0 ? w.xMin : w.xMax;
            if ((near - end) * side > -0.05f && (near - end) * side < room && w.yMax > s.y + 0.05f && w.yMin < s.y + body.size * 1.5f) return false;
        }
        return true;
    }

    void StartRailHop(BuddyRoute.Step hop)
    {
        step = hop; // (the pop goes by step)
        grind = GrindPhase.ToRail;
        grindSide = hop.side;
        SetLeg(hop.takeoffX, true);
    }

    // bored: a ride up the taskbar / window top and back
    bool PlanFunRide(bool tricks)
    {
        if (!CanSkate) return false;
        Vector2 span = RideSpan();
        float m = body.size * 0.8f, x = body.Center.x, lo = span.x + m, hi = span.y - m;
        if (hi - lo < 3.5f * S) return false; // too short to bother
        float to = hi - x > x - lo ? Random.Range(Mathf.Lerp(x, hi, 0.6f), hi) : Random.Range(lo, Mathf.Lerp(x, lo, 0.6f));
        if (Mathf.Abs(to - x) < 2f * S) return false;
        Enter(State.Hopping);
        chasing = leapPlanned = edgeWalk = false;
        hopsLeft = 0;
        Mount();
        rideFun = true;
        rideTo = to;
        rideBack = Mathf.Clamp(x, lo, hi);
        rideLegs = tricks || Random.value < 0.6f ? 1 : 0;
        rideTrick = skills && skills.Ollies && (tricks || Random.value < 0.7f);
        rideFlip = skills && skills.Kickflips && (tricks || Random.value < 0.5f);
        return true;
    }
}
