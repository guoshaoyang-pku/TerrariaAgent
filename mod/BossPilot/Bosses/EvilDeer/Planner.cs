using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;

namespace BossPilot.Bosses.EvilDeer;

/// <summary>
/// An open-loop input sequence over the look-ahead horizon: direction A until Switch, B until
/// Switch2, then C; one jump (JumpAt, held JumpHold ticks), an optional cloud jump, and an
/// optional drop through the platform underfoot.
/// </summary>
public readonly struct Maneuver
{
    public const int None = 255;
    public readonly sbyte A, B, C;
    public readonly byte Switch, Switch2, JumpAt, JumpHold, CloudAt, CloudHold, DownTicks;

    public Maneuver(int a, int b, int switchAt, int jumpAt, int jumpHold, int cloudAt, bool down, int cloudHold = 14, int downTicks = 8, int c = 2, int switch2 = 255)
    {
        A = (sbyte)a; B = (sbyte)b; C = (sbyte)(c == 2 ? b : c);
        Switch = (byte)switchAt; Switch2 = (byte)Math.Max(switch2, switchAt);
        JumpAt = (byte)jumpAt; JumpHold = (byte)jumpHold; CloudAt = (byte)cloudAt; CloudHold = (byte)cloudHold;
        DownTicks = (byte)(down ? downTicks : 0);
    }

    public bool Down => DownTicks > 0;
    public bool Jumps => JumpAt != None;

    public void Input(int t, out int dir, out bool jump, out bool down)
    {
        dir = t < Switch ? A : t < Switch2 ? B : C;
        jump = (JumpAt != None && t >= JumpAt && t < JumpAt + JumpHold) || (CloudAt != None && t >= CloudAt && t < CloudAt + CloudHold);
        down = t < DownTicks;
    }

    /// <summary>The same plan one tick later, so a maneuver found earlier stays available.</summary>
    public Maneuver Shift()
    {
        int s1 = Switch == 0 ? 0 : Switch - 1, s2 = Switch2 == 0 ? 0 : Switch2 == None ? None : Switch2 - 1;
        int a = A, b = B;
        if (s1 == 0)
        {
            a = B; b = C; s1 = s2; s2 = None;
            if (s1 == 0) a = C;
        }
        int ja = JumpAt, jh = JumpHold;
        if (ja != None)
        {
            if (ja > 0) ja--;
            else if (--jh <= 0) ja = None;
        }
        int ca = CloudAt, ch = CloudHold;
        if (ca != None)
        {
            if (ca > 0) ca--;
            else if (--ch <= 0) ca = None;
        }
        int dt = DownTicks > 0 ? DownTicks - 1 : 0;
        return new Maneuver(a, b, s1, ja, Math.Max(jh, 0), ca, dt > 0, Math.Max(ch, 0), dt, C, s2);
    }
}

/// <summary>Terminal/path cost of a simulated pilot state (lower is better).</summary>
public delegate float StateCost(in PilotState s, int t);

/// <summary>
/// Receding-horizon dodge planner: every tick it simulates ~90 candidate
/// maneuvers (15 walking-only ones when nothing is in reach) for 72 ticks with
/// PilotModel, checks them against the predicted HazardField, and applies the
/// first input of the best one. Last tick's choice is re-tried one tick later
/// (warm start), and while a hit is still predicted 300 random maneuvers are
/// searched. Colliding candidates are ranked by how late the first collision
/// is (far-future ones weigh little); safe ones by clearance plus the plan's
/// objective. Overlaps during the pilot's immunity frames only cost.
/// </summary>
public sealed class Planner
{
    public const int H = HazardField.Horizon;

    public readonly PilotModel Model = new();
    public readonly HazardField Field = new();
    public ArenaGrid Grid;
    /// <summary>Extra pixels around the pilot hitbox treated as contact.</summary>
    public float Margin = 4f;
    /// <summary>Clearance (px) under which the danger cost starts to grow.</summary>
    public float Soft = 36f;
    /// <summary>First predicted collision tick of the chosen maneuver (H + 1 = none).</summary>
    public int PredictedHit { get; private set; } = H + 1;
    public Maneuver Chosen { get; private set; }
    /// <summary>The planner that last chose controls (for hit diagnostics).</summary>
    public static Planner Active { get; private set; }

