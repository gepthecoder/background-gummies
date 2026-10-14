using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A window a buddy broke into (WindowInfestation). The window is drawn over by a candy panel in the buddy's colour,
/// exactly its size, following it wherever it goes: a jelly rim, a candy-cane top bar with its name ("TOMATO'S WINDOW",
/// drag it to move the real window), tabs (ME, PLAY, TOYS, WORLD, GROW) and Candy Land on the left: its little room,
/// with gumdrop hills, lollipops, a rainbow and frosting to stand on, where it hops about amazed. ME is its card (level,
/// medals, best friend, windows it's been in); the other tabs are everything the Editor's Buddy menu does, as buttons
/// (BuddyActions). Drag it out (or OUT, or chat "come out") and the panel melts away: your window's there again.
/// Only the window's position and size are used; nothing in it is read or touched (it's just covered).
/// </summary>
[DefaultExecutionOrder(9)] // after the buddies have moved: places its room and puppets its buddy
[DisallowMultipleComponent]
public class InfestedWindow : MonoBehaviour, IGrabTarget
{
    public long WindowId { get; private set; }
    public GummyBody Intruder { get; private set; }
    /// <summary>The window (world units), where the panel is drawn.</summary>
    public Rect Box { get; private set; }
    /// <summary>The buddy's room in Candy Land (world).</summary>
    public Rect Room { get; private set; }
    public enum Phase { Flood, On, Melt }
    public Phase State { get; private set; }
    public int Tab { get; private set; } = BuddyActions.TabMe;
    /// <summary>The last thing a button said (tests).</summary>
    public string Toast { get; private set; } = "";
    public int Presses { get; private set; }

    WindowInfestation home;
    GummyBrain brain;
    CuteFace face;
    BuddySpeech speech;
    BuddyLayer layer;
    BuddyProgress progress;
    Color tint;
    Vector2 contact;
    float phaseT, nextHop, nextLook, nextLine, nextData, toastT = -1f;
    int seed;
    Rect lastRoom;
    bool roomSet;

    // ------------------------------------------------------------------ life

    public void Init(WindowInfestation manager, WindowBox w, GummyBody intruder, Vector2 at, Material material)
    {
        home = manager;
        WindowId = w.id;
        Box = Rect.MinMaxRect(w.xMin, w.yMin, w.xMax, w.yMax);
        Intruder = intruder;
        contact = at;
        mat = material;
        seed = (int)(w.id * 7919 % 100003) + 17;
        brain = intruder.GetComponent<GummyBrain>();
        face = intruder.GetComponent<CuteFace>();
        speech = intruder.GetComponent<BuddySpeech>();
        layer = intruder.GetComponent<BuddyLayer>();
        progress = intruder.GetComponent<BuddyProgress>();
        tint = intruder.color;
        State = Phase.Flood;
        phaseT = 0f;
        PixelSize();
        Layout();
        TakeIn(true);
        Bake(true);
        GrabTargets.Add(this);
    }

    void OnDisable()
    {
        GrabTargets.Remove(this);
        if (dragging) { DesktopPlatforms.EndMove(WindowId, false); dragging = false; }
        LetGo(false);
    }

    void OnDestroy()
    {
        foreach (var o in made) if (o) Destroy(o);
        made.Clear();
    }

    // the buddy goes in: its room, puppet, amazed (pop: it bursts in from where it broke through)
    void TakeIn(bool pop)
    {
        var b = Intruder;
        if (!b) return;
        if (brain && brain.Current == GummyBrain.State.Asleep) brain.WakeUp();
        b.Room = Room;
        lastRoom = Room;
        roomSet = true;
        if (layer) layer.InsideWindow = WindowId;
        if (face) face.Amazed = true;
        if (brain) brain.TowerStart();
        if (pop)
        {
            Vector2 c = Room.center;
            float half = b.size * 0.5f;
            Vector2 p = new Vector2(Mathf.Clamp(contact.x, Room.xMin + half, Room.xMax - half), Mathf.Clamp(contact.y, Room.yMin + half, Room.yMax - half));
            b.Teleport(p);
            Vector2 v = (c - p);
            b.Hop(new Vector2(Mathf.Clamp(v.x * 2.2f, -5f, 5f), Mathf.Max(4f, 6f * Mathf.Sqrt(b.size / 0.6f))), 0.6f);
            if (speech) speech.Say(Pick("WHOA!!", "WHERE AM I?!", "OOOOH!", "I'M IN!"), 1.6f, true);
            nextLine = Time.time + Random.Range(2.5f, 4f);
        }
        nextHop = Time.time + Random.Range(1.2f, 2f);
        nextLook = Time.time + 0.5f;
    }

    // the buddy's out (dragged out, OUT, someone else came in, the window's gone): its room, the puppet, the face back
    void LetGo(bool keepHeld)
    {
        var b = Intruder;
        if (!b) return;
        b.Room = new Rect();
        roomSet = false;
        if (layer) layer.InsideWindow = 0;
        if (face) { face.Amazed = false; face.LookAt = null; }
        if (brain && brain.Towering && !keepHeld) brain.TowerEnd();
    }

    /// <summary>Out it comes. held: you're pulling it out (it stays in your hand, at at); else it hops out (dir: which way,
    /// zero = over the top). Then the panel melts.</summary>
    public void Eject(bool held, Vector2 at, Vector2 dir)
    {
        if (State == Phase.Melt) return;
        var b = Intruder;
        State = Phase.Melt;
        phaseT = 0f;
        Bake(true);
        if (dragging) { DesktopPlatforms.EndMove(WindowId, false); dragging = false; }
        pressed = -99;
        LetGo(held);
        if (b)
        {
            if (held)
            {
                // in your hand, just outside the window (on the side you pulled it out)
                float r = b.size * 0.6f;
                Vector2 p = at;
                if (p.x < Box.xMin) p.x = Mathf.Min(p.x, Box.xMin - r); else if (p.x > Box.xMax) p.x = Mathf.Max(p.x, Box.xMax + r);
                if (p.y < Box.yMin) p.y = Mathf.Min(p.y, Box.yMin - r); else if (p.y > Box.yMax) p.y = Mathf.Max(p.y, Box.yMax + r);
                b.Teleport(p);
            }
            else
            {
                // out over the edge it's pushed through: just outside the window, a little hop away from it
                Vector2 d = dir.sqrMagnitude < 1e-10f ? Vector2.up : dir.normalized;
                float r = b.size * 0.6f + 0.1f;
                Vector2 p = Box.center;
                if (Mathf.Abs(d.x) > Mathf.Abs(d.y)) p = new Vector2(d.x > 0f ? Box.xMax + r : Box.xMin - r, Mathf.Clamp(b.Center.y, Box.yMin + r, Box.yMax - r));
                else p = new Vector2(Mathf.Clamp(b.Center.x, Box.xMin + r, Box.xMax - r), d.y > 0f ? Box.yMax + r : Box.yMin - r);
                Rect w = ScreenBounds.WorldRect;
                p.x = Mathf.Clamp(p.x, w.xMin + r, w.xMax - r);
                p.y = Mathf.Clamp(p.y, w.yMin + r, w.yMax - r);
                b.Teleport(p);
                b.Hop(new Vector2(d.x * 3f, d.y > -0.5f ? 5f : 0f), 0.6f);
            }
        }
        if (home) home.Out(this, b);
    }

