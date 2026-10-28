using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
using BossPilot.Common;

namespace BossPilot.Bosses.BeeSkelWof;

/// <summary>
/// Movement constants of the pilot as vanilla computed them this tick (after
/// equips, buffs and UpdateJumpHeight), so the planner's model uses the real
/// run/jump numbers. Also logs every hit with its source for offline review.
/// </summary>
public sealed class PilotPhysics : ModPlayer
{
    public static float MaxRun = 3f, AccRun = 3f, RunAcc = 0.08f, RunSlow = 0.2f, JumpSpeed = 5.01f, Gravity = 0.4f, MaxFall = 10f;
    public static int JumpHeight = 15;
    public static bool WaterWalk;

    public override void PostUpdateRunSpeeds()
    {
        if (Player.whoAmI != Main.myPlayer) return;
        MaxRun = Player.maxRunSpeed;
        AccRun = Player.accRunSpeed;
        RunAcc = Player.runAcceleration;
        RunSlow = Player.runSlowdown;
        JumpSpeed = Player.jumpSpeed;
        JumpHeight = Player.jumpHeight;
        Gravity = Player.gravity;
        MaxFall = Player.maxFallSpeed;
        WaterWalk = Player.waterWalk;
    }

    public override void OnHurt(Player.HurtInfo info)
    {
        TrialRunner t = TrialRunner.Instance;
        if (t.Current != TrialRunner.Phase.Fighting || Player.whoAmI != Main.myPlayer) return;
        PlayerDeathReason r = info.DamageSource;
        string src = r == null ? "?" : r.SourceNPCIndex >= 0 && r.SourceNPCIndex < Main.maxNPCs
            ? $"npc {Main.npc[r.SourceNPCIndex].type} ai[{Main.npc[r.SourceNPCIndex].ai[0]:F0},{Main.npc[r.SourceNPCIndex].ai[1]:F0},{Main.npc[r.SourceNPCIndex].ai[2]:F0}] at {Main.npc[r.SourceNPCIndex].Center / 16f} v {Main.npc[r.SourceNPCIndex].velocity}"
            : r.SourceProjectileType > 0 ? $"proj {r.SourceProjectileType}" : $"other {r.SourceOtherIndex}";
        Trace.Line($"HIT tick {t.FightTicks} dmg {info.Damage} by {src}; player {Player.Center / 16f} v {Player.velocity} plan {Trace.LastPlan}");
        if (!Trace.Verbose) return;
        foreach (Projectile pr in Main.projectile)
            if (pr.active && pr.hostile && Vector2.Distance(pr.Center, Player.Center) < 240f)
                Trace.Line($"   proj {pr.type} #{pr.whoAmI} pos {pr.position} v {pr.velocity} upd {pr.extraUpdates} age {pr.timeLeft} size {pr.Size} player.pos {Player.position}");
    }
}

/// <summary>Optional per-scenario debug log (env BSW_TRACE=1 enables the per-tick lines; hits are always logged).</summary>
public static class Trace
{
    public static readonly bool Verbose = Environment.GetEnvironmentVariable("BSW_TRACE") == "1";
    public static string LastPlan = "";
    private static StreamWriter w;
    private static string scenario;

    public static void Line(string s)
    {
        string dir = Common.Headless.HeadlessSim.OutDir;
        if (dir == null) return;
        string sc = TrialRunner.Instance.Scenario?.Name ?? "none";
        if (w == null || sc != scenario)
        {
            w?.Dispose();
            scenario = sc;
            w = new StreamWriter(Path.Combine(dir, $"bsw_{sc}.log"), append: true) { AutoFlush = true };
        }
        w.WriteLine(s);
    }
}

/// <summary>Pilot state as seen by the movement model.</summary>
public struct SimState
{
    public Vector2 Pos, Vel;
    public int Jump;
    public bool ReleaseJump, Cloud;

    public readonly Vector2 Center => Pos + new Vector2(PlayerSim.W / 2f, PlayerSim.H / 2f);
}

/// <summary>
/// Kinematic replica of Player.HorizontalMovement / JumpMovement / gravity /
/// DryCollision for a dry, unmounted, wingless pilot. Terrain goes through the
/// game's own Collision.StepUp and Collision.TileCollision, so platforms,
/// slopes and walls behave as in the kernel.
/// </summary>
public static class PlayerSim
{
    public const int W = 20, H = 42;
    public static bool HasCloud;

    public static SimState From(Player p) => new()
    {
        Pos = p.position,
        Vel = p.velocity,
        Jump = p.jump,
        ReleaseJump = p.releaseJump,
        Cloud = HasCloud && p.GetJumpState(ExtraJump.CloudInABottle).Available,
    };

