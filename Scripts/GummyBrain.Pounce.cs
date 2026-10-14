using UnityEngine;

// Pounce (BuddyLaser, the laser pointer's red dot): crouched low it wiggles its back end, then leaps at the dot, the arc
// peaking at the dot's height and coming down where it is. Like the other tricks this happens inside the Hopping state.
public partial class GummyBrain
{
    bool pouncing;
    float pounceT, wiggleFor;
    Vector2 pounceAt;

    /// <summary>Wiggling, about to pounce.</summary>
    public bool Pouncing => pouncing;

    /// <summary>Where the pounce goes (keep it updated while it wiggles: it leaps at where the dot is then).</summary>
    public Vector2 PounceAt { set => pounceAt = value; }

    /// <summary>Wiggle for this long, then leap at the point. Only when it's sitting around or between hops, on its feet.</summary>
    public bool Pounce(Vector2 at, float wiggle)
    {
        if (pouncing || body.Held || body.IsStuck || !body.Grounded || riding || gliding || floating || jetting || towering || body.OnRope) return false;
        bool between = Current == State.Hopping && hop == Hop.Pause && !chasing;
        if (Current != State.Idle && !between) return false;
        Enter(State.Hopping);
        pouncing = true;
        pounceT = 0f;
        wiggleFor = Mathf.Max(wiggle, 0.1f);
        pounceAt = at;
        hop = Hop.Squash;
        hopT = 0f;
        hopsLeft = 0;
        lastAttention = Time.time;
        return true;
    }

    void UpdatePounce(float dt)
    {
        pounceT += dt;
        if (pounceT < wiggleFor)
        {
            // crouched, eyes on it, the back end wiggling
            float down = Smooth(pounceT / 0.12f), w = Mathf.Sin(pounceT * 30f) * Smooth((pounceT - 0.1f) / 0.1f);
            body.Pose = Vector2.Lerp(Vector2.one, new Vector2(1.14f + 0.04f * w, 0.82f - 0.02f * w), down);
            return;
        }
        // leap: the arc peaks at the dot's height (at least a little hop) and comes down where it is
        pouncing = false;
        float feet = body.Extent.yMin, mid = body.Center.y - feet;
        float apex = Mathf.Clamp(pounceAt.y - (feet + mid) + 0.1f * S, 0.3f * S, 3.2f * S);
        float dx = Mathf.Clamp(pounceAt.x - body.Center.x, -2.6f * S, 2.6f * S);
        if (PounceVelocity(apex, dx, out Vector2 v0, out float tLand)) body.Hop(v0 - body.Velocity, tLand + 0.2f);
        launchPose = new Vector2(0.85f, 1.2f);
        hop = Hop.Air;
        hopT = 0f;
        Moment?.Invoke("pounce");
    }

    // like LeapVelocity, with the height of the arc given (back down to the same height, dx to the side)
    bool PounceVelocity(float apex, float dx, out Vector2 v0, out float tLand)
    {
        v0 = Vector2.zero; tLand = 0f;
        float g = body.gravity, lam = Mathf.Max(Drag, 1e-3f);
        float ceil = ScreenBounds.CeilingAt(body.Center);
        if (body.Extent.yMax + apex > ceil - 0.05f) apex = Mathf.Max(0.1f, ceil - 0.05f - body.Extent.yMax);
        float lo = 0f, hi = 80f;
        for (int i = 0; i < 40; i++)
        {
            float m = 0.5f * (lo + hi);
            float peak = m / lam - g / (lam * lam) * Mathf.Log(1f + m * lam / g);
            if (peak < apex) lo = m; else hi = m;
        }
        tLand = FallTime(hi, 0f, g, lam);
        float vx = dx * lam / (1f - Mathf.Exp(-lam * tLand));
        if (Mathf.Abs(vx) > 14f) return false;
        v0 = new Vector2(vx, hi);
        return true;
    }
}
