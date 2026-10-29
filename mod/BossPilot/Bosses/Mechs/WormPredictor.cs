using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace BossPilot.Bosses.Mechs;

/// <summary>
/// Predicts The Destroyer (NPC.AI_037_Destroyer) over the planner horizon. The head
/// steers toward the player (speed 16, per-axis accel 0.1/0.15) only while its hitbox
/// overlaps solid tiles, platform tops or liquid - or while it is below the player and
/// outside the player's ±1000 px box; otherwise it falls (vy += 0.15, cap 16). Body
/// segments keep 44*scale px behind their leader, so the chain slides along the head's
/// trail: segment j at tick t sits at arc length D(t) - s_j on [future head path ∪ body].
/// </summary>
public sealed class WormPredictor
{
    private const int H = Threats.H;
    private readonly Vector2[] fwd = new Vector2[H + 1];
    private readonly float[] fwdArc = new float[H + 1];
    private Vector2[] back = new Vector2[128];
    private float[] backArc = new float[128];
    private int[] backNpc = new int[128];
    private int backCount;

    /// <summary>Head of the worm whose chain was last built, or -1.</summary>
    public int Head { get; private set; } = -1;
    public int Segments => backCount - 1;
    public Vector2 HeadVelocity { get; private set; }
    /// <summary>Predicted head center at tick t.</summary>
    public Vector2 HeadAt(int t) => fwd[Math.Clamp(t, 0, H)];
    public bool HeadHoming { get; private set; }

    public bool Build(Player p, int headType = NPCID.TheDestroyer)
    {
        Head = -1;
        foreach (NPC n in Main.npc)
            if (n.active && n.type == headType) { Head = n.whoAmI; break; }
        if (Head < 0) return false;
        NPC head = Main.npc[Head];

        // Chain: head.ai[0] -> follower ... -> tail.
        backCount = 0;
        Push(head.Center, head.whoAmI);
        int idx = (int)head.ai[0], guard = 0;
        while (idx > 0 && idx < Main.maxNPCs && guard++ < 120)
        {
            NPC s = Main.npc[idx];
            if (!s.active || s.type < NPCID.TheDestroyer || s.type > NPCID.TheDestroyerTail) break;
            Push(s.Center, idx);
            if (s.type == NPCID.TheDestroyerTail) break;
            idx = (int)s.ai[0];
        }

        // Head path.
        Vector2 c = head.Center, v = head.position - head.oldPosition;
        if (v.LengthSquared() < 1e-4f) v = head.velocity;
        HeadVelocity = v;
        HeadHoming = head.localAI[0] == 1f;
        Vector2 target = p.Center;
        fwd[0] = c; fwdArc[0] = 0f;
        for (int t = 1; t <= H; t++)
        {
            bool homing = InTiles(c, head.width, head.height) || (c.Y > target.Y && !NearPlayerBox(c, head.width, head.height, target));
            if (homing) v = HomingStep(c, v, target);
            else v = FallStep(c, v, target);
            c += v;
            fwd[t] = c;
            fwdArc[t] = fwdArc[t - 1] + v.Length();
        }
        return true;
    }

    /// <summary>Adds every segment (head included) to the threat set; returns how many.</summary>
    public int AddThreats(Threats th, Player p, float range, float margin = 4f, float grow = 0.25f)
    {
        if (Head < 0) return 0;
        int added = 0;
        float r2 = range * range;
        for (int j = 0; j < backCount; j++)
        {
            NPC n = Main.npc[backNpc[j]];
            if (Vector2.DistanceSquared(back[j], p.Center) > r2) continue;
            th.NextTag = n.type;
            int k = th.Open(n.width * 0.5f, n.height * 0.5f, 1f, margin, grow + (j == 0 ? 0.35f : 0f));
            if (k < 0) break;
            int b = k * (H + 1);
            for (int t = 0; t <= H; t++)
            {
                Vector2 q = PointAt(fwdArc[t] - backArc[j]);
                th.X[b + t] = q.X; th.Y[b + t] = q.Y;
            }
            th.Close(k, H);
            added++;
        }
        return added;
    }

    /// <summary>Predicted center of chain element j (0 = head) at tick t.</summary>
    public Vector2 SegmentAt(int j, int t) => PointAt(fwdArc[Math.Clamp(t, 0, H)] - backArc[Math.Clamp(j, 0, backCount - 1)]);

    public int SegmentNpc(int j) => backNpc[j];

    private Vector2 PointAt(float u)
    {
        if (u >= 0f)
        {
            for (int t = 1; t <= H; t++)
                if (fwdArc[t] >= u)
                {
                    float seg = fwdArc[t] - fwdArc[t - 1];
                    float a = seg > 1e-3f ? (u - fwdArc[t - 1]) / seg : 1f;
                    return Vector2.Lerp(fwd[t - 1], fwd[t], a);
                }
            return fwd[H];
        }
        u = -u;
        for (int j = 1; j < backCount; j++)
            if (backArc[j] >= u)
            {
                float seg = backArc[j] - backArc[j - 1];
                float a = seg > 1e-3f ? (u - backArc[j - 1]) / seg : 1f;
                return Vector2.Lerp(back[j - 1], back[j], a);
            }
        return back[backCount - 1];
    }