    public static void Step(ref SimState s, int dir, bool jump, bool down)
    {
        bool ground = s.Vel.Y == 0f;
        float maxRun = PilotPhysics.MaxRun, accRun = PilotPhysics.AccRun, acc = PilotPhysics.RunAcc, slow = PilotPhysics.RunSlow;
        float vx = s.Vel.X;
        if (dir < 0 && vx > -maxRun) { if (vx > slow) vx -= slow; vx -= acc; }
        else if (dir > 0 && vx < maxRun) { if (vx < -slow) vx += slow; vx += acc; }
        else if (dir < 0 && vx > -accRun) { if (ground) { if (vx > slow) vx -= slow; vx -= acc * 0.2f; } }
        else if (dir > 0 && vx < accRun) { if (ground) { if (vx < -slow) vx += slow; vx += acc * 0.2f; } }
        else if (ground) vx = vx > slow ? vx - slow : vx < -slow ? vx + slow : 0f;
        else { float h = slow * 0.5f; vx = vx > h ? vx - h : vx < -h ? vx + h : 0f; }
        s.Vel.X = vx;

        if (jump)
        {
            if (s.Jump > 0)
            {
                if (s.Vel.Y == 0f) s.Jump = 0;
                else { s.Vel.Y = -PilotPhysics.JumpSpeed; s.Jump--; }
            }
            else if ((ground || s.Cloud) && s.ReleaseJump)
            {
                s.Vel.Y = -PilotPhysics.JumpSpeed;
                if (ground) { s.Jump = PilotPhysics.JumpHeight; s.Cloud = HasCloud; }
                else { s.Jump = (int)(PilotPhysics.JumpHeight * 0.75f); s.Cloud = false; }
            }
            s.ReleaseJump = false;
        }
        else
        {
            s.Jump = 0;
            s.ReleaseJump = true;
        }

        s.Vel.Y += PilotPhysics.Gravity;
        if (s.Vel.Y > PilotPhysics.MaxFall) s.Vel.Y = PilotPhysics.MaxFall;

        if (s.Vel.Y >= PilotPhysics.Gravity && !down)
        {
            float stepSpeed = 0f, gfx = 0f;
            Collision.StepUp(ref s.Pos, ref s.Vel, W, H, ref stepSpeed, ref gfx);
        }
        Vector2 v = Collision.TileCollision(s.Pos, s.Vel, W, H, down, false);
        if (Collision.up) s.Jump = 0;
        if (PilotPhysics.WaterWalk) v = Collision.WaterCollision(s.Pos, v, W, H, down, false, true);
        s.Vel = v;
        s.Pos += v;
        if (s.Vel.Y == 0f && HasCloud) s.Cloud = true;
    }
}

/// <summary>Something that hurts on contact, with a motion model for prediction.</summary>
public struct Hazard
{
    public Vector2 Pos, Size, Vel;   // top-left px, px per tick
    public float Grav, MaxVy;        // per-tick gravity once Age passes GravDelay
    public int GravDelay;
    public byte Mode;                // 0 linear, 1 pursuit (homes on the pilot), 2 x-band (full height), 3 Queen Bee charge replica
    public float Speed, Accel;       // pursuit cruise speed and per-axis turn rate
    public QueenBeeCharge Qb;        // mode 3 parameters and AI state
    public SkeletronPart Sk;         // modes 4 (head) and 5 (hand) AI state
    public EyeEmitter Eye;           // mode 6: future lasers of a Wall of Flesh eye
    public float Margin;
    public float Weight;
    public int Id;                   // npc index, or 1000 + projectile index
    public int Sub;                  // projectile updates per tick; each one is a separate hit check in the kernel
}

/// <summary>
/// Replica of Queen Bee's charge phase (NPC.cs aiStyle 43, ai[0] == 0), stepped
/// against each candidate's simulated pilot so the planner knows when and where
/// she launches. Phase 0 = aligning (even ai[1]), 1 = charging, 2 = braking.
/// </summary>
public struct QueenBeeCharge
{
    public float ChargeSpeed, TolY, AccY, MaxVy, TurnDist;
    public int Enrage, Phase, Ai1, LastLaunchAi1;
    public int Delay; // ticks until a charge phase can begin (phase -1: drifting until then)

    /// <summary>
    /// A non-charge phase that ends within the horizon: the next phase is random and may be the
    /// charges, so assume the worst and start aligning when the kernel's phase clock runs out.
    /// </summary>
    public static QueenBeeCharge Pending(NPC n, int enrage, int delay)
    {
        var q = From(n, enrage);
        q.Ai1 = 0;
        q.Phase = -1;
        q.Delay = delay;
        return q;
    }

    public static QueenBeeCharge From(NPC n, int enrage)
    {
        var q = new QueenBeeCharge
        {
            Enrage = enrage,
            ChargeSpeed = 12f + 7f * enrage,
            TolY = 20f + 20f * enrage,
            AccY = 0.15f + 0.5f * enrage,
            MaxVy = 12f + 3f * enrage,
            TurnDist = 600f - 100f * enrage,
            Ai1 = (int)n.ai[1],
            LastLaunchAi1 = 2 * (2 + enrage),
        };
        q.Phase = q.Ai1 % 2 == 0 ? 0 : n.ai[2] == 1f ? 2 : 1;
        return q;
    }

