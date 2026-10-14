using UnityEngine;

// Rope moves (BuddySkills SWING, with BuddyRope): swings from a window's bottom edge or the top of the screen, letting go
// when it'll come down where it wants (it keeps predicting its landing while it swings), rope climbs up a window's
// corner and over onto its top, the odd swing just for fun, and hanging from your cursor (LASSO: BuddyRope runs that
// ride, the brain just keeps still and enjoys it). ZIPLINE: strings a line from a window's top corner down to a lower
// spot (or uses one that's up, even one a friend strung) and slides down it hanging from a loop of rope.
public partial class GummyBrain
{
    [Header("Rope")]
    [Tooltip("When it's bored: chance to go for a swing (once it knows how).")]
    [Range(0f, 1f)] public float swingChance = 0.12f;
    [Tooltip("Climbs a rope this fast (x size / 0.6).")]
    public float ropeClimbSpeed = 2.4f;

    BuddyRope rope;
    enum RopeJob { Swing, Grapple, Hang, Zip }
    RopeJob ropeJob;
    BuddyRoute.Step ropeStep, funStep;
    bool funSwing;
    float ropeStuck, ropeLastDx;
    bool pumped;
    int funLayout;
    Zipline zip;
    float zipS, zipV;
    int zipPhase; // 0 stringing it, 1 on it
    [Tooltip("When it's bored on a high window: chance to string a zipline down somewhere (once it knows how).")]
    [Range(0f, 1f)] public float zipChance = 0.12f;

    /// <summary>String a zipline now (chat "zipline"). False if it's busy or there's nowhere to zip to from here.</summary>
    public bool ZipNow() => Current == State.Idle && body.Grounded && !body.Held && PlanFunZip();

    /// <summary>Hang from your cursor (BuddyRope's lasso): stays out of the way until the rope's gone. False if it's busy.</summary>
    public bool BeginHang()
    {
        if (!rope || body.Held || body.IsStuck || (Current != State.Idle && Current != State.Recover)) return false;
        Enter(State.Swing);
        ropeJob = RopeJob.Hang;
        return true;
    }

    /// <summary>Go for a swing now (chat). False if it's busy or there's nothing to swing on near it.</summary>
    public bool SwingNow() => Current == State.Idle && body.Grounded && !body.Held && PlanFunSwing();

    // a swing or rope climb: jumps off as planned and throws the rope
    bool StartRopeMove(BuddyRoute.Step st, bool fun)
    {
        if (!rope || rope.Active) return false;
        BuddyRope.Use use = fun ? BuddyRope.Use.Fun : st.move == BuddyRoute.Move.Grapple ? BuddyRope.Use.Grapple : BuddyRope.Use.Swing;
        if (!rope.Throw(st.hook, st.hang, Mathf.Max(st.rope, st.caught) * 1.1f + 0.05f, use, st.wait)) return false;
        body.Hop(st.launch - body.Velocity, 0.5f);
        ropeStep = st;
        funSwing = fun;
        ropeStuck = 0f;
        ropeLastDx = body.Center.x - st.hang.x;
        pumped = false;
        Enter(State.Swing);
        ropeJob = st.move == BuddyRoute.Move.Grapple ? RopeJob.Grapple : RopeJob.Swing;
        Moment?.Invoke(ropeJob == RopeJob.Grapple ? "grapple" : "swing");
        return true;
    }

