using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace BossPilot.Bosses.JungleTempleOcean;

/// <summary>Axis-aligned box in world pixels; X0 &gt; X1 marks "no threat this tick".</summary>
public struct Box
{
    public float X0, Y0, X1, Y1;

    public Box(float x0, float y0, float x1, float y1) { X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; }

    public static readonly Box None = new(1, 0, -1, 0);
    public bool Valid => X0 <= X1;
    public Vector2 Center => new((X0 + X1) * 0.5f, (Y0 + Y1) * 0.5f);
    public static Box Around(Vector2 c, float hw, float hh) => new(c.X - hw, c.Y - hh, c.X + hw, c.Y + hh);
    public static Box Of(Entity e) => new(e.position.X, e.position.Y, e.position.X + e.width, e.position.Y + e.height);
    public Box Offset(Vector2 d) => new(X0 + d.X, Y0 + d.Y, X1 + d.X, Y1 + d.Y);

    /// <summary>Separation in px (Chebyshev); negative when the boxes overlap.</summary>
    public static float Gap(in Box a, in Box b) =>
        Math.Max(Math.Max(a.X0 - b.X1, b.X0 - a.X1), Math.Max(a.Y0 - b.Y1, b.Y0 - a.Y1));
}

/// <summary>
/// Movement constants of the pilot, captured from the live player right before
/// HorizontalMovement (ground and airborne values differ: wings override run speed in air).
/// </summary>
public sealed class MoveStats
{
    public float MaxRunG = 3f, AccRunG = 6.75f, RunAccG = 0.08f, SlowG = 0.2f;
    public float MaxRunA = 3f, AccRunA = 7.5f, RunAccA = 0.08f, SlowA = 0.2f;
    public bool SeenAir;
    public float Gravity = 0.4f, MaxFall = 10f, JumpSpeed = 5.01f;
    public int JumpHeight = 15;
    public float WingTimeMax;
    public int WingsLogic;
    public int RocketTimeMax;
    public bool RocketBoots;
    // Generic wing ascent constants (Player.WingMovement); a few wings override them.
    public float WConst = 0.1f, WFalling = 0.5f, WRising = 0.1f, WMaxCan = 0.5f, WMaxAsc = 1.5f;

    public void SetWingCurve(int logic)
    {
        WConst = 0.1f; WFalling = 0.5f; WRising = 0.1f; WMaxCan = 0.5f; WMaxAsc = 1.5f;
        switch (logic)
        {
            case 8 or 11 or 24 or 27 or 22: WMaxAsc = 1.66f; break;
            case 21 or 12 or 20 or 23: WMaxAsc = 1.805f; break;
            case 26 or 37: WFalling = 0.75f; WRising = 0.15f; WMaxCan = 1f; WMaxAsc = 2.5f; WConst = 0.125f; break;
            case 29 or 32: WFalling = 0.85f; WRising = 0.15f; WMaxCan = 1f; WMaxAsc = 3f; WConst = 0.135f; break;
        }
    }
}

/// <summary>Records the pilot's per-tick movement constants for the planner's player model.</summary>
public sealed class ProbePlayer : ModPlayer
{
    public static readonly MoveStats Stats = new();

    public override void PostUpdateRunSpeeds()
    {
        if (Player.whoAmI != Main.myPlayer) return;
        MoveStats s = Stats;
        if (Player.velocity.Y == 0f)
        {
            s.MaxRunG = Player.maxRunSpeed; s.AccRunG = Player.accRunSpeed; s.RunAccG = Player.runAcceleration; s.SlowG = Player.runSlowdown;
            if (!s.SeenAir)
            {
                // Before the first airborne tick, guess the wing override (typical 7.5) for the air values.
                s.MaxRunA = s.MaxRunG; s.AccRunA = Math.Max(s.AccRunG, 7.5f); s.RunAccA = s.RunAccG; s.SlowA = s.SlowG;
            }
        }
        else
        {
            s.MaxRunA = Player.maxRunSpeed; s.AccRunA = Player.accRunSpeed; s.RunAccA = Player.runAcceleration; s.SlowA = Player.runSlowdown;
            s.SeenAir = true;
        }
        s.Gravity = Player.gravity;
        s.MaxFall = Player.maxFallSpeed;
        s.JumpSpeed = Player.jumpSpeed;
        s.JumpHeight = Player.jumpHeight;
        s.WingTimeMax = Player.wingTimeMax;
        s.RocketTimeMax = Player.rocketTimeMax;
        s.RocketBoots = Player.rocketBoots != 0;
        if (s.WingsLogic != Player.wingsLogic)
        {
            s.WingsLogic = Player.wingsLogic;
            s.SetWingCurve(s.WingsLogic);
        }
    }
}

