using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace BossPilot.Bosses.Mechs;

/// <summary>
/// Predicted hitboxes of everything that can damage the pilot, sampled for ticks
/// 0..H. Built once per tick and shared by all candidate plans. Positions are box
/// centers in world pixels.
/// </summary>
public sealed class Threats
{
    public const int H = 40;
    public const int Max = 192;
    private const int S = H + 1;

    public readonly float[] X = new float[Max * S], Y = new float[Max * S];
    public readonly float[] HW = new float[Max], HH = new float[Max], Weight = new float[Max];
    public readonly float[] Margin = new float[Max], Grow = new float[Max];
    public readonly float[] MinX = new float[Max], MaxX = new float[Max], MinY = new float[Max], MaxY = new float[Max];
    public readonly int[] Life = new int[Max];
    public int Count;

    // Last displacement per NPC slot, for a one-tick acceleration estimate.
    private readonly Vector2[] lastDisp = new Vector2[Main.maxNPCs];
    private readonly uint[] lastSeen = new uint[Main.maxNPCs];

    /// <summary>
    /// A shot or dash an NPC is scheduled to start Tf ticks from now (read from its AI
    /// timers), aimed at wherever the pilot will be then: plan-dependent, so it is evaluated
    /// per plan. Src is the muzzle at fire time; Offset is how far along the aim the
    /// projectile spawns; Speed is px/tick including extra updates, held for ConstTicks and
    /// then multiplied by Decay each tick (dashes); Noise is the per-axis velocity jitter on
    /// the same scale (base jitter times 1 + extraUpdates).
    /// </summary>
    public struct Shot
    {
        public int Tf, Life, ConstTicks;
        public Vector2 Src;
        public float Speed, Offset, Noise, Decay, HW, HH, Weight;
    }

    public readonly Shot[] Shots = new Shot[48];
    public int ShotCount;

    public void Clear() { Count = 0; ShotCount = 0; }

    public void AddShot(int tf, Vector2 src, float speed, float offset, float noise, float size, int life = H, float weight = 1f)
        => AddDash(tf, src, speed, offset, noise, size, size, life, H, 1f, weight);

    public void AddDash(int tf, Vector2 src, float speed, float offset, float noise, float w, float h, int life, int constTicks, float decay, float weight = 1f)
    {
        if (ShotCount >= Shots.Length || tf < 1 || tf > H) return;
        Shots[ShotCount++] = new Shot
        {
            Tf = tf, Life = life, Src = src, Speed = speed, Offset = offset, Noise = noise,
            HW = w * 0.5f, HH = h * 0.5f, Weight = weight, ConstTicks = constTicks, Decay = decay,
        };
    }

    /// <summary>
    /// First ticks the scheduled shot meets the pilot on this plan: core = on the exact aim
    /// line, margin = within the noise cone. -1 if never.
    /// </summary>
    public void ShotHit(in Shot sh, PState[] tr, float hw, float hh, out int core, out int margin)
    {
        core = margin = -1;
        Vector2 aim = new(tr[sh.Tf].X, tr[sh.Tf].Y);
        Vector2 dir = aim - sh.Src;
        float len = dir.Length();
        if (len < 1f) { core = margin = sh.Tf; return; }
        dir /= len;
        Vector2 cur = sh.Src + dir * sh.Offset;
        int end = Math.Min(H, sh.Tf + sh.Life);
        float spread = sh.Noise / Math.Max(sh.Speed, 1f) * 0.5f;
        float speed = sh.Speed, dist = sh.Offset;
        for (int t = sh.Tf + 1; t <= end; t++)
        {
            Vector2 prev = cur;
            cur += dir * speed;
            dist += speed;
            if (t - sh.Tf >= sh.ConstTicks) speed *= sh.Decay;
            float rx = prev.X - tr[t - 1].X, ry = prev.Y - tr[t - 1].Y;
            float dx = cur.X - tr[t].X - rx, dy = cur.Y - tr[t].Y - ry;
            if (margin < 0)
            {
                float m = 2f + spread * dist;
                if (SegmentHitsBox(rx, ry, dx, dy, sh.HW + hw + m, sh.HH + hh + m)) margin = t;
            }
            if (margin > 0 && SegmentHitsBox(rx, ry, dx, dy, sh.HW + hw, sh.HH + hh)) { core = t; return; }
        }
    }

    /// <summary>Starts a threat; fill X/Y for t = 0..life then call Close.</summary>
    public int Open(float hw, float hh, float weight, float margin, float grow)
    {
        if (Count >= Max) return -1;
        int k = Count++;
        HW[k] = hw; HH[k] = hh; Weight[k] = weight; Margin[k] = margin; Grow[k] = grow;
        return k;
    }

    public void Close(int k, int life)
    {
        life = Math.Clamp(life, 0, H);
        Life[k] = life;
        float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
        int b = k * S;
        for (int t = 0; t <= life; t++)
        {
            x0 = Math.Min(x0, X[b + t]); x1 = Math.Max(x1, X[b + t]);
            y0 = Math.Min(y0, Y[b + t]); y1 = Math.Max(y1, Y[b + t]);
        }
        float m = Margin[k] + Grow[k] * life;
        MinX[k] = x0 - HW[k] - m; MaxX[k] = x1 + HW[k] + m;
        MinY[k] = y0 - HH[k] - m; MaxY[k] = y1 + HH[k] + m;
    }

