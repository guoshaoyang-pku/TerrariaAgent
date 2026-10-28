using System;
using Microsoft.Xna.Framework;
using Terraria;

namespace BossPilot.Bosses.EvilDeer;

/// <summary>
/// Forward models of Deerclops' attacks (NPC.AI_123_Deerclops, Projectile AI_157 / AI_001 /
/// AI_187), written as hazards for the planner.
/// </summary>
public static class DeerclopsModel
{
    public const int IceSpike = 961, Rubble = 962, ShadowHand = 965;

    /// <summary>Live ice spike: thick line from its center along its direction, 200*scale long, 22*scale wide.</summary>
    public static void AddSpike(HazardField f, Projectile pr)
    {
        float scale = Math.Max(pr.scale, pr.ai[1]);
        Vector2 dir = pr.velocity.SafeNormalize(-Vector2.UnitY);
        f.Lines.Add(new LineHazard
        {
            T0 = 0, T1 = Math.Max(1, 21 - (int)pr.ai[0]),
            A = pr.Center, B = pr.Center + dir * 200f * scale, Width = 22f * scale,
        });
    }

    /// <summary>Live rubble: 32x32, gravity 0.15 once ai[0] reaches 5, terminal 16.</summary>
    public static void AddRubble(HazardField f, Projectile pr)
    {
        Track tr = f.NewTrack();
        Vector2 c = pr.Center, v = pr.velocity;
        float ai0 = pr.ai[0];
        int life = pr.timeLeft;
        for (int t = 1; t <= HazardField.Horizon && t <= life; t++)
        {
            ai0++;
            if (ai0 >= 5f) { v.Y += 0.15f; if (v.Y > 16f) v.Y = 16f; }
            c += v;
            tr.Set(t, Box.Centered(c, pr.width, pr.height));
        }
    }

    /// <summary>Live shadow hand, replaying AI_187_ShadowHand exactly (all four variations).</summary>
    public static void AddShadowHand(HazardField f, Projectile pr)
    {
        Track tr = f.NewTrack();
        Vector2 c = pr.Center, v = pr.velocity;
        float ai0 = pr.ai[0], ai1 = pr.ai[1], rot = pr.rotation;
        for (int t = 1; t <= HazardField.Horizon; t++)
        {
            Variation(ai0, out int variation, out float fake, out float max);
            if (fake >= max - 1f) break;
            float from = fake / max;
            switch (variation)
            {
                case 0:
                    v *= 0.98f;
                    break;
                case 1:
                {
                    int dir = v.X > 0f ? 1 : -1;
                    if (v.Length() > 0.1f) v *= 0.95f;
                    float r = 70f * dir;
                    Vector2 pivot = c - rot.ToRotationVector2() * r;
                    float n3 = Utils.Remap(from, 0.3f, 0.5f, 0f, 1f) * Utils.Remap(from, 0.45f, 0.5f, 1f, 0f);
                    float n4 = Utils.Remap(from, 0.5f, 0.55f, 0f, 1f) * Utils.Remap(from, 0.5f, 1f, 1f, 0f);
                    float n5 = n3 * (float)Math.PI / 60f + n4 * -(float)Math.PI * 8f / 60f;
                    rot = MathHelper.WrapAngle(rot + n5 * -dir);
                    c = pivot + rot.ToRotationVector2() * r;
                    break;
                }
                case 2:
                {
                    float n6 = Utils.Remap(from, 0f, 0.4f, 1f, 0f);
                    float n7 = Utils.Remap(from, 0.3f, 0.4f, 0f, 1f) * Utils.Remap(from, 0.4f, 1f, 1f, 0f);
                    v = ai1.ToRotationVector2() * (n6 * 2f + n7 * 8f + 0.01f);
                    break;
                }
                case 3:
                    v = v.RotatedBy(ai1);
                    break;
            }
            ai0++;
            c += v;
            tr.Set(t, Box.Centered(c, pr.width, pr.height));
        }
    }

    private static void Variation(float counter, out int variation, out float fake, out float max)
    {
        fake = counter;
        variation = 0;
        max = 180f;
        if (counter < 180f) return;
        if (counter < 300f) { variation = 1; fake = counter - 180f; max = 120f; return; }
        if (counter < 390f) { variation = 2; fake = counter - 300f; max = 90f; return; }
        if (counter < 480f) { variation = 3; fake = counter - 390f; max = 90f; return; }
        max = 1f;
    }

    /// <summary>
    /// Spikes Deerclops is about to raise in state 1 (20 forward, from 3 tiles ahead,
    /// four every 4 ticks once ai[1] reaches 36) or state 4 (15 to each side from its feet,
    /// two every 2 ticks from ai[1] 56). Heights follow AI_123_Deerclops_TryMakingSpike_FindBestY
    /// for the pilot's current feet; scale uses the largest random roll.
    /// </summary>
    public static void AddPendingSpikes(HazardField f, NPC d, Player target, ArenaGrid g)
    {
        int state = (int)d.ai[0];
        if (state != 1 && state != 4) return;
        float ai1 = d.ai[1];
        Point src = d.Bottom.ToTileCoordinates();
        int targetRow = (int)((target.position.Y + target.height) / 16f);
        if (state == 1)
        {
            int dir = d.direction;
            for (int i = 0; i < 20; i++)
            {
                int spawn = 36 + 4 * (i / 4) - (int)ai1;
                if (spawn + 20 < 1) continue;
                if (spawn > HazardField.Horizon) break;
                int x = src.X + dir * 3 + i * dir;
                Spike(f, g, x, src.Y, targetRow, i * dir * 0.7f * ((float)Math.PI / 4f / 20f), 0.2f + i * 1.1f / 20f, spawn);
            }
        }
        else
        {
            for (int i = 0; i < 15; i++)
            {
                int spawn = 56 + 2 * (i / 2) - (int)ai1;
                if (spawn + 20 < 1) continue;
                if (spawn > HazardField.Horizon) break;
                foreach (int dir in (int[])[d.direction, -d.direction])
                {
                    int x = src.X + i * dir;
                    Spike(f, g, x, src.Y, targetRow, -i * dir * 0.7f * ((float)Math.PI / 4f / 15f), 0.2f + i * 1.1f / 15f, spawn);
                }
            }
        }
    }

