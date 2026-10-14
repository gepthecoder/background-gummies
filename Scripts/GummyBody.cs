using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Gummy soft-body apple. A ring of particles keeps its shape (shape matching) and its volume (area constraint),
/// sticks to the screen edges when it hits them, slides down and peels off, stretches when you pull it and pops
/// free past a limit. Rendered with the WhipIt/Gummy shader: crisp outline, dome shading and a highlight that
/// stays fixed to the light, plus a drop shadow. Tuned in a Python prototype of this exact solver.
/// Units: world, screen height = 10. The root transform follows the body's centre (keep its scale at 1).
/// </summary>
[DisallowMultipleComponent]
public class GummyBody : MonoBehaviour, IGrabTarget
{
    public enum BodyShape { Apple, Seed, Tomato, Bean, Burger, Dino, Duck, Monster }
    public enum Topper { StemLeaf, Sprout, None, Calyx, Crease }

    [Header("Look")]
    [Tooltip("Body width in world units.")]
    public float size = 0.6f;
    [Tooltip("Outline (applied on Play; BuddyProgress morphs it as it grows).")]
    public BodyShape shape = BodyShape.Apple;
    public Topper topper = Topper.StemLeaf;
    public Color color = new Color(0.90f, 0.22f, 0.21f);
    public Color rimColor = new Color(0.45f, 0.08f, 0.08f);
    public Color leafColor = new Color(0.26f, 0.63f, 0.28f);
    public Color stemColor = new Color(0.43f, 0.30f, 0.25f);
    [HideInInspector] public int monsterSeed; // BodyShape.Monster: which lumps (MonsterLook sets it from who's inside)
    [Tooltip("WhipIt/Gummy (WhipIt > Setup assigns it so it's included in builds).")]
    public Shader shader;
    [Tooltip("Sprite material for the stem/leaf and face (WhipIt > Setup assigns Whip.mat).")]
    public Material spriteMaterial;
    public int sortingOrder = 5;
    [Range(0f, 1f)] public float shadowAlpha = 0.28f;
    public Vector2 shadowOffset = new Vector2(0.03f, -0.04f);
    [Range(0f, 2f)] public float gloss = 0.9f;

    [Header("Gummy")]
    [Tooltip("Applied on Play.")]
    [Range(8, 32)] public int particles = 24;
    [Tooltip("How hard it pulls back to its shape each physics step. Lower = floppier.")]
    [Range(0.005f, 0.5f)] public float stiffness = 0.06f;
    [Range(0f, 1f)] public float edgeStiffness = 0.1f;
    [Range(0f, 2f)] public float areaStiffness = 1f;
    [Range(1, 16)] public int iterations = 6;
    public float gravity = 30f;
    [Range(0f, 0.05f)] public float damping = 0.003f;
    [Tooltip("Grip on edges (Coulomb friction: the harder it presses, the more it grips). Lets it roll, slide and spin off walls.")]
    [Range(0f, 1.5f)] public float friction = 0.6f;
    [Range(0f, 1f)] public float restitution = 0.15f;
    [Tooltip("Impacts faster than this raise the Bump event (used for sounds).")]
    public float bumpSpeed = 1f;
    [Tooltip("Land on, stick to and ride the top edges of open windows (needs DesktopPlatforms in the scene).")]
    public bool usePlatforms = true;
    [Tooltip("How fast it rolls back upright when it's lying still (so the face ends up the right way up).")]
    public float uprightSpeed = 3f;
    [Tooltip("Puffs up a little (area) when hovered / held.")]
    public float hoverSwell = 0.06f;
    public float heldSwell = 0.1f;

    [Header("Sticky")]
    public bool sticky = true;
    [Tooltip("Minimum speed into an edge to stick (resting contact doesn't stick).")]
    public float stickSpeed = 2f;
    [Tooltip("Seconds a stuck point holds: holdTime + holdPerSpeed * impact speed, capped at maxHold.")]
    public float holdTime = 0.6f;
    public float holdPerSpeed = 0.1f;
    public float maxHold = 3f;
    [Tooltip("Stuck points slide down faster and faster as they run out of hold.")]
    public float slideSpeed = 0.35f;
    [Tooltip("A stuck point tears off when something pulls it this far per step (x size / 0.6).")]
    public float tearLoad = 0.02f;
    [Tooltip("Pull the grab point this far (x size) from where it's stuck and all the glue pops at once.")]
    public float maxStretch = 2f;
    [Tooltip("Hits this many times more sideways than head-on skid off instead of sticking.")]
    public float glancing = 2f;

    [Header("Walls & windows")]
    [Tooltip("The sides and bottoms of windows are solid too (not just their tops). Windows whose top is off-screen never are.")]
    public bool solidWindows = true;
    [Tooltip("Bump into other buddies (squish against them) instead of passing through.")]
    public bool collideBuddies = true;
    /// <summary>Passes through the other buddies until then (Time.time): squeezing past a crowd on its way somewhere
    /// (BuddyRain keeps pushing it on while it needs it).</summary>
    public float GhostUntil { get; set; } = -1f;
    bool Ghosting => Time.time < GhostUntil;
    [Tooltip("Jelly wobble after an impact: squash per unit of impact speed.")]
    public float impactWobble = 0.02f;
    public float wobbleFrequency = 4f;
    [Range(0f, 1f)] public float wobbleDamping = 0.3f;
    [Tooltip("While climbing: pull towards the wall and how much gravity still pulls.")]
    public float crawlGrip = 70f;
    [Range(0f, 1f)] public float crawlGravity = 0.3f;

    [Header("Grab")]
    [Tooltip("Radius around the cursor that follows it when grabbed (x size / 0.6).")]
    public float grabRadius = 0.35f;
    [Range(0f, 1f)] public float grabStiffness = 0.5f;

    /// <summary>Hit an edge hard enough to stick: contact point, edge normal (pointing away from the edge), impact speed.</summary>
    public event Action<Vector2, Vector2, float> Splat;
    /// <summary>Hit an edge without sticking (lighter bump): contact point, edge normal, impact speed.</summary>
    public event Action<Vector2, Vector2, float> Bump;
    /// <summary>Pulled free of an edge: the spot it was stuck to, direction it was pulled in.</summary>
    public event Action<Vector2, Vector2> Pop;
    /// <summary>A window opened / moved over it and it squeezed out: window id, launch velocity.</summary>
    public event Action<long, Vector2> Escaped;

    public bool Hovered { get; set; }
    public bool Held => grabbing;
    public Vector2 Center { get; private set; }
    /// <summary>Body rotation in degrees (from shape matching).</summary>
    public float Angle { get; private set; }
    public bool IsStuck { get; private set; }
    /// <summary>0..1 while held and stuck: how close the pull is to popping it free.</summary>
    public float Stretch { get; private set; }
    /// <summary>Touching the floor (bottom edge) and not held.</summary>
    public bool Grounded { get; private set; }
    /// <summary>Average velocity of the body (world units / s).</summary>
    public Vector2 Velocity { get; private set; }
    /// <summary>Target shape scale in the body's own frame (x = width, y = height). (1,1) = normal.
    /// Keep x*y near 1 so the volume constraint agrees (e.g. squash 1.15 x 0.87).</summary>
    public Vector2 Pose { get; set; } = Vector2.one;
    /// <summary>x..y extent of what it's standing on (the floor, or the top of a window).</summary>
    public Vector2 SupportSpan { get; private set; } = new Vector2(-1e6f, 1e6f);
    /// <summary>Standing on (or stuck to) the top of a window rather than the floor.</summary>
    public bool OnPlatform { get; private set; }
    /// <summary>Axis-aligned outline bounds (world units), updated every physics step.</summary>
    public Rect Extent { get; private set; }
    /// <summary>What the last Splat / Bump hit: 0 = a screen edge, otherwise the window id.</summary>
    public long LastHitSurface { get; private set; }
    /// <summary>The window (or icon) whose top it's standing on (0 = the floor / nothing).</summary>
    public long SupportWindow { get; private set; }
    /// <summary>The buddy it's sitting on top of (null = none).</summary>
    public GummyBody SupportBody { get; private set; }
    /// <summary>While stuck: the window it's stuck to (0 = a screen edge, or not stuck).</summary>
    public long GluedWindow { get; private set; }
    /// <summary>While stuck: how many outline points hold on, their middle, and the edge normal (away from the edge).</summary>
    public int GluedCount { get; private set; }
    public Vector2 GlueCenter { get; private set; }
    public Vector2 GlueNormal { get; private set; }

    /// <summary>A straight edge to climb. axis 0 = vertical wall at x = pos, 1 = horizontal edge at y = pos;
    /// sign = which side is free space (+1 = the +x / +y side); from..to = where along it the edge exists.</summary>
    public struct Edge { public int axis; public float pos, sign, from, to; public long surface; }
    public bool Crawling => crawling;
    public Edge CrawlEdge => crawlEdge;

    /// <summary>Room it may use beyond the left / right screen edge (for peeking in from off-screen).
    /// Shrinks back by itself as the body comes back in, unless HoldRoom is set.</summary>
    public float RoomLeft { get; set; }
    public float RoomRight { get; set; }
    public bool HoldRoom { get; set; }
    /// <summary>Standing firm on its own legs (GummyBrain sets it around its own hops): stays upright instead of rolling off a landing.</summary>
    public bool Brace { get; set; }

    /// <summary>Hiding behind something (hide and seek): nothing of it is drawn inside this rect (world); zero size = not hiding.
    /// It can't be grabbed there either, so your clicks still reach the window.</summary>
    public Rect HideRect { get; set; }
    /// <summary>The window it's hiding behind: not solid for it, so it can walk in behind it (0 = none).</summary>
    public long HideWindow { get; set; }
    /// <summary>Extra room below the floor (behind the taskbar). Shrinks back as it comes up, unless HoldRoom.</summary>
    public float RoomBelow { get; set; }
    public bool Hiding => HideRect.width > 0f && HideRect.height > 0f;

    /// <summary>Climb: hug this edge and creep along it (speed along the edge, + = towards larger x / y).</summary>
    public void Crawl(Edge edge, float speed)
    {
        crawling = true;
        crawlEdge = edge;
        crawlSpeed = speed;
        for (int i = 0; i < n; i++) glued[i] = false;
        IsStuck = false;
    }

    public void StopCrawl() => crawling = false;

    /// <summary>Glue whatever touches the climbing edge right now, for about this long (then it peels off as usual).</summary>
    public void StickToEdge(float hold)
    {
        Edge e = crawlEdge;
        int tn = 1 - e.axis;
        Vector2 nrm = e.axis == 0 ? new Vector2(e.sign, 0f) : new Vector2(0f, e.sign);
        crawling = false;
        for (int i = 0; i < n; i++)
        {
            if (Mathf.Abs(x[i][e.axis] - e.pos) > 0.01f * size || x[i][tn] < e.from || x[i][tn] > e.to) continue;
            glued[i] = true;
            anchor[i] = x[i];
            anchor[i][e.axis] = e.pos;
            gNrm[i] = nrm;
            gSurf[i] = e.surface;
            life[i] = life0[i] = Mathf.Max(hold * 0.15f, 0.12f);
        }
        patchHold = Mathf.Max(patchHold, hold);
    }

    /// <summary>Jump: adds velocity to the whole body, lets go of any glue and doesn't stick for a moment.</summary>
    public void Hop(Vector2 velocity, float noStickTime = 0.6f)
    {
        for (int i = 0; i < n; i++) { v[i] += velocity; glued[i] = false; }
        IsStuck = false;
        noStickUntil = simTime + noStickTime;
        noGripUntil = simTime + 0.06f; // the floor doesn't hold its feet back on take-off (that would send it spinning)
    }
    float noGripUntil;

    /// <summary>Sets it tumbling: adds a spin (radians / s, + = counterclockwise) round its middle, like a Hop.</summary>
    public void Spin(float omega, float noStickTime = 0.4f)
    {
        if (x == null) return;
        Vector2 c = Center;
        for (int i = 0; i < n; i++) { Vector2 r = x[i] - c; v[i] += new Vector2(-r.y, r.x) * omega; glued[i] = false; }
        IsStuck = false;
        noStickUntil = simTime + noStickTime;
        noGripUntil = simTime + 0.06f;
    }

    // ------------------------------------------------------------------ rope (BuddyRope: swings, hanging from your cursor)

    /// <summary>Hanging on a rope: its middle stays within RopeLength of the hook (closer is fine: the rope goes slack).</summary>
    public bool OnRope => roped;

    /// <summary>The top of its outline right now (where the stem sits: hats go here).</summary>
    public Vector2 TopPoint => x != null && n > 0 ? x[top] : Center;
    /// <summary>
    /// A point of its outline deg degrees round from its top (+ = towards its left, 180 = its bottom), following every
    /// squash and wobble; normal = the outward direction there.
    /// </summary>
    public Vector2 OutlinePoint(float deg, out Vector2 normal)
    {
        if (x == null || n < 3) { normal = Vector2.up; return Center; }
        float f = Mathf.Repeat(top + deg / 360f * n, n);
        int i0 = Mathf.Min((int)f, n - 1), i1 = (i0 + 1) % n;
        Vector2 tan = x[(i1 + 1) % n] - x[(i0 - 1 + n) % n]; // (counterclockwise round the outline)
        normal = tan.sqrMagnitude > 1e-10f ? new Vector2(tan.y, -tan.x).normalized : Vector2.up;
        return Vector2.Lerp(x[i0], x[i1], f - i0);
    }
    /// <summary>How squashed / stretched it looks right now along its own axes (the face squishes this much).</summary>
    public Vector2 Squash { get; private set; } = Vector2.one;
    /// <summary>Its width and height along its own axes (turned with it).</summary>
    public Vector2 Span { get; private set; }
    /// <summary>Show the stem / leaf / calyx.</summary>
    public bool TopperVisible
    {
        get => topperVisible;
        set { topperVisible = value; if (stemRenderer) stemRenderer.enabled = value && stemSprite; }
    }
    bool topperVisible = true;
    /// <summary>Rope length from the hook to its middle (setting it jumps there; ReelTo pulls in smoothly).</summary>
    public float RopeLength { get => ropeLen; set { ropeLen = Mathf.Max(0.05f, value); ropeTarget = ropeLen; } }

