using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using BossPilot.Common;
using BossPilot.Bosses.BeeSkelWof;

namespace BossPilot.Bosses;

/// <summary>
/// Wall of Flesh (mouth 113 aiStyle 27, eyes 114 aiStyle 28, The Hungry 115).
/// Kernel facts used: NPC.SpawnWOF(pos) needs pos below maxTilesY-205 and only
/// shifts the spawn while a player is within 1200 px; the wall then moves at a
/// fixed direction with |vx| = 1.5 (+0.25 &lt;75%, +0.4 &lt;50%, +0.5 &lt;25%, +0.6 &lt;10%)
/// and its Y centered between floor and ceiling scans clamped to
/// [UnderworldLayer+10, +80]. Player.WOFTongue hurts anything inside the
/// 140 px band at position.X+40 (-96 when moving right) at any height, and
/// "tongues" (pulls, later kills) a gross player that is behind the wall or above
/// UnderworldLayer. Eyes share the mouth's life (realLife) with 0 defense vs the
/// mouth's 12, fire EyeLaser (83, 3 updates/tick, 9-13 px/update) in bursts when
/// they have line of sight. The Hungry reach 300/500/700 px (x1.3 every other
/// 100 ticks) and turn into free-flying Hungry II when killed. WoF parts do not
/// count npc slots, so underworld spawns continue at maxSpawns 10*0.3 (Calming: 1).
/// </summary>
public sealed class WallOfFlesh : PilotPlan
{
    public override string Boss => "wall_of_flesh";

    // Post-Skeletron: Phoenix Blaster (dungeon Handgun + hellstone) with Meteor Shot
    // (pierces 2, no gravity, ~9600 px range), Necro armor (+15% ranged), the usual
    // mobility accessories plus Lucky Horseshoe; hellbridge potions: Obsidian Skin,
    // Water Walking (lava-walking on natural terrain), Swiftness, Ironskin,
    // Regeneration, Wrath, Calming.
    private static readonly Kit.Tier MinPreboss = new(
        ItemID.Minishark, ItemID.MusketBall, 999,
        ItemID.FossilHelm, ItemID.FossilShirt, ItemID.FossilPants,
        [ItemID.HermesBoots, ItemID.CloudinaBottle],
        [BuffID.ObsidianSkin, BuffID.Ironskin, BuffID.Calm], Life: 300, Potion: ItemID.HealingPotion);

    private static readonly Kit.Tier StageDefault = new(
        ItemID.PhoenixBlaster, ItemID.MeteorShot, 999,
        ItemID.NecroHelmet, ItemID.NecroBreastplate, ItemID.NecroGreaves,
        [ItemID.HermesBoots, ItemID.CloudinaBalloon, ItemID.Aglet, ItemID.AnkletoftheWind, ItemID.LuckyHorseshoe],
        [BuffID.ObsidianSkin, BuffID.WaterWalking, BuffID.Swiftness, BuffID.Ironskin, BuffID.Regeneration, BuffID.Wrath, BuffID.Calm],
        Life: 400, Potion: ItemID.HealingPotion);

    // One stage ahead (early hardmode): Clockwork Assault Rifle + Crystal Bullets, Cobalt armor.
    private static readonly Kit.Tier StagePlus = new(
        ItemID.ClockworkAssaultRifle, ItemID.CrystalBullet, 999,
        ItemID.CobaltHat, ItemID.CobaltBreastplate, ItemID.CobaltLeggings,
        [ItemID.HermesBoots, ItemID.CloudinaBalloon, ItemID.Aglet, ItemID.AnkletoftheWind, ItemID.LuckyHorseshoe],
        [BuffID.ObsidianSkin, BuffID.WaterWalking, BuffID.Swiftness, BuffID.Ironskin, BuffID.Regeneration, BuffID.Wrath, BuffID.Calm],
        Life: 500, Potion: ItemID.HealingPotion);

    private const int BridgeHalf = 420;

    public override void GrantLoadout(Player p, ScenarioConfig s)
    {
        // A kill flips the world to hardmode; every attempt is a first Wall of Flesh.
        Main.hardMode = false;
        Kit.Apply(p, s.Loadout switch { "min_preboss" => MinPreboss, "stage_plus" => StagePlus, _ => StageDefault }, s.MaxDurationSeconds);
    }

    private static (Point start, int dir, int reach) route;
    private static bool routeFound;

    private static (Point start, int dir, int reach) Route()
    {
        if (!routeFound)
        {
            route = Sites.HellRouteBySim();
            routeFound = true;
            Trace.Line($"natural route start {route.start} dir {route.dir} reach {route.reach} tiles");
        }
        return route;
    }

    /// <summary>
    /// Bridge: over the lava sea under spawn, below the wall's Y clamp (UnderworldLayer+80), so
    /// its eyes sit ~25-30 tiles above the runway. A laser aimed at the pilot then crosses the
    /// pilot's new x at v_x*h/39 px off its height while the pilot keeps running (h = eye height),
    /// which flat, eye-level bridges never give. Natural: start of the farthest simulated natural route.
    /// </summary>
    public override Point ArenaSite(ScenarioConfig s)
    {
        if (s.Arena == "natural") return Route().start;
        return new Point(Main.spawnTileX, Main.maxTilesY - 100);
    }

    private const int Corridor = 60;

