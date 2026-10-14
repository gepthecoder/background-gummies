using UnityEngine;

/// <summary>
/// Extra solid shapes for a gummy body in its room (GummyBody.Room + GummyBody.Arena): the pinball table's flippers,
/// rails and bumpers (PinballTable). The body asks every physics step, so moving shapes (a flipper) stay in step with it.
/// Shapes are numbered by the arena; -1 = none.
/// </summary>
public interface IGummyArena
{
    /// <summary>Once per physics step, before the body moves: move what moves (flippers turn) by dt.</summary>
    void Advance(GummyBody body, float dt);
    /// <summary>Push a point of the body out of every shape. prev = where it was when the step started (so a fast point
    /// that went right through a thin shape goes back to the side it came from). Returns the shape it was pushed out of
    /// (-1: none), the shape's outward normal there and how fast the shape's surface moves there.</summary>
    int Push(ref Vector2 p, Vector2 prev, out Vector2 normal, out Vector2 surfaceVelocity);
    /// <summary>How bouncy (0..1) and grippy (Coulomb friction) a shape is.</summary>
    void Surface(int shape, out float bounce, out float grip);
    /// <summary>After the step: the body touched this shape (where, the shape's normal, how fast it hit it).</summary>
    void Touched(GummyBody body, int shape, Vector2 at, Vector2 normal, float speed);
}
