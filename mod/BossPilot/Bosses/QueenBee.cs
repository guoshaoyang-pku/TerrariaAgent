using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using BossPilot.Common;
using BossPilot.Bosses.BeeSkelWof;

namespace BossPilot.Bosses;

/// <summary>
/// Queen Bee (NPC 222, aiStyle 43). Kernel facts used (NPC.cs, aiStyle 43):
/// enrage term num633 = +1 while her position is above Main.worldSurface and +1
/// while the target is outside ZoneJungle (faster charges 12+7, stingers 8+7,
/// more charges); phases ai[0]: 0 = 3 horizontal charges at 12 px/tick aimed at
/// the pilot once |dy| &lt; 20 (40 enraged), 2 -> 1 = hover ~400 px away and
/// release a bee (210/211, homing at 5 px/tick, accel 0.1) every 40 ticks x6,
/// 3 = hover 300 px above and drop stingers (719, speed 8, gravity 0.1 after
/// 15 ticks) every 40 ticks for 800 ticks; she despawns only if the target dies
/// or is 3000+ px away (ai[0] = 4 is a catch-up dash).
/// </summary>
public sealed class QueenBee : PilotPlan
{
    public override string Boss => "queen_bee";

    // Queen Bee comes after the Eye and the evil boss: hellstone (Molten Fury) is
    // mineable, Jester's Arrows pierce and fly straight (no gravity), which also
    // clears bee swarms in line with her. Fossil armor is the early ranged set;
    // Hermes Boots + Cloud in a Balloon + Aglet + Anklet are the usual mobility
    // kit; Calming keeps jungle spawns off while she occupies 7 npc slots.
    private static readonly Kit.Tier MinPreboss = new(
        ItemID.DemonBow, ItemID.JestersArrow, 999,
        ItemID.FossilHelm, ItemID.FossilShirt, ItemID.FossilPants,
        [ItemID.HermesBoots, ItemID.CloudinaBottle],
        [BuffID.Ironskin, BuffID.Calm], Life: 200);

    private static readonly Kit.Tier StageDefault = new(
        ItemID.MoltenFury, ItemID.JestersArrow, 999,
        ItemID.FossilHelm, ItemID.FossilShirt, ItemID.FossilPants,
        [ItemID.HermesBoots, ItemID.CloudinaBalloon, ItemID.Aglet, ItemID.AnkletoftheWind, ItemID.LuckyHorseshoe],
        [BuffID.Swiftness, BuffID.Ironskin, BuffID.Regeneration, BuffID.Archery, BuffID.Calm], Life: 300, Potion: ItemID.HealingPotion);

    // One stage ahead (post-Skeletron): Hellwing Bow from a Shadow Chest, autofire at 13 ticks.
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

    /// <summary>The real jungle: surface jungle for "jungle", the densest underground jungle for "underground_jungle".</summary>
    public override Point ArenaSite(ScenarioConfig s) => s.Biome switch
    {
        "jungle" => Sites.SurfaceJungle(natural: s.Arena == "natural"),
        "underground_jungle" => Sites.UndergroundJungle(),
        _ => ArenaSites.Default(s.Biome),
    };

    /// <summary>
    /// Abeemination / larva spawns go through NPC.SpawnOnPlayer, which picks a spot off-screen
    /// around the pilot; the headless server cannot size that ring, so place her the same way:
    /// about a screen to one side and above.
    /// </summary>
    public override void SpawnBoss(Player p, ScenarioConfig s)
    {
        float side = Main.rand.NextBool() ? 1f : -1f;
        int i = NPC.NewNPC(new Terraria.DataStructures.EntitySource_BossSpawn(p), (int)(p.Center.X + side * 44 * 16), (int)p.Center.Y - 14 * 16, NPCID.QueenBee);
        if (i < Main.maxNPCs)
        {
            Main.npc[i].target = p.whoAmI;
            p.nearbyActiveNPCs += Main.npc[i].npcSlots; // counted before the next SpawnNPC, as in vanilla
        }
    }

    private float halfWidth;
    private int patrolDir = 1;

