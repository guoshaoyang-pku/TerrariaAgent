using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace BossPilot.Bosses.Mechs;

/// <summary>
/// Reads the fire timers of mech attackers (Classic values from NPC.VanillaAI_Inner) and
/// schedules their next aimed shot in the threat set. The shot is aimed at the pilot's
/// position at fire time, so each plan sees the laser it would draw: plans that are
/// moving across the line of fire when it spawns are the ones that come out clean.
/// </summary>
public static class ShotSchedule
{
    private static Vector2 Vel(NPC n) => n.position - n.oldPosition;

    private static int Ticks(float counter, float threshold, float inc, bool strictlyAbove)
    {
        // counter += inc each tick; fires on the first tick it reaches (>=) / exceeds (>) threshold.
        float need = threshold - counter;
        int k = strictlyAbove ? (int)Math.Floor(need / inc) + 1 : (int)Math.Ceiling(need / inc);
        return Math.Max(k, 1);
    }

    /// <summary>Retinazer (aiStyle 30): eye laser in phase 1, death laser in phase 2.</summary>
    public static void Retinazer(NPC n, Player p, Threats th)
    {
        th.NextTag = 1000 + n.type;
        float life = n.life / (float)n.lifeMax;
        if (n.ai[0] == 0f && n.ai[1] == 0f)
        {
            // ai[3] += 1 per tick while above the pilot and near its hover point; fires at 60.
            int k = Ticks(n.ai[3], 60f, 1f, strictlyAbove: false);
            if (n.ai[2] + k < 600f) th.AddShot(k, n.Center + Vel(n) * k, 27f, 135f, 9.6f, 8f);
        }
        else if (n.ai[0] == 3f)
        {
            bool above = n.ai[1] == 0f;
            float inc = above
                ? 1f + (life < 0.75f ? 1f : 0f) + (life < 0.5f ? 1f : 0f) + (life < 0.25f ? 1f : 0f) + (life < 0.1f ? 2f : 0f)
                : 1f + (life < 0.75f ? 0.5f : 0f) + (life < 0.5f ? 0.75f : 0f) + (life < 0.25f ? 1f : 0f) + (life < 0.1f ? 1.5f : 0f);
            float speed = above ? 8.5f : 9f;
            int k = Ticks(n.localAI[1], above ? 180f : 60f, inc, strictlyAbove: true);
            th.AddShot(k, n.Center + Vel(n) * k, speed * 3f, speed * 15f, 0f, 8f);
            // The side volley repeats inside the horizon.
            int k2 = k + (int)Math.Ceiling(60f / inc);
            if (!above && k2 <= Threats.H) th.AddShot(k2, n.Center + Vel(n) * k2, speed * 3f, speed * 15f, 0f, 8f);
        }
    }

    /// <summary>
    /// Twin dashes: when the hover timer (ai[2] reaching 600, or 400 for Spazmatism's
    /// second form) or the previous dash (ai[2] reaching 70 / 42 / 80) runs out, ai[1] = 1 and
    /// on the next tick velocity is set straight at the pilot: Retinazer 12 px/tick held 25
    /// ticks then x0.96, Spazmatism 13 held 8 then x0.9, its second form 14 held 50 then x0.93;
    /// 4 / 10 / 6 dashes per volley.
    /// </summary>
    public static void TwinDashes(NPC n, Threats th)
    {
        th.NextTag = 1000 + n.type;
        bool ret = n.type == NPCID.Retinazer;
        float speed, decay, hover, period;
        int hold, count;
        if (n.ai[0] == 0f)
        {
            speed = ret ? 12f : 13f; hold = ret ? 25 : 8; decay = ret ? 0.96f : 0.9f;
            period = ret ? 70f : 42f; hover = 600f; count = ret ? 4 : 10;
        }
        else if (n.ai[0] == 3f && !ret)
        {
            speed = 14f; hold = 50; decay = 0.93f; period = 80f; hover = 400f; count = 6;
        }
        else return;

        Vector2 c = n.Center, v = Vel(n);
        int k;
        if (n.ai[1] == 0f) k = (int)Math.Ceiling(hover - n.ai[2]) + 1;
        else if (n.ai[1] == 1f) k = 1;
        else
        {
            if (n.ai[3] + 1 >= count) return; // last dash of the volley
            k = (int)Math.Ceiling(period - n.ai[2]) + 1;
        }
        if (k > Threats.H) return;
        // Muzzle at the aim tick: hover drifts on; a dash in progress decays after its hold.
        float a = n.ai[2];
        for (int t = 0; t < k; t++)
        {
            c += v;
            if (n.ai[1] == 2f && ++a >= hold) v *= decay;
        }
        th.AddDash(k, c, speed, 0f, 0f, n.width, n.height, life: Threats.H, constTicks: hold, decay: decay);
    }

