using UnityEngine;

/// <summary>
/// A cute face for code-drawn buddies: big eyes that follow the cursor, blinking, blush,
/// a smile that turns into a surprised "o" while you hold it. Builds itself under the body visual (first child) at Start.
/// Babies (levels 1 and 2) have bigger eyes with an extra sparkle. toothy: a big open grin with little pointy teeth instead
/// of the smile (the dino).
/// Layout values are fractions of <see cref="scale"/> (the body's width), so it works for any species.
/// </summary>
[DisallowMultipleComponent]
public class CuteFace : MonoBehaviour
{
    [Tooltip("Body width in world units. ProceduralApple sets this.")]
    public float scale = 0.6f;
    public Material material;
    public int sortingOrder = 6;

    [Header("Layout (fractions of scale)")]
    public Vector2 eyeOffset = new Vector2(0.16f, -0.06f);
    public float eyeSize = 0.24f;
    public float pupilSize = 0.15f;
    public Vector2 blushOffset = new Vector2(0.30f, -0.16f);
    public Vector2 blushSize = new Vector2(0.14f, 0.08f);
    public Vector2 mouthOffset = new Vector2(0f, -0.19f);
    public float mouthWidth = 0.12f;
    [Tooltip("A big open grin with little pointy teeth instead of the smile (the dino). mouthWidth = the grin's width.")]
    public bool toothy;

    [Header("Colours")]
    public Color pupilColor = new Color(0.17f, 0.11f, 0.07f);
    public Color blushColor = new Color(1f, 0.55f, 0.65f, 0.55f);

    [Tooltip("Levels up to this one get the big baby eyes (0 = never).")]
    public int babyUntilLevel = 2;
    [Tooltip("How much bigger the baby eyes are.")]
    public float babyEyes = 0.32f;

    [Header("Life")]
    public Vector2 blinkInterval = new Vector2(2f, 5f);
    public float blinkTime = 0.14f;

    /// <summary>Closed "u u" eyes and a little snoring "o" (set by GummyBrain).</summary>
    public bool Asleep { get; set; }

    public enum Expression { Normal, Happy, Grumpy, Strain, Dizzy }
    /// <summary>Current mood (set by GummyBrain). Normal = the default face.</summary>
    public Expression Mood { get; set; }

    /// <summary>The whole face hidden (a fused monster wears its own: MonsterLook).</summary>
    public bool Hidden { get; set; }
    /// <summary>Look there instead of at your cursor (null: the cursor).</summary>
    public Vector2? LookAt { get; set; }
    /// <summary>Amazed: big shiny eyes and an "o" mouth (inside a window it broke into: WindowInfestation).</summary>
    public bool Amazed { get; set; }

    Transform eyeL, eyeR, pupilL, pupilR;
    GameObject faceGo;
    SpriteRenderer sparkL, sparkR;
    Vector3 eyeLPos, eyeRPos;
    BuddyProgress progress;
    float baby = -1f;
    SpriteRenderer smile, mouthO, sleepL, sleepR;
    SpriteRenderer happyL, happyR, spiralL, spiralR, browL, browR, frown, blushL, blushR;
    Vector3 mouthOScale, smileScale;
    Color blushBase;
    IGrabTarget grab;
    float nextBlink, blinkT = -1f;

    static Sprite s_circle, s_smile, s_spiral, s_grin;

