using UnityEngine;

/// <summary>
/// The coffee bean's gadget (its level-10 gadget: BuddySkills.Kit.Espresso): a tiny espresso cup. When a snack is far
/// away (or it's bored, or you say "espresso") it knocks one back ("GULP!") and buzzes for a few seconds: quicker, longer,
/// flatter hops (GummyBrain.Hurry), a steam trail, and it runs straight up window sides it couldn't jump (the route planner
/// gets a higher wall jump while it buzzes). Then the crash: the jitters, shaking on the spot ("TOO MUCH COFFEE").
/// </summary>
[DefaultExecutionOrder(8)] // after BuddyLaser has set the brain's hurry: the espresso's on top
[RequireComponent(typeof(GummyBody))]
[DisallowMultipleComponent]
public class BuddyEspresso : MonoBehaviour
{
    [Tooltip("Sprite material (WhipIt > Setup assigns it).")]
    public Material material;
    [Tooltip("Buzzing this long after a shot (seconds), then the jitters for crashFor.")]
    public float buzzFor = 5f;
    public float crashFor = 2.2f;
    [Tooltip("At most one shot this often (seconds).")]
    public float cooldown = 30f;
    [Tooltip("How much quicker it hops while it buzzes.")]
    public float hurry = 2.1f;
    [Tooltip("A snack at least this far away (world units) is worth a shot.")]
    public float farSnack = 4.5f;
    [Tooltip("Bored: chance per minute to have one anyway (and do some zoomies).")]
    [Range(0f, 1f)] public float funPerMinute = 0.25f;
    public AudioClip gulpClip;
    [Range(0f, 1f)] public float volume = 0.55f;

    /// <summary>Has it (and is old enough).</summary>
    public bool Has => skills && skills.Gear == BuddySkills.Kit.Espresso && skills.Has(BuddySkills.Skill.Gadget);
    /// <summary>Buzzing right now (after a shot): quicker, and runs up window sides.</summary>
    public bool Buzzing => Time.time < buzzUntil;
    /// <summary>The jitters after the buzz.</summary>
    public bool Crashing => !Buzzing && Time.time < crashUntil;
    /// <summary>How much higher it gets up a window side while it buzzes (world units, x size).</summary>
    public float WallBoost => Buzzing ? 2.5f * S : 0f;
    /// <summary>Shots so far (this session).</summary>
    public int Shots { get; private set; }

    GummyBody body;
    GummyBrain brain;
    BuddySkills skills;
    BuddySpeech speech;
    BuddyEater eater;
    AudioSource sfx;
    SpriteRenderer cup;
    const int Puffs = 12;
    readonly SpriteRenderer[] puff = new SpriteRenderer[Puffs];
    readonly Vector2[] puffV = new Vector2[Puffs];
    readonly float[] puffLife = new float[Puffs];
    float buzzUntil = -1f, crashUntil = -1f, nextShot, drinkT = -1f, nextPuff, lastLine = -100f;
    bool crashSaid, hurried, funShot;
    static Sprite s_cup, s_puff;
    static bool s_baked;
    static readonly System.Random s_fx = new System.Random(23);

    float S => body.size / 0.6f;

    void Awake()
    {
        body = GetComponent<GummyBody>();
        brain = GetComponent<GummyBrain>();
        skills = GetComponent<BuddySkills>();
        speech = GetComponent<BuddySpeech>();
        eater = GetComponent<BuddyEater>();
        cup = MakeRenderer("Espresso Cup", body.sortingOrder + 4); // in its hand, in front of its face
        for (int i = 0; i < Puffs; i++) puff[i] = MakeRenderer("Espresso Steam", body.sortingOrder - 1);
        sfx = gameObject.AddComponent<AudioSource>();
        sfx.playOnAwake = false;
        sfx.spatialBlend = 0f;
        nextShot = Time.time + 2f;
    }

