using Terraria;
using Terraria.ModLoader;

namespace BossPilot.Bosses.EvilDeer;

/// <summary>Counts pilot projectile hits on NPCs (diagnostics for aim and pierce quality).</summary>
public sealed class HitStats : GlobalNPC
{
    public static int Hits;
    public static long Damage;

    public override void OnHitByProjectile(NPC npc, Projectile projectile, NPC.HitInfo hit, int damageDone)
    {
        if (projectile.owner != Main.myPlayer) return;
        Hits++;
        Damage += damageDone;
    }
}
