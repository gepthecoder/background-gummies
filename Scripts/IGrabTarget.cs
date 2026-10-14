using System.Collections.Generic;

/// <summary>Anything the mouse can hover, grab and drag (rigid Grabbable, GummyBody, ... ).</summary>
public interface IGrabTarget
{
    bool Hovered { get; set; }
    bool Held { get; }
    /// <summary>Is this world point on the object?</summary>
    bool Contains(UnityEngine.Vector2 world);
    void BeginGrab(UnityEngine.Vector2 world);
    void DragTo(UnityEngine.Vector2 world);
    void EndGrab();
}

/// <summary>Registry the Grabber searches. Targets add themselves in OnEnable and remove themselves in OnDisable.</summary>
public static class GrabTargets
{
    static readonly List<IGrabTarget> s_all = new List<IGrabTarget>();
    public static IReadOnlyList<IGrabTarget> All => s_all;
    public static void Add(IGrabTarget t) { if (!s_all.Contains(t)) s_all.Add(t); }
    public static void Remove(IGrabTarget t) => s_all.Remove(t);
}
