using UnityEngine;

/// <summary>
/// The ball pit (BallPit) for one buddy: it whoops when it lands in the balls and giggles in there (GummyBrain.Pit does
/// the swimming), and now and then (bored, the pit's out, right after you put it out) it climbs onto a window top beside
/// the pit, says so and cannonballs in: tucked up, a flip from level 5, a double from level 10. Nobody near a window? A
/// run-up from the floor and over the rail. The others score the splash. Snacks still come first.
/// </summary>
[DefaultExecutionOrder(5)] // after BuddyEater: the pit only when there's no snack
[RequireComponent(typeof(GummyBody))]
[DisallowMultipleComponent]
public class BuddyPit : MonoBehaviour
{
    [Tooltip("Bored with the pit out: chance per minute it goes for a cannonball.")]
    [Range(0f, 1f)] public float cannonballChance = 0.4f;

    /// <summary>Cannonballs so far (tests).</summary>
    public int Cannonballs { get; private set; }
    /// <summary>On its way to a cannonball, winding up, or in the air.</summary>
    public bool Busy => act != Act.None;
    /// <summary>Where it jumps from (tests).</summary>
    public Vector2 Spot => spot;

    enum Act { None, Going, Wind, Flying }

    GummyBody body;
    GummyBrain brain;
    BuddyEater eater;
    BuddySkills skills;
    BuddySpeech speech;
    Act act;
    Vector2 spot;
    float spotY, actT, nextRoll, flyT, lastLine = -100f;
    int side;
    bool fresh;
    static float s_lastCheer = -100f;

    float S => body.size / 0.6f;
    int Level => skills ? skills.Level : 10;

    void Awake()
    {
        body = GetComponent<GummyBody>();
        brain = GetComponent<GummyBrain>();
        eater = GetComponent<BuddyEater>();
        skills = GetComponent<BuddySkills>();
        speech = GetComponent<BuddySpeech>();
        nextRoll = Time.time + 5f;
    }

    void OnEnable()
    {
        if (brain) brain.Moment += OnMoment;
        BallPit.Splashed += OnSplash;
    }

    void OnDisable()
    {
        if (brain) brain.Moment -= OnMoment;
        BallPit.Splashed -= OnSplash;
        Cancel();
    }

