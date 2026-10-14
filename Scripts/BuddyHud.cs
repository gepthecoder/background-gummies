using UnityEngine;

/// <summary>
/// Little pixel-art HUD for a buddy: "Lv N" and an XP bar above it while you hover it (or right after it earned XP),
/// gold "+N" popups for every bit of XP, and a level-up burst ("LEVEL UP!", sparkles, and a white flash when it
/// grows into a new stage). Everything is drawn in code with a tiny 3x5 pixel font, snapped to screen pixels.
/// </summary>
[RequireComponent(typeof(BuddyProgress))]
[DisallowMultipleComponent]
public class BuddyHud : MonoBehaviour
{
    [Tooltip("Keep the level + XP bar visible this long after earning XP.")]
    public float showAfterGain = 2.5f;
    [Tooltip("Screen pixels per font pixel on a 1080p screen (scales with the resolution).")]
    public int pixelScale1080 = 3;

    [Header("Colours")]
    public Color textColor = new Color(1f, 0.97f, 0.9f);
    public Color xpColor = new Color(1f, 0.82f, 0.25f);
    public Color xpShine = new Color(1f, 0.95f, 0.65f);
    public Color barTrack = new Color(0.25f, 0.18f, 0.13f, 0.95f);
    public Color barOutline = new Color(0.12f, 0.08f, 0.06f, 0.9f);

    const int BarW = 24;
    const int PopupCount = 6, SparkCount = 24;
    const string Chars = "0123456789LEVUPXv+!";
    static readonly string[] GlyphRows =
    {
        "###,#.#,#.#,#.#,###", ".#.,##.,.#.,.#.,###", "###,..#,###,#..,###", "###,..#,.##,..#,###", "#.#,#.#,###,..#,..#",
        "###,#..,###,..#,###", "###,#..,###,#.#,###", "###,..#,.#.,.#.,.#.", "###,#.#,###,#.#,###", "###,#.#,###,..#,###",
        "#..,#..,#..,#..,###", "###,#..,##.,#..,###", "#.#,#.#,#.#,#.#,.#.", "#.#,#.#,#.#,#.#,###", "##.,#.#,##.,#..,#..",
        "#.#,#.#,.#.,#.#,#.#", "...,...,#.#,#.#,.#.", "...,.#.,###,.#.,...", ".#.,.#.,.#.,...,.#.",
    };

    BuddyProgress progress;
    GummyBody body;
    Material mat;
    int baseOrder;

    // shared pixel sprites
    Texture2D fontTex, whiteTex, sparkTex;
    Sprite[] glyphs;
    Sprite white, spark;

    Transform hud;
    PixelText label, levelUp;
    SpriteRenderer barOut, barBack, barFill, barShine;
    float alpha, lastGain = -10f, levelUpT = -10f, shownProgress;
    int labelLevel = -1;
    bool hudShown = true;

    struct Popup { public PixelText text; public Transform root; public Vector2 start; public float age; public int amount; }
    readonly Popup[] popups = new Popup[PopupCount];
    int nextPopup;

    struct Spark { public SpriteRenderer sr; public Vector2 pos, vel; public float age, life; public Color color; }
    readonly Spark[] sparks = new Spark[SparkCount];
    SpriteRenderer flash;
    float flashT = -10f;

    GameObject holder;
    float fontPx = 0.028f, screenPx;

    /// <summary>The level / XP bar is on screen (speech bubbles sit above it then).</summary>
    public bool Showing => alpha > 0.01f;

    void Awake()
    {
        progress = GetComponent<BuddyProgress>();
        body = GetComponent<GummyBody>();
        layer = GetComponent<BuddyLayer>();
    }

    void OnEnable()
    {
        progress.XpGained += OnXp;
        progress.LeveledUp += OnLevelUp;
        if (holder) holder.SetActive(true);
    }

    void OnDisable()
    {
        progress.XpGained -= OnXp;
        progress.LeveledUp -= OnLevelUp;
        if (holder) holder.SetActive(false); // (switched off, e.g. gone into a fused monster: its bar goes with it)
        alpha = 0f;
    }

