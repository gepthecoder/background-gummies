using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Fusing, and being scared of what comes out. Hold a buddy and press it into another one (or into a monster) for a
/// moment: they squelch, wobble and squash into one big monster (BuddyMonster). Everyone outside is scared of monsters:
/// one comes close and they scream, run the other way or hide behind a window (shivering, peeking out) and only come
/// out once it's gone; cornered, they cower and shiver. A duck whose mama is inside isn't scared: it follows the monster
/// at a careful distance ("MAMA...?"). Also brings back the monsters there were when the app closed.
/// </summary>
[DefaultExecutionOrder(9)] // after BuddyRain (6) and BuddyMonster (8): running from a monster wins
[RequireComponent(typeof(GummyBody))]
[DisallowMultipleComponent]
public class BuddyFusion : MonoBehaviour
{
    [Tooltip("Hold it pressed into another buddy this long to fuse (seconds).")]
    public float pressTime = 0.55f;
    [Tooltip("Scared of a monster this close (world units between them).")]
    public float fearDistance = 2.4f;

    [Header("Sounds (WhipIt > Setup assigns them; a monster uses its buddies')")]
    public AudioClip squishClip;
    public AudioClip fuseClip;
    public AudioClip splitClip;
    public AudioClip roarClip;
    public AudioClip stompClip;
    [Range(0f, 1f)] public float volume = 0.7f;

    public enum Mode { None, Run, Hide, Cower, Mama }
    /// <summary>What it's doing about a monster right now.</summary>
    public Mode Current { get; private set; }
    /// <summary>Being pressed into another buddy right now (0..1 of the way to fusing).</summary>
    public float Pressing => pressInto ? Mathf.Clamp01(pressT / pressTime) : 0f;
    /// <summary>Tests: times it got scared, hid, cowered.</summary>
    public int Scares { get; private set; }
    public int Hides { get; private set; }

    GummyBody body;
    GummyBrain brain;
    BuddySpeech speech;
    BuddyDuck duck;
    AudioSource sfx;
    GummyBody pressInto;
    float pressT, modeT, nextPlan, nextLine, calmSince = -1f, lastGap;
    bool hurried, steering, shivering;
    long hideWin;
    Vector2 spot;
    static float s_lastScream = -10f;
    static bool s_restored;
    static readonly List<Vector2> s_hideSpots = new List<Vector2>();

    float S => body.size / 0.6f;
    float HalfW => body.Extent.width > 0.01f ? body.Extent.width * 0.5f : body.size * 0.5f;
    float Feet => body.Extent.yMin - body.SkateLift;

    void Awake()
    {
        body = GetComponent<GummyBody>();
        brain = GetComponent<GummyBrain>();
        speech = GetComponent<BuddySpeech>();
        duck = GetComponent<BuddyDuck>();
        sfx = gameObject.AddComponent<AudioSource>();
        sfx.playOnAwake = false;
        sfx.spatialBlend = 0f;
    }

    void Start()
    {
        // the monsters there were when the app closed (once everyone's in: the friends appear in their Start)
        if (!s_restored) { s_restored = true; StartCoroutine(RestoreSoon()); }
    }

    IEnumerator RestoreSoon()
    {
        yield return null;
        BuddyMonster.Restore();
    }

    void OnDisable()
    {
        Calm(false);
        pressInto = null;
        pressT = 0f;
    }