    public void Step(ref Vector2 pos, ref Vector2 vel, Vector2 size, Vector2 pc)
    {
        Vector2 c = pos + size / 2f;
        if (Phase == -1 && --Delay <= 0) Phase = 0;
        if (Phase == 0 && Ai1 <= LastLaunchAi1)
        {
            if (Math.Abs(c.Y - pc.Y) < TolY)
            {
                Vector2 d = pc - c;
                float len = d.Length();
                vel = len > 0f ? d * (ChargeSpeed / len) : vel;
                Ai1++;
                Phase = 1;
            }
            else
            {
                vel.Y += c.Y < pc.Y ? AccY : -AccY;
                vel.Y = MathHelper.Clamp(vel.Y, -MaxVy, MaxVy);
                float adx = Math.Abs(c.X - pc.X), dir = pc.X > c.X ? 1f : -1f;
                if (adx > 600f) vel.X += 0.15f * dir;
                else if (adx < 300f) vel.X -= 0.15f * dir;
                else vel.X *= 0.8f;
                vel.X = MathHelper.Clamp(vel.X, -16f, 16f);
            }
        }
        else if (Phase == 1 || Phase == 2)
        {
            if (Phase == 1)
            {
                int dirv = vel.X < 0f ? -1 : 1;
                int behind = c.X < pc.X ? -1 : 1;
                bool brake = (dirv == behind && Math.Abs(c.X - pc.X) > TurnDist) || Math.Abs(c.Y - pc.Y) > TurnDist * 1.5f;
                if (brake) { if (Enrage > 0) vel *= 0.5f; Phase = 2; }
            }
            if (Phase == 2)
            {
                vel *= 0.9f;
                if (Enrage > 0) vel *= 0.7f;
                if (Math.Abs(vel.X) + Math.Abs(vel.Y) < 0.1f) { Ai1++; Phase = 0; }
            }
        }
        pos += vel;
    }
}

/// <summary>
/// Replica of Skeletron's head (aiStyle 11, Classic) and hands (aiStyle 12):
/// hover/spin for the head, idle tracking, slam (rise until 200 px above the head,
/// then 18 px/tick at the pilot) and sweep (swing out to 500 px, then 17 px/tick)
/// for the hands, with the ai[3] attack clock. Hands read the head's simulated position.
/// </summary>
public struct SkeletronPart
{
    public float Side, Counter, HeadTimer;
    public int State;
    public bool Spin;

    public static SkeletronPart Head(NPC n) => new() { Spin = n.ai[1] == 1f, HeadTimer = n.ai[2] };

    public static SkeletronPart Hand(NPC hand, NPC head) => new()
    {
        Side = hand.ai[0], State = (int)hand.ai[2], Counter = hand.ai[3], Spin = head.ai[1] != 0f,
    };

    public void StepHead(ref Vector2 pos, ref Vector2 vel, Vector2 size, Vector2 ppos, Vector2 pc)
    {
        Vector2 c = pos + size / 2f;
        if (Spin)
        {
            Vector2 d = pc - c;
            float len = d.Length();
            if (len > 0f) vel = d * (1.5f / len);
            if (++HeadTimer >= 400f) { Spin = false; HeadTimer = 0f; }
        }
        else
        {
            if (++HeadTimer >= 800f) { Spin = true; HeadTimer = 0f; }
            if (pos.Y > ppos.Y - 250f) { if (vel.Y > 0f) vel.Y *= 0.98f; vel.Y -= 0.02f; if (vel.Y > 2f) vel.Y = 2f; }
            else if (pos.Y < ppos.Y - 250f) { if (vel.Y < 0f) vel.Y *= 0.98f; vel.Y += 0.02f; if (vel.Y < -2f) vel.Y = -2f; }
            if (c.X > pc.X) { if (vel.X > 0f) vel.X *= 0.98f; vel.X -= 0.05f; if (vel.X > 8f) vel.X = 8f; }
            if (c.X < pc.X) { if (vel.X < 0f) vel.X *= 0.98f; vel.X += 0.05f; if (vel.X < -8f) vel.X = -8f; }
        }
        pos += vel;
    }

