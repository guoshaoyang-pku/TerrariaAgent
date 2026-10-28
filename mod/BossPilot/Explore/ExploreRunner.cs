using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace BossPilot.Explore;

/// <summary>
/// Executes an exploration job in headless mode (dispatched by JobRunner for "kind": "explore").
/// For each pass (attempt) it runs every task in order; each task appends one row to results.jsonl:
/// result, game seconds, damage taken, items gained/used, tiles dug, blocks placed and planner/MPC
/// statistics. Task logic runs in ModPlayer.SetControls (Control); bookkeeping after each tick.
/// </summary>
public sealed class ExploreRunner
{
    public static ExploreRunner Current { get; private set; }

    public bool Started { get; private set; }
    public bool Done { get; private set; }
    /// <summary>Natural hostile spawns for the running task (read by the NPC.SpawnNPC hook).</summary>
    public bool SpawnsEnabled { get; private set; } = true;

    private readonly ExploreJob job;
    private readonly string outDir;
    private StreamWriter results;
    private WorldSnapshot snapshot;
    private bool hardMode;
    private int pass, taskIndex;
    private Attempt cur;
    private readonly Stopwatch clock = new();

    private sealed class Attempt
    {
        public ExploreTask Spec;
        public TaskExec Exec;
        public int Ticks, Hits, Damage, Dug;
        public Dictionary<int, int> Inv0;
        public readonly Dictionary<string, int> Sources = [];
        public Point Start;
        public StreamWriter Trace;
    }

    private ExploreRunner(ExploreJob job, string outDir)
    {
        this.job = job;
        this.outDir = outDir;
    }

    public static ExploreRunner Load(string json, string outDir) => new(ExploreJob.Parse(json), outDir);

    public void Begin()
    {
        Started = true;
        Current = this;
        results = new StreamWriter(Path.Combine(outDir, "results.jsonl"), append: true) { AutoFlush = true };
        var sw = Stopwatch.StartNew();
        snapshot = WorldSnapshot.Take();
        hardMode = Main.hardMode; // a global flag, not in the tile arrays
        Console.WriteLine($"[BossPilot] explore: world snapshot {sw.ElapsedMilliseconds} ms, {job.Tasks.Count} tasks x {job.Attempts}");
        if (job.WorldInfo)
            File.WriteAllText(Path.Combine(outDir, "world_info.json"),
                JsonSerializer.Serialize(Landmarks.Info(), new JsonSerializerOptions { WriteIndented = true }));
        Next();
    }

    public void AfterTick()
    {
        if (cur == null || Done) return;
        cur.Ticks++;
        Player p = Main.LocalPlayer;
        if (cur.Trace != null && cur.Ticks % 10 == 0)
            cur.Trace.WriteLine(JsonSerializer.Serialize(new
            {
                t = cur.Ticks, x = Math.Round(p.Center.X / 16f, 2), y = Math.Round(p.Center.Y / 16f, 2),
                vx = Math.Round(p.velocity.X, 2), vy = Math.Round(p.velocity.Y, 2), hp = p.statLife, mode = cur.Exec.Nav.Mode,
            }));
        string result = cur.Exec.Result;
        if (result == null && p.dead) result = "death";
        if (result == null && cur.Ticks / 60.0 >= cur.Spec.MaxSeconds) result = "timeout";
        if (result == null) return;
        Record(result);
        taskIndex++;
        Next();
    }

    /// <summary>Per-tick controls, from ModPlayer.SetControls.</summary>
    public void Control(Player p)
    {
        if (cur == null || Done || cur.Exec.Result != null || p.dead) return;
        Navigator.ClearControls(p);
        cur.Exec.Control(p);
    }

    public void NoteHurt(int damage, string source)
    {
        if (cur == null) return;
        cur.Hits++;
        cur.Damage += damage;
        cur.Sources[source] = cur.Sources.GetValueOrDefault(source) + damage;
        cur.Trace?.WriteLine(JsonSerializer.Serialize(new { t = cur.Ticks, hurt = damage, source }));
    }

    public void NoteDug()
    {
        if (cur != null) cur.Dug++;
    }

    public void Fail(string reason)
    {
        if (Done) return;
        if (cur != null) Record("crash: " + reason);
        Close();
    }

    private void Close()
    {
        Done = true;
        cur?.Trace?.Dispose();
        cur = null;
        results?.Dispose();
        Current = null;
    }

    private void Next()
    {
        while (true)
        {
            if (taskIndex >= job.Tasks.Count)
            {
                taskIndex = 0;
                pass++;
            }
            if (pass >= job.Attempts || job.Tasks.Count == 0)
            {
                Close();
                return;
            }
            ExploreTask t = job.Tasks[taskIndex];
            try
            {
                StartTask(t);
                return;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[BossPilot] explore task {t.Label} failed to start: {e}");
                Record("error: " + e.Message);
                taskIndex++;
            }
        }
    }