    /// <summary>Go and cannonball into the pit now (chat "cannonball"). False if there's no pit or it can't right now.</summary>
    public bool CannonballNow()
    {
        var pit = BallPit.Current;
        if (!pit || !brain || act != Act.None || body.Held || brain.InPit) return false;
        if (brain.Current == GummyBrain.State.Asleep) brain.WakeUp();
        return Plan(pit);
    }

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("BuddyPit.Update", t0); }
    void UpdateTimed()
    {
        var pit = BallPit.Current;
        if (!pit || !brain) { if (act != Act.None) Cancel(); fresh = false; return; }
        float now = Time.time;
        if (act != Act.None) { Step(pit, now); return; }
        if (eater && eater.Target) return; // snacks first
        // right after it's put out: a go (about every other buddy); bored: now and then
        if (pit.Fresh && !fresh) { fresh = true; if (brain.IsFree && Random.value < 0.5f && Plan(pit)) return; }
        if (now >= nextRoll && brain.IsFree)
        {
            nextRoll = now + 1f; // (a roll every second it's sitting around)
            if (!BusyElsewhere() && Random.value < cannonballChance / 60f) Plan(pit);
        }
    }

    bool BusyElsewhere()
    {
        var h = GetComponent<BuddyHoops>();
        return (h && (h.Busy || h.Playing)) || BuddyTower.Has(body) || LevelUpParty.Current || BuddyHideSeek.Playing || brain.Towering;
    }

    // ------------------------------------------------------------------ the cannonball

    // a window top beside the pit, a bit higher than its rail, with the pit in jumping distance past its end; else the floor beside it
    bool Plan(BallPit pit)
    {
        float hh = Mathf.Max(body.Extent.height, body.size * 0.7f), hw = Mathf.Max(body.Extent.width, body.size * 0.8f) * 0.5f;
        BuddyRoute.Build(hh, body.solidWindows);
        var surf = BuddyRoute.Surfaces;
        float best = float.MaxValue;
        bool found = false;
        for (int i = 0; i < surf.Count; i++)
        {
            var s = surf[i];
            if (s.win == 0 || DesktopPlatforms.IsIcon(s.win) || s.y < pit.WallTop + 0.6f || s.y > pit.FloorY + 6f || s.x1 - s.x0 < hw * 3f) continue;
            for (int sd = -1; sd <= 1; sd += 2)
            {
                float edge = sd > 0 ? s.x1 : s.x0;
                if (sd > 0 ? pit.X1 < edge + 0.3f : pit.X0 > edge - 0.3f) continue; // the pit's not out that way
                float land = Mathf.Clamp(edge + sd * 1.3f, pit.X0 + 0.45f, pit.X1 - 0.45f);
                float take = edge - sd * hw * 1.4f;
                if (Mathf.Abs(land - take) > 3.6f || take < s.x0 + hw * 0.5f || take > s.x1 - hw * 0.5f) continue;
                if (!brain.PlanTo(new Vector2(take, s.y + 0.3f), s.y, out float cost)) continue;
                cost += 0.3f * (s.y - pit.WallTop);
                if (cost < best) { best = cost; spot = new Vector2(take, s.y); spotY = s.y; side = sd; found = true; }
            }
        }
        if (!found && pit.Window == 0)
        {
            // a run-up from the floor
            for (int sd = -1; sd <= 1; sd += 2)
            {
                float take = sd > 0 ? pit.X0 - BallPit.WallT - 1f : pit.X1 + BallPit.WallT + 1f;
                Rect r = ScreenBounds.WorldRect;
                if (take < r.xMin + hw || take > r.xMax - hw) continue;
                if (!brain.PlanTo(new Vector2(take, pit.FloorY + 0.3f), pit.FloorY, out float cost)) continue;
                if (cost < best) { best = cost; spot = new Vector2(take, pit.FloorY); spotY = pit.FloorY; side = sd; found = true; }
            }
        }
        if (!found) return false;
        act = Act.Going;
        actT = 0f;
        return true;
    }

    void Step(BallPit pit, float now)
    {
        actT += Time.deltaTime;
        if (body.Held || (act != Act.Flying && eater && eater.Target) || brain.Towering) { Cancel(); return; }
        switch (act)
        {
            case Act.Going:
                if (brain.InPit || actT > 25f) { Cancel(); return; }
                if (At())
                {
                    brain.Food = null;
                    if (!Settled()) return;
                    act = Act.Wind;
                    actT = 0f;
                    brain.Linger(2f);
                    Say(1f, L("CANNONBALL!", "CANNONBALL!!", "BEAN BOMB!", "BURGER BOMB!", "DINO-BALL!"), true);
                    return;
                }
                brain.Food = new Vector2(spot.x, spotY + body.size * 0.3f);
                brain.FoodSurface = spotY;
                return;
            case Act.Wind:
                brain.Linger(1f);
                brain.TuckUntil = now + 0.1f;
                if (actT < 0.35f) return;
                Leap(pit, now);
                return;
            case Act.Flying:
                if (brain.InPit || actT > 3f) act = Act.None;
                return;
        }
    }

    // a high arc into the middle of the pit (well, where it's aiming), tucked up, flipping at higher levels
    void Leap(BallPit pit, float now)
    {
        Vector2 c = body.Center;
        float g = body.gravity, drag = body.Drag;
        float tx = Mathf.Clamp(c.x + side * 1.4f, pit.X0 + 0.45f, pit.X1 - 0.45f), ty = pit.Fill;
        float apex = Mathf.Max(c.y, pit.WallTop + body.size * 0.6f) + 0.55f * S;
        float vy = GummyBody.JumpSpeed(apex - c.y, g, drag);
        float tUp = vy / g, tDown = Mathf.Sqrt(2f * Mathf.Max(apex - ty, 0.05f) / g), T = tUp + tDown;
        float vx = (tx - c.x) / T * (1f + drag * T * 0.5f);
        body.Hop(new Vector2(vx - body.Velocity.x, vy - body.Velocity.y), 0.6f);
        int flips = Level >= 10 ? 2 : Level >= 5 ? 1 : 0;
        if (flips > 0) body.Spin(-side * flips * 2f * Mathf.PI / (T * 0.9f), 0.6f);
        brain.TuckUntil = now + T;
        brain.NoDizzyUntil = now + T + 1.5f; // (its own landing)
        Cannonballs++;
        act = Act.Flying;
        actT = 0f;
        flyT = T;
    }

    void Cancel()
    {
        if (act == Act.Going && brain) brain.Food = null;
        act = Act.None;
    }

    bool At() => Mathf.Abs(body.Center.x - spot.x) < Mathf.Max(0.3f * body.size, 0.15f) && Mathf.Abs(body.Extent.yMin - spotY) < 0.25f && body.Grounded;
    bool Settled() => body.Grounded && !body.Held && body.Velocity.sqrMagnitude < 0.6f;

    // ------------------------------------------------------------------ what it says

    void OnMoment(string what)
    {
        if (what == "pit-in") { if (act == Act.None) Say(0.6f, L("BALLS!", "BALL PIT!", "SO MANY BALLS!", "BALLS! BALLS!", "RAWR! BALLS!"), false); }
        else if (what == "pit-giggle") Say(0.35f, L("WHEEE!", "HEHEHE", "I'M SWIMMING!", "BEST DAY EVER", "RAWR! FUN!"), false);
    }

    // somebody else made a big splash: the nearest one awake gives it a score
    void OnSplash(GummyBody who, float speed)
    {
        if (who == body || speed < 6f || !brain || brain.Current == GummyBrain.State.Asleep || Time.time - s_lastCheer < 3f) return;
        if ((who.Center - body.Center).magnitude > 7f || Random.value > 0.6f) return;
        s_lastCheer = Time.time;
        Say(1f, L("SPLASH!", "10 OUT OF 10!", "MY TURN!", "NICE ONE!", "RAWR! SPLASH!"), true);
    }

    void Say(float chance, string line, bool force)
    {
        if (!speech || string.IsNullOrEmpty(line) || Random.value > chance) return;
        if (!force && Time.time - lastLine < 2.5f) return;
        if (speech.Say(line, 0f, force)) lastLine = Time.time;
    }

    string L(string apple, string tomato, string bean, string burger, string dino) => CandyCloud.Line(speech, apple, tomato, bean, burger, dino);
}
