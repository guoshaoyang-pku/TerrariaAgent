using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.UI;

namespace BossPilot.Explore;

/// <summary>
/// A running exploration task. Control is called from ModPlayer.SetControls every tick (controls are
/// already cleared); a task ends by setting Result ("success" or "fail: ...").
/// </summary>
internal abstract class TaskExec
{
    public string Result;
    public readonly Navigator Nav = new();
    protected int Tick;

    public abstract void Start(Player p);
    public abstract void Control(Player p);

    /// <summary>Task-specific fields for the result row.</summary>
    public virtual object Detail => null;

    public static TaskExec Create(ExploreTask t) => t.Type switch
    {
        "landmark" => new LandmarkTask(t.Target),
        "goto" => new GotoTask(t.X ?? Main.spawnTileX, t.Y ?? Main.spawnTileY),
        "ore" => new GatherTask(t.Ores.Select(o => Resources.OreTiles.TryGetValue(o, out int id) ? id : -1).Where(id => id >= 0).ToHashSet(), t.Count),
        "life_crystal" => new GatherTask([TileID.Heart], t.Count),
        "chests" => new ChestTask(t.Count),
        "recall" => new RecallTask(),
        _ => throw new ArgumentException($"unknown task type {t.Type}"),
    };

    protected void FollowNav(Player p)
    {
        Nav.Control(p);
        if (Nav.Failure != null && Result == null) Result = "fail: " + Nav.Failure;
    }

    protected static void Mine(Player p, Point tile)
    {
        if (p.itemAnimation == 0) p.selectedItem = Kit.PickSlot;
        if (p.velocity.X > 0.3f) p.controlLeft = true;
        else if (p.velocity.X < -0.3f) p.controlRight = true;
        Navigator.Aim(tile);
        p.controlUseItem = p.selectedItem == Kit.PickSlot;
    }
}

/// <summary>Reach a landmark; arrival is the game's own Zone/layer state, not just the goal box.</summary>
internal sealed class LandmarkTask(string target) : TaskExec
{
    private int arrivedTicks;

    public override void Start(Player p)
    {
        IGoal goal = Landmarks.Goal(target, Navigator.NodeOf(p));
        if (goal == null) Result = $"fail: no {target} in this world";
        else Nav.SetGoal(goal);
    }

    public override void Control(Player p)
    {
        Tick++;
        // Biome flags come from the scene-metrics tile scan, which a server only runs on demand.
        if (Tick % 20 == 0) p.ForceUpdateBiomes();
        if (Landmarks.Arrived(target, p))
        {
            Result = "success";
            return;
        }
        FollowNav(p);
        if (Nav.Arrived && ++arrivedTicks > 120)
        {
            // Zone flags need enough biome tiles on screen; walk on to the middle of the span once.
            Landmarks.Span s = refined ? null : Landmarks.Nearest(target, Navigator.NodeOf(p).X);
            if (s != null)
            {
                refined = true;
                arrivedTicks = 0;
                Nav.SetGoal(new BoxGoal(s.Mid - 2, 0, s.Mid + 2, (int)Main.worldSurface + 8));
            }
            else Result = "fail: in goal box but landmark not detected";
        }
    }

    private bool refined;

    public override object Detail => new { target, goal = Nav.Goal?.ToString() };
}

internal sealed class GotoTask(int x, int y) : TaskExec
{
    public override void Start(Player p) => Nav.SetGoal(new BoxGoal(x - 2, y - 3, x + 1, y + 2));

    public override void Control(Player p)
    {
        if (Nav.Arrived)
        {
            Result = "success";
            return;
        }
        FollowNav(p);
    }
}

