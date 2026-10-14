using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A playground slide (chat "slide", WhipIt > Buddy > Put Up The Slide): a deck clips onto the top corner of one of your
/// windows, a bumpy yellow chute waves down to a lower window's top (or the taskbar) and a ladder hangs from the deck to
/// whatever's below it. No window that suits = it stands on the taskbar on its own ladder. The buddies (BuddySlide) climb
/// the ladder or walk out along the window top, sit at the top, queue up behind each other and go: alone or as a train,
/// sitting, lying head first, on their backs, spinning, standing up surfing (and wiping out), catching air over the
/// bumps, and when they're fast at the end the lip flings them off in an arc (into the ball pit, if it's there: they aim
/// for it). This moves them while they're on it (the brain's tower puppet, GummyBrain.SlideStart). Move a window it's on
/// and it re-fits; grab its ladder or deck to carry it (let go near a window's top corner and it clips on there).
/// </summary>
[DefaultExecutionOrder(6)] // after BuddySlide (5) has brought its buddy up: moves everyone on it
[DisallowMultipleComponent]
public class Slide : MonoBehaviour, IGrabTarget
{
    public enum Mount { Window, Standing, Held }
    public enum Trick { Sit, Belly, Back, Spin, Surf }

    /// <summary>The slide that's up (null = none).</summary>
    public static Slide Current { get; private set; }

    public Mount State { get; private set; }
    /// <summary>+1 = the chute goes down to the right of the deck.</summary>
    public int Side => g.side;
    /// <summary>The window the deck is on (0 = standing on its ladder), and the one the chute comes down on (0 = the taskbar).</summary>
    public long Window => g.winA;
    public long LandWindow => g.winB;
    /// <summary>The top corner it's on (standing: the top of its ladder).</summary>
    public Vector2 Corner => g.corner;
    /// <summary>Where the chute starts and ends (the lip), how long it is.</summary>
    public Vector2 Top => path[0];
    public Vector2 End => path[N];
    public float Length => arc[N];
    public float LandY => g.landY;
    /// <summary>The ladder (x, foot, top); HasLadder false = nowhere for one.</summary>
    public bool HasLadder => g.ladder;
    public float LadderX => g.ladderX;
    public float LadderFoot => g.footY;
    public float LadderTop => g.corner.y;
    /// <summary>Put up a moment ago (the buddies come and try it).</summary>
    public bool Fresh => Time.time - putAt > 1.5f && Time.time - putAt < 8f;
    /// <summary>Its draw layer for the windows (Occlusion).</summary>
    public int Layer => State == Mount.Held ? Occlusion.Front : g.winA != 0 && Occlusion.Exists(g.winA) ? Occlusion.LayerOf(g.winA) : Occlusion.Back;
    /// <summary>Riders on it (sitting at the top or going down).</summary>
    public int Riders => riders.Count;
    /// <summary>So far: rides, trains (two or more together), launches off the lip, wipeouts, rides by trick.</summary>
    public int Rides { get; private set; }
    public int Trains { get; private set; }
    public int Launches { get; private set; }
    public int Wipeouts { get; private set; }
    /// <summary>Launches aimed at the ball pit.</summary>
    public int AimedAtPit { get; private set; }
    public readonly int[] ByTrick = new int[5];
    /// <summary>Highest speed anyone reached (tests).</summary>
    public float TopSpeed { get; private set; }
    /// <summary>The next one to go alone does this trick (tests, the menu); null = it picks.</summary>
    public static Trick? NextTrick;
    /// <summary>Something launched off the lip (who, how fast).</summary>
    public static event System.Action<GummyBody, float> Launched;

    public bool Hovered { get; set; }
    public bool Held => State == Mount.Held;

    public const float DeckW = 0.62f, LipH = 0.26f, StandH = 2f;
    /// <summary>The ladder hangs this far out from the window's side (under the deck): room for the biggest buddy to climb
    /// it without rubbing against the window.</summary>
    public const float LadderOut = 0.48f;
    const float SlideG = 14f, Mu = 0.12f, Air = 0.04f, LaunchV = 3.6f;
    const int N = 72;

    struct Geo
    {
        public Vector2 corner;
        public int side;
        public long winA, winB;
        public float landX, landY, ladderX, footY, score;
        public bool ladder;
        public WindowBox boxA;
    }

    class Rider
    {
        public BuddySlide who;
        public GummyBody body;
        public GummyBrain brain;
        public float s, v, lift, liftV, cOff, halfW, t, spin, wipeAt = -1f, bunchT;
        public bool riding, bunch;
        public Rider lead;
        public Trick trick;
    }

    Geo g;
    float putAt;
    Vector2 grabOff;
    Material mat;
    AudioSource loop, sfx;
    AudioClip loopClip, launchClip;
    readonly List<Rider> riders = new List<Rider>();
    readonly Vector2[] path = new Vector2[N + 1], tan = new Vector2[N + 1], nrm = new Vector2[N + 1];
    readonly float[] arc = new float[N + 1], curv = new float[N + 1];

    // drawing
    readonly List<SpriteRenderer> beadsBack = new List<SpriteRenderer>(), beadsFront = new List<SpriteRenderer>(), rungs = new List<SpriteRenderer>(), posts = new List<SpriteRenderer>();
    SpriteRenderer railL, railR, deck, handle;
    readonly List<Vector2> postTop = new List<Vector2>();
    readonly List<float> postFoot = new List<float>();

    /// <summary>Draw orders: the back of the chute, the ladder, the deck behind the buddies (3); the front of the chute in
    /// front of every buddy (their orders run 5..61: 62, so whoever's on it sits in it); the posts at the very back (2).</summary>
    public const int BackOrder = 3, FrontOrder = 62, PostOrder = 2;

    // ------------------------------------------------------------------ putting it up

    /// <summary>Put the slide up near this point (the cursor): on the window that suits best, else standing on the taskbar.</summary>
    public static Slide Put(Material material, Vector2 near, AudioClip whee = null, AudioClip boing = null)
    {
        if (Current) return Current;
        var go = new GameObject("Slide");
        var s = go.AddComponent<Slide>();
        s.mat = material;
        s.loopClip = whee;
        s.launchClip = boing;
        s.putAt = Time.time;
        Current = s;
        s.g = Best(near);
        s.State = s.g.winA != 0 ? Mount.Window : Mount.Standing;
        s.Build();
        return s;
    }

