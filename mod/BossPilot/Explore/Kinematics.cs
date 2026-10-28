using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace BossPilot.Explore;

/// <summary>Controls for one simulated tick.</summary>
public struct Input
{
    public bool Left, Right, Jump, Down;
}

/// <summary>
/// Run/jump stats of an unmounted, dry player. Read from the live player (the values its last
/// Player.Update computed: boots, floor friction, ...) or the defaults of the exploration kit.
/// </summary>
public sealed class MoveStats
{
    public float MaxRun = 3f, AccRun = 6f, RunAcc = 0.08f, RunSlow = 0.2f, Gravity = 0.4f, MaxFall = 10f, JumpSpeed = 5.01f;
    public int JumpHeight = 15, Width = 20, Height = 42;
    public bool Cloud = true;

    /// <summary>
    /// Stats as of the last HorizontalMovement. Player.Update resets them to base values before
    /// SetControls and re-applies equipment later, so they are captured in PostUpdateRunSpeeds.
    /// </summary>
    public static MoveStats From(Player p) => p.GetModPlayer<ExplorePilot>().Stats ?? Capture(p);

    /// <summary>Snapshot of the live stats; gravity and jump are taken only when dry (water values are fixed, see Step).</summary>
    public static MoveStats Capture(Player p)
    {
        var m = new MoveStats
        {
            MaxRun = p.maxRunSpeed, AccRun = p.accRunSpeed, RunAcc = p.runAcceleration, RunSlow = p.runSlowdown,
            Cloud = p.GetJumpState(ExtraJump.CloudInABottle).Enabled, Width = p.width, Height = p.height,
        };
        if (!p.wet)
        {
            m.Gravity = p.gravity;
            m.MaxFall = p.maxFallSpeed;
            m.JumpSpeed = Player.jumpSpeed;
            m.JumpHeight = Player.jumpHeight;
        }
        return m;
    }
}

/// <summary>Simulated player body; Pos is the hitbox top-left in world pixels, like Player.position.</summary>
public struct SimState
{
    public Vector2 Pos, Vel;
    public int Jump, FallStart, Damage;
    public bool ReleaseJump, CloudAvail, StairFall, Wet, WasWet, Lava, Airborne;

    public static SimState From(Player p) => new()
    {
        Pos = p.position, Vel = p.velocity, Jump = p.jump, FallStart = p.fallStart, ReleaseJump = p.releaseJump,
        CloudAvail = p.GetJumpState(ExtraJump.CloudInABottle).Available, StairFall = p.stairFall,
        Airborne = p.velocity.Y != 0f, Wet = p.wet && !p.lavaWet && !p.honeyWet && !p.shimmerWet,
    };

    /// <summary>Navigation node of this body: columns X..X+1, feet in row Y.</summary>
    public readonly Point Node(int width, int height) => Kinematics.NodeOf(Pos, width, height);
}

/// <summary>
/// Forward model of Player.Update's movement for a plain player: HorizontalMovement, JumpMovement
/// (incl. Cloud in a Bottle), gravity, then the same Collision calls DryCollision makes (WalkDownSlope,
/// StepDown/StepUp, TileCollision, SlopeCollision) against the live tile map. Used for MPC rollouts
/// and, without collision, to derive the planner's jump templates.
/// </summary>
public static class Kinematics
{
    /// <summary>Node of a body: feet row is the row above the floor (a half block or slope floor counts as its own row).</summary>
    public static Point NodeOf(Vector2 pos, int width, int height) =>
        new((int)Math.Round((pos.X + width / 2f) / 16f) - 1, (int)Math.Floor((pos.Y + height - 9f) / 16f));

    /// <summary>Hitbox top-left that stands a body exactly on node (x, y).</summary>
    public static Vector2 PosOf(int x, int y, int width = 20, int height = 42) => new((x + 1) * 16 - width / 2f, (y + 1) * 16 - height);

    // Player.Update's values while wet in water (no flippers/merman/trident).
    private const float WetGravity = 0.2f, WetMaxFall = 5f, WetJumpSpeed = 6.01f;
    private const int WetJumpHeight = 30;

