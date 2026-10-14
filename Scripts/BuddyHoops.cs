using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Basketball (Hoop + Basketball) for one buddy: it races for the rebound when you shoot and passes the ball back to
/// you; it plays pickup games (PickupGame: dribbles, shoots, passes, defends, steals, blocks); and it dunks: a plain
/// slam once it leaps high enough, the burger off its pogo fork, the tomato on its jetpack (when it works), the dino off
/// a super stomp, and anyone off a teammate's head (a two-high tower: an alley-oop). Shooting gets better every level:
/// bricks and airballs when it's little, swishes later and from further out; ON FIRE it hardly misses.
/// Chat: "shoot", "dunk" (it gets the ball and shows off). Snacks still come first.
/// </summary>
[DefaultExecutionOrder(5)] // after BuddyEater has set (or cleared) its snack: the ball only when there's no snack
[RequireComponent(typeof(GummyBody))]
[DisallowMultipleComponent]
public class BuddyHoops : MonoBehaviour
{
    public enum Dunk { None, Plain, Pogo, Jetpack, Stomp, Tower }

    [Tooltip("Bored with the hoop up and a friend around: chance per minute it starts a pickup game.")]
    [Range(0f, 1f)] public float gameChance = 0.35f;

    static readonly List<BuddyHoops> s_all = new List<BuddyHoops>();
    public static IReadOnlyList<BuddyHoops> All => s_all;

    public GummyBody Body => body;
    public GummyBrain Brain => brain;
    public int Level => skills ? skills.Level : 10;
    /// <summary>It has the ball.</summary>
    public bool HasBall => ball && ball.Carrier == body;
    /// <summary>In the middle of a shot, a dunk or a pass (or boosting a teammate's dunk).</summary>
    public bool Busy => act != Act.None || boostFor;
    /// <summary>Furthest it shoots from (world units): grows with its level.</summary>
    public float Range => Mathf.Lerp(1.6f, 5f, Mathf.Clamp01((Level - 1) / 9f)) + 0.2f * Mathf.Clamp(Level - 10, 0, 8);
    public int Shots { get; private set; }
    public int Dunks { get; private set; }
    public int Blocks { get; private set; }
    public int Steals { get; private set; }
    public int Passes { get; private set; }
    /// <summary>Dunks by kind so far (tests).</summary>
    public readonly int[] DunksBy = new int[6];
    /// <summary>It's standing still as the base of a teammate's alley-oop.</summary>
    public bool Boosting => boostFor;

    enum Act { None, Lift, Jump, Pass, DunkWind, DunkClimb, DunkFly, DunkHang }
    enum Plan { None, Shoot, Dunk }

    GummyBody body;
    GummyBrain brain;
    BuddySkills skills;
    BuddySpeech speech;
    BuddyEater eater;
    BuddyPogo pogo;
    BuddyJetpack jetpack;
    BuddyStomp stomp;
    Basketball ball;
    Act act;
    Plan plan, showOff;
    Dunk dunk;
    float actT, holdSince = -1f, nextPass, nextGameRoll, lastLine = -100f, reboundSince = -100f, boostSince, dunkDur, cOff;
    bool rebounding, passBack, hadBall, dunkFail;
    int jetOutcome;
    Vector2 dunkFrom, spot, passTo;
    float spotY;
    BuddyHoops partner, boostFor, passTarget, helpFor;
    float helpSince;

    float S => body.size / 0.6f;
    float Feet => body.Extent.yMin - body.SkateLift;
    float Height => Mathf.Max(body.Extent.height, body.size * 0.7f);
    float HalfW => Mathf.Max(body.Extent.width, body.size * 0.8f) * 0.5f;

    void Awake()
    {
        body = GetComponent<GummyBody>();
        brain = GetComponent<GummyBrain>();
        skills = GetComponent<BuddySkills>();
        speech = GetComponent<BuddySpeech>();
        eater = GetComponent<BuddyEater>();
        pogo = GetComponent<BuddyPogo>();
        jetpack = GetComponent<BuddyJetpack>();
        stomp = GetComponent<BuddyStomp>();
        nextGameRoll = Time.time + 8f;
    }

    void OnEnable()
    {
        if (!s_all.Contains(this)) s_all.Add(this);
        Basketball.UserShot += OnUserShot;
        Basketball.Scored += OnScored;
        Basketball.Missed += OnMissed;
        Basketball.CaughtFire += OnFire;
    }

    void OnDisable()
    {
        s_all.Remove(this);
        Basketball.UserShot -= OnUserShot;
        Basketball.Scored -= OnScored;
        Basketball.Missed -= OnMissed;
        Basketball.CaughtFire -= OnFire;
        Stop();
    }

    // anything it was in the middle of: off (out of a dunk it falls, the ball drops)
    void Stop()
    {
        if (act >= Act.DunkWind && brain && brain.Towering) brain.TowerEnd();
        if (ball && ball.Carrier == body) ball.Release(body.Velocity * 0.5f);
        if (ball) ball.Lift = false;
        act = Act.None;
        plan = showOff = Plan.None;
        rebounding = passBack = false;
        if (partner) { partner.EndBoost(); partner = null; }
        EndBoost();
        if (jetpack) jetpack.Thrust(0f);
    }

    // ------------------------------------------------------------------ chat / menu

    /// <summary>Get the ball and take a shot (chat "shoot"). False if there's no hoop or ball, or it's busy.</summary>
    public bool ShootNow() => Order(Plan.Shoot);
    /// <summary>Get the ball and dunk it (chat "dunk"). False if it can't dunk on this hoop (yet), or it's busy.</summary>
    public bool DunkNow() => CanDunk(false, out _) != Dunk.None && Order(Plan.Dunk);