    /// <summary>Pull itself up the rope to this length at this speed (world units / s), a little every physics step.</summary>
    public void ReelTo(float length, float speed)
    {
        ropeTarget = Mathf.Max(0.05f, length);
        reelSpeed = Mathf.Max(0f, speed);
    }
    /// <summary>Where the rope hangs from (it follows MoveRope smoothly).</summary>
    public Vector2 RopeHook => ropeTo;
    /// <summary>Where the rope is tied on it: the point of its outline facing the hook.</summary>
    public Vector2 RopePoint { get; private set; }
    /// <summary>How much further its middle is from the hook than the rope is long (something's in the way).</summary>
    public float RopeStretch { get; private set; }

    /// <summary>Catch a rope: hangs from hook (world), length from the hook to its middle.</summary>
    public void Rope(Vector2 hook, float length)
    {
        roped = true;
        ropeAt = ropeFrom = ropeTo = hook;
        ropeLen = ropeTarget = Mathf.Max(0.05f, length);
        for (int i = 0; i < n; i++) glued[i] = false;
        IsStuck = false;
        crawling = false;
        RopeStretch = 0f;
    }

    /// <summary>The hook moved (your cursor): the rope follows over the next steps (fast moves catch up).</summary>
    public void MoveRope(Vector2 hook) => ropeTo = hook;

    /// <summary>Let go of the rope (keeps flying with the speed it has).</summary>
    public void LetGo()
    {
        if (!roped) return;
        roped = false;
        noStickUntil = Mathf.Max(noStickUntil, simTime + 0.4f);
    }

    // ------------------------------------------------------------------ skateboard (BuddySkate draws the board, GummyBrain rides it)

    /// <summary>On its skateboard: it stands on the board (lift higher), the wheels roll (no grip on the floor or window tops),
    /// it stays upright and doesn't stick to anything it bumps into.</summary>
    public bool Skating => skating;
    /// <summary>How high the board holds it up (feet above the floor).</summary>
    public float SkateLift => skating ? skateLift : 0f;
    /// <summary>Leans this much while it rides (degrees, + = to the left).</summary>
    public float Lean { get; set; }

    /// <summary>Step onto the board (hops up onto it if it's standing on something).</summary>
    public void Skate(float lift)
    {
        if (skating) return;
        skating = true;
        skateLift = Mathf.Max(0f, lift);
        skateDrive = false;
        if (Grounded) for (int i = 0; i < n; i++) x[i].y += skateLift; // up onto the board
        for (int i = 0; i < n; i++) glued[i] = false;
        IsStuck = false;
    }

    /// <summary>Push towards this speed (x, world units / s) with up to this much acceleration, while the wheels are down.</summary>
    public void Drive(float speed, float accel)
    {
        skateDrive = true;
        skateVx = speed;
        skateAccel = Mathf.Max(0f, accel);
    }

    /// <summary>Just roll (no pushing, no braking).</summary>
    public void Coast() => skateDrive = false;

    /// <summary>Off the board (back down onto its feet).</summary>
    public void StopSkating()
    {
        skating = false;
        skateDrive = false;
        Lean = 0f;
    }

    bool skating, skateDrive;
    float skateLift, skateVx, skateAccel;

    // ------------------------------------------------------------------ paper plane (BuddyPlane draws it, GummyBrain flies it)

    /// <summary>On its paper plane: it flies at the velocity it's given (gravity doesn't pull it down), stays upright (Lean
    /// tilts it), doesn't stick to anything it bumps into. Landing on something is the brain's call (StopGlide).</summary>
    public bool Gliding => gliding;

    /// <summary>Fly at this velocity, getting there with up to this much acceleration (world units / s²).</summary>
    public void Glide(Vector2 velocity, float accel)
    {
        if (!gliding) { for (int i = 0; i < n; i++) glued[i] = false; IsStuck = false; }
        gliding = true;
        glideV = velocity;
        glideAccel = Mathf.Max(0f, accel);
    }

    /// <summary>Off the plane: gravity again.</summary>
    public void StopGlide()
    {
        gliding = false;
        Lean = 0f;
        TurnTo = float.NaN;
    }

    /// <summary>While it glides: turned to this angle (degrees, + = counterclockwise) instead of staying upright, e.g.
    /// lying head first down a slide (Slide). NaN = upright as usual.</summary>
    public float TurnTo { get; set; } = float.NaN;

    bool gliding;
    Vector2 glideV;
    float glideAccel;

    bool roped;
    float ropeLen, ropeTarget, reelSpeed;
    Vector2 ropeAt, ropeFrom, ropeTo;
    const float RopeFollow = 24f; // the hook moves at most this fast (world units / s): a flick doesn't fire it across the screen

    const float Dt = 1f / 240f;
    const int MaxPts = 64, Sub = 2;

    static readonly int PtsId = Shader.PropertyToID("_Pts"), CountId = Shader.PropertyToID("_Count"),
        ColorId = Shader.PropertyToID("_Color"), RimId = Shader.PropertyToID("_RimColor"),
        ThickId = Shader.PropertyToID("_Thickness"), OffsetId = Shader.PropertyToID("_Offset"),
        FlatId = Shader.PropertyToID("_Flat"), SpecId = Shader.PropertyToID("_Spec"), ClipId = Shader.PropertyToID("_Clip"),
        ClipsId = Shader.PropertyToID("_Clips"), ClipCountId = Shader.PropertyToID("_ClipCount"),
        PatchId = Shader.PropertyToID("_Patches"), PatchColId = Shader.PropertyToID("_PatchColors"),
        PatchRimId = Shader.PropertyToID("_PatchRims"), PatchCountId = Shader.PropertyToID("_PatchCount");

    // ------------------------------------------------------------------ windows in front of it (BuddyLayer)

    public const int MaxOccluders = 8;
    readonly Vector4[] occ = new Vector4[MaxOccluders];
    readonly Rect[] occRects = new Rect[MaxOccluders];
    int occCount;

    /// <summary>BuddyLayer hides what's behind windows (and the hide-and-seek rect) with its own sprite masks.</summary>
    public bool ExternalOcclusion { get; set; }

    /// <summary>Windows in front of it (world rects, at most MaxOccluders): nothing of it is drawn there, and clicks there
    /// go through to the window.</summary>
    public void SetOccluders(System.Collections.Generic.List<Rect> rects)
    {
        int c = Mathf.Min(rects.Count, MaxOccluders);
        bool same = c == occCount;
        for (int i = 0; i < c && same; i++) same = occRects[i] == rects[i];
        if (same) return;
        occCount = c;
        for (int i = 0; i < MaxOccluders; i++)
        {
            Rect r = i < c ? rects[i] : default;
            occRects[i] = r;
            occ[i] = i < c ? new Vector4(r.xMin, r.yMin, r.xMax, r.yMax) : new Vector4(1f, 1f, 0f, 0f);
        }
        if (!bodyMat) return;
        bodyMat.SetVectorArray(ClipsId, occ);
        bodyMat.SetFloat(ClipCountId, occCount);
        shadowMat.SetVectorArray(ClipsId, occ);
        shadowMat.SetFloat(ClipCountId, occCount);
    }

    /// <summary>Is this point of it behind a window in front of it?</summary>
    public bool Occluded(Vector2 pt)
    {
        for (int i = 0; i < occCount; i++) if (occRects[i].Contains(pt)) return true;
        return false;
    }

    int n, top;
    Vector2 restCentroidUv;          // rest centroid relative to the apple's texture origin (world units)
    Vector2[] restUvFrom, restUvTo;  // rest outline in apple-uv space, morphing from -> to
    float morphT = 1f, morphDur = 1f, sizeFrom, sizeTo, faceBaseSize, stemBakeSize;
    Topper bakedTopper;
    SpriteRenderer stemRenderer;
    Vector2[] q, x, p, v, vPrev, corr, nrm, anchor, grabOff;
    float[] L0, life, life0, load, grabW;
    bool[] glued;
    float A0, accumulator, swell, simTime, noStickUntil;
    long[] onPlat;                              // per particle: id of the window top it rests on (0 = none)
    Vector2[] gNrm;                             // glued: normal of the edge it's stuck to
    Vector2[] pen;                              // how far edges pushed each point back out this step (x, y): its normal force
    long[] gSurf;                               // glued: what it's stuck to (0 = screen edge, else window id)
    float patchAge, patchHold;                  // the current stuck patch
    float wobA, wobV;                           // jelly wobble after an impact (squash along wobN)
    Vector2 wobN = Vector2.up;
    bool crawling, righting;
    Edge crawlEdge;
    float crawlSpeed;
    struct Box { public WindowBox b; public Vector2 shift; }
    readonly Box[] boxes = new Box[16];         // solid windows near the body this step
    int boxCount, glueCheckVersion = -1;
    readonly Dictionary<long, Vector2> boxLast = new Dictionary<long, Vector2>();
    readonly HashSet<long> ghost = new HashSet<long>(); // windows it's inside of (opened on top of it) until it's out
    readonly List<long> scratch = new List<long>();
    readonly Platform[] near = new Platform[16]; // platforms near the body this step
    int nearCount, platVersion = -1;
    bool grabbing;
    Vector2 cursor;

    Transform frame, stem;
    Mesh quad;
    Material bodyMat, shadowMat;
    Texture2D stemTex;
    Sprite stemSprite;
    readonly Vector4[] pts = new Vector4[MaxPts];
    readonly Vector3[] quadVerts = new Vector3[4];

    // ------------------------------------------------------------------ lifecycle

    void Awake()
    {
        n = Mathf.Clamp(particles, 8, MaxPts / Sub);
        BuildRestShape();
        Vector2 c = transform.position;
        x = new Vector2[n]; p = new Vector2[n]; v = new Vector2[n]; vPrev = new Vector2[n];
        corr = new Vector2[n]; nrm = new Vector2[n]; anchor = new Vector2[n]; grabOff = new Vector2[n];
        life = new float[n]; life0 = new float[n]; load = new float[n]; grabW = new float[n]; glued = new bool[n];
        for (int i = 0; i < n; i++) x[i] = c + q[i];
        onPlat = new long[n];
        gNrm = new Vector2[n]; gSurf = new long[n]; pen = new Vector2[n];
        Center = c;
        BuildVisuals();
    }

    static readonly List<GummyBody> s_all = new List<GummyBody>();
    /// <summary>Every buddy body in the scene.</summary>
    public static IReadOnlyList<GummyBody> All => s_all;

    void OnEnable() { GrabTargets.Add(this); if (!s_all.Contains(this)) s_all.Add(this); }
    void OnDisable() { grabbing = false; roped = false; skating = false; skateDrive = false; gliding = false; Lean = 0f; TurnTo = float.NaN; GrabTargets.Remove(this); s_all.Remove(this); }

    void OnValidate() { if (bodyMat) ApplyStyle(); }

    void OnDestroy()
    {
        if (bodyMat) Destroy(bodyMat);
        if (shadowMat) Destroy(shadowMat);
        if (hideMask) Destroy(hideMask.gameObject);
        if (hideMaskSprite) Destroy(hideMaskSprite);
        if (hideMaskTex) Destroy(hideMaskTex);
        if (quad) Destroy(quad);
        if (stemSprite) Destroy(stemSprite);
        if (stemTex) Destroy(stemTex);
    }

    /// <summary>Stopped dead, mid-air or wherever it is (caught in the act, bullet time): no physics until it's let go
    /// (picking it up lets go too). It still rides a window that moves.</summary>
    public bool Frozen { get; set; }

    /// <summary>Inside a window it broke into (WindowInfestation): this little room (world) is its whole world: its only walls,
    /// no windows, window tops, icons or other buddies. Zero size = out in the world as usual.</summary>
    public Rect Room { get; set; }
    public bool InRoom => Room.width > 0f && Room.height > 0f;
    /// <summary>More solid shapes in its room (the pinball table's flippers, rails and bumpers): see IGummyArena. Only
    /// used while it's InRoom.</summary>
    public IGummyArena Arena { get; set; }
    /// <summary>Rolls and tumbles freely (it's the pinball): never braces itself against rolling.</summary>
    public bool Tumble { get; set; }

    /// <summary>Its average velocity right now (also in the middle of a physics step, unlike Velocity).</summary>
    public Vector2 LiveVelocity()
    {
        if (v == null || n == 0) return Vector2.zero;
        Vector2 s = Vector2.zero;
        for (int i = 0; i < n; i++) s += v[i];
        return s / n;
    }

    /// <summary>No faster than this (world units / s), as a whole; its wobble and spin are kept.</summary>
    public void LimitSpeed(float max)
    {
        Vector2 mv = LiveVelocity();
        float s = mv.magnitude;
        if (s <= max || s < 1e-6f) return;
        Vector2 d = mv * (1f - max / s);
        for (int i = 0; i < n; i++) v[i] -= d;
    }

