using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A fused monster's look: the buddy the others were squashed into turns into one big lumpy gummy monster built from
/// the parts of everyone inside. Its own colour with its friends' colours sewn on in stitched patches, two eyes for
/// each of them (all sizes, looking every which way, blinking out of step), angry brows, a big fanged grin, and each
/// one's signature bit: the apple's stem as a crooked horn, the tomato's star as a spiky crown, the bean's steam and
/// its crease as a stitched scar (and a zigzag mouth), the burger's filling round its middle and sesame seeds on top,
/// the dino's back spikes, tail, claws and feet (bigger fangs), the duck's bill, tuft, tiny useless wings and webbed
/// feet. The same friends always make the same monster, with the same name. Undo() turns it back.
/// </summary>
[DefaultExecutionOrder(30)] // after the body has moved this frame
[RequireComponent(typeof(GummyBody))]
[DisallowMultipleComponent]
public class MonsterLook : MonoBehaviour
{
    public enum Kind { Apple, Tomato, Bean, Burger, Dino, Duck }

    /// <summary>One buddy inside: what it is and what it looked like when it went in.</summary>
    public struct Member
    {
        public Kind kind;
        public Color color, rim, leaf;
        public float size;

        public static Member Of(GummyBody b)
        {
            var sp = b.GetComponent<BuddySpeech>();
            Kind k = sp ? (Kind)(int)sp.voice
                   : b.shape == GummyBody.BodyShape.Tomato ? Kind.Tomato : b.shape == GummyBody.BodyShape.Bean ? Kind.Bean
                   : b.shape == GummyBody.BodyShape.Burger ? Kind.Burger : b.shape == GummyBody.BodyShape.Dino ? Kind.Dino
                   : b.shape == GummyBody.BodyShape.Duck ? Kind.Duck : Kind.Apple;
            return new Member { kind = k, color = b.color, rim = b.rimColor, leaf = b.leafColor, size = b.size };
        }
    }

    [Tooltip("Sprite material (WhipIt > Setup assigns it; else the body's).")]
    public Material material;

    /// <summary>It's a monster right now.</summary>
    public bool Active { get; private set; }
    /// <summary>Its name (the same friends always make the same one).</summary>
    public string MonsterName { get; private set; }
    /// <summary>Who's inside (0 = this buddy, the one the others were pushed into).</summary>
    public IReadOnlyList<Member> Members => members;
    /// <summary>Which way it faces (-1 left .. 1 right).</summary>
    public float Facing => facing;

    GummyBody body;
    CuteFace face;
    readonly List<Member> members = new List<Member>();
    readonly List<Behaviour> switchedOff = new List<Behaviour>();
    GummyBody.BodyShape oldShape;
    float oldSize;
    Color oldColor, oldRim;
    GummyBody.Topper oldTopper;
    int mask, seed;
    float facing = 1f, wagT;

    // the parts (made once, shown as needed)
    struct Eye { public SpriteRenderer white, pupil, shine; public float u, v, r, nextBlink, blinkT; public Vector2 googly; }
    readonly List<Eye> eyes = new List<Eye>();
    SpriteRenderer mouth, bill, horn, crown, tuft, tail, band, seeds, scar, browL, browR;
    readonly SpriteRenderer[] spikes = new SpriteRenderer[5], wings = new SpriteRenderer[2], arms = new SpriteRenderer[2], feet = new SpriteRenderer[2], puffs = new SpriteRenderer[3];
    readonly List<SpriteRenderer> all = new List<SpriteRenderer>();
    readonly List<Sprite> baked = new List<Sprite>();
    Sprite sMouth, sBill, sHorn, sCrown, sTuft, sTail, sBand, sSeeds, sScar, sBrow, sSpike, sWing, sArm, sPuff;
    readonly Sprite[] sFoot = new Sprite[2];
    readonly bool[] duckFoot = new bool[2];
    bool dinoTail;
    int browA = -1, browB = -1;

    // this frame's frame of reference (At)
    Vector2 cC, cUp, cRight;
    float cW, cH;

    static readonly Color SpikeColor = new Color(1f, 0.72f, 0.22f), DinoBelly = new Color(0.86f, 0.95f, 0.62f);
    static readonly Color Orange = new Color(1f, 0.62f, 0.12f), FootOrange = new Color(1f, 0.55f, 0.1f);
    static readonly Color StemBrown = new Color(0.43f, 0.30f, 0.25f), Pupil = new Color(0.12f, 0.07f, 0.05f), BrowInk = new Color(0.16f, 0.08f, 0.07f);

    void Awake()
    {
        body = GetComponent<GummyBody>();
        face = GetComponent<CuteFace>();
    }

    void OnDisable()
    {
        foreach (var sr in all) if (sr) sr.enabled = false;
    }

    void OnDestroy() => FreeSprites();

    // ------------------------------------------------------------------ becoming one (and back)

    /// <summary>Turns this buddy into the monster made of list (list[0] = this buddy, the one the others went into).</summary>
    public void Become(IList<Member> list)
    {
        if (!body) Awake();
        if (list == null || list.Count < 2) { Undo(); return; }
        if (!Active)
        {
            oldShape = body.shape; oldSize = body.size; oldColor = body.color; oldRim = body.rimColor; oldTopper = body.topper;
            SwitchOffParts();
        }
        members.Clear();
        members.AddRange(list);
        mask = 0;
        foreach (var m in members) mask |= 1 << (int)m.kind;
        seed = mask * 31 + (int)members[0].kind * 7 + members.Count;
        MonsterName = NameFor(members);
        float sum = 0f;
        foreach (var m in members) sum += m.size * m.size;
        body.monsterSeed = seed;
        body.Morph(GummyBody.BodyShape.Monster, Mathf.Sqrt(sum) * 1.12f, Active ? 0.4f : 0.7f);
        body.SetLook(members[0].color, members[0].rim, GummyBody.Topper.None);
        body.TopperVisible = false;
        SewPatches();
        if (face) face.Hidden = true;
        Build();
        Active = true;
    }

