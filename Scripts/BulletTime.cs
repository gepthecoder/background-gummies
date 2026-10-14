using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Bullet time: chat "freeze" (or WhipIt > Buddy > Freeze Time) and time stops. Everything holds still right where it is,
/// like a photo: buddies mid-jump, a snack mid-fall, the ball mid-flight, raindrops, feathers, a window mid-wobble, the
/// sounds (they slur down to nothing). A flash, a ripple, your screens go cool and quiet, little viewfinder corners, and
/// "TIME STOPPED" at the top. You can still pick things up and move them around: what you hold runs on your time; let go
/// and it hangs right there in the air (a throw stays saved for later). Click the bare desktop, tap a buddy (without
/// dragging it) or chat "go": a ripple, slow motion, everything that moves leaves afterimages while it eases back up to
/// normal speed, and the sounds slur back up with it. Time goes on by itself after a few minutes if nobody lets it go.
/// It runs the world's clock (Time.timeScale): everything on Time.deltaTime / Time.time slows and stops with it, the
/// gummies' own physics follows it (GummyBody), and the chat, the window tracking and your hand stay on real time.
/// Lives on the Interaction object (WhipIt > Setup).
/// </summary>
[DefaultExecutionOrder(-40)] // after Grabber (-50): a click on a buddy is a grab, not "go"
[DisallowMultipleComponent]
public class BulletTime : MonoBehaviour
{
    public enum Phase { Off, Stopping, Stopped, Resuming }

    [Tooltip("Time slows to a stop over this long (real seconds).")]
    public float stopIn = 0.35f;
    [Tooltip("Coming back: this slow at first (1 = normal speed) ...")]
    [Range(0.03f, 0.5f)] public float slowest = 0.1f;
    [Tooltip("... for this long (real seconds), then back up to normal speed over this long.")]
    public float holdSlow = 0.8f;
    public float speedUpIn = 2.4f;
    [Tooltip("Nobody lets it go: time goes on by itself after this long (real seconds).")]
    public float maxStopped = 300f;
    [Tooltip("Afterimages behind whatever moves while it's slow.")]
    public bool afterimages = true;
    [Tooltip("The cool tint over your screens while time is stopped (alpha: at the edges; the middle gets about a quarter).")]
    public Color tint = new Color(0.55f, 0.7f, 1f, 0.2f);
    [Tooltip("Sprite material (WhipIt > Setup assigns it).")]
    public Material material;
    [Tooltip("Above the buddies and their bubbles, under the chat box and the laser (300).")]
    public int sortingOrder = 250;
    [Tooltip("Screen pixels per pixel of the label on a 1080p screen.")]
    public int pixelScale1080 = 3;
    [Header("Sounds (WhipIt > Setup assigns them)")]
    public AudioClip stopClip;
    public AudioClip goClip;
    [Range(0f, 1f)] public float volume = 0.5f;

    /// <summary>The one in the scene (on the Interaction object).</summary>
    public static BulletTime Current { get; private set; }
    /// <summary>Time is stopping or stopped: statues (no thinking, eating or chatter); what you pick up still moves.</summary>
    public static bool Stopped => Current && (Current.State == Phase.Stopping || Current.State == Phase.Stopped);
    /// <summary>Stopping, stopped, or still slow on the way back: things that start by themselves wait.</summary>
    public static bool Active => Current && Current.State != Phase.Off;

    public Phase State { get; private set; }
    /// <summary>How fast the world runs (1 = normal, 0 = stopped); Time.timeScale while it's on.</summary>
    public float Scale { get; private set; } = 1f;
    /// <summary>Counts (tests): times time stopped, afterimages left, sounds slowed down with it.</summary>
    public int Freezes { get; private set; }
    public int Afterimages { get; private set; }
    public int Slowed => tracks.Count;

    float phaseT, from = 1f, tapAt;
    Vector2 tapFrom;
    bool wasDown, tapping;

    // ------------------------------------------------------------------ life

    void Awake()
    {
        root = new GameObject("Bullet Time").transform; // (at the world's origin: the afterimages are drawn in world space)
        sfx = gameObject.AddComponent<AudioSource>();
        sfx.playOnAwake = false;
        sfx.spatialBlend = 0f;
    }

    void OnEnable() { Current = this; }

    void OnDisable()
    {
        if (State != Phase.Off) End(false);
        HideAll();
        if (Current == this) Current = null;
    }

