using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Xna.Framework;
using Terraria;

namespace BossPilot.Explore;

/// <summary>Navigation target as a set of standing nodes with a cost-to-go estimate (ticks).</summary>
internal interface IGoal
{
    bool Contains(int x, int y);
    float H(int x, int y);
    /// <summary>A representative node, used to place the planning window.</summary>
    Point Anchor { get; }
    /// <summary>Whether any goal node may lie in the node rectangle (inclusive).</summary>
    bool Overlaps(int x0, int y0, int x1, int y1);
    /// <summary>Goal point closest to a node (places the planning window).</summary>
    Point Nearest(Point from);
}

/// <summary>Any node inside a rectangle of node coordinates (inclusive).</summary>
internal sealed class BoxGoal(int x0, int y0, int x1, int y1) : IGoal
{
    public readonly int X0 = x0, Y0 = y0, X1 = x1, Y1 = y1;
    public bool Contains(int x, int y) => x >= X0 && x <= X1 && y >= Y0 && y <= Y1;
    public float H(int x, int y)
    {
        int dx = x < X0 ? X0 - x : x > X1 ? x - X1 : 0;
        int dy = y < Y0 ? Y0 - y : y > Y1 ? y - Y1 : 0;
        bool down = y < Y0;
        return dx * Planner.WalkCost + dy * (down ? 3f : 6f);
    }
    public bool Overlaps(int x0, int y0, int x1, int y1) => x0 <= X1 && x1 >= X0 && y0 <= Y1 && y1 >= Y0;
    public Point Nearest(Point f) => new(Math.Clamp(f.X, X0, X1), Math.Clamp(f.Y, Y0, Y1));
    public Point Anchor => new(Math.Clamp((X0 + X1) / 2, 50, Main.maxTilesX - 50), Math.Clamp(Y0 == 0 ? Y1 : (Y0 + Y1) / 2, 50, Main.maxTilesY - 50));
    public override string ToString() => $"box[{X0},{Y0}..{X1},{Y1}]";
}

/// <summary>Any node from which at least one of the given tiles is within pickaxe/interaction reach.</summary>
internal sealed class ReachGoal : IGoal
{
    // From node (x, y) a 20x42 body reaches tiles x-4..x+5, y-5..y+3 (Player.IsTargetTileInItemRange, tileBoost 0).
    public const int Left = 4, Right = 5, Up = 5, Down = 3;
    private readonly List<Point> tiles;
    private readonly int bx0, by0, bx1, by1;

    public ReachGoal(List<Point> tiles, int boost = 0)
    {
        this.tiles = tiles;
        bx0 = int.MaxValue; by0 = int.MaxValue; bx1 = int.MinValue; by1 = int.MinValue;
        foreach (Point t in tiles)
        {
            bx0 = Math.Min(bx0, t.X); by0 = Math.Min(by0, t.Y);
            bx1 = Math.Max(bx1, t.X); by1 = Math.Max(by1, t.Y);
        }
        Boost = boost;
    }

    public int Boost { get; }

    public static bool InReach(int nx, int ny, Point t, int boost = 0) =>
        t.X >= nx - Left - boost && t.X <= nx + Right + boost && t.Y >= ny - Up - boost && t.Y <= ny + Down + boost;

    public bool Contains(int x, int y)
    {
        if (x < bx0 - Right - Boost - 1 || x > bx1 + Left + Boost + 1 || y < by0 - Down - Boost - 1 || y > by1 + Up + Boost + 1) return false;
        foreach (Point t in tiles)
            if (InReach(x, y, t, Boost)) return true;
        return false;
    }

    public float H(int x, int y)
    {
        int dx = x < bx0 - Right ? bx0 - Right - x : x > bx1 + Left ? x - bx1 - Left : 0;
        int dy = y < by0 - Down ? by0 - Down - y : y > by1 + Up ? y - by1 - Up : 0;
        bool down = y < by0 - Down;
        return dx * Planner.WalkCost + dy * (down ? 3f : 6f);
    }

