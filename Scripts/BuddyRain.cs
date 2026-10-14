using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The buddy and the rain (Rain): when the clouds come in it runs for cover under a window (anywhere it can get to: the
/// floor under a window, a window top under a higher one; no room under one = it squeezes in behind a window that comes
/// down to its floor) and waits there until it stops ("IT STOPPED!"). Caught in the rain it gets wet: it drips, and once
/// it's out of the rain it shakes itself dry like a dog (spray, "BRRRR!"). After the rain it goes splashing in the
/// puddles: walks into one and jumps up and down in it (SPLOOSH). The duck loves the rain: it stays out, waddles out from
/// under a window and splashes in the puddles while it pours ("COME PLAY, MAMA!").
/// </summary>
[DefaultExecutionOrder(6)] // after BuddyEater (it sets the snack) and BuddyDuck: the shelter wins while it rains
[RequireComponent(typeof(GummyBody))]
[DisallowMultipleComponent]
public class BuddyRain : MonoBehaviour
{
    [Tooltip("After the rain: how likely it goes splashing in a puddle (the duck always, even while it rains).")]
    [Range(0f, 1f)] public float splashChance = 0.7f;
    [Tooltip("Shaking itself dry (WhipIt > Setup assigns it).")]
    public AudioClip shakeClip;
    [Range(0f, 1f)] public float volume = 0.5f;

    public enum Mode { None, Seek, Sheltered, Exposed, ToPuddle, Splashing, Shaking }
    public Mode Current { get; private set; }
    /// <summary>How wet it is (0 dry .. 1 soaked).</summary>
    public float Wet => wet;
    /// <summary>Doing something about the rain (going for cover, sheltering, splashing, shaking): leave it be.</summary>
    public bool Busy => Current == Mode.Seek || Current == Mode.Sheltered || Current == Mode.ToPuddle || Current == Mode.Splashing || Current == Mode.Shaking;
    /// <summary>Sheltering behind a window (not under one).</summary>
    public bool Behind => behindWin != 0;
    /// <summary>Counts (tests): went for cover, splash jumps, shakes, puddle sessions.</summary>
    public int Shelters { get; private set; }
    public int Jumps { get; private set; }
    public int Shakes { get; private set; }
    public int Sessions { get; private set; }

    static readonly List<BuddyRain> s_all = new List<BuddyRain>();
    static float s_lastStopLine = -100f, s_lastRainLine = -100f, s_lastCallLine = -100f;

    GummyBody body;
    GummyBrain brain;
    BuddySpeech speech;
    BuddyProgress progress;
    BuddyHoops hoops;
    BuddyPit pit;
    BuddySlide slide;
    BuddyLaser laser;
    BuddyHideSeek hideSeek;
    BuddyDuck duck;
    AudioSource sfx;
    float steppingOut = -1f, ghostSince = -100f;
    float wet, lastHit = -100f, nextTry, nextSplash, nextLine, modeT, nextPlan, nextCheck, nextJump, dripAcc, sprayAcc, shakeT;
    int planFails, jumpsLeft, sprayed, sessionsSinceRain;
    Vector2 spot;
    long behindWin;
    bool wasRaining, hurried;
    Rain.Puddle puddle;
    Mode afterShake;
    readonly List<Vector3> tried = new List<Vector3>(); // spots it couldn't get to (x, y, until)

    bool IsDuck => speech && speech.voice == BuddySpeech.Voice.Duck;
    float HalfW => body.Extent.width > 0.01f ? body.Extent.width * 0.5f : body.size * 0.5f;
    float Height => body.Extent.height > 0.01f ? body.Extent.height : body.size;
    float Feet => body.Extent.yMin - body.SkateLift;
    float S => body.size / 0.6f;

    void Awake()
    {
        body = GetComponent<GummyBody>();
        brain = GetComponent<GummyBrain>();
        speech = GetComponent<BuddySpeech>();
        progress = GetComponent<BuddyProgress>();
        hoops = GetComponent<BuddyHoops>();
        pit = GetComponent<BuddyPit>();
        slide = GetComponent<BuddySlide>();
        laser = GetComponent<BuddyLaser>();
        hideSeek = GetComponent<BuddyHideSeek>();
        duck = GetComponent<BuddyDuck>();
        sfx = gameObject.AddComponent<AudioSource>();
        sfx.playOnAwake = false;
        sfx.spatialBlend = 0f;
    }

    void OnEnable() { if (!s_all.Contains(this)) s_all.Add(this); }

    void OnDisable()
    {
        s_all.Remove(this);
        Stop();
    }

    /// <summary>A raindrop hit it.</summary>
    public void Hit()
    {
        wet = Mathf.Min(1f, wet + 0.02f);
        lastHit = Time.time;
    }

    // ------------------------------------------------------------------ each frame

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("BuddyRain.Update", t0); }
    void UpdateTimed()
    {
        float now = Time.time, dt = Mathf.Min(Time.deltaTime, 0.05f);
        var rain = Rain.Current;
        if (!rain || !brain) { if (Current != Mode.None) Stop(); return; }
        bool raining = rain.Raining;
        if (raining && !wasRaining) { nextTry = now + Random.Range(0.3f, 1.6f); tried.Clear(); sessionsSinceRain = 0; }
        if (!raining && wasRaining) Stopped(now);
        wasRaining = raining;

        Drip(rain, dt);
        if (now - lastHit > 2f) wet = Mathf.Max(0f, wet - dt * 0.006f); // (it dries a little in the air)

        Ghost(now);
        if (body.Held) { if (Current != Mode.None && Current != Mode.Exposed) Stop(); return; }
        if (brain.Current == GummyBrain.State.Asleep)
        {
            // asleep in the rain: a few drops wake it up
            if (raining && !IsDuck && wet > 0.12f && now - lastHit < 1f && !Dry()) { brain.WakeUp(); Say(Line("HEY! IT'S RAINING!", "WAH! WET!", "COLD BREW?!", "MMF... SOGGY?", "RAWR?! WATER!", "QUACK!"), true); }
            else { if (Current == Mode.Seek || Current == Mode.ToPuddle || Current == Mode.Splashing) Stop(); return; }
        }

        bool here = raining && RainsHere(); // (more than one screen: it rains on one of them)
        switch (Current)
        {
            case Mode.None: Idle(rain, here, now); break;
            case Mode.Exposed: if (!here) Current = Mode.None; else Idle(rain, here, now); break;
            case Mode.Seek: UpdateSeek(raining, now); break;
            case Mode.Sheltered: UpdateSheltered(raining, now); break;
            case Mode.ToPuddle: UpdateToPuddle(rain, raining, now); break;
            case Mode.Splashing: UpdateSplash(rain, now); break;
            case Mode.Shaking: UpdateShake(rain, now, dt); break;
        }
    }

    // is it on the screen it's raining on? (one screen: yes)
    bool RainsHere()
    {
        if (!ScreenBounds.Multi) return true;
        Rect a = Rain.Area;
        Vector2 c = body.Center;
        return c.x > a.xMin - 0.3f && c.x < a.xMax + 0.3f && c.y > a.yMin - 0.3f && c.y < a.yMax + 0.3f;
    }

    void Idle(Rain rain, bool raining, float now)
    {
        if (BusyElsewhere()) return;
        if (raining && !IsDuck)
        {
            // take cover (where it is, if that's under a window already)
            if (now < nextTry || !body.Grounded || body.Held) return;
            nextTry = now + 5f;
            if (Dry()) { Shelter(now, false); return; }
            if (FindShelter(out Vector2 at, out long win)) { Seek(at, win, now); return; }
            if (Current != Mode.Exposed)
            {
                Current = Mode.Exposed;
                Say(Line("NOWHERE TO HIDE!", "I'M GETTING WET!", "I'M BREWING OUT HERE!", "SOGGY BUN...", "RAWR... SOAKED", "QUACK"), true);
            }
            return;
        }
        // out of the rain and wet: shakes it off
        if (wet > 0.35f && now - lastHit > 1.2f && body.Grounded && brain.Current == GummyBrain.State.Idle && !(raining && IsDuck)) { Shake(Mode.None, now); return; }
        // puddles!
        if (rain.Puddles.Count > 0 && now >= nextSplash && brain.IsFree && body.Grounded)
        {
            nextSplash = now + (IsDuck ? Random.Range(2f, 6f) : Random.Range(4f, 9f));
            float chance = IsDuck ? 0.9f : sessionsSinceRain == 0 ? splashChance : splashChance * 0.5f;
            if (Random.value < chance && ToPuddle(rain, now)) return;
        }
        // the duck in the rain: out from under the window, happy
        if (raining && IsDuck && now >= nextTry && brain.IsFree && body.Grounded)
        {
            nextTry = now + Random.Range(4f, 8f);
            if (Dry()) StepOut();
            if (now - s_lastCallLine > 14f && Random.value < 0.5f)
            {
                s_lastCallLine = now;
                bool mamaHides = duck && duck.mama && duck.mama.GetComponent<BuddyRain>() is BuddyRain m && m.Current == Mode.Sheltered;
                Say(mamaHides && Random.value < 0.6f ? "COME PLAY, MAMA!" : Pick("RAIN! QUACK!", "BEST DAY EVER!", "WET IS GOOD!", "SPLISH SPLASH!"), false);
                if (mamaHides && Random.value < 0.7f) { var ms = duck.mama.GetComponent<BuddySpeech>(); if (ms) ms.Say(Pick("NO WAY, I'LL GET SOGGY!", "COME HERE, YOU'LL CATCH A COLD!", "MAYBE LATER!"), 0f, false); }
            }
        }
    }

    // the rain stopped: out from under the window, "IT STOPPED!"; puddle time soon
    void Stopped(float now)
    {
        bool was = Current == Mode.Sheltered || Current == Mode.Seek || Current == Mode.Exposed;
        if (Current == Mode.Sheltered || Current == Mode.Seek) Stop();
        if (Current == Mode.Exposed) Current = Mode.None;
        nextSplash = now + Random.Range(1f, 4f);
        sessionsSinceRain = 0;
        if ((was || IsDuck) && now - s_lastStopLine > 3f && Random.value < 0.8f)
        {
            s_lastStopLine = now;
            Say(Line("IT STOPPED!", "SUNSHINE!", "IS IT OVER?", "DRY AGAIN!", "RAWR! SUN!", "AWW, MORE RAIN!"), true);
        }
    }

    // on its way (for cover, to a puddle, out into the rain) it squeezes past the others instead of getting stuck in a
    // crowd; there, it stays a ghost until it's clear of them (at most 2 s)
    void Ghost(float now)
    {
        bool going = Current == Mode.Seek || Current == Mode.ToPuddle || now < steppingOut;
        if (going) { body.GhostUntil = now + 0.2f; ghostSince = now; return; }
        if (body.GhostUntil > now - 0.05f && now - ghostSince < 2f && Overlapping()) body.GhostUntil = now + 0.2f;
    }

    bool Overlapping()
    {
        Rect me = body.Extent;
        foreach (var o in GummyBody.All)
        {
            if (!o || o == body || !o.isActiveAndEnabled) continue;
            Rect e = o.Extent;
            if (e.xMin < me.xMax - 0.02f && e.xMax > me.xMin + 0.02f && e.yMin < me.yMax - 0.02f && e.yMax > me.yMin + 0.02f) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ taking cover

    void Seek(Vector2 at, long win, float now)
    {
        Current = Mode.Seek;
        modeT = now;
        spot = at;
        behindWin = win;
        planFails = 0;
        nextPlan = 0f;
        Shelters++;
        if (win != 0) { body.HideWindow = win; if (WindowRect(win, out Rect wr)) body.HideRect = wr; } // (behind that window)
        brain.Hurry = 1.6f; // (runs for it)
        hurried = true;
        if (now - s_lastRainLine > 4f || Random.value < 0.35f)
        {
            s_lastRainLine = now;
            Say(Line(Pick("RAIN! TAKE COVER!", "IT'S RAINING!"), Pick("I'LL GET SOGGY!", "NOT MY SKIN!"), Pick("WATERED DOWN?! NO!", "NO! DECAF RAIN!"), Pick("MY BUN! SOGGY!", "NOT THE BUN!"), Pick("RAWR! WATER!", "DINO HATES BATHS!"), "QUACK!"), false);
        }
    }

    void UpdateSeek(bool raining, float now)
    {
        if (!raining || BusyElsewhere()) { Stop(); return; }
        if (now - modeT > 15f) { Remember(spot); Stop(); nextTry = now + 0.5f; return; } // couldn't get there
        if (behindWin != 0)
        {
            if (!WindowRect(behindWin, out Rect wr)) { Stop(); nextTry = now; return; }
            body.HideRect = wr;
            brain.Food = null;
            if (Mathf.Abs(body.Center.x - spot.x) < 0.35f * body.size && brain.Current == GummyBrain.State.Idle) { Shelter(now, true); return; }
            if (brain.IsFree && body.Grounded && now >= nextPlan) { nextPlan = now + 0.5f; brain.Visit(spot.x); }
            return;
        }
        if (body.Grounded && brain.Current == GummyBrain.State.Idle && Mathf.Abs(Feet - spot.y) < 0.15f && Mathf.Abs(body.Center.x - spot.x) < 0.45f && Dry()) { Shelter(now, false); return; }
        if (!GoTo(new Vector2(spot.x, spot.y + body.size * 0.3f), spot.y, now)) { Remember(spot); Stop(); nextTry = now + 0.3f; }
    }

    void Shelter(float now, bool behind)
    {
        Current = Mode.Sheltered;
        modeT = now;
        nextCheck = now + 0.5f;
        nextLine = now + Random.Range(8f, 16f);
        if (!behind) { spot = new Vector2(body.Center.x, Feet); behindWin = 0; }
        Unhurry();
        brain.Food = null;
        brain.StayPut = true;
        if (Random.value < 0.35f) Say(Line(Pick("PHEW, DRY", "SAFE!"), "COZY HERE", "NO DRIP COFFEE TODAY", "SOGGY BUN AVERTED", "DINO SHELTER", "QUACK"), false);
    }

    void UpdateSheltered(bool raining, float now)
    {
        brain.Food = null;
        brain.StayPut = true;
        if (!raining) { Stop(); return; }
        if (behindWin != 0 && WindowRect(behindWin, out Rect wr)) body.HideRect = wr;
        if (now >= nextCheck)
        {
            nextCheck = now + 0.5f;
            bool still = behindWin != 0 ? StillBehind() : Dry();
            if (!still && behindWin == 0 && SpotDry())
            {
                // nudged out from under it (a friend squeezing in): back to its spot
                Current = Mode.Seek;
                modeT = now;
                brain.StayPut = false;
                planFails = 0;
                nextPlan = 0f;
                return;
            }
            if (!still) { bool settled = now - modeT > 3f; Stop(); nextTry = now + 0.2f; if (settled) Say(Line("HEY! MY ROOF!", "WHERE'D IT GO?!", "MY UMBRELLA!", "HEY!", "RAWR! ROOF!", "QUACK?"), false); return; }
        }
        if (wet > 0.35f && now - lastHit > 1f && body.Grounded && behindWin == 0) { Shake(Mode.Sheltered, now); return; }
        if (now >= nextLine)
        {
            nextLine = now + Random.Range(18f, 35f);
            if (Random.value < 0.5f) Say(Line(Pick("I'LL WAIT HERE", "STILL RAINING..."), Pick("BRRR", "COZY"), Pick("PITTER PATTER...", "RAIN = SLOW DRIP"), Pick("NAP WEATHER", "ZZZ... RAIN"), Pick("RAWR... WET OUT THERE", "DINO STAYS DRY"), "QUACK"), false);
        }
    }

    // the spot it went for is still under a window (the window didn't go)
    bool SpotDry()
    {
        float hw = HalfW, top = spot.y + Height;
        return Rain.Roofed(spot.x - 0.7f * hw, top) && Rain.Roofed(spot.x, top) && Rain.Roofed(spot.x + 0.7f * hw, top);
    }

    bool StillBehind()
    {
        foreach (var w in DesktopPlatforms.Windows)
            if (w.id == behindWin) return w.movable && w.yMin <= spot.y + 0.05f && body.Center.x > w.xMin && body.Center.x < w.xMax;
        return false;
    }

    // under a window right where it stands (or behind one)
    bool Dry()
    {
        if (body.Hiding) return true;
        float hw = HalfW, x = body.Center.x, top = body.Extent.yMax;
        return Rain.Roofed(x - 0.7f * hw, top) && Rain.Roofed(x, top) && Rain.Roofed(x + 0.7f * hw, top);
    }

    // the best dry spot it can get to: under a window (on the floor, or on a window top under a higher one), else behind a
    // window that comes down to its floor
    static readonly List<Vector3> s_cands = new List<Vector3>();

    bool FindShelter(out Vector2 at, out long win)
    {
        at = default;
        win = 0;
        Rect r = ScreenBounds.WorldRect;
        float hw = HalfW, h = Height, cx = body.Center.x, feet = Feet;
        s_cands.Clear();
        Sample(r.xMin, r.xMax, r.yMin, hw, h, cx, feet);
        foreach (var p in DesktopPlatforms.Current) if (!DesktopPlatforms.IsIcon(p.id)) Sample(p.x0, p.x1, p.y, hw, h, cx, feet);
        s_cands.Sort((a, b) => a.z.CompareTo(b.z));
        float best = float.MaxValue;
        int plans = 0;
        foreach (var c in s_cands)
        {
            if (plans >= 10 || c.z > best) break;
            if (Tried(c.x, c.y)) continue;
            plans++;
            if (!brain.PlanTo(new Vector2(c.x, c.y + body.size * 0.3f), c.y, out float cost)) continue;
            if (cost < best) { best = cost; at = new Vector2(c.x, c.y); }
        }
        if (BehindSpot(out Vector2 bs, out long bw, out float bc) && bc < best) { best = bc; at = bs; win = bw; }
        return best < float.MaxValue;
    }

    void Sample(float x0, float x1, float y, float hw, float h, float cx, float feet)
    {
        for (float x = x0 + hw + 0.06f; x <= x1 - hw - 0.06f; x += 0.2f)
        {
            if (!Fits(x, y, h, hw) || !Rain.Roofed(x - 0.7f * hw, y + h) || !Rain.Roofed(x, y + h) || !Rain.Roofed(x + 0.7f * hw, y + h) || Claimed(x, y, hw)) continue;
            s_cands.Add(new Vector3(x, y, Mathf.Abs(x - cx) + 2f * Mathf.Abs(y - feet)));
        }
    }

    // room for it there (no window where its body would be)
    static bool Fits(float x, float y, float h, float hw)
    {
        foreach (var w in DesktopPlatforms.Windows)
            if (w.movable && w.xMax > x - hw && w.xMin < x + hw && w.yMin < y + h + 0.04f && w.yMax > y + 0.03f) return false;
        return true;
    }

    // someone's already there (or on the way)
    bool Claimed(float x, float y, float hw)
    {
        foreach (var o in s_all)
        {
            if (o == this || !o.isActiveAndEnabled || (o.Current != Mode.Seek && o.Current != Mode.Sheltered)) continue;
            if (Mathf.Abs(o.spot.y - y) < 0.12f && Mathf.Abs(o.spot.x - x) < hw + o.HalfW + 0.08f) return true;
        }
        foreach (var b in GummyBody.All)
        {
            if (!b || b == body || !b.isActiveAndEnabled) continue;
            Rect e = b.Extent;
            if (Mathf.Abs(e.yMin - y) < 0.15f && e.xMax > x - hw - 0.04f && e.xMin < x + hw + 0.04f) return true;
        }
        return false;
    }

    // behind a window that comes down to the floor it's on (like hide and seek)
    bool BehindSpot(out Vector2 at, out long id, out float cost)
    {
        at = default; id = 0; cost = float.MaxValue;
        if (!body.Grounded) return false;
        Rect scr = ScreenBounds.WorldRect;
        float feet = Feet, hw = HalfW, h = Height, cx = body.Center.x, lo = scr.xMin, hi = scr.xMax;
        if (body.OnPlatform || ScreenBounds.Multi) { lo = Mathf.Max(lo, body.SupportSpan.x); hi = Mathf.Min(hi, body.SupportSpan.y); }
        foreach (var w in DesktopPlatforms.Windows)
        {
            if (!w.movable || w.screen || w.yMin > feet + 0.03f || w.yMax < feet + h + 0.15f) continue;
            float a = Mathf.Max(w.xMin, lo) + hw + 0.12f, c = Mathf.Min(w.xMax, hi) - hw - 0.12f;
            if (c < a) continue;
            float inset = Mathf.Min(1.2f * hw + 0.2f, (c - a) * 0.5f);
            float sx = cx < a ? a + inset : cx > c ? c - inset : cx;
            if (Claimed(sx, feet, hw) || Tried(sx, feet) || Between(cx, sx, feet, w.id)) continue;
            float k = Mathf.Abs(sx - cx) + 2.5f; // (under a window, where you can see it, is better)
            if (k < cost) { cost = k; at = new Vector2(sx, feet); id = w.id; }
        }
        return id != 0;
    }

    static bool Between(float a, float b, float floor, long except)
    {
        float lo = Mathf.Min(a, b), hi = Mathf.Max(a, b), y = floor + 0.05f;
        foreach (var w in DesktopPlatforms.Windows)
            if (w.id != except && w.movable && w.yMin <= y && w.yMax >= y && w.xMax > lo && w.xMin < hi) return true;
        return false;
    }

    static bool WindowRect(long id, out Rect r)
    {
        foreach (var w in DesktopPlatforms.Windows)
            if (w.id == id) { r = Rect.MinMaxRect(w.xMin, w.yMin, w.xMax, w.yMax); return true; }
        r = default;
        return false;
    }

    void Remember(Vector2 at)
    {
        tried.Add(new Vector3(at.x, at.y, Time.time + 20f));
        if (tried.Count > 12) tried.RemoveAt(0);
    }

    bool Tried(float x, float y)
    {
        foreach (var t in tried) if (Time.time < t.z && Mathf.Abs(t.x - x) < 0.5f && Mathf.Abs(t.y - y) < 0.12f) return true;
        return false;
    }

    // the duck: a hop out from under the window into the rain
    void StepOut()
    {
        float hw = HalfW, top = body.Extent.yMax, cx = body.Center.x;
        Rect r = ScreenBounds.WorldRect;
        float lo = r.xMin + hw + 0.1f, hi = r.xMax - hw - 0.1f;
        if (body.OnPlatform || ScreenBounds.Multi) { lo = Mathf.Max(lo, body.SupportSpan.x + hw + 0.05f); hi = Mathf.Min(hi, body.SupportSpan.y - hw - 0.05f); }
        for (float d = 0.4f; d < 4f; d += 0.3f)
            foreach (float s in new[] { -1f, 1f })
            {
                float x = cx + s * d;
                if (x < lo || x > hi || Rain.Roofed(x, top) || Between(cx, x, Feet, 0)) continue;
                if (brain.Visit(x)) steppingOut = Time.time + 2.5f;
                return;
            }
    }

    // ------------------------------------------------------------------ puddles

    /// <summary>Off to splash in a puddle now (chat "puddle"). False if there's none it can get to, or it's busy.</summary>
    public bool SplashNow()
    {
        var rain = Rain.Current;
        if (!rain || Busy || !brain || !brain.IsFree || !body.Grounded || (rain.Raining && !IsDuck && Current != Mode.Exposed)) return false;
        return ToPuddle(rain, Time.time);
    }

    bool ToPuddle(Rain rain, float now)
    {
        Rain.Puddle best = null;
        float bestCost = float.MaxValue, w = HalfW * 2f;
        foreach (var p in rain.Puddles)
        {
            if (p.Width < w * 0.9f || p.Claims >= 2 || p.Volume < 0.05f || Crowded(p)) continue;
            if (!brain.PlanTo(new Vector2(p.X, p.Y + body.size * 0.3f), p.Y, out float cost)) continue;
            cost += p.Claims * 1.5f;
            if (cost < bestCost) { bestCost = cost; best = p; }
        }
        if (best == null) return false;
        puddle = best;
        puddle.Claims++;
        Current = Mode.ToPuddle;
        modeT = now;
        planFails = 0;
        nextPlan = 0f;
        sessionsSinceRain++;
        if (Random.value < 0.6f) Say(Line("PUDDLE!", "SPLASH TIME!", "PUDDLE JUMP!", "SPLOOSH TIME!", "RAWR! PUDDLE!", "PUDDLE! QUACK!"), false);
        return true;
    }

    // someone's sitting in it (sheltering, say): not a place to jump about
    bool Crowded(Rain.Puddle p)
    {
        foreach (var o in s_all)
        {
            if (o == this || !o.isActiveAndEnabled || o.Current == Mode.ToPuddle || o.Current == Mode.Splashing) continue;
            Rect e = o.body.Extent;
            if (Mathf.Abs(e.yMin - p.Y) < 0.2f && e.xMax > p.Left - 0.05f && e.xMin < p.Right + 0.05f) return true;
        }
        return false;
    }

    bool PuddleGone(Rain rain)
    {
        if (puddle == null || puddle.Width < 0.05f) return true;
        foreach (var p in rain.Puddles) if (p == puddle) return false;
        return true;
    }

    bool InPuddle(Rain.Puddle p) => body.Grounded && Mathf.Abs(Feet - p.Y) < 0.14f && Mathf.Abs(body.Center.x - p.X) < Mathf.Max(0.05f, p.Width * 0.5f - HalfW * 0.4f);

    void UpdateToPuddle(Rain rain, bool raining, float now)
    {
        if (PuddleGone(rain) || BusyElsewhere() || (raining && !IsDuck)) { Stop(); return; }
        if (now - modeT > 15f) { Stop(); nextSplash = now + 10f; return; }
        if (InPuddle(puddle))
        {
            Current = Mode.Splashing;
            jumpsLeft = Random.Range(3, 6);
            nextJump = now + 0.25f;
            brain.Food = null;
            brain.StayPut = true;
            body.Brace = true;
            return;
        }
        if (!GoTo(new Vector2(puddle.X, puddle.Y + body.size * 0.3f), puddle.Y, now)) { Stop(); nextSplash = now + 10f; }
    }

    void UpdateSplash(Rain rain, float now)
    {
        brain.Food = null;
        brain.StayPut = true;
        brain.Linger(1f);
        if (PuddleGone(rain)) { Done(now); return; }
        if (!body.Grounded || now < nextJump) return;
        if (jumpsLeft <= 0) { Done(now); return; }
        // up and down in it (a little towards the middle): every landing splashes (Rain)
        float drift = Mathf.Clamp((puddle.X - body.Center.x) * 2f + Random.Range(-0.5f, 0.5f), -1.2f, 1.2f) * Mathf.Sqrt(S);
        float h = Random.Range(0.32f, 0.55f) * S;
        body.Hop(new Vector2(drift, GummyBody.JumpSpeed(h, body.gravity, body.Drag)) - body.Velocity, 0.4f);
        jumpsLeft--;
        Jumps++;
        nextJump = now + 0.45f + Random.value * 0.25f;
        wet = Mathf.Min(1f, wet + 0.08f);
        if (Random.value < 0.22f) Say(Line("SPLOOSH!", "SPLASH!", "SPLISH SPLASH", "PUDDLE POWER!", "STOMP! SPLASH!", "QUACK QUACK!"), false);
    }

    void Done(float now)
    {
        Sessions++;
        if (progress) progress.AddXp(1, body.Center);
        if (brain) brain.Treat();
        if (Random.value < 0.4f) Say(Line("SO MUCH FUN!", "AGAIN! AGAIN!", "SPLASH-PRESSO!", "MY BUN IS SOAKED. WORTH IT", "RAWR! BEST PUDDLE!", "MORE PUDDLES!"), false);
        Stop();
        nextSplash = now + (IsDuck ? Random.Range(4f, 10f) : Random.Range(10f, 25f));
    }

    // ------------------------------------------------------------------ dripping, shaking it off

    void Drip(Rain rain, float dt)
    {
        if (wet < 0.08f || body.Hiding) { dripAcc = 0f; return; }
        dripAcc += dt * (0.4f + wet * 4f);
        while (dripAcc >= 1f)
        {
            dripAcc -= 1f;
            Vector2 at = body.OutlinePoint(180f + Random.Range(-50f, 50f), out _);
            rain.Spray(at, new Vector2(Random.Range(-0.15f, 0.15f), -0.3f), Random.Range(0.02f, 0.03f));
        }
    }

    void Shake(Mode back, float now)
    {
        Current = Mode.Shaking;
        afterShake = back;
        shakeT = 0f;
        sprayAcc = 0f;
        sprayed = 0;
        Shakes++;
        brain.Linger(1.5f);
        if (shakeClip && sfx) { sfx.pitch = Random.Range(0.95f, 1.1f) / Mathf.Sqrt(S); sfx.PlayOneShot(shakeClip, volume); }
        if (Random.value < 0.5f) Say(Line("BRRRR!", "SHAKE SHAKE!", "BRRR-EW!", "BRRR!", "RAWR-RR-RR!", "QUACK-SHAKE!"), false);
    }

    void UpdateShake(Rain rain, float now, float dt)
    {
        brain.Linger(0.5f);
        if (afterShake == Mode.Sheltered) brain.StayPut = true;
        shakeT += dt;
        const float T = 0.8f;
        float env = Mathf.Sin(Mathf.PI * Mathf.Clamp01(shakeT / T));
        float wob = Mathf.Sin(shakeT * Mathf.PI * 2f * 8.5f);
        body.Lean = 16f * env * wob;
        body.Pose = new Vector2(1f + 0.06f * env * wob, 1f - 0.06f * env * wob);
        // water flies off its sides
        sprayAcc += dt * 34f * env * Mathf.Max(wet, 0.3f);
        while (sprayAcc >= 1f)
        {
            sprayAcc -= 1f;
            float side = (sprayed++ % 2 == 0) ? 1f : -1f;
            Vector2 at = body.Center + new Vector2(side * HalfW * 0.9f, Random.Range(-0.15f, 0.3f) * Height);
            rain.Spray(at, new Vector2(side * Random.Range(1.8f, 3.8f), Random.Range(0.4f, 2.4f)), Random.Range(0.025f, 0.045f));
        }
        if (shakeT < T) return;
        body.Lean = 0f;
        body.Pose = Vector2.one;
        wet *= 0.1f;
        Current = afterShake;
        if (Current == Mode.Sheltered) brain.StayPut = true;
    }

    // ------------------------------------------------------------------ helpers

    // the route planner gets it there (false = it can't)
    bool GoTo(Vector2 target, float surfaceY, float now)
    {
        bool settled = body.Grounded && !body.Held && !body.IsStuck && (brain.Current == GummyBrain.State.Idle || brain.Current == GummyBrain.State.Hopping);
        if (now >= nextPlan && settled)
        {
            nextPlan = now + 1f;
            if (brain.PlanTo(target, surfaceY, out _)) planFails = 0;
            else if (++planFails >= 2) { planFails = 0; return false; }
        }
        brain.Food = target;
        brain.FoodSurface = surfaceY;
        return true;
    }

    // whatever it was doing about the rain: over (out from behind a window, its claim on a puddle let go)
    void Stop()
    {
        if (brain)
        {
            brain.StayPut = false;
            if (Current == Mode.Seek || Current == Mode.ToPuddle) brain.Food = null;
        }
        Unhurry();
        if (behindWin != 0 && body)
        {
            if (speech) speech.Suppress(BuddySpeech.Topic.Escape, 1.5f); // (squeezing out from behind the window is part of it)
            body.HideRect = default;
            body.HideWindow = 0;
            if (body.Grounded && !body.Held && Current == Mode.Sheltered) body.Hop(new Vector2(0f, 3.5f * Mathf.Sqrt(S)), 0.6f); // (squeezes out)
        }
        behindWin = 0;
        if (puddle != null) { puddle.Claims = Mathf.Max(0, puddle.Claims - 1); puddle = null; }
        if (body && (Current == Mode.Splashing || Current == Mode.Shaking)) { body.Brace = false; body.Lean = 0f; body.Pose = Vector2.one; }
        Current = Mode.None;
    }

    void Unhurry()
    {
        if (hurried && brain) brain.Hurry = 1f;
        hurried = false;
    }

    bool BusyElsewhere()
    {
        return (hoops && (hoops.Busy || hoops.Playing)) || (pit && pit.Busy) || (slide && slide.Busy) || (laser && laser.Playing)
            || (hideSeek && hideSeek.InGame) || (duck && duck.Arriving) || BuddyTower.Has(body) || LevelUpParty.Current
            || brain.Towering || brain.InPit || (FetchBall.Current && FetchBall.Current.InPlay) || (BuddyChat.Open && BuddyChat.Target == body);
    }

    void Say(string line, bool force)
    {
        if (speech && line != null) speech.Say(line, 0f, force);
    }

    string Line(string apple, string tomato, string bean, string burger, string dino, string duckLine)
    {
        if (!speech) return apple;
        switch (speech.voice)
        {
            case BuddySpeech.Voice.Tomato: return tomato;
            case BuddySpeech.Voice.Bean: return bean;
            case BuddySpeech.Voice.Burger: return burger;
            case BuddySpeech.Voice.Dino: return dino;
            case BuddySpeech.Voice.Duck: return duckLine;
            default: return apple;
        }
    }

    static string Pick(params string[] l) => l[Random.Range(0, l.Length)];

    /// <summary>The rainbow's out: the buddy nearest to it that's awake (and not hidden) says so; another one agrees.</summary>
    public static void CheerRainbow(Vector2 at)
    {
        BuddyRain first = null, second = null;
        float d1 = float.MaxValue, d2 = float.MaxValue;
        foreach (var o in s_all)
        {
            if (!o || !o.isActiveAndEnabled || !o.speech || o.body.Hiding || (o.brain && o.brain.Current == GummyBrain.State.Asleep)) continue;
            float d = (o.body.Center - at).sqrMagnitude;
            if (d < d1) { second = first; d2 = d1; first = o; d1 = d; }
            else if (d < d2) { second = o; d2 = d; }
        }
        if (first) first.Say(first.Line("A RAINBOW!", "OOOH, A RAINBOW!", "RAINBOW ROAST?!", "PRETTY...", "RAWR! COLOURS!", "RAINBOW! QUACK!"), true);
        if (second && second.speech) second.speech.Say(second.Line("WOW!", "SO PRETTY!", "MAKE A WISH!", "LIKE A BIG SMILE", "RAWR!", "QUACK!!"), 0f, false);
    }
}
