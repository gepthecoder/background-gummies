using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Your real windows react when the buddy hits them: a hard landing (you threw or dropped it) on a window's top edge pushes the window down
/// and it springs back like a trampoline, and throwing the buddy into a window's side knocks it for a moment. Windows only
/// move a few pixels and always go back to where they were. Maximized windows are never touched, and clicking
/// anywhere outside the buddy (e.g. grabbing a window yourself) stops all wobbling at once.
/// Desktop icons don't move (they're never touched): landing on one goes "tick", and the Recycle Bin is a trampoline
/// when you throw or drop the buddy onto it (not when it hops onto it itself).
/// </summary>
[RequireComponent(typeof(GummyBody))]
[DisallowMultipleComponent]
public class WindowWobble : MonoBehaviour
{
    [Tooltip("Let the buddy nudge your real windows when it hits them.")]
    public bool wobbleWindows = true;
    [Tooltip("Landing on a window faster than this (after you threw or dropped it) pushes it down.")]
    public float landSpeed = 7f;
    [Tooltip("Thrown into a window faster than this knocks it.")]
    public float knockSpeed = 7f;
    [Tooltip("How much of the impact speed goes into the window.")]
    public float pushStrength = 0.5f;
    public float knockStrength = 0.45f;
    [Tooltip("Wobbles per second.")]
    public float frequency = 5f;
    [Tooltip("How quickly the wobble dies down (0 = forever, 1 = no bounce).")]
    [Range(0.05f, 1f)] public float damping = 0.18f;
    [Tooltip("The furthest a window moves, in world units (about 100 px per unit on a 1080p screen).")]
    public float maxOffset = 0.3f;

    [Tooltip("The Recycle Bin trampoline: bounces back with this much of the landing speed (plus a bit).")]
    public float binBounce = 0.85f;

    [Header("Sound (WhipIt > Setup assigns it)")]
    public AudioClip boingClip;
    [Range(0f, 1f)] public float volume = 0.5f;

    /// <summary>window id, impulse (world units / s)</summary>
    public event System.Action<long, Vector2> Wobbled;

    /// <summary>Windows wobbling right now.</summary>
    public int Active => springs.Count;

    class Spring { public Vector2 offset, velocity; public float lastHit; }

    // shared by all buddies: one spring per window, run by the first buddy
    static readonly Dictionary<long, Spring> springs = new Dictionary<long, Spring>();
    static readonly List<long> finished = new List<long>();
    static readonly List<WindowWobble> s_all = new List<WindowWobble>();
    GummyBody body;
    BuddyProgress progress;
    GummyBrain brain;
    BuddySpeech speech;
    AudioSource audioSrc;
    float lastBin = -10f;
    bool wasHeld;
    float lastRelease = -10f, lastXp = -10f;
    long insideWindow;
    const float ThrowWindow = 2.5f; // knocks count this long after you let go (not its own hops and leaps)

    void Awake()
    {
        body = GetComponent<GummyBody>();
        progress = GetComponent<BuddyProgress>();
        brain = GetComponent<GummyBrain>();
        speech = GetComponent<BuddySpeech>();
        audioSrc = gameObject.AddComponent<AudioSource>();
        audioSrc.playOnAwake = false;
        audioSrc.spatialBlend = 0f;
    }

    void OnEnable()
    {
        body.Splat += OnHit;
        body.Bump += OnHit;
        if (!s_all.Contains(this)) s_all.Add(this);
    }

    void OnDisable()
    {
        body.Splat -= OnHit;
        body.Bump -= OnHit;
        s_all.Remove(this);
        if (s_all.Count == 0) StopAll();
    }

    void OnApplicationQuit() => StopAll();

    /// <summary>Put every window back where it was.</summary>
    public void StopAll()
    {
        foreach (var kv in springs) DesktopPlatforms.EndMove(kv.Key, true);
        springs.Clear();
    }

