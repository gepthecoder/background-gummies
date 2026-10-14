using UnityEngine;

/// <summary>
/// The buddy's skateboard (BuddySkills SKATEBOARD, level 13), drawn in code like everything else: the same chunky blue board
/// for every buddy (grip-tape top, kicked-up tails, trucks, big wheels). It pops out under its feet when
/// it starts a ride, tilts up for an ollie, flips over for a kickflip and gets kicked away when it hops off.
/// GummyBrain does the riding (GummyBrain.Skate.cs); GummyBody rolls on it (Skate / Drive). Rolling sound + a clack on
/// landings, and a line now and then ("OLLIE!", "KICKFLIP!"). Grinding a window edge (GRIND, level 15): sparks fly off the
/// trucks and it scrapes.
/// </summary>
[DefaultExecutionOrder(-8)] // after the brain decides, before the body simulates
[RequireComponent(typeof(GummyBody))]
[DisallowMultipleComponent]
public class BuddySkate : MonoBehaviour
{
    [Tooltip("Sprite material (WhipIt > Setup assigns it).")]
    public Material material;

    [Header("Sounds")]
    public AudioClip rollLoop;
    public AudioClip clackClip;
    public AudioClip grindLoop;
    [Range(0f, 1f)] public float volume = 0.45f;
    public Color sparkColor = new Color(1f, 0.82f, 0.35f);

    /// <summary>Grinding the edge it rides on (GummyBrain sets it): sparks, scrape, the board a little nose-up.</summary>
    public bool Grinding { get; set; }

    /// <summary>The board is out (it's on it, or it's just being kicked away).</summary>
    public bool Out => shown;
    /// <summary>How high the board holds the body up (deck top above the ground).</summary>
    public float Lift => Length * TexH / TexW * DeckTop;
    float Length => body.size * 1.35f;

    GummyBody body;
    GummyBrain brain;
    BuddySpeech speech;
    BuddySkills skills;
    SpriteRenderer sr;
    AudioSource roll, sfx, scrape;
    const int SparkCount = 18;
    readonly SpriteRenderer[] sparks = new SpriteRenderer[SparkCount];
    readonly Vector2[] sparkPos = new Vector2[SparkCount], sparkVel = new Vector2[SparkCount];
    readonly float[] sparkAge = new float[SparkCount], sparkLife = new float[SparkCount];
    int nextSpark;
    float sparkDue;
    static Sprite s_spark;
    bool shown, hiding, wasGrounded;
    float showT, hideT, tilt, flipT = -1f, lastLine = -100f, boardAngle;
    Vector2 hideFrom, hideVel;
    float hideAngle, hideSpin;
    static Sprite s_board;
    // the one board everyone rides (Gašper, 2026-10-03: the same blue board on every buddy)
    static readonly Color DeckColor = new Color(0.22f, 0.56f, 0.88f), GripColor = new Color(0.17f, 0.17f, 0.20f),
        WheelColor = new Color(0.97f, 0.91f, 0.74f), TruckColor = new Color(0.62f, 0.64f, 0.70f);

    const int TexW = 256, TexH = 60;
    const float Behind = 0.01f; // a hair behind its tail, wings, leaf (the same draw order): they go over it
    const float DeckTop = 45f / TexH; // the top of the deck (pivot), from the bottom of the wheels

    void Awake()
    {
        body = GetComponent<GummyBody>();
        brain = GetComponent<GummyBrain>();
        speech = GetComponent<BuddySpeech>();
        skills = GetComponent<BuddySkills>();
        var go = new GameObject("Skateboard");
        go.transform.SetParent(transform, false); // (a child: hiding behind a window masks it too)
        sr = go.AddComponent<SpriteRenderer>();
        sr.sortingOrder = body.sortingOrder - 1; // its soft bottom sits on the deck
        Material m = material ? material : body.spriteMaterial;
        if (m) sr.sharedMaterial = m;
        sr.sprite = Board();
        sr.enabled = false;
        roll = gameObject.AddComponent<AudioSource>();
        roll.playOnAwake = false;
        roll.loop = true;
        roll.spatialBlend = 0f;
        roll.volume = 0f;
        sfx = gameObject.AddComponent<AudioSource>();
        sfx.playOnAwake = false;
        sfx.spatialBlend = 0f;
        scrape = gameObject.AddComponent<AudioSource>();
        scrape.playOnAwake = false;
        scrape.loop = true;
        scrape.spatialBlend = 0f;
        scrape.volume = 0f;
        var spark = Spark();
        for (int i = 0; i < SparkCount; i++)
        {
            var g = new GameObject("Spark");
            g.transform.SetParent(transform, false);
            var r = g.AddComponent<SpriteRenderer>();
            r.sprite = spark;
            r.sortingOrder = body.sortingOrder + 3;
            if (m) r.sharedMaterial = m;
            r.enabled = false;
            sparks[i] = r;
        }
    }

