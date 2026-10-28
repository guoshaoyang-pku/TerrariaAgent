using System;
using System.IO;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ModLoader;
using BossPilot.Common;
using BossPilot.Common.Headless;

namespace BossPilot.Bosses.SlimesEye;

/// <summary>
/// Shared controller for the King Slime / Eye of Cthulhu / Queen Slime plans: per tick it
/// captures the boss model, lets the <see cref="Planner"/> pick movement, aims with lead at
/// <see cref="ChooseTarget"/> and fires, and drinks a healing potion when low.
/// </summary>
public abstract class PilotPlan : BossPlan
{
    protected readonly Planner Planner = new();
    protected FightContext Ctx;
    protected Player P;
    protected NPC BossNpc;

    /// <summary>Boss model for rollouts, re-captured from the live NPC every tick (null = none).</summary>
    protected abstract IBossSim CaptureBoss(NPC boss);

    /// <summary>Positional preference cost of the predicted player center (and boss box) at sample k.</summary>
    protected virtual float Prefer(Vector2 c, Box boss, int k) => 0f;

    /// <summary>Called on the first controlled tick of each attempt.</summary>
    protected virtual void BeginAttempt(Player p) { }

    public override bool Control(Player p, NPC boss, FightContext ctx)
    {
        if (!ReferenceEquals(ctx, Ctx))
        {
            Ctx = ctx;
            Planner.Invalidate();
            Planner.Preference = Prefer;
            BeginAttempt(p);
        }
        P = p;
        BossNpc = TrialRunner.Instance.MainBoss();
        Kit.HealIfLow(p);
        Planner.Boss = BossNpc != null ? CaptureBoss(BossNpc) : null;
        Planner.Drive(p);
        Entity target = ChooseTarget(p, BossNpc);
        if (target == null) p.controlUseItem = false;
        else if (target == BossNpc && Planner.Boss != null) Gunner.Fire(p, LeadWithSim(p, Planner.Boss, target.Center));
        else Gunner.Fire(p, Gunner.Lead(p, target.Center, target.velocity));
        SlimesEyeTrace.Tick(this, p, ctx);
        return true;
    }

    /// <summary>Aim point on the boss from its own forward model (flight time iterated three times).</summary>
    protected static Vector2 LeadWithSim(Player p, IBossSim sim, Vector2 now)
    {
        (float speed, float grav, float delay) = Gunner.Ballistics(p);
        if (speed <= 0.1f) return now;
        Vector2 from = p.RotatedRelativePoint(p.MountedCenter), aim = now;
        for (int it = 0; it < 3; it++)
        {
            float t = MathHelper.Clamp(Vector2.Distance(from, aim) / speed, 0f, 90f);
            sim.Reset();
            Vector2 at = now;
            for (int i = 0; i < (int)t; i++)
            {
                Box b = sim.Step(p.Center, p.position.Y, p.position.Y + p.height);
                if (b.Valid) at = new Vector2(b.X, b.Y);
            }
            float ft = Math.Max(0f, t - delay);
            aim = at - new Vector2(0f, 0.5f * grav * ft * ft);
        }
        return aim;
    }

    /// <summary>Default target: the nearest minion in line of sight within 9 tiles, otherwise the boss.</summary>
    protected virtual Entity ChooseTarget(Player p, NPC boss) => NearestThreat(p, 9 * 16f) ?? (Entity)boss;

    protected NPC NearestThreat(Player p, float range, Func<NPC, bool> filter = null)
    {
        NPC best = null;
        float bestD = range;
        foreach (NPC n in Main.npc)
        {
            if (!n.active || n.friendly || n.boss || n.damage <= 0 || n.dontTakeDamage || n.townNPC) continue;
            if (filter != null && !filter(n)) continue;
            float d = Vector2.Distance(n.Center, p.Center);
            if (d < bestD && Collision.CanHitLine(p.Center, 1, 1, n.Center, 1, 1)) { bestD = d; best = n; }
        }
        return best;
    }

    internal Planner Mpc => Planner;


    /// <summary>Debug hook: return true on ticks whose next replan should dump all candidates.</summary>
    protected internal virtual bool DumpWanted(FightContext ctx) => false;

    public virtual string Describe() => $"{Planner.Current} cost={Planner.LastCost:F0} gap={Planner.LastGap:F0} err={Planner.ErrPlayer:F1}/{Planner.ErrBoss:F1}";
}

/// <summary>
/// Diagnostics for these plans: every hit (with its source) goes to out/slimeseye_hits.jsonl;
/// with BOSSPILOT_SE_TRACE=1 out/slimeseye_trace.jsonl gets a per-10-tick decision trace, the
/// last 30 ticks of decisions before each hit, and candidate dumps when a hit is predicted.
/// </summary>
public sealed class SlimesEyeTrace : ModPlayer
{
    private static StreamWriter hits, trace;
    private static readonly string[] recent = new string[30];
    private static int lastDump = -100;
    private static readonly bool traceOn = Environment.GetEnvironmentVariable("BOSSPILOT_SE_TRACE") == "1";

    private static StreamWriter Open(ref StreamWriter w, string name)
    {
        if (w == null && HeadlessSim.Active && HeadlessSim.OutDir != null)
            w = new StreamWriter(Path.Combine(HeadlessSim.OutDir, name), append: true) { AutoFlush = true };
        return w;
    }

