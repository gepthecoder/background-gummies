using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A basketball hoop (chat "basketball" / "hoops", or WhipIt > Buddy > Put Up The Hoop): the backboard clips onto the
/// top corner of one of your windows, the rim sticking out over the side, and a Basketball comes with it. Move or
/// minimize that window and the hoop falls off; it lands on whatever's below and stands there on its pole (lower). Grab
/// it and drag it: let go near a window's top corner and it clips on there. One hoop at a time; drawn in code. The ball
/// does the bouncing off the rim and the board (Basketball), the buddies do the playing (BuddyHoops, PickupGame).
/// </summary>
[DisallowMultipleComponent]
public class Hoop : MonoBehaviour, IGrabTarget
{
    public enum Mount { Window, Standing, Falling, Held }

    /// <summary>The hoop that's up (null = none).</summary>
    public static Hoop Current { get; private set; }

    /// <summary>Clipped on a window, standing on its pole, falling, or in your hand.</summary>
    public Mount State { get; private set; }
    /// <summary>The way the rim sticks out from the board: +1 = to the right.</summary>
    public int Side { get; private set; } = 1;
    /// <summary>The window it's clipped onto (0 = none).</summary>
    public long Window { get; private set; }
    /// <summary>Where it stands: the window's top corner, or the foot of its pole.</summary>
    public Vector2 Foot => foot;

    // the rim: its middle, how far it is across inside (half), how high above its foot
    public const float RimHalf = 0.27f, RimOnWindow = 0.42f, RimStanding = 1.2f, RimTube = 0.025f;
    const float BoardOff = 0.04f, BoardThick = 0.06f, BoardHeight = 0.78f, BoardBelowRim = 0.15f;

    public bool Hovered { get; set; }
    public bool Held => State == Mount.Held;
    /// <summary>Up and in one place (not falling or in your hand): the ball bounces off it and goes through it.</summary>
    public bool Playable => State == Mount.Window || State == Mount.Standing;

    float RimAbove => State == Mount.Window || (State == Mount.Held && wasWindow) ? RimOnWindow : RimStanding;
    /// <summary>The middle of the rim.</summary>
    public Vector2 Rim => new Vector2(foot.x + Side * (BoardOff + BoardThick + RimTube + RimHalf), foot.y + RimAbove);
    /// <summary>The two ends of the rim (the ball bounces off these): near the board and out at the front.</summary>
    public Vector2 RimBack => new Vector2(foot.x + Side * (BoardOff + BoardThick + RimTube * 0.5f), foot.y + RimAbove);
    public Vector2 RimFront => new Vector2(Rim.x + Side * (RimHalf + RimTube * 0.5f), foot.y + RimAbove);
    /// <summary>The backboard (the ball bounces off it).</summary>
    public Rect Board
    {
        get
        {
            float x0 = foot.x + Side * BoardOff, x1 = foot.x + Side * (BoardOff + BoardThick);
            float y0 = foot.y + RimAbove - BoardBelowRim;
            return Rect.MinMaxRect(Mathf.Min(x0, x1), y0, Mathf.Max(x0, x1), y0 + BoardHeight);
        }
    }
    /// <summary>Its draw order layer for the windows (Occlusion): the window it's on, or what it stands on.</summary>
    public int Layer => State == Mount.Held || State == Mount.Falling ? Occlusion.Front : Window != 0 && Occlusion.Exists(Window) ? Occlusion.LayerOf(Window) : standOn != 0 && Occlusion.Exists(standOn) ? Occlusion.LayerOf(standOn) : Occlusion.Back;
    /// <summary>Hidden behind a window in front of it.</summary>
    public bool Hidden { get; private set; }
    /// <summary>Put up a moment ago (a game starts if there are two to play).</summary>
    public bool Fresh => Time.time - putAt > 2.5f && Time.time - putAt < 8f;
    /// <summary>The net swishes (a basket).</summary>
    public void Swish(float k = 1f) { swish = Mathf.Max(swish, k); }

