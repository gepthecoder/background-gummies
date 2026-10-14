using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A ball pit (chat "ball pit", WhipIt > Buddy > Ball Pit): a padded playground pit full of glossy plastic balls, set on
/// the taskbar beside the window nearest your cursor (or on a window top, riding it). A buddy that falls or jumps in sinks
/// up to its eyes (GummyBody asks Soak: the balls hold it up and slow it down), paddles about, giggles and climbs out over
/// the side (GummyBrain.Pit); now and then one cannonballs in from a window above (BuddyPit), balls flying everywhere.
/// The balls are yours too: grab one and throw it, or swish the cursor through them. Spilled balls hop back in after a
/// while. Grab the rail to move the pit (let go: it drops onto whatever's below). One pit at a time; drawn in code.
/// </summary>
[DefaultExecutionOrder(8)] // after the buddies have moved this frame: the balls make room for them
[DisallowMultipleComponent]
public class BallPit : MonoBehaviour, IGrabTarget
{
    public enum Mount { Standing, Falling, Held }

    /// <summary>The pit that's out (null = none).</summary>
    public static BallPit Current { get; private set; }
    /// <summary>A buddy landed in the balls (who, how fast).</summary>
    public static event Action<GummyBody, float> Splashed;

    /// <summary>Inside width, wall height, wall thickness, ball radius, number of balls.</summary>
    public const float Inner = 3f, WallH = 0.55f, WallT = 0.09f, BallR = 0.075f;
    public const int Count = 64;
    const float G = 14f, AirDrag = 0.5f, Bounce = 0.45f, BallArea = Mathf.PI * BallR * BallR / 0.82f;

    public Mount State { get; private set; }
    /// <summary>Inside: left and right inner wall, the floor it stands on, the top of the walls.</summary>
    public float X0 => x0;
    public float X1 => x0 + Inner;
    public float FloorY => floorY;
    public float WallTop => floorY + WallH;
    /// <summary>How high the balls come (where a buddy floats).</summary>
    public float Fill { get; private set; }
    /// <summary>The window it stands on (0 = the taskbar / desktop).</summary>
    public long Window => onWin;
    /// <summary>Its draw layer for the windows (Occlusion).</summary>
    public int Layer => State != Mount.Standing ? Occlusion.Front : onWin != 0 && Occlusion.Exists(onWin) ? Occlusion.LayerOf(onWin) : Occlusion.Back;
    /// <summary>Put out a moment ago (the buddies come and look).</summary>
    public bool Fresh => Time.time - putAt > 1.5f && Time.time - putAt < 8f;
    /// <summary>Balls that are outside the pit right now.</summary>
    public int Spilled { get; private set; }
    /// <summary>The physics is resting (nothing moving, nobody near).</summary>
    public bool Asleep => asleep;
    /// <summary>So far: splashes (a buddy landing in it fast), balls that hopped back in.</summary>
    public int Splashes { get; private set; }
    public int HoppedHome { get; private set; }

    public bool Hovered { get; set; }
    public bool Held => State == Mount.Held || heldBall >= 0;

    float x0, floorY, putAt, calm, fallV;
    long onWin;
    int platVersion = -1, heldBall = -1, hoverBall = -1;
    bool asleep;
    Vector2 grabOff, lastCursor;
    Material mat;
    readonly Vector2[] pos = new Vector2[Count], prev = new Vector2[Count], vel = new Vector2[Count], vIn = new Vector2[Count];
    readonly bool[] front = new bool[Count], ground = new bool[Count];
    readonly int[] col = new int[Count];
    readonly float[] outRest = new float[Count];
    readonly SpriteRenderer[] ballSr = new SpriteRenderer[Count];
    readonly List<GummyBody> near = new List<GummyBody>();
    readonly HashSet<GummyBody> inside = new HashSet<GummyBody>();
    readonly Dictionary<GummyBody, float> noSoak = new Dictionary<GummyBody, float>();
    readonly List<GummyBody> scratch = new List<GummyBody>();
    AudioSource sfx;
    AudioClip splashClip;

    // the frame: floor mat, back rail; front net, front rail, corner posts
    SpriteRenderer mat0, postL, postR;
    readonly List<SpriteRenderer> netTiles = new List<SpriteRenderer>(), railFront = new List<SpriteRenderer>(), railBack = new List<SpriteRenderer>();

    /// <summary>Draw orders: back rail and mat behind everything, half the balls behind the buddies (3), half in front of
    /// every buddy (their orders run 5..61: 62), then the net (63) and the padded rail and posts (64): a buddy in the pit
    /// is in among the balls.</summary>
    public const int BackOrder = 2, BallBack = 3, BallFront = 62, NetOrder = 63, RailOrder = 64;
    const float Tile = 0.5f;

    // ------------------------------------------------------------------ putting it out

