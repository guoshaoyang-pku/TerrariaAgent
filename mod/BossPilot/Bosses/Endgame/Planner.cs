using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;

namespace BossPilot.Bosses.Endgame;

/// <summary>
/// Pilot dynamics on the Witch's Broom, replicated from Player.HorizontalMovement
/// (mounted: maxRunSpeed = accRunSpeed = 9, runAcceleration = 0.16, airborne
/// coast slowdown runSlowdown/2) and Mount.Hover (vertical 0.16 accel towards
/// -8 / +8 / 0, doubled while reversing). Positions are centers in world px.
/// </summary>
public static class BroomModel
{
    public const float Accel = 0.16f, MaxX = 9f, MaxY = 8f, Slowdown = 0.2f;
    public static readonly Vector2 Half = new(10f, 21f);

    public static void Step(ref Vector2 pos, ref Vector2 vel, int ux, int uy)
    {
        float vx = vel.X;
        if (ux != 0)
        {
            if (vx * ux < MaxX)
            {
                if (vx * ux < -Slowdown) vx += ux * Slowdown;
                vx += ux * Accel;
            }
        }
        else if (vx > Slowdown * 0.5f) vx -= Slowdown * 0.5f;
        else if (vx < -Slowdown * 0.5f) vx += Slowdown * 0.5f;
        else vx = 0f;

        float vy = vel.Y, lo = -0.001f, hi = -0.001f;
        if (uy < 0) { vy -= Accel; lo = -MaxY; }
        else if (uy > 0) { vy += Accel; hi = MaxY; }
        if (vy < lo) vy = lo - vy < Accel ? lo : vy + Accel;
        else if (vy > hi) vy = vy - hi < Accel ? hi : vy - Accel;

        vel = new Vector2(vx, vy);
        pos += vel;
    }
}

/// <summary>
/// Receding-horizon planner over the broom model. Candidates are input plans
/// of one or two segments (left/none/right x up/none/down, switching after 12
/// ticks) plus the previous best plan shifted by a tick. Each is rolled
/// forward and scored on predicted clearance to every hazard, distance to the
/// plan's goal and the keep-in box; the best plan's first input is applied and
/// the search repeats next tick.
/// </summary>
public sealed class Planner
{
    public int Horizon = 60;
    /// <summary>Every tick is scored up to here (fast shots cannot slip between samples), then every 3rd.</summary>
    public int Dense = 24;
    /// <summary>Extra px kept between hitboxes on top of the exact overlap test.</summary>
    public float Margin = 6f;
    /// <summary>Clearance below which a soft penalty starts.</summary>
    public float Soft = 48f;
    public Vector2 Goal;
    public float GoalWeight = 0.01f;
    /// <summary>Keep-in region (world px) for the pilot center.</summary>
    public Rectangle Box;

    public int LastUx { get; private set; }
    public int LastUy { get; private set; }
    public float LastCost { get; private set; }
    public float LastMinClearance { get; private set; }

    private readonly struct PlanSpec(int ux1, int uy1, int switchAt, int ux2, int uy2)
    {
        public readonly int Ux1 = ux1, Uy1 = uy1, SwitchAt = switchAt, Ux2 = ux2, Uy2 = uy2;
        public (int, int) At(int t) => t <= SwitchAt ? (Ux1, Uy1) : (Ux2, Uy2);
        public PlanSpec Shift() => SwitchAt > 1 ? new PlanSpec(Ux1, Uy1, SwitchAt - 1, Ux2, Uy2) : new PlanSpec(Ux2, Uy2, int.MaxValue, Ux2, Uy2);
    }

    private static readonly int[] Switches = [12];
    private readonly List<PlanSpec> candidates = [];
    private PlanSpec previous = new(0, 0, int.MaxValue, 0, 0);

    private readonly List<Hazard> fixedHazards = [];
    private readonly List<Hazard> reactive = [];
    private List<Shape>[] fixedShapes = [];
    private readonly List<Shape> scratch = [];

    private int[] evalTicks = [];

