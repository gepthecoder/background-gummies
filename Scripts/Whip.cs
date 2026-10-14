using UnityEngine;

/// <summary>
/// Mouse-following whip. Left click = wind-up + strike: the loop rolls out and cracks at the tip.
/// Held mode (BuddyWhip): a buddy holds it instead of the mouse (Held, HoldAt, HoldFacing) and strikes with Strike().
/// Sim: position-based rope with mass taper toward the tip + Dynamic Follow-The-Leader length fix
/// (Müller et al. 2012), fixed-rate substeps. Units are world units with ortho size 5 (screen height = 10);
/// scale the whole thing with <see cref="size"/>.
/// Global hotkey (Windows player): Ctrl+Shift+W hides/shows the whip.
/// </summary>
[DisallowMultipleComponent]
public class Whip : MonoBehaviour
{
    [Header("References (filled by WhipIt > Setup)")]
    public Camera cam;
    public Material material;
    public AudioClip swishClip;
    public AudioClip crackClip;

    [Header("Look")]
    [Min(0.1f)] public float size = 1f;
    [Tooltip("Applied on Play.")]
    [Range(8, 64)] public int segments = 24;
    public float lashLength = 3.2f;
    public float handleLength = 0.7f;
    public float handleWidth = 0.13f;
    public float lashStartWidth = 0.075f;
    public float lashEndWidth = 0.012f;
    public Color handleColor = new Color(0.23f, 0.14f, 0.08f);
    public Color lashColor = new Color(0.45f, 0.27f, 0.13f);
    public Color popperColor = new Color(0.93f, 0.87f, 0.74f);

    [Header("Physics")]
    public float gravity = 12f;
    [Tooltip("Velocity lost per substep.")]
    [Range(0f, 0.05f)] public float damping = 0.004f;
    [Range(1, 32)] public int iterations = 8;
    [Tooltip("Mass of the tip relative to the base. Lower = snappier tip.")]
    [Range(0.02f, 1f)] public float tipMass = 0.06f;
    [Range(0f, 1f)] public float bendStiffness = 0.1f;
    [Tooltip("How strongly the lash leaves the handle in the handle's direction.")]
    [Range(0f, 0.5f)] public float handleStiffness = 0.05f;
    [Tooltip("Applied on Play.")]
    [Range(120, 480)] public int substepRate = 240;

    [Header("Idle")]
    [Tooltip("Handle angle in degrees (0 = right, 90 = up) when striking to the right. Mirrored on the right half of the screen.")]
    public float restAngle = 60f;
    [Tooltip("Degrees of handle lean per unit/s of mouse speed.")]
    public float tiltPerSpeed = 2f;
    public float maxTilt = 25f;

    [Header("Strike (click)")]
    public float windupAngle = 40f;
    public float strikeAngle = -80f;
    public Vector2 windupOffset = new Vector2(-0.6f, 0.3f);
    public Vector2 strikeOffset = new Vector2(1.0f, -0.1f);
    public float windupTime = 0.10f;
    public float strikeTime = 0.08f;
    public float recoverTime = 0.35f;

    [Header("Crack")]
    [Tooltip("Crack fires when the lash straightens to this fraction of its length after the strike.")]
    [Range(0.5f, 1f)] public float crackExtension = 0.88f;
    [Tooltip("Also crack when you fling the mouse fast enough (no click).")]
    public bool crackOnFling = true;
    public float flingCrackSpeed = 45f;
    public float crackCooldown = 0.35f;
    [Range(0f, 1f)] public float swishVolume = 0.8f;
    [Range(0f, 1f)] public float crackVolume = 1f;
    [Range(0, 64)] public int sparkCount = 14;

    const int Subdiv = 3;         // Catmull-Rom points per segment for rendering
    const int HandlePoints = 8;   // enough vertices for the handle gradient/width curve
    const float CrackFallback = 0.3f; // s after strike end: crack anyway if the lash never straightened

    int n;                        // segment count used by the sim
    float dt;                     // substep length
    Vector2[] x, p, v, corr;
    float[] w;
    Vector3[] lashPts, handlePts;
    LineRenderer lashLine, handleLine;
    ParticleSystem fx;
    Material fxMaterial;
    AudioSource swishSrc, crackSrc;

    Vector2 gripPrev, lastFrameGrip, gripVel, simGrip, simTip;
    float baseAngle, accumulator, tipSpeed, lastCrackTime = -10f;
    int facing = 1;
    bool flicking, cracked, queuedClick, wasHeld, hotkeyWasHeld, visible = true, covered;
    float flickT, fromAngle;
    Vector2 fromOffset;