    public static void Step(ref SimState s, MoveStats m, Input c, bool collide = true)
    {
        bool grounded = s.Vel.Y == 0f;
        bool wet = s.Wet && collide;
        float gravity = wet ? WetGravity : m.Gravity, maxFall = wet ? WetMaxFall : m.MaxFall, jumpSpeed = wet ? WetJumpSpeed : m.JumpSpeed;
        int jumpHeight = wet ? WetJumpHeight : m.JumpHeight;
        // HorizontalMovement (no mount, wind or dash).
        ref float vx = ref s.Vel.X;
        if (c.Left && vx > -m.MaxRun)
        {
            if (vx > m.RunSlow) vx -= m.RunSlow;
            vx -= m.RunAcc;
        }
        else if (c.Right && vx < m.MaxRun)
        {
            if (vx < -m.RunSlow) vx += m.RunSlow;
            vx += m.RunAcc;
        }
        else if (c.Left && vx > -m.AccRun)
        {
            if (grounded)
            {
                if (vx > m.RunSlow) vx -= m.RunSlow;
                vx -= m.RunAcc * 0.2f;
            }
        }
        else if (c.Right && vx < m.AccRun)
        {
            if (grounded)
            {
                if (vx < -m.RunSlow) vx += m.RunSlow;
                vx += m.RunAcc * 0.2f;
            }
        }
        else if (grounded)
        {
            if (vx > m.RunSlow) vx -= m.RunSlow;
            else if (vx < -m.RunSlow) vx += m.RunSlow;
            else vx = 0f;
        }
        else
        {
            if (vx > m.RunSlow * 0.5f) vx -= m.RunSlow * 0.5f;
            else if (vx < -m.RunSlow * 0.5f) vx += m.RunSlow * 0.5f;
            else vx = 0f;
        }

        // JumpMovement.
        if (c.Jump)
        {
            if (s.Jump > 0)
            {
                if (s.Vel.Y == 0f) s.Jump = 0;
                else
                {
                    s.Vel.Y = -jumpSpeed;
                    s.Jump--;
                }
            }
            else if ((grounded || s.CloudAvail) && s.ReleaseJump)
            {
                if (grounded)
                {
                    s.CloudAvail = m.Cloud;
                    s.Vel.Y = -jumpSpeed;
                    s.Jump = jumpHeight;
                }
                else
                {
                    s.CloudAvail = false;
                    s.Vel.Y = -jumpSpeed;
                    s.Jump = (int)(jumpHeight * 0.75f);
                }
            }
            s.ReleaseJump = false;
        }
        else
        {
            s.Jump = 0;
            s.ReleaseJump = true;
        }

        s.Vel.Y += gravity;
        if (s.Vel.Y > maxFall) s.Vel.Y = maxFall;

        if (!collide)
        {
            s.Pos += s.Vel;
            return;
        }

        int w = m.Width, h = m.Height;
        Vector4 v4 = Collision.WalkDownSlope(s.Pos, s.Vel, w, h, gravity);
        s.Pos = new Vector2(v4.X, v4.Y);
        s.Vel = new Vector2(v4.Z, v4.W);
        float stepSpeed = 1f, gfxOffY = 0f;
        if (s.Vel.Y == gravity) Collision.StepDown(ref s.Pos, ref s.Vel, w, h, ref stepSpeed, ref gfxOffY, 1, false);
        if (s.Vel.Y >= gravity && !c.Down) Collision.StepUp(ref s.Pos, ref s.Vel, w, h, ref stepSpeed, ref gfxOffY, 1, false);
        Vector2 v = Collision.TileCollision(s.Pos, s.Vel, w, h, c.Down, false, 1);
        bool up = Collision.up;
        if (up) s.Jump = 0;
        if (wet)
        {
            // WaterCollision: half the displacement, except along an axis the collision clipped.
            Vector2 d = v * 0.5f;
            if (v.X != s.Vel.X) d.X = v.X;
            if (v.Y != s.Vel.Y) d.Y = v.Y;
            s.Pos += d;
        }
        else s.Pos += v;
        s.Vel = v;
        bool stairFall = s.StairFall || c.Down;
        Vector4 sc = Collision.SlopeCollision(s.Pos, s.Vel, w, h, gravity, stairFall);
        if (Collision.stairFall) stairFall = true;
        else if (!c.Down) stairFall = false;
        s.StairFall = stairFall;
        s.Pos = new Vector2(sc.X, sc.Y);
        s.Vel = new Vector2(sc.Z, sc.W);
        if (up)
        {
            s.Vel.Y = 0.01f;
            s.Jump = 0;
        }

        // Fall damage as Player.Update applies it on landing (25 tiles free, 10 HP per tile beyond).
        int row = (int)(s.Pos.Y / 16f);
        if (s.Vel.Y == 0f)
        {
            int dist = row - s.FallStart;
            if (dist > 25 && !s.Wet) s.Damage += (dist - 25) * 10;
            s.FallStart = row;
            s.CloudAvail = m.Cloud;
        }
        if (s.Jump > 0) s.FallStart = row;
        if (s.Vel.Y != 0f) s.Airborne = true;
        s.Wet = Collision.WetCollision(s.Pos, w, h) && !Collision.honey && !Collision.shimmer;
        if (s.Wet)
        {
            s.WasWet = true;
            s.FallStart = row;
        }
        if (Collision.LavaCollision(s.Pos, w, h)) s.Lava = true;
    }
}