    protected override void Begin(Player p, FightContext ctx)
    {
        halfWidth = ctx.Scenario.Arena switch { "enclosed" => 47f, "natural" => 55f, _ => 57f } * 16f;
    }

    protected override Goal Think(Player p, NPC boss, FightContext ctx, out Vector2 aim, out bool shoot)
    {
        float cx = ctx.Site.X * 16f + 8f;
        bool natural = ctx.Scenario.Arena == "natural";
        var goal = new Goal { X = cx, Tol = 20 * 16, WX = 0.3f, MinX = cx - halfWidth, MaxX = cx + halfWidth, PreferY = float.NaN, KeepJump = true };
        aim = boss?.Center ?? p.Center + new Vector2(0, -200);
        shoot = boss != null;
        bool qb = boss != null && boss.type == NPCID.QueenBee;
        AddGenericHazards(p, n => qb && n.whoAmI == boss.whoAmI);
        if (!qb) return goal;

        int enrage = (boss.position.Y / 16f < Main.worldSurface ? 1 : 0) + (p.ZoneJungle ? 0 : 1);
        // Phase clocks (Classic): stingers last num668*num678 ticks, bees end after the 6th bee.
        int left = int.MaxValue;
        if (boss.ai[0] == 3f) left = (int)((40 - 5 * enrage) * (20 - 5 * enrage) - boss.ai[1]) + 1;
        else if (boss.ai[0] == 1f) { int every = 40 - 18 * enrage; left = (int)((5 - boss.ai[2]) * (every + 1) + (every - boss.ai[1])) + 1; }
        else if (boss.ai[0] == -1f) left = 1;
        if (boss.ai[0] == 0f) Planner.AddQueenBee(boss, enrage, 6f);
        else if (left < Planner.Horizon) Planner.AddQueenBee(boss, enrage, 6f, Math.Max(left, 1));
        else Planner.AddNpc(boss, 8f);

        // While she lines up a charge, stand ~25 tiles off so the dash has to cover distance.
        float dx = p.Center.X - boss.Center.X;
        if (boss.ai[0] == 0f && boss.ai[1] % 2f == 0f)
        {
            float away = dx >= 0 ? 1 : -1;
            float target = boss.Center.X + away * 25 * 16;
            if (target < goal.MinX + 48 || target > goal.MaxX - 48) target = boss.Center.X - away * 25 * 16;
            goal.X = MathHelper.Clamp(target, goal.MinX + 48, goal.MaxX - 48);
            goal.Tol = 5 * 16;
            goal.WX = 1f;
        }
        else if (boss.ai[0] == 3f)
        {
            // Stinger phase: keep running edge to edge on the ground; a stinger aimed from ~300 px
            // above then lands ~v*300/speed px behind the pilot, outside its +-41 px spread.
            // Natural terrain: stay on the flat stretch around the site (in a pit she loses sight and dives).
            float lo = natural ? cx - 14 * 16 : goal.MinX + 64, hi = natural ? cx + 14 * 16 : goal.MaxX - 64;
            float edge = patrolDir > 0 ? hi : lo;
            if (Math.Abs(p.Center.X - edge) < 4 * 16) { patrolDir = -patrolDir; edge = patrolDir > 0 ? hi : lo; }
            goal.X = edge;
            goal.Tol = 2 * 16;
            goal.WX = 1.2f;
            if (!natural) { goal.PreferY = ctx.Site.Y * 16f - PlayerSim.H / 2f; goal.WY = 0.6f; }
        }
        else if (Math.Abs(dx) < 10 * 16)
        {
            float away = dx >= 0 ? 1 : -1;
            float target = boss.Center.X + away * 14 * 16;
            if (target < goal.MinX + 64 || target > goal.MaxX - 64) target = boss.Center.X - away * 14 * 16;
            goal.X = target;
            goal.Tol = 4 * 16;
            goal.WX = 0.8f;
        }

        NPC bee = NearestThreat(p, 420f, n => n.type is NPCID.Bee or NPCID.BeeSmall);
        aim = bee != null ? LeadAt(p, bee) : LeadAt(p, boss);
        return goal;
    }
}