    /// <summary>Spazmatism (aiStyle 31) phase 1: cursed flame every 60 ticks of ai[3].</summary>
    public static void Spazmatism(NPC n, Player p, Threats th)
    {
        th.NextTag = 1000 + n.type;
        if (n.ai[0] != 0f || n.ai[1] != 0f) return;
        int k = Ticks(n.ai[3], 60f, 1f, strictlyAbove: false);
        if (n.ai[2] + k < 600f) th.AddShot(k, n.Center + Vel(n) * k, 12f, 48f, 2f, 16f, life: Threats.H);
    }

    /// <summary>Prime Laser (aiStyle 36): death laser on localAI[0], faster while the head spins.</summary>
    public static void PrimeLaser(NPC n, Threats th)
    {
        th.NextTag = 1000 + n.type;
        int headIdx = (int)n.ai[1];
        if (headIdx < 0 || headIdx >= Main.maxNPCs) return;
        NPC head = Main.npc[headIdx];
        if (n.ai[2] == 0f || n.ai[2] == 3f)
        {
            float inc = head.ai[1] != 0f ? 4f : 1f;
            int k = Ticks(n.localAI[0], 200f, inc, strictlyAbove: true);
            th.AddShot(k, n.Center + Vel(n) * k, 24f, 64f, 6f, 8f);
        }
        else if (n.ai[2] == 1f)
        {
            int k = Ticks(n.localAI[0], 80f, 1f, strictlyAbove: true);
            th.AddShot(k, n.Center + Vel(n) * k, 30f, 80f, 6f, 8f);
        }
    }

    /// <summary>
    /// Spazmatism's second form flamethrower (aiStyle 31, ai[1] == 0): localAI[1] grows by
    /// 1..6 per tick with lost life and fires above 8, a 6 px/tick flame (+half Spazmatism's
    /// velocity, x4 updates, 60 updates of life, hitbox inflated by 30) aimed at the pilot.
    /// </summary>
    public static void SpazFlames(NPC n, Threats th)
    {
        th.NextTag = 1000 + n.type;
        if (n.ai[0] != 3f || n.ai[1] != 0f) return;
        float life = n.life / (float)n.lifeMax;
        float inc = 1f + (life < 0.75f ? 1f : 0f) + (life < 0.5f ? 1f : 0f) + (life < 0.25f ? 1f : 0f) + (life < 0.1f ? 2f : 0f);
        Vector2 v = Vel(n);
        float speed = 6f * 4f;
        int k = Ticks(n.localAI[1], 8f, inc, strictlyAbove: true);
        int period = (int)Math.Floor(8f / inc) + 1;
        for (; k <= Threats.H; k += period)
            th.AddShot(k, n.Center + v * k, speed, -speed / 4f, 1.6f, 66f, life: 15, drift: v * 2f);
    }

    /// <summary>
    /// Prime Saw (aiStyle 33) swing: in ai[2] == 1 it rises (vx x0.95, vy -0.1 to -8) until
    /// 200 px above the head's top, then flies straight at the pilot at 22 px/tick.
    /// </summary>
    public static void PrimeSaw(NPC n, Threats th)
    {
        th.NextTag = 1000 + n.type;
        if (n.ai[2] != 1f) return;
        NPC head = Main.npc[(int)n.ai[1]];
        Vector2 pos = n.position, v = n.velocity;
        float limit = head.position.Y - 200f;
        for (int k = 1; k <= Threats.H; k++)
        {
            v.X *= 0.95f;
            v.Y = Math.Max(v.Y - 0.1f, -8f);
            pos += v;
            if (pos.Y < limit)
            {
                th.AddDash(k + 1, pos + n.Size / 2f, 22f, 0f, 0f, n.width, n.height, Threats.H, Threats.H, 1f);
                return;
            }
        }
    }

