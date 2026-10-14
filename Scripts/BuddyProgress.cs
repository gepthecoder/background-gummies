using System;
using System.IO;
using UnityEngine;

/// <summary>
/// XP, levels and growth for a gummy buddy. Playing with it earns XP (grabbing, throwing, splatting, popping it
/// free, petting, throwing it onto windows, wobbling windows, eating snacks, and a trickle just for keeping it company). Levels grow it through stages:
/// seed -> sprout -> young green apple -> ripe red apple, morphing shape, size, colour and what grows on top.
/// Progress is saved to a small JSON file (see SavePath). Use "preview" in Play mode to scrub through the stages.
/// </summary>
[DefaultExecutionOrder(-100)] // pick the right stage before GummyBody builds itself
[RequireComponent(typeof(GummyBody))]
[DisallowMultipleComponent]
public class BuddyProgress : MonoBehaviour
{
    [Serializable]
    public class Stage
    {
        public string name;
        public int fromLevel;
        public GummyBody.BodyShape shape;
        public float size;
        public Color color, rim;
        public GummyBody.Topper topper;
    }

    public Stage[] stages =
    {
        new Stage { name = "Seed", fromLevel = 1, shape = GummyBody.BodyShape.Seed, size = 0.34f,
            color = new Color(0.60f, 0.40f, 0.24f), rim = new Color(0.30f, 0.18f, 0.10f), topper = GummyBody.Topper.None },
        new Stage { name = "Sprout", fromLevel = 3, shape = GummyBody.BodyShape.Seed, size = 0.40f,
            color = new Color(0.60f, 0.40f, 0.24f), rim = new Color(0.30f, 0.18f, 0.10f), topper = GummyBody.Topper.Sprout },
        new Stage { name = "Young apple", fromLevel = 6, shape = GummyBody.BodyShape.Apple, size = 0.48f,
            color = new Color(0.56f, 0.78f, 0.26f), rim = new Color(0.25f, 0.42f, 0.10f), topper = GummyBody.Topper.StemLeaf },
        new Stage { name = "Apple", fromLevel = 10, shape = GummyBody.BodyShape.Apple, size = 0.60f,
            color = new Color(0.90f, 0.22f, 0.21f), rim = new Color(0.45f, 0.08f, 0.08f), topper = GummyBody.Topper.StemLeaf },
    };

    [Header("XP")]
    public int xpGrab = 2;
    public int xpThrow = 3;
    [Tooltip("When it splats onto an edge after you threw or dropped it.")]
    public int xpSplat = 2;
    [Tooltip("Pulling it off an edge until it pops free.")]
    public int xpPop = 5;
    [Tooltip("While it's happy from petting.")]
    public int xpPetPerSecond = 2;
    [Tooltip("Throwing it onto a window.")]
    public int xpWindowLanding = 1;
    [Tooltip("Knocking or bouncing one of your windows (WindowWobble).")]
    public int xpWindowBonk = 2;
    [Tooltip("Eating a snack (you get one every time you copy something).")]
    public int xpSnack = 5;
    [Tooltip("Just for keeping it company (while it's awake).")]
    public int xpPerMinuteAwake = 1;
    [Tooltip("XP for level 1 -> 2; each level needs extraXpPerLevel more.")]
    public int baseXpPerLevel = 20;
    public int extraXpPerLevel = 15;

    [Header("Preview (Play mode)")]
    [Tooltip("Scrub through the growth stages with the slider below (doesn't touch your saved progress).")]
    public bool preview;
    [Range(0f, 1f)] public float previewGrowth;

    [Header("Sounds (WhipIt > Setup assigns them)")]
    public AudioClip levelUpClip;
    public AudioClip stageUpClip;
    [Range(0f, 1f)] public float volume = 0.6f;

    public int Level { get; private set; } = 1;
    /// <summary>XP inside the current level.</summary>
    public int Xp { get; private set; }
    public int TotalXp { get; private set; }
    public int XpForNext => baseXpPerLevel + extraXpPerLevel * (Level - 1);
    public float Progress01 => Mathf.Clamp01(Xp / (float)Mathf.Max(1, XpForNext));
    public int StageIndex => StageFor(Level);
    public string StageName => stages[StageIndex].name;

