using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace BossPilot.Bosses.SlimesEye;

/// <summary>
/// Forward model of one boss for rollouts. The boss's next moves depend on where the player
/// will be (jump direction, dash aim), so it is stepped inside every rollout with the
/// rollout's predicted player position.
/// </summary>
public interface IBossSim
{
    /// <summary>Restores the state captured from the live NPC for this replan.</summary>
    void Reset();

    /// <summary>One AI tick; returns the contact box (Box.None while hidden or harmless).</summary>
    Box Step(Vector2 playerCenter, float playerTop, float playerBottom);

    /// <summary>Center of the live boss as captured (for prediction-error checks).</summary>
    Vector2 Now();

    /// <summary>Gap to attacks the model spawns itself (projectiles not yet in the world), after Step.</summary>
    float ExtraGap(in Box player) => float.MaxValue;
}

/// <summary>Open-loop control sequence over the horizon: two horizontal segments, up to two jump holds, a platform drop.</summary>
public struct Plan
{
    public sbyte D1, D2;
    public byte T1;
    public sbyte J1, J2;      // jump hold start (-1 = none)
    public byte J1Len, J2Len;
    public byte DownLen;

    public readonly int Dir(int t) => t < T1 ? D1 : D2;
    public readonly bool Jump(int t) => (J1 >= 0 && t >= J1 && t < J1 + J1Len) || (J2 >= 0 && t >= J2 && t < J2 + J2Len);
    public readonly bool Down(int t) => t < DownLen;

    /// <summary>The same plan seen dt ticks later (used to keep committing to the current plan).</summary>
    public readonly Plan Shift(int dt)
    {
        Plan p = this;
        p.T1 = (byte)Math.Max(0, T1 - dt);
        Shift(ref p.J1, ref p.J1Len, dt);
        Shift(ref p.J2, ref p.J2Len, dt);
        p.DownLen = (byte)Math.Max(0, DownLen - dt);
        return p;
    }

    private static void Shift(ref sbyte start, ref byte len, int dt)
    {
        if (start < 0) return;
        int s = start - dt, e = start + len - dt;
        if (e <= 0) { start = -1; len = 0; return; }
        start = (sbyte)Math.Max(0, s);
        len = (byte)(e - Math.Max(0, s));
    }

    public override readonly string ToString() => $"{D1}/{D2}@{T1} j{J1}+{J1Len} j{J2}+{J2Len} v{DownLen}";
}

/// <summary>
/// Sampling MPC over the player's native controls. Every few ticks it rolls out ~100 control
/// sequences (two horizontal segments switching at 6/15/30 ticks x jump, double-jump/flight and
/// platform-drop variants) through <see cref="PlayerModel"/> against the boss model and the predicted threat
/// tracks, scores contact (hard) and clearance below <see cref="Margin"/> (soft) plus a
/// boss-specific positional preference, and executes the cheapest plan open-loop until the
/// next replan.
/// </summary>
public sealed class Planner
{
    public const int H = ThreatSet.Horizon, PrefStep = 3;

    public readonly PlayerModel Model = new();
    public readonly ThreatSet Threats = new();
    public IBossSim Boss;

    /// <summary>Preference cost of the predicted player center and boss box (Box.None if hidden) at sample k (tick k*PrefStep).</summary>
    public Func<Vector2, Box, int, float> Preference = (_, _, _) => 0f;

    public float Margin = 14f, HitCost = 10000f, SoftCost = 4f;
    public int ReplanEvery = 3;

    public Plan Current => cur;
    public float LastCost { get; private set; }
    public float LastGap { get; private set; }
    public int Replans { get; private set; }

    /// <summary>Debug: when set, the next replan records every candidate's cost/gap/first-hit tick/preference.</summary>
    public bool DumpNext;
    public string LastDump;
    private int lastHitTick;
    private float lastPref;

    private readonly List<Plan> cands = [];
    private Plan cur;
    private int age = 1 << 20;

    public void Invalidate()
    {
        age = 1 << 20;
        havePred = false;
    }

    /// <summary>Sets the movement controls for this tick (replanning when due).</summary>
    public void Drive(Player p)
    {
        if (age >= ReplanEvery) { Replan(p); age = 0; }
        int a = age++;
        int d = cur.Dir(a);
        p.controlLeft = d < 0;
        p.controlRight = d > 0;
        p.controlJump = cur.Jump(a);
        p.controlDown = cur.Down(a);
    }

