using UnityEngine;

/// <summary>
/// Makes a GummyBody behave like a little pet: sits and watches the cursor (CuteFace does the eyes),
/// hops around on its own (squash, spring, land), wanders along the floor / taskbar, sometimes towards the
/// cursor, and naps ("zzz", breathing) when you ignore it. Waking up: move the cursor close or grab it.
/// Whenever you hold it, throw it or it's stuck to an edge, the brain lets the physics do its thing.
/// Moods on top: dizzy after a hard hit or throw, grumpy while stuck (straining when you pull),
/// annoyed when you keep poking it (hops away from the cursor), happy when you pet it gently (hearts).
/// Snacks (BuddyEater sets Food): plans a route there with what it knows at its level (BuddySkills, BuddyRoute: hop
/// over, hop down, hop up, leap, jump up beside a window and pull itself over, climb) and goes, one move at a time.
/// A move that didn't get it where it wanted twice is remembered (until the windows move) and planned around.
/// Edges (GummyBrain.Edges.cs): climbs screen edges and window sides, dangles from the top, peeks in from off-screen.
/// Rope (GummyBrain.Rope.cs, BuddyRope): swings, rope climbs, and hanging from your cursor.
/// Skateboard (GummyBrain.Skate.cs, BuddySkate): long rides, ollies off window edges, rides for fun.
/// Paper plane (GummyBrain.Glide.cs, BuddyPlane): glides from window tops to spots far below.
/// Balloon (GummyBrain.Float.cs, BuddyBalloon): floats up onto window tops too high to jump to.
/// Pounce (GummyBrain.Pounce.cs, BuddyLaser): wiggles and pounces on the laser pointer's dot.
/// </summary>
[DefaultExecutionOrder(-10)] // decide before the body simulates this frame
[RequireComponent(typeof(GummyBody))]
[DisallowMultipleComponent]
public partial class GummyBrain : MonoBehaviour
{
    public enum State { Recover, Idle, Hopping, Asleep, Climb, Peek, Swing }

    [Header("Hopping (x size / 0.6)")]
    public float hopSpeed = 1.6f;
    public float hopUp = 5.5f;
    public float squashTime = 0.12f;
    [Tooltip("Squash before the jump; keep x*y near 1.")]
    public Vector2 squashPose = new Vector2(1.15f, 0.87f);
    [Tooltip("Stretch right after take-off.")]
    public Vector2 launchPose = new Vector2(0.9f, 1.11f);
    public Vector2 pauseBetweenHops = new Vector2(0.35f, 0.7f);
    public Vector2Int hopsPerWalk = new Vector2Int(2, 5);

    [Header("Idle")]
    public Vector2 idleTime = new Vector2(2f, 6f);
    [Range(0f, 1f)] public float hopTowardsCursor = 0.4f;
    /// <summary>More than one screen: the chance a stroll may go on over onto the next screen (else it turns at the edge
    /// of its own; you can always throw it over).</summary>
    const float RoamChance = 0.15f;
    [Tooltip("On top of a window: chance to turn around instead of hopping off the edge.")]
    [Range(0f, 1f)] public float stayOnWindow = 0.75f;
    [Tooltip("Cursor this close counts as paying attention to it (x size / 0.6).")]
    public float attentionDistance = 2f;

    [Header("Sleep")]
    [Tooltip("Naps after this many seconds without attention.")]
    public float sleepAfter = 40f;
    [Tooltip("Cursor this close wakes it up (x size / 0.6).")]
    public float wakeDistance = 1.2f;
    public float breathing = 0.03f;
    public float zzzInterval = 1.3f;
    public Color zzzColor = new Color(0.36f, 0.42f, 0.75f, 1f);

    [Header("Moods")]
    [Tooltip("Hitting an edge faster than this (or being thrown faster) makes it dizzy.")]
    public float dizzySpeed = 11f;
    public float dizzyTime = 2.5f;
    [Tooltip("This many grabs within annoyedWindow seconds = annoyed.")]
    public int annoyedPokes = 3;
    public float annoyedWindow = 5f;
    public float annoyedTime = 6f;
    [Tooltip("Petting = hovering over it while moving the cursor this fast (world units / s).")]
    public Vector2 petSpeed = new Vector2(0.3f, 5f);
    [Tooltip("Seconds of petting before it gets happy.")]
    public float petTime = 0.8f;
    public float happyTime = 3f;
    public Color heartColor = new Color(1f, 0.42f, 0.58f, 1f);
    public Color angerColor = new Color(0.9f, 0.15f, 0.15f, 1f);

    [Header("Snacks")]
    [Tooltip("Highest window top it will leap up to (world units).")]
    public float leapHeight = 6f;
    [Tooltip("How far above the landing surface the leap peaks (x size / 0.6).")]
    public float leapMargin = 0.9f;
    [Tooltip("Leaps from at most this far to the side; further away it hops closer first (x size / 0.6).")]
    public float leapReach = 3f;

    public State Current { get; private set; } = State.Recover;
    public CuteFace.Expression Mood { get; private set; }
    /// <summary>Poked too much lately (it doesn't enjoy grabs right now).</summary>
    public bool Annoyed => Time.time < annoyedUntil;
    /// <summary>Enjoying being petted right now.</summary>
    public bool Happy => Time.time < happyUntil;
    /// <summary>Dizzy (after a hard throw or hit).</summary>
    public bool Dizzy => Time.time < dizzyUntil;
    /// <summary>Where the snack it wants is (null = none). BuddyEater sets it every frame.</summary>
    public Vector2? Food { get; set; }
    /// <summary>Height of the surface that snack is lying on.</summary>
    public float FoodSurface { get; set; }

    GummyBody body;
    CuteFace face;
    float stateTime, settleTime, idleFor, lastAttention, pauseFor, nextZ, airTime;
    int hopsLeft, dir = 1;
    bool roam; // (more than one screen) this stroll may go on onto the next screen
    Vector2 home; // (more than one screen) where it lives: where you last put it down
    bool homeSet, movedByYou;
    int homeVersion;
    enum Hop { Pause, Squash, Air }
    Hop hop;
    float hopT;
    bool chasing, leapPlanned, mantle;
    float steadyFor;
    float mantleDir, mantleY, mantleX;
    BuddySkills skills;
    float chewUntil = -1f;
    const float ChewTime = 0.7f;

    // moods
    float dizzyUntil = -1f, annoyedUntil = -1f, happyUntil = -1f, petAccum, stuckTime, lastStuck = -1f, nextHeart, nextAnger;
    readonly float[] pokes = new float[8];
    int pokeIdx;
    bool wasHeld, wasHappy;
    Vector2 lastCursor;