    /// <summary>Take it down (anyone on it falls off).</summary>
    public static void TakeDown()
    {
        if (Current) { Current.ReleaseAll(); Destroy(Current.gameObject); }
        Current = null;
    }

    // the best window corner near the point (the one under the cursor first), else standing on the floor
    static Geo Best(Vector2 near)
    {
        Rect r = ScreenBounds.WorldRect;
        Geo best = default;
        float bestScore = float.MaxValue;
        foreach (var w in DesktopPlatforms.Windows)
        {
            if (!w.movable || w.screen || w.yMax < r.yMin + 1f || w.yMax > r.yMax - 0.7f) continue;
            for (int sd = -1; sd <= 1; sd += 2)
            {
                if (!Fit(w, sd, out Geo f)) continue;
                float score = f.score + 0.25f * (f.corner - near).magnitude - (w.Contains(near) ? 3f : 0f);
                var pit = BallPit.Current; // (its ladder not down in the ball pit, if there's another way)
                if (pit && f.ladder && Mathf.Abs(pit.FloorY - f.footY) < 0.3f && f.ladderX > pit.X0 - 0.45f && f.ladderX < pit.X1 + 0.45f) score += 4f;
                if (score < bestScore) { bestScore = score; best = f; }
            }
        }
        if (bestScore < float.MaxValue) return best;
        r = ScreenBounds.ScreenRectAt(near); // (more than one screen: on the floor of the one near you)
        float x = Mathf.Clamp(near.x, r.xMin + 0.8f, r.xMax - 0.8f);
        return Standing(x, x < r.center.x ? 1 : -1, near.y);
    }

    // standing on its own ladder on the taskbar, the chute down to the taskbar (more than one screen: the floor of the
    // screen at x, y), as near x as it can be without the deck, the chute or the ladder in a window that comes down there
    static Geo Standing(float x, int side, float y)
    {
        Rect r = ScreenBounds.ScreenRectAt(new Vector2(x, y));
        for (int k = 0; k <= 60; k++)
        {
            float cx = x + (k % 2 == 1 ? 1f : -1f) * ((k + 1) / 2) * 0.4f; // (x, then 0.4 either way, 0.8, ...)
            if (cx < r.xMin || cx > r.xMax) continue;
            for (int pass = 0; pass < 2; pass++)
            {
                Geo s = StandAt(cx, pass == 0 ? side : -side, r);
                if (StandFree(s)) return s;
            }
        }
        return StandAt(x, side, r);
    }

    static Geo StandAt(float x, int side, Rect r)
    {
        float dx = StandH * 1.5f + 0.4f;
        if (side > 0 ? x + DeckW + dx > r.xMax - 0.3f : x - DeckW - dx < r.xMin + 0.3f) side = -side; // (room for the chute)
        x = Mathf.Clamp(x, r.xMin + 0.4f + (side < 0 ? DeckW + dx : 0f), r.xMax - 0.4f - (side > 0 ? DeckW + dx : 0f));
        return new Geo
        {
            corner = new Vector2(x, r.yMin + StandH), side = side, winA = 0, winB = 0,
            landX = x + side * (DeckW + dx), landY = r.yMin, ladder = true, ladderX = x + side * LadderOut, footY = r.yMin,
        };
    }

    // none of it (deck, chute, ladder) inside a window that comes down that low (they couldn't get on it)
    static bool StandFree(Geo s)
    {
        float x0 = Mathf.Min(s.corner.x, s.landX) - 0.35f, x1 = Mathf.Max(s.corner.x, s.landX) + 0.35f;
        foreach (var w in DesktopPlatforms.Windows)
            if (w.movable && !w.screen && w.xMax > x0 && w.xMin < x1 && w.yMin < s.corner.y + 0.6f) return false;
        return true;
    }

    /// <summary>Can a slide go on this window's top corner (side +1 = its right corner, the chute going right)? The landing:
    /// a lower window top or the taskbar, about 30 degrees down, nothing in the way.</summary>
    static bool Fit(WindowBox a, int side, out Geo best)
    {
        best = default;
        Rect r = ScreenBounds.WorldRect;
        Vector2 corner = new Vector2(side > 0 ? a.xMax : a.xMin, a.yMax);
        float xs = corner.x + side * DeckW;
        if (xs < r.xMin + 0.3f || xs > r.xMax - 0.3f) return false;
        if (Covered(a, corner, side)) return false;
        float bestScore = float.MaxValue, maxDrop = Mathf.Max(7f, ScreenBounds.ScreenRectAt(corner).height); // (a window as tall as your screen too)
        // the taskbar, and every window top below
        for (int k = -1; k < DesktopPlatforms.Current.Count; k++)
        {
            float y, x0, x1;
            long id;
            if (k < 0) { y = r.yMin; x0 = r.xMin; x1 = r.xMax; id = 0; }
            else
            {
                var p = DesktopPlatforms.Current[k];
                if (DesktopPlatforms.IsIcon(p.id) || p.id == a.id) continue;
                y = p.y; x0 = p.x0; x1 = p.x1; id = p.id;
            }
            float dy = corner.y - y;
            if (dy < 0.9f || dy > maxDrop) continue;
            float want = Mathf.Clamp(dy * 1.55f, 1.1f, 9f);
            float lx = Mathf.Clamp(xs + side * want, x0 + 0.4f, x1 - 0.4f);
            float dx = side * (lx - xs);
            if (dx < 1f || dx > 9.5f || lx < r.xMin + 0.3f || lx > r.xMax - 0.3f) continue;
            float ang = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;
            if (ang < 16f || ang > 62f) continue;
            if (id == 0 && FloorBlocked(lx, y)) continue;
            if (Blocked(new Vector2(xs, corner.y), new Vector2(lx, y + LipH), a.id, id)) continue;
            float score = Mathf.Abs(ang - 32f) * 0.08f + 0.12f * dx + (id != 0 ? -1.2f : 0f);
            if (score < bestScore)
            {
                bestScore = score;
                best = new Geo { corner = corner, side = side, winA = a.id, winB = id, landX = lx, landY = y, score = score, boxA = a };
            }
        }
        if (bestScore == float.MaxValue) return false;
        // the ladder: down from under the deck to whatever's below (no ladder if a window's in the way)
        best.ladderX = corner.x + side * LadderOut;
        best.ladder = FootOf(best.ladderX, corner.y, a.id, out best.footY);
        return true;
    }

