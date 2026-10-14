using UnityEngine;

/// <summary>
/// The buddy's paper plane (BuddySkills PAPER PLANE, level 14): folded from a sheet of ruled notebook paper, drawn in code.
/// It folds out under the buddy when it jumps off a window, the buddy rides on its back (nose a little down, swaying), and
/// when they land the plane carries on alone, flutters down and is gone. GummyBrain flies it (GummyBrain.Glide.cs),
/// GummyBody glides (Glide / StopGlide). A crinkle when it folds, wind while it flies, a line now and then.
/// </summary>
[DefaultExecutionOrder(-8)] // after the brain decides, before the body simulates
[RequireComponent(typeof(GummyBody))]
[DisallowMultipleComponent]
public class BuddyPlane : MonoBehaviour
{
    [Tooltip("Sprite material (WhipIt > Setup assigns it).")]
    public Material material;

    [Header("Sounds")]
    public AudioClip foldClip;
    public AudioClip windLoop;
    [Range(0f, 1f)] public float volume = 0.4f;

    /// <summary>The plane is out under it.</summary>
    public bool Out => shown;

    GummyBody body;
    GummyBrain brain;
    BuddySpeech speech;
    BuddySkills skills;
    SpriteRenderer sr;
    AudioSource wind, sfx;
    bool shown, falling;
    float showT, fallT, dir = 1f, angle, lastLine = -100f;
    Vector2 fallPos, fallVel;
    float fallSpin;
    static Sprite s_plane;
    // the one plane everyone flies (Gašper, 2026-10-03: the same white notebook-paper plane on every buddy)
    static readonly Color PaperColor = new Color(0.98f, 0.98f, 0.96f), LineColor = new Color(0.55f, 0.72f, 0.95f);

    const int TexW = 256, TexH = 108;
    const float Behind = 0.01f; // a hair behind its tail, wings, leaf (the same draw order): they go over it
    static readonly Vector2 Seat = new Vector2(0.5f, 0.74f); // where the buddy sits on it (the top edge at the middle)
    float Length => body.size * 2f;

    void Awake()
    {
        body = GetComponent<GummyBody>();
        brain = GetComponent<GummyBrain>();
        speech = GetComponent<BuddySpeech>();
        skills = GetComponent<BuddySkills>();
        var go = new GameObject("Paper Plane");
        go.transform.SetParent(transform, false); // (a child: hiding behind a window masks it too)
        sr = go.AddComponent<SpriteRenderer>();
        sr.sortingOrder = body.sortingOrder - 1; // under its soft bottom
        Material m = material ? material : body.spriteMaterial;
        if (m) sr.sharedMaterial = m;
        sr.sprite = Plane();
        sr.enabled = false;
        wind = gameObject.AddComponent<AudioSource>();
        wind.playOnAwake = false;
        wind.loop = true;
        wind.spatialBlend = 0f;
        wind.volume = 0f;
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
        shown = falling = false;
        if (sr) sr.enabled = false;
        if (wind) wind.Stop();
    }

    /// <summary>Folds out under it, nose pointing this way (+1 right, -1 left).</summary>
    public void Show(int heading)
    {
        shown = true;
        falling = false;
        showT = 0f;
        dir = heading >= 0 ? 1f : -1f;
        angle = 0f;
        sr.enabled = true;
        var board = GetComponent<BuddySkate>();
        if (board) board.Vanish(); // (never on a paper plane and a board at once)
        if (foldClip && sfx) { sfx.pitch = Random.Range(0.95f, 1.08f); sfx.PlayOneShot(foldClip, volume); }
        if (windLoop && !wind.isPlaying) { wind.clip = windLoop; wind.volume = 0f; wind.Play(); }
    }

    /// <summary>Gone this instant (no flutter): the skateboard is coming out under it.</summary>
    public void Vanish()
    {
        if (shown && wind) wind.Stop();
        shown = falling = false;
        if (sr) sr.enabled = false;
    }

    /// <summary>They've landed: the plane glides on alone a little, flutters down and is gone.</summary>
    public void Drop()
    {
        if (!shown) return;
        shown = false;
        falling = true;
        fallT = 0f;
        fallPos = sr.transform.position;
        fallVel = new Vector2(dir * 2.2f, 0.6f) * Mathf.Sqrt(body.size / 0.6f);
        fallSpin = -dir * Random.Range(160f, 260f);
        if (wind) wind.Stop();
    }

