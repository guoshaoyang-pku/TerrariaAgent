using System;
using System.Collections.Generic;
using BossPilot.Common;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace BossPilot.Bosses.Endgame;

/// <summary>
/// Shared controller for the endgame bosses: every tick it asks the boss for
/// its hazard list (kernel-replica predictors), a goal point and keep-in box,
/// runs the receding-horizon Planner on the broom model, then aims, fires and
/// drinks healing potions. Subclasses supply the boss knowledge.
/// </summary>
public abstract class EndgamePlan : BossPlan
{
    protected readonly Planner Planner = new();
    protected readonly List<Hazard> Hazards = [];
    internal Planner PlannerForLog => Planner;

    /// <summary>Fills <see cref="Hazards"/> for this tick.</summary>
    protected abstract void CollectHazards(Player p, FightContext ctx);

    /// <summary>Sets Planner.Goal / Planner.Box for this tick.</summary>
    protected abstract void ChooseGoal(Player p, FightContext ctx);

    /// <summary>World point to shoot at, or null to hold fire.</summary>
    protected abstract Vector2? AimPoint(Player p, FightContext ctx);

    /// <summary>Projectile speed of the held weapon in px/tick, for target leading.</summary>
    protected virtual float ShotSpeed(Player p) => Math.Max(8f, p.HeldItem.shootSpeed * 2f);

    public override bool Control(Player p, NPC boss, FightContext ctx)
    {
        // Headless servers never rescan SceneMetrics (the client lighting engine does), so keep
        // the pilot's Zone* flags (Hallow, Dungeon, sky...) current for the scenario's biome.
        if (ctx.Tick % 30 == 1) p.ForceUpdateBiomes();
        Hazards.Clear();
        CollectHazards(p, ctx);
        ChooseGoal(p, ctx);
        (int ux, int uy) = Planner.Plan(p.Center, p.velocity, Hazards);

        p.controlLeft = ux < 0;
        p.controlRight = ux > 0;
        p.controlUp = uy < 0;
        p.controlJump = uy < 0 && (p.velocity.Y == 0f || !p.mount.Active);
        p.controlDown = uy > 0;

        Vector2? aim = AimPoint(p, ctx);
        if (aim.HasValue)
        {
            FightContext.Aim(aim.Value);
            p.controlUseItem = true;
        }

        if (p.statLife < p.statLifeMax2 * 0.55f && p.potionDelay <= 0) p.QuickHeal();
        DebugLog.Tick(p, ctx, ux, uy, Planner, Hazards.Count);
        return true;
    }

    /// <summary>Leads a moving target for a straight shot from the pilot.</summary>
    protected Vector2 Lead(Player p, Vector2 target, Vector2 targetVel)
    {
        float speed = ShotSpeed(p);
        float t = Vector2.Distance(p.Center, target) / speed;
        t = Math.Min(t, 40f);
        return target + targetVel * t;
    }

    public override void SpawnBoss(Player p, ScenarioConfig s) => SpawnAbove(p, Entry.NpcId, 12);

    /// <summary>Common healing stack for every tier: potions in the inventory for QuickHeal.</summary>
    protected static void GiveHealing(Player p, bool super)
    {
        Kit.Hotbar(p, 9, super ? ItemID.SuperHealingPotion : ItemID.GreaterHealingPotion, 30);
    }

    protected static NPC FindNpc(int type)
    {
        foreach (NPC n in Main.npc)
            if (n.active && n.type == type) return n;
        return null;
    }
}
