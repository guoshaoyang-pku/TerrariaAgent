using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;

namespace BossPilot.Bosses.Endgame;

/// <summary>
/// A predicted damage source. Each tick the planner rewinds every hazard to the
/// state captured from its live entity and steps it forward with a replica of
/// its kernel AI. Reactive hazards (homing shots, chasing bodies) depend on the
/// pilot's position, so they are re-simulated for every candidate trajectory.
/// </summary>
public abstract class Hazard
{
    public virtual bool Reactive => false;
    public bool Dead { get; protected set; }
    public abstract void Rewind();
    /// <summary>Advances one game tick; <paramref name="pilot"/> is the pilot center after its move this tick.</summary>
    public abstract void Step(Vector2 pilot);
    /// <summary>Appends the damaging shapes since the previous emit (fast movers emit their swept path).</summary>
    public abstract void Emit(List<Shape> sink);
}

/// <summary>Box that moves with constant per-tick velocity (bolts, fireballs, generic projectiles).</summary>
public sealed class LinearHazard : Hazard
{
    private readonly Vector2 pos0, vel, half, blastHalf;
    private readonly int life0, delay0;
    private Vector2 pos, prev;
    private int life, delay;

    /// <param name="delay">Ticks before it starts dealing damage (telegraphs).</param>
    /// <param name="blastHalf">Half size of the Projectile.Kill explosion when its life ends (zero = none).</param>
    public LinearHazard(Vector2 center, Vector2 velocityPerTick, Vector2 half, int life = 600, int delay = 0, Vector2 blastHalf = default)
    {
        pos0 = center; vel = velocityPerTick; this.half = half; life0 = life; delay0 = delay; this.blastHalf = blastHalf;
        Rewind();
    }

    public static LinearHazard Of(Projectile pr) =>
        new(pr.Center, pr.velocity * (1 + pr.extraUpdates), new Vector2(pr.width, pr.height) * 0.5f, pr.timeLeft);

    public override void Rewind() { pos = prev = pos0; life = life0; delay = delay0; Dead = false; }

    public override void Step(Vector2 pilot)
    {
        if (life <= 0) { Dead = true; return; }
        pos += vel;
        life--;
        if (delay > 0) delay--;
    }

    public override void Emit(List<Shape> sink)
    {
        if (!Dead && delay <= 0)
        {
            if (life <= 0 && blastHalf != Vector2.Zero) sink.Add(Shape.Box(pos, blastHalf));
            else if (Vector2.DistanceSquared(pos, prev) > 64f) sink.Add(Shape.Segment(prev, pos, Math.Max(half.X, half.Y)));
            else sink.Add(Shape.Box(pos, half));
        }
        prev = pos;
    }
}

/// <summary>Contact damage of an NPC body extrapolated with a constant velocity.</summary>
public sealed class BodyHazard : Hazard
{
    private readonly Vector2 pos0, vel, half;
    private readonly float damp;
    private Vector2 pos, v;

    public BodyHazard(NPC n, float inflate = 0f, float damp = 1f)
    {
        pos0 = n.Center; vel = n.velocity; half = new Vector2(n.width, n.height) * 0.5f + new Vector2(inflate); this.damp = damp;
        Rewind();
    }

    public override void Rewind() { pos = pos0; v = vel; }
    public override void Step(Vector2 pilot) { pos += v; v *= damp; }
    public override void Emit(List<Shape> sink) => sink.Add(Shape.Box(pos, half));
}