    void OnDestroy()
    {
        if (root) Destroy(root.gameObject);
        foreach (var o in made) if (o) Destroy(o);
        made.Clear();
    }

    // ------------------------------------------------------------------ you

    /// <summary>Stop time (chat "freeze"): the ripple starts at this buddy (none: at the cursor).</summary>
    public bool Freeze(GummyBody who) => Freeze(who ? who.Center : Grabber.CursorWorld);
    public bool Freeze() => Freeze(Grabber.CursorWorld);

    public bool Freeze(Vector2 at)
    {
        if (!isActiveAndEnabled || Stopped) return false;
        if (BandJam.Current) BandJam.Current.Stop(); // (a song runs on the audio clock: it can't wait, it stops)
        from = State == Phase.Resuming ? Scale : 1f;
        tapping = false;
        State = Phase.Stopping;
        phaseT = 0f;
        Freezes++;
        Ripple(at);
        flash = 1f;
        lastPos.Clear();
        Capture();
        Play(stopClip);
        return true;
    }

    /// <summary>Let time go on (a click on the bare desktop, chat "go"): slow motion first. The ripple starts here.</summary>
    public bool Go(Vector2? at = null)
    {
        if (!Stopped) return false;
        tapping = false;
        State = Phase.Resuming;
        phaseT = 0f;
        Ripple(at ?? Grabber.CursorWorld);
        Play(goClip);
        return true;
    }

    /// <summary>WhipIt > Buddy > Freeze Time: stop it, or let it go.</summary>
    public void Toggle() { if (Stopped) Go(); else Freeze(); }

    /// <summary>On a screen, on none of your windows (and not on the taskbar): the bare desktop.</summary>
    public static bool OnBareDesktop(Vector2 p)
    {
        bool on = false;
        if (!ScreenBounds.Multi) on = ScreenBounds.WorldRect.Contains(p);
        else foreach (var s in ScreenBounds.Screens) if (s.work.Contains(p)) { on = true; break; }
        if (!on) return false;
        foreach (var w in DesktopPlatforms.Windows) if (!w.screen && w.Contains(p)) return false; // a window: you're working
        return true;
    }