    float FlickDuration => windupTime + strikeTime + recoverTime;

    /// <summary>Held by a buddy: the grip follows HoldAt instead of the mouse, and clicks don't strike.</summary>
    public bool Held { get; set; }
    /// <summary>Where the holder's hand is (world).</summary>
    public Vector2 HoldAt { get; set; }
    /// <summary>Which way it strikes: +1 right, -1 left.</summary>
    public int HoldFacing { get; set; } = 1;
    /// <summary>Tip position and velocity at each crack.</summary>
    public event System.Action<Vector2, Vector2> Cracked;
    public bool Visible { get => visible; set { if (value != visible) SetVisible(value); } }
    /// <summary>Behind a window in front of its holder (Occlusion): not drawn, but it keeps moving and cracking.</summary>
    public bool Covered { get => covered; set { if (value != covered) { covered = value; ShowLines(); } } }
    public bool Striking => flicking && !cracked;

    /// <summary>Wind up and strike now (held mode).</summary>
    public void Strike() => queuedClick = true;

    /// <summary>Re-apply widths/colours after changing size at runtime.</summary>
    public void Refresh() { if (lashLine) ApplyStyle(); }
    float LashLen => lashLength * size;

    // ------------------------------------------------------------------ lifecycle

    void Awake()
    {
        if (!cam) cam = Camera.main;
        n = segments;
        dt = 1f / substepRate;
        x = new Vector2[n + 1]; p = new Vector2[n + 1]; v = new Vector2[n + 1]; corr = new Vector2[n + 1];
        w = new float[n + 1];
        for (int i = 0; i <= n; i++) w[i] = 1f / Mathf.Lerp(1f, tipMass, i / (float)n);
        w[0] = 0f; // pinned to the handle

        lashPts = new Vector3[n * Subdiv + 1];
        handlePts = new Vector3[HandlePoints];

        if (!material)
        {
            Debug.LogWarning("[WhipIt] No material assigned, run WhipIt > Setup. Using Sprites/Default.");
            material = new Material(Shader.Find("Sprites/Default"));
        }
        lashLine = MakeLine("Lash", 10);
        handleLine = MakeLine("Handle", 11);
        fx = MakeFx();
        swishSrc = MakeSource();
        crackSrc = MakeSource();
        ApplyStyle();
    }

    void OnEnable()
    {
        if (cam && x != null) ResetPose(GripWorld());
    }

    void OnValidate()
    {
        if (lashLine) ApplyStyle();
    }

    void OnDestroy()
    {
        if (fxMaterial) Destroy(fxMaterial);
    }

