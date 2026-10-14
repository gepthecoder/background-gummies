using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The desktop band. Every window top is a note: the wider the window, the lower (C major pentatonic, so whatever they
/// land on goes together, and every desktop layout has its own tune). A buddy landing on a window plays that window's
/// note on its own instrument (apple: marimba, tomato: ukulele, coffee bean: a spoon on a cup, burger: bongo, dino:
/// bass, duck: quack, monster: tuba): loud when you throw or drop it, softly when it hops over onto another window by
/// itself (hopping about on the same one it keeps quiet, apart from a note now and then). Thrown onto the taskbar
/// it's a drum. A note pops up above it and the window's top edge lights up like a key. The notes you play by throwing
/// them are remembered: the next jam plays your tune. Now and then while you sit still for a bit, or when you ask in the
/// chat ("jam", "music", "play a song"...), they all jam together (BandJam). "Quiet" in the chat mutes the band.
/// </summary>
[DefaultExecutionOrder(-9)] // after the brain has set its pose, before the body simulates: the bob on the beat wins
[RequireComponent(typeof(GummyBody))]
[DisallowMultipleComponent]
public class BuddyBand : MonoBehaviour
{
    public enum Instrument { Marimba, Uke, Cup, Bongo, Bass, Quack, Tuba, Maraca, Kick, Snare, Hat }

    [Header("Instruments (WhipIt > Setup assigns them)")]
    public AudioClip marimba;
    public AudioClip uke, cup, bongo, bass, quack, tuba, maraca, kick, snare, hat;
    [Header("Window notes")]
    public bool windowNotes = true;
    [Tooltip("Thrown or dropped onto a window (loud).")]
    [Range(0f, 1f)] public float volume = 0.6f;
    [Tooltip("Its own hops (soft).")]
    [Range(0f, 1f)] public float hopVolume = 0.13f;
    [Tooltip("Pan left/right with where it is on screen.")]
    [Range(0f, 1f)] public float stereoWidth = 0.6f;
    [Header("Jams")]
    public bool jams = true;
    [Range(0f, 1f)] public float jamVolume = 0.5f;
    [Tooltip("Minutes between jams of their own (while you sit still).")]
    public Vector2 jamEveryMinutes = new Vector2(25f, 50f);
    [Tooltip("They only start one of their own once you've not touched mouse or keyboard for this long (seconds).")]
    public float jamWhenIdle = 20f;
    [Tooltip("Sprite material (WhipIt > Setup assigns it).")]
    public Material material;

    /// <summary>The band is muted (chat "quiet"; "music on" / asking for a jam unmutes it). Remembered.</summary>
    public static bool Muted
    {
        get { if (!s_mutedRead) { s_muted = PlayerPrefs.GetInt(MutedKey, 0) == 1; s_mutedRead = true; } return s_muted; }
        set { s_muted = value; s_mutedRead = true; PlayerPrefs.SetInt(MutedKey, value ? 1 : 0); PlayerPrefs.Save(); if (value && BandJam.Current) BandJam.Current.Stop(); }
    }
    /// <summary>Your tune: the last notes you played by throwing buddies onto windows (semitones from C).</summary>
    public static IReadOnlyList<int> Tune => s_tune;
    /// <summary>Goes up with every note you add to your tune.</summary>
    public static int TuneVersion { get; private set; }
    /// <summary>Your tune is fresh enough for a jam to play it (4+ notes, the last one in the last 15 minutes).</summary>
    public static bool TuneReady => s_tune.Count >= 4 && Time.time - s_tuneAt < 900f;
    /// <summary>Every window note / drum played (sims): who, instrument, MIDI note (0 = drum), volume, thrown.</summary>
    public static event System.Action<GummyBody, Instrument, int, float, bool> Noted;

    /// <summary>Its own instrument.</summary>
    public Instrument Mine => ByVoice[(int)(speech ? speech.voice : BuddySpeech.Voice.Apple)];
    public GummyBody Body => body;
    public int WindowNotes { get; private set; }
    public int Drums { get; private set; }

    // C major pentatonic from the G below to the E above: the wider the window, the lower
    public static readonly int[] Steps = { -5, -3, 0, 2, 4, 7, 9, 12, 14, 16 };
    static readonly Instrument[] ByVoice = { Instrument.Marimba, Instrument.Uke, Instrument.Cup, Instrument.Bongo, Instrument.Bass, Instrument.Quack, Instrument.Tuba };
    // the note each clip is on (MIDI; 0 = a drum: no note); how loud each one is next to the others
    static readonly int[] BaseNote = { 72, 60, 84, 60, 36, 72, 36, 0, 0, 0, 0 };
    static readonly float[] Gain = { 1f, 1f, 0.85f, 0.9f, 1f, 0.9f, 1f, 0.8f, 1f, 0.85f, 0.7f };
    const string MutedKey = "WhipIt.BandMuted";
    const float ThrowWindow = 2.5f;
    const int GlyphCount = 6;

    static AudioClip[] s_kit;
    static bool s_muted, s_mutedRead;
    static readonly List<int> s_tune = new List<int>();
    static float s_tuneAt = -1e6f;
    static float s_nextAuto = -1f;
    static int s_autoFrame = -1;
    static Sprite s_note, s_notes, s_bit;

    GummyBody body;
    BuddySpeech speech;
    GummyBrain brain;
    BuddyLayer layer;
    AudioSource[] shots;
    int nextShot;
    bool wasHeld;
    float lastRelease = -10f, lastNote = -10f, bobAt = -10f, bobAmount, lastHopNote = -100f;
    long onSurface = -1;
    bool bobbing, learned;

    // notes floating up, the window top lighting up
    GameObject fxRoot;
    readonly SpriteRenderer[] glyphs = new SpriteRenderer[GlyphCount];
    readonly float[] glyphAge = new float[GlyphCount];
    readonly int[] glyphLayer = new int[GlyphCount];
    readonly Vector2[] glyphFrom = new Vector2[GlyphCount];
    int nextGlyph;
    SpriteRenderer key, keyGlow;
    float keyAge = 99f;

    void Awake()
    {
        body = GetComponent<GummyBody>();
        speech = GetComponent<BuddySpeech>();
        brain = GetComponent<GummyBrain>();
        layer = GetComponent<BuddyLayer>();
        var mine = new[] { marimba, uke, cup, bongo, bass, quack, tuba, maraca, kick, snare, hat };
        foreach (var c in mine) if (c) { s_kit = mine; break; } // (the monster has none of its own: it uses the kit)
        shots = new AudioSource[3];
        for (int i = 0; i < shots.Length; i++)
        {
            var s = gameObject.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.spatialBlend = 0f;
            shots[i] = s;
        }
    }

    void OnEnable()
    {
        body.Bump += OnHit;
        body.Splat += OnHit;
    }

    void OnDisable()
    {
        body.Bump -= OnHit;
        body.Splat -= OnHit;
        if (bobbing && body) { body.Pose = Vector2.one; bobbing = false; }
        if (fxRoot) fxRoot.SetActive(false);
    }

    void OnDestroy()
    {
        if (fxRoot) Destroy(fxRoot);
    }

    // ------------------------------------------------------------------ the sounds

    /// <summary>The clip for an instrument (null = not set up).</summary>
    public static AudioClip ClipFor(Instrument i) => s_kit != null ? s_kit[(int)i] : null;
    /// <summary>The pitch to play an instrument's clip at for this MIDI note (drums: 1).</summary>
    public static float PitchFor(Instrument i, int note) => BaseNote[(int)i] == 0 || note <= 0 ? 1f : Mathf.Pow(2f, (note - BaseNote[(int)i]) / 12f);
    /// <summary>The MIDI note an instrument's clip is on (0 = a drum).</summary>
    public static int BaseOf(Instrument i) => BaseNote[(int)i];
    public static float GainOf(Instrument i) => Gain[(int)i];
    /// <summary>A buddy's instrument by its voice.</summary>
    public static Instrument Of(BuddySpeech.Voice v) => ByVoice[(int)v];
    /// <summary>The note of a window this wide (semitones from C).</summary>
    public static int StepFor(float width)
    {
        float f = width / Mathf.Max(ScreenBounds.MainRect.width, 0.01f); // (more than one screen: the main one's width)
        int i = Mathf.RoundToInt(Mathf.Lerp(Steps.Length - 1, 0, Mathf.InverseLerp(0.15f, 1f, f)));
        return Steps[Mathf.Clamp(i, 0, Steps.Length - 1)];
    }

    /// <summary>Pan for a spot on screen (-1 left .. 1 right, times the width).</summary>
    public static float PanAt(float x, float width)
    {
        Rect r = ScreenBounds.WorldRect;
        return Mathf.Clamp((x - r.center.x) / Mathf.Max(r.width * 0.5f, 0.01f), -1f, 1f) * width;
    }

    void Play(Instrument inst, float pitch, float vol, Vector2 at)
    {
        AudioClip clip = ClipFor(inst);
        if (!clip || vol <= 0f) return;
        AudioSource src = shots[nextShot];
        nextShot = (nextShot + 1) % shots.Length;
        src.pitch = pitch;
        src.panStereo = PanAt(at.x, stereoWidth);
        src.PlayOneShot(clip, vol * Gain[(int)inst]);
    }

    // landed on something
    void OnHit(Vector2 at, Vector2 normal, float speed)
    {
        if (normal.y < 0.7f) return;                           // (tops only: not the sides)
        long surf = body.LastHitSurface;
        bool arrived = surf != onSurface;                      // (got here from somewhere else)
        onSurface = surf;
        if (Muted || !windowNotes) return;
        float now = Time.time;
        if (now - lastNote < 0.18f) return;                   // (one note per landing, bounces are new notes)
        if (DesktopPlatforms.IsIcon(surf)) return;             // (icons go "tick": WindowWobble)
        bool thrown = !body.Held && now - lastRelease < ThrowWindow;
        if (BandJam.Current && !thrown) return;                // (in a jam only what you throw plays along)
        float loud = Mathf.InverseLerp(3f, 14f, speed);
        if (surf != 0 && WidthOf(surf, out float w))
        {
            if (!thrown)
            {
                // its own hops: a note when it gets onto another window (and now and then on the same one)
                if (!arrived && now - lastHopNote < 20f) return;
                lastHopNote = now;
            }
            int step = StepFor(w);
            Instrument inst = Mine;
            int note = BaseNote[(int)inst] + step;
            float vol = thrown ? volume * Mathf.Lerp(0.5f, 1f, loud) : hopVolume * Mathf.Lerp(0.6f, 1f, Mathf.InverseLerp(1f, 6f, speed));
            lastNote = now;
            Play(inst, PitchFor(inst, note), vol, at);
            Glyph(thrown ? 1.25f : 0.85f);
            Key(surf, at, thrown ? 1f : 0.55f);
            Bob(thrown ? 1f : 0.5f);
            WindowNotes++;
            if (thrown && !learned) { learned = true; Learn(step); } // (the landing, not the bounces)
            Noted?.Invoke(body, inst, note, vol, thrown);
            return;
        }
        // thrown onto the taskbar: a drum (bigger = deeper)
        if (thrown && surf == 0 && at.y <= ScreenBounds.FloorAt(at) + 0.1f)
        {
            lastNote = now;
            float vol = volume * Mathf.Lerp(0.5f, 1f, loud);
            Play(Instrument.Kick, Mathf.Lerp(1.15f, 0.8f, Mathf.InverseLerp(0.3f, 1.2f, body.size)), vol, at);
            Drums++;
            Noted?.Invoke(body, Instrument.Kick, 0, vol, true);
        }
    }

    static bool WidthOf(long id, out float w)
    {
        var all = DesktopPlatforms.Windows;
        for (int i = 0; i < all.Count; i++) if (all[i].id == id) { w = all[i].xMax - all[i].xMin; return true; }
        w = 0f;
        return false;
    }

    static void Learn(int step)
    {
        s_tune.Add(step);
        if (s_tune.Count > 8) s_tune.RemoveAt(0);
        s_tuneAt = Time.time;
        TuneVersion++;
    }

    /// <summary>Forget your tune (sims).</summary>
    public static void ForgetTune() { s_tune.Clear(); s_tuneAt = -1e6f; }

    // ------------------------------------------------------------------ every frame

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("BuddyBand.Update", t0); }
    void UpdateTimed()
    {
        float now = Time.time, dt = Time.deltaTime;
        bool held = body.Held;
        if (!held && wasHeld) { lastRelease = now; learned = false; }
        wasHeld = held;

        // bobbing to its own notes
        float k = bobAmount * Mathf.Exp(-(now - bobAt) / 0.09f);
        if (k > 0.01f && body.Grounded && !held)
        {
            body.Pose = new Vector2(1f + 0.08f * k, 1f - 0.09f * k);
            bobbing = true;
        }
        else if (bobbing) { body.Pose = Vector2.one; bobbing = false; }

        UpdateFx(dt);
        AutoJam(now);
    }

    // a jam of their own now and then (one buddy runs this for everyone)
    void AutoJam(float now)
    {
        if (Time.frameCount == s_autoFrame) return;
        s_autoFrame = Time.frameCount;
        if (s_nextAuto < 0f) { s_nextAuto = now + Random.Range(8f, 15f) * 60f; return; } // (the first one fairly soon)
        if (now < s_nextAuto) return;
        if (!CanAutoJam()) { s_nextAuto = now + 60f; return; }
        s_nextAuto = now + Random.Range(jamEveryMinutes.x, Mathf.Max(jamEveryMinutes.x, jamEveryMinutes.y)) * 60f;
        BandJam.Begin(null, false);
    }

    bool CanAutoJam()
    {
        if (!jams || Muted || BandJam.Current || LevelUpParty.Current || BuddyChat.Open || BuddyHideSeek.Playing || BuddyTower.Current || Bedtime.Sleeping) return false;
        if (Rain.Current && Rain.Current.Raining) return false;
        if (Lava.Current && Lava.Current.On) return false;
        if (CaughtInTheAct.Partying) return false;
        float idle = WorkClock.IdleProbe != null ? WorkClock.IdleProbe() : DesktopInput.IdleSeconds;
        if (idle < jamWhenIdle) return false; // (you're busy: not now)
        return BandJam.Ready(false) >= 2;
    }

    /// <summary>When its own next jam may start (sims).</summary>
    public static float NextAutoJam { get => s_nextAuto; set => s_nextAuto = value; }

    // ------------------------------------------------------------------ what you see

    /// <summary>A note seen played (a jam): bob, a note floating up, the window under it lights up.</summary>
    public void Show(float amount, bool glyph, bool keyToo)
    {
        Bob(amount);
        if (glyph) Glyph(amount);
        if (keyToo && body.OnPlatform && body.SupportWindow != 0) Key(body.SupportWindow, new Vector2(body.Center.x, body.Extent.yMin), amount * 0.8f);
    }

    void Bob(float amount)
    {
        bobAt = Time.time;
        bobAmount = Mathf.Clamp01(amount);
    }

    void Glyph(float amount)
    {
        if (!Fx()) return;
        int i = nextGlyph;
        nextGlyph = (nextGlyph + 1) % GlyphCount;
        var sr = glyphs[i];
        sr.sprite = Random.value < 0.5f ? s_note : s_notes;
        if (material) SpriteMats.Fit(sr, material);
        glyphAge[i] = 0f;
        glyphLayer[i] = layer ? layer.Layer : Occlusion.Front;
        float s = body.size;
        glyphFrom[i] = new Vector2(body.Center.x + Random.Range(-0.35f, 0.35f) * s, body.Extent.yMax + 0.05f);
        sr.transform.localScale = Vector3.one * (0.3f * Mathf.Sqrt(s / 0.6f) * Mathf.Lerp(0.8f, 1.15f, Mathf.Clamp01(amount - 0.3f)));
        // where it starts, and clear: UpdateFx fades it in (a new one would otherwise show for a frame, white, wherever it
        // was made: the middle of the screen)
        sr.transform.position = new Vector3(glyphFrom[i].x, glyphFrom[i].y, 0f);
        sr.color = new Color(1f, 1f, 1f, 0f);
        sr.enabled = true;
    }

    void Key(long surf, Vector2 at, float amount)
    {
        if (!Fx()) return;
        var all = DesktopPlatforms.Current;
        for (int i = 0; i < all.Count; i++)
        {
            Platform p = all[i];
            if (p.id != surf || at.x < p.x0 - 0.05f || at.x > p.x1 + 0.05f || Mathf.Abs(at.y - p.y) > 0.15f) continue;
            key.transform.position = new Vector3(0.5f * (p.x0 + p.x1), p.y, 0f);
            key.transform.localScale = new Vector3(p.x1 - p.x0, 0.05f, 1f);
            keyGlow.transform.position = new Vector3(0.5f * (p.x0 + p.x1), p.y + 0.03f, 0f);
            keyGlow.transform.localScale = new Vector3(p.x1 - p.x0, 0.16f, 1f);
            keyAge = 0f;
            keyAmount = Mathf.Clamp01(amount);
            key.enabled = keyGlow.enabled = true;
            return;
        }
    }
    float keyAmount = 1f;

    void UpdateFx(float dt)
    {
        if (!fxRoot) return;
        Color c = body.color;
        Color bright = Color.Lerp(c, Color.white, 0.45f);
        for (int i = 0; i < GlyphCount; i++)
        {
            var sr = glyphs[i];
            if (!sr.enabled) continue;
            glyphAge[i] += dt;
            float u = glyphAge[i] / 1.1f;
            if (u >= 1f) { sr.enabled = false; continue; }
            Vector2 p = glyphFrom[i] + new Vector2(0.12f * Mathf.Sin(glyphAge[i] * 7f + i), 0.75f * Mathf.Sqrt(u)) * Mathf.Sqrt(body.size / 0.6f);
            sr.transform.position = new Vector3(p.x, p.y, 0f);
            float a = Mathf.Clamp01(u / 0.08f) * (1f - u * u);
            if (Occlusion.Hidden(p, glyphLayer[i])) a = 0f;
            sr.color = new Color(bright.r, bright.g, bright.b, a);
        }
        if (key.enabled)
        {
            keyAge += dt;
            float u = keyAge / 0.4f;
            if (u >= 1f) { key.enabled = keyGlow.enabled = false; }
            else
            {
                float a = keyAmount * (1f - u) * (1f - u);
                key.color = new Color(bright.r, bright.g, bright.b, a);
                keyGlow.color = new Color(bright.r, bright.g, bright.b, a * 0.3f);
            }
        }
    }

    bool Fx()
    {
        if (fxRoot) { if (!fxRoot.activeSelf) fxRoot.SetActive(true); return true; }
        Bake();
        fxRoot = new GameObject((name ?? "Buddy") + " Band");
        int o = body.sortingOrder;
        Material m = material ? material : body.spriteMaterial;
        material = m;
        for (int i = 0; i < GlyphCount; i++) glyphs[i] = Make("Note", s_note, m, o + 4);
        key = Make("Key", s_bit, m, o - 4);
        keyGlow = Make("Key Glow", s_bit, m, o - 4);
        return true;
    }

    SpriteRenderer Make(string n, Sprite sprite, Material m, int order)
    {
        var g = new GameObject(n);
        g.transform.SetParent(fxRoot.transform, false);
        var sr = g.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.sortingOrder = order;
        if (m) { sr.sharedMaterial = m; SpriteMats.Fit(sr, m); }
        sr.enabled = false;
        return sr;
    }

    // ------------------------------------------------------------------ pictures

    static void Bake()
    {
        if (s_note) return;
        s_note = NoteSprite(false);
        s_notes = NoteSprite(true);
        var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "Band Key", filterMode = FilterMode.Bilinear };
        var px = new Color32[16];
        for (int i = 0; i < 16; i++) px[i] = new Color32(255, 255, 255, 255);
        tex.SetPixels32(px);
        tex.Apply(false, true);
        s_bit = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4f);
    }

    // a music note (or two beamed ones): white with a dark outline, 1 unit, pivot in the middle
    static Sprite NoteSprite(bool two)
    {
        const int N = 32;
        var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { name = two ? "Band Notes" : "Band Note", filterMode = FilterMode.Bilinear };
        var px = new Color32[N * N];
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                Vector2 p = new Vector2(x + 0.5f, y + 0.5f) * (24f / N); // drawn on a 24 grid
                float d = two
                    ? Mathf.Min(Mathf.Min(Head(p, new Vector2(6.5f, 5.5f)), Head(p, new Vector2(16.5f, 7.5f))),
                        Mathf.Min(Mathf.Min(Seg(p, new Vector2(10.3f, 6f), new Vector2(10.3f, 19.5f)) - 0.9f, Seg(p, new Vector2(20.3f, 8f), new Vector2(20.3f, 21.5f)) - 0.9f),
                            Seg(p, new Vector2(10.3f, 19f), new Vector2(20.3f, 21f)) - 1.6f))
                    : Mathf.Min(Mathf.Min(Head(p, new Vector2(9f, 6f)), Seg(p, new Vector2(12.8f, 6.5f), new Vector2(12.8f, 21f)) - 0.9f),
                        Mathf.Min(Seg(p, new Vector2(12.8f, 20.5f), new Vector2(17.5f, 16f)) - 1.3f, Seg(p, new Vector2(17.5f, 16f), new Vector2(17.8f, 11.5f)) - 1.1f));
                float fill = Mathf.Clamp01(0.5f - d * 1.3f);
                float edge = Mathf.Clamp01(0.5f - (d - 1.4f) * 1.3f);
                float v = Mathf.Lerp(0.16f, 1f, fill / Mathf.Max(edge, 1e-4f));
                byte g = (byte)(v * 255f);
                px[y * N + x] = new Color32(g, g, g, (byte)(edge * 255f));
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), N);
    }

    // a tilted oval note head (signed distance, roughly)
    static float Head(Vector2 p, Vector2 c)
    {
        Vector2 q = p - c;
        float a = 20f * Mathf.Deg2Rad, cs = Mathf.Cos(a), sn = Mathf.Sin(a);
        Vector2 r = new Vector2(cs * q.x + sn * q.y, -sn * q.x + cs * q.y);
        Vector2 e = new Vector2(r.x / 4.3f, r.y / 3.1f);
        return (e.magnitude - 1f) * 3.1f;
    }

    static float Seg(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
        return (p - (a + ab * t)).magnitude;
    }
}