/// <summary>Predicted pilot state (mirrors the Player fields the movement code reads).</summary>
public struct PilotState
{
    public Vector2 Pos, Vel;
    public int Jump;
    public float WingTime;
    public int RocketTime;
    public bool ReleaseJump;

    public const int W = 20, Hgt = 42;
    public Vector2 Center => Pos + new Vector2(W / 2f, Hgt / 2f);
    public Box Box => new(Pos.X, Pos.Y, Pos.X + W, Pos.Y + Hgt);
    public bool Grounded => Vel.Y == 0f;

    public static PilotState From(Player p) => new()
    {
        Pos = p.position, Vel = p.velocity, Jump = p.jump, WingTime = p.wingTime,
        RocketTime = p.rocketTime, ReleaseJump = p.releaseJump,
    };
}

/// <summary>
/// Player kinematics for planning: Player.HorizontalMovement, JumpMovement, WingMovement,
/// gravity/glide and Collision.TileCollision in kernel order (no slopes, water or dashes).
/// </summary>
public static class PilotSim
{
    /// <param name="h">-1 left, 0 none, 1 right.</param>
    /// <param name="v">1 hold jump (jump/fly), 0 none, -1 hold down (drop through platforms).</param>
    public static void Step(ref PilotState s, int h, int v, MoveStats m)
    {
        bool air = s.Vel.Y != 0f;
        bool wings = m.WingsLogic > 0;
        bool airStats = air && wings;
        float maxRun = airStats ? m.MaxRunA : m.MaxRunG, accRun = airStats ? m.AccRunA : m.AccRunG;
        float acc = airStats ? m.RunAccA : m.RunAccG, slow = airStats ? m.SlowA : m.SlowG;
        ref float vx = ref s.Vel.X;
        if (h < 0 && vx > -maxRun) { if (vx > slow) vx -= slow; vx -= acc; }
        else if (h > 0 && vx < maxRun) { if (vx < -slow) vx += slow; vx += acc; }
        else if (h < 0 && vx > -accRun)
        {
            if (!air || wings) { if (vx > slow) vx -= slow; vx -= acc * 0.2f; if (wings) vx -= acc * 0.2f; }
        }
        else if (h > 0 && vx < accRun)
        {
            if (!air || wings) { if (vx < -slow) vx += slow; vx += acc * 0.2f; if (wings) vx += acc * 0.2f; }
        }
        else if (!air) { vx = vx > slow ? vx - slow : vx < -slow ? vx + slow : 0f; }
        else { float sl = slow * 0.5f; vx = vx > sl ? vx - sl : vx < -sl ? vx + sl : 0f; }

        bool up = v > 0;
        ref float vy = ref s.Vel.Y;
        if (up)
        {
            if (s.Jump > 0)
            {
                if (vy == 0f) s.Jump = 0;
                else { vy = -m.JumpSpeed; s.Jump--; }
            }
            else if (vy == 0f && s.ReleaseJump)
            {
                vy = -m.JumpSpeed;
                s.Jump = m.JumpHeight;
            }
            s.ReleaseJump = false;
        }
        else
        {
            s.Jump = 0;
            s.ReleaseJump = true;
        }
        if (vy == 0f && s.ReleaseJump) s.WingTime = m.WingTimeMax;
        bool flying = wings && up && s.WingTime > 0f && s.Jump == 0 && vy != 0f;
        if (flying)
        {
            vy -= m.WConst;
            if (vy > 0f) vy -= m.WFalling;
            else if (vy > -m.JumpSpeed * m.WMaxCan) vy -= m.WRising;
            if (vy < -m.JumpSpeed * m.WMaxAsc) vy = -m.JumpSpeed * m.WMaxAsc;
            s.WingTime -= 1f;
        }
        if (wings && m.RocketBoots && vy != 0f && s.RocketTime != 0)
        {
            s.WingTime = Math.Min(s.WingTime + s.RocketTime * 6, m.WingTimeMax + s.RocketTime * 6);
            s.RocketTime = 0;
        }
        if (vy == 0f) s.RocketTime = m.RocketTimeMax;
        if (!flying)
        {
            if (wings && up && vy > 0f)
            {
                vy += m.Gravity / 3f;
                if (vy > m.MaxFall / 3f) vy = m.MaxFall / 3f;
            }
            else vy += m.Gravity;
        }
        if (vy > m.MaxFall) vy = m.MaxFall;

        Vector2 vel = Collision.TileCollision(s.Pos, s.Vel, PilotState.W, PilotState.Hgt, v < 0, false, 1);
        if (Collision.up) s.Jump = 0;
        s.Vel = vel;
        s.Pos += vel;
    }
}

