using UnityEngine;

// Desktop pinball: the ball's physics (PinballTable is the arena of the buddy that's the ball: IGummyArena). Every physics
// step of the ball the flippers turn and the plunger moves (Advance), each point of the ball is pushed out of the rails,
// flippers, pop bumpers and window bumpers (Push), and what it touched kicks back (Touched): the flippers bat it, the
// bumpers pop it away.
public partial class PinballTable
{
    int plungerSeg;
    float plungerY, plungerVel, plungerOffUntil = -1f;
    int advancedFrame = -1;
    float simT;

    public void Advance(GummyBody body, float dt)
    {
        advancedFrame = Time.frameCount;
        simT += dt;
        TurnFlippers(dt);
        MovePlunger(dt);
        body.LimitSpeed(maxSpeed);
    }

    void TurnFlippers(float dt)
    {
        for (int f = 0; f < 2; f++)
        {
            ref Flipper F = ref flip[f];
            F.prevAng = F.ang;
            bool up = F.up && !paused && (phase == Phase.Ready || phase == Phase.Live);
            F.ang = Mathf.MoveTowards(F.ang, up ? UpAng : RestAng, (up ? FlipUp : FlipDown) * dt);
            F.omega = dt > 0f ? F.side * (F.ang - F.prevAng) / dt : 0f;
        }
    }

    void MovePlunger(float dt)
    {
        float want = plungerRest - plungerTravel * pull;
        float y = Mathf.MoveTowards(plungerY, want, (want < plungerY ? 1.2f : 30f) * dt);
        plungerVel = dt > 0f ? (y - plungerY) / dt : 0f;
        plungerY = y;
        var sg = segs[plungerSeg];
        sg.a.y = sg.b.y = plungerY;
        segs[plungerSeg] = sg;
    }

    public int Push(ref Vector2 p, Vector2 prev, out Vector2 normal, out Vector2 surfaceVelocity)
    {
        int hit = -1;
        normal = Vector2.zero;
        surfaceVelocity = Vector2.zero;
        Vector2 nn;
        for (int k = 0; k < segs.Count; k++)
        {
            Seg sg = segs[k];
            if (sg.kind == KPlunger && simT < plungerOffUntil) continue; // (just launched: it's out of the way)
            if (!Capsule(ref p, prev, sg.a, sg.b, sg.r, sg.oneWay, out nn)) continue;
            hit = k; normal = nn;
            surfaceVelocity = sg.kind == KPlunger ? new Vector2(0f, plungerVel) : Vector2.zero;
        }
        for (int f = 0; f < 2; f++)
            if (FlipperPush(f, ref p, prev, out nn, out Vector2 fv)) { hit = IdFlip + f; normal = nn; surfaceVelocity = fv; }
        for (int k = 0; k < pops.Count; k++)
        {
            Vector2 d = p - pops[k].c;
            if (d.sqrMagnitude >= popR * popR) continue;
            float m = d.magnitude;
            nn = m > 1e-5f ? d / m : Vector2.up;
            p = pops[k].c + nn * popR;
            hit = IdPop + k; normal = nn; surfaceVelocity = Vector2.zero;
        }
        for (int k = 0; k < bumpers.Count; k++)
            if (BoxPush(ref p, prev, bumpers[k].r, out nn)) { hit = IdWin + k; normal = nn; surfaceVelocity = Vector2.zero; }
        return hit;
    }

    public void Surface(int shape, out float bounce, out float grip)
    {
        if (shape >= IdWin) { bounce = 0.45f; grip = 0.1f; }
        else if (shape >= IdPop) { bounce = 0.5f; grip = 0.05f; }
        else if (shape >= IdFlip) { bounce = 0.2f; grip = 0.3f; }
        else if (segs[shape].kind == KPlunger) { bounce = 0f; grip = 0.5f; }
        else { bounce = 0.25f; grip = 0.15f; }
    }

    public void Touched(GummyBody body, int shape, Vector2 at, Vector2 normal, float speed)
    {
        if (body != ball) return;
        if (shape >= IdWin) HitWindow(shape - IdWin, body, at, normal, speed);
        else if (shape >= IdPop) HitPop(shape - IdPop, body, at, normal);
        else if (shape >= IdFlip) HitFlipper(shape - IdFlip, body, at, normal);
        else if (speed > 6f && segs[shape].kind == KWall) Play(dingClip, 0.55f + Random.value * 0.1f, 0.12f); // (a little tick off a rail)
    }

