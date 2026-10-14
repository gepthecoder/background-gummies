using System;
using UnityEngine;

/// <summary>
/// The buddy's balloon (BuddySkills BALLOON, level 17): a shiny gummy balloon on a string, drawn in code. It blows it up
/// from the top of its head, hangs from it on the way up (GummyBrain.Float.cs flies it, GummyBody glides), and lets go
/// above where it's going: the balloon floats off and away. Touch the balloon with your cursor and it pops (rubber bits,
/// a bang) and the buddy drops. A squeaky blow-up, a pop, a line now and then.
/// </summary>
[DefaultExecutionOrder(-8)] // after the brain decides, before the body simulates
[RequireComponent(typeof(GummyBody))]
[DisallowMultipleComponent]
public class BuddyBalloon : MonoBehaviour
{
    [Tooltip("The balloon (WhipIt > Setup picks one per buddy).")]
    public Color color = new Color(0.30f, 0.62f, 0.95f);
    [Tooltip("The string.")]
    public Color stringColor = new Color(0.95f, 0.95f, 0.97f);
    [Tooltip("Sprite / line material (WhipIt > Setup assigns it).")]
    public Material material;
    [Tooltip("Touch the balloon with your cursor this long and it pops (seconds).")]
    public float popAfter = 0.05f;

    [Header("Sounds")]
    public AudioClip inflateClip;
    public AudioClip popClip;
    [Range(0f, 1f)] public float volume = 0.5f;

    public enum Mode { Off, Inflating, Up, Away, Popped }
    public Mode Current { get; private set; }
    /// <summary>It has hold of a balloon (blowing it up, or hanging from it).</summary>
    public bool Holding => Current == Mode.Inflating || Current == Mode.Up;
    /// <summary>Blown up: it can float.</summary>
    public bool Ready => Current == Mode.Up;
    /// <summary>The middle of the balloon (for tests and the cursor).</summary>
    public Vector2 BalloonCenter => knot + (Vector2)(Quaternion.Euler(0f, 0f, angle) * Vector2.up) * (CenterUp * Width * scale);
    /// <summary>You popped it with the cursor.</summary>
    public event Action Popped;

    /// <summary>Blowing it up takes this long (seconds).</summary>
    public const float InflateTime = 0.6f;

    GummyBody body;
    GummyBrain brain;
    BuddySpeech speech;
    BuddySkills skills;
    BuddyLayer layer;
    SpriteRenderer sr;
    LineRenderer str;
    AudioSource sfx;
    Sprite sprite;
    Color bakedColor;
    bool baked;
    Vector2 knot, knotV;
    float t, angle, scale, touchT, lastLine = -100f, awayLen;
    readonly Vector3[] strPts = new Vector3[9];

    // the drawing: 1 unit wide, TexH / TexW tall; the knot (pivot) near the bottom, the balloon's middle CenterUp above it
    const int TexW = 96, TexH = 112;
    const float KnotY = 0.035f, CenterUp = 0.585f;
    float Width => 1.0f * body.size;            // the balloon's width in the world
    float StringLen => 0.7f * body.size;        // from the top of its head to the knot (BuddyRoute.BalloonAbove plans with these)

    // pop: little bits of rubber
    const int Bits = 9;
    readonly SpriteRenderer[] bit = new SpriteRenderer[Bits];
    readonly Vector2[] bitPos = new Vector2[Bits], bitVel = new Vector2[Bits];
    readonly float[] bitSpin = new float[Bits], bitAng = new float[Bits];
    float bitT = -1f;
    static Sprite s_bit;
    static bool s_bitBaked;