    private readonly List<Maneuver> cands = [];
    private PilotState check;
    private bool hasCheck;
    /// <summary>Largest one-step model error seen (px), for diagnostics.</summary>
    public Vector2 MaxErr;
    public int ErrLogs;

    public void ResetDiagnostics() { MaxErr = Vector2.Zero; ErrLogs = 0; hasCheck = false; hasPrev = false; }
    private int candJumpHeight = -1;
    private int quietCount;
    private Maneuver prev;
    private bool hasPrev;
    private Random rng;
    private int immune;
    private int lastIndex = -1;
    private Box[] statics = new Box[64];
    private Track[] tracks = new Track[64];
    private LineHazard[] lines = new LineHazard[64];
    private int nStatics, nTracks, nLines;

    private void BuildCandidates(int jumpHeight)
    {
        cands.Clear();
        candJumpHeight = jumpHeight;
        int[] dirs = [-1, 0, 1];
        var dirPairs = new List<(int, int, int)>();
        foreach (int a in dirs) dirPairs.Add((a, a, H));
        foreach (int sw in (int[])[8, 24])
            foreach (int a in dirs)
                foreach (int b in dirs)
                    if (a != b) dirPairs.Add((a, b, sw));
        // Walking-only candidates first: they are the quiet-time set.
        foreach ((int a, int b, int sw) in dirPairs)
            cands.Add(new Maneuver(a, b, sw, Maneuver.None, 0, Maneuver.None, false));
        quietCount = cands.Count;
        foreach ((int a, int b, int sw) in dirPairs)
        {
            cands.Add(new Maneuver(a, b, sw, 0, 40, Maneuver.None, false));
            cands.Add(new Maneuver(a, b, sw, 0, 6, Maneuver.None, false));
            cands.Add(new Maneuver(a, b, sw, 12, 40, Maneuver.None, false));
            cands.Add(new Maneuver(a, b, sw, 30, 40, Maneuver.None, false));
            cands.Add(new Maneuver(a, b, sw, 0, jumpHeight, jumpHeight + 2, false));
        }
        foreach (int a in dirs) cands.Add(new Maneuver(a, a, H, Maneuver.None, 0, Maneuver.None, true));
    }

