using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using BossPilot.Common;
using BossPilot.Bosses.Mechs;

namespace BossPilot.Bosses;

/// <summary>
/// Skeletron Prime (head 127; Cannon 128, Saw 129, Vice 130, Laser 131). Killing the
/// head ends the fight: every arm checks its head each tick and dies with it (aiStyle
/// 33-36). Loadout: MechKit (Ichor/Cursed Arrows), all damage on the head.
///
/// Kernel facts (aiStyle 32, Classic): the head hovers 200-500 px above the pilot within
/// ±100 px (accel 0.1, max 8) for 600 ticks, then spins toward the pilot at only
/// 2 px/tick for 400 ticks (defense and contact damage doubled) while the arms switch to
/// chasing: Saw at 7 (accel 0.05), Vice re-lunging at 12 whenever slower than 2, Cannon
/// and Laser firing three to four times faster. Idle arms swing on timers: Saw rises and
/// strikes at 22, Vice strikes 5x at 20 then 5x sideways at 17; the Cannon lobs bombs
/// (128 px blast on landing), the Laser fires 8 px/tick death lasers. At day it chases
/// at up to 32 px/tick with 1000 damage - fights are at night.
///
/// Control: MPC planner over head, arms and projectiles, patrolling so the hovering head
/// and its arms trail behind; extra standoff from the head while it spins.
/// </summary>
public sealed class SkeletronPrime : MechPlan
{
    public override string Boss => "skeletron_prime";

    public override void SpawnBoss(Player p, ScenarioConfig s) => SpawnAbove(p, NPCID.SkeletronPrime, 32);

    public override bool Control(Player p, NPC boss, FightContext ctx)
    {
        this.ctx = ctx;
        RefreshBiomes(p, ctx);
        planner.Threats.Clear();
        CollectThreats(p, accCap: 0.5f);
        float patrol = PatrolTarget(p, 240f);
        NPC head = Find(NPCID.SkeletronPrime), laser = Find(NPCID.PrimeLaser);
        if (laser != null) ShotSchedule.PrimeLaser(laser, planner.Threats);
        NPC saw = Find(NPCID.PrimeSaw), vice = Find(NPCID.PrimeVice), cannon = Find(NPCID.PrimeCannon);
        if (saw != null) ShotSchedule.PrimeSaw(saw, planner.Threats);
        if (vice != null) ShotSchedule.PrimeVice(vice, planner.Threats);
        if (cannon != null) ShotSchedule.PrimeCannon(cannon, planner.Threats);
        planner.Goal = (s, t) => Goal(s, t, patrol, head);
        planner.Drive(p);
        Shoot(p, Intruder(p) ?? (head != null && head.Center.Y > p.Center.Y - 560f ? head : null));
        Finish(p);
        return true;
    }

    private float Goal(PState s, int t, float patrol, NPC head)
    {
        float cost = ArenaCost(s, 48f, 0f, 26f);
        cost += Math.Abs(s.X - patrol) / 64f;
        if (head != null)
        {
            Vector2 hp = head.Center + (head.position - head.oldPosition) * t;
            float d = Vector2.Distance(hp, new Vector2(s.X, s.Y));
            float keep = head.ai[1] == 1f ? 360f : 220f;
            if (d < keep) cost += (keep - d) / 12f;
        }
        return cost;
    }
}