/// <summary>
/// Mine veins of the given tile types until `count` of their drops are in the inventory: pick the
/// cheapest vein, walk/dig into reach of it, mine every tile in reach, repeat, then pick up the drops.
/// Also used for life crystals (a 2x2 "vein" of Heart tiles).
/// </summary>
internal sealed class GatherTask(HashSet<int> types, int count) : TaskExec
{
    private enum Phase { Select, Approach, Mine, Collect }
    private Phase phase;
    private List<Resources.Vein> veins;
    private Resources.Vein vein;
    private readonly HashSet<int> skipped = [];
    private readonly HashSet<int> skippedItems = [];
    private readonly List<object> visited = [];
    private int[] itemTypes;
    private int startCount, mineTicks, collectItem = -1, phaseTicks;
    private Point mineTile;
    private Miner miner;

    private int Have(Player p) => itemTypes.Sum(t => Kit.Count(p, t)) - startCount;

    public override void Start(Player p)
    {
        itemTypes = types.Select(t => Resources.TileDrop.GetValueOrDefault(t, -1)).Where(i => i > 0).Distinct().ToArray();
        startCount = itemTypes.Sum(t => Kit.Count(p, t));
        miner = new Miner(p, p.inventory[Kit.PickSlot]);
        veins = Resources.ScanVeins(types, new Rectangle(41, 41, Main.maxTilesX - 82, Main.maxTilesY - 82));
        if (types.Count == 0) Result = "fail: no ore types";
    }

    public override void Control(Player p)
    {
        Tick++;
        phaseTicks++;
        if (Have(p) >= count)
        {
            Result = "success";
            return;
        }
        switch (phase)
        {
            case Phase.Select:
                vein = PickVein(p);
                if (vein == null)
                {
                    Result = Have(p) > 0 ? "fail: veins exhausted" : "fail: no reachable vein";
                    return;
                }
                visited.Add(new { id = vein.Id, type = TileID.Search.GetName(vein.Type), tiles = vein.Tiles.Count, at = new[] { vein.Centroid.X, vein.Centroid.Y } });
                Approach();
                break;
            case Phase.Approach:
                if (Nav.Arrived || InReachAny(p))
                {
                    SetPhase(Phase.Mine);
                    mineTicks = 0;
                    break;
                }
                Nav.Control(p);
                // A vein that takes over two minutes to reach is probably walled in: try the next one.
                if (Nav.Failure != null || phaseTicks > 7200)
                {
                    skipped.Add(vein.Id);
                    SetPhase(Phase.Select);
                }
                break;
            case Phase.Mine:
            {
                List<Point> left = Remaining();
                if (left.Count == 0)
                {
                    SetPhase(Phase.Collect);
                    break;
                }
                if (mineTicks == 0 || !Alive(mineTile) || !Navigator.InReach(p, mineTile))
                {
                    Point? next = left.Where(t => Navigator.InReach(p, t))
                        .OrderBy(t => Vector2.DistanceSquared(new Vector2(t.X * 16 + 8, t.Y * 16 + 8), p.Center))
                        .Cast<Point?>().FirstOrDefault();
                    if (next == null)
                    {
                        if (p.velocity.Y != 0f) break; // still settling after the floor was mined away
                        Approach();
                        break;
                    }
                    mineTile = next.Value;
                    mineTicks = 1;
                }
                int limit = Math.Max(miner.Ticks(mineTile.X, mineTile.Y), 20) * 2 + 90;
                if (++mineTicks > limit)
                {
                    NavGrid.Blacklist.Add(NavGrid.Key(mineTile.X, mineTile.Y));
                    vein.Tiles.Remove(mineTile);
                    mineTicks = 0;
                    break;
                }
                Mine(p, mineTile);
                break;
            }
            case Phase.Collect:
            {
                Item it = collectItem >= 0 ? Main.item[collectItem] : null;
                if (it == null || !it.active || !itemTypes.Contains(it.type) || phaseTicks > 900)
                {
                    if (it != null && it.active && phaseTicks > 900) skippedItems.Add(collectItem);
                    collectItem = NearestDrop(p);
                    if (collectItem < 0)
                    {
                        SetPhase(Phase.Select);
                        break;
                    }
                    Item c = Main.item[collectItem];
                    Point n = Kinematics.NodeOf(c.position, c.width, c.height);
                    Nav.SetGoal(new BoxGoal(n.X - 2, n.Y - 2, n.X + 1, n.Y + 2));
                    phaseTicks = 0;
                }
                Nav.Control(p);
                if (Nav.Failure != null || Nav.Arrived && phaseTicks > 120)
                {
                    skippedItems.Add(collectItem);
                    collectItem = -1;
                }
                break;
            }
        }
    }