    /// <summary>Back to the plain buddy it was.</summary>
    public void Undo()
    {
        if (!Active) return;
        Active = false;
        members.Clear();
        body.SetPatches(0, null, null, null, null);
        body.Morph(oldShape, oldSize, 0.5f);
        body.SetLook(oldColor, oldRim, oldTopper);
        body.TopperVisible = true;
        if (face) face.Hidden = false;
        foreach (var c in switchedOff) if (c) c.enabled = true;
        switchedOff.Clear();
        foreach (var sr in all) if (sr) sr.enabled = false;
        FreeSprites();
    }

    /// <summary>The parts its own kind draws (the dino's spikes, the duck's bill...): the monster draws everyone's now.</summary>
    public IReadOnlyList<Behaviour> SwitchedOff => switchedOff;

    void SwitchOffParts()
    {
        switchedOff.Clear();
        foreach (var c in new Behaviour[] { GetComponent<BuddyDino>(), GetComponent<BuddyDuck>(), GetComponent<BuddyBurger>(), GetComponent<BeanSteam>() })
            if (c && c.enabled) { c.enabled = false; switchedOff.Add(c); }
    }

    bool Has(Kind k) => (mask & (1 << (int)k)) != 0;
    Member First(Kind k) { foreach (var m in members) if (m.kind == k) return m; return members[0]; }
    float PartSize(Kind k) => Mathf.Lerp(First(k).size, body.size, 0.45f); // their parts grow with the monster, not as much

    // their colours sewn on: one patch for each friend inside (the same spots for the same friends)
    static readonly Vector2[] PatchSlots = { new Vector2(-0.56f, 0.30f), new Vector2(0.58f, -0.16f), new Vector2(-0.30f, -0.44f), new Vector2(0.36f, 0.52f), new Vector2(0.70f, 0.26f), new Vector2(-0.74f, -0.10f) };
    static readonly float[] PatchSize = { 0.40f, 0.36f, 0.33f, 0.30f, 0.28f, 0.27f };

    void SewPatches()
    {
        int n = Mathf.Min(members.Count - 1, GummyBody.MaxPatches);
        var rnd = new System.Random(seed);
        var order = new List<int> { 0, 1, 2, 3, 4, 5 };
        for (int i = order.Count - 1; i > 0; i--) { int j = rnd.Next(i + 1); int t = order[i]; order[i] = order[j]; order[j] = t; }
        float flip = rnd.Next(2) == 0 ? 1f : -1f;
        var uv = new Vector2[n]; var r = new float[n]; var col = new Color[n]; var rim = new Color[n];
        for (int i = 0; i < n; i++)
        {
            Vector2 s = PatchSlots[order[i]];
            uv[i] = new Vector2(s.x * flip, s.y);
            r[i] = PatchSize[i] * (0.9f + 0.2f * (float)rnd.NextDouble());
            col[i] = members[i + 1].color;
            rim[i] = members[i + 1].rim;
        }
        body.SetPatches(n, uv, r, col, rim);
    }

    // ------------------------------------------------------------------ its name

    static readonly Dictionary<int, string> Names = new Dictionary<int, string>
    {
        { Bit(Kind.Apple) | Bit(Kind.Tomato), "KETCHAPPLE" }, { Bit(Kind.Apple) | Bit(Kind.Bean), "APPLECCINO" },
        { Bit(Kind.Apple) | Bit(Kind.Burger), "CORE MEAL" }, { Bit(Kind.Apple) | Bit(Kind.Dino), "APPLOSAURUS" },
        { Bit(Kind.Apple) | Bit(Kind.Duck), "QUAPPLE" }, { Bit(Kind.Tomato) | Bit(Kind.Bean), "TOMACCHIATO" },
        { Bit(Kind.Tomato) | Bit(Kind.Burger), "SAUCEQUATCH" }, { Bit(Kind.Tomato) | Bit(Kind.Dino), "TOMATOSAURUS" },
        { Bit(Kind.Tomato) | Bit(Kind.Duck), "TOMALLARD" }, { Bit(Kind.Bean) | Bit(Kind.Burger), "BEANBURGLAR" },
        { Bit(Kind.Bean) | Bit(Kind.Dino), "JAVASAURUS" }, { Bit(Kind.Bean) | Bit(Kind.Duck), "DUCKPRESSO" },
        { Bit(Kind.Burger) | Bit(Kind.Dino), "MEATEOR" }, { Bit(Kind.Burger) | Bit(Kind.Duck), "BIG QUACK" },
        { Bit(Kind.Dino) | Bit(Kind.Duck), "QUACKADACTYL" },
        { Bit(Kind.Apple) | Bit(Kind.Tomato) | Bit(Kind.Dino), "SALADZILLA" },
        { Bit(Kind.Bean) | Bit(Kind.Burger) | Bit(Kind.Dino), "COMBO MEAL" },
        { 63, "GUMMAGEDDON" },
    };
    static readonly string[] Head = { "APPLE", "TOMATO", "BEAN", "BURGER", "DINO", "DUCK" };
    static readonly string[] Tail = { "CRUNCH", "SPLAT", "JITTER", "GRUB", "ZILLA", "DACTYL" };
    static readonly Kind[] Fiercest = { Kind.Dino, Kind.Duck, Kind.Burger, Kind.Tomato, Kind.Bean, Kind.Apple };

    static int Bit(Kind k) => 1 << (int)k;

