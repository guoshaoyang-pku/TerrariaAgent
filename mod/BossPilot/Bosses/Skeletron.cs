using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using BossPilot.Common;
using BossPilot.Bosses.BeeSkelWof;

namespace BossPilot.Bosses;

/// <summary>
/// Skeletron (head 35 aiStyle 11, hands 36 aiStyle 12). Kernel facts used:
/// Main.IsItDay() forces ai[1] = 2 (1000 damage, 9999 defense) and a target more
/// than 2000 px away makes it despawn (ai[1] = 3); 1.4.4.9 has no "left the
/// dungeon" check in Classic. Head: ai[1] = 0 hovers with its top 250 px above
/// the pilot's top for 800 ticks, ai[1] = 1 spins toward the pilot at 1.5 px/tick
/// for 400 ticks (defense -10). Hands alternate (ai[3] staggered by 150): rise to
/// 200 px above the head then slam at the pilot at 18 px/tick, later swing out
/// to 500 px and sweep at 17 px/tick; both aimed at the pilot's center at launch.
/// Classic shoots no skulls. Dungeon spawns: ZoneDungeon needs 250 dungeon
/// bricks nearby, the pilot below worldSurface and a dungeon wall at its center;
/// before Skeletron is downed every dungeon spawn is a Dungeon Guardian
/// (spawnRate 10), so the arena sits where maxSpawns stays 5 (pilot top above
/// worldSurface*16 + 1080 px) and Calming (x0.6) makes it 5 &lt; Skeletron's 6 npc slots.
/// </summary>
public sealed class Skeletron : PilotPlan
{
    public override string Boss => "skeletron";

    // After Queen Bee: same ranged kit (Molten Fury + Jester's Arrows pierce the hands
    // and fly straight up at the head), Fossil armor, standard mobility accessories.
    private static readonly Kit.Tier MinPreboss = new(
        ItemID.DemonBow, ItemID.JestersArrow, 999,
        ItemID.FossilHelm, ItemID.FossilShirt, ItemID.FossilPants,
        [ItemID.HermesBoots, ItemID.CloudinaBottle],
        [BuffID.Ironskin, BuffID.Calm], Life: 240);

    private static readonly Kit.Tier StageDefault = new(
        ItemID.MoltenFury, ItemID.JestersArrow, 999,
        ItemID.FossilHelm, ItemID.FossilShirt, ItemID.FossilPants,
        [ItemID.HermesBoots, ItemID.CloudinaBalloon, ItemID.Aglet, ItemID.AnkletoftheWind, ItemID.LuckyHorseshoe],
        [BuffID.Swiftness, BuffID.Ironskin, BuffID.Regeneration, BuffID.Archery, BuffID.Calm], Life: 340, Potion: ItemID.HealingPotion);

    // One stage ahead (dungeon loot, pre-WoF): Hellwing Bow, Necro armor.
    private static readonly Kit.Tier StagePlus = new(
        ItemID.HellwingBow, ItemID.JestersArrow, 999,
        ItemID.NecroHelmet, ItemID.NecroBreastplate, ItemID.NecroGreaves,
        [ItemID.HermesBoots, ItemID.CloudinaBalloon, ItemID.Aglet, ItemID.AnkletoftheWind, ItemID.LuckyHorseshoe],
        [BuffID.Swiftness, BuffID.Ironskin, BuffID.Regeneration, BuffID.Archery, BuffID.Calm], Life: 400, Potion: ItemID.HealingPotion);

    public override void GrantLoadout(Player p, ScenarioConfig s)
    {
        Main.hardMode = false; // pre-hardmode boss; JobRunner never resets world progression
        Kit.Apply(p, s.Loadout switch { "min_preboss" => MinPreboss, "stage_plus" => StagePlus, _ => StageDefault }, s.MaxDurationSeconds);
    }

    /// <summary>Real dungeon column, floor deep enough for ZoneDungeon on every platform but above the 1.7x maxSpawns band.</summary>
    public override Point ArenaSite(ScenarioConfig s)
    {
        if (s.Biome != "dungeon") return ArenaSites.Default(s.Biome);
        int floor = (int)Main.worldSurface + 63;
        return new Point(Main.dungeonX, floor);
    }

    /// <summary>Dungeon walls behind the whole arena and a dungeon-brick floor (ZoneDungeon), first-kill world state.</summary>
    public override void DecorateArena(Point site, ScenarioConfig s)
    {
        // Attempts must not inherit a previous kill: every attempt is a first Skeletron.
        NPC.downedBoss3 = false;
        if (s.Biome != "dungeon") return;
        int half = s.Arena == "open_sky" ? 80 : 60;
        int height = s.Arena == "open_sky" ? 80 : 40;
        for (int x = site.X - half - 2; x <= site.X + half + 2; x++)
        {
            for (int y = site.Y - height - 2; y < site.Y; y++)
                if (WorldGen.InWorld(x, y, 2)) Main.tile[x, y].WallType = WallID.BlueDungeonUnsafe;
            for (int y = site.Y; y < site.Y + 3; y++)
                if (WorldGen.InWorld(x, y, 2) && x >= site.X - half && x < site.X + half) Main.tile[x, y].ResetToType(TileID.BlueDungeonBrick);
        }
    }

    /// <summary>
    /// SpawnBoss runs after this tick's NPC.CheckActive, so the next NPC.SpawnNPC would still
    /// count 0 nearby npc slots (and roll a Dungeon Guardian at spawnRate 10). In vanilla the
    /// Old Man summon happens during the player update, before CheckActive; credit the slots.
    /// </summary>
    public override void SpawnBoss(Player p, ScenarioConfig s)
    {
        ClearHostiles();
        int i = SpawnAbove(p, NPCID.SkeletronHead, 22);
        if (i < Main.maxNPCs) p.nearbyActiveNPCs += Main.npc[i].npcSlots;
    }

    private float halfWidth;

    protected override void Begin(Player p, FightContext ctx)
    {
        halfWidth = (ctx.Scenario.Arena == "open_sky" ? 76f : 57f) * 16f;
    }

    protected override Goal Think(Player p, NPC boss, FightContext ctx, out Vector2 aim, out bool shoot)
    {
        float cx = ctx.Site.X * 16f + 8f;
        var goal = new Goal { X = cx, Tol = 12 * 16, WX = 0.3f, MinX = cx - halfWidth, MaxX = cx + halfWidth, PreferY = float.NaN, KeepJump = true };
        aim = boss?.Center ?? p.Center + new Vector2(0, -200);
        shoot = boss != null;
        bool head = boss != null && boss.type == NPCID.SkeletronHead;
        AddGenericHazards(p, n => head && (n.whoAmI == boss.whoAmI || n.type == NPCID.SkeletronHand && (int)n.ai[1] == boss.whoAmI));
        if (!head) return goal;
        Planner.AddSkeletron(boss, 6f);

        if (boss.ai[1] == 1f)
        {
            // Spin: kite toward the side with more room, staying ~22 tiles ahead.
            float side = boss.Center.X < cx ? 1f : -1f;
            if (Math.Abs(p.Center.X - boss.Center.X) > 12 * 16) side = Math.Sign(p.Center.X - boss.Center.X);
            goal.X = boss.Center.X + side * 22 * 16;
            goal.Tol = 3 * 16;
            goal.WX = 1f;
        }
        aim = LeadAt(p, boss);
        return goal;
    }
}