    // ------------------------------------------------------------------ the clock

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("BulletTime.Update", t0); }
    void UpdateTimed()
    {
        float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
        bool down = DesktopInput.LeftButtonHeld, click = down && !wasDown, release = !down && wasDown;
        wasDown = down;
        if (State != Phase.Off)
        {
            phaseT += dt;
            switch (State)
            {
                case Phase.Stopping:
                {
                    float u = Mathf.Clamp01(phaseT / Mathf.Max(stopIn, 0.01f));
                    Scale = from * (1f - u) * (1f - u);
                    if (u >= 1f) { Scale = 0f; State = Phase.Stopped; phaseT = 0f; }
                    break;
                }
                case Phase.Stopped:
                    Scale = 0f;
                    if (click && phaseT > 0.25f)
                    {
                        if (!Grabber.Busy) { if (OnBareDesktop(Grabber.CursorWorld)) Go(Grabber.CursorWorld); } // (on a window: you're working)
                        else { tapping = true; tapAt = Time.unscaledTime; tapFrom = Grabber.CursorWorld; } // (picked something up)
                    }
                    else if (release && tapping)
                    {
                        // a quick tap on something (not a drag) lets it go too: there's always a way, maximized windows or not
                        tapping = false;
                        if (Time.unscaledTime - tapAt < 0.3f && (Grabber.CursorWorld - tapFrom).magnitude < 0.12f) Go(Grabber.CursorWorld);
                    }
                    else if (phaseT > maxStopped) Go(null);
                    break;
                case Phase.Resuming:
                {
                    float u = Mathf.Clamp01((phaseT - holdSlow) / Mathf.Max(speedUpIn, 0.01f));
                    Scale = Mathf.Lerp(slowest, 1f, u * u * (3f - 2f * u));
                    if (u >= 1f) End(true);
                    break;
                }
            }
            if (State != Phase.Off) Time.timeScale = Scale;
        }
        Visuals(dt);
        Ghosts(dt);
    }

    void LateUpdate() { long t0 = WhipProf.T(); LateUpdateTimed(); WhipProf.Add("BulletTime.LateUpdate", t0); }
    void LateUpdateTimed()
    {
        if (tracks.Count == 0) return;
        if (State == Phase.Stopping || State == Phase.Resuming)
        {
            scanT -= Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            if (scanT <= 0f) { scanT = 0.25f; Capture(); } // (new sounds slow down too)
        }
        // each sound at its own pitch times the world's speed (if its owner changed the pitch, that's its own now)
        for (int i = tracks.Count - 1; i >= 0; i--)
        {
            Track tr = tracks[i];
            if (!tr.src) { tracks.RemoveAt(i); continue; }
            if (Mathf.Abs(tr.src.pitch - tr.written) > 1e-4f) tr.basePitch = tr.src.pitch;
            tr.written = tr.basePitch * Scale;
            tr.src.pitch = tr.written;
        }
    }

    void End(bool natural)
    {
        State = Phase.Off;
        Scale = 1f;
        phaseT = 0f;
        Time.timeScale = 1f;
        Release();
        lastPos.Clear();
        if (natural && Random.value < 0.6f) Remark();
    }

    void Remark()
    {
        var who = new List<GummyBody>();
        foreach (var b in GummyBody.All)
        {
            if (!b || !b.isActiveAndEnabled || b.Held || !b.GetComponent<BuddySpeech>()) continue;
            var br = b.GetComponent<GummyBrain>();
            if (br && br.Current == GummyBrain.State.Asleep) continue;
            who.Add(b);
        }
        if (who.Count == 0) return;
        var sp = who[Random.Range(0, who.Count)].GetComponent<BuddySpeech>();
        string line = sp.voice == BuddySpeech.Voice.Duck ? "QUACK...?" : sp.voice == BuddySpeech.Voice.Monster ? "WE SAW... EVERYTHING"
            : Lines[Random.Range(0, Lines.Length)];
        sp.Say(line, 0f, true);
    }

    static readonly string[] Lines = { "WHOA...", "DID TIME JUST STOP?", "I SAW EVERYTHING... SLOWLY", "WHAT JUST HAPPENED?", "WAS THAT A DREAM?", "MY WHOLE LIFE WENT BY" };

    // ------------------------------------------------------------------ sound: everything slurs down to a stop and back up

    class Track { public AudioSource src; public float basePitch, written; }
    readonly List<Track> tracks = new List<Track>();
    AudioSource sfx;
    float scanT;

    void Capture()
    {
        var all = FindObjectsByType<AudioSource>(FindObjectsSortMode.None);
        if (all == null) return;
        foreach (var a in all)
        {
            if (!a || a == sfx || !a.isActiveAndEnabled) continue;
            bool known = false;
            foreach (var tr in tracks) if (tr.src == a) { known = true; break; }
            if (!known) tracks.Add(new Track { src = a, basePitch = a.pitch, written = a.pitch });
        }
    }

    void Release()
    {
        foreach (var tr in tracks) if (tr.src && Mathf.Abs(tr.src.pitch - tr.written) <= 1e-4f) tr.src.pitch = tr.basePitch;
        tracks.Clear();
    }

    void Play(AudioClip clip)
    {
        if (!clip || !sfx) return;
        sfx.pitch = 1f;
        sfx.PlayOneShot(clip, volume);
    }

    // ------------------------------------------------------------------ the look: a flash, a ripple, a cool tint, the label

    Transform root;
    readonly List<Object> made = new List<Object>();
    readonly List<SpriteRenderer> veils = new List<SpriteRenderer>();   // one per screen
    readonly List<SpriteRenderer> corners = new List<SpriteRenderer>(); // four per screen
    SpriteRenderer flashSr, ringSr, labelSr;
    Sprite veilSprite, ringSprite, dotSprite, cornerSprite, labelStop, labelStopDim, labelSlow;
    float flash, ringT = -1f, ringR, shown, labelShown, fontPx = 0.028f, screenPx;
    Vector2 ringAt;
    int builtScreens = -1;

    void Ripple(Vector2 at)
    {
        ringAt = at;
        ringT = 0f;
        Rect w = ScreenBounds.WorldRect; // (out to the farthest corner of all your screens)
        ringR = 0f;
        foreach (var c in new[] { new Vector2(w.xMin, w.yMin), new Vector2(w.xMin, w.yMax), new Vector2(w.xMax, w.yMin), new Vector2(w.xMax, w.yMax) })
            ringR = Mathf.Max(ringR, (c - at).magnitude);
    }

    SpriteRenderer MakeSr(string n, int order)
    {
        var go = new GameObject(n);
        go.transform.SetParent(root, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sortingOrder = order;
        if (material) sr.sharedMaterial = material;
        sr.enabled = false;
        return sr;
    }

    void Build()
    {
        if (flashSr) return;
        dotSprite = Bake(4, 4, (x, y) => new Color32(255, 255, 255, 255), FilterMode.Point, new Vector2(0.5f, 0.5f), 4f);
        veilSprite = Bake(64, 64, (x, y) =>
        {
            float dx = (x + 0.5f) / 32f - 1f, dy = (y + 0.5f) / 32f - 1f, r = Mathf.Clamp01(Mathf.Sqrt(dx * dx + dy * dy) / 1.4142f);
            return new Color32(255, 255, 255, (byte)(255f * Mathf.Lerp(0.27f, 1f, r * r)));
        }, FilterMode.Bilinear, new Vector2(0.5f, 0.5f), 64f);
        ringSprite = Bake(128, 128, (x, y) =>
        {
            float dx = (x + 0.5f) / 64f - 1f, dy = (y + 0.5f) / 64f - 1f, r = Mathf.Sqrt(dx * dx + dy * dy);
            float a = Mathf.Clamp01(1f - Mathf.Abs(r - 0.93f) / 0.05f), glow = Mathf.Clamp01(1f - (0.93f - r) / 0.35f) * (r < 0.93f ? 0.25f : 0f);
            return new Color32(255, 255, 255, (byte)(255f * Mathf.Clamp01(a * a + glow)));
        }, FilterMode.Bilinear, new Vector2(0.5f, 0.5f), 64f);
        // a viewfinder corner: an L, 2 px light with a dark outline, the corner at the pivot (top left)
        cornerSprite = Bake(13, 13, (x, y) =>
        {
            int yy = 12 - y; // (from the top)
            bool ink = (yy >= 1 && yy <= 2 && x >= 1 && x <= 11) || (x >= 1 && x <= 2 && yy >= 1 && yy <= 11);
            bool edge = !ink && ((yy <= 3 && x <= 12) || (x <= 3 && yy <= 12));
            return ink ? new Color32(255, 252, 244, 255) : edge ? new Color32(38, 30, 26, 200) : new Color32(0, 0, 0, 0);
        }, FilterMode.Point, new Vector2(0f, 1f), 1f);
        labelStop = Label(true, true);
        labelStopDim = Label(true, false);
        labelSlow = Label(false, true);
        flashSr = MakeSr("Flash", sortingOrder + 20);
        flashSr.sprite = dotSprite;
        ringSr = MakeSr("Ripple", sortingOrder + 5);
        ringSr.sprite = ringSprite;
        labelSr = MakeSr("Label", sortingOrder + 10);
    }

    delegate Color32 Px(int x, int y);

    Sprite Bake(int w, int h, Px f, FilterMode filter, Vector2 pivot, float ppu)
    {
        var px = new Color32[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) px[y * w + x] = f(x, y);
        return Sprite.Create(Tex(px, w, h, filter), new Rect(0, 0, w, h), pivot, ppu, 0, SpriteMeshType.FullRect);
    }

    Texture2D Tex(Color32[] px, int w, int h, FilterMode filter)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = filter, name = "BulletTime" };
        tex.SetPixels32(px);
        tex.Apply(false, false);
        made.Add(tex);
        return tex;
    }

    /// <summary>"|| TIME STOPPED / CLICK THE DESKTOP OR SAY GO" (bars: the pause sign, blinking), or "> SLOW MOTION".</summary>
    Sprite Label(bool stopped, bool icon)
    {
        Color32[] px = BakeLabel(stopped, icon, out int w, out int h);
        return Sprite.Create(Tex(px, w, h, FilterMode.Point), new Rect(0, 0, w, h), Vector2.zero, 1f, 0, SpriteMeshType.FullRect);
    }

    public static Color32[] BakeLabel(bool stopped, bool icon, out int w, out int h)
    {
        const int pad = 3, iconW = 5;
        var fill = new Color32(38, 30, 26, 238);
        var edge = new Color32(255, 252, 244, 255);
        var ink = new Color32(255, 246, 228, 255);
        var cool = new Color32(160, 205, 255, 255);
        var dim = new Color32(150, 138, 124, 255);
        string top = stopped ? "TIME STOPPED" : "SLOW MOTION", hint = "CLICK THE DESKTOP OR SAY GO";
        int tw = iconW + 3 + PixelFont.Measure(top), hw = stopped ? PixelFont.Measure(hint) : 0;
        w = pad + Mathf.Max(tw, hw) + pad + 2;
        h = stopped ? 2 * pad + PixelFont.GlyphH * 2 + 3 : 2 * pad + PixelFont.GlyphH;
        var px = new Color32[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool ex = x == 0 || x == w - 1, ey = y == 0 || y == h - 1;
                px[y * w + x] = ex && ey ? new Color32(0, 0, 0, 0) : ex || ey ? edge : fill;
            }
        int x0 = (w - tw) / 2, yTop = h - 1 - pad;
        if (icon)
            for (int r = 0; r < PixelFont.GlyphH; r++)
                for (int c = 0; c < iconW; c++)
                {
                    // pause: two bars; slow motion: a play triangle
                    bool on = stopped ? c != 2 : c <= Mathf.Min(r, PixelFont.GlyphH - 1 - r) * 2 && c < 4;
                    if (on) px[(yTop - r) * w + x0 + c] = cool;
                }
        PixelFont.Draw(px, w, h, top, x0 + iconW + 3, yTop, ink);
        if (stopped) PixelFont.Draw(px, w, h, hint, (w - hw) / 2, yTop - PixelFont.GlyphH - 3, dim);
        return px;
    }

    void Visuals(float dt)
    {
        bool any = State != Phase.Off || shown > 0.001f || flash > 0f || ringT >= 0f || labelShown > 0.001f;
        if (!any) return;
        Build();
        PixelSize();

        // the cool tint: comes in as it slows, full while stopped, goes as it speeds back up
        float want = State == Phase.Stopped ? 1f : State == Phase.Off ? 0f : 1f - Scale;
        shown = Mathf.MoveTowards(shown, want, dt * 4f);
        int screens = ScreenBounds.Multi ? ScreenBounds.Screens.Count : 1;
        if (screens != builtScreens)
        {
            builtScreens = screens;
            while (veils.Count < screens) { var v = MakeSr("Tint", sortingOrder); v.sprite = veilSprite; veils.Add(v); }
            while (corners.Count < screens * 4) { var c = MakeSr("Corner", sortingOrder + 10); c.sprite = cornerSprite; corners.Add(c); }
            for (int i = screens; i < veils.Count; i++) veils[i].enabled = false;
            for (int i = screens * 4; i < corners.Count; i++) corners[i].enabled = false;
        }
        float cornerA = Mathf.Clamp01(State == Phase.Stopped ? labelShown : 0f);
        for (int i = 0; i < screens; i++)
        {
            Rect full = ScreenBounds.Multi ? ScreenBounds.Screens[i].full : ScreenBounds.WorldRect;
            Rect work = ScreenBounds.Multi ? ScreenBounds.Screens[i].work : ScreenBounds.WorldRect;
            var v = veils[i];
            v.enabled = shown > 0.001f;
            v.transform.position = new Vector3(full.center.x, full.center.y, 0f);
            v.transform.localScale = new Vector3(full.width, full.height, 1f);
            v.color = new Color(tint.r, tint.g, tint.b, tint.a * shown);
            float inset = 6f * fontPx;
            for (int k = 0; k < 4; k++)
            {
                var c = corners[i * 4 + k];
                c.enabled = cornerA > 0.01f;
                if (!c.enabled) continue;
                Vector2 p = k == 0 ? new Vector2(work.xMin + inset, work.yMax - inset) : k == 1 ? new Vector2(work.xMax - inset, work.yMax - inset)
                          : k == 2 ? new Vector2(work.xMax - inset, work.yMin + inset) : new Vector2(work.xMin + inset, work.yMin + inset);
                c.transform.position = Snap(p);
                c.transform.rotation = Quaternion.Euler(0f, 0f, -90f * k); // (top left, top right, bottom right, bottom left)
                c.transform.localScale = new Vector3(fontPx, fontPx, 1f);
                c.color = new Color(1f, 1f, 1f, 0.85f * cornerA);
            }
        }

        // the flash (a camera's), the ripple
        flash = Mathf.Max(0f, flash - dt / 0.3f);
        flashSr.enabled = flash > 0f;
        if (flashSr.enabled)
        {
            Rect w = ScreenBounds.WorldRect;
            flashSr.transform.position = new Vector3(w.center.x, w.center.y, 0f);
            flashSr.transform.localScale = new Vector3(w.width, w.height, 1f);
            flashSr.color = new Color(1f, 1f, 1f, 0.3f * flash * flash);
        }
        if (ringT >= 0f)
        {
            ringT += dt / 0.6f;
            if (ringT >= 1f) { ringT = -1f; ringSr.enabled = false; }
            else
            {
                float u = ringT, e = 1f - (1f - u) * (1f - u) * (1f - u), r = Mathf.Max(0.05f, ringR * e);
                ringSr.enabled = true;
                ringSr.transform.position = new Vector3(ringAt.x, ringAt.y, 0f);
                ringSr.transform.localScale = new Vector3(r, r, 1f); // (the ring's texture is 2 units across)
                ringSr.color = new Color(0.85f, 0.95f, 1f, 0.75f * Mathf.Pow(1f - u, 1.5f));
            }
        }

        // the label: TIME STOPPED (the bars blink) / SLOW MOTION, at the top of the main screen
        bool stopped = State == Phase.Stopped, slow = State == Phase.Resuming && Scale < 0.6f;
        labelShown = Mathf.MoveTowards(labelShown, stopped || slow ? 1f : 0f, dt * (stopped || slow ? 4f : 3f));
        labelSr.enabled = labelShown > 0.001f && labelSlow;
        if (!labelSr.enabled) return;
        if (stopped) labelSr.sprite = Mathf.Repeat(phaseT, 1.2f) < 0.8f ? labelStop : labelStopDim;
        else if (State != Phase.Stopping) labelSr.sprite = labelSlow;
        Sprite s = labelSr.sprite;
        Rect m = ScreenBounds.MainRect;
        float lw = s.rect.width * fontPx, lh = s.rect.height * fontPx;
        Vector2 at = new Vector2(m.center.x - lw * 0.5f, m.yMax - 6f * fontPx - lh + (1f - labelShown) * 5f * fontPx);
        labelSr.transform.position = Snap(at);
        labelSr.transform.localScale = new Vector3(fontPx, fontPx, 1f);
        labelSr.color = new Color(1f, 1f, 1f, labelShown);
    }

    void HideAll()
    {
        shown = flash = labelShown = 0f;
        ringT = -1f;
        foreach (var v in veils) if (v) v.enabled = false;
        foreach (var c in corners) if (c) c.enabled = false;
        if (flashSr) flashSr.enabled = false;
        if (ringSr) ringSr.enabled = false;
        if (labelSr) labelSr.enabled = false;
        foreach (var g in ghosts) g.Off();
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

    // ------------------------------------------------------------------ afterimages (pooled)

    class Ghost
    {
        public GameObject go;
        public MeshRenderer mr;
        public Mesh mesh;
        public Material mat;
        public SpriteRenderer sr;
        public bool on, gummy;
        public float age, life, a0;
        public Color color;
        public void Off() { on = false; if (go) go.SetActive(false); }
    }
    const int MaxGhosts = 120;
    const float GhostEvery = 0.06f, GhostLife = 0.6f, GhostMove = 0.018f;
    readonly List<Ghost> ghosts = new List<Ghost>();
    readonly Dictionary<Object, Vector2> lastPos = new Dictionary<Object, Vector2>();
    readonly Vector3[] quadV = new Vector3[4];
    static readonly int[] QuadT = { 0, 1, 2, 0, 2, 3 };
    static readonly int ColorId = Shader.PropertyToID("_Color");
    float ghostT;
    int nextGhost;

    void Ghosts(float dt)
    {
        foreach (var g in ghosts)
        {
            if (!g.on) continue;
            g.age += dt;
            float u = g.age / g.life;
            if (u >= 1f) { g.Off(); continue; }
            Color c = g.color;
            c.a = g.a0 * (1f - u) * (1f - u);
            if (g.gummy) g.mat.SetColor(ColorId, c);
            else g.sr.color = c;
        }
        if (!afterimages || (State != Phase.Stopping && State != Phase.Resuming) || Scale >= 0.7f) return;
        ghostT += dt;
        if (ghostT < GhostEvery) return;
        ghostT = 0f;
        float a0 = 0.38f * (1f - Scale);

        // the buddies: their outline, flat, a little lighter
        foreach (var b in GummyBody.All)
        {
            if (!b || !b.isActiveAndEnabled || b.Held) continue;
            if (!Moved(b, b.Center)) continue;
            Shader sh = b.GummyShader;
            if (!sh) continue;
            Ghost g = Take(true, sh);
            if (g == null) continue;
            Rect box = b.PaintGhost(g.mat);
            quadV[0] = new Vector3(box.xMin, box.yMin, 0f);
            quadV[1] = new Vector3(box.xMin, box.yMax, 0f);
            quadV[2] = new Vector3(box.xMax, box.yMax, 0f);
            quadV[3] = new Vector3(box.xMax, box.yMin, 0f);
            g.mesh.vertices = quadV;
            g.mesh.triangles = QuadT;
            g.mesh.bounds = new Bounds(new Vector3(box.center.x, box.center.y, 0f), new Vector3(box.width, box.height, 0.1f));
            g.mr.sortingOrder = b.sortingOrder - 2;
            g.color = Color.Lerp(b.color, Color.white, 0.3f);
            Show(g, a0);
        }
        // snacks, the ball, the basketball: a copy of the sprite
        foreach (var s in Snack.All) if (s) SpriteGhost(s, a0);
        if (FetchBall.Current) SpriteGhost(FetchBall.Current, a0);
        if (Basketball.Current) SpriteGhost(Basketball.Current, a0);
    }

    bool Moved(Object o, Vector2 p)
    {
        if (!lastPos.TryGetValue(o, out Vector2 was)) { lastPos[o] = p; return false; }
        if ((p - was).sqrMagnitude < GhostMove * GhostMove) return false;
        lastPos[o] = p;
        return true;
    }

    void SpriteGhost(Component c, float a0)
    {
        var src = c.GetComponent<SpriteRenderer>();
        if (!src || !src.enabled || !src.sprite) return;
        if (!Moved(c, c.transform.position)) return;
        Ghost g = Take(false, null);
        if (g == null) return;
        g.sr.sprite = src.sprite;
        g.sr.sortingOrder = src.sortingOrder - 1;
        if (src.sharedMaterial) g.sr.sharedMaterial = src.sharedMaterial;
        g.go.transform.position = c.transform.position;
        g.go.transform.rotation = c.transform.rotation;
        g.go.transform.localScale = c.transform.localScale; // (snacks and balls sit at the top of the scene)
        Color sc = src.color;
        g.color = new Color(Mathf.Lerp(sc.r, 1f, 0.3f), Mathf.Lerp(sc.g, 1f, 0.3f), Mathf.Lerp(sc.b, 1f, 0.3f), 1f);
        Show(g, a0 * Mathf.Max(sc.a, 0f));
    }

    void Show(Ghost g, float a0)
    {
        g.on = true;
        g.age = 0f;
        g.life = GhostLife;
        g.a0 = a0;
        Color c = g.color;
        c.a = a0;
        if (g.gummy) g.mat.SetColor(ColorId, c);
        else g.sr.color = c;
        g.go.SetActive(true);
        Afterimages++;
    }

    Ghost Take(bool gummy, Shader sh)
    {
        Ghost free = null;
        foreach (var g in ghosts) if (!g.on && g.gummy == gummy) { free = g; break; }
        if (free == null && ghosts.Count < MaxGhosts)
        {
            free = new Ghost { gummy = gummy, go = new GameObject(gummy ? "Afterimage" : "Afterimage Sprite") };
            free.go.transform.SetParent(root, false);
            if (gummy)
            {
                free.mesh = new Mesh { name = "AfterimageQuad" };
                free.mesh.MarkDynamic();
                free.go.AddComponent<MeshFilter>().sharedMesh = free.mesh;
                free.mr = free.go.AddComponent<MeshRenderer>();
                free.mat = new Material(sh) { name = "Afterimage (instance)" };
                free.mr.sharedMaterial = free.mat;
                free.mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                free.mr.receiveShadows = false;
                made.Add(free.mesh);
                made.Add(free.mat);
            }
            else
            {
                free.sr = free.go.AddComponent<SpriteRenderer>();
                free.sr.enabled = true;
                if (material) free.sr.sharedMaterial = material;
            }
            ghosts.Add(free);
        }
        if (free == null)
        {
            // all in use: the oldest of this kind goes
            float best = -1f;
            foreach (var g in ghosts) if (g.gummy == gummy && g.age > best) { best = g.age; free = g; }
        }
        return free;
    }
}
