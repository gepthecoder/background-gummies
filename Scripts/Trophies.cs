using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// The trophy shelf: badges for the games they play. Six feats (floor-is-lava wins, slam dunks, buckets, hide-and-seek
/// games, buddy-tower tops, band jams played to the end), each with a bronze, a silver and a gold tier. A new tier: the
/// buddy proudly holds the medal up over its head for a moment ("GOLD LAVA MEDAL!"), with a chime and sparkles. Chat
/// "trophies" (or WhipIt > Buddy > Show The Trophy Shelf) and a little wooden shelf stands up on a window (or the floor)
/// near them: a row per buddy with its name, its level and its six medals (a dent where one's still to win, a thin bar
/// under each: how far to the next tier); the buddies hop over and stand beside it. It goes again after a while (or chat
/// "trophies" once more). Pixel art like the speech bubbles. Saved in trophies.txt next to the buddy saves. Lives on the
/// Interaction object (WhipIt > Setup).
/// </summary>
[DefaultExecutionOrder(8)] // after BuddyEater (0): it steers them over to the shelf
[DisallowMultipleComponent]
public class Trophies : MonoBehaviour
{
    public enum Feat { Lava, Dunk, Buckets, HideSeek, Tower, Band }
    public const int FeatCount = 6;
    /// <summary>Bronze / silver / gold at this many (per feat).</summary>
    public static readonly int[][] Tiers = { new[] { 1, 5, 20 }, new[] { 1, 10, 40 }, new[] { 5, 25, 100 }, new[] { 1, 10, 30 }, new[] { 1, 5, 20 }, new[] { 1, 10, 30 } };
    public static readonly string[] FeatName = { "LAVA", "DUNK", "BUCKETS", "HIDE & SEEK", "TOWER", "BAND" };
    static readonly string[] TierName = { "", "BRONZE", "SILVER", "GOLD" };

    [Tooltip("How long the shelf stays out (seconds).")]
    public float showFor = 25f;
    [Tooltip("Screen pixels per pixel-art pixel on a 1080p main screen (like the speech bubbles).")]
    public int pixelScale1080 = 3;
    [Tooltip("Sprite material (WhipIt > Setup assigns it).")]
    public Material material;
    [Header("Sounds (WhipIt > Setup assigns them)")]
    public AudioClip dingClip;
    public AudioClip shelfClip, poofClip;
    [Range(0f, 1f)] public float volume = 0.6f;

    /// <summary>The one in the scene (on the Interaction object).</summary>
    public static Trophies Current { get; private set; }
    /// <summary>A buddy reached a new tier (who, feat, tier 1..3).</summary>
    public static event Action<GummyBody, Feat, int> Earned;
    public static string FilePath => Path.Combine(Application.persistentDataPath, "trophies.txt");

    /// <summary>The shelf is out.</summary>
    public bool Showing => shelf && phase != 0;
    /// <summary>Where the shelf stands (world; its feet), and its size.</summary>
    public Rect ShelfRect { get; private set; }
    /// <summary>Counts (tests): medals popped (new tiers), shelves shown.</summary>
    public int Pops { get; private set; }
    public int Shows { get; private set; }

    readonly Dictionary<string, int[]> counts = new Dictionary<string, int[]>();
    bool dirty, loaded;
    float saveAt;

    // ------------------------------------------------------------------ life

    void Awake()
    {
        root = new GameObject("Trophies").transform;
        root.SetParent(transform, false);
        sfx = gameObject.AddComponent<AudioSource>();
        sfx.playOnAwake = false;
        sfx.spatialBlend = 0f;
        Load();
    }

    void OnEnable()
    {
        Current = this;
        Lava.Won += OnLava;
        Basketball.Scored += OnScored;
        BuddyHideSeek.AnyDone += OnHideSeek;
        BuddyTower.Made += OnTower;
        BandJam.Played += OnBand;
    }

    void OnDisable()
    {
        Lava.Won -= OnLava;
        Basketball.Scored -= OnScored;
        BuddyHideSeek.AnyDone -= OnHideSeek;
        BuddyTower.Made -= OnTower;
        BandJam.Played -= OnBand;
        if (dirty) Save();
        HideNow(false);
        if (Current == this) Current = null;
    }

    void OnApplicationQuit() { if (dirty) Save(); }

    void OnDestroy()
    {
        if (root) Destroy(root.gameObject);
        if (shelfTex) Destroy(shelfTex);
    }

    void OnLava(GummyBody b, bool last) => Add(b, Feat.Lava);
    void OnScored(GummyBody b, Basketball.Shot shot) { if (!b) return; Add(b, Feat.Buckets); if (shot.dunk) Add(b, Feat.Dunk); }
    void OnHideSeek(GummyBody b, bool found) => Add(b, Feat.HideSeek);
    void OnTower(GummyBody b) => Add(b, Feat.Tower);
    void OnBand(GummyBody b) => Add(b, Feat.Band);

    // ------------------------------------------------------------------ counting

    /// <summary>How many times (all time) this save did this.</summary>
    public int Count(string save, Feat f) => save != null && counts.TryGetValue(save, out int[] c) ? c[(int)f] : 0;
    /// <summary>0 = none yet, 1 bronze, 2 silver, 3 gold.</summary>
    public int Tier(string save, Feat f) => TierFor(f, Count(save, f));

