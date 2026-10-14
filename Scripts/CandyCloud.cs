using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Candy cloud (Ctrl+Shift+K, chat "candy", WhipIt > Buddy > Candy Cloud): a fluffy cotton-candy cloud puffs up above
/// your cursor and follows it, raining sweets while you hold the keys (wrapped sweets, lollipops, gumdrops, jelly beans,
/// candy hearts). Tap = a shower of 15; hold = it keeps raining, up to 100, then it's empty and zooms off the screen
/// (it zooms off when you let go too, once the first 15 are out). The sweets bounce, roll and pile up (Snack, kind
/// Sweet); the buddies scramble to eat them (1 XP each, a SUGAR RUSH after a dozen), you can grab and flick them, and
/// the ones nobody eats fade away after a while. Only that key combination is read.
/// </summary>
[DefaultExecutionOrder(20)] // after the snacks have moved this frame: it piles them up
[DisallowMultipleComponent]
public class CandyCloud : MonoBehaviour
{
    public const int MinCandies = 15, MaxCandies = 100;

    [Tooltip("Sweets per second while it rains.")]
    public float rate = 9f;
    [Tooltip("Sweets nobody ate fade after lying around this long (seconds).")]
    public float candyLife = 30f;
    [Tooltip("At most this many sweets on screen (the oldest fade early).")]
    public int maxOnScreen = 160;
    [Tooltip("Sprite material (WhipIt > Setup assigns Whip.mat).")]
    public Material material;
    public int candyOrder = 8;

    [Header("Sound (WhipIt > Setup assigns them)")]
    public AudioClip poofClip;
    public AudioClip whooshClip;
    public AudioClip plinkClip;
    [Range(0f, 1f)] public float volume = 0.5f;

    /// <summary>The one in the scene (on the Interaction object).</summary>
    public static CandyCloud Instance { get; private set; }
    /// <summary>Tests can replace the Ctrl+Shift+K check.</summary>
    public static Func<bool> KeysProbe;
    /// <summary>A cloud puffed up (where): the buddies look up and cheer.</summary>
    public static event Action<Vector2> Appeared;
    /// <summary>So far (this session): showers, sweets dropped.</summary>
    public static int Showers, Dropped;

    /// <summary>A cloud is raining right now.</summary>
    public bool Raining { get { foreach (var c in clouds) if (c.state == Phase.Rain) return true; return false; } }
    /// <summary>Clouds on screen (raining or leaving).</summary>
    public int Clouds => clouds.Count;
    /// <summary>The cloud that's up (raining or puffing up): where it is.</summary>
    public bool TryGetCloud(out Vector2 at) { foreach (var c in clouds) if (c.state != Phase.Leave) { at = c.pos; return true; } at = default; return false; }

    enum Phase { Appear, Rain, Leave }

    class Cloud
    {
        public GameObject go;
        public SpriteRenderer body;
        public Vector2 pos, vel;
        public float t, acc, squeeze, leaveT, side, puffT;
        public int dropped, atLeast;
        public bool keys;
        public Phase state;
    }

    readonly List<Cloud> clouds = new List<Cloud>();
    Cloud active;
    bool wasKeys;
    AudioSource audioSrc;
    float lastPlink = -1f;
    const int Puffs = 16;
    readonly SpriteRenderer[] puff = new SpriteRenderer[Puffs];
    readonly Vector2[] puffV = new Vector2[Puffs];
    readonly float[] puffLife = new float[Puffs], puffMax = new float[Puffs], puffSize = new float[Puffs];
    readonly Dictionary<BuddyEater, List<float>> ate = new Dictionary<BuddyEater, List<float>>();
    readonly Dictionary<BuddyEater, float> rushUntil = new Dictionary<BuddyEater, float>();
    readonly List<Snack> sweets = new List<Snack>();