    // ------------------------------------------------------------------ every frame

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("InfestedWindow.Update", t0); }
    void UpdateTimed()
    {
        float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
        phaseT += dt;
        PixelSize();

        // the window: where it is now (while you drag it: where it's going)
        bool found = false;
        WindowBox w = default;
        foreach (var o in DesktopPlatforms.Windows) if (o.id == WindowId && !o.screen) { w = o; found = true; break; }
        if (State != Phase.Melt)
        {
            if (!found || !w.movable || !BigEnough(w)) { Eject(false, Vector2.zero, Vector2.up); }
            else if (dragging) Box = new Rect(dragFrom.x + dragOff.x, dragFrom.y + dragOff.y, dragFrom.width, dragFrom.height);
            else Box = Rect.MinMaxRect(w.xMin, w.yMin, w.xMax, w.yMax);
        }
        else if (found) Box = Rect.MinMaxRect(w.xMin, w.yMin, w.xMax, w.yMax);

        Layout();
        if (State != Phase.Melt) Puppet(dt);

        if (State == Phase.Flood && phaseT >= FloodTime) { State = Phase.On; phaseT = 0f; Bake(true); }
        if (State == Phase.Melt && phaseT >= MeltTime) { Destroy(gameObject); return; }

        // hover: which part of the panel the cursor's on (re-drawn when it changes)
        int hv = Hovered ? HitTest(Grabber.CursorWorld) : -99;
        if (hv != hover) { hover = hv; dirty = true; }
        if (confirmAt >= 0 && Time.unscaledTime - confirmAt > 3f) { confirmAt = -1f; confirmIdx = -1; dirty = true; }
        if (Time.unscaledTime >= nextData) // (its card and the open tab's labels: redrawn when they change)
        {
            nextData = Time.unscaledTime + 0.25f;
            string data = CardData();
            if (data != lastData) { lastData = data; dirty = true; }
        }
        Bake(false);
        Show(dt);
    }

    static bool BigEnough(WindowBox w) => w.xMax - w.xMin >= MinW && w.yMax - w.yMin >= MinH;
    public const float MinW = 3.2f, MinH = 2.4f;

    // the buddy in its room: the room moves with the window; it hops about, looks at everything, says things
    void Puppet(float dt)
    {
        var b = Intruder;
        if (!b || !b.isActiveAndEnabled) { Eject(false, Vector2.zero, Vector2.up); return; }
        if (b.Held)
        {
            // pulled out past the edge: out it comes, in your hand
            Vector2 cur = b.HeldAt;
            float m = 0.3f;
            if (cur.x < Box.xMin - m || cur.x > Box.xMax + m || cur.y < Box.yMin - m || cur.y > Box.yMax + m) { Eject(true, cur, Vector2.zero); return; }
        }
        if (roomSet)
        {
            Vector2 d = new Vector2(Room.xMin - lastRoom.xMin, Room.yMin - lastRoom.yMin);
            if (d.sqrMagnitude > 1e-10f && !b.Held) b.Shift(d);
            lastRoom = Room;
        }
        b.Room = Room;
        if (b.Held) return;
        if (brain && !brain.Towering) brain.TowerStart(); // (after you let go of it in there)
        float now = Time.time;
        if (face && now >= nextLook)
        {
            nextLook = now + Random.Range(0.6f, 1.6f);
            // looks around: the buttons, the sky, you
            float r = Random.value;
            face.LookAt = r < 0.35f ? (Vector2?)null : r < 0.7f ? new Vector2(Random.Range(Box.xMin, Box.xMax), Random.Range(Box.yMin, Box.yMax)) : new Vector2(Random.Range(Room.xMin, Room.xMax), Room.yMax + 1f);
        }
        if (now >= nextHop && b.Grounded && State == Phase.On)
        {
            nextHop = now + Random.Range(1.4f, 3.2f);
            float s = Mathf.Sqrt(b.size / 0.6f);
            float tx = Random.Range(Room.xMin + b.size * 0.6f, Room.xMax - b.size * 0.6f);
            float vx = Mathf.Clamp((tx - b.Center.x) * 1.4f, -3.5f, 3.5f);
            bool big = Random.value < 0.25f;
            b.Hop(new Vector2(vx, (big ? 7.5f : 4.5f) * s), 0.6f);
            if (big && speech && Random.value < 0.4f) speech.Say(Pick("WHEEE!", "SO BOUNCY!", "IT'S SO SOFT!"), 0f, false);
        }
        if (speech && now >= nextLine && State == Phase.On)
        {
            nextLine = now + Random.Range(7f, 14f);
            if (Random.value < 0.75f) speech.Say(Amazed(speech.voice), 0f, false);
        }
    }

    static string Amazed(BuddySpeech.Voice v)
    {
        if (v == BuddySpeech.Voice.Duck) return Pick("QUACK... IT'S ALL CANDY", "QUAAACK! BUTTONS!", "MAMA? I'M IN THE WINDOW!");
        if (v == BuddySpeech.Voice.Monster) return Pick("WE... ARE INSIDE", "CANDY. FOR US", "WE SEE EVERYTHING");
        return Pick("IT'S ALL CANDY IN HERE!", "I CAN SEE EVERYTHING!", "SO MANY BUTTONS!", "I'M IN THE COMPUTER!", "IS THIS... THE INSIDE?",
                    "THE OTHERS WON'T BELIEVE THIS", "IT SMELLS LIKE SUGAR", "I GOT OUT OF THE DESKTOP!", "LOOK AT ME! I'M A WINDOW!",
                    "I CAN SEE THE CODE!", "WHAT DOES THIS BUTTON DO?");
    }

    static string Pick(params string[] s) => s[Random.Range(0, s.Length)];

    // ------------------------------------------------------------------ you (the panel is something you can grab)

    public bool Hovered { get; set; }
    public bool Held => grabbing;
    bool grabbing, dragging;
    Rect dragFrom;
    Vector2 dragStart, dragOff;
    int pressed = -99, hover = -99;
    float confirmAt = -1f;
    int confirmIdx = -1;

    public bool Contains(Vector2 p)
    {
        if (State == Phase.Melt || !Box.Contains(p)) return false;
        if (Occlusion.Hidden(p, Occlusion.LayerOf(WindowId))) return false; // (a window in front of it: the click is that window's)
        // something in front you can grab (its buddy, a snack, a buddy on top): that, not the panel
        var all = GrabTargets.All;
        for (int i = 0; i < all.Count; i++)
        {
            var t = all[i];
            if (t == null || ReferenceEquals(t, this) || t is InfestedWindow) continue;
            if (t is Object o && !o) continue;
            if (t.Contains(p)) return false;
        }
        return true;
    }

    public void BeginGrab(Vector2 p)
    {
        grabbing = true;
        int h = HitTest(p);
        if (h == HitTitle && State == Phase.On && DesktopPlatforms.BeginMove(WindowId))
        {
            dragging = true;
            dragFrom = Box;
            dragStart = p;
            dragOff = Vector2.zero;
        }
        else pressed = h;
        dirty = true;
    }

    public void DragTo(Vector2 p)
    {
        if (!dragging) return;
        dragOff = p - dragStart;
        // (kept on the screens: at least its top bar)
        Rect w = ScreenBounds.WorldRect;
        dragOff.x = Mathf.Clamp(dragOff.x, w.xMin - dragFrom.xMax + 1f, w.xMax - dragFrom.xMin - 1f);
        dragOff.y = Mathf.Clamp(dragOff.y, w.yMin - dragFrom.yMax + 0.5f, w.yMax - dragFrom.yMax);
        if (!DesktopPlatforms.MoveTo(WindowId, dragOff)) { dragging = false; DesktopPlatforms.EndMove(WindowId, false); }
    }

    public void EndGrab()
    {
        grabbing = false;
        if (dragging) { DesktopPlatforms.EndMove(WindowId, false); dragging = false; }
        int h = HitTest(Grabber.CursorWorld);
        if (pressed != -99 && h == pressed && State == Phase.On) Press(pressed);
        pressed = -99;
        dirty = true;
    }

    /// <summary>Press a part of the panel (HitOut, HitTab0 + t, or a button index on the open tab). Tests and the chat use it too.</summary>
    public void Press(int what)
    {
        Presses++;
        if (home) home.Click();
        if (what == HitOut) { Eject(false, Vector2.zero, Vector2.up); return; }
        if (what == HitPrev) { page = Mathf.Max(0, page - 1); dirty = true; return; }
        if (what == HitNext) { page++; dirty = true; return; }
        if (what >= HitTab0 && what < HitTab0 + BuddyActions.Tabs.Length) { SetTab(what - HitTab0); return; }
        if (what < 0) return;
        var acts = BuddyActions.InTab(Tab);
        if (what >= acts.Count) return;
        var a = acts[what];
        if (a.confirm && confirmIdx != what) { confirmIdx = what; confirmAt = Time.unscaledTime; SetToast("SURE? TAP AGAIN"); dirty = true; return; }
        confirmIdx = -1;
        confirmAt = -1f;
        string r;
        try { r = a.run(Ctx()); }
        catch (System.Exception e) { Debug.LogWarning("[WhipIt] " + a.label() + ": " + e.Message); r = "OOPS"; }
        SetToast(string.IsNullOrEmpty(r) ? "OK!" : r);
        if (speech && Random.value < 0.5f) speech.Say(Pick("OOH!", "I PRESSED IT!", "DID YOU SEE THAT?", "AGAIN! AGAIN!"), 0f, false);
        dirty = true;
    }

    public void SetTab(int t)
    {
        if (t == Tab) return;
        Tab = t;
        page = 0;
        confirmIdx = -1;
        dirty = true;
        if (t == BuddyActions.TabMe && brain) brain.Act(new Vector2(0.9f, 1.12f), 0.7f); // (it stands up proud: that's it on the card)
    }

    void SetToast(string s) { Toast = s; toastT = 0f; toastDirty = true; }

    BuddyActions.Ctx Ctx()
    {
        // beside the window on the floor (the side with more room), and over it
        Rect scr = ScreenBounds.ScreenRectAt(Box.center);
        float left = Box.xMin - scr.xMin, right = scr.xMax - Box.xMax;
        float bx = right >= left ? Mathf.Min(Box.xMax + 1.6f, scr.xMax - 1f) : Mathf.Max(Box.xMin - 1.6f, scr.xMin + 1f);
        return new BuddyActions.Ctx { inside = Intruder, beside = new Vector2(bx, scr.yMin + 0.5f), above = new Vector2(Box.center.x, Box.yMax + 1f) };
    }

    // ------------------------------------------------------------------ layout (UI pixels: one is a font pixel, like the bubbles)

    public const int HitNone = -1, HitTitle = -2, HitOut = -3, HitPrev = -4, HitNext = -5, HitStage = -6, HitTab0 = 1000;
    const float FloodTime = 0.6f, MeltTime = 0.7f;
    const int Rim = 4, TitleH = 15, TabH = 12, BtnH = 13, Gap = 3, MinBtnW = 64;

    float fontPx = 0.028f, screenPx;
    int W, H;                       // the panel in UI pixels
    RectInt titleR, outR, stageR, areaR, contentR;
    readonly List<RectInt> tabR = new List<RectInt>();
    readonly List<RectInt> btnR = new List<RectInt>();
    RectInt prevR, nextR;
    int page, pages = 1, firstBtn;
    bool stacked;

    void Layout()
    {
        int w = Mathf.Max(40, Mathf.RoundToInt(Box.width / fontPx)), h = Mathf.Max(30, Mathf.RoundToInt(Box.height / fontPx));
        if (w != W || h != H) { W = w; H = h; dirty = true; }
        int inX0 = Rim + 1, inX1 = W - Rim - 1, inY0 = Rim + 1, inY1 = H - Rim - 1; // (top-down rows: 0 = top)
        titleR = new RectInt(inX0, inY0, inX1 - inX0, TitleH);
        outR = new RectInt(inX1 - 24, inY0 + 2, 22, TitleH - 4);
        int bodyY0 = inY0 + TitleH + 2, bodyY1 = inY1 - 1;
        int bodyW = inX1 - inX0 - 4, bodyH = bodyY1 - bodyY0;
        // Candy Land on the left (or along the bottom if the window is narrow)
        int stageW = Mathf.Clamp(Mathf.RoundToInt(bodyW * 0.38f), 56, 260);
        stacked = bodyW - stageW - Gap < 92;
        if (!stacked)
        {
            stageR = new RectInt(inX0 + 2, bodyY0, stageW, bodyH);
            areaR = new RectInt(stageR.xMax + Gap, bodyY0, inX1 - 2 - (stageR.xMax + Gap), bodyH);
        }
        else
        {
            int sh = Mathf.Clamp(Mathf.RoundToInt(bodyH * 0.45f), 34, 140);
            stageR = new RectInt(inX0 + 2, bodyY1 - sh, bodyW, sh);
            areaR = new RectInt(inX0 + 2, bodyY0, bodyW, bodyH - sh - Gap);
        }
        // tabs along the top of the area, the content under them
        tabR.Clear();
        int tx = areaR.x;
        for (int i = 0; i < BuddyActions.Tabs.Length; i++)
        {
            int tw = PixelFont.Measure(BuddyActions.Tabs[i]) + 10;
            tabR.Add(new RectInt(tx, areaR.y, tw, TabH));
            tx += tw + 2;
        }
        contentR = new RectInt(areaR.x, areaR.y + TabH, areaR.width, Mathf.Max(10, areaR.height - TabH));
        // buttons: a grid, pages if they don't fit
        btnR.Clear();
        if (Tab != BuddyActions.TabMe)
        {
            int n = BuddyActions.InTab(Tab).Count;
            int cols = Mathf.Max(1, (contentR.width - 8 + Gap) / (MinBtnW + Gap));
            int bw = (contentR.width - 8 - (cols - 1) * Gap) / cols;
            int rows = Mathf.Max(1, (contentR.height - 8 + Gap) / (BtnH + Gap));
            int per = cols * rows;
            if (n > per) { rows = Mathf.Max(1, (contentR.height - 8 - 12 + Gap) / (BtnH + Gap)); per = Mathf.Max(1, cols * rows); }
            pages = Mathf.Max(1, (n + per - 1) / per);
            page = Mathf.Clamp(page, 0, pages - 1);
            firstBtn = page * per;
            for (int i = firstBtn; i < Mathf.Min(n, firstBtn + per); i++)
            {
                int k = i - firstBtn, c = k % cols, r = k / cols;
                btnR.Add(new RectInt(contentR.x + 4 + c * (bw + Gap), contentR.y + 4 + r * (BtnH + Gap), bw, BtnH));
            }
            prevR = new RectInt(contentR.x + contentR.width / 2 - 22, contentR.yMax - 12, 14, 9);
            nextR = new RectInt(contentR.x + contentR.width / 2 + 8, contentR.yMax - 12, 14, 9);
        }
        // the buddy's room: Candy Land above its frosting
        int ground = Ground();
        Room = Rect.MinMaxRect(WorldX(stageR.x + 2), WorldY(stageR.yMax - ground), WorldX(stageR.xMax - 2), WorldY(stageR.y + 2));
    }

    int Ground() => Mathf.Clamp(stageR.height / 7, 5, 12);

    // UI pixel (x, row from the top) -> world
    float WorldX(int x) => Box.xMin + x * (Box.width / Mathf.Max(W, 1));
    float WorldY(int row) => Box.yMax - row * (Box.height / Mathf.Max(H, 1));
    Vector2Int ToUi(Vector2 p) => new Vector2Int(Mathf.FloorToInt((p.x - Box.xMin) / (Box.width / Mathf.Max(W, 1))), Mathf.FloorToInt((Box.yMax - p.y) / (Box.height / Mathf.Max(H, 1))));

    /// <summary>What's at this world point: a button index on the open tab, HitTab0 + t, HitTitle, HitOut, HitPrev / HitNext,
    /// HitStage, or HitNone.</summary>
    public int HitTest(Vector2 p)
    {
        if (!Box.Contains(p)) return HitNone;
        Vector2Int u = ToUi(p);
        if (In(outR, u)) return HitOut;
        if (In(titleR, u)) return HitTitle;
        for (int i = 0; i < tabR.Count; i++) if (In(tabR[i], u)) return HitTab0 + i;
        for (int i = 0; i < btnR.Count; i++) if (In(btnR[i], u)) return firstBtn + i;
        if (pages > 1 && In(prevR, u)) return HitPrev;
        if (pages > 1 && In(nextR, u)) return HitNext;
        if (In(stageR, u)) return HitStage;
        return HitNone;
    }

    static bool In(RectInt r, Vector2Int u) => u.x >= r.x && u.x < r.xMax && u.y >= r.y && u.y < r.yMax;

    /// <summary>Where a button is (world), for tests: its middle.</summary>
    public Vector2 ButtonCenter(int index)
    {
        int k = index - firstBtn;
        if (k < 0 || k >= btnR.Count) return Box.center;
        var r = btnR[k];
        return new Vector2(WorldX(r.x) + (WorldX(r.xMax) - WorldX(r.x)) * 0.5f, WorldY(r.y) + (WorldY(r.yMax) - WorldY(r.y)) * 0.5f);
    }
    public Vector2 TabCenter(int t) { var r = tabR[t]; return new Vector2((WorldX(r.x) + WorldX(r.xMax)) * 0.5f, (WorldY(r.y) + WorldY(r.yMax)) * 0.5f); }
    public Vector2 TitleCenter => new Vector2((WorldX(titleR.x) + WorldX(outR.x)) * 0.5f, (WorldY(titleR.y) + WorldY(titleR.yMax)) * 0.5f);

    void PixelSize()
    {
        var cam = Camera.main;
        if (!cam || Screen.height <= 0) { screenPx = 0f; return; }
        screenPx = 2f * cam.orthographicSize / Screen.height;
        int k = Mathf.Max(1, Mathf.RoundToInt(3 * ScreenBounds.MainScreenPixels / 1080f));
        fontPx = screenPx * k;
    }

    // ------------------------------------------------------------------ drawing

    Material mat;
    SpriteRenderer panelSr, toastSr;
    Texture2D panelTex, toastTex;
    Sprite panelSprite, toastSprite;
    Color32[] baked, shown;
    readonly List<Object> made = new List<Object>();
    readonly List<SpriteMask> masks = new List<SpriteMask>();
    readonly List<Rect> occl = new List<Rect>();
    bool dirty = true, toastDirty;
    string lastData = "";
    const int PanelOrder = 2;
    static Sprite s_white;

    void Bake(bool force)
    {
        if (!dirty && !force && State == Phase.On) return;
        if (dirty || force || baked == null || baked.Length != W * H)
        {
            baked = Paint();
            dirty = false;
        }
        Color32[] src = baked;
        if (State == Phase.Flood) src = Flood(Mathf.Clamp01(phaseT / FloodTime));
        else if (State == Phase.Melt) src = Melt(Mathf.Clamp01(phaseT / MeltTime));
        Upload(src);
    }

    void Upload(Color32[] px)
    {
        if (!panelSr)
        {
            var go = new GameObject("Infested Window Panel");
            go.transform.SetParent(transform, false);
            panelSr = go.AddComponent<SpriteRenderer>();
            panelSr.sortingOrder = PanelOrder;
            panelSr.maskInteraction = SpriteMaskInteraction.VisibleOutsideMask;
            if (mat) panelSr.sharedMaterial = mat;
            made.Add(go);
        }
        if (!panelTex || panelTex.width != W || panelTex.height != H)
        {
            if (panelSprite) Destroy(panelSprite);
            if (panelTex) Destroy(panelTex);
            panelTex = new Texture2D(W, H, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point, name = "InfestedWindow" };
            panelSprite = null;
            made.Add(panelTex);
        }
        panelTex.SetPixels32(px);
        panelTex.Apply(false, false);
        if (!panelSprite)
        {
            panelSprite = Sprite.Create(panelTex, new Rect(0, 0, W, H), Vector2.zero, 1f, 0, SpriteMeshType.FullRect);
            made.Add(panelSprite);
            if (mat) panelSr.sharedMaterial = SpriteMats.For(mat, panelTex); // (its own copy: SpriteMats)
        }
        panelSr.sprite = panelSprite;
    }

    void Show(float dt)
    {
        if (!panelSr) return;
        panelSr.enabled = true;
        panelSr.transform.position = new Vector3(Box.xMin, Box.yMin, 0f);
        panelSr.transform.localScale = new Vector3(Box.width / Mathf.Max(W, 1), Box.height / Mathf.Max(H, 1), 1f);

        // windows in front of this one hide those parts of it
        occl.Clear();
        Occlusion.Occluders(Occlusion.LayerOf(WindowId), Box, occl, 8);
        while (masks.Count < occl.Count) masks.Add(MakeMask());
        for (int i = 0; i < masks.Count; i++)
        {
            bool on = i < occl.Count;
            masks[i].enabled = on;
            if (!on) continue;
            masks[i].transform.position = new Vector3(occl[i].center.x, occl[i].center.y, 0f);
            masks[i].transform.localScale = new Vector3(occl[i].width, occl[i].height, 1f);
        }

        // what a button said: a little candy ribbon at the top of the area, fading
        if (toastT >= 0f) toastT += dt;
        if (toastDirty) { toastDirty = false; BakeToast(); }
        if (toastSr)
        {
            float a = toastT < 0f ? 0f : toastT < 2.2f ? 1f : Mathf.Clamp01(1f - (toastT - 2.2f) / 0.5f);
            toastSr.enabled = a > 0.01f && State != Phase.Melt;
            if (toastSr.enabled)
            {
                int tw = toastTex.width, th = toastTex.height;
                float sx = Box.width / Mathf.Max(W, 1), sy = Box.height / Mathf.Max(H, 1);
                int cx = contentR.x + contentR.width / 2 - tw / 2, cy = contentR.yMax - th - 3;
                toastSr.transform.position = new Vector3(WorldX(cx), WorldY(cy + th), 0f);
                toastSr.transform.localScale = new Vector3(sx, sy, 1f);
                toastSr.color = new Color(1f, 1f, 1f, a);
            }
        }
    }

    SpriteMask MakeMask()
    {
        if (!s_white)
        {
            var t = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "InfestMask" };
            var c = new Color32[16];
            for (int i = 0; i < 16; i++) c[i] = new Color32(255, 255, 255, 255);
            t.SetPixels32(c);
            t.Apply(false, true);
            s_white = Sprite.Create(t, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4f);
        }
        var go = new GameObject("Infested Window Mask");
        go.transform.SetParent(transform, false);
        var m = go.AddComponent<SpriteMask>();
        m.sprite = s_white;
        m.isCustomRangeActive = true;
        m.backSortingOrder = PanelOrder - 1;
        m.frontSortingOrder = PanelOrder + 1;
        made.Add(go);
        return m;
    }

    void BakeToast()
    {
        if (string.IsNullOrEmpty(Toast)) return;
        Color32[] px = Ribbon(Toast, tint, out int w, out int h);
        if (!toastSr)
        {
            var go = new GameObject("Infested Window Toast");
            go.transform.SetParent(transform, false);
            toastSr = go.AddComponent<SpriteRenderer>();
            toastSr.sortingOrder = PanelOrder + 1;
            toastSr.maskInteraction = SpriteMaskInteraction.VisibleOutsideMask;
            if (mat) toastSr.sharedMaterial = mat;
            made.Add(go);
        }
        if (toastTex) Destroy(toastTex);
        if (toastSprite) Destroy(toastSprite);
        toastTex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point, name = "InfestToast" };
        toastTex.SetPixels32(px);
        toastTex.Apply(false, false);
        toastSprite = Sprite.Create(toastTex, new Rect(0, 0, w, h), Vector2.zero, 1f, 0, SpriteMeshType.FullRect);
        if (mat) toastSr.sharedMaterial = SpriteMats.For(mat, toastTex);
        toastSr.sprite = toastSprite;
    }

    // the panel appearing: jelly floods in from where it broke through, a wobbly bright edge
    Color32[] Flood(float u)
    {
        if (shown == null || shown.Length != baked.Length) shown = new Color32[baked.Length];
        Vector2Int c = ToUi(contact);
        float maxR = Mathf.Sqrt(W * W + H * H), r = maxR * (1f - (1f - u) * (1f - u) * (1f - u));
        Color32 edge = Lerp32(Lighten(tint, 0.55f), new Color32(255, 255, 255, 255), 0.3f);
        for (int y = 0; y < H; y++)
        {
            int row = H - 1 - y;
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                float dx = x - c.x, dy = row - c.y, d = Mathf.Sqrt(dx * dx + dy * dy);
                float wob = 3f * Mathf.Sin(Mathf.Atan2(dy, dx) * 7f + u * 9f);
                float k = r + wob - d;
                if (k < 0f) { shown[i] = new Color32(0, 0, 0, 0); continue; }
                shown[i] = baked[i].a == 0 ? baked[i] : k < 3f ? edge : baked[i];
            }
        }
        return shown;
    }

    // melting away: a smooth curtain of goo slides down (faster in its drips), stretching jelly strands behind it, fading
    Color32[] Melt(float u)
    {
        if (shown == null || shown.Length != baked.Length) shown = new Color32[baked.Length];
        float fade = 1f - u * u * u, k = u * u;
        Color32 goo = Lerp32(tint, Color.white, 0.12f), gooLight = Lerp32(tint, Color.white, 0.45f);
        var clear = new Color32(0, 0, 0, 0);
        for (int x = 0; x < W; x++)
        {
            float s = 0.62f + 0.18f * Mathf.Sin(x * 0.07f + seed) + 0.12f * Mathf.Sin(x * 0.19f + seed * 0.37f);
            float drip = Mathf.Max(0f, Mathf.Sin(x * 0.31f + seed * 0.11f));
            drip *= drip; drip *= drip;
            int sh = Mathf.RoundToInt(k * H * (s + 0.35f * drip));
            int top = H - 1 - sh, strand = Mathf.RoundToInt(sh * 0.45f * drip);
            for (int y = 0; y < H; y++)
            {
                int sy = y + sh; // (y up: what's drawn here came from higher up)
                Color32 c = sy < H ? baked[sy * W + x] : clear;
                if (sy >= H && y <= top + strand && drip > 0.2f) c = y == top + strand ? gooLight : goo;
                c.a = (byte)(c.a * fade);
                shown[y * W + x] = c;
            }
        }
        return shown;
    }

    // ------------------------------------------------------------------ painting (pure: everything from the state)

    string CardData()
    {
        if (!Intruder) return "";
        string s = progress ? progress.Level + "/" + progress.Xp : "";
        s += "|" + (home ? home.BestFriendOf(Intruder) : "") + "|" + (home ? home.VisitsOf(Intruder) : 0);
        if (Trophies.Current && progress) for (int f = 0; f < Trophies.FeatCount; f++) s += Trophies.Current.Tier(progress.saveName, (Trophies.Feat)f);
        foreach (var a in BuddyActions.InTab(Tab)) s += a.label();
        return s;
    }

    Color32[] Paint()
    {
        var cv = new Canvas(W, H);
        var pal = new Pal(tint);
        var white = new Color32(255, 255, 255, 255);

        // the jelly: a dark edge, the rim (light at the top, deeper at the bottom, glossy), a bevel, the inside
        cv.RoundRect(0, 0, W, H, 3, pal.outline);
        for (int row = 1; row < H - 1; row++)
        {
            float v = row / (float)H;
            Color32 c = v < 0.5f ? Lerp32(pal.jellyLight, pal.jelly, v * 2f) : Lerp32(pal.jelly, pal.jellyDeep, (v - 0.5f) * 2f);
            cv.RoundRectRow(1, 1, W - 1, H - 1, 2, row, c);
        }
        for (int x = 4; x < W - 4; x++) { cv.Blend(x, 1, new Color32(255, 255, 255, 110)); if (((x / 3) % 6) != 5) cv.Blend(x, 2, new Color32(255, 255, 255, 150)); }
        int glossH = Mathf.Max(5, H * 2 / 3);
        for (int row = 4; row < glossH; row++) cv.Blend(1, row, new Color32(255, 255, 255, (byte)(110 - 90 * row / glossH)));
        for (int x = 3; x < W - 3; x++) cv.Blend(x, H - 2, new Color32(0, 0, 0, 40));
        cv.RoundRect(Rim, Rim, W - Rim, H - Rim, 2, pal.jellyDark);
        cv.RoundRect(Rim + 1, Rim + 1, W - Rim - 1, H - Rim - 1, 1, pal.bg);
        Dots(cv, new RectInt(Rim + 1, Rim + 1, W - 2 * Rim - 2, H - 2 * Rim - 2), Lerp32(pal.bg, pal.jellyLight, 0.3f), 9);

        // jelly dripping from the top bar (behind Candy Land and the tabs: it shows between them)
        var t = titleR;
        var rng = new System.Random(seed);
        for (int x = t.x + 2; x < t.xMax - 3; x += 5 + rng.Next(12))
        {
            int len = 3 + rng.Next(8), wd = 2 + rng.Next(2);
            for (int k = 0; k < len; k++) for (int j = 0; j < wd; j++) cv.Set(x + j, t.yMax + k, j == wd - 1 ? Lerp32(pal.jelly, pal.jellyDark, 0.45f) : pal.jelly);
            cv.Disc(x + wd * 0.5f - 0.5f, t.yMax + len, wd * 0.5f + 0.7f, pal.jelly);
            cv.Set(x, t.yMax + len - 1, Lerp32(pal.jelly, white, 0.6f));
        }

        // the top bar: a candy stick (stripes, round: light on top, deeper underneath), its name plate, OUT
        for (int row = t.y; row < t.yMax; row++)
        {
            float v = (row - t.y) / (float)Mathf.Max(1, t.height - 1);
            float lite = v < 0.35f ? (0.35f - v) * 1.2f : 0f, dark = v > 0.6f ? (v - 0.6f) * 0.75f : 0f;
            for (int x = t.x; x < t.xMax; x++)
            {
                Color32 c = ((x + (t.yMax - row)) / 4) % 2 == 0 ? pal.jelly : pal.cream;
                cv.Set(x, row, Lerp32(Lerp32(c, white, lite), pal.jellyDark, dark));
            }
        }
        for (int x = t.x; x < t.xMax; x++) { cv.Set(x, t.y, Lerp32(pal.cream, white, 0.7f)); cv.Set(x, t.yMax - 1, pal.jellyDark); }
        string title = (speech ? speech.Name : "BUDDY") + "'S WINDOW";
        int plateX = t.x + 2, plateY = t.y + 2, plateH = t.height - 4;
        int maxTitle = outR.x - 6 - (plateX + 16);
        while (title.Length > 4 && PixelFont.Measure(title) > maxTitle) title = title.Substring(0, title.Length - 1);
        int plateW = 16 + PixelFont.Measure(title) + 6;
        cv.RoundRect(plateX, plateY, plateX + plateW, plateY + plateH, 4, pal.jellyDark);
        cv.RoundRect(plateX + 1, plateY + 1, plateX + plateW - 1, plateY + plateH - 1, 3, pal.cream);
        for (int x = plateX + 3; x < plateX + plateW - 3; x++) cv.Blend(x, plateY + 1, new Color32(255, 255, 255, 200));
        Face(cv, plateX + 8f, plateY + plateH * 0.5f - 0.5f, 3.6f, pal, false);
        cv.Text(title, plateX + 15, t.y + (t.height - PixelFont.GlyphH) / 2, pal.ink);
        Pill(cv, outR, new Color32(255, 92, 132, 255), "OUT", hover == HitOut, pressed == HitOut, true);

        // Candy Land: its room
        Land(cv, stageR, pal);

        // the content (a soft candy card) and the tabs over it: the open one is the card's colour, it opens into it
        var cr = contentR;
        cv.RoundRect(cr.x, cr.y, cr.xMax, cr.yMax, 3, pal.jellyDark);
        cv.RoundRect(cr.x + 1, cr.y + 1, cr.xMax - 1, cr.yMax - 1, 2, pal.panel);
        Dots(cv, new RectInt(cr.x + 2, cr.y + 3, cr.width - 4, cr.height - 5), Lerp32(pal.panel, pal.jellyLight, 0.2f), 11);
        Sprinkles(cv, new RectInt(cr.x + 3, cr.y + 3, cr.width - 6, cr.height - 6), 0.0022f, seed + 7, 130);
        for (int x = cr.x + 3; x < cr.xMax - 3; x++) cv.Blend(x, cr.y + 1, new Color32(255, 255, 255, 170));
        for (int i = 0; i < tabR.Count; i++)
        {
            var r = tabR[i];
            bool sel = i == Tab, hv = hover == HitTab0 + i;
            Color32 fill = sel ? pal.panel : hv ? Lerp32(pal.jelly, white, 0.45f) : Lerp32(pal.jelly, white, 0.2f);
            int y0 = sel ? r.y - 1 : r.y + 1, y1 = r.yMax + (sel ? 1 : 0);
            cv.RoundRectTop(r.x, y0, r.xMax, y1, 3, pal.jellyDark);
            cv.RoundRectTop(r.x + 1, y0 + 1, r.xMax - 1, y1, 2, fill);
            for (int x = r.x + 3; x < r.xMax - 3; x++) cv.Blend(x, y0 + 1, new Color32(255, 255, 255, (byte)(sel ? 200 : 120)));
            int ty = y0 + 1 + (r.yMax - y0 - 1 - PixelFont.GlyphH) / 2;
            if (sel) cv.Label(BuddyActions.Tabs[i], r.x + 5, ty, pal.jellyDark, Lerp32(pal.jellyLight, white, 0.3f));
            else if (Light(fill)) cv.Label(BuddyActions.Tabs[i], r.x + 5, ty, pal.ink, Lerp32(fill, white, 0.5f));
            else cv.Label(BuddyActions.Tabs[i], r.x + 5, ty, white, pal.jellyDark);
            if (sel) for (int x = r.x + 1; x < r.xMax - 1; x++) cv.Set(x, cr.y, pal.panel);
        }

        if (Tab == BuddyActions.TabMe) Card(cv, cr, pal);
        else Buttons(cv, pal);
        return cv.px;
    }

    // a sky, twinkles, a rainbow, cotton candy clouds, gumdrop hills, lollipops, a candy cane, frosting on a cookie to stand on
    void Land(Canvas cv, RectInt r, Pal pal)
    {
        var rng = new System.Random(seed + 11);
        var white = new Color32(255, 255, 255, 255);
        int ground = Ground(), gy = r.yMax - ground;
        cv.RoundRect(r.x, r.y, r.xMax, r.yMax, 3, pal.jellyDark);
        // the sky: lilac up top, candy pink, a warm glow at the horizon (a hint of its colour)
        Color32 skyTop = Lerp32(new Color32(190, 168, 255, 255), pal.body, 0.15f), skyMid = Lerp32(new Color32(255, 184, 214, 255), pal.body, 0.1f), skyLow = new Color32(255, 240, 226, 255);
        for (int row = r.y + 1; row < r.yMax - 1; row++)
        {
            float v = (row - r.y) / (float)Mathf.Max(1, gy - r.y);
            Color32 sky = v < 0.55f ? Lerp32(skyTop, skyMid, v / 0.55f) : Lerp32(skyMid, skyLow, Mathf.Clamp01((v - 0.55f) / 0.45f));
            cv.RoundRectRow(r.x + 1, r.y + 1, r.xMax - 1, r.yMax - 1, 2, row, sky);
        }
        // twinkles
        int stars = 2 + r.width * (gy - r.y) / 700;
        for (int k = 0; k < stars; k++)
        {
            int sx = r.x + 4 + rng.Next(Mathf.Max(1, r.width - 8)), sy = r.y + 3 + rng.Next(Mathf.Max(1, (gy - r.y) / 2));
            Twinkle(cv, sx, sy, rng.Next(3) == 0 ? 2 : 1, new Color32(255, 255, 255, 230));
        }
        // the rainbow
        Color32[] bands = { new Color32(255, 120, 165, 255), new Color32(255, 178, 110, 255), new Color32(255, 230, 110, 255), new Color32(130, 222, 160, 255), new Color32(125, 185, 255, 255), new Color32(185, 140, 255, 255) };
        float rcx = r.x + r.width * 0.5f, rr = Mathf.Min(r.width * 0.46f, (gy - r.y) * 0.9f);
        float bw = Mathf.Max(1.5f, rr / 11f);
        for (int row = r.y + 1; row < gy; row++)
            for (int x = r.x + 1; x < r.xMax - 1; x++)
            {
                float d = Mathf.Sqrt((x - rcx) * (x - rcx) + (row - gy) * (row - gy));
                int b = Mathf.FloorToInt((rr - d) / bw);
                if (b >= 0 && b < bands.Length) { var c = bands[b]; c.a = 150; cv.Blend(x, row, c); }
                else if (b == bands.Length) cv.Blend(x, row, new Color32(255, 255, 255, 60));
            }
        // cotton candy clouds: pink underneath, a white top
        int clouds = 1 + r.width / 60;
        for (int k = 0; k < clouds; k++)
        {
            float cx = r.x + 10 + (float)rng.NextDouble() * (r.width - 20), cy = r.y + 7 + (float)rng.NextDouble() * Mathf.Max(4, (gy - r.y) * 0.3f);
            float s = 0.8f + (float)rng.NextDouble() * 0.5f;
            Color32 under = new Color32(255, 196, 222, 255), puff = new Color32(255, 252, 254, 255);
            for (int j = 0; j < 4; j++) cv.Disc(cx + (j - 1.5f) * 4.2f * s, cy + 1.2f + (j % 2 == 0 ? 0f : -2f * s), (3.2f + (j % 2) * 1.3f) * s, under);
            for (int j = 0; j < 4; j++) cv.Disc(cx + (j - 1.5f) * 4.2f * s - 0.6f, cy + (j % 2 == 0 ? 0f : -2f * s) - 0.6f, (3.2f + (j % 2) * 1.3f) * s - 0.6f, puff);
        }
        // gumdrop hills: a pale row far away, then the near ones (outlined, sugared, lit from the top left)
        for (int layer = 0; layer < 2; layer++)
        {
            int hills = layer == 0 ? 2 + r.width / 70 : 2 + r.width / 50;
            for (int k = 0; k < hills; k++)
            {
                float hx = r.x + (k + (layer == 0 ? 0.0f : 0.5f)) * r.width / hills + (float)(rng.NextDouble() - 0.5) * 8f;
                float hr = Mathf.Clamp(r.width / hills * (layer == 0 ? 0.7f : 0.55f), 7f, layer == 0 ? 38f : 30f) * (0.8f + (float)rng.NextDouble() * 0.4f);
                float hh = hr * (layer == 0 ? 1.15f : 0.95f);
                Color32 hc = bands[rng.Next(bands.Length)];
                if (layer == 0) hc = Lerp32(hc, Lerp32(new Color32(255, 214, 230, 255), pal.body, 0.1f), 0.55f);
                Color32 edge = Darken(hc, 0.3f);
                int bottom = gy + 1;
                for (int row = Mathf.FloorToInt(bottom - hh) - 1; row < bottom; row++)
                    for (int x = Mathf.FloorToInt(hx - hr) - 1; x <= Mathf.CeilToInt(hx + hr) + 1; x++)
                    {
                        if (x <= r.x || x >= r.xMax - 1 || row <= r.y) continue;
                        float dx = (x - hx) / hr, dy = (bottom - row) / hh;
                        float e = dx * dx + dy * dy;
                        if (e > 1.12f) continue;
                        if (e > 1f) { if (layer == 1) cv.Set(x, row, edge); continue; }
                        bool lit = dx < -0.15f && dy > 0.5f && e < 0.75f, shade = dx > 0.45f && e > 0.55f;
                        cv.Set(x, row, lit ? Lighten(hc, 0.4f) : shade ? Darken(hc, 0.12f) : hc);
                    }
                if (layer == 1)
                {
                    cv.Blend(Mathf.RoundToInt(hx - hr * 0.35f), Mathf.RoundToInt(bottom - hh * 0.75f), new Color32(255, 255, 255, 230));
                    for (int j = 0; j < (int)hr; j++) // sugar
                    {
                        int sx = Mathf.RoundToInt(hx + ((float)rng.NextDouble() * 2f - 1f) * hr * 0.7f), sy = Mathf.RoundToInt(bottom - (float)rng.NextDouble() * hh * 0.6f);
                        cv.Blend(sx, sy, new Color32(255, 255, 255, 150));
                    }
                }
            }
        }
        // lollipops (swirls) on sticks, a candy cane
        int pops = r.width >= 80 ? 2 : 1;
        for (int k = 0; k < pops; k++)
        {
            int lx = r.x + (k == 0 ? r.width / 5 : r.width * 4 / 5);
            int top = Mathf.Max(r.y + 9, gy - Mathf.Clamp(r.height / 2, 14, 46));
            for (int row = top; row < gy + 1; row++) { cv.Set(lx, row, new Color32(252, 250, 246, 255)); cv.Set(lx + 1, row, new Color32(214, 204, 196, 255)); }
            float lr = Mathf.Clamp(r.width / 13f, 4.5f, 10f);
            Color32 lc = bands[(k * 3 + 0) % bands.Length];
            cv.Disc(lx + 0.5f, top, lr + 1f, Darken(lc, 0.35f));
            for (int row = Mathf.FloorToInt(top - lr); row <= Mathf.CeilToInt(top + lr); row++)
                for (int x = Mathf.FloorToInt(lx + 0.5f - lr); x <= Mathf.CeilToInt(lx + 0.5f + lr); x++)
                {
                    float dx = x - (lx + 0.5f), dy = row - top, d = Mathf.Sqrt(dx * dx + dy * dy);
                    if (d > lr) continue;
                    float sw = Mathf.Atan2(dy, dx) / (2f * Mathf.PI) + d / (lr * 0.55f);
                    cv.Set(x, row, (Mathf.FloorToInt(sw * 2f) & 1) == 0 ? lc : white);
                }
            cv.Blend(lx - Mathf.RoundToInt(lr * 0.4f), top - Mathf.RoundToInt(lr * 0.45f), new Color32(255, 255, 255, 230));
            cv.Blend(lx - Mathf.RoundToInt(lr * 0.4f) + 1, top - Mathf.RoundToInt(lr * 0.45f), new Color32(255, 255, 255, 140));
        }
        if (r.width >= 70 && gy - r.y > 30) Cane(cv, r.x + r.width * 62 / 100, gy, Mathf.Clamp((gy - r.y) / 4, 12, 26));
        // the ground: frosting (white, scalloped drips) on a cookie with crumbs, sprinkles on top
        Color32 cookie = new Color32(232, 176, 116, 255), cookieDark = new Color32(196, 136, 82, 255), frost = new Color32(255, 250, 246, 255), frostShade = Lerp32(new Color32(246, 226, 230, 255), pal.body, 0.08f);
        for (int x = r.x + 1; x < r.xMax - 1; x++)
        {
            int fy = gy + Mathf.RoundToInt(0.9f * Mathf.Sin(x * 0.5f + seed) + 0.6f * Mathf.Sin(x * 0.19f + 1f));
            int thick = 2 + Mathf.Max(0, Mathf.RoundToInt(2.4f * Mathf.Sin(x * 0.42f + seed * 0.3f))) + (ground >= 9 ? 1 : 0);
            for (int row = fy; row < r.yMax - 1; row++)
            {
                if (row < fy + thick) cv.Set(x, row, row == fy ? white : row == fy + thick - 1 ? frostShade : frost);
                else cv.Set(x, row, Lerp32(cookie, cookieDark, Mathf.Clamp01((row - fy - thick) / (float)Mathf.Max(1, ground - 2))));
            }
        }
        for (int k = 0; k < r.width * ground / 14; k++) // crumbs
        {
            int x = r.x + 2 + rng.Next(Mathf.Max(1, r.width - 4)), row = gy + 4 + rng.Next(Mathf.Max(1, ground - 5));
            if (row < r.yMax - 1) cv.Blend(x, row, new Color32(150, 92, 50, 150));
        }
        Sprinkles(cv, new RectInt(r.x + 2, gy - 1, r.width - 4, 3), 0.11f, seed + 3, 255);
        // glass: a soft shadow under its top edge, a shine down the left
        for (int x = r.x + 2; x < r.xMax - 2; x++) { cv.Blend(x, r.y + 1, new Color32(0, 0, 0, 45)); cv.Blend(x, r.y + 2, new Color32(0, 0, 0, 20)); }
        for (int row = r.y + 3; row < gy - 2; row++) cv.Blend(r.x + 1, row, new Color32(255, 255, 255, 70));
    }

    // a candy cane stuck in the ground (stripes along it, hooked at the top)
    static void Cane(Canvas cv, int x, int gy, int h)
    {
        Color32 red = new Color32(232, 58, 82, 255), white = new Color32(255, 255, 255, 255), edge = new Color32(150, 30, 50, 255);
        int top = gy - h;
        for (int row = top; row <= gy; row++)
            for (int j = -1; j <= 1; j++)
            {
                Color32 c = ((row + j + 64) / 2) % 2 == 0 ? red : white;
                cv.Set(x + j, row, j == 1 ? Lerp32(c, edge, 0.3f) : c);
            }
        for (int row = top - 1; row <= gy; row++) cv.Set(x + 2, row, edge);
        // the hook: a half ring over to the left
        float hx = x - 3f, hy = top, R = 3f;
        for (int row = Mathf.FloorToInt(hy - R - 2); row <= Mathf.CeilToInt(hy); row++)
            for (int xx = Mathf.FloorToInt(hx - R - 2); xx <= Mathf.CeilToInt(hx + R + 2); xx++)
            {
                float dx = xx - hx, dy = row - hy, d = Mathf.Sqrt(dx * dx + dy * dy);
                if (dy > 0.5f) continue;
                if (Mathf.Abs(d - R) > 2f) continue;
                if (Mathf.Abs(d - R) > 1.3f) { cv.Set(xx, row, edge); continue; }
                float a = Mathf.Atan2(-dy, dx);
                cv.Set(xx, row, (Mathf.FloorToInt(a * 2.6f) & 1) == 0 ? red : white);
            }
        int tipX = Mathf.RoundToInt(hx - R);
        for (int row = top; row <= top + 2; row++) { cv.Set(tipX - 1, row, row == top + 2 ? edge : red); cv.Set(tipX, row, white); cv.Set(tipX + 1, row, red); }
        for (int j = -1; j <= 1; j++) cv.Set(tipX + j, top + 3, edge);
        cv.Blend(x - 1, top + 2, new Color32(255, 255, 255, 200));
    }

    // a little gummy face (its icon, its portrait): a glossy disc in its colour, eyes, a mouth (amazed: wide open)
    static void Face(Canvas cv, float cx, float cy, float r, Pal pal, bool amazed)
    {
        var white = new Color32(255, 255, 255, 255);
        var dark = new Color32(40, 26, 22, 255);
        cv.Disc(cx, cy, r + 1f, pal.outline);
        cv.Disc(cx, cy, r, pal.jelly);
        for (int row = Mathf.FloorToInt(cy - r); row <= Mathf.CeilToInt(cy + r); row++)
            for (int x = Mathf.FloorToInt(cx - r); x <= Mathf.CeilToInt(cx + r); x++)
            {
                float dx = x - cx, dy = row - cy, d2 = dx * dx + dy * dy;
                if (d2 > r * r) continue;
                if (d2 > r * r * 0.6f && dx + dy > r * 0.4f) cv.Blend(x, row, new Color32(pal.jellyDark.r, pal.jellyDark.g, pal.jellyDark.b, 110));
            }
        if (r < 6f)
        {
            cv.Blend(Mathf.RoundToInt(cx - r * 0.55f), Mathf.RoundToInt(cy - r * 0.6f), new Color32(255, 255, 255, 200));
            for (int s = -1; s <= 1; s += 2) { int ex = Mathf.RoundToInt(cx + s * r * 0.4f); cv.Set(ex, Mathf.RoundToInt(cy) - 1, white); cv.Set(ex, Mathf.RoundToInt(cy), dark); }
            return;
        }
        cv.Disc(cx - r * 0.5f, cy - r * 0.55f, r * 0.2f, new Color32(255, 255, 255, 255));
        cv.Blend(Mathf.RoundToInt(cx - r * 0.25f), Mathf.RoundToInt(cy - r * 0.75f), new Color32(255, 255, 255, 180));
        float ex0 = r * 0.36f, ey = cy - r * 0.05f, er = r * (amazed ? 0.25f : 0.2f);
        for (int s = -1; s <= 1; s += 2)
        {
            cv.Disc(cx + s * ex0, ey, er + 0.8f, pal.outline);
            cv.Disc(cx + s * ex0, ey, er, white);
            cv.Disc(cx + s * ex0 + er * 0.12f, ey + er * 0.18f, er * 0.55f, dark);
            cv.Set(Mathf.RoundToInt(cx + s * ex0 - er * 0.15f), Mathf.RoundToInt(ey - er * 0.2f), white);
        }
        float my = cy + r * 0.45f;
        if (amazed)
        {
            cv.Disc(cx, my, r * 0.15f + 0.4f, dark);
            cv.Disc(cx, my + r * 0.07f, r * 0.08f + 0.2f, new Color32(255, 120, 140, 255));
        }
        else for (int x = Mathf.RoundToInt(cx - r * 0.18f); x <= Mathf.RoundToInt(cx + r * 0.18f); x++) cv.Set(x, Mathf.RoundToInt(my + (Mathf.Abs(x - cx) < r * 0.1f ? 1 : 0)), dark);
    }

    // a twinkle: a dot with little arms
    static void Twinkle(Canvas cv, int x, int y, int arm, Color32 c)
    {
        cv.Blend(x, y, c);
        for (int k = 1; k <= arm; k++)
        {
            var q = c; q.a = (byte)(c.a * (1f - 0.35f * k));
            cv.Blend(x + k, y, q); cv.Blend(x - k, y, q); cv.Blend(x, y + k, q); cv.Blend(x, y - k, q);
        }
    }

    // soft polka dots (2 x 2), every 'step' pixels, offset every other row
    static void Dots(Canvas cv, RectInt r, Color32 c, int step)
    {
        int k = 0;
        for (int row = r.y + step / 2; row < r.yMax - 1; row += step, k++)
            for (int x = r.x + (k % 2 == 0 ? step / 2 : step); x < r.xMax - 1; x += step)
            { cv.Set(x, row, c); cv.Set(x + 1, row, c); cv.Set(x, row + 1, c); cv.Set(x + 1, row + 1, c); }
    }

    // sprinkles: little 2-pixel dashes in candy colours, every which way
    static void Sprinkles(Canvas cv, RectInt r, float density, int s, byte alpha)
    {
        var rng = new System.Random(s);
        Color32[] cols = { new Color32(255, 90, 150, 255), new Color32(255, 205, 60, 255), new Color32(80, 200, 150, 255), new Color32(90, 160, 255, 255), new Color32(175, 115, 255, 255), new Color32(255, 140, 80, 255) };
        int n = Mathf.RoundToInt(r.width * r.height * density);
        for (int k = 0; k < n; k++)
        {
            int x = r.x + rng.Next(Mathf.Max(1, r.width - 1)), y = r.y + 1 + rng.Next(Mathf.Max(1, r.height - 1));
            var c = cols[rng.Next(cols.Length)];
            c.a = alpha;
            int dir = rng.Next(4);
            cv.Blend(x, y, c);
            if (dir == 0) cv.Blend(x + 1, y, c);
            else if (dir == 1) cv.Blend(x, y - 1, c);
            else if (dir == 2) cv.Blend(x + 1, y - 1, c);
            else cv.Blend(x + 1, y + 1, c);
        }
    }

    // the open tab's buttons (glossy candy pills), and the pages if they don't all fit
    void Buttons(Canvas cv, Pal pal)
    {
        var acts = BuddyActions.InTab(Tab);
        Color32[] cols = { new Color32(255, 150, 190, 255), new Color32(130, 222, 185, 255), new Color32(255, 222, 110, 255), new Color32(135, 195, 255, 255), new Color32(195, 160, 255, 255), new Color32(255, 180, 130, 255) };
        for (int k = 0; k < btnR.Count; k++)
        {
            int i = firstBtn + k;
            if (i >= acts.Count) break;
            string label = i == confirmIdx ? "SURE? TAP AGAIN" : acts[i].label();
            Color32 c = i == confirmIdx ? new Color32(255, 110, 120, 255) : cols[(i + Tab * 2) % cols.Length];
            Pill(cv, btnR[k], c, label, hover == i, pressed == i);
        }
        if (pages > 1)
        {
            Pill(cv, prevR, pal.jellyLight, "<", hover == HitPrev, pressed == HitPrev);
            Pill(cv, nextR, pal.jellyLight, ">", hover == HitNext, pressed == HitNext);
            string pg = (page + 1) + "/" + pages;
            cv.Text(pg, contentR.x + contentR.width / 2 - PixelFont.Measure(pg) / 2, prevR.y + 2, pal.ink);
        }
    }

    // a glossy candy pill with its label: lifted when the cursor's on it, pushed in when pressed
    static void Pill(Canvas cv, RectInt r, Color32 c, string label, bool hover, bool down, bool whiteLabel = false)
    {
        int lift = down ? 0 : 1;
        Color32 top = Lighten(c, hover ? 0.5f : 0.3f), mid = hover ? Lighten(c, 0.15f) : c, edge = Darken(c, 0.55f), basec = Darken(c, 0.28f);
        int x0 = r.x, x1 = r.xMax, y0 = r.y + (down ? 1 : 0), y1 = r.yMax;
        int rad = Mathf.Min(4, (y1 - y0) / 2);
        cv.RoundRect(x0, y0, x1, y1, rad, edge);
        if (!down) cv.RoundRect(x0 + 1, y0 + 1, x1 - 1, y1 - 1, Mathf.Max(0, rad - 1), basec);
        for (int row = y0 + 1; row < y1 - 1 - lift; row++)
        {
            float v = (row - y0) / (float)Mathf.Max(1, y1 - y0);
            cv.RoundRectRow(x0 + 1, y0 + 1, x1 - 1, y1 - 1 - lift, Mathf.Max(0, rad - 1), row, Lerp32(top, mid, Mathf.Clamp01(v * 1.8f)));
        }
        for (int x = x0 + 3; x < x1 - 3; x++) cv.Blend(x, y0 + 2, new Color32(255, 255, 255, 160));
        string s = label;
        int max = r.width - 6;
        while (s.Length > 1 && PixelFont.Measure(s) > max) s = s.Substring(0, s.Length - 1);
        int tx = x0 + (r.width - PixelFont.Measure(s)) / 2, ty = y0 + (y1 - y0 - lift - PixelFont.GlyphH) / 2;
        if (whiteLabel) cv.Label(s, tx, ty, new Color32(255, 255, 255, 255), Darken(c, 0.5f));
        else cv.Text(s, tx, ty, Darken(c, 0.72f));
    }

    // ME: its name (big), a level badge, its XP, medals, best friend, windows it's been in, a portrait (if there's room)
    void Card(Canvas cv, RectInt r, Pal pal)
    {
        var white = new Color32(255, 255, 255, 255);
        bool side = r.width >= 230 && r.height >= 90; // (room for its portrait beside the numbers; else it goes under them)
        int P = side ? Mathf.Clamp(Mathf.Min(r.width / 4, r.height / 3), 36, 64) : 0;
        int x = r.x + 7, row = r.y + 7, right = r.xMax - 6 - (side ? P + 8 : 0), floor = r.yMax - 3;
        if (row + 10 > floor) return;
        string name = speech ? speech.Name : "BUDDY";
        cv.Big(name, x + 1, row + 1, Lerp32(pal.jellyLight, white, 0.3f));
        cv.Big(name, x, row, pal.jellyDark);
        row += 15;
        if (row + 7 > floor) return;

        // LV 12 (a candy badge) and what it is now
        string lv = progress ? "LV " + progress.Level : "LV ?";
        int lw = PixelFont.Measure(lv) + 8;
        cv.RoundRect(x, row - 2, x + lw, row + 7, 3, pal.jellyDark);
        cv.RoundRect(x + 1, row - 1, x + lw - 1, row + 6, 2, pal.jelly);
        for (int xx = x + 2; xx < x + lw - 2; xx++) cv.Blend(xx, row - 1, new Color32(255, 255, 255, 150));
        if (Light(pal.jelly)) cv.Label(lv, x + 4, row, pal.ink, Lerp32(pal.jelly, white, 0.5f));
        else cv.Label(lv, x + 4, row, white, pal.jellyDark);
        if (progress)
        {
            string st = progress.StageName.ToUpperInvariant();
            while (st.Length > 3 && x + lw + 5 + PixelFont.Measure(st) > right) st = st.Substring(0, st.Length - 1);
            cv.Text(st, x + lw + 5, row, pal.ink);
        }
        row += 12;
        if (row + 8 > floor) return;

        // the XP bar: a candy stick filling up (the numbers beside it, or under it if there's no room)
        string xpT = progress ? progress.Xp + "/" + progress.XpForNext + " XP" : "";
        int bw = Mathf.Min(right - x, 130);
        bool beside = x + bw + 5 + PixelFont.Measure(xpT) <= right;
        if (!beside && right - x - 5 - PixelFont.Measure(xpT) >= 60) { bw = right - x - 5 - PixelFont.Measure(xpT); beside = true; }
        bw = Mathf.Max(20, bw);
        float f = progress ? progress.Progress01 : 0f;
        cv.RoundRect(x, row, x + bw, row + 8, 3, pal.jellyDark);
        cv.RoundRect(x + 1, row + 1, x + bw - 1, row + 7, 2, Lerp32(pal.panel, pal.jellyDark, 0.18f));
        for (int xx = x + 1; xx < x + bw - 1; xx++) cv.Blend(xx, row + 1, new Color32(0, 0, 0, 30));
        int fw = Mathf.RoundToInt((bw - 2) * f);
        for (int yy = row + 1; yy < row + 7; yy++)
            for (int xx = x + 1; xx < x + 1 + fw; xx++)
                cv.Set(xx, yy, ((xx + yy) / 3) % 2 == 0 ? pal.jelly : Lerp32(pal.jellyLight, white, 0.3f));
        for (int xx = x + 2; xx < x + fw; xx++) cv.Blend(xx, row + 2, new Color32(255, 255, 255, 150));
        if (fw > 2) for (int yy = row + 2; yy < row + 6; yy++) cv.Set(x + fw, yy, pal.jellyDark);
        if (beside) cv.Text(xpT, x + bw + 5, row + 2, pal.ink);
        else if (row + 16 <= floor) { row += 11; cv.Text(xpT, x, row, pal.ink); }
        row += 14;
        if (row + 7 > floor) return;

        // medals: one per game, bronze / silver / gold (an empty ring with the game's letter: not yet)
        cv.Text("MEDALS", x, row, pal.jellyDark);
        int mx = x + PixelFont.Measure("MEDALS") + 7;
        string[] games = { "L", "D", "B", "H", "T", "J" };
        for (int k = 0; k < Trophies.FeatCount && k < games.Length; k++)
        {
            int tier = Trophies.Current && progress ? Trophies.Current.Tier(progress.saveName, (Trophies.Feat)k) : 0;
            Color32 mc = tier == 3 ? new Color32(255, 205, 60, 255) : tier == 2 ? new Color32(205, 214, 230, 255) : tier == 1 ? new Color32(214, 136, 74, 255) : new Color32(0, 0, 0, 0);
            float cx = mx + k * 12 + 4, cy = row + 2;
            if (cx + 5 > right) break;
            cv.Disc(cx, cy, 5f, pal.jellyDark);
            if (tier > 0)
            {
                cv.Disc(cx, cy, 4f, Darken(mc, 0.2f));
                cv.Disc(cx, cy, 3f, mc);
                cv.Blend(Mathf.RoundToInt(cx) - 1, Mathf.RoundToInt(cy) - 2, new Color32(255, 255, 255, 220));
                cv.Blend(Mathf.RoundToInt(cx), Mathf.RoundToInt(cy) - 2, new Color32(255, 255, 255, 120));
            }
            else
            {
                cv.Disc(cx, cy, 4f, Lerp32(pal.panel, pal.jellyLight, 0.25f));
                cv.Text(games[k], Mathf.RoundToInt(cx) - 1, Mathf.RoundToInt(cy) - 2, Lerp32(pal.ink, pal.panel, 0.45f));
            }
        }
        row += 13;

        // best friend, windows, slides
        string bf = home ? home.BestFriendOf(Intruder) : null;
        var sl = Intruder ? Intruder.GetComponent<BuddySlide>() : null;
        if (row + 5 <= floor) { Pair(cv, x, row, right, "BEST FRIEND", string.IsNullOrEmpty(bf) ? "NOT YET" : bf, pal); row += 9; }
        if (row + 5 <= floor) { Pair(cv, x, row, right, "WINDOWS VISITED", (home ? home.VisitsOf(Intruder) : 0).ToString(), pal); row += 9; }
        if (sl && row + 5 <= floor) { Pair(cv, x, row, right, "SLIDE RIDES", sl.RidesDone.ToString(), pal); row += 9; }

        // its portrait, amazed, in a frosted cookie frame: beside the numbers, or under them if there's room
        bool quote = false;
        float pcx = 0f, pcy = 0f;
        if (side) { pcx = r.xMax - 7 - P * 0.5f; pcy = r.y + 8 + P * 0.5f; }
        else
        {
            int qRow = r.yMax - 10, avail = qRow - 3 - (row + 2);
            P = Mathf.Min(avail, Mathf.Min(r.width * 45 / 100, 72));
            if (P >= 30) { pcx = r.x + r.width * 0.5f; pcy = row + 2 + avail * 0.5f; quote = true; }
            else P = 0;
        }
        if (P > 0)
        {
            float R = P * 0.5f, cx = pcx, cy = pcy;
            for (int yy = Mathf.FloorToInt(cy - R - 3); yy <= Mathf.CeilToInt(cy + R + 3); yy++)
                for (int xx = Mathf.FloorToInt(cx - R - 3); xx <= Mathf.CeilToInt(cx + R + 3); xx++)
                {
                    float dx = xx - cx, dy = yy - cy, d = Mathf.Sqrt(dx * dx + dy * dy), a = Mathf.Atan2(dy, dx);
                    float sc = R + 1.3f * Mathf.Cos(a * 12f);
                    if (d > sc + 1f) continue;
                    if (d > sc) { cv.Set(xx, yy, pal.jellyDark); continue; }
                    if (d > R - 3f) { cv.Set(xx, yy, dy < -R * 0.3f ? white : Lerp32(pal.cream, pal.jellyLight, 0.25f)); continue; }
                    if (d > R - 4f) { cv.Set(xx, yy, pal.jellyDark); continue; }
                    float v = (dy + R) / (2f * R);
                    cv.Set(xx, yy, Lerp32(Lerp32(new Color32(196, 172, 255, 255), pal.body, 0.15f), new Color32(255, 214, 230, 255), v));
                }
            float fr = R * 0.5f;
            Face(cv, cx, cy + R * 0.12f, fr, pal, true);
            Twinkle(cv, Mathf.RoundToInt(cx - R * 0.55f), Mathf.RoundToInt(cy - R * 0.45f), 2, new Color32(255, 255, 255, 240));
            Twinkle(cv, Mathf.RoundToInt(cx + R * 0.58f), Mathf.RoundToInt(cy - R * 0.2f), 1, new Color32(255, 255, 255, 240));
            Twinkle(cv, Mathf.RoundToInt(cx + R * 0.35f), Mathf.RoundToInt(cy - R * 0.62f), 1, new Color32(255, 240, 150, 255));
        }

        // what it keeps saying
        if (quote || (side && row + 12 < r.yMax - 10))
        {
            string q = speech && speech.voice == BuddySpeech.Voice.Duck ? "♥ QUACK! IT'S ALL CANDY! ♥" : "♥ IT'S ALL CANDY IN HERE! ♥";
            if (PixelFont.Measure(q) > r.width - 10) q = "♥ ALL CANDY! ♥";
            cv.Text(q, r.x + (r.width - PixelFont.Measure(q)) / 2, r.yMax - 10, Lerp32(pal.jelly, pal.ink, 0.25f));
        }
    }

    // "LABEL: value" (the label in its colour, the value darker), cut to fit
    static void Pair(Canvas cv, int x, int row, int right, string label, string value, Pal pal)
    {
        string l = label + ": ";
        cv.Text(l, x, row, pal.jellyDark);
        int vx = x + PixelFont.Measure(l) + 1;
        string v = value;
        while (v.Length > 1 && vx + PixelFont.Measure(v) > right) v = v.Substring(0, v.Length - 1);
        cv.Text(v, vx, row, pal.ink);
    }

    // a little candy ribbon with a message
    static Color32[] Ribbon(string text, Color tint, out int w, out int h)
    {
        var pal = new Pal(tint);
        w = PixelFont.Measure(text) + 12;
        h = PixelFont.GlyphH + 6;
        var cv = new Canvas(w, h);
        cv.RoundRect(0, 0, w, h, 3, pal.outline);
        cv.RoundRect(1, 1, w - 1, h - 1, 2, pal.jelly);
        for (int x = 3; x < w - 3; x++) cv.Blend(x, 1, new Color32(255, 255, 255, 150));
        cv.Label(text, 6, 3, new Color32(255, 255, 255, 255), pal.outline);
        return cv.px;
    }

    // ------------------------------------------------------------------ colours and a little pixel canvas

    struct Pal
    {
        public Color32 body, jelly, jellyLight, jellyDark, jellyDeep, outline, bg, panel, cream, ink;
        public Pal(Color c)
        {
            body = c;
            jelly = Lerp32(c, Color.white, 0.12f);
            jellyLight = Lerp32(c, Color.white, 0.5f);
            jellyDark = Lerp32(c, Color.black, 0.35f);
            jellyDeep = Lerp32(c, Color.black, 0.15f);
            outline = Lerp32(c, Color.black, 0.7f);
            bg = Lerp32(c, Color.white, 0.8f);
            panel = Lerp32(c, Color.white, 0.88f);
            cream = Lerp32(new Color(1f, 0.97f, 0.92f), c, 0.12f);
            ink = Lerp32(c, Color.black, 0.78f);
        }
    }

    static Color32 Lerp32(Color a, Color b, float t) => (Color32)Color.Lerp(a, b, t);
    static bool Light(Color32 c) => (0.299f * c.r + 0.587f * c.g + 0.114f * c.b) / 255f > 0.68f; // (white text wouldn't show on it)
    static Color32 Lighten(Color c, float t) => Lerp32(c, Color.white, t);
    static Color32 Darken(Color c, float t) => Lerp32(c, Color.black, t);

    /// <summary>A w x h pixel buffer (stored y up like the textures), drawn on in rows from the top.</summary>
    public class Canvas
    {
        public readonly int w, h;
        public readonly Color32[] px;
        public Canvas(int w, int h) { this.w = w; this.h = h; px = new Color32[w * h]; }

        public void Set(int x, int row, Color32 c) { if (x >= 0 && x < w && row >= 0 && row < h) px[(h - 1 - row) * w + x] = c; }
        public void Blend(int x, int row, Color32 c)
        {
            if (x < 0 || x >= w || row < 0 || row >= h) return;
            int i = (h - 1 - row) * w + x;
            Color32 d = px[i];
            float a = c.a / 255f;
            px[i] = new Color32((byte)(d.r + (c.r - d.r) * a), (byte)(d.g + (c.g - d.g) * a), (byte)(d.b + (c.b - d.b) * a), (byte)Mathf.Max(d.a, c.a));
        }

        static bool InRound(int x, int row, int x0, int y0, int x1, int y1, int r)
        {
            if (x < x0 || x >= x1 || row < y0 || row >= y1) return false;
            int cx = x < x0 + r ? x0 + r : x >= x1 - r ? x1 - r - 1 : x, cy = row < y0 + r ? y0 + r : row >= y1 - r ? y1 - r - 1 : row;
            int dx = x - cx, dy = row - cy;
            return dx * dx + dy * dy <= r * r + r;
        }

        public void RoundRect(int x0, int y0, int x1, int y1, int r, Color32 c)
        {
            for (int row = Mathf.Max(0, y0); row < Mathf.Min(h, y1); row++) RoundRectRow(x0, y0, x1, y1, r, row, c);
        }

        public void RoundRectRow(int x0, int y0, int x1, int y1, int r, int row, Color32 c)
        {
            for (int x = Mathf.Max(0, x0); x < Mathf.Min(w, x1); x++) if (InRound(x, row, x0, y0, x1, y1, r)) Set(x, row, c);
        }

        // rounded at the top only (tabs)
        public void RoundRectTop(int x0, int y0, int x1, int y1, int r, Color32 c)
        {
            for (int row = Mathf.Max(0, y0); row < Mathf.Min(h, y1); row++)
                for (int x = Mathf.Max(0, x0); x < Mathf.Min(w, x1); x++)
                    if (row >= y0 + r || InRound(x, row, x0, y0, x1, y1 + r, r)) Set(x, row, c);
        }

        public void Disc(float cx, float cy, float r, Color32 c)
        {
            for (int row = Mathf.FloorToInt(cy - r); row <= Mathf.CeilToInt(cy + r); row++)
                for (int x = Mathf.FloorToInt(cx - r); x <= Mathf.CeilToInt(cx + r); x++)
                {
                    float dx = x - cx, dy = row - cy;
                    if (dx * dx + dy * dy <= r * r) Set(x, row, c);
                }
        }

        public void Text(string s, int x, int rowTop, Color32 c) => PixelFont.Draw(px, w, h, s, x, h - 1 - rowTop, c);

        public void Label(string s, int x, int rowTop, Color32 c, Color32 shadow)
        {
            Text(s, x + 1, rowTop + 1, shadow);
            Text(s, x, rowTop, c);
        }

        // twice as big (headings)
        public void Big(string s, int x, int rowTop, Color32 c)
        {
            int tw = PixelFont.Measure(s) + 1, th = PixelFont.GlyphH;
            var tmp = new Color32[tw * th];
            PixelFont.Draw(tmp, tw, th, s, 0, th - 1, new Color32(255, 255, 255, 255));
            for (int yy = 0; yy < th; yy++)
                for (int xx = 0; xx < tw; xx++)
                {
                    if (tmp[yy * tw + xx].a == 0) continue;
                    int r0 = rowTop + (th - 1 - yy) * 2;
                    Set(x + xx * 2, r0, c); Set(x + xx * 2 + 1, r0, c); Set(x + xx * 2, r0 + 1, c); Set(x + xx * 2 + 1, r0 + 1, c);
                }
        }
    }
}
