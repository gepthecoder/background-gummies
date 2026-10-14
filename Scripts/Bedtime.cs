using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Day and night, on your real clock. From 21:00 they get sleepy: they yawn and nap sooner. At about 21:40 one of them
/// calls "PILLOW FORT!": pillows pomf down onto a free bit of the floor (a mat, a big pillow at the back, two walls of
/// pillows and a starry blanket over the top), they all come over and fall asleep in a pile inside it by 22:00, the
/// monster too. All night your cursor doesn't wake them; grab one and it wakes up grumpy ("5 MORE MINUTES...") and goes
/// back to bed. Above the pile a dream bubble shows a little picture of something from today (the monster they made,
/// the jam, the cake, the snacks, the rain, the ball, a level up, hide and seek; sheep or your cursor after a quiet day).
/// The first time you use the computer after 06:00 (or at 10:00 anyway) they wake up and stretch and the fort poofs away.
/// Started in the night, they go straight to bed. Chat "pillow fort" / "bedtime" puts them to bed now, "good morning"
/// wakes them (when it's time, or after a bedtime you asked for). Today's moments are kept in dreams.txt.
/// Lives on the Interaction object (WhipIt > Setup).
/// </summary>
[DefaultExecutionOrder(7)] // after BuddyEater (0), the party and the rain shelter (6): at night, bed wins
public class Bedtime : MonoBehaviour
{
    public enum Phase { Day, Evening, Building, Night, Morning }
    public enum Dream { Monster, Music, Cake, Snack, Rain, Ball, Star, Peekaboo, Sheep, Cursor }

    [Header("Your clock (hours, 24 h)")]
    [Tooltip("Yawning from")]
    public float sleepyAt = 21f;
    [Tooltip("The pillow fort goes up at about (give or take a few minutes)")]
    public float fortAt = 21.67f;
    [Tooltip("Asleep by")]
    public float asleepBy = 22f;
    [Tooltip("The first mouse or keyboard after this wakes them")]
    public float wakeFrom = 6f;
    [Tooltip("...or this, whatever comes first")]
    public float wakeBy = 10f;
    [Header("Sounds (WhipIt > Setup assigns them)")]
    public AudioClip pomfClip;
    public AudioClip blanketClip, yawnClip, poofClip;
    [Range(0f, 1f)] public float volume = 0.5f;
    [Tooltip("Sprite material (WhipIt > Setup assigns it).")]
    public Material material;

    public struct Moment { public Dream kind; public string text; public Color color; }

    public static Bedtime Instance { get; private set; }
    /// <summary>Bedtime: the fort's going up or they're asleep in it.</summary>
    public static bool Sleeping => Instance && (Instance.phase == Phase.Building || Instance.phase == Phase.Night);
    public static string FilePath => Path.Combine(Application.persistentDataPath, "dreams.txt");

    public Phase Current => phase;
    /// <summary>Where the fort is (world units; empty when there's none).</summary>
    public Rect Fort => fortRect;
    /// <summary>Today's moments (what they dream about).</summary>
    public IReadOnlyList<Moment> Today => moments;
    /// <summary>The dream showing now (null = none).</summary>
    public string Dreaming => showing ? dreamText : null;
    public int Dreams { get; private set; }
    public int Yawns { get; private set; }
    public int Grumps { get; private set; }
    /// <summary>"Good morning" may wake them all now (it's day, or you asked for this bedtime).</summary>
    public bool CanWakeAll => forced || DayHour(Hour(WorkClock.Now));

    class Bed { public int slot = -1; public bool inBed, said, gaveUp, wasAsleep; public float since, nextYawn = -1f, sleepAfter = -1f, wakeAt = -1f, restAt; }

    readonly Dictionary<GummyBody, Bed> beds = new Dictionary<GummyBody, Bed>();
    readonly List<GummyBody> order = new List<GummyBody>();
    readonly List<Moment> moments = new List<Moment>();
    readonly HashSet<BuddyProgress> heard = new HashSet<BuddyProgress>();
    Phase phase = Phase.Day;
    DateTime dayKey;
    float fortAtToday = -1f;
    DateTime fortDay;
    bool forced, built, packed, morningSaid;
    float phaseAt, nextDream;
    int sheep;
    int lastFusions = -1, lastJams = -1, snacks;
    bool wasParty, wasRain, wasBall, wasHide;
    string lastDream;
    AudioSource audioSrc;

    // the fort: a mat, a pillow at the back, two walls of two pillows, the blanket on top
    const int Pieces = 7;
    readonly SpriteRenderer[] pieceSr = new SpriteRenderer[Pieces];
    readonly Vector2[] pieceAt = new Vector2[Pieces];
    readonly Vector2[] pieceSize = new Vector2[Pieces];
    readonly float[] pieceRot = new float[Pieces], pieceDrop = new float[Pieces], piecePoof = new float[Pieces];
    readonly bool[] pieceLanded = new bool[Pieces], piecePoofed = new bool[Pieces];
    readonly float[] pieceAspect = new float[Pieces]; // width / height of its picture
    Rect fortRect;
    float floorY, innerL, innerR;
    Transform fortRoot, maskRoot;
    readonly List<SpriteMask> masks = new List<SpriteMask>();
    readonly List<Rect> covering = new List<Rect>();
    const int FortBack = -31, FortFront = -25; // its own draw orders (the buddies use 0..60): the windows-in-front masks touch only it

    // the dream bubble
    SpriteRenderer dream, dot1, dot2;
    GummyBody dreamer;
    bool showing;
    string dreamText;
    float dreamT, dreamLen;
    readonly Dictionary<string, Sprite> dreamCache = new Dictionary<string, Sprite>();
    static Sprite s_dotBig, s_dotSmall, s_white;
    static readonly Color32 Fill = new Color32(255, 252, 244, 255), Ink = new Color32(42, 30, 22, 255);

    // ------------------------------------------------------------------ life

    void Awake()
    {
        Instance = this;
        audioSrc = gameObject.AddComponent<AudioSource>();
        audioSrc.playOnAwake = false;
        audioSrc.spatialBlend = 0f;
    }

    void OnEnable() { Instance = this; BuddyEater.AnyAte += OnAte; }

    void OnDisable()
    {
        BuddyEater.AnyAte -= OnAte;
        if (Instance == this) Instance = null;
    }

    void OnDestroy()
    {
        if (fortRoot) Destroy(fortRoot.gameObject);
        if (maskRoot) Destroy(maskRoot.gameObject);
    }

    void Start()
    {
        Load();
        lastFusions = BuddyMonster.Fusions;
        lastJams = BandJam.Jams;
        DateTime now = WorkClock.Now;
        double h = Hour(now);
        if (NightHour(h, now)) StartBuilding(true); // (started in the night: straight to bed)
        else phase = h >= sleepyAt ? Phase.Evening : Phase.Day;
    }

    static double Hour(DateTime t) => t.Hour + t.Minute / 60.0 + t.Second / 3600.0;
    static DateTime DayOf(DateTime t) => t.AddHours(-6).Date; // a day runs from 06:00 to 06:00

    float FortHour(DateTime now)
    {
        DateTime d = DayOf(now);
        if (fortAtToday < 0f || d != fortDay) { fortDay = d; fortAtToday = fortAt + UnityEngine.Random.Range(-0.08f, 0.12f); }
        return fortAtToday;
    }

