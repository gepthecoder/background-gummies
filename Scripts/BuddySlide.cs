using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The slide (Slide) for one buddy: right after it goes up the buddies nearby rush over ("A SLIDE!"), and when they're
/// bored they go again now and then, a few rides in a row. It walks out along the window top to the deck, or to the foot
/// of the ladder and climbs it rung by rung, sits down at the top (behind whoever's there: they go together as a train)
/// and the slide takes it down; it says what trick it's doing, whoops off the lip, and goes round again. Snacks still win.
/// </summary>
[DefaultExecutionOrder(5)] // after BuddyEater: the slide only when there's no snack
[RequireComponent(typeof(GummyBody))]
[DisallowMultipleComponent]
public class BuddySlide : MonoBehaviour
{
    [Tooltip("Bored with the slide up: chance per minute it goes for a ride.")]
    [Range(0f, 1f)] public float slideChance = 0.5f;

    static readonly List<BuddySlide> s_all = new List<BuddySlide>();

    public int Level => skills ? skills.Level : 10;
    /// <summary>On its way to the slide, climbing, on it, or just off it.</summary>
    public bool Busy => act != Act.None;
    /// <summary>Rides so far, launches off the lip, wipeouts (tests).</summary>
    public int RidesDone { get; private set; }
    public int Launches { get; private set; }
    public int Wipeouts { get; private set; }
    /// <summary>Climbing the ladder right now.</summary>
    public bool Climbing => act == Act.Climb;

    enum Act { None, Going, Climb, On, After }

    GummyBody body;
    GummyBrain brain;
    BuddyEater eater;
    BuddySkills skills;
    BuddySpeech speech;
    Act act;
    bool ladder, fresh;
    Vector2 spot;
    float spotY, actT, cOff, climbY, nextRoll, lastLine = -100f;
    int ridesLeft;

    float S => body.size / 0.6f;

    void Awake()
    {
        body = GetComponent<GummyBody>();
        brain = GetComponent<GummyBrain>();
        eater = GetComponent<BuddyEater>();
        skills = GetComponent<BuddySkills>();
        speech = GetComponent<BuddySpeech>();
        nextRoll = Time.time + 5f;
    }

    void OnEnable() { if (!s_all.Contains(this)) s_all.Add(this); }
    void OnDisable() { s_all.Remove(this); Cancel(); }

    /// <summary>Someone's on the way up (climbing, or nearly at the slide): wait for it, it'll make a train.</summary>
    public static bool Coming(Slide sl)
    {
        foreach (var b in s_all)
            if (b && (b.act == Act.Climb || (b.act == Act.Going && (b.body.Center - b.spot).magnitude < 2.5f)) && !sl.Has(b)) return true;
        return false;
    }

