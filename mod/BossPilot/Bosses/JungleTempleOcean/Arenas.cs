using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace BossPilot.Bosses.JungleTempleOcean;

/// <summary>Arena geometry the controllers plan against (world pixels).</summary>
public sealed class ArenaInfo
{
    public float Left, Right, Top, Floor;
    public List<float> Platforms = [];
    public Vector2 Mid => new((Left + Right) / 2, (Top + Floor) / 2);
    /// <summary>Per-column top of the first solid tile below the arena top (px), for lumpy natural floors.</summary>
    public float[] Ground;
    private int groundX0;

    /// <summary>Samples the solid floor under every column of the arena (call after building).</summary>
    public void ScanGround()
    {
        int x0 = (int)(Left / 16) - 2, x1 = (int)(Right / 16) + 2;
        groundX0 = x0;
        Ground = new float[x1 - x0 + 1];
        for (int x = x0; x <= x1; x++)
        {
            int y = (int)(Top / 16) + 2;
            while (y < Floor / 16 + 4 && !Arenas.Solid(x, y)) y++;
            Ground[x - x0] = y * 16f;
        }
    }

    /// <summary>Highest ground (smallest y, px) under the span [x0, x1] px; Floor if unscanned.</summary>
    public float GroundUnder(float x0, float x1)
    {
        if (Ground == null) return Floor;
        float best = float.MaxValue;
        for (int x = (int)(x0 / 16); x <= (int)(x1 / 16); x++)
        {
            int i = Math.Clamp(x - groundX0, 0, Ground.Length - 1);
            best = Math.Min(best, Ground[i]);
        }
        return best;
    }

    public static ArenaInfo FromTiles(int x0, int x1, int top, int floor) =>
        new() { Left = x0 * 16, Right = (x1 + 1) * 16, Top = top * 16, Floor = floor * 16 };
}

/// <summary>
/// Site search in the real world (underground jungle, Lihzahrd temple, ocean shore) and the
/// small builders the plans use on top of ArenaBuilder (platform rows, temple walls, altars).
/// </summary>
public static class Arenas
{
    private static List<Point> jungleSites;
    private static Point? altar;
    private static string worldKey;

    private static void CheckWorld()
    {
        string key = $"{Main.worldID}:{Main.maxTilesX}x{Main.maxTilesY}";
        if (key == worldKey) return;
        worldKey = key;
        jungleSites = null;
        altar = null;
    }

    /// <summary>
    /// Floor points of up to three non-overlapping underground-jungle arenas (sorted by quality):
    /// deep in mud/jungle grass, away from lava, honey, hives and the temple.
    /// </summary>
    public static List<Point> JungleSites()
    {
        CheckWorld();
        if (jungleSites != null) return jungleSites;
        int yMin = (int)Main.worldSurface + 70, yMax = Main.UnderworldLayer - 90;
        var cands = new List<(int score, Point p)>();
        for (int cx = 150; cx < Main.maxTilesX - 150; cx += 16)
        for (int fy = yMin; fy <= yMax; fy += 12)
        {
            int jungle = 0, bad = 0;
            for (int x = cx - 85; x < cx + 85; x += 4)
            for (int y = fy - 75; y < fy + 25; y += 4)
            {
                Tile t = Main.tile[x, y];
                if (t.HasTile)
                {
                    ushort tt = t.TileType;
                    if (tt is TileID.JungleGrass or TileID.Mud) jungle++;
                    else if (tt is TileID.LihzahrdBrick or TileID.Hive or TileID.LihzahrdAltar) bad += 3;
                }
                if (t.WallType == WallID.LihzahrdBrickUnsafe) bad += 3;
                if (t.LiquidAmount > 0 && t.LiquidType != LiquidID.Water) bad++;
            }
            if (jungle > 250) cands.Add((jungle - 25 * bad, new Point(cx, fy)));
        }
        cands.Sort((a, b) => b.score.CompareTo(a.score));
        jungleSites = [];
        foreach (var (_, p) in cands)
        {
            bool clash = false;
            foreach (Point q in jungleSites)
                if (Math.Abs(q.X - p.X) < 190 && Math.Abs(q.Y - p.Y) < 110) clash = true;
            if (clash) continue;
            jungleSites.Add(p);
            if (jungleSites.Count == 3) break;
        }
        while (jungleSites.Count < 3) jungleSites.Add(new Point(Main.spawnTileX, (int)Main.rockLayer + 60));
        return jungleSites;
    }