    /// <summary>
    /// Prime Vice (aiStyle 34): ai[2] == 1 rises (vy -0.5 to -9) until 280 px above the head,
    /// then strikes at 20 px/tick (five times); ai[2] == 4 drifts sideways (vx +0.5 to 12) until
    /// 500 px from the head, then strikes at 17; while the head spins it re-lunges at 12
    /// whenever |vx|+|vy| falls below 2 (decaying x0.97 per tick).
    /// </summary>
    public static void PrimeVice(NPC n, Threats th)
    {
        th.NextTag = 1000 + n.type;
        NPC head = Main.npc[(int)n.ai[1]];
        Vector2 pos = n.position, v = n.velocity;
        if (n.ai[2] == 1f)
        {
            float limit = head.position.Y - 280f;
            for (int k = 1; k <= Threats.H; k++)
            {
                if (v.Y > 0f) v.Y *= 0.9f;
                v.X = (v.X * 5f + head.velocity.X) / 6f + 0.5f;
                v.Y = Math.Max(v.Y - 0.5f, -9f);
                pos += v;
                if (pos.Y < limit)
                {
                    th.AddDash(k + 1, pos + n.Size / 2f, 20f, 0f, 0f, n.width, n.height, Threats.H, Threats.H, 1f);
                    return;
                }
            }
        }
        else if (n.ai[2] == 4f)
        {
            float hx = head.Center.X;
            for (int k = 1; k <= Threats.H; k++)
            {
                v.Y = (v.Y * 5f + head.velocity.Y) / 6f;
                v.X = Math.Min(v.X + 0.5f, 12f);
                pos += v;
                float cx = pos.X + n.width / 2f;
                if (cx < hx - 500f || cx > hx + 500f)
                {
                    th.AddDash(k + 1, pos + n.Size / 2f, 17f, 0f, 0f, n.width, n.height, Threats.H, Threats.H, 1f);
                    return;
                }
            }
        }
        else if ((n.ai[2] == 0f || n.ai[2] == 3f) && head.ai[1] != 0f)
        {
            float l1 = Math.Abs(v.X) + Math.Abs(v.Y);
            int k = l1 < 2f ? 1 : (int)Math.Ceiling(Math.Log(2f / l1) / Math.Log(0.97f)) + 1;
            if (k > Threats.H) return;
            Vector2 c = n.Center;
            for (int t = 0; t < k; t++) { c += v; v *= 0.97f; }
            th.AddDash(k, c, 12f, 0f, 0f, n.width, n.height, Threats.H, 1, 0.97f);
        }
    }

    /// <summary>Prime Cannon (aiStyle 35) ai[2] == 1: a bomb at 10 px/tick at the pilot every 40 ticks.</summary>
    public static void PrimeCannon(NPC n, Threats th)
    {
        th.NextTag = 1000 + n.type;
        if (n.ai[2] != 1f) return;
        int k = Ticks(n.localAI[0], 40f, 1f, strictlyAbove: true);
        for (; k <= Threats.H; k += 41)
            th.AddShot(k, n.Center + Vel(n) * k, 10f, 40f, 0.4f, 96f);
    }

    /// <summary>Probe (aiStyle 5, type 139): pink laser at 6 x3 px/tick every 120 ticks unless hit.</summary>
    public static void Probe(NPC n, Threats th)
    {
        th.NextTag = 1000 + n.type;
        float inc = n.ai[3] != 0f ? 3f : 1f;
        int k = Ticks(n.localAI[0], 120f, inc, strictlyAbove: false);
        th.AddShot(k, n.Center + Vel(n) * k, 18f, 0f, 0f, 8f);
    }
}
