using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace BossPilot.Bosses.SlimesEye;

/// <summary>
/// Predicted hurt boxes of every hostile non-boss entity over the planning horizon, sampled
/// every tick (fast spikes would tunnel through coarser samples). Each entity is forward-simulated with a model transcribed
/// from its AI (slime hops, homing flyers, ballistic projectiles, Queen Slime's smash); the
/// player is assumed to stay where it is, which only matters for homing and jump direction.
/// Boxes grow slowly with lead time to absorb model error.
/// </summary>
public sealed class ThreatSet
{
    public const int Horizon = 90, Step = 1, Samples = Horizon / Step + 1;

    public readonly List<Box[]> Tracks = [];
    public readonly List<int> Sources = [];   // NPC type (positive) or -projectile type, for logs
    private readonly Stack<Box[]> pool = new();

    /// <summary>NPC types the boss plan simulates itself (excluded here).</summary>
    public HashSet<int> SkipNpcTypes = [];

    public void Clear()
    {
        foreach (Box[] t in Tracks) pool.Push(t);
        Tracks.Clear();
        Sources.Clear();
    }

    private Box[] Rent(int source)
    {
        Box[] a = pool.Count > 0 ? pool.Pop() : new Box[Samples];
        Tracks.Add(a);
        Sources.Add(source);
        return a;
    }

    /// <summary>Smallest gap between the player box and any threat at sample k.</summary>
    public float MinGap(int k, in Box player)
    {
        float best = float.MaxValue;
        for (int i = 0; i < Tracks.Count; i++)
        {
            Box b = Tracks[i][k];
            if (!b.Valid) continue;
            float g = player.Gap(b);
            if (g < best) best = g;
        }
        return best;
    }

    public void Build(Player p, float range = 1400f)
    {
        Clear();
        Vector2 pc = p.Center;
        foreach (NPC n in Main.npc)
        {
            if (!n.active || n.friendly || n.damage <= 0 || n.dontTakeDamage && n.hide) continue;
            if (SkipNpcTypes.Contains(n.type) || n.townNPC) continue;
            if (Vector2.DistanceSquared(n.Center, pc) > range * range) continue;
            PredictNpc(n, pc, Rent(n.type));
        }
        foreach (Projectile pr in Main.projectile)
        {
            if (!pr.active || !pr.hostile || pr.damage <= 0) continue;
            if (Vector2.DistanceSquared(pr.Center, pc) > range * range) continue;
            PredictProjectile(pr, Rent(-pr.type));
        }
    }

    // ---------------------------------------------------------------- NPCs

    private static void PredictNpc(NPC n, Vector2 player, Box[] track)
    {
        switch (n.aiStyle)
        {
            case NPCAIStyleID.Slime:
                PredictSlime(n, player, track);
                return;
            case NPCAIStyleID.DemonEye:
            case NPCAIStyleID.Flying:
            case NPCAIStyleID.Bat:
                (float speed, float acc) = n.type switch
                {
                    NPCID.ServantofCthulhu => (5f, 0.03f),
                    NPCID.QueenSlimeMinionPurple => (6f, 0.3f),
                    _ => (5f, 0.08f),
                };
                PredictHoming(n, player, speed, acc, n.aiStyle == NPCAIStyleID.Bat ? 3f : 1.5f, track);
                return;
            default:
                PredictLinear(n, track);
                return;
        }
    }

    private static float Grow(int t) => 2f + 0.04f * t;

