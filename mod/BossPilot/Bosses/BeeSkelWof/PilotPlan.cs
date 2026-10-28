using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using BossPilot.Common;

namespace BossPilot.Bosses.BeeSkelWof;

/// <summary>
/// Shared scaffolding of the three plans: per-attempt reset, hazard intake,
/// the MPC step, trigger and quick-heal. Subclasses only decide the goal,
/// the aim point and boss-specific hazards.
/// </summary>
public abstract class PilotPlan : BossPlan
{
    protected Planner Planner = new();
    private FightContext seen;

    protected abstract Goal Think(Player p, NPC boss, FightContext ctx, out Vector2 aim, out bool shoot);

    protected virtual void Begin(Player p, FightContext ctx) { }

    public override bool Control(Player p, NPC boss, FightContext ctx)
    {
        if (!ReferenceEquals(ctx, seen))
        {
            seen = ctx;
            Planner = new Planner();
            Begin(p, ctx);
        }
        PlayerSim.HasCloud = HasCloud(p);
        Planner.BeginTick(ctx.Tick);
        Goal g = Think(p, boss, ctx, out Vector2 aim, out bool shoot);
        Planner.Drive(p, g, ctx.Tick);
        Gun.Fire(p, aim, shoot);
        if (p.statLife < p.statLifeMax2 * 0.45f && p.potionDelay == 0) p.controlQuickHeal = true;
        if (Trace.Verbose && ctx.Tick % 120 == 1)
            Trace.Line($"zones jungle {p.ZoneJungle} dungeon {p.ZoneDungeon} uw {p.ZoneUnderworldHeight} wet {p.wet} lava {p.lavaWet} counts jungle {Main.SceneMetrics.JungleTileCount} dungeon {Main.SceneMetrics.DungeonTileCount} npcs {CountHostiles()} calm {p.calmed}");
        if (Trace.Verbose && ctx.Tick % 3 == 0)
            Trace.Line($"t {ctx.Tick} p {p.Center.X / 16f:F1},{p.Center.Y / 16f:F1} v {p.velocity.X:F2},{p.velocity.Y:F2} hz {Planner.Hazards.Count} {Trace.LastPlan} " +
                       $"goal {g.X / 16f:F1} aim {aim.X / 16f:F1},{aim.Y / 16f:F1} boss {(boss == null ? "-" : $"{boss.Center.X / 16f:F1},{boss.Center.Y / 16f:F1} ai {boss.ai[0]:F0},{boss.ai[1]:F0},{boss.ai[2]:F0} hp {boss.life}")}");
        return true;
    }

    private static bool HasCloud(Player p)
    {
        for (int i = 3; i < 10; i++)
        {
            int t = p.armor[i].type;
            if (t is ItemID.CloudinaBottle or ItemID.CloudinaBalloon) return true;
        }
        return false;
    }

    /// <summary>
    /// Adds every hostile NPC and projectile that could reach the pilot within the
    /// horizon. Plans add their own special cases first and pass those ids in skip.
    /// </summary>
    protected void AddGenericHazards(Player p, Func<NPC, bool> skipNpc = null, Func<Projectile, bool> skipProj = null)
    {
        Vector2 pc = p.Center;
        float reach = Planner.Horizon * 8f + 200f;
        foreach (NPC n in Main.npc)
        {
            if (!n.active || n.friendly || n.townNPC || n.damage <= 0 || n.lifeMax <= 5) continue;
            if (skipNpc != null && skipNpc(n)) continue;
            float dist = Vector2.Distance(n.Center, pc) - Math.Max(n.width, n.height);
            if (dist > reach + n.velocity.Length() * Planner.Horizon) continue;
            switch (n.type)
            {
                case NPCID.Bee or NPCID.BeeSmall:
                    Planner.AddPursuer(n, 5.5f, 0.12f, 6f);
                    break;
                case NPCID.TheHungryII or NPCID.CursedSkull or NPCID.Hellbat or NPCID.Lavabat:
                    Planner.AddPursuer(n, Math.Max(4f, n.velocity.Length()), 0.15f, 8f);
                    break;
                default:
                    Planner.AddNpc(n, 6f);
                    break;
            }
        }
        foreach (Projectile pr in Main.projectile)
        {
            if (!pr.active || !pr.hostile || pr.damage <= 0) continue;
            if (skipProj != null && skipProj(pr)) continue;
            float sp = pr.velocity.Length() * (pr.extraUpdates + 1);
            float dist = Vector2.Distance(pr.Center, pc);
            if (dist > sp * Planner.Horizon + 200f) continue;
            if (pr.type == ProjectileID.QueenBeeStinger)
                Planner.AddProjectile(pr, 5f, 0.1f, Math.Max(0, 15 - (int)pr.ai[0]));
            else
                Planner.AddProjectile(pr, 5f);
        }
    }