    /// <summary>Draw order: in front of the buddies, the hoop and ziplines; under the speech bubbles of the front buddies.</summary>
    public const int CloudOrder = 80;
    const float Above = 1.7f, Width = 1.8f;

    void Awake()
    {
        audioSrc = gameObject.AddComponent<AudioSource>();
        audioSrc.playOnAwake = false;
        audioSrc.spatialBlend = 0f;
    }

    void OnEnable()
    {
        Instance = this;
        Snack.Landed += OnLanded;
        BuddyEater.AnyAte += OnAte;
    }

    void OnDisable()
    {
        if (Instance == this) Instance = null;
        Snack.Landed -= OnLanded;
        BuddyEater.AnyAte -= OnAte;
    }

    void OnDestroy()
    {
        foreach (var c in clouds) if (c.go) Destroy(c.go);
        clouds.Clear();
    }

    // ------------------------------------------------------------------ you

    /// <summary>A shower of at least this many (chat "candy"): a cloud over the cursor that rains and zooms off.</summary>
    public void Shower(int count = MinCandies)
    {
        var c = active != null && active.state != Phase.Leave ? active : Spawn();
        c.atLeast = Mathf.Clamp(Mathf.Max(c.atLeast, c.dropped + count), MinCandies, MaxCandies);
    }

    static bool KeysDown()
    {
        if (KeysProbe != null) return KeysProbe();
        return DesktopInput.ChordHeld(DesktopInput.VK_CONTROL, DesktopInput.VK_SHIFT, DesktopInput.VK_K);
    }

