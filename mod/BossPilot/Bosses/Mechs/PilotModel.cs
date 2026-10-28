using System;
using Terraria;
using Terraria.ModLoader;

namespace BossPilot.Bosses.Mechs;

/// <summary>Kinematic state of the pilot used by the planner (center coordinates, px).</summary>
public struct PState
{
    public float X, Y, VX, VY, Wing;
    public int Jump;
    public bool Ground, Released;
}

/// <summary>
/// Per-tick movement constants of the pilot, captured from the real player after
/// tModLoader finished computing run speeds (so armor, boots, wings, Swiftness are in).
/// </summary>
public sealed class PilotStatsPlayer : ModPlayer
{
    public float RunAcc = 0.08f, RunSlow = 0.2f, MaxRun = 3f, AccRun = 6.75f;

    public override void PostUpdateRunSpeeds()
    {
        RunAcc = Player.runAcceleration;
        RunSlow = Player.runSlowdown;
        MaxRun = Player.maxRunSpeed;
        AccRun = Player.accRunSpeed;
    }
}

/// <summary>
/// Simplified copy of Player.HorizontalMovement / JumpMovement / WingMovement and the
/// gravity branch of Player.Update (Terraria 1.4.4.9), plus tile landing. It ignores
/// extra jumps, dashes and slopes; the planner re-plans every tick, so small drift is fine.
/// </summary>
public sealed class PilotModel
{
    public const float HalfW = 10f, HalfH = 21f;

    public float Acc, Slow, MaxRun, AccRun, Grav, MaxFall, JumpSpeed, WingMax;
    public int JumpHeight;
    public bool Wings;

    public void Capture(Player p, out PState s)
    {
        var st = p.GetModPlayer<PilotStatsPlayer>();
        Acc = st.RunAcc; Slow = st.RunSlow; MaxRun = st.MaxRun; AccRun = Math.Max(st.AccRun, st.MaxRun);
        Grav = p.gravity; MaxFall = p.maxFallSpeed;
        JumpSpeed = Player.jumpSpeed; JumpHeight = Player.jumpHeight;
        Wings = p.wingsLogic > 0;
        WingMax = p.wingTimeMax + (p.rocketBoots != 0 ? p.rocketTimeMax * 6 : 0);
        s = new PState
        {
            X = p.Center.X, Y = p.Center.Y, VX = p.velocity.X, VY = p.velocity.Y,
            Wing = p.wingTime + (p.rocketBoots != 0 && p.wingsLogic > 0 ? p.rocketTime * 6 : 0),
            Jump = p.jump, Released = p.releaseJump,
            Ground = p.velocity.Y == 0f && Supported(p.Center.X, p.Center.Y, platforms: true),
        };
    }

    /// <summary>Advances one tick under the given controls.</summary>
    public void Step(ref PState s, int h, bool jumpHeld, bool down)
    {
        // Horizontal (Player.HorizontalMovement).
        if (h < 0)
        {
            if (s.VX > -MaxRun) { if (s.VX > Slow) s.VX -= Slow; s.VX -= Acc; }
            else if (s.VX > -AccRun && (s.Ground || Wings)) { if (s.VX > Slow) s.VX -= Slow; s.VX -= Acc * (Wings ? 0.4f : 0.2f); }
        }
        else if (h > 0)
        {
            if (s.VX < MaxRun) { if (s.VX < -Slow) s.VX += Slow; s.VX += Acc; }
            else if (s.VX < AccRun && (s.Ground || Wings)) { if (s.VX < -Slow) s.VX += Slow; s.VX += Acc * (Wings ? 0.4f : 0.2f); }
        }
        else
        {
            float slow = s.Ground ? Slow : Slow * 0.5f;
            s.VX = s.VX > slow ? s.VX - slow : s.VX < -slow ? s.VX + slow : 0f;
        }

        // Jump (Player.JumpMovement).
        if (jumpHeld)
        {
            if (s.Jump > 0) { s.VY = -JumpSpeed; s.Jump--; }
            else if (s.Ground && s.Released) { s.VY = -JumpSpeed; s.Jump = JumpHeight; s.Ground = false; }
            s.Released = false;
        }
        else { s.Jump = 0; s.Released = true; }
        if (s.Ground && s.Released) s.Wing = WingMax;

        // Wings or gravity.
        bool flying = Wings && jumpHeld && s.Wing > 0 && s.Jump == 0 && !s.Ground;
        if (flying)
        {
            s.VY -= 0.1f;
            if (s.VY > 0f) s.VY -= 0.5f;
            else if (s.VY > -JumpSpeed * 0.5f) s.VY -= 0.1f;
            if (s.VY < -JumpSpeed * 1.5f) s.VY = -JumpSpeed * 1.5f;
            s.Wing -= 1f;
        }
        else if (Wings && jumpHeld && s.VY > 0f)
        {
            s.VY += Grav / 3f;
            if (s.VY > MaxFall / 3f) s.VY = MaxFall / 3f;
        }
        else if (!s.Ground)
        {
            s.VY += Grav;
        }
        if (s.VY > MaxFall) s.VY = MaxFall;

        // Move and collide (feet land on solids and, unless dropping, platforms).
        float nx = s.X + s.VX;
        if (s.VX != 0f && Blocked(nx + Math.Sign(s.VX) * HalfW, s.Y)) { s.VX = 0f; nx = s.X; }
        s.X = nx;
        if (s.VY > 0f)
        {
            float feet = s.Y + HalfH, nf = feet + s.VY;
            int r0 = (int)(feet / 16f), r1 = (int)(nf / 16f);
            bool landed = false;
            for (int r = r0 + (feet % 16f == 0f ? 0 : 1); r <= r1 && !landed; r++)
                if (RowSupports(s.X, r, platforms: !down)) { s.Y = r * 16f - HalfH; s.VY = 0f; s.Ground = true; landed = true; }
            if (!landed) { s.Y += s.VY; s.Ground = false; }
        }
        else if (s.VY < 0f)
        {
            float head = s.Y - HalfH + s.VY;
            if (RowSolid(s.X, (int)(head / 16f))) s.VY = 0f;
            else s.Y += s.VY;
            s.Ground = false;
        }
        else if (s.Ground && !Supported(s.X, s.Y, platforms: !down))
        {
            s.Ground = false;
        }
    }

    public static bool Supported(float cx, float cy, bool platforms) => RowSupports(cx, (int)((cy + HalfH + 1f) / 16f), platforms);

    private static bool RowSupports(float cx, int row, bool platforms)
    {
        int i0 = (int)((cx - HalfW) / 16f), i1 = (int)((cx + HalfW - 1f) / 16f);
        for (int i = i0; i <= i1; i++)
            if (TileAt(i, row, platforms)) return true;
        return false;
    }

    private static bool RowSolid(float cx, int row) => RowSupports(cx, row, platforms: false);

    private static bool Blocked(float x, float cy)
    {
        int i = (int)(x / 16f);
        int j0 = (int)((cy - HalfH) / 16f), j1 = (int)((cy + HalfH - 2f) / 16f);
        for (int j = j0; j <= j1; j++)
            if (TileAt(i, j, platforms: false)) return true;
        return false;
    }

    public static bool TileAt(int i, int j, bool platforms)
    {
        if (!WorldGen.InWorld(i, j, 1)) return true;
        Tile t = Main.tile[i, j];
        if (!t.HasTile || t.IsActuated) return false;
        if (Main.tileSolidTop[t.TileType]) return platforms;
        return Main.tileSolid[t.TileType];
    }
}