    bool NightHour(double h, DateTime now) => h >= FortHour(now) || h < wakeFrom;
    bool DayHour(double h) => h >= wakeFrom && h < sleepyAt;

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("Bed.Update", t0); }
    void UpdateTimed()
    {
        float now = Time.time;
        DateTime clock = WorkClock.Now;
        double h = Hour(clock);
        Collect();
        switch (phase)
        {
            case Phase.Day:
            case Phase.Evening:
                if (NightHour(h, clock)) { StartBuilding(false); break; }
                if (h >= sleepyAt) { phase = Phase.Evening; Sleepy(now); }
                else { if (phase == Phase.Evening) RestoreNaps(); phase = Phase.Day; }
                break;
            case Phase.Building:
                Build(now);
                Beds(now);
                if (built && (AllInBed() || now - phaseAt > 90f)) { phase = Phase.Night; phaseAt = now; nextDream = now + UnityEngine.Random.Range(4f, 8f); }
                break;
            case Phase.Night:
                Beds(now);
                DreamStep(now);
                if (forced && NightHour(h, clock)) forced = false; // (your early bedtime ran into the real night: it ends in the morning)
                if (!forced && DayHour(h) && (Busy() || h >= wakeBy)) Morning();
                break;
            case Phase.Morning:
                Wake(now);
                break;
        }
        UpdatePieces(now);
        UpdateMasks();
        UpdateDream(now);
    }

    // you're at the computer (moved the mouse or typed in the last few seconds)
    static bool Busy()
    {
        float idle = WorkClock.IdleProbe != null ? WorkClock.IdleProbe() : DesktopInput.IdleSeconds;
        return idle < 3f;
    }

    /// <summary>Bedtime now (chat "pillow fort", the menu): the fort goes up and they go to sleep, until "good morning".</summary>
    public void Night()
    {
        if (phase == Phase.Building || phase == Phase.Night) return;
        forced = true;
        StartBuilding(false);
    }

    /// <summary>Good morning: they wake up, stretch, and the fort poofs away.</summary>
    public void Morning()
    {
        if (phase != Phase.Building && phase != Phase.Night) return;
        phase = Phase.Morning;
        phaseAt = Time.time;
        forced = false;
        morningSaid = false;
        if (dreamer) showing = false;
        int i = 0;
        foreach (var b in Bodies())
        {
            var bed = BedOf(b);
            bed.wakeAt = Time.time + 0.3f + 0.45f * i++ + UnityEngine.Random.Range(0f, 0.3f);
        }
    }

    // ------------------------------------------------------------------ evening: sleepy

    void Sleepy(float now)
    {
        foreach (var b in Bodies())
        {
            var bed = BedOf(b);
            var br = b.GetComponent<GummyBrain>();
            if (bed.sleepAfter < 0f) { bed.sleepAfter = br.sleepAfter; br.sleepAfter = Mathf.Min(br.sleepAfter, 25f); } // (naps sooner)
            if (bed.nextYawn < 0f) bed.nextYawn = now + UnityEngine.Random.Range(20f, 120f);
            if (now < bed.nextYawn) continue;
            if (br.Current != GummyBrain.State.Idle || !b.Grounded || b.Held) { bed.nextYawn = now + 5f; continue; } // (once it's sitting about)
            bed.nextYawn = now + UnityEngine.Random.Range(90f, 200f);
            Yawn(b, UnityEngine.Random.value < 0.45f);
        }
    }

    void Yawn(GummyBody b, bool say)
    {
        var br = b.GetComponent<GummyBrain>();
        if (br) br.Act(new Vector2(0.86f, 1.17f), 1.2f); // a big stretch up
        Play(yawnClip, 0.8f, Pitch(b), b.Center.x);
        Yawns++;
        if (say) Say(b, Line(b, "*YAWN* SLEEPY...", "NOT TIRED... *YAWN*", "DECAF... HOURS...", "BUN... SO... HEAVY...", "RAWR... *YAWN*", "QUACK... *YAWN*", "WE ARE... SLEEPY"));
    }

    void RestoreNaps()
    {
        foreach (var kv in beds)
        {
            if (!kv.Key || kv.Value.sleepAfter < 0f) continue;
            var br = kv.Key.GetComponent<GummyBrain>();
            if (br) br.sleepAfter = kv.Value.sleepAfter;
            kv.Value.sleepAfter = -1f;
            kv.Value.nextYawn = -1f;
        }
    }

    // ------------------------------------------------------------------ the fort

    void StartBuilding(bool late)
    {
        phase = Phase.Building;
        phaseAt = Time.time;
        built = packed = false;
        PlaceFort();
        float t = Time.time + (late ? 0.3f : 1f);
        float[] gap = { 0f, 0.7f, 1.5f, 2.1f, 2.9f, 3.5f, 4.6f };
        for (int i = 0; i < Pieces; i++) { pieceDrop[i] = t + gap[i]; pieceLanded[i] = piecePoofed[i] = false; piecePoof[i] = -1f; }
        var who = Bodies();
        GummyBody first = null;
        foreach (var b in who) if (!first || Voice(b) == BuddySpeech.Voice.Apple) first = b;
        if (first) Say(first, late ? Line(first, "BEDTIME! PILLOW FORT!", "LATE! FORT! NOW!", "ZZZ... FORT FIRST", "BUN TIME. BED TIME.", "RAWR! FORT!", "QUACK! BEDTIME!", "WE BUILD. A FORT.")
                                   : Line(first, "PILLOW FORT!", "FORT TIME!", "BLANKET FORT, GO!", "PILLOWS, PLEASE!", "RAWR! PILLOW FORT!", "QUACK! FORT!", "WE WANT. A FORT."));
        int k = 0;
        foreach (var b in who)
        {
            var bed = BedOf(b);
            bed.inBed = bed.said = bed.gaveUp = bed.wasAsleep = false;
            bed.since = Time.time;
            var br = b.GetComponent<GummyBrain>();
            if (br.Current == GummyBrain.State.Asleep && !late) br.WakeUp(); // (napping somewhere: up, to the fort)
            if (b != first && UnityEngine.Random.value < 0.5f && k++ < 2)
                Say(b, Line(b, "I CALL THE MIDDLE!", "BLANKETS!", "COZY MODE!", "BUN PILLOW!", "RAWR! COZY!", "SNUGGLE! QUACK!", "WE ARE. THE PILLOW."), 1.2f + k);
        }
    }

    // a free bit of floor (no windows over it), near them, wide enough for all of them side by side
    void PlaceFort()
    {
        Rect r = ScreenBounds.BusiestRect; // (more than one screen: on the one most of them are on)
        floorY = r.yMin;
        var who = Bodies();
        float inner = 0.3f, tall = 0.6f, sx = 0f;
        foreach (var b in who) { float w = Mathf.Max(b.Extent.width, b.size); inner += w * 1.08f; tall = Mathf.Max(tall, Mathf.Max(b.Extent.height, b.size)); sx += b.Center.x; } // (lying down they're wider)
        float s = Mathf.Clamp(tall / 0.6f, 0.9f, 1.8f);
        float wall = 0.7f * s, pillowH = Mathf.Max(0.44f * s, (tall + 0.3f) / 1.8f);
        inner = Mathf.Min(inner, r.width - 2f * wall - 0.4f);
        float W = inner + 2f * wall, H = 2f * pillowH * 0.95f + 0.4f * s;
        float want = who.Count > 0 ? sx / who.Count : r.center.x;
        float best = float.MaxValue, cx = r.center.x;
        for (float x = r.xMin + W * 0.5f + 0.2f; x <= r.xMax - W * 0.5f - 0.2f; x += 0.25f)
        {
            int cover = 0;
            for (int i = 0; i <= 4; i++)
                for (int j = 0; j <= 2; j++)
                {
                    Vector2 p = new Vector2(x - W * 0.5f + W * i / 4f, floorY + 0.05f + (H - 0.1f) * j / 2f);
                    foreach (var w in DesktopPlatforms.Windows) if (w.Contains(p)) { cover++; break; }
                }
            float score = cover * 10f + Mathf.Abs(x - want) * 0.3f;
            if (score < best) { best = score; cx = x; }
        }
        fortRect = new Rect(cx - W * 0.5f, floorY, W, H);
        innerL = fortRect.xMin + wall;
        innerR = fortRect.xMax - wall;

        // the pieces: where, how big
        float roofH = 0.5f * s;
        Set(0, new Vector2(cx, floorY + 0.06f * s), new Vector2(W - 0.1f, 0.14f * s), 0f);                                  // the mat
        Set(1, new Vector2(cx, floorY + 0.36f * s), new Vector2(inner * 0.94f, 0.62f * s), 0f);                             // the big pillow at the back
        Set(2, new Vector2(fortRect.xMin + wall * 0.5f, floorY + pillowH * 0.5f), new Vector2(wall, pillowH), -4f);           // left wall
        Set(3, new Vector2(fortRect.xMax - wall * 0.5f, floorY + pillowH * 0.5f), new Vector2(wall, pillowH), 3f);            // right wall
        Set(4, new Vector2(fortRect.xMin + wall * 0.52f, floorY + pillowH * 1.42f), new Vector2(wall * 0.96f, pillowH * 0.98f), 5f);
        Set(5, new Vector2(fortRect.xMax - wall * 0.48f, floorY + pillowH * 1.42f), new Vector2(wall * 0.96f, pillowH * 0.98f), -6f);
        float wallTop = floorY + pillowH * 1.42f + pillowH * 0.98f * 0.42f;
        Set(6, new Vector2(cx, wallTop - 0.04f - roofH * 0.146f), new Vector2(W + 0.2f * s, roofH), 0f);                     // the blanket on top, resting on the walls
        Pictures();
        int slot = 0;
        order.Clear();
        order.AddRange(who);
        order.Sort((a, b) => b.size.CompareTo(a.size));
        // the biggest in the middle, the others either side
        var placed = new GummyBody[order.Count];
        int mid = order.Count / 2, left = mid - 1, right = mid + 1;
        if (order.Count > 0) placed[mid] = order[0];
        for (int i = 1; i < order.Count; i++)
        {
            if ((i % 2 == 1 && left >= 0) || right >= order.Count) placed[left--] = order[i];
            else placed[right++] = order[i];
        }
        order.Clear();
        foreach (var b in placed) if (b) { order.Add(b); BedOf(b).slot = slot++; }
    }

    void Set(int i, Vector2 at, Vector2 size, float rot) { pieceAt[i] = at; pieceSize[i] = size; pieceRot[i] = rot; }

    // where its spot in the fort is
    Vector2 SlotOf(GummyBody b, Bed bed)
    {
        if (bed.slot < 0) { bed.slot = order.Count; order.Add(b); }
        float total = 0f, x = 0f;
        foreach (var o in order) total += o ? Mathf.Max(o.Extent.width, o.size) * 0.95f : 0f;
        float start = 0.5f * (innerL + innerR) - total * 0.5f;
        foreach (var o in order)
        {
            if (!o) continue;
            float w = Mathf.Max(o.Extent.width, o.size) * 0.95f;
            if (o == b) { x = start + w * 0.5f; break; }
            start += w;
        }
        return new Vector2(Mathf.Clamp(x, fortRect.xMin + 0.2f, fortRect.xMax - 0.2f), floorY + b.size * 0.3f);
    }

    void Build(float now)
    {
        bool all = true;
        for (int i = 0; i < Pieces; i++)
        {
            if (pieceLanded[i]) continue;
            all = false;
            if (now >= pieceDrop[i] + 0.35f)
            {
                pieceLanded[i] = true;
                if (i == Pieces - 1) Play(blanketClip, 0.9f, UnityEngine.Random.Range(0.95f, 1.05f), pieceAt[i].x);
                else Play(pomfClip, 0.8f, UnityEngine.Random.Range(0.9f, 1.15f), pieceAt[i].x);
            }
        }
        built = all;
    }

    // everyone to bed: over to its spot and lie down; grabbed: up again, grumpy, and back to bed after
    void Beds(float now)
    {
        foreach (var b in Bodies())
        {
            var bed = BedOf(b);
            var br = b.GetComponent<GummyBrain>();
            br.DeepSleep = true;
            bool asleep = br.Current == GummyBrain.State.Asleep;
            if (b.Held)
            {
                if (bed.wasAsleep || bed.inBed)
                {
                    Grumps++;
                    br.Grumpy(6f);
                    Say(b, Line(b, "5 MORE MINUTES...", "IT'S NIGHT!", "ZZZ... WHAT?! NO.", "LET ME SLEEP!", "RAWR... IT'S DARK", "QUACK... NO.", "WE SLEEP. GO AWAY"));
                }
                bed.inBed = bed.wasAsleep = bed.gaveUp = false;
                bed.since = now;
                continue;
            }
            if (bed.wasAsleep && !asleep) { bed.restAt = now + 4f; bed.since = now; bed.inBed = false; } // (woken up some other way: up for a moment, then back to sleep)
            bed.wasAsleep = asleep;
            if (asleep) continue;
            if (bed.gaveUp) { if (b.Grounded && br.Current == GummyBrain.State.Idle && now >= bed.restAt) br.NapNow(); continue; } // (can't get there: it sleeps where it is)
            if (Distracted(b)) { bed.since = now; continue; }
            Vector2 slot = SlotOf(b, bed);
            // there: by its spot, on the floor or snuggled on top of a friend (it's a pile), inside the fort
            bool inside = b.Center.x > fortRect.xMin + 0.1f && b.Center.x < fortRect.xMax - 0.1f;
            bool there = built && inside && b.Grounded && (Mathf.Abs(b.Center.x - slot.x) < 0.15f + 0.3f * b.size || (b.SupportBody && Mathf.Abs(b.Center.x - slot.x) < 0.3f + 0.6f * b.size))
                         && (b.Extent.yMin < floorY + 0.3f || b.SupportBody);
            if (there && br.Current != GummyBrain.State.Hopping && now >= bed.restAt)
            {
                br.Food = null;
                br.NapNow();
                bed.inBed = bed.wasAsleep = true;
                if (!bed.said) { bed.said = true; Say(b, Line(b, "NIGHT NIGHT!", "GOOD NIGHT!", "DECAF DREAMS...", "SWEET BUN DREAMS", "RAWR... NIGHT", "NIGHT, QUACK...", "GOOD NIGHT. RAWR.")); }
                else if (UnityEngine.Random.value < 0.4f) Say(b, "*MUMBLE*");
                continue;
            }
            if (!built && Mathf.Abs(b.Center.x - slot.x) < 0.8f) { br.Food = null; br.Linger(1f); continue; } // (waits by the fort till it's up)
            br.Food = slot;
            br.FoodSurface = floorY;
            if (now - bed.since > (inside ? 25f : 60f)) { bed.gaveUp = true; br.Food = null; } // (can't get there: it sleeps where it is)
        }
    }

    // busy with something it shouldn't be pulled away from (a snack, a jam you asked for, a game, a chat)
    static bool Distracted(GummyBody b)
    {
        var eater = b.GetComponent<BuddyEater>();
        if (eater && eater.Target) return true;
        if (BandJam.Current && BandJam.Current.Plays(b)) return true;
        if (BuddyHideSeek.Playing || LevelUpParty.Current) return true;
        return BuddyChat.Open && BuddyChat.Target == b;
    }

    bool AllInBed()
    {
        foreach (var b in Bodies())
        {
            var bed = BedOf(b);
            if (!bed.inBed && !bed.gaveUp && b.GetComponent<GummyBrain>().Current != GummyBrain.State.Asleep) return false;
        }
        return true;
    }

    // ------------------------------------------------------------------ morning

    void Wake(float now)
    {
        bool anyLeft = false;
        foreach (var b in Bodies())
        {
            var bed = BedOf(b);
            var br = b.GetComponent<GummyBrain>();
            br.DeepSleep = false;
            if (bed.wakeAt < 0f) continue;
            if (now < bed.wakeAt) { anyLeft = true; continue; }
            bed.wakeAt = -1f;
            br.Food = null;
            br.WakeUp();
            br.Act(new Vector2(0.84f, 1.2f), 1.4f); // a big stretch
            if (UnityEngine.Random.value < 0.5f) Play(yawnClip, 0.6f, Pitch(b), b.Center.x);
            if (!morningSaid)
            {
                morningSaid = true; // (the first one up tells you what it dreamt)
                Say(b, lastDream != null ? "I DREAMT OF " + lastDream.TrimEnd('.', '!') + "!" : Line(b, "BEST SLEEP EVER!", "SO COMFY!", "FULLY BREWED!", "FRESH BUN!", "RAWR! RESTED!", "QUACK! MORNING!", "WE ARE. AWAKE."), 1.6f); // (after its first sleepy word)
            }
            else if (UnityEngine.Random.value < 0.35f)
                Say(b, Line(b, "*STRETCH*", "MORNING ALREADY?", "COFFEE TIME!", "BUN-JOUR!", "RAWR... MORNING", "QUACK! SUN'S UP!", "WE SLEPT. WELL."), 1.6f);
        }
        // the fort poofs away, piece by piece
        if (!packed && now - phaseAt > 2.5f)
        {
            packed = true;
            for (int i = Pieces - 1; i >= 0; i--) piecePoof[i] = now + 0.22f * (Pieces - 1 - i);
        }
        if (packed && !anyLeft && now - phaseAt > 2.5f + 0.22f * Pieces + 0.6f)
        {
            phase = Phase.Day;
            fortRect = new Rect();
            RestoreNaps();
            foreach (var kv in beds) { kv.Value.inBed = kv.Value.said = kv.Value.gaveUp = false; kv.Value.slot = -1; }
            order.Clear();
            // a new day: new things to dream about
            moments.Clear();
            snacks = 0;
            dayKey = DayOf(WorkClock.Now);
            Save();
        }
    }

    // ------------------------------------------------------------------ today's moments

    void OnAte(BuddyEater e, Snack s)
    {
        if (Sleeping) return;
        snacks++;
        if (snacks >= 3) Add(Dream.Snack, snacks + " SNACKS...", new Color(0.85f, 0.62f, 0.36f));
    }

    void Collect()
    {
        if (Sleeping || phase == Phase.Morning) return;
        if (BuddyMonster.Fusions > lastFusions)
        {
            lastFusions = BuddyMonster.Fusions;
            var all = BuddyMonster.All;
            if (all.Count > 0 && all[all.Count - 1]) { var m = all[all.Count - 1]; Add(Dream.Monster, m.MonsterName + "...", m.GetComponent<GummyBody>().color); }
        }
        if (BandJam.Jams > lastJams)
        {
            lastJams = BandJam.Jams;
            Add(Dream.Music, BandJam.Current && BandJam.Current.PlaysYourTune ? "YOUR SONG..." : "LA LA LA...", new Color(0.55f, 0.4f, 0.85f));
        }
        bool party = LevelUpParty.Current, rain = Rain.Current && Rain.Current.Raining, ball = FetchBall.Current || Basketball.Current, hide = BuddyHideSeek.Playing;
        if (party && !wasParty) Add(Dream.Cake, "CAKE...", new Color(0.97f, 0.62f, 0.75f));
        if (rain && !wasRain) Add(Dream.Rain, "SPLASH...", new Color(0.4f, 0.6f, 0.95f));
        if (ball && !wasBall) Add(Dream.Ball, "BALL!...", new Color(0.85f, 0.92f, 0.3f));
        if (hide && !wasHide) Add(Dream.Peekaboo, "PEEKABOO...", new Color(0.9f, 0.25f, 0.2f));
        wasParty = party; wasRain = rain; wasBall = ball; wasHide = hide;
        foreach (var b in GummyBody.All)
        {
            var p = b ? b.GetComponent<BuddyProgress>() : null;
            if (!p || heard.Contains(p)) continue;
            heard.Add(p);
            p.LeveledUp += (lv, stage) => { if (!Sleeping) Add(Dream.Star, "LEVEL " + lv + "!", new Color(1f, 0.85f, 0.25f)); };
        }
    }

    void Add(Dream kind, string text, Color color)
    {
        for (int i = 0; i < moments.Count; i++) if (moments[i].kind == kind) { moments.RemoveAt(i); break; }
        moments.Add(new Moment { kind = kind, text = text, color = color });
        while (moments.Count > 10) moments.RemoveAt(0);
        Save();
    }

    void Save()
    {
        try
        {
            var lines = new List<string> { DayOf(WorkClock.Now).ToString("yyyy-MM-dd") };
            foreach (var m in moments) lines.Add((int)m.kind + "|" + m.text + "|" + m.color.r.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + "," + m.color.g.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + "," + m.color.b.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(Application.persistentDataPath);
            File.WriteAllLines(FilePath, lines.ToArray());
        }
        catch (Exception e) { Debug.LogWarning("[WhipIt] Couldn't save dreams: " + e.Message); }
    }

    void Load()
    {
        dayKey = DayOf(WorkClock.Now);
        try
        {
            if (!File.Exists(FilePath)) return;
            string[] lines = File.ReadAllLines(FilePath);
            if (lines.Length == 0) return;
            // today's (or last night's, while it's still night)
            if (lines[0].Trim() != dayKey.ToString("yyyy-MM-dd") && lines[0].Trim() != dayKey.AddDays(-1).ToString("yyyy-MM-dd")) return;
            if (lines[0].Trim() != dayKey.ToString("yyyy-MM-dd") && !NightHour(Hour(WorkClock.Now), WorkClock.Now)) return;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            for (int i = 1; i < lines.Length; i++)
            {
                string[] p = lines[i].Split('|');
                if (p.Length < 3 || !int.TryParse(p[0], out int k)) continue;
                string[] c = p[2].Split(',');
                Color col = Color.white;
                if (c.Length == 3 && float.TryParse(c[0], System.Globalization.NumberStyles.Float, inv, out float r) && float.TryParse(c[1], System.Globalization.NumberStyles.Float, inv, out float g) && float.TryParse(c[2], System.Globalization.NumberStyles.Float, inv, out float bl)) col = new Color(r, g, bl);
                moments.Add(new Moment { kind = (Dream)k, text = p[1], color = col });
            }
        }
        catch (Exception e) { Debug.LogWarning("[WhipIt] Couldn't read dreams: " + e.Message); }
    }

    // ------------------------------------------------------------------ dreams

    void DreamStep(float now)
    {
        if (showing || now < nextDream) return;
        var sleepers = new List<GummyBody>();
        foreach (var b in Bodies()) if (BedOf(b).inBed && b.GetComponent<GummyBrain>().Current == GummyBrain.State.Asleep) sleepers.Add(b);
        if (sleepers.Count == 0) { nextDream = now + 3f; return; }
        dreamer = sleepers[UnityEngine.Random.Range(0, sleepers.Count)];
        Moment m;
        if (moments.Count > 0) m = moments[UnityEngine.Random.Range(0, moments.Count)];
        else if (UnityEngine.Random.value < 0.65f) { sheep++; m = new Moment { kind = Dream.Sheep, text = sheep + " SHEEP...", color = Color.white }; }
        else m = new Moment { kind = Dream.Cursor, text = "YOU...", color = Color.white };
        dreamText = m.text;
        if (m.kind != Dream.Sheep && m.kind != Dream.Cursor) lastDream = m.text;
        dream.sprite = DreamSprite(m);
        if (material) SpriteMats.Fit(dream, material);
        showing = true;
        dreamT = now;
        dreamLen = UnityEngine.Random.Range(6f, 8f);
        Dreams++;
        nextDream = now + dreamLen + UnityEngine.Random.Range(5f, 11f);
    }

    void UpdateDream(float now)
    {
        if (!dream) return;
        float u = now - dreamT;
        bool live = showing && phase == Phase.Night && dreamer && dreamer.isActiveAndEnabled && u <= dreamLen && dreamer.GetComponent<GummyBrain>().Current == GummyBrain.State.Asleep;
        if (!live)
        {
            showing = false;
            if (dream.enabled) dream.enabled = dot1.enabled = dot2.enabled = false;
            return;
        }
        float a = Mathf.Clamp01(u / 0.6f) * Mathf.Clamp01((dreamLen - u) / 0.8f);
        float px = PixelSize();
        Rect r = ScreenBounds.ScreenRectAt(dreamer.Center); // (its own screen)
        Sprite sp = dream.sprite;
        float w = sp ? sp.rect.width * px : 1f, hgt = sp ? sp.rect.height * px : 1f;
        Vector2 head = new Vector2(dreamer.Center.x, dreamer.Extent.yMax);
        float side = head.x > r.center.x ? -1f : 1f;
        Vector2 at = head + new Vector2(side * (0.3f + w * 0.3f), 0.55f + 0.04f * Mathf.Sin(now * 1.3f));
        at.x = Mathf.Clamp(at.x, r.xMin + w * 0.5f + px, r.xMax - w * 0.5f - px);
        at.y = Mathf.Min(at.y, r.yMax - hgt - px);
        dream.transform.position = Snap(new Vector2(at.x - w * 0.5f, at.y), px);
        dream.transform.localScale = new Vector3(px, px, 1f);
        Vector2 d1 = Vector2.Lerp(head + new Vector2(side * 0.08f, 0.1f), at, 0.33f), d2 = Vector2.Lerp(head + new Vector2(side * 0.08f, 0.1f), at, 0.66f);
        dot1.transform.position = Snap(d1, px);
        dot2.transform.position = Snap(d2, px);
        dot1.transform.localScale = dot2.transform.localScale = new Vector3(px, px, 1f);
        bool hid = Occlusion.Hidden(at + new Vector2(0f, hgt * 0.5f), Occlusion.Back);
        var c = new Color(1f, 1f, 1f, hid ? 0f : a);
        dream.color = c;
        dot1.color = new Color(1f, 1f, 1f, hid ? 0f : Mathf.Clamp01(u / 0.3f) * Mathf.Clamp01((dreamLen - u) / 0.8f));
        dot2.color = new Color(1f, 1f, 1f, hid ? 0f : Mathf.Clamp01((u - 0.15f) / 0.3f) * Mathf.Clamp01((dreamLen - u) / 0.8f));
        dream.enabled = dot1.enabled = dot2.enabled = true;
    }

    // the size of one pixel of the pixel font, like the speech bubbles
    float PixelSize()
    {
        var cam = Camera.main;
        if (!cam || Screen.height <= 0) return 0.028f;
        float screenPx = 2f * cam.orthographicSize / Screen.height;
        int scale = 3;
        if (dreamer) { var sp = dreamer.GetComponent<BuddySpeech>(); if (sp) scale = sp.pixelScale1080; }
        return screenPx * Mathf.Max(1, Mathf.RoundToInt(scale * ScreenBounds.MainScreenPixels / 1080f));
    }

    static Vector3 Snap(Vector2 p, float px)
    {
        var cam = Camera.main;
        if (!cam || Screen.height <= 0) return new Vector3(p.x, p.y, 0f);
        float screenPx = 2f * cam.orthographicSize / Screen.height;
        Vector2 o = (Vector2)cam.transform.position - new Vector2(cam.orthographicSize * Screen.width / Screen.height, cam.orthographicSize);
        Vector2 d = p - o;
        return new Vector3(o.x + Mathf.Round(d.x / screenPx) * screenPx, o.y + Mathf.Round(d.y / screenPx) * screenPx, 0f);
    }

    /// <summary>What it mumbles when you chat with it in its sleep.</summary>
    public string SleepTalk(BuddySpeech.Voice v)
    {
        if (moments.Count > 0 && UnityEngine.Random.value < 0.7f) return "ZZZ... " + moments[UnityEngine.Random.Range(0, moments.Count)].text;
        switch (v)
        {
            case BuddySpeech.Voice.Bean: return "ZZZ... DECAF... ZZZ";
            case BuddySpeech.Voice.Duck: return "QUAAA... ZZZ";
            case BuddySpeech.Voice.Dino: return "RAWR... ZZZ";
            case BuddySpeech.Voice.Monster: return "WE... ZZZ...";
            default: return UnityEngine.Random.value < 0.5f ? "ZZZ..." : "MMM... 5 MORE MINUTES...";
        }
    }

    // ------------------------------------------------------------------ the pieces on screen

    void UpdatePieces(float now)
    {
        bool up = phase == Phase.Building || phase == Phase.Night || (phase == Phase.Morning);
        if (!fortRoot && !up) return;
        if (!fortRoot) Pictures();
        for (int i = 0; i < Pieces; i++)
        {
            var sr = pieceSr[i];
            if (!sr) continue;
            bool show = up && now >= pieceDrop[i] && fortRect.width > 0f;
            float poof = piecePoof[i] >= 0f ? (now - piecePoof[i]) / 0.3f : -1f;
            if (poof >= 1f) show = false;
            if (!show) { if (sr.enabled) sr.enabled = false; continue; }
            if (poof >= 0f && !sr.enabled) continue;
            float u = Mathf.Clamp01((now - pieceDrop[i]) / 0.35f);
            Vector2 at = pieceAt[i] + new Vector2(0f, 2.6f * (1f - u) * (1f - u));
            float since = now - pieceDrop[i] - 0.35f;
            float squash = since > 0f ? 0.2f * Mathf.Exp(-since * 7f) * Mathf.Cos(since * 22f) : 0f; // pomf
            if (i == Pieces - 1 && since > 0f) squash *= 1.4f;
            Vector2 sz = pieceSize[i];
            Vector3 scale = new Vector3(sz.x * (1f + squash * 0.5f), sz.y * (1f - squash), 1f);
            Vector3 drawn = new Vector3(scale.x, scale.y / Mathf.Max(pieceAspect[i], 0.01f), 1f); // (the picture is 1 unit wide, aspect units tall)
            float alpha = 1f;
            if (poof >= 0f)
            {
                if (!piecePoofed[i]) { piecePoofed[i] = true; Play(poofClip, 0.5f, UnityEngine.Random.Range(1.1f, 1.3f), at.x); }
                float k = 1f + 0.25f * Mathf.Clamp01(poof);
                drawn = new Vector3(drawn.x * k, drawn.y * k, 1f);
                alpha = 1f - Mathf.Clamp01(poof);
            }
            sr.transform.position = new Vector3(at.x, at.y - (sz.y - scale.y) * 0.5f, 0f);
            sr.transform.localScale = drawn;
            sr.transform.rotation = Quaternion.Euler(0f, 0f, pieceRot[i]);
            sr.color = new Color(1f, 1f, 1f, alpha);
            sr.enabled = true;
        }
    }

    // windows in front of the desktop hide the bits of the fort they cover
    void UpdateMasks()
    {
        covering.Clear();
        bool up = fortRect.width > 0f && fortRoot;
        if (up)
        {
            Rect box = Rect.MinMaxRect(fortRect.xMin - 0.3f, fortRect.yMin - 0.1f, fortRect.xMax + 0.3f, fortRect.yMax + 0.5f);
            Occlusion.Occluders(Occlusion.Back, box, covering, 8);
        }
        if (covering.Count > 0 && !maskRoot) maskRoot = new GameObject("Pillow Fort Occlusion").transform;
        while (masks.Count < covering.Count) masks.Add(MakeMask());
        for (int i = 0; i < masks.Count; i++)
        {
            var m = masks[i];
            bool on = i < covering.Count;
            if (m.enabled != on) m.enabled = on;
            if (!on) continue;
            Rect r = covering[i];
            m.transform.position = new Vector3(r.center.x, r.center.y, 0f);
            m.transform.localScale = new Vector3(r.width, r.height, 1f);
        }
        var mode = covering.Count > 0 ? SpriteMaskInteraction.VisibleOutsideMask : SpriteMaskInteraction.None;
        foreach (var sr in pieceSr) if (sr && sr.maskInteraction != mode) sr.maskInteraction = mode;
    }

    SpriteMask MakeMask()
    {
        var go = new GameObject("Window In Front");
        go.transform.SetParent(maskRoot, false);
        var m = go.AddComponent<SpriteMask>();
        m.sprite = White();
        m.isCustomRangeActive = true;
        m.backSortingOrder = FortBack;
        m.frontSortingOrder = FortFront;
        m.enabled = false;
        return m;
    }

    // the pillows, the mat and the blanket for this fort's size; the dream bubble
    void Pictures()
    {
        if (!fortRoot)
        {
            fortRoot = new GameObject("Pillow Fort").transform;
            string[] names = { "Mat", "Back Pillow", "Pillow", "Pillow", "Pillow", "Pillow", "Blanket" };
            int[] orders = { -30, -29, -28, -28, -27, -27, -26 };
            for (int i = 0; i < Pieces; i++)
            {
                var g = new GameObject(names[i]);
                g.transform.SetParent(fortRoot, false);
                var sr = g.AddComponent<SpriteRenderer>();
                sr.sortingOrder = orders[i];
                sr.enabled = false;
                pieceSr[i] = sr;
            }
            dream = MakeSr("Dream", 96);
            dot1 = MakeSr("Dream Dot", 96);
            dot2 = MakeSr("Dream Dot", 96);
            if (!s_dotSmall) { s_dotSmall = Dot(2); s_dotBig = Dot(3); }
            dot1.sprite = s_dotSmall;
            dot2.sprite = s_dotBig;
            if (material) { SpriteMats.Fit(dot1, material); SpriteMats.Fit(dot2, material); }
        }
        int pick = UnityEngine.Random.Range(0, PillowColors.Length);
        for (int i = 0; i < Pieces; i++)
        {
            Sprite sp = i == 0 ? MatSprite() : i == Pieces - 1 ? BlanketSprite() : PillowSprite((pick + i) % PillowColors.Length, i == 1);
            pieceAspect[i] = sp ? sp.rect.height / Mathf.Max(sp.rect.width, 1f) : 1f;
            pieceSr[i].sprite = sp;
            if (material) { pieceSr[i].sharedMaterial = material; SpriteMats.Fit(pieceSr[i], material); }
        }
    }

    SpriteRenderer MakeSr(string n, int orderIn)
    {
        var g = new GameObject(n);
        g.transform.SetParent(fortRoot, false);
        var sr = g.AddComponent<SpriteRenderer>();
        sr.sortingOrder = orderIn;
        if (material) sr.sharedMaterial = material;
        sr.enabled = false;
        return sr;
    }

    // ------------------------------------------------------------------ helpers

    List<GummyBody> Bodies()
    {
        var list = new List<GummyBody>();
        foreach (var b in GummyBody.All) if (b && b.isActiveAndEnabled && !b.InRoom && b.GetComponent<GummyBrain>()) list.Add(b); // (not one inside a window)
        return list;
    }

    Bed BedOf(GummyBody b)
    {
        if (!beds.TryGetValue(b, out Bed bed)) { bed = new Bed { since = Time.time }; beds[b] = bed; }
        return bed;
    }

    static BuddySpeech.Voice Voice(GummyBody b)
    {
        var sp = b ? b.GetComponent<BuddySpeech>() : null;
        return sp ? sp.voice : BuddySpeech.Voice.Apple;
    }

    static string Line(GummyBody b, string apple, string tomato, string bean, string burger, string dino, string duck, string monster)
    {
        switch (Voice(b))
        {
            case BuddySpeech.Voice.Tomato: return tomato;
            case BuddySpeech.Voice.Bean: return bean;
            case BuddySpeech.Voice.Burger: return burger;
            case BuddySpeech.Voice.Dino: return dino;
            case BuddySpeech.Voice.Duck: return duck;
            case BuddySpeech.Voice.Monster: return monster;
            default: return apple;
        }
    }

    readonly List<(float at, GummyBody who, string text)> later = new List<(float, GummyBody, string)>();

    void Say(GummyBody b, string text, float delay = 0f)
    {
        if (!b || string.IsNullOrEmpty(text)) return;
        var sp = b.GetComponent<BuddySpeech>();
        if (!sp) return;
        if (delay <= 0f) { sp.Say(text, 0f, true); return; }
        later.Add((Time.time + delay, b, text));
    }

    void LateUpdate() { long t0 = WhipProf.T(); LateUpdateTimed(); WhipProf.Add("Bed.LateUpdate", t0); }
    void LateUpdateTimed()
    {
        for (int i = later.Count - 1; i >= 0; i--)
        {
            if (Time.time < later[i].at) continue;
            var l = later[i];
            later.RemoveAt(i);
            var sp = l.who ? l.who.GetComponent<BuddySpeech>() : null;
            if (sp && l.who.isActiveAndEnabled) sp.Say(l.text, 0f, true);
        }
    }

    static float Pitch(GummyBody b)
    {
        switch (Voice(b))
        {
            case BuddySpeech.Voice.Tomato: return 1.2f;
            case BuddySpeech.Voice.Bean: return 1.35f;
            case BuddySpeech.Voice.Burger: return 0.8f;
            case BuddySpeech.Voice.Dino: return 1.1f;
            case BuddySpeech.Voice.Duck: return 1.4f;
            case BuddySpeech.Voice.Monster: return 0.6f;
            default: return 1f;
        }
    }

    void Play(AudioClip clip, float vol, float pitch, float x)
    {
        if (!clip || !audioSrc) return;
        audioSrc.pitch = pitch;
        audioSrc.panStereo = BuddyBand.PanAt(x, 0.5f);
        audioSrc.PlayOneShot(clip, vol * volume);
    }

    static Sprite White()
    {
        if (s_white) return s_white;
        var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "Fort Occluder" };
        var px = new Color32[16];
        for (int i = 0; i < 16; i++) px[i] = new Color32(255, 255, 255, 255);
        tex.SetPixels32(px);
        tex.Apply(false, true);
        s_white = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4f);
        return s_white;
    }

    // ------------------------------------------------------------------ pictures: the fort (smooth, 1 unit, pivot in the middle)

    static float Smooth(float a, float b, float x) { float t = Mathf.Clamp01((x - a) / (b - a)); return t * t * (3f - 2f * t); }

    // a puffy pillow: pinched sides, poking corners, a seam, a button in the middle, lit from the top left
    static readonly Color[] PillowColors = { new Color(0.98f, 0.93f, 0.82f), new Color(0.97f, 0.76f, 0.80f), new Color(0.72f, 0.84f, 0.96f), new Color(0.99f, 0.90f, 0.60f), new Color(0.83f, 0.78f, 0.95f) };
    static readonly Dictionary<int, Sprite> s_pillows = new Dictionary<int, Sprite>();
    static Sprite s_mat, s_blanket;

    static Sprite PillowSprite(int colorIndex, bool wide)
    {
        int key = colorIndex * 2 + (wide ? 1 : 0);
        if (s_pillows.TryGetValue(key, out Sprite cached) && cached) return cached;
        Color c = PillowColors[colorIndex];
        int W = wide ? 112 : 64, H = 40;
        var px = new Color32[W * H];
        Color ink = c * 0.42f; ink.a = 1f;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float u = (x + 0.5f) / W * 2f - 1f, v = (y + 0.5f) / H * 2f - 1f;
                float ax = 0.96f * (1f - 0.07f * (1f - v * v)), ay = 0.92f * (1f - 0.12f * (1f - u * u)); // pinched in the middle of each side
                float e = Mathf.Pow(Mathf.Abs(u) / ax, 3.2f) + Mathf.Pow(Mathf.Abs(v) / ay, 3.2f);
                float edgePx = (e - 1f) * Mathf.Min(W, H) * 0.32f; // ~ pixels past the edge
                float a = Mathf.Clamp01(0.5f - edgePx);
                if (a <= 0f) { px[y * W + x] = new Color32(0, 0, 0, 0); continue; }
                float light = 0.18f * (-u * 0.5f + v * 0.8f) - 0.25f * Smooth(0.55f, 1f, e);
                Color col = light > 0f ? Color.Lerp(c, Color.white, light) : Color.Lerp(c, ink, -light);
                float seam = Mathf.Abs(e - 0.62f);
                if (seam < 0.035f) col = Color.Lerp(col, ink, 0.25f);
                // the button and the creases into it
                Vector2 q = new Vector2(u * W, v * H) * 0.5f;
                float d = q.magnitude;
                if (d < 2.6f) col = Color.Lerp(col, ink, 0.55f);
                else if (d < 9f && (Mathf.Abs(q.x - q.y * 0.9f) < 0.7f || Mathf.Abs(q.x + q.y * 0.9f) < 0.7f)) col = Color.Lerp(col, ink, 0.2f);
                // the outline
                float o = Mathf.Clamp01((edgePx + 2.2f) / 1.2f);
                col = Color.Lerp(col, ink, o);
                col.a = a;
                px[y * W + x] = col;
            }
        return s_pillows[key] = Make(px, W, H, wide ? "Back Pillow" : "Pillow");
    }

    // a thin folded blanket under them
    static Sprite MatSprite()
    {
        if (s_mat) return s_mat;
        const int W = 128, H = 16;
        var px = new Color32[W * H];
        Color a0 = new Color(0.62f, 0.78f, 0.70f), a1 = new Color(0.82f, 0.92f, 0.86f), ink = new Color(0.22f, 0.32f, 0.28f);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float dx = Mathf.Min(x + 0.5f, W - x - 0.5f), dy = Mathf.Min(y + 0.5f, H - y - 0.5f);
                float r = 5f, cx = Mathf.Max(0f, r - dx), cy = Mathf.Max(0f, r - dy);
                float inside = Mathf.Min(Mathf.Min(dx, dy), cx > 0f && cy > 0f ? r - Mathf.Sqrt(cx * cx + cy * cy) : 99f);
                float al = Mathf.Clamp01(inside);
                if (al <= 0f) { px[y * W + x] = new Color32(0, 0, 0, 0); continue; }
                Color col = ((x / 10) % 2 == 0) ? a0 : a1;
                col = Color.Lerp(col, Color.white, 0.15f * (y / (float)H));
                col = Color.Lerp(col, ink, Mathf.Clamp01((2.2f - inside) / 1.2f));
                col.a = al;
                px[y * W + x] = col;
            }
        return s_mat = Make(px, W, H, "Fort Mat");
    }

    // the starry blanket: up over the two walls, sagging a little between them, the ends hanging down, a wavy hem
    static Sprite BlanketSprite()
    {
        if (s_blanket) return s_blanket;
        const int W = 160, H = 48;
        var px = new Color32[W * H];
        Color navy = new Color(0.22f, 0.27f, 0.52f), hem = new Color(0.45f, 0.55f, 0.85f), ink = new Color(0.08f, 0.09f, 0.2f), star = new Color(1f, 0.92f, 0.55f);
        var rng = new System.Random(4021);
        var stars = new List<Vector2>();
        for (int i = 0; i < 26; i++) stars.Add(new Vector2((float)rng.NextDouble() * W, (float)rng.NextDouble() * H));
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float u = (x + 0.5f) / W, fy = y + 0.5f;
                // the top: highest over the posts (u ~ 0.06 and 0.94), a soft sag between, falling away outside
                float post = Mathf.Min(Mathf.Abs(u - 0.06f), Mathf.Abs(u - 0.94f));
                bool outside = u < 0.06f || u > 0.94f;
                float top = H - 2f - (outside ? post * 260f : 7f * Mathf.Sin(Mathf.PI * Mathf.Clamp01((u - 0.06f) / 0.88f)));
                float bottom = top - 15f - (outside ? post * 80f : 0f) + 1.6f * Mathf.Sin(u * 60f); // the wavy hem
                float inside = Mathf.Min(top - fy, fy - bottom);
                float al = Mathf.Clamp01(inside + 0.5f);
                if (al <= 0f) { px[y * W + x] = new Color32(0, 0, 0, 0); continue; }
                Color col = navy;
                if (fy - bottom < 4f) col = hem;
                foreach (var s in stars)
                {
                    float dx = Mathf.Abs(x - s.x), dy = Mathf.Abs(y - s.y);
                    if ((dx < 0.6f && dy < 2.2f) || (dy < 0.6f && dx < 2.2f) || (dx < 1.1f && dy < 1.1f)) { col = star; break; }
                }
                col = Color.Lerp(col, Color.white, 0.12f * Mathf.Clamp01((fy - bottom) / 15f));
                col = Color.Lerp(col, ink, Mathf.Clamp01((2.2f - inside) / 1.2f));
                col.a = al;
                px[y * W + x] = col;
            }
        return s_blanket = Make(px, W, H, "Starry Blanket");
    }

    static Sprite Make(Color32[] px, int w, int h, string n)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = n, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), w); // (1 unit wide)
    }

    // ------------------------------------------------------------------ pictures: the dream (pixel art, like the speech bubbles)

    Sprite DreamSprite(Moment m)
    {
        string key = (int)m.kind + "|" + m.text + "|" + Mathf.RoundToInt(m.color.r * 255f) + "," + Mathf.RoundToInt(m.color.g * 255f) + "," + Mathf.RoundToInt(m.color.b * 255f);
        if (dreamCache.TryGetValue(key, out Sprite s) && s) return s;
        const int IW = 20, IH = 16;
        var icon = Icon(m.kind, m.color, IW, IH);
        string text = m.text.Length > 14 ? m.text.Substring(0, 14) : m.text;
        int tw = PixelFont.Measure(text);
        int W = Mathf.Max(IW, tw) + 16, H = IH + PixelFont.GlyphH + 17;
        var px = new Color32[W * H];
        // a cloud: a box with bumps all round
        var bumps = new List<Vector3>();
        int nx = Mathf.Max(2, Mathf.RoundToInt((W - 13) / 10.5f) + 1), ny = Mathf.Max(2, Mathf.RoundToInt((H - 14) / 9f) + 1);
        float step = (W - 13f) / (nx - 1);
        for (int i = 0; i < nx; i++)
        {
            float x = 6.5f + step * i;
            bumps.Add(new Vector3(x, H - 6.5f, 6f));
            if (i < nx - 1) bumps.Add(new Vector3(x + step * 0.5f, 6f, 5.5f)); // (the bottom ones in between)
        }
        bumps.Add(new Vector3(6.5f, 6f, 5.5f)); bumps.Add(new Vector3(W - 6.5f, 6f, 5.5f));
        for (int i = 0; i < ny; i++) { float y = 7f + (H - 14f) * i / (ny - 1); bumps.Add(new Vector3(6f, y, 5.5f)); bumps.Add(new Vector3(W - 6f, y, 5.5f)); }
        Func<int, int, bool> inCloud = (x, y) =>
        {
            float fx = x + 0.5f, fy = y + 0.5f;
            if (fx < 0f || fy < 0f || fx > W || fy > H) return false;
            if (fx > 7f && fx < W - 7f && fy > 7f && fy < H - 7f) return true;
            foreach (var b in bumps) { float dx = fx - b.x, dy = fy - b.y; if (dx * dx + dy * dy <= b.z * b.z) return true; }
            return false;
        };
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                if (!inCloud(x, y)) continue;
                bool edge = !inCloud(x - 1, y) || !inCloud(x + 1, y) || !inCloud(x, y - 1) || !inCloud(x, y + 1);
                px[y * W + x] = edge ? Ink : Fill;
            }
        int ix = (W - IW) / 2, iy = H - 7 - IH;
        for (int y = 0; y < IH; y++)
            for (int x = 0; x < IW; x++)
            {
                Color32 c = icon[y * IW + x];
                if (c.a > 0) px[(iy + y) * W + ix + x] = c;
            }
        PixelFont.Draw(px, W, H, text, (W - tw) / 2, iy - 2, Ink);
        var tex = new Texture2D(W, H, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point, name = "Dream" };
        tex.SetPixels32(px);
        tex.Apply(false, true);
        s = Sprite.Create(tex, new Rect(0, 0, W, H), Vector2.zero, 1f, 0, SpriteMeshType.FullRect);
        dreamCache[key] = s;
        return s;
    }

    static Sprite Dot(int r)
    {
        int n = 2 * r + 1;
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = x - r, dy = y - r, d = dx * dx + dy * dy;
                if (d > r * r + 0.5f) continue;
                bool edge = d > (r - 1) * (r - 1) + 0.5f;
                px[y * n + x] = edge ? Ink : Fill;
            }
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point, name = "Dream Dot" };
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 1f, 0, SpriteMeshType.FullRect);
    }

    // a little picture of the moment, in pixels (y up), with a dark outline
    static Color32[] Icon(Dream kind, Color tint, int W, int H)
    {
        var px = new Color32[W * H];
        Action<float, float, float, float, Color> ell = (cx, cy, rx, ry, c) =>
        {
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { float dx = (x + 0.5f - cx) / rx, dy = (y + 0.5f - cy) / ry; if (dx * dx + dy * dy <= 1f) px[y * W + x] = c; }
        };
        Action<int, int, int, int, Color> box = (x0, y0, x1, y1, c) =>
        {
            for (int y = Mathf.Max(0, y0); y <= Mathf.Min(H - 1, y1); y++) for (int x = Mathf.Max(0, x0); x <= Mathf.Min(W - 1, x1); x++) px[y * W + x] = c;
        };
        Action<int, int, Color> dot = (x, y, c) => { if (x >= 0 && x < W && y >= 0 && y < H) px[y * W + x] = c; };
        Color white = new Color(1f, 1f, 1f), black = new Color(0.1f, 0.08f, 0.08f);
        switch (kind)
        {
            case Dream.Monster:
                {
                    Color c = tint; c.a = 1f;
                    ell(4.5f, 11f, 3f, 3f, c); ell(15.5f, 11f, 3f, 3f, c);
                    ell(10f, 7f, 8.5f, 6.5f, c);
                    foreach (var e in new[] { new Vector2Int(5, 8), new Vector2Int(9, 10), new Vector2Int(13, 8) }) { box(e.x, e.y, e.x + 2, e.y + 2, white); dot(e.x + 1, e.y + 1, black); }
                    box(6, 3, 14, 4, black);
                    dot(7, 4, white); dot(10, 4, white); dot(13, 4, white);
                    dot(4, 14, Color.Lerp(c, white, 0.5f)); dot(16, 14, Color.Lerp(c, white, 0.5f));
                    break;
                }
            case Dream.Music:
                {
                    Color c = tint; c.a = 1f;
                    ell(5f, 3.5f, 2.6f, 2f, c); ell(14f, 5.5f, 2.6f, 2f, c);
                    box(7, 3, 7, 13, c); box(16, 5, 16, 15, c);
                    for (int x = 7; x <= 16; x++) { int y = 12 + (x - 7) * 2 / 9; box(x, y, x, y + 2, c); }
                    break;
                }
            case Dream.Cake:
                {
                    Color pink = new Color(0.97f, 0.62f, 0.75f), cream = new Color(1f, 0.97f, 0.9f);
                    box(3, 1, 16, 8, pink);
                    box(3, 8, 16, 9, cream); dot(5, 7, cream); dot(9, 7, cream); dot(9, 6, cream); dot(13, 7, cream);
                    box(9, 10, 10, 13, new Color(0.55f, 0.75f, 0.95f)); dot(9, 11, white); dot(10, 12, white);
                    dot(9, 14, new Color(1f, 0.6f, 0.15f)); dot(10, 14, new Color(1f, 0.85f, 0.3f)); dot(10, 15, new Color(1f, 0.6f, 0.15f));
                    break;
                }
            case Dream.Snack:
                {
                    Color dough = new Color(0.86f, 0.64f, 0.38f), chip = new Color(0.35f, 0.2f, 0.1f);
                    ell(10f, 8f, 7.5f, 7f, dough);
                    ell(16.5f, 13.5f, 3f, 3f, new Color(0, 0, 0, 0)); // a bite
                    for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { float dx = x + 0.5f - 16.5f, dy = y + 0.5f - 13.5f; if (dx * dx + dy * dy <= 9f) px[y * W + x] = new Color32(0, 0, 0, 0); }
                    dot(7, 10, chip); dot(11, 5, chip); dot(13, 9, chip); dot(6, 5, chip); dot(9, 12, chip);
                    break;
                }
            case Dream.Rain:
                {
                    Color cloud = new Color(0.78f, 0.82f, 0.9f), drop = new Color(0.35f, 0.55f, 0.95f);
                    ell(6f, 10f, 4f, 3.5f, cloud); ell(11.5f, 11.5f, 5f, 4f, cloud); ell(15.5f, 9.5f, 3.5f, 3f, cloud); box(4, 7, 17, 9, cloud);
                    foreach (var d in new[] { new Vector2Int(5, 3), new Vector2Int(9, 1), new Vector2Int(12, 4), new Vector2Int(16, 2) }) { dot(d.x, d.y, drop); dot(d.x, d.y + 1, drop); }
                    break;
                }
            case Dream.Ball:
                {
                    Color ball = new Color(0.85f, 0.93f, 0.32f);
                    ell(10f, 8f, 7f, 7f, ball);
                    for (int y = 2; y <= 13; y++) { int x = 6 + Mathf.RoundToInt(2.2f * Mathf.Sin((y - 2) / 11f * Mathf.PI)); dot(x, y, white); dot(20 - x, y, white); }
                    break;
                }
            case Dream.Star:
                {
                    Color st = new Color(1f, 0.85f, 0.25f);
                    for (int y = 0; y < H; y++)
                        for (int x = 0; x < W; x++)
                        {
                            float dx = x + 0.5f - 10f, dy = y + 0.5f - 7.5f, a = Mathf.Atan2(dy, dx) - Mathf.PI / 2f, r = Mathf.Sqrt(dx * dx + dy * dy);
                            float k = Mathf.Cos(5f * a) * 0.5f + 0.5f; // 5 points
                            if (r <= Mathf.Lerp(3.2f, 7.5f, Mathf.Pow(k, 3f))) px[y * W + x] = st;
                        }
                    dot(9, 8, black); dot(11, 8, black);
                    break;
                }
            case Dream.Peekaboo:
                {
                    Color frame = new Color(0.82f, 0.85f, 0.9f), bar = new Color(0.5f, 0.6f, 0.8f), peek = tint; peek.a = 1f;
                    ell(16.5f, 7f, 3.5f, 4f, peek);
                    box(1, 2, 14, 14, frame); box(1, 12, 14, 14, bar);
                    box(15, 7, 16, 8, white); dot(16, 7, black);
                    break;
                }
            case Dream.Sheep:
                {
                    Color wool = new Color(0.98f, 0.98f, 0.96f), fence = new Color(0.65f, 0.45f, 0.28f);
                    box(2, 1, 3, 5, fence); box(16, 1, 17, 5, fence); box(2, 3, 17, 3, fence);
                    ell(7f, 9f, 3.5f, 3f, wool); ell(11f, 10f, 4f, 3.2f, wool); ell(9f, 12f, 3.5f, 2.6f, wool);
                    ell(15.5f, 10.5f, 2f, 1.7f, black);
                    dot(8, 6, black); dot(8, 5, black); dot(12, 6, black); dot(12, 5, black);
                    break;
                }
            default: // your cursor
                {
                    string[] rows = { "X.........", "XX........", "X#X.......", "X##X......", "X###X.....", "X####X....", "X#####X...", "X######X..", "X#######X.", "X####XXXXX", "X##X#X....", "X#X.X#X...", "XX..X#X...", "X....X#X..", ".....XXX.." };
                    for (int r = 0; r < rows.Length; r++)
                        for (int x = 0; x < rows[r].Length; x++)
                        {
                            char ch = rows[r][x];
                            if (ch == '.') continue;
                            dot(6 + x, H - 1 - r, ch == 'X' ? black : white);
                        }
                    return Outline(px, W, H, false);
                }
        }
        return Outline(px, W, H, true);
    }

    // a dark outline round what's drawn
    static Color32[] Outline(Color32[] px, int W, int H, bool on)
    {
        if (!on) return px;
        var o = (Color32[])px.Clone();
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                if (px[y * W + x].a > 0) continue;
                bool near = (x > 0 && px[y * W + x - 1].a > 0) || (x < W - 1 && px[y * W + x + 1].a > 0) || (y > 0 && px[(y - 1) * W + x].a > 0) || (y < H - 1 && px[(y + 1) * W + x].a > 0);
                if (near) o[y * W + x] = Ink;
            }
        return o;
    }
}