    void Start()
    {
        mat = body.spriteMaterial;
        baseOrder = body.sortingOrder + 100; // above every buddy (it fades instead of hiding behind windows)
        BakeSprites();

        holder = new GameObject(name + " HUD");
        hud = new GameObject("Level").transform;
        hud.SetParent(holder.transform, false);
        barOut = Box(hud, "Bar Outline", -BarW / 2 - 1, 0, BarW + 2, 5, barOutline, 10);
        barBack = Box(hud, "Bar Track", -BarW / 2, 1, BarW, 3, barTrack, 11);
        barFill = Box(hud, "Bar Fill", -BarW / 2, 1, 0, 3, xpColor, 12);
        barShine = Box(hud, "Bar Shine", -BarW / 2, 3, 0, 1, xpShine, 13);
        label = new PixelText(hud, "Label", 6, this, 14);
        levelUp = new PixelText(hud, "Level Up", 9, this, 14);
        levelUp.Set("LEVEL UP!");

        for (int i = 0; i < PopupCount; i++)
        {
            var root = new GameObject("XP Popup").transform;
            root.SetParent(holder.transform, false);
            popups[i] = new Popup { root = root, text = new PixelText(root, "Text", 5, this, 15), age = 99f };
            popups[i].text.Show(false);
        }
        for (int i = 0; i < SparkCount; i++)
        {
            var sr = MakeRenderer(holder.transform, "Sparkle", spark, 9);
            sr.enabled = false;
            sparks[i] = new Spark { sr = sr, age = 99f, life = 1f };
        }
        flash = MakeRenderer(holder.transform, "Flash", CuteFace.Circle, 8);
        flash.enabled = false;

        shownProgress = progress.Progress01;
        SetHudVisible(false);
    }

    void OnDestroy()
    {
        if (holder) Destroy(holder);
        if (glyphs != null) foreach (var g in glyphs) if (g) Destroy(g);
        if (white) Destroy(white);
        if (spark) Destroy(spark);
        if (fontTex) Destroy(fontTex);
        if (whiteTex) Destroy(whiteTex);
        if (sparkTex) Destroy(sparkTex);
    }

    // ------------------------------------------------------------------ events

    void OnXp(int amount, Vector2 at)
    {
        lastGain = Time.time;
        if (!holder) return;
        // a quick second gain adds to the popup that's still rising
        int last = (nextPopup + PopupCount - 1) % PopupCount;
        if (popups[last].age < 0.3f) { popups[last].amount += amount; popups[last].text.Set("+" + popups[last].amount); return; }
        ref Popup p = ref popups[nextPopup];
        nextPopup = (nextPopup + 1) % PopupCount;
        p.amount = amount;
        p.age = 0f;
        p.start = at + new Vector2(Random.Range(-0.08f, 0.08f), 0.3f) * body.size;
        p.text.Set("+" + amount);
        p.text.Show(true);
    }

    void OnLevelUp(int level, bool newStage)
    {
        lastGain = Time.time;
        levelUpT = Time.time;
        shownProgress = 0f; // the bar empties and fills up again for the new level
        if (!holder) return;
        int count = newStage ? SparkCount : 16;
        for (int i = 0; i < count; i++)
        {
            float a = (i + Random.Range(-0.3f, 0.3f)) / count * 2f * Mathf.PI;
            float sp = Random.Range(1.5f, 3.2f) * body.size;
            sparks[i].pos = body.Center;
            sparks[i].vel = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * sp + new Vector2(0f, 0.6f * body.size);
            sparks[i].age = 0f;
            sparks[i].life = Random.Range(0.6f, 1.1f);
            sparks[i].color = i % 3 == 0 ? Color.white : xpColor;
            sparks[i].sr.enabled = true;
        }
        if (newStage) flashT = Time.time;
    }

    // ------------------------------------------------------------------ per frame