    /// <summary>Debug: open-loop prediction error of the executed plan over one replan interval (px).</summary>
    public float ErrPlayer, ErrBoss;
    private Vector2 predPlayer, predBoss;
    private bool havePred;

    private void Replan(Player p)
    {
        Model.Load(p);
        Body start = Model.Snapshot(p);
        if (havePred)
        {
            ErrPlayer = Vector2.Distance(predPlayer, p.Center);
            ErrBoss = Boss != null && !float.IsNaN(predBoss.X) ? Vector2.Distance(predBoss, Boss.Now()) : 0f;
        }
        Threats.Build(p);
        BuildCandidates(start);
        Plan keep = cur.Shift(age);
        cands.Add(keep);

        float best = float.MaxValue, bestGap = 0f;
        Plan bestPlan = keep;
        System.Text.StringBuilder dump = DumpNext ? new() : null;
        for (int i = 0; i < cands.Count; i++)
        {
            float cost = Evaluate(start, cands[i], out float gap);
            if (i == cands.Count - 1) cost -= 3f; // hysteresis towards the plan being executed
            if (cost < best) { best = cost; bestPlan = cands[i]; bestGap = gap; }
            dump?.Append($"{cands[i]}={cost:F0}/{gap:F0}/{lastHitTick}/{lastPref:F0}; ");
        }
        if (dump != null) { LastDump = dump.ToString(); DumpNext = false; }
        cur = bestPlan;
        // Record where the chosen plan puts the player and boss ReplanEvery ticks from now.
        Body pb = start;
        Boss?.Reset();
        predBoss = new Vector2(float.NaN);
        for (int t = 0; t < ReplanEvery; t++)
        {
            Model.Step(ref pb, cur.Dir(t), cur.Jump(t), cur.Down(t));
            if (Boss != null)
            {
                Box bb = Boss.Step(pb.Center(Model.W, Model.H), pb.Pos.Y, pb.Pos.Y + Model.H);
                predBoss = bb.Valid ? new Vector2(bb.X, bb.Y) : new Vector2(float.NaN);
            }
        }
        predPlayer = pb.Center(Model.W, Model.H);
        havePred = true;
        LastCost = best;
        LastGap = bestGap;
        Replans++;
    }

    /// <summary>Segment switch times tried for the two horizontal segments.</summary>
    public int[] SwitchTimes = [6, 15, 30];

    private void BuildCandidates(in Body s)
    {
        cands.Clear();
        int jh = Model.JumpHeight + 1;
        bool extra = Model.Cloud || Model.Wings;
        bool onPlatform = s.Grounded && StandsOnPlatform(s);
        foreach (int t1 in SwitchTimes)
        for (int d1 = -1; d1 <= 1; d1++)
        for (int d2 = -1; d2 <= 1; d2++)
        {
            if (d1 == d2 && t1 != SwitchTimes[0]) continue; // same plan for every switch time
            Add(d1, d2, t1, -1, 0, -1, 0, 0);
            Add(d1, d2, t1, 0, jh, -1, 0, 0);
            Add(d1, d2, t1, 0, 5, -1, 0, 0);
            Add(d1, d2, t1, 12, jh, -1, 0, 0);
            if (extra) Add(d1, d2, t1, 0, jh, jh + 3, Model.Wings ? 45 : 14, 0);
            if (onPlatform) Add(d1, d2, t1, -1, 0, -1, 0, 10);
        }
    }

    private void Add(int d1, int d2, int t1, int j1, int j1Len, int j2, int j2Len, int down) => cands.Add(new Plan
    {
        D1 = (sbyte)d1, D2 = (sbyte)d2, T1 = (byte)t1,
        J1 = (sbyte)j1, J1Len = (byte)j1Len, J2 = (sbyte)j2, J2Len = (byte)j2Len, DownLen = (byte)down,
    });

    private bool StandsOnPlatform(in Body s)
    {
        int r = (int)((s.Pos.Y + Model.H + 1f) / 16f);
        int i0 = (int)(s.Pos.X / 16f), i1 = (int)((s.Pos.X + Model.W - 0.01f) / 16f);
        bool plat = false;
        for (int i = i0; i <= i1; i++)
        {
            if (Tiles.Solid(i, r)) return false;
            plat |= Tiles.Platform(i, r);
        }
        return plat;
    }

