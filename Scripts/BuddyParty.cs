using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Level-up party: a moment after a buddy levels up (from level 3; not while a seed is being planted), a cake with a
/// candle pops up next to it, confetti flies, a little tune plays and the others come over (the route planner gets them
/// there) and bounce around it. Then they all have some cake (+1 XP each) and the party's over. One party at a time.
/// This sits on every buddy (WhipIt > Setup); the party itself is LevelUpParty.
/// </summary>
[RequireComponent(typeof(GummyBody))]
[DisallowMultipleComponent]
public class BuddyParty : MonoBehaviour
{
    [Tooltip("Parties from this level on (the first few come quickly).")]
    public int fromLevel = 3;
    [Tooltip("The party tune (WhipIt > Setup assigns it).")]
    public AudioClip tune;
    [Range(0f, 1f)] public float volume = 0.5f;
    [Tooltip("Sprite material (WhipIt > Setup assigns it).")]
    public Material material;

    GummyBody body;
    BuddyProgress progress;
    float pendingAt = -1f;
    int pendingLevel;

    void Awake()
    {
        body = GetComponent<GummyBody>();
        progress = GetComponent<BuddyProgress>();
    }

    void OnEnable() { if (progress) progress.LeveledUp += OnLevelUp; }
    void OnDisable() { if (progress) progress.LeveledUp -= OnLevelUp; pendingAt = -1f; }

    void OnLevelUp(int level, bool newStage)
    {
        if (level < fromLevel) return;
        pendingAt = Time.time + 2.2f; // (after its level-up line)
        pendingLevel = level;
    }

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("BuddyParty.Update", t0); }
    void UpdateTimed()
    {
        if (pendingAt < 0f || Time.time < pendingAt) return;
        if (body.Held || !body.Grounded || BandJam.Current || body.InRoom) { pendingAt = Time.time + 0.5f; return; } // once it's down again (and the jam's over, and it's out of a window)
        pendingAt = -1f;
        if (LevelUpParty.Current || AnyPlanting()) return;
        LevelUpParty.Throw(body, pendingLevel, tune, volume, material ? material : body.spriteMaterial);
    }

    static bool AnyPlanting()
    {
        foreach (var b in GummyBody.All)
        {
            var f = b ? b.GetComponent<BuddyFriends>() : null;
            if (f && f.Planting) return true;
        }
        return false;
    }
}

/// <summary>The party (made by BuddyParty): cake, confetti, tune, guests, cake for everyone.</summary>
[DefaultExecutionOrder(6)] // after BuddyEater / BuddyFetch: the guests head for the cake when there's no snack or ball
public class LevelUpParty : MonoBehaviour
{
    /// <summary>The party going on (null = none).</summary>
    public static LevelUpParty Current { get; private set; }
    /// <summary>Where the cake is.</summary>
    public Vector2 Cake => cakeAt;
    public GummyBody Host => host;
    /// <summary>Buddies that came over (the host too).</summary>
    public int Guests => arrived.Count;

    const float Length = 11f, EatAt = 9f;
    const int ConfettiCount = 48;

    GummyBody host;
    int level;
    Vector2 cakeAt;
    float t, surfaceY, cakeW;
    long cakeWin; // Occlusion: the window top the cake stands on (0 = the floor)
    SpriteRenderer cake, flame;
    AudioSource audioSrc;
    readonly List<GummyBody> invited = new List<GummyBody>();
    readonly HashSet<GummyBody> arrived = new HashSet<GummyBody>();
    readonly Dictionary<GummyBody, float> nextBounce = new Dictionary<GummyBody, float>();
    readonly SpriteRenderer[] bits = new SpriteRenderer[ConfettiCount];
    readonly Vector2[] bitPos = new Vector2[ConfettiCount], bitVel = new Vector2[ConfettiCount];
    readonly float[] bitAge = new float[ConfettiCount], bitLife = new float[ConfettiCount], bitSpin = new float[ConfettiCount], bitAngle = new float[ConfettiCount];
    bool eaten;
    static Sprite s_cake, s_flame, s_bit;
    static readonly Color[] Party = { new Color(1f, 0.36f, 0.45f), new Color(1f, 0.82f, 0.2f), new Color(0.35f, 0.75f, 1f), new Color(0.5f, 0.9f, 0.45f), new Color(0.78f, 0.5f, 1f), new Color(1f, 0.6f, 0.25f) };

