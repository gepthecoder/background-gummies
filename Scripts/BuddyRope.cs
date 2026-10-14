using System;
using UnityEngine;

/// <summary>
/// The buddy's rope (BuddySkills: LASSO at level 11, SWING at 12): a candy rope it throws (the apple's is its whip).
/// Lasso: hold your cursor still just above it (or chat "lasso me") and it may lasso your cursor and hang from it, so you
/// carry it around like Spider-Man on a web. It never moves your cursor, and your clicks still go to your apps.
/// Shake the mouse and it lets go and goes flying; lower it onto something and it hops off; after a while its arms get tired.
/// GummyBrain uses the same rope for swings and rope climbs (Throw / Release). Once it knows the lasso it carries the rope
/// coiled at its hip (the apple carries its whip instead), so you can see it has one.
/// </summary>
[DefaultExecutionOrder(-8)] // after the brain decides, before the body simulates
[RequireComponent(typeof(GummyBody))]
[DisallowMultipleComponent]
public class BuddyRope : MonoBehaviour
{
    [Header("Look (WhipIt > Setup assigns these)")]
    public Material material;
    [Tooltip("Rope colour (the apple's rope looks like its whip once it has one).")]
    public Color color = new Color(0.80f, 0.13f, 0.18f);
    [Tooltip("Rope width (x size / 0.6).")]
    public float width = 0.035f;
    [Tooltip("Carries the rope coiled at its hip once it knows the lasso (the apple carries its whip instead).")]
    public bool showCoil = true;

    [Header("Lasso your cursor")]
    [Tooltip("Bring the cursor over and hold it still just above it this long, and it may lasso it (seconds).")]
    public float invitationTime = 1.2f;
    [Range(0f, 1f)] public float lassoChance = 0.6f;
    [Tooltip("Waits at least this long before lassoing your cursor again by itself (seconds).")]
    public float lassoCooldown = 30f;
    [Tooltip("Lets go after hanging this long (seconds).")]
    public float maxRide = 40f;
    [Tooltip("Shaking the mouse: this many quick turns within shakeWindow seconds and it lets go.")]
    public int shakeTurns = 3;
    public float shakeWindow = 0.8f;
    [Tooltip("Mouse speed that counts as shaking (world units / s).")]
    public float shakeSpeed = 6f;
    [Tooltip("XP for a ride (longer than 2 s).")]
    public int xpRide = 3;
    [Tooltip("Whirr of the loop sliding down a zipline (WhipIt > Setup assigns it).")]
    public AudioClip zipLoop;
    [Range(0f, 1f)] public float zipVolume = 0.4f;
    [Tooltip("A zipline it strings lands with this (WhipIt > Setup assigns it).")]
    public AudioClip zipThunk;
    [Tooltip("A zipline it strung snaps with this (a window it was tied to moved).")]
    public AudioClip zipSnap;

    public enum Mode { Off, Throwing, On, Back }
    public enum Use { Cursor, Swing, Grapple, Fun, Zip }

    public Mode Current { get; private set; }
    /// <summary>The rope is out (flying or hooked).</summary>
    public bool Active => Current == Mode.Throwing || Current == Mode.On;
    /// <summary>Hanging from your cursor (or about to).</summary>
    public bool Riding => Active && use == Use.Cursor;
    /// <summary>Where the rope is hooked on (while it's out).</summary>
    public Vector2 Hook => hook;
    /// <summary>The rope got there but it was too far away to hold on.</summary>
    public event Action Missed;

    const float ThrowTime = 0.15f, BackTime = 0.15f; // BuddyRoute plans with the same throw time
    float throwTime = ThrowTime;