/// <summary>
/// A parametrised open-loop control sequence: hold Dir during [DirFrom, DirTo), then optionally brake
/// against the velocity; hold jump for JumpHold ticks from t=0; fire the Cloud jump CloudDelay ticks
/// after the apex (or once a fall exceeds 18 tiles when CloudDelay == FallSafety).
/// </summary>
public struct Policy
{
    public const int Never = -1, FallSafety = 99;
    public sbyte Dir;
    public short DirFrom, DirTo;
    public bool Brake, Down;
    public byte JumpHold;
    public sbyte CloudDelay;

    public override readonly string ToString() => $"d{Dir}[{DirFrom},{(DirTo >= short.MaxValue ? "inf" : DirTo)}){(Brake ? "b" : "")} j{JumpHold} c{CloudDelay}{(Down ? " down" : "")}";
}

/// <summary>Per-rollout state of a Policy (apex and cloud timing).</summary>
public struct PolicyRun
{
    public Policy P;
    private int apexTick, cloudTick;
    private bool apexSeen;

    public PolicyRun(Policy p)
    {
        P = p;
        apexTick = cloudTick = -1;
        apexSeen = false;
    }

    public Input Next(int t, in SimState s)
    {
        var c = new Input { Down = P.Down };
        int dir = 0;
        if (t >= P.DirFrom && t < P.DirTo) dir = P.Dir;
        else if (t >= P.DirTo && P.Brake && Math.Abs(s.Vel.X) > 0.4f) dir = -Math.Sign(s.Vel.X);
        c.Left = dir < 0;
        c.Right = dir > 0;

        bool jump = t < P.JumpHold;
        if (!apexSeen && t >= P.JumpHold && s.Vel.Y >= -0.5f && (s.Airborne || s.Vel.Y != 0f))
        {
            apexSeen = true;
            apexTick = t;
        }
        if (cloudTick < 0 && P.CloudDelay != Policy.Never && s.CloudAvail && t > P.JumpHold && s.Vel.Y != 0f)
        {
            bool fire = P.CloudDelay == Policy.FallSafety
                ? s.Vel.Y > 0f && (int)(s.Pos.Y / 16f) - s.FallStart >= 18
                : apexSeen && t >= apexTick + P.CloudDelay;
            if (fire) cloudTick = t + 1; // one released tick first, so the press registers
        }
        if (cloudTick >= 0 && t >= cloudTick && t < cloudTick + 12) jump = true;
        c.Jump = jump;
        return c;
    }
}
