using UnityEngine;

/// <summary>
/// The tomato's half-broken jetpack (its level-10 gadget: BuddySkills.Kit.Jetpack). Two dented tin cans strapped to its
/// back peek out over its shoulders, nozzles down; flames and smoke when it fires (GummyBrain.Jet.cs flies it). About 6
/// times in 10 it works; otherwise it sputters out on the way up (coughs of black smoke) or backfires (BANG). All drawn in
/// code; its own rumble, sputter and bang.
/// </summary>
[RequireComponent(typeof(GummyBody))]
[DisallowMultipleComponent]
public class BuddyJetpack : MonoBehaviour
{
    [Tooltip("Sprite material (WhipIt > Setup assigns it).")]
    public Material material;
    [Range(0f, 1f), Tooltip("How often it works (the rest it sputters out or backfires).")]
    public float works = 0.6f;
    [Range(0f, 1f), Tooltip("How often it sputters out halfway up (the rest of the failures backfire).")]
    public float sputters = 0.25f;
    public AudioClip rumbleLoop, sputterClip, bangClip;
    [Range(0f, 1f)] public float volume = 0.5f;

    /// <summary>Has it (and is old enough).</summary>
    public bool Has => skills && skills.Gear == BuddySkills.Kit.Jetpack && skills.Has(BuddySkills.Skill.Gadget);
    /// <summary>Flights so far (this session): fired up, worked, sputtered out, backfired.</summary>
    public int Fired { get; private set; }
    public int Sputtered { get; private set; }
    public int Backfired { get; private set; }

    GummyBody body;
    BuddySkills skills;
    BuddySpeech speech;
    AudioSource loop, sfx;
    SpriteRenderer canL, canR, flameL, flameR;
    const int Puffs = 18;
    readonly SpriteRenderer[] puff = new SpriteRenderer[Puffs];
    readonly Vector2[] puffV = new Vector2[Puffs];
    readonly float[] puffLife = new float[Puffs], puffMax = new float[Puffs], puffSize = new float[Puffs];
    float thrust, shown, flick, nextPuff, lastLine = -100f, cough = -1f;
    static Sprite s_can, s_flame, s_puff;
    static bool s_baked;
    static readonly System.Random s_fx = new System.Random(11); // (its own dice for the looks: the behaviour doesn't depend on the smoke)

    float S => body.size / 0.6f;