    /// <summary>Put the pit out near this point (the cursor): on the taskbar beside a window, else wherever there's room.</summary>
    public static BallPit Put(Material material, Vector2 near, AudioClip splash = null)
    {
        if (Current) return Current;
        var go = new GameObject("Ball Pit");
        var p = go.AddComponent<BallPit>();
        p.mat = material;
        p.splashClip = splash;
        p.putAt = Time.time;
        Current = p;
        Spot(near, out p.x0, out p.floorY, out p.onWin);
        p.platVersion = DesktopPlatforms.Version;
        p.Build();
        p.Fillup();
        return p;
    }

    /// <summary>Put it away.</summary>
    public static void TakeDown()
    {
        if (Current) Destroy(Current.gameObject);
        Current = null;
    }

    // where it goes: the floor beside a window side (so they can jump in off the top), the floor under the cursor, or a
    // window top that's wide enough; the nearest to the cursor wins
    static void Spot(Vector2 near, out float bestX0, out float bestY, out long bestWin)
    {
        Rect r = ScreenBounds.WorldRect;
        float outer = Inner + 2f * WallT, fy = ScreenBounds.FloorAt(near); // (more than one screen: the floor under it)
        var c = new Pick { best = float.MaxValue, near = near };
        c.x0 = Mathf.Clamp(near.x - Inner * 0.5f, r.xMin + WallT + 0.05f, r.xMax - WallT - 0.05f - Inner);
        c.y = fy;
        foreach (var w in DesktopPlatforms.Windows)
        {
            if (!w.movable || w.yMax < fy + 1.2f) continue;
            Try(ref c, w.xMin - 0.02f - WallT - Inner, fy, 0, -3.5f);  // left of it, its right wall right against the window (no gap for balls to wedge in)
            Try(ref c, w.xMax + 0.02f + WallT, fy, 0, -3.5f);          // right of it
        }
        Try(ref c, near.x - Inner * 0.5f, fy, 0, 0f);
        foreach (var p in DesktopPlatforms.Current)
        {
            if (DesktopPlatforms.IsIcon(p.id) || p.x1 - p.x0 < outer + 0.2f || p.y > r.yMax - 1.5f) continue;
            float lx0 = Mathf.Clamp(near.x - Inner * 0.5f, p.x0 + WallT + 0.05f, p.x1 - WallT - 0.05f - Inner);
            Try(ref c, lx0, p.y, p.id, 1f);
        }
        if (c.best == float.MaxValue)
            for (float x = r.xMin + WallT + 0.1f; x + Inner + WallT < r.xMax; x += 0.5f) Try(ref c, x, fy, 0, 0f); // anywhere on the floor
        bestX0 = c.x0;
        bestY = c.y;
        bestWin = c.win;
    }

    struct Pick { public float best, x0, y; public long win; public Vector2 near; }

    static void Try(ref Pick c, float lx0, float y, long win, float bonus)
    {
        Rect r = ScreenBounds.WorldRect;
        if (lx0 - WallT < r.xMin + 0.02f || lx0 + Inner + WallT > r.xMax - 0.02f) return;
        if (win == 0 && Blocked(lx0 - WallT, lx0 + Inner + WallT, y)) return;
        float s = Mathf.Abs(lx0 + Inner * 0.5f - c.near.x) + 0.3f * Mathf.Abs(y - c.near.y) + bonus;
        if (OnLadder(lx0 - WallT, lx0 + Inner + WallT, y)) s += 8f; // (not over the slide's ladder: they'd never get up it)
        if (s < c.best) { c.best = s; c.x0 = lx0; c.y = y; c.win = win; }
    }

    /// <summary>The slide's ladder comes down here (between a and b on the surface at y).</summary>
    public static bool OnLadder(float a, float b, float y)
    {
        var sl = Slide.Current;
        if (!sl || !sl.HasLadder || Mathf.Abs(sl.LadderFoot - y) > 0.3f) return false;
        return sl.LadderX > a - 0.45f && sl.LadderX < b + 0.45f;
    }

    // a window standing on the floor there (it'd be in the way)
    static bool Blocked(float a, float b, float y)
    {
        foreach (var w in DesktopPlatforms.Windows)
            if (w.movable && w.xMax > a && w.xMin < b && w.yMin < y + WallH + 0.4f && w.yMax > y + 0.02f) return true;
        return false;
    }

    // the balls, settled in rows (the front / back mix and the colours shuffled)
    void Fillup()
    {
        int perRow = Mathf.FloorToInt(Inner / (2f * BallR * 1.02f));
        for (int i = 0; i < Count; i++)
        {
            int row = i / perRow, k = i % perRow;
            float x = x0 + BallR * 1.05f + k * 2f * BallR * 1.02f + (row % 2) * BallR * 0.9f;
            if (x > X1 - BallR) x -= 2f * BallR;
            pos[i] = prev[i] = new Vector2(x, floorY + BallR + row * BallR * 1.75f + 0.002f * (i % 3));
            vel[i] = Vector2.zero;
            col[i] = (i * 7 + row * 3) % Colors;
            front[i] = Hash01(i * 1.7f + 3.1f) < 0.55f;
            int o = front[i] ? BallFront : BallBack;
            ballSr[i] = Part("Ball", BallSprite(col[i]), o);
        }
        for (int s = 0; s < 120; s++) Step(1f / 120f, false); // let them settle
        asleep = false;
        Place();
    }

