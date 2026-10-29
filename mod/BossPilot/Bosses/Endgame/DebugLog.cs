using System;
using System.IO;
using System.Text.Json;
using BossPilot.Common;
using BossPilot.Common.Headless;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace BossPilot.Bosses.Endgame;

/// <summary>
/// Headless diagnostics for the endgame plans, written next to results.jsonl:
/// hits.jsonl (one row per hit: source, pilot state, boss AI state, planner
/// clearance) and pilot.jsonl (broom-model calibration for the first ticks of
/// each attempt, then a 1 Hz planner trace).
/// </summary>
public static class DebugLog
{
    private static StreamWriter hits, pilot;
    private static Vector2 predictedVel;
    private static bool havePrediction;

    // Ring buffer of the last ticks, dumped with each hit.
    private const int Ring = 90;
    private static readonly object[] ring = new object[Ring];
    private static int ringHead;

    private static StreamWriter Open(ref StreamWriter w, string name)
    {
        if (w == null && HeadlessSim.OutDir != null)
            w = new StreamWriter(Path.Combine(HeadlessSim.OutDir, name), append: true) { AutoFlush = true };
        return w;
    }

    // One-step check of the homing replicas: last tick's model stepped with the pilot's actual move.
    private static readonly System.Collections.Generic.Dictionary<int, (int type, Hazard model)> replicas = [];
    private static double replicaErr, replicaMax;
    private static int replicaN;

    private static void ValidateReplicas(Player p)
    {
        var scratch = new System.Collections.Generic.List<Shape>();
        foreach (var (who, (type, model)) in replicas)
        {
            Projectile pr = Main.projectile[who];
            if (!pr.active || pr.type != type) continue;
            model.Step(p.Center);
            scratch.Clear();
            model.Emit(scratch);
            if (scratch.Count == 0) continue;
            Shape sh = scratch[^1];
            Vector2 c = sh.IsSegment ? sh.B : sh.A;
            double e = Vector2.Distance(c, pr.Center);
            replicaErr += e; replicaN++; replicaMax = Math.Max(replicaMax, e);
        }
        replicas.Clear();
        foreach (Projectile pr in Main.projectile)
        {
            if (!pr.active) continue;
            Hazard h = pr.type switch
            {
                Terraria.ID.ProjectileID.PhantasmalEye => new PhantasmalEye(pr),
                Terraria.ID.ProjectileID.HallowBossRainbowStreak => new RainbowStreak(pr),
                _ => null,
            };
            if (h == null) continue;
            h.Rewind();
            replicas[pr.whoAmI] = (pr.type, h);
        }
    }

    private static readonly System.Collections.Generic.Dictionary<int, object> planned = [];
    private static Vector2 plannedPilot;

    public static void Tick(Player p, FightContext ctx, int ux, int uy, Planner planner, int hazards)
    {
        if (Open(ref pilot, "pilot.jsonl") == null) return;
        int t = ctx.Tick;
        ValidateReplicas(p);
        planned.Clear();
        plannedPilot = p.Center;
        foreach (Projectile pr in Main.projectile)
            if (pr.active && pr.hostile && Vector2.Distance(pr.Center, p.Center) < 200)
                planned[pr.whoAmI] = new { ty = pr.type, c = new[] { MathF.Round(pr.Center.X, 1), MathF.Round(pr.Center.Y, 1) }, v = new[] { MathF.Round(pr.velocity.X, 2), MathF.Round(pr.velocity.Y, 2) }, ai = pr.ai, la = pr.localAI, tl = pr.timeLeft };
        if (t <= 150)
        {
            // Calibration: compare last tick's model prediction with the velocity the kernel produced.
            if (havePrediction && t > 1)
                pilot.WriteLine(JsonSerializer.Serialize(new { s = ctx.Scenario.Name, t, cal = true, pred = new[] { predictedVel.X, predictedVel.Y }, act = new[] { p.velocity.X, p.velocity.Y }, mounted = p.mount.Active }));
            Vector2 pos = p.Center, vel = p.velocity;
            BroomModel.Step(ref pos, ref vel, ux, uy);
            predictedVel = vel;
            havePrediction = true;
        }
        else havePrediction = false;
        ring[ringHead++ % Ring] = new
        {
            t, pos = new[] { (int)p.Center.X, (int)p.Center.Y }, vel = new[] { MathF.Round(p.velocity.X, 2), MathF.Round(p.velocity.Y, 2) },
            act = new[] { ux, uy }, clear = (int)planner.LastMinClearance, cost = (int)planner.LastCost, near = Nearest(p), ray = Ray(p),
            goal = new[] { (int)planner.Goal.X, (int)planner.Goal.Y },
        };
        if (t % 60 == 0)
        {
            NPC b = TrialRunner.Instance.MainBoss();
            pilot.WriteLine(JsonSerializer.Serialize(new
            {
                s = ctx.Scenario.Name, t,
                pos = new[] { p.Center.X / 16f, p.Center.Y / 16f }, vel = new[] { p.velocity.X, p.velocity.Y },
                act = new[] { ux, uy }, cost = planner.LastCost, clear = planner.LastMinClearance, hazards,
                replica = replicaN == 0 ? null : new { n = replicaN, mean = Math.Round(replicaErr / replicaN, 3), max = Math.Round(replicaMax, 3) },
                hp = p.statLife, boss = b == null ? null : new { x = b.Center.X / 16f, y = b.Center.Y / 16f, life = b.life, ai = b.ai },
            }));
            replicaErr = replicaMax = 0; replicaN = 0;
        }
    }