    /// <summary>Throw a party for this buddy (it just reached this level).</summary>
    public static LevelUpParty Throw(GummyBody host, int level, AudioClip tune, float volume, Material material)
    {
        var go = new GameObject("Level Up Party");
        var p = go.AddComponent<LevelUpParty>();
        p.Begin(host, level, tune, volume, material);
        return p;
    }

    void OnEnable() { if (!Current) Current = this; }
    void OnDisable() { if (Current == this) Current = null; }

    void Begin(GummyBody host, int level, AudioClip tune, float volume, Material material)
    {
        Current = this;
        this.host = host;
        this.level = level;
        float s = host.size / 0.6f;
        cakeW = 0.5f * Mathf.Sqrt(s) + 0.1f;
        // the cake: beside the host, on what it's standing on (towards the middle of the screen)
        Rect r = ScreenBounds.ScreenRectAt(host.Center); // (its own screen)
        float side = host.Center.x > r.center.x ? -1f : 1f;
        Vector2 want = new Vector2(Mathf.Clamp(host.Center.x + side * (host.size * 0.9f + cakeW * 0.5f), r.xMin + cakeW, r.xMax - cakeW), host.Extent.yMax);
        surfaceY = BuddyRoute.SurfaceUnder(want, host.size * 0.8f, host.solidWindows, out float y) ? y : r.yMin;
        if (surfaceY < host.Extent.yMin - 0.3f) { want.x = host.Center.x; surfaceY = host.Extent.yMin - host.SkateLift; } // (nothing beside it: right where it is)
        cakeAt = new Vector2(want.x, surfaceY);
        var hl = host.GetComponent<BuddyLayer>();
        cakeWin = hl && hl.SurfaceId > 0 ? hl.SurfaceId : 0;
        cake = MakeSprite("Cake", CakeSprite(), material, host.sortingOrder - 3);
        cake.transform.position = new Vector3(cakeAt.x, cakeAt.y, 0f);
        flame = MakeSprite("Candle Flame", Flame(), material, host.sortingOrder - 2);
        for (int i = 0; i < ConfettiCount; i++) { bits[i] = MakeSprite("Confetti", Bit(), material, host.sortingOrder + 4); bits[i].enabled = false; }
        Burst(host.Center + Vector2.up * host.size * 0.4f, ConfettiCount);
        audioSrc = gameObject.AddComponent<AudioSource>();
        audioSrc.playOnAwake = false;
        audioSrc.spatialBlend = 0f;
        if (tune) audioSrc.PlayOneShot(tune, volume);
        // everyone's invited (the ones awake and not busy come over)
        foreach (var b in GummyBody.All) if (b && b != host && b.isActiveAndEnabled && !b.IsMonster && !b.InRoom) invited.Add(b); // (not the monster: they'd all run)
        arrived.Add(host);
        var sp = host.GetComponent<BuddySpeech>();
        if (sp) sp.Say(Line(sp, Pick("PARTY TIME!", "CAKE!!", "I'M LEVEL " + level + "!"), Pick("PARTY! FOR ME!", "LEVEL " + level + ". OBVIOUSLY", "CAKE. NOW."), Pick("LEVEL " + level + "! PARTY!", "CAKE-PRESSO!", "PARTY-PRESSO!")), 0f, true);
    }

