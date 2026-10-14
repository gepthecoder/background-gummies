using UnityEngine;

/// <summary>
/// The burger's gadget (its level-10 gadget: BuddySkills.Kit.Pogo): a giant fork it bounces on like a pogo stick. Its
/// big jumps go higher (the route planner gets a higher leap: up onto windows it couldn't reach), the fork springs out
/// under it with a BOING, and on landing it wobbles... and now and then tips over ("WHOA WHOA WHOA"). Bored (or you say
/// "pogo"), it bounces on the spot for a bit.
/// </summary>
[RequireComponent(typeof(GummyBody))]
[DisallowMultipleComponent]
public class BuddyPogo : MonoBehaviour
{
    [Tooltip("Sprite material (WhipIt > Setup assigns it).")]
    public Material material;
    [Tooltip("How much higher it leaps with the fork (world units, x size).")]
    public float boost = 2.8f;
    [Range(0f, 1f), Tooltip("Chance it tips over when it lands a pogo jump.")]
    public float tipChance = 0.2f;
    [Tooltip("Bored: chance per minute to bounce around on it.")]
    [Range(0f, 1f)] public float funPerMinute = 0.3f;
    public AudioClip boingClip;
    [Range(0f, 1f)] public float volume = 0.5f;

    /// <summary>Has it (and is old enough).</summary>
    public bool Has => skills && skills.Gear == BuddySkills.Kit.Pogo && skills.Has(BuddySkills.Skill.Gadget);
    /// <summary>How much higher it leaps with it (0 = no fork).</summary>
    public float LeapBoost => Has ? boost * S : 0f;
    /// <summary>On the fork right now (a pogo jump or bouncing).</summary>
    public bool OnFork => forkT >= 0f;
    public int Jumps { get; private set; }
    public int TippedOver { get; private set; }

    GummyBody body;
    GummyBrain brain;
    BuddySkills skills;
    BuddySpeech speech;
    AudioSource sfx;
    SpriteRenderer fork;
    float forkT = -1f, squish, wasVy, lastLine = -100f, nextFun;
    bool wasGrounded = true, airborne;
    int bounces;
    static Sprite s_fork;
    static bool s_baked;
    static readonly System.Random s_fx = new System.Random(29);

    float S => body.size / 0.6f;

    void Awake()
    {
        body = GetComponent<GummyBody>();
        brain = GetComponent<GummyBrain>();
        skills = GetComponent<BuddySkills>();
        speech = GetComponent<BuddySpeech>();
        var go = new GameObject("Pogo Fork");
        go.transform.SetParent(transform, false); // (a child: hiding behind a window masks it too)
        fork = go.AddComponent<SpriteRenderer>();
        fork.sortingOrder = body.sortingOrder - 1; // behind the bun
        fork.color = Color.white;
        Material m = material ? material : body.spriteMaterial;
        if (m) fork.sharedMaterial = m;
        fork.enabled = false;
        sfx = gameObject.AddComponent<AudioSource>();
        sfx.playOnAwake = false;
        sfx.spatialBlend = 0f;
        nextFun = Time.time + 20f;
    }

    void OnDisable() { forkT = -1f; bounces = 0; if (fork) fork.enabled = false; }

    /// <summary>Bounce on the spot for a bit (chat "pogo"). False if it can't right now.</summary>
    public bool Bounce()
    {
        if (!Has || body.Held || !body.Grounded || bounces > 0 || (brain && !brain.IsFree)) return false;
        bounces = 3 + (int)Rnd(0f, 2.99f);
        if (brain) brain.Linger(bounces * 1.2f + 1f);
        Boing(Rnd(5f, 6.5f) * Mathf.Sqrt(S));
        Say(0.8f, "BOING BOING!", "POGO TIME!", "WHEEE!");
        return true;
    }

