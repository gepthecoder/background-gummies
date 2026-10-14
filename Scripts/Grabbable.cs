using UnityEngine;

/// <summary>
/// A rigid physics object the mouse can hover, grab and throw (dragged by a spring joint, thrown with the drag
/// velocity). Scales its first child (the visual) a little on hover / grab so you can tell it's interactive.
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
[DisallowMultipleComponent]
public class Grabbable : MonoBehaviour, IGrabTarget
{
    public float hoverScale = 1.06f;
    public float heldScale = 1.12f;
    [Tooltip("Spring force per unit of mass pulling the object to the cursor.")]
    public float forcePerMass = 600f;
    [Tooltip("Spring stiffness (Hz). Higher = follows the cursor more tightly.")]
    public float frequency = 10f;
    [Range(0f, 1f)] public float dampingRatio = 0.8f;
    public float maxThrowSpeed = 60f;
    [Tooltip("Teleport back onto the screen if it ever ends up outside (e.g. thrown through a wall).")]
    public bool keepOnScreen = true;

    public Rigidbody2D Body { get; private set; }
    public bool Hovered { get; set; }
    public bool Held { get; private set; }

    Collider2D[] colliders;
    TargetJoint2D joint;
    Transform visual;
    float scale = 1f;

    void Awake()
    {
        Body = GetComponent<Rigidbody2D>();
        colliders = GetComponentsInChildren<Collider2D>();
    }

    void OnEnable() => GrabTargets.Add(this);
    void OnDisable() { if (Held) EndGrab(); GrabTargets.Remove(this); }

    void Start()
    {
        if (transform.childCount > 0) visual = transform.GetChild(0);
    }

    public bool Contains(Vector2 p)
    {
        foreach (var c in colliders) if (c && c.enabled && c.OverlapPoint(p)) return true;
        return false;
    }

    public void BeginGrab(Vector2 p)
    {
        Held = true;
        joint = gameObject.AddComponent<TargetJoint2D>();
        joint.autoConfigureTarget = false;
        joint.anchor = Body.transform.InverseTransformPoint(p); // grab where you clicked, so it swings naturally
        joint.target = p;
        joint.maxForce = forcePerMass * Body.mass;
        joint.frequency = frequency;
        joint.dampingRatio = dampingRatio;
    }

    public void DragTo(Vector2 p)
    {
        if (joint) joint.target = p;
    }

    public void EndGrab()
    {
        if (joint) Destroy(joint);
        joint = null;
        Body.linearVelocity = Vector2.ClampMagnitude(Body.linearVelocity, maxThrowSpeed);
        Held = false;
    }

    void LateUpdate() { long t0 = WhipProf.T(); LateUpdateTimed(); WhipProf.Add("Grabbable.LateUpdate", t0); }
    void LateUpdateTimed()
    {
        if (!visual) return;
        float target = Held ? heldScale : Hovered ? hoverScale : 1f;
        scale = Mathf.Lerp(scale, target, 1f - Mathf.Exp(-20f * Time.unscaledDeltaTime));
        visual.localScale = new Vector3(scale, scale, 1f);
    }

    void FixedUpdate() { long t0 = WhipProf.T(); FixedUpdateTimed(); WhipProf.Add("Grabbable.FixedUpdate", t0); }
    void FixedUpdateTimed()
    {
        if (!keepOnScreen || Held) return;
        Rect r = ScreenBounds.WorldRect;
        Vector2 p = Body.position;
        const float margin = 1.5f;
        if (p.x < r.xMin - margin || p.x > r.xMax + margin || p.y < r.yMin - margin || p.y > r.yMax + margin)
        {
            Rect m = ScreenBounds.MainRect; // (more than one screen: back onto the main one)
            Body.position = new Vector2(m.center.x, m.yMax - 1f);
            Body.linearVelocity = Vector2.zero;
            Body.angularVelocity = 0f;
        }
    }
}
