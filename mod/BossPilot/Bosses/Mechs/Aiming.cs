using System;
using Microsoft.Xna.Framework;
using Terraria;

namespace BossPilot.Bosses.Mechs;

/// <summary>Lead aiming for the weapons in the mech kit.</summary>
public static class Aiming
{
    /// <summary>
    /// Daedalus Stormbow (Player.ItemCheck_Shoot, type 3029): 3-4 arrows spawn at
    /// ~cursor X ± 100, at pilot.Center.Y - 600 - 150k, aimed at the cursor (always
    /// downward). Holy/Jester arrows here fly at 20 px/tick (speed cap with Archery +
    /// Magic Quiver), so the cursor is put where the target will be on arrival.
    /// </summary>
    public static Vector2 DaedalusLead(Player p, Vector2 c, Vector2 v, Vector2 a = default, float speed = 20f)
    {
        Vector2 aim = c;
        for (int i = 0; i < 3; i++)
        {
            Vector2 spawn = new(aim.X, p.Center.Y - 675f);
            float t = Math.Min(Vector2.Distance(aim, spawn) / speed, 90f);
            float ta = Math.Min(t, 25f);
            aim = c + v * t + a * (0.5f * ta * ta);
        }
        return aim;
    }

    /// <summary>
    /// Effective px/tick of the first arrow stack: (12.5 + ammo speed) * 1.1 (Magic Quiver)
    /// * 1.2 (Archery, capped at 20), times 1 + extraUpdates (Jester's Arrow has one).
    /// </summary>
    public static float ArrowSpeed(Player p)
    {
        for (int i = 54; i < 58; i++)
        {
            int type = p.inventory[i].type;
            if (type == Terraria.ID.ItemID.JestersArrow) return 34f;
            if (type > 0) return 20f;
        }
        return 20f;
    }

    /// <summary>Straight-shot lead for a projectile of the given speed from the pilot.</summary>
    public static Vector2 DirectLead(Player p, Vector2 c, Vector2 v, float speed)
    {
        Vector2 aim = c;
        for (int i = 0; i < 3; i++)
        {
            float t = Math.Min(Vector2.Distance(aim, p.Center) / speed, 60f);
            aim = c + v * t;
        }
        return aim;
    }
}