    /// <summary>Rollout cost; gap = smallest predicted clearance over the horizon.</summary>
    public float Evaluate(Body b, in Plan plan, out float minGap)
    {
        Boss?.Reset();
        float cost = 0f;
        float hw = Model.W * 0.5f, hh = Model.H * 0.5f;
        bool hit = false;
        lastHitTick = -1;
        lastPref = 0f;
        minGap = float.MaxValue;
        for (int t = 1; t <= H; t++)
        {
            Model.Step(ref b, plan.Dir(t - 1), plan.Jump(t - 1), plan.Down(t - 1));
            Vector2 c = b.Center(Model.W, Model.H);
            Box pb = new(c.X, c.Y, hw, hh);
            Box bossBox = Box.None;
            float gap = Threats.MinGap(t, pb);
            if (Boss != null)
            {
                bossBox = Boss.Step(c, b.Pos.Y, b.Pos.Y + Model.H);
                if (bossBox.Valid) gap = Math.Min(gap, pb.Gap(bossBox));
                gap = Math.Min(gap, Boss.ExtraGap(pb));
            }
            float w = 1f - 0.5f * t / H;
            if (gap < 0f)
            {
                // A predicted hit outweighs any preference; among hitting plans the later hit wins.
                if (!hit) { cost += HitCost * (2f - (float)t / H); lastHitTick = t; }
                hit = true;
                cost += 30f;
            }
            else if (gap < Margin) cost += (Margin - gap) * SoftCost / 3f * w;
            if (gap < minGap) minGap = gap;
            if (t % PrefStep == 0)
            {
                float pc = Preference(c, bossBox, t / PrefStep) * w;
                cost += pc;
                lastPref += pc;
            }
        }
        return cost;
    }
}

/// <summary>Aiming with lead and firing for the held ranged weapon.</summary>
public static class Gunner
{
    /// <summary>Projectile speed (px/tick, extra updates included) and per-tick gravity after the delay.</summary>
    public static (float speed, float grav, float delay) Ballistics(Player p)
    {
        Item w = p.HeldItem;
        float speed = w.shootSpeed;
        int proj = w.shoot;
        if (w.useAmmo > 0)
        {
            Item ammo = p.ChooseAmmo(w);
            if (ammo != null)
            {
                speed += ammo.shootSpeed;
                if (ammo.shoot > 0) proj = ammo.shoot;
            }
            if (w.useAmmo == AmmoID.Arrow && p.archery) speed *= 1.2f;
        }
        int updates = 1;
        float grav = 0f, delay = 0f;
        if (proj > 0 && ContentSamples.ProjectilesByType.TryGetValue(proj, out Projectile sample))
        {
            updates = 1 + sample.extraUpdates;
            if (sample.aiStyle == ProjAIStyleID.Arrow && sample.arrow && proj != ProjectileID.JestersArrow)
            {
                grav = proj == ProjectileID.HolyArrow ? 0.07f : 0.1f;
                delay = proj == ProjectileID.HolyArrow ? 20f : 15f;
            }
        }
        return (speed * updates, grav * updates, delay / updates);
    }

    /// <summary>World point to put the cursor on so a shot from the player meets the target.</summary>
    public static Vector2 Lead(Player p, Vector2 target, Vector2 targetVel)
    {
        (float speed, float grav, float delay) = Ballistics(p);
        if (speed <= 0.1f) return target;
        Vector2 from = p.RotatedRelativePoint(p.MountedCenter);
        Vector2 aim = target;
        for (int i = 0; i < 3; i++)
        {
            float t = Vector2.Distance(from, aim) / speed;
            aim = target + targetVel * Math.Min(t, 60f);
            float ft = Math.Max(0f, t - delay);
            aim.Y -= 0.5f * grav * ft * ft;
        }
        return aim;
    }

    /// <summary>Aims at world and holds/taps use (non-auto weapons are tapped whenever the swing ends).</summary>
    public static void Fire(Player p, Vector2 world, bool fire = true)
    {
        Common.FightContext.Aim(world);
        bool auto = p.HeldItem.autoReuse || p.autoReuseAllWeapons;
        p.controlUseItem = fire && (auto || (p.itemAnimation == 0 && p.releaseUseItem));
    }
}
