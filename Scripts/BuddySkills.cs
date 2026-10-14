using System;
using UnityEngine;

/// <summary>
/// What a buddy can do, by its level: every level it learns a new trick and gets a bit cleverer (quicker to react,
/// better aim, sees snacks from further away, jumps higher, plans further ahead). GummyBrain and BuddyEater ask it
/// what's allowed; the route to a snack is planned with these abilities (BuddyRoute), so a buddy only goes for snacks
/// it can actually get to and never keeps bumping into the same wall.
/// The ladder is data: change the levels in the Inspector, or add a new skill (e.g. riding a skateboard) at the end.
/// Each buddy has its own level-10 gadget (kit): only the apple has a rope (lasso, swings, rope climbs, stringing
/// ziplines; anyone can ride a zipline); the tomato a jetpack, the bean an espresso shot, the burger a pogo fork, the
/// dino a super stomp (Skill.Gadget).
/// </summary>
[RequireComponent(typeof(GummyBody))]
[DisallowMultipleComponent]
public class BuddySkills : MonoBehaviour
{
    public enum Skill
    {
        Hop,        // hops around, eats snacks on whatever it's standing on
        HopDown,    // hops off a window down to a snack below
        HopUp,      // hops up onto low steps
        Climb,      // climbs screen edges and window sides (for fun, and up to snacks it can't jump to)
        ThinkAhead, // plans two moves ahead (over a low window and down the other side)
        Leap,       // big leaps onto windows
        WallJump,   // jumps up beside a window and pulls itself over the top
        Route,      // plans whole routes, and lets a friend have a snack the friend will get to first
        Catch,      // runs under falling snacks and catches them, even in the air
        Ace,        // sharp aim, quick reactions, its longest leaps
        Lasso,      // lassoes your cursor and hangs from it (you carry it around; shake the mouse and it lets go)
        Swing,      // swings on its rope from window bottoms / the top of the screen, and climbs a rope up a window's corner
        Skate,      // rides a skateboard along the taskbar and window tops (fast), and ollies off window edges to far-away spots
        Glide,      // folds a paper plane and glides down from high windows to spots far away
        Grind,      // grinds window edges on its skateboard (sparks), hopping from rail to rail
        Zip,        // strings a zipline from a high window down to a lower one and slides down it (friends can use it too)
        Balloon,    // blows up a balloon and floats up onto a window top too high to jump to, then lets go above it
        Gadget,     // its own level-10 gadget (not the apple: it has the rope): jetpack / espresso shot / pogo fork / super stomp
    }

    /// <summary>Its level-10 gadget. Only the rope kit has the lasso, swings, rope climbs and strings ziplines.</summary>
    public enum Kit { Auto, Rope, Jetpack, Espresso, Pogo, Stomp, None }

    [Serializable]
    public class Step
    {
        public Skill skill;
        public int level;
        public string name;
    }

    [Tooltip("Which trick it learns at which level (its own level).")]
    public Step[] ladder =
    {
        new Step { skill = Skill.Hop, level = 1, name = "HOP" },
        new Step { skill = Skill.HopDown, level = 2, name = "HOP DOWN" },
        new Step { skill = Skill.HopUp, level = 3, name = "HOP UP" },
        new Step { skill = Skill.Climb, level = 4, name = "CLIMB" },
        new Step { skill = Skill.ThinkAhead, level = 5, name = "THINK AHEAD" },
        new Step { skill = Skill.Leap, level = 6, name = "LEAP" },
        new Step { skill = Skill.WallJump, level = 7, name = "WALL JUMP" },
        new Step { skill = Skill.Route, level = 8, name = "ROUTES" },
        new Step { skill = Skill.Catch, level = 9, name = "CATCH" },
        new Step { skill = Skill.Ace, level = 10, name = "ACE" },
        new Step { skill = Skill.Lasso, level = 10, name = "LASSO" },
        new Step { skill = Skill.Gadget, level = 10, name = "GADGET" },
        new Step { skill = Skill.Swing, level = 12, name = "SWING" },
        new Step { skill = Skill.Skate, level = 13, name = "SKATEBOARD" },
        new Step { skill = Skill.Glide, level = 14, name = "PAPER PLANE" },
        new Step { skill = Skill.Grind, level = 15, name = "GRIND" },
        new Step { skill = Skill.Zip, level = 16, name = "ZIPLINE" },
        new Step { skill = Skill.Balloon, level = 17, name = "BALLOON" },
    };

