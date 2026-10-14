using System.Collections.Generic;
using System.IO;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Desktop pinball (chat "pinball"): the main screen turns into a pinball table. Two candy flippers pop up on the
/// taskbar, a drawn Recycle Bin sits between them (the drain), a plunger lane runs up the right edge, candy rails lead
/// down to the flippers and a dot-matrix scoreboard hangs at the top. A buddy is the ball. Your windows are bumpers
/// (BOING, +250, the window wobbles and springs back; maximized ones and huge ones are left out), your desktop icons are
/// targets that light up (+500; all of them: JACKPOT), and three jelly pop bumpers fill the empty space (+100).
/// Keys (only while a game is on, and only these): Left Shift = left flipper, Right Shift = right flipper, hold Space to
/// pull the plunger back and let go to launch (longer = harder), Esc = stop. The game takes the keyboard like the chat
/// does, so the keys don't reach your other apps; click into another app and it pauses (click the scoreboard to go on).
/// Three balls, a different buddy each; the others watch from the stands on top and cheer, the ones on your other
/// screens cheer from there. Grab the ball and it's TILT. The best score is kept in pinball.txt.
/// The ball is the buddy's own gummy body in a room (GummyBody.Room) with this table as its arena (IGummyArena):
/// every physics step it asks the table to push it out of the flippers, rails and bumpers.
/// Lives on the Interaction object (WhipIt > Setup).
/// </summary>
[DefaultExecutionOrder(-9)] // after the brains decide, before the bodies simulate: the flippers move with this frame's keys
[DisallowMultipleComponent]
public partial class PinballTable : MonoBehaviour, IGummyArena, IGrabTarget
{
    [Tooltip("Sprite material (WhipIt > Setup assigns it).")]
    public Material material;
    [Header("Sounds (WhipIt > Setup assigns them)")]
    public AudioClip flipClip;
    public AudioClip popClip, dingClip, launchClip, gulpClip, startClip, overClip, jackpotClip;
    [Range(0f, 1f)] public float volume = 0.6f;
    [Header("Game")]
    public int balls = 3;
    [Tooltip("Gravity on the ball (the table tilts: less than the buddies' own 30).")]
    public float ballGravity = 14f;
    [Tooltip("The ball goes no faster than this (world units / s).")]
    public float maxSpeed = 22f;
    [Tooltip("Seconds after a launch when a drained ball comes back for free.")]
    public float ballSave = 5f;

    public enum Phase { Off, Setup, Ready, Live, Drain, Over, Closing }

    /// <summary>The one in the scene (on the Interaction object).</summary>
    public static PinballTable Current { get; private set; }
    /// <summary>A game is on (the table is out).</summary>
    public static bool Playing => Current && Current.phase != Phase.Off;
    public Phase State => phase;
    public int Score { get; private set; }
    public int High { get; private set; }
    public string HighBy { get; private set; } = "";
    /// <summary>Which ball this is (1..balls).</summary>
    public int BallNumber { get; private set; }
    public GummyBody Ball => ball;
    public bool Paused => paused;
    /// <summary>For tests: what happened (bumpers hit, targets lit, flips...).</summary>
    public int Bumps { get; private set; }
    public int Pops { get; private set; }
    public int Lit { get; private set; }
    public int Jackpots { get; private set; }
    public int Flips { get; private set; }
    public int Strikes { get; private set; }
    public int Drains { get; private set; }
    public int Saves { get; private set; }
    public int Tilts { get; private set; }
    public int Launches { get; private set; }
    public int WindowBumpers => bumpers.Count;
    public int PopBumpers => pops.Count;
    public int Targets => targets.Count;
    public int Fans => fans.Count;
    /// <summary>What's left out and why (for the log).</summary>
    public string Report { get; private set; } = "";
    /// <summary>Tests: keys from here instead of the keyboard (bits: 1 left flipper, 2 right flipper, 4 launch, 8 quit).</summary>
    public static System.Func<int> KeysProbe;

    public static string FilePath => Path.Combine(Application.persistentDataPath, "pinball.txt");

    Phase phase = Phase.Off;
    float phaseT;
    AudioSource sfx, flipSfx;

