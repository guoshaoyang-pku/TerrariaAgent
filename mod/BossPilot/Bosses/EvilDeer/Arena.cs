using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace BossPilot.Bosses.EvilDeer;

/// <summary>Small world-editing helpers for arena decoration (tile coordinates, inclusive ranges).</summary>
public static class Arena
{
    public static void ClearTile(int x, int y)
    {
        if (!WorldGen.InWorld(x, y, 2)) return;
        if (Main.tile[x, y].HasTile) WorldGen.KillTile(x, y, fail: false, effectOnly: false, noItem: true);
    }

    public static void ClearBox(int x0, int x1, int y0, int y1)
    {
        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
        {
            if (!WorldGen.InWorld(x, y, 2)) continue;
            Main.tile[x, y].LiquidAmount = 0;
            ClearTile(x, y);
        }
    }

    public static void PlatformRow(int x0, int x1, int y)
    {
        for (int x = x0; x <= x1; x++)
        {
            if (!WorldGen.InWorld(x, y, 2)) continue;
            ClearTile(x, y);
            WorldGen.PlaceTile(x, y, TileID.Platforms, mute: true, forced: true, style: 0);
        }
    }

    public static void Fill(int x0, int x1, int y0, int y1, int tile)
    {
        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
            if (WorldGen.InWorld(x, y, 2))
                Main.tile[x, y].ResetToType((ushort)tile);
    }

    /// <summary>Topmost solid (non-platform) tile in column x at or below row y0, or -1.</summary>
    public static int SolidBelow(int x, int y0, int maxDepth = 200)
    {
        for (int y = y0; y < y0 + maxDepth && y < Main.maxTilesY - 5; y++)
        {
            Tile t = Main.tile[x, y];
            if (t.HasTile && Main.tileSolid[t.TileType] && !Main.tileSolidTop[t.TileType]) return y;
        }
        return -1;
    }

    public static Vector2 FeetAt(int tileX, int floorRow, Player p) =>
        new(tileX * 16 + 8 - p.width / 2f, floorRow * 16 - p.height);

    private static readonly Dictionary<string, Point> sites = [];

    /// <summary>
    /// Arena floor at the world spawn, fixed for the life of the server. ArenaSites.Default
    /// re-runs SurfaceAt every attempt, which then finds the previous attempt's platforms or
    /// box ceiling and stacks the new arena on top of it; the spawn row from worldgen does not move.
    /// Everything left above the floor by earlier attempts is cleared.
    /// </summary>
    public static Point SpawnSite()
    {
        string key = Main.worldID + ":spawn";
        if (!sites.TryGetValue(key, out Point site))
        {
            int x = Main.spawnTileX, y = Main.spawnTileY;
            bool valid = WorldGen.InWorld(x, y, 10) && Main.tile[x, y].HasTile && Main.tileSolid[Main.tile[x, y].TileType]
                         && !Main.tile[x, y - 1].HasTile;
            site = new Point(x, valid ? y : Common.ArenaSites.SurfaceAt(x));
            sites[key] = site;
            Trace.Line($"[site] spawn ({x},{Main.spawnTileY}) valid={valid} -> floor row {site.Y}");
        }
        ClearBox(site.X - 125, site.X + 125, site.Y - 95, site.Y - 1);
        return site;
    }

    /// <summary>Caches any computed site for the life of the server (world edits must not move it).</summary>
    public static Point Cached(string key, System.Func<Point> compute)
    {
        key = Main.worldID + ":" + key;
        if (!sites.TryGetValue(key, out Point site)) sites[key] = site = compute();
        return site;
    }
}