    void OnEnable() { GrabTargets.Add(this); if (!Current) Current = this; }
    void OnDisable() { GrabTargets.Remove(this); if (Current == this) Current = null; inside.Clear(); }

    // ------------------------------------------------------------------ the buddies (GummyBody, GummyBrain.Pit)

    /// <summary>Is it in the balls (its middle inside the pit, below the top of the balls)?</summary>
    public bool Holds(GummyBody b)
    {
        if (!b || State != Mount.Standing) return false;
        Vector2 c = b.Center;
        return c.x > x0 && c.x < X1 && c.y < Fill + 0.3f * b.size && c.y > floorY - 0.1f;
    }

    /// <summary>The balls don't hold it up for a moment (it's jumping out).</summary>
    public void Skip(GummyBody b, float seconds) { if (b) noSoak[b] = Time.time + seconds; }

    /// <summary>
    /// GummyBody, every physics step for each point of its outline: inside the balls they hold it up (it floats with its top
    /// out, like up to its eyes) and slow it down (thick going). Nothing happens outside, or with no pit out.
    /// </summary>
    public static void Soak(GummyBody b, Vector2 x, ref Vector2 v, float g, float dt)
    {
        var p = Current;
        if (!p || p.State != Mount.Standing || x.x < p.x0 || x.x > p.x0 + Inner || x.y < p.floorY - 0.05f) return; // (not under it)
        float depth = p.Fill - x.y;
        if (depth <= 0f) return;
        if (p.noSoak.Count > 0 && p.noSoak.TryGetValue(b, out float until) && Time.time < until) return;
        float k = Mathf.Clamp01(depth / (0.3f * Mathf.Max(b.size, 0.2f)));
        v.y += g * 2.6f * k * dt;
        v *= 1f - Mathf.Clamp01(5f * k * dt);
    }

    /// <summary>Is any of this body over the pit (worth asking Soak)?</summary>
    public static bool Over(GummyBody b)
    {
        var p = Current;
        if (!p || p.State != Mount.Standing) return false;
        Rect e = b.Extent;
        return e.xMax > p.x0 && e.xMin < p.x0 + Inner && e.yMin < p.Fill && e.yMin > p.floorY - 0.25f; // (in it, not under the window it's on)
    }

    /// <summary>Balls fly up away from here (something landed in them at this speed).</summary>
    public void Splash(Vector2 at, float speed)
    {
        float R = 0.45f + 0.12f * speed;
        for (int i = 0; i < Count; i++)
        {
            if (i == heldBall) continue;
            Vector2 d = pos[i] - at;
            float dist = d.magnitude;
            if (dist > R) continue;
            Vector2 dir = dist > 1e-4f ? d / dist : Vector2.up;
            float k = speed * 0.55f * (1f - dist / R) * (0.75f + 0.5f * Hash01(i + Time.frameCount * 0.37f));
            vel[i] += new Vector2(dir.x * k, Mathf.Abs(dir.y) * k * 0.6f + k * 0.8f);
            prev[i] = pos[i] - vel[i] * (1f / 120f);
        }
        asleep = false;
        calm = 0f;
        if (sfx && splashClip) { sfx.pitch = Mathf.Lerp(1.15f, 0.85f, Mathf.InverseLerp(2f, 10f, speed)); sfx.PlayOneShot(splashClip, Mathf.Clamp01(0.25f + speed * 0.07f)); }
    }

    // ------------------------------------------------------------------ you

    public bool Contains(Vector2 p)
    {
        hoverBall = -1;
        if (State == Mount.Standing && Occlusion.Hidden(new Vector2(x0 + Inner * 0.5f, floorY + 0.1f), Layer)) return false;
        float best = BallR * BallR * 2f;
        for (int i = 0; i < Count; i++)
        {
            float d = (pos[i] - p).sqrMagnitude;
            if (d < best) { best = d; hoverBall = i; }
        }
        if (hoverBall >= 0) return true;
        // the rail along the top and the corner posts
        float top = WallTop;
        if (p.y > top - 0.1f && p.y < top + 0.1f && p.x > x0 - WallT - 0.05f && p.x < X1 + WallT + 0.05f) return true;
        return p.y > floorY && p.y < top + 0.12f && (Mathf.Abs(p.x - (x0 - WallT * 0.5f)) < 0.1f || Mathf.Abs(p.x - (X1 + WallT * 0.5f)) < 0.1f);
    }

    public void BeginGrab(Vector2 p)
    {
        asleep = false;
        calm = 0f;
        if (hoverBall >= 0) { heldBall = hoverBall; grabOff = pos[heldBall] - p; vel[heldBall] = Vector2.zero; outRest[heldBall] = 0f; return; }
        State = Mount.Held;
        onWin = 0;
        grabOff = new Vector2(x0, floorY) - p;
    }