    void Awake()
    {
        sfx = gameObject.AddComponent<AudioSource>();
        sfx.playOnAwake = false;
        sfx.spatialBlend = 0f;
        flipSfx = gameObject.AddComponent<AudioSource>();
        flipSfx.playOnAwake = false;
        flipSfx.spatialBlend = 0f;
        Load();
    }

    void OnEnable() { Current = this; }

    void OnDisable()
    {
        if (phase != Phase.Off) Close(true);
        if (Current == this) Current = null;
    }

    void OnDestroy() => DropArt();

    // ------------------------------------------------------------------ starting and stopping

    /// <summary>Out comes the table and the first ball. False if it can't (no buddies to play with, no screen yet).</summary>
    public bool Begin()
    {
        if (phase == Phase.Over) { Restart(); return true; }
        if (phase == Phase.Closing) Teardown();
        if (phase != Phase.Off) return true;
        if (!Camera.main || !Build()) return false;
        MakeArt();
        // who plays: the buddies on the main screen go up into the stands (the balls take turns from there)
        order.Clear();
        var cands = new List<GummyBody>();
        foreach (var b in GummyBody.All) if (CanPlay(b)) cands.Add(b);
        if (cands.Count == 0) { Teardown(); return false; }
        Rect main = W;
        main.yMax += 0.5f;
        foreach (var b in cands) if (main.Contains(b.Center)) Seat(b, true);
        foreach (var f in fans) order.Add(f.b);
        if (order.Count == 0)
        {
            // nobody on the main screen: one comes over from another screen
            var b = cands[Random.Range(0, cands.Count)];
            Seat(b, true);
            if (FanOf(b) != null) order.Add(b);
        }
        if (order.Count == 0) { Teardown(); return false; }
        for (int i = order.Count - 1; i > 0; i--) { int j = Random.Range(0, i + 1); var t = order[i]; order[i] = order[j]; order[j] = t; }
        Score = 0;
        BallNumber = 0;
        nextBall = 0;
        Bumps = Pops = Lit = Jackpots = Flips = Strikes = Drains = Saves = Tilts = Launches = 0;
        foreach (var t in targets) t.lit = false;
        newHigh = false;
        paused = false;
        Enter(Phase.Setup);
        GrabTargets.Add(this);
        TransparentWindow.BeginTyping();
        focusAt = Time.unscaledTime;
        Play(startClip, 1f, 0.9f);
        Say(Random.value < 0.5f ? "PINBALL!" : "WHO'S FIRST?", 2f);
        return true;
    }

    /// <summary>Stop: the table packs up, everybody's let go (now = no packing-up animation).</summary>
    public void Close(bool now = false)
    {
        if (phase == Phase.Off) return;
        SaveHigh();
        if (ball) ReleaseBall(false);
        foreach (var f in fans.ToArray()) Unseat(f, false, true);
        fans.Clear();
        loose.Clear();
        GrabTargets.Remove(this);
        TransparentWindow.EndTyping();
        if (now || !root) { Teardown(); return; }
        Enter(Phase.Closing);
    }

    void Teardown()
    {
        phase = Phase.Off;
        DropArt();
        GrabTargets.Remove(this);
    }

    void Enter(Phase p) { phase = p; phaseT = 0f; }

    static bool CanPlay(GummyBody b)
    {
        if (!b || !b.isActiveAndEnabled || b.InRoom || b.IsMonster || b.Held || b.Hiding || GiantPotion.IsGiant(b)) return false;
        if (!b.GetComponent<BuddySpeech>()) return false;
        var br = b.GetComponent<GummyBrain>();
        return br && !br.Towering && !b.OnRope;
    }

    // ------------------------------------------------------------------ the table (world units)

    Rect W;                                  // the main screen without its taskbar
    float tp, screenPx, s;                   // world units per art pixel, per screen pixel; the size (1 on a 16:9 screen)
    float ledgeY, shelfT, standsH;           // the stands on top (the ball's ceiling is the shelf)
    float laneX, laneTop, laneW;             // the plunger lane (right edge)
    float fcx, pivY, flipLen, r0, r1, tipY, gapHalf; // the flippers
    float railY;                             // where the rails meet the walls
    float plungerRest, plungerTravel;
    float arcR, arcL;                        // the top corners
    float popR;
    Rect binR, zone, sbRect, xRect;          // the drawn bin, where windows count, the scoreboard, its X
    const float RestAng = -30f * Mathf.Deg2Rad, UpAng = 30f * Mathf.Deg2Rad;
    const float FlipUp = 15f, FlipDown = 9f;  // radians / s
    const float RailR = 0.07f;
    int screensVersion;

