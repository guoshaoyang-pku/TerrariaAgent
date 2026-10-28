using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;

namespace BossPilot.Bosses.Endgame;

/// <summary>
/// Replica of NPC.AI_120_HallowBoss movement (Terraria 1.4.4.9): per-attack
/// fly targets, the case-1 DashTo reposition, the horizontal 50 px/tick dash
/// (cases 8/9), and the deterministic attack cycle keyed by ai[2]. Reactive:
/// almost every attack flies to a point relative to the pilot.
/// </summary>
public sealed class EmpressBody : Hazard
{
    private readonly Vector2 pos0, vel0, half;
    private readonly float ai0_, ai1_, ai2_;
    private readonly bool phase2, hard;   // hard = expert patterns (enraged daytime Empress forces them)
    private readonly float lifeFrac;
    private Vector2 pos, vel;
    private int ai0;
    private float ai1, ai2;

    public override bool Reactive => true;

    public EmpressBody(NPC n)
    {
        pos0 = n.Center; vel0 = n.velocity; half = new Vector2(n.width, n.height) * 0.5f;
        ai0_ = n.ai[0]; ai1_ = n.ai[1]; ai2_ = n.ai[2];
        phase2 = n.AI_120_HallowBoss_IsInPhase2();
        hard = Main.expertMode || NPC.ShouldEmpressBeEnraged();
        lifeFrac = n.life / (float)n.lifeMax;
        Rewind();
    }

    public Vector2 Center => pos;
    public int Attack => ai0;
    public float Timer => ai1;

    public override void Rewind() { pos = pos0; vel = vel0; ai0 = (int)ai0_; ai1 = ai1_; ai2 = ai2_; Dead = false; }

    private int Num17 => (phase2 ? 15 : 0) + (hard ? 5 : 0);

    public override void Step(Vector2 player)
    {
        const float speed = 12f, accel = 0.5f;
        switch (ai0)
        {
            case 0:
                vel *= 0.95f;
                if (++ai1 >= 180) { ai0 = 1; ai1 = 0; }
                break;
            case 1:
            {
                float dur = phase2 ? 20f : 45f;
                if (ai1 <= 10) DashTo(player);
                if (vel.Length() > 16f && ai1 > 10) vel /= 2f;
                vel *= 0.92f;
                if (++ai1 >= dur)
                {
                    ai0 = NextAttack(player);
                    ai1 = 0; ai2++;
                    if (hard && ai0 != 5 && ai0 != 12)
                    {
                        Vector2 from = (pos - player).SafeNormalize(Vector2.Zero);
                        vel = from.RotatedBy(MathHelper.PiOver2 * (player.X > pos.X ? 1 : -1)) * 20f;
                    }
                }
                break;
            }
            case 2: Fly(player + new Vector2(-150, -250), speed, accel); End(60 + 90 - Num17); break;
            case 3: Fly(player + new Vector2(150, -250), speed, accel); End(120); break;
            case 4: Fly(player + new Vector2(0, -350), speed, accel); End(100 + 20 - Num17); break;
            case 5: Fly(player + new Vector2(0, -350), speed, accel); End(42 + 30 - Num17); break;
            case 6: Fly(player + new Vector2(-80, -500), speed * 0.3f, accel * 0.7f); End(180 + 120 - Num17); break;
            case 7: Fly(player + new Vector2(0, -350), speed * 0.4f, accel); End((hard ? 240 : 240) + (hard ? 40 : 20) - Num17); break;
            case 8:
            case 9:
            {
                int dir = ai0 == 8 ? -1 : 1;
                if (ai1 <= 40)
                {
                    Vector2 dest = player + new Vector2(dir * -550, 0);
                    SimpleFly((dest - pos).SafeNormalize(Vector2.Zero) * speed, accel * 2f);
                    if (ai1 == 40) vel *= 0.3f;
                }
                else if (ai1 <= 90)
                {
                    vel = Vector2.Lerp(vel, new Vector2(dir * 50, 0), 0.05f);
                    if (ai1 == 90) vel *= 0.7f;
                }
                else vel *= 0.92f;
                End(90 + 20 - Num17);
                break;
            }
            case 10:
                vel *= 0.95f;
                if (ai1 == 90) pos = player + new Vector2(0, -250);
                if (++ai1 >= 180 + 20 - Num17) { ai0 = 1; ai1 = 0; ai2 = 0; }
                break;
            case 11: Fly(player + new Vector2(0, -350), speed, accel); End(100 + 20 - Num17); break;
            case 12:
                if (ai1 == 0) vel = new Vector2(0, -12);
                vel *= 0.95f;
                End(60 + 90 - Num17);
                break;
            default:
                vel *= 0.95f;
                ai1++;
                break;
        }
        pos += vel;
    }

    private void End(float duration)
    {
        if (++ai1 >= duration) { ai0 = 1; ai1 = 0; }
    }

    private void Fly(Vector2 target, float speed, float accel)
    {
        if (Vector2.Distance(pos, target) > 40f) SimpleFly((target - pos).SafeNormalize(Vector2.Zero) * speed, accel);
    }