    // another window over the corner (where the deck and the handle go)
    static bool Covered(WindowBox a, Vector2 corner, int side)
    {
        float x0 = Mathf.Min(corner.x - side * 0.3f, corner.x + side * (DeckW + 0.3f)), x1 = Mathf.Max(corner.x - side * 0.3f, corner.x + side * (DeckW + 0.3f));
        foreach (var o in DesktopPlatforms.Windows)
            if (o.id != a.id && o.movable && o.xMax > x0 && o.xMin < x1 && o.yMin < corner.y + 0.8f && o.yMax > corner.y - 0.2f) return true;
        return false;
    }

    // a window standing on the floor where the chute comes down
    static bool FloorBlocked(float x, float y)
    {
        foreach (var w in DesktopPlatforms.Windows)
            if (w.movable && x > w.xMin - 0.3f && x < w.xMax + 0.3f && w.yMin < y + 0.6f) return true;
        return false;
    }

    // a window in the way of the chute (and a rider on it), bar the two it joins
    static bool Blocked(Vector2 from, Vector2 to, long a, long b)
    {
        foreach (var w in DesktopPlatforms.Windows)
        {
            if (!w.movable || w.id == a || w.id == b) continue;
            for (int k = 0; k <= 16; k++)
            {
                Vector2 p = Vector2.Lerp(from, to, k / 16f);
                if (p.x > w.xMin - 0.1f && p.x < w.xMax + 0.1f && p.y > w.yMin - 0.15f && p.y < w.yMax + 0.6f) return true;
            }
        }
        return false;
    }

    static bool FootOf(float x, float top, long a, out float foot)
    {
        Rect r = ScreenBounds.WorldRect;
        foot = r.yMin;
        foreach (var p in DesktopPlatforms.Current)
            if (!DesktopPlatforms.IsIcon(p.id) && p.id != a && x > p.x0 + 0.05f && x < p.x1 - 0.05f && p.y < top - 0.4f && p.y > foot) foot = p.y;
        foreach (var w in DesktopPlatforms.Windows)
            if (w.movable && w.id != a && x > w.xMin && x < w.xMax && w.yMax > foot + 0.01f && w.yMin < top) return false; // (a window side in the way)
        return top - foot > 0.4f && top - foot < Mathf.Max(9f, ScreenBounds.ScreenRectAt(new Vector2(x, top)).height + 0.5f);
    }

    void OnEnable() { GrabTargets.Add(this); if (!Current) Current = this; }
    void OnDisable() { GrabTargets.Remove(this); ReleaseAll(); if (Current == this) Current = null; }

    // ------------------------------------------------------------------ the path

    void Shape()
    {
        Vector2 S0 = g.corner + new Vector2(g.side * DeckW, 0.03f), E = new Vector2(g.landX, g.landY + LipH);
        float dx = E.x - S0.x, dy = S0.y - E.y;
        // (a little kicker at the end: the lip points up a bit)
        Vector2 C1 = S0 + new Vector2(dx * 0.32f, 0f), C2 = E + new Vector2(-dx * 0.34f, -Mathf.Min(0.05f + 0.05f * Mathf.Abs(dx), 0.24f));
        // the base curve
        for (int i = 0; i <= N; i++) path[i] = Bez(S0, C1, C2, E, i / (float)N);
        Measure();
        // bumps along the middle
        float L0 = arc[N];
        int humps = Mathf.Clamp(Mathf.RoundToInt(L0 / 1.6f), 1, 4);
        float amp = Mathf.Clamp(0.035f * Mathf.Abs(dx) + 0.03f, 0.05f, 0.11f);
        for (int i = 0; i <= N; i++)
        {
            float u = arc[i] / L0, w = Mathf.Clamp01((u - 0.14f) / 0.68f);
            if (w <= 0f || w >= 1f) continue;
            float env = Mathf.Sin(w * Mathf.PI);
            path[i] += nrm[i] * (amp * env * env * Mathf.Sin(w * humps * 2f * Mathf.PI));
        }
        // never down into what it lands on
        for (int i = N / 2; i <= N; i++) if (path[i].y < g.landY + 0.08f) path[i].y = g.landY + 0.08f;
        Measure();
    }

    void Measure()
    {
        arc[0] = 0f;
        for (int i = 1; i <= N; i++) arc[i] = arc[i - 1] + (path[i] - path[i - 1]).magnitude;
        for (int i = 0; i <= N; i++)
        {
            Vector2 d = path[Mathf.Min(i + 1, N)] - path[Mathf.Max(i - 1, 0)];
            tan[i] = d.sqrMagnitude > 1e-10f ? d.normalized : new Vector2(g.side, 0f);
            Vector2 n = new Vector2(-tan[i].y, tan[i].x);
            nrm[i] = n.y < 0f ? -n : n;
        }
        for (int i = 0; i <= N; i++)
        {
            int a = Mathf.Max(i - 1, 0), b = Mathf.Min(i + 1, N);
            float ds = arc[b] - arc[a];
            curv[i] = ds > 1e-5f ? Vector2.Dot((tan[b] - tan[a]) / ds, -nrm[i]) : 0f;
        }
    }

    static Vector2 Bez(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float u)
    {
        float v = 1f - u;
        return v * v * v * a + 3f * v * v * u * b + 3f * v * u * u * c + u * u * u * d;
    }

    /// <summary>The chute at distance s from the top (before the top: straight back along the deck / the window top).</summary>
    public void Sample(float s, out Vector2 p, out Vector2 t, out Vector2 n, out float k)
    {
        if (s <= 0f) { t = tan[0]; n = nrm[0]; p = path[0] + t * s; k = 0f; return; }
        if (s >= arc[N]) { t = tan[N]; n = nrm[N]; p = path[N] + t * (s - arc[N]); k = 0f; return; }
        int lo = 0, hi = N;
        while (hi - lo > 1) { int m = (lo + hi) >> 1; if (arc[m] <= s) lo = m; else hi = m; }
        float f = (s - arc[lo]) / Mathf.Max(arc[hi] - arc[lo], 1e-6f);
        p = Vector2.Lerp(path[lo], path[hi], f);
        t = Vector2.Lerp(tan[lo], tan[hi], f).normalized;
        n = Vector2.Lerp(nrm[lo], nrm[hi], f).normalized;
        k = Mathf.Lerp(curv[lo], curv[hi], f);
    }