    /// <summary>amount, where (world)</summary>
    public event Action<int, Vector2> XpGained;
    /// <summary>new level, reached a new stage</summary>
    public event Action<int, bool> LeveledUp;

    [Tooltip("Save file name (each buddy has its own).")]
    public string saveName = "buddy_apple";

    /// <summary>The apple's save file (the WhipIt > Buddy menu uses it).</summary>
    public static string SavePath => PathFor("buddy_apple");
    public static string PathFor(string name) => Path.Combine(Application.persistentDataPath, name + ".json");
    string MySave => PathFor(string.IsNullOrEmpty(saveName) ? "buddy_apple" : saveName);

    [Serializable]
    struct SaveData { public int level, xp, totalXp; }

    GummyBody body;
    GummyBrain brain;
    AudioSource audioSrc;
    bool wasHeld, wasOnWindow, dirty, wasPreview;
    float lastGrab = -10f, lastRelease = -10f, lastSplat = -10f, lastWindow = -10f, petTimer, awakeTimer, saveTimer;
    const float ThrowWindow = 2.5f; // splats / window landings count this long after you let go (not its own hops)
    float appliedGrowth = -1f, lastPreview = -1f;

    void Awake()
    {
        body = GetComponent<GummyBody>();
        brain = GetComponent<GummyBrain>();
        audioSrc = gameObject.AddComponent<AudioSource>();
        audioSrc.playOnAwake = false;
        audioSrc.spatialBlend = 0f;
        Load();
        ApplyGrowth(0f); // before GummyBody.Awake builds the body (execution order)
    }

    void OnEnable()
    {
        body.Splat += OnSplat;
        body.Pop += OnPop;
    }

    void OnDisable()
    {
        body.Splat -= OnSplat;
        body.Pop -= OnPop;
        if (dirty) Save();
    }

    void OnApplicationQuit() { if (dirty) Save(); }

    // ------------------------------------------------------------------ XP sources

    void OnSplat(Vector2 at, Vector2 normal, float speed)
    {
        if (Time.time - lastSplat < 1f || Time.time - lastRelease > ThrowWindow) return;
        lastSplat = Time.time;
        AddXp(xpSplat, at);
    }

