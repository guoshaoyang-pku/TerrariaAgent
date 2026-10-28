using System.IO;
using Terraria;
using Terraria.ModLoader;
using BossPilot.Common;

namespace BossPilot.Bosses.Mechs;

/// <summary>
/// Appends one line per hit taken during a mech trial to &lt;out&gt;/mech_hits.jsonl:
/// what hit the pilot and what the planner expected, for offline tuning.
/// </summary>
public sealed class HitLog : ModPlayer
{
    public override void OnHurt(Player.HurtInfo info)
    {
        TrialRunner t = TrialRunner.Instance;
        if (t.Current != TrialRunner.Phase.Fighting || Player.whoAmI != Main.myPlayer || t.Plan is not MechPlan plan) return;
        string dir = Common.Headless.HeadlessSim.OutDir;
        if (dir == null) return;
        var src = info.DamageSource;
        int npcType = src.SourceNPCIndex >= 0 && src.SourceNPCIndex < Main.maxNPCs ? Main.npc[src.SourceNPCIndex].type : -1;
        int pi = src.SourceProjectileLocalIndex;
        Projectile pr = pi >= 0 && pi < Main.maxProjectiles ? Main.projectile[pi] : null;
        string line = System.Text.Json.JsonSerializer.Serialize(new
        {
            scenario = t.Scenario.Name,
            tick = t.FightTicks,
            dmg = info.Damage,
            npc = npcType,
            proj = src.SourceProjectileType,
            x = Player.Center.X / 16f, y = Player.Center.Y / 16f,
            vx = Player.velocity.X, vy = Player.velocity.Y,
            wing = Player.wingTime,
            cost = plan.LastCost,
            predicted = plan.LastFirstHit,
            history = plan.HitHistory,
            free = plan.FreeHistory,
            tags = plan.TagHistory,
            proj_left = pr?.timeLeft ?? -1,
            proj_d = pr != null ? (pr.Center - Player.Center).Length() : -1,
            proj_v = pr != null ? pr.velocity.Length() * (1 + pr.extraUpdates) : -1,
        });
        File.AppendAllText(Path.Combine(dir, "mech_hits.jsonl"), line + "\n");
    }
}
