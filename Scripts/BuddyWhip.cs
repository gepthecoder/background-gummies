using UnityEngine;

/// <summary>
/// The apple gets the whip (the parked Whip in the scene) at unlockLevel and carries it at its side. It cracks it:
/// at snacks lying on a window just above it (the lash knocks them down to it), when it tags the tomato,
/// at your cursor when you've gone idle ("BACK TO WORK!"), now and then just for fun, and when you ask in the chat.
/// Idle = no mouse or keyboard input anywhere for a while (only the time is read, never what you type).
/// </summary>
[DefaultExecutionOrder(-5)] // hand position before the Whip simulates this frame
[RequireComponent(typeof(GummyBody))]
[DisallowMultipleComponent]
public class BuddyWhip : MonoBehaviour
{
    [Tooltip("The parked whip in the scene (WhipIt > Setup assigns it).")]
    public Whip whip;
    [Tooltip("Gets the whip at this level (the red apple).")]
    public int unlockLevel = 10;
    [Tooltip("Whip size relative to the original mouse whip (x size / 0.6).")]
    public float whipSize = 0.32f;

    [Header("When it cracks it")]
    [Tooltip("You haven't touched mouse or keyboard for this long: it comes over and cracks it at your cursor (seconds, 0 = never).")]
    public float idleCrackAfter = 30f;
    [Tooltip("Now and then, just for fun: about every this many seconds (while you're around).")]
    public float flourishEvery = 50f;
    [Tooltip("Whips down snacks on a window above it within this reach (world units).")]
    public float snackReach = 1.6f;
    [Range(0f, 1f)] public float volume = 0.8f;

    public bool Has { get; private set; }

    GummyBody body;
    GummyBrain brain;
    BuddySpeech speech;
    BuddyProgress progress;
    BuddyEater eater;
    BuddyRope rope;
    BuddyLayer layer;
    int facing = 1;
    float hand = 1f; // -1..1: which side the hand is on (slides across when it turns)
    float faceUntil, nextFlourish, lastCrack = -10f, idleCrackAt = -1f, lastSnackTry = -10f;
    bool idleDone, hiddenByUs;
    Snack fetch;
    Vector2 idleTarget;

    void Awake()
    {
        body = GetComponent<GummyBody>();
        brain = GetComponent<GummyBrain>();
        speech = GetComponent<BuddySpeech>();
        progress = GetComponent<BuddyProgress>();
        eater = GetComponent<BuddyEater>();
        rope = GetComponent<BuddyRope>();
        layer = GetComponent<BuddyLayer>();
        nextFlourish = Time.time + flourishEvery;
    }

    void OnEnable()
    {
        if (progress) progress.LeveledUp += OnLevelUp;
        BuddySocial.Tagged += OnTagged;
        if (whip) whip.Cracked += OnCracked;
    }

    void OnDisable()
    {
        if (progress) progress.LeveledUp -= OnLevelUp;
        BuddySocial.Tagged -= OnTagged;
        if (whip) { whip.Cracked -= OnCracked; whip.Held = false; whip.Covered = false; }
    }

    void Start()
    {
        if (progress && progress.Level >= unlockLevel) Give(false);
    }

    void OnLevelUp(int level, bool newStage)
    {
        if (!Has && level >= unlockLevel) Give(true);
    }

    /// <summary>Hand it the whip (the menu and a level-up call this).</summary>
    public void Give(bool announce)
    {
        if (Has || !whip) return;
        Has = true;
        whip.size = whipSize * body.size / 0.6f;
        whip.restAngle = 70f;
        whip.Held = true;
        whip.HoldAt = Hand();
        whip.HoldFacing = facing;
        if (!whip.gameObject.activeSelf) whip.gameObject.SetActive(true);
        whip.Refresh();
        whip.Visible = true;
        if (announce)
        {
            Invoke(nameof(Announce), 1.4f); // after its level-up line
            Invoke(nameof(FirstCrack), 3f);
        }
    }

    void Announce() => Say("OOH, A WHIP!");
    void FirstCrack() => Crack(null, "YEEHAW!");

    /// <summary>Crack it now (towards a point, or the way it's facing). False if it has no whip or can't right now.</summary>
    public bool Crack(Vector2? at = null, string line = null)
    {
        if (!Has || body.Hiding || (rope && rope.Current != BuddyRope.Mode.Off) || Time.time - lastCrack < 0.7f) return false;
        if (brain && brain.Current == GummyBrain.State.Asleep) return false;
        if (at.HasValue) { facing = at.Value.x >= body.Center.x ? 1 : -1; faceUntil = Time.time + 1f; }
        whip.HoldFacing = facing;
        whip.Strike();
        lastCrack = Time.time;
        if (line != null) Say(line);
        return true;
    }

