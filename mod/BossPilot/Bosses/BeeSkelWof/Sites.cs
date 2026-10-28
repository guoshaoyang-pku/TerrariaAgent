using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace BossPilot.Bosses.BeeSkelWof;

/// <summary>
/// Arena locations found from the actual world instead of the spawn column:
/// the real jungle for Queen Bee, a simulated natural route for the Wall of Flesh.
/// </summary>
public static class Sites
{
    private static Point? surfaceJungle, surfaceJungleBuilt, undergroundJungle;

    private static bool Solid(int x, int y)
    {
        Tile t = Main.tile[x, y];
        return t.HasTile && !t.IsActuated && Main.tileSolid[t.TileType] && !Main.tileSolidTop[t.TileType];
    }

    /// <summary>Top of the ground at column x (skips floating islands: needs 12 of the 15 tiles below solid).</summary>
    public static int GroundAt(int x, int y0 = 120)
    {
        for (int y = y0; y < Main.worldSurface + 60; y++)
        {
            if (!Solid(x, y) || Solid(x, y - 1)) continue;
            int n = 0;
            for (int k = 0; k < 15; k++) if (Solid(x, y + k) || Main.tile[x, y + k].LiquidAmount > 0) n++;
            if (n >= 12) return y;
        }
        return (int)Main.worldSurface;
    }

    /// <summary>
    /// Flattest 120-tile stretch of surface jungle (most jungle grass, least height change). The
    /// natural arena gets the best spot; built arenas take the best spot 200+ tiles away from it,
    /// so a flattened arena never replaces the natural terrain in a later attempt.
    /// </summary>
    public static Point SurfaceJungle(bool natural)
    {
        if (natural && surfaceJungle.HasValue) return surfaceJungle.Value;
        if (!natural && surfaceJungleBuilt.HasValue) return surfaceJungleBuilt.Value;
        int w = Main.maxTilesX;
        int[] h = new int[w];
        int[] jg = new int[w];
        for (int x = 50; x < w - 50; x++)
        {
            h[x] = GroundAt(x);
            for (int y = h[x]; y < h[x] + 4; y++)
                if (Main.tile[x, y].HasTile && Main.tile[x, y].TileType == TileID.JungleGrass) jg[x]++;
        }
        float best = float.MaxValue, best2 = float.MaxValue;
        Point site = new(Main.spawnTileX, h[Main.spawnTileX]), site2 = site;
        var scores = new System.Collections.Generic.List<(float score, Point p)>();
        for (int cx = 120; cx < w - 120; cx += 4)
        {
            int jungle = 0, rough = 0, water = 0;
            for (int x = cx - 60; x < cx + 60; x++)
            {
                if (jg[x] > 0) jungle++;
                rough += Math.Abs(h[x + 1] - h[x]);
                if (Main.tile[x, h[x] - 1].LiquidAmount > 0) water++;
            }
            if (jungle < 90) continue;
            float score = rough + water * 2 + Math.Abs(h[cx] - (float)Main.worldSurface) * 0.05f;
            scores.Add((score, new Point(cx, h[cx])));
            if (score < best)
            {
                best = score;
                site = new Point(cx, h[cx]);
            }
        }
        foreach (var (score, pt) in scores)
            if (Math.Abs(pt.X - site.X) >= 200 && score < best2) { best2 = score; site2 = pt; }
        surfaceJungle = site;
        surfaceJungleBuilt = best2 < float.MaxValue ? site2 : site;
        return natural ? site : surfaceJungleBuilt.Value;
    }

    /// <summary>Center of the densest underground jungle (mud + jungle grass) for a 100x50 box.</summary>
    public static Point UndergroundJungle()
    {
        if (undergroundJungle.HasValue) return undergroundJungle.Value;
        int y0 = (int)Main.rockLayer + 10, y1 = Main.UnderworldLayer - 120;
        int bestCount = -1;
        Point site = new(Main.spawnTileX, (int)Main.rockLayer + 60);
        for (int cx = 150; cx < Main.maxTilesX - 150; cx += 25)
        for (int cy = y0 + 50; cy < y1; cy += 20)
        {
            int n = 0;
            for (int x = cx - 50; x < cx + 50; x += 2)
            for (int y = cy - 50; y < cy + 10; y += 2)
            {
                Tile t = Main.tile[x, y];
                if (t.HasTile && (t.TileType == TileID.Mud || t.TileType == TileID.JungleGrass || t.TileType == TileID.Hive)) n++;
            }
            if (n > bestCount)
            {
                bestCount = n;
                site = new Point(cx, cy);
            }
        }
        undergroundJungle = site;
        return site;
    }

