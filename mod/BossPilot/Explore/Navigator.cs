using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace BossPilot.Explore;

/// <summary>
/// Lower-layer navigation to a goal: plans with the windowed A* (amortised over ticks), follows the
/// path with native controls, and turns jump/fall edges into short-horizon MPC: candidate control
/// policies are rolled out through the kinematic model (the game's collision code on the live map)
/// and the one landing furthest along the path is committed, re-searched whenever the real body
/// deviates from the prediction. Dig edges become pickaxe work, pillar/bridge edges block placement.
/// Called from ModPlayer.SetControls once per tick.
/// </summary>
internal sealed class Navigator
{
    public IGoal Goal { get; private set; }
    public bool Arrived { get; private set; }
    public string Failure { get; private set; }
    public string Mode { get; private set; } = "idle";
    public Action<string> Log;

    // Metrics.
    public int Replans, Searches, Deviations, Placed, PlanFails;
    public double PlanMs, SearchMs;

    private const int LookAhead = 40, PlanBudget = 6000;
    private Planner planner;
    private List<Planner.Step> path;
    private bool pathToGoal;
    private int idx, bestIdx, lastProgressTick, offPathTicks, stuckCount, tick, planFailStreak, searchHold, jumpFails;
    private Point stuckNode = new(-1, -1);

    private readonly Dictionary<(long, long), int> attempts = [];
    private int offPathCount;
    private readonly Dictionary<(long, long), int> searchFails = [];
    private bool committed;
    private PolicyRun run;
    private Vector2[] pred = new Vector2[160], predBest = new Vector2[160], predTmp = new Vector2[160];
    private int predLen, runT;
    private Point committedLanding;

    private enum Act { None, Mine, Bridge, Pillar }
    private Act act;
    private readonly List<Point> work = [];
    private int workTicks, workLimit, pillarPhase, pillarTries, phaseTicks;
    private bool issued;
    private Point workCell;

    public void SetGoal(IGoal goal)
    {
        Goal = goal;
        Arrived = false;
        Failure = null;
        path = null;
        planner = null;
        committed = false;
        act = Act.None;
        stuckCount = 0;
        planFailStreak = 0;
        lastProgressTick = tick;
    }

    public void Replan(string why)
    {
        Log?.Invoke($"replan: {why}");
        path = null;
        planner = null;
        committed = false;
        act = Act.None;
    }

    public static void ClearControls(Player p)
    {
        p.controlLeft = p.controlRight = p.controlUp = p.controlDown = p.controlJump = false;
        p.controlUseItem = p.controlUseTile = p.controlHook = p.controlMount = p.controlThrow = false;
        p.controlQuickHeal = p.controlQuickMana = p.controlInv = p.controlSmart = p.controlTorch = false;
    }

    public static void Aim(Point tile)
    {
        Main.mouseX = (int)(tile.X * 16 + 8 - Main.screenPosition.X);
        Main.mouseY = (int)(tile.Y * 16 + 8 - Main.screenPosition.Y);
    }

    public static Point NodeOf(Player p) => Kinematics.NodeOf(p.position, p.width, p.height);

    /// <summary>Standing: no vertical speed, or the small speeds of walking down slopes with floor right below.</summary>
    public static bool Grounded(Player p) =>
        p.velocity.Y == 0f || p.velocity.Y > 0f && p.velocity.Y <= 1f && p.jump == 0
        && Collision.SolidCollision(new Vector2(p.position.X, p.position.Y + p.height), p.width, 3, true);

    public static bool SolidTile(int x, int y)
    {
        Tile t = Main.tile[x, y];
        return t.HasTile && !t.IsActuated && Main.tileSolid[t.TileType] && !Main.tileSolidTop[t.TileType];
    }

    public static bool SupportTile(int x, int y)
    {
        Tile t = Main.tile[x, y];
        return t.HasTile && !t.IsActuated && Main.tileSolid[t.TileType] && (!Main.tileSolidTop[t.TileType] || t.TileFrameY == 0);
    }

