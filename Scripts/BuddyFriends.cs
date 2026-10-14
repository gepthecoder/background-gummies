using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Friends for the apple: at unlockLevel it plants a seed next to itself that wiggles, sprouts and poofs into a little
/// gummy tomato; at beanLevel a pale green coffee seed that becomes a little coffee bean; at burgerLevel a sesame seed that
/// becomes a little gummy slider (it grows into a burger); at dinoLevel a spotted egg that cracks and a baby dino hatches;
/// at duckLevel a lost little duckling waddles in from the edge of the screen and decides the apple is its mama (BuddyDuck).
/// Each friend is an inactive
/// object in the scene until then, with its own save and levels. Once born it's simply there every time the app starts.
/// Several due at once (an old save, a big level jump): one ceremony after the other.
/// </summary>
[RequireComponent(typeof(GummyBody))]
[DisallowMultipleComponent]
public class BuddyFriends : MonoBehaviour
{
    [Tooltip("The tomato (inactive in the scene until it's born). WhipIt > Setup creates it.")]
    public GummyBody tomato;
    [Tooltip("The apple plants the tomato seed at this level.")]
    public int unlockLevel = 5;
    [Tooltip("The coffee bean (inactive in the scene until it's born). WhipIt > Setup creates it.")]
    public GummyBody bean;
    [Tooltip("The apple plants the coffee seed at this level.")]
    public int beanLevel = 10;
    [Tooltip("The burger (inactive in the scene until it's born). WhipIt > Setup creates it.")]
    public GummyBody burger;
    [Tooltip("The apple plants the sesame seed at this level.")]
    public int burgerLevel = 15;
    [Tooltip("The little dino (inactive in the scene until it hatches). WhipIt > Setup creates it.")]
    public GummyBody dino;
    [Tooltip("The apple finds the dino egg at this level.")]
    public int dinoLevel = 20;
    [Tooltip("The duckling (inactive in the scene until it waddles in). WhipIt > Setup creates it.")]
    public GummyBody duck;
    [Tooltip("A lost duckling waddles in and adopts the apple at this level.")]
    public int duckLevel = 25;

    [Header("Sounds (WhipIt > Setup assigns them)")]
    public AudioClip plantClip;
    public AudioClip poofClip;
    [Range(0f, 1f)] public float volume = 0.6f;

    /// <summary>The tomato is here.</summary>
    public bool Born => IsBorn(tomato);
    public bool Planting { get; private set; }
    public event Action<GummyBody> FriendBorn;

    GummyBody body;
    GummyBrain brain;
    BuddySpeech speech;
    BuddyProgress progress;
    AudioSource audioSrc;
    int lastLevel;
    Sprite sproutSprite, dotSprite;
    readonly Sprite[] eggSprites = new Sprite[3];
    bool eggBaked;
    readonly List<Sprite> sprites = new List<Sprite>();
    readonly List<Texture2D> textures = new List<Texture2D>();
    readonly Dictionary<GummyBody, Sprite> seedSprites = new Dictionary<GummyBody, Sprite>();

    void Awake()
    {
        body = GetComponent<GummyBody>();
        brain = GetComponent<GummyBrain>();
        speech = GetComponent<BuddySpeech>();
        progress = GetComponent<BuddyProgress>();
        audioSrc = gameObject.AddComponent<AudioSource>();
        audioSrc.playOnAwake = false;
        audioSrc.spatialBlend = 0f;
    }

    void OnEnable() { if (progress) progress.LeveledUp += OnLevelUp; }
    void OnDisable() { if (progress) progress.LeveledUp -= OnLevelUp; }

    void OnDestroy()
    {
        foreach (var s in sprites) if (s) Destroy(s);
        foreach (var t in textures) if (t) Destroy(t);
    }

    void Start()
    {
        lastLevel = progress ? progress.Level : 1;
        if (!progress) return;
        // born before: just there (side by side); due but never born (levelled past it, an old save): plant soon
        foreach (var f in Friends())
            if (Due(f) && Saved(f)) Appear(f, SideSpot(f), false);
        if (Next() != null) StartCoroutine(Births(3f));
    }