    // ------------------------------------------------------------------ every frame

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("BuddyFusion.Update", t0); }
    void UpdateTimed()
    {
        float now = Time.time, dt = Time.deltaTime;
        if (body.Held) Press(dt);
        else if (pressInto) { pressInto = null; pressT = 0f; }
        Fear(now, dt);
    }

    // ------------------------------------------------------------------ pressed into another buddy

    void Press(float dt)
    {
        GummyBody into = null;
        float best = float.MaxValue;
        foreach (var o in GummyBody.All)
        {
            if (!o || o == body || !o.isActiveAndEnabled) continue;
            if (!o.IsMonster && !o.GetComponent<BuddyFusion>()) continue; // (buddies and monsters only)
            float d = (o.Center - body.Center).magnitude;
            float r = (o.Extent.width + body.Extent.width) * 0.5f;
            bool deep = o.Covers(body.Center) || body.Covers(o.Center) || d < 0.62f * r;
            if (deep && d < best) { best = d; into = o; }
        }
        if (into != pressInto)
        {
            pressInto = into;
            pressT = 0f;
            if (into && CanFuse(into)) Play(squishClip, Random.Range(0.95f, 1.1f));
        }
        if (!into || !CanFuse(into)) return;
        pressT += dt;
        // squelching: it wobbles in your hand
        float k = Mathf.Clamp01(pressT / pressTime);
        body.Pose = new Vector2(1f + 0.12f * k * Mathf.Sin(Time.time * 40f), 1f - 0.12f * k * Mathf.Sin(Time.time * 40f));
        if (pressT < pressTime) return;
        pressT = 0f;
        pressInto = null;
        body.Pose = Vector2.one;
        BuddyMonster.Fuse(body, into);
    }

    bool CanFuse(GummyBody into)
    {
        var m = into.GetComponent<BuddyMonster>();
        if (m && m.Members.Count >= BuddyMonster.MaxMembers) return false;
        if (!m && (into.IsMonster || BuddyMonster.IsInside(into))) return false;
        if (Busy(body) || (!m && Busy(into))) return false;
        if (GiantPotion.IsGiant(body) || GiantPotion.IsGiant(into)) return false; // (a giant: wait till it's popped back)
        foreach (var o in GummyBody.All) // (not while a friend's being born)
        {
            var fr = o ? o.GetComponent<BuddyFriends>() : null;
            if (fr && fr.Planting) return false;
        }
        return true;
    }

    // in the middle of something it can't just vanish from
    static bool Busy(GummyBody b)
    {
        if (b.OnRope || b.Gliding || b.Skating) return true;
        var br = b.GetComponent<GummyBrain>();
        if (br && (br.Towering || br.InPit)) return true;
        var sl = b.GetComponent<BuddySlide>();
        if (sl && sl.Busy) return true;
        var dk = b.GetComponent<BuddyDuck>();
        if (dk && dk.Arriving) return true;
        return BuddyTower.Has(b);
    }

    // ------------------------------------------------------------------ scared of monsters

    void Fear(float now, float dt)
    {
        if (BuddyMonster.All.Count == 0 || body.Held || !brain) { if (Current != Mode.None) Calm(true); return; }
        if (BandJam.Current || Bedtime.Sleeping) { if (Current != Mode.None) Calm(false); return; } // (the band's playing, or it's bedtime: nobody's scared)
        BuddyMonster m = BuddyMonster.Nearest(body, out float gap);
        if (!m) { if (Current != Mode.None) Calm(true); return; }
        lastGap = gap;
        bool mama = duck && duck.mama && m.Has(duck.mama);
        float reach = fearDistance + (m.Chasing == body ? 1.5f : 0f);
        if (body.Extent.yMin > m.GetComponent<GummyBody>().Extent.yMax + 0.3f) reach *= 0.4f; // (up on a window above it: it can't climb)

        if (mama) { Mama(m, gap, now); return; }
        if (Current == Mode.Mama) Calm(false);

        if (Current == Mode.None)
        {
            if (gap > reach || Busy(body)) return;
            Alarm(m, now);
        }
        // far enough for long enough: it's over
        bool safe = gap > reach + 1.2f || (Current == Mode.Hide && gap > reach * 0.6f && AtSpot());
        if (gap > reach + 1.2f) { if (calmSince < 0f) calmSince = now; }
        else calmSince = -1f;
        if (calmSince >= 0f && now - calmSince > (Current == Mode.Hide ? 3f : 1.5f)) { Calm(true); return; }

        switch (Current)
        {
            case Mode.Run: UpdateRun(m, now); break;
            case Mode.Hide: UpdateHide(m, now); break;
            case Mode.Cower: UpdateCower(m, now); break;
        }
        Shiver(now, safe);
    }

    void Alarm(BuddyMonster m, float now)
    {
        Scares++;
        brain.WakeUp();
        modeT = now;
        calmSince = -1f;
        if (now - s_lastScream > 1.2f || Random.value < 0.3f)
        {
            s_lastScream = now;
            Say(Line(Pick("MONSTER!!", "RUN!!", "IT HAS MY LEAF?!"), Pick("EEEK! MONSTER!", "NOT ME! I'M SQUISHY!"), Pick("AAAH! MONSTER!", "TOO MUCH CAFFEINE! RUN!"),
                     Pick("DON'T EAT ME!", "I'M NOT ON THE MENU!"), Pick("RAWR?! ...EEK!", "IT'S SCARIER THAN ME!"), Pick("QUACK!! MONSTER!", "MAMA!! HELP!")), true);
        }
        if (FindHide(m, out Vector2 at, out long win)) { Hide(at, win, now); return; }
        Run(m, now);
    }

    void Run(BuddyMonster m, float now)
    {
        Current = Mode.Run;
        modeT = now;
        Hurry(1.8f);
        nextPlan = 0f;
    }

    void UpdateRun(BuddyMonster m, float now)
    {
        if (now < nextPlan) { Steer(); return; }
        nextPlan = now + 0.6f;
        // the other way, as far as it can on what it stands on
        Rect r = ScreenBounds.WorldRect;
        float lo = r.xMin + HalfW + 0.1f, hi = r.xMax - HalfW - 0.1f;
        if (body.OnPlatform || ScreenBounds.Multi) { lo = Mathf.Max(lo, body.SupportSpan.x + HalfW * 0.5f); hi = Mathf.Min(hi, body.SupportSpan.y - HalfW * 0.5f); } // (more than one screen: along its floor)
        float dir = Mathf.Sign(body.Center.x - m.transform.position.x);
        if (dir == 0f) dir = Random.value < 0.5f ? -1f : 1f;
        float x = Mathf.Clamp(body.Center.x + dir * (fearDistance + 2f), lo, hi);
        if (Mathf.Abs(x - body.Center.x) < 0.35f)
        {
            // cornered: a window to hide behind after all? else it cowers
            if (FindHide(m, out Vector2 at, out long win)) { Hide(at, win, now); return; }
            Cower(now);
            return;
        }
        spot = new Vector2(x, Feet);
        Steer();
    }

    void Hide(Vector2 at, long win, float now)
    {
        Current = Mode.Hide;
        Hides++;
        modeT = now;
        spot = at;
        hideWin = win;
        s_hideSpots.Add(at);
        body.HideWindow = win;
        if (WindowRect(win, out Rect wr)) body.HideRect = wr;
        Hurry(1.8f);
        nextPlan = 0f;
        nextLine = now + Random.Range(4f, 8f);
    }

    void UpdateHide(BuddyMonster m, float now)
    {
        if (!WindowRect(hideWin, out Rect wr)) { Unhide(); Run(m, now); return; }
        body.HideRect = wr;
        if (AtSpot())
        {
            // tucked in: stays put and peeks now and then
            ClearSteer();
            brain.Food = null;
            brain.StayPut = true;
            Unhurry();
            if (now >= nextLine)
            {
                nextLine = now + Random.Range(6f, 12f);
                if (Random.value < 0.5f) Say(Line(Pick("SHHH...", "IS IT STILL THERE?"), Pick("HIDE HIDE HIDE", "I'M NOT HERE"), Pick("NOT A SOUND...", "STAY STILL. STAY STILL"),
                                                 Pick("I'M JUST A BUN...", "SHHH"), Pick("DINOS DON'T HIDE. EXCEPT NOW", "SHHH"), Pick("QUACK... (QUIETLY)", "SHHH")), false);
            }
            return;
        }
        if (now - modeT > 10f) { Unhide(); Run(m, now); return; } // couldn't get there
        // behind the window: hops along its floor there (the route planner doesn't go behind windows)
        ClearSteer();
        brain.StayPut = false;
        if (brain.IsFree && body.Grounded && now >= nextPlan) { nextPlan = now + 0.5f; brain.Visit(spot.x); }
    }

    void Cower(float now)
    {
        Current = Mode.Cower;
        modeT = now;
        Unhurry();
        ClearSteer();
        brain.Food = null;
        brain.StayPut = true;
        Say(Line("EEP!", "PLEASE NO!", "I'M TOO YOUNG TO BE FUSED", "NOT THE BUN!", "MOMMY?!", "MAMA!!"), false);
    }

    void UpdateCower(BuddyMonster m, float now)
    {
        brain.Food = null;
        brain.StayPut = true;
        if (now - modeT > 1.5f) // the way's open again? run
        {
            modeT = now;
            Rect r = ScreenBounds.WorldRect;
            float dir = Mathf.Sign(body.Center.x - m.transform.position.x);
            float x = body.Center.x + dir * 1.5f;
            if (x > r.xMin + HalfW + 0.2f && x < r.xMax - HalfW - 0.2f) { brain.StayPut = false; Run(m, now); }
        }
    }

    // a duck whose mama is inside: it follows the monster, not too close
    void Mama(BuddyMonster m, float gap, float now)
    {
        if (Current != Mode.Mama)
        {
            if (Current != Mode.None) Calm(false);
            Current = Mode.Mama;
            modeT = now;
            nextLine = now + 1f;
            Say(Pick("MAMA...?", "MAMA? IS THAT YOU?"), true);
        }
        if (now >= nextLine)
        {
            nextLine = now + Random.Range(9f, 16f);
            if (Random.value < 0.6f) Say(Pick("MAMA...?", "YOU LOOK DIFFERENT, MAMA", "MAMA, WHY SO MANY EYES?", "I STILL LOVE YOU, MAMA", "MAMA? COME OUT?"), false);
        }
        if (body.Held || !body.Grounded || now < nextPlan) return;
        nextPlan = now + 0.8f;
        float dir = Mathf.Sign(body.Center.x - m.transform.position.x);
        if (dir == 0f) dir = 1f;
        Rect r = ScreenBounds.WorldRect;
        if (gap < 0.8f || gap > 3f)
        {
            float want = m.transform.position.x + dir * (m.GetComponent<GummyBody>().Extent.width * 0.5f + HalfW + 1.6f);
            want = Mathf.Clamp(want, r.xMin + HalfW + 0.1f, r.xMax - HalfW - 0.1f);
            if (Mathf.Abs(want - body.Center.x) > 0.4f && brain.IsFree) brain.Visit(want);
        }
    }

    // over: out from behind its window, back to normal
    void Calm(bool relieved)
    {
        if (Current == Mode.None) return;
        bool was = Current != Mode.Mama;
        Unhide();
        ClearSteer();
        if (brain) brain.StayPut = false;
        Unhurry();
        if (shivering && body) { body.Pose = Vector2.one; shivering = false; }
        Current = Mode.None;
        calmSince = -1f;
        if (relieved && was && Random.value < 0.5f)
            Say(Line(Pick("IS IT GONE?", "PHEW..."), Pick("PHEW! STILL ME", "IS IT GONE?"), "HEART RATE: ESPRESSO", "STILL IN ONE PIECE", "I WASN'T SCARED. RAWR", "MAMA?"), false);
    }

    void Unhide()
    {
        if (hideWin == 0 || !body) return;
        s_hideSpots.Remove(spot);
        if (speech) speech.Suppress(BuddySpeech.Topic.Escape, 1.5f);
        body.HideRect = default;
        body.HideWindow = 0;
        if (body.Grounded && !body.Held) body.Hop(new Vector2(0f, 3.5f * Mathf.Sqrt(S)), 0.6f); // (squeezes out)
        hideWin = 0;
    }

    // scared stiff: a quick little shiver while it stands still
    void Shiver(float now, bool safe)
    {
        bool still = body.Grounded && brain.Current == GummyBrain.State.Idle && Current != Mode.Mama && !safe;
        if (still)
        {
            float w = 0.035f * Mathf.Sin(now * 55f);
            body.Pose = new Vector2(1f + w, 1f - w);
            shivering = true;
        }
        else if (shivering) { body.Pose = Vector2.one; shivering = false; }
    }

    // ------------------------------------------------------------------ hiding spots (behind a window down to its floor)

    bool FindHide(BuddyMonster m, out Vector2 at, out long id)
    {
        at = default; id = 0;
        if (!body.Grounded) return false;
        Rect scr = ScreenBounds.WorldRect;
        float feet = Feet, hw = HalfW, h = body.Extent.height, cx = body.Center.x, mx = m.transform.position.x, lo = scr.xMin, hi = scr.xMax;
        if (body.OnPlatform || ScreenBounds.Multi) { lo = Mathf.Max(lo, body.SupportSpan.x); hi = Mathf.Min(hi, body.SupportSpan.y); }
        float cost = float.MaxValue;
        foreach (var w in DesktopPlatforms.Windows)
        {
            if (!w.movable || w.screen || w.yMin > feet + 0.03f || w.yMax < feet + h + 0.15f) continue;
            float a = Mathf.Max(w.xMin, lo) + hw + 0.12f, c = Mathf.Min(w.xMax, hi) - hw - 0.12f;
            if (c < a) continue;
            float inset = Mathf.Min(1.2f * hw + 0.2f, (c - a) * 0.5f);
            float sx = cx < a ? a + inset : cx > c ? c - inset : cx;
            if (Taken(sx, feet, hw)) continue;
            if ((mx - cx) * (sx - cx) > 0f && Mathf.Abs(sx - cx) > Mathf.Abs(mx - cx) * 0.6f) continue; // (not past the monster)
            float k = Mathf.Abs(sx - cx) + (Mathf.Abs(sx - mx) < fearDistance ? 6f : 0f);
            if (k < cost) { cost = k; at = new Vector2(sx, feet); id = w.id; }
        }
        return id != 0 && cost < 9f;
    }

    static bool Taken(float x, float y, float hw)
    {
        foreach (var s in s_hideSpots) if (Mathf.Abs(s.x - x) < hw * 2f && Mathf.Abs(s.y - y) < 0.2f) return true;
        return false;
    }

    bool AtSpot() => body.Grounded && Mathf.Abs(body.Center.x - spot.x) < 0.35f * body.size && Mathf.Abs(Feet - spot.y) < 0.2f;

    static bool WindowRect(long id, out Rect r)
    {
        foreach (var w in DesktopPlatforms.Windows)
            if (w.id == id) { r = Rect.MinMaxRect(w.xMin, w.yMin, w.xMax, w.yMax); return true; }
        r = default;
        return false;
    }

    // ------------------------------------------------------------------ getting there (the route planner, like a snack)

    void Steer()
    {
        bool settled = body.Grounded && !body.Held && !body.IsStuck && (brain.Current == GummyBrain.State.Idle || brain.Current == GummyBrain.State.Hopping);
        if (settled && Time.time >= nextPlan) { nextPlan = Time.time + 0.6f; brain.PlanTo(new Vector2(spot.x, spot.y + body.size * 0.3f), spot.y, out _); }
        brain.StayPut = false;
        brain.Food = new Vector2(spot.x, spot.y + body.size * 0.3f);
        brain.FoodSurface = spot.y;
        steering = true;
    }

    void ClearSteer()
    {
        if (steering && brain) brain.Food = null;
        steering = false;
    }

    void Hurry(float h)
    {
        brain.Hurry = h;
        hurried = true;
    }

    void Unhurry()
    {
        if (hurried && brain) brain.Hurry = 1f;
        hurried = false;
    }

    // ------------------------------------------------------------------ helpers

    // its line in its own voice: apple, tomato, bean, burger, dino, duck
    string Line(string apple, string tomato, string bean, string burger, string dino, string duckLine)
    {
        var v = speech ? speech.voice : BuddySpeech.Voice.Apple;
        return v == BuddySpeech.Voice.Tomato ? tomato : v == BuddySpeech.Voice.Bean ? bean : v == BuddySpeech.Voice.Burger ? burger
             : v == BuddySpeech.Voice.Dino ? dino : v == BuddySpeech.Voice.Duck ? duckLine : apple;
    }

    void Say(string line, bool force)
    {
        if (speech) speech.Say(line, 0f, force);
    }

    void Play(AudioClip clip, float pitch)
    {
        if (!clip || !sfx) return;
        sfx.pitch = pitch;
        sfx.PlayOneShot(clip, volume);
    }

    static string Pick(params string[] l) => l[Random.Range(0, l.Length)];
}
