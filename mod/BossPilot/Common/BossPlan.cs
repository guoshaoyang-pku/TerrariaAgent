using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;

namespace BossPilot.Common;

/// <summary>
/// Per-boss strategy: loadout, arena site, spawn and per-tick controls.
/// One subclass per boss lives in Bosses/ and is discovered by reflection,
/// keyed by its BossCatalog name. Without a plan the generic LoadoutGiver +
/// AutoPilot are used, so every boss stays runnable while plans are written.
/// </summary>
public abstract class BossPlan
{
    /// <summary>BossCatalog key, e.g. "king_slime".</summary>
    public abstract string Boss { get; }

    public BossCatalog.BossEntry Entry => BossCatalog.Bosses[Boss];

    /// <summary>Called once per attempt after the pilot is reset.</summary>
    public virtual void GrantLoadout(Player p, ScenarioConfig s) => LoadoutGiver.Grant(p, Entry, s.Loadout);

    /// <summary>Tile the arena floor is centered on; the pilot is teleported onto it.</summary>
    public virtual Point ArenaSite(ScenarioConfig s) => ArenaSites.Default(s.Biome);

    /// <summary>Extra building after ArenaBuilder (walls, campfires, temple bricks...).</summary>
    public virtual void DecorateArena(Point site, ScenarioConfig s) { }

    /// <summary>
    /// Spawns the boss. Default: 25 tiles above the pilot. (NPC.SpawnOnPlayer searches
    /// a screen-sized ring that a headless server never sizes, so it silently fails.)
    /// </summary>
    public virtual void SpawnBoss(Player p, ScenarioConfig s) => SpawnAbove(p, Entry.NpcId, 25);

    public static int SpawnAbove(Player p, int type, int tiles)
    {
        int i = NPC.NewNPC(new Terraria.DataStructures.EntitySource_BossSpawn(p), (int)p.Center.X, (int)p.Center.Y - tiles * 16, type);
        if (i < Main.maxNPCs) Main.npc[i].target = p.whoAmI;
        return i;
    }

    /// <summary>
    /// Sets the pilot's controls for this tick (called from ModPlayer.SetControls).
    /// Return false to fall back to AutoPilot for this tick.
    /// </summary>
    public virtual bool Control(Player p, NPC boss, FightContext ctx) => false;

    private static Dictionary<string, BossPlan> plans;

    public static BossPlan For(string boss)
    {
        plans ??= typeof(BossPlan).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(BossPlan).IsAssignableFrom(t))
            .Select(t => (BossPlan)Activator.CreateInstance(t))
            .ToDictionary(p => p.Boss);
        return plans.GetValueOrDefault(boss);
    }

    internal static void Unload() => plans = null;
}

/// <summary>Per-tick fight info handed to BossPlan.Control.</summary>
public sealed class FightContext
{
    public int Tick { get; internal set; }
    public ScenarioConfig Scenario { get; internal set; }
    public Point Site { get; internal set; }
    /// <summary>Scratch state a plan may keep across ticks of one attempt.</summary>
    public Dictionary<string, float> Memory { get; } = [];

    /// <summary>Points the cursor (and so item use) at a world position.</summary>
    public static void Aim(Vector2 world)
    {
        Main.mouseX = (int)(world.X - Main.screenPosition.X);
        Main.mouseY = (int)(world.Y - Main.screenPosition.Y);
    }
}

/// <summary>Default arena locations per biome, in tile coordinates of the floor row.</summary>
public static class ArenaSites
{
    public static Point Default(string biome)
    {
        int spawnX = Main.spawnTileX;
        return biome switch
        {
            // Ocean = within 380 tiles of a world edge, above the surface line.
            "ocean" => new Point(160, SurfaceAt(160)),
            "underworld" => new Point(spawnX, Main.maxTilesY - 110),
            "space" => new Point(spawnX, (int)(Main.worldSurface * 0.25)),
            "underground_jungle" => new Point(spawnX, (int)Main.rockLayer + 60),
            "dungeon" => new Point(Main.dungeonX, Main.dungeonY + 40),
            _ => new Point(spawnX, SurfaceAt(spawnX)),
        };
    }

    /// <summary>
    /// First solid tile from the top at column x. Platforms (solid-top) are skipped: otherwise the
    /// next attempt is built on the previous arena's top platform row, over the cleared void.
    /// </summary>
    public static int SurfaceAt(int x)
    {
        for (int y = 50; y < Main.worldSurface + 50; y++)
        {
            Tile t = Main.tile[x, y];
            if (t.HasTile && Main.tileSolid[t.TileType] && !Main.tileSolidTop[t.TileType]) return y;
        }
        return (int)Main.worldSurface;
    }
}