/// <summary>
/// One danger source predicted over the planning horizon. Passive threats are predicted
/// once per decision; reactive ones (aimed or homing) are re-predicted for every candidate
/// pilot path because their motion depends on where the pilot goes.
/// </summary>
public abstract class Threat
{
    public readonly Box[] Boxes = new Box[Planner.H];
    /// <summary>Safety margin in px at k = 0 and its growth per tick of look-ahead.</summary>
    public float Margin = 4f, Grow = 0.15f;
    /// <summary>Cost multiplier: 1 for real threats, small for hypothetical ones (shots that may be fired).</summary>
    public float Weight = 1f;
    public virtual bool Reactive => false;
    public string Tag = "";

    /// <summary>Fill Boxes[k] = danger box k+1 ticks ahead. centers[k] = pilot center at the same tick (reactive only).</summary>
    public abstract void Predict(Vector2[] centers);
}

/// <summary>Constant-velocity body (most projectiles), optionally with gravity.</summary>
public sealed class LinearThreat : Threat
{
    private Vector2 pos, vel, half;
    private float gravity, maxFall;
    private int life, steps;

    public LinearThreat Set(Vector2 center, Vector2 velocity, float hw, float hh, int ticksLeft, int updatesPerTick = 1, float gravity = 0f, float maxFall = 16f)
    {
        pos = center; vel = velocity; half = new Vector2(hw, hh); life = ticksLeft; steps = updatesPerTick;
        this.gravity = gravity; this.maxFall = maxFall;
        return this;
    }

    public override void Predict(Vector2[] centers)
    {
        Vector2 p = pos, v = vel;
        for (int k = 0; k < Planner.H; k++)
        {
            if (k >= life) { Boxes[k] = Box.None; continue; }
            for (int s = 0; s < steps; s++)
            {
                v.Y = Math.Min(v.Y + gravity, maxFall);
                p += v;
            }
            Boxes[k] = Box.Around(p, half.X, half.Y);
        }
    }
}

/// <summary>
/// Plantera's thorn ball (Projectile.cs aiStyle 14, type 277): straight for 15 ticks, then
/// gravity 0.2, bounces off walls (x -0.9) and floors when falling faster than 3, rolls to a stop.
/// Platforms do not stop it (projectiles fall through them by default).
/// </summary>
public sealed class BallThreat : Threat
{
    private Vector2 pos, vel;
    private int w, h;
    private float age;

    public BallThreat Set(Projectile pr)
    {
        pos = pr.position; vel = pr.velocity; w = pr.width; h = pr.height; age = pr.ai[0];
        return this;
    }