    public void Control(Player p)
    {
        tick++;
        if (Goal == null || Failure != null) return;
        Point node = NodeOf(p);
        bool grounded = Grounded(p);
        if (grounded && Goal.Contains(node.X, node.Y))
        {
            Arrived = true;
            Mode = "arrived";
            Brake(p);
            return;
        }
        Arrived = false;

        if (FallReflex(p, grounded)) return;
        if (SuffocationReflex(p)) return;

        if (act != Act.None)
        {
            if (Math.Abs(p.velocity.X) > 2.5f || p.velocity.Y > (act == Act.Pillar ? 9f : 6f))
            {
                Log?.Invoke($"{act} aborted: knocked/falling v {p.velocity}");
                if (act is Act.Pillar or Act.Bridge) BanNextEdge(node);
                act = Act.None;
                committed = false;
                path = null;
                Replans++;
                return;
            }
            DoAct(p, node, grounded);
            return;
        }

        if (committed)
        {
            if (grounded && runT >= 2)
                committed = false; // landed
            else if (runT < predLen && Vector2.Distance(p.position, pred[runT]) > 6f)
            {
                Deviations++;
                Log?.Invoke($"deviation t{runT} pos {p.position} pred {pred[runT]}");
                committed = false;
            }
            else
            {
                SimState s = SimState.From(p);
                ApplyInput(p, run.Next(runT, s));
                runT++;
                Mode = "air";
                return;
            }
        }

        if (path == null)
        {
            Mode = "plan";
            Brake(p);
            if (grounded || p.wet) Plan(p, node);
            return;
        }

        SyncIndex(node, grounded);
        if (idx < 0)
        {
            if (++offPathTicks > 12 && (grounded || p.wet))
            {
                offPathTicks = 0;
                if (++offPathCount % 20 == 1)
                    Log?.Invoke($"off path at {node} pos {p.position} v {p.velocity}; path {string.Join(" ", path.Skip(Math.Max(0, bestIdx - 1)).Take(5))}\n{Dump(node.X, node.Y)}");
                Replan("off path");
                return;
            }
            idx = Math.Max(0, bestIdx);
        }
        else offPathTicks = 0;

        // No progress for a while: replan from here; repeated at one spot is a failure.
        if (tick - lastProgressTick > 360)
        {
            lastProgressTick = tick;
            if (node == stuckNode) stuckCount++;
            else stuckCount = 1;
            stuckNode = node;
            if (stuckCount >= 4)
            {
                Failure = "stuck";
                return;
            }
            Replan("no progress");
            return;
        }

        if (!grounded)
        {
            if (!SearchAndCommit(p, node, airborne: true)) HoldToward(p, path[Math.Min(idx + 1, path.Count - 1)].X, node);
            return;
        }

        if (idx >= path.Count - 1)
        {
            Replan(pathToGoal ? "end without arrival" : "horizon reached");
            return;
        }

        Planner.Step next = path[idx + 1];
        Planner.Step cur = path[idx];
        int dir = Math.Sign(next.X - cur.X);
        Mode = next.Move.ToString();
        // Terrain edits act on the body where it actually stands (half blocks and slopes shift it a row
        // from the planner's node); further off than one tile, replan.
        if (next.Move is Planner.Move.Bridge or Planner.Move.Pillar or Planner.Move.DigUp or Planner.Move.DigDown
                or Planner.Move.DigWalk or Planner.Move.DigStepUp
            && (node.X != cur.X || node.Y != cur.Y))
        {
            if (Math.Abs(node.X - cur.X) > 1 || Math.Abs(node.Y - cur.Y) > 1)
            {
                Replan("misaligned for " + next.Move);
                return;
            }
            cur = new Planner.Step { X = node.X, Y = node.Y, Move = cur.Move };
            next = new Planner.Step { X = node.X + dir, Y = next.Y - path[idx].Y + node.Y, Move = next.Move };
        }
        switch (next.Move)
        {
            case Planner.Move.Walk:
            case Planner.Move.StepUp:
                Walk(p, node, next.X, dir, SpeedCap());
                break;
            case Planner.Move.Fall:
                Walk(p, node, next.X, dir, 2.2f);
                break;
            case Planner.Move.Jump:
                if (p.wet && !p.lavaWet)
                {
                    // Water physics (half speed, 30-tick jumps) are outside the dry templates: swim toward it.
                    HoldToward(p, next.X, node);
                    p.controlJump = next.Y <= node.Y + 1 && (tick & 15) < 13;
                    Mode = "swim";
                    break;
                }
                if (node.X != cur.X || Math.Abs(node.Y - cur.Y) > 1)
                {
                    Walk(p, node, cur.X, Math.Sign(cur.X - node.X), 2f);
                    break;
                }
                if (tick < searchHold)
                {
                    Align(p, cur.X);
                    break;
                }
                if (!SearchAndCommit(p, node, airborne: false))
                {
                    // From speed the jump did not work out: settle on the take-off node and retry from rest.
                    // Slopes can keep the body from ever settling, so failures are also counted per edge.
                    var edge = (NavGrid.Key(cur.X, cur.Y), NavGrid.Key(next.X, next.Y));
                    int fails = searchFails.GetValueOrDefault(edge) + 1;
                    searchFails[edge] = fails;
                    bool atRest = Math.Abs(p.velocity.X) <= 0.3f && Centered(p, cur.X);
                    if (atRest && ++jumpFails >= 2 || fails >= 6)
                    {
                        Log?.Invoke($"jump {cur}->{next} infeasible ({fails} searches); banning edge\n{Dump(cur.X, cur.Y)}");
                        Planner.BannedEdges.Add(edge);
                        jumpFails = 0;
                        Replan("jump infeasible");
                        break;
                    }
                    searchHold = tick + (atRest ? 2 : 8);
                    Align(p, cur.X);
                }
                else jumpFails = 0;
                break;
            case Planner.Move.Drop:
                if (!Centered(p, cur.X) || Math.Abs(p.velocity.X) > 0.5f) Align(p, cur.X);
                else p.controlDown = true;
                break;
            case Planner.Move.DigWalk:
            case Planner.Move.DigStepUp:
            case Planner.Move.DigDown:
            case Planner.Move.DigUp:
            {
                List<Point> cells = DigCells(cur, next);
                cells.RemoveAll(c => !SolidTile(c.X, c.Y));
                bool vertical = next.Move is Planner.Move.DigDown or Planner.Move.DigUp;
                if (cells.Count == 0)
                {
                    if (next.Move == Planner.Move.DigUp) StartPillar(p, cur);
                    else if (vertical) Walk(p, node, cur.X, 0, 0.5f);
                    else Walk(p, node, next.X, dir, 2f);
                    break;
                }
                if (vertical && !Centered(p, cur.X) || Math.Abs(p.velocity.X) > 0.6f)
                {
                    Align(p, cur.X);
                    break;
                }
                StartMine(cells);
                DoAct(p, node, grounded);
                break;
            }
            case Planner.Move.Pillar:
                if (!Centered(p, cur.X) || Math.Abs(p.velocity.X) > 0.5f) Align(p, cur.X);
                else StartPillar(p, cur);
                break;
            case Planner.Move.Bridge:
            {
                Point cell = dir > 0 ? new Point(cur.X + 1, cur.Y + 1) : new Point(cur.X, cur.Y + 1);
                if (SupportTile(cell.X, cell.Y)) Walk(p, node, next.X, dir, 2f); // placed (or already there): step on
                else if (Math.Abs(p.velocity.X) > 0.5f) Brake(p);
                else
                {
                    act = Act.Bridge;
                    workCell = cell;
                    workTicks = 0;
                    issued = false;
                }
                break;
            }
        }
    }