    public void StepHand(ref Vector2 pos, ref Vector2 vel, Vector2 size, Vector2 headPos, Vector2 headSize, bool headSpin, Vector2 ppos, Vector2 pc)
    {
        Spin = headSpin;
        float hx = pos.X + size.X / 2f, headCx = headPos.X + headSize.X / 2f;
        if (State == 0 || State == 3)
        {
            if (Spin)
            {
                if (pos.Y > headPos.Y - 100f) { if (vel.Y > 0f) vel.Y *= 0.96f; vel.Y -= 0.07f; if (vel.Y > 6f) vel.Y = 6f; }
                else if (pos.Y < headPos.Y - 100f) { if (vel.Y < 0f) vel.Y *= 0.96f; vel.Y += 0.07f; if (vel.Y < -6f) vel.Y = -6f; }
                float tx = headCx - 120f * Side;
                if (hx > tx) { if (vel.X > 0f) vel.X *= 0.96f; vel.X -= 0.1f; if (vel.X > 8f) vel.X = 8f; }
                if (hx < tx) { if (vel.X < 0f) vel.X *= 0.96f; vel.X += 0.1f; if (vel.X < -8f) vel.X = -8f; }
            }
            else
            {
                if (++Counter >= 300f) { State++; Counter = 0f; }
                if (pos.Y > headPos.Y + 230f) { if (vel.Y > 0f) vel.Y *= 0.96f; vel.Y -= 0.04f; if (vel.Y > 3f) vel.Y = 3f; }
                else if (pos.Y < headPos.Y + 230f) { if (vel.Y < 0f) vel.Y *= 0.96f; vel.Y += 0.04f; if (vel.Y < -3f) vel.Y = -3f; }
                float tx = headCx - 200f * Side;
                if (hx > tx) { if (vel.X > 0f) vel.X *= 0.96f; vel.X -= 0.07f; if (vel.X > 8f) vel.X = 8f; }
                if (hx < tx) { if (vel.X < 0f) vel.X *= 0.96f; vel.X += 0.07f; if (vel.X < -8f) vel.X = -8f; }
            }
        }
        else if (State == 1)
        {
            vel.X *= 0.95f;
            vel.Y -= 0.1f;
            if (vel.Y < -8f) vel.Y = -8f;
            if (pos.Y < headPos.Y - 200f) { State = 2; vel = Aim(pos + size / 2f, pc, 18f, vel); }
        }
        else if (State == 2)
        {
            if (pos.Y > ppos.Y || vel.Y < 0f) State = 3;
        }
        else if (State == 4)
        {
            vel.Y *= 0.95f;
            vel.X = MathHelper.Clamp(vel.X + 0.1f * -Side, -8f, 8f);
            if (hx < headCx - 500f || hx > headCx + 500f) { State = 5; vel = Aim(pos + size / 2f, pc, 17f, vel); }
        }
        else if (State == 5 && ((vel.X > 0f && hx > pc.X) || (vel.X < 0f && hx < pc.X))) State = 0;
        pos += vel;
    }

    private static Vector2 Aim(Vector2 from, Vector2 to, float speed, Vector2 fallback)
    {
        Vector2 d = to - from;
        float len = d.Length();
        return len > 0f ? d * (speed / len) : fallback;
    }
}

/// <summary>
/// Fire clock of a Wall of Flesh eye (aiStyle 28): localAI[1] climbs by a life-dependent
/// rate; after 600 a burst starts and every tick it exceeds 45 an EyeLaser (83, 3 updates
/// per tick) leaves the eye center aimed at the pilot's center. Spawns virtual lasers at
/// the replicated fire ticks so the planner can dodge shots that do not exist yet.
/// </summary>
public struct EyeEmitter
{
    public float Timer, Rate, Speed;
    public int Burst, Shots;
    public Vector2 L0, V0, L1, V1, L2, V2;
    public int Alive;

    public static EyeEmitter From(NPC eye, NPC wof)
    {
        float f = (float)wof.life / wof.lifeMax;
        var e = new EyeEmitter { Timer = eye.localAI[1], Burst = (int)eye.localAI[2], Rate = 1f, Shots = 4, Speed = 9f };
        if (f < 0.75f) { e.Rate += 1f; e.Shots++; }
        if (f < 0.5f) { e.Rate += 1f; e.Shots++; e.Speed += 1f; }
        if (f < 0.25f) { e.Rate += 1f; e.Shots += 2; e.Speed += 1f; }
        if (f < 0.1f) { e.Rate += 2f; e.Shots += 3; e.Speed += 2f; }
        return e;
    }

    /// <summary>Advances one tick; returns the smallest gap between any live virtual laser and the pilot box.</summary>
    public float Step(Vector2 eyeCenter, Vector2 p0, Vector2 p1, Vector2 pc)
    {
        Timer += Rate;
        if (Burst == 0)
        {
            if (Timer > 600f) { Burst = 1; Timer = 0f; }
        }
        else if (Timer > 45f)
        {
            Timer = 0f;
            if (++Burst >= Shots) Burst = 0;
            Vector2 d = pc - eyeCenter;
            float len = d.Length();
            if (len > 1f)
            {
                Vector2 v = d * (Speed / len);
                Vector2 pos = eyeCenter + v - new Vector2(3f, 3f);
                int slot = Alive % 3;
                if (slot == 0) { L0 = pos - v; V0 = v; } else if (slot == 1) { L1 = pos - v; V1 = v; } else { L2 = pos - v; V2 = v; }
                Alive++;
            }
        }
        float gap = float.MaxValue;
        int n = Math.Min(Alive, 3);
        for (int i = 0; i < n; i++)
        {
            ref Vector2 l = ref i == 0 ? ref L0 : ref i == 1 ? ref L1 : ref L2;
            Vector2 v = i == 0 ? V0 : i == 1 ? V1 : V2;
            for (int u = 0; u < 3; u++)
            {
                l += v;
                float gx = Math.Max(l.X - p1.X, p0.X - (l.X + 6f));
                float gy = Math.Max(l.Y - p1.Y, p0.Y - (l.Y + 6f));
                gap = Math.Min(gap, Math.Max(gx, gy));
            }
        }
        return gap;
    }
}