    // ------------------------------------------------------------------ frame

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("Whip.Update", t0); }
    void UpdateTimed()
    {
        if (!cam) return;

        bool hk = DesktopInput.ChordHeld(DesktopInput.VK_CONTROL, DesktopInput.VK_SHIFT, DesktopInput.VK_W);
        if (hk && !hotkeyWasHeld) SetVisible(!visible);
        hotkeyWasHeld = hk;

        Vector2 mouse = DesktopInput.MousePosition;
        Vector2 grip = Held ? HoldAt : ScreenToWorld(mouse);
        float frameDt = Mathf.Min(Time.unscaledDeltaTime, 0.1f) * (Held ? Time.timeScale : 1f); // (in a buddy's hand: bullet time slows it too)

        if (!visible) { gripPrev = lastFrameGrip = grip; return; }

        // Cursor teleported (other monitor, alt-tab...): don't fling the lash across the screen.
        if ((grip - gripPrev).sqrMagnitude > 64f * size * size) ResetPose(grip);

        // Strike toward the screen centre; locked while a strike is running.
        if (!flicking)
        {
            float sx = mouse.x / Mathf.Max(1, Screen.width);
            if (ScreenBounds.Multi) { Rect sr = ScreenBounds.ScreenRectAt(grip); sx = (grip.x - sr.xMin) / Mathf.Max(sr.width, 0.01f); } // (the middle of its screen)
            if (Held) facing = HoldFacing >= 0 ? 1 : -1;
            else if (sx < 0.4f) facing = 1; else if (sx > 0.6f) facing = -1;
        }

        // Handle leans into mouse movement.
        if (frameDt > 0f) gripVel = Vector2.Lerp(gripVel, (grip - lastFrameGrip) / frameDt, 1f - Mathf.Exp(-10f * frameDt));
        lastFrameGrip = grip;
        float tilt = Mathf.Clamp(-gripVel.x / size * tiltPerSpeed, -maxTilt, maxTilt);
        float rest = facing > 0 ? restAngle : 180f - restAngle;
        baseAngle = Mathf.LerpAngle(baseAngle, rest + tilt, 1f - Mathf.Exp(-12f * frameDt));

        bool held = DesktopInput.LeftButtonHeld;
        if (!Held && held && !wasHeld && !Grabber.Busy) queuedClick = true; // clicks on objects grab instead of whipping
        wasHeld = held;
        if (queuedClick && CanStartFlick()) { StartFlick(); queuedClick = false; }

        // Fixed-rate substeps, grip interpolated across the frame.
        accumulator += frameDt;
        int steps = Mathf.Min(Mathf.FloorToInt(accumulator / dt), 32);
        accumulator = Mathf.Min(accumulator - steps * dt, dt);
        for (int k = 0; k < steps; k++)
        {
            Vector2 g = Vector2.Lerp(gripPrev, grip, (k + 1f) / steps);
            float angle = baseAngle;
            if (flicking)
            {
                flickT += dt;
                EvalFlick(flickT, out float fa, out Vector2 fo);
                angle += facing * fa;
                g += new Vector2(fo.x * facing, fo.y) * size;
                if (flickT >= FlickDuration) flicking = false;
            }
            Step(g, angle);
            CheckCrack();
        }
        if (steps > 0) gripPrev = grip;

        UpdateLines();
    }

    // ------------------------------------------------------------------ simulation

    void ResetPose(Vector2 grip)
    {
        baseAngle = facing > 0 ? restAngle : 180f - restAngle;
        Vector2 tip = grip + Dir(baseAngle) * handleLength * size;
        float seg = LashLen / n;
        for (int i = 0; i <= n; i++) { x[i] = tip + Vector2.down * (seg * i); v[i] = Vector2.zero; }
        gripPrev = lastFrameGrip = simGrip = grip;
        simTip = tip;
        gripVel = Vector2.zero;
        accumulator = 0f;
        flicking = queuedClick = false;
        UpdateLines();
    }

    void Step(Vector2 grip, float angleDeg)
    {
        float seg = LashLen / n;
        Vector2 dir = Dir(angleDeg);
        Vector2 tip = grip + dir * (handleLength * size);
        float g = gravity * size * dt;
        float keep = 1f - damping;

        // predict
        for (int i = 0; i <= n; i++)
        {
            v[i] *= keep;
            v[i].y -= g;
            p[i] = x[i] + v[i] * dt;
        }

        // constraints (mass-weighted, base pinned)
        float bendRest = 2f * seg;
        for (int it = 0; it < iterations; it++)
        {
            p[0] = tip;
            p[1] += (tip + dir * seg - p[1]) * handleStiffness;

            for (int i = 0; i < n; i++)
            {
                Vector2 d = p[i + 1] - p[i];
                float dist = d.magnitude;
                if (dist < 1e-6f) continue;
                Vector2 c = d * ((dist - seg) / dist / (w[i] + w[i + 1]));
                p[i] += c * w[i];
                p[i + 1] -= c * w[i + 1];
            }

            if (bendStiffness > 0f)
            {
                for (int i = 0; i < n - 1; i++) // resist folding only
                {
                    Vector2 d = p[i + 2] - p[i];
                    float dist = d.magnitude;
                    if (dist >= bendRest || dist < 1e-6f) continue;
                    Vector2 c = d * ((dist - bendRest) / dist / (w[i] + w[i + 2]) * bendStiffness);
                    p[i] += c * w[i];
                    p[i + 2] -= c * w[i + 2];
                }
            }
        }
        p[0] = tip;

        // follow-the-leader: exact segment lengths, base to tip
        corr[0] = Vector2.zero;
        for (int i = 0; i < n; i++)
        {
            Vector2 d = p[i + 1] - p[i];
            float dist = d.magnitude;
            Vector2 c = dist < 1e-6f ? Vector2.zero : d * (seg / dist) - d;
            p[i + 1] += c;
            corr[i + 1] = c;
        }

        // velocities (+ DFTL correction so the length fix doesn't inject energy)
        float inv = 1f / dt;
        for (int i = 0; i <= n; i++)
        {
            Vector2 vel = (p[i] - x[i]) * inv;
            if (i < n) vel -= corr[i + 1] * inv;
            v[i] = vel;
            x[i] = p[i];
        }
        v[0] = Vector2.zero;

        tipSpeed = v[n].magnitude;
        simGrip = grip;
        simTip = tip;
    }

    // ------------------------------------------------------------------ strike + crack

    bool CanStartFlick() => !flicking || cracked || flickT > windupTime + strikeTime + CrackFallback;

    void StartFlick()
    {
        if (flicking) EvalFlick(flickT, out fromAngle, out fromOffset); // blend from the current pose
        else { fromAngle = 0f; fromOffset = Vector2.zero; }
        flicking = true;
        cracked = false;
        flickT = 0f;
        Play(swishSrc, swishClip, swishVolume);
    }

    /// <summary>Handle angle offset (deg) and grip offset (world, before size) for facing = +1.</summary>
    void EvalFlick(float t, out float angle, out Vector2 offset)
    {
        if (t < windupTime)
        {
            float u = t / windupTime; u = 1f - (1f - u) * (1f - u); // ease out
            angle = Mathf.Lerp(fromAngle, windupAngle, u);
            offset = Vector2.Lerp(fromOffset, windupOffset, u);
            return;
        }
        t -= windupTime;
        if (t < strikeTime)
        {
            float u = t / strikeTime; u *= u; // ease in: fastest right before the hand stops
            angle = Mathf.Lerp(windupAngle, strikeAngle, u);
            offset = Vector2.Lerp(windupOffset, strikeOffset, u);
            return;
        }
        t -= strikeTime;
        float r = Mathf.Clamp01(t / recoverTime); r = r * r * (3f - 2f * r);
        angle = strikeAngle * (1f - r);
        offset = strikeOffset * (1f - r);
    }

    void CheckCrack()
    {
        float ext = (x[n] - x[0]).magnitude / LashLen;
        if (flicking)
        {
            if (cracked) return;
            float strikeEnd = windupTime + strikeTime;
            if ((flickT >= strikeEnd && ext >= crackExtension) || flickT >= strikeEnd + CrackFallback) Crack();
        }
        else if (crackOnFling && tipSpeed >= flingCrackSpeed * size && ext >= 0.85f &&
                 Time.unscaledTime - lastCrackTime >= crackCooldown)
        {
            Crack();
        }
    }

    void Crack()
    {
        cracked = true;
        lastCrackTime = Time.unscaledTime;
        Play(crackSrc, crackClip, crackVolume);
        EmitSparks(x[n], v[n]);
        Cracked?.Invoke(x[n], v[n]);
    }

    // ------------------------------------------------------------------ visuals + audio

    void UpdateLines()
    {
        for (int i = 0; i < HandlePoints; i++)
            handlePts[i] = Vector2.Lerp(simGrip, simTip, i / (HandlePoints - 1f));
        handleLine.positionCount = HandlePoints;
        handleLine.SetPositions(handlePts);

        int k = 0;
        for (int i = 0; i < n; i++)
        {
            Vector2 p0 = x[Mathf.Max(i - 1, 0)], p1 = x[i], p2 = x[i + 1], p3 = x[Mathf.Min(i + 2, n)];
            for (int s = 0; s < Subdiv; s++) lashPts[k++] = CatmullRom(p0, p1, p2, p3, s / (float)Subdiv);
        }
        lashPts[k] = x[n];
        lashLine.positionCount = lashPts.Length;
        lashLine.SetPositions(lashPts);
    }

    void ApplyStyle()
    {
        handleLine.widthMultiplier = handleWidth * size;
        handleLine.widthCurve = new AnimationCurve(
            new Keyframe(0f, 1.25f), new Keyframe(0.12f, 1.25f), new Keyframe(0.16f, 1f),
            new Keyframe(0.84f, 0.95f), new Keyframe(0.88f, 0.85f), new Keyframe(1f, 0.8f));
        handleLine.colorGradient = MakeGradient(
            (handleColor * 0.7f, 0f), (handleColor * 0.7f, 0.14f), (handleColor, 0.16f),
            (handleColor, 0.84f), (Color.Lerp(handleColor, lashColor, 0.6f), 0.88f), (lashColor, 1f));

        lashLine.widthMultiplier = lashStartWidth * size;
        lashLine.widthCurve = new AnimationCurve(
            new Keyframe(0f, 1f), new Keyframe(0.6f, 0.55f), new Keyframe(1f, lashEndWidth / Mathf.Max(1e-4f, lashStartWidth)));
        lashLine.colorGradient = MakeGradient(
            (lashColor, 0f), (Color.Lerp(lashColor, popperColor, 0.2f), 0.9f), (popperColor, 0.93f), (popperColor, 1f));
    }

    void EmitSparks(Vector2 pos, Vector2 tipVel)
    {
        var ep = new ParticleSystem.EmitParams
        {
            position = pos,
            velocity = Vector3.zero,
            startSize = 0.38f * size,
            startLifetime = 0.07f,
            startColor = new Color(1f, 1f, 1f, 0.9f)
        };
        fx.Emit(ep, 1); // flash

        for (int i = 0; i < sparkCount; i++)
        {
            float a = Random.value * Mathf.PI * 2f;
            Vector2 d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            ep.velocity = d * (Random.Range(2f, 6f) * size) + tipVel * 0.08f;
            ep.startSize = Random.Range(0.03f, 0.07f) * size;
            ep.startLifetime = Random.Range(0.12f, 0.28f);
            ep.startColor = Color.Lerp(Color.white, new Color(1f, 0.85f, 0.5f), Random.value);
            fx.Emit(ep, 1);
        }
    }

    void Play(AudioSource src, AudioClip clip, float volume)
    {
        if (!clip) return;
        src.pitch = Random.Range(0.93f, 1.07f);
        src.PlayOneShot(clip, volume);
    }

    void SetVisible(bool on)
    {
        visible = on;
        ShowLines();
        if (on) ResetPose(GripWorld());
    }

    void ShowLines()
    {
        bool on = visible && !covered;
        if (lashLine) lashLine.enabled = handleLine.enabled = on;
        if (fx) { var r = fx.GetComponent<ParticleSystemRenderer>(); if (r) r.enabled = on; }
    }

    // ------------------------------------------------------------------ builders

    LineRenderer MakeLine(string name, int order)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.sharedMaterial = material;
        lr.sortingOrder = order;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        lr.alignment = LineAlignment.View;
        lr.textureMode = LineTextureMode.Stretch;
        lr.numCapVertices = 4;
        lr.numCornerVertices = 2;
        return lr;
    }

    ParticleSystem MakeFx()
    {
        var go = new GameObject("CrackFx");
        go.transform.SetParent(transform, false);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.loop = true;
        main.playOnAwake = true;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 256;
        main.gravityModifier = 0f;

        var emission = ps.emission; emission.enabled = false; // we Emit() manually
        var shape = ps.shape; shape.enabled = false;

        var col = ps.colorOverLifetime; col.enabled = true;
        var grad = new Gradient();
        grad.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(new Color(1f, 0.7f, 0.3f), 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.8f, 0.5f), new GradientAlphaKey(0f, 1f) });
        col.color = new ParticleSystem.MinMaxGradient(grad);

        var sol = ps.sizeOverLifetime; sol.enabled = true;
        sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0f));

        var lim = ps.limitVelocityOverLifetime; lim.enabled = true;
        lim.limit = new ParticleSystem.MinMaxCurve(1000f);
        lim.drag = new ParticleSystem.MinMaxCurve(6f);

        fxMaterial = new Material(material) { mainTexture = MakeDotTexture(32) };
        var r = go.GetComponent<ParticleSystemRenderer>();
        r.sharedMaterial = fxMaterial;
        r.renderMode = ParticleSystemRenderMode.Billboard;
        r.sortingOrder = 12;

        ps.Play();
        return ps;
    }

    AudioSource MakeSource()
    {
        var s = gameObject.AddComponent<AudioSource>();
        s.playOnAwake = false;
        s.spatialBlend = 0f;
        return s;
    }

    static Texture2D MakeDotTexture(int res)
    {
        var tex = new Texture2D(res, res, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "WhipDot" };
        var px = new Color32[res * res];
        float c = (res - 1) * 0.5f;
        for (int yy = 0; yy < res; yy++)
            for (int xx = 0; xx < res; xx++)
            {
                float r = Mathf.Sqrt((xx - c) * (xx - c) + (yy - c) * (yy - c)) / c;
                float a = Mathf.Clamp01(1f - r); a *= a;
                px[yy * res + xx] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return tex;
    }

    // ------------------------------------------------------------------ helpers

    Vector2 MouseWorld() => ScreenToWorld(DesktopInput.MousePosition);
    Vector2 GripWorld() => Held ? HoldAt : MouseWorld();

    Vector2 ScreenToWorld(Vector2 s)
    {
        Vector3 wp = cam.ScreenToWorldPoint(new Vector3(s.x, s.y, -cam.transform.position.z));
        return new Vector2(wp.x, wp.y);
    }

    static Vector2 Dir(float deg)
    {
        float r = deg * Mathf.Deg2Rad;
        return new Vector2(Mathf.Cos(r), Mathf.Sin(r));
    }

    static Vector3 CatmullRom(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * ((2f * p1) + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    static Gradient MakeGradient(params (Color c, float t)[] keys)
    {
        var ck = new GradientColorKey[keys.Length];
        for (int i = 0; i < keys.Length; i++) ck[i] = new GradientColorKey(keys[i].c, keys[i].t);
        var g = new Gradient();
        g.SetKeys(ck, new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
        return g;
    }
}