    private static object Nearest(Player p)
    {
        Projectile best = null;
        float bd = float.MaxValue;
        foreach (Projectile pr in Main.projectile)
        {
            if (!pr.active || !pr.hostile) continue;
            float d = Vector2.Distance(pr.Center, p.Center);
            if (d < bd) { bd = d; best = pr; }
        }
        return best == null ? null : new { ty = best.type, x = (int)best.Center.X, y = (int)best.Center.Y, vx = MathF.Round(best.velocity.X, 1), vy = MathF.Round(best.velocity.Y, 1), tl = best.timeLeft };
    }

    /// <summary>First Phantasmal Deathray: owner type, ray angle, spin, age, pilot angle and range from its origin.</summary>
    private static object Ray(Player p)
    {
        foreach (Projectile pr in Main.projectile)
        {
            if (!pr.active || pr.type != Terraria.ID.ProjectileID.PhantasmalDeathray) continue;
            Vector2 rel = p.Center - pr.Center;
            return new { own = Main.npc[(int)pr.ai[1]].type, a = MathF.Round(pr.velocity.ToRotation(), 3), spin = MathF.Round(pr.ai[0], 4), la = (int)pr.localAI[0], me = MathF.Round(rel.ToRotation(), 3), r = (int)rel.Length() };
        }
        return null;
    }

    public static void Hit(Player p, Player.HurtInfo info)
    {
        TrialRunner tr = TrialRunner.Instance;
        if (tr.Current != TrialRunner.Phase.Fighting || Open(ref hits, "hits.jsonl") == null) return;
        int src = info.DamageSource.SourceNPCIndex;
        int npcType = src >= 0 && src < Main.maxNPCs ? Main.npc[src].type : -1;
        NPC b = tr.MainBoss();
        Planner pl = (tr.Plan as EndgamePlan)?.PlannerForLog;
        hits.WriteLine(JsonSerializer.Serialize(new
        {
            s = tr.Scenario.Name, t = tr.FightTicks, dmg = info.Damage,
            proj = info.DamageSource.SourceProjectileType, npc = npcType,
            pos = new[] { p.Center.X / 16f, p.Center.Y / 16f }, vel = new[] { p.velocity.X, p.velocity.Y },
            act = pl == null ? null : new[] { pl.LastUx, pl.LastUy }, clear = pl?.LastMinClearance,
            boss = b == null ? null : new { x = b.Center.X / 16f, y = b.Center.Y / 16f, life = b.life, ai = b.ai, lai = b.localAI },
            trace = Trace(),
            src = Source(p, info),
        }));
    }

    /// <summary>The hitting projectile now and as the planner saw it this tick, with both hitboxes.</summary>
    private static object Source(Player p, Player.HurtInfo info)
    {
        int i = info.DamageSource.SourceProjectileLocalIndex;
        if (i < 0 || i >= Main.maxProjectiles) return null;
        Projectile pr = Main.projectile[i];
        Rectangle pb = p.getRect(), hb = pr.Hitbox;
        return new
        {
            who = i, ty = pr.type, c = new[] { pr.Center.X, pr.Center.Y }, v = new[] { pr.velocity.X, pr.velocity.Y }, ai = pr.ai, la = pr.localAI,
            prect = new[] { pb.X, pb.Y, pb.Width, pb.Height }, hrect = new[] { hb.X, hb.Y, hb.Width, hb.Height },
            pilotAtPlan = new[] { plannedPilot.X, plannedPilot.Y }, atPlan = planned.TryGetValue(i, out object seen) ? seen : null,
        };
    }

    private static object[] Trace()
    {
        var list = new System.Collections.Generic.List<object>();
        for (int i = Math.Max(0, ringHead - 60); i < ringHead; i++) list.Add(ring[i % Ring]);
        return list.ToArray();
    }
}

/// <summary>Routes hurt events of the pilot into DebugLog.</summary>
public sealed class EndgameHitLogger : ModPlayer
{
    public override void OnHurt(Player.HurtInfo info)
    {
        if (Player.whoAmI == Main.myPlayer && TrialRunner.Instance.Plan is EndgamePlan) DebugLog.Hit(Player, info);
    }
}