    void Awake()
    {
        body = GetComponent<GummyBody>();
        brain = GetComponent<GummyBrain>();
        speech = GetComponent<BuddySpeech>();
        skills = GetComponent<BuddySkills>();
        layer = GetComponent<BuddyLayer>();
        Material m = material ? material : body.spriteMaterial;
        var go = new GameObject("Balloon");
        go.transform.SetParent(transform, false); // (a child: hiding behind a window masks it too)
        sr = go.AddComponent<SpriteRenderer>();
        sr.sortingOrder = body.sortingOrder - 1; // it grows out from behind its head
        if (m) sr.sharedMaterial = m;
        sr.enabled = false;
        var sg = new GameObject("Balloon String");
        sg.transform.SetParent(transform, false);
        str = sg.AddComponent<LineRenderer>();
        str.useWorldSpace = true;
        str.sharedMaterial = m ? m : new Material(Shader.Find("Sprites/Default"));
        str.sortingOrder = body.sortingOrder - 1;
        str.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        str.receiveShadows = false;
        str.alignment = LineAlignment.View;
        str.numCapVertices = 2;
        str.enabled = false;
        for (int i = 0; i < Bits; i++)
        {
            var bg = new GameObject("Balloon Bit");
            bg.transform.SetParent(transform, false);
            bit[i] = bg.AddComponent<SpriteRenderer>();
            bit[i].sortingOrder = body.sortingOrder + 3;
            if (m) bit[i].sharedMaterial = m;
            bit[i].enabled = false;
        }
        sfx = gameObject.AddComponent<AudioSource>();
        sfx.playOnAwake = false;
        sfx.spatialBlend = 0f;
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
        Current = Mode.Off;
        if (sr) sr.enabled = false;
        if (str) str.enabled = false;
        for (int i = 0; i < Bits; i++) if (bit[i]) bit[i].enabled = false;
        bitT = -1f;
    }

    void OnDestroy()
    {
        if (sprite) { Destroy(sprite.texture); Destroy(sprite); }
    }

    /// <summary>Blow one up (from the top of its head). It's Ready after InflateTime.</summary>
    public void Inflate()
    {
        if (!baked || !Same(bakedColor, color))
        {
            if (sprite) { Destroy(sprite.texture); Destroy(sprite); }
            sprite = Bake(color);
            bakedColor = color;
            baked = true;
        }
        sr.sprite = sprite;
        Current = Mode.Inflating;
        t = touchT = 0f;
        knot = body.TopPoint;
        knotV = Vector2.zero;
        angle = 0f;
        scale = 0.1f;
        sr.enabled = str.enabled = true;
        if (inflateClip && sfx) { sfx.pitch = UnityEngine.Random.Range(0.95f, 1.08f); sfx.PlayOneShot(inflateClip, volume); }
    }

    /// <summary>Lets go of it: it floats off and away.</summary>
    public void LetGo()
    {
        if (!Holding) return;
        bool blown = Current == Mode.Up;
        Current = Mode.Away;
        t = 0f;
        awayLen = StringLen;
        knotV = new Vector2(knotV.x * 0.5f + body.Velocity.x * 0.3f, Mathf.Max(body.Velocity.y, 0.5f));
        if (!blown) scale = Mathf.Max(scale, 0.5f);
        if (blown && speech && Time.time - lastLine > 6f && UnityEngine.Random.value < 0.4f)
        {
            lastLine = Time.time;
            speech.Say(Line(Pick("BYE BALLOON!", "THANKS BALLOON!", "TA-DA!"), Pick("BYE BYE!", "NAILED IT", "TA-DA!"), Pick("BYE BALLOON!", "SMOOTH LANDING", "TA-DA!")), 0f, false);
        }
    }

    /// <summary>Bursts it (you poked it: Popped fires).</summary>
    public void Pop(bool byYou)
    {
        if (Current != Mode.Up && Current != Mode.Inflating && Current != Mode.Away) return;
        Vector2 c = BalloonCenter;
        float w = Width * scale;
        Current = Mode.Popped;
        sr.enabled = false;
        str.enabled = false;
        if (!s_bitBaked) { s_bit = BakeBit(); s_bitBaked = true; }
        for (int i = 0; i < Bits; i++)
        {
            float a = (i + UnityEngine.Random.Range(-0.3f, 0.3f)) / Bits * 2f * Mathf.PI;
            Vector2 d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            bitPos[i] = c + d * w * 0.3f;
            bitVel[i] = d * UnityEngine.Random.Range(2.5f, 5f) * Mathf.Sqrt(body.size / 0.6f) + Vector2.up * 1.5f;
            bitSpin[i] = UnityEngine.Random.Range(-900f, 900f);
            bitAng[i] = UnityEngine.Random.Range(0f, 360f);
            bit[i].sprite = s_bit;
            bit[i].color = color;
            bit[i].enabled = true;
        }
        bitT = 0f;
        if (popClip && sfx) { sfx.pitch = UnityEngine.Random.Range(0.92f, 1.1f); sfx.PlayOneShot(popClip, volume); }
        if (byYou)
        {
            if (speech) speech.Say(Line(Pick("EEK!", "MY BALLOON!", "NOOO!"), Pick("HEY!!", "RUDE!", "NOOO!"), Pick("EEK!", "MY BALLOON!", "SPILLED!")), 0f, true);
            Popped?.Invoke();
        }
    }