    // a flipper going up bats the ball: off along its normal at least as fast as the flipper's surface there (a bit more)
    void HitFlipper(int f, GummyBody body, Vector2 at, Vector2 normal)
    {
        Flipper F = flip[f];
        trapFrom = Time.time; // (on a flipper: in play, not stuck)
        if (Mathf.Abs(F.omega) < 0.5f) return;
        Vector2 r = at - F.pivot;
        float vs = Vector2.Dot(F.omega * new Vector2(-r.y, r.x), normal);
        if (vs <= 0.5f) return; // (going down: it lets go)
        float want = Mathf.Min(vs * 1.2f, maxSpeed), vb = Vector2.Dot(body.LiveVelocity(), normal);
        if (vb >= want) return;
        body.Hop(normal * (want - vb), 0.2f);
        Strikes++;
        if (want > 12f && Random.value < 0.25f) Say(Random.value < 0.5f ? "WHEEE!" : "WHOAAA!", 0.8f);
    }

    // a pop bumper kicks it away, whatever touches it
    void HitPop(int k, GummyBody body, Vector2 at, Vector2 normal)
    {
        var pb = pops[k];
        if (simT - pb.lastHit < 0.1f) return;
        pb.lastHit = simT;
        pb.flash = 1f;
        float vb = Vector2.Dot(body.LiveVelocity(), normal), want = 9f;
        if (vb < want) body.Hop(normal * (want - vb), 0.2f);
        Pops++;
        Add(100, at + normal * 0.3f, null);
        Play(popClip, Random.Range(0.92f, 1.1f), 0.8f);
        XpFor(body, at);
    }

    // a window bumper: BOING, it pushes back and the window wobbles (WindowWobble: it gives the way it was hit)
    void HitWindow(int k, GummyBody body, Vector2 at, Vector2 normal, float speed)
    {
        if (speed < 1.5f) return;
        var bp = bumpers[k];
        bool top = normal.y > 0.7f;
        if (simT - bp.lastHit < (top ? 0.4f : 0.15f)) return;
        bp.lastHit = simT;
        bumpers[k] = bp;
        bumperHit[bp.id] = simT;
        if (top)
        {
            // landed on its top: no kick up (it would bounce there for ever), a push towards the nearer edge to roll off
            float side = at.x < bp.r.center.x ? -1f : 1f;
            if (Mathf.Abs(body.LiveVelocity().x) < 2.5f) body.Hop(new Vector2(side * 2.5f, 0f), 0.2f);
        }
        else
        {
            float vb = Vector2.Dot(body.LiveVelocity(), normal), want = Mathf.Clamp(speed * 0.7f, 5f, 12f);
            if (vb < want) body.Hop(normal * (want - vb), 0.2f);
        }
        Bumps++;
        Flash(bp.r);
        Add(250, at + normal * 0.3f, "BOING!");
        var wob = body.GetComponent<WindowWobble>();
        bool boing = wob && wob.boingClip && wob.Nudge(bp.id, -normal * Mathf.Clamp(speed, 3f, 10f) * 0.35f, at, speed);
        if (!boing) Play(popClip, 0.7f, 0.7f);
        if (Random.value < 0.3f) Say(Random.value < 0.5f ? "BOING!" : "BONK!", 0.7f);
        XpFor(body, at);
    }

    float lastXp = -9f;
    void XpFor(GummyBody body, Vector2 at)
    {
        if (Time.time - lastXp < 1.5f) return;
        lastXp = Time.time;
        var pr = body.GetComponent<BuddyProgress>();
        if (pr) pr.AddXp(pr.xpWindowBonk, at);
    }

    // ------------------------------------------------------------------ shapes

    // a stick of radius r (a rail, a wall, the gate): out the side the point came from (a fast one that went right
    // through goes back); one-way: only stops points coming from its left side (the gate: from above)
    static bool Capsule(ref Vector2 p, Vector2 prev, Vector2 a, Vector2 b, float r, bool oneWay, out Vector2 n)
    {
        n = Vector2.zero;
        float minX = Mathf.Min(a.x, b.x) - r, maxX = Mathf.Max(a.x, b.x) + r, minY = Mathf.Min(a.y, b.y) - r, maxY = Mathf.Max(a.y, b.y) + r;
        if ((p.x < minX && prev.x < minX) || (p.x > maxX && prev.x > maxX) || (p.y < minY && prev.y < minY) || (p.y > maxY && prev.y > maxY)) return false;
        Vector2 ab = b - a;
        float len = ab.magnitude;
        if (len < 1e-6f) return false;
        Vector2 u = ab / len, ln = new Vector2(-u.y, u.x);
        float tNow = Vector2.Dot(p - a, u), sNow = Vector2.Dot(p - a, ln), sPrev = Vector2.Dot(prev - a, ln);
        if (oneWay)
        {
            if (sPrev < -0.01f || tNow < 0f || tNow > len || sNow >= r) return false;
            p += ln * (r - sNow);
            n = ln;
            return true;
        }
        if (sPrev * sNow < 0f)
        {
            float f = sPrev / (sPrev - sNow), tc = Vector2.Dot(Vector2.Lerp(prev, p, f) - a, u);
            if (tc >= 0f && tc <= len)
            {
                float side = sPrev > 0f ? 1f : -1f;
                n = ln * side;
                p = a + u * Mathf.Clamp(tNow, 0f, len) + n * r;
                return true;
            }
        }
        Vector2 c = a + u * Mathf.Clamp(tNow, 0f, len), d = p - c;
        float m2 = d.sqrMagnitude;
        if (m2 >= r * r) return false;
        float m = Mathf.Sqrt(m2);
        n = m > 1e-6f ? d / m : ln * (sPrev >= 0f ? 1f : -1f);
        p = c + n * r;
        return true;
    }

