using UnityEngine;

/// <summary>
/// The gummy burger (apple level 15, BuddyFriends): the gummy body is the bun; this lays the filling across it (a frilly
/// lettuce leaf, melting cheese, a juicy patty; two patties once it's a double burger) and the sesame seeds on top, all
/// drawn in code and riding the soft body (it squashes, tilts and wobbles with it). Its belly is a trampoline: a buddy
/// that comes down on top of it bounces off, higher than it fell ("BOING!"), and the burger squishes.
/// </summary>
[DefaultExecutionOrder(-9)] // after the brain has set its pose, before the body simulates (the squish wins)
[RequireComponent(typeof(GummyBody))]
[DisallowMultipleComponent]
public class BuddyBurger : MonoBehaviour
{
    [Tooltip("Sprite material (WhipIt > Setup assigns it).")]
    public Material material;
    [Tooltip("Two patties from this level on.")]
    public int doubleFromLevel = 10;
    [Tooltip("How much higher than it fell a buddy bounces off its belly.")]
    public float bounciness = 1.15f;
    public AudioClip boingClip;
    [Range(0f, 1f)] public float volume = 0.5f;

    /// <summary>Bounces so far (this session).</summary>
    public int Bounces { get; private set; }
    /// <summary>A double burger right now.</summary>
    public bool Double => progress && progress.Level >= doubleFromLevel;

    GummyBody body;
    BuddyProgress progress;
    BuddySpeech speech;
    SpriteRenderer filling, seeds;
    AudioSource sfx;
    Sprite single, dbl, seedSprite;
    bool bakedSingle, bakedDouble, bakedSeeds, shownDouble;
    float squish = -1f, lastLine = -100f;
    readonly System.Collections.Generic.Dictionary<GummyBody, float> lastBounce = new System.Collections.Generic.Dictionary<GummyBody, float>();

    const int FillW = 192, FillH1 = 64, FillH2 = 96, SeedW = 128, SeedH = 48;

    void Awake()
    {
        body = GetComponent<GummyBody>();
        progress = GetComponent<BuddyProgress>();
        speech = GetComponent<BuddySpeech>();
        filling = MakeRenderer("Burger Filling");
        seeds = MakeRenderer("Sesame Seeds");
        sfx = gameObject.AddComponent<AudioSource>();
        sfx.playOnAwake = false;
        sfx.spatialBlend = 0f;
    }