    void OnLevelUp(int level, bool newStage)
    {
        int before = lastLevel;
        lastLevel = level;
        if (Planting || Next() == null) return;
        // the apple gets its whip at the same level as the coffee seed: let it show off the whip first
        var whip = GetComponent<BuddyWhip>();
        bool whipToo = whip && before < whip.unlockLevel && level >= whip.unlockLevel;
        StartCoroutine(Births(whipToo ? 6.5f : 1.2f));
    }

    /// <summary>Plant the tomato now, whatever the level (WhipIt > Buddy > Plant Tomato Now).</summary>
    public bool PlantNow() => Plant(tomato);

    /// <summary>Plant the coffee bean now, whatever the level (WhipIt > Buddy > Plant Coffee Bean Now).</summary>
    public bool PlantBeanNow() => Plant(bean);

    /// <summary>Plant the burger now, whatever the level (WhipIt > Buddy > Plant Burger Now).</summary>
    public bool PlantBurgerNow() => Plant(burger);

    /// <summary>Find the dino egg now, whatever the level (WhipIt > Buddy > Hatch Dino Now).</summary>
    public bool PlantDinoNow() => Plant(dino);

    /// <summary>Let the duckling waddle in now, whatever the level (WhipIt > Buddy > Duck Arrives Now).</summary>
    public bool PlantDuckNow() => Plant(duck);

    bool Plant(GummyBody friend)
    {
        if (!friend || IsBorn(friend) || Planting) return false;
        StartCoroutine(Births(0.2f, friend));
        return true;
    }

    public static bool IsBorn(GummyBody friend) => friend && (friend.gameObject.activeSelf || BuddyMonster.IsInside(friend)); // (inside a monster: born, just fused)

    IEnumerable<GummyBody> Friends()
    {
        if (tomato) yield return tomato;
        if (bean) yield return bean;
        if (burger) yield return burger;
        if (dino) yield return dino;
        if (duck) yield return duck;
    }

    int LevelFor(GummyBody friend) => friend == bean ? beanLevel : friend == burger ? burgerLevel : friend == dino ? dinoLevel : friend == duck ? duckLevel : unlockLevel;
    bool Due(GummyBody friend) => friend && progress && progress.Level >= LevelFor(friend);

    // the next one waiting to be born
    GummyBody Next()
    {
        foreach (var f in Friends()) if (Due(f) && !IsBorn(f)) return f;
        return null;
    }

    static string SaveName(GummyBody friend)
    {
        var p = friend.GetComponent<BuddyProgress>();
        return p && !string.IsNullOrEmpty(p.saveName) ? p.saveName : friend.name.ToLowerInvariant().Replace(' ', '_');
    }

    static bool Saved(GummyBody friend) => File.Exists(BuddyProgress.PathFor(SaveName(friend)));

    static string NameOf(GummyBody friend)
    {
        var sp = friend.GetComponent<BuddySpeech>();
        return sp ? sp.Name : "BUDDY";
    }

    // ------------------------------------------------------------------ the births

