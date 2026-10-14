using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The dino's gadget (its level-10 gadget: BuddySkills.Kit.Stomp): the super stomp. A snack it wants sits on a window just
/// above it (too high, or not worth the climb)? It hops up and comes down hard: STOMP! A shockwave ring and dust, the
/// window it stands on wobbles, and snacks lying on window tops close above it bounce off and drop down to it. Bored (or
/// you say "stomp"), it stomps for fun. Every stomp is a RAWR moment.
/// </summary>
[RequireComponent(typeof(GummyBody))]
[DisallowMultipleComponent]
public class BuddyStomp : MonoBehaviour
{
    [Tooltip("Sprite material (WhipIt > Setup assigns it).")]
    public Material material;
    [Tooltip("Knocks snacks off window tops up to this far to each side and this high above it (x size / 0.6).")]
    public float reach = 3.5f;
    public float reachUp = 5f;
    [Tooltip("At most one stomp this often (seconds).")]
    public float cooldown = 6f;
    [Tooltip("Bored: chance per minute to stomp for fun.")]
    [Range(0f, 1f)] public float funPerMinute = 0.25f;
    public AudioClip stompClip;
    [Range(0f, 1f)] public float volume = 0.6f;

    /// <summary>Has it (and is old enough).</summary>
    public bool Has => skills && skills.Gear == BuddySkills.Kit.Stomp && skills.Has(BuddySkills.Skill.Gadget);
    public int Stomps { get; private set; }
    public int Knocked { get; private set; }
    /// <summary>Winding up or coming down right now.</summary>
    public bool Stomping => phase > 0;

    GummyBody body;
    GummyBrain brain;
    BuddySkills skills;
    BuddySpeech speech;
    BuddyEater eater;
    WindowWobble wobble;
    AudioSource sfx;
    SpriteRenderer ring;
    const int Dust = 10;
    readonly SpriteRenderer[] dust = new SpriteRenderer[Dust];
    readonly Vector2[] dustV = new Vector2[Dust];
    readonly float[] dustLife = new float[Dust];
    int phase;          // 0 none, 1 hop up, 2 slam down
    float phaseT, nextStomp, ringT = -1f, nextFun, lastLine = -100f;
    Vector2 ringAt;
    static Sprite s_ring, s_dust;
    static bool s_baked;
    static readonly System.Random s_fx = new System.Random(37);
    readonly List<Snack> hits = new List<Snack>();

    float S => body.size / 0.6f;

    void Awake()
    {
        body = GetComponent<GummyBody>();
        brain = GetComponent<GummyBrain>();
        skills = GetComponent<BuddySkills>();
        speech = GetComponent<BuddySpeech>();
        eater = GetComponent<BuddyEater>();
        wobble = GetComponent<WindowWobble>();
        ring = MakeRenderer("Stomp Ring", body.sortingOrder - 2);
        for (int i = 0; i < Dust; i++) dust[i] = MakeRenderer("Stomp Dust", body.sortingOrder + 3);
        sfx = gameObject.AddComponent<AudioSource>();
        sfx.playOnAwake = false;
        sfx.spatialBlend = 0f;
        nextFun = Time.time + 25f;
    }