    // ------------------------------------------------------------------ each frame

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("Cloud.Update", t0); }
    void UpdateTimed()
    {
        float dt = Mathf.Min(Time.deltaTime, 0.05f), now = Time.time;
        bool keys = KeysDown();
        if (keys && !wasKeys)
        {
            // pressed: the cloud that's up keeps going (held again), else a new one puffs up
            if (active == null || active.state == Phase.Leave) active = Spawn();
        }
        wasKeys = keys;
        if (active != null) active.keys = keys;

        for (int i = clouds.Count - 1; i >= 0; i--)
        {
            var c = clouds[i];
            if (!Step(c, dt)) { if (c.go) Destroy(c.go); clouds.RemoveAt(i); if (c == active) active = null; }
        }
        UpdatePuffs(dt);
        Pile();
        Trim();
        SugarRush(now);
    }

    Cloud Spawn()
    {
        Showers++;
        var c = new Cloud();
        c.go = new GameObject("Candy Cloud");
        c.go.transform.SetParent(transform, false);
        c.body = c.go.AddComponent<SpriteRenderer>();
        c.body.sprite = CloudSprite(0);
        c.body.sortingOrder = CloudOrder;
        c.body.enabled = true;
        if (material) c.body.sharedMaterial = material;
        c.pos = Target();
        c.atLeast = MinCandies;
        c.state = Phase.Appear;
        c.side = c.pos.x > ScreenBounds.WorldRect.center.x ? 1f : -1f;
        clouds.Add(c);
        Play(poofClip, Rnd(0.95f, 1.08f), 0.8f);
        for (int k = 0; k < 10; k++)
        {
            float a = k / 10f * Mathf.PI * 2f;
            Puff(c.pos + new Vector2(Mathf.Cos(a) * 0.6f, Mathf.Sin(a) * 0.3f), new Vector2(Mathf.Cos(a) * 1.6f, Mathf.Sin(a) * 0.9f), Rnd(0.35f, 0.55f), Rnd(0.25f, 0.4f));
        }
        Appeared?.Invoke(c.pos);
        Cheer(c.pos);
        Place(c);
        return c;
    }

    // over the cursor (on screen, below the top edge)
    static Vector2 Target()
    {
        Vector2 cur = Grabber.CursorWorld;
        Rect r = ScreenBounds.ScreenRectAt(cur); // (more than one screen: the one the cursor is on)
        return new Vector2(Mathf.Clamp(cur.x, r.xMin + Width * 0.55f, r.xMax - Width * 0.55f), Mathf.Clamp(cur.y + Above, r.yMin + 1.6f, r.yMax - 0.55f));
    }

    // false = it's gone (off the screen)
    bool Step(Cloud c, float dt)
    {
        c.t += dt;
        c.squeeze = Mathf.MoveTowards(c.squeeze, 0f, dt * 7f);
        Rect r = ScreenBounds.WorldRect;
        if (c.state != Phase.Leave)
        {
            // follows the cursor, a little behind (soft and floaty)
            Vector2 was = c.pos;
            c.pos += (Target() - c.pos) * (1f - Mathf.Exp(-6f * dt));
            c.vel = (c.pos - was) / Mathf.Max(dt, 1e-4f);
        }
        switch (c.state)
        {
            case Phase.Appear:
                if (c.t >= 0.3f) { c.state = Phase.Rain; c.acc = 0.6f; }
                break;
            case Phase.Rain:
            {
                bool more = (c.keys || c.dropped < c.atLeast) && c.dropped < MaxCandies;
                if (!more) { Leave(c); break; }
                c.acc += rate * dt;
                while (c.acc >= 1f && c.dropped < MaxCandies) { c.acc -= 1f; Drop(c); }
                break;
            }
            case Phase.Leave:
            {
                // a little bounce up, then off over the top corner
                c.leaveT += dt;
                c.vel += new Vector2(c.side * 7f, c.leaveT < 0.15f ? -6f : 12f) * dt;
                c.pos += c.vel * dt;
                c.puffT -= dt;
                if (c.puffT <= 0f) { c.puffT = 0.04f; Puff(c.pos - c.vel.normalized * 0.5f + new Vector2(Rnd(-0.2f, 0.2f), Rnd(-0.15f, 0.15f)), -c.vel * 0.1f, Rnd(0.3f, 0.45f), Rnd(0.18f, 0.3f)); }
                if (c.pos.y - 0.7f > r.yMax || c.pos.x + Width < r.xMin || c.pos.x - Width > r.xMax || c.leaveT > 4f) return false;
                break;
            }
        }
        Place(c);
        return true;
    }

    void Leave(Cloud c)
    {
        c.state = Phase.Leave;
        c.leaveT = 0f;
        c.side = c.pos.x > ScreenBounds.WorldRect.center.x ? 1f : -1f;
        c.vel = new Vector2(c.side * 0.5f, 2.2f);
        Play(whooshClip, Rnd(0.95f, 1.1f), 0.7f);
    }

    void Drop(Cloud c)
    {
        int look = Mathf.Min((int)Rnd(0f, Snack.SweetLooks), Snack.SweetLooks - 1);
        float x = c.pos.x + Rnd(-0.55f, 0.55f) * Width * 0.5f * 1.4f;
        Vector2 at = new Vector2(x, c.pos.y - 0.22f);
        Vector2 v = new Vector2(c.vel.x * 0.25f + Rnd(-0.6f, 0.6f), Rnd(-1.2f, -0.2f));
        var s = Snack.Spawn(Snack.Kind.Sweet, look, at, v, material, candyOrder);
        s.xp = 1;
        s.life = candyLife;
        c.dropped++;
        Dropped++;
        c.squeeze = 1f;
    }

    void Place(Cloud c)
    {
        float grow = c.state == Phase.Appear ? Overshoot(Mathf.Clamp01(c.t / 0.3f)) : 1f;
        float bob = 0.05f * Mathf.Sin(c.t * 3.1f);
        float sq = 0.07f * c.squeeze;
        c.body.sprite = CloudSprite(c.state == Phase.Rain ? (c.squeeze > 0.6f ? 2 : 1) : 0);
        var tr = c.go.transform;
        tr.position = new Vector3(c.pos.x, c.pos.y + bob, 0f);
        tr.rotation = Quaternion.Euler(0f, 0f, Mathf.Clamp(-c.vel.x * 2.5f, -12f, 12f));
        tr.localScale = new Vector3(Width * grow * (1f + sq), Width * grow * (1f - sq), 1f);
    }

    static float Overshoot(float u) { float s = 1.9f; u -= 1f; return 1f + u * u * ((s + 1f) * u + s); }

    // ------------------------------------------------------------------ the sweets: piles, too many, a pitter-patter

    // sweets lying on sweets: pushed apart, the upper one held up by the lower (it doesn't sink into the pile)
    void Pile()
    {
        sweets.Clear();
        foreach (var s in Snack.All) if (s.kind == Snack.Kind.Sweet && !s.Held && !s.Eaten) sweets.Add(s);
        int n = sweets.Count;
        if (n < 2) return;
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < n; i++)
            {
                var a = sweets[i];
                for (int j = i + 1; j < n; j++)
                {
                    var b = sweets[j];
                    Vector2 d = b.Position - a.Position;
                    float rr = a.radius + b.radius, dd = d.sqrMagnitude;
                    if (dd >= rr * rr || dd < 1e-10f) continue;
                    float dist = Mathf.Sqrt(dd), over = rr - dist;
                    Vector2 nrm = d / dist; // a -> b
                    // (only fairly flat on top of another one holds it up: on a steep side it slides off, so piles stay low)
                    if (nrm.y > 0.8f) { b.Push(nrm * over * 0.85f, true, a.SurfaceY); a.Push(-nrm * over * 0.15f, false, 0f); }
                    else if (nrm.y < -0.8f) { a.Push(-nrm * over * 0.85f, true, b.SurfaceY); b.Push(nrm * over * 0.15f, false, 0f); }
                    else if (nrm.y > 0.3f) { b.Push(new Vector2(nrm.x * over * 1.1f, nrm.y * over * 0.9f), false, 0f); }
                    else if (nrm.y < -0.3f) { a.Push(new Vector2(-nrm.x * over * 1.1f, -nrm.y * over * 0.9f), false, 0f); }
                    else { a.Push(-nrm * over * 0.5f, false, 0f); b.Push(nrm * over * 0.5f, false, 0f); }
                }
            }
    }

    // too many sweets around: the ones lying there longest fade first
    void Trim()
    {
        int extra = sweets.Count - maxOnScreen;
        for (int i = 0; i < sweets.Count && extra > 0; i++)
            if (sweets[i].Resting) { sweets[i].FadeNow(); extra--; }
    }

    void OnLanded(Snack s)
    {
        if (s.kind != Snack.Kind.Sweet || Time.time - lastPlink < 0.09f) return;
        lastPlink = Time.time;
        Play(plinkClip, Rnd(1.15f, 1.6f), 0.25f);
    }

    // ------------------------------------------------------------------ the buddies

    // the nearest one that's awake: "CANDY!"
    void Cheer(Vector2 at)
    {
        BuddySpeech best = null;
        float bd = 12f;
        foreach (var b in GummyBody.All)
        {
            if (!b || !b.isActiveAndEnabled || b.InRoom) continue;
            var br = b.GetComponent<GummyBrain>();
            var sp = b.GetComponent<BuddySpeech>();
            if (!sp || (br && br.Current == GummyBrain.State.Asleep)) continue;
            float d = (b.Center - at).magnitude;
            if (d < bd) { bd = d; best = sp; }
        }
        if (best) best.Say(Line(best, "CANDY!", "IT'S RAINING CANDY!", "SUGAR CLOUD!", "DESSERT TIME!", "RAWR! CANDY!"), 0f, true);
    }

    // a dozen sweets in half a minute: SUGAR RUSH (a happy flip)
    void OnAte(BuddyEater e, Snack s)
    {
        if (!e || s == null || s.kind != Snack.Kind.Sweet) return;
        float now = Time.time;
        if (!ate.TryGetValue(e, out var list)) ate[e] = list = new List<float>();
        list.Add(now);
        list.RemoveAll(t => now - t > 30f);
        if (list.Count < 12 || (rushUntil.TryGetValue(e, out float until) && now < until + 30f)) return;
        rushUntil[e] = now + 1.5f;
        list.Clear();
        var sp = e.GetComponent<BuddySpeech>();
        if (sp) sp.Say(Line(sp, "SUGAR RUSH!", "SUGAR RUSH!!", "SO. MUCH. SUGAR.", "SUGAR RUSH! BURP", "RAWR! SUGAR!"), 0f, true);
    }

    void SugarRush(float now)
    {
        if (rushUntil.Count == 0) return;
        foreach (var kv in rushUntil)
        {
            var e = kv.Key;
            if (!e || now > kv.Value || now < kv.Value - 1.4f) continue;
            var b = e.GetComponent<GummyBody>();
            var br = e.GetComponent<GummyBrain>();
            if (!b || b.Held || !b.Grounded || (br && br.Current != GummyBrain.State.Idle)) continue;
            // a little flip, once
            float S = b.size / 0.6f;
            b.Hop(new Vector2(0f, GummyBody.JumpSpeed(0.7f * S, b.gravity, b.Drag)), 0.6f);
            b.Spin((UnityEngine.Random.value < 0.5f ? -1f : 1f) * 9f, 0.6f);
            if (br) br.Linger(2f);
            rushUntil[e] = now - 2f; // (done)
            break; // (one a frame: the dictionary isn't changed while it's walked)
        }
    }

    /// <summary>A line in the buddy's own voice (apple, tomato, bean, burger, dino).</summary>
    public static string Line(BuddySpeech sp, string apple, string tomato, string bean, string burger, string dino)
    {
        if (!sp) return apple;
        switch (sp.voice)
        {
            case BuddySpeech.Voice.Tomato: return tomato;
            case BuddySpeech.Voice.Bean: return bean;
            case BuddySpeech.Voice.Burger: return burger;
            case BuddySpeech.Voice.Dino: return dino;
            default: return apple;
        }
    }

    // ------------------------------------------------------------------ puffs of cloud

    void Puff(Vector2 at, Vector2 v, float life, float size)
    {
        for (int i = 0; i < Puffs; i++)
        {
            if (puffLife[i] > 0f) continue;
            if (!puff[i])
            {
                var g = new GameObject("Cloud Puff");
                g.transform.SetParent(transform, false);
                puff[i] = g.AddComponent<SpriteRenderer>();
                puff[i].sprite = PuffSprite();
                puff[i].sortingOrder = CloudOrder - 1;
                if (material) puff[i].sharedMaterial = material;
            }
            puffLife[i] = puffMax[i] = life;
            puffV[i] = v;
            puffSize[i] = size;
            puff[i].transform.position = new Vector3(at.x, at.y, 0f);
            puff[i].enabled = true;
            return;
        }
    }

    void UpdatePuffs(float dt)
    {
        for (int i = 0; i < Puffs; i++)
        {
            if (!puff[i]) continue;
            if (puffLife[i] <= 0f) { if (puff[i].enabled) puff[i].enabled = false; continue; }
            puffLife[i] -= dt;
            puffV[i] *= Mathf.Exp(-4f * dt);
            var tr = puff[i].transform;
            tr.position += (Vector3)(puffV[i] * dt);
            float u = 1f - Mathf.Clamp01(puffLife[i] / puffMax[i]);
            float k = puffSize[i] * (0.6f + 0.8f * u);
            tr.localScale = new Vector3(k, k, 1f);
            puff[i].color = new Color(1f, 1f, 1f, 0.85f * (1f - u));
        }
    }

    void Play(AudioClip clip, float pitch, float vol)
    {
        if (!clip || !audioSrc) return;
        audioSrc.pitch = pitch;
        audioSrc.PlayOneShot(clip, volume * vol);
    }

    // its own dice for the looks (puffs, pitches, where a sweet falls from)
    static readonly System.Random s_fx = new System.Random(11);
    static float Rnd(float a, float b) => a + (float)s_fx.NextDouble() * (b - a);

    // ------------------------------------------------------------------ drawing (baked once)

    const int CloudW = 160, CloudH = 104;
    static readonly Sprite[] s_cloud = new Sprite[3];
    static Sprite s_puff;

    // 0 = happy (^ ^), 1 = raining (eyes open, "o"), 2 = squeezing one out (> <)
    static Sprite CloudSprite(int face)
    {
        if (!s_cloud[face])
        {
            var tex = new Texture2D(CloudW, CloudH, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "Candy Cloud " + face };
            var px = new Color32[CloudW * CloudH];
            float texel = 1f / CloudW;
            for (int j = 0; j < CloudH; j++)
                for (int i = 0; i < CloudW; i++)
                    px[j * CloudW + i] = CloudPaint(face, (i + 0.5f) * texel, (j + 0.5f) * texel, texel);
            tex.SetPixels32(px);
            tex.Apply(false, true);
            s_cloud[face] = Sprite.Create(tex, new Rect(0, 0, CloudW, CloudH), new Vector2(0.5f, 0.5f), CloudW); // 1 unit wide
        }
        return s_cloud[face];
    }

    static Sprite PuffSprite()
    {
        if (!s_puff)
        {
            const int n = 24;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "Cloud Puff" };
            var px = new Color32[n * n];
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                    px[j * n + i] = PuffPaint((i + 0.5f) / n, (j + 0.5f) / n, 1f / n);
            tex.SetPixels32(px);
            tex.Apply(false, true);
            s_puff = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
        }
        return s_puff;
    }

    static float Circle(float x, float y, float cx, float cy, float r) => Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - r;

    /// <summary>The cloud at (x, y): x 0..1 across, y 0..0.65 up (texel = one pixel). A fluffy cotton-candy cloud: pink on
    /// top fading to lilac underneath, white highlights on its puffs, rosy cheeks and a face.</summary>
    internal static Color CloudPaint(int face, float x, float y, float texel)
    {
        // the puffs (circles) and a flat-ish bottom
        float d = Circle(x, y, 0.5f, 0.36f, 0.2f);
        d = Mathf.Min(d, Circle(x, y, 0.3f, 0.3f, 0.155f));
        d = Mathf.Min(d, Circle(x, y, 0.7f, 0.31f, 0.16f));
        d = Mathf.Min(d, Circle(x, y, 0.14f, 0.22f, 0.11f));
        d = Mathf.Min(d, Circle(x, y, 0.86f, 0.22f, 0.11f));
        d = Mathf.Min(d, Circle(x, y, 0.4f, 0.47f, 0.13f));
        float box = Mathf.Max(Mathf.Max(0.12f - x, x - 0.88f), Mathf.Max(0.11f - y, y - 0.3f)) - 0.02f; // the belly
        d = Mathf.Min(d, box);
        // its belly: a little wavy along the bottom
        d = Mathf.Max(d, 0.1f + 0.012f * Mathf.Sin(x * 60f) - y);
        float a = Mathf.Clamp01(0.5f - d / texel);
        if (a <= 0f) return new Color(0f, 0f, 0f, 0f);
        float h = Mathf.Clamp01((y - 0.1f) / 0.5f);
        Color c = Color.Lerp(new Color(0.86f, 0.74f, 0.98f), new Color(1f, 0.80f, 0.90f), h);          // lilac -> pink
        // highlights on the tops of the puffs, soft shade under them
        float hl = Mathf.Min(Mathf.Min(Circle(x, y, 0.47f, 0.44f, 0.09f), Circle(x, y, 0.27f, 0.36f, 0.06f)), Mathf.Min(Circle(x, y, 0.67f, 0.38f, 0.065f), Circle(x, y, 0.38f, 0.53f, 0.05f)));
        c = Color.Lerp(c, Color.white, Mathf.Clamp01(-hl / 0.05f) * 0.75f);
        // a rim (darker pink), a sprinkle of sugar sparkles
        float rim = Mathf.Clamp01(1f + d / (texel * 3f));
        c = Color.Lerp(c, new Color(0.88f, 0.52f, 0.74f), rim * 0.9f);
        float sp = Mathf.Sin(Mathf.Floor(x * 40f) * 12.9898f + Mathf.Floor(y * 40f) * 78.233f) * 43758.5453f;
        if (sp - Mathf.Floor(sp) > 0.985f && d < -texel * 4f) c = Color.Lerp(c, Color.white, 0.8f);
        // the face (on the belly)
        float ex = 0.085f, ey = 0.27f, cx = 0.5f;
        // cheeks
        for (int s = -1; s <= 1; s += 2)
        {
            float ck = Mathf.Sqrt(Mathf.Pow((x - (cx + s * 0.15f)) / 1.4f, 2f) + Mathf.Pow(y - 0.215f, 2f)) - 0.03f;
            c = Color.Lerp(c, new Color(1f, 0.55f, 0.7f), Mathf.Clamp01(0.5f - ck / texel) * 0.7f);
        }
        Color ink = new Color(0.30f, 0.16f, 0.30f);
        for (int s = -1; s <= 1; s += 2)
        {
            float exc = cx + s * ex, ed;
            if (face == 1)
            {
                ed = Circle(x, y, exc, ey, 0.024f); // open: a round eye with a shine
                c = Color.Lerp(c, ink, Mathf.Clamp01(0.5f - ed / texel));
                float gl = Circle(x, y, exc - 0.008f, ey + 0.009f, 0.008f);
                c = Color.Lerp(c, Color.white, Mathf.Clamp01(0.5f - gl / texel));
            }
            else if (face == 0)
            {
                // happy: ^ arcs
                float r0 = Circle(x, y, exc, ey - 0.012f, 0.026f);
                ed = Mathf.Max(Mathf.Abs(r0) - 0.007f, ey - 0.012f - y);
                c = Color.Lerp(c, ink, Mathf.Clamp01(0.5f - ed / texel));
            }
            else
            {
                // squeezing: > <
                float lx = (x - exc) * -s, ly = y - ey;
                float seg = Mathf.Abs(ly) - 0.022f;
                ed = Mathf.Max(Mathf.Abs(lx + Mathf.Abs(ly) * 0.9f - 0.008f) - 0.007f, seg);
                c = Color.Lerp(c, ink, Mathf.Clamp01(0.5f - ed / texel));
            }
        }
        // the mouth: a smile, or a little "o"
        float md;
        if (face == 0) { float r0 = Circle(x, y, cx, 0.22f, 0.03f); md = Mathf.Max(Mathf.Abs(r0) - 0.006f, y - 0.212f); }
        else if (face == 1) md = Mathf.Abs(Circle(x, y, cx, 0.2f, 0.016f)) - 0.006f;
        else md = Mathf.Sqrt(Mathf.Pow((x - cx) / 1.5f, 2f) + Mathf.Pow(y - 0.2f, 2f)) - 0.015f;
        c = Color.Lerp(c, ink, Mathf.Clamp01(0.5f - md / texel));
        c.a = a;
        return c;
    }

    internal static Color PuffPaint(float x, float y, float texel)
    {
        float r = Mathf.Sqrt((x - 0.5f) * (x - 0.5f) + (y - 0.5f) * (y - 0.5f));
        float a = Mathf.Clamp01((0.46f - r) / 0.12f);
        return new Color(1f, 0.93f, 0.97f, a);
    }
}
