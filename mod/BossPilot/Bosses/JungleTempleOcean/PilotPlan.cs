using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using BossPilot.Common;

namespace BossPilot.Bosses.JungleTempleOcean;

/// <summary>
/// Shared control loop of the jungle/temple/ocean plans: each tick, rebuild the predicted
/// threat set (boss-specific kernel models + generic projectiles/NPCs), run the sampling
/// planner, write native controls, and aim the gun with lead at the plan's target.
/// </summary>
public abstract class PilotPlan : BossPlan
{
    protected readonly Planner Planner = new();
    protected ArenaInfo Arena;
    protected FightContext Ctx;
    protected Player P;

    private readonly List<LinearThreat> linearPool = [];
    private readonly List<StaticThreat> staticPool = [];
    private readonly List<FuncThreat> funcPool = [];
    private readonly List<BallThreat> ballPool = [];
    private readonly List<VirtualShot> shotPool = [];
    private int linearUsed, staticUsed, funcUsed, ballUsed, shotUsed;
    private long hitWarnTick = -100;

    /// <summary>NPC types modelled by the plan itself (excluded from the generic linear model).</summary>
    protected virtual ISet<int> ModelledNpcs => EmptySet;
    /// <summary>Projectile types modelled by the plan itself.</summary>
    protected virtual ISet<int> ModelledProjectiles => EmptySet;
    private static readonly HashSet<int> EmptySet = [];

    protected virtual void OnAttemptStart(Player p) { }
    /// <summary>Adds boss-specific threats and sets Planner.Objective / Planner.Boss.</summary>
    protected abstract void BuildThreats(Player p);
    /// <summary>World point to shoot at, or null to hold fire.</summary>
    protected abstract Vector2? AimTarget(Player p, out Vector2 targetVelocity);

    public override bool Control(Player p, NPC boss, FightContext ctx)
    {
        P = p;
        if (!ReferenceEquals(ctx, Ctx))
        {
            Ctx = ctx;
            Planner.Reset();
            Array.Clear(predValid);
            pred1 = Vector2.Zero;
            HitProbe.Context = DescribeThreats;
            ScanHazards();
            OnAttemptStart(p);
        }
        if (ctx.Tick % 10 == 1) Biomes.Refresh(p);
        if (ctx.Tick % 1800 == 0) LogTiming(Boss);
        linearUsed = staticUsed = funcUsed = ballUsed = shotUsed = 0;
        Planner.Threats.Clear();
        Planner.Boss = null;
        AddGenericThreats(p);
        foreach (Box h in hazards)
            if (Math.Abs(h.Center.X - p.Center.X) < 400f && Math.Abs(h.Center.Y - p.Center.Y) < 300f)
                Static("spikes", h, 3f);
        BuildThreats(p);
        CheckPredictions(p);
        Planner.Decide(p);
        Planner.Apply(p);
        RecordPredictions();

        if (Planner.BestHitTick >= 0 && ctx.Tick - hitWarnTick > 30)
        {
            hitWarnTick = ctx.Tick;
            PilotLog.Line($"  warn t={ctx.Tick} unavoidable? k={Planner.BestHitTick} by {Planner.BestHitTag} cost={Planner.BestCost:F0} h={Planner.Horizontal} v={Planner.Vertical} p=({p.Center.X / 16:F1},{p.Center.Y / 16:F1}) v=({p.velocity.X:F1},{p.velocity.Y:F1}) wing={p.wingTime:F0} rocket={p.rocketTime} jump={p.jump} rel={p.releaseJump}");
            NPC b = BossNpc();
            if (b != null)
            {
                var sb = new System.Text.StringBuilder($"    boss ({b.Center.X / 16:F1},{b.Center.Y / 16:F1}) v=({b.velocity.X:F1},{b.velocity.Y:F1}) ai[{b.ai[0]},{b.ai[1]},{b.ai[2]},{b.ai[3]}] path:");
                for (int k = 4; k < Planner.H; k += 5)
                    sb.Append($" k{k}:p({Planner.BestPath[k].Center.X / 16:F1},{Planner.BestPath[k].Center.Y / 16:F1}) b({Planner.BestBoss[k].Center.X / 16:F1},{Planner.BestBoss[k].Center.Y / 16:F1})");
                PilotLog.Line(sb.ToString());
            }
        }

        // Quick heal (the Quick Heal key's own routine; controlQuickHeal is read before SetControls).
        if (p.statLife < p.statLifeMax2 / 2 && p.potionDelay == 0) p.QuickHeal();

        Vector2? aim = AimTarget(p, out Vector2 tv);
        if (aim is Vector2 a)
        {
            FightContext.Aim(Gunnery.Lead(p.Center, a, tv, Gunnery.ShotSpeed(p)));
            p.controlUseItem = true;
        }
        else p.controlUseItem = false;
        return true;
    }