    SpriteRenderer MakeRenderer(string name, int order)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sortingOrder = order;
        sr.color = Color.white;
        Material m = material ? material : body.spriteMaterial;
        if (m) sr.sharedMaterial = m;
        sr.enabled = false;
        return sr;
    }

    void OnDisable()
    {
        phase = 0;
        ringT = -1f;
        if (ring) ring.enabled = false;
        for (int i = 0; i < Dust; i++) { dustLife[i] = 0f; if (dust[i]) dust[i].enabled = false; }
    }

    /// <summary>Stomp now (chat "stomp"). False if it can't right now.</summary>
    public bool Stomp()
    {
        if (!Has || phase > 0 || body.Held || !body.Grounded || Time.time < nextStomp - cooldown + 1.5f) return false;
        phase = 1;
        phaseT = 0f;
        nextStomp = Time.time + cooldown;
        if (brain) { brain.Linger(1.6f); brain.NoDizzyUntil = Time.time + 1.6f; }
        body.Hop(new Vector2(-body.Velocity.x, 5.5f * Mathf.Sqrt(S) - body.Velocity.y), 0.3f); // up...
        return true;
    }

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("BuddyStomp.Update", t0); }
    void UpdateTimed()
    {
        if (!Has) { phase = 0; return; }
        float now = Time.time, dt = Time.deltaTime;
        if (phase == 0)
        {
            // a snack it wants on a window just above (on its feet, not too often)
            if (now >= nextStomp && body.Grounded && !body.Held && eater && SnackAbove(out Snack s) && (eater.Target == s || brain == null || brain.IsFree || brain.Stuck))
            {
                if (Stomp()) Say(0.6f, "STOMP TIME!", "WATCH THIS!", "GET DOWN HERE!");
            }
            else if (now >= nextFun && brain && brain.IsFree && body.Grounded)
            {
                nextFun = now + 10f;
                if (Rnd(0f, 1f) < funPerMinute / 6f && Stomp()) Say(0.6f, "STOMP STOMP!", "EARTHQUAKE!", "RAWR!");
            }
            return;
        }
        phaseT += dt;
        if (phase == 1)
        {
            // at the top of the hop: down, hard
            if (phaseT > 0.12f && body.Velocity.y < 1f)
            {
                phase = 2;
                phaseT = 0f;
                body.Hop(new Vector2(-body.Velocity.x, -12f * Mathf.Sqrt(S) - body.Velocity.y), 0.2f); // (hard: no dizziness from its own stomp)
                body.Pose = new Vector2(0.9f, 1.12f);
            }
            else if (phaseT > 1.2f) phase = 0;
            return;
        }
        if (body.Grounded || phaseT > 1f) { phase = 0; Land(); }
    }

    /// <summary>A stomp right where it stands, without the hop (a stomp dunk launches off it, BuddyHoops).</summary>
    public void Quake() { if (Has) Land(); }

    // STOMP: the ring, dust, the window wobbles, snacks above bounce off towards it
    void Land()
    {
        Stomps++;
        body.Pose = new Vector2(1.18f, 0.8f);
        Vector2 feet = new Vector2(body.Center.x, body.Extent.yMin);
        ringAt = feet;
        ringT = 0f;
        for (int i = 0; i < 8; i++) Puff(feet + new Vector2(Rnd(-0.2f, 0.2f) * S, 0.02f), new Vector2((i % 2 == 0 ? -1f : 1f) * Rnd(1f, 2.5f), Rnd(0.3f, 1.2f)) * Mathf.Sqrt(S));
        if (stompClip && sfx) { sfx.pitch = Rnd(0.9f, 1.05f) * Mathf.Lerp(1.15f, 0.9f, Mathf.InverseLerp(0.38f, 0.62f, body.size)); sfx.PlayOneShot(stompClip, volume); }
        long under = body.OnPlatform ? body.SupportWindow : 0;
        if (wobble && under != 0) wobble.Stomp(under, feet, 5f);
        hits.Clear();
        foreach (var s in Snack.All) if (Near(s)) hits.Add(s);
        foreach (var s in hits)
        {
            Knocked++;
            float dx = body.Center.x - s.Position.x;
            s.Knock(new Vector2(Mathf.Clamp(dx * 1.6f, -4f, 4f), Rnd(2.5f, 4f)));
        }
        Say(hits.Count > 0 ? 0.9f : 0.5f, hits.Count > 0 ? new[] { "STOMP! MINE NOW", "RAWR! DOWN YOU COME", "SNACK QUAKE!" } : new[] { "STOMP!", "RAWR!", "BOOM!" });
    }

    // a snack resting on a window top above it, within reach (to each side and up)
    bool SnackAbove(out Snack best)
    {
        best = null;
        float bd = float.MaxValue;
        foreach (var s in Snack.All) { if (!Near(s)) continue; float d = Mathf.Abs(s.Position.x - body.Center.x); if (d < bd) { bd = d; best = s; } }
        return best != null;
    }

    bool Near(Snack s)
    {
        if (!s || s.Held || s.Eaten || !s.Resting) return false;
        float up = s.SurfaceY - body.Extent.yMin;
        return up > 0.6f * S && up < reachUp * S && Mathf.Abs(s.Position.x - body.Center.x) < reach * S;
    }

    void Say(float chance, params string[] l)
    {
        if (!speech || Time.time - lastLine < 3f || Rnd(0f, 1f) > chance) return;
        if (speech.Say(l[(int)Rnd(0f, l.Length - 0.001f)], 0f, false)) lastLine = Time.time;
    }

    static float Rnd(float a, float b) => a + (float)s_fx.NextDouble() * (b - a);

    void Puff(Vector2 at, Vector2 v)
    {
        for (int i = 0; i < Dust; i++)
        {
            if (dustLife[i] > 0f) continue;
            dustLife[i] = Rnd(0.35f, 0.55f);
            dustV[i] = v;
            dust[i].sprite = s_dust;
            dust[i].transform.position = new Vector3(at.x, at.y, 0f);
            return;
        }
    }

    void LateUpdate() { long t0 = WhipProf.T(); LateUpdateTimed(); WhipProf.Add("BuddyStomp.LateUpdate", t0); }
    void LateUpdateTimed()
    {
        if (!s_baked) { s_ring = Bake(64, 64, RingPaint, "Stomp Ring"); s_dust = Bake(16, 16, DustPaint, "Stomp Dust"); s_baked = true; }
        float dt = Mathf.Min(Time.deltaTime, 0.05f);
        if (ringT >= 0f)
        {
            ringT += dt;
            float u = ringT / 0.45f;
            ring.sprite = s_ring;
            ring.enabled = u < 1f;
            float r = Mathf.Lerp(0.3f, reach * 0.8f, Mathf.Sqrt(Mathf.Clamp01(u))) * S;
            ring.transform.position = new Vector3(ringAt.x, ringAt.y, 0f);
            ring.transform.rotation = Quaternion.identity;
            ring.transform.localScale = new Vector3(r * 2f, r * 0.55f, 1f); // (flattened: a ring on the ground)
            ring.color = new Color(1f, 0.95f, 0.8f, 0.85f * (1f - u));
            if (u >= 1f) ringT = -1f;
        }
        for (int i = 0; i < Dust; i++)
        {
            var sr = dust[i];
            if (dustLife[i] <= 0f) { if (sr.enabled) sr.enabled = false; continue; }
            dustLife[i] -= dt;
            dustV[i] = dustV[i] * (1f - 3f * dt) + Vector2.down * (2f * dt);
            sr.transform.position += (Vector3)(dustV[i] * dt);
            float u = 1f - dustLife[i] / 0.5f;
            sr.transform.localScale = Vector3.one * (0.12f * S * (0.6f + u));
            sr.color = new Color(0.86f, 0.8f, 0.68f, 0.7f * Mathf.Clamp01(dustLife[i] / 0.25f));
            sr.enabled = true;
        }
    }

    static Sprite Bake(int w, int h, System.Func<float, float, float, Color> paint, string name)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = name };
        var px = new Color32[w * h];
        float texel = 1f / w;
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                Color c = paint((i + 0.5f) * texel, (j + 0.5f) * texel, texel);
                px[j * w + i] = new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), Mathf.Clamp01(c.a));
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), w);
    }

    // a soft white ring (1 unit across), brighter on its inner edge
    internal static Color RingPaint(float x, float y, float texel)
    {
        float r = new Vector2(x - 0.5f, y - 0.5f).magnitude;
        float a = Mathf.Clamp01(1f - Mathf.Abs(r - 0.42f) / 0.06f);
        return new Color(1f, 1f, 1f, a * a);
    }

    internal static Color DustPaint(float x, float y, float texel)
    {
        float r = new Vector2(x - 0.5f, y - 0.5f).magnitude;
        return new Color(1f, 1f, 1f, Mathf.Clamp01((0.46f - r) / 0.18f));
    }
}