    GummyBody body;
    GummyBrain brain;
    BuddySkills skills;
    BuddySpeech speech;
    BuddyProgress progress;
    BuddyWhip whip;
    LineRenderer line, loop;
    BuddyLayer layer;
    SpriteRenderer coil;
    Sprite coilSprite;
    Color coilColor;
    bool coilBaked;
    float coilHand = 1f, coilFacing = 1f, coilSwing, coilSwingV, coilPop;
    AudioSource zipSrc;
    Use use;
    Vector2 hook, hang, tipFrom, lastCursor, cursorVel;
    float maxLen, throwT, backT, rideStart, nextLasso, stillFor, lastFar = -100f, groundedFor, stretchFor, lastLine = -100f;
    bool saidWhee;
    int lastSx, lastSy, turnIdx;
    readonly float[] turns = new float[8];
    readonly Vector3[] pts = new Vector3[Segs + 1];
    readonly Vector3[] ring = new Vector3[LoopSegs + 1];
    const int Segs = 18, LoopSegs = 14;

    float S => body.size / 0.6f;

    void Awake()
    {
        body = GetComponent<GummyBody>();
        brain = GetComponent<GummyBrain>();
        skills = GetComponent<BuddySkills>();
        speech = GetComponent<BuddySpeech>();
        progress = GetComponent<BuddyProgress>();
        whip = GetComponent<BuddyWhip>();
        layer = GetComponent<BuddyLayer>();
        line = MakeLine("Rope");
        loop = MakeLine("Rope Loop");
        loop.loop = true;
        coil = MakeCoil();
        zipSrc = gameObject.AddComponent<AudioSource>();
        zipSrc.playOnAwake = false;
        zipSrc.loop = true;
        zipSrc.spatialBlend = 0f;
        zipSrc.volume = 0f;
        lastCursor = Grabber.CursorWorld;
        nextLasso = Time.time + 10f;
    }

    void OnEnable() { if (skills) skills.Learned += OnLearned; }

    void OnDisable()
    {
        if (skills) skills.Learned -= OnLearned;
        if (body) body.LetGo();
        Current = Mode.Off;
        if (line) line.enabled = false;
        if (loop) loop.enabled = false;
        if (coil) coil.enabled = false;
        if (zipSrc) zipSrc.Stop();
    }

    void OnDestroy()
    {
        if (line) Destroy(line.gameObject);
        if (loop) Destroy(loop.gameObject);
        if (coil) Destroy(coil.gameObject);
        if (coilSprite) { Destroy(coilSprite.texture); Destroy(coilSprite); }
    }

    // just learned it: how to use it
    void OnLearned(BuddySkills.Step st)
    {
        if (st.skill == BuddySkills.Skill.Lasso) Invoke(nameof(LassoHint), 4.4f);
        else if (st.skill == BuddySkills.Skill.Swing) Invoke(nameof(SwingHint), 4.4f);
    }
    void LassoHint() => Say("HOLD YOUR CURSOR ABOVE ME!", true);
    void SwingHint() => Say("WATCH ME SWING!", true);

    // ------------------------------------------------------------------ the rope

    /// <summary>
    /// Throw the rope: it flies to hook (drawn there) and catches ThrowTime later, holding the body from hang (the physics
    /// point; the same as hook unless it hangs beside a window's corner). Too far away when it gets there (maxLen and a
    /// bit) = it misses. False if the rope is already out.
    /// </summary>
    public bool Throw(Vector2 hook, Vector2 hang, float maxLen, Use use, float time = ThrowTime)
    {
        if (Active) return false;
        throwTime = Mathf.Max(time, 0.05f);
        this.hook = hook;
        this.hang = hang;
        this.maxLen = maxLen;
        this.use = use;
        throwT = 0f;
        Current = Mode.Throwing;
        float now = Time.time;
        if (use == Use.Swing || use == Use.Fun) { if (now - lastLine > 8f && UnityEngine.Random.value < 0.6f) Say(V("WHEEE!", "TARZAN TIME!", "ZOOOM!")); }
        else if (use == Use.Grapple) { if (now - lastLine > 8f && UnityEngine.Random.value < 0.6f) Say(V("HUP HUP!", "GOING UP!", "UP UP UP!")); }
        return true;
    }

    /// <summary>Hang on to a zipline here (a loop of rope round the line), holding the body from there (rope length to its middle).</summary>
    public bool Hold(Vector2 at, float length)
    {
        if (Active) return false;
        use = Use.Zip;
        hook = hang = at;
        maxLen = length;
        body.Rope(at, length);
        Current = Mode.On;
        if (zipLoop && zipSrc) { zipSrc.clip = zipLoop; zipSrc.volume = 0f; zipSrc.Play(); }
        return true;
    }

