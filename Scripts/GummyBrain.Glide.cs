using UnityEngine;

// Paper plane (BuddySkills PAPER PLANE, with BuddyPlane): off the end of a window top it folds a paper plane, rides on it
// and glides down to a lower surface far away (a route move: BuddyRoute.Move.Glide), aiming for the spot the route picked
// as it goes. Bored on a high window, it glides somewhere for fun. Like the skateboard, this happens inside the Hopping
// state: once it's down, the chase (or nothing, after a fun one) carries on.
public partial class GummyBrain
{
    [Header("Paper plane")]
    [Tooltip("When it's bored on a high window: chance to glide down somewhere on its paper plane (once it has one).")]
    [Range(0f, 1f)] public float glideChance = 0.15f;

    BuddyPlane plane;
    bool gliding, glideFun;
    float glideT, glideStuck;
    BuddyRoute.Step glideStep;

    /// <summary>On its paper plane right now.</summary>
    public bool Gliding => gliding;
    float GlideRatio => skills ? skills.GlideRatio : 0f;

    /// <summary>Glide down somewhere now (chat "fly"). False if it's busy or there's nowhere to glide to from here.</summary>
    public bool GlideNow() => Current == State.Idle && body.Grounded && !body.Held && PlanFunGlide();

    // at the take-off spot: a little hop off the edge, the plane folds out under it
    bool StartGlide(BuddyRoute.Step st, bool fun)
    {
        if (!plane || !plane.isActiveAndEnabled || GlideRatio <= 0f) return false;
        if (riding) EndRide();
        glideStep = st;
        glideFun = fun;
        gliding = true;
        glideT = glideStuck = 0f;
        float up = GummyBody.JumpSpeed(BuddyRoute.GlideHop * S, body.gravity, body.Drag);
        float speed = BuddyRoute.GlideSpeed(body.size);
        body.Hop(new Vector2(st.side * speed * 0.6f - body.Velocity.x, up - body.Velocity.y), 1f);
        plane.Show(st.side);
        if (!fun) Expect();
        hop = Hop.Air;
        hopT = 0f;
        Moment?.Invoke("glide");
        return true;
    }

    void UpdateGlide(float dt)
    {
        glideT += dt;
        hop = Hop.Air;
        float x = body.Center.x, feet = body.Extent.yMin, speed = BuddyRoute.GlideSpeed(body.size);
        int side = glideStep.side;
        // the hop off the edge first (gravity), then it's on the plane
        if (glideT < 0.18f) { body.Pose = Vector2.Lerp(launchPose, Vector2.one, Smooth(glideT / 0.18f)); return; }
        if (glideT > 0.2f && body.Grounded) { GlideLanded(); return; }

        // aim for the landing spot: comes down just steeply enough to get there (never flatter than it can glide)
        float dx = (glideStep.landX - x) * side, drop = feet - glideStep.landY;
        float minSink = speed / Mathf.Max(GlideRatio, 1f), maxSink = speed * 1.6f;
        Vector2 want;
        float edge = side > 0 ? glideStep.from1 : glideStep.from0, hw = Mathf.Max(body.Extent.width, body.size * 0.8f) * 0.5f;
        bool clear = (x - edge) * side > BuddyRoute.GlideClear(hw);
        if (!clear) want = new Vector2(side * speed, 0f); // level until it's past the edge it jumped off
        else if (dx > 0.15f * body.size) want = new Vector2(side * speed, -Mathf.Clamp(speed * drop / dx, minSink, maxSink));
        else want = new Vector2(side * speed * 0.25f, -maxSink); // over the spot (or past it): straight down
        if (clear && drop < -0.3f) want.y = -maxSink; // somehow below where it wanted to land: down onto whatever's there
        body.Glide(want, 14f * S);
        body.Lean = 0f;
        body.Pose = new Vector2(1.03f, 0.97f) + 0.02f * Mathf.Sin(glideT * 9f) * new Vector2(1f, -1f); // riding it, a little nervous

        // flew into a window side (or something else stopped it): off the plane, falls
        glideStuck = body.Velocity.magnitude < speed * 0.25f ? glideStuck + dt : 0f;
        if (glideStuck > 0.4f || glideT > 8f) { EndGlide(); hopT = 0f; }
    }

    void GlideLanded()
    {
        EndGlide();
        hop = Hop.Pause;
        hopT = 0f;
        pauseFor = 0.2f;
        if (glideFun) { chasing = false; hopsLeft = 0; } // just sits after a fun one
    }

    void EndGlide()
    {
        if (!gliding) return;
        gliding = false;
        body.StopGlide();
        if (plane) plane.Drop();
        body.Pose = Vector2.one;
    }

    // bored on a high window: a glide somewhere further down (it hops over to the edge first, maybe on its skateboard)
    BuddyRoute.Step funGlide;
    bool PlanFunGlide()
    {
        if (!plane || !plane.isActiveAndEnabled || GlideRatio <= 0f || !body.OnPlatform) return false;
        CheckLayout();
        if (!BuddyRoute.FunGlide(Me(), Abilities(), IsBanned, out funGlide)) return false;
        funLayout = layout;
        visitX = funGlide.takeoffX;
        edgeKind = EdgeKind.Glide;
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

    // at the take-off spot of a fun glide: go (if the windows are still where they were)
    void ArriveForGlide()
    {
        CheckLayout();
        if (layout == funLayout && StartGlide(funGlide, true))
        {
            chasing = false;
            edgeWalk = false;
            return;
        }
        Enter(State.Idle);
    }
}
