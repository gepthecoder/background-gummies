using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Breaking into your windows. Hold a buddy and push it into a window, from any side, hard (the cursor well inside the
/// window) and long (about two seconds): the window creaks and shudders, jelly seeps in where it's pressing, and then
/// SPLORCH: it's in. The window turns into its candy panel (InfestedWindow), in its colour. Push a second one into the same
/// window and the first gets knocked out the other side; the new one takes over with its own colour. Never a maximized
/// window, or a tiny one. Afterwards it tells the others about it: they don't believe a word of it, unless they've been
/// in one too (then they back it up). It also keeps track of who spends their time with whom (the panel's best friend).
/// Lives on the Interaction object (WhipIt > Setup).
/// </summary>
[DefaultExecutionOrder(8)]
[DisallowMultipleComponent]
public class WindowInfestation : MonoBehaviour
{
    [Tooltip("Pushing this long (seconds, pressing about half a unit deep; deeper is quicker) breaks in.")]
    public float breachTime = 1.8f;
    [Tooltip("Sprite material (WhipIt > Setup assigns it).")]
    public Material material;
    [Header("Sounds (WhipIt > Setup assigns them)")]
    public AudioClip creakClip;
    public AudioClip breachClip, ejectClip, clickClip;
    [Range(0f, 1f)] public float volume = 0.6f;

    /// <summary>The one in the scene (on the Interaction object).</summary>
    public static WindowInfestation Current { get; private set; }
    /// <summary>The windows they're in right now.</summary>
    public IReadOnlyList<InfestedWindow> Infested => list;
    /// <summary>How far the push into a window has got (0..1), the window, who (tests).</summary>
    public float Progress => progress;
    public long PushWindow => pushWin;
    public int Breaches { get; private set; }
    public int Stories { get; private set; }

    readonly List<InfestedWindow> list = new List<InfestedWindow>();

    void Awake()
    {
        sfx = gameObject.AddComponent<AudioSource>();
        sfx.playOnAwake = false;
        sfx.spatialBlend = 0f;
        creak = gameObject.AddComponent<AudioSource>();
        creak.playOnAwake = false;
        creak.spatialBlend = 0f;
        creak.loop = true;
        creak.volume = 0f;
        Load();
    }

    void OnEnable() { Current = this; }

    void OnDisable()
    {
        foreach (var w in list.ToArray()) if (w) w.Eject(false, Vector2.zero, Vector2.up);
        list.Clear();
        if (creak) creak.Stop();
        if (dirtyFriends || dirtyVisits) Save();
        if (Current == this) Current = null;
    }

    void OnDestroy()
    {
        if (fxRoot) Destroy(fxRoot.gameObject);
        foreach (var o in made) if (o) Destroy(o);
    }

    void OnApplicationQuit() { if (dirtyFriends || dirtyVisits) Save(); }

    /// <summary>The window this buddy's in (null: none).</summary>
    public InfestedWindow Of(GummyBody b) { foreach (var w in list) if (w && w.Intruder == b) return w; return null; }
    public InfestedWindow OfWindow(long id) { foreach (var w in list) if (w && w.WindowId == id && w.State != InfestedWindow.Phase.Melt) return w; return null; }

    /// <summary>Chat "come out": out it hops. False if it isn't in one.</summary>
    public bool ComeOut(GummyBody b)
    {
        var w = Of(b);
        if (!w || w.State == InfestedWindow.Phase.Melt) return false;
        w.Eject(false, Vector2.zero, Vector2.up);
        return true;
    }

    // ------------------------------------------------------------------ pushing in

    GummyBody pusher;
    long pushWin;
    float progress, nextNudge, nextRipple;
    Vector2 pushAt, pushIn;
    bool saidPush;

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("WindowInfestation.Update", t0); }
    void UpdateTimed()
    {
        float dt = Mathf.Min(Time.deltaTime, 0.05f), now = Time.time;
        for (int i = list.Count - 1; i >= 0; i--) if (!list[i]) list.RemoveAt(i);
        Push(dt, now);
        Burst(Mathf.Min(Time.unscaledDeltaTime, 0.1f));
        Together(dt);
        Tell(now);
    }

    void Push(float dt, float now)
    {
        GummyBody held = null;
        foreach (var b in GummyBody.All)
            if (b && b.isActiveAndEnabled && b.Held && !b.InRoom && !b.IsMonster && !GiantPotion.IsGiant(b)) { held = b; break; }
        bool pressing = false, sinking = false;
        WindowBox w = default;
        float depth = 0f;
        if (held && !BulletTime.Stopped)
        {
            Vector2 cur = held.HeldAt;
            foreach (var o in DesktopPlatforms.Windows)
            {
                if (o.screen || !o.Contains(cur)) continue;
                w = o; // (the front-most one there: the one you see)
                if (o.movable && o.xMax - o.xMin >= InfestedWindow.MinW && o.yMax - o.yMin >= InfestedWindow.MinH)
                {
                    depth = Mathf.Min(Mathf.Min(cur.x - o.xMin, o.xMax - cur.x), Mathf.Min(cur.y - o.yMin, o.yMax - cur.y));
                    Vector2 edge = Closest(o, held.Center);
                    sinking = o.Contains(held.Center); // (squeezing through already: it's going in)
                    bool touching = sinking || (edge - held.Center).magnitude < held.size * 0.75f + 0.12f;
                    pressing = depth > 0.22f && touching;
                    if (pressing) { pushAt = edge; pushIn = (cur - edge).sqrMagnitude > 1e-6f ? (cur - edge).normalized : Vector2.down; }
                }
                break;
            }
        }
        if (pressing && (held != pusher || w.id != pushWin)) { pusher = held; pushWin = w.id; progress = 0f; saidPush = false; }
        if (pressing) progress += dt * Mathf.Lerp(0.7f, 1.6f, Mathf.InverseLerp(0.22f, 1.2f, depth)) * (sinking ? 3f : 1f) / Mathf.Max(breachTime, 0.2f);
        else progress = Mathf.Max(0f, progress - dt * 1.2f);
        if (!pressing && progress <= 0f) { pusher = null; pushWin = 0; }

        // the window gives a little: it shudders, creaks, jelly seeps in where it's pushing
        if (pressing && now >= nextNudge)
        {
            nextNudge = now + Mathf.Lerp(0.32f, 0.16f, progress);
            var ww = held.GetComponent<WindowWobble>();
            if (ww) ww.Stomp(w.id, pushAt, 0.35f + 1.4f * progress);
        }
        if (pressing && !saidPush && progress > 0.45f)
        {
            saidPush = true;
            var sp = held.GetComponent<BuddySpeech>();
            if (sp) sp.Say(Pick("MMMPH!", "LET ME IN!", "IT'S GIVING!", "SQUEEEZE!"), 0f, true);
        }
        Goo(pressing ? held : null, pressing ? progress : 0f, dt);
        if (creak && creakClip)
        {
            float want = pressing ? Mathf.Clamp01(progress * 1.3f) * volume * 0.7f : 0f;
            creak.volume = Mathf.MoveTowards(creak.volume, want, dt * 3f);
            creak.pitch = 0.8f + 0.6f * progress;
            if (want > 0.01f && !creak.isPlaying) { creak.clip = creakClip; creak.Play(); }
            else if (want <= 0.01f && creak.volume <= 0.01f && creak.isPlaying) creak.Stop();
        }
        if (pressing && progress >= 1f) { Breach(held, w, pushAt); progress = 0f; pusher = null; pushWin = 0; }
    }

    /// <summary>In it goes (or the test says so): a buddy you're holding breaks into this window at this point of its edge.</summary>
    public InfestedWindow Breach(GummyBody b, WindowBox w, Vector2 at)
    {
        if (!b || b.InRoom) return null;
        if (b.Held) { Grabber.Handoff(b, null); b.EndGrab(); }
        var old = OfWindow(w.id);
        if (old)
        {
            // the one in there gets knocked out the far side
            Vector2 away = new Vector2(w.xMin + w.xMax, w.yMin + w.yMax) * 0.5f - at;
            old.Eject(false, Vector2.zero, away);
            var sp = old.Intruder ? old.Intruder.GetComponent<BuddySpeech>() : null;
            if (sp) sp.Say(Pick("HEY! I WAS HERE FIRST!", "NOOO! MY WINDOW!", "RUDE!"), 0f, true);
        }
        var go = new GameObject("Infested Window");
        var iw = go.AddComponent<InfestedWindow>();
        list.Add(iw);
        Breaches++;
        iw.Init(this, w, b, at, material ? material : b.spriteMaterial);
        Play(breachClip, 1f, 1f);
        BurstAt(at, b.color, 26);
        return iw;
    }

    static Vector2 Closest(WindowBox w, Vector2 p)
    {
        float x = Mathf.Clamp(p.x, w.xMin, w.xMax), y = Mathf.Clamp(p.y, w.yMin, w.yMax);
        if (p.x > w.xMin && p.x < w.xMax && p.y > w.yMin && p.y < w.yMax)
        {
            // inside: the nearest edge
            float l = p.x - w.xMin, r = w.xMax - p.x, d = p.y - w.yMin, u = w.yMax - p.y, m = Mathf.Min(Mathf.Min(l, r), Mathf.Min(d, u));
            if (m == l) x = w.xMin; else if (m == r) x = w.xMax; else if (m == d) y = w.yMin; else y = w.yMax;
        }
        return new Vector2(x, y);
    }

    /// <summary>It's out (InfestedWindow): counted, a story to tell later.</summary>
    public void Out(InfestedWindow w, GummyBody b)
    {
        list.Remove(w);
        Play(ejectClip, 1f, 0.9f);
        if (!b) return;
        BurstAt(b.Center, b.color, 12);
        string save = SaveOf(b);
        if (save != null) { visits.TryGetValue(save, out int n); visits[save] = n + 1; dirtyVisits = true; Save(); }
        storyAt[b] = Time.time + Random.Range(20f, 50f);
        var sp = b.GetComponent<BuddySpeech>();
        if (sp) sp.Say(Pick("WHAT A TRIP!", "I'M BACK!", "WHOA... THAT WAS AMAZING", "I'LL NEVER FORGET IT"), 0f, true);
    }

    /// <summary>A panel button was pressed.</summary>
    public void Click() => Play(clickClip, Random.Range(0.95f, 1.1f), 0.5f);

    // ------------------------------------------------------------------ the goo seeping in, sprinkles flying

    Transform fxRoot;
    SpriteRenderer gooSr, ringSr;
    readonly List<Object> made = new List<Object>();
    float ringT;

    void Goo(GummyBody b, float u, float dt)
    {
        if (!fxRoot) fxRoot = new GameObject("Window Infestation").transform;
        if (!gooSr) { gooSr = MakeSr("Goo", Blob(), 3); ringSr = MakeSr("Goo Ripple", Ring(), 3); }
        bool on = b && u > 0.03f && !Occlusion.Hidden(pushAt + pushIn * 0.1f, Occlusion.LayerOf(pushWin));
        gooSr.enabled = on;
        ringSr.enabled = on;
        if (!on) return;
        Color c = b.color;
        float s = b.size * (0.35f + 1.3f * u);
        gooSr.transform.position = new Vector3(pushAt.x + pushIn.x * s * 0.25f, pushAt.y + pushIn.y * s * 0.25f, 0f);
        gooSr.transform.localScale = new Vector3(s, s, 1f);
        gooSr.color = new Color(c.r, c.g, c.b, 0.55f + 0.35f * u);
        ringT = Mathf.Repeat(ringT + dt * (1.2f + 2f * u), 1f);
        float rs = b.size * (0.4f + 1.8f * ringT) * (0.7f + u);
        ringSr.transform.position = gooSr.transform.position;
        ringSr.transform.localScale = new Vector3(rs, rs, 1f);
        Color rc = Color.Lerp(c, Color.white, 0.5f);
        ringSr.color = new Color(rc.r, rc.g, rc.b, (1f - ringT) * 0.7f * u);
    }

    SpriteRenderer MakeSr(string n, Sprite s, int order)
    {
        var go = new GameObject(n);
        go.transform.SetParent(fxRoot, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = s;
        sr.sortingOrder = order;
        if (material) sr.sharedMaterial = s ? SpriteMats.For(material, s.texture) : material; // (its own copy: SpriteMats)
        sr.enabled = false;
        return sr;
    }

    Sprite Blob()
    {
        const int n = 48;
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                float a = Mathf.Atan2(dy, dx), r = Mathf.Sqrt(dx * dx + dy * dy) / (0.82f + 0.12f * Mathf.Sin(a * 5f) + 0.06f * Mathf.Sin(a * 9f + 1f));
                float k = Mathf.Clamp01((1f - r) * 6f);
                bool shine = dx < -0.15f && dx > -0.45f && dy > 0.2f && dy < 0.45f;
                px[y * n + x] = shine && k > 0.5f ? new Color32(255, 255, 255, (byte)(255 * k)) : new Color32(255, 255, 255, (byte)(255 * k * (0.75f + 0.25f * r)));
            }
        return Bake(px, n, "InfestGoo");
    }

    Sprite Ring()
    {
        const int n = 64;
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f, r = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Clamp01(1f - Mathf.Abs(r - 0.9f) / 0.07f);
                px[y * n + x] = new Color32(255, 255, 255, (byte)(255 * a));
            }
        return Bake(px, n, "InfestRing");
    }

    Sprite Bake(Color32[] px, int n, string name)
    {
        var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = name };
        t.SetPixels32(px);
        t.Apply(false, false);
        var s = Sprite.Create(t, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n, 0, SpriteMeshType.FullRect);
        made.Add(t);
        made.Add(s);
        return s;
    }

    // candy sprinkles flying out (pooled)
    const int MaxBits = 48;
    struct Bit { public Vector2 pos, vel; public float t, life, spin; public bool on; }
    readonly Bit[] bits = new Bit[MaxBits];
    readonly SpriteRenderer[] bitSr = new SpriteRenderer[MaxBits];
    static readonly Color[] Candy = { new Color(1f, 0.43f, 0.63f), new Color(1f, 0.84f, 0.31f), new Color(0.43f, 0.82f, 0.67f), new Color(0.43f, 0.67f, 1f), new Color(0.73f, 0.51f, 1f), new Color(1f, 0.63f, 0.39f) };
    Sprite bitSprite;

    void BurstAt(Vector2 at, Color c, int n)
    {
        if (!fxRoot) fxRoot = new GameObject("Window Infestation").transform;
        if (!bitSprite)
        {
            var px = new Color32[4 * 2];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 255, 255, 255);
            var t = new Texture2D(4, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, name = "InfestBit" };
            t.SetPixels32(px);
            t.Apply(false, false);
            bitSprite = Sprite.Create(t, new Rect(0, 0, 4, 2), new Vector2(0.5f, 0.5f), 36f, 0, SpriteMeshType.FullRect);
            made.Add(t);
            made.Add(bitSprite);
        }
        for (int k = 0; k < n; k++)
        {
            int i = -1;
            for (int j = 0; j < MaxBits; j++) if (!bits[j].on) { i = j; break; }
            if (i < 0) return;
            float ang = Random.Range(0f, Mathf.PI * 2f), sp = Random.Range(2f, 6f);
            bits[i] = new Bit { pos = at, vel = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * sp + Vector2.up * 2f, t = 0f, life = Random.Range(0.7f, 1.2f), spin = Random.Range(-720f, 720f), on = true };
            if (!bitSr[i]) bitSr[i] = MakeSr("Sprinkle", bitSprite, 96);
            bitSr[i].color = k % 4 == 0 ? Color.Lerp(c, Color.white, 0.3f) : Candy[Random.Range(0, Candy.Length)];
            bitSr[i].transform.position = new Vector3(at.x, at.y, 0f);
            bitSr[i].enabled = true;
        }
    }

    void Burst(float dt)
    {
        for (int i = 0; i < MaxBits; i++)
        {
            if (!bits[i].on) continue;
            var b = bits[i];
            b.t += dt;
            if (b.t >= b.life) { b.on = false; bits[i] = b; if (bitSr[i]) bitSr[i].enabled = false; continue; }
            b.vel.y -= 12f * dt;
            b.pos += b.vel * dt;
            bits[i] = b;
            var sr = bitSr[i];
            sr.transform.position = new Vector3(b.pos.x, b.pos.y, 0f);
            sr.transform.rotation = Quaternion.Euler(0f, 0f, b.spin * b.t);
            Color c = sr.color;
            c.a = Mathf.Clamp01((b.life - b.t) / 0.3f);
            sr.color = c;
        }
    }

    // ------------------------------------------------------------------ the stories (and who hangs out with whom)

    readonly Dictionary<string, int> visits = new Dictionary<string, int>();
    readonly Dictionary<string, float> together = new Dictionary<string, float>(); // "a|b" (sorted) -> seconds near each other
    readonly Dictionary<GummyBody, float> storyAt = new Dictionary<GummyBody, float>();
    bool dirtyVisits, dirtyFriends;
    float nextTogether, nextFriendsSave, nextTell;

    struct Line { public float at; public GummyBody who; public string text; }
    readonly List<Line> lines = new List<Line>();

    public static string FilePath => Path.Combine(Application.persistentDataPath, "windows.txt");

    public int VisitsOf(GummyBody b) { string s = SaveOf(b); return s != null && visits.TryGetValue(s, out int n) ? n : 0; }

    /// <summary>Who it spends the most time with (their name), null: nobody yet.</summary>
    public string BestFriendOf(GummyBody b)
    {
        string me = SaveOf(b);
        if (me == null) return null;
        string best = null;
        float most = 20f; // (a few minutes' company before it counts)
        foreach (var kv in together)
        {
            string[] ab = kv.Key.Split('|');
            if (ab.Length != 2 || kv.Value <= most) continue;
            string other = ab[0] == me ? ab[1] : ab[1] == me ? ab[0] : null;
            if (other == null) continue;
            most = kv.Value;
            best = other;
        }
        return best == null ? null : NameOfSave(best);
    }

    static string SaveOf(GummyBody b)
    {
        var p = b ? b.GetComponent<BuddyProgress>() : null;
        return p ? p.saveName : null;
    }

    static string NameOfSave(string save)
    {
        foreach (var b in GummyBody.All)
        {
            var p = b ? b.GetComponent<BuddyProgress>() : null;
            if (p && p.saveName == save) { var sp = b.GetComponent<BuddySpeech>(); return sp ? sp.Name : save.ToUpperInvariant(); }
        }
        return save.Replace("buddy_", "").ToUpperInvariant();
    }

    // every couple of seconds: everyone near each other (out in the world, awake) gets the time together
    void Together(float dt)
    {
        float now = Time.time;
        if (now < nextTogether) return;
        float step = 2f;
        nextTogether = now + step;
        var all = GummyBody.All;
        for (int i = 0; i < all.Count; i++)
        {
            var a = all[i];
            if (!a || !a.isActiveAndEnabled || a.InRoom || a.IsMonster) continue;
            for (int j = i + 1; j < all.Count; j++)
            {
                var b = all[j];
                if (!b || !b.isActiveAndEnabled || b.InRoom || b.IsMonster) continue;
                if ((a.Center - b.Center).magnitude > 2.5f * Mathf.Max(1f, (a.size + b.size) / 1.2f)) continue;
                string sa = SaveOf(a), sb = SaveOf(b);
                if (sa == null || sb == null || sa == sb) continue;
                string k = string.CompareOrdinal(sa, sb) < 0 ? sa + "|" + sb : sb + "|" + sa;
                together.TryGetValue(k, out float s);
                together[k] = s + step;
                dirtyFriends = true;
            }
        }
        if (dirtyFriends && now >= nextFriendsSave) { nextFriendsSave = now + 60f; Save(); }
    }

    // one that's been in a window tells someone near it; they don't believe it... unless they've been in one too
    void Tell(float now)
    {
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            if (now < lines[i].at) continue;
            var l = lines[i];
            lines.RemoveAt(i);
            var sp = l.who && l.who.isActiveAndEnabled && !l.who.InRoom ? l.who.GetComponent<BuddySpeech>() : null;
            if (sp) sp.Say(l.text, 0f, true);
        }
        if (now < nextTell || lines.Count > 0) return;
        nextTell = now + 1f;
        GummyBody who = null, listener = null, third = null;
        foreach (var kv in storyAt)
        {
            var teller = kv.Key;
            if (!teller || now < kv.Value || !Ready(teller)) continue;
            listener = third = null;
            foreach (var o in GummyBody.All)
            {
                if (o == teller || !Ready(o) || (o.Center - teller.Center).magnitude > 4f) continue;
                if (!listener) listener = o;
                else if (!third || (VisitsOf(o) > 0 && VisitsOf(third) == 0)) third = o;
            }
            if (listener) { who = teller; break; }
        }
        if (!who) return;
        storyAt[who] = now + Random.Range(240f, 600f);
        Story(who, listener, third, now);
        Stories++;
    }

    bool Ready(GummyBody b)
    {
        if (!b || !b.isActiveAndEnabled || b.InRoom || b.IsMonster || b.Held || !b.GetComponent<BuddySpeech>()) return false;
        var br = b.GetComponent<GummyBrain>();
        return !br || (br.Current != GummyBrain.State.Asleep && !br.Towering);
    }

    void Story(GummyBody teller, GummyBody listener, GummyBody third, float now)
    {
        bool believes = VisitsOf(listener) > 0;
        Say(teller, now, Pick("I WAS INSIDE A WINDOW!", "I WENT INTO A WINDOW! FOR REAL!", "GUESS WHERE I WAS. INSIDE A WINDOW!", "THERE'S CANDY INSIDE THE WINDOWS!"));
        Say(teller, now + 2.2f, Pick("IT WAS ALL CANDY IN THERE", "THERE WERE BUTTONS. SO MANY BUTTONS", "THERE'S A RAINBOW IN THERE!", "I SAW THE INSIDE OF THE COMPUTER"));
        if (believes)
        {
            Say(listener, now + 4.4f, Pick("IT'S TRUE! I WAS IN ONE TOO!", "THE CANDY WINDOW! I KNOW!", "THE BUTTONS ARE REAL!", "I BELIEVE YOU! I'VE BEEN THERE!"));
            Say(teller, now + 6.4f, Pick("RIGHT?!", "YOU KNOW!", "WINDOW BUDDIES!"));
            return;
        }
        Say(listener, now + 4.4f, Pick("SURE YOU WERE", "SOUNDS FAKE", "PICS OR IT DIDN'T HAPPEN", "YOU HIT YOUR HEAD", "NOBODY GETS IN THERE", "HAHA. NO."));
        if (third && VisitsOf(third) > 0)
        {
            Say(third, now + 6.4f, Pick("NO, IT'S TRUE! I WAS IN ONE TOO!", "I BELIEVE IT. I'VE BEEN THERE!", "IT'S REAL! THE CANDY!"));
            Say(teller, now + 8.4f, Pick("SEE?!", "THANK YOU!", "TOLD YOU!"));
            Say(listener, now + 10.2f, Pick("...OK MAYBE", "YOU'RE BOTH WEIRD", "HMPH."));
        }
        else
        {
            Say(teller, now + 6.4f, Pick("I'M NOT MAKING IT UP!", "IT HAPPENED!", "ONE DAY YOU'LL SEE!"));
            if (third) Say(third, now + 8.2f, Pick("SURE, SURE", "HAHA", "OK BUDDY"));
        }
    }

    void Say(GummyBody who, float at, string text) => lines.Add(new Line { at = at, who = who, text = text });

    void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            foreach (string raw in File.ReadAllLines(FilePath))
            {
                string[] f = raw.Trim().Split(' ');
                if (f.Length == 3 && f[0] == "visits" && int.TryParse(f[2], out int n)) visits[f[1]] = n;
                else if (f.Length == 3 && f[0] == "together" && float.TryParse(f[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float s)) together[f[1]] = s;
            }
        }
        catch (System.Exception e) { Debug.LogWarning("[WhipIt] Couldn't read " + FilePath + ": " + e.Message); }
    }

    void Save()
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            foreach (var kv in visits) sb.Append("visits " + kv.Key + " " + kv.Value + "\n");
            foreach (var kv in together) sb.Append("together " + kv.Key + " " + kv.Value.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "\n");
            Directory.CreateDirectory(Application.persistentDataPath);
            File.WriteAllText(FilePath, sb.ToString());
            dirtyVisits = dirtyFriends = false;
        }
        catch (System.Exception e) { Debug.LogWarning("[WhipIt] Couldn't write " + FilePath + ": " + e.Message); }
    }

    // ------------------------------------------------------------------ sound

    AudioSource sfx, creak;

    void Play(AudioClip clip, float pitch, float vol)
    {
        if (!clip || !sfx) return;
        sfx.pitch = pitch;
        sfx.PlayOneShot(clip, volume * vol);
    }

    static string Pick(params string[] s) => s[Random.Range(0, s.Length)];
}