    [Tooltip("Pretend to be this level (0 = its real level). For trying the ladder out in Play mode.")]
    public int levelOverride;
    [Tooltip("Its level-10 gadget. Auto = by its voice: apple the rope (lasso, swings, ziplines), tomato a jetpack, bean an espresso shot, burger a pogo fork, dino a super stomp, the duck none (it has its mama).")]
    public Kit kit = Kit.Auto;
    [SerializeField, HideInInspector] int ladderVersion;

    /// <summary>Its gadget (Auto resolved).</summary>
    public Kit Gear
    {
        get
        {
            if (kit != Kit.Auto) return kit;
            var sp = speech ? speech : GetComponent<BuddySpeech>();
            var v = sp ? sp.voice : BuddySpeech.Voice.Apple;
            return v == BuddySpeech.Voice.Tomato ? Kit.Jetpack : v == BuddySpeech.Voice.Bean ? Kit.Espresso : v == BuddySpeech.Voice.Burger ? Kit.Pogo
                 : v == BuddySpeech.Voice.Dino ? Kit.Stomp : v == BuddySpeech.Voice.Duck ? Kit.None : Kit.Rope;
        }
    }
    /// <summary>Has the rope (the apple): lasso, swings, rope climbs, strings ziplines.</summary>
    public bool RopeKit => Gear == Kit.Rope;
    /// <summary>What its gadget is called ("JETPACK"...; "LASSO" for the rope).</summary>
    public string GadgetName => GadgetNameOf(Gear);
    public static string GadgetNameOf(Kit k) => k == Kit.Jetpack ? "JETPACK" : k == Kit.Espresso ? "ESPRESSO SHOT" : k == Kit.Pogo ? "POGO FORK" : k == Kit.Stomp ? "SUPER STOMP" : k == Kit.None ? "MAMA" : "LASSO";

    // does this trick come with its kit? (rope tricks: only the rope kit; the gadget: everyone else)
    bool Fits(Skill s) => s == Skill.Gadget ? !RopeKit && Gear != Kit.None : (s == Skill.Lasso || s == Skill.Swing || s == Skill.Zip) ? RopeKit : true;
    /// <summary>The name it says for this trick ("JETPACK" for its gadget).</summary>
    public string NameOf(Step st) => st.skill == Skill.Gadget ? GadgetName : st.name;

    /// <summary>Learned a new trick (it says so).</summary>
    public event Action<Step> Learned;

    GummyBody body;
    BuddyProgress progress;
    BuddySpeech speech;

    public int Level => levelOverride > 0 ? levelOverride : progress ? progress.Level : 10;
    float T => Mathf.Clamp01((Level - 1) / 9f); // 0 at level 1, 1 at level 10
    float S => body ? body.size / 0.6f : 1f;

    void Awake()
    {
        body = GetComponent<GummyBody>();
        progress = GetComponent<BuddyProgress>();
        speech = GetComponent<BuddySpeech>();
        AddNewTricks();
    }

    void OnValidate() => AddNewTricks();

    // a scene saved with an older (shorter) ladder: new tricks go on the end, at their usual levels (yours stay as they are);
    // version 2: the lasso comes at level 10 with the whip (it was 11)
    void AddNewTricks()
    {
        if (ladder == null) ladder = new Step[0];
        if (ladderVersion < 2)
        {
            foreach (var st in ladder) if (st != null && st.skill == Skill.Lasso && st.level == 11) st.level = 10;
            ladderVersion = 2;
        }
        foreach (Skill s in Enum.GetValues(typeof(Skill)))
        {
            bool found = false;
            foreach (var st in ladder) if (st != null && st.skill == s) { found = true; break; }
            if (found) continue;
            Array.Resize(ref ladder, ladder.Length + 1);
            ladder[ladder.Length - 1] = new Step { skill = s, level = DefaultLevel(s), name = DefaultName(s) };
        }
    }