    // ------------------------------------------------------------------ the buddies (BuddySlide brings them, this moves them)

    /// <summary>Where the k-th one in the queue waits (its middle): the top of the chute, then back along the window top
    /// (standing: down the ladder).</summary>
    public Vector2 SeatAt(int k, float cOff)
    {
        if (k <= 0 || g.winA != 0)
        {
            Sample(-k * 0.48f, out Vector2 p, out _, out Vector2 n, out _);
            return p + n * cOff;
        }
        return new Vector2(g.ladderX, g.corner.y - 0.15f - k * 0.55f + cOff);
    }

    /// <summary>How many are waiting at the top.</summary>
    public int Waiting { get { int w = 0; foreach (var r in riders) if (!r.riding) w++; return w; } }

    /// <summary>It sat down at the top (BuddySlide): from now on this moves it.</summary>
    public void Sit(BuddySlide who, GummyBody body, GummyBrain brain, float cOff)
    {
        foreach (var r in riders) if (r.who == who) return;
        riders.Add(new Rider { who = who, body = body, brain = brain, cOff = cOff, halfW = Mathf.Max(body.Extent.width, body.size * 0.8f) * 0.5f });
    }

    /// <summary>It's off (grabbed, or something else came up).</summary>
    public void Remove(BuddySlide who)
    {
        for (int i = riders.Count - 1; i >= 0; i--)
        {
            if (riders[i].who != who) continue;
            var r = riders[i];
            riders.RemoveAt(i);
            foreach (var o in riders) if (o.lead == r) o.lead = null; // (the ones behind go on by themselves)
        }
    }

    public bool Has(BuddySlide who) { foreach (var r in riders) if (r.who == who) return true; return false; }

    void ReleaseAll()
    {
        foreach (var r in riders.ToArray())
        {
            if (r.brain && r.brain.Sliding) r.brain.TowerEnd();
            if (r.who) r.who.Released(false, false);
        }
        riders.Clear();
    }