    // floating icons (zzz, hearts, anger marks)
    const int IconCount = 8;
    GameObject iconHolder;
    /// <summary>Its little icons (zzz, hearts, "!"): BuddyLayer hides them behind windows with the rest of it.</summary>
    public GameObject IconRoot => iconHolder;
    readonly SpriteRenderer[] icons = new SpriteRenderer[IconCount];
    readonly Icon[] iconData = new Icon[IconCount];
    int nextIcon;
    Sprite zSprite, heartSprite, angerSprite;
    Texture2D zTex, heartTex, angerTex;

    struct Icon
    {
        public Vector2 start;
        public Color color;
        public float age, life, rise, wobble, scale0, grow;
    }

    float S => body.size / 0.6f;

    void Awake()
    {
        body = GetComponent<GummyBody>();
        face = GetComponent<CuteFace>();
        skills = GetComponent<BuddySkills>();
        rope = GetComponent<BuddyRope>();
        board = GetComponent<BuddySkate>();
        plane = GetComponent<BuddyPlane>();
        balloon = GetComponent<BuddyBalloon>();
        jetpack = GetComponent<BuddyJetpack>();
        espresso = GetComponent<BuddyEspresso>();
        pogo = GetComponent<BuddyPogo>();
        lastAttention = Time.time;
        BuildIcons();
    }

    void OnEnable()
    {
        if (!body) return;
        body.Splat += OnHit;
        body.Bump += OnHit;
        if (iconHolder) iconHolder.SetActive(true);
    }

    void OnDisable()
    {
        body.Splat -= OnHit;
        body.Bump -= OnHit;
        EndRide();
        EndGlide();
        EndFloat();
        EndJet();
        EndTower();
        EndPit();
        body.Pose = Vector2.one;
        body.StopCrawl();
        body.HoldRoom = false;
        body.Brace = false;
        if (face) { face.Asleep = false; face.Mood = CuteFace.Expression.Normal; }
        if (iconHolder) iconHolder.SetActive(false); // (its thought icons go with it, e.g. into a fused monster)
    }

    void OnDestroy()
    {
        if (iconHolder) Destroy(iconHolder);
        if (zSprite) Destroy(zSprite);
        if (zTex) Destroy(zTex);
        if (heartSprite) Destroy(heartSprite);
        if (heartTex) Destroy(heartTex);
        if (angerSprite) Destroy(angerSprite);
        if (angerTex) Destroy(angerTex);
    }