    IEnumerator Births(float delay, GummyBody only = null)
    {
        Planting = true;
        BuddySocial.QuietUntil = Time.time + 30f;
        yield return new WaitForSeconds(delay);

        while (true)
        {
            GummyBody friend = only ? (IsBorn(only) ? null : only) : Next();
            only = null;
            if (!friend) break;
            if (Saved(friend)) { Appear(friend, SideSpot(friend), false); continue; }
            BuddySocial.QuietUntil = Time.time + 30f;
            if (friend == duck) { yield return StartCoroutine(DuckArrives(friend)); continue; } // (no seed: it comes walking in)

            // wait until the apple sits still somewhere (not forever)
            float t0 = Time.time;
            while (Time.time - t0 < 8f && !(body.Grounded && !body.Held && !body.IsStuck && body.Velocity.sqrMagnitude < 1f)) yield return null;
            if (brain) brain.Linger(7f);
            if (speech)
            {
                if (friend == bean) speech.Say("A COFFEE SEED?!", 0f, true);
                else if (friend == burger) speech.Say("A SESAME SEED?!", 0f, true);
                else if (friend == dino) speech.Say("AN EGG?!", 0f, true);
                else speech.Say(BuddySpeech.Topic.Born, 1f, 0f, true); // "OH! A SEED!"
            }
            yield return new WaitForSeconds(0.5f);

            // the seed (the egg) pops out of its top and lands beside it
            bool egg = friend == dino;
            Vector2 land = SideSpot(friend);
            Vector2 from = body.Center + Vector2.up * (body.Extent.height * 0.45f);
            BuildSprites();
            var seed = MakeRenderer(egg ? "Egg" : "Seed", egg ? EggSprite(0) : SeedSprite(friend), body.sortingOrder + 2);
            var sprout = MakeRenderer("Sprout", sproutSprite, body.sortingOrder + 1);
            sprout.transform.SetParent(seed.transform, false);
            sprout.transform.localPosition = new Vector3(0f, 0.09f, 0f);
            sprout.transform.localScale = Vector3.zero;
            float SeedH = egg ? EggH / 200f : 0.2f;
            const float Fly = 0.75f, G = 14f;
            Vector2 target = land + Vector2.up * (SeedH * 0.5f);
            float vy = (target.y - from.y + 0.5f * G * Fly * Fly) / Fly, vx = (target.x - from.x) / Fly;
            Play(plantClip, 1.25f);
            for (float t = 0f; t < Fly; t += Time.deltaTime)
            {
                seed.transform.position = new Vector3(from.x + vx * t, from.y + vy * t - 0.5f * G * t * t, 0f);
                seed.transform.rotation = Quaternion.Euler(0f, 0f, -Mathf.Sign(vx) * 540f * t / Fly);
                Cover(seed);
                yield return null;
            }
            seed.transform.SetPositionAndRotation(target, Quaternion.identity);
            Play(plantClip, 0.9f);

            // plop, wiggle, sprout (the egg: cracks), wiggle harder, swell... poof
            for (float t = 0f; t < 2.4f; t += Time.deltaTime)
            {
                float sq = t < 0.25f ? 0.25f * Mathf.Sin(t / 0.25f * Mathf.PI) : 0f;       // landing squash
                float wig = t > 0.4f ? Mathf.Sin(t * (t < 1.6f ? 16f : 30f)) * (t < 1.6f ? 10f : 18f) : 0f;
                float swell = t > 1.9f ? 1f + 0.5f * (t - 1.9f) / 0.5f : 1f;
                seed.transform.localScale = new Vector3((1f + sq) * swell, (1f - sq) * swell, 1f);
                seed.transform.rotation = Quaternion.Euler(0f, 0f, wig);
                seed.transform.position = new Vector3(target.x, land.y + SeedH * 0.5f * (1f - sq) * swell, 0f);
                if (egg)
                {
                    int crack = t > 1.6f ? 2 : t > 0.9f ? 1 : 0;
                    seed.sprite = EggSprite(crack);
                    if (crack > 0 && t - Time.deltaTime <= (crack == 1 ? 0.9f : 1.6f)) Play(plantClip, crack == 1 ? 0.7f : 0.6f); // crack!
                }
                else
                {
                    float grow = Mathf.Clamp01((t - 0.9f) / 0.4f);
                    sprout.transform.localScale = Vector3.one * (grow * grow * (3f - 2f * grow));
                }
                Cover(seed);
                yield return null;
            }

            Vector2 poofAt = new Vector2(target.x, land.y + SeedH * 0.6f);
            Destroy(seed.gameObject);
            Play(poofClip, 1.1f);
            StartCoroutine(Poof(poofAt, friend == bean ? 1 : friend == burger ? 2 : friend == dino ? 3 : 0));
            Appear(friend, land, true);
            yield return new WaitForSeconds(4f); // let the hellos finish before the next one
        }
        Planting = false;
    }