    void OnPop(Vector2 at, Vector2 dir) => AddXp(xpPop, body.Center);

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("Stage.Update", t0); }
    void UpdateTimed()
    {
        float now = Time.time, dt = Time.deltaTime;
        bool annoyed = brain && brain.Annoyed;

        // grab / throw
        bool held = body.Held;
        if (held && !wasHeld && now - lastGrab > 1.5f && !annoyed) { lastGrab = now; AddXp(xpGrab, body.Center); }
        if (!held && wasHeld)
        {
            lastRelease = now;
            if (body.Velocity.magnitude > 5f) AddXp(xpThrow, body.Center);
        }
        wasHeld = held;

        // petting (it's happy and you're over it)
        if (brain && brain.Mood == CuteFace.Expression.Happy && body.Hovered)
        {
            petTimer += dt;
            if (petTimer >= 1f) { petTimer -= 1f; AddXp(xpPetPerSecond, body.Center); }
        }
        else petTimer = 0f;

        // you threw it onto a window
        bool onWindow = body.OnPlatform;
        if (onWindow && !wasOnWindow && now - lastWindow > 3f && now - lastRelease < ThrowWindow) { lastWindow = now; AddXp(xpWindowLanding, body.Center); }
        wasOnWindow = onWindow;

        // company
        if (!brain || brain.Current != GummyBrain.State.Asleep)
        {
            awakeTimer += dt;
            if (awakeTimer >= 60f) { awakeTimer -= 60f; AddXp(xpPerMinuteAwake, body.Center); }
        }

        // preview slider
        if (preview)
        {
            if (!Mathf.Approximately(previewGrowth, lastPreview)) { lastPreview = previewGrowth; ApplyGrowth(0.35f); }
        }
        else if (wasPreview) { lastPreview = -1f; appliedGrowth = -1f; ApplyGrowth(0.35f); }
        wasPreview = preview;

        saveTimer += dt;
        if (dirty && saveTimer > 20f) Save();
    }

    public void AddXp(int amount, Vector2 at)
    {
        if (amount <= 0) return;
        int oldStage = StageIndex;
        Xp += amount;
        TotalXp += amount;
        bool leveled = false;
        while (Xp >= XpForNext) { Xp -= XpForNext; Level++; leveled = true; }
        dirty = true;
        XpGained?.Invoke(amount, at);

        if (leveled)
        {
            bool newStage = StageIndex != oldStage;
            if (audioSrc && (newStage ? stageUpClip : levelUpClip)) audioSrc.PlayOneShot(newStage ? stageUpClip : levelUpClip, volume);
            LeveledUp?.Invoke(Level, newStage);
            Save();
            ApplyGrowth(newStage ? 1.4f : 0.6f);
        }
        else ApplyGrowth(0.5f);
    }

    // ------------------------------------------------------------------ growth

    int StageFor(int level)
    {
        int s = 0;
        for (int i = 0; i < stages.Length; i++) if (level >= stages[i].fromLevel) s = i;
        return s;
    }

    /// <summary>0..stages-1 continuous: stage index + how far it is towards the next stage.</summary>
    float Growth()
    {
        if (preview) return previewGrowth * (stages.Length - 1);
        int s = StageFor(Level);
        if (s >= stages.Length - 1) return s;
        float from = stages[s].fromLevel, to = stages[s + 1].fromLevel;
        float lv = Level + Progress01;
        return s + Mathf.Clamp01((lv - from) / Mathf.Max(1f, to - from));
    }

    void ApplyGrowth(float duration)
    {
        float g = Growth();
        bool sameStage = Mathf.FloorToInt(g + 1e-4f) == Mathf.FloorToInt(appliedGrowth + 1e-4f);
        if (sameStage && Mathf.Abs(g - appliedGrowth) < 0.002f) return; // a new stage always applies
        appliedGrowth = g;
        int s = Mathf.Clamp(Mathf.FloorToInt(g + 1e-4f), 0, stages.Length - 1);
        float f = g - s;
        Stage cur = stages[s], next = stages[Mathf.Min(s + 1, stages.Length - 1)];
        float size = Mathf.Lerp(cur.size, next.size, f * 0.6f); // grows most of the way, the rest comes with the new stage
        size = GiantPotion.Resize(body, size); // (giant just now: stays giant, and shrinks back to this size after)
        body.Morph(cur.shape, size, duration);
        body.SetLook(cur.color, cur.rim, cur.topper);
    }

    // ------------------------------------------------------------------ save

    void Load()
    {
        try
        {
            if (!File.Exists(MySave)) return;
            var d = JsonUtility.FromJson<SaveData>(File.ReadAllText(MySave));
            Level = Mathf.Max(1, d.level);
            Xp = Mathf.Max(0, d.xp);
            TotalXp = Mathf.Max(0, d.totalXp);
        }
        catch (Exception e) { Debug.LogWarning("[WhipIt] Couldn't read buddy save: " + e.Message); }
    }

    void Save()
    {
        dirty = false;
        saveTimer = 0f;
        WriteSave(Level, Xp, TotalXp, saveName);
    }

    /// <summary>Write a save file (also used by the WhipIt > Buddy editor menu).</summary>
    public static void WriteSave(int level, int xp, int totalXp, string name = "buddy_apple")
    {
        try
        {
            string path = PathFor(string.IsNullOrEmpty(name) ? "buddy_apple" : name);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(new SaveData { level = level, xp = xp, totalXp = totalXp }));
        }
        catch (Exception e) { Debug.LogWarning("[WhipIt] Couldn't write buddy save: " + e.Message); }
    }
}
