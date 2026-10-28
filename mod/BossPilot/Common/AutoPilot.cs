using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace BossPilot.Common;

/// <summary>
/// General-purpose fight controller (v0): per-tick reactive pilot that keeps a
/// range band from the boss, sidesteps incoming hostile projectiles by
/// crossing their velocity line, jumps when horizontally blocked, and holds
/// attack on the boss. Runs on native player controls, 60 Hz, no rendering
/// dependency. Per-boss tuned controllers replace this later; this is the
/// floor that should already survive simple fights.
/// </summary>
public sealed class AutoPilot : ModPlayer
{
    public static bool Enabled = true;

    // Tuning knobs (world units: 1 tile = 16 px).
    private const float PreferredRange = 24f * 16;   // keep ~24 tiles from boss
    private const float RangeSlack = 8f * 16;
    private const float ThreatHorizon = 90f;          // ticks ahead a projectile matters
    private const float ThreatRadius = 3.5f * 16;     // personal space
    private const float MaxSpeed = 11f;

    private int stuckTicks;
    private int jumpCooldown;
    private float lastX;

    // SetControls runs after vanilla input processing, so our writes survive
    // the frame. (PreUpdate writes get overwritten by PlayerInput.)
    public override void SetControls()
    {
        if (!Enabled || TrialRunner.Instance.Current != TrialRunner.Phase.Fighting) return;
        if (Player.dead) return;

        NPC boss = FindBoss();
        Vector2 pos = Player.Center;
        Vector2 vel = Player.velocity;

        // --- Threat scan: nearest hostile projectile predicted to pass close.
        Vector2 dodge = Vector2.Zero;
        float worst = float.MaxValue;
        foreach (Projectile pr in Main.projectile)
        {
            if (!pr.active || !pr.hostile || pr.damage <= 0) continue;
            TryThreat(pr.Center, pr.velocity, Math.Max(pr.width, pr.height) * 0.5f, ref dodge, ref worst, pos);
        }
        // The boss body is itself the main threat: predict its landing point
        // (jumping bosses like King Slime) with a generous personal radius.
        if (boss != null)
            TryThreat(boss.Center, boss.velocity, Math.Max(boss.width, boss.height) * 0.5f + 4f * 16,
                ref dodge, ref worst, pos);

        // --- Range keeping vs boss.
        float moveX = 0f, moveY = 0f;
        if (boss != null)
        {
            Vector2 toBoss = boss.Center - pos;
            float dist = toBoss.Length();
            Vector2 dir = dist > 0.01f ? toBoss / dist : Vector2.Zero;
            if (dist > PreferredRange + RangeSlack) { moveX += dir.X; moveY += dir.Y * 0.4f; }
            else if (dist < PreferredRange - RangeSlack) { moveX -= dir.X; moveY -= dir.Y * 0.4f; }

            // Attack: aim and hold use.
            Main.mouseX = (int)(boss.Center.X - Main.screenPosition.X);
            Main.mouseY = (int)(boss.Center.Y - Main.screenPosition.Y);
            Player.controlUseItem = true;
        }

        if (dodge != Vector2.Zero)
        {
            moveX = dodge.X * 1.8f;
            moveY = dodge.Y * 1.2f;
        }

        // Quick-heal under half health.
        if (Player.statLife < Player.statLifeMax2 / 2 && Player.potionDelay <= 0)
            Player.QuickHeal();

        // --- Translate intent into native controls.
        Player.controlLeft = moveX < -0.2f;
        Player.controlRight = moveX > 0.2f;
        Player.controlDown = moveY > 0.6f;

        bool wantUp = moveY < -0.5f && !Player.controlDown;
        bool blocked = (Player.controlLeft && vel.X > -1f) || (Player.controlRight && vel.X < 1f);
        stuckTicks = blocked && (Player.controlLeft || Player.controlRight) ? stuckTicks + 1 : 0;
        if (jumpCooldown > 0) jumpCooldown--;

        bool onGround = Player.velocity.Y == 0f;
        if ((wantUp && onGround) || stuckTicks > 15)
        {
            if (jumpCooldown == 0)
            {
                Player.controlJump = true;
                jumpCooldown = 20;
            }
        }

        // Hook/mount dashes, accessories etc. are untouched on purpose; the
        // loadout layer decides what the pilot is allowed to use.
        lastX = pos.X;
    }

    private NPC FindBoss()
    {
        NPC best = null;
        float bestDist = float.MaxValue;
        foreach (NPC n in Main.npc)
        {
            if (!n.active || !n.boss) continue;
            float d = Vector2.Distance(n.Center, Player.Center);
            if (d < bestDist) { bestDist = d; best = n; }
        }
        return best;
    }

    /// <summary>
    /// If the threat's trajectory passes within (radius + ThreatRadius) of the
    /// player within ThreatHorizon ticks, propose a perpendicular dodge.
    /// </summary>
    private void TryThreat(Vector2 at, Vector2 vel, float radius, ref Vector2 dodge, ref float worst, Vector2 pos)
    {
        float speed = vel.Length();
        if (speed < 0.01f) return;
        Vector2 toMe = pos - at;
        float tHit = Vector2.Dot(toMe, vel) / (speed * speed);
        if (tHit < 0 || tHit > ThreatHorizon) return;
        Vector2 closest = at + vel * tHit;
        float miss = Vector2.Distance(closest, pos);
        float need = ThreatRadius + radius;
        if (miss < need && miss < worst)
        {
            worst = miss;
            Vector2 perp = new(-vel.Y, vel.X);
            perp.Normalize();
            if (Vector2.Dot(perp, pos - closest) < 0) perp = -perp;
            dodge = perp;
        }
    }
}