    public override void Predict(Vector2[] centers)
    {
        Vector2 p = pos, v = vel;
        float a = age;
        bool dead = false;
        for (int k = 0; k < Planner.H; k++)
        {
            if (dead) { Boxes[k] = Box.None; continue; }
            a++;
            if (a > 15f)
            {
                a = 15f;
                if (v.Y == 0f && v.X != 0f)
                {
                    v.X *= 0.97f;
                    if (v.X > -0.01f && v.X < 0.01f) dead = true;
                }
                v.Y += 0.2f;
            }
            Vector2 c = Collision.TileCollision(p, v, w, h, true, true);
            p += c;
            if (c.X != v.X) v.X = -v.X * 0.9f;
            if (c.Y != v.Y) v.Y = v.Y > 3f ? -v.Y * 0.9f : c.Y;
            Boxes[k] = new Box(p.X, p.Y, p.X + w, p.Y + h);
        }
    }
}

/// <summary>
/// A shot the boss may fire: launched from Origin at tick Delay toward where the candidate
/// path is at that tick, then straight at Speed. Low weight: it rewards plans that keep the
/// pilot moving across the line of fire before an unpredictable (random-timed) shot exists.
/// </summary>
public sealed class VirtualShot : Threat
{
    public Vector2 Origin;
    public float Speed, Half;
    public int Delay;
    public override bool Reactive => true;

    public override void Predict(Vector2[] centers)
    {
        Vector2 p = Origin, v = Vector2.Zero;
        for (int k = 0; k < Planner.H; k++)
        {
            if (k < Delay) { Boxes[k] = Box.None; continue; }
            if (k == Delay)
            {
                Vector2 d = centers[k] - Origin;
                v = d * (Speed / Math.Max(1f, d.Length()));
            }
            p += v;
            Boxes[k] = Box.Around(p, Half, Half);
        }
    }
}

/// <summary>Static danger box (tornado segments, resting parts) with a time window.</summary>
public sealed class StaticThreat : Threat
{
    private Box box;
    public StaticThreat Set(Box b) { box = b; return this; }
    public override void Predict(Vector2[] centers) { for (int k = 0; k < Planner.H; k++) Boxes[k] = box; }
}

/// <summary>Threat predicted by an arbitrary function of the candidate pilot path.</summary>
public sealed class FuncThreat : Threat
{
    public Action<Vector2[], Box[]> Fn;
    public bool IsReactive = true;
    public override bool Reactive => IsReactive;
    public override void Predict(Vector2[] centers) => Fn(centers, Boxes);
}

/// <summary>
/// Sampling MPC over the pilot's native controls: every candidate is "action A until
/// tick T, then action B" (T in 5/12/22/32 ticks or never; 297 plans, horizon H). Each plan is rolled out with
/// PilotSim, scored against all predicted threat boxes (hard hit cost, soft clearance
/// cost) plus a boss-specific positional objective, and the cheapest first action is used.
/// </summary>
public sealed class Planner
{
    public const int H = 48;
    public const float HitCost = 100000f;

    private static readonly int[] Hs = [-1, 0, 1];
    private static readonly int[] Vs = [1, 0, -1];

    public readonly List<Threat> Threats = [];
    /// <summary>Threat whose predicted box is handed to the objective as the boss position (may be null).</summary>
    public Threat Boss;
    /// <summary>(k, pilot state at k, boss box at k) -&gt; cost; evaluated every other tick.</summary>
    public Func<int, PilotState, Box, float> Objective;
    /// <summary>Clearance (px) below which near misses start to cost.</summary>
    public float Soft = 28f, SoftWeight = 30f;
    /// <summary>When false, plans never hold Down (which drops through platforms).</summary>
    public bool AllowDown = true;

    public int Horizontal, Vertical;
    public float BestCost;
    public int BestHitTick = -1;
    public string BestHitTag = "";
    public long Decisions, Ticks100ns;

    private readonly PilotState[] path = new PilotState[H];
    /// <summary>Path and boss boxes of the chosen plan (for prediction-error diagnostics).</summary>
    public readonly PilotState[] BestPath = new PilotState[H];
    public readonly Box[] BestBoss = new Box[H];
    private readonly Vector2[] centers = new Vector2[H];
    private int lastPlan = -1;
    private readonly System.Diagnostics.Stopwatch sw = new();