    // a flipper: a tapered stick turning round its pivot; which side the point was on is judged in the flipper's own
    // frame at the start of the step (so a flipper swinging up through a resting ball carries it up instead of going
    // through it). surfaceVelocity = how fast the flipper moves there.
    bool FlipperPush(int f, ref Vector2 p, Vector2 prev, out Vector2 n, out Vector2 sv)
    {
        n = Vector2.zero;
        sv = Vector2.zero;
        Flipper F = flip[f];
        Vector2 P = F.pivot;
        float reach = flipLen + r0 + 0.05f;
        if ((p - P).sqrMagnitude > reach * reach && (prev - P).sqrMagnitude > reach * reach) return false;
        Vector2 u1 = new Vector2(F.side * Mathf.Cos(F.ang), Mathf.Sin(F.ang)), u0 = new Vector2(F.side * Mathf.Cos(F.prevAng), Mathf.Sin(F.prevAng));
        Vector2 l1 = new Vector2(-u1.y, u1.x), l0 = new Vector2(-u0.y, u0.x);
        float tNow = Vector2.Dot(p - P, u1), sNow = Vector2.Dot(p - P, l1);
        float tPrev = Vector2.Dot(prev - P, u0), sPrev = Vector2.Dot(prev - P, l0);
        float tc = Mathf.Clamp(tNow, 0f, flipLen), r = Mathf.Lerp(r0, r1, tc / flipLen);
        bool through = false;
        if (sPrev * sNow < 0f)
        {
            float fr = sPrev / (sPrev - sNow), tx = Mathf.Lerp(tPrev, tNow, fr);
            through = tx >= 0f && tx <= flipLen;
        }
        if (through)
        {
            n = l1 * (sPrev > 0f ? 1f : -1f);
            p = P + u1 * tc + n * r;
        }
        else
        {
            Vector2 c = P + u1 * tc, d = p - c;
            float m2 = d.sqrMagnitude;
            if (m2 >= r * r) return false;
            float m = Mathf.Sqrt(m2);
            n = m > 1e-6f ? d / m : l1 * (sPrev >= 0f ? 1f : -1f);
            p = c + n * r;
        }
        Vector2 rp = p - P;
        sv = F.omega * new Vector2(-rp.y, rp.x);
        return true;
    }

    // a window: out the side it came in from
    static bool BoxPush(ref Vector2 p, Vector2 prev, Rect r, out Vector2 n)
    {
        n = Vector2.zero;
        if (p.x <= r.xMin || p.x >= r.xMax || p.y <= r.yMin || p.y >= r.yMax) return false;
        float dl = p.x - r.xMin, dr = r.xMax - p.x, db = p.y - r.yMin, dt = r.yMax - p.y;
        int side = -1;
        float best = float.MaxValue;
        if (prev.x <= r.xMin && dl < best) { best = dl; side = 0; }
        if (prev.x >= r.xMax && dr < best) { best = dr; side = 1; }
        if (prev.y <= r.yMin && db < best) { best = db; side = 2; }
        if (prev.y >= r.yMax && dt < best) { best = dt; side = 3; }
        if (side < 0)
        {
            best = dl; side = 0;
            if (dr < best) { best = dr; side = 1; }
            if (db < best) { best = db; side = 2; }
            if (dt < best) { side = 3; }
        }
        switch (side)
        {
            case 0: p.x = r.xMin; n = new Vector2(-1f, 0f); break;
            case 1: p.x = r.xMax; n = new Vector2(1f, 0f); break;
            case 2: p.y = r.yMin; n = new Vector2(0f, -1f); break;
            default: p.y = r.yMax; n = new Vector2(0f, 1f); break;
        }
        return true;
    }
}