    public bool Overlaps(int x0, int y0, int x1, int y1) =>
        x0 <= bx1 + Left + Boost && x1 >= bx0 - Right - Boost && y0 <= by1 + Up + Boost && y1 >= by0 - Down - Boost;

    public Point Nearest(Point f) => new(Math.Clamp(f.X, bx0 - Right, bx1 + Left), Math.Clamp(f.Y, by0 - Down, by1 + Up));

    public Point Anchor => new((bx0 + bx1) / 2, (by0 + by1) / 2);
    public override string ToString() => $"reach[{tiles.Count} tiles @{Anchor}]";
}

/// <summary>
/// Jump motion primitives for the planner: free-space rollouts of the kinematic model from rest,
/// stored as the tiles the hitbox newly overlaps each tick and the row crossings where it can land.
/// </summary>
internal static class JumpTemplates
{
    public sealed class Template
    {
        public Policy P;
        public int Ticks;
        /// <summary>Cells first overlapped at tick t: CellStart[t]..CellStart[t+1].</summary>
        public int[] CellStart;
        public sbyte[] CellX, CellY;
        /// <summary>Per tick: landing row crossed while descending (relative), or sbyte.MinValue.</summary>
        public sbyte[] CrossRow, CrossX0, CrossX1, LandX;
    }

    private static Template[] all;

    public static Template[] All => all ??= Build(new MoveStats());

    public static void Reset() => all = null;

    private static Template[] Build(MoveStats m)
    {
        var list = new List<Template>();
        byte[] holds = [3, 8, 15];
        foreach (byte hold in holds)
        foreach (sbyte cloud in new sbyte[] { Policy.Never, 0 })
        {
            if (cloud == 0 && !m.Cloud) continue;
            list.Add(Make(m, new Policy { Dir = 0, DirTo = 0, JumpHold = hold, CloudDelay = cloud }));
            foreach (sbyte d in new sbyte[] { 1, -1 })
            {
                list.Add(Make(m, new Policy { Dir = d, DirFrom = 0, DirTo = short.MaxValue, JumpHold = hold, CloudDelay = cloud }));
                list.Add(Make(m, new Policy { Dir = d, DirFrom = 0, DirTo = 12, Brake = true, JumpHold = hold, CloudDelay = cloud }));
                list.Add(Make(m, new Policy { Dir = d, DirFrom = 0, DirTo = 22, Brake = true, JumpHold = hold, CloudDelay = cloud }));
                list.Add(Make(m, new Policy { Dir = d, DirFrom = 10, DirTo = short.MaxValue, JumpHold = hold, CloudDelay = cloud }));
            }
        }
        return list.ToArray();
    }