    private void SetPhase(Phase ph)
    {
        phase = ph;
        phaseTicks = 0;
    }

    private void Approach()
    {
        List<Point> left = Remaining().Where(t => miner.Ticks(t.X, t.Y) >= 0).ToList();
        if (left.Count == 0)
        {
            SetPhase(Phase.Collect);
            return;
        }
        Nav.SetGoal(new ReachGoal(left));
        SetPhase(Phase.Approach);
    }

    private static bool Alive(Point t) => Main.tile[t.X, t.Y].HasTile;

    private List<Point> Remaining()
    {
        vein.Tiles.RemoveAll(t => !Main.tile[t.X, t.Y].HasTile || Main.tile[t.X, t.Y].TileType != vein.Type);
        return vein.Tiles;
    }

    private bool InReachAny(Player p) => Remaining().Any(t => Navigator.InReach(p, t) && miner.Ticks(t.X, t.Y) >= 0);

    private Resources.Vein PickVein(Player p)
    {
        Point at = Navigator.NodeOf(p);
        return veins.Where(v => !skipped.Contains(v.Id))
            .Where(v =>
            {
                v.Tiles.RemoveAll(t => !Main.tile[t.X, t.Y].HasTile || Main.tile[t.X, t.Y].TileType != v.Type);
                return v.Tiles.Count > 0 && v.Tiles.Any(t => miner.Ticks(t.X, t.Y) >= 0) && !NearLava(v.Centroid) && Accessible(v);
            })
            .OrderBy(v => Resources.TravelEstimate(at, v.Centroid) - Math.Min(v.Tiles.Count, 20) * 15f)
            .FirstOrDefault();
    }

    /// <summary>Some vein tile has open air or pickaxe-diggable ground within reach (not sealed in e.g. ebonstone).</summary>
    private bool Accessible(Resources.Vein v)
    {
        foreach (Point t in v.Tiles)
            for (int dx = -ReachGoal.Left; dx <= ReachGoal.Right; dx += 3)
            for (int dy = -ReachGoal.Up; dy <= ReachGoal.Down; dy += 2)
            {
                Tile n = Main.tile[t.X + dx, t.Y + dy];
                bool open = !n.HasTile || !Main.tileSolid[n.TileType] || Main.tileSolidTop[n.TileType];
                if (open && !(n.LiquidAmount > 0 && n.LiquidType == LiquidID.Lava)) return true;
                if (n.HasTile && types.Contains(n.TileType) == false && miner.Ticks(t.X + dx, t.Y + dy) > 0) return true;
            }
        return false;
    }

    private static bool NearLava(Point c)
    {
        for (int x = c.X - 3; x <= c.X + 3; x++)
        for (int y = c.Y - 3; y <= c.Y + 3; y++)
        {
            Tile t = Main.tile[x, y];
            if (t.LiquidAmount > 0 && t.LiquidType == LiquidID.Lava) return true;
        }
        return false;
    }

    private int NearestDrop(Player p)
    {
        int best = -1;
        float bestD = 30f * 16f;
        for (int i = 0; i < Main.maxItems; i++)
        {
            Item it = Main.item[i];
            if (!it.active || !itemTypes.Contains(it.type) || skippedItems.Contains(i)) continue;
            float d = Vector2.Distance(it.Center, p.Center);
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }
        return best;
    }

    public override object Detail => new { veins = visited, skipped = skipped.Count };
}