    Vector2 foot, grabOff, fallV;
    float putAt;
    WindowBox box;
    long standOn;
    bool wasWindow;
    float swish, wobble;
    int platVersion = -1;
    Material mat;
    SpriteRenderer board, post, rimBack, rimFront, net, stand;
    static Sprite s_board, s_bar, s_net, s_post, s_stand;

    // ------------------------------------------------------------------ putting it up

    /// <summary>
    /// Put the hoop up (and a ball out): on the window under the cursor if it suits, else the window that suits best
    /// (a top neither too low nor too high, room beside its corner for the rim); no windows = standing on the taskbar.
    /// </summary>
    public static Hoop Put(Material material, Vector2 near)
    {
        if (Current) return Current;
        var go = new GameObject("Basketball Hoop");
        var h = go.AddComponent<Hoop>();
        h.mat = material;
        h.putAt = Time.time;
        h.Build();
        Current = h;
        if (PickCorner(near, out WindowBox w, out int side)) h.Attach(w, side);
        else
        {
            Rect r = ScreenBounds.ScreenRectAt(near); // (more than one screen: on the floor of the one near you)
            h.Side = near.x > r.center.x ? -1 : 1;
            h.foot = new Vector2(Mathf.Clamp(near.x, r.xMin + 1f, r.xMax - 1f), r.yMin);
            h.State = Mount.Standing;
        }
        h.Place();
        Vector2 at = h.Rim + new Vector2(h.Side * 0.9f, 0.2f);
        Basketball.Spawn(at, new Vector2(h.Side * 1.2f, 1f), material);
        return h;
    }

    /// <summary>Take it down (and the ball with it).</summary>
    public static void TakeDown()
    {
        if (Basketball.Current) Destroy(Basketball.Current.gameObject);
        if (Current) Destroy(Current.gameObject);
        Current = null;
    }

    // a window whose top corner it can clip onto: the one under the cursor first, else a top 2-5.5 above the floor
    static bool PickCorner(Vector2 near, out WindowBox best, out int side)
    {
        best = default;
        side = 1;
        Rect r = ScreenBounds.WorldRect;
        float bestScore = float.MaxValue;
        foreach (var w in DesktopPlatforms.Windows)
        {
            if (!w.movable || w.screen || w.yMax > r.yMax - 1.4f || w.yMax < r.yMin + 1.3f) continue;
            for (int s = -1; s <= 1; s += 2)
            {
                float cx = s > 0 ? w.xMax : w.xMin;
                if (cx + s * 1.2f > r.xMax || cx + s * 1.2f < r.xMin) continue; // (no room for the rim out there)
                if (Covered(w, cx, s)) continue;
                float h = w.yMax - ScreenBounds.FloorAt(new Vector2(cx, w.yMax)); // (how high above its floor)
                float score = Mathf.Abs(h - 3.2f) + 0.15f * Mathf.Abs(cx - near.x);
                if (w.Contains(near)) score -= 3f; // the window under your cursor
                if (score < bestScore) { bestScore = score; best = w; side = s; }
            }
        }
        return bestScore < float.MaxValue;
    }

    // another window sitting over where the board and rim would be
    static bool Covered(WindowBox w, float cx, int s)
    {
        float x0 = Mathf.Min(cx, cx + s * 0.75f), x1 = Mathf.Max(cx, cx + s * 0.75f);
        foreach (var o in DesktopPlatforms.Windows)
            if (o.id != w.id && o.movable && o.xMax > x0 && o.xMin < x1 && o.yMin < w.yMax + 1.2f && o.yMax > w.yMax + 0.05f) return true;
        return false;
    }

    void Attach(WindowBox w, int side)
    {
        box = w;
        Window = w.id;
        Side = side;
        standOn = 0;
        foot = new Vector2(side > 0 ? w.xMax : w.xMin, w.yMax);
        State = Mount.Window;
        wobble = 1f;
    }

