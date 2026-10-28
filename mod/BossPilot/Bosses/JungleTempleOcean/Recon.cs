using System;
using System.IO;
using System.Text;
using Terraria;
using Terraria.ID;

namespace BossPilot.Bosses.JungleTempleOcean;

/// <summary>
/// Debug-only world survey (BOSSPILOT_RECON=1): writes world constants, per-column
/// biome tile counts and a downsampled PPM tile map to the headless out dir, so arena
/// sites in the real terrain can be chosen offline.
/// </summary>
public static class Recon
{
    private static bool done;

    public static void DumpOnce()
    {
        if (done || Environment.GetEnvironmentVariable("BOSSPILOT_RECON") != "1") return;
        done = true;
        string dir = Common.Headless.HeadlessSim.OutDir ?? ".";
        var sb = new StringBuilder();
        sb.AppendLine($"maxTiles {Main.maxTilesX}x{Main.maxTilesY} surface {Main.worldSurface} rock {Main.rockLayer} underworld {Main.UnderworldLayer}");
        sb.AppendLine($"spawn {Main.spawnTileX},{Main.spawnTileY} dungeon {Main.dungeonX},{Main.dungeonY} hardMode {Main.hardMode} expert {Main.expertMode}");
        for (int x = 0; x < Main.maxTilesX; x += 20)
        {
            int jungle = 0, lihz = 0, wall87 = 0, water = 0, altar = 0;
            for (int dx = 0; dx < 20; dx++)
            for (int y = 0; y < Main.maxTilesY; y++)
            {
                Tile t = Main.tile[x + dx, y];
                if (t.HasTile)
                {
                    if (t.TileType is TileID.JungleGrass or TileID.Mud) jungle++;
                    if (t.TileType == TileID.LihzahrdBrick) lihz++;
                    if (t.TileType == TileID.LihzahrdAltar) altar++;
                }
                if (t.WallType == WallID.LihzahrdBrickUnsafe) wall87++;
                if (t.LiquidAmount > 0 && t.LiquidType == LiquidID.Water && y < Main.worldSurface) water++;
            }
            sb.AppendLine($"x {x,4}: jungle {jungle,6} lihz {lihz,5} wall87 {wall87,5} water {water,5} altar {altar} surf {Common.ArenaSites.SurfaceAt(x)}");
        }
        for (int x = 0; x < Main.maxTilesX; x++)
        for (int y = 0; y < Main.maxTilesY; y++)
            if (Main.tile[x, y].HasTile && Main.tile[x, y].TileType == TileID.LihzahrdAltar)
                sb.AppendLine($"altar tile {x},{y} frame {Main.tile[x, y].TileFrameX},{Main.tile[x, y].TileFrameY}");
        for (int x = 0; x < Main.maxTilesX; x += 10)
        {
            if (x > 420 && x < Main.maxTilesX - 420) continue;
            int liquidTop = -1, solid = -1;
            for (int y = 50; y < Main.worldSurface + 30; y++)
            {
                Tile t = Main.tile[x, y];
                if (liquidTop < 0 && t.LiquidAmount > 0) liquidTop = y;
                if (t.HasTile && Main.tileSolid[t.TileType]) { solid = y; break; }
            }
            sb.AppendLine($"ocean x {x,4}: liquidTop {liquidTop} solid {solid}");
        }
        File.WriteAllText(Path.Combine(dir, "recon.txt"), sb.ToString());
        WriteMap(Path.Combine(dir, "map.ppm"), 2);
        WriteMap(Path.Combine(dir, "temple.ppm"), 1, 3100, 600, 350, 250);
        WriteMap(Path.Combine(dir, "ocean_l.ppm"), 1, 0, 150, 420, 150);
        WriteMap(Path.Combine(dir, "jungle.ppm"), 1, 3400, 330, 600, 500);
    }

    /// <summary>Downsampled colour map (one pixel per scale x scale tiles, sampled at the top-left tile).</summary>
    public static void WriteMap(string path, int scale, int x0 = 0, int y0 = 0, int w = -1, int h = -1)
    {
        if (w < 0) w = Main.maxTilesX;
        if (h < 0) h = Main.maxTilesY;
        int pw = w / scale, ph = h / scale;
        using var fs = new FileStream(path, FileMode.Create);
        byte[] header = Encoding.ASCII.GetBytes($"P6\n{pw} {ph}\n255\n");
        fs.Write(header);
        byte[] row = new byte[pw * 3];
        for (int py = 0; py < ph; py++)
        {
            for (int px = 0; px < pw; px++)
            {
                (int r, int g, int b) = Color(x0 + px * scale, y0 + py * scale);
                row[px * 3] = (byte)r; row[px * 3 + 1] = (byte)g; row[px * 3 + 2] = (byte)b;
            }
            fs.Write(row);
        }
    }

    private static (int, int, int) Color(int x, int y)
    {
        if (!WorldGen.InWorld(x, y)) return (0, 0, 0);
        Tile t = Main.tile[x, y];
        if (t.HasTile)
        {
            return t.TileType switch
            {
                TileID.JungleGrass => (40, 220, 40),
                TileID.Mud => (110, 90, 50),
                TileID.LihzahrdBrick => (200, 110, 30),
                TileID.LihzahrdAltar => (255, 255, 0),
                TileID.Sand => (230, 210, 130),
                TileID.Stone => (130, 130, 130),
                TileID.Dirt => (150, 100, 60),
                TileID.Grass => (90, 170, 60),
                TileID.Platforms => (255, 0, 255),
                TileID.Ebonstone or TileID.Crimstone or TileID.Pearlstone => (160, 60, 160),
                _ => Main.tileSolid[t.TileType] ? (180, 180, 180) : (90, 90, 90),
            };
        }
        if (t.LiquidAmount > 0)
            return t.LiquidType == LiquidID.Lava ? (255, 80, 0) : t.LiquidType == LiquidID.Honey ? (230, 200, 0) : (40, 80, 230);
        if (t.WallType == WallID.LihzahrdBrickUnsafe) return (100, 50, 10);
        if (t.WallType > 0) return (45, 45, 45);
        return y < Main.worldSurface ? (150, 200, 255) : (0, 0, 0);
    }
}