    public void DragTo(Vector2 p)
    {
        Rect r = ScreenBounds.WorldRect;
        if (heldBall >= 0)
        {
            Vector2 target = p + grabOff, was = pos[heldBall];
            float dt = Mathf.Max(Time.unscaledDeltaTime, 1e-4f); // (your hand: real time)
            vel[heldBall] = Vector2.Lerp(vel[heldBall], (target - was) / dt, 0.5f);
            pos[heldBall] = prev[heldBall] = target;
            return;
        }
        Vector2 want = p + grabOff;
        float nx = Mathf.Clamp(want.x, r.xMin + WallT + 0.02f, r.xMax - WallT - 0.02f - Inner), ny = Mathf.Clamp(want.y, r.yMin, r.yMax - WallH - 0.3f);
        Shift(new Vector2(nx - x0, ny - floorY), true);
    }

    public void EndGrab()
    {
        if (heldBall >= 0)
        {
            vel[heldBall] = Vector2.ClampMagnitude(vel[heldBall], 14f);
            prev[heldBall] = pos[heldBall] - vel[heldBall] * (1f / 120f);
            heldBall = -1;
            return;
        }
        State = Mount.Falling;
        fallV = 0f;
    }

    // moves the pit and the balls in it (and the ones lying near it, when it's carried)
    void Shift(Vector2 d, bool carry)
    {
        if (d.sqrMagnitude < 1e-12f) return;
        float a = x0 - WallT - 0.05f, b = X1 + WallT + 0.05f;
        for (int i = 0; i < Count; i++)
        {
            if (i == heldBall) continue;
            bool mine = pos[i].x > a && pos[i].x < b && pos[i].y < floorY + WallH + 1.5f && pos[i].y > floorY - 0.2f;
            if (!mine && !carry) continue;
            if (!mine) continue;
            pos[i] += d;
            prev[i] += d;
        }
        x0 += d.x;
        floorY += d.y;
        asleep = false;
    }