    /// <summary>
    /// Extends the 400-tile runway to an 840-tile hellbridge, raises the ceiling to 60 tiles,
    /// drains every column above it up to the underworld roof and walls off both ends
    /// (otherwise lava pools cut loose by the clearing pour onto the runway).
    /// </summary>
    public override void DecorateArena(Point site, ScenarioConfig s)
    {
        if (s.Arena != "bridge") return;
        int x0 = site.X - BridgeHalf, x1 = site.X + BridgeHalf;
        for (int x = x0; x <= x1; x++)
        {
            bool cap = x <= x0 + 1 || x >= x1 - 1;
            for (int y = Main.UnderworldLayer; y < site.Y + 4; y++)
            {
                if (!WorldGen.InWorld(x, y, 2)) continue;
                Tile t = Main.tile[x, y];
                if (cap || y >= site.Y)
                {
                    if (!t.HasTile || !Main.tileSolid[t.TileType] || Main.tileSolidTop[t.TileType]) t.ResetToType(TileID.Stone);
                }
                else if (y >= site.Y - Corridor)
                {
                    // ClearTile, not KillTile: killing Hellstone below UnderworldLayer releases lava (WorldGen.KillTile).
                    if (y == site.Y - 6) t.ResetToType(TileID.Platforms);
                    else if (t.HasTile) t.ClearTile();
                }
            }
        }
        // The harness Flatten already killed Hellstone in the core, so drain everything last.
        for (int x = x0; x <= x1; x++)
        for (int y = Main.UnderworldLayer; y < site.Y + 4; y++)
            if (WorldGen.InWorld(x, y, 2)) Main.tile[x, y].LiquidAmount = 0;
        for (int x = x0 - 1; x <= x1 + 1; x++)
        for (int y = site.Y - Corridor - 2; y < site.Y + 5; y++)
            if (WorldGen.InWorld(x, y, 2)) WorldGen.SquareTileFrame(x, y, resetFrame: true);
    }

    private int fleeDir;
    private float minX, maxX;

    public override void SpawnBoss(Player p, ScenarioConfig s)
    {
        Point site = TrialRunner.Instance.Context.Site;
        int behind;
        if (s.Arena == "natural")
        {
            var r = Route();
            fleeDir = r.dir;
            behind = 200;
            int end = r.start.X + r.dir * r.reach;
            minX = Math.Min(r.start.X - r.dir * 20, end) * 16f;
            maxX = Math.Max(r.start.X - r.dir * 20, end) * 16f;
        }
        else
        {
            // Spawn at the bridge end farther from the world center so the run uses the whole bridge.
            fleeDir = site.X >= Main.maxTilesX / 2 ? -1 : 1;
            behind = BridgeHalf - 20;
            minX = (site.X - BridgeHalf + 3) * 16f;
            maxX = (site.X + BridgeHalf - 3) * 16f;
        }
        Vector2 pos = new((site.X - fleeDir * behind) * 16f, (site.Y - 8) * 16f);
        NPC.SpawnWOF(pos);
    }

    protected override Goal Think(Player p, NPC boss, FightContext ctx, out Vector2 aim, out bool shoot)
    {
        var goal = new Goal { X = p.Center.X, Tol = 16, WX = 0.5f, MinX = minX, MaxX = maxX, PreferY = float.NaN, KeepJump = true };
        NPC wof = Main.wofNPCIndex >= 0 ? Main.npc[Main.wofNPCIndex] : boss;
        aim = p.Center + new Vector2(-fleeDir * 300, 0);
        shoot = false;
        if (wof == null || !wof.active || wof.type != NPCID.WallofFlesh)
        {
            AddGenericHazards(p);
            return goal;
        }

        float hpFrac = (float)wof.life / wof.lifeMax;
        float vx = wof.velocity.X;
        // Player.WOFTongue contact band.
        float band = wof.position.X + 40f - (wof.direction > 0 ? 96f : 0f);
        Planner.AddBand(band, band + 140f, vx, 10f);
        AddGenericHazards(p);
        foreach (NPC n in Main.npc)
            if (n.active && n.type == NPCID.WallofFleshEye && Collision.CanHit(n.position, n.width, n.height, p.position, p.width, p.height))
                Planner.AddEye(n, wof, 5f);

        // Stay beyond the Hungry's reach (x1.3 surge) with a margin.
        float reach = hpFrac < 0.5f ? 700f : hpFrac < 0.75f ? 500f : 300f;
        float keep = reach * 1.3f + 260f;
        goal.X = wof.Center.X + wof.direction * keep;
        goal.Tol = 48f;
        goal.WX = 1.5f;
        if (ctx.Scenario.Arena == "bridge")
        {
            // Stay on the runway: the eyes' height above the pilot is what makes running dodge lasers.
            goal.PreferY = ctx.Site.Y * 16f - PlayerSim.H / 2f;
            goal.WY = 3f;
        }

        NPC threat = NearestThreat(p, 520f, n => n.type is not (NPCID.WallofFlesh or NPCID.WallofFleshEye or NPCID.TheHungry));
        if (threat != null)
        {
            aim = LeadAt(p, threat);
            shoot = true;
            return goal;
        }
        // Eyes take full damage (defense 0); pick one with a clear line, else the mouth.
        NPC target = wof;
        float bestD = float.MaxValue;
        foreach (NPC n in Main.npc)
        {
            if (!n.active || n.type != NPCID.WallofFleshEye) continue;
            if (!Collision.CanHit(p.Center, 1, 1, n.position, n.width, n.height)) continue;
            float d = Math.Abs(n.Center.Y - p.Center.Y);
            if (d < bestD) { bestD = d; target = n; }
        }
        aim = LeadAt(p, target);
        shoot = Collision.CanHit(p.Center, 1, 1, target.position, target.width, target.height);
        return goal;
    }
}