    /// <summary>Move the whole thing by d (its room moved: the window it's in was dragged), keeping how it moves.</summary>
    public void Shift(Vector2 d)
    {
        if (x == null || d.sqrMagnitude < 1e-14f) return;
        for (int i = 0; i < n; i++) { x[i] += d; p[i] += d; anchor[i] += d; }
        Center += d;
        Extent = new Rect(Extent.x + d.x, Extent.y + d.y, Extent.width, Extent.height);
    }

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("GummyBody.Update", t0); }
    void UpdateTimed()
    {
        if (Frozen && grabbing) Frozen = false;
        float slow = grabbing ? 1f : Time.timeScale; // (bullet time: the world runs slow or stands still; what you hold keeps up with your hand)
        accumulator += Frozen ? 0f : Mathf.Min(Time.unscaledDeltaTime, 0.05f) * slow;
        int steps = Mathf.Min(Mathf.FloorToInt(accumulator / Dt), 16);
        accumulator = Mathf.Min(accumulator - steps * Dt, Dt);

        float target = grabbing ? heldSwell : Hovered ? hoverSwell : 0f;
        swell = Mathf.Lerp(swell, target, 1f - Mathf.Exp(-15f * Time.unscaledDeltaTime));

        if (morphT < 1f)
        {
            morphT = Mathf.Min(1f, morphT + Time.unscaledDeltaTime * slow / Mathf.Max(morphDur, 0.01f));
            float u = morphT * morphT * (3f - 2f * morphT);
            ApplyRest(u);
            ApplyStyle();
        }

        Rect walls = ScreenBounds.WorldRect;
        if (InRoom) walls = Room; // (inside a window: just its room)
        else
        {
            if (RoomLeft > 0f)
            {
                if (!HoldRoom) RoomLeft = Mathf.Max(0f, Mathf.Min(RoomLeft, walls.xMin - Extent.xMin + 0.001f)); // follows it back in
                walls.xMin -= RoomLeft;
            }
            if (RoomRight > 0f)
            {
                if (!HoldRoom) RoomRight = Mathf.Max(0f, Mathf.Min(RoomRight, Extent.xMax - walls.xMax + 0.001f));
                walls.xMax += RoomRight;
            }
            if (RoomBelow > 0f)
            {
                if (!HoldRoom) RoomBelow = Mathf.Max(0f, Mathf.Min(RoomBelow, walls.yMin - Extent.yMin + 0.001f)); // follows it back up
                walls.yMin -= RoomBelow;
            }
            if (skating && RoomBelow <= 0f && !grabbing) walls.yMin += skateLift; // the board holds it up
            RideWindows();
        }
        Vector2 hookFrom = ropeFrom;
        for (int s = 0; s < steps; s++)
        {
            if (roped) ropeAt = Vector2.MoveTowards(ropeAt, Vector2.Lerp(hookFrom, ropeTo, (s + 1f) / steps), RopeFollow * Dt);
            Step(walls);
        }
        if (roped) ropeFrom = ropeAt;
        UpdateVisuals();
    }

    // ------------------------------------------------------------------ IGrabTarget

    public bool Contains(Vector2 pt)
    {
        if (Hiding && HideRect.Contains(pt)) return false; // behind a window: the click is the window's
        if (Occluded(pt)) return false;
        bool inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            Vector2 a = x[i], b = x[j];
            if ((a.y > pt.y) != (b.y > pt.y) && pt.x < (b.x - a.x) * (pt.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
        }
        if (inside) return true;
        float m = 0.04f * size; // a little forgiveness around the edge
        for (int i = 0; i < n; i++) if ((x[i] - pt).sqrMagnitude < m * m) return true;
        return false;
    }

    public void BeginGrab(Vector2 pt)
    {
        grabbing = true;
        cursor = pt;
        grabStart = pt;
        Taffy = 0f;
        if (heavyAt == null || heavyAt.Length != n) heavyAt = new Vector2[n];
        for (int i = 0; i < n; i++) heavyAt[i] = x[i];
        float r = grabRadius * size / 0.6f;
        int nearest = 0; float best = float.MaxValue; bool any = false;
        for (int i = 0; i < n; i++)
        {
            float d = (x[i] - pt).magnitude;
            grabW[i] = Mathf.Clamp01(1f - d / r);
            grabOff[i] = x[i] - pt;
            any |= grabW[i] > 0f;
            if (d < best) { best = d; nearest = i; }
        }
        if (!any) grabW[nearest] = 1f;
    }

    public void DragTo(Vector2 pt) => cursor = pt;

    public void EndGrab() { grabbing = false; Taffy = 0f; }

    // ------------------------------------------------------------------ a fused monster (BuddyMonster)

    /// <summary>It's a fused monster (BuddyMonster): the others are scared of it, it's not invited to parties or towers.</summary>
    public bool IsMonster { get; set; }
    /// <summary>
    /// Too heavy to carry (a fused monster): held, the rest of it stays where it was and only the bit you grabbed stretches
    /// out of it like taffy. Taffy = how far you've pulled (1 = maxTaffy x size: BuddyMonster pops a friend out there).
    /// </summary>
    public bool Heavy { get; set; }
    public float Taffy { get; private set; }
    [HideInInspector] public float maxTaffy = 1.1f;
    /// <summary>Where you grabbed it (world).</summary>
    public Vector2 GrabPoint => grabStart;
    /// <summary>Where the cursor holding it is (world).</summary>
    public Vector2 HeldAt => cursor;
    Vector2 grabStart;
    Vector2[] heavyAt;

    /// <summary>Is this point inside its outline (just the shape: windows in front don't count)?</summary>
    public bool Covers(Vector2 pt)
    {
        if (x == null) return false;
        bool inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            Vector2 a = x[i], b = x[j];
            if ((a.y > pt.y) != (b.y > pt.y) && pt.x < (b.x - a.x) * (pt.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
        }
        return inside;
    }

    /// <summary>Somewhere else at once, still and in its own shape (a buddy popping out of a monster, the monster
    /// splitting up). Lets go of everything it was stuck to, riding or hanging from.</summary>
    public void Teleport(Vector2 center)
    {
        transform.position = new Vector3(center.x, center.y, transform.position.z);
        if (x == null) return;
        Vector2 c0 = Vector2.zero;
        for (int i = 0; i < n; i++) c0 += x[i];
        c0 /= n;
        Vector2 d = center - c0;
        for (int i = 0; i < n; i++) { x[i] += d; p[i] = x[i]; v[i] = Vector2.zero; vPrev[i] = Vector2.zero; glued[i] = false; anchor[i] += d; }
        Center = center;
        roped = false; skating = false; skateDrive = false; gliding = false; crawling = false;
        IsStuck = false;
        Stretch = 0f;
        Velocity = Vector2.zero;
        UpdateVisuals();
    }

    // ------------------------------------------------------------------ simulation (mirrors the prototype)

    void Step(Rect walls)
    {
        float dt = Dt;
        simTime += dt;
        Vector2 pose = Pose;
        GatherPlatforms();
        GatherBoxes();
        GatherFriends();
        IGummyArena arena = InRoom ? Arena : null;
        if (arena != null)
        {
            if (aHit == null || aHit.Length != n) { aHit = new int[n]; aN = new Vector2[n]; aV = new Vector2[n]; }
            for (int i = 0; i < n; i++) aHit[i] = -1;
            arena.Advance(this, dt);
        }
        float keep = 1f - damping;
        float g = gravity * (crawling ? crawlGravity : 1f); // climbing: it holds on, so gravity pulls less
        Vector2 meanV = Vector2.zero;
        bool rolling = skating && !grabbing, flying = gliding && !grabbing;
        if (flying)
        {
            // on the plane: the whole body towards the velocity it wants, and gravity doesn't pull it down
            noStickUntil = Mathf.Max(noStickUntil, simTime + 0.2f);
            Vector2 mv0 = Vector2.zero;
            for (int i = 0; i < n; i++) mv0 += v[i];
            mv0 /= n;
            Vector2 dv = Vector2.ClampMagnitude(glideV - mv0, glideAccel * dt);
            for (int i = 0; i < n; i++) { v[i] += dv; v[i].y += g * dt; }
        }
        if (rolling)
        {
            noStickUntil = Mathf.Max(noStickUntil, simTime + 0.2f); // on wheels: bumps into things, doesn't stick to them
            if (skateDrive && Grounded)
            {
                // kicks along / brakes: the whole body towards the speed it wants
                float mvx = 0f;
                for (int i = 0; i < n; i++) mvx += v[i].x;
                mvx /= n;
                float dv = Mathf.Clamp(skateVx - mvx, -skateAccel * dt, skateAccel * dt);
                for (int i = 0; i < n; i++) v[i].x += dv;
            }
        }
        bool soak = BallPit.Over(this); // (in the ball pit: the balls hold it up and slow it down)
        for (int i = 0; i < n; i++)
        {
            v[i] *= keep;
            v[i].y -= g * dt;
            if (soak && !grabbing) BallPit.Soak(this, x[i], ref v[i], g, dt);
            if (crawling) Adhere(i, dt);
            vPrev[i] = v[i];
            p[i] = x[i] + v[i] * dt;
            meanV += v[i];
            pen[i] = Vector2.zero;
        }
        meanV /= n;

        bool anyGlued = false;
        for (int i = 0; i < n; i++) anyGlued |= glued[i];
        bool hanging = roped && !grabbing;
        if (hanging)
        {
            if (ropeLen > ropeTarget) ropeLen = Mathf.Max(ropeTarget, ropeLen - reelSpeed * dt); // pulling itself up, smoothly
            noStickUntil = Mathf.Max(noStickUntil, simTime + 0.2f); // doesn't stick to what it brushes past
            // its top turns towards the hook (it hangs from its stem)
            Vector2 cc = Vector2.zero;
            for (int i = 0; i < n; i++) cc += p[i];
            cc /= n;
            Vector2 up = ropeAt - cc;
            if (up.sqrMagnitude > 1e-4f)
            {
                float want = Mathf.Atan2(up.y, up.x) - 0.5f * Mathf.PI, diff = Mathf.DeltaAngle(Angle, want * Mathf.Rad2Deg) * Mathf.Deg2Rad;
                float turn = Mathf.Clamp(diff, -9f * dt, 9f * dt) * Mathf.Clamp01(Mathf.Abs(diff) * 4f);
                float cs0 = Mathf.Cos(turn), sn0 = Mathf.Sin(turn);
                for (int i = 0; i < n; i++)
                {
                    Vector2 r = p[i] - cc;
                    p[i] = cc + new Vector2(cs0 * r.x - sn0 * r.y, sn0 * r.x + cs0 * r.y);
                }
            }
        }

        float kShape = 1f - Mathf.Pow(1f - stiffness, 1f / iterations);
        float kTurn = flying && !float.IsNaN(TurnTo) ? 1f - Mathf.Pow(1f - Mathf.Clamp01(14f * dt), 1f / iterations) : 0f;
        bool brace = Brace && !Tumble && !grabbing && !anyGlued && !crawling && !hanging;
        bool resting = !grabbing && !anyGlued && !hanging && (meanV.sqrMagnitude < 4f || brace);
        bool turning = flying && !float.IsNaN(TurnTo); // (turned on purpose: no wriggling back upright)
        righting = resting && Mathf.Abs(Angle) > 25f && !turning; // lying on its side / upside down: wriggles back upright
        if (turning)
        {
            // turned on purpose (a trick on the slide): it turns itself towards TurnTo, up to about two turns a second
            float diff = (Mathf.Repeat(TurnTo - Angle + 180f, 360f) - 180f) * Mathf.Deg2Rad;
            float turn = Mathf.Clamp(diff, -14f * dt, 14f * dt) * Mathf.Clamp01(Mathf.Abs(diff) * 3f);
            float cs0 = Mathf.Cos(turn), sn0 = Mathf.Sin(turn);
            Vector2 cc = Vector2.zero;
            for (int i = 0; i < n; i++) cc += p[i];
            cc /= n;
            for (int i = 0; i < n; i++)
            {
                Vector2 r = p[i] - cc;
                p[i] = cc + new Vector2(cs0 * r.x - sn0 * r.y, sn0 * r.x + cs0 * r.y);
            }
        }
        if (resting && Mathf.Abs(Angle) > 8f && !turning)
        {
            // turn itself a little each step (even an egg-shaped seed, which would rather lie on its side)
            float turn = -Mathf.Sign(Angle) * Mathf.Min(Mathf.Abs(Angle) * Mathf.Deg2Rad, 2.5f * dt);
            float cs0 = Mathf.Cos(turn), sn0 = Mathf.Sin(turn);
            Vector2 cc = Vector2.zero;
            for (int i = 0; i < n; i++) cc += p[i];
            cc /= n;
            for (int i = 0; i < n; i++)
            {
                Vector2 r = p[i] - cc;
                p[i] = cc + new Vector2(cs0 * r.x - sn0 * r.y, sn0 * r.x + cs0 * r.y);
            }
        }
        float kUpright = resting ? 1f - Mathf.Pow(1f - Mathf.Clamp01(uprightSpeed * (righting ? 4f : 1f) * dt), 1f / iterations) : 0f;
        float areaScale = 1f + swell;
        float qs = Mathf.Sqrt(areaScale);
        float targetArea = A0 * areaScale;
        float loadScale = size / 0.6f;
        bool jiggle = Mathf.Abs(wobA) > 1e-4f;

        Vector2 c = Vector2.zero;
        float theta = 0f;
        for (int it = 0; it < iterations; it++)
        {
            // shape matching: best-fit rotation of the rest shape, pull towards it
            c = Vector2.zero;
            for (int i = 0; i < n; i++) c += p[i];
            c /= n;
            float num = 0f, den = 0f;
            for (int i = 0; i < n; i++)
            {
                Vector2 r = p[i] - c;
                num += q[i].x * r.y - q[i].y * r.x;
                den += q[i].x * r.x + q[i].y * r.y;
            }
            theta = Mathf.Atan2(num, den);
            float th = theta * (1f - kUpright) + (rolling || flying ? Mathf.Clamp(Lean, -7f, 7f) * Mathf.Deg2Rad * kUpright : 0f);
            if (kTurn > 0f) th = theta + (Mathf.Repeat(TurnTo - theta * Mathf.Rad2Deg + 180f, 360f) - 180f) * Mathf.Deg2Rad * kTurn;
            float cs = Mathf.Cos(th), sn = Mathf.Sin(th);
            for (int i = 0; i < n; i++)
            {
                float qx = q[i].x * pose.x, qy = q[i].y * pose.y;
                Vector2 goal = c + qs * new Vector2(cs * qx - sn * qy, sn * qx + cs * qy);
                if (jiggle) goal = Jiggle(goal, c);
                p[i] += (goal - p[i]) * kShape;
            }

            // edge lengths (Jacobi)
            for (int i = 0; i < n; i++)
            {
                Vector2 d = p[(i + 1) % n] - p[i];
                float len = d.magnitude + 1e-9f;
                corr[i] = d * ((len - L0[i] * qs) / len * 0.5f * edgeStiffness);
            }
            for (int i = 0; i < n; i++) { p[i] += corr[i]; p[(i + 1) % n] -= corr[i]; }

            // area (volume) preservation along the outline normals
            float area = SignedArea(p), per = 0f;
            for (int i = 0; i < n; i++)
            {
                Vector2 t = p[(i + 1) % n] - p[(i + n - 1) % n];
                nrm[i] = new Vector2(t.y, -t.x).normalized; // outward for a CCW ring
                per += (p[(i + 1) % n] - p[i]).magnitude;
            }
            float push = (targetArea - area) / Mathf.Max(per, 1e-6f) * areaStiffness;
            for (int i = 0; i < n; i++) p[i] += nrm[i] * push;

            // grab: the region under the cursor follows it
            if (grabbing)
                for (int i = 0; i < n; i++)
                    if (grabW[i] > 0f) p[i] += (cursor + grabOff[i] - p[i]) * (grabW[i] * grabStiffness);
            // too heavy to carry: the rest stays where it was (the grabbed bit stretches out like taffy)
            if (grabbing && Heavy && heavyAt != null)
                for (int i = 0; i < n; i++)
                    p[i] += (heavyAt[i] - p[i]) * ((1f - grabW[i]) * 0.35f);

            // glue: stuck points stay on their anchors (remember how hard they were pulled)
            if (anyGlued)
                for (int i = 0; i < n; i++)
                    if (glued[i]) { load[i] = (p[i] - anchor[i]).magnitude; p[i] = anchor[i]; }

            // rope: its middle no further from the hook than the rope is long (the whole body moves, like a pendulum)
            if (hanging)
            {
                Vector2 rc = Vector2.zero;
                for (int i = 0; i < n; i++) rc += p[i];
                rc /= n;
                Vector2 d = rc - ropeAt;
                float dist = d.magnitude;
                if (dist > ropeLen)
                {
                    Vector2 shift = d * ((ropeLen - dist) / dist);
                    for (int i = 0; i < n; i++) p[i] += shift;
                }
            }

            // screen edges
            for (int i = 0; i < n; i++)
            {
                float cx = Mathf.Clamp(p[i].x, walls.xMin, walls.xMax), cy = Mathf.Clamp(p[i].y, walls.yMin, walls.yMax);
                pen[i].x += Mathf.Abs(cx - p[i].x); pen[i].y += Mathf.Abs(cy - p[i].y);
                p[i].x = cx; p[i].y = cy;
            }

            // window tops: one-sided, only catches points coming from above
            for (int k = 0; k < nearCount; k++)
            {
                Platform pl = near[k];
                for (int i = 0; i < n; i++)
                    if (p[i].x >= pl.x0 && p[i].x <= pl.x1 && p[i].y < pl.y && x[i].y >= pl.y - 1e-4f) { pen[i].y += pl.y - p[i].y; p[i].y = pl.y; }
            }

            // window sides and bottoms
            for (int k = 0; k < boxCount; k++) PushOut(k);

            // other buddies: squish against them
            for (int k = 0; k < friendCount; k++) PushOutOf(friends[k], walls);

            // the arena's own shapes (pinball: flippers, rails, bumpers)
            if (arena != null)
                for (int i = 0; i < n; i++)
                {
                    int sh = arena.Push(ref p[i], x[i], out Vector2 an, out Vector2 av);
                    if (sh >= 0) { aHit[i] = sh; aN[i] = an; aV[i] = av; }
                }
        }
        Center = c;
        Angle = theta * Mathf.Rad2Deg;

        // velocities + contacts (friction, a little bounce, sticking on impact)
        float inv = 1f / dt;
        Hit stuck = default, bumped = default, arenaHit = default;
        bool onScreenFloor = false; // (another screen's floor, higher up: the floor, not a window)
        float screenFloorY = 0f;
        for (int i = 0; i < n; i++)
        {
            Vector2 vn = (p[i] - x[i]) * inv;
            Contact(i, ref vn, 0, walls.xMin, -1f, 0, ref stuck, ref bumped);
            Contact(i, ref vn, 0, walls.xMax, 1f, 0, ref stuck, ref bumped);
            Contact(i, ref vn, 1, walls.yMin, -1f, 0, ref stuck, ref bumped);
            Contact(i, ref vn, 1, walls.yMax, 1f, 0, ref stuck, ref bumped);
            onPlat[i] = 0;
            for (int k = 0; k < nearCount; k++)
            {
                Platform pl = near[k];
                if (DesktopPlatforms.IsScreen(pl.id))
                {
                    if (p[i].x >= pl.x0 && p[i].x <= pl.x1 && Contact(i, ref vn, 1, pl.y, -1f, 0, ref stuck, ref bumped)) { onScreenFloor = true; screenFloorY = pl.y; }
                    continue;
                }
                if (p[i].x >= pl.x0 && p[i].x <= pl.x1 && Contact(i, ref vn, 1, pl.y, -1f, pl.id, ref stuck, ref bumped))
                { onPlat[i] = pl.id; SupportSpan = new Vector2(pl.x0, pl.x1); }
            }
            for (int k = 0; k < boxCount; k++) BoxContact(k, i, ref vn, ref stuck, ref bumped);
            if (crawling) Feet(i, ref vn);
            if (arena != null && aHit[i] >= 0) ArenaContact(arena, i, ref vn, ref arenaHit);
            v[i] = vn;
            x[i] = p[i];
        }
        if (arena != null && aSpeed[0] >= 0f) ArenaTouched(arena);

        // bracing (between its own hops): the landing doesn't turn into a roll
        if (brace)
        {
            Vector2 mc = Vector2.zero, mvel = Vector2.zero;
            for (int i = 0; i < n; i++) { mc += x[i]; mvel += v[i]; }
            mc /= n; mvel /= n;
            float L = 0f, I = 1e-9f;
            for (int i = 0; i < n; i++)
            {
                Vector2 r = x[i] - mc, dv = v[i] - mvel;
                L += r.x * dv.y - r.y * dv.x;
                I += r.sqrMagnitude;
            }
            float w = 0.5f * L / I;
            for (int i = 0; i < n; i++) { Vector2 r = x[i] - mc; v[i] -= w * new Vector2(-r.y, r.x); }
        }

        // on the rope: it swings, it doesn't twirl (its top keeps facing the hook)
        if (hanging)
        {
            Vector2 mc = Vector2.zero, mvel = Vector2.zero;
            for (int i = 0; i < n; i++) { mc += x[i]; mvel += v[i]; }
            mc /= n; mvel /= n;
            float L = 0f, I = 1e-9f;
            for (int i = 0; i < n; i++)
            {
                Vector2 r = x[i] - mc, dv = v[i] - mvel;
                L += r.x * dv.y - r.y * dv.x;
                I += r.sqrMagnitude;
            }
            float w = 0.8f * L / I;
            for (int i = 0; i < n; i++) { Vector2 r = x[i] - mc; v[i] -= w * new Vector2(-r.y, r.x); }
        }

        // wriggling upright slips on the floor: turn in place rather than slide away
        if (righting)
        {
            float mx = 0f;
            for (int i = 0; i < n; i++) mx += v[i].x;
            mx /= n;
            for (int i = 0; i < n; i++) v[i].x -= mx * 0.5f;
        }

        // jelly wobble: squash along the hit, spring back
        float hitSpeed = Mathf.Max(Mathf.Max(stuck.speed, bumped.speed), arenaHit.speed);
        if (hitSpeed > 2f && !grabbing)
        {
            float w = 2f * Mathf.PI * wobbleFrequency;
            float kick = -Mathf.Min(hitSpeed * impactWobble, 0.3f) * w;
            if (kick < wobV) { wobV = kick; wobN = (stuck.count > 0 ? stuck.normal : bumped.count > 0 && bumped.speed >= arenaHit.speed ? bumped.normal : arenaHit.normal).normalized; }
        }
        if (stuck.count > 0) { LastHitSurface = stuck.surface; Splat?.Invoke(stuck.sum / stuck.count, stuck.normal.normalized, stuck.speed); }
        else if (bumped.count > 0) { LastHitSurface = bumped.surface; Bump?.Invoke(bumped.sum / bumped.count, bumped.normal.normalized, bumped.speed); }

        Taffy = grabbing && Heavy ? (cursor - grabStart).magnitude / Mathf.Max(maxTaffy * size, 1e-3f) : 0f;

        // pulled too far from where it's stuck: all the glue pops at once
        Stretch = 0f;
        if (grabbing && anyGlued)
        {
            Vector2 a = Vector2.zero; int k = 0;
            for (int i = 0; i < n; i++) if (glued[i]) { a += anchor[i]; k++; }
            a /= Mathf.Max(k, 1);
            float pull = (cursor - a).magnitude;
            Stretch = Mathf.Clamp01(pull / (maxStretch * size));
            if (pull > maxStretch * size)
            {
                for (int i = 0; i < n; i++) glued[i] = false;
                Stretch = 0f;
                Pop?.Invoke(a, (cursor - a).normalized);
            }
        }

        Peel(dt, loadScale);
        Engulf(walls);

        // state for behaviour scripts (GummyBrain)
        bool onFloor = false, onWindow = false;
        Vector2 mv = Vector2.zero, bMin = x[0], bMax = x[0];
        SupportWindow = 0;
        for (int i = 0; i < n; i++)
        {
            onFloor |= x[i].y <= walls.yMin + 1e-5f; onWindow |= onPlat[i] != 0; mv += v[i];
            if (onPlat[i] != 0) SupportWindow = onPlat[i];
            bMin = Vector2.Min(bMin, x[i]); bMax = Vector2.Max(bMax, x[i]);
        }
        Extent = Rect.MinMaxRect(bMin.x, bMin.y, bMax.x, bMax.y);
        if (roped)
        {
            Vector2 mc = Vector2.zero;
            for (int i = 0; i < n; i++) mc += x[i];
            mc /= n;
            Vector2 toHook = ropeAt - mc;
            float dist = toHook.magnitude, best = float.MinValue;
            RopeStretch = dist - ropeLen;
            Vector2 dirH = dist > 1e-5f ? toHook / dist : Vector2.up;
            for (int i = 0; i < n; i++) { float k = Vector2.Dot(x[i] - mc, dirH); if (k > best) { best = k; RopePoint = x[i]; } }
        }
        onFloor |= onScreenFloor;
        if (onFloor && !onWindow) SupportSpan = ScreenBounds.Multi ? ScreenBounds.FloorSpanAt(Center, onScreenFloor ? screenFloorY : ScreenBounds.WorldRect.yMin) : new Vector2(walls.xMin, walls.xMax);
        OnPlatform = onWindow && !onFloor;
        Grounded = (onFloor || onWindow || onFriend) && !grabbing;
        Velocity = mv / n;

        // wobble spring
        if (jiggle || wobV != 0f)
        {
            float w = 2f * Mathf.PI * wobbleFrequency;
            wobV += (-w * w * wobA - 2f * wobbleDamping * w * wobV) * dt;
            wobA = Mathf.Clamp(wobA + wobV * dt, -0.35f, 0.35f);
            if (Mathf.Abs(wobA) < 1e-4f && Mathf.Abs(wobV) < 1e-3f) { wobA = 0f; wobV = 0f; }
        }
    }

    Vector2 Jiggle(Vector2 goal, Vector2 c)
    {
        Vector2 d = goal - c;
        float dn = d.x * wobN.x + d.y * wobN.y;
        Vector2 along = d - wobN * dn;
        return c + wobN * (dn * (1f - wobA)) + along * (1f + 0.5f * wobA);
    }

    /// <summary>
    /// Glue peels like tape from the ends of the stuck patch. On a wall the top end lets go first, so it ends up hanging
    /// from the bottom before it drops; on the ceiling the middle holds on last, like a drip. Being pulled speeds it up.
    /// </summary>
    void Peel(float dt, float loadScale)
    {
        IsStuck = false;
        int gc = 0;
        Vector2 gs = Vector2.zero, gn = Vector2.zero;
        GluedWindow = 0;
        for (int i = 0; i < n; i++) if (glued[i]) { gc++; gs += anchor[i]; gn += gNrm[i]; if (gSurf[i] != 0) GluedWindow = gSurf[i]; }
        if (gc == 0 && GluedCount > 0 && !grabbing) noStickUntil = Mathf.Max(noStickUntil, simTime + 0.5f); // let go: drops cleanly
        GluedCount = gc;
        if (gc == 0) { patchAge = 0f; patchHold = 0f; return; }
        Vector2 center = gs / gc;
        GlueCenter = center;
        GlueNormal = gn.normalized;
        patchAge += dt;
        float frac = Mathf.Clamp01(patchAge / Mathf.Max(patchHold, 0.2f));
        float tear = tearLoad * loadScale;
        for (int i = 0; i < n; i++)
        {
            if (!glued[i]) continue;
            bool front = !glued[(i + n - 1) % n] || !glued[(i + 1) % n];
            bool wall = Mathf.Abs(gNrm[i].x) > 0.5f;
            float rate = front ? 1f : 0.2f;
            if (wall && front) rate *= anchor[i].y > center.y ? 1.8f : 0.6f;
            if (gNrm[i].y < -0.5f && gc <= 3) rate *= 0.5f; // on the ceiling the last bit hangs on: it dangles like a drip
            rate *= 1f + 2f * load[i] / tear;
            life[i] -= dt * rate;
            if (wall) anchor[i].y -= slideSpeed * frac * dt; // the patch slides down together, faster as it lets go
            if (life[i] <= 0f || load[i] > tear) glued[i] = false;
            else IsStuck = true;
        }
    }

    struct Hit
    {
        public Vector2 sum, normal;
        public int count;
        public float speed;
        public long surface;
        public void Add(Vector2 pt, Vector2 nrm, float s, long surf)
        {
            sum += pt; normal += nrm; count++;
            if (s > speed) { speed = s; surface = surf; }
        }
    }

    // ------------------------------------------------------------------ the arena's shapes (IGummyArena: pinball)

    int[] aHit;                                  // per point: the arena shape it touched this step (-1 = none)
    Vector2[] aN, aV;                            // ... that shape's normal there, and its surface velocity
    readonly int[] aShape = new int[6];          // the shapes touched this step (strongest hit each)
    readonly Vector2[] aAt = new Vector2[6], aNrm = new Vector2[6];
    readonly float[] aSpeed = { -1f, -1f, -1f, -1f, -1f, -1f }; // (-1 = a free slot)

    // bounce off a shape (relative to its surface, which may be moving: a flipper), with Coulomb friction
    void ArenaContact(IGummyArena arena, int i, ref Vector2 vel, ref Hit hit)
    {
        int sh = aHit[i];
        arena.Surface(sh, out float bounce, out float grip);
        Vector2 nn = aN[i], sv = aV[i];
        float into = -Vector2.Dot(vPrev[i] - sv, nn); // speed into it before this step
        Vector2 rel = vel - sv;
        float vnn = Vector2.Dot(rel, nn);
        float want = into > 0f ? into * bounce : 0f;
        if (vnn < want) vnn = want;
        Vector2 t = rel - nn * Vector2.Dot(rel, nn);
        float ts = t.magnitude, cap = grip * Mathf.Max(into, 0f);
        if (ts > 1e-6f) t *= ts <= cap ? 0f : (ts - cap) / ts;
        vel = sv + nn * vnn + t;
        if (into > bumpSpeed) hit.Add(p[i], nn, into, 0);
        // remember the strongest hit on each shape (Touched after the step)
        int k = 0;
        while (k < aShape.Length && aSpeed[k] >= 0f && aShape[k] != sh) k++;
        if (k == aShape.Length) return;
        if (aSpeed[k] < 0f || into > aSpeed[k]) { aShape[k] = sh; aAt[k] = p[i]; aNrm[k] = nn; aSpeed[k] = Mathf.Max(into, 0f); }
    }

    void ArenaTouched(IGummyArena arena)
    {
        for (int k = 0; k < aShape.Length; k++)
        {
            if (aSpeed[k] < 0f) break;
            arena.Touched(this, aShape[k], aAt[k], aNrm[k], aSpeed[k]);
        }
        for (int k = 0; k < aShape.Length; k++) aSpeed[k] = -1f;
    }

    bool Contact(int i, ref Vector2 vel, int axis, float wall, float sign, long surface, ref Hit stuck, ref Hit bumped)
    {
        if (Mathf.Abs(p[i][axis] - wall) > 1e-6f) return false;
        float into = vPrev[i][axis] * sign; // speed towards the wall before this step
        int tan = 1 - axis;
        if (vel[axis] * sign > 0f) vel[axis] = -vel[axis] * restitution;
        bool feet = crawling && axis == crawlEdge.axis && Mathf.Abs(wall - crawlEdge.pos) < 1e-4f; // climbing: it walks here
        bool wheels = skating && !grabbing && axis == 1 && sign < 0f; // on its board: the wheels roll
        if (wheels) vel[tan] *= 1f - 0.15f * Dt; // (a little rolling resistance)
        if (!feet && !wheels && !(righting && axis == 1) && simTime >= noGripUntil)
        {
            // Coulomb friction: grips as hard as it's pressed into the edge (its weight on the floor, the impact on a wall),
            // so it rolls, slides and spins off walls
            float press = Mathf.Max(into, pen[i][axis] / Dt);
            float cap = friction * press * (1f + restitution), vt = vel[tan];
            vt = Mathf.Abs(vt) <= cap * 1.3f ? 0f : vt - Mathf.Sign(vt) * cap;
            if (Mathf.Abs(vt) < 1f) vt *= 1f - friction * 0.6f; // slow creep stops (hops land firmly, resting stays put)
            vel[tan] = vt;
        }
        Vector2 normal = axis == 0 ? new Vector2(-sign, 0f) : new Vector2(0f, -sign);
        float skid = Mathf.Abs(vPrev[i][tan]);
        if (into > stickSpeed && skid >= glancing * into) noStickUntil = Mathf.Max(noStickUntil, simTime + 0.25f); // glancing: skids off
        if (sticky && !glued[i] && !grabbing && !crawling && into > stickSpeed && simTime >= noStickUntil)
        {
            glued[i] = true;
            anchor[i] = p[i];
            gNrm[i] = normal;
            gSurf[i] = surface;
            float hold = Mathf.Min(holdTime + holdPerSpeed * into, maxHold) * UnityEngine.Random.Range(0.75f, 1.25f);
            life[i] = life0[i] = Mathf.Max(hold * 0.4f, 0.15f);
            if (hold > patchHold) patchHold = hold;
            stuck.Add(p[i], normal, into, surface);
        }
        else if (!glued[i] && into > bumpSpeed)
        {
            bumped.Add(p[i], normal, into, surface);
        }
        return true;
    }

    // ------------------------------------------------------------------ window tops

    void GatherPlatforms()
    {
        nearCount = 0;
        if (!usePlatforms || InRoom) return;
        var all = DesktopPlatforms.Current;
        if (all.Count == 0) return;
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            minX = Mathf.Min(minX, x[i].x); maxX = Mathf.Max(maxX, x[i].x);
            minY = Mathf.Min(minY, x[i].y); maxY = Mathf.Max(maxY, x[i].y);
        }
        float m = size;
        for (int k = 0; k < all.Count && nearCount < near.Length; k++)
        {
            Platform pl = all[k];
            if (pl.x1 < minX - m || pl.x0 > maxX + m || pl.y < minY - m || pl.y > maxY + m) continue;
            if (skating && !grabbing) pl.y += skateLift; // on its board: rides on the window top
            near[nearCount++] = pl;
        }
    }

    /// <summary>When a window it's standing on (or stuck to) moves, move with it.</summary>
    void RideWindows()
    {
        if (!usePlatforms || DesktopPlatforms.Version == platVersion) return;
        platVersion = DesktopPlatforms.Version;
        var all = DesktopPlatforms.Current;
        for (int k = 0; k < all.Count; k++)
        {
            Platform pl = all[k];
            if (pl.dx == 0f && pl.dy == 0f) continue;
            bool riding = false;
            for (int i = 0; i < n && !riding; i++) riding = onPlat[i] == pl.id;
            if (!riding || grabbing) continue;
            var d = new Vector2(pl.dx, pl.dy); // the whole body moves along, so it keeps its shape
            for (int i = 0; i < n; i++)
            {
                if (glued[i] && onPlat[i] != pl.id) continue; // still stuck somewhere else
                x[i] += d;
                if (glued[i]) anchor[i] += d;
            }
        }
    }

    // ------------------------------------------------------------------ solid windows

    void GatherBoxes()
    {
        boxCount = 0;
        if (InRoom || !usePlatforms || (!solidWindows && !ScreenBounds.Multi)) return; // (the bits of the world on no screen: always solid)
        var all = DesktopPlatforms.Windows;
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            minX = Mathf.Min(minX, x[i].x); maxX = Mathf.Max(maxX, x[i].x);
            minY = Mathf.Min(minY, x[i].y); maxY = Mathf.Max(maxY, x[i].y);
        }
        float m = size;
        if (boxLast.Count > 64) boxLast.Clear();
        for (int k = 0; k < all.Count; k++)
        {
            WindowBox w = all[k];
            if (!w.movable) continue; // maximized / top off-screen: never solid
            if (!solidWindows && !w.screen) continue;
            Vector2 cur = new Vector2(w.xMin, w.yMin);
            Vector2 shift = boxLast.TryGetValue(w.id, out Vector2 last) ? cur - last : Vector2.zero;
            boxLast[w.id] = cur;
            if (shift.sqrMagnitude > 0f)
            {
                for (int i = 0; i < n; i++) if (glued[i] && gSurf[i] == w.id && Mathf.Abs(gNrm[i].x) > 0.5f) anchor[i] += shift; // stuck to its side
                if (crawling && crawlEdge.surface == w.id) { crawlEdge.pos += shift[crawlEdge.axis]; crawlEdge.from += shift[1 - crawlEdge.axis]; crawlEdge.to += shift[1 - crawlEdge.axis]; }
            }
            if (ghost.Contains(w.id) || w.id == HideWindow) continue;
            if (w.xMax < minX - m || w.xMin > maxX + m || w.yMax < minY - m || w.yMin > maxY + m) continue;
            if (boxCount < boxes.Length) boxes[boxCount++] = new Box { b = w, shift = shift };
        }

        // a window it was stuck to (or climbing) closed or got minimized: let go
        if (DesktopPlatforms.Version != glueCheckVersion)
        {
            glueCheckVersion = DesktopPlatforms.Version;
            for (int i = 0; i < n; i++)
                if (glued[i] && gSurf[i] != 0 && !Exists(gSurf[i])) glued[i] = false;
            if (crawling && crawlEdge.surface != 0 && !Exists(crawlEdge.surface)) crawling = false;
        }
    }

    static bool Exists(long id)
    {
        var all = DesktopPlatforms.Windows;
        for (int k = 0; k < all.Count; k++) if (all[k].id == id) return all[k].movable;
        var pls = DesktopPlatforms.Current;
        for (int k = 0; k < pls.Count; k++) if (pls[k].id == id) return true;
        return false;
    }

    /// <summary>Keep points out of a window: pushed back out of the side (or bottom) they came in through.</summary>
    void PushOut(int k)
    {
        WindowBox b = boxes[k].b;
        Vector2 sh = boxes[k].shift;
        for (int i = 0; i < n; i++)
        {
            Vector2 pt = p[i];
            if (pt.x <= b.xMin || pt.x >= b.xMax || pt.y <= b.yMin || pt.y >= b.yMax) continue;
            Vector2 was = x[i] + sh;                  // where it was, as seen from where the window is now
            if (was.y >= b.yMax - 1e-4f) continue;     // from above: the window top catches it
            if (was.x <= b.xMin + 1e-4f) { pen[i].x += pt.x - b.xMin; p[i].x = b.xMin; }
            else if (was.x >= b.xMax - 1e-4f) { pen[i].x += b.xMax - pt.x; p[i].x = b.xMax; }
            else if (was.y <= b.yMin + 1e-4f) { pen[i].y += pt.y - b.yMin; p[i].y = b.yMin; }
            // else it was already inside (the window opened on top of it): Engulf gets it out
        }
    }

    void BoxContact(int k, int i, ref Vector2 vn, ref Hit stuck, ref Hit bumped)
    {
        WindowBox b = boxes[k].b;
        Vector2 pt = p[i];
        long id = b.screen ? 0 : b.id; // (the edge of a screen, like the walls)
        bool inY = pt.y > b.yMin - 1e-5f && pt.y < b.yMax + 1e-5f, inX = pt.x > b.xMin - 1e-5f && pt.x < b.xMax + 1e-5f;
        if (inY && Contact(i, ref vn, 0, b.xMin, 1f, id, ref stuck, ref bumped)) CapPush(ref vn, 0, 1f, boxes[k].shift);
        else if (inY && Contact(i, ref vn, 0, b.xMax, -1f, id, ref stuck, ref bumped)) CapPush(ref vn, 0, -1f, boxes[k].shift);
        else if (inX && Contact(i, ref vn, 1, b.yMin, 1f, id, ref stuck, ref bumped)) CapPush(ref vn, 1, 1f, boxes[k].shift);
    }

    // a window moving into it pushes it along instead of flinging it
    static void CapPush(ref Vector2 vn, int axis, float sign, Vector2 shift)
    {
        float cap = 2.5f + Mathf.Abs(shift[axis]) * 40f, away = -vn[axis] * sign;
        if (away > cap) vn[axis] = -sign * cap;
    }

    /// <summary>A window opened (or was dragged) on top of it: squeeze out of the nearest side, preferably the top.</summary>
    void Engulf(Rect walls)
    {
        for (int k = 0; k < boxCount; k++)
        {
            WindowBox b = boxes[k].b;
            int inside = 0;
            for (int i = 0; i < n; i++)
                if (x[i].x > b.xMin + 1e-4f && x[i].x < b.xMax - 1e-4f && x[i].y > b.yMin + 1e-4f && x[i].y < b.yMax - 1e-4f) inside++;
            if (inside * 3 <= n) continue;
            ghost.Add(b.id); // lets it pass through this window until it's out
            Escape(b, walls);
        }
        if (ghost.Count == 0) return;
        scratch.Clear();
        foreach (long id in ghost)
        {
            bool still = false;
            var all = DesktopPlatforms.Windows;
            for (int k = 0; k < all.Count && !still; k++)
            {
                if (all[k].id != id) continue;
                WindowBox b = all[k];
                for (int i = 0; i < n && !still; i++)
                    still = x[i].x > b.xMin && x[i].x < b.xMax && x[i].y > b.yMin && x[i].y < b.yMax;
            }
            if (!still) scratch.Add(id);
        }
        foreach (long id in scratch) ghost.Remove(id);
    }

    void Escape(WindowBox b, Rect walls)
    {
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            minX = Mathf.Min(minX, x[i].x); maxX = Mathf.Max(maxX, x[i].x);
            minY = Mathf.Min(minY, x[i].y); maxY = Mathf.Max(maxY, x[i].y);
        }
        float w = maxX - minX, h = maxY - minY, lam = Drag;
        float up = b.yMax - minY, left = maxX - b.xMin, right = b.xMax - minX, down = maxY - b.yMin;
        float best = float.MaxValue; int exit = -1;
        if (b.yMax + h < walls.yMax && up * 0.7f < best) { best = up * 0.7f; exit = 0; } // popping out on top is the fun one
        if (b.xMin - w > walls.xMin && left < best) { best = left; exit = 1; }
        if (b.xMax + w < walls.xMax && right < best) { best = right; exit = 2; }
        if (b.yMin - h > walls.yMin && down < best) { best = down; exit = 3; }
        Vector2 vel;
        switch (exit)
        {
            case 0: vel = new Vector2(0f, JumpSpeed(up + 0.3f * size, gravity, lam)); break;
            case 1: vel = new Vector2(-(left + 0.15f) * lam / (1f - Mathf.Exp(-lam * 0.3f)), 3f); break;
            case 2: vel = new Vector2((right + 0.15f) * lam / (1f - Mathf.Exp(-lam * 0.3f)), 3f); break;
            case 3: vel = new Vector2(0f, -3f); break;
            default: return; // nowhere to go: it just stays in front of that window
        }
        crawling = false;
        Hop(vel, 0.8f);
        Escaped?.Invoke(b.id, vel);
    }

    /// <summary>Air drag per second (the body damps its velocity every 1/240 s step).</summary>
    public float Drag => -Mathf.Log(1f - Mathf.Clamp(damping, 1e-5f, 0.5f)) / Dt;

    /// <summary>Upward launch speed that peaks h higher, with this body's gravity and air drag.</summary>
    public static float JumpSpeed(float h, float g, float lam)
    {
        float lo = 0f, hi = 80f;
        for (int i = 0; i < 40; i++)
        {
            float mid = 0.5f * (lo + hi);
            float peak = mid / lam - g / (lam * lam) * Mathf.Log(1f + mid * lam / g);
            if (peak < h) lo = mid; else hi = mid;
        }
        return hi;
    }

    // ------------------------------------------------------------------ other buddies

    readonly GummyBody[] friends = new GummyBody[4];
    int friendCount;
    bool onFriend;

    void GatherFriends()
    {
        friendCount = 0;
        onFriend = false;
        SupportBody = null;
        if (!collideBuddies || Ghosting || InRoom) return;
        Rect me = Extent;
        for (int k = 0; k < s_all.Count && friendCount < friends.Length; k++)
        {
            GummyBody f = s_all[k];
            if (f == this || f.x == null || !f.collideBuddies || f.Ghosting || f.InRoom) continue;
            Rect o = f.Extent;
            if (o.xMax < me.xMin - 0.1f || o.xMin > me.xMax + 0.1f || o.yMax < me.yMin - 0.1f || o.yMin > me.yMax + 0.1f) continue;
            friends[friendCount++] = f;
        }
    }

    /// <summary>Points that ended up inside another buddy move (half-way per iteration) to its nearest edge, never out
    /// through the screen edges (pushed down into the floor it goes sideways instead, or the floor would bounce it back hard).</summary>
    void PushOutOf(GummyBody f, Rect walls)
    {
        Vector2[] fx = f.x;
        int m = f.n;
        Rect o = f.Extent;
        for (int i = 0; i < n; i++)
        {
            Vector2 pt = p[i];
            if (pt.x < o.xMin || pt.x > o.xMax || pt.y < o.yMin || pt.y > o.yMax) continue;
            bool inside = false;
            for (int a = 0, b = m - 1; a < m; b = a++)
            {
                Vector2 pa = fx[a], pb = fx[b];
                if ((pa.y > pt.y) != (pb.y > pt.y) && pt.x < (pb.x - pa.x) * (pt.y - pa.y) / (pb.y - pa.y) + pa.x) inside = !inside;
            }
            if (!inside) continue;
            float best = float.MaxValue;
            Vector2 cp = pt;
            for (int a = 0; a < m; a++)
            {
                Vector2 pa = fx[a], ba = fx[(a + 1) % m] - pa;
                float h = Mathf.Clamp01(Vector2.Dot(pt - pa, ba) / Mathf.Max(Vector2.Dot(ba, ba), 1e-9f));
                Vector2 c = pa + ba * h;
                float d = (c - pt).sqrMagnitude;
                if (d < best) { best = d; cp = c; }
            }
            Vector2 push = (cp - pt) * 0.5f;
            if (pt.y + push.y < walls.yMin + 1e-4f && push.y < 0f) push = new Vector2((pt.x < f.Center.x ? -1f : 1f) * push.magnitude, 0f);
            Vector2 q2 = pt + push;
            q2.x = Mathf.Clamp(q2.x, walls.xMin, walls.xMax);
            q2.y = Mathf.Clamp(q2.y, walls.yMin, walls.yMax);
            p[i] = q2;
            if (push.y > 0.7f * push.magnitude) { onFriend = true; SupportBody = f; } // sitting on top of it
        }
    }

    // ------------------------------------------------------------------ climbing

    // pulled towards the edge it climbs (when close enough and where the edge exists)
    void Adhere(int i, float dt)
    {
        Edge e = crawlEdge;
        int tn = 1 - e.axis;
        float d = (x[i][e.axis] - e.pos) * e.sign, t = x[i][tn], reach = 0.45f * size;
        if (d >= reach || t < e.from - 0.02f || t > e.to + 0.02f) return;
        v[i][e.axis] -= e.sign * crawlGrip * dt;
        v[i][tn] = Mathf.Lerp(v[i][tn], crawlSpeed, 0.08f * (1f - Mathf.Max(d, 0f) / reach)); // the near side creeps along
    }

    // the points touching the edge walk along it
    void Feet(int i, ref Vector2 vn)
    {
        Edge e = crawlEdge;
        int tn = 1 - e.axis;
        if (Mathf.Abs(p[i][e.axis] - e.pos) < 1e-5f && p[i][tn] >= e.from && p[i][tn] <= e.to)
            vn[tn] = Mathf.Lerp(vn[tn], crawlSpeed, 0.6f);
    }

    static float SignedArea(Vector2[] r)
    {
        float a = 0f;
        for (int i = 0, m = r.Length; i < m; i++)
        {
            Vector2 u = r[i], w = r[(i + 1) % m];
            a += u.x * w.y - w.x * u.y;
        }
        return 0.5f * a;
    }

    // ------------------------------------------------------------------ rest shape (same apple outline as ProceduralApple)

    void BuildRestShape()
    {
        restUvTo = RingUv(shape);
        restUvFrom = (Vector2[])restUvTo.Clone();
        sizeFrom = sizeTo = size;
        q = new Vector2[n];
        L0 = new float[n];
        ApplyRest(1f);
        top = Mathf.RoundToInt(n * 0.25f) % n; // straight up
    }

    /// <summary>Outline points in apple-uv space (the uv square spans 1.25 x body width), rays from a centre.</summary>
    Vector2[] RingUv(BodyShape s)
    {
        var ring = new Vector2[n];
        Vector2 centerUv = s == BodyShape.Seed ? new Vector2(0f, 0.1f) : s == BodyShape.Tomato ? new Vector2(0f, -0.02f) : s == BodyShape.Bean || s == BodyShape.Burger || s == BodyShape.Dino || s == BodyShape.Duck || s == BodyShape.Monster ? Vector2.zero : new Vector2(0f, -0.07f);
        float maxR = s == BodyShape.Seed ? 1.6f : s == BodyShape.Monster ? 1.4f : 1.2f;
        for (int i = 0; i < n; i++)
        {
            float th = 2f * Mathf.PI * i / n;
            var dir = new Vector2(Mathf.Cos(th), Mathf.Sin(th));
            float lo = 0f, hi = maxR;
            for (int b = 0; b < 40; b++)
            {
                float mid = 0.5f * (lo + hi);
                Vector2 at = centerUv + dir * mid;
                float d = s == BodyShape.Seed ? SeedSdf(at) : s == BodyShape.Tomato ? TomatoSdf(at) : s == BodyShape.Bean ? BeanSdf(at) : s == BodyShape.Burger ? BurgerSdf(at) : s == BodyShape.Dino ? DinoSdf(at) : s == BodyShape.Duck ? DuckSdf(at) : s == BodyShape.Monster ? MonsterSdf(at, monsterSeed) : BodySdf(at);
                if (d < 0f) lo = mid; else hi = mid;
            }
            ring[i] = centerUv + dir * lo;
        }
        return ring;
    }

    /// <summary>Rest shape at morph progress u (0 = from, 1 = to): positions, area and edge lengths.</summary>
    void ApplyRest(float u)
    {
        size = Mathf.Lerp(sizeFrom, sizeTo, u);
        float k = 0.625f * size;
        var rest = new Vector2[n];
        for (int i = 0; i < n; i++) rest[i] = Vector2.Lerp(restUvFrom[i], restUvTo[i], u) * k;
        Vector2 rc = Vector2.zero;
        for (int i = 0; i < n; i++) rc += rest[i];
        rc /= n;
        restCentroidUv = rc;
        for (int i = 0; i < n; i++) q[i] = rest[i] - rc;
        A0 = SignedArea(q);
        for (int i = 0; i < n; i++) L0[i] = (q[(i + 1) % n] - q[i]).magnitude;
    }

    /// <summary>Grow / change shape smoothly over duration seconds (the body inflates physically).</summary>
    public void Morph(BodyShape newShape, float newSize, float duration)
    {
        if (q == null) { shape = newShape; size = newSize; return; } // not built yet: Awake will use these
        float u = morphT * morphT * (3f - 2f * morphT);
        var current = new Vector2[n];
        for (int i = 0; i < n; i++) current[i] = Vector2.Lerp(restUvFrom[i], restUvTo[i], u);
        restUvFrom = current;
        restUvTo = RingUv(newShape);
        sizeFrom = size;
        sizeTo = newSize;
        shape = newShape;
        morphDur = duration;
        morphT = duration <= 0f ? 1f : 0f;
        if (morphT >= 1f) { ApplyRest(1f); if (bodyMat) ApplyStyle(); }
    }

    /// <summary>The size it's growing (or shrinking) to: its size once the Morph is done.</summary>
    public float TargetSize => q == null ? size : sizeTo;

    /// <summary>Toasted (0 = not at all .. 1 = crispy brown): the lava (Lava) sets it and lets it fade.</summary>
    public float Toast
    {
        get => toast;
        set
        {
            value = Mathf.Clamp01(value);
            toast = value;
            if (Mathf.Abs(value - toastShown) < 0.01f && (value > 0f || toastShown == 0f)) return; // (only when it shows)
            toastShown = value;
            if (bodyMat) ApplyStyle();
        }
    }
    float toast, toastShown;
    static readonly Color ToastBody = new Color(0.45f, 0.25f, 0.12f), ToastRim = new Color(0.16f, 0.08f, 0.03f);

    /// <summary>Colours and what grows on top (stem + leaf, a sprout, or nothing).</summary>
    public void SetLook(Color body, Color rim, Topper newTopper)
    {
        color = body;
        rimColor = rim;
        topper = newTopper;
        if (bodyMat) ApplyStyle();
        if (stemRenderer && newTopper != bakedTopper) RebakeTopper();
    }

    /// <summary>Tomato: round, a little flattened, a soft dip on top where the leaves sit.</summary>
    static float TomatoSdf(Vector2 uv)
    {
        float y = (uv.y + 0.02f) * 1.16f;
        float r = Mathf.Sqrt(uv.x * uv.x + y * y), th = Mathf.Atan2(y, uv.x);
        float top = th - 0.5f * Mathf.PI, bottom = th + 0.5f * Mathf.PI;
        return r - 0.8f * (1f - 0.06f * Mathf.Exp(-top * top / 0.08f) - 0.045f * Mathf.Exp(-bottom * bottom / 0.4f));
    }

    /// <summary>
    /// Coffee bean: an upright oval (taller than it looks at rest: the gummy settles), a touch narrower at the top, a
    /// flatter bottom so it stands up straight, and a little notch where the crease starts.
    /// </summary>
    static float BeanSdf(Vector2 uv)
    {
        float rx = 0.70f * (1f - 0.05f * uv.y), ry = uv.y > 0f ? 0.98f : 0.92f, pw = uv.y > 0f ? 2f : 2.4f;
        float ax = Mathf.Abs(uv.x) / rx, ay = Mathf.Abs(uv.y) / ry;
        float d = (Mathf.Pow(Mathf.Pow(ax, pw) + Mathf.Pow(ay, pw), 1f / pw) - 1f) * 0.7f;
        float th = Mathf.Atan2(uv.y, uv.x) - 0.5f * Mathf.PI;
        return d + 0.05f * Mathf.Exp(-th * th / 0.02f);
    }

    /// <summary>
    /// Burger: a wide bun, a domed top and a flatter bottom (BuddyBurger lays the lettuce, cheese and patty across it and
    /// the sesame seeds on top).
    /// </summary>
    static float BurgerSdf(Vector2 uv)
    {
        float rx = 0.88f, ry = uv.y > 0f ? 0.78f : 0.62f, pw = uv.y > 0f ? 2.3f : 3.4f;
        float ax = Mathf.Abs(uv.x) / rx, ay = Mathf.Abs(uv.y) / ry;
        return (Mathf.Pow(Mathf.Pow(ax, pw) + Mathf.Pow(ay, pw), 1f / pw) - 1f) * 0.7f;
    }

    /// <summary>
    /// Dino: a chubby upright gumdrop, a big round head on top, a round belly below and a flatter bottom to stand on
    /// (BuddyDino adds the back spikes, the tail, the little arms, the feet and the belly).
    /// </summary>
    static float DinoSdf(Vector2 uv)
    {
        float rx = 0.77f - 0.03f * uv.y, ry = uv.y > 0f ? 0.94f : 0.80f, pw = uv.y > 0f ? 2.1f : 2.7f;
        float ax = Mathf.Abs(uv.x) / rx, ay = Mathf.Abs(uv.y) / ry;
        return (Mathf.Pow(Mathf.Pow(ax, pw) + Mathf.Pow(ay, pw), 1f / pw) - 1f) * 0.7f;
    }

    /// <summary>
    /// Duck(ling): a round fluffy ball, a bit wider at the bottom than at the top (its head), a soft flat bottom to sit on
    /// (BuddyDuck adds the beak, the tuft, the wings, the feet and the tail).
    /// </summary>
    static float DuckSdf(Vector2 uv)
    {
        float rx = 0.8f - 0.07f * uv.y, ry = uv.y > 0f ? 0.86f : 0.78f, pw = uv.y > 0f ? 2f : 2.5f;
        float ax = Mathf.Abs(uv.x) / rx, ay = Mathf.Abs(uv.y) / ry;
        return (Mathf.Pow(Mathf.Pow(ax, pw) + Mathf.Pow(ay, pw), 1f / pw) - 1f) * 0.7f;
    }

    /// <summary>
    /// Fused monster (MonsterLook): a big squat lump, wider than it is tall, a flat bottom to stomp about on and a lumpy
    /// top (which lumps depends on who's inside: seed). Its width is about 1.2 x size.
    /// </summary>
    static float MonsterSdf(Vector2 uv, int seed)
    {
        float th = Mathf.Atan2(uv.y, uv.x);
        float p1 = (seed % 7) * 0.9f, p2 = (seed % 11) * 0.57f;
        float lump = Mathf.Clamp01(uv.y / 0.6f) * (0.07f * Mathf.Sin(3f * th + p1) + 0.045f * Mathf.Sin(5f * th + p2)); // only up top
        float rx = 0.96f + lump, ry = uv.y > 0f ? 0.82f + lump : 0.70f, pw = uv.y > 0f ? 2.2f : 3.2f;
        float ax = Mathf.Abs(uv.x) / rx, ay = Mathf.Abs(uv.y) / ry;
        return (Mathf.Pow(Mathf.Pow(ax, pw) + Mathf.Pow(ay, pw), 1f / pw) - 1f) * 0.7f;
    }

    /// <summary>Egg / pip: round bottom, pointed top, same width as the apple in uv.</summary>
    static float SeedSdf(Vector2 uv)
    {
        const float ra = 0.8f, rb = 0.25f, r = ra - rb, k = 1.7320508f;
        float px = Mathf.Abs(uv.x), py = uv.y + 0.2f;
        float d;
        if (py < 0f) d = Mathf.Sqrt(px * px + py * py) - r;
        else if (k * (px + r) < py) d = Mathf.Sqrt(px * px + (py - k * r) * (py - k * r));
        else d = Mathf.Sqrt((px + r) * (px + r) + py * py) - 2f * r;
        return d - rb;
    }

    static float BodySdf(Vector2 uv)
    {
        float a = (uv - new Vector2(-0.2f, -0.06f)).magnitude - 0.6f;
        float b = (uv - new Vector2(0.2f, -0.06f)).magnitude - 0.6f;
        float h = Mathf.Clamp01(0.5f + 0.5f * (b - a) / 0.12f);
        return Mathf.Lerp(b, a, h) - 0.12f * h * (1f - h);
    }

    // ------------------------------------------------------------------ visuals

    void BuildVisuals()
    {
        // child 0 = face frame (CuteFace builds under it)
        frame = new GameObject("Frame").transform;
        frame.SetParent(transform, false);
        frame.SetAsFirstSibling();

        if (!shader) shader = Shader.Find("WhipIt/Gummy");
        if (!shader) Debug.LogError("[WhipIt] WhipIt/Gummy shader missing, run WhipIt > Setup.");
        bodyMat = new Material(shader) { name = "Gummy (instance)" };
        shadowMat = new Material(shader) { name = "Gummy Shadow (instance)" };
        bodyMat.SetVectorArray(PtsId, pts);
        shadowMat.SetVectorArray(PtsId, pts);

        quad = new Mesh { name = "GummyQuad" };
        quad.MarkDynamic();
        quad.vertices = quadVerts;
        quad.triangles = new[] { 0, 1, 2, 0, 2, 3 };

        MakeQuadRenderer("Shadow", shadowMat, sortingOrder - 2);
        MakeQuadRenderer("Body", bodyMat, sortingOrder);

        var sgo = new GameObject("Stem");
        sgo.transform.SetParent(transform, false);
        stemRenderer = sgo.AddComponent<SpriteRenderer>();
        stemRenderer.sortingOrder = sortingOrder - 1;
        if (spriteMaterial) stemRenderer.sharedMaterial = spriteMaterial;
        stem = sgo.transform;
        RebakeTopper();

        var face = GetComponent<CuteFace>();
        if (!face) face = gameObject.AddComponent<CuteFace>();
        face.scale = size;
        faceBaseSize = size;
        face.sortingOrder = sortingOrder + 2; // (+1 is free for things over the body, under the face)
        if (spriteMaterial && !face.material) face.material = spriteMaterial;

        ApplyStyle();
        bodyMat.SetVectorArray(ClipsId, occ);
        bodyMat.SetFloat(ClipCountId, occCount);
        shadowMat.SetVectorArray(ClipsId, occ);
        shadowMat.SetFloat(ClipCountId, occCount);
        bodyMat.SetVectorArray(PatchId, patchPos); // (array sizes are fixed by the first set)
        bodyMat.SetVectorArray(PatchColId, patchCol);
        bodyMat.SetVectorArray(PatchRimId, patchRim);
        bodyMat.SetFloat(PatchCountId, patchCount);
        UpdateVisuals();
    }

    // ------------------------------------------------------------------ patches (a fused monster: its friends' colours sewn on)

    public const int MaxPatches = 6;
    readonly Vector4[] patchPos = new Vector4[MaxPatches], patchCol = new Vector4[MaxPatches], patchRim = new Vector4[MaxPatches];
    readonly Vector2[] patchUv = new Vector2[MaxPatches];
    readonly float[] patchR = new float[MaxPatches];
    int patchCount;

    /// <summary>How many patches are sewn on.</summary>
    public int PatchCount => patchCount;

    /// <summary>
    /// Sews patches of other colours onto it (MonsterLook): uv = where (the outline's uv: 1 = 0.625 x size from its
    /// middle), r = how big (uv), with a stitched seam round each. They squash and turn with it. Count 0 = none.
    /// </summary>
    public void SetPatches(int count, Vector2[] uv, float[] r, Color[] col, Color[] rim)
    {
        patchCount = Mathf.Clamp(count, 0, MaxPatches);
        for (int i = 0; i < MaxPatches; i++)
        {
            bool on = i < patchCount;
            patchUv[i] = on ? uv[i] : Vector2.zero;
            patchR[i] = on ? r[i] : 0f;
            patchCol[i] = on ? (Vector4)col[i] : Vector4.zero;
            patchRim[i] = on ? (Vector4)rim[i] : Vector4.zero;
            if (!on) patchPos[i] = Vector4.zero;
        }
        if (!bodyMat) return;
        if (patchCount > 0 && x != null) PlacePatches(Quaternion.Euler(0f, 0f, Angle));
        bodyMat.SetVectorArray(PatchId, patchPos);
        bodyMat.SetVectorArray(PatchColId, patchCol);
        bodyMat.SetVectorArray(PatchRimId, patchRim);
        bodyMat.SetFloat(PatchCountId, patchCount);
    }

    /// <summary>Where patch i is right now: world centre xy, radius z, wobble phase w.</summary>
    public Vector4 PatchNow(int i) => i >= 0 && i < patchCount ? patchPos[i] : Vector4.zero;
    /// <summary>Patch i's colour.</summary>
    public Color PatchColor(int i) => i >= 0 && i < patchCount ? (Color)patchCol[i] : Color.clear;

    void PlacePatches(Quaternion rot)
    {
        float k = 0.625f * size;
        for (int i = 0; i < patchCount; i++)
        {
            Vector2 l = patchUv[i] * k - restCentroidUv;
            Vector2 w = Center + (Vector2)(rot * new Vector2(l.x * Squash.x, l.y * Squash.y));
            patchPos[i] = new Vector4(w.x, w.y, patchR[i] * k * 0.5f * (Squash.x + Squash.y), 1.3f + 2.1f * i);
        }
        bodyMat.SetVectorArray(PatchId, patchPos);
    }

    // ------------------------------------------------------------------ hiding behind things

    SpriteMask hideMask;
    Sprite hideMaskSprite;
    Texture2D hideMaskTex;
    bool maskedSprites;

    /// <summary>Body + shadow are clipped in the shader; the face and topper sprites by a sprite mask over the same rect.</summary>
    void UpdateHideClip()
    {
        bool hide = Hiding;
        Rect r = HideRect;
        Vector4 clip = hide ? new Vector4(r.xMin, r.yMin, r.xMax, r.yMax) : new Vector4(1f, 1f, 0f, 0f);
        bodyMat.SetVector(ClipId, clip);
        shadowMat.SetVector(ClipId, clip);
        if (ExternalOcclusion) return; // (BuddyLayer masks the sprites, the hide rect included)
        if (hide && !hideMask)
        {
            hideMaskTex = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "HideMask" };
            var white = new Color32[16];
            for (int i = 0; i < 16; i++) white[i] = new Color32(255, 255, 255, 255);
            hideMaskTex.SetPixels32(white);
            hideMaskTex.Apply(false, true);
            hideMaskSprite = Sprite.Create(hideMaskTex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4f); // 1 x 1 world unit
            hideMask = new GameObject(name + " Hide Mask").AddComponent<SpriteMask>();
            hideMask.sprite = hideMaskSprite;
            hideMask.isCustomRangeActive = true; // only this buddy's sprites (face, leaves)
            hideMask.backSortingOrder = sortingOrder - 3;
            hideMask.frontSortingOrder = sortingOrder + 6;
        }
        if (hideMask)
        {
            hideMask.enabled = hide;
            if (hide)
            {
                hideMask.transform.position = new Vector3(r.center.x, r.center.y, 0f);
                hideMask.transform.localScale = new Vector3(r.width, r.height, 1f);
            }
        }
        if (hide != maskedSprites)
        {
            maskedSprites = hide;
            foreach (var sr in GetComponentsInChildren<SpriteRenderer>(true))
                sr.maskInteraction = hide ? SpriteMaskInteraction.VisibleOutsideMask : SpriteMaskInteraction.None;
        }
    }

    /// <summary>Its shader (bullet time's afterimages are drawn with it).</summary>
    public Shader GummyShader => bodyMat ? shader : null;

    /// <summary>A flat copy of its outline as it is right now (bullet time's afterimages): puts the outline and the windows
    /// in front of it on this material (made from GummyShader) and returns the box it covers (world units).</summary>
    public Rect PaintGhost(Material m)
    {
        int count = n * Sub;
        m.SetVectorArray(PtsId, pts);
        m.SetFloat(CountId, count);
        m.SetVectorArray(ClipsId, occ);
        m.SetFloat(ClipCountId, occCount);
        Rect r = HideRect;
        m.SetVector(ClipId, Hiding ? new Vector4(r.xMin, r.yMin, r.xMax, r.yMax) : new Vector4(1f, 1f, 0f, 0f));
        m.SetVector(OffsetId, Vector4.zero);
        m.SetFloat(FlatId, 1f);
        Vector2 min = new Vector2(float.MaxValue, float.MaxValue), max = -min;
        for (int i = 0; i < count; i++) { var p0 = new Vector2(pts[i].x, pts[i].y); min = Vector2.Min(min, p0); max = Vector2.Max(max, p0); }
        float pad = 0.03f * size;
        return count > 0 ? Rect.MinMaxRect(min.x - pad, min.y - pad, max.x + pad, max.y + pad) : new Rect(Center.x, Center.y, 0f, 0f);
    }

    void MakeQuadRenderer(string name, Material mat, int order)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = quad;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        mr.sortingOrder = order;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
    }

    void ApplyStyle()
    {
        bodyMat.SetColor(ColorId, toastShown > 0f ? Color.Lerp(color, ToastBody, 0.6f * toastShown) : color);
        bodyMat.SetColor(RimId, toastShown > 0f ? Color.Lerp(rimColor, ToastRim, 0.7f * toastShown) : rimColor);
        bodyMat.SetFloat(ThickId, 0.35f * size);
        bodyMat.SetFloat(SpecId, gloss);
        bodyMat.SetFloat(FlatId, 0f);
        shadowMat.SetColor(ColorId, new Color(0f, 0f, 0f, shadowAlpha));
        shadowMat.SetFloat(FlatId, 1f);
        shadowMat.SetVector(OffsetId, shadowOffset * (size / 0.6f));
    }

    void UpdateVisuals()
    {
        Vector3 root = new Vector3(Center.x, Center.y, transform.position.z);
        transform.SetPositionAndRotation(root, Quaternion.identity);

        // smoothed outline -> shader
        int m = n * Sub;
        Vector2 min = new Vector2(float.MaxValue, float.MaxValue), max = -min;
        int k = 0;
        for (int i = 0; i < n; i++)
        {
            Vector2 p0 = x[(i + n - 1) % n], p1 = x[i], p2 = x[(i + 1) % n], p3 = x[(i + 2) % n];
            for (int s = 0; s < Sub; s++)
            {
                Vector2 pt = CatmullRom(p0, p1, p2, p3, s / (float)Sub);
                pts[k++] = new Vector4(pt.x, pt.y, 0f, 0f);
                min = Vector2.Min(min, pt);
                max = Vector2.Max(max, pt);
            }
        }
        bodyMat.SetVectorArray(PtsId, pts);
        shadowMat.SetVectorArray(PtsId, pts);
        bodyMat.SetFloat(CountId, m);
        shadowMat.SetFloat(CountId, m);
        UpdateHideClip();

        // quad covering the body + shadow + anti-aliasing margin (local to the root)
        Vector2 so = shadowOffset * (size / 0.6f);
        float pad = 0.05f * size;
        min = Vector2.Min(min, min + so) - new Vector2(pad, pad) - Center;
        max = Vector2.Max(max, max + so) + new Vector2(pad, pad) - Center;
        quadVerts[0] = new Vector3(min.x, min.y, 0f);
        quadVerts[1] = new Vector3(min.x, max.y, 0f);
        quadVerts[2] = new Vector3(max.x, max.y, 0f);
        quadVerts[3] = new Vector3(max.x, min.y, 0f);
        quad.vertices = quadVerts;
        quad.bounds = new Bounds(new Vector3((min.x + max.x) * 0.5f, (min.y + max.y) * 0.5f, 0f),
                                 new Vector3(max.x - min.x, max.y - min.y, 0.1f));

        // face rides on the body's frame and squishes a little with it
        float ang = Angle;
        var rot = Quaternion.Euler(0f, 0f, ang);
        Vector2 ex = rot * Vector2.right, ey = rot * Vector2.up;
        float sxx = 0f, syy = 0f, qxx = 0f, qyy = 0f, a0 = 0f, a1 = 0f, b0 = 0f, b1 = 0f;
        for (int i = 0; i < n; i++)
        {
            Vector2 r = x[i] - Center;
            float a = Vector2.Dot(r, ex), b = Vector2.Dot(r, ey);
            sxx += a * a; syy += b * b;
            qxx += q[i].x * q[i].x; qyy += q[i].y * q[i].y;
            a0 = Mathf.Min(a0, a); a1 = Mathf.Max(a1, a); b0 = Mathf.Min(b0, b); b1 = Mathf.Max(b1, b);
        }
        Span = new Vector2(a1 - a0, b1 - b0);
        float sx = Mathf.Clamp(Mathf.Sqrt(sxx / qxx), 0.75f, 1.35f), sy = Mathf.Clamp(Mathf.Sqrt(syy / qyy), 0.75f, 1.35f);
        frame.localPosition = (Vector2)(rot * (-restCentroidUv));
        frame.localRotation = rot;
        float grow = faceBaseSize > 0f ? size / faceBaseSize : 1f; // the face grows with the body
        Squash = new Vector2(Mathf.Lerp(1f, sx, 0.5f), Mathf.Lerp(1f, sy, 0.5f));
        frame.localScale = new Vector3(Squash.x * grow, Squash.y * grow, 1f);
        if (patchCount > 0) PlacePatches(rot);

        // stem + leaf (or sprout) sit on the top of the outline
        float baseDepth = topper == Topper.Sprout ? -0.10f : topper == Topper.Calyx ? -0.06f : topper == Topper.Crease ? -0.02f : -0.15f;
        Vector2 stemBase = new Vector2(0f, baseDepth) * (0.625f * size); // base sits a bit inside the top
        stem.localPosition = (Vector2)(x[top] - Center + (Vector2)(rot * stemBase));
        stem.localRotation = rot;
        float ss = stemBakeSize > 0f ? size / stemBakeSize : 1f;
        stem.localScale = new Vector3(ss, ss, 1f);
    }

    static Vector2 CatmullRom(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * ((2f * p1) + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    void RebakeTopper()
    {
        if (stemSprite) Destroy(stemSprite);
        if (stemTex) Destroy(stemTex);
        stemSprite = null; stemTex = null;
        bakedTopper = topper;
        stemBakeSize = size;
        bool onTop = topper == Topper.Calyx || topper == Topper.Crease;
        stemRenderer.sortingOrder = onTop ? sortingOrder + 1 : sortingOrder - 1; // star / crease lie on the body, a stem grows from behind it
        if (topper != Topper.None)
            stemSprite = topper == Topper.Sprout ? BakeSprout(out stemTex) : topper == Topper.Calyx ? BakeCalyx(out stemTex) :
                         topper == Topper.Crease ? BakeCrease(out stemTex) : BakeStem(out stemTex);
        stemRenderer.sprite = stemSprite;
        stemRenderer.enabled = stemSprite && topperVisible;
    }

    Sprite BakeSprout(out Texture2D tex)
    {
        // uv window x -0.45..0.45, y 0.25..1.15: a little green shoot with two round leaves
        const int res = 64;
        const float x0 = -0.45f, y0 = 0.25f, span = 0.9f;
        tex = new Texture2D(res, res, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "GummySprout"
        };
        var px = new Color32[res * res];
        float texel = span / res;
        Color shoot = leafColor * 0.85f; shoot.a = 1f;
        for (int yy = 0; yy < res; yy++)
            for (int xx = 0; xx < res; xx++)
            {
                var uv = new Vector2(x0 + (xx + 0.5f) * texel, y0 + (yy + 0.5f) * texel);
                float dStem = Segment(uv, new Vector2(0f, 0.38f), new Vector2(0f, 0.83f)) - 0.045f;
                Vector2 l = Rotate(uv - new Vector2(-0.2f, 0.88f), 30f), r = Rotate(uv - new Vector2(0.2f, 0.94f), -30f);
                float dL = (new Vector2(l.x / 0.2f, l.y / 0.09f).magnitude - 1f) * 0.09f;
                float dR = (new Vector2(r.x / 0.2f, r.y / 0.09f).magnitude - 1f) * 0.09f;
                float aLeaf = Mathf.Clamp01(0.5f - Mathf.Min(dL, dR) / texel), aStem = Mathf.Clamp01(0.5f - dStem / texel);
                float a = aLeaf + aStem * (1f - aLeaf);
                Color c = Color.Lerp(shoot, leafColor, aLeaf / Mathf.Max(a, 1e-5f));
                c.a = a;
                px[yy * res + xx] = c;
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        var pivot = new Vector2((0f - x0) / span, (0.38f - y0) / span);
        return Sprite.Create(tex, new Rect(0, 0, res, res), pivot, res / (span * 0.625f * size));
    }

    Sprite BakeCalyx(out Texture2D tex)
    {
        // uv window x -0.6..0.6, y -0.05..1.15: a tomato's five-pointed green star lying flat on its top like a little hat
        // (seen from the front, so squashed), darker edges, and a stem nub in the middle
        const int res = 64;
        const float x0 = -0.6f, y0 = -0.05f, span = 1.2f, squash = 0.46f;
        tex = new Texture2D(res, res, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "GummyCalyx"
        };
        var px = new Color32[res * res];
        float texel = span / res;
        var b = new Vector2(0f, 0.40f);
        Color edge = leafColor * 0.62f, stemC = leafColor * 0.8f;
        edge.a = stemC.a = 1f;
        float[] turn = { 8f, 66f, 149f, 212f, 294f };     // five sepals, a little uneven
        float[] len = { 0.46f, 0.52f, 0.50f, 0.53f, 0.48f };
        for (int yy = 0; yy < res; yy++)
            for (int xx = 0; xx < res; xx++)
            {
                var uv = new Vector2(x0 + (xx + 0.5f) * texel, y0 + (yy + 0.5f) * texel);
                Vector2 q = uv - b;
                q.y /= squash; // back to the top view
                float dStar = 1e3f;
                for (int k = 0; k < turn.Length; k++)
                {
                    float r = turn[k] * Mathf.Deg2Rad, c = Mathf.Cos(r), sn = Mathf.Sin(r);
                    var lq = new Vector2(c * q.x + sn * q.y, -sn * q.x + c * q.y); // sepal points along +y
                    dStar = Mathf.Min(dStar, UnevenCapsule(lq, 0.11f, 0.02f, len[k]));
                }
                dStar *= Mathf.Sqrt(squash); // roughly back to screen distance (for the anti-aliasing)
                float dStem = Segment(uv, b, b + new Vector2(0.02f, 0.13f)) - 0.035f;
                float aStar = Mathf.Clamp01(0.5f - dStar / texel), aInner = Mathf.Clamp01(0.5f - (dStar + 0.022f) / texel);
                float aStem = Mathf.Clamp01(0.5f - dStem / texel);
                Color col = Over(Over(Over(new Color(0f, 0f, 0f, 0f), edge, aStar), leafColor, aInner), stemC, aStem);
                px[yy * res + xx] = col;
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        var pivot = new Vector2((0f - x0) / span, (0.40f - y0) / span);
        return Sprite.Create(tex, new Rect(0, 0, res, res), pivot, res / (span * 0.625f * size));
    }

    Sprite BakeCrease(out Texture2D tex)
    {
        // a coffee bean's crease: a soft S-shaped groove from the notch on top down to just above the eyes, drawn
        // as a dark see-through line with a faint light edge, so it works on every roast (green to dark)
        const int res = 64;
        const float x0 = -0.3f, y0 = -0.72f, span = 0.8f, len = 0.66f;
        tex = new Texture2D(res, res, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "GummyCrease"
        };
        var px = new Color32[res * res];
        float texel = span / res;
        var dark = new Color(0.10f, 0.05f, 0.02f, 0.62f);
        var light = new Color(1f, 0.96f, 0.86f, 0.30f);
        const int segs = 16;
        var pts = new Vector2[segs + 1];
        for (int i = 0; i <= segs; i++)
        {
            float t = i / (float)segs;
            pts[i] = new Vector2(0.055f * Mathf.Sin(t * Mathf.PI * 1.6f - 0.3f), -t * len);
        }
        for (int yy = 0; yy < res; yy++)
            for (int xx = 0; xx < res; xx++)
            {
                var uv = new Vector2(x0 + (xx + 0.5f) * texel, y0 + (yy + 0.5f) * texel);
                float dLine = 1e3f, dLight = 1e3f;
                for (int i = 0; i < segs; i++)
                {
                    float t = (i + 0.5f) / segs;
                    float w = 0.012f + 0.024f * Mathf.Sin(t * Mathf.PI); // thin at both ends
                    dLine = Mathf.Min(dLine, Segment(uv, pts[i], pts[i + 1]) - w);
                    dLight = Mathf.Min(dLight, Segment(uv, pts[i] + new Vector2(0.04f, 0f), pts[i + 1] + new Vector2(0.04f, 0f)) - w * 0.45f);
                }
                float aLine = Mathf.Clamp01(0.5f - dLine / texel), aLight = Mathf.Clamp01(0.5f - dLight / texel) * (1f - aLine);
                Color col = Over(Over(new Color(0f, 0f, 0f, 0f), light, aLight * light.a), dark, aLine * dark.a);
                px[yy * res + xx] = col;
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        var pivot = new Vector2((0f - x0) / span, (0f - y0) / span); // the top of the crease
        return Sprite.Create(tex, new Rect(0, 0, res, res), pivot, res / (span * 0.625f * size));
    }

    // straight-alpha "src over dst" with src opacity a
    static Color Over(Color dst, Color src, float a)
    {
        float outA = a + dst.a * (1f - a);
        if (outA <= 1e-5f) return new Color(0f, 0f, 0f, 0f);
        float k = dst.a * (1f - a);
        return new Color((src.r * a + dst.r * k) / outA, (src.g * a + dst.g * k) / outA, (src.b * a + dst.b * k) / outA, outA);
    }

    // distance to a capsule from (0,0) radius r1 to (0,h) radius r2 (iq)
    static float UnevenCapsule(Vector2 p, float r1, float r2, float h)
    {
        p.x = Mathf.Abs(p.x);
        float bb = (r1 - r2) / h, a = Mathf.Sqrt(1f - bb * bb), k = -bb * p.x + a * p.y;
        if (k < 0f) return p.magnitude - r1;
        if (k > a * h) return new Vector2(p.x, p.y - h).magnitude - r2;
        return p.x * a + p.y * bb - r1;
    }

    Sprite BakeStem(out Texture2D tex)
    {
        // uv window x -0.2..0.6, y 0.25..1.05 of the apple drawing (same stem + leaf shapes as ProceduralApple)
        const int res = 64;
        const float x0 = -0.2f, y0 = 0.25f, span = 0.8f;
        tex = new Texture2D(res, res, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "GummyStem"
        };
        var px = new Color32[res * res];
        float texel = span / res;
        for (int yy = 0; yy < res; yy++)
            for (int xx = 0; xx < res; xx++)
            {
                var uv = new Vector2(x0 + (xx + 0.5f) * texel, y0 + (yy + 0.5f) * texel);
                Vector2 lq = Rotate(uv - new Vector2(0.27f, 0.70f), -35f);
                float dLeaf = (new Vector2(lq.x / 0.24f, lq.y / 0.10f).magnitude - 1f) * 0.10f;
                float dStem = Segment(uv, new Vector2(0f, 0.38f), new Vector2(0.07f, 0.78f)) - 0.05f;
                float aLeaf = Mathf.Clamp01(0.5f - dLeaf / texel), aStem = Mathf.Clamp01(0.5f - dStem / texel);
                Color c = leafColor; float a = aLeaf;
                if (aStem > 0f) { c = Color.Lerp(leafColor, stemColor, aStem / Mathf.Max(aStem + aLeaf * (1f - aStem), 1e-5f)); a = aStem + aLeaf * (1f - aStem); }
                c.a = a;
                px[yy * res + xx] = c;
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        var pivot = new Vector2((0f - x0) / span, (0.38f - y0) / span); // stem base
        return Sprite.Create(tex, new Rect(0, 0, res, res), pivot, res / (span * 0.625f * size));
    }

    static float Segment(Vector2 pt, Vector2 a, Vector2 b)
    {
        Vector2 pa = pt - a, ba = b - a;
        float h = Mathf.Clamp01(Vector2.Dot(pa, ba) / Vector2.Dot(ba, ba));
        return (pa - ba * h).magnitude;
    }

    static Vector2 Rotate(Vector2 v2, float deg)
    {
        float r = deg * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r);
        return new Vector2(c * v2.x - s * v2.y, s * v2.x + c * v2.y);
    }
}