    void OnLearned(BuddySkills.Step st)
    {
        if (st.skill == BuddySkills.Skill.Balloon) Invoke(nameof(Hint), 4.4f);
    }
    void Hint() { if (speech) speech.Say(Line("I CAN FLOAT NOW!", "BALLOON TOMATO!", "FLOATY BEAN!"), 2.4f, true); }

    void OnMoment(string what)
    {
        if (!speech || what != "float") return;
        float now = Time.time;
        if (now - lastLine < 15f || UnityEngine.Random.value > 0.5f) return;
        lastLine = now;
        speech.Say(Line(Pick("UP UP AND AWAY!", "WHEEE!", "LOOK, NO HANDS!"), Pick("BALLOON RIDE!", "UP I GO!", "SO FLOATY"), Pick("FLOATY BEAN!", "HOT AIR BEAN", "UP UP!")), 0f, false);
    }

    string Line(string apple, string tomato, string bean) => !speech ? apple : speech.voice == BuddySpeech.Voice.Tomato ? tomato : speech.voice == BuddySpeech.Voice.Bean ? bean : apple;
    static string Pick(params string[] l) => l[UnityEngine.Random.Range(0, l.Length)];

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("BuddyBalloon.Update", t0); }
    void UpdateTimed()
    {
        // your cursor on the balloon: pop
        if ((Current == Mode.Up || (Current == Mode.Inflating && t > InflateTime * 0.6f)) && !body.Held)
        {
            Vector2 d = Grabber.CursorWorld - BalloonCenter;
            float w = Width * scale;
            float rx = 0.46f * w, ry = 0.52f * w;
            bool on = (d.x * d.x) / (rx * rx) + (d.y * d.y) / (ry * ry) < 1f;
            touchT = on ? touchT + Time.deltaTime : 0f;
            if (touchT >= popAfter) Pop(true);
        }
    }

    void LateUpdate() { long t0 = WhipProf.T(); LateUpdateTimed(); WhipProf.Add("BuddyBalloon.LateUpdate", t0); }
    void LateUpdateTimed()
    {
        float dt = Mathf.Min(Time.deltaTime, 0.05f);
        UpdateBits(dt);
        if (Current == Mode.Off || Current == Mode.Popped) return;
        t += dt;
        Vector2 top = body.TopPoint;
        float S = body.size / 0.6f, len = StringLen;

        if (Current == Mode.Inflating)
        {
            // grows out of the top of its head, squeaking, the string coming out with it
            float u = Mathf.Clamp01(t / InflateTime);
            float wob = 0.06f * Mathf.Sin(t * 38f) * (1f - u);
            scale = Mathf.Lerp(0.12f, 1f, Back(u)) + wob;
            Vector2 want = top + Vector2.up * (len * Smooth(u)) + Vector2.right * (0.04f * S * Mathf.Sin(t * 9f));
            knot = Vector2.Lerp(knot, want, 1f - Mathf.Exp(-20f * dt));
            knotV = Vector2.zero;
            angle = Mathf.LerpAngle(angle, 6f * Mathf.Sin(t * 11f) * (1f - u), 1f - Mathf.Exp(-12f * dt));
            if (u >= 1f) { Current = Mode.Up; scale = 1f; }
        }
        else if (Current == Mode.Up)
        {
            // straight above it on a taut string (it pulls): trails sideways when it moves, sways a little
            float ax = top.x + 0.06f * S * Mathf.Sin(t * 1.7f);
            knotV.x += ((ax - knot.x) * 60f - knotV.x * 7f) * dt;
            knotV.y = body.Velocity.y;
            knot.x += knotV.x * dt;
            float dx = Mathf.Clamp(knot.x - top.x, -len * 0.7f, len * 0.7f);
            knot = new Vector2(top.x + dx, top.y + Mathf.Sqrt(len * len - dx * dx));
            scale = 1f;
            angle = Mathf.LerpAngle(angle, Tilt(knot - top), 1f - Mathf.Exp(-10f * dt));
        }
        else // Away
        {
            // off it goes: up, swaying, the string trailing; gone off the top of the screen (or after a while)
            knotV.y = Mathf.Min(knotV.y + 3f * dt, 2.6f * Mathf.Sqrt(S));
            knotV.x = Mathf.Lerp(knotV.x, 0.4f * S * Mathf.Sin(t * 1.3f), 1f - Mathf.Exp(-2f * dt));
            knot += knotV * dt;
            angle = Mathf.LerpAngle(angle, -knotV.x * 8f, 1f - Mathf.Exp(-4f * dt));
            float fade = Mathf.Clamp01(6f - t);
            if (knot.y - Width > ScreenBounds.WorldRect.yMax + 0.2f || fade <= 0f) { Current = Mode.Off; sr.enabled = str.enabled = false; return; }
        }

        sr.transform.position = new Vector3(knot.x, knot.y, 0f);
        sr.transform.rotation = Quaternion.Euler(0f, 0f, angle);
        float w0 = Width * scale;
        float squeak = Current == Mode.Inflating ? 1f + 0.05f * Mathf.Sin(t * 30f) : 1f;
        sr.transform.localScale = new Vector3(w0 * squeak, w0 / squeak, 1f);

        // the string: from its head up to the knot (a slight curve when the balloon lags), or trailing below the balloon
        str.widthMultiplier = 0.012f * S;
        Color sc = stringColor;
        if (layer && layer.Hides(strPts[0]) && layer.Hides(knot)) sc.a = 0f; // (behind a window in front of it)
        str.startColor = str.endColor = sc;
        str.positionCount = strPts.Length;
        if (Current == Mode.Away)
        {
            for (int i = 0; i < strPts.Length; i++)
            {
                float k = i / (strPts.Length - 1f);
                Vector2 p = knot + Vector2.down * (awayLen * k) + Vector2.right * (0.05f * S * Mathf.Sin(t * 6f + k * 5f) * k);
                strPts[i] = new Vector3(p.x, p.y, 0f);
            }
        }
        else
        {
            Vector2 from = top, to = knot, mid = 0.5f * (from + to) + Vector2.right * (-knotV.x * 0.02f);
            for (int i = 0; i < strPts.Length; i++)
            {
                float k = i / (strPts.Length - 1f);
                Vector2 p = (1f - k) * (1f - k) * from + 2f * (1f - k) * k * mid + k * k * to;
                strPts[i] = new Vector3(p.x, p.y, 0f);
            }
        }
        str.SetPositions(strPts);
    }

    void UpdateBits(float dt)
    {
        if (bitT < 0f) return;
        bitT += dt;
        float life = 0.6f, w = Width;
        for (int i = 0; i < Bits; i++)
        {
            bitVel[i] *= 1f - 2.5f * dt;
            bitVel[i].y -= 9f * dt;
            bitPos[i] += bitVel[i] * dt;
            bitAng[i] += bitSpin[i] * dt;
            float k = 1f - Mathf.Clamp01((bitT - life * 0.5f) / (life * 0.5f));
            bit[i].transform.position = new Vector3(bitPos[i].x, bitPos[i].y, 0f);
            bit[i].transform.rotation = Quaternion.Euler(0f, 0f, bitAng[i]);
            float s = w * 0.28f * k;
            bit[i].transform.localScale = new Vector3(s, s, 1f);
        }
        if (bitT > life) { bitT = -1f; for (int i = 0; i < Bits; i++) bit[i].enabled = false; if (Current == Mode.Popped) Current = Mode.Off; }
    }

    // the balloon's up points away from its head, along the string
    static float Tilt(Vector2 d) => d.sqrMagnitude > 1e-6f ? Mathf.Clamp(-Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg, -35f, 35f) : 0f;
    static float Smooth(float u) { u = Mathf.Clamp01(u); return u * u * (3f - 2f * u); }
    static float Back(float u) { u = Mathf.Clamp01(u) - 1f; return 1f + u * u * (2.7f * u + 1.7f); }
    static bool Same(Color a, Color b) => Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) < 0.003f;

    // ------------------------------------------------------------------ drawing it

    static Sprite Bake(Color c)
    {
        var tex = new Texture2D(TexW, TexH, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "Balloon" };
        var px = new Color32[TexW * TexH];
        float texel = 1f / TexW;
        for (int j = 0; j < TexH; j++)
            for (int i = 0; i < TexW; i++)
            {
                Color p = BalloonPaint((i + 0.5f) * texel - 0.5f, (j + 0.5f) * texel, texel, c);
                px[j * TexW + i] = new Color(Mathf.Clamp01(p.r), Mathf.Clamp01(p.g), Mathf.Clamp01(p.b), Mathf.Clamp01(p.a));
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return Sprite.Create(tex, new Rect(0, 0, TexW, TexH), new Vector2(0.5f, KnotY * TexW / TexH), TexW); // 1 unit wide
    }

    // a gummy balloon (x -0.5..0.5, y 0..TexH/TexW): a little egg-shaped (narrower at the bottom), see-through at the
    // middle and richer at the rim, a big shine top left and a small one, the tied knot at the bottom
    static Color BalloonPaint(float x, float y, float texel, Color c)
    {
        Color outC = new Color(0f, 0f, 0f, 0f);
        Vector2 ctr = new Vector2(0f, CenterUp + KnotY);
        float rx = 0.46f, ry = 0.52f;
        float yy = y - ctr.y;
        float rxy = rx * (1f - 0.12f * Mathf.Clamp(-yy / ry, 0f, 1f)); // narrower towards the knot
        Vector2 q = new Vector2(x / rxy, yy / ry);
        float ql = q.magnitude;
        float d = (ql - 1f) * Mathf.Min(rxy, ry); // ~ signed distance
        float a = Cover(d, texel);
        if (a > 0f)
        {
            float rim = Mathf.Clamp01(ql);                    // 0 in the middle, 1 at the edge
            float shade = 0.78f + 0.22f * (1f - rim * rim);   // a bit darker towards the rim
            float lit = Mathf.Clamp01(1f - ((x + 0.12f) * (x + 0.12f) / 0.09f + (yy - 0.18f) * (yy - 0.18f) / 0.12f)); // lit from the top left
            Color body = new Color(c.r * shade, c.g * shade, c.b * shade);
            body = Color.Lerp(body, Color.Lerp(c, Color.white, 0.35f), lit * 0.45f);
            if (rim > 0.9f) body = Color.Lerp(body, new Color(c.r * 0.6f, c.g * 0.6f, c.b * 0.6f), (rim - 0.9f) / 0.1f * 0.8f); // a rim
            float alpha = 0.82f + 0.16f * rim;
            // shines: a big soft one, a small sharp one
            Vector2 s1 = new Vector2((x + 0.17f) / 0.10f, (yy - 0.22f) / 0.16f), s2 = new Vector2((x + 0.04f) / 0.035f, (yy - 0.36f) / 0.035f);
            float sh = Mathf.Clamp01(1.2f - s1.magnitude) * 0.75f + Mathf.Clamp01((1f - s2.magnitude) * 3f);
            body = Color.Lerp(body, Color.white, Mathf.Clamp01(sh));
            outC = new Color(body.r, body.g, body.b, a * Mathf.Max(alpha, Mathf.Clamp01(sh)));
        }
        // the knot: a little tied triangle under it
        float ky = y - KnotY, kh = ctr.y - ry - KnotY + 0.02f;
        if (ky > -0.03f && ky < kh)
        {
            float hwk = 0.018f + 0.04f * Mathf.Clamp01(ky / Mathf.Max(kh, 1e-3f));
            float kd = Mathf.Abs(x) - hwk;
            float ka = Cover(kd, texel) * Cover(-ky - 0.03f, texel);
            if (ka > 0f)
            {
                Color kc = new Color(c.r * 0.7f, c.g * 0.7f, c.b * 0.7f, 1f);
                outC = Over(outC, kc, ka);
            }
        }
        return outC;
    }

    static Sprite BakeBit()
    {
        // a curly scrap of rubber (white: tinted with the balloon's colour)
        const int R = 24;
        var tex = new Texture2D(R, R, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "Balloon Bit" };
        var px = new Color32[R * R];
        float texel = 1f / R;
        for (int j = 0; j < R; j++)
            for (int i = 0; i < R; i++)
            {
                float x = (i + 0.5f) * texel - 0.5f, y = (j + 0.5f) * texel - 0.5f;
                float r = Mathf.Sqrt(x * x + y * y), ang = Mathf.Atan2(y, x);
                float d = Mathf.Abs(r - 0.28f) - 0.09f * (0.5f + 0.5f * Mathf.Sin(ang * 2f)); // a thick crescent
                if (ang > 2.2f || ang < -2.6f) d = 1f;
                float a = Cover(d, texel);
                px[j * R + i] = new Color(1f, 1f, 1f, a);
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return Sprite.Create(tex, new Rect(0, 0, R, R), new Vector2(0.5f, 0.5f), R);
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