    // ------------------------------------------------------------------ each frame

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("BallPit.Update", t0); }
    void UpdateTimed()
    {
        float dt = Mathf.Min(Time.deltaTime, 1f / 30f), now = Time.time;
        Rect r = ScreenBounds.WorldRect;
        if (!sfx) { sfx = gameObject.AddComponent<AudioSource>(); sfx.playOnAwake = false; sfx.spatialBlend = 0f; }

        // riding the window it's on (gone: it falls); carried; falling
        if (State == Mount.Standing && onWin != 0 && DesktopPlatforms.Version != platVersion)
        {
            platVersion = DesktopPlatforms.Version;
            bool found = false;
            foreach (var p in DesktopPlatforms.Current)
                if (p.id == onWin) { Shift(new Vector2(p.dx, p.dy), false); found = true; break; }
            if (!found) { State = Mount.Falling; fallV = 0f; onWin = 0; }
        }
        if (State == Mount.Falling)
        {
            fallV -= 18f * dt;
            float was = floorY, landY = r.yMin;
            long on = 0;
            foreach (var p in DesktopPlatforms.Current)
                if (!DesktopPlatforms.IsIcon(p.id) && x0 - WallT >= p.x0 - 0.3f && X1 + WallT <= p.x1 + 0.3f && p.y <= was + 1e-3f && p.y > landY) { landY = p.y; on = p.id; }
            float ny = Mathf.Max(landY, floorY + fallV * dt);
            Shift(new Vector2(0f, ny - floorY), false);
            if (floorY <= landY + 1e-4f)
            {
                // thump: the balls jump
                State = Mount.Standing;
                onWin = on;
                platVersion = DesktopPlatforms.Version;
                float k = Mathf.Clamp(-fallV * 0.25f, 0.5f, 3f);
                for (int i = 0; i < Count; i++) if (i != heldBall) { vel[i] += new Vector2(Hash01(i * 3.3f + now) - 0.5f, 1f) * k; prev[i] = pos[i] - vel[i] * (1f / 120f); }
            }
        }

        // buddies near it (the balls make room for them), and splashes as they land in it
        near.Clear();
        foreach (var b in GummyBody.All)
        {
            if (!b || !b.isActiveAndEnabled || b.InRoom) continue;
            Rect e = b.Extent;
            if (e.xMax > x0 - WallT - 0.3f && e.xMin < X1 + WallT + 0.3f && e.yMin < Fill + 0.8f && e.yMax > floorY - 0.2f) near.Add(b);
        }
        Splashes0();

        // the cursor swishing through them
        Vector2 cur = Grabber.CursorWorld;
        Vector2 cv = dt > 0f ? (cur - lastCursor) / dt : Vector2.zero;
        lastCursor = cur;
        bool stir = heldBall < 0 && State != Mount.Held && cv.sqrMagnitude > 0.25f && cur.x > x0 - 0.2f && cur.x < X1 + 0.2f && cur.y > floorY && cur.y < Fill + 0.3f;

        if (asleep && (near.Count > 0 || stir || heldBall >= 0 || State != Mount.Standing)) { asleep = false; calm = 0f; }
        if (!asleep)
        {
            const int sub = 2;
            for (int s = 0; s < sub; s++) Step(dt / sub, stir, cur, cv);
            float vmax = 0f;
            for (int i = 0; i < Count; i++) vmax = Mathf.Max(vmax, vel[i].sqrMagnitude);
            calm = vmax < 0.01f && near.Count == 0 && heldBall < 0 && State == Mount.Standing ? calm + dt : 0f;
            if (calm > 1f) asleep = true;
        }
        HomeBalls(dt);
        Place();
    }

    void Splashes0()
    {
        scratch.Clear();
        foreach (var b in near) if (Holds(b)) scratch.Add(b);
        foreach (var b in scratch)
            if (!inside.Contains(b))
            {
                float speed = b.Velocity.magnitude;
                if (speed > 2.2f && !b.Held)
                {
                    Splashes++;
                    Splash(new Vector2(b.Center.x, b.Extent.yMin + 0.05f), speed);
                    Splashed?.Invoke(b, speed);
                }
            }
        inside.Clear();
        foreach (var b in scratch) inside.Add(b);
    }

    void Step(float h, bool stir, Vector2 cur = default, Vector2 cv = default)
    {
        Fill = floorY + Mathf.Min(InPit() * BallArea / Inner, WallH + 0.2f);
        for (int i = 0; i < Count; i++)
        {
            vIn[i] = vel[i];
            ground[i] = false;
            if (i == heldBall) continue;
            prev[i] = pos[i];
            vel[i].y -= G * h;
            vel[i] *= 1f - AirDrag * h;
            pos[i] += vel[i] * h;
        }
        for (int it = 0; it < 2; it++)
        {
            Balls();
            for (int i = 0; i < Count; i++)
            {
                if (i == heldBall) continue;
                Walls(i);
                foreach (var b in near) Body(i, b);
                if (stir) Stir(i, cur, cv, h);
                World(i);
            }
        }
        float inv = 1f / h;
        for (int i = 0; i < Count; i++)
        {
            if (i == heldBall) continue;
            Vector2 nv = (pos[i] - prev[i]) * inv;
            if (ground[i])
            {
                if (vIn[i].y < -1.2f) nv.y = -vIn[i].y * Bounce;   // a bounce
                nv.x *= Mathf.Exp(-4f * h);                        // rolls to a stop
            }
            vel[i] = nv;
        }
    }

    int InPit()
    {
        int n = 0;
        for (int i = 0; i < Count; i++) if (pos[i].x > x0 && pos[i].x < X1 && pos[i].y < WallTop + 0.4f) n++;
        return n;
    }

    // balls push each other apart (the one in your hand stays put)
    void Balls()
    {
        const float d2 = 4f * BallR * BallR;
        for (int i = 0; i < Count; i++)
            for (int j = i + 1; j < Count; j++)
            {
                Vector2 d = pos[j] - pos[i];
                float dd = d.sqrMagnitude;
                if (dd >= d2 || dd < 1e-12f) continue;
                float dist = Mathf.Sqrt(dd);
                Vector2 c = d * ((2f * BallR - dist) / dist);
                if (i == heldBall) pos[j] += c;
                else if (j == heldBall) pos[i] -= c;
                else { pos[i] -= c * 0.5f; pos[j] += c * 0.5f; }
            }
    }

    // the pit's floor (it's carried or falling: the balls go with it) and its two walls (thin and padded, a round rail on
    // top: balls roll over it)
    void Walls(int i)
    {
        float top = WallTop, half = WallT * 0.5f;
        if (pos[i].x > x0 - half && pos[i].x < X1 + half && pos[i].y < floorY + BallR && prev[i].y >= floorY + BallR - 0.1f) { pos[i].y = floorY + BallR; ground[i] = true; }
        for (int s = 0; s < 2; s++)
        {
            float cx = s == 0 ? x0 - half : X1 + half;
            Vector2 p = pos[i];
            if (p.y < top)
            {
                if (Mathf.Abs(p.x - cx) >= half + BallR) continue;
                bool left = prev[i].x < cx;
                pos[i].x = left ? cx - half - BallR : cx + half + BallR;
            }
            else
            {
                Vector2 d = p - new Vector2(cx, top);
                float m = d.magnitude, rr = half + 0.02f + BallR;
                if (m >= rr || m < 1e-6f) continue;
                pos[i] = new Vector2(cx, top) + d / m * rr;
            }
        }
    }

    // a buddy: the balls are pushed out of it (an ellipse its size)
    void Body(int i, GummyBody b)
    {
        Rect e = b.Extent;
        Vector2 c = e.center, d = pos[i] - c;
        float ax = e.width * 0.5f + BallR, ay = e.height * 0.5f + BallR;
        if (Mathf.Abs(d.x) >= ax || Mathf.Abs(d.y) >= ay) return;
        float k = (d.x * d.x) / (ax * ax) + (d.y * d.y) / (ay * ay);
        if (k >= 1f) return;
        if (k < 1e-6f) d = new Vector2(0f, ay * 0.5f);
        pos[i] = c + d / Mathf.Sqrt(Mathf.Max(k, 1e-6f));
    }

    // your cursor moving through them: they get pushed along
    void Stir(int i, Vector2 cur, Vector2 cv, float h)
    {
        Vector2 d = pos[i] - cur;
        float rr = 0.14f + BallR;
        if (d.sqrMagnitude >= rr * rr) return;
        float m = d.magnitude;
        Vector2 n = m > 1e-5f ? d / m : Vector2.up;
        pos[i] = cur + n * rr;
        pos[i] += Vector2.ClampMagnitude(cv, 6f) * (h * 0.6f);
    }

    // the screen, window tops (from above), window sides and bottoms: for balls that flew out
    void World(int i)
    {
        Rect r = ScreenBounds.WorldRect;
        Vector2 p = pos[i], q = prev[i];
        if (p.x < r.xMin + BallR) p.x = r.xMin + BallR;
        if (p.x > r.xMax - BallR) p.x = r.xMax - BallR;
        if (p.y > r.yMax - BallR) p.y = r.yMax - BallR;
        if (p.y < r.yMin + BallR) { p.y = r.yMin + BallR; ground[i] = true; }
        foreach (var pl in DesktopPlatforms.Current)
        {
            if (p.x < pl.x0 || p.x > pl.x1 || q.y - BallR < pl.y - 1e-3f || p.y - BallR >= pl.y) continue;
            p.y = pl.y + BallR;
            ground[i] = true;
        }
        foreach (var w in DesktopPlatforms.Windows)
        {
            if (!w.movable) continue;
            float wx0 = w.xMin - BallR, wx1 = w.xMax + BallR, wy0 = w.yMin - BallR;
            if (p.x <= wx0 || p.x >= wx1 || p.y <= wy0 || p.y - BallR >= w.yMax) continue;
            if (q.y - BallR >= w.yMax - 1e-3f) continue;
            if (q.x <= wx0 + 1e-3f) p.x = wx0;
            else if (q.x >= wx1 - 1e-3f) p.x = wx1;
            else if (q.y <= wy0 + 1e-3f) p.y = wy0;
            else p.y = w.yMax + BallR;
        }
        pos[i] = p;
    }

    // balls lying outside for a while hop back in (from far away: they drop in from above)
    void HomeBalls(float dt)
    {
        int spilled = 0;
        for (int i = 0; i < Count; i++)
        {
            bool outside = pos[i].x < x0 - WallT || pos[i].x > X1 + WallT || pos[i].y < floorY - 0.15f;
            if (!outside || i == heldBall) { outRest[i] = 0f; continue; }
            spilled++;
            if (vel[i].sqrMagnitude < 0.04f) outRest[i] += dt; else outRest[i] = Mathf.Max(0f, outRest[i] - dt);
            if (outRest[i] < 9f || State != Mount.Standing) continue;
            outRest[i] = 0f;
            HoppedHome++;
            float tx = Mathf.Lerp(x0 + 0.25f, X1 - 0.25f, Hash01(i * 5.1f + Time.time)), ty = Fill + 0.3f;
            if ((pos[i] - new Vector2(tx, ty)).magnitude > 8f)
            {
                pos[i] = prev[i] = new Vector2(tx, WallTop + 1.2f);
                vel[i] = Vector2.zero;
            }
            else
            {
                float apex = Mathf.Max(pos[i].y, WallTop) + 0.55f;
                float vy = Mathf.Sqrt(2f * G * (apex - pos[i].y)), tu = vy / G, td = Mathf.Sqrt(2f * Mathf.Max(apex - ty, 0.01f) / G);
                vel[i] = new Vector2((tx - pos[i].x) / (tu + td) * 1.05f, vy * 1.02f);
                prev[i] = pos[i] - vel[i] * (1f / 120f);
            }
            asleep = false;
        }
        Spilled = spilled;
    }

    // ------------------------------------------------------------------ drawing

    void Build()
    {
        int tiles = Mathf.CeilToInt((Inner + 2f * WallT) / Tile);
        mat0 = Part("Pit Mat", MatSprite(), BackOrder);
        for (int k = 0; k < tiles; k++)
        {
            netTiles.Add(Part("Pit Net", NetSprite(), NetOrder));
            railFront.Add(Part("Pit Rail", RailSprite(k % RailColors), RailOrder));
            railBack.Add(Part("Pit Rail (back)", RailSprite(k % RailColors), BackOrder));
        }
        postL = Part("Pit Post", PostSprite(), RailOrder);
        postR = Part("Pit Post", PostSprite(), RailOrder);
    }

    SpriteRenderer Part(string name, Sprite s, int order)
    {
        var g = new GameObject(name);
        g.transform.SetParent(transform, false);
        var sr = g.AddComponent<SpriteRenderer>();
        sr.sprite = s;
        sr.sortingOrder = order;
        sr.color = Color.white;
        if (mat) sr.sharedMaterial = mat;
        return sr;
    }

    void Place()
    {
        int layer = Layer;
        float a = x0 - WallT, top = WallTop, width = Inner + 2f * WallT;
        int tiles = netTiles.Count;
        float tw = width / tiles;
        bool hid = Occlusion.Hidden(new Vector2(x0 + Inner * 0.5f, floorY + 0.1f), layer);
        Set(mat0, new Vector2(a + width * 0.5f, floorY + 0.025f), new Vector2(width, 0.05f / 0.25f), hid);
        for (int k = 0; k < tiles; k++)
        {
            float cx = a + (k + 0.5f) * tw;
            bool h = Occlusion.Hidden(new Vector2(cx, floorY + WallH * 0.5f), layer);
            Set(netTiles[k], new Vector2(cx, floorY + WallH * 0.5f), new Vector2(tw, WallH), h);
            Set(railFront[k], new Vector2(cx, top), new Vector2(tw * 1.06f, 0.15f / 0.5f), h);
            Set(railBack[k], new Vector2(cx, top + 0.035f), new Vector2(tw * 1.06f, 0.11f / 0.5f), h);
            railBack[k].color = new Color(0.72f, 0.72f, 0.8f, 1f);
        }
        float ph = WallH + 0.14f;
        Set(postL, new Vector2(x0 - WallT * 0.5f, floorY), new Vector2(0.17f, ph / 3f), Occlusion.Hidden(new Vector2(x0, floorY + 0.2f), layer));
        Set(postR, new Vector2(X1 + WallT * 0.5f, floorY), new Vector2(0.17f, ph / 3f), Occlusion.Hidden(new Vector2(X1, floorY + 0.2f), layer));
        for (int i = 0; i < Count; i++)
        {
            var sr = ballSr[i];
            sr.transform.position = new Vector3(pos[i].x, pos[i].y, 0f);
            float k = 2.3f * BallR;
            sr.transform.localScale = new Vector3(k, k, 1f);
            bool insideFrame = pos[i].x > a && pos[i].x < a + width && pos[i].y < top + 0.3f;
            sr.enabled = !Occlusion.Hidden(pos[i], insideFrame ? layer : Occlusion.Back);
        }
    }

    static void Set(SpriteRenderer sr, Vector2 at, Vector2 size, bool hidden)
    {
        sr.transform.position = new Vector3(at.x, at.y, 0f);
        sr.transform.rotation = Quaternion.identity;
        sr.transform.localScale = new Vector3(size.x, size.y, 1f);
        sr.enabled = !hidden;
    }

    // ------------------------------------------------------------------ sprites (baked once)

    const int Colors = 6, RailColors = 4;
    static readonly Sprite[] s_balls = new Sprite[Colors], s_rails = new Sprite[RailColors];
    static Sprite s_net, s_post, s_mat;

    static Sprite BallSprite(int c) { if (!s_balls[c]) s_balls[c] = Bake(48, 48, new Vector2(0.5f, 0.5f), "Pit Ball", (x, y, t) => BallPaint(c, x, y, t)); return s_balls[c]; }
    static Sprite RailSprite(int c) { if (!s_rails[c]) s_rails[c] = Bake(32, 16, new Vector2(0.5f, 0.5f), "Pit Rail", (x, y, t) => RailPaint(c, x, y, t)); return s_rails[c]; }
    static Sprite NetSprite() { if (!s_net) s_net = Bake(32, 32, new Vector2(0.5f, 0.5f), "Pit Net", NetPaint); return s_net; }
    static Sprite PostSprite() { if (!s_post) s_post = Bake(16, 48, new Vector2(0.5f, 0f), "Pit Post", PostPaint); return s_post; }
    static Sprite MatSprite() { if (!s_mat) s_mat = Bake(32, 8, new Vector2(0.5f, 0.5f), "Pit Mat", MatPaint); return s_mat; }

    // x 0..1 across, y 0..h/w up (same texel size); 1 unit wide
    static Sprite Bake(int w, int h, Vector2 pivot, string name, Func<float, float, float, Color> paint)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = name };
        var px = new Color32[w * h];
        float texel = 1f / w;
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                Color c = paint((i + 0.5f) * texel, (j + 0.5f) * texel, texel);
                px[j * w + i] = new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), Mathf.Clamp01(c.a));
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return Sprite.Create(tex, new Rect(0, 0, w, h), pivot, w);
    }

    static readonly Color[] BallColors =
    {
        new Color(0.95f, 0.22f, 0.24f), new Color(1f, 0.80f, 0.16f), new Color(0.20f, 0.52f, 0.96f),
        new Color(0.26f, 0.80f, 0.36f), new Color(1f, 0.52f, 0.14f), new Color(0.80f, 0.38f, 0.92f),
    };

    /// <summary>A glossy plastic ball (x, y 0..1): shaded round, a bright highlight, a darker edge. 1 unit sprite, the
    /// ball fills 0.87 of it (scaled 2.3 x its radius).</summary>
    internal static Color BallPaint(int c, float x, float y, float texel)
    {
        Vector2 q = new Vector2(x - 0.5f, y - 0.5f) / 0.435f;
        float r = q.magnitude, d = (r - 1f) * 0.435f;
        float a = Mathf.Clamp01(0.5f - d / texel);
        if (a <= 0f) return new Color(0f, 0f, 0f, 0f);
        Color baseC = BallColors[c % Colors];
        float light = Mathf.Clamp01(0.62f + 0.38f * Vector2.Dot(q, new Vector2(-0.55f, 0.65f)) + 0.25f * Mathf.Sqrt(Mathf.Max(0f, 1f - r * r)));
        Color col = baseC * (0.55f + 0.6f * light);
        float spec = Mathf.Clamp01(1f - (q - new Vector2(-0.35f, 0.42f)).magnitude / 0.28f);
        col = Color.Lerp(col, Color.white, spec * spec * 0.9f);
        float bounceLight = Mathf.Clamp01(1f - (q - new Vector2(0.35f, -0.6f)).magnitude / 0.4f);
        col = Color.Lerp(col, Color.Lerp(baseC, Color.white, 0.4f), bounceLight * 0.3f);
        if (d > -texel * 1.5f) col = Color.Lerp(col, baseC * 0.45f, 0.7f);
        col.a = a;
        return col;
    }

    static readonly Color[] RailColorsC = { new Color(0.95f, 0.26f, 0.3f), new Color(1f, 0.8f, 0.2f), new Color(0.24f, 0.56f, 0.98f), new Color(0.3f, 0.8f, 0.42f) };

    /// <summary>A padded rail section (x 0..1 along, y 0..0.5 across): a soft round tube in one playground colour, stitched
    /// ends, a shine along the top.</summary>
    internal static Color RailPaint(int c, float x, float y, float texel)
    {
        float cy = 0.25f, half = 0.2f;
        float ex = Mathf.Max(Mathf.Abs(x - 0.5f) - (0.5f - 0.06f), 0f);
        float d = Mathf.Sqrt(ex * ex * 4f + (y - cy) * (y - cy)) - half;
        float a = Mathf.Clamp01(0.5f - d / texel);
        if (a <= 0f) return new Color(0f, 0f, 0f, 0f);
        Color baseC = RailColorsC[c % RailColors];
        float k = (y - cy) / half;
        Color col = baseC * (0.62f + 0.38f * Mathf.Sqrt(Mathf.Max(0f, 1f - k * k)) + 0.12f * k);
        if (k > 0.25f && k < 0.6f && Mathf.Abs(x - 0.5f) < 0.4f) col = Color.Lerp(col, Color.white, 0.45f);
        if (Mathf.Abs(x - 0.5f) > 0.44f) col *= 0.82f; // the seam between sections
        if (d > -texel * 1.5f) col = Color.Lerp(col, baseC * 0.45f, 0.6f);
        col.a = a;
        return col;
    }

    /// <summary>The safety net across the front (x, y 0..1): white diamond mesh you see the balls through.</summary>
    internal static Color NetPaint(float x, float y, float texel)
    {
        float a = Mathf.Repeat(x * 4f + y * 4f, 1f), b = Mathf.Repeat(x * 4f - y * 4f, 1f);
        float line = Mathf.Min(Mathf.Min(a, 1f - a), Mathf.Min(b, 1f - b));
        float k = Mathf.Clamp01(1f - line / 0.07f);
        if (y < 0.07f) k = 1f; // a band along the bottom
        Color c = y < 0.07f ? new Color(0.22f, 0.4f, 0.85f) : new Color(0.96f, 0.97f, 1f);
        c.a = k * (y < 0.07f ? 1f : 0.7f);
        return c;
    }

    /// <summary>A padded corner post (x 0..1 across, y 0..3 up, pivot at its foot): blue, a white stripe, a round cap.</summary>
    internal static Color PostPaint(float x, float y, float texel)
    {
        const float H = 3f;
        float d = Mathf.Max(Mathf.Abs(x - 0.5f) - 0.42f, -y);
        if (y > H - 0.42f) d = Mathf.Max(new Vector2(x - 0.5f, y - (H - 0.42f)).magnitude - 0.42f, -y);
        float a = Mathf.Clamp01(0.5f - d / texel);
        if (a <= 0f) return new Color(0f, 0f, 0f, 0f);
        Color c = new Color(0.22f, 0.45f, 0.92f);
        if (Mathf.Abs(y - H * 0.55f) < 0.25f) c = new Color(0.97f, 0.97f, 1f);
        c *= 0.78f + 0.3f * Mathf.Sqrt(Mathf.Max(0f, 1f - Mathf.Pow((x - 0.5f) / 0.42f, 2f)));
        if (Mathf.Abs(x - 0.36f) < 0.07f && y < H - 0.3f) c = Color.Lerp(c, Color.white, 0.35f);
        if (d > -texel * 1.3f) c *= 0.65f;
        c.a = a;
        return c;
    }

    /// <summary>The soft floor mat (x 0..1, y 0..0.25).</summary>
    internal static Color MatPaint(float x, float y, float texel)
    {
        float d = Mathf.Abs(y - 0.125f) - 0.11f;
        Color c = new Color(0.18f, 0.32f, 0.72f) * (0.85f + 0.4f * y);
        c.a = Mathf.Clamp01(0.5f - d / texel);
        return c;
    }

    static float Hash01(float a)
    {
        float h = Mathf.Sin(a * 12.9898f + 4.1414f) * 43758.5453f;
        return h - Mathf.Floor(h);
    }
}