    struct Seg { public Vector2 a, b; public float r; public bool oneWay; public int kind, style; }
    const int KWall = 0, KGate = 1, KPlunger = 2;
    const int IdFlip = 1000, IdPop = 2000, IdWin = 3000;
    readonly List<Seg> segs = new List<Seg>();

    struct Flipper { public Vector2 pivot; public float side, ang, prevAng, omega; public bool up; }
    readonly Flipper[] flip = new Flipper[2];

    class Pop { public Vector2 c; public Color32 col; public float lastHit = -9f, flash; }
    readonly List<Pop> pops = new List<Pop>();

    struct Bumper { public long id; public Rect r; public float lastHit; }
    readonly List<Bumper> bumpers = new List<Bumper>();
    readonly Dictionary<long, float> bumperHit = new Dictionary<long, float>();
    int platVersion = -1;

    class Target { public long id; public Vector2 c; public float r; public bool lit; public float flash; }
    readonly List<Target> targets = new List<Target>();

    bool Build()
    {
        W = ScreenBounds.MainRect;
        var cam = Camera.main;
        if (W.width < 4f || W.height < 4f || Screen.height <= 0) return false;
        screensVersion = ScreenBounds.Version;
        screenPx = 2f * cam.orthographicSize / Screen.height;
        int mainPx = ScreenBounds.MainScreenPixels;
        tp = screenPx * Mathf.Max(1, Mathf.RoundToInt(mainPx / 720f));
        s = Mathf.Clamp((W.width - 1f) / 16.8f, 0.6f, 1f);

        // the scoreboard: a dot matrix, its dots about 1/270 of the screen tall
        dmdPitch = Mathf.Max(3, Mathf.RoundToInt(mainPx / 270f));
        int dw = DmdW * dmdPitch + 4 * dmdPitch, dh = DmdH * dmdPitch + 4 * dmdPitch;
        float sbW = dw * screenPx, sbH = dh * screenPx;
        shelfT = 0.12f;
        standsH = Mathf.Max(1.2f, sbH + shelfT + 0.06f);
        ledgeY = W.yMax - standsH;

        laneW = 0.95f;
        laneX = W.xMax - laneW;
        fcx = 0.5f * (W.xMin + laneX);
        flipLen = 2.3f * s; r0 = 0.2f * s; r1 = 0.11f * s;
        gapHalf = 0.6f;
        float d = flipLen * Mathf.Cos(-RestAng) + gapHalf;
        pivY = W.yMin + 1f + flipLen * Mathf.Sin(-RestAng);
        tipY = pivY + flipLen * Mathf.Sin(RestAng);
        flip[0] = new Flipper { pivot = new Vector2(fcx - d, pivY), side = 1f, ang = RestAng, prevAng = RestAng };
        flip[1] = new Flipper { pivot = new Vector2(fcx + d, pivY), side = -1f, ang = RestAng, prevAng = RestAng };
        float rise = Mathf.Clamp((fcx - d - W.xMin) * Mathf.Tan(16f * Mathf.Deg2Rad), 0.9f, 2.4f);
        railY = pivY + rise;
        binR = new Rect(fcx - gapHalf - 0.15f, W.yMin, 2f * gapHalf + 0.3f, Mathf.Min(0.78f, tipY - W.yMin - 0.18f));
        plungerRest = W.yMin + 0.75f;
        plungerTravel = 0.5f;
        arcR = Mathf.Min(1.7f, (ledgeY - railY) * 0.4f);
        arcL = Mathf.Min(1.2f, (ledgeY - railY) * 0.3f);
        laneTop = ledgeY - arcR - 0.35f;
        popR = 0.42f * s;

        segs.Clear();
        // the rails down to the flippers (their tops meet the flippers' tops)
        Vector2 pl = flip[0].pivot, pr = flip[1].pivot;
        Wall(new Vector2(W.xMin, railY), new Vector2(pl.x - 0.02f, pl.y + r0 - RailR), RailR, 0);
        Wall(new Vector2(laneX, railY), new Vector2(pr.x + 0.02f, pr.y + r0 - RailR), RailR, 0);
        // the lane's wall, the one-way gate at its top (falls back onto it: rolls off into the table), the plunger
        Wall(new Vector2(laneX, W.yMin - 0.2f), new Vector2(laneX, laneTop), RailR, 1);
        segs.Add(new Seg { a = new Vector2(laneX, laneTop), b = new Vector2(W.xMax, laneTop + 0.3f), r = RailR, oneWay = true, kind = KGate, style = 3 });
        segs.Add(new Seg { a = new Vector2(laneX + 0.04f, plungerRest), b = new Vector2(W.xMax - 0.04f, plungerRest), r = 0.08f, kind = KPlunger });
        plungerSeg = segs.Count - 1;
        plungerY = plungerRest;
        // the round top corners
        Arc(new Vector2(W.xMax - arcR, ledgeY - arcR), arcR, 0f, 90f, 7);
        Arc(new Vector2(W.xMin + arcL, ledgeY - arcL), arcL, 90f, 180f, 5);

        zone = Rect.MinMaxRect(W.xMin, railY + 0.8f, W.xMax - arcR - 0.3f, ledgeY);
        float sbX = Mathf.Clamp(fcx - sbW * 0.5f, W.xMin + 0.1f, W.xMax - sbW - 0.1f);
        sbRect = new Rect(sbX, ledgeY + shelfT, sbW, sbH);
        float xr = Mathf.Max(0.16f, 5f * dmdPitch * screenPx);
        xRect = new Rect(sbRect.xMax - xr * 0.6f, sbRect.yMax - xr * 0.6f, xr, xr);
        platVersion = -1;
        Desk(true);
        return true;
    }