    /// <summary>AI_001_Slimes ground timer and hop impulses; lands on floors and platforms.</summary>
    private static void PredictSlime(NPC n, Vector2 player, Box[] track)
    {
        Vector2 c = n.Center, v = n.velocity;
        float hw = n.width * 0.5f, hh = n.height * 0.5f;
        float ai0 = n.ai[0];
        int dir = n.direction == 0 ? 1 : n.direction;
        bool ground = v.Y == 0f;
        bool flag = !Main.dayTime || n.life != n.lifeMax || n.position.Y > Main.worldSurface * 16.0;
        float inc = 1f + (flag ? 1f : 0f) + n.type switch { NPCID.QueenSlimeMinionBlue => 5f, NPCID.QueenSlimeMinionPink => 3f, _ => 0f };
        float lim = n.type == NPCID.QueenSlimeMinionPink ? -500f : -1000f;
        // Crystal slimes in sight stand and shoot instead of hopping (ai[0] pinned at -40).
        bool shooter = n.type == NPCID.QueenSlimeMinionBlue || n.type == NPCID.QueenSlimeMinionPink;
        float floor = Tiles.FloorBelow(c.X, c.Y + hh - 2f, platforms: true);
        track[0] = new Box(c.X, c.Y, hw + 2f, hh + 2f);
        for (int t = 1; t <= Horizon; t++)
        {
            if (ground)
            {
                v.X *= 0.8f;
                if (Math.Abs(v.X) < 0.1f) v.X = 0f;
                ai0 += inc;
                int jump = ai0 >= 0f ? 1 : ai0 >= lim && ai0 <= lim * 0.5f ? 2 : ai0 >= lim * 2f && ai0 <= lim * 1.5f ? 3 : 0;
                if (shooter && Math.Abs(player.X - c.X) < 500f && t < 4) jump = 0;
                if (jump > 0)
                {
                    dir = player.X < c.X ? -1 : 1;
                    if (jump == 3) { v.Y = -8f; v.X += 3 * dir; ai0 = -200f; }
                    else { v.Y = -6f; v.X += 2 * dir; ai0 = -120f + (jump == 1 ? lim : lim * 2f); }
                    if (n.type == NPCID.QueenSlimeMinionPink) { v.Y *= 1.6f; v.X *= 1.2f; }
                    ground = false;
                }
            }
            else if ((dir == 1 && v.X < 3f) || (dir == -1 && v.X > -3f))
            {
                if ((dir == -1 && v.X < 0.01f) || (dir == 1 && v.X > -0.01f)) v.X += 0.2f * dir;
                else v.X *= 0.93f;
            }
            if (!ground)
            {
                v.Y = Math.Min(v.Y + 0.3f, 10f);
                c += v;
                if (v.Y > 0f && c.Y + hh >= floor)
                {
                    c.Y = floor - hh;
                    v.Y = 0f;
                    ground = true;
                }
                else if (v.Y > 0f && float.IsInfinity(floor))
                {
                    floor = Tiles.FloorBelow(c.X, c.Y + hh - 2f, platforms: true);
                }
            }
            if (t % Step == 0)
            {
                if (!ground) floor = Tiles.FloorBelow(c.X, c.Y + hh - 2f, platforms: true);
                // Our own shots knock slimes around, so their tracks get twice the usual slack.
                float g = 2f * Grow(t);
                track[t / Step] = new Box(c.X, c.Y, hw + g, hh + g);
            }
        }
    }

    /// <summary>
    /// Per-axis accelerate-toward-target rule shared by AI_005 flyers, the Eye's hover and
    /// Heavenly Slimes (with their own speed/acceleration).
    /// </summary>
    public static void HomingStep(ref Vector2 v, Vector2 from, Vector2 to, float speed, float acc)
    {
        Vector2 d = to - from;
        float len = d.Length();
        if (len < 0.01f) return;
        d *= speed / len;
        if (v.X < d.X) { v.X += acc; if (v.X < 0f && d.X > 0f) v.X += acc; }
        else if (v.X > d.X) { v.X -= acc; if (v.X > 0f && d.X < 0f) v.X -= acc; }
        if (v.Y < d.Y) { v.Y += acc; if (v.Y < 0f && d.Y > 0f) v.Y += acc; }
        else if (v.Y > d.Y) { v.Y -= acc; if (v.Y > 0f && d.Y < 0f) v.Y -= acc; }
    }

    private static void PredictHoming(NPC n, Vector2 player, float speed, float acc, float spread, Box[] track)
    {
        Vector2 c = n.Center, v = n.velocity;
        float hw = n.width * 0.5f, hh = n.height * 0.5f;
        track[0] = new Box(c.X, c.Y, hw + 2f, hh + 2f);
        for (int t = 1; t <= Horizon; t++)
        {
            HomingStep(ref v, c, player, speed, acc);
            c += v;
            if (t % Step == 0)
            {
                float g = Grow(t) * spread;
                track[t / Step] = new Box(c.X, c.Y, hw + g, hh + g);
            }
        }
    }

