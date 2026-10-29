using System;
using Terraria;

namespace BossPilot.Bosses.Mechs;

/// <summary>
/// Short-horizon model-predictive movement: every tick, roll the pilot model forward
/// H ticks under each of 216 two-segment control plans (switch after 5 or 14 ticks), score each against the shared
/// threat prediction (swept hitbox tests) plus a boss-specific goal, and apply the first
/// tick of the best one. Re-planning every tick absorbs model and prediction error.
/// </summary>
public sealed class Planner
{
    public enum V : byte { Fall, Rise, Hover, Drop }

    public const int H = Threats.H;
    private const float HitCost = 1000f, NearCost = 25f, NearDist = 40f, SwitchCost = 3f;

    private readonly record struct Plan(int H1, V V1, int T1, int H2, V V2);

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
    private readonly int[] freeHistory = new int[32];
    private readonly int[] tagHistory = new int[32];

    /// <summary>Tag of what the best plan was predicted to hit, last 32 ticks, oldest first.</summary>
    public string TagHistory()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < tagHistory.Length; i++) sb.Append(tagHistory[(histPos + i) % tagHistory.Length]).Append(',');
        return sb.ToString();
    }

    /// <summary>Plans without a predicted core hit, last 32 ticks, oldest first.</summary>
    public string FreeHistory()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < freeHistory.Length; i++) sb.Append(freeHistory[(histPos + i) % freeHistory.Length]).Append(',');
        return sb.ToString();
    }
    private int histPos;

    private static Plan[] BuildPlans()
    {
        var list = new System.Collections.Generic.List<Plan>();
        foreach (int t1 in new[] { 5, 14 })
        foreach (V v1 in new[] { V.Fall, V.Rise, V.Hover, V.Drop })
        for (int h1 = -1; h1 <= 1; h1++)
        foreach (V v2 in new[] { V.Fall, V.Hover, V.Rise })
        for (int h2 = -1; h2 <= 1; h2++)
            list.Add(new Plan(h1, v1, t1, h2, v2));
        return list.ToArray();
    }

    /// <summary>
    /// A predicted contact costs more the sooner it is; margin-only contacts cost 30%, and
    /// contacts inside the post-hit immunity window 20% (harmless, but the pilot should not
    /// loiter in the danger zone until immunity runs out).
    /// </summary>
    private static float Penalty(int core, int margin, int from)
    {
        int t = core > 0 ? core : margin;
        float c = HitCost * (core > 0 ? 1f : 0.3f) * (1.6f - t / (float)H);
        return t < from ? c * 0.2f : c;
    }

    /// <summary>Plans and applies this tick's movement controls to the pilot.</summary>
    public void Drive(Player p)
    {
        Model.Capture(p, out PState s0);
        // Contacts during the post-hit immunity frames do nothing (Player.Hurt returns early).
        int from = p.immune ? Math.Max(1, p.immuneTime) : 1;
        TrackModelError(s0);
        float bestCost = float.MaxValue;
        int bestIdx = 0, bestHit = -1, free = 0, bestTag = 0;
        float hw = PilotModel.HalfW + Pad, hh = PilotModel.HalfH + Pad;

        for (int i = 0; i < Plans.Length; i++)
        {
            Plan pl = Plans[i];
            traj[0] = s0;
            PState s = s0;
            float x0 = s.X, x1 = s.X, y0 = s.Y, y1 = s.Y;
            for (int t = 1; t <= H; t++)
            {
                bool first = t <= pl.T1;
                int h = first ? pl.H1 : pl.H2;
                V v = first ? pl.V1 : pl.V2;
                bool jump = v == V.Rise || (v == V.Hover && s.VY > 0f && !s.Ground);
                Model.Step(ref s, h, jump, v == V.Drop);
                traj[t] = s;
                x0 = Math.Min(x0, s.X); x1 = Math.Max(x1, s.X);
                y0 = Math.Min(y0, s.Y); y1 = Math.Max(y1, s.Y);
            }

            float cost = i == lastPlan ? 0f : SwitchCost;
            int firstHit = -1, hitTag = 0;
            Threats th = Threats;
            for (int k = 0; k < th.Count; k++)
            {
                if (th.MaxX[k] < x0 - hw || th.MinX[k] > x1 + hw || th.MaxY[k] < y0 - hh || th.MinY[k] > y1 + hh) continue;
                th.FirstHit(k, traj, hw, hh, out int core, out int margin);
                if (margin > 0)
                {
                    cost += th.Weight[k] * Penalty(core, margin, from);
                    if (core >= from && (firstHit < 0 || core < firstHit)) { firstHit = core; hitTag = th.Tag[k]; }
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
                    cost += th.Shots[k].Weight * Penalty(core, margin, from);
                    if (core >= from && (firstHit < 0 || core < firstHit)) { firstHit = core; hitTag = th.Shots[k].Tag; }
                }
            }
            if (firstHit < 0) free++;
            if (cost >= bestCost) continue;
            for (int t = 4; t <= H; t += 4) cost += Goal(traj[t], t) * 0.1f;
            if (cost < bestCost)
            {
                bestCost = cost; bestIdx = i; bestHit = firstHit; bestTag = hitTag;
                Array.Copy(traj, best, H + 1);
            }
        }

        lastPlan = bestIdx;
        BestCost = bestCost;
        BestFirstHit = bestHit;
        history[histPos] = bestHit;
        freeHistory[histPos] = free;
        tagHistory[histPos] = bestTag;
        histPos = (histPos + 1) % history.Length;
        Plan bp = Plans[bestIdx];
        p.controlLeft = bp.H1 < 0;
        p.controlRight = bp.H1 > 0;
        p.controlJump = bp.V1 == V.Rise || (bp.V1 == V.Hover && s0.VY > 0f && !s0.Ground);
        p.controlDown = bp.V1 == V.Drop;
        p.controlUp = false;
    }

    public void Reset() => lastPlan = -1;

    // One-tick model error of the executed plan (diagnostics, MECHS_MODEL_LOG=1).
    private static readonly bool LogModel = Environment.GetEnvironmentVariable("MECHS_MODEL_LOG") == "1";
    private uint lastTick;
    private int errN;
    private float errX, errY, errVX, errVY, errMaxVY;
    private void TrackModelError(in PState s0)
    {
        if (!LogModel) return;
        if (lastTick + 1 == Main.GameUpdateCount)
        {
            PState e = best[1];
            errN++;
            errX += Math.Abs(e.X - s0.X); errY += Math.Abs(e.Y - s0.Y);
            errVX += Math.Abs(e.VX - s0.VX); float dvy = Math.Abs(e.VY - s0.VY); errVY += dvy;
            errMaxVY = Math.Max(errMaxVY, dvy);
            if (errN >= 600)
            {
                string dir = global::BossPilot.Common.Headless.HeadlessSim.OutDir;
                if (dir != null)
                    System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "model_err.jsonl"),
                        $"{{\"n\":{errN},\"x\":{errX / errN:F3},\"y\":{errY / errN:F3},\"vx\":{errVX / errN:F3},\"vy\":{errVY / errN:F3},\"maxvy\":{errMaxVY:F2}}}\n");
                errN = 0; errX = errY = errVX = errVY = errMaxVY = 0f;
            }
        }
        lastTick = Main.GameUpdateCount;
    }
}