    /// <summary>Top-left tile of the Lihzahrd altar, if the world has one.</summary>
    public static Point? TempleAltar()
    {
        CheckWorld();
        if (altar != null) return altar;
        for (int x = 0; x < Main.maxTilesX; x++)
        for (int y = 0; y < Main.maxTilesY; y++)
        {
            Tile t = Main.tile[x, y];
            if (t.HasTile && t.TileType == TileID.LihzahrdAltar && t.TileFrameX == 0 && t.TileFrameY == 0)
            {
                altar = new Point(x, y);
                return altar;
            }
        }
        return null;
    }

    public static bool Solid(int x, int y) => WorldGen.InWorld(x, y, 1) && Main.tile[x, y].HasTile && Main.tileSolid[Main.tile[x, y].TileType] && !Main.tileSolidTop[Main.tile[x, y].TileType];

    /// <summary>First solid tile row at or below y in column x.</summary>
    public static int GroundBelow(int x, int y, int maxDepth = 200)
    {
        for (int d = 0; d < maxDepth; d++)
            if (Solid(x, y + d)) return y + d;
        return y + maxDepth;
    }

    /// <summary>Clears a rectangle (tiles and liquid) above floorY and lays a solid floor of the given tiles.</summary>
    public static void Carve(Point floor, int width, int height, ushort floorTile, ushort topTile, int floorRows = 4)
    {
        for (int x = floor.X - width / 2; x < floor.X + width / 2; x++)
        {
            for (int y = floor.Y - height; y < floor.Y; y++)
            {
                if (!WorldGen.InWorld(x, y, 2)) continue;
                Main.tile[x, y].ClearTile();
                Main.tile[x, y].LiquidAmount = 0;
            }
            for (int y = floor.Y; y < floor.Y + floorRows; y++)
                if (WorldGen.InWorld(x, y, 2))
                {
                    Main.tile[x, y].ResetToType(y == floor.Y ? topTile : floorTile);
                    Main.tile[x, y].LiquidAmount = 0;
                }
        }
    }

    /// <summary>Wooden platform rows spanning [x0, x1] at the given tile rows.</summary>
    public static void PlatformRows(int x0, int x1, IEnumerable<int> rows, int style = 0)
    {
        foreach (int y in rows)
            for (int x = x0; x <= x1; x++)
            {
                if (!WorldGen.InWorld(x, y, 2)) continue;
                WorldGen.KillTile(x, y, fail: false, effectOnly: false, noItem: true);
                WorldGen.PlaceTile(x, y, TileID.Platforms, mute: true, forced: true, style: style);
            }
    }

    /// <summary>Puts a background wall on every cell of the rectangle.</summary>
    public static void Walls(int x0, int y0, int x1, int y1, ushort wall)
    {
        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
            if (WorldGen.InWorld(x, y, 2)) Main.tile[x, y].WallType = wall;
    }

    /// <summary>Replaces every solid tile of one type inside the rectangle with another.</summary>
    public static void Retile(int x0, int y0, int x1, int y1, ushort from, ushort to)
    {
        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
            if (WorldGen.InWorld(x, y, 2) && Main.tile[x, y].HasTile && Main.tile[x, y].TileType == from)
                Main.tile[x, y].ResetToType(to);
    }

    /// <summary>Resets the sky-light/frame state of a region after bulk edits.</summary>
    public static void Reframe(int x0, int y0, int x1, int y1)
    {
        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
            if (WorldGen.InWorld(x, y, 2)) WorldGen.SquareTileFrame(x, y, resetFrame: true);
    }
}

/// <summary>
/// Tile-count biomes (jungle, hallow, ...) come from Main.SceneMetrics, which the client refreshes
/// from its lighting pass while drawing. A headless server never draws, so the counts stay at
/// zero and Player.ZoneJungle is never set; rescan around the pilot ourselves.
/// </summary>
public static class Biomes
{
    public static void Refresh(Player p)
    {
        Main.SceneMetrics.ScanAndExportToMain(new SceneMetricsScanSettings
        {
            VisualScanArea = null,
            BiomeScanCenterPositionInWorld = p.Center,
            ScanOreFinderData = false,
        });
        p.UpdateBiomes();
    }
}
