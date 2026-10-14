using UnityEngine;

// Balloon (BuddySkills BALLOON, with BuddyBalloon): beside a window top too high to jump to it blows up a balloon, floats
// straight up past the window's end, drifts across above it and lets go (the balloon floats off), dropping onto it (a
// route move: BuddyRoute.Move.Float). Poke the balloon with your cursor and it pops: down it goes. Bored down low, it
// floats up somewhere for fun. Like the paper plane, this happens inside the Hopping state.
public partial class GummyBrain
{
    [Header("Balloon")]
    [Tooltip("When it's bored down low: chance to float up onto a higher window on a balloon (once it has one).")]
    [Range(0f, 1f)] public float floatChance = 0.12f;

    BuddyBalloon balloon;
    bool floating, floatFun;
    float floatT, floatStuck, floatTop;
    int floatPhase; // 0 = blowing it up, 1 = going up, 2 = across
    BuddyRoute.Step floatStep;

    /// <summary>Hanging from its balloon right now (or blowing it up).</summary>
    public bool Floating => floating;
    float BalloonDrift => skills ? skills.BalloonDrift : 0f;

    /// <summary>Float up somewhere now (chat "balloon"). False if it's busy or there's nowhere higher to float to from here.</summary>
    public bool FloatNow() => Current == State.Idle && body.Grounded && !body.Held && PlanFunFloat();

    // at the take-off spot: it blows up the balloon (standing there), then up it goes
    bool StartFloat(BuddyRoute.Step st, bool fun)
    {
        if (!balloon || !balloon.isActiveAndEnabled || BalloonDrift <= 0f || !body.Grounded) return false;
        if (riding) EndRide();
        floatStep = st;
        floatFun = fun;
        floating = true;
        floatT = floatStuck = 0f;
        floatPhase = 0;
        floatTop = st.landY + BuddyRoute.FloatClear * S;
        balloon.Inflate();
        body.Glide(new Vector2(0f, -0.5f), 8f * S); // (stands still on its feet while it blows: no sticking, no sliding)
        if (!fun) Expect();
        hop = Hop.Air;
        hopT = 0f;
        Moment?.Invoke("float");
        return true;
    }

    void UpdateFloat(float dt)
    {
        floatT += dt;
        hop = Hop.Air;
        if (!balloon || !balloon.Holding) { EndFloat(); hopT = 0f; landingCheck = false; return; } // you popped it: down it goes (not the move's fault)
        float x = body.Center.x, feet = body.Extent.yMin;
        float rise = BuddyRoute.FloatRise(body.size), across = BuddyRoute.FloatAcross(body.size);
        if (floatPhase == 0)
        {
            // blowing it up: puffs, standing still
            float puff = Mathf.Sin(floatT * 16f);
            body.Pose = new Vector2(1f + 0.05f * puff, 1f - 0.04f * puff);
            body.Glide(new Vector2(0f, -0.5f), 8f * S);
            if (!balloon.Ready) return;
            floatPhase = 1;
            body.Pose = new Vector2(0.97f, 1.04f);
        }

        Vector2 want;
        float bob = 0.15f * Mathf.Sin(floatT * 2.3f);
        if (floatPhase == 1)
        {
            // straight up where it stands (gently at first, slowing near the top)
            float togo = floatTop - feet;
            want = new Vector2(Mathf.Clamp((floatStep.takeoffX - x) * 2f, -0.4f, 0.4f) + bob, rise * Mathf.Clamp(togo * 1.5f + 0.25f, 0.3f, 1f));
            if (togo < 0.04f) floatPhase = 2;
        }
        else
        {
            // across above the top it's going to, holding its height, then lets go over the spot
            float dx = floatStep.landX - x;
            if (Mathf.Abs(dx) < 0.25f * body.size) { FloatLetGo(); return; }
            want = new Vector2(Mathf.Clamp(dx * 2.5f, -1f, 1f) * across, Mathf.Clamp((floatTop - feet) * 3f, -0.6f, 0.6f) + 0.3f * bob);
        }
        body.Glide(want, 5f * S);
        body.Lean = Mathf.Clamp(-body.Velocity.x * 5f, -12f, 12f); // hangs back a little as it drifts
        body.Pose = new Vector2(0.97f, 1.04f) + 0.015f * Mathf.Sin(floatT * 7f) * new Vector2(-1f, 1f); // dangling, legs kicking

        // bumped into something it didn't expect (a window moved over it): lets go
        floatStuck = body.Velocity.magnitude < 0.2f * rise ? floatStuck + dt : 0f;
        if (floatStuck > 0.8f || floatT > 16f) { EndFloat(); hopT = 0f; }
    }

    // over the spot: lets go (the balloon floats off) and drops on
    void FloatLetGo()
    {
        bool fun = floatFun;
        EndFloat();
        hop = Hop.Air;
        hopT = 0f;
        if (fun) { chasing = false; hopsLeft = 0; } // just sits after a fun one
    }

    void EndFloat()
    {
        if (!floating) return;
        floating = false;
        body.StopGlide();
        if (balloon && balloon.Holding) balloon.LetGo();
        body.Pose = Vector2.one;
        body.Lean = 0f;
    }

    // bored down low: up onto a higher window (it hops over to the take-off spot first, maybe on its skateboard)
    BuddyRoute.Step funFloat;
    bool PlanFunFloat()
    {
        if (!balloon || !balloon.isActiveAndEnabled || BalloonDrift <= 0f) return false;
        CheckLayout();
        float total = 0f;
        bool found = false;
        foreach (var kv in BuddyRoute.MovesFrom(Me(), Abilities()))
        {
            BuddyRoute.Step st = kv.Key;
            if (st.move != BuddyRoute.Move.Float || IsBanned(st.key)) continue;
            float w = st.landY - st.fromY + 1f; // higher ones more likely
            total += w;
            if (Random.value * total < w) { funFloat = st; found = true; }
        }
        if (!found) return false;
        funLayout = layout;
        visitX = funFloat.takeoffX;
        edgeKind = EdgeKind.Float;
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

    // at the take-off spot of a fun float: go (if the windows are still where they were)
    void ArriveForFloat()
    {
        CheckLayout();
        if (layout == funLayout && StartFloat(funFloat, true))
        {
            chasing = false;
            edgeWalk = false;
            return;
        }
        Enter(State.Idle);
    }
}