    public static void Tick(PilotPlan plan, Player p, FightContext ctx)
    {
        if (!traceOn) return;
        // Ring buffer of the last 30 ticks of decisions, flushed into the hit log on every hit.
        NPC kb = TrialRunner.Instance.MainBoss();
        recent[ctx.Tick % recent.Length] = $"{ctx.Tick}:p({p.Center.X / 16f:F1},{p.Center.Y / 16f:F1},{p.velocity.X:F1},{p.velocity.Y:F1})"
            + (kb == null ? "" : $" b({kb.Center.X / 16f:F1},{kb.Center.Y / 16f:F1},{kb.velocity.X:F1},{kb.velocity.Y:F1},ai {kb.ai[0]:F0},{kb.ai[1]:F0})") + $" {plan.Describe()}";
        if (plan.DumpWanted(ctx) || (plan.Mpc.LastGap < 0f && ctx.Tick - lastDump > 30)) { plan.Mpc.DumpNext = true; lastDump = ctx.Tick; }
        if (plan.Mpc.LastDump != null)
        {
            Open(ref trace, "slimeseye_trace.jsonl")?.WriteLine($"{{\"s\":\"{ctx.Scenario.Name}\",\"t\":{ctx.Tick},\"dump\":\"{plan.Mpc.LastDump}\"}}");
            plan.Mpc.LastDump = null;
        }
        if (ctx.Tick % 10 != 0) return;
        NPC b = TrialRunner.Instance.MainBoss();
        string boss = b == null ? "null" : $"[{b.Center.X / 16f:F1},{b.Center.Y / 16f:F1},{b.velocity.X:F1},{b.velocity.Y:F1},{b.ai[0]:F0},{b.ai[1]:F0},{b.ai[2]:F0},{b.ai[3]:F0},{b.localAI[0]:F0},{b.localAI[1] / 16f:F1},{b.localAI[2] / 16f:F1}]";
        int minions = 0, shots = 0;
        foreach (NPC n in Main.npc) if (n.active && !n.boss && !n.friendly && n.damage > 0) minions++;
        foreach (Projectile pr in Main.projectile) if (pr.active && pr.hostile) shots++;
        Open(ref trace, "slimeseye_trace.jsonl")?.WriteLine(
            $"{{\"s\":\"{ctx.Scenario.Name}\",\"t\":{ctx.Tick},\"m\":{minions},\"h\":{shots},\"p\":[{p.Center.X / 16f:F2},{p.Center.Y / 16f:F2},{p.velocity.X:F2},{p.velocity.Y:F2}],\"b\":{boss},\"plan\":\"{plan.Describe()}\"}}");
    }

    public override void OnHurt(Player.HurtInfo info)
    {
        TrialRunner t = TrialRunner.Instance;
        if (t.Current != TrialRunner.Phase.Fighting || Player.whoAmI != Main.myPlayer || t.Plan is not PilotPlan plan) return;
        PlayerDeathReason r = info.DamageSource;
        string src = "other";
        Vector2 at = Vector2.Zero, vel = Vector2.Zero;
        if (r.SourceNPCIndex >= 0 && r.SourceNPCIndex < Main.maxNPCs)
        {
            NPC n = Main.npc[r.SourceNPCIndex];
            src = $"npc:{n.type}:ai[{n.ai[0]:F0},{n.ai[1]:F0},{n.ai[2]:F0},{n.ai[3]:F0}]";
            at = n.Center; vel = n.velocity;
        }
        else if (r.SourceProjectileType > 0)
        {
            src = $"proj:{r.SourceProjectileType}";
            if (r.SourceProjectileLocalIndex >= 0 && r.SourceProjectileLocalIndex < Main.maxProjectiles)
            {
                Projectile pr = Main.projectile[r.SourceProjectileLocalIndex];
                at = pr.Center; vel = pr.velocity;
            }
        }
        else if (r.SourceOtherIndex >= 0) src = $"other:{r.SourceOtherIndex}";
        if (traceOn)
        {
            int now = t.FightTicks;
            var lines = new System.Collections.Generic.List<string>();
            for (int i = 29; i >= 0; i--)
            {
                string line = recent[((now - i) % recent.Length + recent.Length) % recent.Length];
                if (line != null) lines.Add(line);
            }
            Open(ref trace, "slimeseye_trace.jsonl")?.WriteLine($"{{\"s\":\"{t.Scenario.Name}\",\"t\":{now},\"before_hit\":{System.Text.Json.JsonSerializer.Serialize(lines)}}}");
        }
        Open(ref hits, "slimeseye_hits.jsonl")?.WriteLine(
            $"{{\"s\":\"{t.Scenario.Name}\",\"t\":{t.FightTicks},\"src\":\"{src}\",\"dmg\":{info.Damage},\"p\":[{Player.Center.X / 16f:F2},{Player.Center.Y / 16f:F2},{Player.velocity.X:F2},{Player.velocity.Y:F2}],\"at\":[{at.X / 16f:F2},{at.Y / 16f:F2},{vel.X:F2},{vel.Y:F2}],\"plan\":\"{plan.Describe()}\"}}");
    }

    public override void Unload()
    {
        hits?.Dispose(); hits = null;
        trace?.Dispose(); trace = null;
    }
}