    void OnEnable()
    {
        if (brain) brain.Moment += OnMoment;
        if (skills) skills.Learned += OnLearned;
    }

    void OnDisable()
    {
        if (brain) brain.Moment -= OnMoment;
        if (skills) skills.Learned -= OnLearned;
        shown = hiding = false;
        Grinding = false;
        if (sr) sr.enabled = false;
        if (roll) roll.Stop();
        if (scrape) scrape.Stop();
        foreach (var r in sparks) if (r) r.enabled = false;
    }

    /// <summary>Out under its feet (pops in).</summary>
    public void Show()
    {
        var plane = GetComponent<BuddyPlane>();
        if (plane) plane.Vanish(); // (never on a board and a paper plane at once)
        shown = true;
        hiding = false;
        showT = 0f;
        tilt = 0f;
        flipT = -1f;
        sr.enabled = true;
        sr.flipY = false;
        wasGrounded = true;
        Clack(0.6f);
        if (rollLoop && !roll.isPlaying) { roll.clip = rollLoop; roll.volume = 0f; roll.Play(); }
    }

    /// <summary>Kicked away: flies up a little, spins and is gone.</summary>
    public void Hide()
    {
        if (!shown) return;
        shown = false;
        hiding = true;
        hideT = 0f;
        hideFrom = sr.transform.position;
        hideVel = new Vector2(-body.Velocity.x * 0.2f + Random.Range(-0.5f, 0.5f), 3.5f) * Mathf.Sqrt(body.size / 0.6f);
        hideAngle = boardAngle;
        hideSpin = Random.value < 0.5f ? 540f : -540f;
        flipT = -1f;
        Grinding = false;
        if (roll) roll.Stop();
        if (scrape) scrape.Stop();
    }

    /// <summary>Gone this instant (no kick-away): the paper plane is folding out under it.</summary>
    public void Vanish()
    {
        shown = hiding = false;
        flipT = -1f;
        Grinding = false;
        if (sr) sr.enabled = false;
        if (roll) roll.Stop();
        if (scrape) scrape.Stop();
    }

    /// <summary>Pops with it: the nose comes up (and with a kickflip the board flips over under it).</summary>
    public void Ollie(bool kickflip)
    {
        tilt = 15f;
        if (kickflip) flipT = 0f;
        Clack(0.8f);
    }

    /// <summary>Back down on its wheels.</summary>
    public void Landed() => Clack(1f);

    void Clack(float loud)
    {
        if (clackClip && sfx) { sfx.pitch = Random.Range(0.92f, 1.08f); sfx.PlayOneShot(clackClip, volume * loud); }
    }

    void OnLearned(BuddySkills.Step st)
    {
        if (st.skill == BuddySkills.Skill.Skate) Invoke(nameof(SkateHint), 4.4f);
    }
    void SkateHint() { if (speech) speech.Say(Line("I GOT A SKATEBOARD!", "SKATEBOARD! RADICAL", "BEAN ON WHEELS!"), 2.4f, true); }

