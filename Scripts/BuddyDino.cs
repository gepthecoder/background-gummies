using UnityEngine;

/// <summary>
/// The little dino (apple level 20, BuddyFriends: it hatches from an egg): the gummy body is its head and tummy; this adds
/// what makes it a dino, all drawn in code and riding the soft body: a row of back spikes over its head and down its
/// back, a stubby tail that wags, two tiny arms, little clawed feet and a pale belly. It turns its tail and spikes to
/// where it's going (or towards you when you're close). Now and then, when you come close after a while away, it ducks
/// and jumps up at you with a "RAWR!" (arms up) and giggles; chat "rawr" and it roars back. Every RAWR it says roars.
/// </summary>
[RequireComponent(typeof(GummyBody))]
[DisallowMultipleComponent]
public class BuddyDino : MonoBehaviour
{
    [Tooltip("Sprite material (WhipIt > Setup assigns it).")]
    public Material material;
    public Color spikeColor = new Color(1f, 0.72f, 0.22f);
    public Color bellyColor = new Color(0.86f, 0.95f, 0.62f);
    [Tooltip("How long its tail is (x size).")]
    public float tailLength = 0.5f;
    [Tooltip("Surprise RAWR when you come close after this long away (seconds), at most once every rawrEvery.")]
    public float awayFor = 20f;
    public float rawrEvery = 45f;
    [Range(0f, 1f)] public float rawrChance = 0.45f;
    public AudioClip rawrClip;
    [Range(0f, 1f)] public float volume = 0.55f;

    /// <summary>Which way it faces (-1 left .. 1 right; in between while it turns).</summary>
    public float Facing => facing;
    /// <summary>RAWRs so far (this session).</summary>
    public int Roars { get; private set; }

    GummyBody body;
    GummyBrain brain;
    BuddySpeech speech;
    AudioSource sfx;
    readonly SpriteRenderer[] spikes = new SpriteRenderer[4];
    SpriteRenderer tail, armL, armR, footL, footR, belly;
    static Sprite s_spike, s_belly;              // white, tinted
    static bool s_baked;
    Sprite tailSprite, armSprite, footSprite;    // in its own colours (rebaked when it changes colour at a new stage)
    Color bakedBody, bakedRim;
    bool bakedLimbs;
    float facing = 1f, wagT, roarT = -1f, lastRoar = -100f, lastSound = -100f, farSince, roarDelay;
    bool near;

    // the spikes over its head towards its back (degrees round from the top towards its back) and how big; more on its tail
    static readonly float[] SpikeAt = { -8f, 16f, 40f, 63f };
    static readonly float[] SpikeSize = { 0.85f, 1f, 0.9f, 0.62f };

    float S => body.size / 0.6f;

    void Awake()
    {
        body = GetComponent<GummyBody>();
        brain = GetComponent<GummyBrain>();
        speech = GetComponent<BuddySpeech>();
        int o = body.sortingOrder;
        for (int i = 0; i < spikes.Length; i++) spikes[i] = MakeRenderer("Dino Spike", o - 1); // behind the body: they peek out over its outline
        tail = MakeRenderer("Dino Tail", o - 1);
        belly = MakeRenderer("Dino Belly", o + 1);                                            // over the body, under the face
        armL = MakeRenderer("Dino Arm L", o + 1);
        armR = MakeRenderer("Dino Arm R", o + 1);
        footL = MakeRenderer("Dino Foot L", o + 1);
        footR = MakeRenderer("Dino Foot R", o + 1);
        sfx = gameObject.AddComponent<AudioSource>();
        sfx.playOnAwake = false;
        sfx.spatialBlend = 0f;
        farSince = -1000f;
    }

