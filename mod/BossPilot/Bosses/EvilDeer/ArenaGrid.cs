using Microsoft.Xna.Framework;
using Terraria;

namespace BossPilot.Bosses.EvilDeer;

/// <summary>
/// Snapshot of the tiles around the arena as flat bit arrays, so the pilot
/// model and the worm predictor can query collision thousands of times per
/// tick without touching Main.tile. Arenas do not change during a fight.
/// Outside the captured window queries fall back to the live tile map.
/// </summary>
public sealed class ArenaGrid
{
    public readonly int X0, Y0, W, H;
    private readonly bool[] solid;   // blocks the player (solid, not a platform)
    private readonly bool[] platform; // one-way platform (tileSolidTop)
    private readonly bool[] worm;    // counts as "in tiles" for AI_006 worms

    private ArenaGrid(int x0, int y0, int w, int h)
    {
        X0 = x0; Y0 = y0; W = w; H = h;
        solid = new bool[w * h];
        platform = new bool[w * h];
        worm = new bool[w * h];
    }

    /// <summary>Captures a window of tiles centered on <paramref name="center"/> (tile coords).</summary>
    public static ArenaGrid Capture(Point center, int halfWidth, int up, int down)
    {
        int x0 = System.Math.Max(5, center.X - halfWidth);
        int y0 = System.Math.Max(5, center.Y - up);
        int x1 = System.Math.Min(Main.maxTilesX - 5, center.X + halfWidth);
        int y1 = System.Math.Min(Main.maxTilesY - 5, center.Y + down);
        var g = new ArenaGrid(x0, y0, x1 - x0, y1 - y0);
        for (int x = x0; x < x1; x++)
        for (int y = y0; y < y1; y++)
        {
            int i = (x - x0) * g.H + (y - y0);
            g.solid[i] = LiveSolid(x, y);
            g.platform[i] = LivePlatform(x, y);
            g.worm[i] = LiveWorm(x, y);
        }
        return g;
    }

    public bool Solid(int x, int y)
    {
        int dx = x - X0, dy = y - Y0;
        if ((uint)dx < (uint)W && (uint)dy < (uint)H) return solid[dx * H + dy];
        return WorldGen.InWorld(x, y, 2) && LiveSolid(x, y);
    }

    public bool Platform(int x, int y)
    {
        int dx = x - X0, dy = y - Y0;
        if ((uint)dx < (uint)W && (uint)dy < (uint)H) return platform[dx * H + dy];
        return WorldGen.InWorld(x, y, 2) && LivePlatform(x, y);
    }

    /// <summary>Solid or platform: anything the player can stand on.</summary>
    public bool Standable(int x, int y) => Solid(x, y) || Platform(x, y);

    public bool WormTile(int x, int y)
    {
        int dx = x - X0, dy = y - Y0;
        if ((uint)dx < (uint)W && (uint)dy < (uint)H) return worm[dx * H + dy];
        return WorldGen.InWorld(x, y, 2) && LiveWorm(x, y);
    }

    /// <summary>
    /// AI_006 tile test for a worm segment rectangle (world px): any overlapped tile that is
    /// solid, a top-surface tile with frameY 0 (platforms), or holds more than 64 liquid.
    /// </summary>
    public bool WormInTiles(float x, float y, int w, int h)
    {
        int x0 = (int)(x / 16f), x1 = (int)((x + w) / 16f);
        int y0 = (int)(y / 16f), y1 = (int)((y + h) / 16f);
        for (int tx = x0; tx <= x1; tx++)
        for (int ty = y0; ty <= y1; ty++)
        {
            if (!WormTile(tx, ty)) continue;
            float px = tx * 16, py = ty * 16;
            if (x + w > px && x < px + 16 && y + h > py && y < py + 16) return true;
        }
        return false;
    }

    /// <summary>Topmost standable tile row at or below <paramref name="fromY"/> in column x, or -1.</summary>
    public int GroundBelow(int x, int fromY, int maxDepth = 80)
    {
        for (int y = fromY; y < fromY + maxDepth; y++)
            if (Standable(x, y)) return y;
        return -1;
    }

    private static bool LiveSolid(int x, int y)
    {
        Tile t = Main.tile[x, y];
        return t.HasUnactuatedTile && Main.tileSolid[t.TileType] && !Main.tileSolidTop[t.TileType];
    }

    private static bool LivePlatform(int x, int y)
    {
        Tile t = Main.tile[x, y];
        return t.HasUnactuatedTile && Main.tileSolidTop[t.TileType];
    }

    private static bool LiveWorm(int x, int y)
    {
        Tile t = Main.tile[x, y];
        if (t.LiquidAmount > 64) return true;
        if (!t.HasUnactuatedTile) return false;
        return Main.tileSolid[t.TileType] || (Main.tileSolidTop[t.TileType] && t.TileFrameY == 0);
    }
}