    private static Template Make(MoveStats m, Policy p)
    {
        const int maxTicks = 110;
        var s = new SimState { Pos = Kinematics.PosOf(0, 0, m.Width, m.Height), ReleaseJump = true, CloudAvail = m.Cloud };
        var run = new PolicyRun(p);
        var seen = new HashSet<(int, int)>();
        var cx = new List<sbyte>();
        var cy = new List<sbyte>();
        var start = new List<int>();
        var crossRow = new List<sbyte>();
        var cx0 = new List<sbyte>();
        var cx1 = new List<sbyte>();
        var landX = new List<sbyte>();
        AddCells(s.Pos, m, seen, cx, cy); // t = 0: the start body
        start.Add(0);
        crossRow.Add(sbyte.MinValue); cx0.Add(0); cx1.Add(0); landX.Add(0);
        int t;
        for (t = 1; t <= maxTicks; t++)
        {
            float prevBottom = s.Pos.Y + m.Height;
            Input c = run.Next(t - 1, s);
            Kinematics.Step(ref s, m, c, collide: false);
            s.Airborne = true;
            start.Add(cx.Count);
            AddCells(s.Pos, m, seen, cx, cy);
            float bottom = s.Pos.Y + m.Height;
            int rowPrev = (int)Math.Floor(prevBottom / 16f), row = (int)Math.Floor(bottom / 16f);
            if (s.Vel.Y > 0f && row > rowPrev)
            {
                crossRow.Add((sbyte)row);
                cx0.Add((sbyte)Math.Floor(s.Pos.X / 16f));
                cx1.Add((sbyte)Math.Floor((s.Pos.X + m.Width - 0.01f) / 16f));
                landX.Add((sbyte)(Math.Round((s.Pos.X + m.Width / 2f) / 16f) - 1));
            }
            else
            {
                crossRow.Add(sbyte.MinValue); cx0.Add(0); cx1.Add(0); landX.Add(0);
            }
            if (bottom / 16f > 60) break;
        }
        start.Add(cx.Count);
        return new Template
        {
            P = p, Ticks = Math.Min(t, maxTicks), CellStart = start.ToArray(), CellX = cx.ToArray(), CellY = cy.ToArray(),
            CrossRow = crossRow.ToArray(), CrossX0 = cx0.ToArray(), CrossX1 = cx1.ToArray(), LandX = landX.ToArray(),
        };
    }

    private static void AddCells(Vector2 pos, MoveStats m, HashSet<(int, int)> seen, List<sbyte> cx, List<sbyte> cy)
    {
        int x0 = (int)Math.Floor(pos.X / 16f), x1 = (int)Math.Floor((pos.X + m.Width - 0.01f) / 16f);
        int y0 = (int)Math.Floor(pos.Y / 16f), y1 = (int)Math.Floor((pos.Y + m.Height - 0.01f) / 16f);
        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
            if (seen.Add((x, y)))
            {
                cx.Add((sbyte)x);
                cy.Add((sbyte)y);
            }
    }
}

/// <summary>
/// Windowed weighted A* over standing nodes (body columns x..x+1, feet in row y). Edges: walk, step
/// up, walk-off falls, platform drops, jump templates, and terrain edits — dig sideways / stair up /
/// down, pillar up and bridge with blocks. Costs are game ticks. If the goal lies outside the
/// window, nodes on the window's border that are closer to it are terminal (receding horizon).
/// Resumable: Run(budget) expands at most `budget` nodes per call so planning spreads over ticks.
/// </summary>
internal sealed class Planner
{
    public const float WalkCost = 4f;

    public enum Move : byte { Start, Walk, StepUp, Fall, Drop, Jump, DigWalk, DigStepUp, DigDown, DigUp, Pillar, Bridge }

    public struct Step
    {
        public int X, Y;
        public Move Move;
        public short Tpl;
        public override readonly string ToString() => $"{Move}({X},{Y}){(Move == Move.Jump ? "#" + Tpl : "")}";
    }

    public readonly NavGrid Grid;
    public readonly IGoal Goal;
    public bool Done { get; private set; }
    public List<Step> Path { get; private set; }
    public bool ReachedGoal { get; private set; }
    public int Expanded { get; private set; }
    public double Ms { get; private set; }
    public string Note { get; private set; }

    private readonly int sx, sy;
    private readonly bool goalInside;
    private readonly float weight, startH;
    private readonly int maxExpand, blocks;
    private readonly float[] g;
    private readonly int[] parent;
    private readonly Move[] move;
    private readonly short[] tpl;
    private readonly bool[] closed;
    /// <summary>Block placed by the pillar/bridge edge that reached a node (grid index), so they can chain.</summary>
    private readonly int[] placed;
    private readonly PriorityQueue<int, float> open = new();
    private int bestIdx = -1;
    private float bestH = float.MaxValue;

    public static int MaxFallFree = 24, MaxFallCloud = 44;

    /// <summary>Jump edges the controller could not execute this attempt (from, to node keys).</summary>
    public static readonly HashSet<(long, long)> BannedEdges = [];

