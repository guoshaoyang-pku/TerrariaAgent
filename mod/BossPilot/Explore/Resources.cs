using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace BossPilot.Explore;

/// <summary>
/// Resource objects as the Spec's section 5.4 describes them: each ore vein gets an id, a tile near
/// its centroid, a count and its exact tile set (the mask the lower layer mines). Chests and life
/// crystals are listed with their anchor tiles.
/// </summary>
internal static class Resources
{
    public static readonly Dictionary<string, int> OreTiles = new()
    {
        ["copper"] = TileID.Copper, ["tin"] = TileID.Tin, ["iron"] = TileID.Iron, ["lead"] = TileID.Lead,
        ["silver"] = TileID.Silver, ["tungsten"] = TileID.Tungsten, ["gold"] = TileID.Gold, ["platinum"] = TileID.Platinum,
        ["demonite"] = TileID.Demonite, ["crimtane"] = TileID.Crimtane, ["meteorite"] = TileID.Meteorite,
        ["hellstone"] = TileID.Hellstone, ["obsidian"] = TileID.Obsidian,
    };

    public static readonly Dictionary<int, int> TileDrop = new()
    {
        [TileID.Copper] = ItemID.CopperOre, [TileID.Tin] = ItemID.TinOre, [TileID.Iron] = ItemID.IronOre, [TileID.Lead] = ItemID.LeadOre,
        [TileID.Silver] = ItemID.SilverOre, [TileID.Tungsten] = ItemID.TungstenOre, [TileID.Gold] = ItemID.GoldOre,
        [TileID.Platinum] = ItemID.PlatinumOre, [TileID.Demonite] = ItemID.DemoniteOre, [TileID.Crimtane] = ItemID.CrimtaneOre,
        [TileID.Meteorite] = ItemID.Meteorite, [TileID.Hellstone] = ItemID.Hellstone, [TileID.Obsidian] = ItemID.Obsidian,
        [TileID.Heart] = ItemID.LifeCrystal,
    };

    public sealed class Vein
    {
        public int Id, Type;
        public List<Point> Tiles = [];
        public Point Centroid;
        public override string ToString() => $"vein#{Id} {TileID.Search.GetName(Type)} x{Tiles.Count} @{Centroid}";
    }

    /// <summary>8-connected components of the given tile types inside the area (tile coords).</summary>
    public static List<Vein> ScanVeins(HashSet<int> types, Rectangle area)
    {
        var veins = new List<Vein>();
        int x0 = Math.Max(area.Left, 41), x1 = Math.Min(area.Right, Main.maxTilesX - 42);
        int y0 = Math.Max(area.Top, 41), y1 = Math.Min(area.Bottom, Main.maxTilesY - 42);
        var seen = new HashSet<long>();
        var stack = new Stack<Point>();
        for (int x = x0; x < x1; x++)
        for (int y = y0; y < y1; y++)
        {
            Tile t = Main.tile[x, y];
            if (!t.HasTile || !types.Contains(t.TileType) || !seen.Add(NavGrid.Key(x, y))) continue;
            var v = new Vein { Id = veins.Count + 1, Type = t.TileType };
            stack.Push(new Point(x, y));
            long sx = 0, sy = 0;
            while (stack.Count > 0)
            {
                Point c = stack.Pop();
                v.Tiles.Add(c);
                sx += c.X;
                sy += c.Y;
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    int nx = c.X + dx, ny = c.Y + dy;
                    if (nx < x0 || ny < y0 || nx >= x1 || ny >= y1) continue;
                    Tile n = Main.tile[nx, ny];
                    if (n.HasTile && n.TileType == v.Type && seen.Add(NavGrid.Key(nx, ny))) stack.Push(new Point(nx, ny));
                }
            }
            // Representative tile: the vein tile closest to the centroid.
            var mean = new Vector2(sx / (float)v.Tiles.Count, sy / (float)v.Tiles.Count);
            Point bestTile = v.Tiles[0];
            float bestD = float.MaxValue;
            foreach (Point c in v.Tiles)
            {
                float d = Vector2.DistanceSquared(new Vector2(c.X, c.Y), mean);
                if (d < bestD)
                {
                    bestD = d;
                    bestTile = c;
                }
            }
            v.Centroid = bestTile;
            veins.Add(v);
        }
        return veins;
    }

    /// <summary>Rough travel cost (ticks) from a node to a tile: running, falling/digging down, climbing.</summary>
    public static float TravelEstimate(Point from, Point to)
    {
        int dx = Math.Abs(to.X - from.X), dy = to.Y - from.Y;
        return dx * 5f + (dy > 0 ? dy * 12f : -dy * 18f);
    }
}