    void OnLearned(BuddySkills.Step st)
    {
        if (st.skill == BuddySkills.Skill.Glide) Invoke(nameof(GlideHint), 4.4f);
    }
    void GlideHint() { if (speech) speech.Say(Line("I CAN FLY NOW!", "AIRMAIL TOMATO!", "BEAN-747 READY!"), 2.4f, true); }

    void OnMoment(string what)
    {
        if (!speech || what != "glide") return;
        float now = Time.time;
        if (now - lastLine < 15f || Random.value > 0.5f) return;
        lastLine = now;
        speech.Say(Line(Pick("WHOOSH!", "I'M FLYING!", "WHEEE!"), Pick("AIRMAIL!", "TOMATO FLIGHT 1!", "LOOK AT ME!"), Pick("BEAN-747!", "FIRST CLASS!", "JET LAG!")), 0f, false);
    }

    string Line(string apple, string tomato, string bean) => speech.voice == BuddySpeech.Voice.Tomato ? tomato : speech.voice == BuddySpeech.Voice.Bean ? bean : apple;
    static string Pick(params string[] l) => l[Random.Range(0, l.Length)];

    void LateUpdate() { long t0 = WhipProf.T(); LateUpdateTimed(); WhipProf.Add("BuddyPlane.LateUpdate", t0); }
    void LateUpdateTimed()
    {
        float dt = Mathf.Min(Time.deltaTime, 0.05f);
        if (falling)
        {
            // on its own: glides on, slows, flutters down, fades out
            fallT += dt;
            fallVel.y -= 3f * dt;
            fallVel.x *= 1f - 1.2f * dt;
            fallPos += fallVel * dt;
            angle += fallSpin * dt * Mathf.Clamp01(fallT * 2f);
            float k = 1f - Mathf.Clamp01((fallT - 0.35f) / 0.35f);
            sr.transform.position = new Vector3(fallPos.x, fallPos.y, Behind);
            sr.transform.rotation = Quaternion.Euler(0f, 0f, angle);
            sr.transform.localScale = new Vector3(Length * dir * k, Length * k, 1f);
            if (fallT > 0.7f) { falling = false; sr.enabled = false; }
            return;
        }
        if (!shown) return;
        if (body.Held) { Drop(); return; }

        showT += dt;
        // under it, nose down a little when it comes down steeply, swaying
        Vector2 vel = body.Velocity;
        float pitch = Mathf.Clamp(Mathf.Atan2(vel.y, Mathf.Max(Mathf.Abs(vel.x), 0.5f)) * Mathf.Rad2Deg * 0.3f, -12f, 8f);
        float sway = 3.5f * Mathf.Sin(Time.time * 2f * Mathf.PI * 1.3f);
        angle = Mathf.LerpAngle(angle, body.Angle + dir * pitch + sway, 1f - Mathf.Exp(-10f * dt));
        float below = body.Center.y - body.Extent.yMin;
        var rot = Quaternion.Euler(0f, 0f, body.Angle);
        Vector2 at = body.Center + (Vector2)(rot * Vector2.down) * (below - 0.02f * body.size);
        float unfold = Mathf.Clamp01(showT / 0.18f), s = Length * Mathf.Lerp(0.2f, 1f, Smooth(unfold));
        sr.transform.position = new Vector3(at.x, at.y, Behind);
        sr.transform.rotation = Quaternion.Euler(0f, 0f, angle);
        sr.transform.localScale = new Vector3(s * dir, s, 1f);

        if (wind && wind.isPlaying)
        {
            float speed = vel.magnitude;
            wind.volume = Mathf.MoveTowards(wind.volume, Mathf.Clamp01(speed / 4f) * volume, dt * 2f);
            wind.pitch = 0.85f + Mathf.Clamp01(speed / 6f) * 0.35f;
        }
    }

    static float Smooth(float u) { u = Mathf.Clamp01(u); return u * u * (3f - 2f * u); }

    // ------------------------------------------------------------------ drawing it

