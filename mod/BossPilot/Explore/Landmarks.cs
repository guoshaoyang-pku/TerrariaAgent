using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace BossPilot.Explore;

/// <summary>
/// World knowledge for landmark tasks, read from the loaded world (full-map knowledge: the Spec leaves
/// the agent's allowed visibility open, so this layer currently uses the whole tile map):
/// surface biome spans, layer depths, dungeon entrance, and arrival tests via the player's Zone flags.
/// </summary>
internal static class Landmarks
{
    public sealed class Span
    {
        public string Biome;
        public int X0, X1;
        public int Mid => (X0 + X1) / 2;
        public int Len => X1 - X0 + 1;
    }

    private static List<Span> spans;
    private static int[] surface;

    public static void Reset()
    {
        spans = null;
        surface = null;
    }

    /// <summary>Row of the first solid tile from the sky at column x (above the cavern layer).</summary>
    public static int SurfaceY(int x)
    {
        surface ??= new int[Main.maxTilesX];
        if (surface[x] != 0) return surface[x];
        int limit = (int)Main.rockLayer;
        int y = 40;
        for (; y < limit; y++)
        {
            Tile t = Main.tile[x, y];
            if (t.HasTile && Main.tileSolid[t.TileType] && !Main.tileSolidTop[t.TileType] && t.TileType != TileID.Cloud && t.TileType != TileID.RainCloud) break;
        }
        return surface[x] = y;
    }

    private static string Classify(int x)
    {
        int y0 = SurfaceY(x);
        int snow = 0, sand = 0, jungle = 0, corrupt = 0, crimson = 0;
        for (int y = y0; y < y0 + 6 && y < Main.maxTilesY; y++)
        {
            Tile t = Main.tile[x, y];
            if (!t.HasTile) continue;
            switch (t.TileType)
            {
                case TileID.SnowBlock or TileID.IceBlock or TileID.SnowBrick: snow++; break;
                case TileID.Sand or TileID.HardenedSand or TileID.Sandstone: sand++; break;
                case TileID.JungleGrass or TileID.Mud: jungle++; break;
                case TileID.CorruptGrass or TileID.Ebonstone or TileID.Ebonsand or TileID.CorruptIce or TileID.CorruptHardenedSand or TileID.CorruptSandstone: corrupt++; break;
                case TileID.CrimsonGrass or TileID.Crimstone or TileID.Crimsand or TileID.FleshIce or TileID.CrimsonHardenedSand or TileID.CrimsonSandstone: crimson++; break;
            }
        }
        int best = Math.Max(Math.Max(snow, sand), Math.Max(jungle, Math.Max(corrupt, crimson)));
        if (best < 2) return null;
        if (best == corrupt) return "corruption";
        if (best == crimson) return "crimson";
        if (best == snow) return "snow";
        if (best == jungle) return "jungle";
        return "desert";
    }

    /// <summary>Contiguous surface spans per biome (gaps up to 12 columns merged; beaches excluded from desert).</summary>
    public static List<Span> Spans()
    {
        if (spans != null) return spans;
        spans = [];
        Span cur = null;
        int gap = 0;
        for (int x = 60; x < Main.maxTilesX - 60; x++)
        {
            string b = Classify(x);
            if (b == "desert" && (x < WorldGen.beachDistance + 20 || x > Main.maxTilesX - WorldGen.beachDistance - 20)) b = null;
            if (cur != null && b == cur.Biome)
            {
                cur.X1 = x;
                gap = 0;
                continue;
            }
            if (cur != null && ++gap <= 12) continue;
            if (cur != null && cur.Len >= 20) spans.Add(cur);
            cur = b == null ? null : new Span { Biome = b, X0 = x, X1 = x };
            gap = 0;
        }
        if (cur != null && cur.Len >= 20) spans.Add(cur);
        // Dunes, ponds and grass patches split one biome's surface; merge same-biome spans across short gaps.
        var merged = new List<Span>();
        foreach (Span s in spans)
        {
            Span last = merged.LastOrDefault(m => m.Biome == s.Biome);
            if (last != null && s.X0 - last.X1 <= 80 && merged[^1] == last) last.X1 = s.X1;
            else merged.Add(s);
        }
        return spans = merged;
    }

    public static Span Nearest(string biome, int fromX)
    {
        IEnumerable<Span> c = Spans().Where(s => biome == "evil" ? s.Biome is "corruption" or "crimson" : s.Biome == biome);
        return c.OrderBy(s => Math.Max(0, Math.Max(s.X0 - fromX, fromX - s.X1)) - s.Len * 0.2f).FirstOrDefault();
    }

    public static readonly string[] All =
        ["ocean_left", "ocean_right", "dungeon", "jungle", "snow", "desert", "evil", "underground", "cavern", "underworld"];