    /// <summary>The monster's name for these friends (list[0] = the one the others went into).</summary>
    public static string NameFor(IList<Member> list)
    {
        int m = 0;
        foreach (var x in list) m |= Bit(x.kind);
        if (Names.TryGetValue(m, out string nm)) return nm;
        Kind host = list[0].kind, other = host;
        foreach (var k in Fiercest) if (k != host && (m & Bit(k)) != 0) { other = k; break; }
        int count = 0;
        for (int i = 0; i < 6; i++) if ((m & (1 << i)) != 0) count++;
        return (count >= 5 ? "ULTRA " : count == 4 ? "MEGA " : "") + Head[(int)host] + Tail[(int)other];
    }

    // ------------------------------------------------------------------ building the parts

    void Build()
    {
        FreeSprites();
        foreach (var sr in all) if (sr) sr.enabled = false; // (parts of the last monster it was stay hidden unless it has them again)
        int o = body.sortingOrder;
        Material mat = material ? material : body.spriteMaterial;
        var rnd = new System.Random(seed + 17);

        // the mouth: zigzag with a bean inside, big fangs with a dino, a tongue unless it's all teeth
        bool zig = Has(Kind.Bean), fangs = Has(Kind.Dino);
        sMouth = Bake(MouthW, MouthH, new Vector2(0.5f, 0.5f), "Monster Mouth", (x, y, tx) => MouthPaint(x, y, tx, zig, fangs));
        sBrow = Bake(BrowW, BrowH, new Vector2(0.5f, 0.5f), "Monster Brow", BrowPaint);
        mouth = R(ref mouth, "Monster Mouth", o + 2, mat);
        browL = R(ref browL, "Monster Brow", o + 4, mat);
        browR = R(ref browR, "Monster Brow", o + 4, mat);

        // eyes: two for each of them, the biggest for the one the others went into
        foreach (var e in eyes) { if (e.white) e.white.enabled = false; if (e.pupil) e.pupil.enabled = false; if (e.shine) e.shine.enabled = false; }
        LayoutEyes(rnd, mat, o);

        // the apple: its stem as a crooked horn (with its leaf); the tomato: its star as a spiky crown; the duck: its tuft
        if (Has(Kind.Apple)) { Color leaf = First(Kind.Apple).leaf; sHorn = Bake(HornW, HornH, new Vector2(0.5f, 0.05f), "Monster Horn", (x, y, tx) => HornPaint(x, y, tx, StemBrown, leaf)); horn = R(ref horn, "Monster Horn", o - 1, mat); }
        if (Has(Kind.Tomato)) { Color leaf = First(Kind.Tomato).leaf; sCrown = Bake(CrownW, CrownH, new Vector2(0.5f, 0.08f), "Monster Crown", (x, y, tx) => CrownPaint(x, y, tx, leaf)); crown = R(ref crown, "Monster Crown", o - 1, mat); }
        if (Has(Kind.Duck))
        {
            Member d = First(Kind.Duck);
            Color fill = d.color, edge = Color.Lerp(d.color, d.rim, 0.75f);
            sTuft = Bake(48, 48, new Vector2(0.5f, 0.1f), "Monster Tuft", (x, y, tx) => BuddyDuck.TuftPaint(x, y, tx, fill, edge));
            sWing = Bake(48, 48, new Vector2(0.2f, 0.85f), "Monster Wing", (x, y, tx) => BuddyDuck.WingPaint(x, y, tx, fill, edge));
            sBill = Bake(64, 40, new Vector2(0.5f, 0.5f), "Monster Bill", (x, y, tx) => BuddyDuck.BillPaint(x, y, tx, Orange));
            tuft = R(ref tuft, "Monster Tuft", o - 1, mat);
            bill = R(ref bill, "Monster Bill", o + 4, mat);
            for (int i = 0; i < 2; i++) wings[i] = R(ref wings[i], "Monster Wing", o - 1, mat);
        }
        // the dino: back spikes, its tail, little claws
        if (Has(Kind.Dino))
        {
            Member d = First(Kind.Dino);
            var pal = BuddyDino.Palette.Make(d.color, d.rim, DinoBelly);
            sSpike = Bake(48, 52, new Vector2(0.5f, 0.06f), "Monster Spike", BuddyDino.SpikePaint);
            sTail = Bake(128, 96, new Vector2(0.06f, 0.36f), "Monster Tail", (x, y, tx) => BuddyDino.TailPaint(x, y, tx, pal));
            sArm = Bake(48, 48, new Vector2(0.24f, 0.76f), "Monster Claw", (x, y, tx) => BuddyDino.ArmPaint(x, y, tx, pal));
            for (int i = 0; i < spikes.Length; i++) { spikes[i] = R(ref spikes[i], "Monster Spike", o - 1, mat); spikes[i].color = SpikeColor; }
            for (int i = 0; i < 2; i++) arms[i] = R(ref arms[i], "Monster Claw", o + 1, mat);
        }
        else if (Has(Kind.Duck))
        {
            Member d = First(Kind.Duck);
            Color fill = d.color, edge = Color.Lerp(d.color, d.rim, 0.75f);
            sTail = Bake(48, 40, new Vector2(0.1f, 0.3f), "Monster Tail", (x, y, tx) => BuddyDuck.TailPaint(x, y, tx, fill, edge));
        }
        dinoTail = Has(Kind.Dino);
        if (sTail) tail = R(ref tail, "Monster Tail", o - 1, mat);
        // feet: the dino's and the duck's (one of each when it has both: it stomps on mismatched feet)
        Sprite dinoFoot = null, webFoot = null;
        if (Has(Kind.Dino)) { var d = First(Kind.Dino); var pal = BuddyDino.Palette.Make(d.color, d.rim, DinoBelly); dinoFoot = Bake(64, 40, new Vector2(0.45f, 0.06f), "Monster Foot", (x, y, tx) => BuddyDino.FootPaint(x, y, tx, pal)); }
        if (Has(Kind.Duck)) webFoot = Bake(64, 32, new Vector2(0.4f, 0.8f), "Monster Web Foot", (x, y, tx) => BuddyDuck.FootPaint(x, y, tx, FootOrange));
        for (int i = 0; i < 2; i++)
        {
            bool web = webFoot != null && (dinoFoot == null || i == (seed & 1));
            sFoot[i] = web ? webFoot : dinoFoot;
            duckFoot[i] = web;
            if (sFoot[i]) feet[i] = R(ref feet[i], "Monster Foot", o + 1, mat);
        }
        // the burger: its filling round its middle, sesame seeds on its head
        if (Has(Kind.Burger))
        {
            sBand = Keep(BuddyBurger.BakeFilling(false));
            sSeeds = Keep(BuddyBurger.BakeSeeds());
            band = R(ref band, "Monster Filling", o + 1, mat);
            seeds = R(ref seeds, "Monster Seeds", o + 1, mat);
        }
        // the bean: its crease as a stitched scar, and steam
        if (Has(Kind.Bean))
        {
            sScar = Bake(ScarW, ScarH, new Vector2(0.5f, 0.5f), "Monster Scar", ScarPaint);
            sPuff = Bake(32, 32, new Vector2(0.5f, 0.5f), "Monster Steam", PuffPaint);
            scar = R(ref scar, "Monster Scar", o + 1, mat);
            for (int i = 0; i < puffs.Length; i++) puffs[i] = R(ref puffs[i], "Monster Steam", o - 1, mat);
        }
    }