    // side view, nose to the right: the wing (the top, ruled notebook paper) and the folded keel below it
    static Sprite Plane()
    {
        if (s_plane && s_plane.texture) return s_plane;
        Color paper = PaperColor, line = LineColor;
        var tex = new Texture2D(TexW, TexH, TextureFormat.RGBA32, false) { name = "Paper Plane", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        var px = new Color32[TexW * TexH];
        for (int y = 0; y < TexH; y++)
            for (int x = 0; x < TexW; x++)
            {
                Color c = PlanePaint((x + 0.5f) / TexW, (y + 0.5f) / TexH, 1f / TexW, paper, line);
                c.r = Mathf.Clamp01(c.r); c.g = Mathf.Clamp01(c.g); c.b = Mathf.Clamp01(c.b); c.a = Mathf.Clamp01(c.a);
                px[y * TexW + x] = c;
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        // 1 unit long; the buddy sits on the top edge in the middle
        s_plane = Sprite.Create(tex, new Rect(0, 0, TexW, TexH), Seat, TexW, 0, SpriteMeshType.FullRect); // (the whole picture: no tight outline cutting bits off)
        return s_plane;
    }

    // u along it (0 tail .. 1 nose), v up (0..1 over the texture height); texel = one pixel in u. The classic paper plane you
    // know from a mile off: a dart pointing forward, the top wing (ruled notebook paper, a red margin line) and the folded
    // underside below it (in its shade), a crease to the nose, a bold blue-grey outline round both
    internal static Color PlanePaint(float u, float v, float texel, Color paper, Color line)
    {
        float aspect = (float)TexH / TexW;
        Vector2 p = new Vector2(u, v * aspect);
        Vector2 nose = new Vector2(0.985f, 0.205f), tailTop = new Vector2(0.015f, 0.405f), notch = new Vector2(0.15f, 0.2f), tailLow = new Vector2(0.05f, 0.012f);
        float dTop = Tri(p, tailTop, nose, notch), dLow = Tri(p, notch, nose, tailLow);
        Color ink = new Color(0.25f, 0.33f, 0.5f);
        float edge = 0.016f, aa = texel;
        Color outC = new Color(0f, 0f, 0f, 0f);
        if (dLow < aa)
        {
            // the underside: in the shade, a crease along the keel
            Color c = paper * 0.8f;
            float keel = Seg(p, new Vector2(0.3f, 0.125f), nose);
            c = Color.Lerp(c, paper * 0.62f, Mathf.Clamp01(1f - keel / (texel * 2.5f)));
            c = Color.Lerp(c, ink, Mathf.Clamp01((dLow + edge) / (texel * 1.5f)));
            c.a = Mathf.Clamp01(0.5f - dLow / aa);
            outC = Over(outC, c);
        }
        if (dTop < aa)
        {
            Color c = paper;
            // ruled lines across the paper and a red margin line near the tail
            float ruled = Mathf.Repeat((p.y - 0.18f * p.x) / 0.045f, 1f);
            if (ruled < 0.16f) c = Color.Lerp(c, line, 0.7f);
            if (Mathf.Abs(p.x - 0.2f) < texel * 1.6f) c = Color.Lerp(c, new Color(0.95f, 0.45f, 0.5f), 0.8f);
            // the centre fold (from the notch to the nose) a little darker
            float fold = Seg(p, notch, nose);
            c = Color.Lerp(c, paper * 0.75f, Mathf.Clamp01(1f - fold / (texel * 3f)) * 0.8f);
            c = Color.Lerp(c, ink, Mathf.Clamp01((dTop + edge) / (texel * 1.5f)));
            c.a = Mathf.Clamp01(0.5f - dTop / aa);
            outC = Over(outC, c);
        }
        return outC;
    }

    // signed distance to a triangle (negative inside)
    static float Tri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float d = Mathf.Min(Seg(p, a, b), Mathf.Min(Seg(p, b, c), Seg(p, c, a)));
        float s1 = Cross(b - a, p - a), s2 = Cross(c - b, p - b), s3 = Cross(a - c, p - c);
        bool inside = (s1 >= 0f && s2 >= 0f && s3 >= 0f) || (s1 <= 0f && s2 <= 0f && s3 <= 0f);
        return inside ? -d : d;
    }

    static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

    static float Seg(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float h = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Vector2.Dot(ab, ab));
        return (p - a - ab * h).magnitude;
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