    /// <summary>The loop slid along the zipline to here.</summary>
    public void MoveHold(Vector2 at)
    {
        if (Current != Mode.On || use != Use.Zip) return;
        hook = hang = at;
        body.MoveRope(at);
    }

    /// <summary>Let go (the rope flies back to it).</summary>
    public void Release()
    {
        if (!Active) return;
        if (use == Use.Zip && Current == Mode.On) { body.LetGo(); Current = Mode.Off; return; } // (it held a pulley handle: no rope to pull back)
        bool ride = use == Use.Cursor && Current == Mode.On;
        tipFrom = Current == Mode.On ? hook : Vector2.Lerp(Tie(hook), hook, Ease(throwT / throwTime));
        body.LetGo();
        Current = Mode.Back;
        backT = 0f;
        if (ride) EndRide();
    }

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("BuddyRope.Update", t0); }
    void UpdateTimed()
    {
        float now = Time.time, dt = Time.deltaTime;
        Vector2 cur = Grabber.CursorWorld;
        bool jumped = (cur - lastCursor).sqrMagnitude > 9f; // the cursor jumped (another screen...)
        cursorVel = dt > 0f ? (cur - lastCursor) / dt : Vector2.zero;
        lastCursor = cur;
        if ((cur - body.Center).sqrMagnitude > 16f) lastFar = now;
        TrackShake(now);

        if (zipSrc && zipSrc.isPlaying)
        {
            bool sliding = Current == Mode.On && use == Use.Zip;
            float speed = body.Velocity.magnitude;
            zipSrc.volume = Mathf.MoveTowards(zipSrc.volume, sliding ? Mathf.Clamp01(speed / 6f) * zipVolume : 0f, dt * 3f);
            zipSrc.pitch = 0.8f + Mathf.Clamp01(speed / 7f) * 0.6f;
            if (!sliding && zipSrc.volume <= 0f) zipSrc.Stop();
        }
        switch (Current)
        {
            case Mode.Off:
                WatchInvitation(now, dt);
                break;
            case Mode.Throwing:
                if (use == Use.Cursor) hook = hang = cur;
                if (body.Held || (use == Use.Cursor && jumped)) { Release(); break; }
                throwT += dt;
                if (throwT >= throwTime) Latch(now);
                break;
            case Mode.On:
                if (body.Held || !body.OnRope) { Release(); break; } // grabbed (or something else let go)
                if (use == Use.Cursor) { hook = hang = cur; body.MoveRope(cur); Ride(now, dt, jumped); }
                break;
            case Mode.Back:
                backT += dt;
                if (backT >= BackTime) Current = Mode.Off;
                break;
        }
    }

    void Latch(float now)
    {
        float d = (body.Center - hang).magnitude;
        if (d > maxLen * 1.25f + 0.05f)
        {
            // got there, but it's too far to hold on
            Current = Mode.Back;
            backT = 0f;
            tipFrom = hook;
            Missed?.Invoke();
            return;
        }
        body.Rope(hang, Mathf.Max(d, body.size * 0.5f)); // as long as it is right now (no yank)
        Current = Mode.On;
        if (use == Use.Cursor)
        {
            rideStart = now;
            saidWhee = false;
            groundedFor = stretchFor = 0f;
            for (int i = 0; i < turns.Length; i++) turns[i] = -100f;
        }
    }

    // ------------------------------------------------------------------ lasso your cursor

    /// <summary>The longest rope for your cursor (0 = it hasn't learned the lasso yet).</summary>
    public float LassoLength => skills ? skills.LassoLength : 2.4f * S;

    /// <summary>Is your cursor where it could lasso it? tooFar: it knows how, but the cursor isn't just above it.</summary>
    public bool CanLasso(out bool tooFar)
    {
        tooFar = false;
        if (LassoLength <= 0f || Active) return false;
        Vector2 cur = Grabber.CursorWorld;
        float above = cur.y - body.Extent.yMax;
        tooFar = (cur - body.Center).magnitude > LassoLength * 1.2f || above < 0.15f * S || Mathf.Abs(cur.x - body.Center.x) > LassoLength;
        return !tooFar;
    }

    /// <summary>Lasso your cursor now (chat, menu, or it felt like it). False if it can't right now.</summary>
    public bool LassoCursor()
    {
        if (!CanLasso(out _) || body.Hiding || !brain || !brain.BeginHang()) return false;
        Vector2 cur = Grabber.CursorWorld;
        if (body.Grounded) // a little jump up towards it as it throws
            body.Hop(new Vector2(Mathf.Clamp(cur.x - body.Center.x, -1f, 1f) * 1.5f, GummyBody.JumpSpeed(0.35f * S, body.gravity, body.Drag)), 0.5f);
        Throw(cur, cur, LassoLength, Use.Cursor);
        nextLasso = Time.time + lassoCooldown;
        Say(V("GIDDY UP!", "GOTCHA, CURSOR!", "ROPE-SPRESSO!"));
        return true;
    }

    float askedUntil = -1f;

    /// <summary>You asked for a ride (chat): it lassoes your cursor as soon as it's above it (for this many seconds).</summary>
    public void LassoSoon(float seconds) => askedUntil = Time.time + seconds;

    // you brought the cursor over and hold it still just above its head: it might take that as an invitation
    void WatchInvitation(float now, float dt)
    {
        if (now < askedUntil && !BuddyChat.Open && brain && body.Grounded && CanLasso(out _) && LassoCursor()) { askedUntil = -1f; return; }
        if (LassoLength <= 0f || now < nextLasso || !brain || !brain.IsFree || !body.Grounded || body.Hiding || BuddyChat.Open || BuddyHideSeek.Playing)
        {
            stillFor = 0f;
            return;
        }
        Vector2 cur = Grabber.CursorWorld;
        float above = cur.y - body.Extent.yMax;
        bool spot = Mathf.Abs(cur.x - body.Center.x) < 1.2f * S && above > 0.3f * S && above < LassoLength - 0.4f * S;
        bool still = cursorVel.sqrMagnitude < 1.5f * 1.5f;
        bool cameOver = now - lastFar < 8f; // not a cursor that's been parked there all along
        stillFor = spot && still && cameOver ? stillFor + dt : 0f;
        if (stillFor < invitationTime) return;
        stillFor = 0f;
        nextLasso = now + lassoCooldown * 0.3f; // not in the mood: maybe next time
        if (UnityEngine.Random.value < lassoChance) LassoCursor();
    }

    // hanging from your cursor
    void Ride(float now, float dt, bool jumped)
    {
        float ride = now - rideStart;
        if (Shaken(now))
        {
            Vector2 v = body.Velocity;
            Release();
            body.Hop(v * 0.5f, 0.5f); // off it goes
            Say(V("WHOAAA!", "WHEEEEE!", "ZOOOOM!"), true);
            return;
        }
        Rect scr = ScreenBounds.WorldRect;
        Vector2 cur = Grabber.CursorWorld;
        if (jumped || cur.x < scr.xMin - 0.2f || cur.x > scr.xMax + 0.2f || cur.y < scr.yMin - 0.2f || cur.y > scr.yMax + 0.2f) { Release(); return; } // off to another screen
        // caught on something (dragged behind a window): lets go
        stretchFor = body.RopeStretch > 0.8f * S ? stretchFor + dt : 0f;
        if (stretchFor > 0.3f) { Release(); Say("OOPS!"); return; }
        // lowered onto something (the rope's slack, or your cursor's down beside it): hops off
        bool slack = body.Grounded && ((body.Center - cur).magnitude < body.RopeLength - 0.1f || cur.y < body.Extent.yMax + 0.1f);
        groundedFor = slack ? groundedFor + dt : 0f;
        if (groundedFor > 0.6f && ride > 1f) { Release(); Say(V("THANKS FOR THE RIDE!", "AGAIN! AGAIN!", "WHAT A TRIP!")); return; }
        if (ride > maxRide) { Release(); Say(V("MY ARMS!", "OK I'M DONE", "ARMS... TIRED...")); return; }
        if (!saidWhee && body.Velocity.sqrMagnitude > 49f) { saidWhee = true; Say("WHEEE!"); }
        // climbs up to its usual length (it caught a bit further away)
        float want = Mathf.Max(LassoLength, body.size * 0.6f);
        if (body.RopeLength > want) body.ReelTo(want, 2f * S);
    }

    void EndRide()
    {
        float now = Time.time;
        nextLasso = Mathf.Max(nextLasso, now + lassoCooldown);
        if (now - rideStart > 2f && progress) progress.AddXp(xpRide, body.Center);
    }

    // quick back-and-forth with the mouse: count the turns
    void TrackShake(float now)
    {
        if (Mathf.Abs(cursorVel.x) > shakeSpeed)
        {
            int sx = cursorVel.x > 0f ? 1 : -1;
            if (lastSx != 0 && sx != lastSx) Turn(now);
            lastSx = sx;
        }
        if (Mathf.Abs(cursorVel.y) > shakeSpeed)
        {
            int sy = cursorVel.y > 0f ? 1 : -1;
            if (lastSy != 0 && sy != lastSy) Turn(now);
            lastSy = sy;
        }
    }

    void Turn(float now) { turns[turnIdx] = now; turnIdx = (turnIdx + 1) % turns.Length; }

    bool Shaken(float now)
    {
        int k = 0;
        foreach (float t in turns) if (now - t <= shakeWindow) k++;
        return k >= shakeTurns;
    }

    // ------------------------------------------------------------------ drawing

    void LateUpdate() { long t0 = WhipProf.T(); LateUpdateTimed(); WhipProf.Add("BuddyRope.LateUpdate", t0); }
    void LateUpdateTimed()
    {
        DrawCoil();
        if (Current == Mode.Off || !line || (use == Use.Zip && Current == Mode.On)) // (on a zipline it holds the pulley's handle: Zipline draws it)
        {
            if (line && line.enabled) { line.enabled = false; loop.enabled = false; }
            return;
        }
        bool whipLook = whip && whip.Has && whip.whip;
        Color col = whipLook ? whip.whip.lashColor : color;
        line.enabled = true;
        line.startColor = line.endColor = col;
        line.widthMultiplier = width * S * (whipLook ? 1.15f : 1f);

        Vector2 tip, from;
        float sag = 0f, arc = 0f;
        switch (Current)
        {
            case Mode.Throwing:
            {
                float u = Ease(throwT / throwTime);
                from = Tie(hook);
                tip = Vector2.Lerp(from, hook, u);
                arc = 0.18f * (hook - from).magnitude * Mathf.Sin(Mathf.PI * u); // flies in a little arc
                break;
            }
            case Mode.On:
                from = body.OnRope ? body.RopePoint : Tie(hook);
                tip = hook;
                // slack hangs down (the rope is as long as hook-to-middle minus the bit from its middle to where it's tied)
                float len = body.RopeLength - (body.RopePoint - body.Center).magnitude, d = (tip - from).magnitude;
                sag = len > d ? Mathf.Sqrt(len * len - d * d) * 0.5f : 0f;
                break;
            default:
            {
                float u = Ease(backT / BackTime);
                from = Tie(tipFrom);
                tip = Vector2.Lerp(tipFrom, from, u);
                arc = 0.12f * (tipFrom - from).magnitude * (1f - u);
                break;
            }
        }
        if (layer && layer.Hides(from) && layer.Hides(tip)) { line.enabled = false; loop.enabled = false; return; } // behind a window in front of it
        Vector2 mid = 0.5f * (from + tip), dir = tip - from;
        Vector2 perp = dir.sqrMagnitude > 1e-6f ? new Vector2(-dir.y, dir.x).normalized : Vector2.up;
        if (perp.y < 0f) perp = -perp;
        Vector2 ctrl = mid + Vector2.down * sag + perp * arc;
        line.positionCount = Segs + 1;
        for (int i = 0; i <= Segs; i++)
        {
            float t = i / (float)Segs;
            Vector2 p = (1f - t) * (1f - t) * from + 2f * (1f - t) * t * ctrl + t * t * tip;
            pts[i] = new Vector3(p.x, p.y, 0f);
        }
        line.SetPositions(pts);

        // the loop: around your cursor (hugging the arrow), or round the hook
        bool showLoop = Current == Mode.On || (Current == Mode.Throwing && throwT > throwTime * 0.6f);
        loop.enabled = showLoop;
        if (!showLoop) return;
        loop.startColor = loop.endColor = whipLook ? whip.whip.popperColor : col;
        loop.widthMultiplier = width * S * 0.8f;
        float r = (use == Use.Cursor ? 0.07f : 0.045f) * S;
        Vector2 c = use == Use.Cursor ? tip + new Vector2(0.04f, -0.09f) * S : tip + Vector2.down * r * 0.6f;
        if (use != Use.Cursor && DesktopPlatforms.IconAt(tip, out IconBox icon)) { c = icon.Center; r = 0.55f * Mathf.Max(icon.xMax - icon.xMin, icon.yMax - icon.yMin); } // lassoed an icon
        loop.positionCount = LoopSegs;
        for (int i = 0; i < LoopSegs; i++)
        {
            float a = i * 2f * Mathf.PI / LoopSegs;
            ring[i] = new Vector3(c.x + Mathf.Cos(a) * r, c.y + Mathf.Sin(a) * r * 0.75f, 0f);
        }
        loop.SetPositions(ring);
    }

    // where the rope leaves the body when it's aimed at a point (the side facing it, near the top)
    Vector2 Tie(Vector2 towards)
    {
        Vector2 c = body.Center, d = towards - c;
        float h = Mathf.Max(body.Extent.height, body.size * 0.6f) * 0.45f;
        return d.sqrMagnitude > 1e-6f ? c + d.normalized * h : c + Vector2.up * h;
    }

    static float Ease(float u) { u = Mathf.Clamp01(u); return 1f - (1f - u) * (1f - u); }

    // ------------------------------------------------------------------ the coil at its hip

    /// <summary>The rope is coiled at its hip right now (it knows the lasso, has no whip, the rope isn't out).</summary>
    public bool CoilShown => coil && coil.enabled;

    void DrawCoil()
    {
        if (!coil) return;
        bool show = showCoil && LassoLength > 0f && !(whip && whip.Has && whip.whip) && Current == Mode.Off && !body.Hiding;
        if (!show)
        {
            if (coil.enabled) coil.enabled = false;
            coilPop = 0f;
            return;
        }
        if (!coilBaked || !SameColor(coilColor, color))
        {
            if (coilSprite) { Destroy(coilSprite.texture); Destroy(coilSprite); }
            coilSprite = BakeCoil(color);
            coilColor = color;
            coilBaked = true;
            coil.sprite = coilSprite;
        }
        coil.enabled = true;
        float dt = Mathf.Min(Time.deltaTime, 0.05f);
        coilPop = Mathf.Min(1f, coilPop + dt / 0.22f); // pops back in when the rope's back (or just learned)

        // on the side facing your cursor (like the apple's whip hand); it goes round behind it when it turns
        float dx = Grabber.CursorWorld.x - body.Center.x;
        if (Mathf.Abs(dx) > 0.3f) coilFacing = dx > 0f ? 1f : -1f;
        coilHand = Mathf.MoveTowards(coilHand, coilFacing, 6f * dt);
        // hangs from where it's tied: swings back a little when it moves
        float target = Mathf.Clamp(-body.Velocity.x * 3.5f, -28f, 28f);
        coilSwingV += ((target - coilSwing) * 120f - coilSwingV * 9f) * dt;
        coilSwing = Mathf.Clamp(coilSwing + coilSwingV * dt, -40f, 40f);

        float ang = body.Angle;
        var rot = Quaternion.Euler(0f, 0f, ang);
        Vector2 up = rot * Vector2.up, right = rot * Vector2.right, span = body.Span;
        float w = Mathf.Max(span.x, body.size * 0.5f), h = Mathf.Max(span.y, body.size * 0.5f);
        Vector2 tie = body.Center + right * (coilHand * 0.52f * w) + up * (0.1f * h);
        float size = 0.62f * Mathf.Min(w, h * 1.1f) * Pop(coilPop);
        coil.flipX = coilHand < 0f; // the loose end on the outside
        coil.sortingOrder = Mathf.Abs(coilHand) > 0.6f ? body.sortingOrder + 1 : body.sortingOrder - 1; // on its hip; round behind it when it turns
        coil.transform.position = new Vector3(tie.x, tie.y, 0f);
        coil.transform.rotation = Quaternion.Euler(0f, 0f, ang + coilSwing);
        coil.transform.localScale = new Vector3(size, size, 1f);
    }

    static float Pop(float u) { u = Mathf.Clamp01(u) - 1f; return 1f + u * u * (2.7f * u + 1.7f); } // eases out with a little overshoot
    static bool SameColor(Color a, Color b) => Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) < 0.003f;

    SpriteRenderer MakeCoil()
    {
        var go = new GameObject("Rope Coil");
        go.transform.SetParent(transform, false); // (a child: hiding behind a window masks it too)
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sortingOrder = body.sortingOrder + 1; // on its hip (over the body, under the face)
        Material m = material ? material : body.spriteMaterial;
        if (m) sr.sharedMaterial = m;
        sr.enabled = false;
        return sr;
    }

    const int CoilRes = 96;
    const float CoilTieY = 0.93f; // the pivot: where it's tied on (top middle)

    static Sprite BakeCoil(Color rope)
    {
        var tex = new Texture2D(CoilRes, CoilRes, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "Rope Coil" };
        var px = new Color32[CoilRes * CoilRes];
        float texel = 1f / CoilRes;
        for (int j = 0; j < CoilRes; j++)
            for (int i = 0; i < CoilRes; i++)
            {
                Color c = CoilPaint((i + 0.5f) * texel - 0.5f, (j + 0.5f) * texel, texel, rope);
                px[j * CoilRes + i] = new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), Mathf.Clamp01(c.a));
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return Sprite.Create(tex, new Rect(0, 0, CoilRes, CoilRes), new Vector2(0.5f, CoilTieY), CoilRes); // 1 unit wide
    }

    // three loops of twisted rope, a wrap where it's tied on, and the loose end hanging down (x -0.5..0.5, y 0..1)
    static Color CoilPaint(float x, float y, float texel, Color rope)
    {
        Color outC = new Color(0f, 0f, 0f, 0f);
        const float hw = 0.046f;
        // back to front: the loops, then the loose end, then the wrap on top
        for (int k = 0; k < 3; k++)
        {
            Vector2 c = new Vector2(-0.065f + 0.065f * k, 0.545f - 0.012f * k);
            float rx = 0.30f - 0.008f * k, ry = 0.29f + 0.006f * (k - 1);
            Vector2 q = new Vector2((x - c.x) / rx, (y - c.y) / ry);
            float ql = q.magnitude;
            if (ql < 1e-4f) continue;
            Vector2 g = new Vector2(q.x / rx, q.y / ry) / ql;
            float d = (ql - 1f) / Mathf.Max(g.magnitude, 1e-4f); // signed distance to the loop's centre line
            float a = Cover(Mathf.Abs(d) - hw, texel);
            if (a <= 0f) continue;
            Vector2 n = new Vector2(q.x / ql, q.y / ql); // outwards
            float twist = Mathf.Atan2(q.y, q.x) * 9f + d / hw * 1.4f + k * 1.3f;
            outC = Over(outC, Tube(rope, d / hw, n, twist), a);
        }
        {
            // the loose end: out from the front loop's bottom, curling down, a little knot at the end
            Vector2 p0 = new Vector2(0.24f, 0.33f), p1 = new Vector2(0.33f, 0.24f), p2 = new Vector2(0.29f, 0.13f);
            float best = 9f, bt = 0f;
            for (int k = 0; k <= 24; k++)
            {
                float t = k / 24f;
                Vector2 b = (1f - t) * (1f - t) * p0 + 2f * (1f - t) * t * p1 + t * t * p2;
                float dd = (new Vector2(x, y) - b).magnitude;
                if (dd < best) { best = dd; bt = t; }
            }
            float tw = hw * Mathf.Lerp(1f, 0.85f, bt);
            float a = Cover(best - tw, texel);
            if (a > 0f) outC = Over(outC, Tube(rope, Mathf.Clamp(best / tw, 0f, 1f), new Vector2(0.7f, 0.7f), bt * 14f), a);
            float kd = (new Vector2(x, y) - p2).magnitude - hw * 1.25f;
            float ka = Cover(kd, texel);
            if (ka > 0f) outC = Over(outC, Tube(rope * 0.92f, Mathf.Clamp01(kd / (hw * 1.25f) + 1f), (new Vector2(x, y) - p2).normalized, 0f), ka);
        }
        {
            // the wrap: a band of rope round the loops where it's tied to its hip
            float bx = Mathf.Abs(x) - 0.07f, by = Mathf.Abs(y - 0.855f) - 0.06f;
            float bd = new Vector2(Mathf.Max(bx, 0f), Mathf.Max(by, 0f)).magnitude + Mathf.Min(Mathf.Max(bx, by), 0f) - 0.02f;
            float a = Cover(bd, texel);
            if (a > 0f)
            {
                Color band = Tube(rope * 0.8f, Mathf.Clamp(x / 0.09f, -1f, 1f), new Vector2(1f, 0f), (y - 0.795f) * 52f + 1.6f); // (two turns of the wrap)
                outC = Over(outC, band, a);
            }
        }
        return outC;
    }

    // a lit rope tube: s = -1..1 across it, n = the outward direction in the picture, twist = the stripe phase along it
    static Color Tube(Color rope, float s, Vector2 n, float twist)
    {
        s = Mathf.Clamp(s, -1f, 1f);
        float z = Mathf.Sqrt(Mathf.Max(0f, 1f - s * s));
        Vector3 nn = new Vector3(n.x * s, n.y * s, z);
        Vector3 L = new Vector3(-0.45f, 0.6f, 0.66f);
        float diff = Mathf.Max(0f, nn.x * L.x + nn.y * L.y + nn.z * L.z);
        float spec = Mathf.Pow(diff, 14f);
        float stripe = Mathf.Sin(twist) > 0.55f ? 0.82f : 1f; // twisted strands
        float edge = Mathf.Abs(s) > 0.78f ? 0.7f : 1f;
        float k = (0.5f + 0.62f * diff) * stripe * edge;
        Color c = new Color(rope.r * k, rope.g * k, rope.b * k, 1f);
        return Color.Lerp(c, Color.white, spec * 0.45f);
    }

    static Color Over(Color under, Color over, float a)
    {
        float oa = a + under.a * (1f - a);
        if (oa <= 1e-5f) return new Color(0f, 0f, 0f, 0f);
        Color c = (over * a + under * (under.a * (1f - a))) / oa;
        c.a = oa;
        return c;
    }

    static float Cover(float d, float texel) => Mathf.Clamp01(0.5f - d / texel); // d < 0 inside

    LineRenderer MakeLine(string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.sharedMaterial = material ? material : new Material(Shader.Find("Sprites/Default"));
        lr.sortingOrder = body.sortingOrder - 1; // behind the body (it's tied on its top)
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        lr.alignment = LineAlignment.View;
        lr.numCapVertices = 3;
        lr.numCornerVertices = 2;
        lr.enabled = false;
        return lr;
    }

    // ------------------------------------------------------------------ talk

    string V(string apple, string tomato, string bean)
    {
        var v = speech ? speech.voice : BuddySpeech.Voice.Apple;
        return v == BuddySpeech.Voice.Tomato ? tomato : v == BuddySpeech.Voice.Bean ? bean : apple;
    }

    void Say(string text, bool force = false)
    {
        if (!speech) return;
        if (speech.Say(text, 0f, force)) lastLine = Time.time;
    }
}