    public void Reset()
    {
        Threats.Clear();
        Boss = null;
        lastPlan = -1;
    }

    /// <summary>Switch ticks of the two-segment plans (the last entry means "no switch").</summary>
    private static readonly int[] Switch = [5, 12, 22, 32, H];
    private readonly PilotState[,] prefix = new PilotState[9, H];

    public void Decide(Player p)
    {
        sw.Restart();
        MoveStats m = ProbePlayer.Stats;
        PilotState start = PilotState.From(p);
        foreach (Threat t in Threats)
            if (!t.Reactive) t.Predict(null);
        // Constant-action rollouts double as the shared prefixes of every two-segment plan.
        for (int a = 0; a < 9; a++)
        {
            PilotState s = start;
            for (int k = 0; k < H; k++)
            {
                PilotSim.Step(ref s, Hs[a / 3], Vs[a % 3], m);
                prefix[a, k] = s;
            }
        }

        float best = float.MaxValue;
        int bestPlan = Encode(4, Switch.Length - 1, 4);
        int bestHit = -1;
        string bestTag = "";
        float[] decay = Decay;
        for (int a1 = 0; a1 < 9; a1++)
        for (int ti = 0; ti < Switch.Length; ti++)
        for (int a2 = 0; a2 < 9; a2++)
        {
            int T = Switch[ti];
            if ((T == H) != (a2 == a1)) continue;
            if (!AllowDown && (a1 % 3 == 2 || a2 % 3 == 2)) continue;
            Rollout(a1, T, a2, m);
            float cost = 0f;
            int hitTick = -1;
            string hitTag = "";
            foreach (Threat t in Threats)
            {
                if (t.Reactive) t.Predict(centers);
                Box[] bx = t.Boxes;
                for (int k = 0; k < H; k++)
                {
                    if (!bx[k].Valid) continue;
                    float gap = Box.Gap(path[k].Box, bx[k]) - (t.Margin + t.Grow * k);
                    if (gap < 0f)
                    {
                        cost += HitCost * decay[k] * t.Weight;
                        if (t.Weight >= 0.5f && (hitTick < 0 || k < hitTick)) { hitTick = k; hitTag = t.Tag; }
                        break;
                    }
                    if (gap < Soft) cost += SoftWeight * (1f - gap / Soft) * decay[k] * t.Weight;
                }
                if (cost >= best) break;
            }
            if (cost >= best) continue;
            if (Objective != null)
            {
                Box[] boss = Boss?.Boxes;
                for (int k = 1; k < H; k += 2)
                    cost += 2f * decay[k] * Objective(k, path[k], boss != null ? boss[k] : Box.None);
            }
            // Hysteresis: keep following the previous first action unless something is clearly better.
            if (lastPlan >= 0 && a1 == lastPlan / (Switch.Length * 9)) cost -= 6f;
            if (cost < best) { best = cost; bestPlan = Encode(a1, ti, a2); bestHit = hitTick; bestTag = hitTag; }
        }
        lastPlan = bestPlan;
        // Replay the winner so its path and boss prediction can be checked against reality later.
        {
            int a1 = bestPlan / (Switch.Length * 9), ti = bestPlan / 9 % Switch.Length, a2 = bestPlan % 9;
            Rollout(a1, Switch[ti], a2, m);
            Array.Copy(path, BestPath, H);
            if (Boss != null)
            {
                if (Boss.Reactive) Boss.Predict(centers);
                Array.Copy(Boss.Boxes, BestBoss, H);
            }
            Horizontal = Hs[a1 / 3];
            Vertical = Vs[a1 % 3];
        }
        BestCost = best;
        BestHitTick = bestHit;
        BestHitTag = bestTag;
        Decisions++;
        Ticks100ns += sw.Elapsed.Ticks;
    }

    private static int Encode(int a1, int ti, int a2) => (a1 * Switch.Length + ti) * 9 + a2;