    /// <summary>Constant-acceleration path, with |v| capped at vmax (vmax 0 = no cap).</summary>
    public void AddBallistic(Vector2 c, Vector2 v, Vector2 a, float vmax, float hw, float hh, float weight, float margin, float grow, int life)
    {
        int k = Open(hw, hh, weight, margin, grow);
        if (k < 0) return;
        int b = k * S;
        life = Math.Min(life, H);
        for (int t = 0; t <= life; t++)
        {
            X[b + t] = c.X; Y[b + t] = c.Y;
            v += a;
            if (vmax > 0f && v.LengthSquared() > vmax * vmax) v = Vector2.Normalize(v) * vmax;
            c += v;
        }
        Close(k, life);
    }

    /// <summary>Displacement of an NPC over its last update (works for segments moved by position).</summary>
    public Vector2 Displacement(NPC n) => n.position - n.oldPosition;

    /// <summary>Acceleration estimate from the previous tick's displacement; zero on the first sighting.</summary>
    public Vector2 Accel(NPC n, float cap)
    {
        Vector2 d = Displacement(n);
        Vector2 a = lastSeen[n.whoAmI] + 1 == Main.GameUpdateCount ? d - lastDisp[n.whoAmI] : Vector2.Zero;
        lastDisp[n.whoAmI] = d;
        lastSeen[n.whoAmI] = Main.GameUpdateCount;
        if (a.LengthSquared() > cap * cap) a = Vector2.Normalize(a) * cap;
        return a;
    }

    /// <summary>Default NPC predictor: last displacement plus a decaying acceleration estimate.</summary>
    public void AddNpc(NPC n, float weight = 1f, float margin = 4f, float grow = 0.6f, float accCap = 0.4f)
    {
        Vector2 v = Displacement(n), a = Accel(n, accCap);
        int k = Open(n.width * 0.5f, n.height * 0.5f, weight, margin, grow);
        if (k < 0) return;
        int b = k * S;
        Vector2 c = n.Center;
        for (int t = 0; t <= H; t++)
        {
            X[b + t] = c.X; Y[b + t] = c.Y;
            if (t < 20) v += a;
            c += v;
        }
        Close(k, H);
    }

    /// <summary>Hostile projectile: straight line (with extra updates), gravity for bombs.</summary>
    public void AddProjectile(Projectile pr)
    {
        int eu = 1 + pr.extraUpdates;
        Vector2 v = pr.velocity * eu;
        Vector2 a = Vector2.Zero;
        float hw = pr.width * 0.5f, hh = pr.height * 0.5f, vmax = 0f;
        int life = pr.timeLeft / eu;
        switch (pr.type)
        {
            case ProjectileID.EyeFire: hw += 30; hh += 30; life = Math.Min(life, 60 / eu); break; // eye fire hitbox is inflated by 30
            case ProjectileID.BombSkeletronPrime: hw = hh = 64; a = new Vector2(0, 0.2f); vmax = 10f; break;     // Prime bomb: 128x128 blast on landing
        }
        AddBallistic(pr.Center, v, a, vmax, hw, hh, 1f, 2f, 0.15f, life);
    }

    /// <summary>
    /// First ticks in 1..H at which the pilot box, sweeping from tr[t-step] to tr[t], meets
    /// threat k: core = predicted boxes touch, margin = within the uncertainty margin; -1 if
    /// never. Swept against the relative motion so fast lasers cannot tunnel.
    /// </summary>
    public void FirstHit(int k, PState[] tr, float hw, float hh, out int core, out int margin)
    {
        core = margin = -1;
        int b = k * (H + 1);
        int life = Life[k];
        int prev = 0;
        for (int t = 1; t <= life; t += t < 16 ? 1 : 2)
        {
            float rx = X[b + prev] - tr[prev].X, ry = Y[b + prev] - tr[prev].Y;
            float dx = X[b + t] - tr[t].X - rx, dy = Y[b + t] - tr[t].Y - ry;
            if (margin < 0)
            {
                float m = Margin[k] + Grow[k] * t;
                if (SegmentHitsBox(rx, ry, dx, dy, HW[k] + hw + m, HH[k] + hh + m)) margin = t;
            }
            if (margin > 0 && SegmentHitsBox(rx, ry, dx, dy, HW[k] + hw, HH[k] + hh)) { core = t; return; }
            prev = t;
        }
    }

    /// <summary>Box clearance (px, negative = overlap) between pilot and threat at tick t.</summary>
    public float Clearance(int k, int t, in PState s, float hw, float hh)
    {
        int b = k * (H + 1);
        float cx = Math.Abs(X[b + t] - s.X) - HW[k] - hw;
        float cy = Math.Abs(Y[b + t] - s.Y) - HH[k] - hh;
        return Math.Max(cx, cy);
    }

    private static bool SegmentHitsBox(float rx, float ry, float dx, float dy, float ex, float ey)
    {
        float t0 = 0f, t1 = 1f;
        if (!Slab(rx, dx, ex, ref t0, ref t1)) return false;
        return Slab(ry, dy, ey, ref t0, ref t1);
    }

    private static bool Slab(float r, float d, float e, ref float t0, ref float t1)
    {
        if (Math.Abs(d) < 1e-4f) return r > -e && r < e;
        float a = (-e - r) / d, c = (e - r) / d;
        if (a > c) (a, c) = (c, a);
        if (a > t0) t0 = a;
        if (c < t1) t1 = c;
        return t0 <= t1;
    }
}