/// <summary>Loot the nearest unlocked, non-empty chests: walk into reach, right-click, Loot All.</summary>
internal sealed class ChestTask(int count) : TaskExec
{
    private readonly HashSet<int> done = [];
    private readonly HashSet<int> skipped = [];
    private readonly List<object> log = [];
    private int chest = -1, openTicks, looted, fallbacks;

    public override void Start(Player p) => Next(p);

    private void Next(Player p)
    {
        Point at = Navigator.NodeOf(p);
        chest = -1;
        float best = float.MaxValue;
        for (int i = 0; i < Main.maxChests; i++)
        {
            Chest c = Main.chest[i];
            if (c == null || done.Contains(i) || skipped.Contains(i)) continue;
            Tile t = Main.tile[c.x, c.y];
            if (!t.HasTile || !TileID.Sets.BasicChest[t.TileType] || Chest.IsLocked(c.x, c.y)) continue;
            if (!c.item.Any(it => it != null && !it.IsAir)) continue;
            float d = Resources.TravelEstimate(at, new Point(c.x, c.y));
            if (d < best)
            {
                best = d;
                chest = i;
            }
        }
        if (chest < 0)
        {
            Result = looted > 0 ? "fail: no more chests" : "fail: no reachable chest";
            return;
        }
        Chest ch = Main.chest[chest];
        Nav.SetGoal(new ReachGoal([new Point(ch.x, ch.y), new Point(ch.x + 1, ch.y), new Point(ch.x, ch.y + 1), new Point(ch.x + 1, ch.y + 1)]));
        openTicks = 0;
    }

    public override void Control(Player p)
    {
        if (looted >= count)
        {
            Result = "success";
            return;
        }
        if (chest < 0) return;
        Chest c = Main.chest[chest];
        if (!Nav.Arrived)
        {
            Nav.Control(p);
            if (Nav.Failure != null)
            {
                skipped.Add(chest);
                log.Add(new { chest, at = new[] { c.x, c.y }, result = "unreachable: " + Nav.Failure });
                Next(p);
            }
            return;
        }
        // In reach: interact like a right click, the way TileInteractionsUse opens a chest.
        openTicks++;
        if (p.chest != chest)
        {
            if (p.velocity.X > 0.3f) p.controlLeft = true;
            else if (p.velocity.X < -0.3f) p.controlRight = true;
            Navigator.Aim(new Point(c.x, c.y));
            p.controlUseTile = openTicks % 2 == 1;
            if (openTicks > 30)
            {
                // The interaction path never fired headless; open it directly (same state change).
                fallbacks++;
                p.OpenChest(c.x, c.y, chest);
            }
            return;
        }
        int before = c.item.Count(it => it != null && !it.IsAir);
        ChestUI.LootAll();
        int after = c.item.Count(it => it != null && !it.IsAir);
        p.chest = -1;
        Main.playerInventory = false;
        done.Add(chest);
        looted++;
        log.Add(new { chest, at = new[] { c.x, c.y }, stacks = before - after, open_ticks = openTicks });
        if (looted < count) Next(p);
    }

    public override object Detail => new { looted, open_fallbacks = fallbacks, chests = log };
}

/// <summary>Use the Magic Mirror to return to spawn.</summary>
internal sealed class RecallTask : TaskExec
{
    private Vector2 start;

    public override void Start(Player p)
    {
        start = p.Center;
        if (Kit.Count(p, ItemID.MagicMirror) == 0) Result = "fail: no mirror";
    }

    public override void Control(Player p)
    {
        Tick++;
        if (Landmarks.Arrived("spawn", p) && (Tick == 1 || Vector2.Distance(p.Center, start) > 64f))
        {
            Result = "success";
            return;
        }
        if (p.itemAnimation == 0) p.selectedItem = Kit.MirrorSlot;
        p.controlUseItem = p.selectedItem == Kit.MirrorSlot;
        if (Tick > 600) Result = "fail: mirror did not fire";
    }
}