    void LateUpdate() { long t0 = WhipProf.T(); LateUpdateTimed(); WhipProf.Add("BuddyHud.LateUpdate", t0); }
    void LateUpdateTimed()
    {
        if (!holder) return;
        float now = Time.time, dt = Time.deltaTime;
        UpdatePixelSize();

        // level + bar
        bool leveling = now - levelUpT < 1.6f;
        bool want = body.Hovered || body.Held || now - lastGain < showAfterGain || leveling;
        alpha = Mathf.MoveTowards(alpha, want ? 1f : 0f, dt * (want ? 6f : 3f));
        if (alpha > 0f)
        {
            SetHudVisible(true);
            shownProgress = Mathf.MoveTowards(shownProgress, progress.Progress01, dt * 1.5f);
            if (labelLevel != progress.Level) { labelLevel = progress.Level; label.Set("Lv " + labelLevel); }

            float t = now - levelUpT;
            int bounce = t < 0.8f ? Mathf.RoundToInt(4f * Mathf.Abs(Mathf.Sin(t * Mathf.PI * 2.5f)) * (1f - t / 0.8f)) : 0;
            label.Place(-label.Width / 2, 6 + bounce);
            label.SetColor(Fade(t < 0.6f ? xpColor : textColor));

            int w = Mathf.RoundToInt(BarW * Mathf.Clamp01(shownProgress));
            barFill.transform.localScale = new Vector3(w, 3f, 1f);
            barShine.transform.localScale = new Vector3(w, 1f, 1f);
            barFill.enabled = barShine.enabled = w > 0;
            barOut.color = Fade(barOutline);
            barBack.color = Fade(barTrack);
            barFill.color = Fade(xpColor);
            barShine.color = Fade(xpShine);

            if (leveling)
            {
                levelUp.Show(true);
                levelUp.Place(-levelUp.Width / 2, 14 + Mathf.RoundToInt(Mathf.Min(t, 0.5f) * 12f));
                bool blink = t < 0.6f && Mathf.FloorToInt(t / 0.1f) % 2 == 1;
                Color c = blink ? Color.white : xpColor;
                c.a *= Mathf.Clamp01((1.6f - t) / 0.4f);
                levelUp.SetColor(c);
            }
            else levelUp.Show(false);

            hud.position = Snap(HudAnchor(label.Width));
            hud.localScale = new Vector3(fontPx, fontPx, 1f);
        }
        else SetHudVisible(false);

        UpdatePopups(dt);
        UpdateSparks(now, dt);
    }

    Vector2 HudAnchor(int widthPx)
    {
        float s = body.size, h = 14f * fontPx, half = (BarW / 2 + 2) * fontPx;
        var p = body.Center + new Vector2(0f, s * 0.75f + 2f * fontPx);
        UnityEngine.Rect r = ScreenBounds.ScreenRectAt(body.Center); // (its own screen)
        if (p.y + h > r.yMax) p.y = body.Center.y - s * 0.75f - h; // no room above (stuck to the top): show it below
        p.x = Mathf.Clamp(p.x, r.xMin + half, r.xMax - half);
        return p;
    }

    void UpdatePopups(float dt)
    {
        for (int i = 0; i < PopupCount; i++)
        {
            ref Popup p = ref popups[i];
            if (p.age >= 1f) continue;
            p.age += dt;
            if (p.age >= 1f) { p.text.Show(false); continue; }
            float u = p.age, rise = 1f - (1f - u) * (1f - u); // ease out
            p.root.position = Snap(p.start + new Vector2(0f, 0.55f * body.size * rise));
            p.root.localScale = new Vector3(fontPx, fontPx, 1f);
            p.text.Place(-p.text.Width / 2, 0);
            Color c = xpColor;
            c.a = u < 0.6f ? 1f : 1f - (u - 0.6f) / 0.4f;
            p.text.SetColor(c);
        }
    }