    static int DefaultLevel(Skill s) => s == Skill.Lasso || s == Skill.Gadget ? 10 : (int)s + 1;
    static string DefaultName(Skill s) => s == Skill.Route ? "ROUTES" : s == Skill.ThinkAhead ? "THINK AHEAD" : s == Skill.HopDown ? "HOP DOWN" :
        s == Skill.HopUp ? "HOP UP" : s == Skill.WallJump ? "WALL JUMP" : s == Skill.Skate ? "SKATEBOARD" : s == Skill.Glide ? "PAPER PLANE" : s == Skill.Zip ? "ZIPLINE" : s.ToString().ToUpperInvariant();

    void OnEnable() { if (progress) progress.LeveledUp += OnLevelUp; }
    void OnDisable() { if (progress) progress.LeveledUp -= OnLevelUp; }

    public bool Has(Skill s)
    {
        if (!Fits(s)) return false;
        foreach (var st in ladder) if (st.skill == s) return Level >= st.level;
        return false;
    }

    /// <summary>Does it ever get this trick (its kit)?</summary>
    public bool Gets(Skill s) => Fits(s);

    /// <summary>The level it learns this skill at (int.MaxValue if never: not with its kit).</summary>
    public int LevelFor(Skill s)
    {
        if (!Fits(s)) return int.MaxValue;
        foreach (var st in ladder) if (st.skill == s) return st.level;
        return int.MaxValue;
    }

    /// <summary>The next trick it'll learn (null if it knows them all).</summary>
    public Step Next()
    {
        Step best = null;
        foreach (var st in ladder) if (Fits(st.skill) && st.level > Level && (best == null || st.level < best.level)) best = st;
        return best;
    }

    /// <summary>The trick it learned last (null at the start).</summary>
    public Step Latest()
    {
        Step best = null;
        foreach (var st in ladder) if (Fits(st.skill) && st.level <= Level && st.level > 1 && (best == null || st.level >= best.level)) best = st; // (same level: the later one, its gadget)
        return best;
    }

    public int Count { get { int n = 0; foreach (var st in ladder) if (Fits(st.skill) && st.level <= Level) n++; return n; } }

    // ------------------------------------------------------------------ how good it is at things (world units, seconds)

    /// <summary>Waits this long before going after a snack it just spotted.</summary>
    public float Reaction => Has(Skill.Ace) ? 0.12f : Mathf.Lerp(1.1f, 0.2f, T);
    /// <summary>Its hops land this far off (x size), randomly. Sharp from Route on.</summary>
    public float AimError => Has(Skill.Route) ? 0f : Mathf.Lerp(0.45f, 0.1f, T);
    /// <summary>Notices snacks this far away.</summary>
    public float NoticeRange => Mathf.Lerp(6f, 14f, Mathf.Clamp01((Level - 1) / 7f));
    /// <summary>Highest step it can hop up onto.</summary>
    public float HopUpHeight => Has(Skill.HopUp) ? Mathf.Lerp(0.45f, 1.2f, T) * S : 0f;
    /// <summary>Highest window it can leap onto, and how far it leaps sideways.</summary>
    public float LeapHeight => Has(Skill.Leap) ? Mathf.Lerp(2.6f, 6f, Mathf.Clamp01((Level - LevelFor(Skill.Leap)) / 4f)) : 0f;
    public float LeapReach => Has(Skill.Leap) ? Mathf.Lerp(2f, 3f, Mathf.Clamp01((Level - LevelFor(Skill.Leap)) / 4f)) * S : 1.4f * S;
    /// <summary>Highest window it can jump up beside and pull itself onto.</summary>
    public float WallJumpHeight => Has(Skill.WallJump) ? Mathf.Max(LeapHeight, 3f) + 0.5f : 0f;
    /// <summary>How many moves (hops up / down, leaps, climbs) it plans ahead.</summary>
    public int PlanMoves => Has(Skill.Route) ? 8 : Has(Skill.ThinkAhead) ? 2 : 1;
    /// <summary>Longest rope when it lassoes your cursor (from your cursor to its middle).</summary>
    public float LassoLength => Has(Skill.Lasso) ? 2.4f * S : 0f;
    /// <summary>Longest rope for swings and rope climbs (from the hook to its middle); grows for 4 levels after it learns to swing.</summary>
    public float RopeReach => Has(Skill.Swing) ? Mathf.Lerp(4.5f, 7f, Mathf.Clamp01((Level - LevelFor(Skill.Swing)) / 4f)) * S : 0f;
    /// <summary>Top skateboard speed (world units / s; 0 = no skateboard yet); gets faster for 3 levels after it learns.</summary>
    public float SkateSpeed => Has(Skill.Skate) ? Mathf.Lerp(5f, 7f, Mathf.Clamp01((Level - LevelFor(Skill.Skate)) / 3f)) * Mathf.Sqrt(S) : 0f;
    /// <summary>Does tricks on the board for fun: an ollie a level after it learns to skate, kickflips two levels after.</summary>
    public bool Ollies => Has(Skill.Skate) && Level >= LevelFor(Skill.Skate) + 1;
    public bool Kickflips => Has(Skill.Skate) && Level >= LevelFor(Skill.Skate) + 2;
    /// <summary>How far its paper plane glides for every unit it comes down (0 = no paper plane yet); better for 2 levels.</summary>
    public float GlideRatio => Has(Skill.Glide) ? Mathf.Lerp(2.6f, 3.6f, Mathf.Clamp01((Level - LevelFor(Skill.Glide)) / 2f)) : 0f;
    /// <summary>Longest zipline it can string (0 = not yet); longer for 2 levels after it learns.</summary>
    public float ZipLength => Has(Skill.Zip) ? Mathf.Lerp(8f, 12f, Mathf.Clamp01((Level - LevelFor(Skill.Zip)) / 2f)) * S : 0f;

