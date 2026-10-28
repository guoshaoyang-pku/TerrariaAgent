using System;
using Terraria;

namespace BossPilot.Bosses.Mechs;

/// <summary>
/// Short-horizon model-predictive movement: every tick, roll the pilot model forward
/// H ticks under each of ~70 two-segment control plans, score each against the shared
/// threat prediction (swept hitbox tests) plus a boss-specific goal, and apply the first
/// tick of the best one. Re-planning every tick absorbs model and prediction error.
/// </summary>
public sealed class Planner
{
    public enum V : byte { Fall, Rise, Hover, Drop }

    public const int H = Threats.H;
    private const int T1 = 8;
    private const float HitCost = 1000f, NearCost = 25f, NearDist = 40f, SwitchCost = 3f;

    private readonly record struct Plan(int H1, V V1, int H2, V V2);

    private static readonly Plan[] Plans = BuildPlans();
    public readonly Threats Threats = new();
    public readonly PilotModel Model = new();
    private readonly PState[] traj = new PState[H + 1];
    private readonly PState[] best = new PState[H + 1];
    private int lastPlan = -1;

    /// <summary>Goal cost of the pilot being in state s at tick t (t = 0 is now).</summary>
    public Func<PState, int, float> Goal = (s, t) => 0f;

    /// <summary>Extra player-hitbox padding (px) applied to every threat.</summary>
    public float Pad = 2f;

    public PState[] BestTrajectory => best;
    public float BestCost { get; private set; }
    public int BestFirstHit { get; private set; }

    /// <summary>BestFirstHit of the last 32 ticks, oldest first (for hit diagnostics).</summary>
    public string HitHistory()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < history.Length; i++) sb.Append(history[(histPos + i) % history.Length]).Append(',');
        return sb.ToString();
    }

    private readonly int[] history = new int[32];
    private int histPos;

    private static Plan[] BuildPlans()
    {
        var list = new System.Collections.Generic.List<Plan>();
        foreach (V v1 in new[] { V.Fall, V.Rise, V.Hover, V.Drop })
        for (int h1 = -1; h1 <= 1; h1++)
        foreach (V v2 in new[] { V.Fall, V.Hover, V.Rise })
        for (int h2 = -1; h2 <= 1; h2++)
            list.Add(new Plan(h1, v1, h2, v2));
        return list.ToArray();
    }

    /// <summary>A predicted contact costs more the sooner it is; margin-only contacts cost a third.</summary>
    private static float Penalty(int core, int margin) =>
        core > 0 ? HitCost * (1.6f - core / (float)H) : HitCost * 0.3f * (1.6f - margin / (float)H);

    /// <summary>Plans and applies this tick's movement controls to the pilot.</summary>
    public void Drive(Player p)
    {
        Model.Capture(p, out PState s0);
        float bestCost = float.MaxValue;
        int bestIdx = 0, bestHit = -1;
        float hw = PilotModel.HalfW + Pad, hh = PilotModel.HalfH + Pad;

        for (int i = 0; i < Plans.Length; i++)
        {
            Plan pl = Plans[i];
            traj[0] = s0;
            PState s = s0;
            float x0 = s.X, x1 = s.X, y0 = s.Y, y1 = s.Y;
            for (int t = 1; t <= H; t++)
            {
                bool first = t <= T1;
                int h = first ? pl.H1 : pl.H2;
                V v = first ? pl.V1 : pl.V2;
                bool jump = v == V.Rise || (v == V.Hover && s.VY > 0f && !s.Ground);
                Model.Step(ref s, h, jump, v == V.Drop);
                traj[t] = s;
                x0 = Math.Min(x0, s.X); x1 = Math.Max(x1, s.X);
                y0 = Math.Min(y0, s.Y); y1 = Math.Max(y1, s.Y);
            }

            float cost = i == lastPlan ? 0f : SwitchCost;
            int firstHit = -1;
            Threats th = Threats;
            for (int k = 0; k < th.Count; k++)
            {
                if (th.MaxX[k] < x0 - hw || th.MinX[k] > x1 + hw || th.MaxY[k] < y0 - hh || th.MinY[k] > y1 + hh) continue;
                th.FirstHit(k, traj, hw, hh, out int core, out int margin);
                if (margin > 0)
                {
                    cost += th.Weight[k] * Penalty(core, margin);
                    int hit = core > 0 ? core : margin;
                    if (core > 0 && (firstHit < 0 || hit < firstHit)) firstHit = hit;
                    if (core > 0) continue;
                }
                for (int t = 2; t <= 24; t += 4)
                {
                    if (t > th.Life[k]) break;
                    float c = th.Clearance(k, t, traj[t], hw, hh);
                    if (c < NearDist) cost += th.Weight[k] * NearCost * (NearDist - c) / NearDist * (1f - t / 32f);
                }
            }
            for (int k = 0; k < th.ShotCount; k++)
            {
                th.ShotHit(th.Shots[k], traj, hw, hh, out int core, out int margin);
                if (margin > 0)
                {
                    cost += th.Shots[k].Weight * Penalty(core, margin);
                    if (core > 0 && (firstHit < 0 || core < firstHit)) firstHit = core;
                }
            }
            if (cost >= bestCost) continue;
            for (int t = 4; t <= H; t += 4) cost += Goal(traj[t], t) * 0.1f;
            if (cost < bestCost)
            {
                bestCost = cost; bestIdx = i; bestHit = firstHit;
                Array.Copy(traj, best, H + 1);
            }
        }

        lastPlan = bestIdx;
        BestCost = bestCost;
        BestFirstHit = bestHit;
        history[histPos] = bestHit;
        histPos = (histPos + 1) % history.Length;
        Plan bp = Plans[bestIdx];
        p.controlLeft = bp.H1 < 0;
        p.controlRight = bp.H1 > 0;
        p.controlJump = bp.V1 == V.Rise || (bp.V1 == V.Hover && s0.VY > 0f && !s0.Ground);
        p.controlDown = bp.V1 == V.Drop;
        p.controlUp = false;
    }

    public void Reset() => lastPlan = -1;
}
