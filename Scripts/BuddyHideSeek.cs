using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Hide and seek: the buddies hide behind one of your windows (one that comes down to the floor they're on) or
/// behind the taskbar, and you look for them. Now and then one peeks out (or bobs up over the taskbar) and
/// sometimes giggles. Found: hover the cursor over the spot for a moment, move / minimize the window, or grab
/// the bit that's peeking out; a snack lures them out too. Nobody finds it in time: "BOO!".
/// Start: chat ("hide and seek"), WhipIt > Buddy > Play Hide And Seek, or they suggest it now and then.
/// </summary>
[RequireComponent(typeof(GummyBody))]
[DisallowMultipleComponent]
public class BuddyHideSeek : MonoBehaviour
{
    [Tooltip("Comes out by itself (\"BOO!\") after this long (seconds).")]
    public float hideFor = 60f;
    [Tooltip("Peeks out every this many seconds (min, max).")]
    public Vector2 peekEvery = new Vector2(7f, 14f);
    [Tooltip("Cursor held still over its hiding spot this long = found.")]
    public float hoverToFind = 0.6f;
    public int xpFound = 5;
    [Tooltip("Suggests a game by itself: chance per minute while it's bored and you're around.")]
    [Range(0f, 1f)] public float suggestChance = 0.06f;

    public enum Phase { None, Going, Hidden, Peeking, Found, Out }
    public Phase Current { get; private set; }
    /// <summary>Somebody is hiding (or on the way to a spot).</summary>
    public static bool Playing { get { foreach (var h in s_all) if (h.InGame) return true; return false; } }
    public bool InGame => Current == Phase.Going || Current == Phase.Hidden || Current == Phase.Peeking;
    /// <summary>true = you found it, false = it won ("BOO!").</summary>
    public event Action<bool> Done;
    /// <summary>Any buddy's game is over (who, true = you found it).</summary>
    public static event Action<GummyBody, bool> AnyDone;

    static readonly List<BuddyHideSeek> s_all = new List<BuddyHideSeek>();
    static float s_nextSuggest;

    GummyBody body;
    GummyBrain brain;
    BuddySpeech speech;
    BuddyProgress progress;

    // the spot
    long window;            // 0 = behind the taskbar
    float spotX, phaseAt, nextPeek, peekUntil, hoverFor, lastVisit;
    Vector2 lastCursor;
    readonly List<KeyValuePair<float, string>> lines = new List<KeyValuePair<float, string>>();

    void Awake()
    {
        body = GetComponent<GummyBody>();
        brain = GetComponent<GummyBrain>();
        speech = GetComponent<BuddySpeech>();
        progress = GetComponent<BuddyProgress>();
    }

    void OnEnable()
    {
        if (!s_all.Contains(this)) s_all.Add(this);
        if (s_nextSuggest <= 0f) s_nextSuggest = Time.time + 60f;
    }

    void OnDisable()
    {
        s_all.Remove(this);
        if (InGame) Reveal();
        Current = Phase.None;
    }

    // ------------------------------------------------------------------ starting a game

    /// <summary>Everyone who can goes and hides. Returns how many did.</summary>
    public static int StartGame(BuddyHideSeek caller = null)
    {
        if (Playing) return 0;
        int hid = 0;
        BuddyHideSeek first = null;
        foreach (var h in s_all.ToArray())
            if (h.TryHide(h == caller)) { hid++; if (!first) first = h; }
        if (hid == 0)
        {
            var who = caller ? caller : s_all.Count > 0 ? s_all[0] : null;
            if (who) who.Line(Pick("NO GOOD SPOTS :(", "NOWHERE TO HIDE!"), 0f);
            return 0;
        }
        first.Line(Pick("COUNT TO 10!", "HIDE AND SEEK!", "DON'T PEEK!"), 0.9f);
        foreach (var h in s_all) if (h.InGame && h != first) h.Line(Pick("NO PEEKING!", "HEHE", "READY?"), 2.1f);
        s_nextSuggest = Time.time + 300f;
        return hid;
    }

    bool TryHide(bool evenIfBusy)
    {
        if (InGame || !brain || !body.Grounded || body.Held || body.IsStuck) return false;
        if (brain.Current == GummyBrain.State.Asleep) return false;
        if (!evenIfBusy && !brain.IsFree) return false;
        if (!FindSpot(out window, out spotX)) return false;
        Current = Phase.Going;
        phaseAt = Time.time;
        lastVisit = -10f;
        hoverFor = 0f;
        body.HideWindow = window;
        UpdateRect();
        return true;
    }

    // a window that comes down to its floor and is big enough to cover it, or the taskbar
    bool FindSpot(out long id, out float x)
    {
        id = 0; x = 0f;
        Rect e = body.Extent, scr = ScreenBounds.ScreenRectAt(body.Center); // (its own screen)
        float floor = e.yMin, w = e.width, h = e.height, cx = body.Center.x;
        float lo = scr.xMin, hi = scr.xMax;
        if (body.OnPlatform) { lo = Mathf.Max(lo, body.SupportSpan.x); hi = Mathf.Min(hi, body.SupportSpan.y); }
        float best = 12f;
        var wins = DesktopPlatforms.Windows;
        for (int k = 0; k < wins.Count; k++)
        {
            WindowBox b = wins[k];
            if (!b.movable || b.screen || b.yMin > floor + 0.03f || b.yMax < floor + h + 0.15f) continue;
            float a = Mathf.Max(b.xMin, lo) + w * 0.5f + 0.1f, c = Mathf.Min(b.xMax, hi) - w * 0.5f - 0.1f;
            if (c < a) continue;
            float inset = Mathf.Min(0.6f * w + 0.2f, (c - a) * 0.5f); // a bit in from the edge it comes in by
            float sx = cx < a ? a + inset : cx > c ? c - inset : Mathf.Clamp(cx + (UnityEngine.Random.value - 0.5f) * w * 2f, a, c);
            float d = Mathf.Abs(sx - cx) + UnityEngine.Random.value * 1.5f;
            if (d >= best || SomethingBetween(cx, sx, floor, b.id)) continue;
            best = d; id = b.id; x = sx;
        }
        if (id != 0 && UnityEngine.Random.value < 0.8f) return true;

        // behind the taskbar (only from the real floor, and only if the taskbar is tall enough to cover it)
        bool onFloor = !body.OnPlatform && Mathf.Abs(floor - scr.yMin) < 0.05f;
        if (onFloor && TaskbarRoom(body.Center) > 0.85f * h)
        {
            float tx = Mathf.Clamp(cx + UnityEngine.Random.Range(1f, 3f) * (UnityEngine.Random.value < 0.5f ? -1f : 1f), scr.xMin + w, scr.xMax - w);
            if (!SomethingBetween(cx, tx, floor, 0)) { id = 0; x = tx; return true; }
        }
        return id != 0;
    }

    // how tall the taskbar under it is (world units; 0: none, or not one it can get behind)
    static float TaskbarRoom(Vector2 at)
    {
        if (ScreenBounds.Multi)
        {
            // its own screen's taskbar, if its floor is the lowest one (on a higher one there's no room under the floor)
            var sc = ScreenBounds.Screens[ScreenBounds.ScreenIndexAt(at)];
            if (Mathf.Abs(sc.work.yMin - ScreenBounds.WorldRect.yMin) > 0.02f) return 0f;
            return Mathf.Max(0f, sc.work.yMin - sc.full.yMin);
        }
        var cam = Camera.main;
        if (!cam) return 0f;
        float screenBottom = cam.transform.position.y - cam.orthographicSize;
        return Mathf.Max(0f, ScreenBounds.WorldRect.yMin - screenBottom);
    }

    bool SomethingBetween(float a, float b, float floor, long except)
    {
        if (!body.solidWindows && !ScreenBounds.Multi) return false;
        float lo = Mathf.Min(a, b), hi = Mathf.Max(a, b), y = floor + 0.05f;
        var wins = DesktopPlatforms.Windows;
        for (int k = 0; k < wins.Count; k++)
        {
            WindowBox w = wins[k];
            if (w.id != except && w.movable && (body.solidWindows || w.screen) && w.yMin <= y && w.yMax >= y && w.xMax > lo && w.xMin < hi) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ playing

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("BuddyHideSeek.Update", t0); }
    void UpdateTimed()
    {
        float now = Time.time;
        for (int i = 0; i < lines.Count; i++)
            if (now >= lines[i].Key) { if (speech) speech.Say(lines[i].Value, 0f, true); lines.RemoveAt(i--); }

        // bored? suggest a game (the first buddy decides for everyone)
        if (s_all.Count > 0 && s_all[0] == this && now > s_nextSuggest)
        {
            s_nextSuggest = now + 60f;
            bool around = (Grabber.CursorWorld - body.Center).magnitude < 8f;
            if (around && brain && brain.IsFree && !Playing && !BuddyChat.Open && UnityEngine.Random.value < suggestChance) StartGame(this);
        }

        if (!InGame) return;
        if (!UpdateRect()) { Found(Pick("HEY, MY WINDOW!", "WHERE'D IT GO?")); return; } // window closed / minimized / maximized

        // lured out by a snack, or you picked it up
        if (body.Held) { Found(Pick("YOU GOT ME!", "AW MAN")); return; }
        if (brain.Food.HasValue) { if (Current == Phase.Going) Abandon("SNACK!"); else Found(Pick("SNACK!!", "...WORTH IT")); return; }

        // cursor held over the spot
        Vector2 cur = Grabber.CursorWorld;
        Rect e = body.Extent;
        bool over = cur.x > e.xMin - 0.1f && cur.x < e.xMax + 0.1f && cur.y > e.yMin - 0.1f && cur.y < e.yMax + 0.15f;
        bool still = (cur - lastCursor).magnitude < 3f * Mathf.Max(Time.deltaTime, 1e-3f); // under ~3 units/s
        lastCursor = cur;
        hoverFor = over && still ? hoverFor + Time.deltaTime : over ? hoverFor : 0f;
        if (Current != Phase.Going && hoverFor >= hoverToFind) { Found(Pick("YOU FOUND ME!", "AW MAN", "HOW?!")); return; }

        // you moved the window away
        if (Current == Phase.Hidden && window != 0 && Exposed() > 0.6f) { Found(Pick("HEY! NO FAIR", "YOU FOUND ME!")); return; }

        if (now - phaseAt > hideFor) { Boo(); return; }
        brain.Linger(1.5f);

        switch (Current)
        {
            case Phase.Going:
                if (Mathf.Abs(body.Center.x - spotX) < 0.35f * body.size && brain.Current == GummyBrain.State.Idle)
                {
                    Current = Phase.Hidden;
                    nextPeek = now + UnityEngine.Random.Range(peekEvery.x, peekEvery.y);
                    if (window == 0) { body.RoomBelow = TaskbarRoom(body.Center); body.HoldRoom = true; } // sinks behind the taskbar and stays down
                }
                else GoTo(spotX, now);
                if (now - phaseAt > 12f) Abandon(null); // couldn't get there
                break;

            case Phase.Hidden:
                if (now > nextPeek) StartPeek(now);
                else if (Mathf.Abs(body.Center.x - spotX) > 0.5f * body.size && brain.Current == GummyBrain.State.Idle) GoTo(spotX, now); // nudged: back in
                break;

            case Phase.Peeking:
                if (now > peekUntil)
                {
                    Current = Phase.Hidden;
                    nextPeek = now + UnityEngine.Random.Range(peekEvery.x, peekEvery.y);
                    if (window != 0) GoTo(spotX, now);
                }
                break;
        }
    }

    void GoTo(float x, float now)
    {
        if (now - lastVisit < 0.5f || !brain.IsFree || !body.Grounded) return;
        if (brain.Visit(x)) lastVisit = now;
    }

    void StartPeek(float now)
    {
        Current = Phase.Peeking;
        peekUntil = now + UnityEngine.Random.Range(1.2f, 2.2f);
        if (window == 0)
        {
            // bob up until its eyes clear the taskbar edge
            float up = Mathf.Max(0.1f, TaskbarRoom(body.Center) - 0.4f * body.Extent.height);
            body.Hop(new Vector2(0f, GummyBody.JumpSpeed(up, body.gravity, body.Drag)), 0.8f);
            peekUntil = now + 1f;
        }
        else if (WindowRect(window, out Rect r))
        {
            // shuffle over to the nearer side until part of it shows
            float w = body.Extent.width;
            float left = r.xMin + 0.1f * w, right = r.xMax - 0.1f * w;
            float x = Mathf.Abs(body.Center.x - r.xMin) < Mathf.Abs(body.Center.x - r.xMax) ? left : right;
            Rect scr = ScreenBounds.ScreenRectAt(body.Center);
            if (x - w * 0.5f < scr.xMin || x + w * 0.5f > scr.xMax) x = x == left ? right : left;
            brain.Visit(x);
        }
        if (UnityEngine.Random.value < 0.3f) Line(Pick("HEHE", "SHH!", "*GIGGLE*"), 0.3f);
    }

    /// <summary>How much of it (0..1) is outside the window it hides behind.</summary>
    float Exposed()
    {
        Rect e = body.Extent, r = body.HideRect;
        float ow = Mathf.Max(0f, Mathf.Min(e.xMax, r.xMax) - Mathf.Max(e.xMin, r.xMin));
        float oh = Mathf.Max(0f, Mathf.Min(e.yMax, r.yMax) - Mathf.Max(e.yMin, r.yMin));
        return 1f - ow * oh / Mathf.Max(e.width * e.height, 1e-5f);
    }

    // keep the clip rect on the window (it may move); false if it's gone
    bool UpdateRect()
    {
        if (window == 0)
        {
            Rect scr = ScreenBounds.WorldRect;
            body.HideRect = Rect.MinMaxRect(scr.xMin - 10f, scr.yMin - 10f, scr.xMax + 10f, scr.yMin);
            return true;
        }
        if (!WindowRect(window, out Rect r)) return false;
        body.HideRect = r;
        return true;
    }

    static bool WindowRect(long id, out Rect r)
    {
        var wins = DesktopPlatforms.Windows;
        for (int k = 0; k < wins.Count; k++)
        {
            if (wins[k].id != id) continue;
            WindowBox b = wins[k];
            r = Rect.MinMaxRect(b.xMin, b.yMin, b.xMax, b.yMax);
            return b.movable;
        }
        r = default;
        return false;
    }

    // ------------------------------------------------------------------ the end

    void Found(string line)
    {
        bool wasHidden = Current == Phase.Hidden || Current == Phase.Peeking;
        Reveal();
        Current = Phase.Found;
        if (line != null)
        {
            Line(line, 0.15f);
            if (wasHidden && progress) progress.AddXp(xpFound, body.Center);
        }
        Done?.Invoke(true);
        AnyDone?.Invoke(body, true);
        GameOver();
    }

    // gave up on the game without being found (couldn't get to the spot, went for a snack)
    void Abandon(string line)
    {
        Reveal();
        Current = Phase.Out;
        if (line != null) Line(line, 0.1f);
        GameOver();
    }

    void Boo()
    {
        Reveal();
        Current = Phase.Out;
        Line("BOO!", 0.1f);
        if (brain) brain.Linger(2f);
        Done?.Invoke(false);
        AnyDone?.Invoke(body, false);
        GameOver(Pick("I WIN!", "TOO GOOD", "HEHEHE"));
    }

    // back in view: pops out from behind the taskbar; behind a solid window it squeezes out (usually on top)
    void Reveal()
    {
        if (speech) speech.Suppress(BuddySpeech.Topic.Escape, 1.5f); // squeezing out from behind the window is part of the game
        bool taskbar = window == 0 && body.RoomBelow > 0f;
        body.HideRect = default;
        body.HideWindow = 0;
        body.HoldRoom = false;
        if (taskbar && body.Extent.yMax < ScreenBounds.FloorAt(body.Center) + 0.6f * body.Extent.height)
            body.Hop(new Vector2(0f, GummyBody.JumpSpeed(TaskbarRoom(body.Center) + 0.4f * body.size, body.gravity, body.Drag)), 0.8f);
        else if (!body.Held && body.Grounded) body.Hop(new Vector2(0f, 3.5f * Mathf.Sqrt(body.size / 0.6f)), 0.6f);
    }

    void GameOver(string myLine = null)
    {
        if (myLine != null) Line(myLine, 1.6f);
        if (Playing) return;
        bool allFound = true;
        foreach (var h in s_all) if (h.Current == Phase.Out) allFound = false;
        if (allFound && s_all.Count > 1 && myLine == null) Line(Pick("YOU'RE GOOD!", "AGAIN?"), 1.8f);
    }

    void Line(string text, float delay)
    {
        if (!speech) return;
        if (delay <= 0f) { speech.Say(text, 0f, true); return; }
        lines.Add(new KeyValuePair<float, string>(Time.time + delay, text));
    }

    static string Pick(params string[] s) => s[UnityEngine.Random.Range(0, s.Length)];
}
