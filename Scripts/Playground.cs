using UnityEngine;

/// <summary>
/// The playground's toy box (on the Interaction object; WhipIt > Setup assigns the sounds): puts the ball pit (BallPit)
/// and the slide (Slide) out near the cursor, with their sounds. Chat "ball pit" / "slide", or the WhipIt > Buddy menu.
/// </summary>
[DisallowMultipleComponent]
public class Playground : MonoBehaviour
{
    [Tooltip("Sprite material (WhipIt > Setup assigns Whip.mat).")]
    public Material material;

    [Header("Sound (WhipIt > Setup assigns them)")]
    public AudioClip pitSplash;
    public AudioClip slideLoop;
    public AudioClip slideBoing;

    public static Playground Instance { get; private set; }

    void OnEnable() => Instance = this;
    void OnDisable() { if (Instance == this) Instance = null; }

    /// <summary>The ball pit, near this point (the one that's out if there is one).</summary>
    public static BallPit PutPit(Material fallback, Vector2 near)
    {
        var p = Instance;
        return BallPit.Put(p && p.material ? p.material : fallback, near, p ? p.pitSplash : null);
    }

    /// <summary>The slide, near this point (the one that's up if there is one).</summary>
    public static Slide PutSlide(Material fallback, Vector2 near)
    {
        var p = Instance;
        return Slide.Put(p && p.material ? p.material : fallback, near, p ? p.slideLoop : null, p ? p.slideBoing : null);
    }
}