    /// <summary>How far across it can drift on its balloon on the way up (0 = no balloon yet); further for 2 levels after it learns.</summary>
    public float BalloonDrift => Has(Skill.Balloon) ? Mathf.Lerp(5f, 8f, Mathf.Clamp01((Level - LevelFor(Skill.Balloon)) / 2f)) * S : 0f;

    /// <summary>How high its jetpack takes it (0 = no jetpack): higher than it leaps, more for 3 levels after it gets it. (It doesn't always work.)</summary>
    public float JetHeight => Gear == Kit.Jetpack && Has(Skill.Gadget) ? Mathf.Lerp(7.5f, 9.5f, Mathf.Clamp01((Level - LevelFor(Skill.Gadget)) / 3f)) : 0f; // (higher than it can leap)

    /// <summary>Everything it can do right now, for the route planner.</summary>
    public BuddyRoute.Abilities Abilities(float leapCap, float reachCap) => new BuddyRoute.Abilities
    {
        drop = Has(Skill.HopDown),
        hopUp = HopUpHeight,
        hopReach = 1.4f * S,
        leapUp = Mathf.Min(LeapHeight, leapCap),
        leapReach = Mathf.Min(LeapReach, reachCap),
        wallUp = WallJumpHeight,
        climb = Has(Skill.Climb),
        moves = PlanMoves,
        rope = RopeReach,
        skate = SkateSpeed,
        glide = GlideRatio,
        zip = ZipLength,
        zipRide = Has(Skill.HopDown), // (anyone but a brand new seed can slide down a line a friend strung)
        balloon = BalloonDrift,
        jet = JetHeight,
    };

    /// <summary>Everything any buddy can do (no BuddySkills on it, or "could I get there one day?").</summary>
    public static BuddyRoute.Abilities Max(float s, float leapCap, float reachCap) => new BuddyRoute.Abilities
    {
        drop = true, hopUp = 1.2f * s, hopReach = 1.4f * s, leapUp = leapCap, leapReach = reachCap * s, wallUp = Mathf.Max(leapCap, 3f) + 0.5f,
        climb = true, moves = 8, rope = 7f * s, skate = 7f * Mathf.Sqrt(s), glide = 3.6f, zip = 12f * s, zipRide = true, balloon = 8f * s, jet = 6f * s,
    };

    // ------------------------------------------------------------------ learning

    void OnLevelUp(int level, bool newStage)
    {
        int n = 0;
        foreach (var st in ladder)
        {
            if (st.level != level || st.level <= 1 || !Fits(st.skill)) continue;
            Learned?.Invoke(st);
            if (speech) StartCoroutine(SayLater("NEW TRICK: " + NameOf(st) + "!", 1.6f + 2.6f * n++)); // after its level-up line (two at one level: one after the other)
        }
    }

    System.Collections.IEnumerator SayLater(string line, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (speech && speech.isActiveAndEnabled) speech.Say(line, 2.4f, true);
    }

}