/// <summary>One macro action of the planner: two horizontal legs plus timed jump / drop.</summary>
public struct Cand
{
    public sbyte Dir1, Dir2;
    public byte Switch, JumpAt, JumpHold, CloudAt, CloudHold, DownUntil;

    public readonly void At(int k, out int dir, out bool jump, out bool down)
    {
        dir = k < Switch ? Dir1 : Dir2;
        jump = (k >= JumpAt && k < JumpAt + JumpHold) || (k >= CloudAt && k < CloudAt + CloudHold);
        down = k < DownUntil;
    }

    public override readonly string ToString() =>
        $"{Dir1}/{Dir2}@{Switch} j{(JumpHold == 0 ? "-" : JumpAt + "x" + JumpHold)} c{(CloudHold == 0 ? "-" : CloudAt + "x" + CloudHold)} d{DownUntil}";
}

/// <summary>Objective handed to the planner: where the pilot would like to be.</summary>
public struct Goal
{
    public float X;          // desired center x (px)
    public float Tol;        // no cost within +-Tol
    public float WX;         // cost per tile outside the tolerance
    public float MinX, MaxX; // hard-ish arena limits for the center (px)
    public float PreferY;    // desired center y, NaN for none
    public float WY;
    public bool KeepJump;    // reward ending on the ground / with the cloud jump left
}

/// <summary>
/// Receding-horizon planner: each tick it rolls ~50 macro actions through the
/// kernel-faithful movement model against predicted hazard motion and keeps
/// the cheapest; only the first tick of the winner is executed.
/// </summary>
public sealed class Planner
{
    public int Horizon = 40;
    public float HitCost = 1000f;
    public readonly List<Hazard> Hazards = [];

    private readonly List<Cand> cands = [];
    private Vector2[][] lin = [];
    private Cand last;
    private int lastTick = -10;
    private readonly QueenBeeCharge[] qbState = new QueenBeeCharge[16];
    private Vector2 predicted;
    private readonly Dictionary<int, Vector2> predictedHaz = [];
    private readonly SkeletronPart[] skState = new SkeletronPart[16];
    private readonly EyeEmitter[] eyeState = new EyeEmitter[4];

    public Cand Last => last;
    public float LastCost { get; private set; }
    public int LastHitAt { get; private set; }

    public Planner()
    {
        sbyte[][] legs = [[-1, -1], [1, 1], [0, 0], [-1, 1], [1, -1], [-1, 0], [1, 0], [0, -1], [0, 1]];
        foreach (sbyte[] l in legs)
        {
            byte sw = (byte)(l[0] == l[1] ? 255 : 12);
            Add(l, sw, 0, 0, 0, 0, 0);        // no jump
            Add(l, sw, 0, 30, 0, 0, 0);       // full jump now
            Add(l, sw, 0, 6, 0, 0, 0);        // short hop now
            Add(l, sw, 8, 30, 0, 0, 0);       // jump a bit later
            Add(l, sw, 0, 21, 23, 20, 0);     // full jump now, cloud jump at the apex
            Add(l, sw, 0, 0, 0, 20, 0);       // cloud jump now (if airborne)
            Add(l, sw, 0, 0, 0, 0, 14);       // drop through platforms
        }
    }

    private void Add(sbyte[] l, byte sw, byte jumpAt, byte hold, byte cloudAt, byte cloudHold, byte down) =>
        cands.Add(new Cand { Dir1 = l[0], Dir2 = l[1], Switch = sw, JumpAt = jumpAt, JumpHold = hold, CloudAt = cloudAt, CloudHold = cloudHold, DownUntil = down });

    private readonly Dictionary<int, (Vector2 pos, int tick, int type)> seen = [];
    private int now;

    public void AddNpc(NPC n, float margin, float weight = 1f)
    {
        // Worm segments are dragged after their leader; their velocity field is not their motion,
        // so use the displacement observed since the previous tick.
        Vector2 v = n.velocity;
        if (n.aiStyle == 6)
        {
            v = seen.TryGetValue(n.whoAmI, out var last) && last.tick == now - 1 && last.type == n.type ? n.position - last.pos : Vector2.Zero;
            seen[n.whoAmI] = (n.position, now, n.type);
        }
        Hazards.Add(new Hazard { Pos = n.position, Size = n.Size, Vel = v, Margin = margin, Weight = weight, Id = n.whoAmI, GravDelay = int.MaxValue });
    }

    public void AddPursuer(NPC n, float speed, float accel, float margin)
    {
        Hazards.Add(new Hazard { Pos = n.position, Size = n.Size, Vel = n.velocity, Mode = 1, Speed = speed, Accel = accel, Margin = margin, Weight = 1f, Id = n.whoAmI, GravDelay = int.MaxValue });
    }

