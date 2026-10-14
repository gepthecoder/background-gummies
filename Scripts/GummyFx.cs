using UnityEngine;

/// <summary>
/// Sounds and goo for a GummyBody: squelch on splat, soft bump on lighter hits, a squish when you grab it,
/// a rubbery creak while you stretch it (pitch rises with the pull), a "plop" when it pops free,
/// goo droplets, and a goo spot on the edge that fades away.
/// A splat leaves a little gummy paw print, and sliding down a wall leaves a wet goo trail.
/// </summary>
[RequireComponent(typeof(GummyBody))]
[DisallowMultipleComponent]
public class GummyFx : MonoBehaviour
{
    [Header("Sounds (WhipIt > Setup assigns them)")]
    public AudioClip splatClip;
    public AudioClip bumpClip;
    public AudioClip popClip;
    public AudioClip stretchLoop;
    [Range(0f, 1f)] public float splatVolume = 0.7f;
    [Range(0f, 1f)] public float bumpVolume = 0.35f;
    [Range(0f, 1f)] public float grabVolume = 0.25f;
    [Range(0f, 1f)] public float stretchVolume = 0.45f;
    [Range(0f, 1f)] public float popVolume = 0.8f;
    [Tooltip("Pan sounds left/right with the apple's position on screen.")]
    [Range(0f, 1f)] public float stereoWidth = 0.6f;

    [Header("Goo")]
    [Range(0, 32)] public int splatDroplets = 10;
    [Range(0, 32)] public int popDroplets = 14;
    public float spotFadeTime = 2.5f;
    [Tooltip("Multiplied with the body colour for droplets and spots.")]
    public Color gooTint = new Color(0.85f, 0.85f, 0.85f, 1f);

    [Header("Prints + trails")]
    public float trailFadeTime = 3f;
    public float printFadeTime = 4f;

    const int SpotCount = 6, DotCount = 12, TrailCount = 2, TrailPoints = 64;

    GummyBody body;
    AudioSource[] shots;
    AudioSource loop;
    int nextShot, nextSpot;
    GameObject goo;                 // world-space holder (the body's root moves with the body)
    ParticleSystem drops;
    Material dropMat, lineMat;
    readonly SpriteRenderer[] spots = new SpriteRenderer[SpotCount];
    readonly float[] spotAge = new float[SpotCount];
    readonly float[] spotAlpha = new float[SpotCount];
    readonly long[] spotWin = new long[SpotCount]; // Occlusion: the window it's on (0 = the desktop)
    float lastBump = -1f, lastSplat = -1f;
    bool wasHeld;

    // paw-print dots
    readonly SpriteRenderer[] dots = new SpriteRenderer[DotCount];
    readonly float[] dotAge = new float[DotCount];
    readonly long[] dotWin = new long[DotCount];
    int nextDot;

    // goo trails
    struct Trail { public LineRenderer lr; public Vector3[] pts; public int count; public float fade; public bool live; }
    readonly Trail[] trails = new Trail[TrailCount];
    int trail = -1;

    void Awake()
    {
        body = GetComponent<GummyBody>();
        shots = new AudioSource[3];
        for (int i = 0; i < shots.Length; i++) shots[i] = MakeSource(false);
        loop = MakeSource(true);
        loop.clip = stretchLoop;
        loop.volume = 0f;
        BuildGoo();
    }

    void OnEnable()
    {
        body.Splat += OnSplat;
        body.Bump += OnBump;
        body.Pop += OnPop;
        if (goo) goo.SetActive(true);
    }

    void OnDisable()
    {
        body.Splat -= OnSplat;
        body.Bump -= OnBump;
        body.Pop -= OnPop;
        if (loop) loop.Stop();
        if (goo) goo.SetActive(false); // (its goo goes with it, e.g. into a fused monster)
    }