    void UpdateSwing(float now, float dt)
    {
        lastAttention = now;
        if (body.Held) { Enter(State.Recover); return; } // (that lets go of the rope)
        if (ropeJob == RopeJob.Zip) { UpdateZip(dt); return; }
        if (!rope || !rope.Active)
        {
            // the rope's gone: the ride's over, or it missed
            if (ropeJob == RopeJob.Hang) Enter(State.Recover);
            else AfterRope();
            return;
        }
        if (ropeJob == RopeJob.Hang)
        {
            float k = 0.04f * Mathf.Sin(now * 7f);
            body.Pose = new Vector2(0.94f + k, 1.06f - k); // dangling, kicking its feet
            happyUntil = Mathf.Max(happyUntil, now + 0.3f);
            return;
        }
        if (rope.Current != BuddyRope.Mode.On) { body.Pose = launchPose; return; } // the rope's still flying
        if (ropeJob == RopeJob.Grapple) { UpdateGrapple(dt); return; }

        // swinging: kicks its legs at the bottom (like on a playground swing; BuddyRoute plans with the same kick)
        float dx = body.Center.x - ropeStep.hang.x;
        Vector2 vel = body.Velocity;
        if (!pumped && ropeLastDx * ropeStep.side < 0f && dx * ropeStep.side >= 0f && vel.x * ropeStep.side > 0f)
        {
            pumped = true;
            Vector2 kick = vel.normalized * BuddyRoute.Pump;
            body.Hop(kick, 0.3f);
            vel += kick; // (the body picks it up on its next step)
        }
        ropeLastDx = dx;
        body.Pose = pumped ? Vector2.Lerp(new Vector2(1.1f, 0.92f), new Vector2(0.95f, 1.05f), Smooth(stateTime * 4f)) : new Vector2(0.95f, 1.05f);
        // pulls itself up to the planned length (a planned pull, or the rope caught a bit further away than planned)
        body.ReelTo(ropeStep.rope, BuddyRoute.ReelSpeed(body.size));
        // let go at the best moment: it keeps looking ahead along its swing (from where it really is) for where it'd land
        var me = Me();
        bool any = BuddyRoute.BestRelease(me, body.Center, vel, ropeStep.hang, body.RopeLength, ropeStep.rope, ropeStep.side, pumped,
            ropeStep.landY, ropeStep.zone0, ropeStep.zone1, ropeStep.landX, out float when, out _);
        // (and checks that letting go right now really comes down there: the best moment may be just after the kick)
        if (any && when <= dt * 1.2f && BuddyRoute.Flight(body.Center, vel, me, ropeStep.landY, ropeStep.zone0, ropeStep.zone1, out _)) { LetGoRope(); return; }
        if (!any && pumped)
        {
            // it won't make it on this swing: swings back and lets go over where it started (and remembers that didn't work)
            float hw = Mathf.Max(body.Extent.width, body.size * 0.8f) * 0.5f;
            bool back = BuddyRoute.BestRelease(me, body.Center, vel, ropeStep.hang, body.RopeLength, ropeStep.rope, -ropeStep.side, true,
                ropeStep.fromY, ropeStep.from0 + hw * 0.6f, ropeStep.from1 - hw * 0.6f, ropeStep.takeoffX, out float whenBack, out _);
            if (back && whenBack <= dt * 1.2f && BuddyRoute.Flight(body.Center, vel, me, ropeStep.fromY, ropeStep.from0 + hw * 0.6f, ropeStep.from1 - hw * 0.6f, out _)) { LetGoRope(); return; }
        }
        if (stateTime > 5f) LetGoRope(); // (never lined up either way)
    }

    // hand over hand up the rope, then over onto the top
    void UpdateGrapple(float dt)
    {
        float sw = Mathf.Sin(stateTime * 2f * Mathf.PI / 0.45f);
        body.Pose = new Vector2(1f - 0.04f * sw, 1f + 0.06f * sw);
        float topOff = body.Extent.yMax - body.Center.y, shortest = topOff + 0.04f;
        body.ReelTo(shortest, ropeClimbSpeed * Mathf.Sqrt(S));
        bool up = body.RopeLength <= shortest + 0.02f && body.Center.y > ropeStep.hang.y - shortest - 0.15f;
        if (up)
        {
            rope.Release();
            float rise = Mathf.Max(ropeStep.landY - body.Extent.yMin, 0f) + 0.25f * S;
            body.Hop(new Vector2(-body.Velocity.x, GummyBody.JumpSpeed(rise, body.gravity, body.Drag) - body.Velocity.y), 0.8f);
            Enter(State.Hopping);
            chasing = !funSwing;
            hopsLeft = 0;
            hop = Hop.Air;
            hopT = 0f;
            mantle = true; // over onto the top once it's above it (UpdateHop)
            mantleDir = ropeStep.side;
            mantleY = ropeStep.landY;
            mantleX = ropeStep.landX;
            return;
        }
        // not getting any higher (something's in the way): gives up
        ropeStuck = body.RopeStretch > 0.25f ? ropeStuck + dt : 0f;
        if (ropeStuck > 1f || stateTime > 7f) LetGoRope();
    }