    public void AddQueenBee(NPC n, int enrage, float margin, int pendingDelay = -1)
    {
        QueenBeeCharge q = pendingDelay >= 0 ? QueenBeeCharge.Pending(n, enrage, pendingDelay) : QueenBeeCharge.From(n, enrage);
        Hazards.Add(new Hazard { Pos = n.position, Size = n.Size, Vel = n.velocity, Mode = 3, Qb = q, Margin = margin, Weight = 1f, Id = n.whoAmI, GravDelay = int.MaxValue });
    }

    /// <summary>Skeletron head followed by its hands (hands must come right after their head).</summary>
    public void AddSkeletron(NPC head, float margin)
    {
        Hazards.Add(new Hazard { Pos = head.position, Size = head.Size, Vel = head.velocity, Mode = 4, Sk = SkeletronPart.Head(head), Margin = margin, Weight = 1f, Id = head.whoAmI, GravDelay = int.MaxValue });
        foreach (NPC n in Main.npc)
            if (n.active && n.type == NPCID.SkeletronHand && (int)n.ai[1] == head.whoAmI)
                Hazards.Add(new Hazard { Pos = n.position, Size = n.Size, Vel = n.velocity, Mode = 5, Sk = SkeletronPart.Hand(n, head), Margin = margin, Weight = 1f, Id = n.whoAmI, GravDelay = int.MaxValue });
    }

    public void AddEye(NPC eye, NPC wof, float margin)
    {
        Hazards.Add(new Hazard { Pos = eye.position, Size = eye.Size, Vel = eye.velocity, Mode = 6, Eye = EyeEmitter.From(eye, wof), Margin = margin, Weight = 1f, Id = -2, GravDelay = int.MaxValue });
    }

    public void AddProjectile(Projectile pr, float margin, float grav = 0f, int gravDelay = int.MaxValue, float maxVy = 16f)
    {
        int upd = pr.extraUpdates + 1;
        Hazards.Add(new Hazard
        {
            Pos = pr.position, Size = pr.Size, Vel = pr.velocity * upd, Margin = margin, Weight = 1f, Id = 1000 + pr.whoAmI,
            Grav = grav * upd * upd, GravDelay = gravDelay, MaxVy = maxVy * upd, Sub = upd,
        });
    }

    public void AddBand(float x0, float x1, float vx, float margin)
    {
        Hazards.Add(new Hazard { Pos = new Vector2(x0, -1e6f), Size = new Vector2(x1 - x0, 2e6f), Vel = new Vector2(vx, 0), Mode = 2, Margin = margin, Weight = 1f, Id = -1, GravDelay = int.MaxValue });
    }

    /// <summary>Call before adding this tick's hazards.</summary>
    public void BeginTick(int tick)
    {
        now = tick;
        Hazards.Clear();
    }