    // two eyes for each of them, packed into the top half of its face: big ones near the middle, the rest round them
    void LayoutEyes(System.Random rnd, Material mat, int o)
    {
        int n = Mathf.Min(members.Count * 2, 12);
        var sizes = new List<float>();
        float shrink = n <= 4 ? 1f : n <= 6 ? 0.88f : n <= 8 ? 0.78f : n <= 10 ? 0.7f : 0.64f;
        for (int i = 0; i < n; i++)
        {
            float b = i < 2 ? 0.115f : 0.095f - 0.004f * i;
            sizes.Add(b * shrink * (0.82f + 0.36f * (float)rnd.NextDouble()));
        }
        sizes.Sort((a, b2) => b2.CompareTo(a));
        float aspect = 0.78f; // its height / width (a monster's squat)
        var placed = new List<Vector3>(); // u, v (half-extents), r (of the width)
        for (int i = 0; i < n; i++)
        {
            float r = sizes[i], bestScore = float.MinValue;
            Vector3 best = new Vector3(0f, 0.3f, r);
            for (int c = 0; c < 260; c++)
            {
                float a = (float)rnd.NextDouble() * Mathf.PI * 2f, d = Mathf.Sqrt((float)rnd.NextDouble());
                float u = Mathf.Cos(a) * d * 0.62f, v = 0.30f + Mathf.Sin(a) * d * 0.30f;
                float clear = 1f;
                foreach (var p in placed)
                {
                    float dx = (u - p.x) * 0.5f, dy = (v - p.y) * 0.5f * aspect; // in widths
                    clear = Mathf.Min(clear, Mathf.Sqrt(dx * dx + dy * dy) - r - p.z);
                }
                float score = Mathf.Min(clear, 0.03f) - 0.06f * Mathf.Sqrt(u * u + (v - 0.3f) * (v - 0.3f)) * (i < 2 ? 3f : 1f);
                if (clear > 0.012f) score += 0.05f;
                if (score > bestScore) { bestScore = score; best = new Vector3(u, v, r); }
            }
            placed.Add(best);
        }
        // the two biggest get the angry brows (they're the boss's)
        browA = 0; browB = n > 1 ? 1 : -1;
        if (browB >= 0 && placed[browA].x > placed[browB].x) { int t = browA; browA = browB; browB = t; }
        for (int i = 0; i < n; i++)
        {
            Eye e = i < eyes.Count ? eyes[i] : new Eye();
            e.white = R(ref e.white, "Monster Eye", o + 2, mat);
            e.pupil = R(ref e.pupil, "Monster Pupil", o + 3, mat);
            e.shine = R(ref e.shine, "Monster Shine", o + 4, mat);
            e.white.sprite = e.pupil.sprite = e.shine.sprite = CuteFace.Circle;
            e.white.color = Color.white;
            e.pupil.color = Pupil;
            e.shine.color = Color.white;
            e.u = placed[i].x; e.v = placed[i].y; e.r = placed[i].z;
            float ga = (float)rnd.NextDouble() * Mathf.PI * 2f;
            e.googly = i < 2 ? Vector2.zero : new Vector2(Mathf.Cos(ga), Mathf.Sin(ga)) * (0.3f + 0.5f * (float)rnd.NextDouble()); // the rest look every which way
            e.nextBlink = Time.time + 1f + 4f * (float)rnd.NextDouble();
            e.blinkT = -1f;
            if (i < eyes.Count) eyes[i] = e; else eyes.Add(e);
        }
        for (int i = n; i < eyes.Count; i++) { var e = eyes[i]; e.r = 0f; eyes[i] = e; }
    }