    // the duckling: in from the floor's edge on the apple's side ("QUACK?"), it waddles over and meets its mama
    IEnumerator DuckArrives(GummyBody friend)
    {
        BuddySocial.QuietUntil = Time.time + 40f;
        float t0 = Time.time;
        while (Time.time - t0 < 8f && !(body.Grounded && !body.Held && !body.IsStuck && body.Velocity.sqrMagnitude < 1f)) yield return null;
        if (brain) brain.Linger(6f);

        var fp = friend.GetComponent<BuddyProgress>();
        float size = fp && fp.stages != null && fp.stages.Length > 0 ? fp.stages[0].size : friend.size;
        Rect r = ScreenBounds.WorldRect;
        int side = body.Center.x <= r.center.x ? 1 : -1; // +1: in from the left (the shorter waddle)
        float room = size * 1.3f + 0.15f;
        float fy = ScreenBounds.FloorNear(side > 0 ? r.xMin + 0.05f : r.xMax - 0.05f, body.Center.y); // (more than one screen: the floor at that edge)
        friend.transform.position = new Vector3(side > 0 ? r.xMin - size * 0.6f : r.xMax + size * 0.6f, fy + size * 0.5f + 0.03f, 0f);
        if (side > 0) friend.RoomLeft = room; else friend.RoomRight = room;
        friend.HoldRoom = true; // (BuddyDuck lets go once it's in)
        if (fp && !Saved(friend)) BuddyProgress.WriteSave(1, 0, 0, fp.saveName); // here for good
        friend.gameObject.SetActive(true);
        var dk = friend.GetComponent<BuddyDuck>();
        if (dk) dk.Arrive(body, side);
        else friend.HoldRoom = false;
        if (speech) StartCoroutine(SayLine(speech, "WHAT WAS THAT?", 1.3f));

        // the waddle and the meeting (the apple stays put for it)
        t0 = Time.time;
        while (dk && dk.Arriving && Time.time - t0 < 35f)
        {
            BuddySocial.QuietUntil = Time.time + 6f;
            yield return null;
        }
        // the others say hello
        float at = 0.8f;
        foreach (var other in GummyBody.All)
        {
            if (!other || other == body || other == friend) continue;
            var os = other.GetComponent<BuddySpeech>();
            if (os) { StartCoroutine(SayLater(os, BuddySpeech.Topic.Hi, NameOf(friend), at)); at += 1.3f; }
        }
        BuddySocial.QuietUntil = Time.time + at + 2f;
        FriendBorn?.Invoke(friend);
        yield return new WaitForSeconds(at + 1f);
    }

    IEnumerator SayLine(BuddySpeech who, string line, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (who && who.isActiveAndEnabled) who.Say(line, 0f, true);
    }

    // the friend's biggest size (its level isn't loaded before it wakes up)
    static float FriendSize(GummyBody friend)
    {
        var fp = friend.GetComponent<BuddyProgress>();
        float size = 0f;
        if (fp && fp.stages != null) foreach (var st in fp.stages) size = Mathf.Max(size, st.size);
        return size > 0f ? size : friend.size;
    }

    /// <summary>Activate the friend standing at this spot (feet at spot.y).</summary>
    void Appear(GummyBody friend, Vector2 spot, bool fresh)
    {
        var fp = friend.GetComponent<BuddyProgress>();
        float size = fresh && fp && fp.stages != null && fp.stages.Length > 0 ? fp.stages[0].size : FriendSize(friend);
        friend.transform.position = new Vector3(spot.x, spot.y + size * 0.5f + 0.03f, 0f);
        BuddySocial.QuietUntil = Time.time + (fresh ? 6f : 0f);
        friend.gameObject.SetActive(true);
        if (fresh)
        {
            if (fp && !Saved(friend)) BuddyProgress.WriteSave(1, 0, 0, fp.saveName); // born for good
            friend.Hop(new Vector2(0f, 6f), 0.6f);
            var fs = friend.GetComponent<BuddySpeech>();
            if (fs) StartCoroutine(SayLater(fs, BuddySpeech.Topic.Born, null, 0.5f)); // "HI!!"
            string who = NameOf(friend);
            if (speech) StartCoroutine(SayLater(speech, BuddySpeech.Topic.Hi, who, 2.2f)); // "HI TOMATO!"
            // the others say hello too
            float at = 3.6f;
            foreach (var other in GummyBody.All)
            {
                if (!other || other == body || other == friend) continue;
                var os = other.GetComponent<BuddySpeech>();
                if (os) { StartCoroutine(SayLater(os, BuddySpeech.Topic.Hi, who, at)); at += 1.3f; }
            }
            if (brain) brain.Linger(4f);
        }
        FriendBorn?.Invoke(friend);
    }