    // ------------------------------------------------------------------ every frame

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("BuddyWhip.Update", t0); }
    void UpdateTimed()
    {
        if (!Has || !whip) { if (whip && whip.Covered) whip.Covered = false; return; }
        float now = Time.time;

        // faces the cursor (or what it's about to whip)
        if (now > faceUntil)
        {
            float dx = Grabber.CursorWorld.x - body.Center.x;
            if (Mathf.Abs(dx) > 0.3f) facing = dx > 0f ? 1 : -1;
        }
        whip.Held = true;
        whip.HoldAt = Hand();
        whip.HoldFacing = facing;

        // hiding (hide and seek): the whip would give it away; swinging: the whip is its rope (BuddyRope draws it)
        bool away = body.Hiding || (rope && rope.Current != BuddyRope.Mode.Off);
        whip.Covered = layer && layer.Hides(whip.HoldAt); // behind a window in front of it: not drawn, still works
        if (away && whip.Visible) { whip.Visible = false; hiddenByUs = true; }
        else if (!away && hiddenByUs) { whip.Visible = true; hiddenByUs = false; }
        if (away || !brain) return;

        IdleCheck(now);
        SnackCheck(now);

        if (now > nextFlourish)
        {
            nextFlourish = now + flourishEvery * Random.Range(0.6f, 1.4f);
            bool around = (Grabber.CursorWorld - body.Center).magnitude < 8f;
            if (around && brain.IsFree && !BuddyChat.Open && !BuddyHideSeek.Playing && Random.value < 0.5f) Crack(null, Random.value < 0.5f ? "YEEHAW!" : "HUP!");
        }
    }

    // the whip hand: at its side, a bit below the middle
    Vector2 Hand()
    {
        Rect e = body.Extent;
        float w = e.width > 0.01f ? e.width : body.size;
        hand = Mathf.MoveTowards(hand, facing, 8f * Time.deltaTime);
        return body.Center + new Vector2(hand * 0.42f * w, -0.08f * w);
    }

    // you went quiet: it hops over and cracks it at your cursor, once per quiet stretch
    void IdleCheck(float now)
    {
        if (idleCrackAfter <= 0f) return;
        float idle = DesktopInput.IdleSeconds;
        if (idle < 2f) { idleDone = false; idleCrackAt = -1f; return; }
        if (idleDone || idle < idleCrackAfter) return;
        if (idleCrackAt < 0f)
        {
            if (brain.Current == GummyBrain.State.Asleep) brain.WakeUp();
            if (!brain.IsFree) return;
            idleTarget = Grabber.CursorWorld;
            Rect scr = ScreenBounds.WorldRect;
            int side = idleTarget.x >= body.Center.x ? 1 : -1;
            float x = Mathf.Clamp(idleTarget.x - side * 1.1f, scr.xMin + 0.5f, scr.xMax - 0.5f);
            if (Mathf.Abs(x - body.Center.x) > 0.5f && Mathf.Abs(x - body.Center.x) < 8f) brain.Visit(x);
            idleCrackAt = now + 3f; // cracks when it gets there (or after 3 s)
            return;
        }
        bool there = brain.Current == GummyBrain.State.Idle && Mathf.Abs(body.Center.x - (idleTarget.x - Mathf.Sign(idleTarget.x - body.Center.x) * 1.1f)) < 0.6f;
        if (now > idleCrackAt || there)
        {
            idleDone = true;
            Crack(idleTarget, Random.value < 0.5f ? "BACK TO WORK!" : Random.value < 0.5f ? "HEY! WAKE UP!" : "HELLOOO?");
        }
    }

    // a snack on a window just above: whip it down
    void SnackCheck(float now)
    {
        if (!eater || now - lastSnackTry < 3f || !brain.IsFree && brain.Current != GummyBrain.State.Hopping) return;
        Snack s = eater.Target;
        if (!s || !s.Resting) return;
        Vector2 d = s.Position - body.Center;
        float above = s.SurfaceY - body.Extent.yMin;
        if (above < 0.3f || above > 1.4f || Mathf.Abs(d.x) > snackReach || Mathf.Abs(d.x) < 0.3f) return;
        lastSnackTry = now;
        fetch = s;
        Crack(s.Position, Random.value < 0.5f ? "GOTCHA!" : null);
    }

    void OnCracked(Vector2 tip, Vector2 tipVel)
    {
        if (!fetch) return;
        Snack s = fetch;
        fetch = null;
        if (!s || s.Held || s.Eaten) return;
        if ((s.Position - tip).magnitude > 1.2f && Mathf.Abs(s.Position.x - body.Center.x) > snackReach) return; // missed
        // knocked off the window, towards its mouth
        Vector2 to = body.Center - s.Position;
        s.Knock(new Vector2(Mathf.Clamp(to.x * 2.2f, -5f, 5f), 3.5f));
    }

    void OnTagged(GummyBody tagger, GummyBody it)
    {
        if (tagger == body) Crack(it.Center);
    }

    void Say(string text)
    {
        if (speech) speech.Say(text, 0f, true);
    }
}
