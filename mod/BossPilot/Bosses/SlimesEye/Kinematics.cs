using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace BossPilot.Bosses.SlimesEye;

/// <summary>Tile queries for the rollout models (slopes and half bricks count as full blocks).</summary>
public static class Tiles
{
    /// <summary>Blocks movement from every side.</summary>
    public static bool Solid(int i, int j)
    {
        if (!WorldGen.InWorld(i, j, 1)) return true;
        Tile t = Main.tile[i, j];
        return t.HasTile && !t.IsActuated && Main.tileSolid[t.TileType] && !Main.tileSolidTop[t.TileType];
    }

    /// <summary>Platforms and other stand-on-top tiles.</summary>
    public static bool Platform(int i, int j)
    {
        if (!WorldGen.InWorld(i, j, 1)) return false;
        Tile t = Main.tile[i, j];
        return t.HasTile && !t.IsActuated && Main.tileSolidTop[t.TileType];
    }

    /// <summary>
    /// World y of the first floor top crossed when a box spanning [left, right) moves its bottom
    /// from oldBottom down to newBottom, or NaN if none.
    /// </summary>
    public static float Land(float left, float right, float oldBottom, float newBottom, bool platforms)
    {
        int i0 = (int)(left / 16f), i1 = (int)((right - 0.01f) / 16f);
        int r0 = (int)((oldBottom - 0.01f) / 16f) + 1, r1 = (int)(newBottom / 16f);
        for (int r = r0; r <= r1; r++)
        for (int i = i0; i <= i1; i++)
            if (Solid(i, r) || (platforms && Platform(i, r))) return r * 16f;
        return float.NaN;
    }

    /// <summary>World y of the first floor (solid or platform) at or below worldY in column x, or +inf.</summary>
    public static float FloorBelow(float worldX, float worldY, bool platforms, int maxTiles = 60)
    {
        int i = (int)(worldX / 16f);
        int j0 = Math.Max(0, (int)(worldY / 16f));
        for (int j = j0; j < j0 + maxTiles; j++)
            if (Solid(i, j) || (platforms && Platform(i, j))) return j * 16f;
        return float.PositiveInfinity;
    }
}

/// <summary>Axis-aligned box by center and half extents.</summary>
public struct Box
{
    public float X, Y, HW, HH;

    public Box(float x, float y, float hw, float hh) { X = x; Y = y; HW = hw; HH = hh; }

    public static Box Of(Entity e) => new(e.Center.X, e.Center.Y, e.width * 0.5f, e.height * 0.5f);

    public static readonly Box None = new(float.NaN, float.NaN, 0, 0);
    public readonly bool Valid => !float.IsNaN(X);

    /// <summary>Signed separation: negative = overlap depth, positive = clearance (Chebyshev).</summary>
    public readonly float Gap(in Box o)
    {
        float gx = Math.Abs(X - o.X) - HW - o.HW;
        float gy = Math.Abs(Y - o.Y) - HH - o.HH;
        return Math.Max(gx, gy);
    }

    public readonly Box Inflate(float d) => new(X, Y, HW + d, HH + d);
}

/// <summary>Player state the rollout model needs (mirrors Player fields).</summary>
public struct Body
{
    public Vector2 Pos;      // top-left, like Entity.position
    public Vector2 Vel;
    public int Jump;         // Player.jump: sustained-jump ticks left
    public bool Extra;       // cloud jump still available
    public bool Released;    // Player.releaseJump
    public float WingTime;
    public bool Grounded;    // velocity.Y == 0 after collision

    public readonly Vector2 Center(int w, int h) => Pos + new Vector2(w * 0.5f, h * 0.5f);
}

/// <summary>
/// Approximate player kinematics for MPC rollouts, transcribed from Player.HorizontalMovement,
/// JumpMovement, WingMovement and the gravity step (Terraria 1.4.4.9). Collision is a per-tick
/// tile sweep: solid tiles block every side, platforms only feet coming from above unless
/// down is held. No slopes, step-up, dashes or mounts.
/// </summary>
public sealed class PlayerModel
{
    public int W = 20, H = 42;
    public float MaxRun, AccRun, RunAcc, Slow, JumpSpeed, Gravity, MaxFall, Wind;
    public int JumpHeight;
    public bool Cloud, Wings;
    public float WingTimeMax;