    void OnHit(Vector2 at, Vector2 normal, float speed)
    {
        // a hard knock wakes it up (its own hop landings are gentle and don't count)
        if (speed > 7f) lastAttention = Time.time;
        if (speed > dizzySpeed && Current != State.Hopping && Time.time > NoDizzyUntil) dizzyUntil = Time.time + dizzyTime; // not from landing its own (chase) hops (or a stomp)
        if (Current == State.Asleep && speed > 7f) Enter(State.Recover);
    }

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("GummyBrain.Update", t0); }
    void UpdateTimed()
    {
        float now = Time.time, dt = Time.deltaTime;
        if (body.Frozen || BulletTime.Stopped) { lastAttention = now; return; } // (stopped dead / time stopped: no thinking, no hopping)
        stateTime += dt;
        if (ScreenBounds.Multi) UpdateHome();

        float cursorDist = (Grabber.CursorWorld - body.Center).magnitude;
        if (body.Held || body.Hovered || cursorDist < attentionDistance * S) lastAttention = now;
        if (Food.HasValue) lastAttention = now; // no napping with a snack around

        // physics has the say while it's held, stuck, flying or still rolling
        // (a short bounce after landing doesn't count as flying)
        bool disturbed = body.Held || (body.IsStuck && !towering); // (in a buddy tower, stuck to the floor is fine)
        airTime = body.Grounded ? 0f : airTime + dt;
        bool offFloor = airTime > 0.3f;
        bool calm = !disturbed && body.Grounded && body.Velocity.sqrMagnitude < 0.25f;
        settleTime = calm ? settleTime + dt : 0f;

        UpdateMood(now, dt);
        bool dizzy = now < dizzyUntil, annoyed = now < annoyedUntil, happy = now < happyUntil;

        if (PitStep(now, dt)) { } // (in the ball pit: swimming, GummyBrain.Pit)
        else switch (Current)
        {
            case State.Recover:
                body.Pose = now < TuckUntil ? TuckPose : now < actFrom + actLen ? ActPose(now) : Vector2.one;
                if (body.IsStuck && body.GlueNormal.y < -0.5f) // dangling from the top edge: wiggles
                {
                    float wg = 0.05f * Mathf.Sin(now * 9f);
                    body.Pose = new Vector2(1f + wg, 1f - wg);
                }
                if (settleTime > 0.5f) Enter(State.Idle);
                break;

            case State.Idle:
                body.Pose = now < TuckUntil ? TuckPose : now < actFrom + actLen ? ActPose(now) : Breathe(0.01f, now);
                if (disturbed || offFloor) { Enter(State.Recover); break; }
                if (now - lastAttention > sleepAfter && !annoyed) { Enter(State.Asleep); break; }
                if (dizzy || happy) break;                                         // stays put: wobbling / enjoying the petting
                if (TryCatch(now)) break;                                           // a snack falling right onto it: jumps to meet it
                if (Food.HasValue && !Stuck && stateTime > (OnTheWay ? 0.15f : Reaction) && CanReachFood()) { StartChase(); break; } // snack! (quicker the cleverer it is)
                if (now < lingerUntil || StayPut) break;                            // hanging out (chatting with a friend, out of the rain)
                if (annoyed && stateTime > 0.4f) { StartWalk(true); break; }       // gets away from you
                if (stateTime > idleFor && TryEdgeAdventure()) break;               // climb / peek
                if (stateTime > idleFor) StartWalk(false);
                break;

            case State.Climb:
                UpdateClimb(now);
                break;

            case State.Peek:
                UpdatePeek(now);
                break;

            case State.Swing:
                UpdateSwing(now, dt);
                break;

            case State.Hopping:
                if (disturbed || (hop != Hop.Air && offFloor)) { Enter(State.Recover); break; }
                UpdateHop(dt);
                break;

            case State.Asleep:
                { Vector2 br = Breathe(breathing, now); body.Pose = DeepSleep ? new Vector2(LiePose.x * br.x, LiePose.y * br.y) : br; } // (for the night: lying down)
                if (body.Held || (!DeepSleep && cursorDist < wakeDistance * S)) { WakeUp(); break; } // (for the night: only grabbing wakes it)
                if (now >= nextZ) { SpawnIcon(zSprite, zzzColor, new Vector2(0.25f, 0.3f), 2.2f, 0.5f, 0.12f, 0.28f, 0.17f); nextZ = now + zzzInterval; }
                break;
        }

        // dizzy wobble (not while you hold it or it's in the middle of a hop)
        if (dizzy && !body.Held && Current != State.Hopping && Current != State.Asleep && Current != State.Swing)
        {
            float w = 0.05f * Mathf.Sin(now * 14f);
            body.Pose = new Vector2(1f + w, 1f - w);
        }

        // chewing a snack
        if (now < chewUntil && !body.Held && Current != State.Hopping)
        {
            float c = 0.07f * Mathf.Abs(Mathf.Sin((1f - (chewUntil - now) / ChewTime) * 3f * Mathf.PI));
            body.Pose = new Vector2(1f + c, 1f - c);
        }

        // its own hops: braced on landing and while winding up (it doesn't roll off its own landings)
        body.Brace = Current == State.Hopping && (hop != Hop.Air || gliding || floating || jetting || towering) && !mantle;
        if (face) face.Mood = Current == State.Asleep ? CuteFace.Expression.Normal : Mood;
        UpdateIcons(dt);
    }

    void UpdateMood(float now, float dt)
    {
        // grabbed: count pokes, remember how hard it was thrown on release
        bool held = body.Held;
        if (held && !wasHeld)
        {
            pokes[pokeIdx] = now;
            pokeIdx = (pokeIdx + 1) % pokes.Length;
            int recent = 0;
            foreach (float t in pokes) if (t > 0f && now - t < annoyedWindow) recent++;
            if (recent >= annoyedPokes)
            {
                annoyedUntil = now + annoyedTime;
                happyUntil = -1f;
                for (int i = 0; i < pokes.Length; i++) pokes[i] = 0f;
                nextAnger = now;
            }
        }
        if (!held && wasHeld && body.Velocity.magnitude > dizzySpeed * 1.1f) dizzyUntil = now + dizzyTime; // flung
        wasHeld = held;

        // stuck time survives the short let-go / re-stick moments while it slides down an edge
        if (body.IsStuck) { stuckTime += dt; lastStuck = now; }
        else if (now - lastStuck > 0.5f) stuckTime = 0f;
        bool stuckRecently = now - lastStuck < 0.5f;

        // petting: gentle cursor movement over it
        Vector2 cur = Grabber.CursorWorld;
        float cursorSpeed = dt > 0f ? (cur - lastCursor).magnitude / dt : 0f;
        lastCursor = cur;
        bool petting = body.Hovered && !held && cursorSpeed > petSpeed.x * S && cursorSpeed < petSpeed.y * S;
        petAccum = petting ? petAccum + dt : Mathf.Max(0f, petAccum - dt * 0.5f);
        if (petAccum > petTime && body.Hovered && !held)
        {
            happyUntil = now + happyTime;
            annoyedUntil = -1f; // petting calms it down
            if (now >= nextHeart) { SpawnIcon(heartSprite, heartColor, new Vector2(Random.Range(-0.2f, 0.2f), 0.35f), 1.4f, 0.45f, 0.06f, 0.18f, 0.06f); nextHeart = now + 0.5f; }
        }

        bool happy = now < happyUntil;
        if (happy && !wasHappy && Current == State.Idle && body.Grounded && !body.IsStuck)
            body.Hop(new Vector2(0f, hopUp * 0.45f * Mathf.Sqrt(S)), 0.6f); // little happy bounce
        wasHappy = happy;

        if (now < annoyedUntil && now >= nextAnger && Current != State.Asleep)
        {
            SpawnIcon(angerSprite, angerColor, new Vector2(0.28f, 0.3f), 1f, 0.1f, 0f, 0.2f, 0.08f);
            nextAnger = now + 1.2f;
        }

        // what the face shows
        if (now < dizzyUntil) Mood = CuteFace.Expression.Dizzy;
        else if (held && body.IsStuck && body.Stretch > 0.3f) Mood = CuteFace.Expression.Strain;
        else if (stuckRecently && stuckTime > 0.3f) Mood = body.GlueNormal.y < -0.5f ? CuteFace.Expression.Happy : CuteFace.Expression.Grumpy; // dangling is fun
        else if (now < annoyedUntil) Mood = CuteFace.Expression.Grumpy;
        else if (happy) Mood = CuteFace.Expression.Happy;
        else Mood = CuteFace.Expression.Normal;
    }

    void Enter(State s)
    {
        State was = Current;
        if (riding) EndRide(); // (off the board, whatever comes next)
        if (gliding) EndGlide();
        if (floating) EndFloat();
        if (jetting) EndJet();
        if (towering) EndTower();
        if (pitting) EndPit();
        pouncing = false;
        Current = s;
        stateTime = 0f;
        if (s != State.Hopping) chasing = leapPlanned = edgeWalk = mantle = false;
        if (s != State.Hopping && s != State.Climb) foodClimb = false;
        if (was == State.Climb && s != State.Climb) body.StopCrawl();
        if (was == State.Peek && s != State.Peek) body.HoldRoom = false;
        if (was == State.Swing && s != State.Swing && rope && rope.Active) rope.Release();
        if (was == State.Swing && s != State.Swing && zip) { zip.Off(body); zip = null; }
        if (face) face.Asleep = s == State.Asleep;
        if (s == State.Idle) idleFor = Random.Range(idleTime.x, idleTime.y);
        if (s == State.Asleep) nextZ = Time.time + 0.4f;
        if (s != State.Idle && s != State.Asleep) body.Pose = Vector2.one;
    }

    void StartWalk(bool awayFromCursor)
    {
        Rect r = ScreenBounds.WorldRect;
        float x = body.Center.x;
        float cx = Grabber.CursorWorld.x;
        bool cursorOnScreen = cx > r.xMin && cx < r.xMax && ScreenBounds.SameScreen(Grabber.CursorWorld, body.Center); // (on its screen)

        if (awayFromCursor && cursorOnScreen)
            dir = cx > x ? -1 : 1;
        else if (Random.value < hopTowardsCursor && Mathf.Abs(cx - x) > 1f * S && cursorOnScreen)
            dir = cx > x ? 1 : -1;
        else
            dir = Random.value < 0.5f ? -1 : 1;
        roam = false;
        bool longWalk = false;
        if (ScreenBounds.Multi && !awayFromCursor)
        {
            // more than one screen: away from home (where you last put it) it heads back; at home, now and then it wanders over
            if (homeSet && ScreenBounds.ScreenIndexAt(home) != ScreenBounds.ScreenIndexAt(body.Center))
            {
                if (Random.value < 0.75f) dir = home.x > x ? 1 : -1;
                roam = (dir > 0) == (home.x > x);
            }
            else longWalk = roam = Random.value < RoamChance;
        }
        hopsLeft = awayFromCursor ? Random.Range(2, 4) : Random.Range(hopsPerWalk.x, hopsPerWalk.y + 1);
        if (longWalk) hopsLeft += Random.Range(4, 9); // (a proper wander)
        hop = Hop.Squash;
        hopT = 0f;
        Enter(State.Hopping);
        chasing = leapPlanned = false;
    }

    void UpdateHop(float dt)
    {
        if (riding) { UpdateRide(dt); return; }
        if (gliding) { UpdateGlide(dt); return; }
        if (floating) { UpdateFloat(dt); return; }
        if (jetting) { UpdateJet(dt); return; }
        if (towering) { UpdateTower(dt); return; }
        if (pouncing) { UpdatePounce(dt); return; }
        hopT += dt;
        switch (hop)
        {
            case Hop.Pause:
                body.Pose = Vector2.one;
                if (chasing && TryCatch(Time.time)) { hop = Hop.Air; hopT = 0f; break; } // waiting under a falling snack: jump to meet it
                // on its feet and upright again before the next hop (a hop off a wobbly landing tumbles it over)
                steadyFor = body.Grounded && Mathf.Abs(body.Angle) < 12f ? steadyFor + dt : 0f;
                if (hopT >= pauseFor && steadyFor < 0.05f && hopT < pauseFor + 1f) break;
                if (hopT >= pauseFor)
                {
                    if (!chasing && Food.HasValue && !Stuck && CanReachFood()) chasing = true; // a snack turned up mid-stroll
                    bool edgeTrip = edgeWalk && (!Food.HasValue || foodClimb);
                    if (chasing && !edgeTrip) { CheckLanding(); planAt = -10f; if (!Progress()) { Enter(State.Idle); return; } }
                    if (chasing ? !ChaseNeedsHop() : hopsLeft <= 0)
                    {
                        if (edgeTrip && EdgeArrived()) { ArriveAtEdge(); return; }
                        Enter(State.Idle);
                        return;
                    }
                    if (chasing && !edgeTrip && step.move == BuddyRoute.Move.Climb && AtTakeoff()) { StartFoodClimb(); return; }
                    if (chasing && TryRideLeg()) break; // a long way along here: on the skateboard
                    hop = Hop.Squash; hopT = 0f;
                    if (chasing) leapPlanned = !edgeTrip && step.move != BuddyRoute.Move.Walk && step.move != BuddyRoute.Move.Drop && AtTakeoff();
                }
                break;

            case Hop.Squash:
                if (chasing)
                {
                    // a leap gets a deeper, longer wind-up
                    float st = leapPlanned ? squashTime * 2f : squashTime;
                    Vector2 sp = leapPlanned ? Vector2.one + (squashPose - Vector2.one) * 1.4f : squashPose;
                    body.Pose = Vector2.Lerp(Vector2.one, sp, Smooth(hopT / st));
                    if (hopT < st) break;
                    bool trip = edgeWalk && (!Food.HasValue || foodClimb);
                    if (!trip && CanReachFood() && step.move == BuddyRoute.Move.Climb && AtTakeoff()) { StartFoodClimb(); return; }
                    if (!LaunchChase()) { Enter(State.Idle); return; }
                    hop = Hop.Air; hopT = 0f;
                    break;
                }
                body.Pose = Vector2.Lerp(Vector2.one, squashPose, Smooth(hopT / squashTime));
                if (hopT >= squashTime)
                {
                    // on a window: usually turn around before hopping off its edge
                    if (body.OnPlatform)
                    {
                        Vector2 span = body.SupportSpan;
                        float vy = hopUp * Mathf.Sqrt(S);
                        float land = body.Center.x + dir * hopSpeed * S * (2f * vy / Mathf.Max(body.gravity, 0.01f));
                        float edge = body.size * 0.3f;
                        if ((land > span.y - edge || land < span.x + edge) && Random.value < stayOnWindow) dir = -dir;
                    }

                    // turn around before the screen edge, and before a window side
                    Rect r = ScreenBounds.WorldRect;
                    float margin = body.size * 1.2f;
                    if (body.Center.x < r.xMin + margin) dir = 1;
                    else if (body.Center.x > r.xMax - margin) dir = -1;
                    else if (ScreenBounds.Multi)
                    {
                        // more than one screen: it mostly stays on its own (the stroll turns at its edge), now and then it
                        // goes on over (not down a long drop, nor up more than a hop)
                        float x0 = body.Center.x;
                        bool onFloor = !body.OnPlatform && body.Grounded;
                        Vector2 span = body.SupportSpan;
                        // its screen: the one it stands on (hanging over the end of its floor: still that one)
                        Rect own = ScreenBounds.ScreenRectAt(new Vector2(onFloor ? Mathf.Clamp(x0, span.x + 0.01f, span.y - 0.01f) : x0, body.Extent.yMin + 0.1f));
                        float end = dir < 0 ? own.xMin : own.xMax;
                        if (onFloor) end = dir < 0 ? Mathf.Max(end, span.x) : Mathf.Min(end, span.y); // (the end of its floor)
                        float vy = hopUp * Mathf.Sqrt(S), land = x0 + dir * hopSpeed * S * (2f * vy / Mathf.Max(body.gravity, 0.01f));
                        bool atEdge = dir < 0 ? Mathf.Min(x0 - margin, land - body.size * 0.5f) < end : Mathf.Max(x0 + margin, land + body.size * 0.5f) > end;
                        if (atEdge && (!roam || !CanCross(dir, end))) dir = -dir;
                    }
                    float stepUp = ScreenBounds.Multi && roam ? ScreenStep(dir) : 0f;
                    if (stepUp > 0f) // the next screen's floor, a little higher: a bigger hop up onto it
                        body.Hop(new Vector2(dir * hopSpeed * S, GummyBody.JumpSpeed(stepUp + 0.35f * S, body.gravity, body.Drag)));
                    else
                    {
                        if (BlockedByWindow(dir)) dir = -dir;
                        body.Hop(new Vector2(dir * hopSpeed * S, hopUp * Mathf.Sqrt(S)));
                    }
                    hopsLeft--;
                    hop = Hop.Air; hopT = 0f;
                }
                break;

            case Hop.Air:
                // jumped up beside a window: once it's above the top, over onto it
                if (mantle && body.Extent.yMin > mantleY + 0.02f)
                {
                    // aim well onto the top (at the snack if it's up there, or at least a body width in from the edge)
                    float target = Food.HasValue && Mathf.Abs(FoodSurface - mantleY) < 0.1f ? Food.Value.x : mantleX;
                    float inside = body.Center.x + mantleDir * body.Extent.width * 1.2f;
                    if ((target - inside) * mantleDir < 0f) target = inside;
                    body.Hop(new Vector2(Mathf.Clamp((target - body.Center.x) / 0.35f - body.Velocity.x, -6f, 6f), 0.5f), 0.6f);
                    mantle = false;
                }
                // stretch on take-off, relax in the air
                body.Pose = Vector2.Lerp(launchPose, Vector2.one, Smooth(hopT / 0.15f));
                if (hopT > 0.1f && body.Grounded)
                {
                    hop = Hop.Pause; hopT = 0f;
                    mantle = false;
                    pauseFor = chasing ? Random.Range(0.1f, 0.22f) / Mathf.Clamp(Hurry, 1f, 2.5f) : Random.Range(pauseBetweenHops.x, pauseBetweenHops.y);
                }
                else if (hopT > (chasing ? 3f : 2f)) Enter(State.Recover); // got knocked away / landed somewhere odd
                break;
        }
    }

    // ------------------------------------------------------------------ snacks

    /// <summary>Wake up now (a snack landed nearby, or the cursor came close).</summary>
    public void WakeUp()
    {
        if (Current != State.Asleep) return;
        lastAttention = Time.time;
        if (face) face.Asleep = false;
        if (!body.Held && !body.IsStuck && body.Grounded) body.Hop(new Vector2(0f, hopUp * 0.6f * Mathf.Sqrt(S))); // startled
        Enter(State.Recover);
    }

    /// <summary>No nodding off just now (a game's on: the floor is lava).</summary>
    public void KeepAwake() => lastAttention = Time.time;

    float lingerUntil = -1f;

    /// <summary>How much of a hurry it's in when it goes after something (1 = normal; the laser dot: zoomies).</summary>
    public float Hurry { get; set; } = 1f;
    /// <summary>Hard landings don't make it dizzy until then (its own moves: a stomp).</summary>
    public float NoDizzyUntil { get; set; } = -1f;
    /// <summary>Tucked up (squashed round) until then, while it sits or flies (a cannonball: BuddyPit).</summary>
    public float TuckUntil { get; set; } = -1f;
    static readonly Vector2 TuckPose = new Vector2(1.12f, 0.89f);

    /// <summary>Asleep for the night (Bedtime): it lies down, and your cursor coming close doesn't wake it (grabbing it does).</summary>
    public bool DeepSleep { get; set; }
    static readonly Vector2 LiePose = new Vector2(1.13f, 0.88f);
    float actFrom = -10f, actLen;
    Vector2 actPose = Vector2.one;

    /// <summary>Hold a pose for a moment while it sits about (a yawn, a stretch): in, held, eased back.</summary>
    public void Act(Vector2 pose, float seconds)
    {
        actPose = pose;
        actFrom = Time.time;
        actLen = Mathf.Max(seconds, 0.05f);
    }

    Vector2 ActPose(float now)
    {
        float u = (now - actFrom) / actLen;
        float k = u < 0.3f ? Smooth(u / 0.3f) : u > 0.7f ? Smooth((1f - u) / 0.3f) : 1f;
        return Vector2.Lerp(Vector2.one, actPose, k);
    }

    /// <summary>Grumpy for a while (e.g. woken up in the night): the grumpy face and the little anger marks.</summary>
    public void Grumpy(float seconds)
    {
        annoyedUntil = Time.time + seconds;
        happyUntil = -1f;
        nextAnger = Time.time;
    }

    /// <summary>Stay where it is for a while (no strolls or adventures, stays awake), e.g. while chatting. Snacks still win.</summary>
    public void Linger(float seconds)
    {
        lingerUntil = Mathf.Max(lingerUntil, Time.time + seconds);
        lastAttention = Time.time;
    }

    /// <summary>
    /// Hop out of the way (BuddyDodge): sideways (dir) at this speed and up this fast. Only when it's sitting around or
    /// between hops, on its feet; it lands and carries on (a chase picks up again). False if it can't right now.
    /// </summary>
    public bool Dodge(float dir, float speed, float up)
    {
        if (body.Held || body.IsStuck || !body.Grounded || riding || gliding || floating || jetting || towering || body.OnRope) return false;
        bool between = Current == State.Hopping && hop == Hop.Pause;
        if (Current != State.Idle && !between) return false;
        body.Hop(new Vector2(dir * speed - body.Velocity.x, up - body.Velocity.y), 0.7f);
        Enter(State.Recover); // (lands, settles, carries on)
        lastAttention = Time.time;
        return true;
    }

    /// <summary>Just ate something: chews, happy face, a heart.</summary>
    public void Treat()
    {
        float now = Time.time;
        chewUntil = now + ChewTime;
        happyUntil = Mathf.Max(happyUntil, now + 1.2f);
        annoyedUntil = -1f;
        lastAttention = now;
        SpawnIcon(heartSprite, heartColor, new Vector2(0f, 0.35f), 1.4f, 0.45f, 0.06f, 0.18f, 0.06f);
    }

    void StartChase()
    {
        CheckLanding();
        chaseFood = Food.Value;
        hop = Hop.Squash;
        hopT = 0f;
        Enter(State.Hopping);
        chasing = true;
        planAt = -10f;
        leapPlanned = CanReachFood() && step.move != BuddyRoute.Move.Walk && step.move != BuddyRoute.Move.Drop && AtTakeoff();
        TryRideLeg(); // far: on the skateboard
    }

    // ------------------------------------------------------------------ snacks: the route there

    BuddyRoute.Step step;               // the next move towards the snack
    bool stepOk, foodClimb, landingCheck;
    float planAt = -10f, bestCost = float.MaxValue;
    Vector2 planFood;
    string landingKey;
    float landingY;
    int stall, layout;
    Vector2 progressFood;
    bool tracking;
    readonly System.Collections.Generic.Dictionary<string, float> banned = new System.Collections.Generic.Dictionary<string, float>();
    readonly System.Collections.Generic.Dictionary<string, int> fails = new System.Collections.Generic.Dictionary<string, int>();

    /// <summary>It tried and got nowhere (blocked, or the route keeps failing): BuddyEater leaves that snack for a while.</summary>
    public bool Stuck { get; set; }

    float Reaction => skills ? skills.Reaction : 0.25f;
    Vector2 chaseFood = new Vector2(1e6f, 1e6f);
    // already on its way to this snack (it just landed from a move): no thinking it over again
    bool OnTheWay => Food.HasValue && (Food.Value - chaseFood).sqrMagnitude < 0.04f;
    float Aim() => skills && skills.AimError > 0f ? Random.Range(-1f, 1f) * skills.AimError * body.size : 0f;

    BuddyEspresso espresso;
    BuddyPogo pogo;

    BuddyRoute.Abilities Abilities()
    {
        var a = skills ? skills.Abilities(leapHeight, leapReach * S) : BuddySkills.Max(S, leapHeight, leapReach);
        if (!rope) { a.rope = 0f; a.zip = 0f; a.zipRide = false; } // no rope to swing on (or to hang on to a zipline with)
        if (!board) a.skate = 0f; // no skateboard
        if (!plane) a.glide = 0f; // no paper plane
        if (!balloon) a.balloon = 0f; // no balloon
        if (!jetpack) a.jet = 0f; // no jetpack
        if (espresso && espresso.Buzzing) a.wallUp += espresso.WallBoost; // (buzzing on espresso: it runs up window sides)
        if (pogo && pogo.Has) a.leapUp = Mathf.Max(a.leapUp, Mathf.Min(skills ? skills.LeapHeight : leapHeight, leapHeight) + pogo.LeapBoost); // (on its pogo fork)
        return a;
    }

    BuddyRoute.Body Me() => new BuddyRoute.Body
    {
        x = body.Center.x, y = body.Extent.yMin, halfW = Mathf.Max(body.Extent.width, body.size * 0.8f) * 0.5f,
        height = Mathf.Max(body.Extent.height, body.size * 0.7f), onWindow = body.OnPlatform, solid = body.solidWindows,
        g = body.gravity, drag = body.Drag, cOff = Mathf.Max(body.Center.y - body.Extent.yMin, body.size * 0.3f), size = body.size,
    };

    bool IsBanned(string key) => banned.TryGetValue(key, out float until) && Time.time < until;

    // the windows moved: what didn't work before might now
    void CheckLayout()
    {
        int h = BuddyRoute.LayoutHash();
        if (h == layout) return;
        layout = h;
        banned.Clear();
        fails.Clear();
    }

    /// <summary>Can it get to this spot (on the surface at surfaceY) from where it stands, with what it knows? cost ~ distance + effort.</summary>
    public bool PlanTo(Vector2 at, float surfaceY, out float cost)
    {
        cost = float.MaxValue;
        if (!body.Grounded || body.Held || body.IsStuck) return false;
        CheckLayout();
        return BuddyRoute.Find(Me(), new Vector2(at.x, surfaceY), Abilities(), IsBanned, out _, out cost, out _);
    }

    /// <summary>Could it get there once it knows every trick? (To tell "too high for me yet" from "too high".)</summary>
    public bool CouldEverReach(Vector2 at, float surfaceY)
    {
        if (!body.Grounded) return false;
        return BuddyRoute.Find(Me(), new Vector2(at.x, surfaceY), BuddySkills.Max(S, leapHeight, leapReach), IsBanned, out _, out _, out _);
    }

    bool CanReachFood()
    {
        if (!Food.HasValue) return false;
        if (Time.time - planAt > 0.2f || (Food.Value - planFood).sqrMagnitude > 0.0025f)
        {
            planAt = Time.time;
            planFood = Food.Value;
            CheckLayout();
            float c = float.MaxValue;
            stepOk = body.Grounded && BuddyRoute.Find(Me(), new Vector2(Food.Value.x, FoodSurface), Abilities(), IsBanned, out step, out c, out _);
            curCost = stepOk ? c : float.MaxValue;
        }
        return stepOk;
    }
    float curCost = float.MaxValue;

    bool ChaseNeedsHop()
    {
        if (edgeWalk && (!Food.HasValue || foodClimb)) return !EdgeArrived() && stateTime < (foodClimb ? 15f : 10f); // walking over to an edge
        if (!CanReachFood()) return false;
        float dx = Food.Value.x - body.Center.x;
        return !(step.move == BuddyRoute.Move.Walk && Mathf.Abs(dx) < 0.3f * body.size); // not there yet
    }

    // standing where the next move starts from
    bool AtTakeoff()
    {
        if (step.move == BuddyRoute.Move.Walk) return true;
        if (step.wall)
        {
            float gap = step.side > 0 ? step.wallX - body.Extent.xMax : body.Extent.xMin - step.wallX;
            return gap >= 0.03f && gap <= 1.2f * body.size;
        }
        return Mathf.Abs(step.takeoffX - body.Center.x) < 0.3f * body.size;
    }

    bool LaunchChase()
    {
        if (edgeWalk && (!Food.HasValue || foodClimb))
            return HopToward(edgeTarget.x - body.Center.x, true);
        if (!Food.HasValue || !CanReachFood()) return false;
        float x = body.Center.x;
        if (step.move == BuddyRoute.Move.Walk) return HopToward(step.landX - x + Aim(), true);
        if (!AtTakeoff()) return HopToward(step.takeoffX - x + Aim(), true); // over to where the move starts
        Expect();
        float h = step.landY - body.Extent.yMin;
        switch (step.move)
        {
            case BuddyRoute.Move.Drop:
                return HopToward(step.landX - x, false); // off the edge
            case BuddyRoute.Move.Climb:
                return false; // (StartFoodClimb does it)
            case BuddyRoute.Move.Swing:
            case BuddyRoute.Move.Grapple:
                return StartRopeMove(step, false);
            case BuddyRoute.Move.Glide:
                if (StartGlide(step, false)) return true;
                break; // (no plane after all: an ordinary jump)
            case BuddyRoute.Move.Zip:
                return StartZip(step, false);
            case BuddyRoute.Move.Float:
                if (StartFloat(step, false)) return true;
                banned[step.key] = Time.time + 120f; // (no balloon after all)
                return false;
            case BuddyRoute.Move.Jet:
                if (StartJet(step, false)) return true;
                banned[step.key] = Time.time + 120f; // (no jetpack after all)
                return false;
        }
        if (step.wall)
        {
            // up beside the window side: jump straight up, then over onto the top
            body.Hop(new Vector2(-body.Velocity.x, GummyBody.JumpSpeed(h + 0.9f * S, body.gravity, body.Drag)), 3f);
            mantle = true;
            mantleDir = step.side;
            mantleY = step.landY;
            mantleX = step.landX;
            return true;
        }
        if (!LeapVelocity(h, step.landX - x, Mathf.Max(h, 0.5f), out Vector2 v0, out float tLand)) { banned[step.key] = Time.time + 120f; return false; }
        body.Hop(v0, tLand + 0.35f); // no sticking on the way up or on landing
        return true;
    }

    // up a window side to a snack it can't jump to: hop over to the side, climb, over onto the top
    void StartFoodClimb()
    {
        Expect();
        WindowBox w = step.box;
        edgeSide = step.side;
        edge = new GummyBody.Edge { axis = 0, pos = step.wallX, sign = -step.side, from = w.yMin, to = w.yMax, surface = w.id };
        edgeKind = EdgeKind.Climb;
        GoToEdge();
        foodClimb = true;
    }

    // about to do a move: check where it lands
    void Expect()
    {
        landingCheck = true;
        landingKey = step.key;
        landingY = step.landY;
    }

    void CheckLanding()
    {
        if (!landingCheck || !body.Grounded) return;
        landingCheck = false;
        if (Mathf.Abs(body.Extent.yMin - landingY) < 0.15f) { fails.Remove(landingKey); return; }
        int n = fails.TryGetValue(landingKey, out int f) ? f + 1 : 1;
        fails[landingKey] = n;
        if (n >= 2) banned[landingKey] = Time.time + 120f; // that doesn't work from here: another way (or not for a while)
    }

    // getting closer? hopping in place (a friend in the way, a move that keeps failing) = give up on this snack for now
    bool Progress()
    {
        if (!Food.HasValue) return true;
        if (!tracking || (Food.Value - progressFood).sqrMagnitude > 0.01f) { tracking = true; progressFood = Food.Value; bestCost = float.MaxValue; stall = 0; }
        if (!CanReachFood()) { Stuck = true; return false; }
        if (curCost < bestCost - 0.15f) { bestCost = curCost; stall = 0; return true; }
        if (++stall < 6) return true;
        stall = 0;
        Stuck = true;
        return false;
    }

    float lastCatch = -10f;

    // Catch: a snack is falling right above it: jump up to meet it (it eats whatever touches its mouth)
    bool TryCatch(float now)
    {
        if (!skills || !skills.Has(BuddySkills.Skill.Catch) || !body.Grounded || now - lastCatch < 1f) return false;
        float top = body.Extent.yMax - body.Center.y;
        foreach (var s in Snack.All)
        {
            if (s.Held || s.Resting || s.Velocity.y > -0.3f) continue;
            Vector2 rel = s.Position - body.Center;
            float above = rel.y - top;
            if (Mathf.Abs(rel.x) > body.size * 0.7f || above < 0.2f || above > 3.5f * S) continue;
            if (above / Mathf.Max(-s.Velocity.y, 1f) > 0.3f) continue; // not yet: jump when it's about to arrive
            lastCatch = now;
            float meet = above * 0.5f; // about half way
            body.Hop(new Vector2(rel.x * 3f, GummyBody.JumpSpeed(meet + 0.1f, body.gravity, body.Drag)), 0.8f);
            Moment?.Invoke("catch");
            return true;
        }
        return false;
    }

    // hop towards it (off the edge if it's below), landing on the spot rather than past it
    bool HopToward(float dx, bool exact)
    {
        dir = dx >= 0f ? 1 : -1;
        float hz = Mathf.Clamp(Hurry, 1f, 2.5f);
        float vy = hopUp * Mathf.Sqrt(S) * 1.1f, maxVx = hopSpeed * Mathf.Sqrt(S) * 1.6f, vx = dir * maxVx; // eager: bigger hops than a stroll
        if (hz > 1f) { vy *= Mathf.Lerp(1f, 0.8f, Mathf.Clamp01(hz - 1f)); maxVx *= hz; vx = dir * maxVx; } // in a hurry: long, flat, quick hops
        // under a low window: flatter hops (it doesn't bonk its head on the window and bounce back)
        float room = Headroom(dir * Mathf.Min(Mathf.Abs(dx), maxVx * 0.5f));
        if (room < 2f) vy = Mathf.Min(vy, GummyBody.JumpSpeed(Mathf.Max(room - 0.05f, 0.08f), body.gravity, Drag));
        if (exact)
        {
            float g = body.gravity, lam = Mathf.Max(Drag, 1e-3f);
            float air = FallTime(vy, 0f, g, lam);
            vx = Mathf.Clamp(dx * lam / (1f - Mathf.Exp(-lam * air)), -maxVx, maxVx);
        }
        body.Hop(new Vector2(vx, vy), 0.9f); // bigger hops wobble more on landing: don't stick to the floor
        return true;
    }

    // how much room there is above its head here and a little way along (the bottom of a solid window over it), up to 2
    float Headroom(float along)
    {
        if (!body.solidWindows && !ScreenBounds.Multi) return 2f;
        Rect e = body.Extent;
        float x0 = Mathf.Min(e.xMin, e.xMin + along), x1 = Mathf.Max(e.xMax, e.xMax + along), room = 2f;
        var wins = DesktopPlatforms.Windows;
        for (int k = 0; k < wins.Count; k++)
        {
            WindowBox w = wins[k];
            if (!w.movable || (!body.solidWindows && !w.screen) || w.xMax < x0 || w.xMin > x1 || w.yMin < e.yMax - 0.02f) continue;
            room = Mathf.Min(room, w.yMin - e.yMax);
        }
        return room;
    }

    // ballistics with the body's air drag (GummyBody damps every 1/240 s step)
    float Drag => -Mathf.Log(1f - Mathf.Clamp(body.damping, 0f, 0.5f)) * 240f;

    static float Rise(float vy, float t, float g, float lam) => (vy + g / lam) * (1f - Mathf.Exp(-lam * t)) / lam - g * t / lam;

    /// <summary>Time until a jump with vertical speed vy comes back down to height h (on the way down).</summary>
    static float FallTime(float vy, float h, float g, float lam)
    {
        float lo = Mathf.Log(1f + vy * lam / g) / lam, hi = lo + 5f; // from the peak
        for (int i = 0; i < 40; i++)
        {
            float mid = 0.5f * (lo + hi);
            if (Rise(vy, mid, g, lam) > h) lo = mid; else hi = mid;
        }
        return hi;
    }

    /// <summary>Launch velocity that peaks leapMargin above a surface h higher up and comes down on it dx to the side.</summary>
    bool LeapVelocity(float h, float dx, float maxH, out Vector2 v0, out float tLand)
    {
        v0 = Vector2.zero; tLand = 0f;
        float g = body.gravity, lam = Mathf.Max(Drag, 1e-3f), apex = Mathf.Max(h, 0f) + leapMargin * S;
        if (h > maxH + 0.05f || body.Extent.yMax + apex > ScreenBounds.CeilingAt(body.Center) - 0.05f) return false;
        float lo = 0f, hi = 80f;
        for (int i = 0; i < 40; i++)
        {
            float mid = 0.5f * (lo + hi);
            float peak = mid / lam - g / (lam * lam) * Mathf.Log(1f + mid * lam / g);
            if (peak < apex) lo = mid; else hi = mid;
        }
        tLand = FallTime(hi, h, g, lam);
        float vx = dx * lam / (1f - Mathf.Exp(-lam * tLand));
        if (Mathf.Abs(vx) > 14f) return false;
        v0 = new Vector2(vx, hi);
        return true;
    }

    static Vector2 Breathe(float amount, float t)
    {
        float b = amount * Mathf.Sin(t * 2f * Mathf.PI / 3f); // one breath every 3 s
        return new Vector2(1f - b, 1f + b);
    }

    static float Smooth(float u) { u = Mathf.Clamp01(u); return u * u * (3f - 2f * u); }

    // ------------------------------------------------------------------ floating icons

    void BuildIcons()
    {
        iconHolder = new GameObject(name + " Icons");
        zSprite = BakeIcon(out zTex, "Zzz", ZShape);
        heartSprite = BakeIcon(out heartTex, "Heart", HeartShape);
        angerSprite = BakeIcon(out angerTex, "Anger", AngerShape);
        for (int i = 0; i < IconCount; i++)
        {
            var go = new GameObject("Icon");
            go.transform.SetParent(iconHolder.transform, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sortingOrder = body.sortingOrder + 5;
            if (body.spriteMaterial) sr.sharedMaterial = body.spriteMaterial;
            sr.enabled = false;
            icons[i] = sr;
        }
    }

    /// <summary>Offset, rise, wobble and sizes are fractions of the body size.</summary>
    void SpawnIcon(Sprite sprite, Color color, Vector2 offset, float life, float rise, float wobble, float scale0, float grow)
    {
        int i = nextIcon;
        nextIcon = (nextIcon + 1) % IconCount;
        iconData[i] = new Icon
        {
            start = body.Center + offset * body.size, color = color, age = 0f, life = life,
            rise = rise, wobble = wobble, scale0 = scale0, grow = grow
        };
        icons[i].sprite = sprite;
        icons[i].enabled = true;
    }

    void UpdateIcons(float dt)
    {
        float s = body.size;
        for (int i = 0; i < IconCount; i++)
        {
            if (!icons[i] || !icons[i].enabled) continue;
            ref Icon ic = ref iconData[i];
            ic.age += dt;
            float u = ic.age / ic.life;
            if (u >= 1f) { icons[i].enabled = false; continue; }
            Vector2 pos = ic.start + new Vector2(ic.wobble * s * Mathf.Sin(u * 6f), ic.rise * s * u);
            icons[i].transform.position = new Vector3(pos.x, pos.y, 0f);
            float k = (ic.scale0 + ic.grow * u) * s;
            icons[i].transform.localScale = new Vector3(k, k, 1f);
            Color c = ic.color;
            c.a *= Mathf.Min(u * 5f, 1f) * (1f - u);
            icons[i].color = c;
        }
    }

    // shapes in -1..1 (white, tinted by the renderer); return coverage 0..1 for a point
    delegate float Shape(Vector2 p, float texel);

    static float ZShape(Vector2 p, float texel)
    {
        Vector2 a = new Vector2(-0.6f, 0.6f), b = new Vector2(0.6f, 0.6f), c = new Vector2(-0.6f, -0.6f), d = new Vector2(0.6f, -0.6f);
        float dist = Mathf.Min(Seg(p, a, b), Mathf.Min(Seg(p, b, c), Seg(p, c, d))) - 0.13f; // top bar, diagonal, bottom bar
        return Mathf.Clamp01(0.5f - dist / texel);
    }

    static float HeartShape(Vector2 p, float texel)
    {
        // classic heart curve (x^2 + y^2 - 1)^3 - x^2 y^3 <= 0, supersampled
        float cov = 0f;
        for (int sy = 0; sy < 4; sy++)
            for (int sx = 0; sx < 4; sx++)
            {
                float x = (p.x + (sx - 1.5f) * texel * 0.25f) * 1.3f;
                float y = (p.y + (sy - 1.5f) * texel * 0.25f) * 1.3f + 0.15f;
                float q = x * x + y * y - 1f;
                if (q * q * q - x * x * y * y * y <= 0f) cov += 1f / 16f;
            }
        return cov;
    }

    static float AngerShape(Vector2 p, float texel)
    {
        // four little corner brackets around the centre (the classic "annoyed" vein mark)
        float dist = float.MaxValue;
        for (int qx = -1; qx <= 1; qx += 2)
            for (int qy = -1; qy <= 1; qy += 2)
            {
                var corner = new Vector2(0.18f * qx, 0.18f * qy);
                dist = Mathf.Min(dist, Seg(p, corner, corner + new Vector2(0.45f * qx, 0f)));
                dist = Mathf.Min(dist, Seg(p, corner, corner + new Vector2(0f, 0.45f * qy)));
            }
        return Mathf.Clamp01(0.5f - (dist - 0.1f) / texel);
    }

    static Sprite BakeIcon(out Texture2D tex, string name, Shape shape)
    {
        const int res = 32;
        tex = new Texture2D(res, res, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = name
        };
        var px = new Color32[res * res];
        const float texel = 2f / res;
        for (int y = 0; y < res; y++)
            for (int x = 0; x < res; x++)
            {
                var p = new Vector2((x + 0.5f) * texel - 1f, (y + 0.5f) * texel - 1f);
                px[y * res + x] = new Color32(255, 255, 255, (byte)(shape(p, texel) * 255f));
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return Sprite.Create(tex, new Rect(0, 0, res, res), new Vector2(0.5f, 0.5f), res); // 1 unit wide
    }

    static float Seg(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 pa = p - a, ba = b - a;
        float h = Mathf.Clamp01(Vector2.Dot(pa, ba) / Vector2.Dot(ba, ba));
        return (pa - ba * h).magnitude;
    }
}