    /// <summary>
    /// Picks the natural underworld route by simulation: from drop points every 40 tiles a greedy
    /// runner (Hermes speed, balloon + cloud jumps, water walking, kernel tile collision) runs each
    /// way for 40 s; the farthest dry run wins. Returns its landing point, direction and reach.
    /// </summary>
    public static (Point start, int dir, int reach) HellRouteBySim()
    {
        var saved = (PilotPhysics.MaxRun, PilotPhysics.AccRun, PilotPhysics.RunAcc, PilotPhysics.RunSlow, PilotPhysics.JumpSpeed,
                     PilotPhysics.JumpHeight, PilotPhysics.Gravity, PilotPhysics.MaxFall, PilotPhysics.WaterWalk, PlayerSim.HasCloud);
        PilotPhysics.MaxRun = 4.2f; PilotPhysics.AccRun = 6f; PilotPhysics.RunAcc = 0.112f; PilotPhysics.RunSlow = 0.2f;
        PilotPhysics.JumpSpeed = 6.51f; PilotPhysics.JumpHeight = 20; PilotPhysics.Gravity = 0.4f; PilotPhysics.MaxFall = 10f;
        PilotPhysics.WaterWalk = true; PlayerSim.HasCloud = true;
        Point best = new(Main.spawnTileX, Main.maxTilesY - 100);
        int bestDir = -1, bestReach = -1;
        var tried = new System.Collections.Generic.List<(int reach, int x, int dir)>();
        try
        {
            for (int x = 200; x < Main.maxTilesX - 200; x += 40)
            {
                var s = new SimState { Pos = new Vector2(x * 16 - 10, (Main.UnderworldLayer + 22) * 16), ReleaseJump = true, Cloud = true };
                if (Overlaps(s, solid: true)) continue;
                for (int t = 0; t < 300; t++)
                {
                    PlayerSim.Step(ref s, 0, false, false);
                    if (s.Vel.Y == 0f && t > 3) break;
                }
                if (s.Vel.Y != 0f || Overlaps(s, solid: false)) continue;
                foreach (int dir in new[] { -1, 1 })
                {
                    int reach = Run(s, dir);
                    tried.Add((reach, x, dir));
                    if (reach > bestReach)
                    {
                        bestReach = reach;
                        bestDir = dir;
                        best = new Point((int)(s.Center.X / 16f), (int)((s.Pos.Y + PlayerSim.H) / 16f) + 1);
                    }
                }
            }
        }
        finally
        {
            tried.Sort((a, b) => b.reach.CompareTo(a.reach));
            Trace.Line("route candidates (reach, x, dir): " + string.Join(" ", tried.GetRange(0, Math.Min(8, tried.Count))));
            (PilotPhysics.MaxRun, PilotPhysics.AccRun, PilotPhysics.RunAcc, PilotPhysics.RunSlow, PilotPhysics.JumpSpeed,
             PilotPhysics.JumpHeight, PilotPhysics.Gravity, PilotPhysics.MaxFall, PilotPhysics.WaterWalk, PlayerSim.HasCloud) = saved;
        }
        return (best, bestDir, bestReach);
    }

    private static int Run(SimState s, int dir)
    {
        float x0 = s.Center.X, far = 0f;
        int lastGain = 0;
        bool pressed = false;
        for (int t = 0; t < 2400; t++)
        {
            Vector2 c = s.Center;
            int ax = (int)((c.X + dir * 18f) / 16f);
            bool blocked = false;
            for (int y = (int)(s.Pos.Y / 16f); y <= (int)((s.Pos.Y + PlayerSim.H - 4) / 16f); y++)
                if (Solid(ax, y)) blocked = true;
            bool ground = s.Vel.Y == 0f;
            // Hold jump while blocked; release once falling so the cloud jump can fire.
            bool jump = blocked && (ground || s.Jump > 0 || (!pressed && s.Cloud && s.Vel.Y > 0f));
            pressed = jump;
            PlayerSim.Step(ref s, dir, jump, false);
            if (Overlaps(s, solid: false) || s.Pos.Y > (Main.maxTilesY - 40) * 16) break;
            float gain = (s.Center.X - x0) * dir;
            if (gain > far) { far = gain; lastGain = t; }
            if (t - lastGain > 90) break;
        }
        return (int)(far / 16f);
    }

    /// <summary>Pilot box touches liquid (solid: false) or a solid tile (solid: true).</summary>
    private static bool Overlaps(in SimState s, bool solid)
    {
        int x0 = (int)(s.Pos.X / 16f), x1 = (int)((s.Pos.X + PlayerSim.W - 1) / 16f);
        int y0 = (int)(s.Pos.Y / 16f), y1 = (int)((s.Pos.Y + PlayerSim.H - 1) / 16f);
        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
        {
            if (!WorldGen.InWorld(x, y, 2)) return true;
            if (solid) { if (Solid(x, y)) return true; continue; }
            // Liquid fills the bottom of its tile; water walking stands on that surface (Collision.WaterCollision).
            int liquid = Main.tile[x, y].LiquidAmount;
            if (liquid > 0 && s.Pos.Y + PlayerSim.H > y * 16 + 16 - (liquid / 32 * 2 + 2) + 3) return true;
        }
        return false;
    }
}