    // ------------------------------------------------------------------ zipline

    // at the take-off spot by the corner: string a line there (or use the one that's up), then hop on
    bool StartZip(BuddyRoute.Step st, bool fun)
    {
        if (!rope || rope.Active) return false;
        zip = st.rope < 0f ? Zipline.Find(st.hook, st.hang) : null;
        if (st.rope < 0f && (zip == null || !zip.Ready)) { banned[st.key] = Time.time + 30f; return false; } // the line's gone
        if (zip == null)
        {
            Surface(st, out long fromWin, out long toWin);
            zip = Zipline.String(body, st.hook, st.hang, fromWin, toWin, rope.color, rope.material, rope.width * S, st.landY, rope.zipThunk, rope.zipSnap);
            Moment?.Invoke("zipline");
        }
        ropeStep = st;
        funSwing = fun;
        zipPhase = 0;
        zipV = 0f;
        if (!fun) Expect();
        Enter(State.Swing);
        ropeJob = RopeJob.Zip;
        return true;
    }

    // the windows the line is tied to (not desktop icons: those don't count as anchors)
    void Surface(BuddyRoute.Step st, out long fromWin, out long toWin)
    {
        fromWin = toWin = 0;
        foreach (var w in DesktopPlatforms.Windows)
        {
            if (Mathf.Abs(w.yMax - (st.hook.y - 0.05f)) < 0.06f && st.hook.x >= w.xMin - 0.06f && st.hook.x <= w.xMax + 0.06f) fromWin = w.id;
            if (Mathf.Abs(w.yMax - st.landY) < 0.06f && st.landX >= w.xMin && st.landX <= w.xMax) toWin = w.id;
        }
    }

    void UpdateZip(float dt)
    {
        if (zip == null || (!zip.Ready && zipPhase == 1)) { LetGoZip(); return; } // it snapped
        float topOff = Mathf.Max(body.Extent.yMax - body.Center.y, body.size * 0.35f), hold = topOff + BuddyRoute.ZipHang; // (hanging from the pulley handle)
        if (zipPhase == 0)
        {
            // throwing it out (the line shoots down from the corner), then a hop out onto it
            body.Pose = launchPose;
            if (!zip.Ready) { if (stateTime > 2f) { LetGoZip(); } return; }
            float hw = Mathf.Max(body.Extent.width, body.size * 0.8f) * 0.5f;
            zipS = Mathf.Min(zip.Along(zip.From + (zip.To - zip.From).normalized * (hw * 1.4f / Mathf.Max(Mathf.Abs((zip.To - zip.From).normalized.x), 0.3f))), zip.Length * 0.5f);
            Vector2 at = zip.HangAt(zipS);
            body.Hop(new Vector2(Mathf.Clamp((at.x - body.Center.x) / 0.25f, -5f, 5f) - body.Velocity.x, 3f - body.Velocity.y), 0.6f);
            if (!rope.Hold(at, Mathf.Max((body.Center - at).magnitude, hold))) { LetGoZip(); return; }
            body.ReelTo(hold, 6f * Mathf.Sqrt(S));
            zipPhase = 1;
            zip.Ride(body, zipS);
            return;
        }
        // sliding: gravity along the line, some drag, gently braking near the end
        Vector2 d = (zip.To - zip.From).normalized;
        float left = zip.Length - zipS, top = 7f * Mathf.Sqrt(S);
        zipV += (body.gravity * -d.y * 0.55f - 0.4f * zipV) * dt;
        float brakeTo = Mathf.Sqrt(2f * 6f * Mathf.Max(left - 0.1f, 0f)) + 1.2f;
        zipV = Mathf.Min(zipV, Mathf.Min(top, brakeTo));
        zipS += zipV * dt;
        Vector2 p = zip.HangAt(zipS); // (from the rope as it hangs: its sag, the dip under it)
        rope.MoveHold(p);
        body.ReelTo(hold, 6f * Mathf.Sqrt(S));
        zip.Ride(body, zipS);
        body.Pose = Vector2.Lerp(body.Pose, new Vector2(0.94f, 1.06f), 0.2f); // hanging on, feet dangling
        happyUntil = Mathf.Max(happyUntil, Time.time + 0.2f);
        if (left <= 0.03f || stateTime > 12f) LetGoZip();
    }