    /// <summary>Go down the slide now (chat "go slide"). False if there's no slide or it can't get there.</summary>
    public bool SlideNow()
    {
        var sl = Slide.Current;
        if (!sl || !brain || act != Act.None || body.Held || brain.InPit || brain.Towering) return false;
        if (brain.Current == GummyBrain.State.Asleep) brain.WakeUp();
        if (!Plan(sl)) return false;
        ridesLeft = 1;
        return true;
    }

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("BuddySlide.Update", t0); }
    void UpdateTimed()
    {
        var sl = Slide.Current;
        if (!sl || !brain) { if (act != Act.None) Cancel(); fresh = false; return; }
        float now = Time.time, dt = Mathf.Min(Time.deltaTime, 0.05f);
        if (act != Act.None) { Step(sl, now, dt); return; }
        if (eater && eater.Target) return; // snacks first
        // it's just gone up: most of them come and try it
        if (sl.Fresh && !fresh)
        {
            fresh = true;
            if (brain.IsFree && !BusyElsewhere() && (body.Center - sl.Corner).magnitude < 14f && Random.value < 0.75f && Plan(sl))
            {
                ridesLeft = Random.Range(1, 4);
                Say(1f, L("A SLIDE!", "A SLIDE!!", "SLIDE TIME!", "WHEEE TIME!", "RAWR! SLIDE!"), false);
                return;
            }
        }
        // bored: now and then
        if (now >= nextRoll && brain.IsFree)
        {
            nextRoll = now + 1f;
            if (!BusyElsewhere() && Random.value < slideChance / 60f && Plan(sl)) ridesLeft = Random.Range(1, 3);
        }
    }

    bool BusyElsewhere()
    {
        var h = GetComponent<BuddyHoops>();
        var p = GetComponent<BuddyPit>();
        return (h && (h.Busy || h.Playing)) || (p && p.Busy) || BuddyTower.Has(body) || LevelUpParty.Current || BuddyHideSeek.Playing || brain.InPit;
    }

    // the way up: along the window top to the deck, or to the foot of the ladder (whichever's quicker; the deck if it's even)
    bool Plan(Slide sl)
    {
        float hw = Mathf.Max(body.Extent.width, body.size * 0.8f) * 0.5f, best = float.MaxValue;
        bool found = false;
        if (sl.Window != 0)
        {
            Vector2 at = new Vector2(sl.Corner.x - sl.Side * (hw * 1.5f + 0.05f), sl.Corner.y);
            if (brain.PlanTo(at + new Vector2(0f, 0.3f), at.y, out float c)) { best = c - 0.4f; spot = at; spotY = at.y; ladder = false; found = true; }
        }
        if (sl.HasLadder)
        {
            Vector2 at = new Vector2(sl.LadderX, sl.LadderFoot);
            if (brain.PlanTo(at + new Vector2(0f, 0.3f), at.y, out float c) && c < best) { best = c; spot = at; spotY = at.y; ladder = true; found = true; }
        }
        if (!found) return false;
        act = Act.Going;
        actT = 0f;
        return true;
    }

    void Step(Slide sl, float now, float dt)
    {
        actT += dt;
        if (body.Held) { Cancel(); return; }
        switch (act)
        {
            case Act.Going:
                if ((eater && eater.Target) || brain.InPit || brain.Towering || actT > 25f) { Cancel(); return; }
                if (At())
                {
                    brain.Food = null;
                    if (!Settled()) return;
                    cOff = Mathf.Max(body.Center.y - body.Extent.yMin, body.size * 0.3f);
                    if (!brain.SlideStart()) { Cancel(); return; }
                    if (ladder) { act = Act.Climb; actT = 0f; climbY = body.Extent.yMin; return; }
                    sl.Sit(this, body, brain, cOff);
                    act = Act.On;
                    return;
                }
                brain.Food = new Vector2(spot.x, spotY + body.size * 0.3f);
                brain.FoodSurface = spotY;
                return;
            case Act.Climb:
            {
                // up the ladder, a rung at a time (a little bob with each one)
                if (!brain.Sliding) { Cancel(); return; }
                float speed = 1.3f * Mathf.Sqrt(S);
                climbY = Mathf.Min(climbY + speed * dt, sl.LadderTop + 0.04f);
                Vector2 at = new Vector2(sl.LadderX, climbY + cOff);
                Vector2 v = (at - body.Center) * 12f;
                body.Glide(Vector2.ClampMagnitude(v, 4f), 60f * Mathf.Sqrt(S));
                float w = 0.05f * Mathf.Sin(actT * 14f);
                body.Pose = new Vector2(1f - w, 1f + w);
                body.TurnTo = float.NaN;
                if (climbY >= sl.LadderTop + 0.04f || (sl.Window == 0 && sl.Waiting > 0 && climbY >= sl.LadderTop - 0.55f * sl.Waiting))
                {
                    sl.Sit(this, body, brain, cOff);
                    act = Act.On;
                }
                if (actT > 15f) Cancel();
                return;
            }
            case Act.On:
                if (!brain.Sliding || !sl.Has(this)) { act = Act.After; actT = 0f; }
                return;
            case Act.After:
                // off it: once it's landed and settled, round again (if it wants)
                if (brain.Sliding) return;
                if (actT < 1f || !body.Grounded || body.Velocity.sqrMagnitude > 0.6f) { if (actT > 8f) act = Act.None; return; }
                act = Act.None;
                if (ridesLeft > 0 && !(eater && eater.Target) && !brain.InPit && Plan(sl)) return;
                ridesLeft = 0;
                return;
        }
    }

    void Cancel()
    {
        if (act == Act.Going && brain) brain.Food = null;
        var sl = Slide.Current;
        if (sl && sl.Has(this)) sl.Remove(this);
        if (brain && brain.Sliding) brain.TowerEnd();
        act = Act.None;
    }

    bool At() => Mathf.Abs(body.Center.x - spot.x) < Mathf.Max(0.3f * body.size, 0.15f) && Mathf.Abs(body.Extent.yMin - spotY) < 0.25f && body.Grounded;
    bool Settled() => body.Grounded && !body.Held && body.Velocity.sqrMagnitude < 0.6f;

    // ------------------------------------------------------------------ the slide tells it

    /// <summary>Off it goes (Slide): n = how many in its train (it's at the front), or -k = k-th behind the front one.</summary>
    public void Going(int n, Slide.Trick trick)
    {
        if (n < 0) { if (n == -1) Say(0.7f, L("ALL ABOARD!", "WAIT FOR ME!", "BEAN TRAIN!", "BURGER TRAIN!", "RAWR! TRAIN!"), false); return; }
        if (n > 1) { Say(1f, L("CHOO CHOO!", "CHOO CHOO!!", "SLIDE TRAIN!", "ALL ABOARD!", "RAWR-CHOO!"), true); return; }
        switch (trick)
        {
            case Slide.Trick.Belly: Say(0.9f, L("SUPERMAN!", "HEAD FIRST!", "BEAN TORPEDO!", "BURGER BOBSLED!", "DINO DIVE!"), true); break;
            case Slide.Trick.Back: Say(0.8f, L("LOOK MA!", "FEET FIRST!", "RELAX MODE", "LAZY RIDE", "RAWR. CHILL"), true); break;
            case Slide.Trick.Spin: Say(0.9f, L("SPINNY!", "WHIRLYGIG!", "ESPRESSO SPIN!", "SPINNING BUN!", "DINO TWISTER!"), true); break;
            case Slide.Trick.Surf: Say(0.9f, L("SURF'S UP!", "NO HANDS!", "BEAN SURFING!", "HANG TEN!", "RAWR! SURF!"), true); break;
            default: Say(0.8f, L("WHEEE!", "WHEEEEE!", "WHEE-PRESSO!", "WHEEE!", "RAWR-WHEEE!"), true); break;
        }
    }

    /// <summary>It's off the slide (launched off the lip, wiped out, got off at the end, or it was taken off).</summary>
    public void Released(bool launched, bool wipeout)
    {
        if (act != Act.On && act != Act.Climb) return;
        RidesDone++;
        ridesLeft--;
        act = Act.After;
        actT = 0f;
        if (launched) { Launches++; Say(0.6f, L("YAHOO!", "WHEEEE!", "AIRBORNE!", "FLYING BUN!", "RAWR! AIR!"), false); }
        if (wipeout) { Wipeouts++; Say(1f, L("WHOA!", "WHOAAA!", "BEAN DOWN!", "WIPEOUT!", "RAWR?!"), true); }
    }

    /// <summary>It got to the end slowly and the ones behind bumped into it.</summary>
    public void Bumped() => Say(0.6f, L("AGAIN!", "AGAIN AGAIN!", "ONE MORE!", "AGAIN!", "RAWR! AGAIN!"), false);

    void Say(float chance, string line, bool force)
    {
        if (!speech || string.IsNullOrEmpty(line) || Random.value > chance) return;
        if (!force && Time.time - lastLine < 2.5f) return;
        if (speech.Say(line, 0f, force)) lastLine = Time.time;
    }

    string L(string apple, string tomato, string bean, string burger, string dino) => CandyCloud.Line(speech, apple, tomato, bean, burger, dino);
}