    /// <summary>NPC.SimpleFlyMovement.</summary>
    private void SimpleFly(Vector2 desired, float a)
    {
        if (vel.X < desired.X) { vel.X += a; if (vel.X < 0 && desired.X > 0) vel.X += a; }
        else if (vel.X > desired.X) { vel.X -= a; if (vel.X > 0 && desired.X < 0) vel.X -= a; }
        if (vel.Y < desired.Y) { vel.Y += a; if (vel.Y < 0 && desired.Y > 0) vel.Y += a; }
        else if (vel.Y > desired.Y) { vel.Y -= a; if (vel.Y > 0 && desired.Y < 0) vel.Y -= a; }
    }

    /// <summary>NPC.AI_120_HallowBoss_DashTo.</summary>
    private void DashTo(Vector2 target)
    {
        Vector2 tp = target + new Vector2(0, -300);
        if (Vector2.Distance(pos, tp) > 200f) tp -= (tp - pos).SafeNormalize(Vector2.Zero) * 100f;
        Vector2 v = tp - pos;
        float lerp = Utils.GetLerpValue(100f, 600f, v.Length(), clamped: true);
        float len = Math.Min(v.Length(), 18f);
        vel = Vector2.Lerp(v.SafeNormalize(Vector2.Zero) * len, v / 6f, lerp);
    }

    /// <summary>Attack chosen at the end of case 1 (same table as the kernel).</summary>
    private int NextAttack(Vector2 player)
    {
        int idx = (int)ai2, pick;
        if (!phase2)
        {
            int[] cycle = [2, 8, 6, 8, 5, 2, 8, 4, 8, 5];
            pick = cycle[idx % cycle.Length];
            if (lifeFrac <= 0.5f) pick = 10;
        }
        else
        {
            int[] cycle = hard ? [7, 2, 8, 11, 5, 2, 6, 4, 8, 12] : [7, 2, 8, 5, 2, 6, 4, 8, 12];
            pick = cycle[idx % cycle.Length];
        }
        if (pick == 8 && player.X > pos.X) pick = 9;
        return pick;
    }

    public override void Emit(List<Shape> sink) => sink.Add(Shape.Box(pos, half));
}

/// <summary>
/// HallowBossRainbowStreak (873, AI_171): curls and slows for 60 ticks, then
/// homes on the pilot (SmoothStep towards 30 px/tick) until 30 ticks before
/// expiry, then flies straight. Reactive.
/// </summary>
public sealed class RainbowStreak : Hazard
{
    private readonly Vector2 tl0, vel0;
    private readonly int life0, who;
    private Vector2 tl, vel, prev;
    private int life;

    public override bool Reactive => true;

    public RainbowStreak(Projectile pr) { tl0 = pr.position; vel0 = pr.velocity; life0 = pr.timeLeft; who = pr.whoAmI; Rewind(); }

    public override void Rewind() { tl = prev = tl0; vel = vel0; life = life0; Dead = false; }

    public override void Step(Vector2 pilot)
    {
        if (life > 140)
        {
            float c = (float)Math.Cos(who % 6f / 6f + tl.X / 320f + tl.Y / 160f);
            vel *= 0.98f;
            vel = vel.RotatedBy(c * MathHelper.TwoPi * 0.125f / 30f);
        }
        else if (life > 30)
        {
            Vector2 aim = (pilot - (tl + new Vector2(15))).SafeNormalize(Vector2.Zero) * 30f;
            float amount = MathHelper.Lerp(0.05f, 0.1f, Utils.GetLerpValue(140f, 30f, life, clamped: true));
            vel = Vector2.SmoothStep(vel, aim, amount);
        }
        tl += vel;
        if (--life <= 0) Dead = true;
    }

    public override void Emit(List<Shape> sink)
    {
        Vector2 c = tl + new Vector2(15), pc = prev + new Vector2(15);
        sink.Add(Vector2.DistanceSquared(c, pc) > 64f ? Shape.Segment(pc, c, 15f) : Shape.Box(c, new Vector2(15)));
        prev = tl;
    }
}

/// <summary>
/// HallowBossLastingRainbow (872, AI_173): velocity rotates by ai[0], which ramps
/// to pi/360 per tick; collision is every other cached old position of the last
/// 80 (Projectile.Colliding), modelled as a chain of thick segments.
/// </summary>
public sealed class RainbowTrail : Hazard
{
    private const int Trail = 80, Every = 8;
    private readonly Vector2 tl0, vel0;
    private readonly float ai0_;
    private readonly int life0;
    private readonly Vector2[] hist0;
    private Vector2 tl, vel;
    private float ai0;
    private int life;
    private readonly List<Vector2> hist = [];   // newest first, top-left

    public RainbowTrail(Projectile pr)
    {
        tl0 = pr.position; vel0 = pr.velocity; ai0_ = pr.ai[0]; life0 = pr.timeLeft;
        int n = 0;
        while (n < Trail && n < pr.oldPos.Length && pr.oldPos[n] != Vector2.Zero) n++;
        hist0 = new Vector2[n];
        Array.Copy(pr.oldPos, hist0, n);
        Rewind();
    }