    IEnumerator SayLater(BuddySpeech who, BuddySpeech.Topic topic, string about, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (!who || !who.isActiveAndEnabled) yield break;
        if (about != null) who.Say(topic, about, 1f, 0f, true);
        else who.Say(topic, 1f, 0f, true);
    }

    // beside the apple where there's room: on what it stands on (or, while it's still dropping in at launch, next to it),
    // and not on top of a friend that's already there
    Vector2 SideSpot(GummyBody friend)
    {
        Rect r = ScreenBounds.WorldRect;
        float half = body.Extent.width > 0.01f ? body.Extent.width * 0.5f : body.size * 0.55f; // not simulated yet at launch
        float y = body.Grounded ? body.Extent.yMin : body.Extent.width > 0.01f ? r.yMin : body.Center.y - half;
        float lo = r.xMin + 0.3f, hi = r.xMax - 0.3f;
        if (body.Grounded && (body.OnPlatform || ScreenBounds.Multi)) { lo = Mathf.Max(lo, body.SupportSpan.x + 0.2f); hi = Mathf.Min(hi, body.SupportSpan.y - 0.2f); } // (more than one screen: its floor)
        float mine = FriendSize(friend);
        float off = half + mine * 0.55f + 0.15f;
        float cx = body.Center.x;
        float side = (hi - cx) >= (cx - lo) ? 1f : -1f; // the roomier side
        float best = 0f, bestScore = -1f;
        foreach (float sgn in new[] { side, -side })
            for (int k = 0; k < 3; k++)
            {
                float x = Mathf.Clamp(cx + sgn * (off + k * mine * 1.2f), lo, hi);
                if (Mathf.Abs(x - cx) < off * 0.6f) continue;
                float clear = Clearance(friend, x);
                float score = Mathf.Min(clear, mine) - k * 0.01f; // room enough, as close as possible
                if (score > bestScore + 1e-4f) { bestScore = score; best = x; }
                if (clear >= mine) break;
            }
        if (bestScore < 0f) best = Mathf.Clamp(cx + side * off, lo, hi);
        return new Vector2(best, y);
    }

    // distance from x to the nearest other buddy's side (big = free)
    float Clearance(GummyBody friend, float x)
    {
        float clear = 1e3f;
        foreach (var o in GummyBody.All)
        {
            if (!o || o == body || o == friend) continue;
            float oh = o.Extent.width > 0.01f ? o.Extent.width * 0.5f : o.size * 0.5f;
            clear = Mathf.Min(clear, Mathf.Abs(o.Center.x - x) - oh - FriendSize(friend) * 0.5f);
        }
        return clear;
    }

    // kind: 0 tomato, 1 coffee bean, 2 burger, 3 dino (its colours in the confetti; the dino's has eggshell bits)
    IEnumerator Poof(Vector2 at, int kind)
    {
        const int N = 12;
        var dots = new SpriteRenderer[N];
        var vel = new Vector2[N];
        for (int i = 0; i < N; i++)
        {
            dots[i] = MakeRenderer("Poof", dotSprite, body.sortingOrder + 3);
            float a = (i + UnityEngine.Random.value * 0.5f) / N * Mathf.PI * 2f;
            vel[i] = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * UnityEngine.Random.Range(2.2f, 3.4f);
            dots[i].transform.position = at;
            dots[i].color = kind == 1
                ? (i % 3 == 0 ? new Color(0.62f, 0.40f, 0.22f) : i % 3 == 1 ? new Color(1f, 0.95f, 0.85f) : new Color(0.60f, 0.70f, 0.45f))
                : kind == 2
                ? (i % 4 == 0 ? new Color(0.90f, 0.62f, 0.30f) : i % 4 == 1 ? new Color(1f, 0.95f, 0.85f) : i % 4 == 2 ? new Color(0.50f, 0.82f, 0.30f) : new Color(1f, 0.80f, 0.25f))
                : kind == 3
                ? (i % 3 == 0 ? new Color(0.98f, 0.96f, 0.88f) : i % 3 == 1 ? new Color(0.45f, 0.80f, 0.33f) : new Color(1f, 0.72f, 0.22f))
                : (i % 3 == 0 ? new Color(1f, 0.55f, 0.45f) : i % 3 == 1 ? new Color(1f, 0.95f, 0.85f) : new Color(0.55f, 0.85f, 0.4f));
        }
        var flash = MakeRenderer("Flash", dotSprite, body.sortingOrder + 2);
        flash.transform.position = at;
        for (float t = 0f; t < 0.55f; t += Time.deltaTime)
        {
            float u = t / 0.55f, dt = Time.deltaTime;
            for (int i = 0; i < N; i++)
            {
                vel[i] *= 1f - 5f * dt;
                dots[i].transform.position += (Vector3)(vel[i] * dt);
                dots[i].transform.localScale = Vector3.one * (0.09f * (1f - u));
                Cover(dots[i]);
            }
            flash.transform.localScale = Vector3.one * (0.2f + 0.7f * Mathf.Sqrt(u));
            flash.color = new Color(1f, 1f, 1f, 0.8f * (1f - u));
            Cover(flash);
            yield return null;
        }
        foreach (var d in dots) Destroy(d.gameObject);
        Destroy(flash.gameObject);
    }