    // ------------------------------------------------------------------ each frame

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("Rider.Update", t0); }
    void UpdateTimed()
    {
        float dt = Mathf.Min(Time.deltaTime, 0.05f);
        if (!sfx) { sfx = gameObject.AddComponent<AudioSource>(); sfx.playOnAwake = false; sfx.spatialBlend = 0f; }
        if (!loop) { loop = gameObject.AddComponent<AudioSource>(); loop.playOnAwake = false; loop.spatialBlend = 0f; loop.loop = true; loop.clip = loopClip; loop.volume = 0f; }
        if (State == Mount.Window) CheckWindows();

        // anyone that's been taken off (grabbed, or its brain let go)
        for (int i = riders.Count - 1; i >= 0; i--)
        {
            var r = riders[i];
            if (r.body && r.brain && r.brain.Sliding && !r.body.Held && r.who) continue;
            riders.RemoveAt(i);
            foreach (var o in riders) if (o.lead == r) o.lead = null;
            if (r.who) r.who.Released(false, false);
        }

        Depart();
        float fastest = 0f;
        const int sub = 4;
        for (int k = 0; k < sub; k++) for (int i = 0; i < riders.Count; i++) if (riders[i].riding) Move(riders[i], dt / sub);
        for (int i = riders.Count - 1; i >= 0; i--)
        {
            var r = riders[i];
            r.t += dt;
            if (!r.riding) { Wait(r, i); continue; }
            fastest = Mathf.Max(fastest, r.v);
            if (AtEnd(r, dt)) continue;
            Steer(r);
        }
        TopSpeed = Mathf.Max(TopSpeed, fastest);
        if (loop && loopClip)
        {
            float want = Mathf.Clamp01(fastest / 7f) * 0.5f;
            loop.volume = Mathf.MoveTowards(loop.volume, want, dt * 2f);
            loop.pitch = 0.8f + 0.06f * fastest;
            if (want > 0.01f && !loop.isPlaying) loop.Play();
            else if (want <= 0.01f && loop.volume <= 0.01f && loop.isPlaying) loop.Stop();
        }
        Place();
    }

    // the ones at the top: sit in their places; the first goes when the chute's clear (with whoever's waiting behind it: a train)
    void Wait(Rider r, int index)
    {
        int k = 0;
        foreach (var o in riders) { if (o == r) break; if (!o.riding) k++; }
        Vector2 at = SeatAt(k, r.cOff);
        Fly(r.body, at, Vector2.zero, 6f);
        r.body.TurnTo = float.NaN;
        float w = 0.03f * Mathf.Sin(r.t * 7f);
        r.body.Pose = new Vector2(1.04f + w, 0.96f - w); // fidgeting
    }

    void Depart()
    {
        Rider head = null;
        int seated = 0;
        foreach (var r in riders) { if (r.riding) { if (r.s < 1.6f || r.bunch) return; continue; } if (head == null) head = r; seated++; }
        if (head == null) return;
        // sitting at the very top (not still on its way along the window top)
        if ((head.body.Center - SeatAt(0, head.cOff)).magnitude > 0.12f || head.t < 0.5f) return;
        bool coming = BuddySlide.Coming(this);
        bool train = g.winA != 0;
        if (train && coming && head.t < 3.5f && seated < 3) return; // (wait for the one coming up: a train)
        var group = new List<Rider>();
        foreach (var r in riders) if (!r.riding && group.Count < (train ? 3 : 1)) group.Add(r);
        Rider lead = null;
        float s = 0f;
        for (int i = 0; i < group.Count; i++)
        {
            var r = group[i];
            if (i > 0) s -= (r.halfW + group[i - 1].halfW) * 1.05f;
            r.riding = true;
            r.s = s;
            r.v = 1.2f;
            r.lead = lead;
            r.trick = group.Count > 1 ? Trick.Sit : NextTrick ?? PickTrick(r);
            if (group.Count == 1) NextTrick = null;
            r.wipeAt = r.trick == Trick.Surf && Random.value < WipeChance(r) ? Random.Range(0.35f, 0.7f) : -1f;
            r.spin = 0f;
            r.t = 0f;
            if (i == 0) lead = r;
            Rides++;
            ByTrick[(int)r.trick]++;
        }
        if (group.Count > 1) Trains++;
        if (head.who) head.who.Going(group.Count, head.trick);
        for (int i = 1; i < group.Count; i++) if (group[i].who) group[i].who.Going(-i, Trick.Sit);
    }

    // tricks get better with its level: sitting; on its back (3); head first (4); spinning (6); surfing (8, wipeouts until 12)
    Trick PickTrick(Rider r)
    {
        int lv = r.who ? r.who.Level : 10;
        var can = new List<Trick> { Trick.Sit };
        if (lv >= 3) can.Add(Trick.Back);
        if (lv >= 4) can.Add(Trick.Belly);
        if (lv >= 6) can.Add(Trick.Spin);
        if (lv >= 8) can.Add(Trick.Surf);
        if (can.Count > 1 && Random.value < 0.25f) return Trick.Sit;
        return can[Random.Range(0, can.Count)];
    }

    static float WipeChance(Rider r) { int lv = r.who ? r.who.Level : 10; return Mathf.Clamp01(0.45f - 0.04f * (lv - 8)); }

    // down the chute: gravity along it, friction, a little air drag; over a bump fast enough it leaves the chute (air)
    void Move(Rider r, float h)
    {
        if (r.lead != null && r.lead.riding && r.lead.body && riders.Contains(r.lead))
        {
            // in a train: right behind the one in front
            float gap = (r.halfW + r.lead.halfW) * (r.lead.bunch ? 0.72f : 1.05f);
            r.s = Mathf.MoveTowards(r.s, r.lead.s - gap, (r.lead.v + 2f) * h);
            r.v = r.lead.v;
            r.lift = Mathf.Max(0f, r.lift + r.liftV * h);
            if (r.lift <= 0f) r.liftV = 0f; else r.liftV -= SlideG * h;
            return;
        }
        if (r.bunch) return;
        Sample(r.s, out _, out Vector2 t, out Vector2 n, out float k);
        float mu = Mu * (r.trick == Trick.Belly || r.trick == Trick.Back ? 0.5f : r.trick == Trick.Surf ? 0.75f : 1f);
        float a = -SlideG * t.y - (r.lift > 0f ? 0f : mu * SlideG * Mathf.Max(n.y, 0f)) - Air * r.v * r.v;
        r.v = Mathf.Max(r.s < 0.2f ? 1.2f : 0.5f, r.v + a * h);
        r.s += r.v * h;
        float push = r.v * r.v * k - SlideG * n.y;
        if (r.lift > 0f || push > 0f)
        {
            r.liftV += push * h;
            r.lift += r.liftV * h;
            if (r.lift < 0f) { r.lift = 0f; r.liftV = 0f; }
            r.lift = Mathf.Min(r.lift, 0.6f);
        }
    }

    // the body follows its spot on the chute, turned for its trick
    void Steer(Rider r)
    {
        Sample(r.s, out Vector2 p, out Vector2 t, out Vector2 n, out _);
        float lying = r.trick == Trick.Belly || r.trick == Trick.Back ? r.halfW * 0.9f : r.cOff;
        Vector2 at = p + n * (lying + r.lift);
        Fly(r.body, at, t * r.v, r.v + 5f);
        float slope = Mathf.Atan2(t.y, t.x) * Mathf.Rad2Deg, face = g.side > 0 ? slope : slope + 180f; // (face: the slope's angle seen going down it)
        float u = r.s / Mathf.Max(Length, 0.01f);
        var b = r.body;
        switch (r.trick)
        {
            case Trick.Sit: b.TurnTo = face * 0.7f; b.Pose = new Vector2(1.08f, 0.93f); break;
            case Trick.Belly: b.TurnTo = slope - 90f; b.Pose = new Vector2(0.86f, 1.16f); break;   // its top points down the chute
            case Trick.Back: b.TurnTo = slope + 90f; b.Pose = new Vector2(0.86f, 1.16f); break;    // its top points up the chute
            case Trick.Spin:
                if (u > 0.15f && u < 0.85f) r.spin += -g.side * 720f / Mathf.Max(0.7f * Length / Mathf.Max(r.v, 1f), 0.4f) * Time.deltaTime;
                b.TurnTo = face * 0.7f + r.spin;
                b.Pose = Vector2.one;
                break;
            case Trick.Surf:
                b.TurnTo = 8f * Mathf.Sin(r.t * 9f) - g.side * 6f;
                b.Pose = new Vector2(0.93f, 1.08f);
                if (r.wipeAt > 0f && u > r.wipeAt) Wipeout(r, t);
                break;
        }
    }

    // standing up and it's wobbled off: off the side, tumbling
    void Wipeout(Rider r, Vector2 t)
    {
        Wipeouts++;
        r.wipeAt = -1f;
        var b = r.body;
        float S = b.size / 0.6f;
        Off(r);
        b.Hop(t * (r.v * 0.6f) + new Vector2(0f, 3.2f * Mathf.Sqrt(S)) - b.Velocity, 0.5f);
        b.Spin(-g.side * Random.Range(7f, 11f), 0.5f);
        if (r.who) r.who.Released(false, true);
    }

    // the end of the chute: fast = off the lip in an arc (at the ball pit if it can reach); slow = it stops at the end
    // (the ones behind bump into it) and gets off
    bool AtEnd(Rider r, float dt)
    {
        if (r.lead != null && riders.Contains(r.lead)) return false; // (in a train: the one in front decides)
        if (r.bunch)
        {
            r.bunchT += dt;
            Sample(Length, out Vector2 p, out Vector2 t, out Vector2 n, out _);
            Fly(r.body, p + n * r.cOff, Vector2.zero, 4f);
            r.body.Pose = new Vector2(1.18f, 0.85f);
            if (r.bunchT < 0.5f) return true;
            // everyone in the bunch gets off, front first
            var off = new List<Rider> { r };
            foreach (var o in riders) if (o != r && o.riding && FollowsChain(o, r)) off.Add(o);
            foreach (var o in off)
            {
                float S = o.body.size / 0.6f;
                Off(o);
                o.body.Hop(new Vector2(g.side * 1.6f * Mathf.Sqrt(S), 2.6f * Mathf.Sqrt(S)) - o.body.Velocity, 0.5f);
                if (o.who) o.who.Released(false, false);
            }
            return true;
        }
        if (r.s < Length) return false;
        if (r.v < LaunchV) { r.bunch = true; r.bunchT = 0f; r.v = 0f; if (r.who) r.who.Bumped(); return true; }
        Launch(r);
        return true;
    }

    static bool FollowsChain(Rider o, Rider head) { for (var l = o.lead; l != null; l = l.lead) if (l == head) return true; return false; }

    void Launch(Rider r)
    {
        Sample(Length, out _, out Vector2 t, out _, out _);
        var b = r.body;
        float g0 = b.gravity, v = r.v * 1.12f;
        Vector2 dir = t;
        Vector2 launch = dir * v;
        // the ball pit within reach out there: it aims for it (a hop over its rail into the middle)
        var pit = BallPit.Current;
        if (pit && pit.State == BallPit.Mount.Standing)
        {
            float px = 0.5f * (pit.X0 + pit.X1), d = (px - End.x) * g.side;
            float lipY = End.y + r.cOff, apex = Mathf.Max(lipY, pit.WallTop + b.size * 0.6f) + 0.3f, ty = pit.Fill + 0.1f;
            float vy = Mathf.Sqrt(2f * g0 * (apex - lipY)), T = vy / g0 + Mathf.Sqrt(2f * Mathf.Max(apex - ty, 0.05f) / g0);
            Vector2 aim = new Vector2(g.side * d / T * (1f + b.Drag * T * 0.5f), vy);
            if (d > 0.3f && aim.magnitude < v * 1.7f && aim.magnitude > v * 0.4f) { launch = aim; AimedAtPit++; }
        }
        Off(r);
        b.Hop(launch - b.Velocity, 0.6f);
        if (r.who && r.who.Level >= 9 && r.trick != Trick.Sit) b.Spin(-g.side * 2f * Mathf.PI / 0.45f, 0.6f); // a flip off the lip
        r.brain.NoDizzyUntil = Time.time + 2.5f;
        Launches++;
        Launched?.Invoke(b, v);
        if (sfx && launchClip) { sfx.pitch = Random.Range(0.95f, 1.15f); sfx.PlayOneShot(launchClip, 0.6f); }
        if (r.who) r.who.Released(true, false);
        // the ones behind it now go on by themselves
        foreach (var o in riders) if (o.lead == r) o.lead = null;
    }

    // off the slide: the brain has it again
    void Off(Rider r)
    {
        riders.Remove(r);
        foreach (var o in riders) if (o.lead == r) { o.lead = null; o.v = r.v; }
        r.body.TurnTo = float.NaN;
        if (r.brain && r.brain.Sliding) r.brain.TowerEnd();
    }

    static void Fly(GummyBody b, Vector2 at, Vector2 along, float maxV)
    {
        Vector2 v = along + (at - b.Center) * 14f;
        if (v.magnitude > maxV) v = v.normalized * maxV;
        b.Glide(v, 90f * Mathf.Sqrt(b.size / 0.6f));
    }

    // ------------------------------------------------------------------ the windows it's on moved

    void CheckWindows()
    {
        bool aOk = false;
        foreach (var w in DesktopPlatforms.Windows)
            if (w.id == g.winA) { aOk = Mathf.Abs(w.xMin - g.boxA.xMin) < 0.03f && Mathf.Abs(w.xMax - g.boxA.xMax) < 0.03f && Mathf.Abs(w.yMax - g.boxA.yMax) < 0.03f; break; }
        bool bOk = g.winB == 0;
        if (!bOk)
            foreach (var p in DesktopPlatforms.Current)
                if (p.id == g.winB) { bOk = Mathf.Abs(p.y - g.landY) < 0.03f && g.landX > p.x0 + 0.1f && g.landX < p.x1 - 0.1f; break; }
        if (aOk && bOk) return;
        // re-fit on the same window if it's still there (either side), else stand where the deck was
        ReleaseAll();
        WindowBox a = default;
        bool found = false;
        foreach (var w in DesktopPlatforms.Windows) if (w.id == g.winA) { a = w; found = true; break; }
        if (found && (Fit(a, g.side, out Geo f) || Fit(a, -g.side, out f))) g = f;
        else { g = Standing(g.corner.x, g.side, g.corner.y); State = Mount.Standing; }
        Build();
    }

    // ------------------------------------------------------------------ you

    public bool Contains(Vector2 p)
    {
        if (Occlusion.Hidden(g.corner + new Vector2(g.side * 0.2f, 0.05f), Layer)) return false;
        if (g.ladder && Mathf.Abs(p.x - g.ladderX) < 0.17f && p.y > g.footY && p.y < g.corner.y + 0.25f) return true;
        float x0 = Mathf.Min(g.corner.x - g.side * 0.05f, g.corner.x + g.side * DeckW), x1 = Mathf.Max(g.corner.x - g.side * 0.05f, g.corner.x + g.side * DeckW);
        return p.x > x0 && p.x < x1 && Mathf.Abs(p.y - g.corner.y) < 0.12f;
    }

    public void BeginGrab(Vector2 p)
    {
        ReleaseAll();
        State = Mount.Held;
        grabOff = g.corner - p;
    }

    public void DragTo(Vector2 p)
    {
        Vector2 want = p + grabOff;
        Rect r = ScreenBounds.ScreenRectAt(want);
        want.x = Mathf.Clamp(want.x, r.xMin + 0.3f, r.xMax - 0.3f);
        want.y = Mathf.Clamp(want.y, r.yMin + 1f, r.yMax - 0.6f);
        Vector2 d = want - g.corner;
        g.corner += d;
        g.landX += d.x;
        g.landY += d.y;
        g.ladderX += d.x;
        g.footY += d.y;
        g.winA = g.winB = 0;
        Build();
    }

    public void EndGrab()
    {
        // near a window's top corner: clips on there; else it stands on the taskbar
        float best = 0.8f;
        WindowBox bw = default;
        int bs = 0;
        foreach (var w in DesktopPlatforms.Windows)
        {
            if (!w.movable || w.screen) continue;
            for (int sd = -1; sd <= 1; sd += 2)
            {
                float d = (new Vector2(sd > 0 ? w.xMax : w.xMin, w.yMax) - g.corner).magnitude;
                if (d < best) { best = d; bw = w; bs = sd; }
            }
        }
        if (bs != 0 && (Fit(bw, bs, out Geo f) || Fit(bw, -bs, out f))) { g = f; State = Mount.Window; }
        else { g = Standing(g.corner.x, g.side, g.corner.y); State = Mount.Standing; }
        Build();
    }

    // ------------------------------------------------------------------ drawing

    void Build()
    {
        Shape();
        if (!deck)
        {
            deck = Part("Slide Deck", PlankSprite(), BackOrder);
            handle = Part("Slide Handle", HandleSprite(), FrontOrder);
            railL = Part("Ladder Rail", RailSprite(), BackOrder);
            railR = Part("Ladder Rail", RailSprite(), BackOrder);
        }
        // posts under the chute (and under a standing deck), down to whatever's below
        postTop.Clear();
        postFoot.Clear();
        float L = Length;
        foreach (float u in new[] { 0.36f, 0.68f })
        {
            Sample(u * L, out Vector2 p, out _, out _, out _);
            if (Ground(p.x, p.y - 0.1f, out float foot) && p.y - 0.1f - foot > 0.25f) { postTop.Add(p - new Vector2(0f, 0.08f)); postFoot.Add(foot); }
        }
        if (g.winA == 0) { postTop.Add(g.corner + new Vector2(g.side * (DeckW - 0.04f), -0.03f)); postFoot.Add(g.footY); }
        Place();
    }

    // the highest surface under a point (the taskbar if nothing else)
    static bool Ground(float x, float y, out float foot)
    {
        Rect r = ScreenBounds.WorldRect;
        foot = r.yMin;
        foreach (var p in DesktopPlatforms.Current)
            if (!DesktopPlatforms.IsIcon(p.id) && x > p.x0 && x < p.x1 && p.y < y && p.y > foot) foot = p.y;
        return y - foot < 9f;
    }

    SpriteRenderer Part(string name, Sprite s, int order)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = s;
        sr.sortingOrder = order;
        sr.color = Color.white;
        if (mat) sr.sharedMaterial = mat;
        return sr;
    }

    void Place()
    {
        if (!deck) return;
        int layer = Layer;
        // the chute: overlapping pieces along it, a back (behind whoever's on it) and a low front (in front of them)
        float L = Length, step = 0.11f;
        int n = Mathf.Max(2, Mathf.CeilToInt(L / step) + 1);
        while (beadsBack.Count < n) { beadsBack.Add(Part("Chute (back)", BackSprite(), BackOrder)); beadsFront.Add(Part("Chute", FrontSprite(), FrontOrder)); }
        for (int k = 0; k < beadsBack.Count; k++)
        {
            if (k >= n) { beadsBack[k].enabled = beadsFront[k].enabled = false; continue; }
            float s = Mathf.Min(k * step, L);
            Sample(s, out Vector2 p, out Vector2 t, out Vector2 nn, out _);
            float deg = Mathf.Atan2(t.y, t.x) * Mathf.Rad2Deg;
            bool hid = Occlusion.Hidden(p, layer);
            Set(beadsBack[k], p + nn * 0.04f, deg, new Vector2(0.17f, 0.28f / 0.5f), hid);
            Set(beadsFront[k], p - nn * 0.02f, deg, new Vector2(0.17f, 0.16f / 0.5f), hid);
        }
        // the deck and the handle at the top
        Vector2 c = g.corner;
        float dx0 = c.x - g.side * 0.06f, dx1 = c.x + g.side * (DeckW + 0.02f);
        Set(deck, new Vector2(0.5f * (dx0 + dx1), c.y - 0.03f), 0f, new Vector2(Mathf.Abs(dx1 - dx0), 0.07f / 0.25f), Occlusion.Hidden(c, layer));
        Vector2 hp = c + new Vector2(g.side * (DeckW - 0.03f), 0.16f);
        Set(handle, hp, 0f, new Vector2(0.24f * g.side, 0.34f), Occlusion.Hidden(hp, layer));
        // the ladder
        float top = c.y + 0.22f, len = top - g.footY;
        bool ladder = g.ladder && len > 0.2f;
        Set(railL, new Vector2(g.ladderX - 0.13f, 0.5f * (top + g.footY)), 0f, new Vector2(0.06f / 0.25f, len), !ladder || Occlusion.Hidden(new Vector2(g.ladderX, g.footY + 0.1f), layer));
        Set(railR, new Vector2(g.ladderX + 0.13f, 0.5f * (top + g.footY)), 0f, new Vector2(0.06f / 0.25f, len), !ladder || Occlusion.Hidden(new Vector2(g.ladderX, g.footY + 0.1f), layer));
        int nr = ladder ? Mathf.FloorToInt((c.y - 0.06f - g.footY) / 0.2f) : 0;
        while (rungs.Count < nr) rungs.Add(Part("Ladder Rung", RungSprite(), BackOrder));
        for (int k = 0; k < rungs.Count; k++)
        {
            if (k >= nr) { rungs[k].enabled = false; continue; }
            Vector2 rp = new Vector2(g.ladderX, g.footY + 0.14f + k * 0.2f);
            Set(rungs[k], rp, 0f, new Vector2(0.3f, 0.05f / 0.25f), Occlusion.Hidden(rp, layer));
        }
        // posts
        while (posts.Count < postTop.Count) posts.Add(Part("Slide Post", PostSprite(), PostOrder));
        for (int k = 0; k < posts.Count; k++)
        {
            if (k >= postTop.Count) { posts[k].enabled = false; continue; }
            float h = postTop[k].y - postFoot[k];
            Set(posts[k], new Vector2(postTop[k].x, 0.5f * (postTop[k].y + postFoot[k])), 0f, new Vector2(0.07f / 0.25f, h), Occlusion.Hidden(new Vector2(postTop[k].x, postFoot[k] + 0.1f), layer));
        }
    }

    static void Set(SpriteRenderer sr, Vector2 at, float deg, Vector2 size, bool hidden)
    {
        sr.transform.position = new Vector3(at.x, at.y, 0f);
        sr.transform.rotation = Quaternion.Euler(0f, 0f, deg);
        sr.transform.localScale = new Vector3(size.x, size.y, 1f);
        sr.enabled = !hidden;
    }

    // ------------------------------------------------------------------ sprites (baked once)

    static Sprite s_front, s_back, s_rail, s_rung, s_plank, s_post, s_handle;
    static Sprite FrontSprite() { if (!s_front) s_front = Bake(32, 16, ChuteFrontPaint, "Chute"); return s_front; }
    static Sprite BackSprite() { if (!s_back) s_back = Bake(32, 16, ChuteBackPaint, "Chute Back"); return s_back; }
    static Sprite RailSprite() { if (!s_rail) s_rail = Bake(8, 32, (x, y, t) => TubePaint(x, y, t, new Color(0.92f, 0.24f, 0.26f), true), "Ladder Rail"); return s_rail; }
    static Sprite RungSprite() { if (!s_rung) s_rung = Bake(32, 8, (x, y, t) => TubePaint(x, y, t, new Color(1f, 0.82f, 0.22f), false), "Ladder Rung"); return s_rung; }
    static Sprite PlankSprite() { if (!s_plank) s_plank = Bake(32, 8, (x, y, t) => TubePaint(x, y, t, new Color(0.26f, 0.52f, 0.95f), false), "Slide Deck"); return s_plank; }
    static Sprite PostSprite() { if (!s_post) s_post = Bake(8, 32, (x, y, t) => TubePaint(x, y, t, new Color(0.26f, 0.52f, 0.95f), true), "Slide Post"); return s_post; }
    static Sprite HandleSprite() { if (!s_handle) s_handle = Bake(8, 16, HandlePaint, "Slide Handle"); return s_handle; }

    // w x h pixels, x 0..1 across, y 0..h/w up; the longer side is 1 unit (like the hoop's sprites)
    static Sprite Bake(int w, int h, System.Func<float, float, float, Color> paint, string name)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = name };
        var px = new Color32[w * h];
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                Color c = paint((i + 0.5f) / w, (j + 0.5f) / h, 1f / Mathf.Max(w, h));
                px[j * w + i] = new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), Mathf.Clamp01(c.a));
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), Mathf.Max(w, h));
    }

    static Color Clear => new Color(0f, 0f, 0f, 0f);

    /// <summary>A piece of the chute's front wall (u along, v across 0..1): sunny yellow plastic, a red lip along the top,
    /// a shine, rounded ends that overlap the next piece.</summary>
    internal static Color ChuteFrontPaint(float u, float v, float texel)
    {
        float ex = Mathf.Max(Mathf.Abs(u - 0.5f) - 0.4f, 0f) * 2f;
        float d = Mathf.Sqrt(ex * ex + Mathf.Pow(Mathf.Max(Mathf.Abs(v - 0.5f) - 0.38f, 0f), 2f)) - 0.1f;
        float a = Mathf.Clamp01(0.5f - d / (texel * 2f));
        if (a <= 0f) return Clear;
        Color c = Color.Lerp(new Color(0.97f, 0.56f, 0.08f), new Color(1f, 0.84f, 0.2f), Mathf.Clamp01(v * 1.3f));
        if (v > 0.74f) c = Color.Lerp(new Color(0.95f, 0.22f, 0.24f), new Color(1f, 0.5f, 0.5f), Mathf.Clamp01((v - 0.74f) / 0.26f) * 0.6f); // the lip
        if (v > 0.5f && v < 0.6f) c = Color.Lerp(c, Color.white, 0.4f);                                                                         // shine
        if (v < 0.12f) c *= 0.8f;
        c.a = a;
        return c;
    }

    /// <summary>A piece of the chute's back wall and bed (behind the rider): deeper orange, darker inside.</summary>
    internal static Color ChuteBackPaint(float u, float v, float texel)
    {
        float ex = Mathf.Max(Mathf.Abs(u - 0.5f) - 0.4f, 0f) * 2f;
        float d = Mathf.Sqrt(ex * ex + Mathf.Pow(Mathf.Max(Mathf.Abs(v - 0.5f) - 0.38f, 0f), 2f)) - 0.1f;
        float a = Mathf.Clamp01(0.5f - d / (texel * 2f));
        if (a <= 0f) return Clear;
        Color c = Color.Lerp(new Color(0.78f, 0.36f, 0.05f), new Color(0.95f, 0.6f, 0.12f), v);
        if (v > 0.8f) c = new Color(0.86f, 0.2f, 0.22f);  // the back lip
        c.a = a;
        return c;
    }

    /// <summary>A round plastic tube (vertical: x across; else y across), shaded, a shine.</summary>
    internal static Color TubePaint(float u, float v, float texel, Color baseC, bool vertical)
    {
        float k = vertical ? (u - 0.5f) * 2f : (v - 0.5f) * 2f, along = vertical ? v : u;
        float d = Mathf.Abs(k) - 0.9f;
        float ends = Mathf.Min(along, 1f - along);
        float a = Mathf.Clamp01((0.9f - Mathf.Abs(k)) * 6f) * Mathf.Clamp01(ends * 40f);
        if (a <= 0f) return Clear;
        Color c = baseC * (0.62f + 0.42f * Mathf.Sqrt(Mathf.Max(0f, 1f - k * k)));
        if (k < -0.15f && k > -0.55f) c = Color.Lerp(c, Color.white, 0.35f);
        if (d > -0.15f) c *= 0.7f;
        c.a = a;
        return c;
    }

    /// <summary>The handle at the top of the chute (x 0..1, y 0..2): a red loop, like a playground grab bar.</summary>
    internal static Color HandlePaint(float u, float v, float texel)
    {
        // a bar up, curving over at the top
        float y = v * 2f, x = u;
        float d = Mathf.Abs(x - 0.35f) - 0.18f;
        if (y > 1.6f) d = Mathf.Abs(new Vector2(x - 0.6f, y - 1.6f).magnitude - 0.25f) - 0.18f;
        if (y < 0.02f) d = 1f;
        float a = Mathf.Clamp01(0.5f - d / (texel * 2f));
        if (a <= 0f) return Clear;
        Color c = new Color(0.92f, 0.24f, 0.26f) * (0.75f + 0.3f * Mathf.Clamp01(1f - Mathf.Abs(x - 0.3f) * 3f));
        c.a = a;
        return c;
    }
}