    /// <summary>Extra slack for accepting a frontier node that does not get closer (escape after failures).</summary>
    public float FrontierSlack = -20f;

    public Planner(NavGrid grid, IGoal goal, int startX, int startY, int blocks, float weight = 1.5f, int maxExpand = 80000)
    {
        Grid = grid;
        Goal = goal;
        sx = startX;
        sy = startY;
        this.blocks = blocks;
        this.weight = weight;
        this.maxExpand = maxExpand;
        int n = grid.W * grid.H;
        g = new float[n];
        Array.Fill(g, float.MaxValue);
        parent = new int[n];
        move = new Move[n];
        tpl = new short[n];
        closed = new bool[n];
        placed = new int[n];
        goalInside = goal.Overlaps(grid.X0 + 1, grid.Y0 + 1, grid.X0 + grid.W - 2, grid.Y0 + grid.H - 2);
        startH = goal.H(sx, sy);
        if (!grid.In(sx, sy))
        {
            Done = true;
            Note = "start outside window";
            return;
        }
        Point gn = goal.Nearest(new Point(sx, sy));
        fl = gn.X < grid.X0 + 3;
        fr = gn.X >= grid.X0 + grid.W - 4;
        ft = gn.Y < grid.Y0 + 5;
        fb = gn.Y >= grid.Y0 + grid.H - 3;
        if (!(fl || fr || ft || fb)) fl = fr = ft = fb = true;
        int s = grid.Index(sx, sy);
        g[s] = 0;
        parent[s] = -1;
        placed[s] = -1;
        open.Enqueue(s, weight * startH);
    }

    /// <summary>After failed plans: accept frontier nodes on every side of the window.</summary>
    public void Escape() => fl = fr = ft = fb = true;

    public void Run(int budget)
    {
        if (Done) return;
        var sw = Stopwatch.StartNew();
        while (budget-- > 0)
        {
            if (open.Count == 0 || Expanded >= maxExpand)
            {
                Finish(bestIdx, false, open.Count == 0 ? "exhausted" : "budget");
                break;
            }
            int idx = open.Dequeue();
            if (closed[idx]) continue;
            closed[idx] = true;
            Expanded++;
            int x = Grid.X0 + idx % Grid.W, y = Grid.Y0 + idx / Grid.W;
            if (Goal.Contains(x, y))
            {
                Finish(idx, true, "goal");
                break;
            }
            float h = Goal.H(x, y);
            if (h < bestH)
            {
                bestH = h;
                bestIdx = idx;
            }
            if (!goalInside && IsFrontier(x, y) && h < startH + FrontierSlack)
            {
                Finish(idx, false, "frontier");
                break;
            }
            Expand(x, y, g[idx], idx);
        }
        Ms += sw.Elapsed.TotalMilliseconds;
    }

    private bool fl, fr, ft, fb;

    /// <summary>Border band of the window on a side the goal lies beyond (any side when escaping).</summary>
    private bool IsFrontier(int x, int y) =>
        (fl && x < Grid.X0 + 3) || (fr && x >= Grid.X0 + Grid.W - 4) || (ft && y < Grid.Y0 + 5) || (fb && y >= Grid.Y0 + Grid.H - 3);

    private void Finish(int idx, bool goal, string note)
    {
        Done = true;
        ReachedGoal = goal;
        Note = note;
        if (idx < 0) return;
        var path = new List<Step>();
        for (int i = idx; i >= 0; i = parent[i])
            path.Add(new Step { X = Grid.X0 + i % Grid.W, Y = Grid.Y0 + i / Grid.W, Move = parent[i] < 0 ? Move.Start : move[i], Tpl = tpl[i] });
        path.Reverse();
        Path = path.Count > 1 || goal ? path : null;
    }

