using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace BossPilot.Bosses.EvilDeer;

/// <summary>Kinematic state of the pilot for look-ahead simulation.</summary>
public struct PilotState
{
    public Vector2 Pos;   // top-left, like Player.position
    public Vector2 Vel;
    public int Jump;      // Player.jump (held-jump ticks left)
    public bool Released; // Player.releaseJump
    public bool Extra;    // cloud jump still available
    public bool OnGround => Vel.Y == 0f;
}

/// <summary>
/// Reduced copy of the player movement code (Player.HorizontalMovement,
/// JumpMovement, gravity and tile collision) for the no-mount, no-wing case.
/// Speeds come from MoveStats (last update's final values; at SetControls time
/// Player.Update has already reset them to base).
/// Collision uses the ArenaGrid snapshot (flat floors, platforms, walls).
/// </summary>
public sealed class PilotModel
{
    public float MaxRun, AccRun, RunAccel, RunSlow, JumpSpeed, Gravity, MaxFall, Wind;
    public int JumpHeight, W = 20, H = 42;
    public bool HasCloud;

    public void Read(Player p)
    {
        if (MoveStats.Valid)
        {
            MaxRun = MoveStats.MaxRun;
            AccRun = MoveStats.AccRun;
            RunAccel = MoveStats.RunAccel;
            RunSlow = MoveStats.RunSlow;
            JumpSpeed = MoveStats.JumpSpeed;
            JumpHeight = MoveStats.JumpHeight;
            Gravity = MoveStats.Gravity;
            MaxFall = MoveStats.MaxFall;
        }
        else
        {
            MaxRun = p.maxRunSpeed;
            AccRun = p.accRunSpeed;
            RunAccel = p.runAcceleration;
            RunSlow = p.runSlowdown;
            JumpSpeed = Player.jumpSpeed;
            JumpHeight = Player.jumpHeight;
            Gravity = p.gravity;
            MaxFall = p.maxFallSpeed;
        }
        W = p.width;
        H = p.height;
        HasCloud = p.GetJumpState(ExtraJump.CloudInABottle).Enabled;
        Wind = 0f;
        if (p.windPushed)
        {
            Wind = Math.Sign(Main.windSpeedCurrent) * 0.06f;
            if (Math.Abs(Main.windSpeedCurrent) > 0.5f) Wind *= 1.37f;
        }
    }

    public static PilotState Capture(Player p) => new()
    {
        Pos = p.position,
        Vel = p.velocity,
        Jump = p.jump,
        Released = p.releaseJump,
        Extra = p.GetJumpState(ExtraJump.CloudInABottle).Available,
    };

