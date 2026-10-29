using System;
using Terraria;

namespace BossPilot.Explore;

/// <summary>
/// Whole-world copy of the tile arrays (Main.tile.GetData, a memcpy per data kind) plus chest
/// contents, so every attempt starts on the same terrain despite digging, building and looting.
/// </summary>
internal sealed class WorldSnapshot
{
    private TileTypeData[] type;
    private WallTypeData[] wall;
    private LiquidData[] liquid;
    private TileWallWireStateData[] wire;
    private TileWallBrightnessInvisibilityData[] bright;
    private Item[][] chests;

    public static WorldSnapshot Take()
    {
        var s = new WorldSnapshot
        {
            type = (TileTypeData[])Main.tile.GetData<TileTypeData>().Clone(),
            wall = (WallTypeData[])Main.tile.GetData<WallTypeData>().Clone(),
            liquid = (LiquidData[])Main.tile.GetData<LiquidData>().Clone(),
            wire = (TileWallWireStateData[])Main.tile.GetData<TileWallWireStateData>().Clone(),
            bright = (TileWallBrightnessInvisibilityData[])Main.tile.GetData<TileWallBrightnessInvisibilityData>().Clone(),
            chests = new Item[Main.maxChests][],
        };
        for (int i = 0; i < Main.maxChests; i++)
        {
            Chest c = Main.chest[i];
            if (c == null) continue;
            s.chests[i] = new Item[c.item.Length];
            for (int k = 0; k < c.item.Length; k++) s.chests[i][k] = c.item[k]?.Clone() ?? new Item();
        }
        return s;
    }

    public void Restore()
    {
        Array.Copy(type, Main.tile.GetData<TileTypeData>(), type.Length);
        Array.Copy(wall, Main.tile.GetData<WallTypeData>(), wall.Length);
        Array.Copy(liquid, Main.tile.GetData<LiquidData>(), liquid.Length);
        Array.Copy(wire, Main.tile.GetData<TileWallWireStateData>(), wire.Length);
        Array.Copy(bright, Main.tile.GetData<TileWallBrightnessInvisibilityData>(), bright.Length);
        for (int i = 0; i < Main.maxChests; i++)
        {
            Chest c = Main.chest[i];
            if (c == null || chests[i] == null) continue;
            for (int k = 0; k < c.item.Length && k < chests[i].Length; k++) c.item[k] = chests[i][k].Clone();
        }
    }
}
