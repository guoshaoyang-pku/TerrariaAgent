using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using BossPilot.Common;
using BossPilot.Bosses.Mechs;

namespace BossPilot.Bosses;

/// <summary>
/// The Twins (Retinazer 125 + Spazmatism 126; the fight ends when both are dead, as with
/// the Mechanical Eye, which summons both). Loadout: MechKit (Ichor/Cursed Arrows).
///
/// Kernel facts (aiStyle 30/31, Classic): Retinazer hovers 300 px above and 300 px to the
/// side, firing a 9 px/tick laser (x3 extra updates) every 60 ticks while above the
/// pilot; every 600 ticks it dashes 4x at 12 px/tick. Spazmatism hovers 400 px to the
/// side at the pilot's height, firing 12 px/tick cursed flames every 60 ticks; every 600
/// ticks it dashes 10x at 13. Below 40% life each transforms: Retinazer alternates 300
/// ticks above (death laser every ~180) with 180 ticks at the side (laser every ~60);
/// Spazmatism chases at 4 px/tick to 180 px beside the pilot with a flamethrower
/// (~360 px reach, 66 px box) and dashes 6x at 14 for 400 ticks. Both flee at day.
///
/// Control: MPC planner over all of that, with a patrol goal so aimed shots trail behind,
/// and a standoff from Spazmatism's flamethrower once it has transformed. Retinazer dies
/// first: its first-form laser is the only noisy (unschedulable) shot, its second form's
/// lasers are exact, and Spazmatism's second form then never overlaps a living Retinazer.
/// </summary>
public sealed class Twins : MechPlan
{
    public override string Boss => "twins";

    public override void SpawnBoss(Player p, ScenarioConfig s)
    {
        SpawnAt(p, NPCID.Retinazer, -30, -35);
        SpawnAt(p, NPCID.Spazmatism, 30, -35);
    }

    private static void SpawnAt(Player p, int type, int dx, int dy)
    {
        int i = NPC.NewNPC(new Terraria.DataStructures.EntitySource_BossSpawn(p), (int)p.Center.X + dx * 16, (int)p.Center.Y + dy * 16, type);
        if (i < Main.maxNPCs) Main.npc[i].target = p.whoAmI;
    }

    public override bool Control(Player p, NPC boss, FightContext ctx)
    {
        this.ctx = ctx;
        RefreshBiomes(p, ctx);
        planner.Threats.Clear();
        CollectThreats(p, accCap: 0.45f);
        float patrol = PatrolTarget(p, 240f);
        NPC ret = Find(NPCID.Retinazer), spaz = Find(NPCID.Spazmatism);
        if (ret != null) { ShotSchedule.Retinazer(ret, p, planner.Threats); ShotSchedule.TwinDashes(ret, planner.Threats); }
        if (spaz != null)
        {
            ShotSchedule.Spazmatism(spaz, p, planner.Threats);
            ShotSchedule.TwinDashes(spaz, planner.Threats);
            ShotSchedule.SpazFlames(spaz, planner.Threats);
        }
        // Spazmatism transforming (ai[0] 1, 2) or flaming (ai[0] 3, ai[1] 0): run for the end of the
        // arena with the most room on our side of it; it only chases at 4 px/tick.
        bool flee = spaz != null && spaz.ai[0] >= 1f && (spaz.ai[0] < 3f || spaz.ai[1] == 0f);
        if (flee)
        {
            (float left, float right) = MechKit.ArenaSpan(ctx);
            float near = p.Center.X < spaz.Center.X ? left + 120f : right - 120f;
            float far = p.Center.X < spaz.Center.X ? right - 120f : left + 120f;
            patrol = Math.Abs(near - spaz.Center.X) > 700f ? near : far;
        }
        planner.Goal = (s, t) => Goal(s, t, patrol, spaz, flee);
        planner.Drive(p);
        Shoot(p, Intruder(p) ?? PickTarget(p, ret, spaz));
        Finish(p);
        return true;
    }

    private float Goal(PState s, int t, float patrol, NPC spaz, bool flee)
    {
        // Space has a quarter of the gravity: anything above the floor takes ages to come down.
        bool space = ctx.Scenario.Biome == "space";
        float cost = ArenaCost(s, 48f, 0f, space ? 5f : 12f) * (space ? 2f : 1f);
        cost += Math.Abs(s.X - patrol) / (flee ? 24f : 64f);
        if (spaz != null)
        {
            float d = Vector2.Distance(spaz.Center + (spaz.position - spaz.oldPosition) * t, new Vector2(s.X, s.Y));
            float keep = flee ? 560f : 260f;
            if (d < keep) cost += (keep - d) / (flee ? 8f : 20f);
        }
        return cost;
    }

    // Kill order experiment switch (MECHS_TWIN_ORDER=spaz|ret); see the class summary for the default.
    private static readonly bool SpazFirst = Environment.GetEnvironmentVariable("MECHS_TWIN_ORDER") == "spaz";

    private static NPC PickTarget(Player p, NPC ret, NPC spaz)
    {
        NPC first = SpazFirst ? spaz : ret, second = SpazFirst ? ret : spaz;
        if (first != null && first.Center.Y > p.Center.Y - 560f) return first;
        if (second != null && second.Center.Y > p.Center.Y - 560f) return second;
        return first ?? second;
    }
}
