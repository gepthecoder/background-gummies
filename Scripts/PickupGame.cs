using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A pickup game at the Hoop (BuddyHoops does the playing): the buddies that are awake and free split into two teams
/// (strongest first, snake draft: 1v1, 2v1, 2v2, 3v2) and play to 5, a point a basket. Whoever has the ball dribbles to
/// a spot and shoots, passes to an open teammate, or dunks; the other team defends (steals, blocks); after a basket
/// the other team takes it out. You grab the ball = the game waits for you ("HEY! OUR BALL!"); too long and it's off.
/// The winners dance, the losers want a rematch. Starts when they're bored and the hoop's up, right after you put it up,
/// or chat "game" / "1v1" / "pickup"; one at a time.
/// </summary>
[DefaultExecutionOrder(6)]
public class PickupGame : MonoBehaviour
{
    public const int ToWin = 5;

    /// <summary>The game going on (null = none).</summary>
    public static PickupGame Current { get; private set; }
    /// <summary>So far (this session): games started, games played to the end.</summary>
    public static int Started, Finished;

    public readonly List<BuddyHoops>[] Teams = { new List<BuddyHoops>(), new List<BuddyHoops>() };
    public readonly int[] Score = new int[2];
    /// <summary>You've got the ball: they wait.</summary>
    public bool Paused => ball && ball.Held;
    /// <summary>Not right after the ball changed hands.</summary>
    public bool CanSteal => Time.time - lastPossession > 1.2f;
    /// <summary>The team that won (-1 = nobody yet / called off).</summary>
    public int Winner { get; private set; } = -1;
    public bool Over => over;

    Basketball ball;
    float t, lastScore, inboundUntil, lastPossession, heldSince = -1f, overAt;
    int inbound = -1;
    bool over, toldHeld;
    readonly Dictionary<BuddyHoops, float> nextBounce = new Dictionary<BuddyHoops, float>();
    static float s_quietUntil = -100f;

    /// <summary>Which team it's on (-1 = not playing, or the game's over).</summary>
    public int TeamOf(BuddyHoops h) => over || !h ? -1 : Teams[0].Contains(h) ? 0 : Teams[1].Contains(h) ? 1 : -1;

    // ------------------------------------------------------------------ starting one

    /// <summary>Start a game with everyone that's free (force = the chat / menu asked: right now, waking them up).</summary>
    public static bool TryStart(BuddyHoops caller, bool force = false)
    {
        float now = Time.time;
        if (Current || (!force && now < s_quietUntil)) return false;
        var hoop = Hoop.Current;
        var b = Basketball.Current;
        if (!hoop || !hoop.Playable || !b || b.Held || Busy()) return false;
        var players = new List<BuddyHoops>();
        foreach (var h in BuddyHoops.All)
            if (Free(h, force || h == caller) && (h.Body.Center - hoop.Rim).magnitude < 16f) players.Add(h);
        if (players.Count < 2) return false;
        if (players.Count > 5) players.RemoveRange(5, players.Count - 5);
        var go = new GameObject("Pickup Game");
        var g = go.AddComponent<PickupGame>();
        g.Begin(players, caller);
        return true;
    }

    static bool Busy() => LevelUpParty.Current || BuddyHideSeek.Playing || LaserPointer.On || BuddyTower.Current || (FetchBall.Current && FetchBall.Current.InPlay);

    static bool Free(BuddyHoops h, bool asked)
    {
        if (!h || !h.isActiveAndEnabled || !h.Brain || h.Busy) return false;
        var b = h.Body;
        if (b.Held || b.IsStuck || BuddyTower.Has(b) || h.Brain.Towering) return false; // (in a tower, mid-dunk, on the slide)
        var eater = b.GetComponent<BuddyEater>();
        if (eater && eater.Target) return false;
        var fetch = b.GetComponent<BuddyFetch>();
        if (fetch && fetch.Carrying) return false;
        if (h.Brain.Current == GummyBrain.State.Asleep) { if (!asked) return false; h.Brain.WakeUp(); return true; }
        return asked ? h.Brain.Current != GummyBrain.State.Swing && h.Brain.Current != GummyBrain.State.Climb : h.Brain.IsFree;
    }