    /// <summary>Fills path/centers for "a1 until T, then a2" from the shared constant prefixes.</summary>
    private void Rollout(int a1, int T, int a2, MoveStats m)
    {
        for (int k = 0; k < T; k++)
        {
            path[k] = prefix[a1, k];
            centers[k] = path[k].Center;
        }
        if (T >= H) return;
        PilotState s = prefix[a1, T - 1];
        for (int k = T; k < H; k++)
        {
            PilotSim.Step(ref s, Hs[a2 / 3], Vs[a2 % 3], m);
            path[k] = s;
            centers[k] = s.Center;
        }
    }

    /// <summary>Writes the decision to the player's native controls.</summary>
    public void Apply(Player p)
    {
        p.controlLeft = Horizontal < 0;
        p.controlRight = Horizontal > 0;
        p.controlJump = Vertical > 0;
        p.controlDown = Vertical < 0;
        p.controlUp = false;
    }

    private static readonly float[] Decay = MakeDecay();

    private static float[] MakeDecay()
    {
        var d = new float[H];
        for (int k = 0; k < H; k++) d[k] = MathF.Pow(0.965f, k);
        return d;
    }
}

/// <summary>Aiming helpers.</summary>
public static class Gunnery
{
    /// <summary>Effective muzzle speed (px/tick) of the held gun and its ammo, counting extra updates.</summary>
    public static float ShotSpeed(Player p)
    {
        Item gun = p.HeldItem;
        float speed = gun.shootSpeed;
        int proj = gun.shoot;
        foreach (Item ammo in p.inventory)
        {
            if (!ammo.IsAir && gun.useAmmo > 0 && ammo.ammo == gun.useAmmo)
            {
                speed += ammo.shootSpeed;
                if (ammo.shoot > 0) proj = ammo.shoot;
                break;
            }
        }
        int extra = proj > 0 && proj < Terraria.ID.ProjectileID.Count ? Terraria.ID.ContentSamples.ProjectilesByType[proj].extraUpdates : 0;
        return Math.Max(4f, speed * (1 + extra));
    }

    /// <summary>Aim point that intercepts a target moving at constant velocity.</summary>
    public static Vector2 Lead(Vector2 from, Vector2 target, Vector2 targetVel, float speed, float maxTicks = 60f)
    {
        Vector2 aim = target;
        for (int i = 0; i < 3; i++)
        {
            float t = Math.Min(Vector2.Distance(from, aim) / speed, maxTicks);
            aim = target + targetVel * t;
        }
        return aim;
    }
}

/// <summary>Per-attempt debug notes (hit causes, planner timing) in the headless out dir.</summary>
public static class PilotLog
{
    private static System.IO.StreamWriter w;

    public static void Line(string s)
    {
        try
        {
            if (w == null)
            {
                string dir = Common.Headless.HeadlessSim.OutDir;
                if (dir == null) return;
                w = new System.IO.StreamWriter(System.IO.Path.Combine(dir, "pilot.log"), append: true) { AutoFlush = true };
            }
            w.WriteLine(s);
        }
        catch (Exception) { }
    }
}

/// <summary>Logs what hit the pilot during a trial (NPC or projectile type and nearby context).</summary>
public sealed class HitProbe : ModPlayer
{
    public static Func<string> Context;

    public override void OnHurt(Player.HurtInfo info)
    {
        if (Player.whoAmI != Main.myPlayer || Common.TrialRunner.Instance.Current != Common.TrialRunner.Phase.Fighting) return;
        if (Common.TrialRunner.Instance.Plan is not PilotPlan) return;
        var src = info.DamageSource;
        string what = src.SourceNPCIndex >= 0 ? $"npc {Main.npc[src.SourceNPCIndex].type}" :
            src.SourceProjectileType > 0 ? $"proj {src.SourceProjectileType}" : $"other {src.SourceOtherIndex}";
        PilotLog.Line($"HIT t={Common.TrialRunner.Instance.FightTicks} {what} dmg={info.Damage} p=({Player.Center.X / 16:F1},{Player.Center.Y / 16:F1}) v=({Player.velocity.X:F1},{Player.velocity.Y:F1}) {Context?.Invoke()}");
    }
}