    private int reflexHold;

    /// <summary>
    /// Safety reflex below the planner: a long fall that no committed rollout is handling fires the
    /// Cloud in a Bottle before the 25-tile fall-damage threshold (any upward speed resets fallStart).
    /// </summary>
    private bool FallReflex(Player p, bool grounded)
    {
        if (reflexHold > 0)
        {
            reflexHold--;
            p.controlJump = true;
            return true;
        }
        if (grounded || p.velocity.Y <= 1f || committed && run.P.CloudDelay != Policy.Never) return false;
        int fell = (int)(p.position.Y / 16f) - p.fallStart;
        if (fell < 19 || !p.GetJumpState(Terraria.ModLoader.ExtraJump.CloudInABottle).Available) return false;
        if (!p.releaseJump) return true; // release this tick so next tick's press registers
        Log?.Invoke($"fall reflex: cloud after {fell} tiles");
        p.controlJump = true;
        reflexHold = 11;
        committed = false;
        return true;
    }

    /// <summary>Buried by falling sand (Suffocation drains life without a hit): mine out the tiles in the body.</summary>
    private bool SuffocationReflex(Player p)
    {
        if (!p.HasBuff(BuffID.Suffocation)) return false;
        int x0 = (int)(p.position.X / 16f), x1 = (int)((p.position.X + p.width - 1) / 16f);
        int y0 = (int)(p.position.Y / 16f), y1 = (int)((p.position.Y + p.height - 1) / 16f);
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
            if (SolidTile(x, y))
            {
                if (path != null) Replan("suffocating");
                if (p.itemAnimation == 0) p.selectedItem = Kit.PickSlot;
                Aim(new Point(x, y));
                p.controlUseItem = p.selectedItem == Kit.PickSlot;
                Mode = "unbury";
                return true;
            }
        return false;
    }

