using System;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// Copy = snack: every time you press Ctrl+C (or copy some other way, e.g. right-click > Copy) a snack drops from the
/// top of the screen above the cursor, and the buddy goes to eat it. It only looks at whether Ctrl+C is held and at
/// the clipboard's change counter (GetClipboardSequenceNumber), never at what you copied or any other keys.
/// </summary>
[DisallowMultipleComponent]
public class SnackDropper : MonoBehaviour
{
    public bool dropOnCopy = true;
    [Tooltip("No new snacks while this many are lying around.")]
    public int maxSnacks = 6;
    [Tooltip("At most one snack this often (seconds).")]
    public float minInterval = 1.2f;
    [Tooltip("Sprite material (WhipIt > Setup assigns Whip.mat).")]
    public Material material;
    public int sortingOrder = 8;

    [Header("Sound (WhipIt > Setup assigns it)")]
    public AudioClip dropClip;
    [Range(0f, 1f)] public float volume = 0.45f;

    /// <summary>Tests can replace the clipboard counter / the Ctrl+C check.</summary>
    public static Func<uint> ClipboardProbe;
    public static Func<bool> CopyKeysProbe;
    /// <summary>You copied something (once per copy, whether or not a snack could drop).</summary>
    public static event Action Copied;

    uint lastSeq;
    bool wasCopyKeys;
    float lastChange = -10f, lastDrop = -10f;
    AudioSource audioSrc;

    void Awake()
    {
        audioSrc = gameObject.AddComponent<AudioSource>();
        audioSrc.playOnAwake = false;
        audioSrc.spatialBlend = 0f;
    }

    void Start() => lastSeq = ClipboardVersion(); // whatever was copied before we started doesn't count

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("SnackDropper.Update", t0); }
    void UpdateTimed()
    {
        if (!dropOnCopy) return;
        bool keys = CopyKeysDown();
        bool pressed = keys && !wasCopyKeys; // Ctrl+C, even with nothing selected
        wasCopyKeys = keys;
        uint seq = ClipboardVersion();
        bool changed = seq != lastSeq;       // copied some other way (menus, right-click)
        lastSeq = seq;
        if (!pressed && !changed) return;
        float now = Time.time;
        bool sameCopy = now - lastChange < 0.6f; // the key press and the clipboard change of one copy arrive together
        lastChange = now;
        if (sameCopy) return;
        Copied?.Invoke();
        Drop(false);
    }

    /// <summary>Drop a snack above the cursor now (force = ignore the rate limit; kind: this one, else any, now and then
    /// the rare giant potion; aboveX: above there instead of the cursor). Returns it, or null.</summary>
    public Snack Drop(bool force, Snack.Kind? kind = null, float? aboveX = null)
    {
        float now = Time.time;
        if (!force && (now - lastDrop < minInterval || Snack.All.Count >= maxSnacks)) return null;
        lastDrop = now;
        Vector2 over = aboveX.HasValue ? new Vector2(aboveX.Value, Grabber.CursorWorld.y) : Grabber.CursorWorld;
        Rect r = ScreenBounds.ScreenRectAt(aboveX.HasValue ? new Vector2(aboveX.Value, ScreenBounds.WorldRect.center.y) : over); // (more than one screen: the one the cursor is on)
        float x = Mathf.Clamp(over.x + UnityEngine.Random.Range(-0.2f, 0.2f), r.xMin + 0.4f, r.xMax - 0.4f);
        Snack.Kind k = kind ?? (Snack.Kind)UnityEngine.Random.Range(0, 3);
        if (kind == null && GiantPotion.Roll()) k = Snack.Kind.Potion; // (about one in 25: its own dice)
        var snack = Snack.Spawn(k, new Vector2(x, r.yMax - 0.15f), new Vector2(UnityEngine.Random.Range(-0.6f, 0.6f), 0f), material, sortingOrder);
        if (audioSrc && dropClip) { audioSrc.pitch = UnityEngine.Random.Range(0.95f, 1.1f); audioSrc.PlayOneShot(dropClip, volume); }
        return snack;
    }

    static uint ClipboardVersion()
    {
        if (ClipboardProbe != null) return ClipboardProbe();
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        return GetClipboardSequenceNumber();
#else
        return 0;
#endif
    }

    static bool CopyKeysDown()
    {
        if (CopyKeysProbe != null) return CopyKeysProbe();
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        // global key state (the overlay never has focus). C is read every frame so its "tapped since the last check"
        // bit (0x0001) never carries over from typing a plain "c" earlier.
        bool c = (GetAsyncKeyState(VK_C) & 0x8001) != 0;
        bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
        return ctrl && c;
#else
        return false;
#endif
    }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    const int VK_CONTROL = 0x11, VK_C = 0x43;
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
#endif
}
