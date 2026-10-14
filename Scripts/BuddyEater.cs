using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The buddy eats snacks: picks the one it can get to quickest (a planned route with what it knows at its level,
/// BuddySkills) and sends GummyBrain after it, and eats any snack that touches its mouth (also when you drop a snack
/// on it, or hold it over one). Snacks it can't get to it leaves alone ("TOO HIGH... FOR NOW"); from the Route skill on
/// it lets a friend have a snack the friend will get to first, and with Catch it goes for snacks still falling.
/// Eating = crunch sound, chewing, happy face and a heart, and XP (BuddyProgress.xpSnack).
/// </summary>
[RequireComponent(typeof(GummyBody))]
[DisallowMultipleComponent]
public class BuddyEater : MonoBehaviour
{
    [Tooltip("Goes after snacks this close (world units).")]
    public float noticeDistance = 14f;
    [Tooltip("A snack landing this close wakes it up (world units).")]
    public float wakeDistance = 5f;
    [Tooltip("Gives up on a snack it can't get to after this long, and tries again later.")]
    public float giveUpAfter = 12f;

    [Header("Sound (WhipIt > Setup assigns it)")]
    public AudioClip crunchClip;
    [Range(0f, 1f)] public float volume = 0.6f;

    public event Action<Snack> Ate;
    /// <summary>Any buddy ate a snack (who, what): for snack rivalry between buddies.</summary>
    public static event Action<BuddyEater, Snack> AnyAte;
    public Snack Target => target;
    /// <summary>How far / hard its way to the target is (for letting a friend have it).</summary>
    public float TargetCost => target ? targetCost : float.MaxValue;

    static readonly List<BuddyEater> s_all = new List<BuddyEater>();
    GummyBody body;
    GummyBrain brain;
    BuddyProgress progress;
    BuddySkills skills;
    BuddyLaser laser;
    BuddySpeech speech;
    AudioSource audioSrc;
    Snack target;
    float targetSince, targetCost = float.MaxValue, targetY, nextPick, lastTold = -100f;
    Vector2 targetAt;
    readonly HashSet<Snack> told = new HashSet<Snack>();
    readonly Dictionary<Snack, float> ignoreUntil = new Dictionary<Snack, float>();
    readonly List<Snack> touching = new List<Snack>();

    void Awake()
    {
        body = GetComponent<GummyBody>();
        brain = GetComponent<GummyBrain>();
        progress = GetComponent<BuddyProgress>();
        skills = GetComponent<BuddySkills>();
        laser = GetComponent<BuddyLaser>();
        speech = GetComponent<BuddySpeech>();
        audioSrc = gameObject.AddComponent<AudioSource>();
        audioSrc.playOnAwake = false;
        audioSrc.spatialBlend = 0f;
    }

    void OnEnable()
    {
        Snack.Landed += OnLanded;
        if (!s_all.Contains(this)) s_all.Add(this);
    }

    void OnDisable()
    {
        Snack.Landed -= OnLanded;
        s_all.Remove(this);
        if (brain) brain.Food = null;
    }

    void OnLanded(Snack s)
    {
        if (brain && (s.Position - body.Center).magnitude < wakeDistance) brain.WakeUp(); // smells it
    }

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("BuddyEater.Update", t0); }
    void UpdateTimed()
    {
        float now = Time.time;
        if (BulletTime.Stopped) return; // (time stopped: a snack you put in its mouth waits for time to go on)

        // anything touching the mouth gets eaten
        touching.Clear();
        float reach = body.size * 0.5f;
        foreach (var s in Snack.All)
            if (!s.Held && (s.Position - body.Center).magnitude < reach + s.radius && GiantPotion.CanEat(body, s)) touching.Add(s);
        foreach (var s in touching) Eat(s);

        bool asleep = brain && brain.Current == GummyBrain.State.Asleep;
        bool dot = laser && laser.Playing; // (chasing the laser pointer's dot: that's where it's going, not this snack's fault)
        if (brain && brain.Stuck && !dot)
        {
            // it got nowhere: leave that one for a while
            if (target) ignoreUntil[target] = now + 45f;
            target = null;
            brain.Stuck = false;
        }
        if (target && (!target.isActiveAndEnabled || target.Eaten || target.Held)) target = null;
        // what to go after: the snack it can get to quickest (picked again a few times a second, while it's on its feet)
        if (!brain) PickNearest(now, asleep);
        else if (now >= nextPick && CanPlan()) { nextPick = now + 0.25f; Pick(now, asleep); }
        if (target && !target.Resting)
        {
            // still falling (Catch): go where it'll land
            if (BuddyRoute.Landing(target.Position, target.Velocity, target.radius, target.gravity, out Vector2 at, out float y)) { targetAt = at; targetY = y; }
        }
        else if (target) { targetAt = target.Position; targetY = target.SurfaceY; }
        // only the time it's actually trying counts (not while it's flying, stuck, dizzy on the floor...)
        if (target && brain && !dot && !brain.Jetting && !brain.Towering && (brain.Current == GummyBrain.State.Idle || brain.Current == GummyBrain.State.Hopping)) targetSince += Time.deltaTime;
        if (target && targetSince > giveUpAfter + Mathf.Min(targetCost, 20f)) // (a long way gets longer)
        {
            ignoreUntil[target] = now + 30f; // can't get to it (too high?), later
            target = null;
        }
        if (asleep && target) brain.WakeUp();
        if (ignoreUntil.Count > 32) { var dead = new List<Snack>(); foreach (var kv in ignoreUntil) if (!kv.Key || now > kv.Value) dead.Add(kv.Key); foreach (var d in dead) ignoreUntil.Remove(d); }
        if (brain)
        {
            brain.Food = target ? targetAt : (Vector2?)null;
            if (target) brain.FoodSurface = targetY;
        }
    }

    bool CanPlan() => body.Grounded && !body.Held && !body.IsStuck && brain.Current != GummyBrain.State.Climb;

    void Pick(float now, bool asleep)
    {
        Snack best = null, cantReach = null;
        float bestCost = float.MaxValue, nearest = float.MaxValue;
        float notice = skills ? Mathf.Min(noticeDistance, skills.NoticeRange) : noticeDistance;
        bool share = skills && skills.Has(BuddySkills.Skill.Route);
        bool catcher = skills && skills.Has(BuddySkills.Skill.Catch);
        float within = Nearest(now, asleep, notice, catcher); // (a candy shower: only the nearest few are worth planning a way to)
        foreach (var s in Snack.All)
        {
            if (s.Held || s.Eaten || !GiantPotion.CanEat(body, s)) continue; // (a giant potion: not for a monster, one giant at a time)
            bool falling = !s.Resting;
            if (falling && !catcher) continue;
            if (ignoreUntil.TryGetValue(s, out float until) && now < until) continue;
            float d = (s.Position - body.Center).magnitude;
            if (asleep && d > wakeDistance) continue; // sleeps through snacks far away
            if (d > notice || d > within) continue;
            Vector2 at = s.Position;
            float y = s.SurfaceY;
            if (falling && !BuddyRoute.Landing(s.Position, s.Velocity, s.radius, s.gravity, out at, out y)) continue;
            if (!brain.PlanTo(at, y, out float cost))
            {
                if (!falling && d < nearest) { nearest = d; cantReach = s; }
                continue;
            }
            if (share && FriendGetsIt(s, cost)) continue; // let a friend have it
            if (cost < bestCost) { bestCost = cost; best = s; }
        }
        if (best != target) { target = best; targetSince = 0f; }
        targetCost = bestCost;
        if (!best && cantReach && !asleep && !BuddyTower.Has(body) && (cantReach.kind == Snack.Kind.Sweet || !BuddyTower.Want(body, cantReach))) TooHigh(cantReach, now); // (friends could help: a buddy tower; not for one little sweet)
    }

    const int PlanAtMost = 8;
    static readonly List<float> s_dist = new List<float>();

    // how far the PlanAtMost-th nearest snack it'd consider is (everything, when there aren't more than that)
    float Nearest(float now, bool asleep, float notice, bool catcher)
    {
        if (Snack.All.Count <= PlanAtMost) return float.MaxValue;
        s_dist.Clear();
        foreach (var s in Snack.All)
        {
            if (s.Held || s.Eaten || (!s.Resting && !catcher)) continue;
            if (ignoreUntil.TryGetValue(s, out float until) && now < until) continue;
            float d = (s.Position - body.Center).magnitude;
            if ((asleep && d > wakeDistance) || d > notice) continue;
            s_dist.Add(d);
        }
        if (s_dist.Count <= PlanAtMost) return float.MaxValue;
        s_dist.Sort();
        return s_dist[PlanAtMost - 1];
    }

    // a friend that's going for it and will get there first
    bool FriendGetsIt(Snack s, float myCost)
    {
        foreach (var e in s_all)
            if (e != this && e.target == s && e.TargetCost + 0.3f < myCost) return true;
        return false;
    }

    // it would love that snack but can't get to it (yet): says so, once
    void TooHigh(Snack s, float now)
    {
        if (!speech || told.Contains(s) || now - lastTold < 20f || speech.Talking) return;
        bool someday = brain.CouldEverReach(s.Position, s.SurfaceY);
        string[] lines = someday ? new[] { "TOO HIGH... FOR NOW", "WHEN I'M BIGGER!", "CAN'T GET THERE YET" } : new[] { "TOO HIGH!", "CAN'T REACH :(" };
        if (!speech.Say(lines[UnityEngine.Random.Range(0, lines.Length)])) return; // (busy talking: next time)
        if (told.Count > 32) told.Clear();
        told.Add(s);
        lastTold = now;
    }

    // no brain to plan with: the nearest one lying around
    void PickNearest(float now, bool asleep)
    {
        Snack best = null;
        float bestD = noticeDistance;
        foreach (var s in Snack.All)
        {
            if (s.Held || !s.Resting) continue;
            if (ignoreUntil.TryGetValue(s, out float until) && now < until) continue;
            float d = (s.Position - body.Center).magnitude;
            if (asleep && d > wakeDistance) continue;
            if (d < bestD) { bestD = d; best = s; }
        }
        if (best != target) { target = best; targetSince = 0f; }
    }

    void Eat(Snack s)
    {
        s.Eat(Mouth);
        if (s == target) target = null;
        ignoreUntil.Remove(s);
        if (audioSrc && crunchClip) { audioSrc.pitch = UnityEngine.Random.Range(0.93f, 1.08f); audioSrc.PlayOneShot(crunchClip, volume); }
        if (brain) brain.Treat();
        if (progress) progress.AddXp(s.xp >= 0 ? s.xp : progress.xpSnack, s.Position);
        Ate?.Invoke(s);
        AnyAte?.Invoke(this, s);
    }

    Vector2 Mouth()
    {
        Vector2 down = Quaternion.Euler(0f, 0f, body.Angle) * Vector2.down;
        return body.Center + down * (0.12f * body.size);
    }
}