    private void Push(Vector2 c, int npc)
    {
        if (backCount == back.Length)
        {
            Array.Resize(ref back, back.Length * 2);
            Array.Resize(ref backArc, back.Length);
            Array.Resize(ref backNpc, back.Length);
        }
        back[backCount] = c;
        backNpc[backCount] = npc;
        backArc[backCount] = backCount == 0 ? 0f : backArc[backCount - 1] + Vector2.Distance(back[backCount - 1], c);
        backCount++;
    }

    private static bool NearPlayerBox(Vector2 c, int w, int h, Vector2 player)
    {
        // Head rect vs. (player.position ± 1000) rect, as in AI_037.
        float px = player.X - 10f, py = player.Y - 21f;
        return c.X + w * 0.5f > px - 1000f && c.X - w * 0.5f < px + 1000f
            && c.Y + h * 0.5f > py - 1000f && c.Y - h * 0.5f < py + 1000f;
    }

    private static bool InTiles(Vector2 c, int w, int h)
    {
        int x0 = (int)((c.X - w * 0.5f) / 16f) - 1, x1 = (int)((c.X + w * 0.5f) / 16f) + 2;
        int y0 = (int)((c.Y - h * 0.5f) / 16f) - 1, y1 = (int)((c.Y + h * 0.5f) / 16f) + 2;
        for (int i = x0; i < x1; i++)
        for (int j = y0; j < y1; j++)
        {
            if (!WorldGen.InWorld(i, j, 1)) continue;
            Tile t = Main.tile[i, j];
            bool solid = t.HasTile && !t.IsActuated && (Main.tileSolid[t.TileType] || (Main.tileSolidTop[t.TileType] && t.TileFrameY == 0));
            if (!solid && t.LiquidAmount <= 64) continue;
            if (c.X + w * 0.5f > i * 16 && c.X - w * 0.5f < i * 16 + 16 && c.Y + h * 0.5f > j * 16 && c.Y - h * 0.5f < j * 16 + 16)
                return true;
        }
        return false;
    }

    private static Vector2 FallStep(Vector2 c, Vector2 v, Vector2 target)
    {
        const float max = 16f, a = 0.1f;
        v.Y += 0.15f;
        if (v.Y > max) v.Y = max;
        if (Math.Abs(v.X) + Math.Abs(v.Y) < max * 0.4f) v.X += v.X < 0f ? -a * 1.1f : a * 1.1f;
        else if (v.Y == max) v.X += v.X < target.X - c.X ? a : -a;
        else if (v.Y > 4f) v.X += v.X < 0f ? a * 0.9f : -a * 0.9f;
        return v;
    }

    /// <summary>The burrowing branch of AI_037 (head, not attached), with the same per-axis rules.</summary>
    private static Vector2 HomingStep(Vector2 c, Vector2 v, Vector2 target)
    {
        const float max = 16f, a = 0.1f, a2 = 0.15f;
        float dx = (int)(target.X / 16f) * 16 - (int)(c.X / 16f) * 16;
        float dy = (int)(target.Y / 16f) * 16 - (int)(c.Y / 16f) * 16;
        float len = (float)Math.Sqrt(dx * dx + dy * dy);
        if (len < 1f) return v;
        float ax = Math.Abs(dx), ay = Math.Abs(dy);
        float k = max / len;
        dx *= k; dy *= k;
        bool sameX = (v.X > 0f && dx > 0f) || (v.X < 0f && dx < 0f);
        bool sameY = (v.Y > 0f && dy > 0f) || (v.Y < 0f && dy < 0f);
        if (sameX && sameY)
        {
            v.X += v.X < dx ? a2 : v.X > dx ? -a2 : 0f;
            v.Y += v.Y < dy ? a2 : v.Y > dy ? -a2 : 0f;
        }
        if (sameX || sameY)
        {
            v.X += v.X < dx ? a : v.X > dx ? -a : 0f;
            v.Y += v.Y < dy ? a : v.Y > dy ? -a : 0f;
            if (Math.Abs(dy) < max * 0.2f && ((v.X > 0f && dx < 0f) || (v.X < 0f && dx > 0f))) v.Y += v.Y > 0f ? a * 2f : -a * 2f;
            if (Math.Abs(dx) < max * 0.2f && ((v.Y > 0f && dy < 0f) || (v.Y < 0f && dy > 0f))) v.X += v.X > 0f ? a * 2f : -a * 2f;
        }
        else if (ax > ay)
        {
            v.X += v.X < dx ? a * 1.1f : v.X > dx ? -a * 1.1f : 0f;
            if (Math.Abs(v.X) + Math.Abs(v.Y) < max * 0.5f) v.Y += v.Y > 0f ? a : -a;
        }
        else
        {
            v.Y += v.Y < dy ? a * 1.1f : v.Y > dy ? -a * 1.1f : 0f;
            if (Math.Abs(v.X) + Math.Abs(v.Y) < max * 0.5f) v.X += v.X > 0f ? a : -a;
        }
        return v;
    }
}