    void OnMoment(string what)
    {
        if (!speech) return;
        float now = Time.time;
        if (what == "skate")
        {
            if (now - lastLine < 20f || Random.value > 0.3f) return;
            lastLine = now;
            speech.Say(Line(Pick("SKATE TIME!", "ROLLIN'!", "WHEEE!"), Pick("RADICAL!", "OUTTA MY WAY!", "TOMATO EXPRESS"), Pick("BEAN SPEED!", "ESPRESSO EXPRESS!", "ZOOOM!")), 0f, false);
        }
        else if (what == "grind")
        {
            if (now - lastLine < 8f || Random.value > 0.5f) return;
            lastLine = now;
            speech.Say(Line(Pick("GRIIIND!", "SPARKS!", "RAIL TIME!"), Pick("SHRED IT!", "TOMATO GRIND!", "HOT RAILS!"), Pick("FRESH GRIND!", "GRIND-PRESSO!", "SPARKS!")), 0f, false);
        }
        else if (what == "ollie" || what == "kickflip")
        {
            if (now - lastLine < 6f || Random.value > 0.55f) return;
            lastLine = now;
            speech.Say(what == "kickflip" ? Line("KICKFLIP!", "SICK FLIP!", "FLIP-PRESSO!") : Line("OLLIE!", "AIR TOMATO!", "HOP-PRESSO!"), 0f, false);
        }
    }

    string Line(string apple, string tomato, string bean) => speech.voice == BuddySpeech.Voice.Tomato ? tomato : speech.voice == BuddySpeech.Voice.Bean ? bean : apple;
    static string Pick(params string[] l) => l[Random.Range(0, l.Length)];

    // ------------------------------------------------------------------ under its feet

    void LateUpdate() { long t0 = WhipProf.T(); LateUpdateTimed(); WhipProf.Add("BuddySkate.LateUpdate", t0); }
    void LateUpdateTimed()
    {
        float dt = Mathf.Min(Time.deltaTime, 0.05f);
        UpdateSparks(dt);
        if (hiding)
        {
            hideT += dt;
            hideVel.y -= body.gravity * 0.5f * dt;
            hideFrom += hideVel * dt;
            hideAngle += hideSpin * dt;
            float k = 1f - Mathf.Clamp01((hideT - 0.12f) / 0.18f);
            sr.transform.position = new Vector3(hideFrom.x, hideFrom.y, Behind);
            sr.transform.rotation = Quaternion.Euler(0f, 0f, hideAngle);
            sr.transform.localScale = new Vector3(Length * k, Length * k, 1f);
            if (hideT > 0.3f) { hiding = false; sr.enabled = false; }
            return;
        }
        if (!shown) return;
        if (body.Held) { Hide(); return; }

        showT += dt;
        bool grounded = body.Grounded;
        if (grounded && !wasGrounded) Landed();
        wasGrounded = grounded;

        // under its feet, turned with it
        float ang = body.Angle;
        var rot = Quaternion.Euler(0f, 0f, ang);
        Vector2 down = rot * Vector2.down;
        float below = body.Center.y - body.Extent.yMin;
        Vector2 at = body.Center + down * below;
        tilt = Mathf.MoveTowards(tilt, Grinding && grounded ? 5f : 0f, 90f * dt); // the nose comes back down after an ollie (a little up on a grind)
        float dir = body.Velocity.x >= 0f ? 1f : -1f;
        float scaleY = 1f;
        sr.flipY = false;
        if (flipT >= 0f)
        {
            // kickflip: it turns over once under its feet
            flipT += dt;
            float c = Mathf.Cos(Mathf.Clamp01(flipT / 0.36f) * 2f * Mathf.PI);
            scaleY = Mathf.Max(Mathf.Abs(c), 0.25f);
            sr.flipY = c < 0f;
            at += (Vector2)(rot * Vector2.down) * (Length * TexH / TexW * 0.35f * (1f - Mathf.Abs(c))); // drops a little away from its feet
            if (flipT > 0.36f) flipT = -1f;
        }
        float pop = Mathf.Clamp01(showT / 0.14f), s = Length * (pop < 1f ? Mathf.Lerp(0.3f, 1.08f, pop) : 1f + 0.08f * Mathf.Max(0f, 1f - (showT - 0.14f) / 0.1f));
        sr.transform.position = new Vector3(at.x, at.y, Behind);
        boardAngle = ang + tilt * dir;
        sr.transform.rotation = Quaternion.Euler(0f, 0f, boardAngle);
        sr.transform.localScale = new Vector3(s, s * scaleY, 1f);

        // grinding: sparks off the back truck, scrape
        bool grinding = Grinding && grounded && Mathf.Abs(body.Velocity.x) > 1f;
        if (grinding && Time.time >= sparkDue)
        {
            sparkDue = Time.time + 0.025f;
            Vector2 truck = at + (Vector2)(rot * Vector2.right) * (-dir * Length * 0.28f) + (Vector2)(rot * Vector2.down) * (Lift * 0.6f);
            EmitSpark(truck, new Vector2(-body.Velocity.x * Random.Range(0.25f, 0.6f), Random.Range(0.6f, 2.4f)) * Mathf.Sqrt(body.size / 0.6f));
        }
        if (scrape)
        {
            if (grinding && grindLoop && !scrape.isPlaying) { scrape.clip = grindLoop; scrape.Play(); }
            scrape.volume = Mathf.MoveTowards(scrape.volume, grinding ? volume * 0.8f : 0f, dt * 4f);
            if (!grinding && scrape.isPlaying && scrape.volume <= 0f) scrape.Stop();
        }

        // rolling sound: louder the faster it goes, quiet in the air
        if (roll && roll.isPlaying)
        {
            float speed = Mathf.Abs(body.Velocity.x);
            float want = grounded ? Mathf.Clamp01(speed / 6f) * volume : 0f;
            roll.volume = Mathf.MoveTowards(roll.volume, want, dt * 3f);
            roll.pitch = 0.8f + Mathf.Clamp01(speed / 7f) * 0.5f;
        }
    }