    /// <summary>Navigation goal (node box) for a landmark, or null if the world has none.</summary>
    public static IGoal Goal(string target, Point from)
    {
        int ws = (int)Main.worldSurface, rock = (int)Main.rockLayer, under = Main.UnderworldLayer, maxX = Main.maxTilesX, maxY = Main.maxTilesY;
        switch (target)
        {
            case "ocean_left": return new BoxGoal(41, 0, WorldGen.beachDistance - 40, (int)WorldGen.oceanLevel - 2);
            case "ocean_right": return new BoxGoal(maxX - WorldGen.beachDistance + 40, 0, maxX - 43, (int)WorldGen.oceanLevel - 2);
            case "dungeon": return new BoxGoal(Main.dungeonX - 6, Main.dungeonY - 8, Main.dungeonX + 5, Main.dungeonY + 4);
            case "underground": return new BoxGoal(41, ws + 4, maxX - 43, rock - 2);
            case "cavern": return new BoxGoal(41, rock + 4, maxX - 43, under - 2);
            case "underworld": return new BoxGoal(41, under + 4, maxX - 43, maxY - 45);
            case "spawn": return new BoxGoal(Main.spawnTileX - 3, Main.spawnTileY - 4, Main.spawnTileX + 2, Main.spawnTileY + 2);
            default:
            {
                Span s = Nearest(target, from.X);
                if (s == null) return null;
                int q = Math.Max(4, s.Len / 4);
                return new BoxGoal(s.Mid - q, 0, s.Mid + q, ws + 8);
            }
        }
    }

    /// <summary>Whether the player is at the landmark, via the game's own biome/layer state.</summary>
    public static bool Arrived(string target, Player p)
    {
        float cx = p.Center.X / 16f, cy = p.Center.Y / 16f;
        bool surfaceY = cy < Main.worldSurface + 8;
        return target switch
        {
            "ocean_left" => p.ZoneBeach && cx < Main.maxTilesX / 2,
            "ocean_right" => p.ZoneBeach && cx > Main.maxTilesX / 2,
            "dungeon" => Vector2.Distance(p.Center / 16f, new Vector2(Main.dungeonX, Main.dungeonY)) <= 8f,
            "jungle" => p.ZoneJungle && surfaceY,
            "snow" => p.ZoneSnow && surfaceY,
            "desert" => p.ZoneDesert && surfaceY,
            "corruption" => p.ZoneCorrupt && surfaceY,
            "crimson" => p.ZoneCrimson && surfaceY,
            "evil" => (p.ZoneCorrupt || p.ZoneCrimson) && surfaceY,
            "underground" => cy > Main.worldSurface && cy <= Main.rockLayer,
            "cavern" => cy > Main.rockLayer && cy <= Main.UnderworldLayer,
            "underworld" => cy > Main.UnderworldLayer,
            "spawn" => Math.Abs(cx - Main.spawnTileX) < 4 && Math.Abs(cy - Main.spawnTileY) < 6,
            _ => false,
        };
    }

    /// <summary>Summary of the loaded world for the job's world_info.json.</summary>
    public static object Info()
    {
        int chests = 0, locked = 0;
        foreach (Chest c in Main.chest)
        {
            if (c == null) continue;
            chests++;
            if (Chest.IsLocked(c.x, c.y)) locked++;
        }
        var ore = new Dictionary<string, int>();
        int crystals = 0;
        for (int x = 0; x < Main.maxTilesX; x++)
        for (int y = 0; y < Main.maxTilesY; y++)
        {
            Tile t = Main.tile[x, y];
            if (!t.HasTile) continue;
            if (t.TileType == TileID.Heart && t.TileFrameX == 0 && t.TileFrameY == 0) crystals++;
            foreach (var kv in Resources.OreTiles)
                if (kv.Value == t.TileType) ore[kv.Key] = ore.GetValueOrDefault(kv.Key) + 1;
        }
        return new
        {
            size = new[] { Main.maxTilesX, Main.maxTilesY },
            seed = Main.ActiveWorldFileData?.Seed,
            spawn = new[] { Main.spawnTileX, Main.spawnTileY },
            dungeon = new[] { Main.dungeonX, Main.dungeonY },
            world_surface = Main.worldSurface,
            rock_layer = Main.rockLayer,
            underworld = Main.UnderworldLayer,
            ocean_level = WorldGen.oceanLevel,
            crimson = WorldGen.crimson,
            spans = Spans().Select(s => new { s.Biome, s.X0, s.X1, surface_y = SurfaceY(s.Mid) }).ToArray(),
            chests,
            chests_locked = locked,
            life_crystals = crystals,
            ore_tiles = ore,
        };
    }
}