    /// <summary>Plans and writes this tick's movement controls onto the player.</summary>
    public void Drive(Player p, in Goal goal, int tick)
    {
        int hz = Horizon;
        int nh = Hazards.Count;
        if (lin.Length < nh) Array.Resize(ref lin, nh + 16);
        for (int i = 0; i < nh; i++)
        {
            Hazard h = Hazards[i];
            if (h.Mode is 1 or 3 or 4 or 5 or 6) continue;
            lin[i] ??= new Vector2[64];
            Vector2 pos = h.Pos, vel = h.Vel;
            lin[i][0] = pos;
            for (int k = 1; k <= hz; k++)
            {
                if (k > h.GravDelay) { vel.Y += h.Grav; if (vel.Y > h.MaxVy) vel.Y = h.MaxVy; }
                pos += vel;
                lin[i][k] = pos;
            }
        }

        SimState s0 = PlayerSim.From(p);
        bool continuing = tick == lastTick + 1;
        if (Trace.Verbose && continuing)
        {
            float err = Vector2.Distance(p.position, predicted);
            if (err > 1.5f) Trace.Line($"  model err {err:F1}px tick {tick}: predicted {predicted} actual {p.position} v {p.velocity} plan {last}");
            for (int i = 0; i < nh; i++)
            {
                Hazard h = Hazards[i];
                if (h.Id < 0 || !predictedHaz.TryGetValue(h.Id, out Vector2 ph)) continue;
                float he = Vector2.Distance(ph, h.Pos);
                if (he > 3f && Vector2.Distance(h.Pos, p.position) < 400f) Trace.Line($"  hazard err {he:F1}px id {h.Id} mode {h.Mode} predicted {ph} actual {h.Pos}");
            }
        }
        float best = float.MaxValue;
        Cand bestC = cands[2];
        int bestHit = 0;
        Span<Vector2> ppos = stackalloc Vector2[16];
        Span<Vector2> pvel = stackalloc Vector2[16];
        QueenBeeCharge[] qst = qbState;
        SkeletronPart[] sst = skState;
        EyeEmitter[] est = eyeState;

        for (int ci = 0; ci < cands.Count + 1; ci++)
        {
            Cand c;
            if (ci < cands.Count) c = cands[ci];
            else if (continuing) c = Shift(last);
            else break;

            SimState s = s0;
            int ne = 0;
            for (int i = 0; i < nh && ne < 4; i++)
                if (Hazards[i].Mode == 6) est[ne++] = Hazards[i].Eye;
            int np = 0;
            for (int i = 0; i < nh && np < 16; i++)
                if (Hazards[i].Mode is 1 or 3 or 4 or 5) { ppos[np] = Hazards[i].Pos; pvel[np] = Hazards[i].Vel; qst[np] = Hazards[i].Qb; sst[np] = Hazards[i].Sk; np++; }

            int hitAt = 0;
            float near = 0f;
            float path = 0f;
            for (int k = 1; k <= hz; k++)
            {
                c.At(k - 1, out int dir, out bool jump, out bool down);
                PlayerSim.Step(ref s, dir, jump, down);
                float px0 = s.Pos.X, py0 = s.Pos.Y, px1 = px0 + PlayerSim.W, py1 = py0 + PlayerSim.H;
                Vector2 pc = s.Center;
                int pi = 0, ei = 0;
                float wk = k <= 24 ? 1f : 0.6f;
                Vector2 headPos = default, headSize = default;
                bool headSpin = false;
                for (int i = 0; i < nh; i++)
                {
                    Hazard h = Hazards[i];
                    Vector2 hp;
                    if (h.Mode == 6)
                    {
                        if (ei >= ne) continue;
                        Vector2 ec = h.Pos + h.Vel * k + h.Size / 2f;
                        float eg = est[ei++].Step(ec, new Vector2(px0, py0), new Vector2(px1, py1), pc);
                        if (eg < h.Margin) { if (hitAt == 0) hitAt = k; }
                        else if (eg < h.Margin + 40f) { float q = (h.Margin + 40f - eg) / 40f; near += q * q * wk; }
                        continue;
                    }
                    if (h.Mode == 4)
                    {
                        if (pi >= np) continue;
                        sst[pi].StepHead(ref ppos[pi], ref pvel[pi], h.Size, s.Pos, pc);
                        hp = ppos[pi];
                        headPos = hp;
                        headSize = h.Size;
                        headSpin = sst[pi].Spin;
                        pi++;
                    }
                    else if (h.Mode == 5)
                    {
                        if (pi >= np) continue;
                        sst[pi].StepHand(ref ppos[pi], ref pvel[pi], h.Size, headPos, headSize, headSpin, s.Pos, pc);
                        hp = ppos[pi];
                        pi++;
                    }
                    else if (h.Mode == 3)
                    {
                        if (pi >= np) continue;
                        qst[pi].Step(ref ppos[pi], ref pvel[pi], h.Size, pc);
                        hp = ppos[pi];
                        pi++;
                    }
                    else if (h.Mode == 1)
                    {
                        if (pi >= np) continue;
                        Vector2 hv = pvel[pi];
                        Vector2 d = pc - (ppos[pi] + h.Size / 2f);
                        float len = d.Length();
                        if (len > 1f) d *= h.Speed / len;
                        hv.X += Math.Sign(d.X - hv.X) * Math.Min(h.Accel, Math.Abs(d.X - hv.X));
                        hv.Y += Math.Sign(d.Y - hv.Y) * Math.Min(h.Accel, Math.Abs(d.Y - hv.Y));
                        pvel[pi] = hv;
                        ppos[pi] += hv;
                        hp = ppos[pi];
                        pi++;
                    }
                    else hp = lin[i][k];
                    float m = h.Margin;
                    float gx = Math.Max(hp.X - px1, px0 - (hp.X + h.Size.X));
                    float gy = h.Mode == 2 ? -1f : Math.Max(hp.Y - py1, py0 - (hp.Y + h.Size.Y));
                    float gap = Math.Max(gx, gy);
                    if (h.Sub > 1 && h.Mode == 0)
                    {
                        // Swept check: the kernel tests the pilot after every projectile update.
                        Vector2 a = lin[i][k - 1], step = (hp - a) / h.Sub;
                        for (int j = 1; j < h.Sub; j++)
                        {
                            Vector2 q = a + step * j;
                            float sx = Math.Max(q.X - px1, px0 - (q.X + h.Size.X));
                            float sy = Math.Max(q.Y - py1, py0 - (q.Y + h.Size.Y));
                            gap = Math.Min(gap, Math.Max(sx, sy));
                        }
                    }
                    if (gap < m)
                    {
                        if (hitAt == 0) hitAt = k;
                    }
                    else if (gap < m + 40f)
                    {
                        float q = (m + 40f - gap) / 40f;
                        near += q * q * wk * h.Weight;
                    }
                }
                if (hitAt != 0) break;
                if (k == hz / 2 || k == hz) path += GoalCost(s, goal);
            }

            float cost = path + near * 3f;
            if (hitAt != 0) cost += HitCost * (2f - (float)hitAt / hz) + GoalCost(s, goal);
            else if (goal.KeepJump && s.Vel.Y != 0f && !s.Cloud) cost += 12f;
            if (ci == cands.Count) cost -= 1.5f; // hysteresis: keep the plan in progress
            if (cost < best)
            {
                best = cost;
                bestC = c;
                bestHit = hitAt;
            }
        }

        last = bestC;
        lastTick = tick;
        if (Trace.Verbose)
        {
            SimState t1 = s0;
            bestC.At(0, out int pd, out bool pj, out bool pdn);
            PlayerSim.Step(ref t1, pd, pj, pdn);
            predicted = t1.Pos;
            predictedHaz.Clear();
            for (int i = 0; i < nh; i++)
                if (Hazards[i].Id >= 0 && Hazards[i].Mode is 0) predictedHaz[Hazards[i].Id] = lin[i][1];
        }
        LastCost = best;
        LastHitAt = bestHit;
        bestC.At(0, out int d0, out bool j0, out bool dn0);
        p.controlLeft = d0 < 0;
        p.controlRight = d0 > 0;
        p.controlJump = j0;
        p.controlDown = dn0;
        Trace.LastPlan = $"{bestC} cost {best:F1} hit@{bestHit}";
    }