    private static void PredictLinear(NPC n, Box[] track)
    {
        Vector2 c = n.Center, v = n.velocity;
        float hw = n.width * 0.5f, hh = n.height * 0.5f;
        bool grav = !n.noGravity;
        float floor = grav ? Tiles.FloorBelow(c.X, c.Y + hh - 2f, platforms: true) : float.PositiveInfinity;
        track[0] = new Box(c.X, c.Y, hw + 2f, hh + 2f);
        for (int t = 1; t <= Horizon; t++)
        {
            if (grav && c.Y + hh < floor) v.Y = Math.Min(v.Y + 0.3f, 10f);
            c += v;
            if (grav && c.Y + hh > floor) { c.Y = floor - hh; v.Y = 0f; }
            if (t % Step == 0)
            {
                float g = Grow(t) * 2f;
                track[t / Step] = new Box(c.X, c.Y, hw + g, hh + g);
            }
        }
    }

    // ---------------------------------------------------------------- projectiles

    private static void PredictProjectile(Projectile pr, Box[] track)
    {
        float hw = pr.width * 0.5f, hh = pr.height * 0.5f;
        if (pr.type == ProjectileID.QueenSlimeSmash)
        {
            // AI_135_OgreStomp: fixed center, square grows from 5 to 30 tiles over ai[0] = 0..9, then dies.
            for (int k = 0; k < Samples; k++)
            {
                float a = pr.ai[0] + k * Step;
                float half = a > 9.5f ? -1f : 8f * MathHelper.Lerp(5f, 30f, MathHelper.Clamp(a / 9f, 0f, 1f));
                track[k] = half < 0f ? Box.None : new Box(pr.Center.X, pr.Center.Y, half, half);
            }
            return;
        }

        Vector2 c = pr.Center, v = pr.velocity;
        float ai0 = pr.ai[0];
        int updates = 1 + pr.extraUpdates;
        int bounces = pr.type is ProjectileID.QueenSlimeGelAttack or ProjectileID.QueenSlimeMinionPinkBall ? Math.Max(0, pr.penetrate - 1) : 0;
        bool gelGravity = pr.aiStyle == ProjAIStyleID.Arrow && pr.type is ProjectileID.QueenSlimeGelAttack or ProjectileID.QueenSlimeMinionBlueSpike
            or ProjectileID.QueenSlimeMinionPinkBall or ProjectileID.SpikedSlimeSpike or ProjectileID.IceSpike;
        bool arrowGravity = pr.aiStyle == ProjAIStyleID.Arrow && !gelGravity;
        bool alive = true;
        int life = pr.timeLeft;
        track[0] = new Box(c.X, c.Y, hw + 2f, hh + 2f);
        for (int t = 1; t <= Horizon; t++)
        {
            for (int u = 0; u < updates && alive; u++)
            {
                ai0++;
                if (gelGravity && ai0 >= 5f) v.Y = Math.Min(v.Y + 0.15f, 16f);
                else if (arrowGravity && ai0 >= 15f) v.Y = Math.Min(v.Y + 0.1f, 16f);
                Vector2 next = c + v;
                if (pr.tileCollide && Tiles.Solid((int)(next.X / 16f), (int)((next.Y + (v.Y > 0 ? hh : -hh)) / 16f)))
                {
                    if (bounces > 0 && v.Y > 1f)
                    {
                        v.Y *= -0.95f;
                        bounces--;
                        next = c + v;
                    }
                    else
                    {
                        alive = false;
                        break;
                    }
                }
                c = next;
                if (--life <= 0) alive = false;
            }
            if (t % Step == 0)
            {
                float g = 2f + 0.03f * t;
                track[t / Step] = alive ? new Box(c.X, c.Y, hw + g, hh + g) : Box.None;
            }
        }
    }
}