    private void Relax(int from, int x, int y, float cost, Move m, short t = 0, int placedCell = -1)
    {
        if (!Grid.In(x, y)) return;
        int idx = Grid.Index(x, y);
        if (closed[idx]) return;
        if (BannedEdges.Count > 0 && BannedEdges.Contains((NavGrid.Key(Grid.X0 + from % Grid.W, Grid.Y0 + from / Grid.W), NavGrid.Key(x, y)))) return;
        float ng = g[from] + cost + Grid.BodyPenalty(x, y);
        if (ng >= g[idx]) return;
        g[idx] = ng;
        parent[idx] = from;
        move[idx] = m;
        tpl[idx] = t;
        placed[idx] = placedCell;
        open.Enqueue(idx, ng + weight * Goal.H(x, y));
    }

    public static float FallTicks(int rows)
    {
        // From rest: gravity 0.4 px/tick^2 up to 10 px/tick (reached after 125 px).
        float px = rows * 16f;
        return px <= 125f ? MathF.Sqrt(px * 5f) : 25f + (px - 125f) / 10f;
    }

    private float FallPenalty(int rows, int lx, int ly)
    {
        if (rows <= MaxFallFree || Grid.WetBody(lx, ly)) return 0f;
        if (rows <= MaxFallCloud) return 20f; // needs the cloud jump mid-fall to reset fall damage
        return float.NaN;
    }