    void Begin(List<BuddyHoops> players, BuddyHoops caller)
    {
        Current = this;
        Started++;
        ball = Basketball.Current;
        // strongest first, snake draft: A, B, B, A, A
        players.Sort((a, b) => b.Level.CompareTo(a.Level));
        int[] pick = { 0, 1, 1, 0, 0 };
        for (int i = 0; i < players.Count; i++) Teams[pick[i]].Add(players[i]);
        lastScore = lastPossession = Time.time;
        Basketball.Scored += OnScored;
        var c = caller && TeamOf(caller) >= 0 ? caller : players[0];
        string vs = Teams[0].Count + "V" + Teams[1].Count;
        c.Say(1f, c.L("PICKUP GAME! " + vs, vs + "! FIRST TO 5", "GAME ON! " + vs, "BALL IS LIFE. " + vs, vs + "! RAWR"), true);
        foreach (var h in players)
            if (h != c) h.Say(0.6f, h.L("YOU'RE ON!", "BRING IT!", "LET'S GOOO!", "GOT GAME", "RAWR! ON!"), false);
    }

    // ------------------------------------------------------------------ the game

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("PickupGame.Update", t0); }
    void UpdateTimed()
    {
        float dt = Mathf.Min(Time.deltaTime, 0.05f), now = Time.time;
        t += dt;
        if (!ball) ball = Basketball.Current;
        if (over) { Celebrate(now); return; }
        if (!ball || !Hoop.Current) { End(null); return; }
        if (Busy()) { End(null); return; }
        for (int k = 0; k < 2; k++) Teams[k].RemoveAll(h => !h || !h.isActiveAndEnabled);
        if (Teams[0].Count == 0 || Teams[1].Count == 0) { End(null); return; }
        BuddySocial.QuietUntil = Mathf.Max(BuddySocial.QuietUntil, now + 1f); // (no visits, chats or tag while they play)
        foreach (var team in Teams)
            foreach (var h in team)
                if (h.Brain.Current == GummyBrain.State.Asleep) h.Brain.WakeUp();
        // you took the ball: they wait (a while)
        if (Paused)
        {
            if (heldSince < 0f) heldSince = now;
            if (!toldHeld) { toldHeld = true; var s = Teams[0][0]; s.Say(1f, s.L("HEY! OUR BALL!", "HEY! WE'RE PLAYING!", "GIVE IT BACK!", "MY BALL!", "RAWR! OUR BALL!"), true); }
            if (now - heldSince > 25f) { End(Teams[1][0].L("FINE. KEEP IT", "BALL HOG!", "GAME OVER I GUESS", "KEEP IT THEN", "RAWR. FINE")); return; }
            lastScore += dt; // (the clock stops)
            return;
        }
        heldSince = -1f;
        toldHeld = false;
        if (now - lastScore > 75f) { End(Teams[0][0].L("TIE GAME!", "CALL IT A TIE", "WE'RE TIRED", "SNACK BREAK", "TIE! RAWR")); return; }
        if (t > 300f) { Win(Score[0] >= Score[1] ? 0 : 1); return; }
    }

    void OnScored(GummyBody by, Basketball.Shot shot)
    {
        if (over || !by) return;
        var h = by.GetComponent<BuddyHoops>();
        int team = TeamOf(h);
        if (team < 0) return;
        Score[team]++;
        lastScore = Time.time;
        inbound = 1 - team;
        inboundUntil = Time.time + 1.5f;
        h.Say(1f, h.ScoredLine(shot) + " " + Score[team] + "-" + Score[1 - team], true);
        if (Score[team] >= ToWin) Win(team);
    }

    /// <summary>Someone picked it up (or stole it).</summary>
    public void Possession(BuddyHoops h)
    {
        lastPossession = Time.time;
        inbound = -1;
    }

    /// <summary>May it pick up the loose ball? (Right after a basket only the other team may.)</summary>
    public bool CanTouch(BuddyHoops h) => inbound < 0 || Time.time > inboundUntil || TeamOf(h) == inbound;

    /// <summary>It's going up for a shot: the defenders near it jump.</summary>
    public void ShotComing(BuddyHoops shooter)
    {
        int team = TeamOf(shooter);
        if (team < 0) return;
        foreach (var o in Teams[1 - team]) o.Contest(shooter);
    }

    /// <summary>The ball's just left its hands: a defender up there may block it.</summary>
    public void Released(BuddyHoops shooter, Basketball b)
    {
        int team = TeamOf(shooter);
        if (team < 0) return;
        foreach (var o in Teams[1 - team]) if (o.TryBlock(shooter, b)) return;
    }

    /// <summary>A teammate nobody's guarding (the one nearest the rim), or null.</summary>
    public BuddyHoops OpenTeammate(BuddyHoops h)
    {
        int team = TeamOf(h);
        if (team < 0 || !Hoop.Current) return null;
        BuddyHoops best = null;
        float bd = float.MaxValue;
        foreach (var m in Teams[team])
        {
            if (m == h || m.Busy || m.Body.Held || !m.Body.Grounded) continue;
            bool open = true;
            foreach (var o in Teams[1 - team]) if ((o.Body.Center - m.Body.Center).magnitude < 1f) open = false;
            if (!open) continue;
            float d = (m.Body.Center - Hoop.Current.Rim).sqrMagnitude;
            if (d < bd) { bd = d; best = m; }
        }
        return best;
    }

    /// <summary>Any teammate it can pass to, or null.</summary>
    public BuddyHoops AnyTeammate(BuddyHoops h)
    {
        int team = TeamOf(h);
        if (team < 0) return null;
        foreach (var m in Teams[team]) if (m != h && !m.Busy && !m.Body.Held) return m;
        return null;
    }

    // ------------------------------------------------------------------ the end

    void Win(int team)
    {
        Winner = team;
        Finished++;
        over = true;
        overAt = Time.time;
        Basketball.Scored -= OnScored;
        foreach (var h in Teams[team]) h.Say(1f, h.L("WE WIN!", "TOO EASY!", "CHAMPS!", "GG EZ", "RAWR! CHAMPS!"), true);
        foreach (var h in Teams[1 - team]) h.Say(0.8f, h.L("GG", "REMATCH!", "I LET YOU WIN", "NEXT TIME", "RAWR... GG"), false);
        foreach (var h in Teams[team]) { var p = h.Body.GetComponent<BuddyProgress>(); if (p) p.AddXp(2, h.Body.Center); }
        // whoever has the ball drops it
        if (ball && ball.Carrier) ball.Release(new Vector2(0f, 1.5f));
    }

    // the winners bounce around for a few seconds
    void Celebrate(float now)
    {
        if (Winner >= 0)
            foreach (var h in Teams[Winner])
            {
                if (!h || !h.Body.Grounded || h.Body.Held) continue;
                if (!nextBounce.TryGetValue(h, out float nb)) { nextBounce[h] = now + Random.Range(0.1f, 0.5f); continue; }
                if (now < nb) continue;
                nextBounce[h] = now + Random.Range(0.5f, 0.9f);
                h.Body.Hop(new Vector2(0f, GummyBody.JumpSpeed(Random.Range(0.3f, 0.55f) * h.Body.size / 0.6f, h.Body.gravity, h.Body.Drag)), 0.5f);
                h.Brain.Linger(2f);
            }
        if (now - overAt > 3.5f) Close();
    }

    /// <summary>Call it off (line: what someone says about it).</summary>
    public void End(string line)
    {
        if (line != null && Teams[0].Count > 0 && Teams[0][0]) Teams[0][0].Say(1f, line, true);
        Close();
    }

    void Close()
    {
        Basketball.Scored -= OnScored;
        s_quietUntil = Time.time + 60f;
        foreach (var team in Teams)
            foreach (var h in team)
                if (h && h.Brain) h.Brain.Food = null;
        over = true;
        if (Current == this) Current = null;
        enabled = false;
        Destroy(gameObject);
    }

    void OnDisable()
    {
        Basketball.Scored -= OnScored;
        if (Current == this) Current = null;
    }
}
