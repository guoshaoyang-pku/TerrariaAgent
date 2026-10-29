using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace BossPilot.Bosses.Endgame;

/// <summary>
/// Wide platform arenas for the endgame fights. ArenaBuilder's 120x40 layout is
/// too small for bosses that dash 550 px and hover 500 px above the pilot, so
/// the plans widen it: a cleared box, a stone floor, and standard wood-platform
/// rows low over the floor (the broom pilot flies above them, where a falling
/// mount would otherwise land on a platform the planner does not model).
/// </summary>
public static class Arenas
{
    public static void Platforms(Point site, int width, int height, int rows, int gap)
    {
        int x0 = site.X - width / 2, x1 = site.X + width / 2;
        for (int x = x0; x < x1; x++)
        {
            for (int y = site.Y - height; y < site.Y; y++)
            {
                if (!WorldGen.InWorld(x, y, 10)) continue;
                Tile t = Main.tile[x, y];
                t.LiquidAmount = 0;
                if (t.HasTile) WorldGen.KillTile(x, y, fail: false, effectOnly: false, noItem: true);
            }
            for (int y = site.Y; y < site.Y + 4; y++)
                if (WorldGen.InWorld(x, y, 10) && !Main.tile[x, y].HasTile)
                    WorldGen.PlaceTile(x, y, TileID.Stone, mute: true, forced: true);
        }
        for (int r = 1; r <= rows; r++)
        {
            int y = site.Y - gap * r;
            for (int x = x0; x < x1; x++)
                if (WorldGen.InWorld(x, y, 10))
                    WorldGen.PlaceTile(x, y, TileID.Platforms, mute: true, forced: true, style: 0);
        }
    }

    /// <summary>Removes the floor slab (ArenaBuilder lays 10 rows), leaving a pad of 2*keep tiles at the site.</summary>
    public static void ClearFloor(Point site, int width, int keep)
    {
        for (int x = site.X - width / 2; x < site.X + width / 2; x++)
        {
            if (Math.Abs(x - site.X) < keep) continue;
            for (int y = site.Y; y < site.Y + 12; y++)
                if (WorldGen.InWorld(x, y, 10) && Main.tile[x, y].HasTile)
                    WorldGen.KillTile(x, y, fail: false, effectOnly: false, noItem: true);
        }
    }

    /// <summary>
    /// Keep-in box in world px: inset from the arena sides, between two heights above
    /// the floor (tiles), and below the world-top clamp (Player.Update keeps
    /// position.Y >= Main.topWorld + 656, ~tile 41).
    /// </summary>
    public static Rectangle FlightBox(Point site, int width, int inset, int lowTiles, int highTiles)
    {
        int left = (site.X - width / 2 + inset) * 16, right = (site.X + width / 2 - inset) * 16;
        int top = Math.Max(site.Y - highTiles, 47) * 16, bottom = (site.Y - lowTiles) * 16;
        if (bottom - top < 160) bottom = top + 160;
        return new Rectangle(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// Surface row at column x, scanning from below the space layer so floating
    /// floors left by an earlier space arena in the same world are ignored, and
    /// skipping platforms (tileSolid is true for them too) so an attempt never
    /// builds its arena on the previous attempt's top platform row.
    /// </summary>
    public static int SurfaceAt(int x)
    {
        for (int y = (int)(Main.worldSurface * 0.4); y < Main.worldSurface + 50; y++)
        {
            Tile t = Main.tile[x, y];
            if (t.HasTile && Main.tileSolid[t.TileType] && !Main.tileSolidTop[t.TileType]) return y;
        }
        return (int)Main.worldSurface;
    }

    /// <summary>
    /// Arena site for a biome: surface sites at the spawn column; space just inside the
    /// sky layer (ZoneSkyHeight: tile y <= 0.35 worldSurface) and off the spawn column.
    /// </summary>
    public static Point Site(string biome)
    {
        if (biome == "space")
        {
            int x = Main.spawnTileX + (Main.spawnTileX < Main.maxTilesX / 2 ? 300 : -300);
            return new Point(x, (int)(Main.worldSurface * 0.35) - 1);
        }
        return new Point(Main.spawnTileX, SurfaceAt(Main.spawnTileX));
    }
}
