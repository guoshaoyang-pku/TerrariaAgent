using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace BossPilot.Bosses.SlimesEye;

/// <summary>
/// Arena helpers. Sites are found by the first *solid* tile from the top: the shared
/// ArenaSites.SurfaceAt also stops on platforms, so after one flat_platforms arena every later
/// attempt is built on the previous arena's top platform row, 36 tiles higher, over a void.
/// </summary>
public static class ArenaKit
{
    public static int Surface(int x)
    {
        for (int y = 50; y < Main.worldSurface + 50; y++)
            if (Tiles.Solid(x, y)) return y;
        return (int)Main.worldSurface;
    }

    public static Point Site(int x) => new(x, Surface(x));

    /// <summary>
    /// Standard flat arena around site: clears [x-half, x+half) for `clear` tiles above the floor,
    /// lays a `floorTile` floor 6 rows deep, platform rows at the given heights above the floor and
    /// stone walls just outside both ends so nothing walks off the arena.
    /// </summary>
    public static void Flat(Point site, int half, int clear, int[] rows, int wallHeight, ushort floorTile = TileID.Stone, int floorDepth = 6)
    {
        int x0 = site.X - half, x1 = site.X + half;
        for (int x = x0 - 2; x < x1 + 2; x++)
        {
            for (int y = site.Y - clear; y < site.Y + floorDepth; y++)
            {
                if (!WorldGen.InWorld(x, y, 5)) continue;
                Tile t = Main.tile[x, y];
                t.LiquidAmount = 0;
                bool wall = x < x0 || x >= x1;
                if (wall)
                {
                    if (y >= site.Y - wallHeight) t.ResetToType(TileID.Stone);
                    else if (t.HasTile) WorldGen.KillTile(x, y, noItem: true);
                }
                else if (y < site.Y)
                {
                    if (t.HasTile) WorldGen.KillTile(x, y, noItem: true);
                    t.WallType = 0;
                }
                else t.ResetToType(floorTile);
            }
        }
        foreach (int r in rows)
        {
            int y = site.Y - r;
            for (int x = x0; x < x1; x++)
                if (WorldGen.InWorld(x, y, 5) && !Main.tile[x, y].HasTile)
                    WorldGen.PlaceTile(x, y, TileID.Platforms, mute: true, forced: true, style: 0);
        }
        for (int x = x0 - 3; x <= x1 + 2; x++)
        for (int y = site.Y - clear - 1; y <= site.Y + floorDepth; y++)
            if (WorldGen.InWorld(x, y, 5)) WorldGen.SquareTileFrame(x, y, resetFrame: false);
    }
}