    void LetGoZip()
    {
        if (zip) zip.Off(body);
        zip = null;
        if (rope.Active) rope.Release();
        body.Hop(Vector2.zero, 1f);
        AfterRope();
    }

    // bored on a high window: a zipline down somewhere (it walks / rides over to the corner first)
    BuddyRoute.Step funZip;
    bool PlanFunZip()
    {
        if (!rope || rope.Active || !skills || !skills.Has(BuddySkills.Skill.Zip) || !body.OnPlatform) return false;
        CheckLayout();
        float total = 0f;
        bool found = false;
        foreach (var kv in BuddyRoute.MovesFrom(Me(), Abilities()))
        {
            BuddyRoute.Step st = kv.Key;
            if (st.move != BuddyRoute.Move.Zip || IsBanned(st.key)) continue;
            float w = Mathf.Abs(st.landX - st.takeoffX) + 1f; // longer ones more likely
            total += w;
            if (Random.value * total < w) { funZip = st; found = true; }
        }
        if (!found) return false;
        funLayout = layout;
        visitX = funZip.takeoffX;
        edgeKind = EdgeKind.Zip;
        edgeSide = visitX < body.Center.x ? -1 : 1;
        edge = new GummyBody.Edge { axis = 0, pos = visitX, sign = -edgeSide, from = -1e4f, to = 1e4f };
        edgeTarget = new Vector2(visitX, body.Extent.yMin);
        hop = Hop.Squash;
        hopT = 0f;
        Enter(State.Hopping);
        chasing = true;
        edgeWalk = true;
        leapPlanned = false;
        TryRideLeg(); // far: on the skateboard
        return true;
    }

    // at the corner for a fun zipline: go (if the windows are still where they were)
    void ArriveForZip()
    {
        CheckLayout();
        if (layout == funLayout && StartZip(funZip, true)) return;
        Enter(State.Idle);
    }

    void LetGoRope()
    {
        rope.Release();
        body.Hop(Vector2.zero, 1.5f); // (lands on its feet, doesn't splat and stick)
        AfterRope();
    }

    // flying off the rope: lands (like one of its hops), then carries on after the snack (or just sits, after a fun one)
    void AfterRope()
    {
        Enter(State.Hopping);
        chasing = !funSwing;
        hopsLeft = 0;
        hop = Hop.Air;
        hopT = 0f;
    }

    // bored: a swing (or rope climb) somewhere near, for fun
    bool PlanFunSwing()
    {
        if (!rope || rope.Active || !skills || !skills.Has(BuddySkills.Skill.Swing)) return false;
        CheckLayout();
        if (!BuddyRoute.FunSwing(Me(), Abilities(), IsBanned, out funStep)) return false;
        funLayout = layout;
        visitX = funStep.takeoffX;
        edgeKind = EdgeKind.Swing;
        edgeSide = visitX < body.Center.x ? -1 : 1;
        edge = new GummyBody.Edge { axis = 0, pos = visitX, sign = -edgeSide, from = -1e4f, to = 1e4f };
        edgeTarget = new Vector2(visitX, body.Extent.yMin);
        hop = Hop.Squash;
        hopT = 0f;
        Enter(State.Hopping);
        chasing = true;
        edgeWalk = true;
        leapPlanned = false;
        TryRideLeg(); // far: on the skateboard
        return true;
    }

    // at the take-off spot of a fun swing: go (if the windows are still where they were)
    void ArriveForSwing()
    {
        CheckLayout();
        if (layout == funLayout && StartRopeMove(funStep, true)) return;
        Enter(State.Idle);
    }
}