    public static int TierFor(Feat f, int n)
    {
        int t = 0;
        foreach (int need in Tiers[(int)f]) if (n >= need) t++;
        return t;
    }

    /// <summary>One more for this buddy (a fused monster: everyone inside). A new tier: the medal pops.</summary>
    public void Add(GummyBody b, Feat f, int n = 1)
    {
        if (!b || n <= 0) return;
        var mon = b.IsMonster ? b.GetComponent<BuddyMonster>() : null;
        int best = 0;
        if (mon && mon.Members.Count > 0)
            foreach (var m in mon.Members) best = Mathf.Max(best, Bump(SaveOf(m), f, n));
        else best = Bump(SaveOf(b), f, n);
        if (best > 0) Pop(b, f, best);
    }

    // +n for one save; the new tier if it just reached one (else 0)
    int Bump(string save, Feat f, int n)
    {
        if (string.IsNullOrEmpty(save)) return 0;
        if (!counts.TryGetValue(save, out int[] c)) counts[save] = c = new int[FeatCount];
        int before = TierFor(f, c[(int)f]);
        c[(int)f] += n;
        int after = TierFor(f, c[(int)f]);
        dirty = true;
        saveAt = Time.unscaledTime + 2f;
        if (Showing) needBake = true;
        return after > before ? after : 0;
    }

    static string SaveOf(GummyBody b)
    {
        if (!b) return null;
        var p = b.GetComponent<BuddyProgress>();
        return p && !string.IsNullOrEmpty(p.saveName) ? p.saveName : b.gameObject.name.ToLowerInvariant().Replace(' ', '_');
    }

    void Load()
    {
        loaded = true;
        counts.Clear();
        try
        {
            if (!File.Exists(FilePath)) return;
            foreach (string line in File.ReadAllLines(FilePath))
            {
                int bar = line.IndexOf('|');
                if (bar <= 0) continue;
                var c = new int[FeatCount];
                string[] parts = line.Substring(bar + 1).Split(',');
                for (int i = 0; i < FeatCount && i < parts.Length; i++) int.TryParse(parts[i], out c[i]);
                counts[line.Substring(0, bar)] = c;
            }
        }
        catch (Exception e) { Debug.LogWarning("[WhipIt] Couldn't read the trophies: " + e.Message); }
    }

    void Save()
    {
        dirty = false;
        try
        {
            var sb = new System.Text.StringBuilder();
            foreach (var kv in counts) sb.Append(kv.Key).Append('|').Append(string.Join(",", kv.Value)).Append('\n');
            Directory.CreateDirectory(Application.persistentDataPath);
            File.WriteAllText(FilePath, sb.ToString());
        }
        catch (Exception e) { Debug.LogWarning("[WhipIt] Couldn't save the trophies: " + e.Message); }
    }