    SpriteRenderer MakeRenderer(string name, int order)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false); // (a child: hiding behind a window masks it too)
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sortingOrder = order;
        sr.color = Color.white;
        Material m = material ? material : body.spriteMaterial;
        if (m) sr.sharedMaterial = m;
        sr.enabled = false;
        return sr;
    }

    void OnEnable() { if (speech) speech.Said += OnSaid; }

    void OnDisable()
    {
        if (speech) speech.Said -= OnSaid;
        foreach (var sr in All()) if (sr) sr.enabled = false;
        roarT = -1f;
    }

    void OnDestroy() => FreeLimbs();

    void FreeLimbs()
    {
        foreach (var sp in new[] { tailSprite, armSprite, footSprite }) if (sp) { Destroy(sp.texture); Destroy(sp); }
        tailSprite = armSprite = footSprite = null;
    }

    System.Collections.Generic.IEnumerable<SpriteRenderer> All()
    {
        foreach (var s in spikes) yield return s;
        yield return tail; yield return belly; yield return armL; yield return armR; yield return footL; yield return footR;
    }

    // every RAWR it says roars (a cute little one)
    void OnSaid(string line)
    {
        if (!line.Contains("RAWR") || !rawrClip || !sfx || Time.time - lastSound < 1.5f) return;
        lastSound = Time.time;
        sfx.pitch = Random.Range(0.95f, 1.12f) * Mathf.Lerp(1.15f, 0.9f, Mathf.InverseLerp(0.38f, 0.62f, body.size)); // the little ones squeakier
        sfx.PlayOneShot(rawrClip, volume);
    }

    /// <summary>RAWR! (a little duck, then up on its toes with its arms up; chat "rawr")</summary>
    public void Roar(bool say = true)
    {
        if (roarT >= 0f) return;
        roarT = 0f;
        Roars++;
        lastRoar = Time.time;
        roarDelay = say ? 0.25f : -1f;
    }

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("BuddyDino.Update", t0); }
    void UpdateTimed()
    {
        float now = Time.time, dt = Time.deltaTime;

        // which way it faces: where it's going, else towards you when you're close and it's just standing there
        Vector2 v = body.Velocity;
        Vector2 cur = Grabber.CursorWorld;
        float want = facing;
        if (Mathf.Abs(v.x) > 0.8f && !body.Held) want = Mathf.Sign(v.x);
        else if (!body.Held && (cur - body.Center).magnitude < 4f && Mathf.Abs(cur.x - body.Center.x) > 0.5f * S) want = Mathf.Sign(cur.x - body.Center.x);
        want = want >= 0f ? 1f : -1f;
        facing = Mathf.MoveTowards(facing, want, dt * 4.5f);

        // the surprise: you come close after a while away, it ducks and jumps up at you: RAWR!
        bool close = (cur - body.Center).magnitude < 2.2f * S;
        bool far = (cur - body.Center).magnitude > 5f * S;
        if (far) { if (near || farSince < 0f) farSince = now; near = false; }
        else if (close && !near)
        {
            near = true;
            bool free = brain && brain.IsFree && body.Grounded && !body.Held && !BuddyChat.Open && !BuddyHideSeek.Playing && brain.Current != GummyBrain.State.Asleep;
            if (free && now - farSince > awayFor && now - lastRoar > rawrEvery && Random.value < rawrChance) Roar();
        }

        if (roarT >= 0f)
        {
            roarT += dt;
            if (roarDelay >= 0f && roarT >= roarDelay)
            {
                roarDelay = -1f;
                if (speech) speech.Say(Pick("RAWR!!", "RAWRRR!", "RAWR! SCARED?"), 0f, true);
                if (body.Grounded && !body.Held) body.Hop(new Vector2(0f, 4.2f * Mathf.Sqrt(S)), 0.25f); // up on its toes
            }
            // duck, spring up, arms up for a moment
            float u = roarT;
            float duck = u < 0.25f ? Mathf.Sin(u / 0.25f * Mathf.PI * 0.5f) : Mathf.Max(0f, 1f - (u - 0.25f) / 0.12f);
            if (!body.Held) body.Pose = new Vector2(1f + 0.14f * duck, 1f - 0.2f * duck);
            if (u > 1.4f)
            {
                roarT = -1f;
                if (!body.Held) body.Pose = Vector2.one;
                if (speech && Random.value < 0.5f) speech.Say(Pick("HEHE", "GOTCHA!", "TOO SCARY?"), 0f, false);
            }
        }
    }

    static string Pick(params string[] l) => l[Random.Range(0, l.Length)];

    // ------------------------------------------------------------------ its spikes, tail, arms, feet, belly

    void LateUpdate() { long t0 = WhipProf.T(); LateUpdateTimed(); WhipProf.Add("BuddyDino.LateUpdate", t0); }
    void LateUpdateTimed()
    {
        if (!s_baked) { Bake(); s_baked = true; }
        if (!bakedLimbs || !Same(body.color, bakedBody) || !Same(body.rimColor, bakedRim))
        {
            FreeLimbs();
            var pal = Palette.Make(body.color, body.rimColor, bellyColor);
            tailSprite = BakeSprite(TailW, TailH, new Vector2(0.06f, 0.36f), "Dino Tail", (x, y, tx) => TailPaint(x, y, tx, pal));
            armSprite = BakeSprite(ArmW, ArmH, new Vector2(0.24f, 0.76f), "Dino Arm", (x, y, tx) => ArmPaint(x, y, tx, pal));
            footSprite = BakeSprite(FootW, FootH, new Vector2(0.45f, 0.06f), "Dino Foot", (x, y, tx) => FootPaint(x, y, tx, pal));
            bakedBody = body.color; bakedRim = body.rimColor; bakedLimbs = true;
        }
        float s = body.size, f = facing, t = Time.time, dt = Time.deltaTime;
        float ang = body.Angle;
        var rot = Quaternion.Euler(0f, 0f, ang);
        Vector2 up = rot * Vector2.up, right = rot * Vector2.right;
        Vector2 span = body.Span;
        float w = Mathf.Max(span.x, s * 0.5f), h = Mathf.Max(span.y, s * 0.5f);
        bool happy = brain && brain.Mood == CuteFace.Expression.Happy;
        bool moving = Mathf.Abs(body.Velocity.x) > 0.8f;
        float roarArms = roarT >= 0f ? Mathf.Clamp01((roarT - 0.2f) / 0.12f) * Mathf.Clamp01((1.3f - roarT) / 0.25f) : 0f;

        // the spikes: along its outline from the front of its head down its back (they swing over as it turns)
        for (int i = 0; i < spikes.Length; i++)
        {
            var sr = spikes[i];
            sr.sprite = s_spike;
            sr.color = spikeColor;
            sr.enabled = true;
            Vector2 p = body.OutlinePoint(SpikeAt[i] * f, out Vector2 n);
            float k = SpikeSize[i] * s;
            p -= n * (0.05f * k);
            sr.transform.position = new Vector3(p.x, p.y, 0f);
            sr.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(n.y, n.x) * Mathf.Rad2Deg - 90f);
            sr.transform.localScale = new Vector3(0.22f * k, 0.22f * k, 1f);
        }

        // the tail: from its lower back, pointing away from where it's going, wagging (round under it as it turns)
        wagT += dt * (happy ? 11f : moving ? 8f : 3f);
        float wag = Mathf.Sin(wagT) * (happy ? 16f : moving ? 10f : 6f);
        {
            Vector2 p = body.OutlinePoint(180f - 60f * f, out Vector2 n);
            p -= n * (0.09f * s);
            tail.sprite = tailSprite;
            tail.enabled = true;
            tail.transform.position = new Vector3(p.x, p.y, 0f);
            tail.transform.rotation = Quaternion.Euler(0f, 0f, ang - f * 6f + wag);
            float len = tailLength * s;
            tail.transform.localScale = new Vector3(-f * len, len * (TailH / (float)TailW), 1f);
        }

        // the belly: a pale patch on its tummy, a bit towards where it faces
        {
            Vector2 p = body.Center + right * (0.07f * f * w) + up * (-0.2f * h);
            belly.sprite = s_belly;
            belly.color = bellyColor;
            belly.enabled = true;
            belly.transform.position = new Vector3(p.x, p.y, 0.01f); // (a hair behind its arms and feet: same draw order, they go on top)
            belly.transform.rotation = rot;
            belly.transform.localScale = new Vector3(0.54f * w, 0.5f * h, 1f);
        }

        // tiny arms: on its chest, reaching out a little (they wave as it runs; up when it roars)
        for (int side = -1; side <= 1; side += 2)
        {
            var sr = side < 0 ? armL : armR;
            Vector2 p = body.Center + right * ((0.25f * side + 0.06f * f) * w) + up * (-0.1f * h);
            float swing = moving ? Mathf.Sin(t * 10f + side) * 12f : Mathf.Sin(t * 2.2f + side) * 4f;
            sr.sprite = armSprite;
            sr.enabled = true;
            sr.transform.position = new Vector3(p.x, p.y, 0f);
            sr.transform.rotation = Quaternion.Euler(0f, 0f, ang + side * (swing + 75f * roarArms));
            sr.transform.localScale = new Vector3(side * 0.19f * s, 0.19f * s, 1f);
        }

        // little feet with claws at its bottom, toes towards where it faces
        for (int side = -1; side <= 1; side += 2)
        {
            var sr = side < 0 ? footL : footR;
            Vector2 p = body.OutlinePoint(180f + 27f * side, out _);
            p += right * (0.04f * f * w) + up * (0.012f * s);
            sr.sprite = footSprite;
            sr.enabled = true;
            sr.transform.position = new Vector3(p.x, p.y, 0f);
            sr.transform.rotation = rot;
            float toes = Mathf.Abs(f) > 0.3f ? Mathf.Sign(f) : side;
            sr.transform.localScale = new Vector3(toes * 0.26f * s, 0.26f * s, 1f);
        }
    }

    // ------------------------------------------------------------------ drawing (spikes + belly: baked once, white, tinted;
    //                                                                     tail, arms, feet: in its colours)

    const int SpikeW = 48, SpikeH = 52, TailW = 128, TailH = 96, ArmW = 48, ArmH = 48, FootW = 64, FootH = 40, BellyW = 64, BellyH = 64;

    static void Bake()
    {
        s_spike = BakeSprite(SpikeW, SpikeH, new Vector2(0.5f, 0.06f), "Dino Spike", SpikePaint);
        s_belly = BakeSprite(BellyW, BellyH, new Vector2(0.5f, 0.5f), "Dino Belly", BellyPaint);
    }

    /// <summary>The colours its tail, arms and feet are drawn in.</summary>
    internal struct Palette
    {
        public Color fill, edge, light, under, claw, spike;
        public static Palette Make(Color body, Color rim, Color belly) => new Palette
        {
            fill = Color.Lerp(body, rim, 0.08f),
            spike = new Color(1f, 0.72f, 0.22f, 1f),
            edge = Color.Lerp(body, rim, 0.7f),
            light = Color.Lerp(body, Color.white, 0.22f),
            under = Color.Lerp(body, belly, 0.55f),
            claw = new Color(1f, 0.98f, 0.9f, 1f),
        };
    }

    static Sprite BakeSprite(int w, int h, Vector2 pivot, string name, System.Func<float, float, float, Color> paint)
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
        return Sprite.Create(tex, new Rect(0, 0, w, h), pivot, w); // 1 unit wide
    }

    // a chunky spike plate pointing up (base at the bottom), a rounded tip, lighter in the middle, a darker edge
    internal static Color SpikePaint(float x, float y, float texel)
    {
        float H = SpikeH / (float)SpikeW, top = H * 0.9f, r = 0.08f;
        float v = Mathf.Clamp01(y / top);                                     // 0 base .. 1 tip
        float half = 0.46f * Mathf.Pow(1f - v, 0.85f) + r;                  // narrows to a rounded point
        float d = Mathf.Max(Mathf.Abs(x - 0.5f) - half, 0.01f - y);
        if (y > top) d = new Vector2(x - 0.5f, y - top).magnitude - r;
        float a = Cover(d, texel);
        if (a <= 0f) return Clear;
        float shade = Mathf.Lerp(1f, 0.86f, Mathf.Abs(x - 0.5f) / Mathf.Max(half, 0.01f));
        Color c = new Color(shade, shade, shade, 1f);
        if (d > -texel * 2.4f && y > 0.03f) c = new Color(0.70f, 0.62f, 0.58f, 1f); // its edge (not along the base: that's inside the body)
        c.a = a;
        return c;
    }

    // a chunky tail pointing right and up (base on the left), a rounded tip, little spikes along its top, a pale underside
    internal static Color TailPaint(float x, float y, float texel, Palette pal)
    {
        float u = Mathf.Clamp01((x - 0.02f) / 0.9f);
        float mid = Mid(u);                                         // its middle line, curling up towards the tip
        float half = Half(u);                                       // thick at the base, a rounded tip
        float d = Mathf.Max(Mathf.Abs(y - mid) - half, 0.02f - x);
        if (x > 0.92f) d = new Vector2(x - 0.92f, y - Mid(1f)).magnitude - Half(1f);
        Color c = Clear;
        // little spikes along its top
        foreach (float sk in new[] { 0.28f, 0.5f, 0.7f })
        {
            float sx = 0.02f + 0.9f * sk, top = Mid(sk) + Half(sk);
            float hw = 0.08f * (1f - 0.35f * sk), ht = 0.14f * (1f - 0.4f * sk);
            float q = 1f - Mathf.Abs(x - sx) / hw;
            float sd = Mathf.Max(y - (top - 0.02f + ht * Mathf.Max(q, 0f)), Mathf.Max(top - 0.04f - y, Mathf.Abs(x - sx) - hw));
            float sa = Cover(sd, texel);
            if (sa > 0f) c = Over(c, sd > -texel * 1.8f ? pal.spike * 0.7f : pal.spike, sa);
        }
        float a = Cover(d, texel);
        if (a > 0f)
        {
            float k = (y - mid) / Mathf.Max(half, 1e-3f);            // -1 underside .. 1 top
            Color t = k > 0.3f ? Color.Lerp(pal.fill, pal.light, Mathf.Clamp01((k - 0.3f) * 1.6f)) : k < -0.25f ? Color.Lerp(pal.fill, pal.under, Mathf.Clamp01((-k - 0.25f) * 2.2f)) : pal.fill;
            if (d > -texel * 2.4f && x > 0.08f) t = pal.edge;         // its outline (not at the base: that's behind the body)
            c = Over(c, t, a);
        }
        return c;
    }

    static float Mid(float u) => 0.24f + 0.22f * u + 0.2f * u * u;
    static float Half(float u) => 0.22f * Mathf.Pow(1f - u, 0.75f) + 0.045f;

    // a tiny chubby arm: from the shoulder (top left) down and out to a little hand with two claws
    internal static Color ArmPaint(float x, float y, float texel, Palette pal)
    {
        Vector2 sh = new Vector2(0.24f, 0.76f), hand = new Vector2(0.5f, 0.5f);
        Vector2 ab = hand - sh, ap = new Vector2(x, y) - sh;
        float t = Mathf.Clamp01(Vector2.Dot(ap, ab) / ab.sqrMagnitude);
        float r = Mathf.Lerp(0.21f, 0.18f, t);
        float d = (ap - ab * t).magnitude - r;
        Color c = Clear;
        // two little claws at the hand (under the arm's outline)
        for (int k = 0; k < 2; k++)
        {
            Vector2 cb = hand + (k == 0 ? new Vector2(0.11f, 0.02f) : new Vector2(0.04f, -0.12f)), dir = new Vector2(1f, k == 0 ? -0.35f : -1.1f).normalized;
            Vector2 q = new Vector2(x, y) - cb;
            float along = Vector2.Dot(q, dir), across = Mathf.Abs(dir.x * q.y - dir.y * q.x);
            float cd = Mathf.Max(across - 0.07f * (1f - Mathf.Clamp01(along / 0.17f)), Mathf.Max(-along, along - 0.17f));
            float ca = Cover(cd, texel);
            if (ca > 0f) c = Over(c, cd > -texel * 1.5f ? Color.Lerp(pal.claw, pal.edge, 0.5f) : pal.claw, ca);
        }
        float a = Cover(d, texel);
        if (a > 0f)
        {
            Color arm = (ap - ab * t).y > r * 0.3f ? Color.Lerp(pal.fill, pal.light, 0.5f) : pal.fill;
            if (d > -texel * 2.4f) arm = pal.edge;
            c = Over(c, arm, a);
        }
        return c;
    }

    // a stubby foot (toes to the right) with three toe claws poking out in front
    internal static Color FootPaint(float x, float y, float texel, Palette pal)
    {
        float H = FootH / (float)FootW;
        Color c = Clear;
        for (int k = 0; k < 3; k++)
        {
            Vector2 cb = new Vector2(0.72f, H * (0.66f - 0.26f * k));
            Vector2 dir = new Vector2(1f, -0.1f - 0.3f * k).normalized;
            Vector2 q = new Vector2(x, y) - cb;
            float along = Vector2.Dot(q, dir), across = Mathf.Abs(dir.x * q.y - dir.y * q.x);
            float cd = Mathf.Max(across - 0.07f * (1f - Mathf.Clamp01(along / 0.2f)), Mathf.Max(-along, along - 0.2f));
            float ca = Cover(cd, texel);
            if (ca > 0f) c = Over(c, cd > -texel * 1.5f ? Color.Lerp(pal.claw, pal.edge, 0.5f) : pal.claw, ca);
        }
        // a rounded lump, flat underneath
        Vector2 c0 = new Vector2(0.42f, H * 0.42f);
        float dx = (x - c0.x) / 0.38f, dy = (y - c0.y) / (H * 0.42f);
        float d = Mathf.Max((new Vector2(dx, dy).magnitude - 1f) * 0.3f, 0.03f - y);
        float a = Cover(d, texel);
        if (a > 0f)
        {
            Color foot = dy > 0.4f ? Color.Lerp(pal.fill, pal.light, 0.5f) : pal.fill;
            if (d > -texel * 2.4f) foot = pal.edge;
            c = Over(c, foot, a);
        }
        return c;
    }

    // a soft pale oval with a few belly stripes
    internal static Color BellyPaint(float x, float y, float texel)
    {
        float u = x * 2f - 1f, v = y * 2f - 1f;
        float d = (new Vector2(u, v * 1.05f).magnitude - 0.96f) * 0.5f;
        float a = Mathf.Clamp01(0.5f - d / (texel * 1.6f));
        if (a <= 0f) return Clear;
        Color c = Color.white;
        float stripe = Mathf.Abs(Mathf.Repeat(v * 2.6f + 0.3f, 1f) - 0.5f);
        if (stripe < 0.06f && v < 0.55f && Mathf.Abs(u) < 0.8f * Mathf.Sqrt(Mathf.Max(0f, 1f - v * v))) c = new Color(0.86f, 0.86f, 0.80f, 1f);
        c.a = a * 0.95f;
        return c;
    }

    static readonly Color Clear = new Color(0f, 0f, 0f, 0f);

    static bool Same(Color a, Color b) => Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) + Mathf.Abs(a.a - b.a) < 1e-4f;

    static Color Over(Color under, Color over, float a)
    {
        float oa = a + under.a * (1f - a);
        if (oa <= 1e-5f) return Clear;
        Color c = (over * a + under * (under.a * (1f - a))) / oa;
        c.a = oa;
        return c;
    }

    static float Cover(float d, float texel) => Mathf.Clamp01(0.5f - d / texel); // d < 0 inside
}