    // behind a window in front of the apple (Occlusion): not drawn there
    void Cover(SpriteRenderer sr)
    {
        if (!sr) return;
        var layer = GetComponent<BuddyLayer>();
        sr.enabled = !layer || !Occlusion.Hidden(sr.transform.position, layer.Layer);
    }

    void Play(AudioClip clip, float pitch)
    {
        if (!audioSrc || !clip) return;
        audioSrc.pitch = pitch;
        audioSrc.PlayOneShot(clip, volume);
    }

    SpriteRenderer MakeRenderer(string n, Sprite s, int order)
    {
        var go = new GameObject(n);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = s;
        sr.sortingOrder = order;
        if (body.spriteMaterial) sr.sharedMaterial = body.spriteMaterial;
        return sr;
    }

    // ------------------------------------------------------------------ code-drawn sprites

    void BuildSprites()
    {
        if (sproutSprite) return;
        // sprout: two little leaves on a stem
        sproutSprite = Bake(32, 24, 200f, out Texture2D sproutTex, (u, v) =>
        {
            float stem = Mathf.Abs(u) - 0.07f;
            if (v > 0.2f) stem = 1f;
            float l1 = Leaf(u + 0.42f, v - 0.25f, 0.25f), l2 = Leaf(-u + 0.42f, v - 0.25f, -0.25f);
            float d = Mathf.Min(stem, Mathf.Min(l1, l2));
            return Shade(d, new Color(0.36f, 0.72f, 0.3f), new Color(0.16f, 0.4f, 0.14f), new Vector2(9f, 9f));
        });
        dotSprite = Bake(16, 16, 16f, out Texture2D dotTex, (u, v) =>
        {
            float d = new Vector2(u, v).magnitude - 0.85f;
            return new Color(1f, 1f, 1f, Mathf.Clamp01(-d * 8f));
        });
    }

    // the tomato's seed: a little brown teardrop; the coffee seed: a pale green oval with a crease (a green coffee bean);
    // the burger's: a cream sesame seed
    Sprite SeedSprite(GummyBody friend)
    {
        if (seedSprites.TryGetValue(friend, out Sprite s) && s) return s;
        bool coffee = friend == bean, sesame = friend == burger;
        s = Bake(28, 40, 200f, out Texture2D tex, (u, v) =>
        {
            if (coffee)
            {
                float d = (new Vector2(u / 0.8f, v / 0.92f).magnitude - 1f) * 0.8f;
                float crease = Mathf.Abs(u - 0.12f * Mathf.Sin(v * 2.4f)) - 0.07f;
                if (crease < 0f && Mathf.Abs(v) < 0.7f && d < -0.12f) return new Color(0.36f, 0.42f, 0.24f, 1f);
                return Shade(d, new Color(0.64f, 0.72f, 0.48f), new Color(0.33f, 0.40f, 0.22f), new Vector2(u + 0.32f, v - 0.3f));
            }
            float y = v * 1.1f;
            float w = (sesame ? 0.66f : 0.78f) * Mathf.Sqrt(Mathf.Max(0f, 1f - y * y)) * (1f - 0.35f * y); // pointy top
            if (sesame) return Shade(Mathf.Abs(u) - w, new Color(0.98f, 0.93f, 0.76f), new Color(0.72f, 0.62f, 0.40f), new Vector2(u + 0.24f, v - 0.25f));
            return Shade(Mathf.Abs(u) - w, new Color(0.62f, 0.42f, 0.24f), new Color(0.3f, 0.18f, 0.1f), new Vector2(u + 0.28f, v - 0.25f));
        });
        seedSprites[friend] = s;
        return s;
    }

