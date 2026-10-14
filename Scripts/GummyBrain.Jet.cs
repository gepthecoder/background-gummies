using UnityEngine;

// Jetpack (the tomato's gadget, BuddySkills.Gadget with Kit.Jetpack, BuddyJetpack): beside a window top too high to jump
// to it fires up its dented two-can jetpack, flies straight up past the window's end, across above it and cuts the engine
// over the spot, dropping on (a route move: BuddyRoute.Move.Jet). It's half broken: about 6 times in 10 it works; otherwise
// it sputters out on the way up (cough, black smoke, down it drops: "STUPID JETPACK!") or backfires (BANG, a tumble
// sideways, dizzy). A move that fails twice from the same spot is skipped for a while, like any other. Bored down low, it
// flies up somewhere for fun. Like the balloon, this happens inside the Hopping state.
public partial class GummyBrain
{
    [Header("Jetpack")]
    [Tooltip("When it's bored down low: chance to fly up onto a higher window with its jetpack (once it has one).")]
    [Range(0f, 1f)] public float jetChance = 0.12f;

    BuddyJetpack jetpack;
    bool jetting, jetFun;
    float jetT, jetStuck, jetTop, jetFrom, jetFailAt, jetBangAt;
    int jetPhase;   // 0 = firing up, 1 = going up, 2 = across
    int jetOutcome; // 0 = it works, 1 = it sputters out on the way up, 2 = it backfires
    BuddyRoute.Step jetStep;

    /// <summary>Flying on its jetpack right now (or firing it up).</summary>
    public bool Jetting => jetting;
    float JetHeight => skills ? skills.JetHeight : 0f;

    /// <summary>Fly up somewhere now (chat "jetpack"). False if it's busy or there's nowhere higher to fly to from here.</summary>
    public bool JetNow() => Current == State.Idle && body.Grounded && !body.Held && PlanFunJet();

    // at the take-off spot: fire it up (standing there), then up it goes (or not)
    bool StartJet(BuddyRoute.Step st, bool fun)
    {
        if (!jetpack || !jetpack.isActiveAndEnabled || JetHeight <= 0f || !body.Grounded) return false;
        if (riding) EndRide();
        jetStep = st;
        jetFun = fun;
        jetting = true;
        jetT = jetStuck = 0f;
        jetPhase = 0;
        jetFrom = body.Extent.yMin;
        jetTop = st.landY + BuddyRoute.JetClear * S;
        float r = Random.value;
        jetOutcome = r < jetpack.works ? 0 : r < jetpack.works + jetpack.sputters ? 1 : 2;
        jetFailAt = Random.Range(0.3f, 0.75f);  // (how far up it gets before it sputters out)
        jetBangAt = Random.Range(0.25f, 0.5f);  // (how long after lift-off it backfires)
        jetpack.Ignite();
        body.Glide(new Vector2(0f, -0.5f), 8f * S); // (stands still while it fires up)
        if (!fun) Expect();
        hop = Hop.Air;
        hopT = 0f;
        Moment?.Invoke("jetpack");
        return true;
    }

    void UpdateJet(float dt)
    {
        jetT += dt;
        hop = Hop.Air;
        if (!jetpack) { EndJet(); return; }
        float x = body.Center.x, feet = body.Extent.yMin;
        float rise = BuddyRoute.JetRise(body.size), across = BuddyRoute.JetAcross(body.size);
        if (jetPhase == 0)
        {
            // firing up: rumbling, crouched, a few coughs of smoke
            float rumble = Mathf.Sin(jetT * 45f);
            body.Pose = new Vector2(1.06f + 0.02f * rumble, 0.92f - 0.02f * rumble);
            body.Glide(new Vector2(0f, -0.5f), 8f * S);
            jetpack.Thrust(0.35f + 0.25f * Mathf.Abs(rumble));
            if (jetT < 0.45f) return;
            jetPhase = 1;
            jetT = 0f;
            body.Pose = new Vector2(0.94f, 1.08f);
            jetpack.Thrust(1f);
            jetpack.Say(jetOutcome == 0 ? 0.35f : 0.15f, "WHOOOSH!", "LIFT OFF!", "HERE WE GO!");
        }

        Vector2 want;
        if (jetPhase == 1)
        {
            // straight up where it stands (full throttle, easing off near the top)
            float togo = jetTop - feet, done = Mathf.Clamp01((feet - jetFrom) / Mathf.Max(jetTop - jetFrom, 0.1f));
            if (jetOutcome == 1 && done >= jetFailAt) { Sputter(); return; }
            if (jetOutcome == 2 && jetT >= jetBangAt) { Backfire(); return; }
            want = new Vector2(Mathf.Clamp((jetStep.takeoffX - x) * 2f, -0.5f, 0.5f), rise * Mathf.Clamp(togo * 1.4f + 0.3f, 0.35f, 1f));
            jetpack.Thrust(1f);
            if (togo < 0.04f) jetPhase = 2;
        }
        else
        {
            // across above the top it's going to, holding its height, then cuts the engine over the spot
            float dx = jetStep.landX - x;
            if (Mathf.Abs(dx) < 0.25f * body.size) { JetLand(); return; }
            want = new Vector2(Mathf.Clamp(dx * 2.5f, -1f, 1f) * across, Mathf.Clamp((jetTop - feet) * 3f, -0.6f, 0.6f));
            jetpack.Thrust(0.7f);
        }
        body.Glide(want, 9f * S);
        body.Lean = Mathf.Clamp(-body.Velocity.x * 4f, -14f, 14f);
        body.Pose = new Vector2(0.95f, 1.06f) + 0.02f * Mathf.Sin(jetT * 30f) * new Vector2(-1f, 1f); // shaking with the thrust

        // bumped into something it didn't expect (a window moved over it): engine off
        jetStuck = body.Velocity.magnitude < 0.2f * rise ? jetStuck + dt : 0f;
        if (jetStuck > 0.6f || jetT > 8f) { EndJet(); hopT = 0f; }
    }

