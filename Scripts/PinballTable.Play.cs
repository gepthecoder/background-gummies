using System.Collections.Generic;
using System.IO;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

// Desktop pinball: the game itself. Keys, the plunger, the ball's turn (from the stands into the lane, out through the
// bin and back up into the stands), scoring, targets, the ball save, tilt, pause, the crowd.
public partial class PinballTable
{
    GummyBody ball;
    float savedGravity, savedRestitution, savedDamping;
    bool savedSticky, tilted;
    readonly List<GummyBody> order = new List<GummyBody>(); // who's the ball, in turn
    int nextBall;
    float pull;                       // the plunger pulled back (0..1)
    int keysWas;
    bool paused, newHigh, gulped, saved, saveUsed;
    float pausedFor, focusAt = -9f, liveAt, trapFrom, jackpotAt = -1f, lidOpen, shake, cheerAt = -9f, overSpaceOk;
    int stuckKicks;
    Vector2 lastC, trapMin, trapMax;
    string msg = "", msg2 = "";
    float msgUntil;
    bool msgBlink;

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("PinballTable.Update", t0); }
    void UpdateTimed()
    {
        if (phase == Phase.Off) return;
        float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f), now = Time.unscaledTime;
        phaseT += dt;
        if (phase == Phase.Closing)
        {
            Animate(dt, now);
            if (phaseT > 0.5f) Teardown();
            return;
        }
        if (ScreenBounds.Version != screensVersion) { Close(true); return; } // (screens changed: no table any more)
        Desk(false);
        Keys(dt, now);
        Crowd(now);
        switch (phase)
        {
            case Phase.Setup: if (phaseT > 0.9f) NextBall(); break;
            case Phase.Ready: ReadyStep(); break;
            case Phase.Live: LiveStep(dt); break;
            case Phase.Drain: DrainStep(); break;
            case Phase.Over: if (phaseT > 10f) Close(); break;
        }
        if (jackpotAt >= 0f && now - jackpotAt > 2.2f) { jackpotAt = -1f; foreach (var t in targets) t.lit = false; }
        if (paused) { pausedFor += dt; if (pausedFor > 90f) { Close(); return; } }
        else pausedFor = 0f;
        Animate(dt, now);
    }

    void LateUpdate() { long t0 = WhipProf.T(); LateUpdateTimed(); WhipProf.Add("PinballTable.LateUpdate", t0); }
    void LateUpdateTimed()
    {
        if (phase == Phase.Off || advancedFrame == Time.frameCount) return;
        // no ball moved the flippers this frame (none on the table, or it's frozen): they move by themselves
        float dt = Mathf.Min(Time.deltaTime, 0.05f);
        for (int i = 0; i < 4; i++) { TurnFlippers(dt / 4f); MovePlunger(dt / 4f); }
    }

    // ------------------------------------------------------------------ keys (only while a game is on)

    void Keys(float dt, float now)
    {
        bool focused = true;
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        focused = Application.isFocused || now - focusAt < 0.8f; // (clicked into another app: pause)
#endif
        SetPaused(!focused || BuddyChat.Open);
        int k = paused ? 0 : KeysProbe != null ? KeysProbe() : ReadKeyboard();
        int down = k & ~keysWas, up = keysWas & ~k;
        keysWas = k;
        if ((down & 8) != 0) { Close(); return; }
        bool play = phase == Phase.Ready || phase == Phase.Live;
        for (int f = 0; f < 2; f++)
        {
            int bit = f == 0 ? 1 : 2;
            flip[f].up = play && (k & bit) != 0;
            if (play && (down & bit) != 0) { Flips++; PlayFlip(1f + 0.06f * f, 0.9f); }
            else if (play && (up & bit) != 0) PlayFlip(0.8f, 0.35f);
        }
        if (phase == Phase.Over)
        {
            if ((down & 4) != 0 && phaseT > 1.2f) Restart(); // (space: again)
            return;
        }
        if (phase != Phase.Ready || !ball) { pull = 0f; return; }
        if ((k & 4) != 0) pull = Mathf.Min(1f, pull + dt / 1.1f);
        else if (pull > 0f)
        {
            Launch(pull);
            pull = 0f;
        }
    }

    static int ReadKeyboard()
    {
#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb == null) return 0;
        return (kb.leftShiftKey.isPressed ? 1 : 0) | (kb.rightShiftKey.isPressed ? 2 : 0) | (kb.spaceKey.isPressed ? 4 : 0) | (kb.escapeKey.wasPressedThisFrame ? 8 : 0);
#else
        return (Input.GetKey(KeyCode.LeftShift) ? 1 : 0) | (Input.GetKey(KeyCode.RightShift) ? 2 : 0) | (Input.GetKey(KeyCode.Space) ? 4 : 0) | (Input.GetKeyDown(KeyCode.Escape) ? 8 : 0);
#endif
    }

    void SetPaused(bool p)
    {
        if (p == paused) return;
        paused = p;
        pull = 0f;
        if (ball) ball.Frozen = p;
        if (p) Msg("PAUSED", "CLICK HERE TO PLAY", 1e9f, false);
        else if (msg == "PAUSED") msgUntil = 0f;
    }

    // let go of the plunger: up it goes, the longer it was pulled the harder
    void Launch(float power)
    {
        if (!ball || power < 0.05f) return;
        Vector2 c = ball.Center;
        bool onIt = c.x > laneX && ball.Extent.yMin < plungerY + 0.3f;
        plungerOffUntil = simT + 0.12f;
        Play(launchClip, 0.9f + 0.25f * power, 0.9f);
        if (!onIt) return;
        ball.Hop(Vector2.up * Mathf.Lerp(10f, 19f, power), 0.3f);
        Launches++;
        if (Random.value < 0.5f) Say(power > 0.8f ? "WHEEEEE!" : "HERE I GO!", 1f);
    }

    // ------------------------------------------------------------------ the ball's turn

    Rect BallRoom => Rect.MinMaxRect(W.xMin, W.yMin, W.xMax, ledgeY);
    Vector2 PlungerSpot(GummyBody b) => new Vector2(laneX + laneW * 0.5f, plungerRest + 0.09f + b.size * 0.5f + 0.02f);

    void NextBall()
    {
        BallNumber++;
        if (BallNumber > balls) { GameOver(); return; }
        GummyBody b = null;
        for (int i = 0; i < order.Count && !b; i++)
        {
            var c = order[nextBall++ % order.Count];
            if (c && c.isActiveAndEnabled && !c.Held && !c.IsMonster) b = c;
        }
        if (!b) foreach (var f in fans) if (f.b && !f.b.Held) { b = f.b; break; }
        if (!b || !TakeBall(b)) { GameOver(); return; }
        Enter(Phase.Ready);
        Msg("BALL " + BallNumber, "HOLD SPACE, LET GO", 2.5f, false);
    }

    bool TakeBall(GummyBody b)
    {
        var f = FanOf(b);
        if (f != null) Unseat(f, true, false);
        var br = b.GetComponent<GummyBrain>();
        if (!br) return false;
        if (br.Current == GummyBrain.State.Asleep) br.WakeUp();
        if (b.IsStuck) b.Hop(Vector2.zero, 0.3f); // (stuck to something: it lets go)
        if (!br.Towering && !br.TowerStart()) return false;
        ball = b;
        savedGravity = b.gravity; savedRestitution = b.restitution; savedSticky = b.sticky; savedDamping = b.damping;
        b.gravity = ballGravity;
        b.damping = 0.0006f; // (it rolls on: a buddy's own air drag would stop a pinball dead)
        b.restitution = 0.3f;
        b.sticky = false;
        b.Tumble = true;
        b.Frozen = paused;
        b.Room = BallRoom;
        b.Arena = this;
        var ly = b.GetComponent<BuddyLayer>();
        if (ly) ly.ForceFront = true;
        var face = b.GetComponent<CuteFace>();
        if (face) { face.Amazed = true; face.LookAt = null; }
        Vector2 at = PlungerSpot(b);
        Poof(b.Center);
        b.Teleport(at);
        Poof(at);
        lastC = at;
        tilted = false;
        saveUsed = false;
        stuckKicks = 0;
        Say(Random.value < 0.5f ? "MY TURN!" : "LAUNCH ME!", 1.6f);
        return true;
    }

    // the ball's turn is over: its own gravity and bounce back; up into the stands (toSeat) or just let go
    void ReleaseBall(bool toSeat)
    {
        var b = ball;
        ball = null;
        if (!b) return;
        b.Arena = null;
        b.Room = new Rect();
        b.Tumble = false;
        b.Frozen = false;
        b.gravity = savedGravity;
        b.damping = savedDamping;
        b.restitution = savedRestitution;
        b.sticky = savedSticky;
        var ly = b.GetComponent<BuddyLayer>();
        if (ly) ly.ForceFront = false;
        var face = b.GetComponent<CuteFace>();
        if (face) { face.Amazed = false; face.LookAt = null; }
        if (toSeat && Seat(b, true) != null) return;
        var br = b.GetComponent<GummyBrain>();
        if (br && br.Towering) br.TowerEnd();
    }

    void ReadyStep()
    {
        if (!ball) { NextBall(); return; }
        if (ball.Held) { Tilt(); return; }
        Vector2 c = ball.Center;
        if (c.y > laneTop + 0.3f || c.x < laneX - 0.3f) { Enter(Phase.Live); liveAt = simT; trapMin = trapMax = lastC = c; trapFrom = Time.time; stuckKicks = 0; }
    }

    void LiveStep(float dt)
    {
        if (!ball) { Enter(Phase.Drain); tilted = true; return; }
        if (ball.Held) { Tilt(); return; }
        Vector2 c = ball.Center;
        Lights(c);
        // down through the gap: into the bin
        if (c.y < tipY - 0.3f && Mathf.Abs(c.x - fcx) < gapHalf + 0.45f) { Drained(); return; }
        // back in the lane somehow (under the gate): launch it again
        if (c.x > laneX + 0.05f && c.y < laneTop - 0.2f) { Enter(Phase.Ready); return; }
        lidOpen = c.y < tipY + 1.6f && Mathf.Abs(c.x - fcx) < gapHalf + 1.2f && ball.Velocity.y < 0f ? 1f : 0f;
        // stuck somewhere (wedged, or bouncing about in a little gap) and no flipper's touched it for a while: a nudge
        // towards the middle, and after a few the lane again
        trapMin = Vector2.Min(trapMin, c); trapMax = Vector2.Max(trapMax, c);
        if (trapMax.x - trapMin.x > 1.3f || trapMax.y - trapMin.y > 1.3f || paused) { trapMin = trapMax = c; trapFrom = Time.time; }
        if (Time.time - trapFrom > 5f)
        {
            trapMin = trapMax = c;
            trapFrom = Time.time;
            if (++stuckKicks > 3) { Msg("BALL STUCK", "BACK TO THE LANE", 2f, false); Poof(c); ball.Teleport(PlungerSpot(ball)); Enter(Phase.Ready); stuckKicks = 0; return; }
            ball.Hop(new Vector2(Mathf.Sign(fcx - c.x) * 4f, c.y > 0.5f * (W.yMin + ledgeY) ? -2f : 6f), 0.3f);
            Msg("NUDGE!", "", 1f, false);
        }
    }

    void Drained()
    {
        Enter(Phase.Drain);
        Drains++;
        ball.Arena = null;
        ball.Room = Rect.MinMaxRect(binR.xMin + 0.1f, binR.yMin + 0.03f, binR.xMax - 0.1f, tipY + 0.5f);
        saved = !saveUsed && simT - liveAt < ballSave; // (once a ball)
        saveUsed |= saved;
        gulped = false;
        lidOpen = 1f;
        Say(saved ? "WHOOPS!" : Random.value < 0.5f ? "NOOOOO!" : "AAAAH!", 1.2f);
        if (!saved) Cheer(0.5f, Random.value < 0.5f ? "AWWW!" : "OH NO!");
    }

    void DrainStep()
    {
        if (tilted)
        {
            if (phaseT > 1.6f) NextBall();
            return;
        }
        if (!ball) { tilted = true; return; }
        if (!gulped && (ball.Center.y < binR.yMax - 0.1f || phaseT > 0.8f))
        {
            gulped = true;
            lidOpen = 0f;
            shake = 0.6f;
            Play(gulpClip, 1f, 0.9f);
        }
        if (phaseT < 1.6f) return;
        if (saved)
        {
            // the ball save: straight back into the lane, same buddy
            Saves++;
            Msg("BALL SAVED!", "", 1.8f, true);
            var b = ball;
            b.Room = BallRoom;
            b.Arena = this;
            Poof(b.Center);
            b.Teleport(PlungerSpot(b));
            Poof(PlungerSpot(b));
            lastC = b.Center;
            Enter(Phase.Ready);
            Say("PHEW!", 1.2f);
            return;
        }
        ReleaseBall(true);
        if (BallNumber >= balls) GameOver();
        else NextBall();
    }

    void Tilt()
    {
        Tilts++;
        Msg("TILT!", "HANDS OFF THE BALL", 2f, true);
        var b = ball;
        ReleaseBall(false); // (it's in your hand: it's yours; back to the stands when you let go)
        if (b) loose.Add(new Loose { b = b });
        tilted = true;
        Enter(Phase.Drain);
        Play(overClip, 1.5f, 0.5f);
    }

    void GameOver()
    {
        Enter(Phase.Over);
        if (ball) ReleaseBall(true);
        bool best = newHigh;
        SaveHigh();
        Msg("GAME OVER", best ? "NEW HIGH SCORE!" : "SPACE: PLAY AGAIN", 1e9f, false);
        Play(best ? jackpotClip : overClip, 1f, 0.9f);
        Cheer(1f, best ? "NEW RECORD!!" : "GOOD GAME!");
    }

    // again (space or a click on the scoreboard after the game): same table, same crowd
    void Restart()
    {
        Score = 0;
        BallNumber = 0;
        newHigh = false;
        foreach (var t in targets) t.lit = false;
        msgUntil = 0f;
        Enter(Phase.Setup);
        phaseT = 0.5f; // (the table's already out)
        Play(startClip, 1f, 0.9f);
    }

    // ------------------------------------------------------------------ targets (your icons) and scoring

    void Lights(Vector2 c)
    {
        float br = ball.size * 0.35f;
        foreach (var t in targets)
        {
            if (t.lit) continue;
            if (DistToSeg(t.c, lastC, c) > t.r + br) continue;
            t.lit = true;
            t.flash = 1f;
            Lit++;
            Add(500, t.c, null);
            int on = 0;
            foreach (var q in targets) if (q.lit) on++;
            if (on == targets.Count)
            {
                Jackpots++;
                Add(5000, t.c + Vector2.up * 0.4f, "JACKPOT!");
                Msg("JACKPOT!", "ALL ICONS LIT", 2.5f, true);
                Play(jackpotClip, 1f, 1f);
                jackpotAt = Time.unscaledTime;
                Cheer(1f, "JACKPOT!!");
                Say("JACKPOT!", 1.5f);
            }
            else
            {
                Play(dingClip, 1f + 0.06f * on, 0.8f);
                Msg("TARGET " + on + "/" + targets.Count, "", 1.2f, false);
            }
        }
        lastC = c;
    }

    static float DistToSeg(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float l2 = ab.sqrMagnitude;
        if (l2 < 1e-8f) return (p - a).magnitude;
        return (p - (a + ab * Mathf.Clamp01(Vector2.Dot(p - a, ab) / l2))).magnitude;
    }

    void Add(int points, Vector2 at, string label)
    {
        Score += points;
        if (Score > High) { if (!newHigh && High > 0) { Msg("HIGH SCORE!", "", 1.5f, true); Cheer(0.6f, "NEW RECORD!"); } newHigh = true; High = Score; HighBy = ball ? NameOf(ball) : HighBy; }
        Popup(label ?? ("+" + points), at, label != null);
    }

    void Msg(string big, string small, float seconds, bool blink)
    {
        msg = big;
        msg2 = small ?? "";
        msgUntil = Time.unscaledTime + seconds;
        msgBlink = blink;
    }

    static string NameOf(GummyBody b)
    {
        var sp = b ? b.GetComponent<BuddySpeech>() : null;
        return sp ? sp.Name.ToUpperInvariant() : "BUDDY";
    }

    float saidAt = -9f;
    void Say(string line, float seconds)
    {
        var sp = ball ? ball.GetComponent<BuddySpeech>() : null;
        if (!sp || Time.unscaledTime - saidAt < 1.2f) return;
        saidAt = Time.unscaledTime;
        sp.Say(line, seconds, true);
    }

    // ------------------------------------------------------------------ the crowd: the stands on top, and the other screens

    class Fan { public GummyBody b; public GummyBrain brain; public CuteFace face; public BuddyLayer layer; public Rect seat; public float nextHop; }
    readonly List<Fan> fans = new List<Fan>();
    class Loose { public GummyBody b; public float letGo = -1f; }
    readonly List<Loose> loose = new List<Loose>();

    Fan FanOf(GummyBody b) { foreach (var f in fans) if (f.b == b) return f; return null; }

    // up into the stands: a seat of its own on the shelf, in front of everything, watching the ball
    Fan Seat(GummyBody b, bool poof)
    {
        var have = FanOf(b);
        if (have != null) return have;
        if (!b || !b.isActiveAndEnabled) return null;
        var br = b.GetComponent<GummyBrain>();
        if (!br) return null;
        if (br.Current == GummyBrain.State.Asleep) br.WakeUp();
        if (b.IsStuck) b.Hop(Vector2.zero, 0.3f); // (stuck to something: it lets go)
        if (!br.Towering && !br.TowerStart()) return null;
        if (!FreeSeat(out Rect seat)) { br.TowerEnd(); return null; }
        var f = new Fan { b = b, brain = br, face = b.GetComponent<CuteFace>(), layer = b.GetComponent<BuddyLayer>(), seat = seat, nextHop = Time.unscaledTime + Random.Range(1f, 4f) };
        b.Room = seat;
        if (f.layer) f.layer.ForceFront = true;
        Vector2 at = new Vector2(seat.center.x, seat.yMin + b.size * 0.5f + 0.02f);
        if (poof) Poof(b.Center);
        b.Teleport(at);
        if (poof) Poof(at);
        fans.Add(f);
        return f;
    }

    void Unseat(Fan f, bool keepTower, bool hop)
    {
        fans.Remove(f);
        var b = f.b;
        if (!b) return;
        b.Room = new Rect();
        if (f.layer) f.layer.ForceFront = false;
        if (f.face) f.face.LookAt = null;
        if (!keepTower && f.brain && f.brain.Towering) f.brain.TowerEnd();
        if (hop && !b.Held) b.Hop(new Vector2(Random.Range(-1.5f, 1.5f), 3f), 0.4f);
    }

    // the seats: along the shelf, nearest the scoreboard first, left and right
    bool FreeSeat(out Rect seat)
    {
        const float sw = 0.95f;
        float y0 = ledgeY + shelfT, y1 = W.yMax;
        for (int i = 0; i < 40; i++)
        {
            int k = i / 2;
            float x = i % 2 == 0 ? sbRect.xMax + 0.12f + k * sw : sbRect.xMin - 0.12f - (k + 1) * sw;
            if (x < W.xMin + 0.02f || x + sw > W.xMax - 0.02f) continue;
            var r = Rect.MinMaxRect(x, y0, x + sw, y1);
            bool taken = false;
            foreach (var f in fans) if (Mathf.Abs(f.seat.xMin - r.xMin) < 0.01f) { taken = true; break; }
            if (!taken) { seat = r; return true; }
        }
        seat = default;
        return false;
    }

    void Crowd(float now)
    {
        Vector2? watch = ball ? ball.Center : (Vector2?)null;
        for (int i = fans.Count - 1; i >= 0; i--)
        {
            var f = fans[i];
            if (!f.b || !f.b.isActiveAndEnabled) { fans.RemoveAt(i); continue; }
            if (f.b.Held) { Unseat(f, false, false); loose.Add(new Loose { b = f.b }); continue; } // (you took it out of the stands)
            if (f.face) f.face.LookAt = watch;
            if (now >= f.nextHop && f.b.Grounded && !paused)
            {
                f.nextHop = now + Random.Range(2.5f, 6f);
                f.b.Hop(new Vector2(Random.Range(-0.6f, 0.6f), Random.Range(2.5f, 3.6f)), 0.4f);
            }
        }
        // the ones you took out (or the ball you grabbed): back up into the stands a moment after you let go
        for (int i = loose.Count - 1; i >= 0; i--)
        {
            var l = loose[i];
            if (!l.b || !l.b.isActiveAndEnabled) { loose.RemoveAt(i); continue; }
            if (l.b.Held) { l.letGo = -1f; continue; }
            if (l.letGo < 0f) l.letGo = now;
            if (now - l.letGo > 0.8f) { loose.RemoveAt(i); Seat(l.b, true); }
        }
    }

    // everyone cheers: the stands hop, one shouts; the ones on your other screens join in
    void Cheer(float strength, string line)
    {
        bool said = false;
        foreach (var f in fans)
        {
            if (!f.b || Random.value > 0.4f + 0.6f * strength) continue;
            if (f.b.Grounded) f.b.Hop(new Vector2(0f, 3f + 2f * strength), 0.4f);
            f.nextHop = Time.unscaledTime + Random.Range(1.5f, 4f);
            if (!said && line != null && Random.value < 0.6f) { var sp = f.b.GetComponent<BuddySpeech>(); if (sp) said = sp.Say(line, 1.4f, true); }
        }
        if (strength < 0.9f || Time.unscaledTime - cheerAt < 4f) return;
        cheerAt = Time.unscaledTime;
        foreach (var b in GummyBody.All)
        {
            if (!b || b == ball || FanOf(b) != null || !CanPlay(b) || !b.Grounded) continue;
            var br = b.GetComponent<GummyBrain>();
            if (!br || br.Current != GummyBrain.State.Idle) continue;
            b.Hop(new Vector2(0f, 4f), 0.4f);
            var sp = b.GetComponent<BuddySpeech>();
            if (sp && Random.value < 0.5f) sp.Say(Random.value < 0.5f ? "GO GO GO!" : "WOOO!", 1.2f, false);
        }
    }

    // ------------------------------------------------------------------ IGrabTarget: the scoreboard (click: play on / again; X: stop)

    public bool Hovered { get; set; }
    public bool Held => clicking;
    bool clicking;

    public bool Contains(Vector2 p) => phase != Phase.Off && phase != Phase.Closing && (sbRect.Contains(p) || xRect.Contains(p));
    public void BeginGrab(Vector2 p) { clicking = true; }
    public void DragTo(Vector2 p) { }

    public void EndGrab()
    {
        clicking = false;
        Vector2 p = Grabber.CursorWorld;
        if (xRect.Contains(p)) { Close(); return; }
        if (!sbRect.Contains(p)) return;
        if (phase == Phase.Over) Restart();
        TransparentWindow.BeginTyping();
        focusAt = Time.unscaledTime;
        if (paused) SetPaused(false);
    }

    // ------------------------------------------------------------------ the best score (pinball.txt)

    void Load()
    {
        High = 0;
        HighBy = "";
        try
        {
            if (!File.Exists(FilePath)) return;
            foreach (var line in File.ReadAllLines(FilePath))
            {
                if (line.StartsWith("high=") && int.TryParse(line.Substring(5), out int h)) High = h;
                else if (line.StartsWith("by=")) HighBy = line.Substring(3);
            }
        }
        catch (System.Exception e) { Debug.LogWarning("[WhipIt] pinball.txt: " + e.Message); }
    }

    void SaveHigh()
    {
        if (!newHigh) return;
        try
        {
            Directory.CreateDirectory(Application.persistentDataPath);
            File.WriteAllText(FilePath, "high=" + High + "\nby=" + HighBy + "\n");
        }
        catch (System.Exception e) { Debug.LogWarning("[WhipIt] pinball.txt: " + e.Message); }
    }

    // ------------------------------------------------------------------ sounds

    void Play(AudioClip c, float pitch, float vol)
    {
        if (!sfx || !c) return;
        sfx.pitch = pitch;
        sfx.PlayOneShot(c, volume * vol);
    }

    void PlayFlip(float pitch, float vol)
    {
        if (!flipSfx || !flipClip) return;
        flipSfx.pitch = pitch * Random.Range(0.97f, 1.03f);
        flipSfx.PlayOneShot(flipClip, volume * vol);
    }
}