    void EmitSpark(Vector2 at, Vector2 vel)
    {
        int i = nextSpark;
        nextSpark = (nextSpark + 1) % SparkCount;
        sparkPos[i] = at;
        sparkVel[i] = vel;
        sparkAge[i] = 0f;
        sparkLife[i] = Random.Range(0.18f, 0.4f);
        sparks[i].enabled = true;
    }

    void UpdateSparks(float dt)
    {
        float s = body.size * 0.16f;
        for (int i = 0; i < SparkCount; i++)
        {
            var r = sparks[i];
            if (!r || !r.enabled) continue;
            sparkAge[i] += dt;
            if (sparkAge[i] > sparkLife[i]) { r.enabled = false; continue; }
            sparkVel[i].y -= body.gravity * 0.35f * dt;
            sparkPos[i] += sparkVel[i] * dt;
            float k = 1f - sparkAge[i] / sparkLife[i];
            r.transform.position = new Vector3(sparkPos[i].x, sparkPos[i].y, 0f);
            r.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(sparkVel[i].y, sparkVel[i].x) * Mathf.Rad2Deg);
            r.transform.localScale = new Vector3(s * (0.6f + 0.8f * k), s * 0.5f, 1f);
            Color c = Color.Lerp(new Color(1f, 0.45f, 0.15f), sparkColor, k);
            c.a = Mathf.Clamp01(k * 1.5f);
            r.color = c;
        }
    }

    // a little streak of light (white-hot middle)
    static Sprite Spark()
    {
        if (s_spark) return s_spark;
        const int w = 16, h = 6;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = "Spark", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        var px = new Color32[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float u = (x + 0.5f) / w * 2f - 1f, v = (y + 0.5f) / h * 2f - 1f;
                float d = Mathf.Sqrt(u * u + v * v * 4f);
                float a = Mathf.Clamp01(1.2f - d * 1.2f);
                float core = Mathf.Clamp01(1f - d * 2f);
                px[y * w + x] = new Color(1f, 1f - 0.2f * (1f - core), 1f - 0.6f * (1f - core), a);
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        s_spark = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), w, 0, SpriteMeshType.FullRect);
        return s_spark;
    }

    // ------------------------------------------------------------------ drawing it

    // side view: a long deck with kicked-up tails (grip tape on top), trucks, two wheels; pivot = the middle of the deck top
    static Sprite Board()
    {
        if (s_board && s_board.texture) return s_board;
        Color deck = DeckColor, grip = GripColor, wheel = WheelColor, truck = TruckColor;
        var tex = new Texture2D(TexW, TexH, TextureFormat.RGBA32, false) { name = "Skateboard", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        var px = new Color32[TexW * TexH];
        for (int y = 0; y < TexH; y++)
            for (int x = 0; x < TexW; x++)
            {
                Color c = BoardPaint(x + 0.5f, y + 0.5f, deck, grip, wheel, truck);
                c.r = Mathf.Clamp01(c.r); c.g = Mathf.Clamp01(c.g); c.b = Mathf.Clamp01(c.b); c.a = Mathf.Clamp01(c.a);
                px[y * TexW + x] = c;
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        var sp = Sprite.Create(tex, new Rect(0, 0, TexW, TexH), new Vector2(0.5f, DeckTop), TexW, 0, SpriteMeshType.FullRect); // 1 unit long (the whole picture: no tight outline cutting bits off)
        s_board = sp;
        return sp;
    }

    // the board in pixels (x 0..TexW, y 0..TexH from the bottom of the wheels): a chunky cartoon board you can read at
    // desktop size: a thick deck in its colour with kicked-up tails, black grip tape on top and a white stripe along the
    // side, sturdy grey trucks, big fat wheels with hubs, and a dark outline round all of it
    internal static Color BoardPaint(float x, float y, Color deck, Color grip, Color wheel, Color truck)
    {
        Color ink = new Color(0.13f, 0.13f, 0.2f);
        const float line = 2.6f;
        Color outC = new Color(0f, 0f, 0f, 0f);
        foreach (float wx in new[] { 0.23f * TexW, 0.77f * TexW })
        {
            // the truck: a hanger from under the deck down to the axle
            const float wy = 14.5f;
            float hw = Mathf.Lerp(9f, 16f, Mathf.InverseLerp(wy, 32f, y));
            float dT = Mathf.Max(Mathf.Abs(x - wx) - hw, Mathf.Max(wy - y, y - 32f));
            if (dT < 0.5f)
            {
                Color t = Color.Lerp(truck * 1.1f, truck * 0.8f, Mathf.InverseLerp(wy, 32f, y));
                if (Mathf.Abs(x - wx + hw * 0.4f) < 2f) t = Color.Lerp(t, Color.white, 0.35f);
                t = Color.Lerp(t, ink, Mathf.Clamp01((dT + line) / line));
                t.a = Mathf.Clamp01(0.5f - dT);
                outC = Over(outC, t);
            }
            // the wheel: fat, light, a coloured hub, a darker ring
            float d = Mathf.Sqrt((x - wx) * (x - wx) + (y - wy) * (y - wy)), R = 14f;
            if (d < R + 0.5f)
            {
                Color w = d < 5f ? truck * 0.75f : d < 7f ? Color.Lerp(wheel, truck, 0.5f) : wheel * (0.9f + 0.12f * (y - wy) / R);
                if (d > 7f && (x - wx) < -2f && (y - wy) > 2f && d < 11f) w = Color.Lerp(w, Color.white, 0.4f);    // shine
                w = Color.Lerp(w, ink, Mathf.Clamp01((d - (R - line)) / line));
                w.a = Mathf.Clamp01(R + 0.5f - d);
                outC = Over(outC, w);
            }
        }
        // the deck: a thick band along a centre line that kicks up at both ends, rounded ends
        float u = x / TexW, kick = 0.14f;
        float k = u < kick ? 1f - u / kick : u > 1f - kick ? (u - (1f - kick)) / kick : 0f;
        float cy = 38f + 13f * k * k, half = 7f;
        const float endPad = 6f;
        float ex = x < endPad ? endPad - x : x > TexW - endPad ? x - (TexW - endPad) : 0f;
        float inside = ex > 0f ? Mathf.Sqrt(ex * ex + Mathf.Pow(Mathf.Max(Mathf.Abs(y - cy), 0f), 2f)) - half : Mathf.Abs(y - cy) - half; // signed: < 0 inside
        if (inside < 0.5f)
        {
            float side = (y - cy + half) / (2f * half); // 0 bottom .. 1 top
            Color c = side > 0.74f ? grip : side < 0.24f ? deck * 0.68f : deck * (0.95f + 0.15f * side);
            if (side > 0.42f && side < 0.58f && ex <= 0f) c = Color.Lerp(c, Color.white, 0.75f); // the stripe along its side
            c = Color.Lerp(c, ink, Mathf.Clamp01((inside + line) / line));
            c.a = Mathf.Clamp01(0.5f - inside);
            outC = Over(outC, c);
        }
        return outC;
    }


    static Color Over(Color under, Color over)
    {
        float a = over.a + under.a * (1f - over.a);
        if (a < 1e-5f) return new Color(0f, 0f, 0f, 0f);
        Color c = (over * over.a + under * under.a * (1f - over.a)) / a;
        c.a = a;
        return c;
    }
}