    private static Cand Shift(Cand c)
    {
        static byte Dec(byte b) => b == 255 ? (byte)255 : b == 0 ? (byte)0 : (byte)(b - 1);
        if (c.JumpHold > 0) { if (c.JumpAt == 0) c.JumpHold--; else c.JumpAt--; }
        if (c.CloudHold > 0) { if (c.CloudAt == 0) c.CloudHold--; else c.CloudAt--; }
        if (c.Switch != 255) { c.Switch = Dec(c.Switch); if (c.Switch == 0) { c.Dir1 = c.Dir2; c.Switch = 255; } }
        c.DownUntil = Dec(c.DownUntil);
        return c;
    }

    private static float GoalCost(in SimState s, in Goal g)
    {
        float cx = s.Pos.X + PlayerSim.W / 2f;
        float cost = 0f;
        float dx = Math.Abs(cx - g.X) - g.Tol;
        if (dx > 0f) cost += dx / 16f * g.WX;
        if (cx < g.MinX) cost += (g.MinX - cx) / 16f * 20f;
        if (cx > g.MaxX) cost += (cx - g.MaxX) / 16f * 20f;
        if (!float.IsNaN(g.PreferY)) cost += Math.Abs(s.Pos.Y + PlayerSim.H / 2f - g.PreferY) / 16f * g.WY;
        return cost;
    }
}

/// <summary>Aiming and trigger discipline.</summary>
public static class Gun
{
    /// <summary>Distance the held weapon's projectile covers per tick (shoot speed x updates).</summary>
    public static float ShotSpeed(Player p, out int projType)
    {
        Item w = p.HeldItem;
        float speed = w.shootSpeed;
        projType = w.shoot;
        if (w.useAmmo > 0)
        {
            Item ammo = p.ChooseAmmo(w);
            if (ammo != null && !ammo.IsAir)
            {
                speed += ammo.shootSpeed;
                if (ammo.shoot > 0) projType = ammo.shoot;
                if (w.useAmmo == Terraria.ID.AmmoID.Arrow && p.archery && speed < 20f) speed = Math.Min(speed * 1.2f, 20f);
            }
        }
        int upd = projType > 0 ? ContentSamples.ProjectilesByType[projType].extraUpdates + 1 : 1;
        return speed * upd;
    }

    /// <summary>First-order intercept point for a constant-velocity target.</summary>
    public static Vector2 Lead(Vector2 from, Vector2 target, Vector2 tvel, float speed)
    {
        Vector2 d = target - from;
        float a = Vector2.Dot(tvel, tvel) - speed * speed;
        float b = 2f * Vector2.Dot(d, tvel);
        float c = Vector2.Dot(d, d);
        float t;
        if (Math.Abs(a) < 1e-4f) t = b < 0 ? -c / b : 0f;
        else
        {
            float disc = b * b - 4f * a * c;
            if (disc < 0f) return target;
            float sq = (float)Math.Sqrt(disc);
            float t1 = (-b - sq) / (2f * a), t2 = (-b + sq) / (2f * a);
            t = Math.Min(t1, t2);
            if (t < 0f) t = Math.Max(t1, t2);
        }
        if (t < 0f || t > 240f) return target;
        return target + tvel * t;
    }

    /// <summary>Aims at a world point and pulls the trigger (re-pressing for weapons without autofire).</summary>
    public static void Fire(Player p, Vector2 aim, bool shoot)
    {
        FightContext.Aim(aim);
        if (!shoot) { p.controlUseItem = false; return; }
        p.controlUseItem = p.HeldItem.autoReuse || (p.itemAnimation == 0 && p.releaseUseItem);
    }
}