    public (int ux, int uy) Plan(Vector2 pos, Vector2 vel, List<Hazard> hazards)
    {
        if (evalTicks.Length == 0 || evalTicks[^1] != Horizon)
        {
            var e = new List<int>();
            for (int t = 1; t <= Horizon; t++) if (t <= Dense || (t - Dense) % 3 == 0) e.Add(t);
            evalTicks = [.. e];
        }
        int steps = evalTicks.Length;
        if (fixedShapes.Length != steps + 1)
        {
            fixedShapes = new List<Shape>[steps + 1];
            for (int i = 0; i <= steps; i++) fixedShapes[i] = [];
        }
        fixedHazards.Clear();
        reactive.Clear();
        foreach (Hazard h in hazards) (h.Reactive ? reactive : fixedHazards).Add(h);

        // Pilot-independent hazards are stepped once and shared by every candidate.
        foreach (List<Shape> l in fixedShapes) l.Clear();
        scratch.Clear();
        foreach (Hazard h in fixedHazards)
        {
            h.Rewind();
            int tick = 0;
            for (int k = 1; k <= steps; k++)
            {
                while (tick < evalTicks[k - 1] && !h.Dead) { h.Step(Vector2.Zero); tick++; }
                if (h.Dead) break;
                h.Emit(scratch);
                // Keep only shapes the broom (<= 9 px/tick) could reach by then.
                float reach = BroomModel.MaxX * tick + Soft + Margin + 30f;
                foreach (Shape sh in scratch) if (sh.Reach(pos) <= reach) fixedShapes[k].Add(sh);
                scratch.Clear();
            }
        }

        candidates.Clear();
        candidates.Add(previous.Shift());
        for (int ux = -1; ux <= 1; ux++)
        for (int uy = -1; uy <= 1; uy++)
        {
            candidates.Add(new PlanSpec(ux, uy, int.MaxValue, ux, uy));
            foreach (int sw in Switches)
                for (int vx = -1; vx <= 1; vx++)
                for (int vy = -1; vy <= 1; vy++)
                    if (vx != ux || vy != uy) candidates.Add(new PlanSpec(ux, uy, sw, vx, vy));
        }

        float best = float.MaxValue, bestMin = 0;
        PlanSpec bestPlan = candidates[0];
        foreach (PlanSpec c in candidates)
        {
            float cost = Rollout(pos, vel, c, steps, out float minC);
            (int ux, int uy) = c.At(1);
            if (ux != LastUx || uy != LastUy) cost += 0.5f;
            if (cost < best) { best = cost; bestPlan = c; bestMin = minC; }
        }
        previous = bestPlan;
        (LastUx, LastUy) = bestPlan.At(1);
        LastCost = best; LastMinClearance = bestMin;
        return (LastUx, LastUy);
    }

    private float Rollout(Vector2 pos, Vector2 vel, PlanSpec plan, int steps, out float minClear)
    {
        foreach (Hazard h in reactive) h.Rewind();
        float danger = 0, goal = 0, bounds = 0;
        minClear = float.MaxValue;
        int t = 0;
        for (int k = 1; k <= steps; k++)
        {
            while (t < evalTicks[k - 1])
            {
                (int ux, int uy) = plan.At(++t);
                BroomModel.Step(ref pos, ref vel, ux, uy);
                foreach (Hazard h in reactive) if (!h.Dead) h.Step(pos);
            }
            scratch.Clear();
            foreach (Hazard h in reactive) if (!h.Dead) h.Emit(scratch);

            float c = float.MaxValue;
            foreach (Shape sh in fixedShapes[k]) c = Math.Min(c, sh.Clearance(pos, BroomModel.Half));
            foreach (Shape sh in scratch) c = Math.Min(c, sh.Clearance(pos, BroomModel.Half));
            c -= Margin;
            minClear = Math.Min(minClear, c);
            // Weight per tick of horizon covered by this sample.
            float span = k == 1 ? evalTicks[0] : evalTicks[k - 1] - evalTicks[k - 2];
            float w = span / (1f + t / 24f);
            if (c < 0) danger += 500f * w * (1f + Math.Min(-c, 60f) / 20f);
            else if (c < Soft) { float q = 1f - c / Soft; danger += 6f * w * q * q; }

            goal += Vector2.Distance(pos, Goal);
            float ox = Math.Max(Box.Left - pos.X, pos.X - Box.Right);
            float oy = Math.Max(Box.Top - pos.Y, pos.Y - Box.Bottom);
            if (ox > 0) bounds += ox;
            if (oy > 0) bounds += oy;
            if (k % 3 == 0 && Collision.SolidCollision(pos - BroomModel.Half, 20, 42)) bounds += 400f;
        }
        return danger + goal * GoalWeight / steps * 10f + bounds * 0.05f;
    }
}
