using System;
using Microsoft.Xna.Framework;
using Terraria;
using BossPilot.Common;
using Terraria.ID;

namespace BossPilot.Bosses.EvilDeer;

/// <summary>
/// Aiming and trigger handling for the held ranged weapon: intercept lead for
/// constant-velocity targets, an optional arrow-drop correction (AI_001 adds
/// 0.1 px/update² after 15 updates), and press/release for weapons without
/// autoReuse.
/// </summary>
public sealed class Gunner
{
    /// <summary>Projectile speed in px per tick (velocity times updates per tick).</summary>
    public float Speed = 14f;
    /// <summary>Updates per tick of the fired projectile (1 + extraUpdates).</summary>
    public int Updates = 2;
    public bool ArrowDrop;
    private bool pressedLast;
    private int lastAnim;
    /// <summary>Uses of the weapon so far (diagnostics).</summary>
    public int Shots;

    /// <summary>Configures speed from the held weapon and the ammo it will use.</summary>
    public void Configure(Player p)
    {
        Item w = p.HeldItem;
        float speed = w.shootSpeed;
        int proj = w.shoot;
        Item ammo = p.ChooseAmmo(w);
        if (ammo != null && !ammo.IsAir)
        {
            speed += ammo.shootSpeed;
            if (ammo.shoot > ProjectileID.None) proj = ammo.shoot;
        }
        int extra = proj > ProjectileID.None ? Terraria.ID.ContentSamples.ProjectilesByType[proj].extraUpdates : 0;
        bool arrow = proj > ProjectileID.None && Terraria.ID.ContentSamples.ProjectilesByType[proj].arrow;
        if (arrow && p.archery) speed *= 1.2f;
        Updates = extra + 1;
        Speed = speed * Updates;
        ArrowDrop = arrow;
    }

    /// <summary>Lead point for a target at <paramref name="pos"/> moving at <paramref name="vel"/>.</summary>
    public Vector2 Lead(Vector2 from, Vector2 pos, Vector2 vel)
    {
        Vector2 d = pos - from;
        float a = vel.LengthSquared() - Speed * Speed;
        float b = 2f * Vector2.Dot(d, vel);
        float c = d.LengthSquared();
        float t;
        if (Math.Abs(a) < 1e-4f) t = b != 0f ? -c / b : 0f;
        else
        {
            float disc = b * b - 4f * a * c;
            if (disc < 0f) t = d.Length() / Speed;
            else
            {
                float s = (float)Math.Sqrt(disc);
                float t1 = (-b - s) / (2f * a), t2 = (-b + s) / (2f * a);
                t = Math.Min(t1, t2) > 0f ? Math.Min(t1, t2) : Math.Max(t1, t2);
            }
        }
        if (t < 0f || float.IsNaN(t)) t = d.Length() / Speed;
        Vector2 aim = pos + vel * t;
        if (ArrowDrop)
        {
            // Drop of an arrow after t ticks: gravity 0.1/update² starting after 15 updates.
            float n = Math.Max(0f, t * Updates - 15f);
            aim.Y -= 0.05f * n * n;
        }
        return aim;
    }

    /// <summary>Aims at a world point and holds (or pulses) the trigger.</summary>
    public void Fire(Player p, Vector2 at, bool fire)
    {
        if (p.itemAnimation > lastAnim) Shots++;
        lastAnim = p.itemAnimation;
        Vector2 dir = at - p.Center;
        if (dir.LengthSquared() < 1f) dir = new Vector2(p.direction, 0f);
        dir.Normalize();
        // Direction is all item use reads; stay on-screen so input clamping cannot bend it.
        FightContext.Aim(p.Center + dir * 160f);
        if (!fire)
        {
            p.controlUseItem = false;
            pressedLast = false;
            return;
        }
        if (p.HeldItem.autoReuse) p.controlUseItem = true;
        else
        {
            bool press = !pressedLast && p.itemAnimation == 0;
            p.controlUseItem = press;
            pressedLast = press;
        }
    }
}