    public void Step(ref PilotState s, int dir, bool jump, bool down, ArenaGrid g)
    {
        bool ground = s.Vel.Y == 0f;
        float vx = s.Vel.X;
        bool left = dir < 0, right = dir > 0;
        if (left && vx > -MaxRun)
        {
            if (vx > RunSlow) vx -= RunSlow;
            vx -= RunAccel;
        }
        else if (right && vx < MaxRun)
        {
            if (vx < -RunSlow) vx += RunSlow;
            vx += RunAccel;
        }
        else if (left && vx > -AccRun)
        {
            if (ground)
            {
                if (vx > RunSlow) vx -= RunSlow;
                vx -= RunAccel * 0.2f;
            }
        }
        else if (right && vx < AccRun)
        {
            if (ground)
            {
                if (vx < -RunSlow) vx += RunSlow;
                vx += RunAccel * 0.2f;
            }
        }
        else if (ground)
        {
            if (vx > RunSlow) vx -= RunSlow;
            else if (vx < -RunSlow) vx += RunSlow;
            else vx = 0f;
        }
        else
        {
            if (vx > RunSlow * 0.5f) vx -= RunSlow * 0.5f;
            else if (vx < -RunSlow * 0.5f) vx += RunSlow * 0.5f;
            else vx = 0f;
        }
        if (Wind != 0f)
        {
            float w = Wind * (ground ? 1f : 1.5f);
            if (dir != 0) w = MathHelper.Clamp(w * 0.8f, -0.072f, 0.072f);
            if (w < 0f && vx > w) { vx += w; if (vx < w) vx = w; }
            if (w > 0f && vx < w) { vx += w; if (vx > w) vx = w; }
        }
        s.Vel.X = vx;

        if (jump)
        {
            if (s.Jump > 0)
            {
                if (s.Vel.Y == 0f) s.Jump = 0;
                else { s.Vel.Y = -JumpSpeed; s.Jump--; }
            }
            else if ((ground || s.Extra) && s.Released)
            {
                if (ground)
                {
                    s.Vel.Y = -JumpSpeed;
                    s.Jump = JumpHeight;
                    s.Extra = HasCloud;
                }
                else
                {
                    s.Vel.Y = -JumpSpeed;
                    s.Jump = (int)(JumpHeight * 0.75f);
                    s.Extra = false;
                }
            }
            s.Released = false;
        }
        else
        {
            s.Jump = 0;
            s.Released = true;
        }
        if (ground && !jump) s.Extra = HasCloud;

        s.Vel.Y += Gravity;
        if (s.Vel.Y > MaxFall) s.Vel.Y = MaxFall;
        Collide(ref s, down, g);
    }

    /// <summary>Axis-separated sweep against the grid; lands on platforms unless dropping.</summary>
    private void Collide(ref PilotState s, bool down, ArenaGrid g)
    {
        // Horizontal.
        float nx = s.Pos.X + s.Vel.X;
        int top = (int)(s.Pos.Y / 16f), bot = (int)((s.Pos.Y + H - 0.01f) / 16f);
        if (s.Vel.X > 0f)
        {
            int c0 = (int)((s.Pos.X + W - 0.01f) / 16f), c1 = (int)((nx + W - 0.01f) / 16f);
            for (int c = c0 + 1; c <= c1; c++)
                if (ColumnBlocked(g, c, top, bot)) { nx = c * 16 - W; s.Vel.X = 0f; break; }
        }
        else if (s.Vel.X < 0f)
        {
            int c0 = (int)(s.Pos.X / 16f), c1 = (int)(nx / 16f);
            for (int c = c0 - 1; c >= c1; c--)
                if (ColumnBlocked(g, c, top, bot)) { nx = (c + 1) * 16; s.Vel.X = 0f; break; }
        }
        s.Pos.X = nx;

        // Vertical.
        float ny = s.Pos.Y + s.Vel.Y;
        int l = (int)(s.Pos.X / 16f), r = (int)((s.Pos.X + W - 0.01f) / 16f);
        if (s.Vel.Y > 0f)
        {
            float feet = s.Pos.Y + H, nfeet = ny + H;
            int r0 = (int)Math.Ceiling(feet / 16f), r1 = (int)(nfeet / 16f);
            for (int row = r0; row <= r1; row++)
            {
                bool hit = false;
                for (int c = l; c <= r && !hit; c++)
                    hit = g.Solid(c, row) || (!down && g.Platform(c, row));
                if (hit) { ny = row * 16 - H; s.Vel.Y = 0f; break; }
            }
        }
        else if (s.Vel.Y < 0f)
        {
            int r0 = (int)(s.Pos.Y / 16f) - 1, r1 = (int)(ny / 16f);
            for (int row = r0; row >= r1; row--)
            {
                bool hit = false;
                for (int c = l; c <= r && !hit; c++) hit = g.Solid(c, row);
                if (hit) { ny = (row + 1) * 16; s.Vel.Y = 0f; s.Jump = 0; break; }
            }
        }
        s.Pos.Y = ny;
    }

    private static bool ColumnBlocked(ArenaGrid g, int c, int top, int bot)
    {
        for (int row = top; row <= bot; row++)
            if (g.Solid(c, row)) return true;
        return false;
    }
}