    // Prediction-error diagnostics: what the chosen plan expected 20 ticks ahead vs what happened.
    private const int Lag = 20;
    private readonly Vector2[] predPilot = new Vector2[Lag], predBoss = new Vector2[Lag];
    private readonly bool[] predValid = new bool[Lag];
    private double errPilot, errBoss, err1;
    private int errN, errBossN, err1N;
    private Vector2 pred1;
    private float err1Max;
    private float errBossMax;

    private void CheckPredictions(Player p)
    {
        int slot = Ctx.Tick % Lag;
        if (pred1 != Vector2.Zero)
        {
            float e1 = Vector2.Distance(pred1, p.Center);
            err1 += e1; err1N++; err1Max = Math.Max(err1Max, e1);
        }
        if (predValid[slot])
        {
            errPilot += Vector2.Distance(predPilot[slot], p.Center);
            errN++;
            NPC b = BossNpc();
            if (b != null && predBoss[slot] != Vector2.Zero)
            {
                float e = Vector2.Distance(predBoss[slot], b.Center);
                errBoss += e; errBossN++;
                errBossMax = Math.Max(errBossMax, e);
            }
        }
        if (Ctx.Tick % 1800 == 0 && errN > 0)
        {
            PilotLog.Line($"  pred err @1: pilot {err1 / Math.Max(1, err1N):F2} px (max {err1Max:F1}); @{Lag}: pilot {errPilot / errN:F1} px, boss {(errBossN > 0 ? errBoss / errBossN : 0):F1} px (max {errBossMax:F0})");
            errPilot = errBoss = err1 = 0; errN = errBossN = err1N = 0; errBossMax = err1Max = 0;
        }
    }

    private void RecordPredictions()
    {
        int slot = Ctx.Tick % Lag;
        pred1 = Planner.BestPath[0].Center;
        predPilot[slot] = Planner.BestPath[Lag - 1].Center;
        predBoss[slot] = Planner.Boss != null && Planner.BestBoss[Lag - 1].Valid ? Planner.BestBoss[Lag - 1].Center : Vector2.Zero;
        predValid[slot] = true;
    }

    /// <summary>The NPC whose motion Planner.Boss predicts (the main boss by default).</summary>
    protected virtual NPC BossNpc() => TrialRunner.Instance.MainBoss();

    private readonly List<Box> hazards = [];

    /// <summary>Spike tiles inside the arena (temple floors have them) as static threats, merged per row.</summary>
    private void ScanHazards()
    {
        hazards.Clear();
        if (Arena == null) return;
        int x0 = (int)(Arena.Left / 16) - 2, x1 = (int)(Arena.Right / 16) + 2;
        int y0 = (int)(Arena.Top / 16) - 2, y1 = (int)(Arena.Floor / 16) + 3;
        for (int y = y0; y <= y1; y++)
        {
            int run = -1;
            for (int x = x0; x <= x1 + 1; x++)
            {
                bool spike = x <= x1 && WorldGen.InWorld(x, y) && Main.tile[x, y].HasTile &&
                    Main.tile[x, y].TileType is Terraria.ID.TileID.Spikes or Terraria.ID.TileID.WoodenSpikes or Terraria.ID.TileID.PressurePlates;
                if (spike && run < 0) run = x;
                else if (!spike && run >= 0)
                {
                    hazards.Add(new Box(run * 16 - 2, y * 16 - 2, x * 16 + 2, y * 16 + 18));
                    run = -1;
                }
            }
        }
        if (hazards.Count > 0) PilotLog.Line($"{hazards.Count} spike runs in the arena");
    }

    /// <summary>
    /// Cost of being airborne with little flight left: height above the floor or a platform
    /// times the missing wing time, so plans keep enough wing time to get back down safely.
    /// </summary>
    protected float WingReserveCost(in PilotState s, float reserve = 90f)
    {
        if (s.Vel.Y == 0f || Arena == null) return 0f;
        float bottom = s.Pos.Y + PilotState.Hgt, ground = Arena.Floor;
        foreach (float py in Arena.Platforms)
            if (py >= bottom && py < ground) ground = py;
        float h = Math.Max(0f, ground - bottom);
        return h / 16f * Math.Max(0f, 1f - s.WingTime / reserve) * 0.6f;
    }

    private void AddGenericThreats(Player p)
    {
        ISet<int> skipProj = ModelledProjectiles;
        foreach (Projectile pr in Main.projectile)
        {
            if (!pr.active || !pr.hostile || pr.damage <= 0 || skipProj.Contains(pr.type)) continue;
            if (Vector2.DistanceSquared(pr.Center, p.Center) > 1600f * 1600f) continue;
            Linear($"proj{pr.type}", pr.Center, pr.velocity, pr.width / 2f, pr.height / 2f, pr.timeLeft, 1 + pr.extraUpdates, 3f, 0.12f);
        }
        ISet<int> skipNpc = ModelledNpcs;
        foreach (NPC n in Main.npc)
        {
            if (!n.active || n.friendly || n.damage <= 0 || skipNpc.Contains(n.type)) continue;
            if (Vector2.DistanceSquared(n.Center, p.Center) > 1600f * 1600f) continue;
            Linear($"npc{n.type}", n.Center, n.velocity, n.width / 2f, n.height / 2f, 1000, 1, 4f, 0.3f);
        }
    }