    SpriteRenderer MakeSprite(string name, Sprite sprite, Material material, int order)
    {
        var g = new GameObject(name);
        g.transform.SetParent(transform, false);
        var sr = g.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.sortingOrder = order;
        if (material) sr.sharedMaterial = material;
        return sr;
    }

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("LevelUpParty.Update", t0); }
    void UpdateTimed()
    {
        float dt = Mathf.Min(Time.deltaTime, 0.05f), now = Time.time;
        t += dt;
        UpdateConfetti(dt);
        if (!host) { End(); return; }

        // the cake: pops in, candle flickers; eaten at the end
        float pop = Mathf.Clamp01(t / 0.25f), k = eaten ? Mathf.Clamp01(1f - (t - EatAt) / 0.6f) : pop < 1f ? Mathf.Lerp(0.2f, 1.1f, pop) : 1f + 0.1f * Mathf.Max(0f, 1f - (t - 0.25f) / 0.15f);
        cake.transform.localScale = new Vector3(cakeW * k, cakeW * k, 1f);
        cake.transform.position = new Vector3(cakeAt.x, cakeAt.y, 0f);
        bool hid = Occlusion.Hidden(cakeAt + Vector2.up * (cakeW * 0.5f), CakeLayer); // behind a window in front of it
        cake.color = new Color(1f, 1f, 1f, hid ? 0f : 1f);
        flame.enabled = !eaten && pop >= 1f && !hid;
        float fl = 1f + 0.18f * Mathf.Sin(now * 23f) + 0.1f * Mathf.Sin(now * 37f);
        flame.transform.localScale = new Vector3(cakeW * 0.16f * fl, cakeW * 0.22f * fl, 1f);
        flame.transform.position = new Vector3(cakeAt.x, cakeAt.y + cakeW * 0.98f, 0f);

        // the guests: over to the cake (no snack or ball in the way), then bounce around it
        foreach (var b in invited)
        {
            if (!b || !b.isActiveAndEnabled) continue;
            var brain = b.GetComponent<GummyBrain>();
            if (!brain || brain.Current == GummyBrain.State.Asleep || b.Held || BuddyTower.Has(b)) continue;
            bool near = Mathf.Abs(b.Center.x - cakeAt.x) < cakeW * 0.5f + b.size * 1.6f && Mathf.Abs(b.Extent.yMin - b.SkateLift - surfaceY) < 0.25f;
            if (near && !arrived.Contains(b))
            {
                arrived.Add(b);
                var sp = b.GetComponent<BuddySpeech>();
                if (sp) sp.Say(Line(sp, Pick("HAPPY LEVEL UP!", "CONGRATS!", "PARTY!!"), Pick("FINE. CONGRATS", "WHERE'S MY CAKE", "PARTY!"), Pick("CONGRATS-PRESSO!", "PARTY! BUZZ!", "CAKE + COFFEE!")), 0f, false);
                brain.Food = null;
            }
            if (arrived.Contains(b) || eaten) continue;
            var eater = b.GetComponent<BuddyEater>();
            var fetch = b.GetComponent<BuddyFetch>();
            if ((eater && eater.Target) || (fetch && fetch.Carrying)) continue;
            float off = (invited.IndexOf(b) % 2 == 0 ? -1f : 1f) * (cakeW * 0.5f + b.size * 0.7f);
            brain.Food = new Vector2(cakeAt.x + off, surfaceY + b.size * 0.3f);
            brain.FoodSurface = surfaceY;
        }
        // a happy bounce now and then by the cake
        if (!eaten)
            foreach (var b in arrived)
            {
                if (!b || !b.Grounded || b.Held) continue;
                var brain = b.GetComponent<GummyBrain>();
                if (brain && brain.Current != GummyBrain.State.Idle) continue;
                if (!nextBounce.TryGetValue(b, out float nb)) { nextBounce[b] = now + Random.Range(0.2f, 1f); continue; }
                if (now < nb) continue;
                nextBounce[b] = now + Random.Range(0.7f, 1.5f);
                b.Hop(new Vector2(0f, GummyBody.JumpSpeed(Random.Range(0.25f, 0.5f) * b.size / 0.6f, b.gravity, b.Drag)), 0.6f);
                if (brain) brain.Linger(2f);
            }

        if (!eaten && t >= EatAt) EatCake();
        if (t >= Length && Confetti() == 0) End();
    }

    void EatCake()
    {
        eaten = true;
        // crumbs, and a bite for everyone close by
        for (int i = 0; i < 14; i++) Spawn(cakeAt + Vector2.up * cakeW * 0.4f, new Vector2(Random.Range(-2.5f, 2.5f), Random.Range(1.5f, 4f)), new Color(0.98f, 0.82f, 0.72f));
        foreach (var b in arrived)
        {
            if (!b || Mathf.Abs(b.Center.x - cakeAt.x) > cakeW * 0.5f + b.size * 2f) continue;
            var brain = b.GetComponent<GummyBrain>();
            if (brain) brain.Treat();
            var p = b.GetComponent<BuddyProgress>();
            if (p) p.AddXp(1, b.Center);
        }
        var sp = host ? host.GetComponent<BuddySpeech>() : null;
        if (sp) sp.Say(Line(sp, "NOM NOM CAKE", "BEST. CAKE.", "CAKE + ESPRESSO!"), 0f, false);
    }

    void End()
    {
        foreach (var b in invited)
        {
            if (!b || arrived.Contains(b)) continue;
            var brain = b.GetComponent<GummyBrain>();
            if (brain) brain.Food = null;
        }
        if (Current == this) Current = null;
        Destroy(gameObject);
        enabled = false;
    }

    // ------------------------------------------------------------------ confetti

    void Burst(Vector2 at, int n)
    {
        for (int i = 0; i < n; i++)
        {
            float a = Random.Range(-0.6f, 0.6f) + Mathf.PI * 0.5f, v = Random.Range(3.5f, 7f);
            Spawn(at, new Vector2(Mathf.Cos(a) * v, Mathf.Sin(a) * v), Party[i % Party.Length]);
        }
    }

    void Spawn(Vector2 at, Vector2 vel, Color c)
    {
        int best = -1;
        for (int i = 0; i < ConfettiCount; i++) if (!bits[i].enabled) { best = i; break; }
        if (best < 0) return;
        bitPos[best] = at;
        bitVel[best] = vel;
        bitAge[best] = 0f;
        bitLife[best] = Random.Range(2.2f, 3.8f);
        bitSpin[best] = Random.Range(-720f, 720f);
        bitAngle[best] = Random.Range(0f, 360f);
        bits[best].color = c;
        bits[best].enabled = true;
    }

    int Confetti()
    {
        int n = 0;
        foreach (var b in bits) if (b && b.enabled) n++;
        return n;
    }

    int CakeLayer => cakeWin != 0 && Occlusion.Exists(cakeWin) ? Occlusion.LayerOf(cakeWin) : Occlusion.Back;

    void UpdateConfetti(float dt)
    {
        Rect r = ScreenBounds.WorldRect;
        for (int i = 0; i < ConfettiCount; i++)
        {
            var sr = bits[i];
            if (!sr || !sr.enabled) continue;
            bitAge[i] += dt;
            if (bitAge[i] > bitLife[i]) { sr.enabled = false; continue; }
            // flutters down: strong air drag, a little sideways wobble
            bitVel[i] += new Vector2(Mathf.Sin(bitAge[i] * 6f + i) * 2f, -7f) * dt;
            bitVel[i] *= 1f - 2.2f * dt;
            bitPos[i] += bitVel[i] * dt;
            if (bitPos[i].y < r.yMin + 0.02f) { bitPos[i].y = r.yMin + 0.02f; bitVel[i] = Vector2.zero; } // (lies on the taskbar a moment)
            bitAngle[i] += bitSpin[i] * dt;
            float flip = Mathf.Abs(Mathf.Cos(bitAge[i] * 9f + i));
            sr.transform.position = new Vector3(bitPos[i].x, bitPos[i].y, 0f);
            sr.transform.rotation = Quaternion.Euler(0f, 0f, bitAngle[i]);
            sr.transform.localScale = new Vector3(0.09f, 0.05f * (0.3f + 0.7f * flip), 1f);
            Color c = sr.color;
            c.a = Occlusion.Hidden(bitPos[i], CakeLayer) ? 0f : Mathf.Clamp01((bitLife[i] - bitAge[i]) / 0.5f);
            sr.color = c;
        }
    }

    static string Line(BuddySpeech sp, string apple, string tomato, string bean) => sp.voice == BuddySpeech.Voice.Tomato ? tomato : sp.voice == BuddySpeech.Voice.Bean ? bean : apple;
    static string Pick(params string[] l) => l[Random.Range(0, l.Length)];

    // ------------------------------------------------------------------ drawing (baked once)

    static Sprite Bit()
    {
        if (s_bit) return s_bit;
        var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "Confetti", filterMode = FilterMode.Bilinear };
        var px = new Color32[16];
        for (int i = 0; i < 16; i++) px[i] = new Color32(255, 255, 255, 255);
        tex.SetPixels32(px);
        tex.Apply(false, true);
        s_bit = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4f); // 1 x 1 unit
        return s_bit;
    }

    static Sprite Flame()
    {
        if (s_flame) return s_flame;
        const int w = 16, h = 24;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = "Flame", filterMode = FilterMode.Bilinear };
        var px = new Color32[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float u = (x + 0.5f) / w * 2f - 1f, v = (y + 0.5f) / h;
                float half = 0.9f * Mathf.Sin(Mathf.PI * Mathf.Pow(v, 0.7f)) * (1f - v * 0.35f); // a teardrop
                float d = Mathf.Abs(u) - half;
                Color c = Color.Lerp(new Color(1f, 0.97f, 0.7f), new Color(1f, 0.55f, 0.1f), Mathf.Clamp01(Mathf.Abs(u) / Mathf.Max(half, 0.01f)));
                c.a = Mathf.Clamp01(0.5f - d * w * 0.5f);
                px[y * w + x] = c;
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        s_flame = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0f), w);
        return s_flame;
    }

    // a two-layer pink cake with white frosting and a striped candle; 1 unit wide, pivot at the bottom middle
    static Sprite CakeSprite()
    {
        if (s_cake) return s_cake;
        const int res = 64;
        var tex = new Texture2D(res, res, TextureFormat.RGBA32, false) { name = "Cake", filterMode = FilterMode.Bilinear };
        var px = new Color32[res * res];
        for (int y = 0; y < res; y++)
            for (int x = 0; x < res; x++)
            {
                float u = (x + 0.5f) / res * 2f - 1f, v = (y + 0.5f) / res;
                px[y * res + x] = CakePaint(u, v);
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        s_cake = Sprite.Create(tex, new Rect(0, 0, res, res), new Vector2(0.5f, 0f), res);
        return s_cake;
    }

    // u -1..1 across, v 0..1 up
    static Color CakePaint(float u, float v)
    {
        Color c = new Color(0f, 0f, 0f, 0f);
        float au = Mathf.Abs(u);
        // plate
        if (v < 0.06f && au < 0.98f) c = new Color(0.86f, 0.88f, 0.92f) * (0.9f + v);
        // bottom layer
        if (v >= 0.05f && v < 0.38f && au < 0.82f) c = new Color(0.98f, 0.62f, 0.72f) * (0.9f + 0.1f * (1f - au));
        // top layer
        if (v >= 0.38f && v < 0.64f && au < 0.64f) c = new Color(1f, 0.72f, 0.8f) * (0.92f + 0.08f * (1f - au));
        // frosting on each layer, dripping down a bit
        float drip1 = 0.33f - 0.05f * Mathf.Max(0f, Mathf.Sin(u * 11f)), drip2 = 0.59f - 0.05f * Mathf.Max(0f, Mathf.Sin(u * 13f + 1f));
        if ((v >= drip1 && v < 0.4f && au < 0.84f) || (v >= drip2 && v < 0.66f && au < 0.66f)) c = new Color(1f, 0.98f, 0.95f);
        // sprinkles
        if (v > 0.1f && v < 0.3f && au < 0.78f && Mathf.Abs(Mathf.Sin(u * 37f) * Mathf.Sin(v * 53f)) > 0.93f) c = Party[(int)((u + 1f) * 7f + v * 13f) % Party.Length];
        // the candle: blue and white stripes
        if (v >= 0.64f && v < 0.97f && au < 0.07f) c = Mathf.Repeat((v - u * 0.5f) * 14f, 1f) < 0.5f ? new Color(0.35f, 0.6f, 1f) : new Color(0.97f, 0.97f, 1f);
        return c;
    }
}
