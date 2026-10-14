using UnityEngine;

/// <summary>
/// They learn to dodge you: a buddy remembers how you grab it (how often, and whether you swoop in from above or from the
/// side) and how hard you throw it, and when your cursor swoops at it fast it may hop out of the way, sidestepping a
/// grab from above or jumping over one from the side. Better every level (from level 3: sooner, more often, quicker hops).
/// Slow cursor = no dodging (petting is always fine); happy, asleep, busy or carrying something = no dodging; after a few
/// dodges in a row it lets you have it. Chat "stop dodging" to make it behave for a while, "dodge" to make it try.
/// </summary>
[RequireComponent(typeof(GummyBody))]
[DisallowMultipleComponent]
public class BuddyDodge : MonoBehaviour
{
    [Tooltip("Dodges from this level on.")]
    public int fromLevel = 3;
    [Tooltip("A cursor coming at it faster than this is a grab coming (world units / s).")]
    public float swoopSpeed = 6f;
    [Tooltip("At most this many dodges in a row (within 15 s), then it lets you catch it.")]
    public int maxInARow = 3;

    /// <summary>How wary of your grabs it is right now (0..1): goes up when you grab / throw it, slowly back down.</summary>
    public float Wariness => wary;
    /// <summary>Dodges so far (since it woke up in this session).</summary>
    public int Dodges { get; private set; }

    GummyBody body;
    GummyBrain brain;
    BuddySkills skills;
    BuddySpeech speech;
    BuddyProgress progress;
    BuddyFetch fetch;
    Vector2 lastCursor, cursorVel;
    float wary = 0.2f, fromAbove = 0.5f, cooldown, behaveUntil = -1f, tryUntil = -1f, lastLine = -100f;
    readonly float[] recent = new float[6];
    int recentIdx;
    bool armed = true, wasHeld;
    int grabsThisMinute;
    float minuteStart;

    void Awake()
    {
        body = GetComponent<GummyBody>();
        brain = GetComponent<GummyBrain>();
        skills = GetComponent<BuddySkills>();
        speech = GetComponent<BuddySpeech>();
        progress = GetComponent<BuddyProgress>();
        fetch = GetComponent<BuddyFetch>();
        lastCursor = Grabber.CursorWorld;
        for (int i = 0; i < recent.Length; i++) recent[i] = -100f;
    }

    /// <summary>Behave (no dodging) for this long (chat "stop dodging").</summary>
    public void Behave(float seconds) { behaveUntil = Time.time + seconds; tryUntil = -1f; }
    /// <summary>Try to dodge everything for this long (chat "dodge me").</summary>
    public void Tease(float seconds) { tryUntil = Time.time + seconds; behaveUntil = -1f; }