    /// <summary>Nearest hostile NPC within range that is worth shooting before the boss.</summary>
    protected static NPC NearestThreat(Player p, float range, Func<NPC, bool> filter)
    {
        NPC best = null;
        float bd = range;
        foreach (NPC n in Main.npc)
        {
            if (!n.active || n.friendly || n.dontTakeDamage || n.lifeMax <= 5 || !filter(n)) continue;
            float d = Vector2.Distance(n.Center, p.Center);
            if (d < bd && Collision.CanHit(p.Center, 1, 1, n.position, n.width, n.height)) { bd = d; best = n; }
        }
        return best;
    }

    /// <summary>Aim point leading a moving NPC with the held weapon's projectile speed.</summary>
    protected static Vector2 LeadAt(Player p, NPC n, Vector2? point = null)
    {
        float speed = Gun.ShotSpeed(p, out _);
        Vector2 target = point ?? n.Center;
        return Gun.Lead(p.Center, target, n.velocity, Math.Max(speed, 1f));
    }

    private static int CountHostiles()
    {
        int n = 0;
        foreach (NPC npc in Main.npc) if (npc.active && !npc.friendly && !npc.townNPC) n++;
        return n;
    }

    /// <summary>Removes stray hostile NPCs before the summon (same cleanup JobRunner does at attempt start).</summary>
    protected static void ClearHostiles()
    {
        foreach (NPC n in Main.npc)
            if (n.active && !n.townNPC && !n.friendly) n.active = false;
    }
}

/// <summary>
/// Workaround for a headless-harness gap: SceneMetrics (biome tile counts behind
/// ZoneJungle, ZoneDungeon, ZoneCorrupt...) is only rescanned by Lighting.LightTiles,
/// which runs from Main.DoDraw, and a dedicated server never draws. Without this the
/// pilot is never "in the jungle" (Queen Bee enrages) or "in the dungeon". Rescans the
/// biome area around the pilot every 10 ticks, like a client's lighting pass would,
/// for this plan family's trials only.
/// </summary>
public sealed class BiomeScan : ModPlayer
{
    private static readonly HashSet<string> Bosses = ["queen_bee", "skeletron", "wall_of_flesh"];
    private static readonly bool NoScan = System.Environment.GetEnvironmentVariable("BSW_NOSCAN") == "1";

    public override void PreUpdate()
    {
        if (!Common.Headless.HeadlessSim.Active || Player.whoAmI != Main.myPlayer || Main.GameUpdateCount % 10 != 0 || NoScan) return;
        TrialRunner t = TrialRunner.Instance;
        if (t.Current is not (TrialRunner.Phase.Preparing or TrialRunner.Phase.Fighting) || t.Scenario == null || !Bosses.Contains(t.Scenario.Boss)) return;
        Main.SceneMetrics.ScanAndExportToMain(new SceneMetricsScanSettings { BiomeScanCenterPositionInWorld = Player.Center });
    }
}
