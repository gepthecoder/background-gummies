using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

/// <summary>
/// Your day, kept on this PC: how long you've been active today and how long you've been at it without a break (only
/// the time since your last mouse / keyboard input is read, never what you typed or which app you were in), how many
/// breaks you took, plus a few counts from the app (snacks eaten, things copied, throws, chat messages).
/// Starts over every day; saved to today.json in the app's data folder. The buddies use it to answer "how long have I
/// been working?" and "how's my day?", the coffee bean steams after a long stretch, and the first time the app runs
/// each day someone says good morning.
/// </summary>
[DisallowMultipleComponent]
public class WorkClock : MonoBehaviour
{
    [Tooltip("Mouse / keyboard input within this many seconds = you're working.")]
    public float activeWithin = 60f;
    [Tooltip("This long without any input = a break (the work stretch starts over). Seconds.")]
    public float breakAfter = 300f;
    [Tooltip("The first time the app runs each day, a buddy says good morning (good afternoon...) after this many seconds.")]
    public float greetAfter = 6f;

    public static WorkClock Instance { get; private set; }
    /// <summary>Tests: seconds since the last input / the clock.</summary>
    public static Func<float> IdleProbe;
    public static Func<DateTime> NowProbe;
    public static DateTime Now => NowProbe != null ? NowProbe() : DateTime.Now;

    /// <summary>Active seconds today.</summary>
    public float ActiveToday => day.activeSeconds;
    /// <summary>Active seconds since your last break (short pauses don't end it).</summary>
    public float Streak { get; private set; }
    public float LongestStreak => day.longestStreak;
    public int Breaks => day.breaks;
    /// <summary>Minutes since midnight when your last break ended (-1: none yet today), and how long it was (minutes).</summary>
    public int LastBreakAt => day.lastBreakAt;
    public int LastBreakLength => day.lastBreakLength;
    /// <summary>No input for breakAfter seconds: you're away.</summary>
    public bool Away { get; private set; }
    public int Snacks => day.snacks;
    public int Copies => day.copies;
    public int Throws => day.throws;
    public int Chats => day.chats;
    /// <summary>"HH:mm" you were first here today.</summary>
    public string FirstSeen => day.firstSeen;
    /// <summary>A buddy has already said good morning today.</summary>
    public bool Greeted { get => day.greeted; set { if (day.greeted != value) { day.greeted = value; dirty = true; } } }
    /// <summary>You came back from a break: how long it was (seconds).</summary>
    public event Action<float> BackFromBreak;

    public static string FilePath => Path.Combine(Application.persistentDataPath, "today.json");

    [Serializable]
    class Day
    {
        public string date = "", firstSeen = "";
        public float activeSeconds, longestStreak;
        public int breaks, snacks, copies, throws, chats, lastBreakAt = -1, lastBreakLength;
        public bool greeted;
        public string[] eaters = new string[0];
        public int[] eaten = new int[0];
    }

    Day day = new Day();
    bool dirty;
    float saveTimer, awaySince;
    readonly Dictionary<GummyBody, bool> wasHeld = new Dictionary<GummyBody, bool>();
    readonly Dictionary<GummyBody, float> letGo = new Dictionary<GummyBody, float>();

    void Awake()
    {
        Instance = this;
        Load();
        Roll(Now);
    }

    void OnEnable()
    {
        Instance = this;
        BuddyEater.AnyAte += OnAte;
        SnackDropper.Copied += OnCopied;
    }

    void OnDisable()
    {
        BuddyEater.AnyAte -= OnAte;
        SnackDropper.Copied -= OnCopied;
        if (dirty) Save();
        if (Instance == this) Instance = null;
    }

    void OnApplicationQuit() { if (dirty) Save(); }

    void Start()
    {
        if (!day.greeted) Invoke(nameof(MorningHello), greetAfter);
    }