    void OnEnable() { GrabTargets.Add(this); if (!Current) Current = this; }
    void OnDisable() { GrabTargets.Remove(this); if (Current == this) Current = null; }

    // ------------------------------------------------------------------ you

    public bool Contains(Vector2 p)
    {
        if (Hidden) return false;
        Rect b = Board;
        if (p.x > b.xMin - 0.06f && p.x < b.xMax + 0.06f && p.y > foot.y - 0.02f && p.y < b.yMax + 0.04f) return true; // the board and its post
        return State == Mount.Standing && Mathf.Abs(p.x - foot.x) < 0.2f && p.y > foot.y && p.y < foot.y + 0.12f; // its foot
    }

    public void BeginGrab(Vector2 p)
    {
        wasWindow = State == Mount.Window;
        State = Mount.Held;
        Window = 0;
        standOn = 0;
        grabOff = foot - p;
    }

    public void DragTo(Vector2 p)
    {
        Vector2 want = p + grabOff;
        Rect r = ScreenBounds.ScreenRectAt(want);
        foot = new Vector2(Mathf.Clamp(want.x, r.xMin + 0.2f, r.xMax - 0.2f), Mathf.Clamp(want.y, r.yMin, r.yMax - 1.6f));
    }

    public void EndGrab()
    {
        wasWindow = false;
        // near a window's top corner: clips on there (the rim out over the side); else it falls
        float best = 0.6f;
        WindowBox bw = default;
        int bs = 0;
        Rect r = ScreenBounds.WorldRect;
        foreach (var w in DesktopPlatforms.Windows)
        {
            if (!w.movable || w.screen || w.yMax > r.yMax - 1.3f) continue;
            for (int s = -1; s <= 1; s += 2)
            {
                Vector2 c = new Vector2(s > 0 ? w.xMax : w.xMin, w.yMax);
                if (c.x + s * 0.9f > r.xMax || c.x + s * 0.9f < r.xMin) continue;
                float d = (c - foot).magnitude;
                if (d < best) { best = d; bw = w; bs = s; }
            }
        }
        if (bs != 0) { Attach(bw, bs); return; }
        State = Mount.Falling;
        fallV = Vector2.zero;
    }

