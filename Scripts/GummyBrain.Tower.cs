using UnityEngine;

// Buddy tower (BuddyTower): while it's part of one, BuddyTower moves it (standing at the bottom, climbing onto a
// friend's head, perched up there, jumping off the top onto the window) and the brain just keeps out of the way: it's
// inside the Hopping state like a jetpack flight, braced, awake. Grabbed = out of the tower (Recover, like any hop).
public partial class GummyBrain
{
    bool towering, sliding;

    /// <summary>In a buddy tower right now (BuddyTower moves it). Also while it's on the slide (Slide moves it).</summary>
    public bool Towering => towering;
    /// <summary>On the slide (climbing its ladder, waiting at the top, sliding down): the Slide moves it.</summary>
    public bool Sliding => sliding;

    /// <summary>Onto the slide: from now on the slide moves it (like a tower). False if it can't right now.</summary>
    public bool SlideStart()
    {
        if (sliding) return true;
        if (towering || !TowerStart()) return false;
        sliding = true;
        return true;
    }

    /// <summary>Join a tower: from now on BuddyTower moves it. False if it can't right now (held, asleep, on a rope...).</summary>
    public bool TowerStart()
    {
        if (sliding) return false; // (the slide has it)
        if (towering) return true;
        if (GiantPotion.IsGiant(body)) return false; // (too big for towers and slides)
        if (body.Held || body.IsStuck || body.OnRope || Current == State.Asleep || Current == State.Swing || Current == State.Climb || Current == State.Peek) return false;
        Enter(State.Hopping); // (off the board, plane, balloon, jetpack)
        towering = true;
        chasing = leapPlanned = edgeWalk = mantle = false;
        hop = Hop.Air;
        hopT = 0f;
        lastAttention = Time.time;
        return true;
    }

    /// <summary>Out of the tower: it lands (or stands) and carries on.</summary>
    public void TowerEnd()
    {
        if (!towering) return;
        EndTower();
        Enter(State.Recover);
    }

    /// <summary>Dizzy for a while (popped out of a fused monster).</summary>
    public void MakeDizzy(float seconds) => dizzyUntil = Mathf.Max(dizzyUntil, Time.time + seconds);

    /// <summary>Fell off a toppling tower: out of it, a bit dizzy.</summary>
    public void TowerFell(float dizzy)
    {
        TowerEnd();
        if (dizzy > 0f) dizzyUntil = Mathf.Max(dizzyUntil, Time.time + dizzy);
    }

    void EndTower()
    {
        if (!towering) return;
        towering = false;
        sliding = false;
        body.StopGlide();
        body.Lean = 0f;
        body.Pose = Vector2.one;
    }

    void UpdateTower(float dt)
    {
        hop = Hop.Air; // (in the air or not: BuddyTower decides)
        hopT = 0f;
        lastAttention = Time.time;
    }
}
