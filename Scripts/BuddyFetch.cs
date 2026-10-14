using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Fetch (FetchBall): when you throw the ball, every buddy that's awake and not busy races for it (the route planner gets
/// it there, skateboard, rope and all). The first to touch it picks it up in its mouth and brings it back to the spot
/// under your cursor, then tosses it up to you: XP for the winner, a line from the others. It doesn't fetch while it's
/// going after a snack (snacks come first), and it gives up if it can't get there.
/// </summary>
[DefaultExecutionOrder(5)] // after BuddyEater has set (or cleared) its snack: the ball only when there's no snack
[RequireComponent(typeof(GummyBody))]
[DisallowMultipleComponent]
public class BuddyFetch : MonoBehaviour
{
    [Tooltip("XP for bringing the ball back.")]
    public int xpFetch = 4;
    [Tooltip("Gives up chasing (or bringing it back) after this long.")]
    public float giveUpAfter = 20f;

    /// <summary>Has the ball in its mouth.</summary>
    public bool Carrying => ball && ball.Carrier == body;

    static readonly List<BuddyFetch> s_all = new List<BuddyFetch>();
    GummyBody body;
    GummyBrain brain;
    BuddyEater eater;
    BuddyProgress progress;
    BuddySpeech speech;
    FetchBall ball;
    float chaseSince = -1f, carrySince;
    bool racing;

    void Awake()
    {
        body = GetComponent<GummyBody>();
        brain = GetComponent<GummyBrain>();
        eater = GetComponent<BuddyEater>();
        progress = GetComponent<BuddyProgress>();
        speech = GetComponent<BuddySpeech>();
    }

    void OnEnable()
    {
        FetchBall.Thrown += OnThrown;
        if (!s_all.Contains(this)) s_all.Add(this);
    }

    void OnDisable()
    {
        FetchBall.Thrown -= OnThrown;
        s_all.Remove(this);
        if (Carrying) ball.Toss(Vector2.zero, false);
        racing = false;
    }

    void OnThrown(FetchBall b)
    {
        ball = b;
        racing = false;
        chaseSince = -1f;
        if (!brain || brain.Current == GummyBrain.State.Asleep && (b.Position - body.Center).magnitude > 4f) return; // sleeps through it (unless it's close)
        if (brain.Current == GummyBrain.State.Asleep) brain.WakeUp();
        racing = true;
        chaseSince = Time.time;
        if (speech && Random.value < 0.45f) speech.Say(Line(Pick("BALL!", "I GOT IT!", "MINE!"), Pick("MINE MINE MINE", "BALL!!", "OUTTA MY WAY"), Pick("FETCH MODE!", "BALL! ZOOM!", "MINE!")), 0f, false);
    }

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("BuddyFetch.Update", t0); }
    void UpdateTimed()
    {
        if (!brain) return;
        if (!ball) ball = FetchBall.Current;
        if (!ball) { racing = false; return; }
        float now = Time.time;

        if (Carrying)
        {
            // bringing it back: to the spot under your cursor, then a toss up to you
            Vector2 cur = Grabber.CursorWorld;
            bool there = Mathf.Abs(body.Center.x - cur.x) < 0.9f * Mathf.Max(body.size / 0.6f, 0.7f) && body.Grounded;
            if (there || now - carrySince > giveUpAfter) { Deliver(cur, there); return; }
            if (eater && eater.Target) return; // a snack on the way: that first
            if (BuddyRoute.SurfaceUnder(cur, body.size * 0.8f, body.solidWindows, out float y))
            {
                brain.Food = new Vector2(cur.x, y + body.size * 0.3f);
                brain.FoodSurface = y;
            }
            return;
        }

        if (!racing || !ball.InPlay || ball.Held) { racing = false; return; }
        if (ball.Carrier) { Lost(); return; } // someone else got it
        if (eater && eater.Target) return;    // a snack: that first (the ball can wait)
        if (now - chaseSince > giveUpAfter || brain.Current == GummyBrain.State.Asleep) { racing = false; return; }

        // mouth on it: picked up
        if ((ball.Position - body.Center).magnitude < body.size * 0.5f + ball.radius && ball.PickUp(body))
        {
            carrySince = now;
            racing = false;
            if (speech && Random.value < 0.6f) speech.Say(Line("GOT IT!", "HA! MINE!", "GOT THE BEAN-BALL!"), 0f, false);
            foreach (var f in s_all) if (f != this) f.Lost();
            return;
        }
        // after it: where it is (or where it'll land)
        Vector2 at = ball.Position;
        float sy = ball.SurfaceY;
        if (!ball.Resting && !BuddyRoute.Landing(ball.Position, ball.Velocity, ball.radius, ball.gravity, out at, out sy)) return;
        brain.Food = at;
        brain.FoodSurface = sy;
    }

    void Deliver(Vector2 cursor, bool there)
    {
        // a little toss up towards your cursor (it lands at your feet if you don't catch it)
        Vector2 d = cursor - ball.Position;
        float up = Mathf.Clamp(d.y, 0.6f, 3f);
        float vy = Mathf.Sqrt(2f * ball.gravity * up);
        float t = vy / ball.gravity * 1.6f;
        ball.Toss(new Vector2(Mathf.Clamp(d.x / Mathf.Max(t, 0.3f), -4f, 4f), vy), true);
        brain.Food = null;
        if (there && progress) progress.AddXp(xpFetch, body.Center);
        if (there) brain.Treat(); // happy (heart)
        if (speech) speech.Say(there ? Line(Pick("AGAIN! AGAIN!", "THROW IT!", "HERE YOU GO!"), Pick("BEAT THAT!", "AGAIN!", "TOO EASY"), Pick("AGAIN! FASTER!", "ESPRESSO FETCH!", "THROW IT!")) : Line("HERE!", "CATCH!", "HERE!"), 0f, true);
    }

    // someone else got it: off the chase
    void Lost()
    {
        if (!racing) return;
        racing = false;
        brain.Food = null;
        if (speech && Random.value < 0.35f) speech.Say(Line(Pick("AWW", "NEXT TIME!", "NO FAIR!"), Pick("I LET YOU WIN", "CHEATER!", "HMPH"), Pick("TOO SLOW...", "NEEDED COFFEE", "NO FAIR!")), 0f, false);
    }

    string Line(string apple, string tomato, string bean) => !speech ? apple : speech.voice == BuddySpeech.Voice.Tomato ? tomato : speech.voice == BuddySpeech.Voice.Bean ? bean : apple;
    static string Pick(params string[] l) => l[Random.Range(0, l.Length)];
}