    void Wall(Vector2 a, Vector2 b, float r, int style) => segs.Add(new Seg { a = a, b = b, r = r, kind = KWall, style = style });

    void Arc(Vector2 c, float R, float from, float to, int n)
    {
        for (int i = 0; i < n; i++)
        {
            float a0 = Mathf.Lerp(from, to, i / (float)n) * Mathf.Deg2Rad, a1 = Mathf.Lerp(from, to, (i + 1) / (float)n) * Mathf.Deg2Rad;
            Wall(c + R * new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)), c + R * new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)), RailR, 2);
        }
    }

    // your windows (bumpers) and icons (targets), again whenever they change; first = also the pop bumpers
    void Desk(bool first)
    {
        if (!first && DesktopPlatforms.Version == platVersion) return; // (polled 30 times a second: cheap, no garbage)
        platVersion = DesktopPlatforms.Version;
        bumpers.Clear();
        float zoneArea = zone.width * zone.height;
        int skipped = 0;
        var wins = DesktopPlatforms.Windows;
        for (int i = 0; i < wins.Count && bumpers.Count < 24; i++)
        {
            WindowBox w = wins[i];
            if (w.screen) continue;
            if (!w.movable) { skipped++; continue; } // (maximized: never touched)
            Rect r = Rect.MinMaxRect(Mathf.Max(w.xMin, zone.xMin), Mathf.Max(w.yMin, zone.yMin), Mathf.Min(w.xMax, zone.xMax), Mathf.Min(w.yMax, zone.yMax));
            if (r.width < 0.4f || r.height < 0.3f) continue;
            if (r.width * r.height > 0.45f * zoneArea) { skipped++; continue; } // (too big to play round)
            // no gaps the ball would get wedged in: right up to the stands / the wall
            if (zone.yMax - r.yMax < 1.5f) r.yMax = zone.yMax;
            if (r.xMin - W.xMin < 1.2f) r.xMin = W.xMin;
            float last = bumperHit.TryGetValue(w.id, out float lh) ? lh : -9f;
            bumpers.Add(new Bumper { id = w.id, r = r, lastHit = last });
        }

        // icons: targets (the ones the ball can get to); lit stays lit. Only remade when they're not the same any more.
        Rect reach = Rect.MinMaxRect(W.xMin, railY + 0.3f, laneX - 0.1f, ledgeY);
        int n = 0;
        bool same = true;
        foreach (var ic in DesktopPlatforms.Icons)
        {
            Vector2 c = ic.Center;
            if (!reach.Contains(c) || n >= 30) continue;
            bool inside = false;
            foreach (var b in bumpers) if (b.r.Contains(c)) inside = true;
            if (inside) continue;
            if (n >= targets.Count || targets[n].id != ic.id || (targets[n].c - c).sqrMagnitude > 1e-4f) same = false;
            n++;
        }
        if (n != targets.Count) same = false;
        if (!same || first)
        {
            var was = new Dictionary<long, bool>();
            foreach (var t in targets) was[t.id] = t.lit;
            targets.Clear();
            foreach (var ic in DesktopPlatforms.Icons)
            {
                Vector2 c = ic.Center;
                if (!reach.Contains(c) || targets.Count >= 30) continue;
                bool inside = false;
                foreach (var b in bumpers) if (b.r.Contains(c)) inside = true;
                if (inside) continue;
                targets.Add(new Target { id = ic.id, c = c, r = Mathf.Max(0.2f, 0.5f * Mathf.Max(ic.xMax - ic.xMin, ic.yMax - ic.yMin)), lit = was.TryGetValue(ic.id, out bool l) && l });
            }
            if (root) MakeRings();
        }
        if (first) PlacePops();
        if (first || bumpers.Count != reportBumpers || skipped != reportSkipped || targets.Count != reportTargets)
        {
            reportBumpers = bumpers.Count; reportSkipped = skipped; reportTargets = targets.Count;
            Report = bumpers.Count + " windows are bumpers" + (skipped > 0 ? ", " + skipped + " left out (maximized or too big)" : "") + ", " + targets.Count + " icons are targets, " + pops.Count + " pop bumpers";
        }
    }
    int reportBumpers = -1, reportSkipped = -1, reportTargets = -1;

    // three jelly pop bumpers in a triangle in the middle, moved out of the way of windows and icons
    void PlacePops()
    {
        pops.Clear();
        float my = 0.5f * (zone.yMin + zone.yMax);
        var want = new[] { new Vector2(fcx - 1.8f * s, my + 0.7f), new Vector2(fcx + 1.8f * s, my + 0.7f), new Vector2(fcx, my - 0.6f) };
        var cols = new[] { new Color32(255, 112, 168, 255), new Color32(96, 214, 178, 255), new Color32(255, 206, 84, 255) };
        for (int k = 0; k < want.Length; k++)
        {
            Vector2 best = Vector2.zero;
            float bd = float.MaxValue;
            for (float y = zone.yMin; y <= zone.yMax; y += 0.25f)
                for (float x = zone.xMin; x <= zone.xMax; x += 0.25f)
                {
                    var c = new Vector2(x, y);
                    float dd = (c - want[k]).sqrMagnitude;
                    if (dd < bd && PopFits(c)) { bd = dd; best = c; }
                }
            if (bd < float.MaxValue) pops.Add(new Pop { c = best, col = cols[k] });
        }
    }

    bool PopFits(Vector2 c)
    {
        float clear = popR + 0.8f; // (the ball gets past)
        if (c.x < W.xMin + clear || c.x > zone.xMax - popR || c.y < zone.yMin + popR || c.y > ledgeY - clear) return false;
        foreach (var b in bumpers)
        {
            float dx = Mathf.Max(Mathf.Max(b.r.xMin - c.x, 0f), c.x - b.r.xMax), dy = Mathf.Max(Mathf.Max(b.r.yMin - c.y, 0f), c.y - b.r.yMax);
            if (dx * dx + dy * dy < clear * clear) return false;
        }
        foreach (var t in targets) if ((t.c - c).sqrMagnitude < (t.r + popR + 0.2f) * (t.r + popR + 0.2f)) return false;
        foreach (var p in pops) if ((p.c - c).sqrMagnitude < (2f * popR + 0.9f) * (2f * popR + 0.9f)) return false;
        return true;
    }
}
