using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Speech bubbles: short pixel-font lines when things happen (grabbed, thrown, splat, dizzy, poked too much, petted,
/// a snack lands, eating, levelling up, falling asleep / waking, climbing, dangling, peeking, found, window boing,
/// squeezed out from under a window) and now and then just to chat. Each buddy has its own voice (lines + blip pitch).
/// Other scripts can make it talk with Say(). Now and then it chats about you (what you told it in the chat box).
/// </summary>
[RequireComponent(typeof(GummyBody))]
[DisallowMultipleComponent]
public class BuddySpeech : MonoBehaviour
{
    public enum Voice { Apple, Tomato, Bean, Burger, Dino, Duck, Monster }
    public enum Topic
    {
        Grab, Throw, Splat, Dizzy, Annoyed, Pet, SnackSpotted, Ate, LevelUp, NewStage, Sleep, Wake, Climb, Top, Dangle,
        Peek, Found, Boing, Escape, Chatter, Hi, Mine, Stolen, Innocent, Born
    }

    public Voice voice = Voice.Apple;
    public bool talk = true;
    [Tooltip("0 = quiet, 1 = never shuts up.")]
    [Range(0f, 1f)] public float chattiness = 0.7f;
    [Tooltip("At least this long between two bubbles (seconds).")]
    public float minGap = 2.5f;
    [Tooltip("Random chatter every this many seconds (min, max) while it's awake and you're nearby.")]
    public Vector2 chatterEvery = new Vector2(50f, 110f);
    [Tooltip("Screen pixels per font pixel on a 1080p screen.")]
    public int pixelScale1080 = 3;

    [Header("Sound (WhipIt > Setup assigns it)")]
    public AudioClip blipClip;
    [Range(0f, 1f)] public float volume = 0.25f;

    /// <summary>A bubble is up right now.</summary>
    public bool Talking => bubble && bubble.enabled;
    public string Current { get; private set; }
    public event Action<string> Said;

    GummyBody body;
    GummyBrain brain;
    BuddyEater eater;
    BuddyProgress progress;
    WindowWobble wobble;
    BuddyHud hud;
    BuddyLayer layer;
    AudioSource audioSrc;
    GameObject holder;
    SpriteRenderer bubble;
    float shownAt = -10f, hold, lastSaid = -10f, nextChatter, fontPx = 0.028f, screenPx;
    int bubbleW, bubbleH;
    Rect placed;            // where the bubble is (world), so another buddy's bubble can stack above it
    static readonly List<BuddySpeech> s_all = new List<BuddySpeech>();
    readonly Dictionary<Topic, float> lastTopic = new Dictionary<Topic, float>();
    readonly Dictionary<string, Sprite> cache = new Dictionary<string, Sprite>();
    readonly List<Texture2D> textures = new List<Texture2D>();

    // what it noticed last frame
    bool wasHeld, wasAnnoyed, wasDangling;
    GummyBrain.State lastState;
    CuteFace.Expression lastMood;
    float lastAte = -10f;

    void Awake()
    {
        body = GetComponent<GummyBody>();
        brain = GetComponent<GummyBrain>();
        eater = GetComponent<BuddyEater>();
        progress = GetComponent<BuddyProgress>();
        wobble = GetComponent<WindowWobble>();
        hud = GetComponent<BuddyHud>();
        layer = GetComponent<BuddyLayer>();
        audioSrc = gameObject.AddComponent<AudioSource>();
        audioSrc.playOnAwake = false;
        audioSrc.spatialBlend = 0f;
        nextChatter = Time.time + UnityEngine.Random.Range(chatterEvery.x * 0.5f, chatterEvery.y);
    }

    void OnEnable()
    {
        if (!s_all.Contains(this)) s_all.Add(this);
        body.Splat += OnSplat;
        body.Escaped += OnEscaped;
        Snack.Landed += OnSnackLanded;
        if (eater) eater.Ate += OnAte;
        if (progress) progress.LeveledUp += OnLevelUp;
        if (wobble) wobble.Wobbled += OnWobbled;
        if (brain) brain.Moment += OnMoment;
    }

    void OnDisable()
    {
        s_all.Remove(this);
        body.Splat -= OnSplat;
        body.Escaped -= OnEscaped;
        Snack.Landed -= OnSnackLanded;
        if (eater) eater.Ate -= OnAte;
        if (progress) progress.LeveledUp -= OnLevelUp;
        if (wobble) wobble.Wobbled -= OnWobbled;
        if (brain) brain.Moment -= OnMoment;
        if (bubble) bubble.enabled = false;
    }

    void OnDestroy()
    {
        if (holder) Destroy(holder);
        foreach (var s in cache.Values) if (s) Destroy(s);
        foreach (var t in textures) if (t) Destroy(t);
    }

    // ------------------------------------------------------------------ talking

    /// <summary>Say something now (force = ignore the gap between bubbles). Returns false if it kept quiet.</summary>
    public bool Say(string text, float seconds = 0f, bool force = false)
    {
        if (!talk || string.IsNullOrEmpty(text)) return false;
        float now = Time.time;
        if (!force && now - lastSaid < minGap) return false;
        if (!holder) Build();
        Current = text;
        lastSaid = now;
        shownAt = now;
        hold = seconds > 0f ? seconds : Mathf.Clamp(1.3f + 0.07f * text.Length, 1.5f, 3.5f);
        bubble.sprite = BubbleSprite(text);
        bubble.enabled = true;
        if (audioSrc && blipClip)
        {
            audioSrc.pitch = (voice == Voice.Tomato ? 1.35f : voice == Voice.Bean ? 1.6f : voice == Voice.Burger ? 0.74f : voice == Voice.Dino ? 1.22f : voice == Voice.Duck ? 1.5f : voice == Voice.Monster ? 0.55f : 1f) * UnityEngine.Random.Range(0.95f, 1.05f);
            audioSrc.PlayOneShot(blipClip, volume);
        }
        Said?.Invoke(text);
        return true;
    }

    /// <summary>Say a random line about this topic (with its own cooldown and a chance to stay quiet).</summary>
    public bool Say(Topic topic, float chance = 1f, float cooldown = 0f, bool force = false) => SayTopic(topic, chance, cooldown, force, null);

    bool SayTopic(Topic topic, float chance, float cooldown, bool force, string about)
    {
        float now = Time.time;
        if (mutedUntil.TryGetValue(topic, out float mu) && now < mu) return false;
        if (cooldown > 0f && lastTopic.TryGetValue(topic, out float t) && now - t < cooldown) return false;
        if (UnityEngine.Random.value > chance * (force ? 1f : Mathf.Lerp(0.3f, 1.2f, chattiness))) return false;
        if (voice == Voice.Duck && about == "APPLE") about = "MAMA"; // (the duckling's mama)
        string[] lines = Lines(voice, topic, about);
        if (lines == null || lines.Length == 0) return false;
        string line = lines[UnityEngine.Random.Range(0, lines.Length)];
        if (about != null) line = line.Replace("{0}", about);
        if (!Say(line, 0f, force)) return false;
        lastTopic[topic] = now;
        return true;
    }

    /// <summary>A line about this topic that names someone ("HI {0}!"): about = their name. Without a name, those lines are skipped.</summary>
    public bool Say(Topic topic, string about, float chance = 1f, float cooldown = 0f, bool force = false) => SayTopic(topic, chance, cooldown, force, about);

    /// <summary>What it's called in its bubbles and the chat box ("APPLE", "TOMATO", "BEAN", "BURGER", "DINO").</summary>
    public static string NameOf(Voice v) => v == Voice.Tomato ? "TOMATO" : v == Voice.Bean ? "BEAN" : v == Voice.Burger ? "BURGER" : v == Voice.Dino ? "DINO" : v == Voice.Duck ? "DUCK" : v == Voice.Monster ? "MONSTER" : "APPLE";
    /// <summary>Its name (a fused monster: the monster's own name, APPLOSAURUS...).</summary>
    public string Name => voice == Voice.Monster && TryGetComponent(out MonsterLook look) && look.Active ? look.MonsterName : NameOf(voice);

    public void Hush() { if (bubble) bubble.enabled = false; }

    /// <summary>Don't say anything about this topic for a while.</summary>
    public void Suppress(Topic topic, float seconds) => mutedUntil[topic] = Time.time + seconds;
    readonly Dictionary<Topic, float> mutedUntil = new Dictionary<Topic, float>();

    // ------------------------------------------------------------------ what it reacts to

    void OnSplat(Vector2 at, Vector2 n, float speed) { if (speed > 10f) Say(Topic.Splat, 0.4f, 6f); }
    void OnEscaped(long id, Vector2 v) => Say(Topic.Escape, 0.9f, 5f, true);
    void OnWobbled(long id, Vector2 imp) => Say(Topic.Boing, 0.4f, 6f);
    void OnAte(Snack s) { lastAte = Time.time; Say(Topic.Ate, 0.65f, 3f, true); }

    void OnLevelUp(int level, bool newStage)
    {
        if (newStage) Say(Topic.NewStage, 1f, 0f, true);
        else Say(Topic.LevelUp, 0.8f, 0f, true);
    }

    void OnMoment(string what)
    {
        if (what == "found") Say(Topic.Found, 1f, 5f, true);
        else if (what == "top") Say(Topic.Top, 0.6f, 20f);
        else if (what == "peek") Say(Topic.Peek, 0.6f, 20f);
        else if (what == "climb") Say(Topic.Climb, 0.4f, 20f);
        else if (what == "catch" && UnityEngine.Random.value < 0.5f) Say(voice == Voice.Tomato ? "GOT IT!" : voice == Voice.Bean ? "CAUGHT!" : voice == Voice.Burger ? "NICE CATCH, ME" : voice == Voice.Dino ? "CHOMP! GOT IT" : voice == Voice.Duck ? "QUACK! GOT IT" : voice == Voice.Monster ? "CHOMP!! OURS" : "GOTCHA!", 0f, true);
    }

    void OnSnackLanded(Snack s)
    {
        if (brain && brain.Current == GummyBrain.State.Asleep) return;
        if ((s.Position - body.Center).magnitude > 9f) return;
        if (UnityEngine.Random.value < 0.4f && Say(KindLine(s.kind), 0f, false)) { lastTopic[Topic.SnackSpotted] = Time.time; return; }
        Say(Topic.SnackSpotted, 0.7f, 4f);
    }

    string KindLine(Snack.Kind k)
    {
        bool candy = k == Snack.Kind.Candy || k == Snack.Kind.Sweet; // (the candy cloud's sweets too)
        if (k == Snack.Kind.Potion) return voice == Voice.Tomato ? "OOH, SPARKLY!" : voice == Voice.Bean ? "MAGIC BREW!" : voice == Voice.Burger ? "SECRET SAUCE?!" : voice == Voice.Dino ? "MAGIC?! RAWR!" : voice == Voice.Duck ? "SHINY! QUACK!" : voice == Voice.Monster ? "GLOWY SNACK!" : "A POTION?!";
        if (voice == Voice.Tomato) return k == Snack.Kind.Cookie ? "COOKIE! DIBS" : candy ? "CANDYYY" : "SPLASH!";
        if (voice == Voice.Bean) return k == Snack.Kind.Cookie ? "DUNK IT!" : candy ? "SUGAR RUSH!" : "BREW WATER!";
        if (voice == Voice.Burger) return k == Snack.Kind.Cookie ? "DESSERT FIRST" : candy ? "SIDE ORDER!" : "A DRINK! COMBO!";
        if (voice == Voice.Dino) return k == Snack.Kind.Cookie ? "PREY! A COOKIE!" : candy ? "CANDY! RAWR!" : "A DINO DRINK!";
        if (voice == Voice.Duck) return k == Snack.Kind.Cookie ? "BREAD?! NO, COOKIE!" : candy ? "CANDY! QUACK!" : "A PUDDLE! QUACK!";
        if (voice == Voice.Monster) return k == Snack.Kind.Cookie ? "COOKIE! ONE BITE!" : candy ? "ALL THE CANDY!" : "GULP!";
        return k == Snack.Kind.Cookie ? "A COOKIE!" : candy ? "CANDY!" : "WATER! YES";
    }

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("BuddySpeech.Update", t0); }
    void UpdateTimed()
    {
        float now = Time.time;
        bool held = body.Held;
        if (BulletTime.Stopped) { wasHeld = held; UpdateBubble(now); return; } // (time stopped: statues; the chat still gets an answer)
        if (held && !wasHeld) Say(Topic.Grab, 0.45f, 5f);
        if (!held && wasHeld && body.Velocity.magnitude > 9f) Say(Topic.Throw, 0.6f, 5f, true);
        wasHeld = held;

        bool dangling = body.IsStuck && body.GlueNormal.y < -0.5f;
        if (dangling && !wasDangling) Say(Topic.Dangle, 0.7f, 15f);
        wasDangling = dangling;

        if (brain)
        {
            if (brain.Mood != lastMood)
            {
                if (brain.Mood == CuteFace.Expression.Dizzy) Say(Topic.Dizzy, 0.7f, 8f, true);
                else if (brain.Mood == CuteFace.Expression.Happy && now - lastAte > 2f && body.Hovered) Say(Topic.Pet, 0.5f, 12f);
                lastMood = brain.Mood;
            }
            bool annoyed = brain.Annoyed;
            if (annoyed && !wasAnnoyed) Say(Topic.Annoyed, 0.9f, 10f, true);
            wasAnnoyed = annoyed;

            if (brain.Current != lastState)
            {
                if (brain.Current == GummyBrain.State.Asleep) { Say(Topic.Sleep, 0.35f, 30f); Hush(); }
                else if (lastState == GummyBrain.State.Asleep) Say(Topic.Wake, 0.6f, 20f, true);
                lastState = brain.Current;
            }

            // just chatting
            if (now > nextChatter)
            {
                nextChatter = now + UnityEngine.Random.Range(chatterEvery.x, chatterEvery.y);
                bool free = brain.Current == GummyBrain.State.Idle && !held;
                if (free && !BuddyChat.Open && !BandJam.Current && (Grabber.CursorWorld - body.Center).magnitude < 7f) // (not in the middle of a song)
                {
                    // sometimes about you (your name, what you're working on, what you like)
                    string personal = UnityEngine.Random.value < 0.3f ? ChatReplies.PersonalLine(voice) : null;
                    if (personal != null) { if (Say(personal)) lastTopic[Topic.Chatter] = now; }
                    else Say(Topic.Chatter, 0.8f);
                }
            }
        }

        UpdateBubble(now);
    }

    // ------------------------------------------------------------------ the bubble

    void Build()
    {
        holder = new GameObject(name + " Speech");
        var go = new GameObject("Bubble");
        go.transform.SetParent(holder.transform, false);
        bubble = go.AddComponent<SpriteRenderer>();
        bubble.sortingOrder = body.sortingOrder + 100; // above every buddy (it fades instead of hiding behind windows)
        if (body.spriteMaterial) bubble.sharedMaterial = body.spriteMaterial;
        bubble.enabled = false;
    }

    void UpdateBubble(float now)
    {
        if (!bubble || !bubble.enabled) return;
        float age = now - shownAt;
        if (age > hold + 0.25f || (brain && brain.Current == GummyBrain.State.Asleep && age > 1.2f)) { bubble.enabled = false; return; }
        PixelSize();

        // above its head (above the level bar when that's showing), kept on screen
        float s = body.size, lift = hud && hud.Showing ? 16f * fontPx : 0f;
        float w = bubbleW * fontPx, h = bubbleH * fontPx;
        Vector2 p = new Vector2(body.Center.x - w * 0.5f, body.Center.y + s * 0.75f + 2f * fontPx + lift);
        Rect r = ScreenBounds.ScreenRectAt(body.Center); // (its own screen)
        if (p.y + h > r.yMax) p.y = body.Center.y - s * 0.8f - h; // no room above: below it
        p.x = Mathf.Clamp(p.x, r.xMin + fontPx, r.xMax - w - fontPx);
        // a friend talking at the same time: the newer bubble goes on top of the older one
        for (int k = 0; k < s_all.Count; k++)
        {
            BuddySpeech o = s_all[k];
            if (o == this || !o.Talking || o.shownAt > shownAt || (o.shownAt == shownAt && k > s_all.IndexOf(this))) continue;
            Rect a = o.placed;
            if (p.x < a.xMax && p.x + w > a.xMin && p.y < a.yMax && p.y + h > a.yMin) p.y = Mathf.Min(a.yMax + fontPx, r.yMax - h);
        }
        int bounce = age < 0.25f ? Mathf.RoundToInt(3f * Mathf.Sin(age / 0.25f * Mathf.PI)) : 0; // pops up
        p.y += bounce * fontPx;
        placed = new Rect(p.x, p.y, w, h);
        holder.transform.position = Snap(p);
        holder.transform.localScale = new Vector3(fontPx, fontPx, 1f);
        float fade = age > hold ? 1f - (age - hold) / 0.25f : 1f;
        bubble.color = new Color(1f, 1f, 1f, Mathf.Clamp01(fade) * (layer ? layer.HeadSeen : 1f)); // (gone while a window in front covers its head)
    }

    void PixelSize()
    {
        var cam = Camera.main;
        if (!cam || Screen.height <= 0) { screenPx = 0f; return; }
        screenPx = 2f * cam.orthographicSize / Screen.height;
        fontPx = screenPx * Mathf.Max(1, Mathf.RoundToInt(pixelScale1080 * ScreenBounds.MainScreenPixels / 1080f));
    }

    Vector3 Snap(Vector2 p)
    {
        if (screenPx <= 0f) return new Vector3(p.x, p.y, 0f);
        var cam = Camera.main;
        Vector2 o = (Vector2)cam.transform.position - new Vector2(cam.orthographicSize * Screen.width / Screen.height, cam.orthographicSize);
        Vector2 d = p - o;
        return new Vector3(o.x + Mathf.Round(d.x / screenPx) * screenPx, o.y + Mathf.Round(d.y / screenPx) * screenPx, 0f);
    }

    Sprite BubbleSprite(string text)
    {
        if (cache.TryGetValue(text, out Sprite cached) && cached)
        {
            bubbleW = (int)cached.rect.width; bubbleH = (int)cached.rect.height;
            return cached;
        }
        Color32[] px = Bake(text, out int w, out int h);
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point, name = "Bubble" };
        tex.SetPixels32(px);
        tex.Apply(false, true);
        textures.Add(tex);
        var sprite = Sprite.Create(tex, new Rect(0, 0, w, h), Vector2.zero, 1f, 0, SpriteMeshType.FullRect);
        if (cache.Count > 48) { foreach (var sp in cache.Values) if (sp) Destroy(sp); cache.Clear(); foreach (var t in textures) if (t && t != tex) Destroy(t); textures.Clear(); textures.Add(tex); }
        cache[text] = sprite;
        bubbleW = w; bubbleH = h;
        return sprite;
    }

    /// <summary>A white rounded box with a dark outline, a little tail at the bottom, and the text (pixels, y up).</summary>
    public static Color32[] Bake(string text, out int w, out int h)
    {
        const int tail = 3;
        List<string> lines = PixelFont.Wrap(text, 12);
        int textW = 0;
        foreach (var l in lines) textW = Mathf.Max(textW, PixelFont.Measure(l));
        int textH = lines.Count * PixelFont.LineHeight - 2;
        int boxW = textW + 8, boxH = textH + 6;
        w = boxW;
        h = boxH + tail;
        var px = new Color32[w * h];
        var clear = new Color32(0, 0, 0, 0);
        var fill = new Color32(255, 252, 244, 255);
        var ink = new Color32(42, 30, 22, 255);
        int tx = w / 2;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                Color32 c = clear;
                if (y >= tail)
                {
                    int by = y - tail;
                    bool edgeX = x == 0 || x == boxW - 1, edgeY = by == 0 || by == boxH - 1;
                    bool inner = (x == 1 || x == boxW - 2) && (by == 1 || by == boxH - 2);
                    if (edgeX && edgeY) c = clear;                 // rounded corner
                    else if (edgeX || edgeY || inner) c = ink;
                    else c = fill;
                    if (by == 0 && Mathf.Abs(x - tx) <= 1) c = fill; // opening for the tail
                }
                else
                {
                    int hw = y;                                    // 0 at the tip, 2 at the top
                    int dx = Mathf.Abs(x - tx);
                    if (dx < hw) c = fill;
                    else if (dx == hw) c = ink;
                }
                px[y * w + x] = c;
            }
        int top = h - 4; // outline + 2 px padding
        for (int i = 0; i < lines.Count; i++)
        {
            int lw = PixelFont.Measure(lines[i]);
            PixelFont.Draw(px, w, h, lines[i], 4 + (textW - lw) / 2, top - i * PixelFont.LineHeight, ink);
        }
        return px;
    }

    // ------------------------------------------------------------------ lines

    static string[] Lines(Voice v, Topic t, string about)
    {
        var table = v == Voice.Tomato ? TomatoLines : v == Voice.Bean ? BeanLines : v == Voice.Burger ? BurgerLines : v == Voice.Dino ? DinoLines : v == Voice.Duck ? DuckLines : v == Voice.Monster ? MonsterLines : AppleLines;
        if (!table.TryGetValue(t, out string[] l)) return null;
        bool named = false;
        foreach (string line in l) if (line.Contains("{0}")) named = true;
        if (!named || about != null) return l;
        var plain = new List<string>(); // no name to put in: only the lines without one
        foreach (string line in l) if (!line.Contains("{0}")) plain.Add(line);
        return plain.ToArray();
    }

    static readonly Dictionary<Topic, string[]> AppleLines = new Dictionary<Topic, string[]>
    {
        { Topic.Grab, new[] { "HEY!", "OOH, UP!", "WHEE", "CAREFUL!", "NOT AGAIN" } },
        { Topic.Throw, new[] { "WHEEEE!", "AAAAH!", "YAAAY", "I CAN FLY" } },
        { Topic.Splat, new[] { "SPLAT", "OUCH", "STICKY...", "I'M OK!" } },
        { Topic.Dizzy, new[] { "@_@", "WOOZY...", "STARS..." } },
        { Topic.Annoyed, new[] { "STOP POKING", "RUDE!", "HMPH!", "HANDS OFF" } },
        { Topic.Pet, new[] { "\u2665", "HEHE", "MORE PLS", "PURR" } },
        { Topic.SnackSpotted, new[] { "SNACK!", "FOOD!", "OOH!" } },
        { Topic.Ate, new[] { "NOM", "YUM!", "CRUNCH", "MORE?", "DELISH" } },
        { Topic.LevelUp, new[] { "LEVEL UP!", "STRONGER!", "YAY!" } },
        { Topic.NewStage, new[] { "I GREW!", "LOOK AT ME!", "NEW ME!" } },
        { Topic.Sleep, new[] { "NAP TIME", "SO SLEEPY" } },
        { Topic.Wake, new[] { "HUH?", "I'M UP!", "5 MORE MIN" } },
        { Topic.Climb, new[] { "UP UP UP", "CLIMB TIME" } },
        { Topic.Top, new[] { "GREAT VIEW", "HELLO UP HERE" } },
        { Topic.Dangle, new[] { "HANGING OUT", "HELP?", "WHEE" } },
        { Topic.Peek, new[] { "PEEKABOO", "CAN'T SEE ME" } },
        { Topic.Found, new[] { "YOU FOUND ME!", "AW MAN" } },
        { Topic.Boing, new[] { "BOING!", "OOPS" } },
        { Topic.Escape, new[] { "PHEW!", "HEY, WINDOW!", "SQUISHED" } },
        { Topic.Chatter, new[] { "HI!", "WHAT R U CODING?", "BREAK TIME?", "I'M BORED", "DRINK WATER", "NICE CODE", "CTRL+C = SNACKS", "PET ME?" } },
        { Topic.Hi, new[] { "HI {0}!", "HEY BUDDY", "YO!" } },
        { Topic.Mine, new[] { "MINE!", "TOO SLOW" } },
        { Topic.Stolen, new[] { "HEY!!", "THAT WAS MINE", "NO FAIR" } },
        { Topic.Innocent, new[] { "...", "LA LA LA", "NOTHING!", "HI :)" } },
        { Topic.Born, new[] { "OH! A SEED!" } },
    };

    static readonly Dictionary<Topic, string[]> TomatoLines = new Dictionary<Topic, string[]>
    {
        { Topic.Grab, new[] { "YIKES", "LET GO!", "OI!", "WHEEE" } },
        { Topic.Throw, new[] { "YAHOO!", "AGAIN!", "WOOO" } },
        { Topic.Splat, new[] { "SPLOT", "SQUISH", "HAHA" } },
        { Topic.Dizzy, new[] { "@_@", "SPINNY" } },
        { Topic.Annoyed, new[] { "GRR", "QUIT IT", "HMPH" } },
        { Topic.Pet, new[] { "\u2665", "HEH", "OK FINE \u2665" } },
        { Topic.SnackSpotted, new[] { "MINE!", "SNACK!!", "DIBS!" } },
        { Topic.Ate, new[] { "NOM NOM", "HEHEHE", "GONE" } },
        { Topic.LevelUp, new[] { "LEVEL UP!", "RIPER!" } },
        { Topic.NewStage, new[] { "I'M RIPE!", "REDDER!" } },
        { Topic.Sleep, new[] { "ZZZ", "NIGHT" } },
        { Topic.Wake, new[] { "WHA?", "UP!" } },
        { Topic.Climb, new[] { "RACE YA UP", "HUP HUP" } },
        { Topic.Top, new[] { "KING OF THE WINDOW" } },
        { Topic.Dangle, new[] { "LOOK MA", "WHEE" } },
        { Topic.Peek, new[] { "BOO!", "PEEK" } },
        { Topic.Found, new[] { "NO FAIR!" } },
        { Topic.Boing, new[] { "BOING!", "AGAIN!" } },
        { Topic.Escape, new[] { "EEK!", "PHEW" } },
        { Topic.Chatter, new[] { "HI!!", "BORED", "SNACKS?", "WHERE'S APPLE?", "COPY SOMETHING", "TAG!" } },
        { Topic.Hi, new[] { "HI {0}!", "YO {0}", "SUP" } },
        { Topic.Mine, new[] { "HEHE MINE", "TOO SLOW!" } },
        { Topic.Stolen, new[] { "HEY!", "RUDE!" } },
        { Topic.Innocent, new[] { "...", "WHISTLE~", "NOTHING!" } },
        { Topic.Born, new[] { "HI!!", "WHERE AM I?" } },
    };

    // the coffee bean: jittery, a bit of a workaholic, loves a pun, secretly worries you skip your breaks
    static readonly Dictionary<Topic, string[]> BeanLines = new Dictionary<Topic, string[]>
    {
        { Topic.Grab, new[] { "WHOA WHOA", "CAREFUL, I SPILL", "UP WE BREW" } },
        { Topic.Throw, new[] { "ESPRESSO!!", "WHEEEE", "FULL ROAST!" } },
        { Topic.Splat, new[] { "GROUNDED", "OOF", "STILL WIRED" } },
        { Topic.Dizzy, new[] { "@_@", "DECAF ME..." } },
        { Topic.Annoyed, new[] { "I'M BITTER NOW", "HEY!", "NOT COOL" } },
        { Topic.Pet, new[] { "\u2665", "PURRRR", "BEAN HAPPY" } },
        { Topic.SnackSpotted, new[] { "SNACK BREAK!", "FUEL!", "OOH!" } },
        { Topic.Ate, new[] { "BUZZING!", "NOM", "MORE FUEL" } },
        { Topic.LevelUp, new[] { "LEVEL UP!", "STRONGER BREW" } },
        { Topic.NewStage, new[] { "ROASTED!", "DARKER ROAST!", "SMELL ME!" } },
        { Topic.Sleep, new[] { "DECAF MODE", "ZZZ..." } },
        { Topic.Wake, new[] { "I'M UP! I'M UP!", "WIRED!" } },
        { Topic.Climb, new[] { "CLIMB! CLIMB!", "FASTER!" } },
        { Topic.Top, new[] { "TOP OF THE MUG!" } },
        { Topic.Dangle, new[] { "JUST HANGING", "DRIP DRIP" } },
        { Topic.Peek, new[] { "PEEK!", "BEAN THERE" } },
        { Topic.Found, new[] { "BEAN SPOTTED", "AW BEANS" } },
        { Topic.Boing, new[] { "BOING!", "SORRY!" } },
        { Topic.Escape, new[] { "PHEW!", "TIGHT SQUEEZE" } },
        { Topic.Chatter, new[] { "WHAT'S BREWING?", "SHIP IT!", "FOCUS MODE", "DRINK WATER TOO", "BEAN THERE", "DEADLINE? LOVE IT", "STRETCH!", "SO PRODUCTIVE" } },
        { Topic.Hi, new[] { "HEY {0}!", "MORNING {0}!", "O/" } },
        { Topic.Mine, new[] { "FASTEST BEAN", "MINE! ZOOM" } },
        { Topic.Stolen, new[] { "HEY! MY FUEL", "RUDE!" } },
        { Topic.Innocent, new[] { "...", "JUST BREWING", "NOTHING!" } },
        { Topic.Born, new[] { "HI!! COFFEE?", "WHERE'S THE MUG?" } },
    };

    // the burger: big, slow, laid-back, a foodie who loves a cheesy pun and a long nap; gentle with the little ones
    static readonly Dictionary<Topic, string[]> BurgerLines = new Dictionary<Topic, string[]>
    {
        { Topic.Grab, new[] { "OOF, I'M HEAVY", "MIND THE CHEESE", "EASY, CHEF" } },
        { Topic.Throw, new[] { "FLYING BURGER!", "WHOOOA", "ORDER UP!" } },
        { Topic.Splat, new[] { "SPLAT. STILL TASTY", "OOF", "SAUCE EVERYWHERE" } },
        { Topic.Dizzy, new[] { "@_@", "SCRAMBLED..." } },
        { Topic.Annoyed, new[] { "HEY, I'M GRILLED", "NOT THE BUNS", "HMPH" } },
        { Topic.Pet, new[] { "\u2665", "MMM, WARM", "EXTRA CHEESE \u2665" } },
        { Topic.SnackSpotted, new[] { "LUNCH!", "SNACK O'CLOCK", "OOH, FOOD" } },
        { Topic.Ate, new[] { "MMMM", "CHEF'S KISS", "MORE PLS", "BURP. SORRY" } },
        { Topic.LevelUp, new[] { "LEVEL UP!", "UPSIZED!" } },
        { Topic.NewStage, new[] { "BIGGER BUN!", "EXTRA LAYER!", "DOUBLE TIME!" } },
        { Topic.Sleep, new[] { "FOOD COMA", "NAP TIME...", "ZZZ" } },
        { Topic.Wake, new[] { "IS IT LUNCH?", "HUH? FOOD?" } },
        { Topic.Climb, new[] { "HEAVE... HO", "THIS IS CARDIO" } },
        { Topic.Top, new[] { "TOP BUN!", "NICE VIEW" } },
        { Topic.Dangle, new[] { "HANG ON, CHEESE", "WHOA" } },
        { Topic.Peek, new[] { "PEEK-A-BUN", "PEEK" } },
        { Topic.Found, new[] { "BUN DEAL", "YOU FOUND ME" } },
        { Topic.Boing, new[] { "BOING!", "BOUNCY BUN" } },
        { Topic.Escape, new[] { "PHEW", "SQUASHED PATTY" } },
        { Topic.Chatter, new[] { "LUNCH YET?", "HUNGRY?", "TAKE A BREAK", "RELAX, CHEF", "WELL DONE!", "SNACK TIME?", "BUN-BELIEVABLE", "STRETCH YOUR LEGS" } },
        { Topic.Hi, new[] { "HEY {0}!", "SUP {0}", "HI LITTLE {0}" } },
        { Topic.Mine, new[] { "BIG BITE!", "MINE, SORRY" } },
        { Topic.Stolen, new[] { "HEY! MY LUNCH", "RUDE" } },
        { Topic.Innocent, new[] { "...", "JUST CHILLING", "NOTHING!" } },
        { Topic.Born, new[] { "HI!! HUNGRY?", "WHERE'S THE KETCHUP?" } },
    };

    // the duckling: tiny, fluffy, sure the apple is its mama; quacks a lot, follows her everywhere, thinks everything is bread
    static readonly Dictionary<Topic, string[]> DuckLines = new Dictionary<Topic, string[]>
    {
        { Topic.Grab, new[] { "QUACK? UP?", "PUT ME DOWN! QUACK", "MAMA! HELP!" } },
        { Topic.Throw, new[] { "I'M FLYING! QUACK!", "WHEEE-QUACK!", "LOOK MAMA!" } },
        { Topic.Splat, new[] { "QUACK... OW", "MEANT TO DO THAT", "SQUISHY LANDING" } },
        { Topic.Dizzy, new[] { "@_@", "QUAAACK..." } },
        { Topic.Annoyed, new[] { "QUACK! STOP IT", "I'M TELLING MAMA!", "GRUMPY DUCK" } },
        { Topic.Pet, new[] { "\u2665", "QUACK \u2665", "SOFT, RIGHT?" } },
        { Topic.SnackSpotted, new[] { "BREAD?!", "SNACK! QUACK!", "FOOD! QUACK!" } },
        { Topic.Ate, new[] { "NOM QUACK", "MORE BREAD!", "YUMMY!", "QUACK-ALICIOUS" } },
        { Topic.LevelUp, new[] { "LEVEL UP! QUACK!", "LOOK MAMA! LEVEL UP!" } },
        { Topic.NewStage, new[] { "LOOK MAMA, BIGGER!", "FLUFFY POWER!", "ALMOST A SWAN!" } },
        { Topic.Sleep, new[] { "DUCK NAP...", "ZZZ... QUACK... ZZZ" } },
        { Topic.Wake, new[] { "QUACK! I'M UP", "MAMA? MORNING!" } },
        { Topic.Climb, new[] { "WADDLE UP!", "HUP! QUACK!" } },
        { Topic.Top, new[] { "LOOK MAMA! UP HERE!", "TOP DUCK!" } },
        { Topic.Dangle, new[] { "HELP! NO HANDS!", "QUACK! HELP!" } },
        { Topic.Peek, new[] { "PEEKABOO! QUACK", "PEEK-A-DUCK" } },
        { Topic.Found, new[] { "AW, QUACK", "YOU FOUND ME!" } },
        { Topic.Boing, new[] { "BOING-QUACK!", "BOUNCY DUCK!" } },
        { Topic.Escape, new[] { "PHEW. QUACK", "SAFE!" } },
        { Topic.Chatter, new[] { "QUACK!", "MAMA IS THE BEST", "WADDLE WADDLE", "I CAN SWIM. PROBABLY", "BREAD?", "QUACK QUACK", "WHERE MAMA GOES, I GO", "IS THAT A POND?" } },
        { Topic.Hi, new[] { "QUACK {0}!", "HI {0}! QUACK!", "{0}!! QUACK" } },
        { Topic.Mine, new[] { "MY SNACK! QUACK", "MINE MINE MINE" } },
        { Topic.Stolen, new[] { "HEY! QUACK!", "THAT WAS MINE!" } },
        { Topic.Innocent, new[] { "...QUACK", "JUST A LITTLE DUCK", "NOTHING! QUACK" } },
        { Topic.Born, new[] { "QUACK?", "MAMA?" } },
    };

    // the little dino: a happy, bouncy toon T-rex who's sure it's terrifying; RAWRs a lot, chomps everything, tiny arms
    static readonly Dictionary<Topic, string[]> DinoLines = new Dictionary<Topic, string[]>
    {
        { Topic.Grab, new[] { "RAWR? UP?", "PUT ME DOWN! RAWR", "MY ARMS CAN'T HOLD ON" } },
        { Topic.Throw, new[] { "I'M A PTERODACTYL!", "RAWRRR!", "WHEEE!" } },
        { Topic.Splat, new[] { "OOF. STILL FIERCE", "MEANT TO DO THAT", "RAWR... OW" } },
        { Topic.Dizzy, new[] { "@_@", "EXTINCT..." } },
        { Topic.Annoyed, new[] { "GRRR!", "I BITE! (NOT REALLY)", "RAWR! STOP IT" } },
        { Topic.Pet, new[] { "\u2665", "HEHE \u2665", "PURR... I MEAN RAWR" } },
        { Topic.SnackSpotted, new[] { "PREY SPOTTED!", "CHOMP TIME!", "FOOD!!" } },
        { Topic.Ate, new[] { "CHOMP!", "NOM NOM RAWR", "MORE!", "TEETH WORK!" } },
        { Topic.LevelUp, new[] { "LEVEL UP! RAWR!", "STRONGER!" } },
        { Topic.NewStage, new[] { "BIGGER! SCARIER!", "LOOK HOW BIG!", "ALMOST A T-REX!" } },
        { Topic.Sleep, new[] { "DINO NAP...", "ZZZ... RAWR... ZZZ" } },
        { Topic.Wake, new[] { "RAWR! I'M UP", "WAS I EXTINCT?" } },
        { Topic.Climb, new[] { "TINY ARMS! HARD!", "HUP! HUP!" } },
        { Topic.Top, new[] { "KING OF THE HILL!", "RAWR FROM UP HERE!" } },
        { Topic.Dangle, new[] { "HELP! TINY ARMS!", "CAN'T REACH!" } },
        { Topic.Peek, new[] { "BOO!", "PEEK... RAWR" } },
        { Topic.Found, new[] { "AW, YOU FOUND ME", "RAWR! TOO GOOD" } },
        { Topic.Boing, new[] { "BOING-A-SAURUS!", "BOING!" } },
        { Topic.Escape, new[] { "PHEW", "SURVIVED!" } },
        { Topic.Chatter, new[] { "RAWR!", "I'M SO FIERCE", "TINY ARMS, BIG HUGS", "WANNA SEE MY TEETH?", "STOMP STOMP", "65 MILLION YEARS OLD", "SCARED? HEHE", "BREAK TIME! RAWR" } },
        { Topic.Hi, new[] { "RAWR {0}!", "HI {0}! RAWR!", "{0}! MY FRIEND!" } },
        { Topic.Mine, new[] { "MY PREY!", "CHOMP! MINE" } },
        { Topic.Stolen, new[] { "HEY! MY PREY!", "RAWR!!" } },
        { Topic.Innocent, new[] { "...", "JUST A LITTLE DINO", "NOTHING! RAWR" } },
        { Topic.Born, new[] { "RAWR!!", "I'M A DINO! RAWR!" } },
    };

    // a fused monster (BuddyMonster): goofy-scary, talks about itself as "WE", a deep voice
    static readonly Dictionary<Topic, string[]> MonsterLines = new Dictionary<Topic, string[]>
    {
        { Topic.Grab, new[] { "GRRR! WE'RE HEAVY", "NO LIFTING THE MONSTER", "PULL ALL YOU LIKE" } },
        { Topic.Throw, new[] { "WHEEE-RAWR!", "MONSTER AIRLINES!" } },
        { Topic.Splat, new[] { "SPLORCH.", "THUD. RAWR", "WE'RE FINE" } },
        { Topic.Dizzy, new[] { "@_@ @_@ @_@", "SO MANY STARS..." } },
        { Topic.Annoyed, new[] { "GRRRRR!", "STOP POKING! WE BITE!", "HMPH TIMES SIX" } },
        { Topic.Pet, new[] { "\u2665... RAWR", "WE LIKE THAT", "PURR-RAWR" } },
        { Topic.SnackSpotted, new[] { "FOOOOD!", "SNACK! OURS!", "CHOMP TIME" } },
        { Topic.Ate, new[] { "CHOMP!", "GULP.", "ONE BITE!", "MORE! MORE!" } },
        { Topic.LevelUp, new[] { "RAAAWR!" } },
        { Topic.NewStage, new[] { "RAAAWR!" } },
        { Topic.Sleep, new[] { "MONSTER NAP...", "ZZZ... RAWR... ZZZ" } },
        { Topic.Wake, new[] { "WHO WOKE US?!", "RAWR?" } },
        { Topic.Climb, new[] { "HUP! HEAVY!", "UP WE GO" } },
        { Topic.Top, new[] { "FEAR US FROM UP HERE", "RAWR FROM THE TOP" } },
        { Topic.Dangle, new[] { "HELP? RAWR?", "TOO HEAVY TO HANG" } },
        { Topic.Peek, new[] { "BOO!", "PEEK-A-RAWR" } },
        { Topic.Found, new[] { "WE WERE NOT HIDING" } },
        { Topic.Boing, new[] { "BOING-RAWR!", "BOING TIMES SIX" } },
        { Topic.Escape, new[] { "SQUISH... RAWR", "FREE!" } },
        { Topic.Chatter, new[] { "RAAAWR!", "WE ARE ONE", "FEAR US! ...PLEASE?", "WHERE DID EVERYONE GO?", "SO MANY EYES. SO MUCH BLINKING", "WE ARE NOT HUNGRY. (LIE)", "HUG? NO? RAWR", "ALL OF US. ONE GUMMY" } },
        { Topic.Hi, new[] { "RAAAWR, {0}!", "HELLO TINY {0}", "{0}! COME HERE!" } },
        { Topic.Mine, new[] { "OURS!", "CHOMP! OURS" } },
        { Topic.Stolen, new[] { "HEY! OUR SNACK!", "GRRRR!" } },
        { Topic.Innocent, new[] { "...", "WE ARE A NORMAL BUDDY", "NOTHING TO SEE" } },
        { Topic.Born, new[] { "RAAAAAWR!" } },
    };
}