    // ------------------------------------------------------------------ each frame

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("Trophies.Update", t0); }
    void UpdateTimed()
    {
        float dt = Mathf.Min(Time.deltaTime, 0.05f), now = Time.time;
        if (dirty && Time.unscaledTime >= saveAt) Save();
        PixelSize();
        UpdateShelf(dt, now);
        UpdatePops(dt, now);
        StepSparkles(dt);
    }

    // ------------------------------------------------------------------ the shelf

    SpriteRenderer shelf;
    Texture2D shelfTex;
    Transform root;
    AudioSource sfx;
    int phase;            // 0 away, 1 popping up, 2 out, 3 going
    float phaseT, shelfW, shelfH;
    long onPlat, layerWin = -1;
    int platVersion = -1;
    Vector2 shelfAt;      // its feet (world)
    bool needBake;
    readonly List<string> rows = new List<string>();

    /// <summary>Show the shelf (the one who asked says something about it). False if there's nobody to show it for.</summary>
    public bool Show(GummyBody asker = null)
    {
        if (Showing) { if (phase == 3) { phase = 2; phaseT = 0f; } return true; }
        Rows();
        if (rows.Count == 0) return false;
        PixelSize();
        Bake();
        if (!Place()) return false;
        phase = 1;
        phaseT = 0f;
        Shows++;
        Play(shelfClip, 1f, 0.9f);
        for (int i = 0; i < 8; i++) Sparkle(new Vector2(shelfAt.x + Rnd(-0.5f, 0.5f) * shelfW, shelfAt.y + Rnd(0f, 1f) * shelfH), Rnd2(1.2f) + new Vector2(0f, 0.5f), Rnd(0.05f, 0.09f), Gold(), Rnd(0.5f, 0.9f));
        Gather(asker);
        return true;
    }

    /// <summary>Put it away (with a poof).</summary>
    public void Hide() { if (Showing && phase != 3) { phase = 3; phaseT = 0f; Release(); Play(poofClip, 1.1f, 0.6f); } }

    void HideNow(bool poof)
    {
        Release();
        phase = 0;
        if (shelf) shelf.enabled = false;
    }

    // which rows: everyone here (and the ones inside a fused monster), in family order
    void Rows()
    {
        rows.Clear();
        foreach (var b in GummyBody.All)
        {
            if (!b || !b.isActiveAndEnabled) continue;
            var mon = b.IsMonster ? b.GetComponent<BuddyMonster>() : null;
            if (mon && mon.Members.Count > 0) { foreach (var m in mon.Members) AddRow(SaveOf(m)); }
            else if (b.GetComponent<BuddyProgress>()) AddRow(SaveOf(b));
        }
        rows.Sort((a, c) => Rank(a).CompareTo(Rank(c)));
    }

    void AddRow(string save) { if (!string.IsNullOrEmpty(save) && !rows.Contains(save)) rows.Add(save); }

    static int Rank(string save)
    {
        string[] order = { "apple", "tomato", "bean", "burger", "dino", "duck" };
        for (int i = 0; i < order.Length; i++) if (save.EndsWith(order[i])) return i;
        return 99;
    }

    static string NameOf(string save)
    {
        string n = save.StartsWith("buddy_") ? save.Substring(6) : save;
        return n.Replace('_', ' ').ToUpperInvariant();
    }

    int LevelOf(string save)
    {
        foreach (var b in GummyBody.All)
        {
            if (!b) continue;
            var mon = b.IsMonster ? b.GetComponent<BuddyMonster>() : null;
            if (mon) { foreach (var m in mon.Members) if (SaveOf(m) == save) { var pm = m.GetComponent<BuddyProgress>(); if (pm) return pm.Level; } continue; }
            if (SaveOf(b) == save) { var p = b.GetComponent<BuddyProgress>(); if (p) return p.Level; }
        }
        return 0;
    }

    // a window top near them with room for it (and a buddy either side), or the floor
    bool Place()
    {
        Rect busy = ScreenBounds.BusiestRect;
        float mx = 0f;
        int n = 0;
        foreach (var b in GummyBody.All) if (b && b.isActiveAndEnabled && busy.Contains(b.Center)) { mx += b.Center.x; n++; }
        mx = n > 0 ? mx / n : busy.center.x;
        float best = float.MaxValue;
        bool found = false;
        foreach (var pl in DesktopPlatforms.Current)
        {
            if (DesktopPlatforms.IsScreen(pl.id) || DesktopPlatforms.IsIcon(pl.id)) continue;
            if (pl.x1 - pl.x0 < shelfW + 2f * Room || pl.y + shelfH > busy.yMax - 0.3f || pl.y < busy.yMin || pl.x1 < busy.xMin || pl.x0 > busy.xMax) continue;
            float x = Mathf.Clamp(mx, pl.x0 + shelfW * 0.5f + Room, pl.x1 - shelfW * 0.5f - Room);
            if (Covered(x, pl.y, pl.id)) continue;
            int reach = Reach(x, pl.y);
            if (reach == 0) continue; // (nobody could get up there to stand by it)
            float score = Mathf.Abs(x - mx) + 0.4f * (pl.y - busy.yMin) - 2.5f * reach;
            if (score < best) { best = score; shelfAt = new Vector2(x, pl.y); onPlat = pl.id; found = true; }
        }
        // the floor near them, if more of them can get there
        {
            float x = Mathf.Clamp(mx + 1.2f, busy.xMin + shelfW * 0.5f + 0.4f, busy.xMax - shelfW * 0.5f - 0.4f);
            float y = ScreenBounds.FloorAt(new Vector2(x, busy.center.y));
            float score = Mathf.Abs(x - mx) - 2.5f * Reach(x, y) + 0.5f; // (a window top shows it off better: a little bonus)
            if (!found || score < best) { shelfAt = new Vector2(x, y); onPlat = 0; }
        }
        platVersion = DesktopPlatforms.Version;
        layerWin = onPlat;
        ShelfRect = new Rect(shelfAt.x - shelfW * 0.5f, shelfAt.y, shelfW, shelfH);
        return true;
    }

    const float Room = 0.9f; // (beside it on its window: a buddy either side)

    // how many of them (on their feet, awake, on its screen) could get over to beside the shelf standing there
    int Reach(float x, float y)
    {
        int n = 0;
        foreach (var b in GummyBody.All)
        {
            if (!b || !b.isActiveAndEnabled || b.Held || b.InRoom || !b.Grounded || !ScreenBounds.SameScreen(b.Center, new Vector2(x, y + 0.1f))) continue;
            var br = b.GetComponent<GummyBrain>();
            if (!br || br.Current == GummyBrain.State.Asleep) continue;
            float side = b.Center.x < x ? -1f : 1f;
            if (br.PlanTo(new Vector2(x + side * (shelfW * 0.5f + 0.45f), y + b.size * 0.3f), y, out _)) n++;
        }
        return n;
    }

    // a window over where it'd stand (the space above that window top)
    bool Covered(float x, float y, long under)
    {
        var wins = DesktopPlatforms.Windows;
        for (int i = 0; i < wins.Count; i++)
        {
            WindowBox w = wins[i];
            if (w.id == under) continue;
            if (w.xMax > x - shelfW * 0.5f && w.xMin < x + shelfW * 0.5f && w.yMax > y + 0.02f && w.yMin < y + shelfH) return true;
        }
        return false;
    }

    void UpdateShelf(float dt, float now)
    {
        if (phase == 0) return;
        phaseT += dt;
        // it rides its window (gone: away it goes)
        if (onPlat != 0 && DesktopPlatforms.Version != platVersion)
        {
            platVersion = DesktopPlatforms.Version;
            bool there = false;
            foreach (var pl in DesktopPlatforms.Current) if (pl.id == onPlat) { shelfAt += new Vector2(pl.dx, pl.dy); there = true; break; }
            if (!there && phase != 3) { phase = 3; phaseT = 0f; Release(); Play(poofClip, 1.1f, 0.6f); }
        }
        ShelfRect = new Rect(shelfAt.x - shelfW * 0.5f, shelfAt.y, shelfW, shelfH);
        if (needBake && phase != 3) { needBake = false; Rows(); Bake(); }
        float sy = 1f, sx = 1f;
        switch (phase)
        {
            case 1: // pops up with a little overshoot
                {
                    float u = Mathf.Clamp01(phaseT / 0.35f);
                    sy = u < 0.7f ? Mathf.Lerp(0.05f, 1.12f, u / 0.7f) : Mathf.Lerp(1.12f, 1f, (u - 0.7f) / 0.3f);
                    sx = 1f / Mathf.Sqrt(Mathf.Max(sy, 0.3f));
                    if (u >= 1f) { phase = 2; phaseT = 0f; }
                    break;
                }
            case 2:
                Steer(now);
                if (phaseT >= showFor) { phase = 3; phaseT = 0f; Release(); Play(poofClip, 1.1f, 0.6f); }
                break;
            case 3: // poof
                {
                    float u = Mathf.Clamp01(phaseT / 0.3f);
                    sy = sx = 1f - u * u;
                    if (phaseT < dt * 1.5f) for (int i = 0; i < 10; i++) Sparkle(new Vector2(shelfAt.x + Rnd(-0.5f, 0.5f) * shelfW, shelfAt.y + Rnd(0f, 1f) * shelfH), Rnd2(1.5f), Rnd(0.05f, 0.1f), Gold(), Rnd(0.4f, 0.8f));
                    if (u >= 1f) { phase = 0; if (shelf) shelf.enabled = false; return; }
                    break;
                }
        }
        bool hidden = Occlusion.ThingHidden(shelfAt + new Vector2(0f, 0.1f), false, true, onPlat, ref layerWin);
        shelf.enabled = !hidden;
        shelf.transform.position = Snap(shelfAt);
        shelf.transform.localScale = new Vector3(fontPx * sx, fontPx * sy, 1f);
    }

    // ------------------------------------------------------------------ the buddies come over and stand beside it

    class G { public GummyBody b; public GummyBrain br; public float dx, since, nextPlan; public bool there, steered, talk, said; }
    readonly List<G> gs = new List<G>();

    void Gather(GummyBody asker)
    {
        Release();
        if (Busy()) { if (asker) Say(asker, Pick("TA-DA! OUR TROPHIES", "LOOK! OUR MEDALS")); return; }
        var cands = new List<(float d, GummyBody b)>();
        foreach (var b in GummyBody.All)
        {
            if (!b || !b.isActiveAndEnabled || b.Held || b.InRoom) continue;
            var br = b.GetComponent<GummyBrain>();
            if (!br || br.Current == GummyBrain.State.Asleep || br.Towering || br.Sliding || !ScreenBounds.SameScreen(b.Center, shelfAt)) continue;
            cands.Add((Mathf.Abs(b.Center.x - shelfAt.x), b));
        }
        cands.Sort((a, c) => a.d.CompareTo(c.d));
        // spots along its surface, both sides, nearest first
        Vector2 span = SurfaceSpan();
        int left = 0, right = 0, talkers = 0;
        foreach (var c in cands)
        {
            var b = c.b;
            float w = Mathf.Max(b.Extent.width, b.size * 0.8f);
            int side = b.Center.x < shelfAt.x ? -1 : 1;
            float x = 0f;
            bool ok = false;
            for (int tries = 0; tries < 2 && !ok; tries++)
            {
                int k = side < 0 ? left : right;
                x = shelfAt.x + side * (shelfW * 0.5f + 0.12f + w * 0.5f + k * 0.62f);
                if (x - w * 0.5f > span.x && x + w * 0.5f < span.y) ok = true;
                else side = -side;
            }
            if (!ok) continue;
            if (side < 0) left++; else right++;
            bool talk = b == asker || (talkers < 2 && Rand.NextDouble() < 0.6);
            if (talk) talkers++;
            gs.Add(new G { b = b, br = b.GetComponent<GummyBrain>(), dx = x - shelfAt.x, since = Time.time, talk = talk }); // (beside the shelf: it moves along if the window does)
        }
        if (asker) Say(asker, Pick("TA-DA! OUR TROPHIES", "LOOK! OUR MEDALS", "THE TROPHY SHELF!"));
    }

    Vector2 SurfaceSpan()
    {
        if (onPlat != 0) foreach (var pl in DesktopPlatforms.Current) if (pl.id == onPlat) return new Vector2(pl.x0, pl.x1);
        return ScreenBounds.FloorSpanAt(shelfAt, shelfAt.y);
    }

    void Steer(float now)
    {
        if (Busy()) { if (gs.Count > 0) Release(); return; }
        foreach (var g in gs)
        {
            if (!g.b || !g.b.isActiveAndEnabled || !g.br) continue;
            if (g.b.Held) { g.there = false; g.since = now; continue; }
            Vector2 spot = new Vector2(shelfAt.x + g.dx, shelfAt.y);
            bool there = g.b.Grounded && Mathf.Abs(g.b.Center.x - spot.x) < 0.28f + 0.3f * g.b.size && Mathf.Abs(g.b.Extent.yMin - spot.y) < 0.2f;
            if (there)
            {
                if (!g.there) { g.there = true; g.br.Act(new Vector2(0.92f, 1.1f), 0.5f); }
                g.br.Food = null;
                g.br.StayPut = true;
                if (g.talk && !g.said && phaseT > 1.2f) { g.said = true; Say(g.b, Brag(g.b), Rnd(0.2f, 1.5f)); }
                continue;
            }
            g.there = false;
            if (now - g.since > 12f) { if (g.steered) { g.br.Food = null; g.br.StayPut = false; } g.steered = false; continue; } // (can't get there: it looks from where it is)
            if (now >= g.nextPlan && g.b.Grounded) // (it plans from its feet; in the air it keeps going)
            {
                g.nextPlan = now + 0.8f;
                g.steered = g.br.PlanTo(new Vector2(spot.x, spot.y + g.b.size * 0.3f), spot.y, out _);
                if (!g.steered) { g.br.Food = null; g.br.StayPut = false; }
            }
            if (g.steered) { g.br.StayPut = false; g.br.Food = new Vector2(spot.x, spot.y + g.b.size * 0.3f); g.br.FoodSurface = spot.y; }
        }
    }

    void Release()
    {
        foreach (var g in gs)
        {
            if (!g.b || !g.br) continue;
            if (g.steered || g.there) { g.br.Food = null; g.br.StayPut = false; }
        }
        gs.Clear();
    }

    // what it says about its medals
    string Brag(GummyBody b)
    {
        var mon = b.IsMonster ? b.GetComponent<BuddyMonster>() : null;
        string save = mon && mon.Members.Count > 0 ? SaveOf(mon.Members[0]) : SaveOf(b);
        int bestT = 0, bestF = -1, total = 0;
        for (int f = 0; f < FeatCount; f++)
        {
            int t = Tier(save, (Feat)f);
            total += t > 0 ? 1 : 0;
            if (t > bestT) { bestT = t; bestF = f; }
        }
        if (bestF < 0) return Line(b, "NO MEDALS YET... SOON!", "MY SHELF IS EMPTY :(", "NOT YET. BREWING.", "NEXT TIME, A MEDAL!", "RAWR. NO MEDALS. YET.", "QUACK... NONE YET", "WE WILL WIN SOME.");
        string m = TierName[bestT] + " " + FeatName[bestF];
        return total >= 3 ? Pick("LOOK AT ALL MINE!", "MY " + m + "!") : Pick("MY " + m + "!", "SEE? " + m + "!");
    }

    static bool Busy() => Bedtime.Sleeping || (Lava.Current && Lava.Current.On) || (Rain.Current && Rain.Current.Raining)
        || LevelUpParty.Current || BandJam.Current || PickupGame.Current || BuddyTower.Current || BuddyHideSeek.Playing || (GiantPotion.Current && GiantPotion.Current.Giant);

    // ------------------------------------------------------------------ a new medal: held up high

    class PopUp { public GummyBody b; public Feat f; public int tier; public float t; public SpriteRenderer sr; }
    readonly List<PopUp> pops = new List<PopUp>();
    readonly Queue<PopUp> waiting = new Queue<PopUp>();
    const float PopTime = 2.8f;

    void Pop(GummyBody b, Feat f, int tier)
    {
        Pops++;
        var p = new PopUp { b = b, f = f, tier = tier };
        foreach (var q in pops) if (q.b == b) { waiting.Enqueue(p); return; } // (one at a time over its head)
        Begin(p);
        Earned?.Invoke(b, f, tier);
    }

    void Begin(PopUp p)
    {
        p.t = 0f;
        if (!p.sr)
        {
            var go = new GameObject("Medal");
            go.transform.SetParent(root, false);
            p.sr = go.AddComponent<SpriteRenderer>();
            p.sr.sortingOrder = 13;
            if (material) p.sr.sharedMaterial = material;
        }
        p.sr.sprite = MedalSprite(p.f, p.tier);
        p.sr.enabled = true;
        pops.Add(p);
        Play(dingClip, p.tier == 3 ? 1f : p.tier == 2 ? 0.94f : 0.89f, 0.85f);
        var br = p.b ? p.b.GetComponent<GummyBrain>() : null;
        if (br && !br.Towering) br.Act(new Vector2(0.88f, 1.14f), 0.8f); // (stretches up, proud)
        Say(p.b, TierName[p.tier] + " " + FeatName[(int)p.f] + " MEDAL!", 1.2f); // (after whatever it says about winning)
        if (p.b) for (int i = 0; i < 10; i++) Sparkle(Over(p.b) + Rnd2(0.2f), Rnd2(1.4f) + new Vector2(0f, 0.4f), Rnd(0.05f, 0.09f), Gold(), Rnd(0.5f, 0.9f));
    }

    // held up beside its head (its speech bubble goes over its head): the medal's middle
    Vector2 Over(GummyBody b)
    {
        Rect r = ScreenBounds.ScreenRectAt(b.Center);
        float side = b.Extent.xMax + 9f * fontPx < r.xMax ? 1f : -1f;
        return new Vector2(side > 0f ? b.Extent.xMax + 7.5f * fontPx : b.Extent.xMin - 7.5f * fontPx, b.Center.y + 0.15f * b.size);
    }

    void UpdatePops(float dt, float now)
    {
        for (int i = pops.Count - 1; i >= 0; i--)
        {
            var p = pops[i];
            p.t += dt;
            if (!p.b || !p.b.isActiveAndEnabled || p.t >= PopTime)
            {
                p.sr.enabled = false;
                pops.RemoveAt(i);
                // the next one for this buddy
                int nw = waiting.Count;
                for (int q = 0; q < nw; q++) { var w = waiting.Dequeue(); if (w.b == p.b && p.b) { Begin(w); Earned?.Invoke(w.b, w.f, w.tier); break; } waiting.Enqueue(w); }
                Destroy(p.sr.gameObject);
                continue;
            }
            float u = p.t / PopTime;
            float rise = u < 0.15f ? Smooth(u / 0.15f) : 1f, gone = u > 0.85f ? (u - 0.85f) / 0.15f : 0f;
            Vector2 at = Over(p.b) + new Vector2(0f, -0.25f * (1f - rise) + 0.04f * Mathf.Sin(p.t * 5f) + 0.3f * gone);
            p.sr.transform.position = Snap(at - new Vector2(0f, 8f * fontPx));
            float k = fontPx * (u < 0.15f ? Mathf.Lerp(0.4f, 1f, Smooth(u / 0.15f)) : 1f) * (1f - gone * 0.5f);
            p.sr.transform.localScale = new Vector3(k, k, 1f);
            p.sr.color = new Color(1f, 1f, 1f, 1f - gone);
            if (Rand.NextDouble() < dt * 6f) Sparkle(at + Rnd2(0.25f), new Vector2(Rnd(-0.2f, 0.2f), Rnd(0.2f, 0.6f)), Rnd(0.04f, 0.07f), Gold(), Rnd(0.4f, 0.7f));
        }
    }

    // ------------------------------------------------------------------ pixel art

    float fontPx = 0.028f, screenPx;

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

    const int Frame = 3, NameW = 27, Slot = 12, RowH = 20, HeaderH = 10, Legs = 2;
    static readonly Color32 Clear = new Color32(0, 0, 0, 0), Outline = new Color32(58, 34, 20, 255), Wood = new Color32(150, 96, 56, 255),
        WoodDark = new Color32(118, 72, 40, 255), WoodLight = new Color32(186, 128, 78, 255), Shadow = new Color32(92, 56, 32, 255),
        Ink = new Color32(255, 246, 230, 255), InkSoft = new Color32(232, 200, 160, 255), InkGold = new Color32(255, 214, 110, 255),
        Dent = new Color32(124, 78, 44, 255), DentRim = new Color32(100, 62, 34, 255), BarBg = new Color32(96, 58, 32, 255), BarFill = new Color32(255, 210, 90, 255);

    /// <summary>The shelf picture for these rows (pixels, y up): frame, header, a plank per row with the name, level, six
    /// medals (or dents) and the progress bars.</summary>
    public static Color32[] BakeShelf(IList<string> rowSaves, Func<string, Feat, int> count, Func<string, int> level, out int w, out int h)
    {
        int n = rowSaves.Count;
        w = Frame * 2 + NameW + Slot * FeatCount;
        h = Legs + Frame * 2 + HeaderH + RowH * n;
        var px = new Color32[w * h];
        // body: outline, wood with a little grain, a bevel
        for (int y = Legs; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool edge = x == 0 || x == w - 1 || y == Legs || y == h - 1;
                bool corner = (x == 0 || x == w - 1) && (y == Legs || y == h - 1);
                Color32 c = corner ? Clear : edge ? Outline : Wood;
                if (!edge && (x == 1 || y == h - 2)) c = WoodLight;
                else if (!edge && (x == w - 2 || y == Legs + 1)) c = Shadow;
                else if (!edge && ((x * 7 + y * 13) % 29 == 0 || (x + y * 3) % 41 == 0)) c = WoodDark; // grain
                px[y * w + x] = c;
            }
        // legs
        for (int y = 0; y < Legs; y++) for (int dx = 0; dx < 4; dx++) { Put(px, w, h, 2 + dx, y, Outline); Put(px, w, h, w - 3 - dx, y, Outline); }
        // header: darker band, the title
        int hy0 = h - Frame - HeaderH;
        for (int y = hy0; y < h - Frame; y++) for (int x = Frame; x < w - Frame; x++) px[y * w + x] = WoodDark;
        for (int x = Frame; x < w - Frame; x++) { Put(px, w, h, x, hy0, Shadow); Put(px, w, h, x, hy0 - 1, WoodLight); }
        string title = "TROPHIES";
        PixelFont.Draw(px, w, h, title, (w - PixelFont.Measure(title)) / 2, h - Frame - 2, InkGold);
        // rows, top to bottom
        for (int r = 0; r < n; r++)
        {
            int top = hy0 - 2 - r * RowH, bottom = top - RowH + 1; // (bottom: the plank)
            for (int x = Frame; x < w - Frame; x++) { Put(px, w, h, x, bottom, Shadow); Put(px, w, h, x, bottom + 1, WoodLight); }
            string save = rowSaves[r];
            string name = NameOf(save);
            if (PixelFont.Measure(name) > NameW - 2) name = name.Substring(0, Mathf.Min(name.Length, 6));
            PixelFont.Draw(px, w, h, name, Frame + 1, top - 3, Ink);
            int lv = level != null ? level(save) : 0;
            if (lv > 0) PixelFont.Draw(px, w, h, "LV" + lv, Frame + 1, top - 10, InkSoft);
            for (int f = 0; f < FeatCount; f++)
            {
                int c = count(save, (Feat)f), t = TierFor((Feat)f, c);
                int x0 = Frame + NameW + f * Slot, y0 = bottom + 4;
                if (t > 0) DrawMedal(px, w, h, x0, y0, (Feat)f, t);
                else DrawDent(px, w, h, x0, y0);
                // how far to the next tier
                float prog = t >= 3 ? 1f : Mathf.Clamp01((c - (t == 0 ? 0 : Tiers[f][t - 1])) / (float)(Tiers[f][t] - (t == 0 ? 0 : Tiers[f][t - 1])));
                for (int k = 0; k < 9; k++) Put(px, w, h, x0 + 1 + k, bottom + 2, k < Mathf.RoundToInt(prog * 9f) ? (t >= 3 ? InkGold : BarFill) : BarBg);
            }
        }
        return px;
    }

    // a medal 11 wide, 15 tall, its bottom-left at (x0, y0): a ribbon (the feat's colours) and a disc (the tier's) with the icon
    static void DrawMedal(Color32[] px, int w, int h, int x0, int y0, Feat f, int tier)
    {
        Color32 fill, rim, hi;
        if (tier >= 3) { fill = new Color32(255, 204, 48, 255); rim = new Color32(168, 118, 12, 255); hi = new Color32(255, 242, 170, 255); }
        else if (tier == 2) { fill = new Color32(204, 210, 220, 255); rim = new Color32(106, 112, 128, 255); hi = new Color32(248, 250, 255, 255); }
        else { fill = new Color32(206, 128, 54, 255); rim = new Color32(118, 66, 22, 255); hi = new Color32(242, 182, 112, 255); }
        Color32 rib = RibbonDark[(int)f], rib2 = RibbonLight[(int)f];
        // ribbon: a V from the top corners down to the disc
        for (int y = 9; y < 15; y++)
        {
            int k = 14 - y; // 0 at the top
            for (int s = 0; s < 3; s++)
            {
                Put(px, w, h, x0 + 1 + k / 2 + s, y0 + y, s == 1 ? rib2 : rib);
                Put(px, w, h, x0 + 9 - k / 2 - s, y0 + y, s == 1 ? rib2 : rib);
            }
        }
        // disc
        for (int y = 0; y < 11; y++)
            for (int x = 0; x < 11; x++)
            {
                float dx = x - 5f, dy = y - 5f, d = Mathf.Sqrt(dx * dx + dy * dy);
                if (d > 5.5f) continue;
                Color32 c = d > 4.5f ? rim : fill;
                if (d <= 4.5f && dx < -1f && dy > 1f && d > 2.6f) c = hi;
                Put(px, w, h, x0 + x, y0 + y, c);
            }
        // the icon, 7 x 7 in the middle
        string[] icon = Icons[(int)f];
        for (int y = 0; y < 7; y++)
            for (int x = 0; x < 7; x++)
                if (icon[y][x] == '#') Put(px, w, h, x0 + 2 + x, y0 + 8 - y, rim);
    }

    static void DrawDent(Color32[] px, int w, int h, int x0, int y0)
    {
        for (int y = 0; y < 11; y++)
            for (int x = 0; x < 11; x++)
            {
                float dx = x - 5f, dy = y - 5f, d = Mathf.Sqrt(dx * dx + dy * dy);
                if (d > 5.5f) continue;
                Put(px, w, h, x0 + x, y0 + y, d > 4.5f ? DentRim : Dent);
            }
    }

    static void Put(Color32[] px, int w, int h, int x, int y, Color32 c) { if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = c; }

    static readonly Color32[] RibbonDark = { new Color32(214, 66, 34, 255), new Color32(44, 100, 210, 255), new Color32(226, 116, 24, 255), new Color32(128, 70, 190, 255), new Color32(46, 150, 80, 255), new Color32(214, 70, 150, 255) };
    static readonly Color32[] RibbonLight = { new Color32(255, 146, 70, 255), new Color32(120, 170, 255, 255), new Color32(255, 186, 90, 255), new Color32(186, 140, 240, 255), new Color32(124, 214, 146, 255), new Color32(255, 150, 206, 255) };

    // rows top to bottom
    static readonly string[][] Icons =
    {
        new[] { "...#...", "..##...", "..###..", ".#####.", ".##.##.", ".#...#.", "..###.." }, // lava: a flame
        new[] { "..###..", ".#####.", "..###..", "#######", ".#.#.#.", "..#.#..", "...#..." }, // dunk: the ball going through the hoop
        new[] { "..###..", ".#.#.#.", "#..#..#", "#######", "#..#..#", ".#.#.#.", "..###.." }, // buckets: a ball
        new[] { ".......", "..###..", ".#...#.", "#..#..#", ".#...#.", "..###..", "......." }, // hide and seek: an eye
        new[] { "...#...", "..###..", "...#...", ".#####.", "..###..", "#######", ".#####." }, // tower: a stack
        new[] { "...##..", "...#.#.", "...#...", "...#...", ".###...", "####...", ".##...." }, // band: a note
    };

    void Bake()
    {
        var px = BakeShelf(rows, Count, LevelOf, out int w, out int h);
        if (shelfTex) Destroy(shelfTex);
        shelfTex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point, name = "Trophy Shelf" };
        shelfTex.SetPixels32(px);
        shelfTex.Apply(false, true);
        if (!shelf)
        {
            var go = new GameObject("Trophy Shelf");
            go.transform.SetParent(root, false);
            shelf = go.AddComponent<SpriteRenderer>();
            shelf.sortingOrder = 4; // (behind the buddies standing next to it)
            if (material) shelf.sharedMaterial = material;
        }
        if (shelf.sprite) Destroy(shelf.sprite);
        shelf.sprite = Sprite.Create(shelfTex, new Rect(0, 0, w, h), new Vector2(0.5f, 0f), 1f, 0, SpriteMeshType.FullRect);
        shelfW = w * fontPx;
        shelfH = h * fontPx;
    }

    static readonly Dictionary<int, Sprite> s_medals = new Dictionary<int, Sprite>();

    static Sprite MedalSprite(Feat f, int tier)
    {
        int key = (int)f * 4 + tier;
        if (s_medals.TryGetValue(key, out Sprite s) && s) return s;
        const int w = 13, h = 17;
        var px = new Color32[w * h];
        DrawMedal(px, w, h, 1, 1, f, tier);
        // an outline round it so it reads on any background
        var o = (Color32[])px.Clone();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (px[y * w + x].a > 0) continue;
                bool near = false;
                for (int dy = -1; dy <= 1 && !near; dy++) for (int dx = -1; dx <= 1 && !near; dx++) { int xx = x + dx, yy = y + dy; if (xx >= 0 && xx < w && yy >= 0 && yy < h && px[yy * w + xx].a > 0) near = true; }
                if (near) o[y * w + x] = Outline;
            }
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point, name = "Medal" };
        tex.SetPixels32(o);
        tex.Apply(false, true);
        return s_medals[key] = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0f), 1f, 0, SpriteMeshType.FullRect);
    }

    // ------------------------------------------------------------------ sparkles (pooled)

    const int MaxSparkles = 48;
    struct P { public Vector2 pos, vel; public float t, life, size; public Color color; public bool on; }
    readonly P[] sparkles = new P[MaxSparkles];
    readonly SpriteRenderer[] sparkleSr = new SpriteRenderer[MaxSparkles];
    int nextSparkle;

    static Color Gold()
    {
        double r = Rand.NextDouble();
        return r < 0.5 ? new Color(1f, 0.9f, 0.45f) : r < 0.8 ? Color.white : new Color(1f, 0.75f, 0.35f);
    }

    void Sparkle(Vector2 at, Vector2 vel, float size, Color c, float life)
    {
        int i = -1;
        for (int k = 0; k < MaxSparkles; k++) { int j = (nextSparkle + k) % MaxSparkles; if (!sparkles[j].on) { i = j; nextSparkle = (j + 1) % MaxSparkles; break; } }
        if (i < 0) return;
        sparkles[i] = new P { pos = at, vel = vel, size = size, color = c, life = life, on = true };
        if (!sparkleSr[i])
        {
            var go = new GameObject("Trophy Sparkle");
            go.transform.SetParent(root, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = Snack.StarSprite();
            sr.sortingOrder = 14;
            if (material) sr.sharedMaterial = material;
            sparkleSr[i] = sr;
        }
    }

    void StepSparkles(float dt)
    {
        for (int i = 0; i < MaxSparkles; i++)
        {
            ref P p = ref sparkles[i];
            var sr = sparkleSr[i];
            if (!p.on) { if (sr && sr.enabled) sr.enabled = false; continue; }
            p.t += dt;
            if (p.t >= p.life) { p.on = false; sr.enabled = false; continue; }
            float u = p.t / p.life;
            p.vel *= 1f - 2.2f * dt;
            p.pos += p.vel * dt;
            sr.enabled = true;
            float s = p.size * 2f * Mathf.Sin(Mathf.PI * Mathf.Min(1f, u * 1.2f));
            sr.transform.position = new Vector3(p.pos.x, p.pos.y, 0f);
            sr.transform.localScale = new Vector3(s, s, 1f);
            Color c = p.color;
            c.a = 1f - u * u;
            sr.color = c;
        }
    }

    // ------------------------------------------------------------------ sound and words

    void Play(AudioClip clip, float pitch, float vol)
    {
        if (!clip || !sfx) return;
        sfx.pitch = pitch;
        sfx.PlayOneShot(clip, volume * vol);
    }

    static string Line(GummyBody b, string apple, string tomato, string bean, string burger, string dino, string duck, string monster)
    {
        var sp = b ? b.GetComponent<BuddySpeech>() : null;
        switch (sp ? sp.voice : BuddySpeech.Voice.Apple)
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

    void LateUpdate() { long t0 = WhipProf.T(); LateUpdateTimed(); WhipProf.Add("PopUp.LateUpdate", t0); }
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

    static float Smooth(float u) { u = Mathf.Clamp01(u); return u * u * (3f - 2f * u); }

    // its own dice: the buddies' dice stay as they were
    static readonly System.Random Rand = new System.Random(61);
    static float Rnd(float a, float b) => a + (float)Rand.NextDouble() * (b - a);
    static Vector2 Rnd2(float r) => new Vector2(Rnd(-r, r), Rnd(-r, r));
    static string Pick(params string[] l) => l[Rand.Next(l.Length)];
}
