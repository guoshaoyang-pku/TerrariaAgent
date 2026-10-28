using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;

namespace BossPilot.Bosses.Endgame;

/// <summary>
/// CultistBossIceMist (464, aiStyle 86). The cast mist (ai[1] == 1) drifts at its
/// velocity, spins pi/30 per tick and every 30 ticks drops a shard burst; a burst
/// (ai[1] != 1) stays put and for 45 ticks pushes six 30x30 hitboxes outwards
/// from 0 to 720 px along rotation(velocity) - 90deg + k*60deg (Projectile.Colliding).
/// Future bursts of a mist are predicted from its spin.
/// </summary>
public sealed class IceMist : Hazard
{
    private struct Burst { public Vector2 C; public float Angle; public float Ai0; }

    private readonly bool isCast;
    private readonly Vector2 c0, vel;
    private readonly float ai0_, rot0;
    private readonly int w;
    private readonly List<Burst> bursts = [];
    private readonly List<Burst> prevBursts = [];
    private Vector2 c;
    private float ai0, rot;

    public IceMist(Projectile pr)
    {
        isCast = pr.ai[1] == 1f;
        c0 = pr.Center; vel = pr.velocity; ai0_ = pr.ai[0]; rot0 = pr.rotation; w = pr.width;
        Rewind();
    }

    public override void Rewind()
    {
        c = c0; ai0 = ai0_; rot = rot0; Dead = false;
        bursts.Clear();
        if (!isCast) bursts.Add(new Burst { C = c0, Angle = vel.ToRotation(), Ai0 = ai0_ });
    }

    public override void Step(Vector2 pilot)
    {
        prevBursts.Clear();
        prevBursts.AddRange(bursts);
        for (int i = bursts.Count - 1; i >= 0; i--)
        {
            Burst b = bursts[i];
            b.Ai0++;
            if (b.Ai0 >= 45) bursts.RemoveAt(i);
            else bursts[i] = b;
        }
        if (isCast && ai0 < 150)
        {
            ai0++;
            if (ai0 % 30f == 0f) bursts.Add(new Burst { C = c, Angle = rot, Ai0 = 0 });
            rot += MathHelper.Pi / 30f;
            c += vel;
        }
        if (bursts.Count == 0 && (!isCast || ai0 >= 150)) Dead = true;
    }

    public override void Emit(List<Shape> sink)
    {
        if (isCast && ai0 < 150) sink.Add(Shape.Box(c, new Vector2(w * 0.5f)));
        foreach (Burst b in bursts)
        {
            float r = 720f * (b.Ai0 % 45f / 45f);
            // Swept over the last two ticks (32 px), so a fast ring cannot jump past the pilot.
            float r0 = Math.Max(0, r - 32f);
            for (int k = 0; k < 6; k++)
            {
                Vector2 u = (b.Angle - MathHelper.PiOver2 + k * MathHelper.Pi / 3f).ToRotationVector2();
                sink.Add(Shape.Segment(b.C + u * r0, b.C + u * r, 15f));
            }
        }
    }
}

/// <summary>
/// CultistBossLightningOrb (465): 80x80, fires a CultistBossLightningOrbArc every
/// 30 ticks until tick 180, aimed at the pilot's position at that tick (no lead),
/// then fades out (still damaging) for ~51 ticks.
/// Reactive, so candidates that keep moving see the arcs miss.
/// </summary>
public sealed class LightningOrb : Hazard
{
    private readonly Vector2 c;
    private readonly float ai0_;
    private readonly List<ArcPath> arcs = [];
    private float ai0;

    public override bool Reactive => true;

    public LightningOrb(Projectile pr) { c = pr.Center; ai0_ = pr.ai[0]; Rewind(); }

    public override void Rewind() { ai0 = ai0_; arcs.Clear(); Dead = false; }

    public override void Step(Vector2 pilot)
    {
        foreach (ArcPath a in arcs) a.Step();
        ai0++;
        if (ai0 % 30f == 0f && ai0 < 180f) arcs.Add(new ArcPath(c, (pilot - c).SafeNormalize(Vector2.UnitY), 0f, 0f));
    }

    public override void Emit(List<Shape> sink)
    {
        // After tick 180 it fades out over ~51 ticks (alpha +5) and still hurts until Kill.
        if (ai0 < 232) sink.Add(Shape.Box(c, new Vector2(40)));
        foreach (ArcPath a in arcs) a.Emit(sink);
    }
}

/// <summary>
/// Reach of one CultistBossLightningOrbArc (466, aiStyle 88): every 1.6 ticks it
/// picks a seeded random heading in the forward half-plane of its aim ai[0],
/// keeping its sideways drift within +-40 px of the aim line, at 7 px x 5
/// updates. So the head is 0.7-35 px/tick down the line (about 22 on average)
/// and the 20-position trail hangs ~4 ticks behind: a +-47 px band whose
/// front advances at 35 and back at 10 px/tick.
/// </summary>
public sealed class ArcPath
{
    private readonly Vector2 start, dir;
    private float front, back;

    /// <param name="front">Distance along the aim line of the arc head now.</param>
    /// <param name="back">Distance of the trail end now (negative behind the start point).</param>
    public ArcPath(Vector2 start, Vector2 dir, float front, float back) { this.start = start; this.dir = dir; this.front = front; this.back = back; }

    public void Step() { front += 35f; back += 10f; }