    // ------------------------------------------------------------------ each frame

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("Hoop.Update", t0); }
    void UpdateTimed()
    {
        float dt = Mathf.Min(Time.deltaTime, 1f / 30f);
        Rect r = ScreenBounds.WorldRect;
        if (State == Mount.Window)
        {
            // still there and still in the same place? (moved or minimized: it falls off)
            bool found = false;
            foreach (var w in DesktopPlatforms.Windows)
            {
                if (w.id != Window) continue;
                found = true;
                if (Mathf.Abs(w.xMin - box.xMin) > 0.03f || Mathf.Abs(w.xMax - box.xMax) > 0.03f || Mathf.Abs(w.yMax - box.yMax) > 0.03f) found = false;
                break;
            }
            if (!found) { State = Mount.Falling; fallV = new Vector2(Side * 0.6f, 0.5f); Window = 0; }
        }
        if (State == Mount.Falling)
        {
            fallV.y -= 18f * dt;
            Vector2 prev = foot;
            foot += fallV * dt;
            foot.x = Mathf.Clamp(foot.x, r.xMin + 0.2f, r.xMax - 0.2f);
            float landY = r.yMin;
            long on = 0;
            foreach (var p in DesktopPlatforms.Current)
                if (foot.x >= p.x0 && foot.x <= p.x1 && p.y <= prev.y + 1e-3f && p.y > landY) { landY = p.y; on = p.id; }
            if (foot.y <= landY)
            {
                foot.y = landY;
                standOn = on;
                State = Mount.Standing;
                wobble = 1f;
                // (the rim mustn't stick out off the screen)
                if (foot.x + Side * 0.9f > r.xMax || foot.x + Side * 0.9f < r.xMin) Side = -Side;
            }
        }
        if (State == Mount.Standing && standOn != 0 && DesktopPlatforms.Version != platVersion)
        {
            // riding the window top it stands on (gone: it falls)
            platVersion = DesktopPlatforms.Version;
            bool found = false;
            foreach (var p in DesktopPlatforms.Current)
                if (p.id == standOn) { foot += new Vector2(p.dx, p.dy); found = true; break; }
            if (!found) { State = Mount.Falling; fallV = Vector2.zero; standOn = 0; }
        }
        swish = Mathf.MoveTowards(swish, 0f, dt * 2.5f);
        wobble = Mathf.MoveTowards(wobble, 0f, dt * 2f);
        Hidden = !Held && State != Mount.Falling && Occlusion.Hidden(Board.center, Layer);
        Place();
    }

    // ------------------------------------------------------------------ drawing

    void Build()
    {
        if (!s_board) Bake();
        // (above the buddies' own draw orders, 5..61, and their masks; below the zipline, 70+)
        board = Part("Backboard", s_board, 64);
        post = Part("Post", s_post, 63);
        rimBack = Part("Rim (back)", s_bar, 65);
        rimFront = Part("Rim (front)", s_bar, 67);
        net = Part("Net", s_net, 67);
        stand = Part("Stand", s_stand, 63);
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
        if (!board) return;
        Rect b = Board;
        float tilt = wobble * 6f * Mathf.Sin(Time.time * 22f);
        // (drawn a bit thicker than it bounces: it reaches back over the window's corner; the board sprite is 0.25 x 1)
        float bx0 = foot.x - Side * 0.02f, bx1 = foot.x + Side * (BoardOff + BoardThick);
        Set(board, new Vector2(0.5f * (bx0 + bx1), b.center.y), new Vector2(Mathf.Abs(bx1 - bx0) / 0.25f, b.height), tilt);
        Vector2 rim = Rim;
        float rimW = 2f * (RimHalf + RimTube);
        Set(rimBack, rim + new Vector2(0f, 0.012f), new Vector2(rimW, 0.035f / 0.25f), 0f);
        Set(rimFront, rim - new Vector2(0f, 0.012f), new Vector2(rimW, 0.035f / 0.25f), 0f);
        float sw = swish;
        Set(net, rim + new Vector2(0f, -0.2f - 0.06f * sw), new Vector2(rimW * (1f - 0.12f * sw), 0.38f + 0.12f * sw), 3f * sw * Mathf.Sin(Time.time * 30f));
        float postTop = b.yMin, postX = foot.x + Side * (BoardOff + BoardThick * 0.5f);
        Set(post, new Vector2(postX, (foot.y + postTop) * 0.5f), new Vector2(0.05f / 0.25f, Mathf.Max(0.02f, postTop - foot.y)), 0f);
        bool standing = State == Mount.Standing || State == Mount.Falling || (State == Mount.Held && !wasWindow);
        stand.enabled = standing && !Hidden;
        Set(stand, new Vector2(postX, foot.y + 0.04f), new Vector2(0.5f, 0.08f / 0.25f), 0f);
        board.enabled = post.enabled = rimBack.enabled = rimFront.enabled = net.enabled = !Hidden;
    }

    static void Set(SpriteRenderer sr, Vector2 at, Vector2 size, float deg)
    {
        sr.transform.position = new Vector3(at.x, at.y, 0f);
        sr.transform.rotation = Quaternion.Euler(0f, 0f, deg);
        sr.transform.localScale = new Vector3(size.x, size.y, 1f);
    }

    static void Bake()
    {
        s_board = BakeSprite(24, 96, BoardPaint, "Backboard");   // 0.25 x 1 units
        s_bar = BakeSprite(32, 8, RimPaint, "Rim");              // 1 x 0.25
        s_net = BakeSprite(48, 48, NetPaint, "Net");             // 1 x 1
        s_post = BakeSprite(8, 32, PostPaint, "Post");           // 0.25 x 1
        s_stand = BakeSprite(32, 8, StandPaint, "Stand");        // 1 x 0.25
    }

    static Sprite BakeSprite(int w, int h, System.Func<float, float, float, Color> paint, string name)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = name };
        var px = new Color32[w * h];
        float texel = 1f / w;
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                Color c = paint((i + 0.5f) / w, (j + 0.5f) / h, texel);
                px[j * w + i] = new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), Mathf.Clamp01(c.a));
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), Mathf.Max(w, h)); // (the longer side is 1 unit)
    }

    // u, v in 0..1 (the board seen from the side, a little turned towards you: white with a red edge and the red square)
    internal static Color BoardPaint(float u, float v, float texel)
    {
        float e = Mathf.Min(Mathf.Min(u, 1f - u) * 0.25f, Mathf.Min(v, 1f - v)); // distance to the edge in board units
        Color c = new Color(0.97f, 0.97f, 0.99f);
        if (v > 0.2f && v < 0.47f && u > 0.25f && u < 0.75f) c = new Color(0.86f, 0.18f, 0.16f);      // the square above the rim
        if (v > 0.24f && v < 0.43f && u > 0.38f && u < 0.62f) c = new Color(0.97f, 0.97f, 0.99f);
        if (e < 0.035f) c = new Color(0.86f, 0.18f, 0.16f);                                            // red edge
        if (e < 0.012f) c = new Color(0.45f, 0.08f, 0.08f);
        c *= 0.9f + 0.1f * u;
        c.a = 1f;
        return c;
    }

    // an orange-red tube (u along it, v across)
    internal static Color RimPaint(float u, float v, float texel)
    {
        float d = Mathf.Abs(v - 0.5f) * 2f;
        Color c = Color.Lerp(new Color(1f, 0.45f, 0.12f), new Color(0.62f, 0.16f, 0.04f), d * d);
        c.a = Mathf.Clamp01((1f - d) * 3f);
        return c;
    }

    // a white net: diagonal strings, narrowing to the bottom (u across, v up: the top at v = 1)
    internal static Color NetPaint(float u, float v, float texel)
    {
        float halfW = Mathf.Lerp(0.3f, 0.5f, v);
        float x = (u - 0.5f) / halfW; // -1..1 across the net at this height
        if (Mathf.Abs(x) > 1f || v < 0.02f) return new Color(0f, 0f, 0f, 0f);
        float a = Mathf.Repeat(x * 3f + v * 4f, 1f), b = Mathf.Repeat(-x * 3f + v * 4f, 1f);
        float line = Mathf.Min(Mathf.Min(a, 1f - a), Mathf.Min(b, 1f - b));
        float k = Mathf.Clamp01(1f - line / 0.09f);
        if (Mathf.Abs(x) > 0.93f) k = Mathf.Max(k, 0.8f);
        return new Color(0.97f, 0.97f, 0.97f, k * 0.95f);
    }

    internal static Color PostPaint(float u, float v, float texel)
    {
        float d = Mathf.Abs(u - 0.5f) * 2f;
        return new Color(0.52f, 0.55f, 0.6f) * (1.05f - 0.35f * d * d) + new Color(0f, 0f, 0f, 1f);
    }

    internal static Color StandPaint(float u, float v, float texel)
    {
        float d = Mathf.Max(Mathf.Abs(u - 0.5f) * 2f, Mathf.Abs(v - 0.5f) * 2f);
        Color c = new Color(0.22f, 0.24f, 0.28f) * (1.1f - 0.3f * v);
        c.a = Mathf.Clamp01((1f - d) * 6f);
        return c;
    }
}