    private void StartTask(ExploreTask t)
    {
        Player p = Main.LocalPlayer;
        bool fresh = t.Start != "continue" || cur == null && pass == 0 && taskIndex == 0 || p.dead;
        cur?.Trace?.Dispose();
        SpawnsEnabled = t.Spawns ?? job.Spawns;
        if (fresh)
        {
            snapshot.Restore();
            Main.hardMode = hardMode;
            ResetEntities(p);
            Kit.Grant(p, job.Kit);
            Main.dayTime = job.Time != "night";
            Main.time = 0;
            Main.raining = false;
            Main.StopRain();
            NavGrid.Blacklist.Clear();
            Planner.BannedEdges.Clear();
        }
        var a = new Attempt
        {
            Spec = t,
            Exec = TaskExec.Create(t),
            Inv0 = Inventory(p),
            Start = Navigator.NodeOf(p),
        };
        if (job.Trace)
        {
            string name = string.Concat(t.Label.Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_'));
            a.Trace = new StreamWriter(Path.Combine(outDir, $"trace-{name}-{t.Attempt ?? pass}.jsonl"));
            a.Exec.Nav.Log = msg => a.Trace.WriteLine(JsonSerializer.Serialize(new { t = a.Ticks, log = msg }));
        }
        cur = a;
        clock.Restart();
        a.Exec.Start(p);
        Console.WriteLine($"[BossPilot] explore: start {t.Label} pass {pass} at {a.Start} (spawns {(SpawnsEnabled ? "on" : "off")})");
    }

    private static void ResetEntities(Player p)
    {
        foreach (NPC n in Main.npc)
            if (n.active && !n.townNPC) n.active = false;
        foreach (Projectile pr in Main.projectile) pr.active = false;
        foreach (Item it in Main.item) it.active = false;
        if (p.dead)
        {
            p.dead = false;
            p.respawnTimer = 0;
        }
        p.chest = -1;
        Main.playerInventory = false;
        for (int i = 0; i < Player.MaxBuffs; i++)
        {
            p.buffType[i] = 0;
            p.buffTime[i] = 0;
        }
        p.Spawn(PlayerSpawnContext.ReviveFromDeath);
        p.immune = false;
        p.immuneTime = 0;
        p.velocity = Vector2.Zero;
        p.fallStart = (int)(p.position.Y / 16f);
    }

    private static Dictionary<int, int> Inventory(Player p)
    {
        var d = new Dictionary<int, int>();
        for (int i = 0; i < 58; i++)
        {
            Item it = p.inventory[i];
            if (it == null || it.IsAir) continue;
            d[it.type] = d.GetValueOrDefault(it.type) + it.stack;
        }
        return d;
    }

    private void Record(string result)
    {
        Attempt a = cur;
        if (a == null) return;
        Player p = Main.LocalPlayer;
        Dictionary<int, int> inv = Inventory(p);
        var gained = new SortedDictionary<string, int>();
        var used = new SortedDictionary<string, int>();
        foreach (int type in inv.Keys.Union(a.Inv0.Keys))
        {
            int d = inv.GetValueOrDefault(type) - a.Inv0.GetValueOrDefault(type);
            string name = ItemID.Search.GetName(type);
            if (d > 0) gained[name] = d;
            else if (d < 0) used[name] = -d;
        }
        Navigator n = a.Exec.Nav;
        double wall = clock.Elapsed.TotalSeconds;
        Point end = Navigator.NodeOf(p);
        var row = new
        {
            task = a.Spec.Label,
            type = a.Spec.Type,
            target = a.Spec.Target,
            attempt = a.Spec.Attempt ?? pass,
            result,
            success = result == "success",
            seconds = Math.Round(a.Ticks / 60.0, 2),
            damage_taken = a.Damage,
            hits = a.Hits,
            damage_sources = a.Sources,
            hp_end = p.statLife,
            buffs = Enumerable.Range(0, Player.MaxBuffs).Where(i => p.buffType[i] > 0 && p.buffTime[i] > 0)
                .Select(i => BuffID.Search.GetName(p.buffType[i])).ToArray(),
            items_gained = gained,
            items_used = used,
            tiles_dug = a.Dug,
            blocks_placed = n.Placed,
            replans = n.Replans,
            plan_fails = n.PlanFails,
            plan_ms = Math.Round(n.PlanMs, 1),
            mpc_searches = n.Searches,
            mpc_deviations = n.Deviations,
            mpc_ms = Math.Round(n.SearchMs, 1),
            start = new[] { a.Start.X, a.Start.Y },
            end = new[] { end.X, end.Y },
            spawns = SpawnsEnabled,
            speedup = wall > 0 ? Math.Round(a.Ticks / 60.0 / wall, 1) : 0,
            detail = a.Exec.Detail,
        };
        string line = JsonSerializer.Serialize(row);
        results.WriteLine(line);
        Console.WriteLine("[BossPilot] " + line);
        a.Trace?.Flush();
    }
}
