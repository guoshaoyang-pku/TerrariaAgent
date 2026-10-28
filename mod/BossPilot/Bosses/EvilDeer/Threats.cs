using Microsoft.Xna.Framework;
using Terraria;

namespace BossPilot.Bosses.EvilDeer;

/// <summary>Generic hazard sources shared by the plans.</summary>
public static class Threats
{
    /// <summary>
    /// Hostile projectiles extrapolated at constant velocity (velocity is per update,
    /// so it is scaled by 1 + extraUpdates). Types in <paramref name="skip"/> are modelled by the plan.
    /// </summary>
    public static void AddProjectiles(HazardField f, params int[] skip)
    {
        foreach (Projectile pr in Main.projectile)
        {
            if (!pr.active || !pr.hostile || pr.damage <= 0) continue;
            if (System.Array.IndexOf(skip, pr.type) >= 0) continue;
            int ticks = System.Math.Min(HazardField.Horizon, pr.timeLeft / (pr.extraUpdates + 1) + 1);
            f.AddLinear(new Box(pr.position.X, pr.position.Y, pr.width, pr.height), pr.velocity * (pr.extraUpdates + 1), ticks);
        }
    }

    /// <summary>Hostile NPCs that are not part of the boss, extrapolated at constant velocity.</summary>
    public static void AddStrays(HazardField f, System.Func<NPC, bool> isBoss)
    {
        foreach (NPC n in Main.npc)
        {
            if (!n.active || n.friendly || n.damage <= 0 || n.townNPC || isBoss(n)) continue;
            f.AddLinear(new Box(n.position.X, n.position.Y, n.width, n.height), n.velocity);
        }
    }
}