    // ---------------------------------------------------------------- planning

    private void Plan(Player p, Point node)
    {
        if (planner == null)
        {
            var miner = new Miner(p, p.inventory[Kit.PickSlot]);
            (int x0, int y0, int w, int h) = Window(node, Goal.Nearest(node), 1f + 0.6f * planFailStreak);
            var grid = new NavGrid(x0, y0, w, h, miner);
            // The 20 px body can stand on the last pixels of a ledge, where the rounded node has no floor:
            // start from the nearest node that is standable (else just free), preferring the same row.
            Point start = node;
            Point[] around = [new(0, 0), new(-1, 0), new(1, 0), new(0, -1), new(0, 1), new(-1, -1), new(1, -1), new(-1, 1), new(1, 1)];
            Point? pick = null;
            foreach (Point o in around)
                if (grid.Standable(node.X + o.X, node.Y + o.Y)) { pick = new Point(node.X + o.X, node.Y + o.Y); break; }
            if (pick == null)
                foreach (Point o in around)
                    if (grid.BodyFree(node.X + o.X, node.Y + o.Y)) { pick = new Point(node.X + o.X, node.Y + o.Y); break; }
            if (pick != null) start = pick.Value;
            int blocks = Kit.Count(p, ItemID.DirtBlock);
            planner = new Planner(grid, Goal, start.X, start.Y, blocks) { FrontierSlack = planFailStreak == 0 ? -20f : 60f * planFailStreak };
            if (planFailStreak > 0) planner.Escape();
            Replans++;
        }
        planner.Run(PlanBudget);
        if (!planner.Done) return;
        PlanMs += planner.Ms;
        Log?.Invoke($"plan {planner.Note}: {planner.Path?.Count ?? 0} steps, {planner.Expanded} expanded, {planner.Ms:F1} ms, window {planner.Grid.X0},{planner.Grid.Y0} {planner.Grid.W}x{planner.Grid.H}");
        if (planner.Path == null || planner.Path.Count < 2 && !planner.ReachedGoal)
        {
            PlanFails++;
            if (++planFailStreak >= 3) Failure = "no path: " + planner.Note;
            planner = null;
            return;
        }
        planFailStreak = 0;
        path = planner.Path;
        pathToGoal = planner.ReachedGoal;
        planner = null;
        idx = 0;
        bestIdx = 0;
        lastProgressTick = tick;
    }

    /// <summary>
    /// Planning window: the pilot sits near the trailing edge and the window extends toward the goal's
    /// nearest point — 300 x 180 for lateral goals, 160 x 340 for depth goals — grown after failed plans.
    /// </summary>
    private static (int, int, int, int) Window(Point n, Point a, float grow)
    {
        bool vertical = Math.Abs(a.Y - n.Y) > Math.Abs(a.X - n.X);
        int w = (int)((vertical ? 160 : 300) * grow), h = (int)((vertical ? 340 : 180) * grow);
        static (int, int) Span(int from, int to, int size, int lead)
        {
            int lo = Math.Min(from, to), hi = Math.Max(from, to);
            if (hi - lo + 2 * lead <= size)
            {
                int pad = (size - (hi - lo)) / 2;
                return (lo - pad, lo - pad + size);
            }
            return to >= from ? (from - lead, from - lead + size) : (from + lead - size, from + lead);
        }
        (int x0, int x1) = Span(n.X, a.X, w, 40);
        (int y0, int y1) = Span(n.Y, a.Y, h, 45);
        return (x0, y0, x1 - x0, y1 - y0);
    }

    private void SyncIndex(Point node, bool grounded)
    {
        int found = -1;
        int lo = Math.Max(0, idx - 3), hi = Math.Min(path.Count - 1, idx + LookAhead);
        for (int i = lo; i <= hi; i++)
            if (path[i].X == node.X && path[i].Y == node.Y)
            {
                found = i;
                if (i >= idx) break;
            }
        if (found < 0 && grounded)
        {
            // Half blocks and slopes shift the feet by a row; accept a neighbour at or after the cursor.
            for (int i = Math.Max(idx, 0); i <= hi; i++)
                if (Math.Abs(path[i].X - node.X) <= 1 && Math.Abs(path[i].Y - node.Y) <= 1)
                {
                    found = i;
                    break;
                }
        }
        // Fresh plan whose start was shifted a tile (body overlapped a slope): we are at its start.
        if (found < 0 && bestIdx == 0 && idx <= 0 && Math.Abs(path[0].X - node.X) <= 1 && Math.Abs(path[0].Y - node.Y) <= 1) found = 0;
        idx = found;
        if (found > bestIdx)
        {
            bestIdx = found;
            lastProgressTick = tick;
        }
    }