    SpriteRenderer MakeRenderer(string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false); // (a child: hiding behind a window masks it too)
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sortingOrder = body.sortingOrder + 1; // over the bun, under the face
        Material m = material ? material : body.spriteMaterial;
        if (m) sr.sharedMaterial = m;
        sr.enabled = false;
        return sr;
    }

    void OnDisable()
    {
        if (filling) filling.enabled = false;
        if (seeds) seeds.enabled = false;
    }

    void OnDestroy()
    {
        foreach (var sp in new[] { single, dbl, seedSprite }) if (sp) { Destroy(sp.texture); Destroy(sp); }
    }

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("BuddyBurger.Update", t0); }
    void UpdateTimed()
    {
        float now = Time.time;
        // the belly trampoline: someone coming down on top of it bounces off
        if (!body.Held)
        {
            Rect e = body.Extent;
            foreach (var o in GummyBody.All)
            {
                if (!o || o == body || o.Held || !o.isActiveAndEnabled) continue;
                Vector2 v = o.Velocity;
                if (v.y > -1.5f) continue;
                var ob = o.GetComponent<GummyBrain>();
                if (ob && ob.Towering) continue; // (climbing onto it in a buddy tower: no bounce)
                float feet = o.Extent.yMin; // (a wide window: something falling fast moves a lot in one frame)
                if (feet < e.yMax - 0.35f * body.size || feet > e.yMax + 0.3f * body.size || o.Center.y < body.Center.y) continue;
                if (Mathf.Abs(o.Center.x - body.Center.x) > e.width * 0.42f) continue;
                if (lastBounce.TryGetValue(o, out float lb) && now - lb < 0.5f) continue;
                lastBounce[o] = now;
                Bounce(o, v);
            }
        }
        if (squish >= 0f)
        {
            // squished by the bounce: down, then springs back
            squish += Time.deltaTime;
            float u = squish / 0.35f, k = Mathf.Exp(-5f * u) * Mathf.Cos(u * 9f);
            body.Pose = new Vector2(1f + 0.16f * k, 1f - 0.2f * k);
            if (squish > 0.35f) { squish = -1f; body.Pose = Vector2.one; }
        }
    }

    void Bounce(GummyBody o, Vector2 v)
    {
        Bounces++;
        float s = Mathf.Sqrt(o.size / 0.6f);
        float up = Mathf.Clamp(-v.y * bounciness + 1.5f, 6f * s, 12f * s);
        float side = Mathf.Clamp((o.Center.x - body.Center.x) * 2f, -1.5f, 1.5f); // off the side it landed on
        o.Hop(new Vector2(side - v.x * 0.3f, up - v.y), 0.5f);
        squish = 0f;
        if (boingClip && sfx) { sfx.pitch = Random.Range(0.8f, 0.95f); sfx.PlayOneShot(boingClip, volume); }
        var os = o.GetComponent<BuddySpeech>();
        if (os) os.Say(BuddySpeech.Topic.Boing, 0.6f, 3f);
        if (speech && Time.time - lastLine > 5f && Random.value < 0.4f)
        {
            lastLine = Time.time;
            speech.Say(Pick("OOF!", "MY BELLY!", "BOUNCY BUN!", "HEHE, TICKLES"), 0.3f, false);
        }
    }

    static string Pick(params string[] l) => l[Random.Range(0, l.Length)];

    void LateUpdate() { long t0 = WhipProf.T(); LateUpdateTimed(); WhipProf.Add("BuddyBurger.LateUpdate", t0); }
    void LateUpdateTimed()
    {
        bool two = Double;
        if (!bakedSingle) { single = BakeFilling(false); bakedSingle = true; }
        if (two && !bakedDouble) { dbl = BakeFilling(true); bakedDouble = true; }
        if (!bakedSeeds) { seedSprite = BakeSeeds(); bakedSeeds = true; }
        if (two != shownDouble || filling.sprite == null) { filling.sprite = two ? dbl : single; shownDouble = two; }
        seeds.sprite = seedSprite;
        filling.enabled = true;
        seeds.enabled = true;

        // across the lower part of the bun (tilting and squashing with it), the seeds on the top
        float ang = body.Angle;
        var rot = Quaternion.Euler(0f, 0f, ang);
        Vector2 up = rot * Vector2.up, span = body.Span;
        float w = Mathf.Max(span.x, body.size * 0.5f), h = Mathf.Max(span.y, body.size * 0.4f);
        float band = two ? 0.42f : 0.3f;
        Vector2 at = body.Center + up * (-(two ? 0.1f : 0.13f) * h);
        filling.transform.position = new Vector3(at.x, at.y, 0f);
        filling.transform.rotation = Quaternion.Euler(0f, 0f, ang);
        filling.transform.localScale = new Vector3(w * 1.05f, h * band / ((two ? FillH2 : FillH1) / (float)FillW), 1f);
        Vector2 st = body.Center + up * (0.31f * h);
        seeds.transform.position = new Vector3(st.x, st.y, 0f);
        seeds.transform.rotation = Quaternion.Euler(0f, 0f, ang);
        seeds.transform.localScale = new Vector3(w * 0.66f, h * 0.2f / (SeedH / (float)SeedW), 1f);
    }

    // ------------------------------------------------------------------ drawing (baked once)

    internal static Sprite BakeFilling(bool two)
    {
        int H = two ? FillH2 : FillH1;
        var tex = new Texture2D(FillW, H, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "Burger Filling" };
        var px = new Color32[FillW * H];
        float texel = 1f / FillW;
        for (int j = 0; j < H; j++)
            for (int i = 0; i < FillW; i++)
            {
                Color c = FillingPaint((i + 0.5f) * texel - 0.5f, (j + 0.5f) / H, texel * FillW / H, two);
                px[j * FillW + i] = new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), Mathf.Clamp01(c.a));
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return Sprite.Create(tex, new Rect(0, 0, FillW, H), new Vector2(0.5f, 0.5f), FillW); // 1 unit wide
    }

    // x -0.5..0.5 across, v 0..1 up the band (tv = one texel in v): the patty (or two), melting cheese, a frilly lettuce leaf
    static Color FillingPaint(float x, float v, float tv, bool two)
    {
        Color outC = new Color(0f, 0f, 0f, 0f);
        float texel = tv; // (x and v texels are about the same size here)
        int patties = two ? 2 : 1;
        float unit = two ? 1f / 1.45f : 1f; // the single layout squeezed into the lower part when there are two
        for (int k = 0; k < patties; k++)
        {
            float b0 = two ? (k == 0 ? 0.04f : 0.42f) : 0.08f;  // bottom of this patty
            float ph = 0.42f * unit, ch = 0.16f * unit;
            // patty: a rounded slab, a bit narrower than the bun, char speckles and grill lines
            Vector2 pc = new Vector2(0f, b0 + ph * 0.5f);
            float hx = 0.475f, hy = ph * 0.5f, r = Mathf.Min(0.08f, hy * 0.9f);
            float dx = Mathf.Abs(x - pc.x) - (hx - r), dy = Mathf.Abs(v - pc.y) - (hy - r);
            float d = new Vector2(Mathf.Max(dx, 0f), Mathf.Max(dy, 0f)).magnitude + Mathf.Min(Mathf.Max(dx, dy), 0f) - r;
            d += 0.006f * Mathf.Sin(x * 90f + k * 2f) * Mathf.Sin(v * 70f); // lumpy edge
            float a = Cover(d, texel);
            if (a > 0f)
            {
                float t = Mathf.Clamp01((v - b0) / ph);
                Color meat = Color.Lerp(new Color(0.30f, 0.16f, 0.09f), new Color(0.47f, 0.27f, 0.15f), t);
                float speck = Hash(Mathf.Floor(x * 70f), Mathf.Floor(v * 40f) + k * 31f);
                if (speck > 0.86f) meat *= 0.8f; else if (speck < 0.08f) meat = Color.Lerp(meat, new Color(0.62f, 0.40f, 0.24f), 0.35f);
                if (Mathf.Abs(Mathf.Repeat(x * 7f + v * 2f, 1f) - 0.5f) < 0.05f && t > 0.45f) meat *= 0.78f; // grill marks
                if (d > -texel * 1.5f) meat *= 0.75f; // its edge
                meat.a = 1f;
                outC = Over(outC, meat, a);
            }
            // cheese: a melting slice over the patty, the corners and a few drips hanging down its side
            float c0 = b0 + ph * 0.82f, c1 = c0 + ch;
            float drip = 0f;
            foreach (float px in k == 0 ? new[] { -0.4f, -0.08f, 0.21f, 0.43f } : new[] { -0.3f, 0.12f, 0.38f })
            {
                float wdt = 0.05f, depth = (k == 0 ? 0.22f : 0.18f) * unit * (0.6f + 0.4f * Hash(px * 10f, k));
                float u = Mathf.Abs(x - px) / wdt;
                if (u < 1f) drip = Mathf.Max(drip, depth * Mathf.Sqrt(1f - u * u));
            }
            float cheeseEdge = 0.47f + 0.015f * Mathf.Sin(v * 40f);
            float cd = Mathf.Max(Mathf.Max(c0 - drip - v, v - c1), Mathf.Abs(x) - cheeseEdge);
            float ca = Cover(cd * 1f, texel);
            if (ca > 0f)
            {
                float tt = Mathf.Clamp01((v - (c0 - drip)) / Mathf.Max(c1 - c0 + drip, 1e-3f));
                Color cheese = Color.Lerp(new Color(0.96f, 0.62f, 0.10f), new Color(1f, 0.86f, 0.30f), tt);
                if (cd > -texel * 1.5f) cheese = Color.Lerp(cheese, new Color(0.85f, 0.50f, 0.06f), 0.6f);
                if (Mathf.Abs(v - (c1 - 0.03f * unit)) < 0.012f && Mathf.Abs(x) < 0.4f) cheese = Color.Lerp(cheese, Color.white, 0.35f); // shine
                cheese.a = 1f;
                outC = Over(outC, cheese, ca);
            }
        }
        // lettuce: a wavy green frill on top, sticking out past the bun at both ends
        float l0 = two ? 0.75f : 0.55f, l1 = two ? 0.92f : 0.82f;
        float top = l1 + 0.05f * Mathf.Sin(x * 55f) + 0.025f * Mathf.Sin(x * 23f + 1f);
        float bot = l0 + 0.03f * Mathf.Sin(x * 41f + 2f);
        float ld = Mathf.Max(Mathf.Max(bot - v, v - top), Mathf.Abs(x) - (0.49f + 0.01f * Mathf.Sin(v * 50f)));
        float la = Cover(ld, texel);
        if (la > 0f)
        {
            float tt = Mathf.Clamp01((v - bot) / Mathf.Max(top - bot, 1e-3f));
            Color leaf = Color.Lerp(new Color(0.28f, 0.58f, 0.16f), new Color(0.56f, 0.86f, 0.34f), tt);
            if (Mathf.Abs(Mathf.Repeat(x * 9f, 1f) - 0.5f) < 0.04f) leaf = Color.Lerp(leaf, new Color(0.80f, 0.95f, 0.6f), 0.4f); // veins
            if (ld > -texel * 1.5f) leaf *= 0.8f;
            leaf.a = 1f;
            outC = Over(outC, leaf, la);
        }
        return outC;
    }

    internal static Sprite BakeSeeds()
    {
        var tex = new Texture2D(SeedW, SeedH, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "Sesame Seeds" };
        var px = new Color32[SeedW * SeedH];
        float texel = 1f / SeedW;
        for (int j = 0; j < SeedH; j++)
            for (int i = 0; i < SeedW; i++)
            {
                Color c = SeedsPaint((i + 0.5f) * texel - 0.5f, (j + 0.5f) * texel, texel);
                px[j * SeedW + i] = new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), Mathf.Clamp01(c.a));
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return Sprite.Create(tex, new Rect(0, 0, SeedW, SeedH), new Vector2(0.5f, 0.5f), SeedW); // 1 unit wide
    }

    // seven sesame seeds over the dome (x -0.5..0.5, y 0..SeedH/SeedW): little cream teardrops at all angles
    static Color SeedsPaint(float x, float y, float texel)
    {
        Color outC = new Color(0f, 0f, 0f, 0f);
        float H = SeedH / (float)SeedW;
        for (int i = 0; i < 7; i++)
        {
            float sx = -0.39f + 0.13f * i + 0.02f * Mathf.Sin(i * 2.7f);
            float sy = H * (0.3f + 0.42f * (1f - (sx / 0.45f) * (sx / 0.45f))) + 0.03f * Mathf.Sin(i * 1.9f);
            float a = (i * 47f % 120f - 60f) * Mathf.Deg2Rad;
            Vector2 p = new Vector2(x - sx, y - sy);
            Vector2 q = new Vector2(Mathf.Cos(a) * p.x + Mathf.Sin(a) * p.y, -Mathf.Sin(a) * p.x + Mathf.Cos(a) * p.y);
            float rx = 0.044f * (q.x > 0f ? 0.85f : 1f), ry = 0.022f;
            float d = (new Vector2(q.x / rx, q.y / ry).magnitude - 1f) * ry;
            float ca = Cover(d, texel);
            if (ca <= 0f) continue;
            Color seed = Color.Lerp(new Color(0.88f, 0.80f, 0.58f), new Color(1f, 0.97f, 0.86f), Mathf.Clamp01(0.5f + q.y / ry * 0.5f));
            seed.a = 1f;
            outC = Over(outC, seed, ca);
        }
        return outC;
    }

    static float Hash(float a, float b)
    {
        float h = Mathf.Sin(a * 12.9898f + b * 78.233f) * 43758.5453f;
        return h - Mathf.Floor(h);
    }

    static Color Over(Color under, Color over, float a)
    {
        float oa = a + under.a * (1f - a);
        if (oa <= 1e-5f) return new Color(0f, 0f, 0f, 0f);
        Color c = (over * a + under * (under.a * (1f - a))) / oa;
        c.a = oa;
        return c;
    }

    static float Cover(float d, float texel) => Mathf.Clamp01(0.5f - d / texel); // d < 0 inside
}