    // over the spot: engine off, it drops on
    void JetLand()
    {
        bool fun = jetFun;
        EndJet();
        hop = Hop.Air;
        hopT = 0f;
        if (jetpack) jetpack.Say(0.35f, "IT WORKED!", "NAILED IT!", "SMOOTH!");
        if (fun) { chasing = false; hopsLeft = 0; } // just sits after a fun one
    }

    // it coughs out on the way up: down it drops (the landing check counts it as a failed move)
    void Sputter()
    {
        EndJet();
        if (jetpack) { jetpack.Sputter(); jetpack.Say(0.9f, "STUPID JETPACK!", "COUGH COUGH", "NOT AGAIN!", "NOOOO"); }
        body.Hop(new Vector2(0f, Mathf.Min(0f, -body.Velocity.y) + 0.5f), 0.4f);
        hop = Hop.Air;
        hopT = 0f;
    }

    // BANG: it shoots off sideways tumbling, plops down dizzy
    void Backfire()
    {
        EndJet();
        int side = Random.value < 0.5f ? -1 : 1;
        Rect r = ScreenBounds.ScreenRectAt(body.Center);
        if (body.Center.x + side * 2f < r.xMin + 0.5f || body.Center.x + side * 2f > r.xMax - 0.5f) side = -side; // (not off the screen)
        if (jetpack) { jetpack.Backfire(); jetpack.Say(0.9f, "KABOOM!", "BAD JETPACK!", "WHOAAA!"); }
        body.Hop(new Vector2(side * 4.5f * Mathf.Sqrt(S) - body.Velocity.x, 3f - body.Velocity.y), 0.5f);
        body.Spin(-side * 9f);
        dizzyUntil = Time.time + dizzyTime + 1f;
        hop = Hop.Air;
        hopT = 0f;
    }

    void EndJet()
    {
        if (!jetting) return;
        jetting = false;
        body.StopGlide();
        if (jetpack) jetpack.Thrust(0f);
        body.Pose = Vector2.one;
        body.Lean = 0f;
    }

    // bored down low: up onto a higher window (it hops over to the take-off spot first, maybe on its skateboard)
    BuddyRoute.Step funJet;
    bool PlanFunJet()
    {
        if (!jetpack || !jetpack.isActiveAndEnabled || JetHeight <= 0f) return false;
        CheckLayout();
        float total = 0f;
        bool found = false;
        foreach (var kv in BuddyRoute.MovesFrom(Me(), Abilities()))
        {
            BuddyRoute.Step st = kv.Key;
            if (st.move != BuddyRoute.Move.Jet || IsBanned(st.key)) continue;
            float w = st.landY - st.fromY + 1f; // higher ones more likely
            total += w;
            if (Random.value * total < w) { funJet = st; found = true; }
        }
        if (!found) return false;
        funLayout = layout;
        visitX = funJet.takeoffX;
        edgeKind = EdgeKind.Jet;
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

    // at the take-off spot of a fun flight: go (if the windows are still where they were)
    void ArriveForJet()
    {
        CheckLayout();
        if (layout == funLayout && StartJet(funJet, true))
        {
            chasing = false;
            edgeWalk = false;
            return;
        }
        Enter(State.Idle);
    }
}