    private float SpeedCap()
    {
        for (int k = 1; k <= 6 && idx + k < path.Count; k++)
        {
            Planner.Move m = path[idx + k].Move;
            bool reverse = idx + k >= 2 && Math.Sign(path[idx + k].X - path[idx + k - 1].X) * Math.Sign(path[idx + 1].X - path[idx].X) < 0;
            if (m is Planner.Move.Walk or Planner.Move.StepUp && !reverse) continue;
            if (m == Planner.Move.Jump && !reverse) return k <= 1 ? 4f : 6.5f;
            return k <= 1 ? 1.2f : k <= 3 ? 2.2f : 3.5f;
        }
        return 6.5f;
    }

    // ---------------------------------------------------------------- low-level motion

    private static void ApplyInput(Player p, Input c)
    {
        p.controlLeft = c.Left;
        p.controlRight = c.Right;
        p.controlJump = c.Jump;
        p.controlDown = c.Down;
    }

    private static void Brake(Player p)
    {
        if (p.velocity.X > 0.3f) p.controlLeft = true;
        else if (p.velocity.X < -0.3f) p.controlRight = true;
    }

    private static bool Centered(Player p, int nodeX) => Math.Abs(p.Center.X - (nodeX + 1) * 16f) <= 3f;

    private static void Align(Player p, int nodeX)
    {
        float dx = (nodeX + 1) * 16f - p.Center.X;
        float vx = p.velocity.X;
        // Approach at a speed that friction (0.2/tick) can kill within the remaining distance.
        float want = Math.Sign(dx) * Math.Min(2.5f, MathF.Sqrt(Math.Abs(dx) * 0.3f));
        if (Math.Abs(dx) <= 3f) want = 0f;
        if (vx < want - 0.25f) p.controlRight = true;
        else if (vx > want + 0.25f) p.controlLeft = true;
    }

    private void Walk(Player p, Point node, int targetX, int dir, float cap)
    {
        if (dir == 0)
        {
            Align(p, targetX);
            return;
        }
        float vx = p.velocity.X * dir;
        if (vx > cap) return; // let friction bleed speed
        if (dir > 0) p.controlRight = true;
        else p.controlLeft = true;
    }

    private static void HoldToward(Player p, int targetX, Point node)
    {
        int d = Math.Sign(targetX - node.X);
        if (d > 0) p.controlRight = true;
        else if (d < 0) p.controlLeft = true;
    }

    // ---------------------------------------------------------------- MPC

    private struct Rollout
    {
        public Policy P;
        public float Score;
        public int Len, Matched;
        public Point Land;
    }