    /// <summary>
    /// Chooses and applies this tick's movement controls. <paramref name="objective"/> is
    /// sampled at mid-horizon and at the horizon end.
    /// </summary>
    public void Plan(Player p, StateCost objective)
    {
        Active = this;
        Model.Read(p);
        if (candJumpHeight != Model.JumpHeight) BuildCandidates(Model.JumpHeight);
        PilotState start = PilotModel.Capture(p);
        Filter(start);
        // Contact cannot hurt while the pilot is immune: overlaps then only cost, they do not count as hits.
        immune = p.immune ? p.immuneTime : 0;

        bool onPlatform = StandingOnPlatform(start);
        int count = Threatened ? cands.Count : quietCount;
        float best = float.MaxValue;
        int bestHit = H + 1;
        Maneuver bestM = cands[0];
        int bestIdx = -1;
        for (int i = 0; i < count; i++)
            if (Consider(cands[i], start, objective, onPlatform, ref best, ref bestM, ref bestHit, i == lastIndex ? 2f : 0f)) bestIdx = i;
        // Warm start: last tick's choice, advanced one tick, keeps a found escape alive.
        if (hasPrev && Consider(prev.Shift(), start, objective, onPlatform, ref best, ref bestM, ref bestHit, 3f)) bestIdx = -1;
        // Emergency: a hit is still predicted, so search random maneuvers more finely.
        if (bestHit <= H)
        {
            rng ??= new Random(1234);
            for (int i = 0; i < 300; i++)
            {
                int a = rng.Next(3) - 1, b = rng.Next(3) - 1, c = rng.Next(3) - 1, sw = rng.Next(1, 40), sw2 = sw + rng.Next(4, 40);
                int ja = Maneuver.None, jh = 0, ca = Maneuver.None, ch = 14;
                if (rng.Next(3) > 0)
                {
                    ja = rng.Next(0, 40);
                    jh = rng.Next(2, 41);
                    if (Model.HasCloud && rng.Next(2) == 0) { ca = ja + jh + 1 + rng.Next(0, 20); ch = rng.Next(2, 20); }
                }
                bool dn = onPlatform && rng.Next(8) == 0;
                if (Consider(new Maneuver(a, b, sw, ja, jh, ca, dn, ch, rng.Next(2, 12), c, sw2), start, objective, onPlatform, ref best, ref bestM, ref bestHit, 0f)) bestIdx = -1;
            }
        }
        lastIndex = bestIdx;
        Chosen = bestM;
        prev = bestM;
        hasPrev = true;
        // Model check: compare last tick's one-step prediction with where the pilot really is.
        if (hasCheck)
        {
            float ex = Math.Abs(start.Pos.X - check.Pos.X), ey = Math.Abs(start.Pos.Y - check.Pos.Y);
            if (ex > MaxErr.X) MaxErr.X = ex;
            if (ey > MaxErr.Y) MaxErr.Y = ey;
            if ((ex > 2f || ey > 2f) && ErrLogs++ < 12)
                Trace.Line($"[model] err=({ex:F1},{ey:F1}) predicted pos={check.Pos} vel={check.Vel} actual pos={start.Pos} vel={start.Vel} jump={start.Jump} extra={start.Extra}");
        }
        check = start;
        {
            Chosen.Input(0, out int d0, out bool j0, out bool dn0);
            Model.Step(ref check, d0, j0, dn0, Grid);
            hasCheck = true;
        }
        PredictedHit = bestHit;
        Chosen.Input(0, out int dir, out bool jump, out bool down);
        p.controlLeft = dir < 0;
        p.controlRight = dir > 0;
        p.controlJump = jump;
        p.controlDown = down;
    }

    private bool Consider(in Maneuver m, in PilotState start, StateCost objective, bool onPlatform, ref float best, ref Maneuver bestM, ref int bestHit, float bonus)
    {
        if (m.Down && !onPlatform) return false;
        float score = Evaluate(m, start, objective, out int hit) - bonus;
        if (score >= best) return false;
        best = score; bestM = m; bestHit = hit;
        return true;
    }

    private float Evaluate(in Maneuver m, PilotState s, StateCost objective, out int hitTick)
    {
        hitTick = H + 1;
        float danger = 0f, obj = 0f;
        int overlap = 0;
        for (int i = 0; i < Field.Reactive.Count; i++) Field.Reactive[i].Begin();
        for (int t = 1; t <= H; t++)
        {
            m.Input(t - 1, out int dir, out bool jump, out bool down);
            Model.Step(ref s, dir, jump, down, Grid);
            Box me = new Box(s.Pos.X, s.Pos.Y, Model.W, Model.H).Inflate(Margin);
            float clear = Clearance(me, t, s.Pos + new Vector2(Model.W / 2f, Model.H / 2f));
            if (clear < 0f)
            {
                if (t > immune)
                {
                    hitTick = t;
                    return HitCost(t) + 1e3f * overlap;
                }
                overlap++;
                continue;
            }
            if (clear < Soft)
            {
                float d = Soft - clear;
                danger += d * d * (t <= 24 ? 1f : t <= 48 ? 0.5f : 0.25f);
            }
            if (t == H / 2) obj += 0.5f * objective(s, t);
        }
        obj += 1e3f * overlap;
        obj += objective(s, H);
        if (m.Jumps) obj += 30f;
        if (m.Down) obj += 200f;
        return danger * 0.05f + obj;
    }

    /// <summary>
    /// Cost of a first collision at tick t: prohibitive in the near term, fading for far-future
    /// predictions (replanning usually escapes them), so that a sure loss of position (dropping off
    /// a standing row costs several thousand) is preferred only over hits closer than ~50 ticks.
    /// </summary>
    private static float HitCost(int t) => t <= 20 ? 1e6f * (H + 2 - t) : 1e6f * (float)Math.Exp(-(t - 20) / 6f) + 300f * (H + 2 - t);