    private void Expand(int x, int y, float g0, int from)
    {
        NavGrid G = Grid;
        int virt = placed[from];
        // Solid in the grid, or the block this node's own pillar/bridge edge just placed.
        bool Solid(int cx, int cy) => G.IsSolid(cx, cy) || virt >= 0 && G.In(cx, cy) && G.Index(cx, cy) == virt;
        for (int d = -1; d <= 1; d += 2)
        {
            int nx = x + d;
            // Walk, or walk off a ledge and fall straight down.
            if (G.BodyFree(nx, y))
            {
                if (G.Standable(nx, y)) Relax(from, nx, y, WalkCost, Move.Walk);
                else
                {
                    for (int k = 1; k <= MaxFallCloud + 12; k++)
                    {
                        if (!G.BodyFree(nx, y + k)) break;
                        if (G.Standable(nx, y + k))
                        {
                            float pen = FallPenalty(k, nx, y + k);
                            if (!float.IsNaN(pen)) Relax(from, nx, y + k, WalkCost + FallTicks(k) + pen, Move.Fall);
                            break;
                        }
                    }
                    // Bridge: one block under the far foot.
                    if (blocks > 0)
                    {
                        int bx = d > 0 ? x + 1 : x;
                        if (G.Placeable(bx, y + 1) && Solid(d > 0 ? x : x + 1, y + 1) && G.In(bx, y + 1))
                            Relax(from, nx, y, 14f + 8f, Move.Bridge, 0, G.Index(bx, y + 1));
                    }
                }
            }
            // Step up one tile (Collision.StepUp): needs headroom over the current body.
            if (!G.Blocked(x, y - 3) && !G.Blocked(x + 1, y - 3) && G.Standable(nx, y - 1))
                Relax(from, nx, y - 1, WalkCost + 1f, Move.StepUp);

            // Dig sideways: clear the new column of the destination body.
            {
                int col = d > 0 ? x + 2 : x - 1;
                int c0 = G.ClearCost(col, y - 2), c1 = G.ClearCost(col, y - 1), c2 = G.ClearCost(col, y);
                if (c0 >= 0 && c1 >= 0 && c2 >= 0 && c0 + c1 + c2 > 0 && (G.Support(nx, y + 1) || G.Support(nx + 1, y + 1)))
                    Relax(from, nx, y, WalkCost + c0 + c1 + c2, Move.DigWalk);
            }
            // Dig a stair step up: head row over the body plus the destination's new column.
            {
                int col = d > 0 ? x + 2 : x - 1;
                int stepCol = d > 0 ? x + 2 : x - 1;
                if (G.Support(stepCol, y))
                {
                    int h0 = G.ClearCost(x, y - 3, true), h1 = G.ClearCost(x + 1, y - 3, true);
                    int c0 = G.ClearCost(col, y - 3), c1 = G.ClearCost(col, y - 2), c2 = G.ClearCost(col, y - 1);
                    if (h0 >= 0 && h1 >= 0 && c0 >= 0 && c1 >= 0 && c2 >= 0 && h0 + h1 + c0 + c1 + c2 > 0)
                        Relax(from, nx, y - 1, WalkCost + 2f + h0 + h1 + c0 + c1 + c2, Move.DigStepUp);
                }
            }
        }

        // Down: drop through platforms, or dig out the floor under the body, then fall to the next support.
        {
            bool solidUnder = G.IsSolid(x, y + 1) || G.IsSolid(x + 1, y + 1);
            bool topUnder = G.IsTop(x, y + 1) || G.IsTop(x + 1, y + 1);
            int c0 = G.ClearCost(x, y + 1), c1 = G.ClearCost(x + 1, y + 1);
            if ((solidUnder || topUnder) && c0 >= 0 && c1 >= 0)
            {
                for (int k = 1; k <= MaxFallCloud + 12; k++)
                {
                    if (k >= 2 && (G.Blocked(x, y + k) || G.Blocked(x + 1, y + k))) break;
                    if (G.Support(x, y + k + 1) || G.Support(x + 1, y + k + 1))
                    {
                        float pen = FallPenalty(k, x, y + k);
                        if (!float.IsNaN(pen))
                            Relax(from, x, y + k, (solidUnder ? c0 + c1 + 4f : 6f) + FallTicks(k) + pen, solidUnder ? Move.DigDown : Move.Drop);
                        break;
                    }
                }
            }
        }

        // Up in place: pillar a block under the feet (digging the head row first if needed).
        if (blocks > 0)
        {
            int h0 = G.ClearCost(x, y - 3, true), h1 = G.ClearCost(x + 1, y - 3, true);
            bool col0 = Solid(x, y + 1) && G.Placeable(x, y), col1 = Solid(x + 1, y + 1) && G.Placeable(x + 1, y);
            if (h0 >= 0 && h1 >= 0 && (col0 || col1) && G.In(x + 1, y))
                Relax(from, x, y - 1, 26f + 8f + h0 + h1, h0 + h1 > 0 ? Move.DigUp : Move.Pillar, 0, G.Index(col0 ? x : x + 1, y));
        }

        // Jumps from rest.
        JumpTemplates.Template[] tpls = JumpTemplates.All;
        for (short ti = 0; ti < tpls.Length; ti++)
        {
            JumpTemplates.Template T = tpls[ti];
            for (int t = 1; t < T.Ticks; t++)
            {
                int row = T.CrossRow[t];
                if (row != sbyte.MinValue)
                {
                    bool land = false;
                    for (int c = T.CrossX0[t]; c <= T.CrossX1[t]; c++)
                        if (G.Support(x + c, y + row)) land = true;
                    if (land)
                    {
                        int lx = x + T.LandX[t], ly = y + row - 1;
                        if ((lx != x || ly != y) && G.Standable(lx, ly)) Relax(from, lx, ly, t + 2f, Move.Jump, ti);
                        break;
                    }
                }
                bool blocked = false;
                for (int k = T.CellStart[t]; k < T.CellStart[t + 1]; k++)
                {
                    // Templates are dry physics: once in flight, entering water invalidates them.
                    byte f = G.F(x + T.CellX[k], y + T.CellY[k]);
                    if ((f & (NavGrid.Solid | NavGrid.Lava)) != 0 || t > 4 && (f & NavGrid.Water) != 0)
                    {
                        blocked = true;
                        break;
                    }
                }
                if (blocked) break;
            }
        }
    }
}