    SpriteRenderer MakeRenderer(string name, int order)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sortingOrder = order;
        sr.color = Color.white;
        Material m = material ? material : body.spriteMaterial;
        if (m) sr.sharedMaterial = m;
        sr.enabled = false;
        return sr;
    }

    void OnDisable()
    {
        buzzUntil = crashUntil = -1f;
        drinkT = -1f;
        if (cup) cup.enabled = false;
        for (int i = 0; i < Puffs; i++) { puffLife[i] = 0f; if (puff[i]) puff[i].enabled = false; }
    }

    /// <summary>Knock one back now (chat "espresso"). False if it can't (no cup yet, just had one, being held).</summary>
    public bool Drink(bool fun = false, bool quiet = false)
    {
        if (!Has || body.Held || drinkT >= 0f || Buzzing || Crashing || Time.time < nextShot - cooldown + 6f) return false;
        Shots++;
        drinkT = 0f;
        nextShot = Time.time + cooldown;
        if (gulpClip && sfx) { sfx.pitch = Rnd(0.95f, 1.1f); sfx.PlayOneShot(gulpClip, volume); }
        if (speech && !quiet && Time.time - lastLine > 4f) { lastLine = Time.time; speech.Say(Pick("GULP!", "ESPRESSO TIME!", "ONE SHOT!", "BEAN FUEL!"), 0f, true); }
        funShot = fun;
        return true;
    }

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("BuddyEspresso.Update", t0); }
    void UpdateTimed()
    {
        if (!Has) return;
        float now = Time.time;
        // a far-away snack it's going for: worth a shot (on its feet, not too often)
        if (drinkT < 0f && !Buzzing && !Crashing && now >= nextShot && body.Grounded && !body.Held && brain && eater && eater.Target && brain.Food.HasValue)
        {
            if (Mathf.Abs(eater.Target.Position.x - body.Center.x) > farSnack * S || eater.Target.SurfaceY - body.Extent.yMin > 3f * S) Drink();
        }
        // bored: one anyway, now and then
        else if (drinkT < 0f && !Buzzing && !Crashing && now >= nextShot && brain && brain.IsFree && body.Grounded && Rnd(0f, 1f) < funPerMinute * Time.deltaTime / 60f)
            Drink(true);

        if (drinkT >= 0f)
        {
            drinkT += Time.deltaTime;
            if (drinkT > 0.75f)
            {
                drinkT = -1f;
                buzzUntil = now + buzzFor;
                crashUntil = buzzUntil + crashFor;
                crashSaid = false;
                if (funShot && brain && brain.IsFree)
                {
                    // zoomies: off to the far side and back
                    Rect r = ScreenBounds.WorldRect;
                    if (ScreenBounds.Multi) { r.xMin = Mathf.Max(r.xMin, body.SupportSpan.x); r.xMax = Mathf.Min(r.xMax, body.SupportSpan.y); } // (along its floor)
                    float x = body.Center.x, go = (x - r.xMin > r.xMax - x ? -1f : 1f) * Rnd(3f, 6f) * S;
                    brain.Visit(Mathf.Clamp(x + go, r.xMin + 0.5f, r.xMax - 0.5f));
                }
            }
        }
        if (Buzzing && brain) { brain.Hurry = Mathf.Max(brain.Hurry, hurry); hurried = true; }
        else if (hurried) { hurried = false; if (brain && brain.Hurry <= hurry + 0.01f) brain.Hurry = 1f; } // (back to normal)
        if (Crashing)
        {
            // the jitters: shaking where it stands
            if (brain) brain.Linger(0.3f);
            if (!crashSaid && speech) { crashSaid = true; speech.Say(Pick("TOO MUCH COFFEE", "BZZZZZT", "MY HEART...", "I SEE SOUNDS"), 0f, true); }
            if (body.Grounded && !body.Held) body.Pose = new Vector2(1f + 0.05f * Mathf.Sin(now * 70f), 1f - 0.05f * Mathf.Sin(now * 70f + 1f));
        }
    }

    static string Pick(params string[] l) => l[(int)Rnd(0f, l.Length - 0.001f)];
    static float Rnd(float a, float b) => a + (float)s_fx.NextDouble() * (b - a);

    void LateUpdate() { long t0 = WhipProf.T(); LateUpdateTimed(); WhipProf.Add("BuddyEspresso.LateUpdate", t0); }
    void LateUpdateTimed()
    {
        if (!s_baked) { s_cup = Bake(32, 32, new Vector2(0.5f, 0.1f), "Espresso Cup", CupPaint); s_puff = Bake(16, 16, new Vector2(0.5f, 0.5f), "Steam", PuffPaint); s_baked = true; }
        float dt = Mathf.Min(Time.deltaTime, 0.05f), ang = body.Angle;
        var rot = Quaternion.Euler(0f, 0f, ang);
        Vector2 up = rot * Vector2.up, right = rot * Vector2.right;
        float w = Mathf.Max(body.Span.x, body.size * 0.5f), h = Mathf.Max(body.Span.y, body.size * 0.5f);

        // the cup: up to its mouth, tipped back
        if (drinkT >= 0f)
        {
            float u = Mathf.Clamp01(drinkT / 0.75f), lift = Mathf.Sin(Mathf.Min(u * 1.6f, 1f) * Mathf.PI * 0.5f) * (1f - Mathf.Clamp01((u - 0.85f) / 0.15f));
            Vector2 p = body.Center + right * (0.42f * w) + up * Mathf.Lerp(-0.15f * h, -0.05f * h, lift);
            cup.sprite = s_cup;
            cup.enabled = true;
            cup.transform.position = new Vector3(p.x, p.y, 0f);
            cup.transform.rotation = Quaternion.Euler(0f, 0f, ang + 70f * lift);
            cup.transform.localScale = Vector3.one * (0.38f * w);
        }
        else if (cup.enabled) cup.enabled = false;

        // steam trail while it buzzes (off its top, left behind as it zooms)
        if (Buzzing && Time.time >= nextPuff)
        {
            nextPuff = Time.time + 0.07f;
            Spawn(body.TopPoint + Random2() * (0.05f * S), -body.Velocity * 0.15f + Vector2.up * Rnd(0.4f, 0.9f));
        }
        for (int i = 0; i < Puffs; i++)
        {
            var sr = puff[i];
            if (puffLife[i] <= 0f) { if (sr.enabled) sr.enabled = false; continue; }
            puffLife[i] -= dt;
            float u = 1f - puffLife[i] / 0.6f;
            puffV[i] *= 1f - 2f * dt;
            sr.transform.position += (Vector3)(puffV[i] * dt);
            sr.transform.localScale = Vector3.one * (0.09f * S * (0.6f + 1.4f * u));
            sr.color = new Color(1f, 1f, 1f, 0.55f * (1f - u));
            sr.enabled = true;
        }
    }

    static Vector2 Random2() => new Vector2(Rnd(-1f, 1f), Rnd(-1f, 1f));

    void Spawn(Vector2 at, Vector2 v)
    {
        for (int i = 0; i < Puffs; i++)
        {
            if (puffLife[i] > 0f) continue;
            puffLife[i] = 0.6f;
            puffV[i] = v;
            puff[i].sprite = s_puff;
            puff[i].transform.position = new Vector3(at.x, at.y, 0f);
            return;
        }
    }

    static Sprite Bake(int w, int h, Vector2 pivot, string name, System.Func<float, float, float, Color> paint)
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

    static float Cover(float d, float texel) => Mathf.Clamp01(0.5f - d / texel);

    // a tiny white espresso cup with a handle on the right, a saucer, dark coffee at the brim
    internal static Color CupPaint(float x, float y, float texel)
    {
        Color c = new Color(0f, 0f, 0f, 0f);
        // saucer
        float sd = new Vector2((x - 0.5f) / 0.46f, (y - 0.1f) / 0.07f).magnitude - 1f;
        float sa = Cover(sd * 0.07f, texel);
        if (sa > 0f) c = new Color(0.92f, 0.92f, 0.94f, sa);
        // handle: a ring on the right
        float hr = (new Vector2(x - 0.72f, y - 0.45f)).magnitude;
        float ha = Cover(Mathf.Abs(hr - 0.1f) - 0.035f, texel) * (x > 0.7f ? 1f : 0f);
        if (ha > 0f) c = Color.Lerp(c, new Color(0.95f, 0.95f, 0.97f), ha);
        if (ha > 0f) c.a = Mathf.Max(c.a, ha);
        // the cup: narrower at the bottom
        float t = Mathf.Clamp01((y - 0.14f) / 0.5f);
        float half = Mathf.Lerp(0.18f, 0.27f, t);
        float d = Mathf.Max(Mathf.Abs(x - 0.5f) - half, Mathf.Max(0.14f - y, y - 0.64f));
        float a = Cover(d, texel);
        if (a > 0f)
        {
            float k = (x - 0.5f) / half;
            Color cupC = new Color(0.98f, 0.98f, 0.99f) * (0.82f + 0.18f * Mathf.Sqrt(Mathf.Max(0f, 1f - k * k)));
            if (y > 0.58f) cupC = new Color(0.32f, 0.18f, 0.1f); // coffee (with crema)
            if (y > 0.58f && Mathf.Abs(k) < 0.6f && y > 0.61f) cupC = new Color(0.62f, 0.42f, 0.24f);
            if (d > -texel * 1.6f) cupC = new Color(0.6f, 0.6f, 0.65f);
            c = Color.Lerp(c, cupC, a);
            c.a = Mathf.Max(c.a, a);
        }
        return c;
    }

    internal static Color PuffPaint(float x, float y, float texel)
    {
        float r = new Vector2(x - 0.5f, y - 0.5f).magnitude;
        return new Color(1f, 1f, 1f, Mathf.Clamp01((0.45f - r) / 0.15f));
    }
}
