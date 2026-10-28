using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using BossPilot.Common;
using BossPilot.Bosses.Mechs;

namespace BossPilot.Bosses;

/// <summary>
/// The Twins (Retinazer 125 + Spazmatism 126; the fight ends when both are dead, as with
/// the Mechanical Eye, which summons both). Loadout: MechKit, Holy Arrows.
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
/// first: it hovers above the pilot, where the Daedalus volley lands best.
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
        planner.Threats.Clear();
        CollectThreats(p, accCap: 0.45f);
        float patrol = PatrolTarget(p, 240f);
        NPC ret = Find(NPCID.Retinazer), spaz = Find(NPCID.Spazmatism);
        if (ret != null) { ShotSchedule.Retinazer(ret, p, planner.Threats); ShotSchedule.TwinDashes(ret, planner.Threats); }
        if (spaz != null) { ShotSchedule.Spazmatism(spaz, p, planner.Threats); ShotSchedule.TwinDashes(spaz, planner.Threats); }
        planner.Goal = (s, t) => Goal(s, t, patrol, spaz);
        planner.Drive(p);
        Shoot(p, PickTarget(p, ret, spaz));
        Finish(p);
        return true;
    }

    private float Goal(PState s, int t, float patrol, NPC spaz)
    {
        float cost = ArenaCost(s, 48f, 0f, 26f);
        cost += Math.Abs(s.X - patrol) / 64f;
        if (spaz != null)
        {
            float d = Vector2.Distance(spaz.Center + (spaz.position - spaz.oldPosition) * t, new Vector2(s.X, s.Y));
            float keep = spaz.ai[0] >= 3f ? 460f : 260f;
            if (d < keep) cost += (keep - d) / 12f;
        }
        return cost;
    }

    // Kill order experiment switch (MECHS_TWIN_ORDER=spaz|ret); see the class summary for the default.
    private static readonly bool SpazFirst = Environment.GetEnvironmentVariable("MECHS_TWIN_ORDER") != "ret";

    private static NPC PickTarget(Player p, NPC ret, NPC spaz)
    {
        NPC first = SpazFirst ? spaz : ret, second = SpazFirst ? ret : spaz;
        if (first != null && first.Center.Y > p.Center.Y - 560f) return first;
        if (second != null && second.Center.Y > p.Center.Y - 560f) return second;
        return first ?? second;
    }
}
