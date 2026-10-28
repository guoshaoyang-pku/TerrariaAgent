using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace BossPilot.Common;

/// <summary>
/// Builds the arena variants referenced by scenario configs, centered on the
/// player. Sizes are intentionally modest; scenario JSON does not carry
/// dimensions yet (Spec section 15 leaves layout search open).
/// </summary>
public static class ArenaBuilder
{
    public static void Build(string arena, string biome, Vector2 center)
    {
        Point origin = (center / 16f).ToPoint();
        switch (arena)
        {
            case "flat_platforms":
                Flatten(origin, width: 120, height: 40);
                PaintBiomeFloor(origin, biome, width: 120);
                Platforms(origin, width: 120, rows: 3, gap: 12);
                break;
            case "open_sky":
                Flatten(origin, width: 160, height: 80);
                break;
            case "enclosed":
                Flatten(origin, width: 100, height: 50);
                PaintBiomeFloor(origin, biome, width: 100);
                Box(origin, width: 100, height: 50);
                break;
            case "bridge":
                // Wall of Flesh style: one long horizontal runway. The wall
                // does not stop and the fight can run for minutes at ~12
                // tiles/s, so span nearly the whole world.
                Flatten(origin, width: Main.maxTilesX - 300, height: 40);
                Platforms(origin, width: Main.maxTilesX - 300, rows: 1, gap: 6);
                break;
            case "natural":
            default:
                break; // leave terrain untouched
        }
    }

    /// <summary>
    /// Replaces the arena floor strip with the biome's grass/block type so the
    /// game registers the requested biome at the fight location.
    /// </summary>
    private static void PaintBiomeFloor(Point origin, string biome, int width)
    {
        int tile = biome switch
        {
            "corruption" => TileID.CorruptGrass,
            "crimson" => TileID.CrimsonGrass,
            "jungle" or "underground_jungle" => TileID.JungleGrass,
            "hallow" => TileID.HallowedGrass,
            "ocean" or "desert" => TileID.Sand,
            "snow" => TileID.SnowBlock,
            "underworld" => TileID.Ash,
            _ => -1,
        };
        if (tile < 0) return;
        for (int x = origin.X - width / 2; x < origin.X + width / 2; x++)
            if (WorldGen.InWorld(x, origin.Y, 2))
                WorldGen.PlaceTile(x, origin.Y, tile, mute: true, forced: true);
    }

    private static void Flatten(Point origin, int width, int height)
    {
        for (int x = origin.X - width / 2; x < origin.X + width / 2; x++)
        for (int y = origin.Y - height; y < origin.Y + 10; y++)
        {
            if (!WorldGen.InWorld(x, y, 2)) continue;
            Main.tile[x, y].LiquidAmount = 0; // drain lava/water pockets
            if (y < origin.Y) WorldGen.KillTile(x, y, fail: false, effectOnly: false, noItem: true);
            else if (!Main.tile[x, y].HasTile) WorldGen.PlaceTile(x, y, TileID.Stone, mute: true, forced: true);
        }
    }

    private static void Platforms(Point origin, int width, int rows, int gap)
    {
        for (int r = 0; r < rows; r++)
        {
            int y = origin.Y - gap * (r + 1);
            for (int x = origin.X - width / 2; x < origin.X + width / 2; x++)
                if (WorldGen.InWorld(x, y, 2))
                {
                    WorldGen.KillTile(x, y, fail: false, effectOnly: false, noItem: true);
                    WorldGen.PlaceTile(x, y, TileID.Platforms, mute: true, forced: true, style: 0);
                }
        }
    }

    private static void Box(Point origin, int width, int height)
    {
        for (int x = origin.X - width / 2; x <= origin.X + width / 2; x++)
        for (int y = origin.Y - height; y <= origin.Y + 10; y++)
        {
            bool edge = x == origin.X - width / 2 || x == origin.X + width / 2
                     || y == origin.Y - height || y == origin.Y + 10;
            if (edge && WorldGen.InWorld(x, y, 2))
                WorldGen.PlaceTile(x, y, TileID.Stone, mute: true, forced: true);
        }
    }
}
