using Terraria;
using Terraria.ModLoader;
using BossPilot.Common;

namespace BossPilot.Bosses.EvilDeer;

/// <summary>Logs what hit the pilot during one of these plans' fights, with the planner's view.</summary>
public sealed class HitLog : ModPlayer
{
    public override void OnHurt(Player.HurtInfo info)
    {
        TrialRunner t = TrialRunner.Instance;
        if (t.Current != TrialRunner.Phase.Fighting || Player.whoAmI != Main.myPlayer) return;
        if (t.Plan is not (Bosses.EaterOfWorlds or Bosses.BrainOfCthulhu or Bosses.Deerclops)) return;
        var src = info.DamageSource;
        string what = src.SourceNPCIndex >= 0 ? $"npc {Main.npc[src.SourceNPCIndex].type} at ({Main.npc[src.SourceNPCIndex].Center.X / 16f:F1},{Main.npc[src.SourceNPCIndex].Center.Y / 16f:F1}) v={Main.npc[src.SourceNPCIndex].velocity}"
                    : src.SourceProjectileType > 0 ? $"proj {src.SourceProjectileType}"
                    : $"other {src.SourceOtherIndex}";
        Planner pl = Planner.Active;
        Trace.Line($"[hit] t={t.FightTicks} dmg={info.Damage} by {what} pilot=({Player.Center.X / 16f:F1},{Player.Center.Y / 16f:F1}) v={Player.velocity} " +
                   $"predicted={pl?.PredictedHit} chosen=({pl?.Chosen.A},{pl?.Chosen.B},{pl?.Chosen.JumpAt},{pl?.Chosen.Down})");
    }
}