    private bool SearchAndCommit(Player p, Point node, bool airborne)
    {
        Searches++;
        var sw = Stopwatch.StartNew();
        MoveStats m = MoveStats.From(p);
        SimState s0 = SimState.From(p);
        s0.Airborne = airborne;
        int target = Math.Min(idx + 1, path.Count - 1);
        int dir = Math.Sign(path[target].X - node.X);
        if (dir == 0) dir = Math.Sign(path[Math.Min(idx + 2, path.Count - 1)].X - node.X);
        if (dir == 0) dir = p.direction;
        bool holding = airborne && p.jump > 0 && !p.releaseJump;
        // A ground jump refreshes the Cloud in a Bottle (Player.RefreshDoubleJumps) later this tick.
        bool cloud = airborne ? s0.CloudAvail : m.Cloud;

        var best = new Rollout { Score = float.MinValue };
        void Try(Policy pol)
        {
            Rollout r = Evaluate(pol, s0, m, airborne, predTmp, out int len);
            if (r.Score > best.Score)
            {
                best = r;
                (predBest, predTmp) = (predTmp, predBest);
                best.Len = len;
            }
        }

        // The planner's own primitive for this edge first (it was validated on the same map from rest).
        if (!airborne && path[target].Move == Planner.Move.Jump) Try(JumpTemplates.All[path[target].Tpl].P);
        var holds = airborne ? (holding ? new byte[] { 0, 15 } : new byte[] { 0 }) : new byte[] { 3, 8, 15 };
        sbyte[] clouds = cloud ? [Policy.Never, 0, Policy.FallSafety] : [Policy.Never];
        foreach (byte jh in holds)
        foreach (sbyte cd in clouds)
        {
            Try(new Policy { Dir = 0, DirTo = 0, JumpHold = jh, CloudDelay = cd });
            Try(new Policy { Dir = (sbyte)dir, DirTo = short.MaxValue, JumpHold = jh, CloudDelay = cd });
            foreach (short to in airborne ? new short[] { 3, 6, 10, 16, 24 } : new short[] { 6, 12, 20, 30 })
                Try(new Policy { Dir = (sbyte)dir, DirTo = to, Brake = true, JumpHold = jh, CloudDelay = cd });
            if (!airborne) Try(new Policy { Dir = (sbyte)dir, DirFrom = 8, DirTo = short.MaxValue, JumpHold = jh, CloudDelay = cd });
            else
            {
                Try(new Policy { Dir = (sbyte)-dir, DirTo = short.MaxValue, JumpHold = jh, CloudDelay = cd });
                Try(new Policy { Dir = (sbyte)-dir, DirTo = 6, Brake = true, JumpHold = jh, CloudDelay = cd });
            }
        }
        // Local refinement around the best rollout.
        for (int round = 0; round < 2 && best.Score > float.MinValue; round++)
        {
            Policy b = best.P;
            if (!airborne)
                foreach (int dh in new[] { -2, -1, 1, 2 })
                {
                    int h = b.JumpHold + dh;
                    if (h >= 1 && h <= 15) Try(b with { JumpHold = (byte)h });
                }
            if (b.DirTo is > 0 and < short.MaxValue)
                foreach (int dt in new[] { -3, -1, 1, 3, 6 })
                {
                    int to = b.DirTo + dt;
                    if (to > b.DirFrom) Try(b with { DirTo = (short)to });
                }
            Try(b with { Brake = !b.Brake });
            if (cloud)
                foreach (sbyte cd in new sbyte[] { Policy.Never, 0, 4, 8, Policy.FallSafety })
                    if (cd != b.CloudDelay) Try(b with { CloudDelay = cd });
        }
        SearchMs += sw.Elapsed.TotalMilliseconds;

        bool progress = best.Matched > idx;
        if (best.Score == float.MinValue || !progress && !airborne)
        {
            Log?.Invoke($"search {(airborne ? "air" : "ground")} at {node} pos {p.position} v {p.velocity} idx {idx}: no progress (best {best.P} -> {best.Land} m{best.Matched}); next {string.Join(" ", path.Skip(idx).Take(4))}");
            return false;
        }
        if (!airborne && idx + 1 < path.Count)
        {
            var edge = (NavGrid.Key(path[idx].X, path[idx].Y), NavGrid.Key(path[idx + 1].X, path[idx + 1].Y));
            int n = attempts.GetValueOrDefault(edge) + 1;
            attempts[edge] = n;
            if (n > 3)
            {
                Log?.Invoke($"jump {path[idx]}->{path[idx + 1]} tried {n - 1} times; banning edge\n{Dump(node.X, node.Y)}");
                Planner.BannedEdges.Add(edge);
                Replan("jump keeps failing");
                return false;
            }
        }
        Log?.Invoke($"search {(airborne ? "air" : "ground")} at {node} idx {idx}->{best.Matched} {best.P} land {best.Land} in {best.Len}t score {best.Score:F1}");
        (pred, predBest) = (predBest, pred);
        predLen = best.Len;
        run = new PolicyRun(best.P);
        committedLanding = best.Land;
        committed = true;
        runT = 0;
        ApplyInput(p, run.Next(0, SimState.From(p)));
        runT = 1;
        return true;
    }