    /// <summary>The fork springs out under it for a big jump it's making right now (a pogo dunk, BuddyHoops).</summary>
    public void Spring()
    {
        if (!Has) return;
        Jumps++;
        forkT = 0f;
        squish = 1f;
        airborne = false;
        wasGrounded = false;
        if (boingClip && sfx) { sfx.pitch = Rnd(1.05f, 1.2f); sfx.PlayOneShot(boingClip, volume); }
    }

    void Boing(float up)
    {
        Jumps++;
        forkT = 0f;
        squish = 1f;
        body.Hop(new Vector2(Rnd(-0.4f, 0.4f) - body.Velocity.x * 0.5f, up - body.Velocity.y), 0.4f);
        if (boingClip && sfx) { sfx.pitch = Rnd(1.15f, 1.35f); sfx.PlayOneShot(boingClip, volume); }
    }

    void Say(float chance, params string[] l)
    {
        if (!speech || Time.time - lastLine < 3f || Rnd(0f, 1f) > chance) return;
        if (speech.Say(l[(int)Rnd(0f, l.Length - 0.001f)], 0f, false)) lastLine = Time.time;
    }

    static float Rnd(float a, float b) => a + (float)s_fx.NextDouble() * (b - a);

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("BuddyPogo.Update", t0); }
    void UpdateTimed()
    {
        if (!Has) { if (fork.enabled) fork.enabled = false; forkT = -1f; return; }
        float now = Time.time, dt = Time.deltaTime;
        bool grounded = body.Grounded && !body.Held;
        float vy = body.Velocity.y;

        // a big jump off the ground (one of its leaps): the fork springs out under it
        if (!grounded && wasGrounded && !body.Held && vy > 6.5f * Mathf.Sqrt(S) && forkT < 0f && !body.OnRope && !(brain && (brain.Floating || brain.Jetting || brain.Towering)))
        {
            Jumps++;
            forkT = 0f;
            squish = 1f;
            if (boingClip && sfx) { sfx.pitch = Rnd(1.05f, 1.2f); sfx.PlayOneShot(boingClip, volume * 0.8f); }
            if (Rnd(0f, 1f) < 0.3f) Say(1f, "BOING!", "POGO FORK!", "UP WE GO!");
        }
        if (!grounded) airborne = true;

        // landed off the fork: bounce again (bouncing on the spot), or put it away; now and then it tips over
        if (grounded && airborne && forkT >= 0f)
        {
            airborne = false;
            squish = 1f;
            if (bounces > 0 && --bounces > 0) Boing(Rnd(5f, 6.5f) * Mathf.Sqrt(S));
            else
            {
                forkT = -0.3f; // (puts it away in a moment)
                if (Rnd(0f, 1f) < tipChance)
                {
                    TippedOver++;
                    int side = Rnd(0f, 1f) < 0.5f ? -1 : 1;
                    body.Hop(new Vector2(side * 1.2f, 1.5f), 0.3f);
                    body.Spin(-side * 6f);
                    Say(1f, "WHOA WHOA WHOA", "TIMBERRR", "OOF. BUN DOWN");
                }
            }
        }
        if (grounded && !airborne && forkT < 0f && forkT > -1f) { forkT += dt; if (forkT >= 0f) forkT = -1f; } // (putting it away)
        if (forkT >= 0f) forkT += dt;
        squish = Mathf.MoveTowards(squish, 0f, dt * 5f);
        wasGrounded = grounded;
        wasVy = vy;

        // bored: bounce around a bit
        if (bounces == 0 && brain && brain.IsFree && grounded && now >= nextFun)
        {
            nextFun = now + 10f;
            if (Rnd(0f, 1f) < funPerMinute / 6f) Bounce();
        }
    }

    void LateUpdate() { long t0 = WhipProf.T(); LateUpdateTimed(); WhipProf.Add("BuddyPogo.LateUpdate", t0); }
    void LateUpdateTimed()
    {
        if (!s_baked) { s_fork = Bake(); s_baked = true; }
        bool show = Has && (forkT >= 0f || (forkT < -0.001f && forkT > -1f));
        fork.enabled = show;
        if (!show) return;
        float ang = body.Angle;
        var rot = Quaternion.Euler(0f, 0f, ang);
        Vector2 up = rot * Vector2.up;
        float h = Mathf.Max(body.Span.y, body.size * 0.5f), w = Mathf.Max(body.Span.x, body.size * 0.5f);
        // the handle in its middle, the tines down below it (springy: shorter on take-off / landing)
        float len = h * (1.25f - 0.3f * squish) * (forkT < 0f ? Mathf.Clamp01(-forkT / 0.3f) : Mathf.Clamp01(forkT / 0.08f + 0.3f));
        Vector2 top = body.Center + up * (0.05f * h);
        fork.sprite = s_fork;
        fork.transform.position = new Vector3(top.x, top.y, 0f);
        fork.transform.rotation = rot;
        fork.transform.localScale = new Vector3(w * 0.5f, len / 3f, 1f); // (the sprite is 1 x 3, pivot at its top)
    }

    static Sprite Bake()
    {
        const int W = 24, H = 72;
        var tex = new Texture2D(W, H, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "Pogo Fork" };
        var px = new Color32[W * H];
        float texel = 1f / W;
        for (int j = 0; j < H; j++)
            for (int i = 0; i < W; i++)
            {
                Color c = ForkPaint((i + 0.5f) * texel, (j + 0.5f) * texel, texel);
                px[j * W + i] = new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), Mathf.Clamp01(c.a));
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return Sprite.Create(tex, new Rect(0, 0, W, H), new Vector2(0.5f, 1f), W); // 1 x 3 units, pivot at the top of the handle
    }

    static float Cover(float d, float texel) => Mathf.Clamp01(0.5f - d / texel);

    // a big silver fork, tines down (x 0..1, y 0..3: the handle at the top, four tines at the bottom), a spring in the middle
    internal static Color ForkPaint(float x, float y, float texel)
    {
        float d;
        float cx = Mathf.Abs(x - 0.5f);
        if (y > 1.5f) d = Mathf.Max(cx - (0.12f + 0.03f * (y - 1.5f)), y - 2.97f);                // the handle (a bit wider at the top)
        else if (y > 0.95f) d = cx - Mathf.Lerp(0.42f, 0.12f, (y - 0.95f) / 0.55f);                  // the neck widening into the head
        else
        {
            // four tines with gaps, pointed at the bottom
            float tine = Mathf.Abs(Mathf.Repeat((x - 0.08f) / 0.84f * 4f, 1f) - 0.5f) / 4f * 0.84f;     // distance to the nearest tine's middle
            float half = 0.065f * Mathf.Clamp01((y - 0.04f) / 0.25f);
            d = Mathf.Max(Mathf.Max(tine - half, cx - 0.42f), 0.02f - y);
            if (y > 0.8f) d = Mathf.Min(d, cx - 0.42f);                                               // (the head above the gaps)
        }
        float a = Cover(d, texel);
        if (a <= 0f) return new Color(0f, 0f, 0f, 0f);
        float shade = 0.7f + 0.3f * Mathf.Clamp01(1f - cx * 3f) + 0.15f * Mathf.Sin(y * 3f);
        Color c = new Color(0.82f, 0.84f, 0.88f) * shade;
        if (y > 1.55f && y < 2.1f && Mathf.Abs(Mathf.Repeat(y * 10f, 1f) - 0.5f) < 0.2f) c = new Color(0.55f, 0.57f, 0.62f); // the spring coils
        if (cx < 0.04f && y > 1.6f) c = Color.Lerp(c, Color.white, 0.6f);                         // shine
        if (d > -texel * 1.6f) c = new Color(0.42f, 0.44f, 0.5f);
        c.a = a;
        return c;
    }
}