    bool Order(Plan p)
    {
        ball = Basketball.Current;
        if (!ball || !Hoop.Current || !Hoop.Current.Playable || Busy || PickupGame.Current || ball.Held || (ball.Carrier && ball.Carrier != body)) return false;
        if (brain.Current == GummyBrain.State.Asleep) brain.WakeUp();
        showOff = p;
        plan = p;
        holdSince = Time.time;
        return true;
    }

    /// <summary>Is it one of the players in the pickup game going on?</summary>
    public bool Playing => PickupGame.Current && PickupGame.Current.TeamOf(this) >= 0;

    // ------------------------------------------------------------------ each frame

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("BuddyHoops.Update", t0); }
    void UpdateTimed()
    {
        ball = Basketball.Current;
        var hoop = Hoop.Current;
        if (!ball || !hoop || !brain) { if (act != Act.None || rebounding || showOff != Plan.None) Stop(); return; }
        float now = Time.time, dt = Mathf.Min(Time.deltaTime, 0.05f);
        if (BuddyTower.Has(body)) return;
        if (act != Act.None) { DoAct(hoop, dt); return; }
        if (boostFor) { Boost(hoop); return; }
        bool has = HasBall;
        if (has && !hadBall) { holdSince = now; nextPass = now + Random.Range(1.2f, 2.2f); if (plan == Plan.None) plan = ChoosePlan(); }
        if (!has && hadBall && act == Act.None) { plan = Plan.None; CancelDunk(); }
        hadBall = has;
        var game = PickupGame.Current;
        int team = game ? game.TeamOf(this) : -1;
        if (has)
        {
            brain.Linger(1.5f);
            if (eater && eater.Target) { ball.Release(new Vector2(0f, 1.5f)); return; } // a snack: drops it
            if (team >= 0) { Handle(hoop, game, now); return; }
            if (helpFor) { var to = helpFor; helpFor = null; StartPass(to, null); return; } // (it was in a friend's way: passes it over)
            if (showOff != Plan.None) { Handle(hoop, null, now); return; }
            PassBack(now); // (a rebound, or it just picked it up: back to you)
            return;
        }
        if (eater && eater.Target) return; // snacks first
        if (team >= 0) { OffBall(hoop, game, now); return; }
        if (helpFor)
        {
            if (now - helpSince > 8f || !helpFor.isActiveAndEnabled || ball.Carrier || ball.Held) { helpFor = null; brain.Food = null; return; }
            Chase(dt);
            return;
        }
        if (showOff != Plan.None || rebounding)
        {
            if (rebounding && now - reboundSince > 15f) { rebounding = false; brain.Food = null; return; }
            if (ball.Carrier && ball.Carrier != body)
            {
                var c = ball.Carrier.GetComponent<BuddyHoops>();
                if (c && (c.helpFor == this || (c.act == Act.Pass && c.passTarget == this))) { brain.Food = null; return; } // (a friend's passing it over)
                rebounding = false; showOff = Plan.None; brain.Food = null; return;
            }
            if (ball.Held) { brain.Food = null; return; }
            Chase(dt);
            return;
        }
        // the hoop's just gone up: a game (if there's someone to play with)
        if (!game && hoop.Fresh && brain.IsFree && PickupGame.TryStart(this)) return;
        // bored, the hoop's up and a friend's around: a pickup game
        if (!game && now >= nextGameRoll && brain.IsFree)
        {
            nextGameRoll = now + 10f;
            if (Random.value < gameChance / 6f) PickupGame.TryStart(this);
        }
    }

    // ------------------------------------------------------------------ with the ball

    Plan ChoosePlan()
    {
        if (showOff != Plan.None) return showOff;
        return CanDunk(Playing, out _) != Dunk.None && Random.value < 0.5f ? Plan.Dunk : Plan.Shoot;
    }

    void Handle(Hoop hoop, PickupGame game, float now)
    {
        if (!hoop.Playable) { brain.Food = null; return; }
        if (game && game.Paused) { brain.Food = null; return; }
        float held = now - holdSince;
        // a teammate open and it's been a moment: pass
        if (game && plan == Plan.Shoot && held > 1.2f && now >= nextPass)
        {
            nextPass = now + 1f;
            var open = game.OpenTeammate(this);
            if (open && Random.value < 0.35f) { StartPass(open, null); return; }
        }
        if (plan == Plan.Dunk && DunkPlay(hoop, game, now, held)) return;
        if (plan == Plan.Dunk) plan = Plan.Shoot;
        // to a spot it shoots from (in front of the rim, within its range), then shoot
        bool clock = held > 7f;
        if (!clock && FindSpot(hoop, out float sx, out float sy))
        {
            if (At(sx, sy)) { if (Settled()) StartShot(hoop); else brain.Food = null; }
            else MoveTo(sx, sy);
            return;
        }
        // nowhere to shoot from (or the shot clock): a pass if there's a teammate, else a heave from here
        if (game && held > 2f) { var mate = game.AnyTeammate(this); if (mate) { StartPass(mate, null); return; } }
        if (clock || held > 4f) { if (Settled()) StartShot(hoop); return; }
        brain.Food = null;
    }

    // after a rebound of your shot (or picked up): back to you
    void PassBack(float now)
    {
        rebounding = false;
        if (!Settled() && now - holdSince < 1.5f) { brain.Food = null; return; }
        Rect r = ScreenBounds.WorldRect;
        Vector2 c = Grabber.CursorWorld;
        if (!r.Contains(c)) c = new Vector2(Mathf.Clamp(c.x, r.xMin + 0.5f, r.xMax - 0.5f), Mathf.Clamp(c.y, r.yMin + 0.5f, r.yMax - 0.5f));
        StartPass(null, c);
    }

    void StartPass(BuddyHoops to, Vector2? at)
    {
        passTarget = to;
        passTo = at ?? Vector2.zero;
        act = Act.Pass;
        actT = 0f;
        brain.Food = null;
        brain.Linger(1f);
    }

    void StartShot(Hoop hoop)
    {
        act = Act.Lift;
        actT = 0f;
        ball.Lift = true;
        brain.Food = null;
        brain.Linger(2f);
        if (PickupGame.Current) PickupGame.Current.ShotComing(this);
    }

    // ------------------------------------------------------------------ shots, passes, dunks in progress

    void DoAct(Hoop hoop, float dt)
    {
        actT += dt;
        bool hasBall = HasBall;
        if (body.Held) { Stop(); return; }
        switch (act)
        {
            case Act.Lift:
                if (!hasBall) { act = Act.None; return; }
                body.Pose = new Vector2(1.08f, 0.93f);
                if (actT < 0.3f) return;
                body.Pose = Vector2.one;
                body.Hop(new Vector2(-body.Velocity.x, GummyBody.JumpSpeed(0.35f * S, body.gravity, body.Drag) - body.Velocity.y), 0.3f);
                act = Act.Jump;
                actT = 0f;
                return;
            case Act.Jump:
                if (!hasBall) { act = Act.None; return; }
                if (actT < 0.12f && body.Velocity.y > 0.6f) return;
                Release(hoop);
                act = Act.None;
                return;
            case Act.Pass:
                if (!hasBall) { act = Act.None; return; }
                if (actT < 0.2f) return;
                Vector2 to = passTarget ? passTarget.body.Center + new Vector2(0f, passTarget.body.size * 0.25f) : passTo;
                if (!Basketball.Aim(ball.Position, to, 0.35f + 0.08f * Mathf.Abs(to.x - ball.Position.x), ball.gravity, out Vector2 v)) v = new Vector2(Mathf.Sign(to.x - ball.Position.x) * 2f, 3f);
                ball.Release(Vector2.ClampMagnitude(v, 14f));
                Passes++;
                if (!passTarget) Say(0.6f, L("HERE YOU GO!", "CATCH!", "YOUR BALL!", "SHOOT AGAIN!", "HERE! RAWR"), false);
                act = Act.None;
                return;
            default:
                DunkStep(hoop, dt);
                return;
        }
    }

    // the shot: aimed at the rim, off by however good it is
    void Release(Hoop hoop)
    {
        Vector2 v = ShotVelocity(ball.Position, hoop.Rim, Level, Range, ball.OnFire && ball.FireOwner == body, ball.gravity);
        ball.Shoot(v, body);
        Shots++;
        showOff = Plan.None; // (a show-off shot: just the one)
        if (PickupGame.Current) PickupGame.Current.Released(this, ball);
    }

    /// <summary>
    /// How a buddy of this level throws the ball from `from` at the rim: aimed (Basketball.Aim), then off by its aim and
    /// its feel for the distance, both better every level (and far better ON FIRE); beyond its range it's a heave.
    /// </summary>
    public static Vector2 ShotVelocity(Vector2 from, Vector2 rimAt, int level, float range, bool onFire, float g)
    {
        Vector2 rim = rimAt + new Vector2(0f, 0.03f);
        float d = Mathf.Abs(rim.x - from.x);
        float t = Mathf.Clamp01((level - 1) / 9f);
        float err = Mathf.Lerp(AimWorst, AimAt10, t) * Mathf.Lerp(0.75f, 1.2f, Mathf.Clamp01(d / Mathf.Max(range, 0.5f)));
        if (level > 10) err *= Mathf.Lerp(1f, 0.5f, Mathf.Clamp01((level - 10) / 8f));
        if (onFire) err *= 0.35f;                    // ON FIRE
        if (d > range) err *= 1f + (d - range) * 0.6f; // (a heave)
        Vector2 aim = rim + new Vector2(Gauss() * err, 0f);
        float clear = 0.45f + 0.12f * d;
        if (!Basketball.Aim(from, aim, clear, g, out Vector2 v)) v = new Vector2(Mathf.Sign(rim.x - from.x) * 1f, 6f);
        return v * (1f + Gauss() * err * SpeedErr);
    }

    /// <summary>Aim error (world units, one sigma) at level 1 and level 10, and how much of it goes into the throw's speed.</summary>
    public static float AimWorst = 0.8f, AimAt10 = 0.24f, SpeedErr = 0.12f; // (about 15% at level 1, 35% at 5, 75% at 10, 85% from 14)

    static float Gauss()
    {
        float u1 = Mathf.Max(Random.value, 1e-4f), u2 = Random.value;
        return Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Cos(2f * Mathf.PI * u2);
    }

    // ------------------------------------------------------------------ where to shoot from

    // on the surface it's on if the rim's in range from there, else on what's below the rim in front of it
    bool FindSpot(Hoop hoop, out float x, out float y)
    {
        x = y = 0f;
        Vector2 rim = hoop.Rim;
        int side = hoop.Side;
        float range = Range, hw = HalfW;
        BuddyRoute.Build(Height, body.solidWindows);
        var surfs = BuddyRoute.Surfaces;
        int mine = BuddyRoute.SurfaceAt(body.Center.x, Feet);
        float bestCost = float.MaxValue;
        for (int k = 0; k < 2; k++)
        {
            int si;
            if (k == 0) si = mine;
            else si = BuddyRoute.SurfaceBelow(rim.x + side * Mathf.Min(2.2f, range * 0.8f), rim.y - 0.3f);
            if (si < 0) continue;
            BuddyRoute.Surface s = surfs[si];
            if (s.y > rim.y - 0.3f) continue;
            float dMin = 1f, dMax = Mathf.Max(Mathf.Min(range * 0.85f, 6f), 1.2f);
            // the part of this surface in front of the rim at a good distance
            float a = rim.x + side * dMin, b = rim.x + side * dMax;
            float lo = Mathf.Max(Mathf.Min(a, b), s.x0 + hw), hi = Mathf.Min(Mathf.Max(a, b), s.x1 - hw);
            if (lo > hi) continue;
            float want = k == 0 ? Mathf.Clamp(body.Center.x, lo, hi) : Mathf.Clamp(rim.x + side * Mathf.Min(2.2f, range * 0.7f), lo, hi);
            if (!ShotClear(hoop, new Vector2(want, s.y + Height + 0.4f * S))) continue;
            float cost = Mathf.Abs(want - body.Center.x) + (k == 0 ? 0f : 1f);
            if (k == 1 && !(si == mine) && !brain.PlanTo(new Vector2(want, s.y + 0.3f), s.y, out float c)) continue;
            if (cost < bestCost) { bestCost = cost; x = want; y = s.y; }
            if (k == 0 && bestCost < float.MaxValue) break;
        }
        return bestCost < float.MaxValue;
    }

    // nothing in the way of a shot from there (a window over it, or between it and the rim)
    bool ShotClear(Hoop hoop, Vector2 from)
    {
        Vector2 rim = hoop.Rim;
        if (!Basketball.Aim(from, rim, 0.45f + 0.12f * Mathf.Abs(rim.x - from.x), 18f, out Vector2 v)) return false;
        Vector2 p = from;
        const float h = 1f / 60f;
        for (int i = 0; i < 240; i++)
        {
            v.y -= 18f * h;
            v *= 1f - Basketball.Drag * h;
            p += v * h;
            if ((p - rim).sqrMagnitude < 0.36f) return true;
            if (BuddyRoute.Blocked(p, 0.14f, 0.14f, true)) return false;
            if (v.y < 0f && p.y < rim.y - 0.5f) return false;
        }
        return false;
    }

    bool At(float x, float y) => Mathf.Abs(body.Center.x - x) < Mathf.Max(0.3f * body.size, 0.15f) && Mathf.Abs(Feet - y) < 0.25f && body.Grounded;
    bool Settled() => body.Grounded && !body.Held && body.Velocity.sqrMagnitude < 0.6f;

    void MoveTo(float x, float y)
    {
        if (At(x, y)) { brain.Food = null; return; }
        brain.Food = new Vector2(x, y + body.size * 0.3f);
        brain.FoodSurface = y;
    }

    // after the loose ball (where it'll land), and picks it up when it's at its mouth
    float chaseBest = float.MaxValue, chaseStall;

    void Chase(float dt)
    {
        // getting nowhere (the ball's up on a window it can't get to): it rolls off there in a moment
        float dist = (ball.Position - body.Center).magnitude;
        if (dist < chaseBest - 0.3f) { chaseBest = dist; chaseStall = 0f; } else chaseStall += dt;
        if (chaseStall > 5f)
        {
            chaseStall = 0f;
            chaseBest = float.MaxValue;
            if (ball.Resting && ball.RollOff()) Say(0.5f, L("IT'S STUCK UP THERE", "GET DOWN, BALL!", "ROLL, BALL, ROLL", "COME DOWN!", "RAWR! COME DOWN"), false);
            else if (!PickupGame.Current && !helpFor)
            {
                // someone nearer the ball (maybe in the way): it gets it and passes it over; else it gives up
                BuddyHoops near = null;
                foreach (var o in s_all)
                    if (o != this && o.isActiveAndEnabled && !o.Busy && !o.helpFor && o.brain && o.brain.IsFree && (o.body.Center - ball.Position).magnitude < dist - 0.2f && (near == null || (o.body.Center - ball.Position).sqrMagnitude < (near.body.Center - ball.Position).sqrMagnitude)) near = o;
                if (near && (showOff != Plan.None || rebounding)) { near.helpFor = this; near.helpSince = Time.time; Say(0.6f, L("PASS IT!", "LITTLE HELP?", "BALL ME!", "OVER HERE!", "RAWR! PASS!"), false); }
                else { rebounding = false; showOff = Plan.None; brain.Food = null; return; }
            }
        }
        var hp = Hoop.Current;
        bool goaltend = ball.ShotLive && hp && ball.Position.y > hp.Rim.y - 0.35f && Mathf.Abs(ball.Position.x - hp.Rim.x) < 1.2f; // (no grabbing a shot on its way in)
        if (!goaltend && (ball.Position - body.Center).magnitude < body.size * 0.5f + ball.radius + 0.06f && !ball.Carrier && !ball.Held)
        {
            var g = PickupGame.Current;
            if (g && g.TeamOf(this) >= 0 && !g.CanTouch(this)) { brain.Food = null; return; }
            if (ball.PickUp(body))
            {
                chaseBest = float.MaxValue;
                chaseStall = 0f;
                if (g) g.Possession(this);
                brain.Food = null;
                return;
            }
        }
        Vector2 at = ball.Position;
        float sy = ball.SurfaceY;
        if (!ball.Resting && !BuddyRoute.Landing(ball.Position, ball.Velocity, ball.radius, ball.gravity, out at, out sy)) { brain.Food = null; return; }
        brain.Food = at;
        brain.FoodSurface = sy;
    }

    // ------------------------------------------------------------------ pickup game, without the ball

    void OffBall(Hoop hoop, PickupGame game, float now)
    {
        brain.Linger(1.5f);
        if (game.Paused || !hoop.Playable) { brain.Food = null; return; }
        var carrier = ball.Carrier;
        if (!carrier && !ball.Held)
        {
            if (game.CanTouch(this)) Chase(Time.deltaTime);
            else brain.Food = null; // (they just scored: the other team takes it out)
            return;
        }
        var ch = carrier ? carrier.GetComponent<BuddyHoops>() : null;
        if (!ch) { brain.Food = null; return; }
        if (game.TeamOf(ch) == game.TeamOf(this))
        {
            // get open: in front of the rim, away from whoever has it
            if (!FindSpot(hoop, out float sx, out float sy)) { brain.Food = null; return; }
            float away = Mathf.Abs(sx - ch.body.Center.x) < 1.2f ? Mathf.Clamp(sx + hoop.Side * 1.2f, sx - 2f, sx + 2f) : sx;
            MoveTo(away, sy);
            return;
        }
        // defend: between it and the rim, close
        float toRim = Mathf.Sign(hoop.Rim.x - ch.body.Center.x);
        float gx = ch.body.Center.x + toRim * (ch.body.size * 0.5f + body.size * 0.55f);
        float gy = ch.Feet;
        MoveTo(gx, gy);
        // a steal: right up against it while it dribbles
        if (!ball.Lift && Mathf.Abs(ch.body.Center.x - body.Center.x) < (ch.body.size + body.size) * 0.55f && Mathf.Abs(ch.Feet - Feet) < 0.25f && game.CanSteal)
        {
            float p = Mathf.Clamp(0.25f + 0.04f * (Level - ch.Level), 0.08f, 0.5f) * Time.deltaTime;
            if (Random.value < p)
            {
                ball.Take(body);
                Steals++;
                game.Possession(this);
                Say(0.9f, L("STEAL!", "MINE NOW!", "YOINK!", "MY BUN BALL", "RAWR! STOLEN"), true);
                ch.Say(0.5f, ch.L("HEY!", "NO FAIR!", "FOUL!", "MY BALL!", "HEY! RAWR"), false);
            }
        }
    }

    /// <summary>A shot's coming (PickupGame): jump to block it if it's close enough.</summary>
    public void Contest(BuddyHoops shooter)
    {
        if (Busy || !body.Grounded || body.Held || brain.Current == GummyBrain.State.Asleep) return;
        if (Mathf.Abs(shooter.body.Center.x - body.Center.x) > 1.3f || Mathf.Abs(shooter.Feet - Feet) > 0.4f) return;
        if (Random.value > Mathf.Lerp(0.45f, 0.85f, Mathf.Clamp01((Level - 1) / 12f))) return; // (reacts in time)
        body.Hop(new Vector2((shooter.body.Center.x - body.Center.x) * 1.5f - body.Velocity.x, GummyBody.JumpSpeed(0.5f * S, body.gravity, body.Drag) - body.Velocity.y), 0.3f);
    }

    /// <summary>The shot's just gone up: if it's up there right by the ball, it may get a hand on it.</summary>
    public bool TryBlock(BuddyHoops shooter, Basketball b)
    {
        if (body.Grounded || body.Held) return false;
        if ((b.Position - body.Center).magnitude > body.size * 0.6f + 0.35f) return false;
        float p = Mathf.Clamp(0.3f + 0.04f * (Level - shooter.Level), 0.1f, 0.55f);
        if (Random.value > p) return false;
        int away = b.Position.x > body.Center.x ? 1 : -1;
        b.Deflect(new Vector2(away * Random.Range(1.5f, 3f), Random.Range(1f, 2.5f)));
        Blocks++;
        Say(1f, L("BLOCKED!", "NOT TODAY!", "REJECTED!", "DENIED!", "RAWR! BLOCKED"), true);
        return true;
    }

    // ------------------------------------------------------------------ dunks

    /// <summary>Can it dunk on this hoop, and how (the most fun way it can). inGame: a tower dunk needs a teammate.</summary>
    public Dunk CanDunk(bool inGame, out BuddyHoops mate)
    {
        mate = null;
        var hoop = Hoop.Current;
        if (!hoop || !hoop.Playable) return Dunk.None;
        if (!DunkSpot(hoop, out _, out float gy)) return Dunk.None;
        float need = hoop.Rim.y + 0.12f - gy;
        float leap = skills ? skills.LeapHeight : 6f;
        var options = new List<Dunk>();
        if (pogo && pogo.Has && Mathf.Max(leap, 2f) + pogo.LeapBoost >= need) options.Add(Dunk.Pogo);
        if (jetpack && jetpack.Has && (skills ? skills.JetHeight : 0f) >= need) options.Add(Dunk.Jetpack);
        if (stomp && stomp.Has && Mathf.Max(leap, 2f) + 2.5f * S >= need) options.Add(Dunk.Stomp);
        if (options.Count == 0 && leap >= need) options.Add(Dunk.Plain);
        // off a teammate's head
        var g = PickupGame.Current;
        foreach (var o in s_all)
        {
            if (o == this || !o.isActiveAndEnabled || o.Busy || o.HasBall) continue;
            if (inGame ? (!g || g.TeamOf(o) != g.TeamOf(this)) : !o.brain.IsFree) continue;
            float jump = Mathf.Max(skills ? Mathf.Max(skills.HopUpHeight, leap) : 1f, 0.75f * S) + 0.5f;
            if (o.Height * 0.96f + jump < need) continue;
            if (mate == null || (o.body.Center - body.Center).sqrMagnitude < (mate.body.Center - body.Center).sqrMagnitude) mate = o;
        }
        if (mate) options.Add(Dunk.Tower);
        if (options.Count == 0) return Dunk.None;
        // gadgets first (they're the show), then off a friend, then a plain one
        foreach (var d in options) if (d == Dunk.Pogo || d == Dunk.Jetpack || d == Dunk.Stomp) return Random.value < 0.8f || options.Count == 1 ? d : options[options.Count - 1];
        return options[Random.Range(0, options.Count)];
    }

    // where it takes off from: just in front of the rim, on what's below it
    bool DunkSpot(Hoop hoop, out float x, out float y)
    {
        x = y = 0f;
        Vector2 rim = hoop.Rim;
        BuddyRoute.Build(Height, body.solidWindows);
        float want = rim.x + hoop.Side * (Hoop.RimHalf + HalfW + 0.25f);
        int si = BuddyRoute.SurfaceBelow(want, rim.y - 0.3f);
        if (si < 0) return false;
        var s = BuddyRoute.Surfaces[si];
        if (want - HalfW < s.x0 || want + HalfW > s.x1) return false;
        if (rim.y - s.y > 12f) return false;
        // room up there for it (and the way up)
        float top = rim.y + 0.2f + Height;
        if (BuddyRoute.Blocked(new Vector2(want, (s.y + 0.1f + top) * 0.5f), HalfW * 0.8f, (top - s.y - 0.1f) * 0.5f, body.solidWindows)) return false;
        x = want;
        y = s.y;
        return true;
    }

    // walking over to the take-off spot (with a teammate to boost: it goes under the rim first), then up
    bool DunkPlay(Hoop hoop, PickupGame game, float now, float held)
    {
        if (held > (dunk == Dunk.Tower ? 14f : 9f)) { CancelDunk(); return false; }
        if (dunk == Dunk.None)
        {
            dunk = CanDunk(game, out BuddyHoops mate);
            if (dunk == Dunk.None) return false;
            if (dunk == Dunk.Tower) { partner = mate; partner.boostFor = this; partner.boostSince = now; }
        }
        if (!DunkSpot(hoop, out float dx, out float dy)) { CancelDunk(); return false; }
        if (dunk == Dunk.Tower)
        {
            if (!partner || !partner.boostFor) { CancelDunk(); return false; }
            // the teammate goes to the spot, it waits beside it on the other side from where the teammate's coming
            // (nobody has to get past anybody), and once the teammate's set: up it goes
            bool set = partner.SetUnder(dx, dy);
            int wait = partner.body.Center.x > dx ? -1 : 1;
            float off = partner.HalfW + HalfW + 0.2f, wx = dx + wait * off;
            if (BuddyRoute.Blocked(new Vector2(wx, dy + 0.05f + Height * 0.5f), HalfW * 0.8f, Height * 0.5f, body.solidWindows)) wx = dx - wait * off;
            if (!At(wx, dy)) { MoveTo(wx, dy); return true; }
            brain.Food = null;
            if (!set || !Settled()) return true;
            StartDunk(hoop);
            return true;
        }
        if (!At(dx, dy)) { MoveTo(dx, dy); return true; }
        brain.Food = null;
        if (Settled()) StartDunk(hoop);
        return true;
    }

    void CancelDunk()
    {
        dunk = Dunk.None;
        if (partner) { partner.EndBoost(); partner = null; }
    }

    void StartDunk(Hoop hoop)
    {
        if (!brain.TowerStart()) { CancelDunk(); return; }
        act = Act.DunkWind;
        actT = 0f;
        dunkFail = false;
        cOff = Mathf.Max(body.Center.y - body.Extent.yMin, body.size * 0.3f);
        ball.Lift = true;
        if (dunk == Dunk.Jetpack && jetpack)
        {
            float r = Random.value;
            jetOutcome = r < jetpack.works ? 0 : r < jetpack.works + jetpack.sputters ? 1 : 2;
            jetpack.Ignite();
        }
        Say(0.5f, dunk == Dunk.Tower ? L("ALLEY-OOP!", "BOOST ME!", "LIFT ME UP!", "OOP!", "RAWR! OOP!") : L("WATCH THIS!", "SLAM TIME!", "STAND BACK!", "HOLD MY BUN", "RAWR! DUNK!"), false);
    }

    void DunkStep(Hoop hoop, float dt)
    {
        if (!brain.Towering) { act = Act.None; CancelDunk(); return; } // (knocked out of it)
        if (!HasBall && act != Act.DunkHang) { brain.TowerEnd(); act = Act.None; CancelDunk(); return; }
        Vector2 rim = hoop.Rim;
        switch (act)
        {
            case Act.DunkWind:
                body.Glide(Vector2.zero, 20f * S);
                body.Pose = new Vector2(1.1f, 0.9f);
                if (dunk == Dunk.Jetpack && jetpack) jetpack.Thrust(0.4f + 0.2f * Mathf.Abs(Mathf.Sin(actT * 40f)));
                if (actT < 0.3f) return;
                body.Pose = new Vector2(0.92f, 1.09f);
                actT = 0f;
                dunkFrom = body.Center;
                if (dunk == Dunk.Tower && partner) { act = Act.DunkClimb; dunkDur = 0.5f; return; }
                Launch(rim);
                return;
            case Act.DunkClimb:
            {
                // up onto the teammate's head
                if (!partner || !partner.boostFor) { brain.TowerEnd(); act = Act.None; CancelDunk(); return; }
                var pb = partner.body;
                Vector2 T = new Vector2(pb.Center.x, pb.Extent.yMax + cOff + 0.02f);
                float u = Smooth(actT / dunkDur);
                Vector2 C = new Vector2(Mathf.Lerp(dunkFrom.x, T.x, 0.35f), Mathf.Max(dunkFrom.y, T.y) + 0.5f * body.size);
                Fly(Bez(dunkFrom, C, T, u), 9f);
                if (actT >= dunkDur + 0.15f)
                {
                    partner.Say(0.8f, partner.L("BOOST!", "UP YOU GO!", "OOF! GO!", "HUP!", "RAWR! GO!"), true);
                    actT = 0f;
                    dunkFrom = body.Center;
                    Launch(rim);
                }
                return;
            }
            case Act.DunkFly:
            {
                float u = Smooth(actT / dunkDur);
                Vector2 T = new Vector2(rim.x + hoop.Side * 0.04f, rim.y + cOff + 0.14f);
                Vector2 C = new Vector2(Mathf.Lerp(dunkFrom.x, T.x, 0.2f), T.y + 0.45f * body.size);
                Fly(Bez(dunkFrom, C, T, u), 14f * Mathf.Sqrt(S));
                body.Pose = new Vector2(0.92f, 1.09f);
                if (dunk == Dunk.Jetpack && jetpack)
                {
                    jetpack.Thrust(1f);
                    if (jetOutcome == 1 && u > 0.5f) { JetFail(false); return; }
                    if (jetOutcome == 2 && u > 0.3f) { JetFail(true); return; }
                }
                if (actT < dunkDur) return;
                // SLAM
                ball.Slam(rim + new Vector2(0f, 0.16f), body);
                Dunks++;
                DunksBy[(int)dunk]++;
                if (PickupGame.Current) PickupGame.Current.Released(this, ball);
                if (jetpack) jetpack.Thrust(0f);
                if (partner) { partner.EndBoost(); partner = null; }
                act = Act.DunkHang;
                actT = 0f;
                return;
            }
            case Act.DunkHang:
                // hanging on the rim a moment, then down
                body.Glide(Vector2.zero, 30f * S);
                body.Pose = new Vector2(0.9f, 1.12f);
                if (actT < 0.35f) return;
                brain.NoDizzyUntil = Time.time + 2f; // (it lets go of the rim: a long way down, but its own)
                brain.TowerEnd();
                act = Act.None;
                dunk = Dunk.None;
                plan = Plan.None;
                showOff = Plan.None;
                return;
        }
    }

    void Launch(Vector2 rim)
    {
        act = Act.DunkFly;
        float hgt = Mathf.Max(rim.y - dunkFrom.y, 0.2f);
        dunkDur = Mathf.Clamp(0.35f + 0.09f * hgt, 0.45f, 0.95f);
        if (dunk == Dunk.Pogo && pogo) pogo.Spring();
        if (dunk == Dunk.Stomp && stomp) stomp.Quake();
        if (dunk == Dunk.Jetpack && jetpack) jetpack.Thrust(1f);
    }

    // the jetpack let it down: coughs out (it drops, the ball with it) or backfires (a tumble, the ball flies off)
    void JetFail(bool bang)
    {
        brain.TowerEnd();
        act = Act.None;
        CancelDunk();
        plan = Plan.None;
        showOff = Plan.None;
        if (bang)
        {
            int side = Random.value < 0.5f ? -1 : 1;
            jetpack.Backfire();
            jetpack.Say(0.9f, "KABOOM!", "BAD JETPACK!", "WHOAAA!");
            ball.Release(new Vector2(-side * 3f, 3f));
            body.Hop(new Vector2(side * 3.5f * Mathf.Sqrt(S) - body.Velocity.x, 2.5f - body.Velocity.y), 0.5f);
            body.Spin(-side * 8f);
        }
        else
        {
            jetpack.Sputter();
            jetpack.Say(0.9f, "STUPID JETPACK!", "NOT NOW!", "COUGH COUGH");
            ball.Release(new Vector2(0f, 0.5f));
        }
        jetpack.Thrust(0f);
    }

    // ------------------------------------------------------------------ boosting a teammate's dunk

    // goes to the spot under the rim and stands there (still) until the dunker's been up off its head
    void Boost(Hoop hoop)
    {
        if (!boostFor || !boostFor.isActiveAndEnabled || Time.time - boostSince > 10f || boostFor.partner != this) { EndBoost(); return; }
        brain.Linger(1.5f);
    }

    // true once it's standing still on the spot (a puppet there)
    bool SetUnder(float x, float y)
    {
        if (brain.Towering) { body.Pose = new Vector2(1.04f, 0.96f); return true; }
        if (!At(x, y)) { MoveTo(x, y); return false; }
        brain.Food = null;
        if (!Settled()) return false;
        return brain.TowerStart();
    }

    void EndBoost()
    {
        if (!boostFor) return;
        boostFor = null;
        if (brain && brain.Towering) brain.TowerEnd();
    }

    // ------------------------------------------------------------------ you

    void OnUserShot()
    {
        // a couple of buddies near the hoop race for the rebound
        var hoop = Hoop.Current;
        if (!hoop || !ball || PickupGame.Current || Busy || !brain || brain.Current == GummyBrain.State.Asleep || (eater && eater.Target)) return;
        if ((body.Center - hoop.Rim).magnitude > 14f) return;
        int nearer = 0;
        foreach (var o in s_all)
            if (o != this && o.isActiveAndEnabled && o.brain && o.brain.Current != GummyBrain.State.Asleep && (o.body.Center - hoop.Rim).sqrMagnitude < (body.Center - hoop.Rim).sqrMagnitude) nearer++;
        if (nearer >= 2) return;
        rebounding = true;
        reboundSince = Time.time;
    }

    /// <summary>What it says when it scores (PickupGame adds the score).</summary>
    public string ScoredLine(Basketball.Shot shot) => shot.dunk ? DunkLine() : shot.bank ? L("BANK!", "OFF THE GLASS!", "BANK SHOT!", "OFF THE BOARD", "BANK! RAWR") : shot.swish ? L("SWISH!", "NOTHING BUT NET!", "SWISH-PRESSO!", "BUCKETS!", "SWISH! RAWR") : L("BUCKETS!", "IT'S IN!", "SCORE!", "TWO!", "RAWR! IN!");

    void OnScored(GummyBody by, Basketball.Shot shot)
    {
        if (by == body)
        {
            if (!Playing) Say(1f, ScoredLine(shot), true); // (in a game PickupGame says it, with the score)
            if (brain) brain.Treat();
            return;
        }
        if (by == null && Commentator()) Say(0.55f, L("NICE SHOT!", "SHOW OFF!", "WHOA! NICE!", "NOT BAD, HUMAN", "RAWR! NICE!"), false);
    }

    string DunkLine()
    {
        switch (dunk)
        {
            case Dunk.Pogo: return "POGO SLAM!";
            case Dunk.Jetpack: return "JETPACK JAM!";
            case Dunk.Stomp: return "STOMP DUNK!";
            case Dunk.Tower: return "ALLEY-OOP!";
            default: return L("SLAM DUNK!", "BOOM! DUNK!", "DUNK-PRESSO!", "SLAMMA JAMMA", "DINO DUNK!");
        }
    }

    void OnMissed(GummyBody by, bool airball)
    {
        if (by == body) { if (Random.value < 0.6f) Say(1f, airball ? L("AIRBALL...", "AIRBALL?!", "OOPS", "AIR BUN", "RAWR... AIRBALL") : L("BRICK!", "CLANK!", "SO CLOSE", "OOF. RIM", "RAWR! RIM!"), false); return; }
        if (by == null && Commentator()) Say(0.4f, airball ? L("AIRBALL!", "AIRBALL! HA", "AIR-PRESSO", "MISSED. BADLY", "AIRBALL! RAWR") : L("BRICK!", "CLANK!", "SO CLOSE!", "ALMOST!", "RAWR! RIM!"), false);
    }

    void OnFire(GummyBody owner)
    {
        if (owner == body) { Say(1f, L("I'M ON FIRE!", "ON FIRE!!", "HOT HOT HOT!", "SIZZLING!", "FIRE DINO!"), true); return; }
        if (owner == null ? Commentator() : (Random.value < 0.35f && brain && brain.Current != GummyBrain.State.Asleep))
            Say(1f, owner == null ? L("YOU'RE ON FIRE!", "YOU'RE HEATING UP!", "ON FIIIRE!", "TOO HOT!", "RAWR! FIRE!") : L("HE'S ON FIRE!", "STOP HIM!", "SO HOT!", "GUARD HIM!", "TOO HOT! RAWR"), false);
    }

    // the one that comments on your shots: the nearest one to the hoop that's awake
    bool Commentator()
    {
        var hoop = Hoop.Current;
        if (!hoop || !brain || brain.Current == GummyBrain.State.Asleep) return false;
        foreach (var o in s_all)
            if (o != this && o.isActiveAndEnabled && o.brain && o.brain.Current != GummyBrain.State.Asleep && (o.body.Center - hoop.Rim).sqrMagnitude < (body.Center - hoop.Rim).sqrMagnitude) return false;
        return true;
    }

    // ------------------------------------------------------------------ bits

    void Fly(Vector2 at, float maxV)
    {
        Vector2 v = (at - body.Center) * 16f;
        if (v.magnitude > maxV) v = v.normalized * maxV;
        body.Glide(v, 80f * Mathf.Sqrt(S));
    }

    static float Smooth(float u) { u = Mathf.Clamp01(u); return u * u * (3f - 2f * u); }
    static Vector2 Bez(Vector2 a, Vector2 c, Vector2 b, float u) => (1f - u) * (1f - u) * a + 2f * u * (1f - u) * c + u * u * b;

    /// <summary>Say a line (not too often unless forced).</summary>
    public void Say(float chance, string line, bool force)
    {
        if (!speech || string.IsNullOrEmpty(line) || Random.value > chance) return;
        if (!force && Time.time - lastLine < 2.5f) return;
        if (speech.Say(line, 0f, force)) lastLine = Time.time;
    }

    /// <summary>A line in its own voice (apple / tomato, bean, burger, dino).</summary>
    public string L(string any, string tomato, string bean, string burger, string dino)
    {
        if (!speech) return any;
        switch (speech.voice)
        {
            case BuddySpeech.Voice.Tomato: return tomato;
            case BuddySpeech.Voice.Bean: return bean;
            case BuddySpeech.Voice.Burger: return burger;
            case BuddySpeech.Voice.Dino: return dino;
            default: return any;
        }
    }
}