    public override void Rewind()
    {
        tl = tl0; vel = vel0; ai0 = ai0_; life = life0; Dead = false;
        hist.Clear();
        hist.AddRange(hist0);
    }

    public override void Step(Vector2 pilot)
    {
        const float max = MathHelper.Pi / 360f;
        vel = vel.RotatedBy(ai0);
        if (ai0 < max) ai0 += max / 30f;
        tl += vel;
        hist.Insert(0, tl);
        if (hist.Count > Trail) hist.RemoveAt(hist.Count - 1);
        if (--life <= 0) Dead = true;
    }

    public override void Emit(List<Shape> sink)
    {
        Vector2 off = new(15);
        for (int i = 0; i + Every < hist.Count; i += Every)
            sink.Add(Shape.Segment(hist[i] + off, hist[i + Every] + off, 15f));
        if (hist.Count == 1) sink.Add(Shape.Box(hist[0] + off, off));
    }
}

/// <summary>
/// FairyQueenLance (919, AI_179): fixed for 60 ticks (harmless), then flies at
/// 40 px/tick along ai[0]. Collision: line center +- 40 px along its rotation, 8 px wide.
/// </summary>
public sealed class Lance : Hazard
{
    private readonly Vector2 c0, dir;
    private readonly float la0;
    private Vector2 c, prev;
    private float la;

    public Lance(Projectile pr) { c0 = pr.Center; dir = pr.ai[0].ToRotationVector2(); la0 = pr.localAI[0]; Rewind(); }

    public override void Rewind() { c = prev = c0; la = la0; Dead = false; }

    public override void Step(Vector2 pilot)
    {
        la++;
        if (la >= 60) c += dir * 40f;
        if (la >= 360) Dead = true;
    }

    public override void Emit(List<Shape> sink)
    {
        if (la > 60) sink.Add(Shape.Segment(prev - dir * 40f, c + dir * 40f, 4f));
        prev = c;
    }
}

/// <summary>
/// FairyQueenSunDance rays (923, AI_180) of one cast. They stay centered on the
/// Empress (who drifts towards pilot + (-80,-500) at 3.6 px/tick in case 6),
/// rotate 20 degrees between ticks 50 and 180, and damage only after tick 60
/// with three nested line widths (Projectile.Colliding). Reactive through the Empress.
/// </summary>
public sealed class SunDance : Hazard
{
    private readonly struct Ray(float baseAngle, float localAI) { public readonly float Base = baseAngle, Local = localAI; }

    private readonly List<Ray> rays = [];
    private readonly Vector2 boss0, bossVel0;
    private Vector2 boss, bossVel;
    private int t;

    public override bool Reactive => true;

    public SunDance(NPC empress) { boss0 = empress.Center; bossVel0 = empress.velocity; Rewind(); }

    public void Add(Projectile pr) => rays.Add(new Ray(pr.ai[0], pr.localAI[0]));
    public bool Any => rays.Count > 0;

    public override void Rewind() { boss = boss0; bossVel = bossVel0; t = 0; Dead = false; }

    public override void Step(Vector2 pilot)
    {
        Vector2 target = pilot + new Vector2(-80, -500);
        if (Vector2.Distance(boss, target) > 40f)
        {
            Vector2 d = (target - boss).SafeNormalize(Vector2.Zero) * 3.6f;
            const float a = 0.35f;
            if (bossVel.X < d.X) { bossVel.X += a; if (bossVel.X < 0 && d.X > 0) bossVel.X += a; }
            else if (bossVel.X > d.X) { bossVel.X -= a; if (bossVel.X > 0 && d.X < 0) bossVel.X -= a; }
            if (bossVel.Y < d.Y) { bossVel.Y += a; if (bossVel.Y < 0 && d.Y > 0) bossVel.Y += a; }
            else if (bossVel.Y > d.Y) { bossVel.Y -= a; if (bossVel.Y > 0 && d.Y < 0) bossVel.Y -= a; }
        }
        boss += bossVel;
        t++;
    }

    public override void Emit(List<Shape> sink)
    {
        foreach (Ray r in rays)
        {
            float la = r.Local + t;
            if (la <= 60 || la >= 180) continue;
            float scale = Utils.GetLerpValue(0f, 20f, la, clamped: true) * Utils.GetLerpValue(180f, 120f, la, clamped: true);
            float rot = r.Base + Utils.GetLerpValue(50f, 180f, la, clamped: true) * MathHelper.Pi / 9f;
            Vector2 u = rot.ToRotationVector2();
            float w = scale * 0.7f;
            sink.Add(Shape.Segment(boss, boss + u * scale * 510f, w * 50f));
            sink.Add(Shape.Segment(boss, boss + u * scale * 660f, w * 30f));
            sink.Add(Shape.Segment(boss, boss + u * scale * 800f, w * 5f));
        }
    }
}
