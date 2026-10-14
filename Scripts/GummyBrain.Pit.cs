using UnityEngine;

// Ball pit (BallPit): once it's in the balls the brain swims instead of hopping: paddle strokes (little pushes: the balls
// hold it up and slow it down), a giggle now and then, a few seconds of fun, then it paddles to the side and jumps out over
// the rail (towards its snack, if there is one: a snack outside gets it out at once). Grabbed = out of it. It's inside the
// Hopping state meanwhile (like the tower), so nothing else starts. Moments: "pit-in", "pit-giggle", "pit-out".
public partial class GummyBrain
{
    bool pitting, pitOut;
    float pitT, pitNext, pitStay, pitLeft, pitAgain = -10f, pitTry, pitStroke = -10f, pitGiggle, pitLastOut = -100f;
    int pitDir = 1;

    /// <summary>In the ball pit right now (swimming in the balls).</summary>
    public bool InPit => pitting;
    /// <summary>Times it climbed out of the ball pit (tests).</summary>
    public int PitExits { get; private set; }

    // true = it's in the pit and this frame is handled here
    bool PitStep(float now, float dt)
    {
        var pit = BallPit.Current;
        if (!pitting)
        {
            if (!pit || now < pitAgain || body.Held || body.OnRope || towering || jetting || gliding || floating || riding || pouncing) return false;
            if (Current == State.Climb || Current == State.Swing || Current == State.Peek) return false;
            if (!pit.Holds(body)) return false;
            StartPit(now);
        }
        if (!pit || body.Held) { EndPit(); Enter(State.Recover); return false; }
        if (!pit.Holds(body)) { pitLeft += dt; if (pitLeft > 0.3f) { EndPit(); Enter(State.Recover); return false; } }
        else pitLeft = 0f;
        pitT += dt;
        lastAttention = now;
        hop = Hop.Air;
        hopT = 0f;

        float cx = body.Center.x;
        Rect e = body.Extent;
        // a snack outside: out, that way; enough fun: out at the nearer side
        if (Food.HasValue && (Food.Value.x < pit.X0 || Food.Value.x > pit.X1 || FoodSurface > pit.WallTop))
        {
            if (!pitOut) pitTry = 0f;
            pitOut = true;
            pitDir = Food.Value.x < cx ? -1 : 1;
        }
        if (!pitOut && pitT > pitStay) { pitOut = true; pitTry = 0f; pitDir = cx - pit.X0 < pit.X1 - cx ? -1 : 1; }

        if (pitOut)
        {
            pitTry += dt;
            if (pitTry > 9f) { pitDir = -pitDir; pitTry = 0f; } // can't get out that side: the other one
            bool atWall = pitDir < 0 ? e.xMin < pit.X0 + 0.15f : e.xMax > pit.X1 - 0.15f;
            if (atWall && now >= pitNext - 0.2f) { JumpOutOfPit(pit, now); return true; }
        }

        if (now >= pitNext)
        {
            // a stroke: a little push along (and up), the balls slow it down again
            int dir = pitDir;
            if (!pitOut)
            {
                if (Food.HasValue) dir = Food.Value.x < cx ? -1 : 1;
                else
                {
                    if (Random.value < 0.25f) dir = -dir;
                    if (cx < pit.X0 + 0.45f) dir = 1;
                    else if (cx > pit.X1 - 0.45f) dir = -1;
                }
                pitDir = dir;
            }
            float s = Mathf.Sqrt(S);
            body.Hop(new Vector2(dir * Random.Range(1.2f, 1.8f) * s, Random.Range(0.8f, 1.3f) * s), 0.3f);
            pitStroke = now;
            pitNext = now + Random.Range(0.45f, 0.7f);
        }
        // paddling: a squish with every stroke, and a happy face
        float u = Mathf.Clamp01((now - pitStroke) / 0.35f);
        float w = 0.08f * Mathf.Sin(u * Mathf.PI);
        body.Pose = new Vector2(1f + w, 1f - w);
        happyUntil = Mathf.Max(happyUntil, now + 0.3f);
        if (now >= pitGiggle) { pitGiggle = now + Random.Range(3.5f, 7f); Moment?.Invoke("pit-giggle"); }
        return true;
    }

    void StartPit(float now)
    {
        if (Current == State.Asleep) { if (face) face.Asleep = false; }
        Enter(State.Hopping);
        pitting = true;
        pitOut = false;
        chasing = leapPlanned = edgeWalk = mantle = false;
        hop = Hop.Air;
        hopT = 0f;
        pitT = pitLeft = pitTry = 0f;
        pitStay = now - pitLastOut < 20f ? Random.Range(1.5f, 3f) : Random.Range(5f, 11f); // (back in again right away: a quick swim across)
        pitNext = now + Random.Range(0.3f, 0.6f);
        pitGiggle = now + Random.Range(0.8f, 2f);
        pitDir = Random.value < 0.5f ? -1 : 1;
        lastAttention = now;
        Moment?.Invoke("pit-in");
    }

    // up and over the rail, away from the pit
    void JumpOutOfPit(BallPit pit, float now)
    {
        pit.Skip(body, 0.5f);
        float rise = Mathf.Max(pit.WallTop + 0.22f - body.Extent.yMin, 0.3f);
        float vy = GummyBody.JumpSpeed(rise + 0.1f * S, body.gravity, body.Drag);
        float vx = pitDir * 2.2f * Mathf.Sqrt(S);
        body.Hop(new Vector2(vx - body.Velocity.x, vy - body.Velocity.y), 0.6f);
        PitExits++;
        EndPit();
        pitAgain = now + 1f;
        pitLastOut = now;
        Moment?.Invoke("pit-out");
        Enter(State.Recover);
    }

    void EndPit()
    {
        if (!pitting) return;
        pitting = false;
        body.Pose = Vector2.one;
    }
}