    void Start()
    {
        TryGetComponent(out grab);
        TryGetComponent(out progress);
        Transform body = transform.childCount > 0 ? transform.GetChild(0) : transform;
        faceGo = new GameObject("Face");
        var face = faceGo.transform;
        face.SetParent(body, false);

        float s = scale;
        blushL = MakeSprite(face, "Blush L", Circle, new Vector2(-blushOffset.x, blushOffset.y) * s, blushSize * s, blushColor, 0);
        blushR = MakeSprite(face, "Blush R", Circle, new Vector2(blushOffset.x, blushOffset.y) * s, blushSize * s, blushColor, 0);
        blushBase = blushColor;
        eyeL = MakeEye(face, new Vector2(-eyeOffset.x, eyeOffset.y) * s, out pupilL);
        eyeR = MakeEye(face, new Vector2(eyeOffset.x, eyeOffset.y) * s, out pupilR);
        eyeLPos = eyeL.localPosition;
        eyeRPos = eyeR.localPosition;
        sparkL = MakeSprite(pupilL, "Sparkle", Circle, new Vector2(-0.26f, -0.24f), Vector2.one * 0.2f, Color.white, 2); // baby eyes: a second little shine
        sparkR = MakeSprite(pupilR, "Sparkle", Circle, new Vector2(-0.26f, -0.24f), Vector2.one * 0.2f, Color.white, 2);
        float mw = mouthWidth / 0.625f * s; // smile sprite: arc covers 62.5% of its width
        float gw = mouthWidth / 0.88f * s;  // grin sprite: the mouth covers 88% of its width
        smile = toothy ? MakeSprite(face, "Grin", Grin, mouthOffset * s, new Vector2(gw, gw), Color.white, 1)
                       : MakeSprite(face, "Smile", Smile, mouthOffset * s, new Vector2(mw, mw), pupilColor, 1);
        mouthO = MakeSprite(face, "Mouth O", Circle, mouthOffset * s, new Vector2(0.07f, 0.085f) * s, pupilColor, 1);
        mouthO.enabled = false;
        mouthOScale = mouthO.transform.localScale;
        float ew = eyeSize * 0.8f / 0.625f * s; // closed eyes = little smile arcs
        sleepL = MakeSprite(face, "Eye Closed L", Smile, new Vector2(-eyeOffset.x, eyeOffset.y) * s, new Vector2(ew, ew), pupilColor, 1);
        sleepR = MakeSprite(face, "Eye Closed R", Smile, new Vector2(eyeOffset.x, eyeOffset.y) * s, new Vector2(ew, ew), pupilColor, 1);
        sleepL.enabled = sleepR.enabled = false;
        smileScale = smile.transform.localScale;

        // mood parts (hidden until needed)
        Vector2 el = new Vector2(-eyeOffset.x, eyeOffset.y) * s, er = new Vector2(eyeOffset.x, eyeOffset.y) * s;
        happyL = MakeSprite(face, "Eye Happy L", Smile, el, new Vector2(ew, ew) * 0.8f, pupilColor, 1); // "^ ^"
        happyR = MakeSprite(face, "Eye Happy R", Smile, er, new Vector2(ew, ew) * 0.8f, pupilColor, 1);
        happyL.flipY = happyR.flipY = true;
        float sw = eyeSize * 1.05f * s;
        spiralL = MakeSprite(face, "Eye Dizzy L", Spiral, el, new Vector2(sw, sw), pupilColor, 1);
        spiralR = MakeSprite(face, "Eye Dizzy R", Spiral, er, new Vector2(sw, sw), pupilColor, 1);
        Vector2 bs = new Vector2(0.13f, 0.035f) * s, lift = new Vector2(0f, 0.17f * s);
        browL = MakeSprite(face, "Brow L", Circle, el + lift, bs, pupilColor, 2);
        browR = MakeSprite(face, "Brow R", Circle, er + lift, bs, pupilColor, 2);
        browL.transform.localRotation = Quaternion.Euler(0f, 0f, -18f); // inner ends down = grumpy
        browR.transform.localRotation = Quaternion.Euler(0f, 0f, 18f);
        frown = MakeSprite(face, "Frown", Smile, mouthOffset * s + new Vector2(0f, 0.02f * s), new Vector2(mw, mw) * 0.9f, pupilColor, 1);
        frown.flipY = true;
        happyL.enabled = happyR.enabled = spiralL.enabled = spiralR.enabled = false;
        browL.enabled = browR.enabled = frown.enabled = false;

        nextBlink = Time.time + Random.Range(blinkInterval.x, blinkInterval.y);
    }

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("CuteFace.Update", t0); }
    void UpdateTimed()
    {
        if (!eyeL) return;
        if (faceGo && faceGo.activeSelf == Hidden) faceGo.SetActive(!Hidden);
        if (Hidden) return;

        Vector2 target = LookAt ?? Grabber.CursorWorld;
        Look(eyeL, pupilL, target);
        Look(eyeR, pupilR, target);

        float open = 1f;
        if (blinkT < 0f && Time.time >= nextBlink) blinkT = 0f;
        if (blinkT >= 0f)
        {
            blinkT += Time.deltaTime;
            float u = blinkT / blinkTime;
            if (u >= 1f) { blinkT = -1f; nextBlink = Time.time + Random.Range(blinkInterval.x, blinkInterval.y); }
            else open = Mathf.Max(Mathf.Abs(u * 2f - 1f), 0.08f); // close then open
        }
        bool asleep = Asleep;
        Expression m = asleep ? Expression.Normal : Mood;
        bool dizzy = m == Expression.Dizzy, happy = m == Expression.Happy;
        bool strain = m == Expression.Strain, grumpy = m == Expression.Grumpy || strain;

        // eyes (bigger and sparklier on a baby; they grow back to normal over a moment when it levels up past it)
        float wantBaby = progress && babyUntilLevel > 0 && progress.Level <= babyUntilLevel ? 1f : 0f;
        baby = baby < 0f ? wantBaby : Mathf.MoveTowards(baby, wantBaby, Time.deltaTime * 0.8f);
        float big = (1f + babyEyes * baby) * (Amazed && !asleep ? 1.3f : 1f);
        eyeL.localPosition = new Vector3(eyeLPos.x * (1f + 0.12f * baby), eyeLPos.y + 0.02f * scale * baby, 0f);
        eyeR.localPosition = new Vector3(eyeRPos.x * (1f + 0.12f * baby), eyeRPos.y + 0.02f * scale * baby, 0f);
        sparkL.enabled = sparkR.enabled = baby > 0.5f || Amazed;
        bool plainEyes = !asleep && !dizzy && !happy;
        var eyeScale = plainEyes ? new Vector3(big, (strain ? 0.35f : open) * big, 1f) : Vector3.zero;
        eyeL.localScale = eyeScale;
        eyeR.localScale = eyeScale;
        sleepL.enabled = sleepR.enabled = asleep;
        happyL.enabled = happyR.enabled = happy;
        spiralL.enabled = spiralR.enabled = dizzy;
        if (dizzy)
        {
            var spin = Quaternion.Euler(0f, 0f, -Time.time * 540f);
            spiralL.transform.localRotation = spin;
            spiralR.transform.localRotation = spin;
        }
        browL.enabled = browR.enabled = grumpy;

        // mouth: strain > held > asleep > dizzy > grumpy > happy > normal
        bool held = grab != null && grab.Held;
        bool showO = !strain && (held || asleep || dizzy || Amazed);
        bool showFrown = strain || (grumpy && !held);
        mouthO.enabled = showO;
        mouthO.transform.localScale = asleep ? mouthOScale * 0.6f : dizzy && !held ? mouthOScale * 0.8f : mouthOScale;
        frown.enabled = showFrown && !showO;
        smile.enabled = !showO && !frown.enabled;
        smile.transform.localScale = happy ? smileScale * (toothy ? 1.12f : 1.3f) : smileScale;

        // happy = rosy cheeks
        Color b = blushBase;
        if (happy) b.a = Mathf.Min(1f, b.a * 1.6f);
        blushL.color = blushR.color = b;
    }

    /// <summary>Everything about how its face is drawn right now, for finding drawing bugs (FaceDebug).</summary>
    public string Describe()
    {
        var sb = new System.Text.StringBuilder();
        if (!eyeL) return "  face not built yet\n";
        Transform face = eyeL.parent;
        string path = "";
        for (Transform t = face; t && t != transform.parent; t = t.parent) path = t.name + (path.Length > 0 ? "/" + path : "");
        sb.Append($"  face under {path}, scale {scale:0.000}, order {sortingOrder}, baby {baby:0.00}, mood {Mood}, asleep {Asleep}, level {(progress ? progress.Level : -1)}\n");
        foreach (var sr in face.GetComponentsInChildren<SpriteRenderer>(true))
        {
            var tr = sr.transform;
            sb.Append($"    {sr.name,-14} on {sr.enabled,-5} active {sr.gameObject.activeInHierarchy,-5} visible {sr.isVisible,-5} sprite {(sr.sprite ? sr.sprite.name : "NULL"),-11} " +
                      $"order {sr.sortingOrder} layer {sr.sortingLayerName} mask {sr.maskInteraction} color {sr.color.r:0.00},{sr.color.g:0.00},{sr.color.b:0.00},{sr.color.a:0.00} " +
                      $"pos {tr.position.x:0.000},{tr.position.y:0.000},{tr.position.z:0.000} size {tr.lossyScale.x:0.0000},{tr.lossyScale.y:0.0000} mat {(sr.sharedMaterial ? sr.sharedMaterial.name : "NULL")}\n");
        }
        return sb.ToString();
    }

    void Look(Transform eye, Transform pupil, Vector2 target)
    {
        Vector2 d = eye.InverseTransformPoint(target); // eye space: follows the body's rotation
        float max = (eyeSize - pupilSize) * 0.5f * scale * 0.85f;
        float k = Mathf.Clamp01(d.magnitude / (scale * 1.5f)); // cursor on the face = look straight ahead
        pupil.localPosition = d.sqrMagnitude > 1e-8f ? (Vector3)(d.normalized * (max * k)) : Vector3.zero;
    }

    Transform MakeEye(Transform face, Vector2 pos, out Transform pupil)
    {
        var eye = new GameObject("Eye").transform;
        eye.SetParent(face, false);
        eye.localPosition = pos;
        MakeSprite(eye, "White", Circle, Vector2.zero, Vector2.one * (eyeSize * scale), Color.white, 0);
        pupil = MakeSprite(eye, "Pupil", Circle, Vector2.zero, Vector2.one * (pupilSize * scale), pupilColor, 1).transform;
        MakeSprite(pupil, "Shine", Circle, new Vector2(0.28f, 0.3f), Vector2.one * 0.38f, Color.white, 2); // relative to the pupil
        return eye;
    }

    SpriteRenderer MakeSprite(Transform parent, string name, Sprite sprite, Vector2 pos, Vector2 size, Color color, int order)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localScale = new Vector3(size.x, size.y, 1f);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.color = color;
        sr.sortingOrder = sortingOrder + order;
        if (material) sr.sharedMaterial = material;
        return sr;
    }

    // ------------------------------------------------------------------ shared sprites (baked once)

    /// <summary>Shared soft-edged white disc sprite (1 unit wide), also used by GummyFx.</summary>
    internal static Sprite Circle
    {
        get
        {
            if (s_circle) return s_circle;
            const int res = 64;
            var px = new Color32[res * res];
            for (int y = 0; y < res; y++)
                for (int x = 0; x < res; x++)
                {
                    float u = (x + 0.5f) / res * 2f - 1f, v = (y + 0.5f) / res * 2f - 1f;
                    float d = Mathf.Sqrt(u * u + v * v) - 0.96f;
                    px[y * res + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(0.5f - d * res * 0.5f) * 255f));
                }
            s_circle = Bake(px, res, res, new Vector2(0.5f, 0.5f), "CuteCircle");
            return s_circle;
        }
    }

    static Sprite Smile
    {
        get
        {
            if (s_smile) return s_smile;
            const int w = 64, h = 32;
            var px = new Color32[w * h];
            const float cy = 0.35f, r = 0.7f, half = 0.12f;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float u = (x + 0.5f) / w * 2f - 1f, v = (y + 0.5f) / h - 0.5f; // u -1..1, v -0.5..0.5 (same texel size)
                    float d = Mathf.Abs(Mathf.Sqrt(u * u + (v - cy) * (v - cy)) - r) - half;
                    float a = Mathf.Clamp01(0.5f - d * 32f) * Mathf.Clamp01((cy - r * 0.45f - v) * 32f); // lower part of the ring only
                    px[y * w + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            s_smile = Bake(px, w, h, new Vector2(0.5f, 0.3f), "CuteSmile");
            return s_smile;
        }
    }

    // a big open grin: a dark mouth with its corners turned up, a row of little pointy teeth along the top, two little fangs
    // at the bottom and a pink tongue (pivot = the middle of the top lip)
    static Sprite Grin
    {
        get
        {
            if (s_grin) return s_grin;
            const int w = 96, h = 64;
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float u = (x + 0.5f) / w * 2f - 1f, v = ((y + 0.5f) / h * 2f - 1f) * h / w; // same texel size both ways
                    px[y * w + x] = GrinPaint(u, v, 2f / w);
                }
            s_grin = Bake(px, w, h, new Vector2(0.5f, 0.8f), "CuteGrin");
            return s_grin;
        }
    }

    internal static Color GrinPaint(float u, float v, float texel)
    {
        const float half = 0.86f, rim = 0.075f;
        float top = 0.40f + 0.14f * u * u;                                                  // the top lip, corners up
        float open = Mathf.Abs(u) < half ? 0.80f * Mathf.Sqrt(1f - (u / half) * (u / half)) : 0f;
        float bottom = top - open;
        float d = Mathf.Max(Mathf.Max(v - top, bottom - v), Mathf.Abs(u) - half);           // < 0 inside the mouth
        float a = Mathf.Clamp01(0.5f - d / texel);
        if (a <= 0f) return new Color(0f, 0f, 0f, 0f);
        Color c = Color.Lerp(new Color(0.30f, 0.05f, 0.07f), new Color(0.45f, 0.10f, 0.11f), Mathf.Clamp01((v - bottom) / 0.6f));
        // the tongue
        float tu = u / 0.40f, tv = (v - (0.40f - 0.80f + 0.11f)) / 0.17f;
        if (tu * tu + tv * tv < 1f) c = Color.Lerp(new Color(0.93f, 0.42f, 0.50f), new Color(1f, 0.62f, 0.68f), Mathf.Clamp01(tv * 0.5f + 0.5f));
        // little pointy teeth hanging from the top lip, and two little fangs from the bottom
        float tooth = 0f;
        for (int k = 0; k < 6; k++)
        {
            float uk = -0.62f + k * 0.248f, hw = 0.105f, depth = 0.21f * (1f - 0.3f * Mathf.Abs(uk));
            float q = 1f - Mathf.Abs(u - uk) / hw;
            if (q > 0f) tooth = Mathf.Max(tooth, Mathf.Clamp01(((v - (top - 0.02f - depth * q)) / texel) + 0.5f));
        }
        for (int k = -1; k <= 1; k += 2)
        {
            float uk = 0.47f * k, hw = 0.085f, q = 1f - Mathf.Abs(u - uk) / hw;
            float b = top - (Mathf.Abs(uk) < half ? 0.80f * Mathf.Sqrt(1f - (uk / half) * (uk / half)) : 0f);
            if (q > 0f) tooth = Mathf.Max(tooth, Mathf.Clamp01((((b + 0.02f + 0.15f * q) - v) / texel) + 0.5f));
        }
        if (tooth > 0f) c = Color.Lerp(c, new Color(1f, 0.99f, 0.94f), tooth);
        // the lips: a dark outline all round (the corners close into a line)
        c = Color.Lerp(c, new Color(0.14f, 0.08f, 0.05f), Mathf.Clamp01(0.5f + (d + rim) / texel));
        c.a = a;
        return c;
    }

    static Sprite Spiral
    {
        get
        {
            if (s_spiral) return s_spiral;
            const int res = 48;
            const float turns = 2.25f, rMax = 0.9f, half = 0.075f;
            float a = rMax / (turns * 2f * Mathf.PI), texel = 2f / res;
            var px = new Color32[res * res];
            for (int y = 0; y < res; y++)
                for (int x = 0; x < res; x++)
                {
                    float u = (x + 0.5f) * texel - 1f, v = (y + 0.5f) * texel - 1f;
                    float r = Mathf.Sqrt(u * u + v * v), phi = Mathf.Atan2(v, u);
                    if (phi < 0f) phi += 2f * Mathf.PI;
                    float d = float.MaxValue;
                    for (int k = 0; k < 4; k++) // nearest arm of r = a * theta
                    {
                        float th = phi + 2f * Mathf.PI * k;
                        if (th > turns * 2f * Mathf.PI) break;
                        d = Mathf.Min(d, Mathf.Abs(r - a * th));
                    }
                    if (r > rMax + half) d = float.MaxValue;
                    float alpha = Mathf.Clamp01(0.5f - (d - half) / texel);
                    px[y * res + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
                }
            s_spiral = Bake(px, res, res, new Vector2(0.5f, 0.5f), "CuteSpiral");
            return s_spiral;
        }
    }

    static Sprite Bake(Color32[] px, int w, int h, Vector2 pivot, string name)
    {
        var t = new Texture2D(w, h, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            name = name
        };
        t.SetPixels32(px);
        t.Apply(false, true);
        return Sprite.Create(t, new Rect(0, 0, w, h), pivot, w); // 1 unit wide
    }
}