    void UpdateSparks(float now, float dt)
    {
        for (int i = 0; i < SparkCount; i++)
        {
            ref Spark s = ref sparks[i];
            if (s.age >= s.life) continue;
            s.age += dt;
            if (s.age >= s.life) { s.sr.enabled = false; continue; }
            s.vel *= Mathf.Exp(-3.5f * dt);
            s.pos += s.vel * dt;
            float u = s.age / s.life;
            float k = fontPx * (u < 0.6f ? 1f : 0.6f); // shrinks near the end
            s.sr.transform.position = Snap(s.pos - new Vector2(2.5f, 2.5f) * k);
            s.sr.transform.localScale = new Vector3(k, k, 1f);
            Color c = s.color;
            c.a = u < 0.7f ? 1f : 1f - (u - 0.7f) / 0.3f;
            s.sr.color = c;
        }

        float ft = now - flashT;
        if (ft < 0.45f)
        {
            flash.enabled = true;
            float k = body.size * (1.3f + 0.7f * ft / 0.45f);
            flash.transform.position = body.Center;
            flash.transform.localScale = new Vector3(k, k, 1f);
            flash.color = new Color(1f, 1f, 1f, 0.9f * (1f - ft / 0.45f));
        }
        else if (flash.enabled) flash.enabled = false;
    }

    void SetHudVisible(bool on)
    {
        if (hudShown == on) return;
        hudShown = on;
        barOut.enabled = barBack.enabled = on;
        if (!on) { barFill.enabled = barShine.enabled = false; levelUp.Show(false); }
        label.Show(on);
    }

    Color Fade(Color c) { c.a *= alpha * (layer ? layer.HeadSeen : 1f); return c; } // (gone while a window in front covers its head)
    BuddyLayer layer;

    // ------------------------------------------------------------------ pixel grid

    void UpdatePixelSize()
    {
        var cam = Camera.main;
        if (!cam || Screen.height <= 0) { screenPx = 0f; return; }
        screenPx = 2f * cam.orthographicSize / Screen.height;
        int k = Mathf.Max(1, Mathf.RoundToInt(pixelScale1080 * ScreenBounds.MainScreenPixels / 1080f));
        fontPx = screenPx * k;
    }

    /// <summary>Round a world position to the screen's pixel grid so the pixel font stays crisp.</summary>
    Vector3 Snap(Vector2 p)
    {
        if (screenPx <= 0f) return new Vector3(p.x, p.y, 0f);
        var cam = Camera.main;
        Vector2 origin = (Vector2)cam.transform.position - new Vector2(cam.orthographicSize * Screen.width / Screen.height, cam.orthographicSize);
        Vector2 d = p - origin;
        return new Vector3(origin.x + Mathf.Round(d.x / screenPx) * screenPx, origin.y + Mathf.Round(d.y / screenPx) * screenPx, 0f);
    }

    // ------------------------------------------------------------------ building blocks

