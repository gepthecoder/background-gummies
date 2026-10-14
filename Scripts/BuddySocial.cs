using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The buddies hanging out: now and then one hops over to another for a chat (whispered secrets while your cursor is
/// far away, and suddenly very innocent when it comes close), a game of tag, or a nap side by side when the other one
/// is sleeping. Snack rivalry: eating the snack another one was going for gets a "MINE!" and a "HEY!!".
/// Works for any number of buddies (two at a time do things together). Put it on every buddy; the first one runs the show.
/// </summary>
[RequireComponent(typeof(GummyBody))]
[DisallowMultipleComponent]
public class BuddySocial : MonoBehaviour
{
    [Tooltip("How often it thinks about going over to its friend (seconds, min / max).")]
    public Vector2 visitEvery = new Vector2(18f, 40f);
    [Range(0f, 1f)] public float visitChance = 0.7f;
    [Tooltip("A visit is a game of tag this often.")]
    [Range(0f, 1f)] public float tagChance = 0.3f;
    [Tooltip("Goes over and naps next to its friend when the friend falls asleep.")]
    [Range(0f, 1f)] public float napTogether = 0.7f;
    [Tooltip("Says something when it eats the snack the other one was going for.")]
    [Range(0f, 1f)] public float rivalry = 0.6f;
    [Tooltip("Whispers secrets when the cursor is further away than this (world units)...")]
    public float whisperDistance = 5f;
    [Tooltip("... and acts innocent when it comes closer than this.")]
    public float innocentDistance = 3f;

    /// <summary>No greeting / visits before this time (the tomato's birth has its own lines).</summary>
    public static float QuietUntil;
    /// <summary>Tag: (who tagged, who's "it" now).</summary>
    public static event System.Action<GummyBody, GummyBody> Tagged;

    public GummyBody Body => body;
    public GummyBrain Brain => brain;
    public BuddySpeech Speech => speech;
    /// <summary>Two of them are in the middle of a conversation.</summary>
    public bool Chatting { get { foreach (var l in lines) if (l.convo) return true; return false; } }
    public bool Secret => secret && Chatting;

    static readonly List<BuddySocial> s_all = new List<BuddySocial>();

    GummyBody body;
    GummyBrain brain;
    BuddySpeech speech;
    BuddyEater eater;

    // the show (run by the first buddy for both)
    enum Plan { None, Chat, Tag, Nap }
    Plan plan;
    BuddySocial visitor, host;
    BuddySocial talkA, talkB;   // who's in the conversation
    float planSince, nextPlan, lastRivalry = -10f, friendSince = -1f, innocentUntil = -10f;
    int tagRounds, tries, known;
    bool greeted, secret, friendWasAsleep, napDecided;

    struct Line { public float at, seconds; public BuddySocial who; public string text, about; public BuddySpeech.Topic topic; public bool useTopic, convo; }
    readonly List<Line> lines = new List<Line>();

    void Awake()
    {
        body = GetComponent<GummyBody>();
        brain = GetComponent<GummyBrain>();
        speech = GetComponent<BuddySpeech>();
        eater = GetComponent<BuddyEater>();
        nextPlan = Time.time + Random.Range(8f, 16f);
    }

    void OnEnable()
    {
        if (!s_all.Contains(this)) s_all.Add(this);
        if (brain) brain.Moment += OnMoment;
        BuddyEater.AnyAte += OnAnyAte;
    }

    void OnDisable()
    {
        s_all.Remove(this);
        if (brain) brain.Moment -= OnMoment;
        BuddyEater.AnyAte -= OnAnyAte;
        foreach (var s in s_all) s.Forget();
        Forget();
    }

    void Forget()
    {
        plan = Plan.None;
        visitor = host = null;
        talkA = talkB = null;
        lines.Clear();
        friendSince = -1f;
        known = 0;
        pendingChaser = pendingRunner = chaseTarget = null;
        napSleeper = null;
        tagRounds = 0;
    }

    bool Director => s_all.Count >= 2 && s_all[0] == this;

    void OnMoment(string what)
    {
        if (what != "arrived") return;
        if (s_all.Count >= 2 && s_all[0]) s_all[0].Arrived(this);
    }

    // ------------------------------------------------------------------ the show

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("BuddySocial.Update", t0); }
    void UpdateTimed()
    {
        if (!Director) return;
        float now = Time.time;
        if (friendSince < 0f || known != s_all.Count) { friendSince = now; greeted = false; known = s_all.Count; }

        // hello when they're all here (not during a birth, that has its own lines)
        if (!greeted && now - friendSince > 2.5f)
        {
            greeted = true;
            if (now > QuietUntil) Greet();
        }

        RunLines(now);
        if (BuddyHideSeek.Playing || BuddyChat.Open)
        {
            // busy playing hide and seek / talking to you: no visits, chats or games of tag right now
            if (plan != Plan.None) EndPlan();
            pendingChaser = pendingRunner = null;
            lines.RemoveAll(l => l.convo);
            nextPlan = Mathf.Max(nextPlan, now + 10f);
            return;
        }
        WatchConversation(now);
        WatchNaps(now);

        // a visit that went wrong (grabbed, snack, stuck): forget it
        if (plan != Plan.None && (now - planSince > 14f || visitor.body.Held || host.body.Held)) EndPlan();

        if (plan == Plan.None && !Chatting && now > nextPlan && now > QuietUntil)
        {
            nextPlan = now + Random.Range(visitEvery.x, visitEvery.y);
            if (Random.value < visitChance && AwakeCount() >= 2)
            {
                tries = 0;
                tagRounds = 0;
                BuddySocial who = PickVisitor();
                BuddySocial to = PickFriend(who, false);
                Plan kind = Random.value < tagChance ? Plan.Tag : Plan.Chat;
                if (!to || !StartVisit(who, to, kind)) nextPlan = now + 4f; // try again soon
            }
        }
    }

    /// <summary>Start a game of tag now: this buddy goes after the other one (the chat uses it). False if it can't.</summary>
    public static bool PlayTag(GummyBody who)
    {
        if (s_all.Count < 2 || !who) return false;
        BuddySocial dir = s_all[0], me = null;
        foreach (var soc in s_all) if (soc.body == who) me = soc;
        if (!me || dir.plan != Plan.None) return false;
        BuddySocial to = dir.PickFriend(me, true);
        if (!to) return false;
        dir.tries = 0;
        dir.tagRounds = 0;
        dir.pendingChaser = dir.pendingRunner = dir.chaseTarget = null;
        return dir.StartVisit(me, to, Plan.Tag);
    }

    static bool Awake(BuddySocial s) => s && s.brain && s.brain.Current != GummyBrain.State.Asleep;

    static int AwakeCount()
    {
        int n = 0;
        foreach (var s in s_all) if (Awake(s)) n++;
        return n;
    }

    // with two it's a coin toss (as it always was); with more, any one that's awake
    static BuddySocial PickVisitor()
    {
        if (s_all.Count == 2) return Random.value < 0.5f ? s_all[0] : s_all[1];
        var awake = new List<BuddySocial>();
        foreach (var s in s_all) if (Awake(s)) awake.Add(s);
        return awake.Count > 0 ? awake[Random.Range(0, awake.Count)] : null;
    }

    // someone for it to go and see: the other one (two buddies), else an awake one on the same ground if there is one
    // (nearest = for tag from the chat, so it's quick), else any awake one
    BuddySocial PickFriend(BuddySocial who, bool nearest)
    {
        if (!who) return null;
        if (s_all.Count == 2) { BuddySocial o = s_all[0] == who ? s_all[1] : s_all[0]; return Awake(o) ? o : null; }
        var near = new List<BuddySocial>();
        var any = new List<BuddySocial>();
        foreach (var s in s_all)
        {
            if (s == who || !Awake(s)) continue;
            any.Add(s);
            if (SameGround(who.body, s.body)) near.Add(s);
        }
        var pool = near.Count > 0 ? near : any;
        if (pool.Count == 0) return null;
        if (!nearest) return pool[Random.Range(0, pool.Count)];
        BuddySocial best = pool[0];
        foreach (var s in pool)
            if (Mathf.Abs(s.body.Center.x - who.body.Center.x) < Mathf.Abs(best.body.Center.x - who.body.Center.x)) best = s;
        return best;
    }

    // "HI TOMATO!" / "HI APPLE!": each one that's awake greets the next one
    void Greet()
    {
        var awake = new List<BuddySocial>();
        foreach (var s in s_all) if (Awake(s)) awake.Add(s);
        if (awake.Count < 2) return;
        for (int i = 0; i < awake.Count; i++)
        {
            BuddySocial to = awake[(i + 1) % awake.Count];
            Speak(awake[i], BuddySpeech.Topic.Hi, 1.4f * i, to.speech ? to.speech.Name : null);
        }
    }

    bool StartVisit(BuddySocial who, BuddySocial to, Plan kind)
    {
        if (!who || !to || who == to) return false;
        if (!who.brain.IsFree || !SameGround(who.body, to.body, kind == Plan.Tag)) return false;
        float gap = kind == Plan.Tag ? -0.05f : kind == Plan.Nap ? 0.02f : 0.15f;
        if (!Spot(who.body, to.body, gap, out float x)) return false;
        bool there = Mathf.Abs(x - who.body.Center.x) < 0.35f * who.body.size;
        if (!there && !who.brain.Visit(x)) return false;
        plan = kind;
        visitor = who;
        host = to;
        planSince = Time.time;
        if (kind == Plan.Tag && tagRounds <= 0) tagRounds = Random.Range(2, 5);
        if (kind == Plan.Chat) to.brain.Linger(8f); // waits for it
        if (there) Arrived(who);
        return true;
    }

    // where to stand next to the friend (on the side it comes from), on the same surface
    static bool Spot(GummyBody me, GummyBody friend, float gap, out float x)
    {
        float side = me.Center.x < friend.Center.x ? -1f : 1f;
        float half = me.Extent.width * 0.5f, reach = half + friend.Extent.width * 0.5f + gap * me.size;
        x = friend.Center.x + side * reach;
        Rect r = ScreenBounds.WorldRect;
        float lo = r.xMin + half + 0.02f, hi = r.xMax - half - 0.02f;
        if (me.OnPlatform || ScreenBounds.Multi) { lo = Mathf.Max(lo, me.SupportSpan.x + half * 0.5f); hi = Mathf.Min(hi, me.SupportSpan.y - half * 0.5f); } // (more than one screen: its floor)
        if (x < lo || x > hi) { x = friend.Center.x - side * reach; side = -side; } // no room that side: the other side
        return x >= lo && x <= hi && Mathf.Abs(x - me.Center.x) < 14f;
    }

    // on the same floor / window top (loose: the friend may be in the middle of a hop, e.g. running away in tag)
    static bool SameGround(GummyBody a, GummyBody b, bool loose = false)
    {
        if (!a.Grounded || a.IsStuck || b.IsStuck || b.Held) return false;
        bool hopping = loose && !b.Grounded && b.Extent.yMin > a.Extent.yMin - 0.05f && b.Extent.yMin < a.Extent.yMin + 1.2f;
        if (!b.Grounded && !hopping) return false;
        if (!hopping && Mathf.Abs(a.Extent.yMin - b.Extent.yMin) > 0.12f) return false;
        if (!hopping && a.OnPlatform != b.OnPlatform) return false;
        if (!hopping && a.OnPlatform && (Mathf.Abs(a.SupportSpan.x - b.SupportSpan.x) > 0.05f || Mathf.Abs(a.SupportSpan.y - b.SupportSpan.y) > 0.05f)) return false;
        // no solid window in between (nor a bit of the world on no screen)
        if (!a.solidWindows && !ScreenBounds.Multi) return true;
        float lo = Mathf.Min(a.Center.x, b.Center.x), hi = Mathf.Max(a.Center.x, b.Center.x), y = a.Extent.yMin + 0.05f;
        var wins = DesktopPlatforms.Windows;
        for (int k = 0; k < wins.Count; k++)
        {
            WindowBox w = wins[k];
            if (w.movable && (a.solidWindows || w.screen) && w.yMin <= y && w.yMax >= y && w.xMax > lo && w.xMin < hi) return false;
        }
        return true;
    }

    static bool Close(GummyBody a, GummyBody b, float extra) =>
        Mathf.Abs(a.Center.x - b.Center.x) < (a.Extent.width + b.Extent.width) * 0.5f + extra * a.size &&
        Mathf.Abs(a.Extent.yMin - b.Extent.yMin) < 0.15f;

    void Arrived(BuddySocial who)
    {
        if (!Director || plan == Plan.None || who != visitor) return;
        float now = Time.time;
        switch (plan)
        {
            case Plan.Chat:
                if (!Close(visitor.body, host.body, 0.6f) || !host.brain.IsFree) { EndPlan(); return; }
                StartConversation(visitor, host, now);
                EndPlan();
                break;

            case Plan.Tag:
                if (!Close(visitor.body, host.body, 0.35f))
                {
                    // it moved: after it again (a few times)
                    BuddySocial v = visitor, h = host;
                    EndPlan();
                    if (++tries > 4) { tagRounds = 0; Speak(v, "TOO FAST!", 0.2f, 1.4f); v.brain.Linger(1.5f); }
                    else { pendingChaser = v; chaseTarget = h; chaseAt = now + 0.15f; } // after it again
                    return;
                }
                // tagged! the other one runs off, then comes after this one
                Speak(visitor, "TAG!", 0f, 1.2f);
                Tagged?.Invoke(visitor.body, host.body);
                Speak(host, Random.value < 0.5f ? "HEY!" : "NO FAIR!", 0.5f, 1.2f);
                BuddySocial it = host, runner = visitor;
                EndPlan();
                tries = 0;
                if (--tagRounds > 0)
                {
                    float away = Mathf.Sign(runner.body.Center.x - it.body.Center.x);
                    float x = Mathf.Clamp(runner.body.Center.x + away * Random.Range(2.5f, 4.5f), ScreenBounds.WorldRect.xMin + 0.5f, ScreenBounds.WorldRect.xMax - 0.5f);
                    if (ScreenBounds.Multi) x = Mathf.Clamp(x, runner.body.SupportSpan.x + 0.5f, runner.body.SupportSpan.y - 0.5f); // (along its floor)
                    pendingRunner = runner;    // run! (as soon as it's back on its feet)
                    runX = x;
                    runUntil = now + 1.5f;
                    pendingChaser = it;       // counts to one...
                    chaseTarget = runner;
                    chaseAt = now + 1.1f;
                    it.brain.Linger(1.1f);
                }
                else
                {
                    Speak(runner, Random.value < 0.5f ? "HEHE" : "GOOD GAME", 1.3f, 1.4f);
                    it.brain.Linger(2f);
                }
                break;

            case Plan.Nap:
                if (host.brain.Current == GummyBrain.State.Asleep && Close(visitor.body, host.body, 0.8f))
                {
                    visitor.brain.NapNow();
                    if (Random.value < 0.5f) Speak(visitor, Random.value < 0.5f ? "SCOOTCH" : "ZZZ...", 0f, 1.4f);
                }
                EndPlan();
                break;
        }
    }

    BuddySocial pendingChaser, pendingRunner, chaseTarget;
    float chaseAt, runX, runUntil;

    void EndPlan()
    {
        plan = Plan.None;
        visitor = host = null;
    }

    // ------------------------------------------------------------------ conversations

    void StartConversation(BuddySocial a, BuddySocial b, float now)
    {
        Vector2 mid = (a.body.Center + b.body.Center) * 0.5f;
        secret = (Grabber.CursorWorld - mid).magnitude > whisperDistance && now > innocentUntil;
        string[][] pool = secret ? Secrets : Chats;
        if (!secret && (IsBean(a) || IsBean(b)) && Random.value < 0.5f) pool = BeanChats;
        string[] talk = pool[Random.Range(0, pool.Length)];
        bool inOrder = s_all.IndexOf(a) < s_all.IndexOf(b);
        talkA = inOrder ? a : b; // in the order they joined
        talkB = inOrder ? b : a;
        float t = 0.3f;
        for (int i = 0; i < talk.Length; i++)
        {
            float hold = Mathf.Clamp(1.2f + 0.06f * talk[i].Length, 1.4f, 2.4f);
            lines.Add(new Line { at = now + t, seconds = hold, who = i % 2 == 0 ? a : b, text = talk[i], convo = true });
            t += hold + 0.35f;
        }
        a.brain.Linger(t + 0.5f);
        b.brain.Linger(t + 0.5f);
    }

    static bool IsBean(BuddySocial s) => s.speech && s.speech.voice == BuddySpeech.Voice.Bean;

    void WatchConversation(float now)
    {
        // tag: the one who got away runs off, the one who's "it" counts to one and comes after it
        if (pendingRunner && (pendingRunner.brain.Visit(runX) || now > runUntil)) pendingRunner = null;
        if (pendingChaser && now > chaseAt && plan == Plan.None)
        {
            BuddySocial c = pendingChaser;
            pendingChaser = null;
            if (!StartVisit(c, chaseTarget, Plan.Tag))
            {
                if (now < chaseAt + 3f && chaseTarget) pendingChaser = c; // not ready yet: again next frame
                else tagRounds = 0;
            }
        }

        if (!Chatting) return;
        BuddySocial a = talkA, b = talkB;
        if (!a || !b) { lines.RemoveAll(l => l.convo); return; }
        // interrupted (grabbed, a snack, fell asleep...): stop talking
        bool busy = a.body.Held || b.body.Held || !a.brain || !b.brain ||
                    (a.brain.Current != GummyBrain.State.Idle && a.brain.Current != GummyBrain.State.Recover) ||
                    (b.brain.Current != GummyBrain.State.Idle && b.brain.Current != GummyBrain.State.Recover);
        if (busy) { lines.RemoveAll(l => l.convo); return; }

        // plotting something and the cursor comes over: nothing to see here
        if (secret)
        {
            Vector2 mid = (a.body.Center + b.body.Center) * 0.5f;
            if ((Grabber.CursorWorld - mid).magnitude < innocentDistance)
            {
                lines.RemoveAll(l => l.convo);
                secret = false;
                innocentUntil = now + 20f;
                BuddySocial first = Random.value < 0.5f ? a : b;
                if (first.speech) first.speech.Say(BuddySpeech.Topic.Innocent, 1f, 0f, true);
                Speak(first == a ? b : a, BuddySpeech.Topic.Innocent, 0.9f);
                a.brain.Linger(2.5f);
                b.brain.Linger(2.5f);
            }
        }
    }

    void RunLines(float now)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            Line l = lines[i];
            if (now < l.at) continue;
            lines.RemoveAt(i--);
            if (!l.who || !l.who.speech || !l.who.isActiveAndEnabled) continue;
            if (l.useTopic) { if (l.about != null) l.who.speech.Say(l.topic, l.about, 1f, 0f, true); else l.who.speech.Say(l.topic, 1f, 0f, true); }
            else l.who.speech.Say(l.text, l.seconds, true);
        }
    }

    void Speak(BuddySocial who, string text, float delay, float seconds = 0f)
    {
        if (delay <= 0f) { if (who && who.speech) who.speech.Say(text, seconds, true); return; }
        lines.Add(new Line { at = Time.time + delay, seconds = seconds, who = who, text = text });
    }

    void Speak(BuddySocial who, BuddySpeech.Topic topic, float delay, string about = null)
    {
        if (delay <= 0f)
        {
            if (who && who.speech) { if (about != null) who.speech.Say(topic, about, 1f, 0f, true); else who.speech.Say(topic, 1f, 0f, true); }
            return;
        }
        lines.Add(new Line { at = Time.time + delay, who = who, topic = topic, useTopic = true, about = about });
    }

    // ------------------------------------------------------------------ naps

    void WatchNaps(float now)
    {
        // someone's asleep while someone else is awake
        BuddySocial sleeper = null;
        foreach (var s in s_all) if (s.brain && !Awake(s)) { sleeper = s; break; }
        bool asleep = sleeper != null && AwakeCount() > 0;
        if (!asleep) { friendWasAsleep = false; napSleeper = null; return; }
        if (!friendWasAsleep || sleeper != napSleeper)
        {
            napSleeper = sleeper;
            // the friend just dozed off: in a bit, maybe go and lie down next to it
            friendWasAsleep = true;
            napDecided = Random.value > napTogether;
            napAt = now + Random.Range(2f, 6f);
        }
        if (napDecided || now < napAt || plan != Plan.None) return;
        BuddySocial awake = PickFriend(sleeper, true);
        if (!awake || !awake.brain.IsFree) return;
        if (Close(awake.body, sleeper.body, 0.8f)) { awake.brain.NapNow(); napDecided = true; }
        else if (StartVisit(awake, sleeper, Plan.Nap)) napDecided = true;
        else if (now > napAt + 15f) napDecided = true; // can't get there
    }

    float napAt;
    BuddySocial napSleeper;

    // ------------------------------------------------------------------ snack rivalry

    void OnAnyAte(BuddyEater who, Snack s)
    {
        if (!Director || Time.time - lastRivalry < 12f) return;
        BuddySocial winner = null;
        foreach (var soc in s_all) if (soc.eater == who) winner = soc;
        if (!winner) return;
        BuddySocial loser = null; // whoever else was going for that one
        foreach (var soc in s_all) if (soc != winner && soc.eater && soc.eater.Target == s) { loser = soc; break; }
        if (!loser) return;
        lastRivalry = Time.time;
        if (Random.value > rivalry) return;
        Speak(winner, BuddySpeech.Topic.Mine, 0.15f);
        Speak(loser, BuddySpeech.Topic.Stolen, 0.8f);
        if (loser.brain) loser.brain.Linger(1.5f);
    }

    // ------------------------------------------------------------------ what they talk about (visitor first)

    static readonly string[][] Secrets =
    {
        new[] { "PSST...", "WHAT?", "IS HE LOOKING?", "NOPE", "SNACK HEIST?", "HEHEHE" },
        new[] { "PSST!", "YEAH?", "LET'S HIDE THE MOUSE", "HAHA YES" },
        new[] { "SECRET PLAN", "TELL ME", "WOBBLE ALL THE WINDOWS", "EVIL. I LOVE IT" },
        new[] { "HE COPIES A LOT", "MORE SNACKS 4 US", "SHH!" },
        new[] { "IS HE LOOKING?", "NO...", "OK. BOOP", "HEY!" },
        new[] { "PSST", "WHAT", "I LICKED A WINDOW", "EW. HOW WAS IT" , "SALTY" },
    };

    static readonly string[][] Chats =
    {
        new[] { "HI!", "HI!!", "\u2665" },
        new[] { "NICE WALLPAPER", "I KNOW RIGHT" },
        new[] { "I ATE 3 COOKIES", "I ATE 4", "NO FAIR" },
        new[] { "RACE YA", "YOU'RE ON", "...LATER" },
        new[] { "YOU LOOK JUICY", "THANKS \u2665" },
        new[] { "SO MANY WINDOWS", "SO MANY TOPS", "TO SIT ON" },
        new[] { "WANNA NAP?", "LATER", "OK" },
    };

    // when the coffee bean is one of the two
    static readonly string[][] BeanChats =
    {
        new[] { "COFFEE?", "YOU ARE COFFEE", "OH. RIGHT" },
        new[] { "HE WORKS SO HARD", "I KNOW!", "STEAM SOON" },
        new[] { "RACE YA!", "YOU'RE TOO FAST", "I'M CAFFEINATED" },
        new[] { "DO YOU EVER SLEEP?", "SLEEP?", "...WOW" },
        new[] { "YOU SMELL NICE", "FRESHLY ROASTED \u2665" },
    };
}