    // landed on a window top
    void OnHit(Vector2 at, Vector2 normal, float speed)
    {
        long surf = body.LastHitSurface;
        if (DesktopPlatforms.IsIcon(surf)) { OnIcon(surf, at, normal, speed); return; }
        if (!wobbleWindows) return;
        // thrown into the side (or bottom) of a solid window: knock it the way it was hit
        if (surf != 0 && normal.y < 0.7f)
        {
            if (speed >= knockSpeed && Time.time - lastRelease < ThrowWindow) Kick(surf, -normal * speed * knockStrength, at, speed);
            return;
        }
        if (normal.y < 0.7f || speed < landSpeed || Time.time - lastRelease > ThrowWindow) return; // you threw / dropped it (not its own chase hops)
        var all = DesktopPlatforms.Current;
        for (int k = 0; k < all.Count; k++)
        {
            Platform pl = all[k];
            if (DesktopPlatforms.IsIcon(pl.id) || DesktopPlatforms.IsScreen(pl.id)) continue; // (another screen's floor: not a window)
            if (at.x < pl.x0 - 0.02f || at.x > pl.x1 + 0.02f || Mathf.Abs(at.y - pl.y) > 0.02f) continue;
            Kick(pl.id, new Vector2(0f, -speed * pushStrength), at, speed);
            return;
        }
    }

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("Spring.Update", t0); }
    void UpdateTimed()
    {
        float now = Time.time, dt = Time.deltaTime;

        bool held = body.Held;
        if (!held && wasHeld) lastRelease = now;
        wasHeld = held;

        // you're clicking something else (e.g. dragging a window yourself): hands off
        if (springs.Count > 0 && DesktopInput.LeftButtonHeld && !Grabber.Busy) StopAll();

        // thrown across a window it can fly in front of (solid windows off): knock the one it flies into
        long win = 0; bool movable = false;
        var wins = DesktopPlatforms.Windows;
        for (int i = 0; i < wins.Count; i++)
            if (!wins[i].screen && wins[i].Contains(body.Center)) { win = wins[i].id; movable = wins[i].movable; break; } // front-most
        Vector2 vel = body.Velocity;
        if (wobbleWindows && !body.solidWindows && win != 0 && win != insideWindow && movable && !held && now - lastRelease < ThrowWindow && vel.magnitude > knockSpeed)
            Kick(win, vel * knockStrength, body.Center, vel.magnitude);
        insideWindow = win;

        // springs
        if (springs.Count == 0 || s_all.Count == 0 || s_all[0] != this) return;
        float w = 2f * Mathf.PI * frequency, k = w * w, c = 2f * damping * w;
        const int sub = 4;
        float h = Mathf.Min(dt, 0.05f) / sub;
        finished.Clear();
        foreach (var kv in springs)
        {
            Spring s = kv.Value;
            for (int i = 0; i < sub; i++)
            {
                s.velocity += (-k * s.offset - c * s.velocity) * h;
                s.offset += s.velocity * h;
            }
            if (s.offset.magnitude > maxOffset) s.offset = s.offset.normalized * maxOffset;
            bool settled = s.offset.magnitude < 0.002f && s.velocity.magnitude < 0.03f;
            if (settled) { DesktopPlatforms.EndMove(kv.Key, true); finished.Add(kv.Key); }
            else if (!DesktopPlatforms.MoveTo(kv.Key, s.offset)) finished.Add(kv.Key); // someone else has it now
        }
        foreach (long id in finished) springs.Remove(id);
    }

    // landed on a desktop icon
    void OnIcon(long id, Vector2 at, Vector2 normal, float speed)
    {
        if (normal.y < 0.7f) return;
        float now = Time.time;
        bool own = brain && brain.Current == GummyBrain.State.Hopping && now - lastRelease > ThrowWindow; // its own hop onto it
        if (DesktopPlatforms.IsBin(id) && speed > 3f && !own && now - lastBin > 0.25f)
        {
            // the Recycle Bin: boing!
            lastBin = now;
            float up = Mathf.Clamp(speed * binBounce + 2f, 6f, 15f);
            body.Hop(new Vector2(-body.Velocity.x * 0.3f, up - body.Velocity.y), 0.6f);
            Play(1.3f, Mathf.Clamp(speed / 10f, 0.5f, 1f));
            if (speech && Random.value < 0.6f) speech.Say(Random.value < 0.5f ? "BOING!" : "WHEEE!", 0f, false);
            if (progress && now - lastXp > 1f) { lastXp = now; progress.AddXp(progress.xpWindowBonk, at); }
        }
        else if (speed > landSpeed && !own) Play(1.6f, 0.25f); // a little tick
    }

    void Play(float pitch, float vol)
    {
        if (!audioSrc || !boingClip) return;
        audioSrc.pitch = pitch * Random.Range(0.95f, 1.05f);
        audioSrc.PlayOneShot(boingClip, volume * vol);
    }

    /// <summary>A stomp on this window top (the dino's super stomp): it gives and springs back. No XP (its own move).</summary>
    public void Stomp(long id, Vector2 at, float strength)
    {
        if (!wobbleWindows || id == 0 || DesktopPlatforms.IsIcon(id)) return;
        Kick(id, new Vector2(0f, -strength), at, strength * 2f, false);
    }

    /// <summary>A bump into this window (a pinball bumper): it gives the way it was pushed and springs back, boing. No XP.
    /// False if it can't be nudged (maximized, gone, wobbling off).</summary>
    public bool Nudge(long id, Vector2 impulse, Vector2 at, float speed)
    {
        if (!wobbleWindows || id == 0 || DesktopPlatforms.IsIcon(id) || DesktopPlatforms.IsScreen(id)) return false;
        Kick(id, impulse, at, speed, false);
        return springs.ContainsKey(id);
    }

    void Kick(long id, Vector2 impulse, Vector2 at, float speed, bool xp = true)
    {
        float now = Time.time;
        if (!springs.TryGetValue(id, out Spring s))
        {
            if (!DesktopPlatforms.BeginMove(id)) return;
            s = new Spring { lastHit = -10f };
            springs[id] = s;
        }
        if (now - s.lastHit < 0.15f) return; // one impact, one kick
        s.lastHit = now;
        s.velocity = Vector2.ClampMagnitude(s.velocity + impulse, 8f);

        if (audioSrc && boingClip)
        {
            audioSrc.pitch = Random.Range(0.92f, 1.08f) * (speed > 12f ? 0.85f : 1f);
            audioSrc.PlayOneShot(boingClip, volume * Mathf.Clamp(speed / 12f, 0.35f, 1f));
        }
        if (xp && progress && now - lastXp > 1f) { lastXp = now; progress.AddXp(progress.xpWindowBonk, at); }
        Wobbled?.Invoke(id, impulse);
    }
}