    private Rollout Evaluate(Policy pol, SimState s, MoveStats m, bool airborne, Vector2[] traj, out int len)
    {
        var run = new PolicyRun(pol);
        int t = 0, hurtTicks = 0;
        const int maxT = 150;
        bool startedAir = airborne;
        for (; t < maxT && t < traj.Length - 1; t++)
        {
            traj[t] = s.Pos;
            Kinematics.Step(ref s, m, run.Next(t, s));
            if (s.Lava) break;
            if (Collision.HurtTiles(s.Pos, m.Width, m.Height, null).type >= 0) hurtTicks++;
            if (s.Airborne && (t > 0 || startedAir) && (s.Vel.Y == 0f || s.Vel.Y > 0f && s.Vel.Y <= 1f && s.Jump == 0
                && Collision.SolidCollision(new Vector2(s.Pos.X, s.Pos.Y + m.Height), m.Width, 3, true))) { t++; break; }
            if (!s.Airborne && t > 40) { t++; break; }
        }
        traj[t] = s.Pos;
        len = t;
        Point land = s.Node(m.Width, m.Height);
        var r = new Rollout { P = pol, Land = land, Matched = -1, Score = float.MinValue };
        if (s.Lava) return r;
        int matched = -1;
        bool near = false;
        int hi = Math.Min(path.Count - 1, idx + LookAhead);
        for (int i = idx; i <= hi; i++)
            if (path[i].X == land.X && path[i].Y == land.Y) matched = i;
        if (matched < 0)
            for (int i = idx; i <= hi; i++)
                if (Math.Abs(path[i].X - land.X) <= 1 && Math.Abs(path[i].Y - land.Y) <= 1)
                {
                    matched = i;
                    near = true;
                }
        // No-hit is the objective: 10 HP of fall damage outweighs 25 nodes of progress.
        float pen = (near ? 6f : 0f) + t * 0.05f + s.Damage * 25f + hurtTicks * 150f + (s.WasWet ? 3f : 0f) + (s.Vel.Y == 0f ? 0f : 4f);
        if (matched >= 0)
        {
            r.Matched = matched;
            r.Score = matched * 10f - pen;
        }
        else if (airborne)
        {
            // Nothing lands on the path: land somewhere safe and close to the goal, then replan.
            r.Score = -10000f - Goal.H(land.X, land.Y) - pen * 10f;
        }
        return r;
    }

    // ---------------------------------------------------------------- terrain work

    private static List<Point> DigCells(Planner.Step cur, Planner.Step next)
    {
        int x = cur.X, y = cur.Y;
        int d = Math.Sign(next.X - cur.X);
        var cells = new List<Point>();
        switch (next.Move)
        {
            case Planner.Move.DigWalk:
            {
                int col = d > 0 ? x + 2 : x - 1;
                for (int j = y - 2; j <= y; j++) cells.Add(new Point(col, j));
                break;
            }
            case Planner.Move.DigStepUp:
            {
                int col = d > 0 ? x + 2 : x - 1;
                cells.Add(new Point(x, y - 3));
                cells.Add(new Point(x + 1, y - 3));
                for (int j = y - 3; j <= y - 1; j++) cells.Add(new Point(col, j));
                break;
            }
            case Planner.Move.DigDown:
                cells.Add(new Point(x, y + 1));
                cells.Add(new Point(x + 1, y + 1));
                break;
            case Planner.Move.DigUp:
                cells.Add(new Point(x, y - 3));
                cells.Add(new Point(x + 1, y - 3));
                break;
        }
        return cells;
    }

    private void BanNextEdge(Point node)
    {
        if (path != null && idx >= 0 && idx + 1 < path.Count)
            Planner.BannedEdges.Add((NavGrid.Key(path[idx].X, path[idx].Y), NavGrid.Key(path[idx + 1].X, path[idx + 1].Y)));
    }

    private void StartMine(List<Point> cells)
    {
        work.Clear();
        work.AddRange(cells);
        act = Act.Mine;
        workTicks = 0;
        workLimit = 0;
    }

    private void StartPillar(Player p, Planner.Step cur)
    {
        act = Act.Pillar;
        pillarPhase = 0;
        pillarTries = 0;
        workTicks = 0;
        // The column with a solid block under it and nothing in the cell itself (furniture blocks placement).
        bool Free(int x) => !Main.tile[x, cur.Y].HasTile || Main.tileCut[Main.tile[x, cur.Y].TileType];
        int col = SolidTile(cur.X, cur.Y + 1) && Free(cur.X) ? cur.X : cur.X + 1;
        if (!SolidTile(col, cur.Y + 1) && SolidTile(cur.X, cur.Y + 1)) col = cur.X;
        workCell = new Point(col, cur.Y);
    }