    int Level => skills ? skills.Level : progress ? progress.Level : 1;
    // 0 at fromLevel - 1, 1 by level 14: how soon it reacts, how often, how quick the hop
    float Skill => Mathf.Clamp01((Level - fromLevel + 1) / (14f - fromLevel + 1f));

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("BuddyDodge.Update", t0); }
    void UpdateTimed()
    {
        float now = Time.time, dt = Mathf.Max(Time.deltaTime, 1e-4f);
        Vector2 cur = Grabber.CursorWorld;
        Vector2 v = (cur - lastCursor) / dt;
        if (v.sqrMagnitude > 900f) v = Vector2.zero; // (the cursor jumped: another screen, or warped)
        cursorVel = Vector2.Lerp(cursorVel, v, 1f - Mathf.Exp(-25f * dt));
        lastCursor = cur;

        // remembering: grabbed (how it came at it), thrown hard
        bool held = body.Held;
        if (held && !wasHeld) OnGrabbed(now);
        if (!held && wasHeld && body.Velocity.magnitude > 9f) wary = Mathf.Min(1f, wary + 0.15f);
        wasHeld = held;
        wary = Mathf.MoveTowards(wary, 0.15f, dt * 0.01f); // forgets slowly (a minute or two)

        if (cooldown > 0f) cooldown -= dt;
        Vector2 rel = body.Center - cur;
        float d = rel.magnitude, speed = cursorVel.magnitude;
        if (speed < swoopSpeed * 0.5f || d > 4f) armed = true; // (one try per swoop)
        if (!armed || cooldown > 0f || Level < fromLevel || now < behaveUntil) return;
        if (!brain || brain.Happy || brain.Dizzy || brain.Current == GummyBrain.State.Asleep || body.Held || body.Hovered && speed < swoopSpeed) return;
        if (fetch && fetch.Carrying) return;
        if (DesktopInput.LeftButtonHeld && !body.Hovered) return; // dragging something else around

        // a swoop: fast, at it, about to arrive
        float closing = d > 1e-3f ? Vector2.Dot(cursorVel, rel / d) : 0f;
        float skill = Skill, lead = 0.12f + 0.22f * skill;
        if (speed < swoopSpeed || closing < speed * 0.75f || d > closing * lead + body.size * 0.7f) return;
        armed = false;

        // dodge? (more when it's wary, annoyed or playing; less when it already dodged a lot)
        int inARow = 0;
        foreach (float t in recent) if (now - t < 15f) inARow++;
        if (inARow >= maxInARow)
        {
            if (now - lastLine > 6f) Say(Line("OK OK, YOU GOT ME", "FINE. CATCH ME", "OK, TIRED NOW"), now);
            return;
        }
        float chance = skill * (0.25f + 0.75f * wary) * (brain.Annoyed ? 1.4f : 1f);
        if (now < tryUntil) chance = Mathf.Max(chance, 0.5f + 0.4f * skill);
        if (Random.value > Mathf.Min(chance, 0.85f)) return;
        if (!Hop(cur, skill)) return;
        Dodges++;
        recent[recentIdx] = now;
        recentIdx = (recentIdx + 1) % recent.Length;
        cooldown = 1.2f - 0.5f * skill;
        if (progress && Dodges % 3 == 1) progress.AddXp(1, body.Center);
        if (Random.value < 0.5f) Say(Line(Pick("MISSED ME!", "TOO SLOW!", "NOPE!"), Pick("NOPE.", "NICE TRY", "AS IF"), Pick("TOO FAST FOR U", "DECAF HANDS?", "WHOOSH!")), now);
    }

    // out of the way: from above = a sidestep away from the cursor; from the side = a jump up (the cursor goes under it)
    bool Hop(Vector2 cur, float skill)
    {
        float S = Mathf.Sqrt(body.size / 0.6f);
        bool above = cursorVel.y < -Mathf.Abs(cursorVel.x) * 0.7f;
        // it learned which way you usually come from: leans that way of dodging too
        if (Random.value < Mathf.Abs(fromAbove - 0.5f)) above = fromAbove > 0.5f;
        float away = body.Center.x >= cur.x ? 1f : -1f;
        if (Mathf.Abs(cursorVel.x) > 0.5f && !above) away = Mathf.Sign(cursorVel.x); // (on the way the hand is going: it jumps over it)
        // room to hop that way? (screen edge, end of the window it's on)
        Rect r = ScreenBounds.WorldRect;
        Vector2 span = body.SupportSpan;
        float lo = Mathf.Max(r.xMin, span.x) + body.size * 0.5f, hi = Mathf.Min(r.xMax, span.y) - body.size * 0.5f;
        float reach = (1f + skill) * body.size;
        if (away > 0f && body.Center.x + reach > hi) away = -1f;
        else if (away < 0f && body.Center.x - reach < lo) away = 1f;
        float side = above ? (2.6f + 2.2f * skill) * S : (1.2f + 1f * skill) * S;
        float up = above ? GummyBody.JumpSpeed(0.25f * body.size / 0.6f, body.gravity, body.Drag) : GummyBody.JumpSpeed((0.7f + 0.5f * skill) * body.size / 0.6f + 0.2f, body.gravity, body.Drag);
        return brain.Dodge(away, side, up);
    }

    void OnGrabbed(float now)
    {
        // how it came at it, just now
        bool above = cursorVel.y < -Mathf.Abs(cursorVel.x) * 0.7f;
        if (cursorVel.magnitude > 2f) fromAbove = Mathf.Lerp(fromAbove, above ? 1f : 0f, 0.3f);
        wary = Mathf.Min(1f, wary + 0.2f);
        if (now - minuteStart > 60f) { minuteStart = now; grabsThisMinute = 0; }
        grabsThisMinute++;
        if (grabsThisMinute == 4 && Level >= fromLevel) Say(Line(Pick("I'M ONTO YOU", "NOT AGAIN!", "STOP GRABBING!"), Pick("HANDS OFF.", "I'M WATCHING YOU", "AGAIN?!"), Pick("TOO MUCH GRAB", "I SEE YOUR HAND", "BREW-TAL")), now, true);
    }

    void Say(string line, float now, bool force = false)
    {
        if (!speech) return;
        if (speech.Say(line, 0f, force)) lastLine = now;
    }

    string Line(string apple, string tomato, string bean) => !speech ? apple : speech.voice == BuddySpeech.Voice.Tomato ? tomato : speech.voice == BuddySpeech.Voice.Bean ? bean : apple;
    static string Pick(params string[] l) => l[Random.Range(0, l.Length)];
}