    const int EggW = 40, EggH = 50;

    // the dino egg: cream with green spots, a crack (1), cracked right across (2)
    Sprite EggSprite(int crack)
    {
        if (!eggBaked)
        {
            for (int k = 0; k < 3; k++)
            {
                int ck = k;
                eggSprites[k] = Bake(EggW, EggH, 200f, out Texture2D tex, (u, v) => EggPaint(u, v, ck));
            }
            eggBaked = true;
        }
        return eggSprites[Mathf.Clamp(crack, 0, 2)];
    }

    internal static Color EggPaint(float u, float v, int crack)
    {
        // u, v -1..1 across the sprite (it's taller than wide): an egg, rounder at the bottom
        float x = u * EggW / (float)EggH, y = v;
        float rx = 0.74f * (1f - 0.16f * y);
        float d = (new Vector2(x / rx, (y + 0.06f) / (y > -0.06f ? 0.93f : 0.88f)).magnitude - 1f) * 0.6f;
        if (d > 0.04f) return new Color(0f, 0f, 0f, 0f);
        Color fill = new Color(0.98f, 0.96f, 0.88f), rim = new Color(0.70f, 0.64f, 0.50f);
        Color c = d > -0.08f ? rim : fill;
        // green spots
        Vector2 p = new Vector2(x, y);
        foreach (var sp in new[] { new Vector3(-0.28f, 0.32f, 0.13f), new Vector3(0.22f, 0.05f, 0.16f), new Vector3(-0.16f, -0.38f, 0.12f), new Vector3(0.30f, -0.48f, 0.09f), new Vector3(0.05f, 0.62f, 0.09f) })
            if (d < -0.08f && (p - new Vector2(sp.x, sp.y)).magnitude < sp.z) c = new Color(0.55f, 0.82f, 0.40f);
        // shine
        if ((p - new Vector2(-0.3f, 0.45f)).magnitude < 0.1f && d < -0.08f) c = Color.Lerp(c, Color.white, 0.6f);
        // the crack: a zigzag across the middle (from the right first)
        if (crack > 0 && d < -0.02f)
        {
            float from = crack == 1 ? 0.15f : -1f;
            if (x > from)
            {
                float zig = 0.08f * (Mathf.Abs(Mathf.Repeat(x * 3.2f, 1f) - 0.5f) * 4f - 1f);
                if (Mathf.Abs(y - (0.08f + zig)) < 0.045f) c = new Color(0.32f, 0.26f, 0.18f);
            }
        }
        c.a = Mathf.Clamp01((0.04f - d) * 30f);
        return c;
    }

    static float Leaf(float u, float v, float tilt)
    {
        float c = Mathf.Cos(tilt), s = Mathf.Sin(tilt);
        float a = u * c - v * s, b = u * s + v * c;
        return new Vector2(a / 0.42f, b / 0.2f).magnitude - 1f;
    }

    static Color Shade(float d, Color fill, Color rim, Vector2 shine)
    {
        if (d > 0.04f) return new Color(0f, 0f, 0f, 0f);
        Color c = d > -0.1f ? rim : fill;
        if (shine.magnitude < 0.14f) c = Color.Lerp(c, Color.white, 0.55f);
        c.a = Mathf.Clamp01((0.04f - d) * 30f);
        return c;
    }

    Sprite Bake(int w, int h, float ppu, out Texture2D tex, Func<float, float, Color> f)
    {
        var px = new Color32[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float u = ((x + 0.5f) / w * 2f - 1f), v = ((y + 0.5f) / h * 2f - 1f);
                px[y * w + x] = f(u, v);
            }
        tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "Friend" };
        tex.SetPixels32(px);
        tex.Apply(false, true);
        var sprite = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), ppu);
        sprites.Add(sprite);
        textures.Add(tex);
        return sprite;
    }
}
