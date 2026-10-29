using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace BossPilot.Bosses.EvilDeer;

/// <summary>
/// Grenade ballistics (Projectile AI_016, type 30): thrown at the item's shootSpeed, no gravity
/// for the first 10 ticks, then +0.2 px/tick²; passes through platforms; on touching an NPC it
/// arms (timeLeft = 3) and explodes as a 128x128 box. The explosion also hurts its thrower.
/// </summary>
public static class Grenades
{
    public const int Proj = ProjectileID.Grenade;
    public const float Blast = 128f;

    /// <summary>Position of a grenade thrown from <paramref name="from"/> after t ticks.</summary>
    public static Vector2 At(Vector2 from, Vector2 v, int t)
    {
        Vector2 pos = from;
        for (int k = 1; k <= t; k++)
        {
            if (k > 10) v.Y += 0.2f;
            pos += v;
        }
        return pos;
    }

    /// <summary>
    /// Direction whose arc passes closest to <paramref name="target"/>; returns false when no
    /// arc gets within <paramref name="tolerance"/> px. <paramref name="ticks"/> is the arrival time.
    /// </summary>
    public static bool Solve(Vector2 from, Vector2 target, float speed, float tolerance, out Vector2 dir, out int ticks)
    {
        dir = Vector2.UnitY;
        ticks = 0;
        float bestMiss = float.MaxValue, bestAngle = 0f;
        int bestT = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            float center = bestAngle, span = pass == 0 ? MathHelper.Pi : 0.09f;
            int steps = pass == 0 ? 72 : 12;
            for (int k = 0; k <= steps; k++)
            {
                float a = center - span + 2f * span * k / steps;
                Vector2 v = a.ToRotationVector2() * speed, pos = from;
                float prevD = float.MaxValue;
                for (int t = 1; t <= 90; t++)
                {
                    if (t > 10) v.Y += 0.2f;
                    pos += v;
                    float d = Vector2.Distance(pos, target);
                    if (d < bestMiss - 0.5f || (Math.Abs(d - bestMiss) <= 0.5f && t < bestT))
                    {
                        bestMiss = d; bestAngle = a; bestT = t;
                    }
                    if (d > prevD && t > 12 && d > 200f) break;
                    prevD = d;
                }
            }
        }
        dir = bestAngle.ToRotationVector2();
        ticks = bestT;
        return bestMiss <= tolerance;
    }

    /// <summary>Own live grenades near the pilot as blast boxes (they can explode on any contact).</summary>
    public static void AddOwnBlasts(HazardField f, Player p)
    {
        foreach (Projectile pr in Main.projectile)
        {
            if (!pr.active || pr.owner != p.whoAmI || pr.type != Proj) continue;
            if (Vector2.Distance(pr.Center, p.Center) > 260f) continue;
            Track tr = f.NewTrack();
            Vector2 c = pr.Center, v = pr.velocity;
            float ai0 = pr.ai[0];
            for (int t = 1; t <= HazardField.Horizon && t <= pr.timeLeft; t++)
            {
                ai0++;
                if (ai0 > 10f) v.Y += 0.2f;
                c += v;
                // Throws start inside the pilot (and are only thrown with nothing near); count the
                // blast once it is clear of where the pilot stands now, so the planner keeps away.
                if (Vector2.Distance(c, p.Center) > 96f) tr.Set(t, Box.Centered(c, Blast, Blast));
            }
        }
    }
}