    SpriteRenderer R(ref SpriteRenderer sr, string name, int order, Material mat)
    {
        if (!sr)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false); // (a child: hiding behind a window masks it too)
            sr = go.AddComponent<SpriteRenderer>();
            if (mat) sr.sharedMaterial = mat;
            all.Add(sr);
        }
        sr.sortingOrder = order;
        sr.color = Color.white;
        sr.enabled = false;
        return sr;
    }

    Sprite Keep(Sprite s) { if (s) baked.Add(s); return s; }

    void FreeSprites()
    {
        foreach (var sp in baked) if (sp) { Destroy(sp.texture); Destroy(sp); }
        baked.Clear();
        sMouth = sBill = sHorn = sCrown = sTuft = sTail = sBand = sSeeds = sScar = sBrow = sSpike = sWing = sArm = sPuff = null;
        sFoot[0] = sFoot[1] = null;
    }

    // ------------------------------------------------------------------ every frame: on the body, where it is now

    void LateUpdate() { long t0 = WhipProf.T(); LateUpdateTimed(); WhipProf.Add("MonsterLook.LateUpdate", t0); }
    void LateUpdateTimed()
    {
        if (!Active) return;
        float t = Time.time, dt = Time.deltaTime;

        // which way it faces: where it's going, else towards you when you're close
        Vector2 v = body.Velocity, cur = Grabber.CursorWorld;
        float want = facing;
        if (Mathf.Abs(v.x) > 0.8f && !body.Held) want = Mathf.Sign(v.x);
        else if (!body.Held && (cur - body.Center).magnitude < 5f && Mathf.Abs(cur.x - body.Center.x) > 0.4f * body.size) want = Mathf.Sign(cur.x - body.Center.x);
        facing = Mathf.MoveTowards(facing, want >= 0f ? 1f : -1f, dt * 3.5f);

        float S = body.size, f = facing, fs = f >= 0f ? 1f : -1f, ang = body.Angle;
        var rot = Quaternion.Euler(0f, 0f, ang);
        cUp = rot * Vector2.up; cRight = rot * Vector2.right; cC = body.Center;
        Vector2 span = body.Span;
        cW = Mathf.Max(span.x, S * 0.6f); cH = Mathf.Max(span.y, S * 0.5f);

        // eyes (they blink out of step; the boss's two look at you, the rest wherever)
        Vector2 look = cur - cC;
        look = look.sqrMagnitude > 1e-4f ? look.normalized : Vector2.zero;
        for (int i = 0; i < eyes.Count; i++)
        {
            Eye e = eyes[i];
            if (e.r <= 0f) { e.white.enabled = e.pupil.enabled = e.shine.enabled = false; continue; }
            if (e.blinkT < 0f && t >= e.nextBlink) e.blinkT = 0f;
            float open = 1f;
            if (e.blinkT >= 0f)
            {
                e.blinkT += dt;
                open = Mathf.Abs(e.blinkT / 0.08f - 1f);
                if (e.blinkT >= 0.16f) { e.blinkT = -1f; e.nextBlink = t + Random.Range(1.5f, 6f); open = 1f; }
            }
            open = Mathf.Max(open, 0.08f);
            Vector2 at = At(e.u + 0.12f * f * (1f - 0.4f * Mathf.Abs(e.u)), e.v);
            float d = 2f * e.r * cW;
            Place(e.white, CuteFace.Circle, at, ang, new Vector2(d, d * open));
            Vector2 lk = (look + e.googly).normalized * (0.21f * d);
            Vector2 pp = at + new Vector2(lk.x, lk.y * open);
            float pd = 0.56f * d;
            Place(e.pupil, CuteFace.Circle, pp, ang, new Vector2(pd, pd * open));
            Place(e.shine, CuteFace.Circle, pp + (Vector2)(rot * new Vector2(-0.16f * pd, 0.18f * pd)), ang, new Vector2(0.34f * pd, 0.34f * pd * open));
            e.shine.enabled = open > 0.5f;
            eyes[i] = e;
        }
        // angry brows over the boss's two eyes, tilted down towards the middle
        for (int k = 0; k < 2; k++)
        {
            var sr = k == 0 ? browL : browR;
            int idx = k == 0 ? browA : browB;
            if (idx < 0 || idx >= eyes.Count || eyes[idx].r <= 0f) { sr.enabled = false; continue; }
            Eye e = eyes[idx];
            float d = 2f * e.r * cW;
            Vector2 at = At(e.u + 0.12f * f * (1f - 0.4f * Mathf.Abs(e.u)), e.v) + cUp * (0.62f * d);
            Place(sr, sBrow, at, ang + (k == 0 ? -1f : 1f) * 16f, new Vector2(1.05f * d, 1.05f * d));
        }
        // the grin (it chomps a little)
        {
            float mw = 0.58f * cW;
            Place(mouth, sMouth, At(0.08f * f, -0.36f), ang, new Vector2(mw, mw * (1f + 0.04f * Mathf.Sin(t * 3.1f))));
        }
        if (sBill) Place(bill, sBill, At(0.12f * f, -0.12f), ang, new Vector2(fs * 0.4f * PartSize(Kind.Duck), 0.4f * PartSize(Kind.Duck)));

        // up top: the horn, the crown, the tuft (front to back)
        int tops = (sHorn ? 1 : 0) + (sCrown ? 1 : 0) + (sTuft ? 1 : 0), slot = 0;
        float[] slots = tops == 1 ? TopSlots1 : tops == 2 ? TopSlots2 : TopSlots3; // degrees towards its front
        if (sHorn) Top(horn, sHorn, slots[slot++], 0.5f * PartSize(Kind.Apple), 0.10f, -8f * f, fs, t);
        if (sCrown) Top(crown, sCrown, slots[slot++], 0.56f * PartSize(Kind.Tomato), 0.09f, 0f, 1f, t);
        if (sTuft) Top(tuft, sTuft, slots[slot++], 0.32f * PartSize(Kind.Duck), 0.04f, 6f * Mathf.Sin(t * 3f), fs, t);

        // the dino's spikes down its back, from behind whatever's on its head
        if (sSpike)
        {
            float ps = PartSize(Kind.Dino);
            float from = tops >= 2 ? 44f : tops == 1 ? 30f : 14f;
            float[] k = { 0.85f, 1f, 0.95f, 0.8f, 0.6f };
            for (int i = 0; i < spikes.Length; i++)
            {
                float deg = (from + i * (118f - from) / (spikes.Length - 1)) * fs;
                Vector2 p = body.OutlinePoint(deg, out Vector2 n);
                float sz = 0.26f * ps * k[i];
                p -= n * (0.05f * sz);
                Place(spikes[i], sSpike, p, Mathf.Atan2(n.y, n.x) * Mathf.Rad2Deg - 90f, new Vector2(sz, sz));
            }
        }
        // the tail at its lower back (wags)
        if (sTail)
        {
            wagT += dt * 5f;
            if (dinoTail)
            {
                Vector2 p = body.OutlinePoint(180f - 62f * f, out Vector2 n);
                p -= n * (0.08f * S);
                float len = 0.55f * PartSize(Kind.Dino);
                Place(tail, sTail, p, ang - f * 6f + Mathf.Sin(wagT) * 8f, new Vector2(-f * len, len * (96f / 128f)));
            }
            else
            {
                Vector2 p = body.OutlinePoint(180f - 55f * f, out Vector2 n);
                p -= n * (0.05f * S);
                float len = 0.3f * PartSize(Kind.Duck);
                Place(tail, sTail, p, ang + Mathf.Sin(wagT) * 6f, new Vector2(-fs * len, len));
            }
        }
        // the duck's wings: tiny, at its sides, flapping away and getting it nowhere
        if (sWing)
        {
            float ws = 0.32f * First(Kind.Duck).size, flap = Mathf.Sin(t * 16f) * 26f;
            for (int i = 0; i < 2; i++)
            {
                float side = i == 0 ? -1f : 1f;
                Vector2 p = body.OutlinePoint(-side * 96f, out Vector2 n);
                p -= n * (0.06f * ws);
                Place(wings[i], sWing, p, ang + side * (10f - flap), new Vector2(side * ws, ws));
            }
        }
        // the dino's clawed little arms, out of its sides low down (they paw at the air)
        if (sArm)
        {
            float as_ = 0.3f * PartSize(Kind.Dino);
            for (int i = 0; i < 2; i++)
            {
                float side = i == 0 ? -1f : 1f;
                Vector2 p = body.OutlinePoint(-side * 112f, out Vector2 n);
                p -= n * (0.12f * as_);
                float swing = Mathf.Sin(t * 3.2f + side) * 10f;
                Place(arms[i], sArm, p, ang + side * (10f + swing), new Vector2(side * as_, as_));
            }
        }
        // feet at its bottom
        for (int i = 0; i < 2; i++)
        {
            if (!sFoot[i]) continue;
            float side = i == 0 ? -1f : 1f;
            Vector2 p = body.OutlinePoint(180f + 30f * side, out _);
            float toes = Mathf.Abs(f) > 0.3f ? fs : side;
            if (duckFoot[i])
            {
                float fsz = 0.3f * PartSize(Kind.Duck);
                Place(feet[i], sFoot[i], p + cRight * (0.03f * f * cW) + cUp * (0.02f * S), ang, new Vector2(toes * fsz, fsz));
            }
            else
            {
                float fsz = 0.3f * PartSize(Kind.Dino);
                Place(feet[i], sFoot[i], p + cRight * (0.04f * f * cW) + cUp * (0.012f * S), ang, new Vector2(toes * fsz, fsz));
            }
        }
        // the burger's filling round its middle (under the grin) and its sesame seeds on top
        if (sBand)
        {
            Place(band, sBand, At(0f, -0.56f), ang, new Vector2(cW * 1.04f, cH * 0.24f / (64f / 192f)));
            Place(seeds, sSeeds, At(-0.1f * f, 0.78f), ang, new Vector2(cW * 0.5f, cH * 0.15f / (48f / 128f)));
        }
        // the bean's crease as a stitched scar down one side, and steam off its head
        if (sScar)
        {
            float len = 0.62f * cH;
            Place(scar, sScar, At(-0.62f * f, 0.02f), ang + 18f * f, new Vector2(len * ScarW / ScarH, len * ScarW / ScarH)); // (the sprite is 1 wide, 4 tall)
            for (int i = 0; i < puffs.Length; i++)
            {
                float u = Mathf.Repeat(t * 0.45f + i / (float)puffs.Length, 1f);
                Vector2 src = body.OutlinePoint(10f * f, out Vector2 n);
                Vector2 p = src + cUp * (u * 0.55f * S) + cRight * (Mathf.Sin(u * 6f + i) * 0.06f * S - 0.1f * f * S * u);
                float sz = Mathf.Lerp(0.12f, 0.34f, u) * S;
                Place(puffs[i], sPuff, p, 0f, new Vector2(sz, sz));
                puffs[i].color = new Color(1f, 1f, 1f, 0.75f * Mathf.Sin(u * Mathf.PI));
            }
        }
    }

    static readonly float[] TopSlots1 = { 4f }, TopSlots2 = { 24f, -16f }, TopSlots3 = { 32f, 2f, -28f };

    // a part up top: on its outline deg degrees towards its front from the top, leaning out with it
    void Top(SpriteRenderer sr, Sprite sp, float frontDeg, float size, float sink, float lean, float flipX, float t)
    {
        Vector2 p = body.OutlinePoint(-frontDeg * Mathf.Sign(facing == 0f ? 1f : facing), out Vector2 n);
        p -= n * (sink * size);
        Place(sr, sp, p, Mathf.Atan2(n.y, n.x) * Mathf.Rad2Deg - 90f + lean + 3f * Mathf.Sin(t * 1.7f + frontDeg), new Vector2(flipX * size, size));
    }

    Vector2 At(float u, float v) => cC + cRight * (u * 0.5f * cW) + cUp * (v * 0.5f * cH);

    static void Place(SpriteRenderer sr, Sprite sp, Vector2 at, float deg, Vector2 scale)
    {
        if (!sr) return;
        sr.sprite = sp;
        sr.enabled = sp;
        sr.transform.position = new Vector3(at.x, at.y, 0f);
        sr.transform.rotation = Quaternion.Euler(0f, 0f, deg);
        sr.transform.localScale = new Vector3(scale.x, scale.y, 1f);
    }

    // ------------------------------------------------------------------ drawing

    const int MouthW = 192, MouthH = 96, HornW = 64, HornH = 96, CrownW = 96, CrownH = 80, ScarW = 32, ScarH = 128, BrowW = 48, BrowH = 16;

    Sprite Bake(int w, int h, Vector2 pivot, string name, System.Func<float, float, float, Color> paint)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = name };
        var px = new Color32[w * h];
        float texel = 1f / w;
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                Color c = paint((i + 0.5f) * texel, (j + 0.5f) * texel, texel); // x 0..1, y 0..h/w (same texel size)
                px[j * w + i] = new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), Mathf.Clamp01(c.a));
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return Keep(Sprite.Create(tex, new Rect(0, 0, w, h), pivot, w)); // 1 unit wide
    }

    // straight-alpha "src over dst" with src opacity a
    static Color Over(Color dst, Color src, float a)
    {
        float outA = a + dst.a * (1f - a);
        if (outA <= 1e-5f) return new Color(0f, 0f, 0f, 0f);
        float k = dst.a * (1f - a);
        return new Color((src.r * a + dst.r * k) / outA, (src.g * a + dst.g * k) / outA, (src.b * a + dst.b * k) / outA, outA);
    }

    static float Segment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 pa = p - a, ba = b - a;
        float h = Mathf.Clamp01(Vector2.Dot(pa, ba) / Vector2.Dot(ba, ba));
        return (pa - ba * h).magnitude;
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

    // a big grin (x 0..1, y 0..0.5): wide, curling up at the corners, dark inside with a tongue, an ink edge, fangs from
    // the top (big ones with a dino inside) and little teeth from the bottom; zig = all the way round (a bean's zigzag)
    internal static Color MouthPaint(float x, float y, float texel, bool zig, bool bigFangs)
    {
        float X = x * 2f - 1f, Y = y * 2f - 0.5f, px = texel * 2f; // X -1..1, Y -0.5..0.5, same units
        const float line = 0.045f;
        float yT = 0.04f + 0.30f * X * X, yB = -0.40f + 0.80f * X * X;
        float d = Mathf.Max((Y - yT) / Mathf.Sqrt(1f + 0.36f * X * X), (yB - Y) / Mathf.Sqrt(1f + 2.56f * X * X)); // < 0 inside
        Color ink = new Color(0.20f, 0.05f, 0.07f, 1f);
        float aOut = Mathf.Clamp01(0.5f - d / px);
        if (aOut <= 0f) return new Color(0f, 0f, 0f, 0f);
        float depth = Mathf.Clamp01((yT - Y) / 0.35f);
        Color c = Color.Lerp(new Color(0.46f, 0.08f, 0.12f), new Color(0.20f, 0.02f, 0.06f), depth);
        // the tongue
        if (!zig)
        {
            float tx = X / 0.32f, ty = (Y + 0.27f) / 0.15f;
            float dT = (Mathf.Sqrt(tx * tx + ty * ty) - 1f) * 0.15f;
            float aT = Mathf.Clamp01(0.5f - dT / px);
            Color tongue = Color.Lerp(new Color(0.98f, 0.48f, 0.58f), new Color(0.86f, 0.32f, 0.44f), Mathf.Clamp01(0.5f - Mathf.Abs(X) / 0.025f));
            c = Over(c, tongue, aT);
        }
        // teeth: fangs down from the top lip, little teeth up from the bottom one
        Color tooth = new Color(1f, 0.97f, 0.88f, 1f);
        float aTooth = 0f, dToothMin = 1e3f;
        int nTop = zig ? 7 : 2, nBot = zig ? 6 : 2;
        for (int i = 0; i < nTop; i++)
        {
            float xi = zig ? Mathf.Lerp(-0.72f, 0.72f, i / (float)(nTop - 1)) : (i == 0 ? -1f : 1f) * (bigFangs ? 0.44f : 0.38f);
            float L = zig ? 0.16f : bigFangs ? 0.30f : 0.21f, bw = zig ? 0.11f : bigFangs ? 0.10f : 0.075f;
            float top = 0.04f + 0.30f * xi * xi + 0.05f, tip = top - L - 0.05f;
            float dTooth = Mathf.Max(Mathf.Abs(X - xi) - bw * Mathf.Clamp01((Y - tip) / (top - tip)), tip - Y);
            dToothMin = Mathf.Min(dToothMin, dTooth);
        }
        for (int i = 0; i < nBot; i++)
        {
            float xi = zig ? Mathf.Lerp(-0.6f, 0.6f, i / (float)(nBot - 1)) : (i == 0 ? -1f : 1f) * 0.18f;
            float L = zig ? 0.13f : 0.10f, bw = zig ? 0.10f : 0.06f;
            float bot = -0.40f + 0.80f * xi * xi - 0.05f, tip = bot + L + 0.05f;
            float dTooth = Mathf.Max(Mathf.Abs(X - xi) - bw * Mathf.Clamp01((tip - Y) / (tip - bot)), Y - tip);
            dToothMin = Mathf.Min(dToothMin, dTooth);
        }
        aTooth = Mathf.Clamp01(0.5f - dToothMin / px);
        Color tc = Color.Lerp(tooth, ink, Mathf.Clamp01((dToothMin + 0.02f) / px + 0.5f));
        c = Over(c, tc, aTooth);
        // the lips' ink edge
        c = Color.Lerp(c, ink, Mathf.Clamp01((d + line) / px + 0.5f));
        c.a = aOut;
        return c;
    }

    // the apple's stem as a crooked horn (x 0..1, y 0..1.5): a tapering brown curve bending forward (+x), an ink edge,
    // a shine down one side, and its leaf sticking out partway up
    internal static Color HornPaint(float x, float y, float texel, Color stem, Color leaf)
    {
        var p = new Vector2(x, y);
        Vector2 p0 = new Vector2(0.48f, 0.05f), p1 = new Vector2(0.36f, 0.80f), p2 = new Vector2(0.78f, 1.36f);
        const int segs = 18;
        float dH = 1e3f, along = 0f;
        Vector2 prev = p0;
        for (int i = 1; i <= segs; i++)
        {
            float t = i / (float)segs;
            Vector2 q = (1f - t) * (1f - t) * p0 + 2f * (1f - t) * t * p1 + t * t * p2;
            float tm = (i - 0.5f) / segs, r = Mathf.Lerp(0.2f, 0.045f, tm);
            float dd = Segment(p, prev, q) - r;
            if (dd < dH) { dH = dd; along = tm; }
            prev = q;
        }
        Color ink = new Color(0.17f, 0.10f, 0.08f, 1f);
        Color o = new Color(0f, 0f, 0f, 0f);
        // the leaf (behind the horn)
        Vector2 lq = p - new Vector2(0.70f, 0.66f);
        float ca = Mathf.Cos(-0.6f), sa = Mathf.Sin(-0.6f);
        lq = new Vector2(ca * lq.x - sa * lq.y, sa * lq.x + ca * lq.y);
        float dL = (new Vector2(lq.x / 0.26f, lq.y / 0.11f).magnitude - 1f) * 0.11f;
        if (dL < texel)
        {
            Color lc = Color.Lerp(leaf, leaf * 0.6f, Mathf.Clamp01((dL + 0.025f) / texel + 0.5f));
            if (Mathf.Abs(lq.y) < 0.012f && lq.x > -0.2f && lq.x < 0.2f) lc = Color.Lerp(lc, leaf * 0.7f, 0.7f);
            lc.a = 1f;
            o = Over(o, lc, Mathf.Clamp01(0.5f - dL / texel));
        }
        if (dH < texel)
        {
            Color hc = Color.Lerp(stem * 1.15f, stem * 0.75f, Mathf.Clamp01((x - 0.42f) / 0.25f + 0.5f));
            if (x < 0.46f && along < 0.7f && dH < -0.05f) hc = Color.Lerp(hc, Color.white, 0.18f);
            hc = Color.Lerp(hc, ink, Mathf.Clamp01((dH + 0.03f) / texel + 0.5f));
            hc.a = 1f;
            o = Over(o, hc, Mathf.Clamp01(0.5f - dH / texel));
        }
        return o;
    }

    // the tomato's star as a spiky crown (x 0..1, y 0..0.83): five pointed sepals fanned up from a nub at the bottom
    internal static Color CrownPaint(float x, float y, float texel, Color leaf)
    {
        var p = new Vector2(x, y) - new Vector2(0.5f, 0.07f);
        float[] turn = { -62f, -31f, 0f, 31f, 62f };
        float[] len = { 0.40f, 0.52f, 0.62f, 0.52f, 0.40f };
        float d = 1e3f;
        for (int k = 0; k < turn.Length; k++)
        {
            float r = turn[k] * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r);
            var lq = new Vector2(c * p.x + s * p.y, -s * p.x + c * p.y);
            d = Mathf.Min(d, UnevenCapsule(lq, 0.09f, 0.015f, len[k]));
        }
        d = Mathf.Min(d, new Vector2(p.x / 1.4f, p.y).magnitude - 0.08f);
        if (d > texel) return new Color(0f, 0f, 0f, 0f);
        Color edge = leaf * 0.55f; edge.a = 1f;
        Color c2 = Color.Lerp(leaf * 1.08f, edge, Mathf.Clamp01((d + 0.035f) / texel + 0.5f));
        c2.a = Mathf.Clamp01(0.5f - d / texel);
        return c2;
    }

    // the bean's crease as a stitched scar (x 0..1, y 0..4): a dark S down the middle, cross stitches over it
    internal static Color ScarPaint(float x, float y, float texel)
    {
        var p = new Vector2(x, y);
        const int segs = 20;
        float dLine = 1e3f, dSt = 1e3f;
        Vector2 prev = new Vector2(0.5f + 0.16f * Mathf.Sin(-0.3f), 3.85f);
        for (int i = 1; i <= segs; i++)
        {
            float t = i / (float)segs;
            var q = new Vector2(0.5f + 0.16f * Mathf.Sin(t * Mathf.PI * 1.6f - 0.3f), 3.85f - t * 3.7f);
            float w = 0.03f + 0.07f * Mathf.Sin(t * Mathf.PI);
            dLine = Mathf.Min(dLine, Segment(p, prev, q) - w);
            if (i % 3 == 0 && i < segs) // a cross stitch here
            {
                Vector2 dir = (q - prev).normalized, nrm = new Vector2(-dir.y, dir.x);
                dSt = Mathf.Min(dSt, Segment(p, q - nrm * 0.30f, q + nrm * 0.30f) - 0.035f);
            }
            prev = q;
        }
        float aL = Mathf.Clamp01(0.5f - dLine / texel), aS = Mathf.Clamp01(0.5f - dSt / texel);
        Color o = Over(new Color(0f, 0f, 0f, 0f), new Color(0.10f, 0.05f, 0.03f, 1f), aL * 0.7f);
        return Over(o, new Color(0.14f, 0.08f, 0.06f, 1f), aS * 0.85f);
    }

    // a soft puff of steam (x, y 0..1)
    internal static Color PuffPaint(float x, float y, float texel)
    {
        float r = new Vector2(x - 0.5f, y - 0.5f).magnitude / 0.5f;
        return new Color(1f, 0.99f, 0.96f, Mathf.Clamp01((1f - r) * 1.6f) * Mathf.Clamp01(1.2f - r));
    }

    // a thick angry brow (x 0..1, y 0..0.33)
    internal static Color BrowPaint(float x, float y, float texel)
    {
        float d = Segment(new Vector2(x, y), new Vector2(0.12f, 0.17f), new Vector2(0.88f, 0.17f)) - Mathf.Lerp(0.07f, 0.1f, x);
        Color c = BrowInk;
        c.a = Mathf.Clamp01(0.5f - d / texel);
        return c;
    }
}