    // Player.Update resets run/jump stats to base values before SetControls and only finalizes them
    // (equipment, moveSpeed, UpdateJumpHeight) right before HorizontalMovement, so they are cached there.
    private static float cMaxRun = 3f, cAccRun = 3f, cRunAcc = 0.08f, cSlow = 0.2f, cJumpSpeed = 5.01f, cGravity = 0.4f, cMaxFall = 10f;
    private static int cJumpHeight = 15;

    /// <summary>Called from ModPlayer.PostUpdateRunSpeeds with the final movement stats of the tick.</summary>
    public static void Cache(Player p)
    {
        cMaxRun = p.maxRunSpeed; cAccRun = Math.Max(p.accRunSpeed, p.maxRunSpeed);
        cRunAcc = p.runAcceleration; cSlow = p.runSlowdown;
        cJumpSpeed = Player.jumpSpeed; cJumpHeight = Player.jumpHeight;
        cGravity = p.gravity; cMaxFall = p.maxFallSpeed;
    }

    /// <summary>Loads the movement stats of the previous tick.</summary>
    public void Load(Player p)
    {
        W = p.width; H = p.height;
        MaxRun = cMaxRun; AccRun = cAccRun; RunAcc = cRunAcc; Slow = cSlow;
        JumpSpeed = cJumpSpeed; JumpHeight = cJumpHeight;
        Gravity = cGravity; MaxFall = cMaxFall;
        Cloud = p.GetJumpState(ExtraJump.CloudInABottle).Enabled;
        Wings = p.wingsLogic > 0 && p.wingTimeMax > 0;
        WingTimeMax = p.wingTimeMax;
        Wind = 0f;
        if (p.windPushed)
        {
            Wind = Math.Sign(Main.windSpeedCurrent) * 0.06f;
            if (Math.Abs(Main.windSpeedCurrent) > 0.5f) Wind *= 1.37f;
        }
    }

    public Body Snapshot(Player p) => new()
    {
        Pos = p.position, Vel = p.velocity, Jump = p.jump,
        Extra = p.GetJumpState(ExtraJump.CloudInABottle).Available,
        Released = p.releaseJump, WingTime = p.wingTime, Grounded = p.velocity.Y == 0f,
    };

    public void Step(ref Body b, int dir, bool jump, bool down)
    {
        Horizontal(ref b, dir);
        Vertical(ref b, jump);
        Collide(ref b, down);
        if (b.Grounded) b.WingTime = WingTimeMax;
    }

    private void Horizontal(ref Body b, int dir)
    {
        ref float vx = ref b.Vel.X;
        bool boost = b.Grounded || Wings;
        if (dir < 0)
        {
            if (vx > -MaxRun)
            {
                if (vx > Slow) vx -= Slow;
                vx -= RunAcc;
            }
            else if (vx > -AccRun && boost)
            {
                if (vx > Slow) vx -= Slow;
                vx -= RunAcc * 0.2f * (Wings && !b.Grounded ? 2 : 1);
            }
        }
        else if (dir > 0)
        {
            if (vx < MaxRun)
            {
                if (vx < -Slow) vx += Slow;
                vx += RunAcc;
            }
            else if (vx < AccRun && boost)
            {
                if (vx < -Slow) vx += Slow;
                vx += RunAcc * 0.2f * (Wings && !b.Grounded ? 2 : 1);
            }
        }
        else
        {
            float s = b.Grounded ? Slow : Slow * 0.5f;
            if (vx > s) vx -= s;
            else if (vx < -s) vx += s;
            else vx = 0f;
        }
        if (Wind != 0f)
        {
            float w = Wind * (b.Grounded ? 1f : 1.5f);
            if (dir != 0) w = MathHelper.Clamp(w * 0.8f, -0.072f, 0.072f);
            if (w < 0f && vx > w) vx = Math.Max(vx + w, w);
            if (w > 0f && vx < w) vx = Math.Min(vx + w, w);
        }
    }