    SpriteRenderer MakeRenderer(Transform parent, string n, Sprite sprite, int order)
    {
        var go = new GameObject(n);
        go.transform.SetParent(parent, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.sortingOrder = baseOrder + order;
        if (mat) sr.sharedMaterial = mat;
        return sr;
    }

    SpriteRenderer Box(Transform parent, string n, int x, int y, int w, int h, Color c, int order)
    {
        var sr = MakeRenderer(parent, n, white, order);
        sr.transform.localPosition = new Vector3(x, y, 0f);
        sr.transform.localScale = new Vector3(w, h, 1f);
        sr.color = c;
        return sr;
    }

    Sprite Glyph(char ch)
    {
        int i = Chars.IndexOf(ch);
        return i >= 0 ? glyphs[i] : null;
    }

    void BakeSprites()
    {
        // font atlas: 5x7 cells (3x5 glyph + 1 px dark outline), 1 px gap between cells
        const int cw = 5, ch = 7, stride = 6;
        int w = Chars.Length * stride;
        var px = new Color32[w * ch];
        var clear = new Color32(0, 0, 0, 0);
        var fill = new Color32(255, 255, 255, 255);
        var edge = new Color32(38, 26, 18, 235);
        for (int i = 0; i < px.Length; i++) px[i] = clear;
        glyphs = new Sprite[Chars.Length];
        for (int g = 0; g < Chars.Length; g++)
        {
            string[] rows = GlyphRows[g].Split(',');
            for (int cy = 0; cy < ch; cy++)
                for (int cx = 0; cx < cw; cx++)
                {
                    int idx = cy * w + g * stride + cx;
                    if (GlyphOn(rows, cx, cy)) { px[idx] = fill; continue; }
                    for (int oy = -1; oy <= 1 && px[idx].a == 0; oy++)
                        for (int ox = -1; ox <= 1; ox++)
                            if (GlyphOn(rows, cx + ox, cy + oy)) { px[idx] = edge; break; }
                }
        }
        fontTex = PixelTexture(w, ch, px, "BuddyFont");
        for (int g = 0; g < Chars.Length; g++)
            glyphs[g] = Sprite.Create(fontTex, new UnityEngine.Rect(g * stride, 0, cw, ch), Vector2.zero, 1f, 0, SpriteMeshType.FullRect);

        whiteTex = PixelTexture(1, 1, new[] { fill }, "BuddyWhite");
        white = Sprite.Create(whiteTex, new UnityEngine.Rect(0, 0, 1, 1), Vector2.zero, 1f, 0, SpriteMeshType.FullRect);

        // sparkle: a little 5x5 pixel plus
        var sp = new Color32[25];
        for (int y = 0; y < 5; y++)
            for (int x = 0; x < 5; x++)
                sp[y * 5 + x] = x == 2 || y == 2 ? fill : clear;
        sparkTex = PixelTexture(5, 5, sp, "BuddySparkle");
        spark = Sprite.Create(sparkTex, new UnityEngine.Rect(0, 0, 5, 5), Vector2.zero, 1f, 0, SpriteMeshType.FullRect);
    }

    /// <summary>Is glyph pixel (cx, cy) set? Cell coordinates with y up; the 3x5 glyph sits inside a 1 px border.</summary>
    static bool GlyphOn(string[] rows, int cx, int cy)
    {
        int col = cx - 1, row = 5 - cy;
        return col >= 0 && col < 3 && row >= 0 && row < 5 && rows[row][col] == '#';
    }

    static Texture2D PixelTexture(int w, int h, Color32[] px, string n)
    {
        var t = new Texture2D(w, h, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point, name = n
        };
        t.SetPixels32(px);
        t.Apply(false, true);
        return t;
    }

    /// <summary>A row of glyph sprites. Positions are in font pixels (the parent is scaled to one font pixel).</summary>
    sealed class PixelText
    {
        readonly Transform root;
        readonly SpriteRenderer[] slots;
        readonly BuddyHud hud;
        string text;
        bool shown = true;
        public int Width { get; private set; }

        public PixelText(Transform parent, string n, int capacity, BuddyHud owner, int order)
        {
            hud = owner;
            root = new GameObject(n).transform;
            root.SetParent(parent, false);
            slots = new SpriteRenderer[capacity];
            for (int i = 0; i < capacity; i++) slots[i] = owner.MakeRenderer(root, "Glyph", null, order);
        }

        public void Set(string s)
        {
            if (s == text) return;
            text = s;
            int x = 0, used = 0;
            for (int i = 0; i < s.Length && used < slots.Length; i++)
            {
                if (s[i] == ' ') { x += 3; continue; }
                var sr = slots[used++];
                sr.sprite = hud.Glyph(s[i]);
                sr.transform.localPosition = new Vector3(x, 0f, 0f);
                x += 4; // 3 px glyph + 1 px gap (outlines overlap)
            }
            for (int i = used; i < slots.Length; i++) slots[i].sprite = null;
            Width = x + 1;
            Show(shown);
        }

        public void Place(int x, int y) => root.localPosition = new Vector3(x, y, 0f);

        public void SetColor(Color c) { foreach (var sr in slots) sr.color = c; }

        public void Show(bool on)
        {
            shown = on;
            foreach (var sr in slots) sr.enabled = on && sr.sprite;
        }
    }
}