    private float Clearance(in Box me, int t, Vector2 center)
    {
        float clear = float.MaxValue;
        for (int i = 0; i < Field.Reactive.Count; i++)
            if (Field.Reactive[i].Step(t, center, out Box b)) clear = Math.Min(clear, me.Gap(b));
        for (int i = 0; i < nStatics; i++)
            clear = Math.Min(clear, me.Gap(statics[i]));
        for (int i = 0; i < nTracks; i++)
        {
            Track tr = tracks[i];
            if (tr.On[t]) clear = Math.Min(clear, me.Gap(tr.At[t]));
        }
        for (int i = 0; i < nLines; i++)
        {
            ref LineHazard l = ref lines[i];
            if (t < l.T0 || t > l.T1) continue;
            float cp = 0f;
            if (Collision.CheckAABBvLineCollision(new Vector2(me.L, me.T), new Vector2(me.R - me.L, me.B - me.T), l.A, l.B, l.Width, ref cp))
                return -1f;
            clear = Math.Min(clear, Math.Max(0f, LineGap(me, l)));
        }
        return clear;
    }

    /// <summary>Cheap clearance estimate to a line hazard (distance from box center to the segment).</summary>
    private static float LineGap(in Box me, in LineHazard l)
    {
        Vector2 c = me.Center;
        Vector2 ab = l.B - l.A;
        float len2 = ab.LengthSquared();
        float u = len2 > 0f ? MathHelper.Clamp(Vector2.Dot(c - l.A, ab) / len2, 0f, 1f) : 0f;
        float d = Vector2.Distance(c, l.A + ab * u);
        return d - l.Width / 2f - Math.Max(me.R - me.L, me.B - me.T) / 2f;
    }

    /// <summary>Keeps only hazards that can come near the pilot's reach this horizon.</summary>
    private void Filter(in PilotState s)
    {
        float reachX = H * 6.8f + 40f + Soft;
        Box env = new()
        {
            L = s.Pos.X - reachX, R = s.Pos.X + Model.W + reachX,
            T = s.Pos.Y - 300f - Soft, B = s.Pos.Y + Model.H + 520f + Soft,
        };
        nStatics = nTracks = nLines = 0;
        foreach (Box b in Field.Statics)
            if (b.Intersects(env)) Push(ref statics, ref nStatics, b);
        foreach (Track t in Field.Tracks)
            if (t.Any && t.Bounds.Intersects(env)) Push(ref tracks, ref nTracks, t);
        foreach (LineHazard l in Field.Lines)
            if (l.Bounds.Intersects(env)) Push(ref lines, ref nLines, l);
    }

    /// <summary>True when hazards are within reach (the plan may then slow fire or skip extras).</summary>
    public bool Threatened => nStatics + nTracks + nLines + Field.Reactive.Count > 0;

    private static void Push<T>(ref T[] arr, ref int n, T v)
    {
        if (n == arr.Length) Array.Resize(ref arr, n * 2);
        arr[n++] = v;
    }

    private bool StandingOnPlatform(in PilotState s)
    {
        if (s.Vel.Y != 0f) return false;
        int row = (int)Math.Round((s.Pos.Y + Model.H) / 16f);
        int l = (int)(s.Pos.X / 16f), r = (int)((s.Pos.X + Model.W - 0.01f) / 16f);
        bool plat = false;
        for (int c = l; c <= r; c++)
        {
            if (Grid.Solid(c, row)) return false;
            plat |= Grid.Platform(c, row);
        }
        return plat;
    }

    /// <summary>Simulates the chosen maneuver and returns the pilot state after <paramref name="ticks"/>.</summary>
    public PilotState Preview(Player p, in Maneuver m, int ticks)
    {
        PilotState s = PilotModel.Capture(p);
        for (int t = 0; t < ticks; t++)
        {
            m.Input(t, out int dir, out bool jump, out bool down);
            Model.Step(ref s, dir, jump, down, Grid);
        }
        return s;
    }
}