    /// <summary>Jump, extra jump, wings and gravity. Returns whether wings were used.</summary>
    private bool Vertical(ref Body b, bool jump)
    {
        bool flying = false;
        if (jump)
        {
            if (b.Jump > 0)
            {
                if (b.Vel.Y == 0f) b.Jump = 0;
                else { b.Vel.Y = -JumpSpeed; b.Jump--; }
            }
            else if ((b.Grounded || b.Extra) && b.Released)
            {
                if (b.Grounded)
                {
                    b.Extra = Cloud;
                    b.Vel.Y = -JumpSpeed;
                    b.Jump = JumpHeight;
                }
                else
                {
                    b.Extra = false;
                    b.Vel.Y = -JumpSpeed;
                    b.Jump = (int)(JumpHeight * 0.75f);
                }
            }
            b.Released = false;
            flying = Wings && b.Jump == 0 && !b.Grounded && b.WingTime > 0f;
        }
        else
        {
            b.Jump = 0;
            b.Released = true;
        }

        if (flying)
        {
            // Generic WingMovement (constantAscend 0.1, falling 0.5, rising 0.1, max 1.5x jumpSpeed).
            b.Vel.Y -= 0.1f;
            if (b.Vel.Y > 0f) b.Vel.Y -= 0.5f;
            else if (b.Vel.Y > -JumpSpeed * 0.5f) b.Vel.Y -= 0.1f;
            if (b.Vel.Y < -JumpSpeed * 1.5f) b.Vel.Y = -JumpSpeed * 1.5f;
            b.WingTime -= 1f;
        }
        else
        {
            b.Vel.Y += Gravity;
            if (b.Vel.Y > MaxFall) b.Vel.Y = MaxFall;
        }
        return flying;
    }

    private void Collide(ref Body b, bool down)
    {
        // Horizontal sweep against solid tiles over the body's height (feet excluded so floors never block).
        float nx = b.Pos.X + b.Vel.X;
        if (b.Vel.X != 0f)
        {
            int edge = b.Vel.X > 0f ? (int)((nx + W) / 16f) : (int)(nx / 16f);
            int j0 = (int)((b.Pos.Y + 1f) / 16f), j1 = (int)((b.Pos.Y + H - 2f) / 16f);
            for (int j = j0; j <= j1; j++)
            {
                if (!Tiles.Solid(edge, j)) continue;
                nx = b.Vel.X > 0f ? edge * 16f - W - 0.01f : (edge + 1) * 16f + 0.01f;
                b.Vel.X = 0f;
                break;
            }
        }
        b.Pos.X = nx;

        float ny = b.Pos.Y + b.Vel.Y;
        int i0 = (int)(b.Pos.X / 16f), i1 = (int)((b.Pos.X + W - 0.01f) / 16f);
        b.Grounded = false;
        if (b.Vel.Y > 0f)
        {
            int rFrom = (int)((b.Pos.Y + H - 0.01f) / 16f) + 1;
            int rTo = (int)((ny + H) / 16f);
            for (int r = rFrom; r <= rTo; r++)
            {
                bool hit = false;
                for (int i = i0; i <= i1 && !hit; i++)
                    hit = Tiles.Solid(i, r) || (!down && Tiles.Platform(i, r));
                if (!hit) continue;
                ny = r * 16f - H;
                b.Vel.Y = 0f;
                b.Grounded = true;
                break;
            }
        }
        else if (b.Vel.Y < 0f)
        {
            int rFrom = (int)(b.Pos.Y / 16f) - 1;
            int rTo = (int)(ny / 16f);
            for (int r = rFrom; r >= rTo; r--)
            {
                bool hit = false;
                for (int i = i0; i <= i1 && !hit; i++) hit = Tiles.Solid(i, r);
                if (!hit) continue;
                ny = (r + 1) * 16f + 0.01f;
                b.Vel.Y = 0f;
                b.Jump = 0;
                break;
            }
        }
        b.Pos.Y = ny;
    }
}

/// <summary>Feeds <see cref="PlayerModel.Cache"/> with the final movement stats of each tick.</summary>
public sealed class SlimesEyeMoveStats : ModPlayer
{
    public override void PostUpdateRunSpeeds()
    {
        if (Player.whoAmI == Main.myPlayer) PlayerModel.Cache(Player);
    }
}
