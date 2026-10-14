using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Plans how a buddy gets to a spot (a snack): the desktop as surfaces it can stand on (the floor between solid
/// windows, and the visible tops of windows) joined by the moves it knows (BuddySkills): hop down off an edge, hop up a
/// low step, leap onto a window, jump up beside a window side and pull itself over, or climb it. The cheapest route
/// within what it can do wins; it only needs the first move (it plans again after every landing).
/// No route = it doesn't go (instead of hopping into the same window side forever).
/// With a rope (BuddySkills.Swing) two more moves: swing from a window's bottom edge or the top of the screen and let go
/// at the right moment (the swing is simulated to find when), and climb a rope hooked on a window's top corner.
/// With a skateboard (BuddySkills.Skate) long rides along a surface cost less, and it can ollie off the end of a surface
/// at speed and fly further than it can hop. With a paper plane (BuddySkills.Glide) it glides down from a window top to a
/// lower surface far away. With a balloon (BuddySkills.Balloon) it floats straight up beside a higher surface, across
/// above it and lets go.
/// </summary>
public static class BuddyRoute
{
    public enum Move { Walk, Drop, Hop, Leap, WallJump, Climb, Swing, Grapple, Ollie, Glide, Zip, Float, Jet }

    /// <summary>What the buddy can do (heights in world units).</summary>
    public struct Abilities
    {
        public bool drop, climb;
        public float hopUp, hopReach, leapUp, leapReach, wallUp;
        public int moves;
        public float rope;      // longest rope, hook to its middle (0 = no rope moves)
        public float skate;     // top skateboard speed (0 = no skateboard)
        public float glide;     // paper plane: how far it glides for every unit it comes down (0 = no paper plane)
        public float zip;       // longest zipline it can string (0 = can't)
        public bool zipRide;    // can slide down a zipline that's already up
        public float balloon;   // balloon: how far across it can drift on the way up (0 = no balloon)
        public float jet;       // jetpack: how high it can fly up (0 = no jetpack); it doesn't always work
    }

    /// <summary>The buddy right now: feet (x, y), half its width, its height; solid = windows' sides and bottoms block it.</summary>
    public struct Body
    {
        public float x, y, halfW, height;
        public bool onWindow, solid;
        public float g, drag, cOff; // its gravity and air drag (for swings), how high its middle is above its feet
        public float size;          // its size (rope moves are planned with this: steady while it squashes and breathes)
    }

    /// <summary>Somewhere to stand: x0..x1 at height y; win = the window whose top it is (0 = the floor).</summary>
    public struct Surface
    {
        public float x0, x1, y;
        public long win;
    }

    /// <summary>
    /// One move: hop over to takeoffX on the surface it's on, then do the move, landing at (landX, landY).
    /// Walk = the goal is on this surface: hop to landX. side = the way it goes (-1 left, +1 right).
    /// wall = up beside a window side at wallX (straight up, then over the top), not in an arc.
    /// </summary>
    public struct Step
    {
        public Move move;
        public float takeoffX, landX, landY, wallX;
        public int side;
        public bool wall;
        public WindowBox box;   // the window it climbs (Climb)
        public string key;      // "from>to:move", to remember what didn't work
        // rope moves: where the rope hooks on (drawn), where it hangs from (physics), how it jumps off, rope length,
        // and where on the target it may let go to land (zone0..zone1)
        public Vector2 hook, hang, launch;
        public float rope, zone0, zone1, wait; // rope: pulls itself up to this length; wait: how long the rope flies before it catches
        public float caught;                   // how far it is from the hook when the rope catches
        public float fromY, from0, from1;      // the surface it swings from (to swing back onto if it can't make it)
    }

    static readonly List<Surface> surf = new List<Surface>();
    static readonly List<Vector2> cuts = new List<Vector2>();
    static bool solidNow = true; // the plan being made: windows' sides and bottoms block it

    // in its way: a solid window (if windows are solid for it), or a bit of the world on no screen (always)
    static bool Solid(WindowBox w) => w.movable && (solidNow || w.screen);

    /// <summary>The surfaces of the last plan (for tests and debugging).</summary>
    public static IReadOnlyList<Surface> Surfaces => surf;

    // ------------------------------------------------------------------ the map

    /// <summary>Where it can stand: floor pieces between windows it can't walk under, and window tops.</summary>
    public static void Build(float bodyHeight, bool solid)
    {
        surf.Clear();
        cuts.Clear();
        Rect r = ScreenBounds.WorldRect;
        float fy = r.yMin;
        var wins = DesktopPlatforms.Windows;
        solidNow = solid;
        if (solid || ScreenBounds.Multi)
            for (int k = 0; k < wins.Count; k++)
            {
                WindowBox w = wins[k];
                if (Solid(w) && w.yMin < fy + bodyHeight && w.yMax > fy + 0.05f) cuts.Add(new Vector2(w.xMin, w.xMax));
            }
        cuts.Sort((a, b) => a.x.CompareTo(b.x));
        float x = r.xMin;
        foreach (var c in cuts)
        {
            if (c.x > x) surf.Add(new Surface { x0 = x, x1 = Mathf.Min(c.x, r.xMax), y = fy, win = 0 });
            x = Mathf.Max(x, c.y);
        }
        if (x < r.xMax) surf.Add(new Surface { x0 = x, x1 = r.xMax, y = fy, win = 0 });

        var plats = DesktopPlatforms.Current;
        for (int k = 0; k < plats.Count; k++)
        {
            Platform p = plats[k];
            float x0 = Mathf.Max(p.x0, r.xMin), x1 = Mathf.Min(p.x1, r.xMax);
            if (x1 - x0 < 0.05f || p.y <= fy + 0.02f || p.y > r.yMax - 0.1f) continue;
            surf.Add(new Surface { x0 = x0, x1 = x1, y = p.y, win = p.id });
        }
    }

    /// <summary>The height of what's under a point (the highest surface below it): where to bring the ball back to.</summary>
    public static bool SurfaceUnder(Vector2 p, float bodyHeight, bool solid, out float y)
    {
        Build(bodyHeight, solid);
        int i = Below(p.x, p.y);
        y = i >= 0 ? surf[i].y : 0f;
        return i >= 0;
    }

    /// <summary>The surface under a point (feet at y), or -1.</summary>
    public static int SurfaceAt(float x, float y, bool preferWindow = true)
    {
        int best = -1;
        float bd = 0.15f;
        for (int i = 0; i < surf.Count; i++)
        {
            Surface s = surf[i];
            if (x < s.x0 - 0.12f || x > s.x1 + 0.12f) continue;
            float d = Mathf.Abs(s.y - y);
            if (preferWindow && s.win != 0) d -= 0.02f;
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }

    /// <summary>A fingerprint of where the windows are (when it changes, moves that failed may work now).</summary>
    public static int LayoutHash()
    {
        int h = 17;
        var wins = DesktopPlatforms.Windows;
        for (int k = 0; k < wins.Count; k++)
        {
            WindowBox w = wins[k];
            h = h * 31 + Mathf.RoundToInt(w.xMin * 5f);
            h = h * 31 + Mathf.RoundToInt(w.xMax * 5f);
            h = h * 31 + Mathf.RoundToInt(w.yMin * 5f);
            h = h * 31 + Mathf.RoundToInt(w.yMax * 5f);
        }
        return h;
    }

    // ------------------------------------------------------------------ planning

    // cost so far, + how far it'd still be from the goal (so of two ways onto the same surface it keeps the one that lands nearer)
    struct Node { public float cost, est, entry; public int moves, parent; public Step via; public bool done; }
    static Node[] nodes = new Node[32];
    static readonly List<Step> edges = new List<Step>();

    /// <summary>
    /// The cheapest way from where the body is to goal (x on the surface at height goal.y) with these abilities.
    /// first = the move to do now; cost ~ distance + effort; moves = how many moves (0 = just walk over).
    /// banned: moves that didn't work from here lately (by Step.key). False if it can't get there.
    /// </summary>
    public static bool Find(Body b, Vector2 goal, Abilities a, Func<string, bool> banned, out Step first, out float cost, out int moves)
    {
        first = default;
        cost = float.MaxValue;
        moves = 0;
        Build(b.height, b.solid);
        int start = SurfaceAt(b.x, b.y, b.onWindow);
        int end = SurfaceAt(goal.x, goal.y);
        if (start < 0 || end < 0) return false;
        if (start == end)
        {
            first = new Step { move = Move.Walk, takeoffX = goal.x, landX = goal.x, landY = goal.y, side = goal.x >= b.x ? 1 : -1 };
            cost = Ride(Mathf.Abs(goal.x - b.x), b, a);
            return true;
        }

        int n = surf.Count;
        if (nodes.Length < n) nodes = new Node[n * 2];
        for (int i = 0; i < n; i++) nodes[i] = new Node { cost = float.MaxValue, est = float.MaxValue, parent = -1 };
        nodes[start].cost = 0f;
        nodes[start].est = Ride(Mathf.Abs(goal.x - b.x), b, a);
        nodes[start].entry = b.x;

        while (true)
        {
            // the cheapest one not done yet
            int u = -1;
            for (int i = 0; i < n; i++) if (!nodes[i].done && nodes[i].est < float.MaxValue && (u < 0 || nodes[i].est < nodes[u].est)) u = i;
            if (u < 0 || u == end) break;
            nodes[u].done = true;
            if (nodes[u].moves >= a.moves) continue;
            Moves(u, nodes[u].entry, b, a);
            foreach (Step st in edges)
            {
                if (banned != null && banned(st.key)) continue;
                int v = IndexFromKey(st);
                float c = nodes[u].cost + Ride(Mathf.Abs(st.takeoffX - nodes[u].entry), b, a) + Effort(st, surf[v].y - surf[u].y);
                float est = c + Ride(Mathf.Abs(goal.x - st.landX), b, a);
                if (est < nodes[v].est && !nodes[v].done)
                {
                    nodes[v].cost = c;
                    nodes[v].est = est;
                    nodes[v].entry = st.landX;
                    nodes[v].moves = nodes[u].moves + 1;
                    nodes[v].parent = u;
                    nodes[v].via = st;
                }
            }
        }
        if (nodes[end].est == float.MaxValue) return false;
        cost = nodes[end].cost + Ride(Mathf.Abs(goal.x - nodes[end].entry), b, a);
        moves = nodes[end].moves;
        int at = end;
        while (nodes[at].parent != start) at = nodes[at].parent;
        first = nodes[at].via;
        return true;
    }

    /// <summary>Further than this along a surface, it skates there (if it has a board) instead of hopping.</summary>
    public static float SkateFrom(float size) => 2.4f * size / 0.6f;

    // getting dist along a surface: hopping (cost = distance) or, far enough, on the skateboard (quicker)
    static float Ride(float dist, Body b, Abilities a)
    {
        if (a.skate <= 0f) return dist;
        float m = SkateFrom(b.size);
        return dist <= m ? dist : m + (dist - m) * 0.45f;
    }

    static float Effort(Step st, float dy)
    {
        switch (st.move)
        {
            case Move.Ollie: return 0.9f;
            case Move.Glide: return 1.4f;
            case Move.Zip: return 1.35f; // (a bit better than the paper plane: the newest trick gets used where it can)
            case Move.Float: return 2.3f + 0.1f * Mathf.Max(dy, 0f) + 0.3f * Mathf.Abs(st.landX - st.takeoffX); // slow, but up anywhere
            case Move.Jet: return 2.4f + 0.12f * Mathf.Max(dy, 0f) + 0.25f * Mathf.Abs(st.landX - st.takeoffX); // quick, but it's a gamble
            case Move.Drop: return 0.6f;
            case Move.Hop: return 1f;
            case Move.Leap: return 1.6f;
            case Move.WallJump: return 2.2f;
            case Move.Climb: return 4f + 1.5f * Mathf.Max(dy, 0f);
            case Move.Swing: return 2f;
            case Move.Grapple: return 2.5f + 0.25f * Mathf.Max(dy, 0f);
        }
        return 0f;
    }

    // the step's target surface index is kept in its key's tail ("...#index")
    static int IndexFromKey(Step st) => int.Parse(st.key.Substring(st.key.LastIndexOf('#') + 1));

    static string Name(int i) => surf[i].win != 0 ? "w" + surf[i].win : "f" + Mathf.RoundToInt(surf[i].x0 * 10f);

    static Step Make(Move m, int from, int to, float takeoff, float land, int side)
    {
        return new Step
        {
            move = m, takeoffX = takeoff, landX = land, landY = surf[to].y, side = side,
            key = Name(from) + ">" + Name(to) + ":" + m + "#" + to,
            fromY = surf[from].y, from0 = surf[from].x0, from1 = surf[from].x1,
        };
    }

    /// <summary>Every move it can make from surface u (standing at entryX).</summary>
    static void Moves(int u, float entryX, Body b, Abilities a)
    {
        edges.Clear();
        Rect r = ScreenBounds.WorldRect;
        Surface A = surf[u];
        float hw = b.halfW;
        for (int v = 0; v < surf.Count; v++)
        {
            if (v == u) continue;
            Surface B = surf[v];
            if (B.x1 - B.x0 < hw * 1.6f) continue; // too narrow to stand on
            float dy = B.y - A.y;

            // off an edge, down onto what's right below it
            if (dy < -0.1f && a.drop && A.win != 0)
                for (int side = -1; side <= 1; side += 2)
                {
                    float edgeX = side < 0 ? A.x0 : A.x1;
                    float px = edgeX + side * hw * 1.3f;
                    if (px < r.xMin + hw || px > r.xMax - hw) continue;
                    if (Blocked(px, A.y + 0.05f, b.solid)) continue;
                    if (Below(px, A.y - 0.05f) != v) continue;
                    float take = Mathf.Clamp(edgeX - side * hw * 0.6f, A.x0 + hw * 0.5f, A.x1 - hw * 0.5f);
                    edges.Add(Make(Move.Drop, u, v, take, px, side));
                }

            // up (or across a gap) onto B, from beside one of its ends
            if (dy > -0.1f)
                for (int side = -1; side <= 1; side += 2)
                {
                    // side +1: comes from the left onto B's left end; -1: from the right onto its right end
                    float edgeX = side > 0 ? B.x0 : B.x1;
                    float land = B.x1 - B.x0 < hw * 4f ? 0.5f * (B.x0 + B.x1) : Mathf.Clamp(edgeX + side * hw * 1.5f, B.x0 + hw, B.x1 - hw); // (narrow: the middle)
                    WindowBox wb = default;
                    int face = b.solid || ScreenBounds.Multi ? Face(edgeX, side, B.y, out wb) : 0;
                    if (face < 0) continue; // a taller window stands there: not from this side
                    if (face > 0 && dy > 0.1f)
                    {
                        // a solid window side: jump straight up beside it, then over onto the top (or climb it)
                        bool toFloor = wb.yMin <= A.y + 0.1f; // a wall down to where it stands (else it's floating)
                        float take = edgeX - side * hw * 1.8f;
                        bool roomy = take >= A.x0 + hw * 0.5f && take <= A.x1 - hw * 0.5f;
                        bool touches = side > 0 ? A.x1 >= edgeX - 0.12f : A.x0 <= edgeX + 0.12f;
                        if (!roomy) continue;
                        bool tall = b.y + dy + b.height * 1.6f > ScreenBounds.CeilingAt(new Vector2(edgeX, B.y + 0.01f)); // no room above to jump that high
                        float colTop = B.y + b.height * 1.3f, colBot = A.y + 0.1f;
                        if (!tall && (b.solid || ScreenBounds.Multi) && InBox(new Vector2(take, 0.5f * (colTop + colBot)), hw * 0.7f, 0.5f * (colTop - colBot), b.solid))
                            tall = true; // (a window over where it'd jump up: it'd bonk its head)
                        Move m = Move.Walk;
                        if (!tall && dy <= a.hopUp) m = Move.Hop;
                        else if (!tall && toFloor && dy <= a.wallUp) m = Move.WallJump;
                        else if (!tall && !toFloor && dy <= a.leapUp) m = Move.Leap;
                        else if (toFloor && touches && a.climb) m = Move.Climb;
                        if (m == Move.Walk) continue;
                        Step st = Make(m, u, v, take, land, side);
                        st.wall = true;
                        st.wallX = edgeX;
                        st.box = wb;
                        edges.Add(st);
                    }
                    else
                    {
                        // an arc: B is about level (a gap to hop over) or not solid; take off outside B, land on its end
                        float take = side > 0 ? Mathf.Min(A.x1 - hw * 0.6f, edgeX - hw - 0.25f) : Mathf.Max(A.x0 + hw * 0.6f, edgeX + hw + 0.25f);
                        if (take < A.x0 + hw * 0.5f || take > A.x1 - hw * 0.5f) continue;
                        float dist = Mathf.Abs(land - take);
                        Move m = dy <= a.hopUp && dist <= a.hopReach ? Move.Hop : dy <= a.leapUp && dist <= a.leapReach ? Move.Leap : Move.Walk;
                        if (m == Move.Walk) continue;
                        if (b.y + Mathf.Max(dy, 0f) + b.height * 1.6f > Mathf.Min(ScreenBounds.CeilingAt(new Vector2(take, A.y + 0.01f)), ScreenBounds.CeilingAt(new Vector2(land, B.y + 0.01f)))) continue;
                        if ((b.solid || ScreenBounds.Multi) && ArcBlocked(take, land, A, B, Mathf.Max(dy, 0f) + 0.9f * b.size / 0.6f, hw)) continue; // a window stands in between
                        edges.Add(Make(m, u, v, take, land, side));
                    }
                }
        }
        if (a.skate > 0f) Ollies(u, b, a);
        if (a.glide > 0f) Glides(u, b, a);
        if (a.zip > 0f || a.zipRide) Zips(u, b, a);
        if (a.balloon > 0f) Floats(u, b, a);
        if (a.jet > 0f) Jets(u, b, a);
        if (a.rope > 0f) { RopeEdges(b, a); edges.AddRange(ropeTo[u]); }
    }

    // ------------------------------------------------------------------ balloon: float up onto a higher surface

    /// <summary>How fast it floats up on its balloon (world units / s).</summary>
    public static float FloatRise(float size) => 2.5f * Mathf.Sqrt(Mathf.Max(size, 0.1f) / 0.6f);
    /// <summary>How fast it drifts across up there (world units / s).</summary>
    public static float FloatAcross(float size) => 2f * Mathf.Sqrt(Mathf.Max(size, 0.1f) / 0.6f);
    /// <summary>It floats across this high above the top it's going to (its feet, x size / 0.6), then lets go.</summary>
    public const float FloatClear = 0.35f;
    /// <summary>The balloon above its head: from the top of the body to the top of the balloon (x size).</summary>
    public const float BalloonAbove = 1.85f;

    // from A straight up beside B's near end (from A's end nearest it if A doesn't reach), across above B and down onto it:
    // landing near that end (and in the middle of a wide one), never up through B itself
    static void Floats(int u, Body b, Abilities a)
    {
        Surface A = surf[u];
        Rect r = ScreenBounds.WorldRect;
        float hw = b.halfW, S = b.size / 0.6f;
        if (A.x1 - A.x0 < hw * 1.2f) return;
        for (int v = 0; v < surf.Count; v++)
        {
            if (v == u) continue;
            Surface B = surf[v];
            if (B.y - A.y < 1.2f * S || B.x1 - B.x0 < hw * 2.4f) continue; // (low steps: a hop is quicker)
            float top = B.y + FloatClear * S;
            if (top + b.height + BalloonAbove * b.size * 0.6f > r.yMax) continue; // no room up there for it and its balloon
            for (int side = -1; side <= 1; side += 2)
            {
                // side +1: up past B's left end (x0), across to the right; -1: past its right end
                float edgeX = side > 0 ? B.x0 : B.x1;
                float take = Mathf.Clamp(edgeX - side * (hw * 1.6f + 0.1f), Mathf.Max(A.x0, r.xMin) + hw * 0.6f, Mathf.Min(A.x1, r.xMax) - hw * 0.6f);
                if (take + hw > B.x0 && take - hw < B.x1) continue; // (under B: it'd float into its bottom)
                float wide = B.x1 - B.x0;
                int n = wide > hw * 6f ? 2 : 1;
                for (int k = 0; k < n; k++)
                {
                    float land = k == 1 || wide < hw * 4f ? 0.5f * (B.x0 + B.x1) : Mathf.Clamp(edgeX + side * hw * 1.6f, B.x0 + hw, B.x1 - hw);
                    if (Mathf.Abs(land - take) > a.balloon) continue;
                    if (!FloatPath(b, take, A.y, land, top)) continue;
                    edges.Add(Make(Move.Float, u, v, take, land, side));
                }
            }
        }
    }

    // ------------------------------------------------------------------ jetpack: fly up onto a higher surface

    /// <summary>How fast its jetpack takes it up / across (world units / s), when it works.</summary>
    public static float JetRise(float size) => 4.2f * Mathf.Sqrt(Mathf.Max(size, 0.1f) / 0.6f);
    public static float JetAcross(float size) => 3f * Mathf.Sqrt(Mathf.Max(size, 0.1f) / 0.6f);
    /// <summary>It flies across this high above the top it's going to (its feet, x size / 0.6), then cuts the engine.</summary>
    public const float JetClear = 0.3f;

    // like the balloon: straight up beside B's near end, across above it, down onto it; only as high as the jetpack goes
    static void Jets(int u, Body b, Abilities a)
    {
        Surface A = surf[u];
        Rect r = ScreenBounds.WorldRect;
        float hw = b.halfW, S = b.size / 0.6f;
        if (A.x1 - A.x0 < hw * 1.2f) return;
        for (int v = 0; v < surf.Count; v++)
        {
            if (v == u) continue;
            Surface B = surf[v];
            float dy = B.y - A.y;
            if (dy < 1.2f * S || dy > a.jet || B.x1 - B.x0 < hw * 2.4f) continue; // (low steps: a hop is quicker)
            float top = B.y + JetClear * S;
            if (top + b.height + 0.2f > r.yMax) continue; // no room up there
            for (int side = -1; side <= 1; side += 2)
            {
                float edgeX = side > 0 ? B.x0 : B.x1;
                float take = Mathf.Clamp(edgeX - side * (hw * 1.6f + 0.1f), Mathf.Max(A.x0, r.xMin) + hw * 0.6f, Mathf.Min(A.x1, r.xMax) - hw * 0.6f);
                if (take + hw > B.x0 && take - hw < B.x1) continue; // (under B: it'd fly into its bottom)
                float land = (B.x1 - B.x0) < hw * 4f ? 0.5f * (B.x0 + B.x1) : Mathf.Clamp(edgeX + side * hw * 1.6f, B.x0 + hw, B.x1 - hw);
                if (Mathf.Abs(land - take) > 4f * S) continue;
                if (!FloatPath(b, take, A.y, land, top, 0.15f)) continue;
                edges.Add(Make(Move.Jet, u, v, take, land, side));
            }
        }
    }

    // up from take (feet at fromY) to top, then across to land at that height: no solid window in the way of it or of
    // what's above it (the balloon: BalloonAbove x size)
    static bool FloatPath(Body b, float take, float fromY, float land, float top, float above = BalloonAbove)
    {
        if (!b.solid && !ScreenBounds.Multi) return true;
        float hx = Mathf.Max(b.halfW, 0.36f * b.size) * 0.85f, stack = b.height + above * b.size, hy = stack * 0.5f - 0.05f;
        int nUp = Mathf.Clamp(Mathf.CeilToInt((top - fromY) / 0.3f), 3, 30);
        for (int k = 1; k <= nUp; k++)
        {
            float feet = Mathf.Lerp(fromY + 0.1f, top, k / (float)nUp);
            if (InBox(new Vector2(take, feet + stack * 0.5f), hx, hy, b.solid)) return false;
        }
        int nA = Mathf.Clamp(Mathf.CeilToInt(Mathf.Abs(land - take) / 0.3f), 2, 30);
        for (int k = 1; k <= nA; k++)
            if (InBox(new Vector2(Mathf.Lerp(take, land, k / (float)nA), top + stack * 0.5f), hx, hy, b.solid)) return false;
        return true;
    }

    // ------------------------------------------------------------------ zipline: down a line from a window's top corner

    /// <summary>How far below a zipline a rider's top hangs (the pulley wheel, the laces, the handle it holds).</summary>
    public const float ZipHang = 0.2f;
    /// <summary>The low end of a line hangs this far above the surface it comes down on (so it lands as it gets there).</summary>
    public static float ZipEnd(Body b) => b.height + ZipHang;

    // new lines from A's ends down onto lower surfaces (near / middle / far spots), and lines that are already up there
    static void Zips(int u, Body b, Abilities a)
    {
        Surface A = surf[u];
        float hw = b.halfW, S = b.size / 0.6f;
        if (a.zipRide)
            foreach (var z in Zipline.All)
            {
                if (!z.Ready || Mathf.Abs(z.From.y - A.y) > 0.2f || z.From.x < A.x0 - 0.2f || z.From.x > A.x1 + 0.2f) continue;
                int v = Below(z.To.x, z.To.y - ZipEnd(b) + 0.15f);
                if (v < 0 || v == u) continue;
                int side = z.To.x > z.From.x ? 1 : -1;
                float take = Mathf.Clamp(z.From.x - side * hw * 0.8f, A.x0 + hw * 0.5f, A.x1 - hw * 0.5f);
                Step st = Make(Move.Zip, u, v, take, z.To.x, side);
                st.hook = z.From;
                st.hang = z.To;
                st.rope = -1f; // (a line that's up)
                edges.Add(st);
            }
        if (a.zip <= 0f || A.win == 0 || DesktopPlatforms.IsIcon(A.win)) return; // new lines: tied to a window's top corner
        for (int v = 0; v < surf.Count; v++)
        {
            if (v == u) continue;
            Surface B = surf[v];
            float dy = A.y - B.y;
            if (dy < 1.2f * S || B.x1 - B.x0 < hw * 2.4f) continue;
            for (int side = -1; side <= 1; side += 2)
            {
                float edgeX = side > 0 ? A.x1 : A.x0, take = edgeX - side * hw * 0.8f;
                if (take < A.x0 + hw * 0.5f || take > A.x1 - hw * 0.5f) continue;
                Vector2 from = new Vector2(edgeX, A.y + 0.05f);
                // how far along it can come down: not too steep (it'd just be a drop), not too flat to slide, not too long
                float drop = from.y - (B.y + ZipEnd(b));
                if (drop >= a.zip) continue;
                float dMin = Mathf.Max(drop / 1.5f, hw * 2f), dMax = Mathf.Min(drop / 0.15f, Mathf.Sqrt(a.zip * a.zip - drop * drop));
                if (dMax < dMin) continue;
                float lo = Mathf.Max(B.x0 + hw * 1.3f, side > 0 ? edgeX + dMin : edgeX - dMax), hi = Mathf.Min(B.x1 - hw * 1.3f, side > 0 ? edgeX + dMax : edgeX - dMin);
                if (hi - lo < 0.05f) continue;
                int n = hi - lo > 3f * S ? 3 : 1;
                for (int k = 0; k < n; k++)
                {
                    float land = n == 1 ? 0.5f * (lo + hi) : side > 0 ? Mathf.Lerp(lo, hi, k / 2f) : Mathf.Lerp(hi, lo, k / 2f);
                    Vector2 to = new Vector2(land, B.y + ZipEnd(b));
                    float len = (to - from).magnitude;
                    if (!ZipPath(b, from, to, A.win, B.win, v)) continue;
                    Step st = Make(Move.Zip, u, v, take, land, side);
                    st.hook = from;
                    st.hang = to;
                    st.rope = len;
                    edges.Add(st);
                }
            }
        }
    }

    // the body hanging under the line all the way down: no window in the way (bar the one it's tied to), nothing to land on first
    static bool ZipPath(Body b, Vector2 from, Vector2 to, long fromWin, long toWin, int target)
    {
        float hh = b.height * 0.5f, hang = hh + 0.12f; // its middle, below the line
        var wins = DesktopPlatforms.Windows;
        for (int k = 1; k <= 14; k++)
        {
            Vector2 p = Vector2.Lerp(from, to, k / 14f) + Vector2.down * hang;
            for (int j = 0; j < wins.Count; j++)
            {
                WindowBox w = wins[j];
                if (!w.movable || w.id == fromWin || w.id == toWin) continue; // (tied to the one, comes down on the other)
                if (p.x + b.halfW * 0.8f > w.xMin && p.x - b.halfW * 0.8f < w.xMax && p.y + hh > w.yMin && p.y - hh * 0.8f < w.yMax) return false;
            }
        }
        for (int i = 0; i < surf.Count; i++)
        {
            if (i == target) continue;
            Surface s = surf[i];
            float feetFrom = from.y - b.height - 0.12f, feetTo = to.y - b.height - 0.12f;
            if (s.y >= feetFrom - 0.05f || s.y <= feetTo + 0.05f) continue;
            float x = Mathf.Lerp(from.x, to.x, (feetFrom - s.y) / (feetFrom - feetTo));
            if (x > s.x0 - b.halfW * 0.5f && x < s.x1 + b.halfW * 0.5f) return false;
        }
        return true;
    }

    // ------------------------------------------------------------------ paper plane: glide down from a window top

    /// <summary>How fast it glides along (world units / s).</summary>
    public static float GlideSpeed(float size) => 3.4f * Mathf.Sqrt(Mathf.Max(size, 0.1f) / 0.6f);
    /// <summary>The glide starts with a little hop up off the edge (x size / 0.6).</summary>
    public const float GlideHop = 0.35f;

    // off one end of a window top (A), gliding down onto a lower surface: a few landing spots (near, middle, far) within
    // what it can reach, so the plan can pick the one nearest where it's going
    static void Glides(int u, Body b, Abilities a)
    {
        Surface A = surf[u];
        if (A.win == 0) return; // from window tops (the floor is as low as it gets)
        float hw = b.halfW, S = b.size / 0.6f;
        for (int v = 0; v < surf.Count; v++)
        {
            if (v == u) continue;
            Surface B = surf[v];
            float dy = B.y - A.y;
            if (dy > -1.5f * S || B.x1 - B.x0 < hw * 2.4f) continue;
            for (int side = -1; side <= 1; side += 2)
            {
                float edgeX = side > 0 ? A.x1 : A.x0, take = edgeX - side * hw * 0.7f, clear = edgeX + side * GlideClear(hw);
                if (take < A.x0 + hw * 0.5f || take > A.x1 - hw * 0.5f) continue;
                float drop = -dy, dMin = drop * 0.7f + hw, dMax = drop * a.glide;
                float lo = side > 0 ? clear + dMin : clear - dMax, hi = side > 0 ? clear + dMax : clear - dMin;
                lo = Mathf.Max(lo, B.x0 + hw * 1.2f);
                hi = Mathf.Min(hi, B.x1 - hw * 1.2f);
                if (hi - lo < 0.05f) continue;
                int n = hi - lo > 3f * S ? 3 : 1;
                for (int k = 0; k < n; k++)
                {
                    float land = n == 1 ? 0.5f * (lo + hi) : side > 0 ? Mathf.Lerp(lo, hi, k / 2f) : Mathf.Lerp(hi, lo, k / 2f);
                    if (!GlidePath(b, take, clear, A.y, land, B.y, v)) continue;
                    edges.Add(Make(Move.Glide, u, v, take, land, side));
                }
            }
        }
    }

    /// <summary>It flies level until it's this far past the edge it took off from (then down: it doesn't clip the corner).</summary>
    public static float GlideClear(float halfW) => halfW * 1.4f;

    // the glide from the take-off (a hop above it), level until it's clear of the edge, then straight down to the landing:
    // no window in the way, nothing to land on first
    static bool GlidePath(Body b, float take, float clear, float fromY, float land, float toY, int target)
    {
        Rect r = ScreenBounds.WorldRect;
        Vector2 a0 = new Vector2(take, fromY + b.cOff + GlideHop * b.size / 0.6f), a = new Vector2(clear, a0.y), z = new Vector2(land, toY + b.cOff);
        if (a.y + b.cOff > r.yMax) return false;
        for (int k = 1; k <= 4; k++)
            if (InBox(Vector2.Lerp(a0, a, k / 4f), b.halfW * 0.8f, b.cOff * 0.8f, b.solid)) return false;
        for (int k = 1; k <= 12; k++)
        {
            Vector2 p = Vector2.Lerp(a, z, k / 12f);
            if (InBox(p, b.halfW * 0.8f, b.cOff * 0.8f, b.solid)) return false;
        }
        for (int i = 0; i < surf.Count; i++)
        {
            if (i == target) continue;
            Surface s = surf[i];
            float y = s.y + b.cOff;
            if (y >= a.y - 0.05f || y <= z.y + 0.05f) continue;
            float x = Mathf.Lerp(a.x, z.x, (a.y - y) / (a.y - z.y));
            if (x > s.x0 - b.halfW * 0.5f && x < s.x1 + b.halfW * 0.5f) return false;
        }
        return true;
    }

    /// <summary>Every move it could make from the surface it stands on (no rope moves), e.g. to pick a rail to grind.</summary>
    /// <summary>Every move it could make from the surface it stands on (no rope moves), with the surface each lands on.</summary>
    public static List<KeyValuePair<Step, Surface>> MovesFrom(Body b, Abilities a)
    {
        var list = new List<KeyValuePair<Step, Surface>>();
        Build(b.height, b.solid);
        int u = SurfaceAt(b.x, b.y, b.onWindow);
        if (u < 0) return list;
        a.rope = 0f;
        Moves(u, b.x, b, a);
        foreach (var e in edges) list.Add(new KeyValuePair<Step, Surface>(e, surf[IndexFromKey(e)]));
        return list;
    }

    /// <summary>A glide for fun from where it stands (a window top), at random, preferring long ones. False if none.</summary>
    public static bool FunGlide(Body b, Abilities a, Func<string, bool> banned, out Step st)
    {
        st = default;
        if (a.glide <= 0f) return false;
        Build(b.height, b.solid);
        int u = SurfaceAt(b.x, b.y, b.onWindow);
        if (u < 0 || surf[u].win == 0) return false;
        edges.Clear();
        Glides(u, b, a);
        float total = 0f;
        foreach (var e in edges)
        {
            if (banned != null && banned(e.key)) continue;
            float w = Mathf.Abs(e.landX - e.takeoffX) + 1f; // longer glides more likely
            total += w;
            if (UnityEngine.Random.value * total < w) st = e;
        }
        return total > 0f;
    }

    // ------------------------------------------------------------------ skateboard: ollie off the end of a surface

    /// <summary>How high an ollie pops above the higher of take-off and landing (x size / 0.6).</summary>
    public const float OlliePop = 0.6f;

    /// <summary>
    /// The launch for an ollie that comes down dx further along and dy higher (feet), with its gravity and air drag.
    /// False if it would need to go faster than maxSpeed.
    /// </summary>
    public static bool OllieLaunch(Body b, float dx, float dy, float maxSpeed, out Vector2 v0)
    {
        float g = b.g, lam = Mathf.Max(b.drag, 1e-3f);
        float vy = JumpSpeed(Mathf.Max(dy, 0f) + OlliePop * b.size / 0.6f, g, lam);
        float tTop = Mathf.Log(1f + vy * lam / g) / lam;
        float tl = FallTo(vy, dy, tTop, g, lam);
        float vx = dx * lam / (1f - Mathf.Exp(-lam * tl));
        v0 = new Vector2(vx, vy);
        return Mathf.Abs(vx) <= maxSpeed;
    }

    // off one end of A at full speed, over a gap onto B (level or lower, a little higher at most)
    static void Ollies(int u, Body b, Abilities a)
    {
        Surface A = surf[u];
        float hw = b.halfW, S = b.size / 0.6f, top = ScreenBounds.WorldRect.yMax;
        if (A.x1 - A.x0 < b.size * 3f) return; // no room for a run-up
        for (int v = 0; v < surf.Count; v++)
        {
            if (v == u) continue;
            Surface B = surf[v];
            if (B.x1 - B.x0 < hw * 2.4f) continue;
            float dy = B.y - A.y;
            if (dy > 0.8f * S) continue;
            if (A.y + Mathf.Max(dy, 0f) + OlliePop * S + b.height + 0.25f * S > top) continue; // it'd bump its head on the top of the screen
            for (int side = -1; side <= 1; side += 2)
            {
                float edgeX = side > 0 ? A.x1 : A.x0, near = side > 0 ? B.x0 : B.x1;
                float gap = (near - edgeX) * side;
                if (gap < 0.3f) continue; // right below the edge (hop down / drop) or overlapping
                float take = edgeX - side * hw * 0.7f;
                float land = B.x1 - B.x0 < hw * 5f ? 0.5f * (B.x0 + B.x1) : near + side * hw * 2f; // (narrow: the middle)
                if (!OllieLaunch(b, land - take, dy, a.skate * 1.15f, out Vector2 v0)) continue;
                if ((b.solid || ScreenBounds.Multi) && Face(near, side, B.y, out _) < 0) continue; // a taller window stands at that end
                float zone0 = Mathf.Max(B.x0 + hw * 0.4f, land - hw * 2f), zone1 = Mathf.Min(B.x1 - hw * 0.4f, land + hw * 2f);
                if (!Flight(new Vector2(take, A.y + b.cOff), v0, b, B.y, zone0, zone1, out _)) continue;
                edges.Add(Make(Move.Ollie, u, v, take, land, side));
            }
        }
    }

    // a solid window side at x facing the way it comes from: 1 = the window whose top B is (box), -1 = a taller one is in
    // the way, 0 = none (open: an arc is fine)
    static int Face(float x, int side, float topY, out WindowBox box)
    {
        box = default;
        int found = 0;
        var wins = DesktopPlatforms.Windows;
        for (int k = 0; k < wins.Count; k++)
        {
            WindowBox w = wins[k];
            if (!Solid(w) || Mathf.Abs((side > 0 ? w.xMin : w.xMax) - x) > 0.08f || w.yMin > topY) continue;
            if (w.yMax > topY + 0.12f) return -1;
            box = w;
            found = 1;
        }
        return found;
    }

    // a solid window (not the one it starts on or lands on) stands between take-off and landing, up into the arc
    // (e.g. a small window standing on the taskbar between two bits of floor)
    static bool ArcBlocked(float take, float land, Surface A, Surface B, float rise, float hw)
    {
        float x0 = Mathf.Min(take, land) + hw * 0.3f, x1 = Mathf.Max(take, land) - hw * 0.3f;
        float y0 = Mathf.Min(A.y, B.y) + 0.05f, y1 = Mathf.Max(A.y, B.y) + rise;
        var wins = DesktopPlatforms.Windows;
        for (int k = 0; k < wins.Count; k++)
        {
            WindowBox w = wins[k];
            if (!Solid(w) || w.id == A.win || w.id == B.win) continue;
            if (w.xMax > x0 && w.xMin < x1 && w.yMax > y0 && w.yMin < y1) return true;
        }
        return false;
    }

    // a solid window right at this spot and height (it can't drop through there)
    static bool Blocked(float x, float y, bool solid)
    {
        if (!solid && !ScreenBounds.Multi) return false;
        var wins = DesktopPlatforms.Windows;
        for (int k = 0; k < wins.Count; k++)
        {
            WindowBox w = wins[k];
            if (w.movable && (solid || w.screen) && x > w.xMin && x < w.xMax && y > w.yMin && y < w.yMax) return true;
        }
        return false;
    }

    // the highest surface under x below height y
    static int Below(float x, float y)
    {
        int best = -1;
        for (int i = 0; i < surf.Count; i++)
        {
            Surface s = surf[i];
            if (x < s.x0 || x > s.x1 || s.y > y) continue;
            if (best < 0 || s.y > surf[best].y) best = i;
        }
        return best;
    }

    // ------------------------------------------------------------------ rope moves (worked out once per window layout)

    struct Bar { public float x0, x1, y; }
    static readonly List<Bar> bars = new List<Bar>();
    // one set per buddy size / rope length (they plan in turns): the moves to other surfaces, and swings back onto the same one
    class RopeSet { public int sig, seen, used, want, nextU = -1; public float changed; public List<Step>[] to, fun, nextTo; public bool[] funDone; }
    static readonly List<RopeSet> ropeSets = new List<RopeSet>();
    static List<Step>[] ropeTo = new List<Step>[0], ropeFun = new List<Step>[0];
    static int ropeClock;
    const float ThrowTime = 0.15f;   // the rope flies this long before it catches (BuddyRope does the same)
    /// <summary>Swinging through the bottom it kicks its legs once (like on a playground swing): this much speed (world units / s).</summary>
    public const float Pump = 3f;

    /// <summary>
    /// A swing or rope climb for fun from where it stands (to anywhere, also back onto the same surface), one with its
    /// take-off within reach. False if there's none.
    /// </summary>
    public static bool FunSwing(Body b, Abilities a, Func<string, bool> banned, out Step st)
    {
        st = default;
        if (a.rope <= 0f) return false;
        Build(b.height, b.solid);
        int u = SurfaceAt(b.x, b.y, b.onWindow);
        if (u < 0) return false;
        RopeEdges(b, a);
        if (ropeSet == null || ropeSet.funDone.Length != surf.Count) return false;
        if (!ropeSet.funDone[u])
        {
            // swings back onto the same surface: only worked out when it feels like one
            ropeSet.funDone[u] = true;
            Body rb = RopeBody(b);
            Bars(rb);
            for (int side = -1; side <= 1; side += 2) TrySwing(u, u, side, rb, a);
        }
        int count = 0;
        foreach (var list in new[] { ropeFun[u], ropeTo[u] })
            foreach (var e in list)
            {
                if (Mathf.Abs(e.takeoffX - b.x) > 5f || (banned != null && banned(e.key))) continue;
                count++;
                if (UnityEngine.Random.Range(0, count) == 0) st = e; // one of them, at random
            }
        return count > 0;
    }

    static RopeSet ropeSet;
    static float lastWorkedOut = -1f;
    const float Settle = 0.4f; // windows being dragged: works the rope moves out again once they've stopped for this long

    // planned with its size (not its squash of the moment, so the plan stays put while it breathes and hops)
    static Body RopeBody(Body b)
    {
        if (b.size > 0f) { b.halfW = 0.55f * b.size; b.height = 1.1f * b.size; b.cOff = 0.55f * b.size; }
        return b;
    }

    // the rope moves from every surface (cached until the windows, its size or its rope change)
    static void RopeEdges(Body b, Abilities a)
    {
        b = RopeBody(b);
        int sig = 17;
        foreach (var sf in surf) sig = sig * 31 + Mathf.RoundToInt(sf.x0 * 10f) * 7 + Mathf.RoundToInt(sf.x1 * 10f) * 13 + Mathf.RoundToInt(sf.y * 10f);
        sig = sig * 31 + LayoutHash();
        sig = sig * 31 + Mathf.RoundToInt(b.size * 50f) * 3 + Mathf.RoundToInt(a.rope * 10f) * 7 + Mathf.RoundToInt(b.g) * 11 + (b.solid ? 1 : 0);
        // one set per buddy (its size and rope): the same windows = the same moves
        int who = Mathf.RoundToInt(b.size * 50f) * 7 + Mathf.RoundToInt(a.rope * 10f) * 13 + (b.solid ? 1 : 0);
        float now = Time.unscaledTime;
        ropeClock++;
        RopeSet mine = null;
        foreach (var set in ropeSets) if (set.want == who) { mine = set; break; }
        if (mine == null)
        {
            if (ropeSets.Count < 4) ropeSets.Add(mine = new RopeSet());
            else foreach (var set in ropeSets) if (mine == null || set.used < mine.used) mine = set; // the one not used for longest
            mine.want = who;
            mine.sig = mine.seen = 0;
            mine.to = null;
            mine.nextU = -1;
        }
        mine.used = ropeClock;
        ropeSet = mine;
        int n = surf.Count;
        if (mine.to == null || mine.to.Length != n)
        {
            // new surfaces (the first time, or a window opened or closed): worked out right away
            mine.to = NewLists(n);
            mine.fun = NewLists(n);
            mine.funDone = new bool[n];
            mine.sig = 0;
            mine.nextU = -1;
        }
        Serve(mine);
        if (mine.sig == sig) return;
        bool first = mine.sig == 0;
        if (!first)
        {
            // the windows moved (you're dragging one): keeps what it had until they've been still for a moment
            if (sig != mine.seen) { mine.seen = sig; mine.changed = now; mine.nextU = -1; }
            if (now - mine.changed < Settle || now == lastWorkedOut) return; // (one buddy's share of work per frame)
            lastWorkedOut = now;
        }
        else mine.nextU = -1;
        // works the new moves out a couple of surfaces per frame, then swaps them in
        if (mine.nextU < 0)
        {
            if (mine.nextTo == null || mine.nextTo.Length != n) mine.nextTo = NewLists(n);
            for (int i = 0; i < n; i++) mine.nextTo[i].Clear();
            mine.nextU = 0;
        }
        Bars(b);
        ropeTo = mine.nextTo;
        int last = first ? n : Mathf.Min(n, mine.nextU + SurfacesPerFrame);
        for (int u = mine.nextU; u < last; u++)
            for (int v = 0; v < n; v++)
            {
                Surface B = surf[v];
                if (v == u || B.x1 - B.x0 < b.halfW * 2.4f) continue; // (swings back onto the same surface: FunSwing)
                if (B.win != 0) TryGrapple(u, v, b, a);
                for (int side = -1; side <= 1; side += 2) TrySwing(u, v, side, b, a);
            }
        mine.nextU = last;
        if (last >= n)
        {
            var t = mine.to; mine.to = mine.nextTo; mine.nextTo = t;
            for (int i = 0; i < n; i++) { mine.fun[i].Clear(); mine.funDone[i] = false; }
            mine.sig = sig;
            mine.nextU = -1;
        }
        Serve(mine);
    }

    const int SurfacesPerFrame = 2;

    static void Serve(RopeSet set) { ropeTo = set.to; ropeFun = set.fun; }

    static List<Step>[] NewLists(int n)
    {
        var l = new List<Step>[n];
        for (int i = 0; i < n; i++) l[i] = new List<Step>();
        return l;
    }

    // what a rope can hang from: the bottom edge of a window up in the air, the top of the screen, and desktop icons
    static void Bars(Body b)
    {
        Rect r = ScreenBounds.WorldRect;
        bars.Clear();
        var wins = DesktopPlatforms.Windows;
        for (int k = 0; k < wins.Count; k++)
        {
            WindowBox w = wins[k];
            if (w.yMin > r.yMin + b.height * 1.5f && w.xMax - w.xMin > 0.3f) bars.Add(new Bar { x0 = w.xMin + 0.08f, x1 = w.xMax - 0.08f, y = w.yMin });
        }
        bars.Add(new Bar { x0 = r.xMin + 0.15f, x1 = r.xMax - 0.15f, y = r.yMax });
        // an icon: the rope loops round it (a hook at one spot)
        var icons = DesktopPlatforms.Icons;
        for (int k = 0, n = 0; k < icons.Count && n < MaxIconHooks; k++)
        {
            Vector2 c = icons[k].Center;
            if (c.y < r.yMin + b.height * 1.5f) continue;
            bars.Add(new Bar { x0 = c.x, x1 = c.x, y = c.y });
            n++;
        }
    }

    const int MaxIconHooks = 24;

    // up a window's side on a rope hooked over its top corner, then over onto the top
    static void TryGrapple(int u, int v, Body b, Abilities a)
    {
        Surface A = surf[u], B = surf[v];
        float dy = B.y - A.y;
        if (dy < b.height * 1.2f) return; // a hop does that
        WindowBox w = default;
        bool found = false;
        var wins = DesktopPlatforms.Windows;
        for (int k = 0; k < wins.Count; k++) if (wins[k].id == B.win) { w = wins[k]; found = true; break; }
        if (!found || B.y + b.height * 1.3f > ScreenBounds.WorldRect.yMax) return;
        float hw = b.halfW;
        for (int side = -1; side <= 1; side += 2)
        {
            // side +1: comes from the left (the window is to its right): hooks the left corner
            float cornerX = side > 0 ? w.xMin : w.xMax;
            if (Mathf.Abs((side > 0 ? B.x0 : B.x1) - cornerX) > 0.1f) continue; // that end of the top is covered
            float take = cornerX - side * (hw + 0.12f);
            if (take < A.x0 + hw * 0.5f || take > A.x1 - hw * 0.5f) continue;
            // hangs just outside the side (the drawn rope goes to the corner)
            Vector2 hook = new Vector2(cornerX, B.y), hang = new Vector2(cornerX - side * (hw + 0.04f), B.y);
            // a jump, and the rope catches at the top of it: close enough?
            float up = Mathf.Clamp(hang.y - (A.y + b.cOff) - a.rope + 0.3f, 0.3f, 1.6f);
            float vy = JumpSpeed(up, b.g, b.drag);
            float tTop = Mathf.Log(1f + vy * Mathf.Max(b.drag, 1e-3f) / b.g) / Mathf.Max(b.drag, 1e-3f);
            float cy = A.y + b.cOff + Rise(vy, tTop, b.g, Mathf.Max(b.drag, 1e-3f));
            float len = hang.y - cy;
            if (len > a.rope || len < b.cOff) continue;
            if (SegmentBlocked(new Vector2(take, cy), hook - new Vector2(side * 0.02f, 0.02f), b.solid)) continue;
            float land = Mathf.Clamp(cornerX + side * hw * 2f, B.x0 + hw, B.x1 - hw);
            Step st = Make(Move.Grapple, u, v, take, land, side);
            st.hook = hook; st.hang = hang; st.launch = new Vector2(0f, vy); st.rope = st.caught = len; st.wait = Mathf.Max(tTop, ThrowTime);
            st.zone0 = B.x0 + hw * 0.6f; st.zone1 = B.x1 - hw * 0.6f;
            st.key = st.key.Replace(":Grapple#", ":Grapple" + side + "#");
            ropeTo[u].Add(st);
        }
    }

    // a swing: from A, jump, the rope catches on a bar, swing, let go, land on B (or back on A, for fun)
    static void TrySwing(int u, int v, int side, Body b, Abilities a)
    {
        Surface A = surf[u], B = surf[v];
        float hw = b.halfW, w = hw * 2f;
        float lo = Mathf.Min(A.x0, B.x0) - a.rope, hi = Mathf.Max(A.x1, B.x1) + a.rope;
        if (v != u && (side > 0 ? B.x1 < A.x0 + w : B.x0 > A.x1 - w)) return; // B isn't that way
        if (v != u && (side > 0 ? B.x0 - A.x1 : A.x0 - B.x1) > a.rope * 2f + 3f) return; // too far
        Step best = default;
        float bestErr = float.MaxValue;
        foreach (var bar in bars)
        {
            if (bar.x1 < lo || bar.x0 > hi) continue;
            if (bar.y - Mathf.Max(A.y, B.y) < b.height + 0.4f) continue; // no room to hang under it
            if (bar.y - (A.y + b.cOff) > a.rope + 1.4f) continue; // out of reach
            // where to hook: over the gap, or near either end
            float edgeA = side > 0 ? A.x1 : A.x0, edgeB = side > 0 ? B.x0 : B.x1;
            float mid = v == u ? edgeA - side * a.rope * 0.5f : 0.5f * (edgeA + edgeB);
            for (int c = 0; c < 4; c++)
            {
                float px = c == 0 ? mid : c == 1 ? edgeA + side * w * 0.5f : c == 2 ? edgeA - side * w : edgeB - side * w * 0.5f;
                px = Mathf.Clamp(px, bar.x0, bar.x1);
                if (c > 0 && Mathf.Abs(px - Mathf.Clamp(mid, bar.x0, bar.x1)) < 0.2f) continue; // (same spot)
                Vector2 hook = new Vector2(px, bar.y);
                for (int k = 0; k < 3; k++)
                {
                    float take = px - side * w * (1.3f + 1.4f * k);
                    if (take < A.x0 + hw * 0.6f || take > A.x1 - hw * 0.6f) continue;
                    for (int j = 0; j < 2; j++)
                    {
                        Vector2 launch = new Vector2(side * (j == 0 ? 1.2f : 2.4f), JumpSpeed(j == 0 ? 0.5f * w : 1.3f * w, b.g, b.drag));
                        float ideal = v == u ? take + side * 3f * w : Mathf.Clamp(edgeB + side * w * 2f, B.x0 + w, B.x1 - w);
                        if (!SimSwing(b, A, B, hook, take, launch, a.rope, side, ideal, out float landX, out float len, out float z0, out float z1, out float caught)) continue;
                        float err = Mathf.Abs(landX - ideal) + 0.3f * k + 0.2f * j;
                        if (v == u && Mathf.Abs(landX - take) < 1.2f * w) continue; // a swing that goes nowhere
                        if (err >= bestErr) continue;
                        if (err < 0.5f) { bestErr = err; best = MakeSwing(u, v, take, landX, side, hook, launch, len, z0, z1, caught); goto done; } // good enough
                        bestErr = err;
                        best = MakeSwing(u, v, take, landX, side, hook, launch, len, z0, z1, caught);
                    }
                }
            }
        }
        done:
        if (bestErr < float.MaxValue) (v == u ? ropeFun[u] : ropeTo[u]).Add(best);
    }

    static Step MakeSwing(int u, int v, float take, float landX, int side, Vector2 hook, Vector2 launch, float len, float z0, float z1, float caught)
    {
        Step st = Make(Move.Swing, u, v, take, landX, side);
        st.hook = st.hang = hook; st.launch = launch; st.rope = len; st.caught = caught; st.zone0 = z0; st.zone1 = z1; st.wait = ThrowTime;
        st.key = st.key.Replace(":Swing#", ":Swing" + side + "#");
        return st;
    }

    // the swing, step by step (the same physics as the body: gravity, its air drag, the rope holding its middle)
    // len = the rope it pulls itself up to right after the rope catches (= where it caught, unless it pulls)
    static bool SimSwing(Body b, Surface A, Surface B, Vector2 hook, float take, Vector2 launch, float ropeMax, int side, float ideal,
        out float landX, out float len, out float zone0, out float zone1, out float caught)
    {
        landX = 0f; len = 0f; caught = 0f;
        float hw = b.halfW;
        zone0 = B.x0 + hw * 0.6f; zone1 = B.x1 - hw * 0.6f;
        const float h = 1f / 120f;
        float keep = Mathf.Exp(-b.drag * h);
        Vector2 c = new Vector2(take, A.y + b.cOff), vel = launch;
        for (float t = 0f; t < ThrowTime; t += h) { vel *= keep; vel.y -= b.g * h; c += vel * h; }
        caught = (c - hook).magnitude;
        if (caught > ropeMax || caught < b.cOff + 0.25f || c.y > hook.y - 0.2f) return false;
        if (SegmentBlocked(c, hook, b.solid)) return false;
        for (int pull = 0; pull < 2; pull++)
        {
            len = pull == 0 ? caught : Mathf.Max(b.cOff + 0.3f, caught * 0.65f); // (pulls itself up a third of the way: off the floor)
            if (Ahead(b, c, vel, hook, caught, len, side, false, B.y, zone0, zone1, ideal, out _, out landX, 1f / 60f)) return true;
        }
        return false;
    }

    /// <summary>How fast it pulls itself up its rope (world units / s): the same when it swings and when it plans.</summary>
    public static float ReelSpeed(float size) => 3f * Mathf.Sqrt(Mathf.Max(size, 0.1f) / 0.6f);

    /// <summary>
    /// Swinging now (middle at c, moving at vel, on a rope len long from hook, kicked already or not): when to let go to
    /// come down on the surface at height y between zone0 and zone1 as close to ideal as it gets. when = seconds from now
    /// (0 = now), x = where its middle lands. False if no moment on this swing works.
    /// </summary>
    public static bool BestRelease(Body b, Vector2 c, Vector2 vel, Vector2 hook, float len, float reelTo, int side, bool pumped, float y, float zone0, float zone1,
        float ideal, out float when, out float x)
    {
        Build(b.height, b.solid);
        return Ahead(b, c, vel, hook, len, Mathf.Min(reelTo, len), side, pumped, y, zone0, zone1, ideal, out when, out x, 1f / 120f);
    }

    const float MaxFall = 3f; // lets go only where it lands without a long fall (a drop does that better)

    static bool Ahead(Body b, Vector2 c, Vector2 vel, Vector2 hook, float len, float reelTo, int side, bool pumped, float y, float zone0, float zone1,
        float ideal, out float when, out float landX, float h)
    {
        when = 0f; landX = 0f;
        float keep = Mathf.Exp(-b.drag * h), hw = b.halfW, reel = ReelSpeed(b.size) * h;
        int steps = Mathf.RoundToInt(2.5f / h), every = 2; // (looks at letting go every other step: 1/60 s live, 1/30 s planning)
        Rect r = ScreenBounds.WorldRect;
        float best = float.MaxValue;
        bool forward = false;
        for (int i = 0; i < steps; i++) // 2.5 s
        {
            // let go here?
            if (vel.x * side > 0.5f && i % every == 0 && Flight(c, vel, b, y, zone0, zone1, out float x) && Peak(c, vel, b) - y <= MaxFall)
            {
                float err = Mathf.Abs(x - ideal);
                if (err < best - 0.01f) { best = err; landX = x; when = i * h; }
            }
            // one step the way the body does it (position-based: move, hold it to the rope, speed = how far it went)
            Vector2 prev = c;
            vel *= keep;
            vel.y -= b.g * h;
            Vector2 p = c + vel * h;
            if (len > reelTo) len = Mathf.Max(reelTo, len - reel); // pulling itself up the rope
            Vector2 d = p - hook;
            float dist = d.magnitude;
            if (dist > len) p = hook + d * (len / dist);
            vel = (p - c) / h;
            c = p;
            if (!pumped && (prev.x - hook.x) * side < 0f && (c.x - hook.x) * side >= 0f && vel.x * side > 0f) { pumped = true; vel += vel.normalized * Pump; } // the kick
            if (vel.x * side > 0.5f) forward = true;
            else if (forward && vel.x * side < -0.2f) break; // swung all the way over and back: that was its chance
            if (c.x < r.xMin + hw || c.x > r.xMax - hw || c.y - b.cOff < r.yMin + 0.02f) break;
            if (HitsSurface(prev, c, b.cOff, hw * 0.6f) || InBox(c, hw * 0.85f, b.cOff * 0.85f, b.solid)) break;
        }
        return best < float.MaxValue;
    }

    // highest its feet get after letting go here
    static float Peak(Vector2 c, Vector2 vel, Body b)
    {
        float lam = Mathf.Max(b.drag, 1e-3f), tTop = vel.y > 0f ? Mathf.Log(1f + vel.y * lam / b.g) / lam : 0f;
        return c.y - b.cOff + Rise(vel.y, tTop, b.g, lam);
    }

    /// <summary>
    /// Let go now (middle at c, moving at vel): does it come down on the surface at height y, between zone0 and zone1,
    /// without hitting a window or landing on something else first? x = where its middle lands.
    /// </summary>
    public static bool Flight(Vector2 c, Vector2 vel, Body b, float y, float zone0, float zone1, out float x)
    {
        x = 0f;
        float g = b.g, lam = Mathf.Max(b.drag, 1e-3f);
        float y0 = c.y - b.cOff; // feet
        float tTop = vel.y > 0f ? Mathf.Log(1f + vel.y * lam / g) / lam : 0f;
        if (y0 + Rise(vel.y, tTop, g, lam) < y + 0.02f) return false; // never gets up there
        if (y0 < y - 0.05f && vel.y <= 0f) return false;
        // quick look without air drag first (it only shortens the flight a little): nowhere near = no
        float disc = vel.y * vel.y + 2f * g * (y0 - y);
        if (disc >= 0f)
        {
            float tq = (vel.y + Mathf.Sqrt(disc)) / g, xq = c.x + vel.x * tq * Mathf.Max(0.3f, 1f - 0.5f * lam * tq);
            if (xq < zone0 - 1f || xq > zone1 + 1f) return false;
        }
        float tl = FallTo(vel.y, y - y0, tTop, g, lam);
        x = c.x + vel.x * (1f - Mathf.Exp(-lam * tl)) / lam;
        if (x < zone0 || x > zone1) return false;
        // on the way: windows in the way, or something else it would land on first
        Rect r = ScreenBounds.WorldRect;
        for (int k = 1; k <= 8; k++)
        {
            float t = tl * k / 9f;
            Vector2 p = new Vector2(c.x + vel.x * (1f - Mathf.Exp(-lam * t)) / lam, c.y + Rise(vel.y, t, g, lam));
            if (p.x < r.xMin + b.halfW * 0.5f || p.x > r.xMax - b.halfW * 0.5f || p.y > r.yMax - b.cOff * 0.5f) return false;
            if (InBox(p, b.halfW * 0.8f, b.cOff * 0.8f, b.solid)) return false;
        }
        foreach (var s in surf)
        {
            if (s.y <= y + 0.05f || s.y > y0 + Rise(vel.y, tTop, g, lam)) continue;
            float ts = FallTo(vel.y, s.y - y0, tTop, g, lam);
            if (ts >= tl) continue;
            float xs = c.x + vel.x * (1f - Mathf.Exp(-lam * ts)) / lam;
            if (xs > s.x0 - b.halfW * 0.3f && xs < s.x1 + b.halfW * 0.3f) return false;
        }
        return true;
    }

    static float Rise(float vy, float t, float g, float lam) => (vy + g / lam) * (1f - Mathf.Exp(-lam * t)) / lam - g * t / lam;

    // time on the way down (after tTop) when it's dy above where it started
    static float FallTo(float vy, float dy, float tTop, float g, float lam)
    {
        float lo = tTop, hi = tTop + 4f;
        for (int i = 0; i < 16; i++)
        {
            float mid = 0.5f * (lo + hi);
            if (Rise(vy, mid, g, lam) > dy) lo = mid; else hi = mid;
        }
        return hi;
    }

    /// <summary>Launch speed that peaks h higher (with drag).</summary>
    static float JumpSpeed(float h, float g, float lam) => GummyBody.JumpSpeed(Mathf.Max(h, 0.01f), g, Mathf.Max(lam, 1e-3f));

    // its feet went down through the top of a surface between prev and c
    static bool HitsSurface(Vector2 prev, Vector2 c, float cOff, float halfW)
    {
        float fy0 = prev.y - cOff, fy1 = c.y - cOff;
        foreach (var s in surf)
            if (fy0 >= s.y - 0.01f && fy1 < s.y && c.x > s.x0 - halfW && c.x < s.x1 + halfW) return true;
        return false;
    }

    // its body (middle c, half sizes) inside a solid window
    /// <summary>A box (middle c, half sizes hx, hy) overlaps a solid window (solid = windows' sides and bottoms block it).</summary>
    public static bool Blocked(Vector2 c, float hx, float hy, bool solid) => InBox(c, hx, hy, solid);

    /// <summary>The highest surface at x no higher than y (an index into Surfaces after Build / SurfaceUnder), or -1.</summary>
    public static int SurfaceBelow(float x, float y) => Below(x, y);

    static bool InBox(Vector2 c, float hx, float hy, bool solid)
    {
        if (!solid && !ScreenBounds.Multi) return false;
        var wins = DesktopPlatforms.Windows;
        for (int k = 0; k < wins.Count; k++)
        {
            WindowBox w = wins[k];
            if (w.movable && (solid || w.screen) && c.x + hx > w.xMin && c.x - hx < w.xMax && c.y + hy > w.yMin && c.y - hy < w.yMax) return true;
        }
        return false;
    }

    // a rope from a to b would go through a solid window
    static bool SegmentBlocked(Vector2 a, Vector2 b, bool solid)
    {
        if (!solid && !ScreenBounds.Multi) return false;
        for (int k = 1; k < 12; k++)
        {
            Vector2 p = Vector2.Lerp(a, b, k / 12f);
            var wins = DesktopPlatforms.Windows;
            for (int j = 0; j < wins.Count; j++)
            {
                WindowBox w = wins[j];
                if (w.movable && (solid || w.screen) && p.x > w.xMin + 0.02f && p.x < w.xMax - 0.02f && p.y > w.yMin + 0.02f && p.y < w.yMax - 0.02f) return true;
            }
        }
        return false;
    }

    // ------------------------------------------------------------------ falling snacks

    /// <summary>Where a falling snack will land (same physics as Snack), within a few seconds. False if it won't (soon).</summary>
    public static bool Landing(Vector2 pos, Vector2 vel, float radius, float gravity, out Vector2 at, out float surfaceY)
    {
        at = pos;
        surfaceY = 0f;
        Build(0.5f, true);
        Rect r = ScreenBounds.WorldRect;
        const float h = 1f / 60f;
        for (int i = 0; i < 240; i++)
        {
            Vector2 prev = pos;
            vel.y -= gravity * h;
            vel *= 1f - 0.2f * h;
            pos += vel * h;
            if (pos.x < r.xMin + radius) { pos.x = r.xMin + radius; vel.x = -vel.x * 0.35f; }
            if (pos.x > r.xMax - radius) { pos.x = r.xMax - radius; vel.x = -vel.x * 0.35f; }
            for (int k = 0; k < surf.Count; k++)
            {
                Surface s = surf[k];
                if (prev.y - radius >= s.y - 0.01f && pos.y - radius < s.y && pos.x >= s.x0 && pos.x <= s.x1)
                {
                    at = new Vector2(pos.x, s.y + radius);
                    surfaceY = s.y;
                    return true;
                }
            }
        }
        return false;
    }
}
