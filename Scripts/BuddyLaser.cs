using UnityEngine;

/// <summary>
/// Chases the laser pointer's red dot like a cat (LaserPointer): spots it ("A DOT!"), runs and hops after it wherever it
/// goes (the route planner gets it there: under it on the taskbar, up onto the window it's on), and when it's close it
/// crouches, wiggles and pounces. Paws on it = caught (a little XP now and then)... and then it's gone again. The dot beats
/// snacks, the ball and cake; it ignores it while it's carrying the ball, hiding, or being held. Laser off: "WHERE'D IT GO?"
/// </summary>
[DefaultExecutionOrder(7)] // after BuddyEater, BuddyFetch and BuddyParty have set where it's going: the dot wins
[RequireComponent(typeof(GummyBody))]
[DisallowMultipleComponent]
public class BuddyLaser : MonoBehaviour
{
    [Tooltip("Notices the dot within this far (world units); a sleeping buddy only wakes up for it much closer.")]
    public float noticeRange = 10f;
    [Tooltip("Pounces from this close (x size / 0.6).")]
    public float pounceReach = 2.2f;
    [Tooltip("Zoomies: how much quicker it hops after the dot than after a snack.")]
    public float hurry = 1.7f;
    [Tooltip("XP for catching it (at most once every xpEvery seconds).")]
    public int xpCatch = 1;
    public float xpEvery = 10f;

    /// <summary>After the dot right now.</summary>
    public bool Playing => playing;
    /// <summary>Times it got its paws on the dot (this session).</summary>
    public int Catches { get; private set; }
    /// <summary>Pounces so far (this session).</summary>
    public int Pounces { get; private set; }

    GummyBody body;
    GummyBrain brain;
    BuddySpeech speech;
    BuddyProgress progress;
    BuddyFetch fetch;
    bool playing;
    float nextPounce, lastCatch = -100f, lastXp = -100f, lastLine = -100f, onSince;

    float S => body.size / 0.6f;

    void Awake()
    {
        body = GetComponent<GummyBody>();
        brain = GetComponent<GummyBrain>();
        speech = GetComponent<BuddySpeech>();
        progress = GetComponent<BuddyProgress>();
        fetch = GetComponent<BuddyFetch>();
    }

    void OnEnable() => LaserPointer.Toggled += OnToggled;
    void OnDisable()
    {
        LaserPointer.Toggled -= OnToggled;
        if (playing && brain) { brain.Food = null; brain.Hurry = 1f; }
        playing = false;
    }

    void OnToggled(bool on)
    {
        if (on) { onSince = Time.time; return; }
        if (!playing) return;
        playing = false;
        if (brain) { brain.Food = null; brain.Hurry = 1f; }
        Say(Line(Pick("WHERE'D IT GO?", "COME BACK!", "DOT?"), Pick("NOOO, MY DOT!", "IT RAN AWAY", "DOT??"), Pick("...DOT?", "SIGNAL LOST", "WHERE IS IT")), 0.5f);
    }

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("BuddyLaser.Update", t0); }
    void UpdateTimed()
    {
        if (!brain || !LaserPointer.On) { if (playing && brain) brain.Hurry = 1f; playing = false; return; }
        float now = Time.time;
        Vector2 dot = LaserPointer.Dot;
        float d = (dot - body.Center).magnitude;
        bool asleep = brain.Current == GummyBrain.State.Asleep;
        if (!playing)
        {
            if (fetch && fetch.Carrying) return;
            if (BuddyHideSeek.Playing || body.Hiding) return;
            if (asleep && d > 3f * S) return; // sleeps through it unless it's right there
            if (d > noticeRange) return;
            if (asleep) brain.WakeUp();
            playing = true;
            nextPounce = now + Random.Range(0.4f, 1f);
            Say(Line(Pick("A DOT!", "WHAT IS THAT?!", "OOH!"), Pick("RED THING!!", "IT'S MINE!", "GET IT!"), Pick("DOT DETECTED", "MUST. CATCH.", "TARGET!")), 0f);
        }
        brain.Hurry = 1f;
        if (fetch && fetch.Carrying) return;
        if (body.Held || BuddyHideSeek.Playing) return;
        brain.Linger(0.5f); // stays with it (no strolls, no naps, no adventures)

        // paws on it: caught! (a little XP now and then)
        if (d < body.size * 0.5f && now - lastCatch > 1.2f)
        {
            lastCatch = now;
            Catches++;
            if (progress && xpCatch > 0 && now - lastXp > xpEvery) { lastXp = now; progress.AddXp(xpCatch, body.Center); }
            if (Random.value < 0.5f) Say(Line(Pick("GOT IT!", "MINE!", "HA!"), Pick("GOTCHA!", "CAUGHT IT!", "TOO EASY"), Pick("CAPTURED.", "GOT IT!", "BEAN WINS")), 0f);
        }

        if (brain.Pouncing) { brain.PounceAt = dot; brain.Food = null; return; } // (crouched: watching it)
        if (!body.Grounded) { brain.Food = null; return; } // (in the air: lands first)

        // where the dot is: the top of whatever's under it
        if (!BuddyRoute.SurfaceUnder(dot, Mathf.Max(body.Extent.height, body.size * 0.7f), body.solidWindows, out float y)) { brain.Food = null; return; }
        Vector2 rel = dot - body.Center;
        float feet = body.Extent.yMin, above = dot.y - feet;
        bool sameLevel = Mathf.Abs(y - feet) < 0.2f;
        bool close = Mathf.Abs(rel.x) < pounceReach * S && above > -0.3f && above < 3.2f * S;
        bool slow = LaserPointer.DotVelocity.magnitude < 7f;
        if (sameLevel && close)
        {
            // close: crouch, wiggle, pounce (not every moment: it watches a bit first)
            brain.Food = null;
            if (slow && now > nextPounce && brain.Pounce(dot, Random.Range(0.3f, 0.65f)))
            {
                Pounces++;
                nextPounce = now + Random.Range(0.9f, 1.8f);
                if (Random.value < 0.25f) Say(Line(Pick("POUNCE!", "HUP!", "GOTCHA!"), Pick("HYAA!", "MINE!", "POUNCE!"), Pick("HYAAA!", "ENGAGE!", "POUNCE!")), 0f);
            }
            return;
        }
        // further: after it (to the spot under it), zoomies
        brain.Hurry = hurry;
        brain.Food = new Vector2(dot.x, y + body.size * 0.3f);
        brain.FoodSurface = y;
        if (brain.Stuck) brain.Stuck = false; // (it keeps moving: worth another try)
    }

    void Say(string line, float delay)
    {
        float now = Time.time;
        if (!speech || now - lastLine < 4f) return;
        if (speech.Say(line, delay, false)) lastLine = now;
    }

    string Line(string apple, string tomato, string bean) => !speech ? apple : speech.voice == BuddySpeech.Voice.Tomato ? tomato : speech.voice == BuddySpeech.Voice.Bean ? bean : apple;
    static string Pick(params string[] l) => l[Random.Range(0, l.Length)];
}