    void OnDestroy()
    {
        if (goo) Destroy(goo);
        if (dropMat) Destroy(dropMat);
        if (lineMat) Destroy(lineMat);
    }

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("GummyFx.Update", t0); }
    void UpdateTimed()
    {
        float dt = Time.unscaledDeltaTime;

        // squish when you pick it up
        if (body.Held && !wasHeld) Play(bumpClip, grabVolume, 1.3f, body.Center);
        wasHeld = body.Held;

        // rubbery creak while stretched: louder and higher the closer it is to popping
        float target = body.Held && body.IsStuck
            ? stretchVolume * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.15f, 1f, body.Stretch))
            : 0f;
        loop.volume = Mathf.MoveTowards(loop.volume, target, dt * 3f);
        loop.pitch = 0.8f + 0.7f * body.Stretch;
        loop.panStereo = Pan(body.Center.x);
        if (loop.volume > 0.001f && !loop.isPlaying && loop.clip) loop.Play();
        else if (loop.volume <= 0.001f && loop.isPlaying) loop.Stop();

        // goo spots fade out
        for (int i = 0; i < SpotCount; i++)
        {
            if (!spots[i] || !spots[i].enabled) continue;
            spotAge[i] += dt * Time.timeScale;
            float u = spotAge[i] / Mathf.Max(spotFadeTime, 0.01f);
            if (u >= 1f) { spots[i].enabled = false; continue; }
            Color c = spots[i].color;
            c.a = Seen(spots[i].transform.position, spotWin[i]) ? spotAlpha[i] * (1f - u * u) : 0f;
            spots[i].color = c;
        }
        float fx = dt * (body.Held ? 1f : Time.timeScale); // (bullet time: drops hang in the air)
        UpdateDots(fx);
        UpdateTrail(fx);
    }

    // ------------------------------------------------------------------ events

    void OnSplat(Vector2 at, Vector2 normal, float speed)
    {
        // one splat touches the edge over several physics steps: play it once
        if (Time.unscaledTime - lastSplat < 0.15f) return;
        lastSplat = Time.unscaledTime;
        float k = Mathf.InverseLerp(body.stickSpeed, 20f, speed); // 0 = soft stick, 1 = hard throw
        Play(splatClip, splatVolume * Mathf.Lerp(0.5f, 1f, k), Random.Range(0.92f, 1.08f) * Mathf.Lerp(1.1f, 0.9f, k), at);
        Emit(at, normal, Mathf.RoundToInt(splatDroplets * Mathf.Lerp(0.4f, 1f, k)), Mathf.Lerp(1.5f, 4f, k));
        if (k > 0.25f) { Spot(at, normal, Mathf.Lerp(0.8f, 1.3f, k)); Paw(at, normal, Mathf.Lerp(0.8f, 1.2f, k)); }
    }

    void OnBump(Vector2 at, Vector2 normal, float speed)
    {
        if (Time.unscaledTime - lastBump < 0.25f) return; // one thud per landing, not one per bounce
        lastBump = Time.unscaledTime;
        float k = Mathf.InverseLerp(body.bumpSpeed, body.stickSpeed * 2f, speed);
        Play(bumpClip, bumpVolume * Mathf.Lerp(0.3f, 1f, k), Random.Range(0.95f, 1.15f), at);
    }

    void OnPop(Vector2 at, Vector2 dir)
    {
        Play(popClip, popVolume, Random.Range(0.95f, 1.1f), at);
        Emit(at, dir, popDroplets, 3.5f);
        Spot(at, dir, 1f);
    }

    // ------------------------------------------------------------------ helpers

    void Play(AudioClip clip, float volume, float pitch, Vector2 at)
    {
        if (!clip || volume <= 0f) return;
        AudioSource src = shots[nextShot];
        nextShot = (nextShot + 1) % shots.Length;
        src.pitch = pitch;
        src.panStereo = Pan(at.x);
        src.PlayOneShot(clip, volume);
    }

    float Pan(float worldX)
    {
        Rect r = ScreenBounds.WorldRect;
        float half = Mathf.Max(r.width * 0.5f, 0.01f);
        return Mathf.Clamp((worldX - r.center.x) / half, -1f, 1f) * stereoWidth;
    }

    void Emit(Vector2 at, Vector2 normal, int count, float speed)
    {
        if (!drops || count <= 0) return;
        float s = body.size / 0.6f;
        Color baseC = body.color * gooTint;
        float na = Mathf.Atan2(normal.y, normal.x);
        var ep = new ParticleSystem.EmitParams();
        for (int i = 0; i < count; i++)
        {
            float a = na + Random.Range(-1.2f, 1.2f); // spray within +-70 degrees of the edge normal
            var d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            ep.position = at + normal * (0.02f * s);
            ep.velocity = d * (Random.Range(0.5f, 1f) * speed * s);
            ep.startSize = Random.Range(0.025f, 0.06f) * s;
            ep.startLifetime = Random.Range(0.35f, 0.7f);
            Color c = Color.Lerp(baseC, baseC * 0.75f, Random.value);
            c.a = 1f;
            ep.startColor = c;
            drops.Emit(ep, 1);
        }
    }

    void Spot(Vector2 at, Vector2 normal, float scale)
    {
        SpriteRenderer sr = spots[nextSpot];
        if (!sr) return;
        float s = body.size;
        sr.transform.position = new Vector3(at.x, at.y, 0f);
        sr.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(normal.y, normal.x) * Mathf.Rad2Deg + 90f); // long axis along the edge
        sr.transform.localScale = new Vector3(0.22f * s * scale, 0.07f * s * scale, 1f);
        Color c = body.color * gooTint * 0.8f;
        spotAlpha[nextSpot] = 0.6f;
        c.a = spotAlpha[nextSpot];
        sr.color = c;
        sr.enabled = true;
        spotAge[nextSpot] = 0f;
        spotWin[nextSpot] = body.LastHitSurface;
        nextSpot = (nextSpot + 1) % SpotCount;
    }

    // ------------------------------------------------------------------ juicy edges

    void Paw(Vector2 at, Vector2 normal, float scale)
    {
        float s = body.size * scale;
        Vector2 tangent = new Vector2(normal.y, -normal.x);
        Color c = body.color * gooTint * 0.7f;
        for (int j = -1; j <= 1; j++) // three little toe dots just off the edge, above the goo spot
        {
            SpriteRenderer sr = dots[nextDot];
            if (!sr) return;
            Vector2 pos = at + normal * (0.1f * s + (j == 0 ? 0.025f * s : 0f)) + tangent * (0.085f * s * j);
            sr.transform.position = new Vector3(pos.x, pos.y, 0f);
            float d = (j == 0 ? 0.065f : 0.055f) * s;
            sr.transform.localScale = new Vector3(d, d, 1f);
            c.a = 0.55f;
            sr.color = c;
            sr.enabled = true;
            dotAge[nextDot] = 0f;
            dotWin[nextDot] = body.LastHitSurface;
            nextDot = (nextDot + 1) % DotCount;
        }
    }

    // a goo spot / paw print on that window (0 = the desktop): not behind a window in front of it
    static bool Seen(Vector3 at, long win) => !Occlusion.Hidden(at, win != 0 && Occlusion.Exists(win) ? Occlusion.LayerOf(win) : Occlusion.Back);

    void UpdateDots(float dt)
    {
        for (int i = 0; i < DotCount; i++)
        {
            if (!dots[i] || !dots[i].enabled) continue;
            dotAge[i] += dt;
            float u = dotAge[i] / Mathf.Max(printFadeTime, 0.01f);
            if (u >= 1f) { dots[i].enabled = false; continue; }
            Color c = dots[i].color;
            c.a = Seen(dots[i].transform.position, dotWin[i]) ? 0.55f * (1f - u * u) : 0f;
            dots[i].color = c;
        }
    }

    // wet streak while it slides down a wall
    void UpdateTrail(float dt)
    {
        bool onWall = body.IsStuck && body.GluedCount > 0 && Mathf.Abs(body.GlueNormal.x) > 0.7f;
        if (onWall)
        {
            Vector2 at = body.GlueCenter + body.GlueNormal * 0.012f;
            if (trail < 0)
            {
                trail = trails[0].live ? 1 : 0; // the other one may still be fading
                ref Trail nt = ref trails[trail];
                if (nt.lr) { nt.count = 0; nt.fade = -1f; nt.live = true; nt.lr.positionCount = 0; nt.lr.enabled = true; }
                else trail = -1;
            }
            if (trail >= 0)
            {
                ref Trail t = ref trails[trail];
                Vector3 last = t.count > 0 ? t.pts[t.count - 1] : new Vector3(1e6f, 0f, 0f);
                if (t.count < TrailPoints && ((Vector2)last - at).magnitude > 0.02f)
                {
                    t.pts[t.count++] = new Vector3(at.x, at.y, 0f);
                    t.lr.positionCount = t.count;
                    t.lr.SetPositions(t.pts);
                }
                Color c = body.color * gooTint; c.a = 0.35f;
                t.lr.startColor = t.lr.endColor = c;
                t.lr.widthMultiplier = 0.22f * body.size;
            }
        }
        else if (trail >= 0) { trails[trail].fade = 0f; trail = -1; }

        for (int i = 0; i < TrailCount; i++)
        {
            ref Trail t = ref trails[i];
            if (!t.live || t.fade < 0f) continue;
            t.fade += dt;
            float u = t.fade / Mathf.Max(trailFadeTime, 0.01f);
            if (u >= 1f || t.count < 2) { t.live = false; t.lr.enabled = false; continue; }
            Color c = body.color * gooTint; c.a = 0.35f * (1f - u);
            t.lr.startColor = t.lr.endColor = c;
        }
    }

    LineRenderer MakeLine(string n, float width, int order)
    {
        var go = new GameObject(n);
        go.transform.SetParent(goo.transform, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.widthMultiplier = width;
        lr.numCapVertices = 2;
        lr.numCornerVertices = 1;
        lr.alignment = LineAlignment.TransformZ;
        lr.sortingOrder = order;
        if (lineMat) lr.sharedMaterial = lineMat;
        lr.enabled = false;
        return lr;
    }

    AudioSource MakeSource(bool looping)
    {
        var src = gameObject.AddComponent<AudioSource>();
        src.playOnAwake = false;
        src.loop = looping;
        src.spatialBlend = 0f;
        return src;
    }

    void BuildGoo()
    {
        goo = new GameObject(name + " Goo");
        Sprite circle = CuteFace.Circle;

        for (int i = 0; i < SpotCount; i++)
        {
            var go = new GameObject("Spot");
            go.transform.SetParent(goo.transform, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = circle;
            sr.sortingOrder = body.sortingOrder - 3;
            if (body.spriteMaterial) sr.sharedMaterial = body.spriteMaterial;
            sr.enabled = false;
            spots[i] = sr;
        }

        for (int i = 0; i < DotCount; i++)
        {
            var go = new GameObject("Paw Dot");
            go.transform.SetParent(goo.transform, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = circle;
            sr.sortingOrder = body.sortingOrder - 3;
            if (body.spriteMaterial) sr.sharedMaterial = body.spriteMaterial;
            sr.enabled = false;
            dots[i] = sr;
        }

        // goo trail lines (a plain white texture so vertex colours show)
        Material lineBase = body.spriteMaterial ? body.spriteMaterial : new Material(Shader.Find("Sprites/Default"));
        lineMat = new Material(lineBase) { name = "Gummy Lines (instance)", mainTexture = Texture2D.whiteTexture };
        for (int k = 0; k < TrailCount; k++)
            trails[k] = new Trail { lr = MakeLine("Goo Trail", 0.13f, body.sortingOrder - 4), pts = new Vector3[TrailPoints], fade = -1f };

        var pgo = new GameObject("Droplets");
        pgo.transform.SetParent(goo.transform, false);
        drops = pgo.AddComponent<ParticleSystem>();
        drops.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = drops.main;
        main.loop = true;
        main.playOnAwake = true;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 256;
        main.gravityModifier = 3f; // Physics.gravity 9.81 x 3 ~ the gummy's own gravity (screen height = 10)

        var emission = drops.emission; emission.enabled = false; // Emit() manually
        var shape = drops.shape; shape.enabled = false;

        var col = drops.colorOverLifetime; col.enabled = true;
        var grad = new Gradient();
        grad.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.6f), new GradientAlphaKey(0f, 1f) });
        col.color = new ParticleSystem.MinMaxGradient(grad);

        var sol = drops.sizeOverLifetime; sol.enabled = true;
        sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.6f));

        Material baseMat = body.spriteMaterial ? body.spriteMaterial : new Material(Shader.Find("Sprites/Default"));
        dropMat = new Material(baseMat) { name = "Gummy Droplets (instance)", mainTexture = circle ? circle.texture : null };
        var r = pgo.GetComponent<ParticleSystemRenderer>();
        r.sharedMaterial = dropMat;
        r.renderMode = ParticleSystemRenderMode.Billboard;
        r.sortingOrder = body.sortingOrder + 4;

        drops.Play();
    }
}