    public void Emit(List<Shape> sink) => sink.Add(Shape.Segment(start + dir * back, start + dir * front, 47f));
}

/// <summary>A CultistBossLightningOrbArc (466) already in flight, see <see cref="ArcPath"/>.</summary>
public sealed class LightningArc : Hazard
{
    private readonly Vector2 start, dir;
    private ArcPath path;

    public LightningArc(Projectile pr)
    {
        dir = pr.ai[0].ToRotationVector2();
        // localAI[0] is the sideways drift along aim + 90deg; step back onto the aim line.
        start = pr.Center - (pr.ai[0] + MathHelper.PiOver2).ToRotationVector2() * pr.localAI[0];
        Rewind();
    }

    public override void Rewind() { path = new ArcPath(start, dir, 0f, -90f); Dead = false; }
    public override void Step(Vector2 pilot) => path.Step();
    public override void Emit(List<Shape> sink) => path.Emit(sink);
}

/// <summary>
/// Lunatic Cultist body (439, 50 contact). While repositioning (ai[0] == 1) the
/// AI undoes the velocity step on odd ai[1] (except 1) and zeroes velocity when
/// ai[1] runs out, so it advances one velocity step on even ai[1] only; in its
/// other states it hovers in place (the intro drifts up 1 px/tick).
/// </summary>
public sealed class CultistBody : Hazard
{
    private readonly Vector2 c0, v0, half;
    private readonly float ai0_, ai1_;
    private Vector2 c, v;
    private float ai1;

    public CultistBody(NPC n) { c0 = n.Center; v0 = n.velocity; ai0_ = n.ai[0]; ai1_ = n.ai[1]; half = new Vector2(n.width, n.height) * 0.5f + new Vector2(2f); Rewind(); }

    public override void Rewind() { c = c0; v = v0; ai1 = ai1_; Dead = false; }

    public override void Step(Vector2 pilot)
    {
        if (ai0_ == 1f)
        {
            if (ai1 > 0)
            {
                bool moves = (int)ai1 % 2 == 0;
                ai1--;
                if (ai1 <= 0) v = Vector2.Zero;
                if (moves) c += v;
            }
        }
        else c += v;
    }

    public override void Emit(List<Shape> sink) => sink.Add(Shape.Box(c, half));
}

/// <summary>
/// CultistBossFireBall (467, aiStyle 1, 2 updates/tick): from its third update
/// until ai[1] reaches 200 it turns towards the pilot by AngleLerp 0.008 per
/// update at constant speed; within 20 px of the pilot it is killed, and Kill
/// blows it up as a 176x176 hitbox. Reactive. Clone fireballs (468) do not home.
/// </summary>
public sealed class CultistFireball : Hazard
{
    private readonly Vector2 c0, v0, half;
    private readonly float ai1_;
    private Vector2 c, v, prev;
    private float ai1;
    private bool blast;
    private int blastFor;   // kept for a few ticks so sparse samples late in the horizon still see it

    public override bool Reactive => true;

    public CultistFireball(Projectile pr) { c0 = pr.Center; v0 = pr.velocity; ai1_ = pr.ai[1]; half = new Vector2(pr.width, pr.height) * 0.5f; Rewind(); }

    public override void Rewind() { c = prev = c0; v = v0; ai1 = ai1_; blast = false; blastFor = 3; Dead = false; }

    public override void Step(Vector2 pilot)
    {
        if (blast) { if (--blastFor <= 0) Dead = true; return; }
        for (int u = 0; u < 2; u++)
        {
            if (ai1 == 1f) ai1 = 21f;
            else if (ai1 > 20f && ai1 < 200f)
            {
                ai1++;
                Vector2 d = pilot - c;
                if (d.Length() < 20f) { blast = true; return; }
                float rot = v.ToRotation().AngleLerp(d.ToRotation(), 0.008f);
                v = new Vector2(v.Length(), 0f).RotatedBy(rot);
            }
            c += v;
        }
    }

    public override void Emit(List<Shape> sink)
    {
        if (Dead) return;
        sink.Add(blast ? Shape.Box(c, new Vector2(88f)) : Shape.Segment(prev, c, half.X));
        prev = c;
    }
}

/// <summary>
/// Ancient Light (NPC 522, aiStyle 100): after 60 ticks its velocity rotates by
/// ai[1] per tick, after 120 it slows by 0.98 per tick. Contact damage.
/// </summary>
public sealed class AncientLight : Hazard
{
    private readonly Vector2 c0, v0, half;
    private readonly float ai0_, spin;
    private Vector2 c, v, prev;
    private float ai0;

    public AncientLight(NPC n) { c0 = n.Center; v0 = n.velocity; ai0_ = n.ai[0]; spin = n.ai[1]; half = new Vector2(n.width, n.height) * 0.5f; Rewind(); }

    public override void Rewind() { c = prev = c0; v = v0; ai0 = ai0_; Dead = false; }

    public override void Step(Vector2 pilot)
    {
        ai0++;
        if (ai0 > 60) v = v.RotatedBy(spin);
        if (ai0 > 120) v *= 0.98f;
        c += v;
    }

    public override void Emit(List<Shape> sink)
    {
        sink.Add(Vector2.DistanceSquared(c, prev) > 64f ? Shape.Segment(prev, c, half.X) : Shape.Box(c, half));
        prev = c;
    }
}