    protected LinearThreat Linear(string tag, Vector2 c, Vector2 v, float hw, float hh, int life, int steps, float margin, float grow, float gravity = 0f)
    {
        if (linearUsed == linearPool.Count) linearPool.Add(new LinearThreat());
        LinearThreat t = linearPool[linearUsed++].Set(c, v, hw, hh, life, steps, gravity);
        t.Tag = tag; t.Margin = margin; t.Grow = grow; t.Weight = 1f;
        Planner.Threats.Add(t);
        return t;
    }

    /// <summary>Adds hypothetical aimed shots from origin at the given delays (see VirtualShot).</summary>
    protected void Virtual(string tag, Vector2 origin, float speed, float half, float weight, params int[] delays)
    {
        foreach (int d in delays)
        {
            if (shotUsed == shotPool.Count) shotPool.Add(new VirtualShot());
            VirtualShot t = shotPool[shotUsed++];
            t.Origin = origin; t.Speed = speed; t.Half = half; t.Delay = d;
            t.Tag = tag; t.Margin = 4f; t.Grow = 0.1f; t.Weight = weight;
            Planner.Threats.Add(t);
        }
    }

    protected BallThreat Ball(Projectile pr, float margin, float grow)
    {
        if (ballUsed == ballPool.Count) ballPool.Add(new BallThreat());
        BallThreat t = ballPool[ballUsed++].Set(pr);
        t.Tag = $"ball{pr.type}"; t.Margin = margin; t.Grow = grow; t.Weight = 1f;
        Planner.Threats.Add(t);
        return t;
    }

    protected StaticThreat Static(string tag, Box b, float margin)
    {
        if (staticUsed == staticPool.Count) staticPool.Add(new StaticThreat());
        StaticThreat t = staticPool[staticUsed++].Set(b);
        t.Tag = tag; t.Margin = margin; t.Grow = 0f; t.Weight = 1f;
        Planner.Threats.Add(t);
        return t;
    }

    protected FuncThreat Func(string tag, Action<Vector2[], Box[]> fn, float margin, float grow, bool reactive = true)
    {
        if (funcUsed == funcPool.Count) funcPool.Add(new FuncThreat());
        FuncThreat t = funcPool[funcUsed++];
        t.Fn = fn; t.IsReactive = reactive; t.Tag = tag; t.Margin = margin; t.Grow = grow; t.Weight = 1f;
        Planner.Threats.Add(t);
        return t;
    }

    /// <summary>Soft walls: quadratic cost for leaving the arena interior (with a buffer in px).</summary>
    protected float BoundsCost(Vector2 c, float buffer, float topBuffer)
    {
        if (Arena == null) return 0f;
        float cost = 0f;
        float l = Arena.Left + buffer - c.X, r = c.X - (Arena.Right - buffer), t = Arena.Top + topBuffer - c.Y;
        if (l > 0) cost += (l / 32f) * (l / 32f);
        if (r > 0) cost += (r / 32f) * (r / 32f);
        if (t > 0) cost += (t / 32f) * (t / 32f);
        return cost;
    }

    private string DescribeThreats()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"plan h={Planner.Horizontal} v={Planner.Vertical} hitk={Planner.BestHitTick}:{Planner.BestHitTag} |");
        foreach (NPC n in Main.npc)
            if (n.active && n.damage > 0 && !n.friendly && Vector2.Distance(n.Center, P.Center) < 400)
                sb.Append($" n{n.type}@({n.Center.X / 16:F0},{n.Center.Y / 16:F0}) v({n.velocity.X:F1},{n.velocity.Y:F1}) ai[{n.ai[0]:F0},{n.ai[1]:F0},{n.ai[2]:F0}]");
        foreach (Projectile pr in Main.projectile)
            if (pr.active && pr.hostile && Vector2.Distance(pr.Center, P.Center) < 300)
                sb.Append($" p{pr.type}@({pr.Center.X / 16:F0},{pr.Center.Y / 16:F0}) v({pr.velocity.X:F1},{pr.velocity.Y:F1})");
        return sb.ToString();
    }

    /// <summary>Logs planner timing once per attempt end (called by plans when convenient).</summary>
    protected void LogTiming(string name)
    {
        if (Planner.Decisions == 0) return;
        PilotLog.Line($"{name}: {Planner.Decisions} decisions, {Planner.Ticks100ns / 10.0 / Planner.Decisions:F0} us avg");
    }

    protected static NPC FindNpc(int type)
    {
        foreach (NPC n in Main.npc)
            if (n.active && n.type == type) return n;
        return null;
    }
}