    void Awake()
    {
        body = GetComponent<GummyBody>();
        skills = GetComponent<BuddySkills>();
        speech = GetComponent<BuddySpeech>();
        int o = body.sortingOrder;
        flameL = MakeRenderer("Jet Flame L", o - 2);
        flameR = MakeRenderer("Jet Flame R", o - 2);
        canL = MakeRenderer("Jetpack Can L", o - 1); // behind the body: over its shoulders
        canR = MakeRenderer("Jetpack Can R", o - 1);
        for (int i = 0; i < Puffs; i++) { puff[i] = MakeRenderer("Jet Smoke", o - 2); }
        loop = gameObject.AddComponent<AudioSource>();
        loop.playOnAwake = false;
        loop.loop = true;
        loop.spatialBlend = 0f;
        loop.volume = 0f;
        sfx = gameObject.AddComponent<AudioSource>();
        sfx.playOnAwake = false;
        sfx.spatialBlend = 0f;
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

    void OnEnable() { if (skills) skills.Learned += OnLearned; }

    void OnDisable()
    {
        if (skills) skills.Learned -= OnLearned;
        thrust = 0f;
        if (loop) loop.Stop();
        foreach (var sr in new[] { canL, canR, flameL, flameR }) if (sr) sr.enabled = false;
        for (int i = 0; i < Puffs; i++) { puffLife[i] = 0f; if (puff[i]) puff[i].enabled = false; }
    }

    void OnLearned(BuddySkills.Step st)
    {
        if (st.skill == BuddySkills.Skill.Gadget && skills.Gear == BuddySkills.Kit.Jetpack) Invoke(nameof(Hint), 4.4f);
    }

    void Hint() { if (speech) speech.Say("IT... MOSTLY WORKS", 2.6f, true); }

    // ------------------------------------------------------------------ what the brain does with it

    /// <summary>Fire it up (a cough or two of smoke, the rumble starts).</summary>
    public void Ignite()
    {
        Fired++;
        thrust = 0.3f;
        for (int i = 0; i < 3; i++) Puff(Nozzle(i % 2 == 0 ? -1 : 1), new Vector2(Rnd(-0.6f, 0.6f), Rnd(-0.3f, 0.3f)), 0.5f, true);
        if (rumbleLoop && loop) { loop.clip = rumbleLoop; loop.volume = 0f; loop.Play(); }
    }

    /// <summary>Throttle (0 = off, 1 = full).</summary>
    public void Thrust(float t)
    {
        thrust = Mathf.Clamp01(t);
        if (thrust <= 0f && loop && loop.isPlaying && cough < 0f) loop.Stop();
    }

    /// <summary>It coughs out: pops, black smoke, flame gone.</summary>
    public void Sputter()
    {
        Sputtered++;
        thrust = 0f;
        cough = 0f;
        Play(sputterClip, Rnd(0.92f, 1.08f));
        for (int i = 0; i < 6; i++) Puff(Nozzle(i % 2 == 0 ? -1 : 1), new Vector2(Rnd(-0.8f, 0.8f), Rnd(-0.6f, 0.4f)), 0.8f, true);
    }

    /// <summary>BANG: a burst of flame and black smoke.</summary>
    public void Backfire()
    {
        Backfired++;
        thrust = 0f;
        cough = 0f;
        Play(bangClip, Rnd(0.95f, 1.05f));
        for (int i = 0; i < 10; i++) { float a = i / 10f * Mathf.PI * 2f; Puff(body.Center, new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * Rnd(1.2f, 2.2f), 1.1f, true); }
    }

    /// <summary>Says one of these (sometimes: chance), not too often.</summary>
    public void Say(float chance, params string[] lines)
    {
        if (!speech || Time.time - lastLine < 3f || Rnd(0f, 1f) > chance) return;
        if (speech.Say(lines[(int)Rnd(0f, lines.Length - 0.001f)], 0f, false)) lastLine = Time.time;
    }

    void Play(AudioClip clip, float pitch)
    {
        if (!clip || !sfx) return;
        sfx.pitch = pitch;
        sfx.PlayOneShot(clip, volume);
    }

    static float Rnd(float a, float b) => a + (float)s_fx.NextDouble() * (b - a);

    // ------------------------------------------------------------------ drawing

    // the bottom of a can's nozzle (side -1 left, +1 right)
    Vector2 Nozzle(int side)
    {
        float ang = body.Angle;
        var rot = Quaternion.Euler(0f, 0f, ang);
        Vector2 up = rot * Vector2.up, right = rot * Vector2.right;
        Vector2 span = body.Span;
        float w = Mathf.Max(span.x, body.size * 0.5f), h = Mathf.Max(span.y, body.size * 0.5f);
        return body.Center + right * (side * 0.47f * w) + up * (-0.1f * h);
    }

    void LateUpdate() { long t0 = WhipProf.T(); LateUpdateTimed(); WhipProf.Add("BuddyJetpack.LateUpdate", t0); }
    void LateUpdateTimed()
    {
        if (!s_baked) { s_can = Bake(24, 48, new Vector2(0.5f, 0.08f), "Jetpack Can", CanPaint); s_flame = Bake(24, 48, new Vector2(0.5f, 1f), "Jet Flame", FlamePaint); s_puff = Bake(24, 24, new Vector2(0.5f, 0.5f), "Jet Smoke", PuffPaint); s_baked = true; }
        float dt = Mathf.Min(Time.deltaTime, 0.05f);
        bool has = Has;
        float ang = body.Angle;
        var rot = Quaternion.Euler(0f, 0f, ang);
        Vector2 up = rot * Vector2.up, right = rot * Vector2.right;
        Vector2 span = body.Span;
        float w = Mathf.Max(span.x, body.size * 0.5f), h = Mathf.Max(span.y, body.size * 0.5f);

        // the cans over its shoulders (tilted out a little), shaking with the thrust
        float shake = thrust > 0f ? 0.012f * S * Mathf.Sin(Time.time * 60f) * thrust : 0f;
        for (int side = -1; side <= 1; side += 2)
        {
            var can = side < 0 ? canL : canR;
            Vector2 p = Nozzle(side) + up * shake;
            can.sprite = s_can;
            can.enabled = has;
            can.transform.position = new Vector3(p.x, p.y, 0f);
            can.transform.rotation = Quaternion.Euler(0f, 0f, ang - side * 8f);
            can.transform.localScale = new Vector3(side * 0.24f * w, 0.24f * w, 1f); // (the sprite is 1 x 2: 0.24 w wide, 0.48 w tall)
        }

        // flames: flicker with the throttle (and splutter when it coughs)
        shown = Mathf.MoveTowards(shown, thrust, dt * 6f);
        flick += dt * 40f;
        if (cough >= 0f) { cough += dt; if (cough > 0.7f) { cough = -1f; if (loop) loop.Stop(); } }
        float sput = cough >= 0f ? (Mathf.Sin(cough * 50f) > 0.3f ? 0.5f * (1f - cough / 0.7f) : 0f) : 0f;
        float f = Mathf.Max(shown, sput);
        for (int side = -1; side <= 1; side += 2)
        {
            var fl = side < 0 ? flameL : flameR;
            Vector2 p = Nozzle(side) + up * shake;
            float jitter = 1f + 0.18f * Mathf.Sin(flick + side * 1.7f) + 0.1f * Mathf.Sin(flick * 2.3f + side);
            fl.sprite = s_flame;
            fl.enabled = has && f > 0.02f;
            fl.transform.position = new Vector3(p.x, p.y, 0f);
            fl.transform.rotation = Quaternion.Euler(0f, 0f, ang - side * 8f);
            fl.transform.localScale = new Vector3(0.2f * w * Mathf.Lerp(0.6f, 1f, f), 0.2f * w * f * jitter * 1.6f, 1f);
        }
        if (loop && loop.isPlaying)
        {
            loop.volume = Mathf.MoveTowards(loop.volume, volume * Mathf.Max(shown, sput) * 0.9f, dt * 3f);
            loop.pitch = 0.8f + 0.4f * shown + (cough >= 0f ? 0.25f * Mathf.Sin(cough * 40f) : 0f);
        }

        // exhaust: light smoke while it flies
        if (has && shown > 0.3f && Time.time >= nextPuff)
        {
            nextPuff = Time.time + Mathf.Lerp(0.09f, 0.04f, shown);
            int side = Rnd(0f, 1f) < 0.5f ? -1 : 1;
            Puff(Nozzle(side) - up * (0.2f * w * shown), -up * Rnd(0.5f, 1.2f) + right * Rnd(-0.3f, 0.3f), 0.45f, false);
        }
        UpdatePuffs(dt);
    }

    void Puff(Vector2 at, Vector2 v, float life, bool black)
    {
        for (int i = 0; i < Puffs; i++)
        {
            if (puffLife[i] > 0f) continue;
            puffLife[i] = puffMax[i] = life * Rnd(0.8f, 1.2f);
            puffV[i] = v;
            puffSize[i] = (black ? 0.16f : 0.11f) * S * Rnd(0.8f, 1.2f);
            var sr = puff[i];
            sr.sprite = s_puff;
            float g = black ? Rnd(0.15f, 0.3f) : Rnd(0.75f, 0.9f);
            sr.color = new Color(g, g, g, black ? 0.9f : 0.6f);
            sr.transform.position = new Vector3(at.x, at.y, 0f);
            return;
        }
    }

    void UpdatePuffs(float dt)
    {
        for (int i = 0; i < Puffs; i++)
        {
            var sr = puff[i];
            if (puffLife[i] <= 0f) { if (sr.enabled) sr.enabled = false; continue; }
            puffLife[i] -= dt;
            float u = 1f - puffLife[i] / puffMax[i];
            puffV[i] = puffV[i] * (1f - 2.5f * dt) + Vector2.up * (0.8f * dt);
            sr.transform.position += (Vector3)(puffV[i] * dt);
            sr.transform.localScale = Vector3.one * (puffSize[i] * (0.6f + 1.2f * u));
            Color c = sr.color;
            c.a = (1f - u) * (c.r < 0.5f ? 0.9f : 0.6f);
            sr.color = c;
            sr.enabled = puffLife[i] > 0f;
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
        return Sprite.Create(tex, new Rect(0, 0, w, h), pivot, w); // 1 unit wide
    }

    static float Cover(float d, float texel) => Mathf.Clamp01(0.5f - d / texel);

    // a dented tin can (x 0..1, y 0..2): silver with a red band, a rounded lid, a rivet, a dent; a dark nozzle at the bottom
    internal static Color CanPaint(float x, float y, float texel)
    {
        Color c = new Color(0f, 0f, 0f, 0f);
        // nozzle: a dark flared cone at the bottom
        if (y < 0.36f)
        {
            float half = 0.2f + 0.16f * (0.36f - y) / 0.36f;
            float d = Mathf.Max(Mathf.Abs(x - 0.5f) - half, Mathf.Max(0.02f - y, y - 0.36f));
            float a = Cover(d, texel);
            if (a > 0f) { c = new Color(0.25f, 0.24f, 0.26f) * (0.8f + 0.4f * (1f - Mathf.Abs(x - 0.5f) / half)); c.a = a; }
        }
        // the can: a rounded rectangle from y 0.3 to 1.95, a dent on its side
        float dent = 0.05f * Mathf.Exp(-Mathf.Pow((y - 1.15f) / 0.12f, 2f));
        float hx = 0.4f - (x > 0.5f ? dent : 0f), cy = 1.12f, hy = 0.82f, r = 0.16f;
        float dx = Mathf.Abs(x - 0.5f) - (hx - r), dy = Mathf.Abs(y - cy) - (hy - r);
        float dd = new Vector2(Mathf.Max(dx, 0f), Mathf.Max(dy, 0f)).magnitude + Mathf.Min(Mathf.Max(dx, dy), 0f) - r;
        float ca = Cover(dd, texel);
        if (ca > 0f)
        {
            float k = (x - 0.5f) / hx; // -1 .. 1 across (a cylinder)
            float shade = 0.62f + 0.38f * Mathf.Sqrt(Mathf.Max(0f, 1f - k * k)) - 0.12f * k;
            Color metal = new Color(0.78f, 0.80f, 0.84f) * shade;
            if (Mathf.Abs(y - 1.42f) < 0.14f) metal = new Color(0.86f, 0.18f, 0.16f) * shade;               // red band
            if (Mathf.Abs(k + 0.4f) < 0.1f && y > 0.5f && y < 1.85f) metal = Color.Lerp(metal, Color.white, 0.45f); // shine
            if ((new Vector2(x - 0.32f, y - 0.75f)).magnitude < 0.05f) metal = new Color(0.45f, 0.46f, 0.5f);   // rivet
            if (Mathf.Abs(y - 1.05f) < 0.015f || Mathf.Abs(y - 0.55f) < 0.015f) metal *= 0.8f;                // seams
            if (dent > 0.01f && x > 0.62f) metal *= 0.82f;                                                   // the dent's shadow
            if (dd > -texel * 1.8f) metal = new Color(0.32f, 0.33f, 0.37f);                                  // edge
            metal.a = 1f;
            float oa = ca + c.a * (1f - ca);
            c = (metal * ca + c * (c.a * (1f - ca))) / Mathf.Max(oa, 1e-4f);
            c.a = oa;
        }
        return c;
    }

    // a flame hanging down from the nozzle (pivot at its top): white-yellow core, orange, a red tip that fades
    internal static Color FlamePaint(float x, float y, float texel)
    {
        float v = 1f - y / 2f; // 0 at the nozzle .. 1 at the tip
        float half = 0.42f * Mathf.Sqrt(Mathf.Max(0f, 1f - v)) * (1f - 0.3f * v) + 0.02f;
        float d = Mathf.Abs(x - 0.5f) - half;
        float a = Cover(d, texel) * Mathf.Clamp01((1f - v) * 2.2f);
        if (a <= 0f) return new Color(0f, 0f, 0f, 0f);
        float k = Mathf.Abs(x - 0.5f) / Mathf.Max(half, 1e-3f);
        Color c = Color.Lerp(new Color(1f, 0.98f, 0.8f), new Color(1f, 0.62f, 0.12f), Mathf.Clamp01(k * 1.3f + v * 0.8f));
        c = Color.Lerp(c, new Color(0.95f, 0.2f, 0.08f), Mathf.Clamp01((v - 0.5f) * 2f));
        c.a = a;
        return c;
    }

    // a soft round puff of smoke (white: tinted grey / black)
    internal static Color PuffPaint(float x, float y, float texel)
    {
        Vector2 q = new Vector2(x - 0.5f, y - 0.5f);
        float r = q.magnitude + 0.015f * Mathf.Sin(Mathf.Atan2(q.y, q.x) * 5f);
        float a = Mathf.Clamp01((0.46f - r) / 0.12f);
        float shade = 0.85f + 0.3f * Mathf.Clamp01(-q.x - q.y + 0.2f);
        return new Color(shade, shade, shade, a);
    }
}