    private static void Spike(HazardField f, ArenaGrid g, int x, int fromRow, int targetRow, float angle, float scale, int spawn)
    {
        int y = BestRow(g, x, fromRow, targetRow);
        if (!g.Standable(x, y)) return;
        Vector2 a = new(x * 16 + 8, y * 16 - 8);
        Vector2 dir = new Vector2(0f, -1f).RotatedBy(angle);
        f.Lines.Add(new LineHazard { T0 = Math.Max(1, spawn), T1 = spawn + 20, A = a, B = a + dir * 200f * scale, Width = 22f * scale });
    }

    private static int BestRow(ArenaGrid g, int x, int fromRow, int targetRow)
    {
        int num = fromRow;
        int step = Math.Sign(targetRow - fromRow);
        if (step != 0)
        {
            int end = targetRow + step * 15;
            int? best = null;
            float bestD = float.PositiveInfinity;
            for (int i = fromRow; i != end; i += step)
            {
                if (!g.Standable(x, i)) continue;
                float dd = Math.Abs(i - targetRow);
                if (!best.HasValue || dd < bestD) { best = i; bestD = dd; }
            }
            if (best.HasValue) num = best.Value;
        }
        for (int j = 0; j < 20 && g.Solid(x, num); j++) num--;
        for (int k = 0; k < 20 && !g.Standable(x, num); k++) num++;
        return num;
    }

    /// <summary>Rubble Deerclops will throw in state 2 (one per tick from ai[1] 32), at speeds 8, 12 and 16.</summary>
    public static void AddPendingRubble(HazardField f, NPC d, ArenaGrid g)
    {
        if ((int)d.ai[0] != 2) return;
        float ai1 = d.ai[1];
        Point src = d.Top.ToTileCoordinates();
        src.X += d.direction * 3;
        src.Y -= 10;
        for (int j = 0; j < 20; j++)
        {
            int spawn = 32 + j - (int)ai1;
            if (spawn < 1) continue;
            if (spawn > HazardField.Horizon) break;
            int x = src.X + j * d.direction;
            int y = -1;
            for (int i = 0; i < 35; i++)
                if (g.Solid(x, src.Y + i)) { y = src.Y + i; break; }
            if (y < 0) continue;
            Vector2 dir = new Vector2(0f, -1f).RotatedBy(j * d.direction * 0.7f * ((float)Math.PI / 4f / 20f));
            foreach (float speed in (float[])[8f, 12f, 16f])
            {
                Track tr = f.NewTrack();
                Vector2 c = new(x * 16 + 8, y * 16 - 8), v = dir * speed;
                float ai0 = 0f;
                for (int t = spawn; t <= HazardField.Horizon; t++)
                {
                    ai0++;
                    if (ai0 >= 5f) { v.Y += 0.15f; if (v.Y > 16f) v.Y = 16f; }
                    c += v;
                    tr.Set(t, Box.Centered(c, 32, 32));
                }
            }
        }
    }

    /// <summary>
    /// Body: walks toward the pilot (lerp 1/4 to 3.5-4.5 px/tick), halts within 80 px or while
    /// attacking. Contact uses only the top 74 px of the hitbox (GetMeleeCollisionData shrinks it by
    /// 80), plus a 64x180 claw box 42-106 px in front while animation frame 15 shows: state 1 around
    /// ai[1] 36-39 and state 2 around ai[1] 4-7 (frames advance every 4 ticks).
    /// </summary>
    public static void AddBody(HazardField f, NPC d, Player target)
    {
        Track body = f.NewTrack();
        Track claw = f.NewTrack();
        float speed = 3.5f + 1f * (1f - (float)d.life / d.lifeMax);
        Vector2 pos = d.position;
        float vx = d.velocity.X;
        int state = (int)d.ai[0];
        float ai1 = d.ai[1];
        bool halted = state is 1 or 2 or 3 or 4 or 5 or 7 or 8;
        for (int t = 1; t <= HazardField.Horizon; t++)
        {
            float dx = target.Center.X - (pos.X + d.width / 2f);
            if (halted || Math.Abs(dx) < 80f)
            {
                vx *= 0.9f;
                if (vx > -0.1f && vx < 0.1f) vx = 0f;
            }
            else vx = MathHelper.Lerp(vx, Math.Sign(dx) * speed, 0.25f);
            pos.X += vx;
            body.Set(t, new Box(pos.X, pos.Y, d.width, d.height - 80));
            float k = ai1 + t;
            bool frame15 = (state == 1 && k >= 34f && k <= 42f) || (state == 2 && k >= 2f && k <= 10f);
            if (frame15)
            {
                float cx = pos.X + d.width / 2f, cy = pos.Y + d.height / 2f;
                float x0 = d.direction > 0 ? cx + 42f : cx - 106f;
                claw.Set(t, new Box(x0, cy - 100f, 64f, 180f));
            }
        }
    }
}