    private void DoAct(Player p, Point node, bool grounded)
    {
        workTicks++;
        switch (act)
        {
            case Act.Mine:
            {
                Mode = "mine";
                while (work.Count > 0 && !SolidTile(work[0].X, work[0].Y))
                {
                    work.RemoveAt(0);
                    workTicks = 0;
                    workLimit = 0;
                }
                if (work.Count == 0)
                {
                    act = Act.None;
                    return;
                }
                Point c = work[0];
                if (workLimit == 0)
                {
                    int ticks = new Miner(p, p.inventory[Kit.PickSlot]).Ticks(c.X, c.Y);
                    workLimit = ticks < 0 ? 1 : ticks * 2 + 90;
                }
                if (!InReach(p, c) || workTicks > workLimit)
                {
                    Log?.Invoke($"mine {c} failed ({(InReach(p, c) ? "timeout" : "out of reach")})");
                    if (InReach(p, c)) NavGrid.Blacklist.Add(NavGrid.Key(c.X, c.Y));
                    Replan("mining failed");
                    return;
                }
                Brake(p);
                if (p.itemAnimation == 0) p.selectedItem = Kit.PickSlot;
                Aim(c);
                p.controlUseItem = p.selectedItem == Kit.PickSlot;
                return;
            }
            case Act.Bridge:
            {
                Mode = "bridge";
                if (SupportTile(workCell.X, workCell.Y))
                {
                    if (issued) Placed++;
                    act = Act.None;
                    return;
                }
                if (workTicks > 60 || Kit.Count(p, ItemID.DirtBlock) == 0)
                {
                    Log?.Invoke($"bridge at {workCell} failed\n{Dump(node.X, node.Y)}");
                    BanNextEdge(node);
                    Replan("bridge failed");
                    return;
                }
                Brake(p);
                if (p.itemAnimation == 0) p.selectedItem = Kit.BlockSlot;
                Aim(workCell);
                p.controlUseItem = p.selectedItem == Kit.BlockSlot;
                issued |= p.controlUseItem;
                return;
            }
            case Act.Pillar:
            {
                Mode = "pillar";
                if (workTicks > 200 || pillarTries > 3 || Kit.Count(p, ItemID.DirtBlock) == 0)
                {
                    Log?.Invoke($"pillar at {workCell} failed\n{Dump(node.X, node.Y)}");
                    BanNextEdge(node);
                    Replan("pillar failed");
                    return;
                }
                if (pillarPhase == 0)
                {
                    if (p.itemAnimation == 0) p.selectedItem = Kit.BlockSlot;
                    if (!grounded || Math.Abs(p.velocity.X) > 0.4f)
                    {
                        Brake(p);
                        return;
                    }
                    p.controlJump = true;
                    pillarPhase = 1;
                    phaseTicks = 0;
                    pillarTries++;
                    return;
                }
                if (pillarPhase == 1)
                {
                    phaseTicks++;
                    p.controlJump = p.velocity.Y < 0f && p.jump > 8;
                    if (SupportTile(workCell.X, workCell.Y))
                    {
                        Placed++;
                        pillarPhase = 2;
                        return;
                    }
                    if (p.position.Y + p.height <= workCell.Y * 16 - 1)
                    {
                        Aim(workCell);
                        p.controlUseItem = p.selectedItem == Kit.BlockSlot;
                    }
                    if (grounded && phaseTicks > 3) pillarPhase = 0;
                    return;
                }
                if (grounded)
                {
                    act = Act.None;
                    return;
                }
                return;
            }
        }
    }

    /// <summary>ASCII map around a node for logs: # solid, = platform, ~ water, L lava, @ body.</summary>
    public static string Dump(int x, int y, int rx = 14, int ry = 8)
    {
        var sb = new System.Text.StringBuilder();
        for (int j = y - ry; j <= y + 3; j++)
        {
            sb.Append($"{j,5} ");
            for (int i = x - rx; i <= x + rx; i++)
            {
                Tile t = Main.tile[i, j];
                char c = '.';
                if (t.LiquidAmount > 0) c = t.LiquidType == LiquidID.Lava ? 'L' : '~';
                if (t.HasTile && !t.IsActuated && Main.tileSolid[t.TileType]) c = Main.tileSolidTop[t.TileType] ? '=' : t.IsHalfBlock ? 'h' : t.Slope != Terraria.ID.SlopeType.Solid ? '/' : '#';
                else if (t.HasTile) c = ',';
                if (i >= x && i <= x + 1 && j >= y - 2 && j <= y) c = '@';
                sb.Append(c);
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Player.IsTargetTileInItemRange for the held pickaxe/block (tileBoost 0).</summary>
    public static bool InReach(Player p, Point t, int boost = 0) =>
        p.position.X / 16f - Player.tileRangeX - boost <= t.X && (p.position.X + p.width) / 16f + Player.tileRangeX + boost - 1f >= t.X
        && p.position.Y / 16f - Player.tileRangeY - boost <= t.Y && (p.position.Y + p.height) / 16f + Player.tileRangeY + boost - 2f >= t.Y;
}