    // ------------------------------------------------------------------ counting

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("Day.Update", t0); }
    void UpdateTimed()
    {
        DateTime now = Now;
        Roll(now);
        float dt = Mathf.Min(Time.unscaledDeltaTime, 0.5f); // a sleeping PC doesn't count as work
        float idle = IdleProbe != null ? IdleProbe() : DesktopInput.IdleSeconds;

        if (idle < activeWithin)
        {
            day.activeSeconds += dt;
            Streak += dt;
            if (Streak > day.longestStreak) day.longestStreak = Streak;
            dirty = true;
        }
        if (idle >= breakAfter && !Away)
        {
            // gone for a while: that's a break, the stretch is over
            Away = true;
            awaySince = Time.unscaledTime - idle;
            if (Streak >= 60f) { day.breaks++; dirty = true; }
            Streak = 0f;
        }
        else if (Away && idle < breakAfter)
        {
            Away = false;
            float length = Time.unscaledTime - awaySince;
            day.lastBreakAt = now.Hour * 60 + now.Minute;
            day.lastBreakLength = Mathf.RoundToInt(length / 60f);
            dirty = true;
            BackFromBreak?.Invoke(length);
        }

        // throws: it's flying fast just after you let go of it
        float t = Time.time;
        foreach (var b in GummyBody.All)
        {
            if (!b) continue;
            bool held = b.Held;
            if (wasHeld.TryGetValue(b, out bool was) && was && !held) letGo[b] = t;
            wasHeld[b] = held;
            if (letGo.TryGetValue(b, out float at) && t - at < 0.2f && b.Velocity.magnitude > 5f) { day.throws++; dirty = true; letGo.Remove(b); }
        }

        saveTimer += Time.unscaledDeltaTime;
        if (dirty && saveTimer > 30f) Save();
    }

    void OnAte(BuddyEater who, Snack s)
    {
        day.snacks++;
        string n = who ? Who(who.gameObject) : "?";
        int i = Array.IndexOf(day.eaters, n);
        if (i < 0)
        {
            Array.Resize(ref day.eaters, day.eaters.Length + 1);
            Array.Resize(ref day.eaten, day.eaten.Length + 1);
            i = day.eaters.Length - 1;
            day.eaters[i] = n;
        }
        day.eaten[i]++;
        dirty = true;
    }

    void OnCopied() { day.copies++; dirty = true; }

    /// <summary>A chat message was sent.</summary>
    public static void NoteChat()
    {
        if (!Instance) return;
        Instance.day.chats++;
        Instance.dirty = true;
    }

    /// <summary>Snacks this buddy ate today.</summary>
    public int EatenBy(GameObject buddy)
    {
        int i = Array.IndexOf(day.eaters, Who(buddy));
        return i >= 0 && i < day.eaten.Length ? day.eaten[i] : 0;
    }

    static string Who(GameObject g)
    {
        var p = g.GetComponent<BuddyProgress>();
        return p && !string.IsNullOrEmpty(p.saveName) ? p.saveName : g.name;
    }

    /// <summary>Pretend you've been at it this long (WhipIt > Buddy menu, to see the steam).</summary>
    public void AddStreak(float seconds)
    {
        Streak += seconds;
        day.activeSeconds += seconds;
        if (Streak > day.longestStreak) day.longestStreak = Streak;
        dirty = true;
    }

    // ------------------------------------------------------------------ the day

    // a new day: start over
    void Roll(DateTime now)
    {
        string date = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (day.date == date) return;
        day = new Day { date = date, firstSeen = now.ToString("HH:mm", CultureInfo.InvariantCulture) };
        Streak = 0f;
        dirty = true;
    }

    // the first time today: good morning (afternoon, evening) from whoever's awake, the apple first
    void MorningHello()
    {
        if (day.greeted) return;
        BuddySpeech who = null;
        foreach (var b in GummyBody.All)
        {
            var sp = b ? b.GetComponent<BuddySpeech>() : null;
            var br = b ? b.GetComponent<GummyBrain>() : null;
            if (!sp || (br && br.Current == GummyBrain.State.Asleep)) continue;
            if (!who || sp.voice == BuddySpeech.Voice.Apple) who = sp;
        }
        if (!who || BuddyChat.Open) return;
        if (who.Talking) { Invoke(nameof(MorningHello), 3f); return; } // busy saying something else: in a bit
        if (who.Say(ChatReplies.Greeting(who.voice), 0f, true)) Greeted = true;
    }

    void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var d = JsonUtility.FromJson<Day>(File.ReadAllText(FilePath));
            if (d != null)
            {
                if (d.eaters == null) d.eaters = new string[0];
                if (d.eaten == null || d.eaten.Length != d.eaters.Length) d.eaten = new int[d.eaters.Length];
                day = d;
            }
        }
        catch (Exception e) { Debug.LogWarning("[WhipIt] Couldn't read today.json: " + e.Message); }
    }

    void Save()
    {
        dirty = false;
        saveTimer = 0f;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonUtility.ToJson(day));
        }
        catch (Exception e) { Debug.LogWarning("[WhipIt] Couldn't write today.json: " + e.Message); }
    }

    /// <summary>"2H 05M", "45M", "1M" ("<1M" under a minute).</summary>
    public static string Duration(float seconds)
    {
        int m = Mathf.FloorToInt(seconds / 60f);
        if (m < 1) return "<1M";
        if (m < 60) return m + "M";
        return (m / 60) + "H " + (m % 60).ToString("00") + "M";
    }
}
