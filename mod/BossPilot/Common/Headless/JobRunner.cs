using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;
using Terraria;

namespace BossPilot.Common.Headless;

/// <summary>
/// Executes a job file in headless mode: every scenario is attempted
/// `attempts` times; each attempt appends one row to results.jsonl in the out
/// dir and one line to stdout. Between attempts the pilot is healed, buffs
/// and hostile entities are cleared, and the arena is rebuilt.
/// Jobs with "kind": "explore" are handed to Explore.ExploreRunner instead (natural world, no arena).
/// </summary>
public sealed class JobRunner
{
    public sealed class Job
    {
        [JsonPropertyName("kind")] public string Kind { get; init; } = "boss";
        [JsonPropertyName("scenarios")] public List<ScenarioConfig> Scenarios { get; init; } = [];
        [JsonPropertyName("attempts")] public int Attempts { get; init; } = 1;
    }

    private readonly Job spec;
    private readonly StreamWriter results;
    private readonly Explore.ExploreRunner explore;
    private readonly Stopwatch attemptClock = new();
    private int scenarioIndex;
    private int attempt;

    public bool Started { get; private set; }
    public bool Done { get; private set; }

    private JobRunner(Job spec, string outDir)
    {
        this.spec = spec;
        results = new StreamWriter(Path.Combine(outDir, "results.jsonl"), append: true) { AutoFlush = true };
    }

    private JobRunner(Explore.ExploreRunner explore) => this.explore = explore;

    public static JobRunner Load(string path, string outDir)
    {
        string json = File.ReadAllText(path);
        Job spec = JsonSerializer.Deserialize<Job>(json,
            new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (spec.Kind == "explore") return new JobRunner(Explore.ExploreRunner.Load(json, outDir));
        return new JobRunner(spec, outDir);
    }

    public void Begin()
    {
        Started = true;
        if (explore != null)
        {
            explore.Begin();
            Done = explore.Done;
            return;
        }
        StartAttempt();
    }

    public void AfterTick()
    {
        if (explore != null)
        {
            explore.AfterTick();
            Done = explore.Done;
            return;
        }
        if (TrialRunner.Instance.Current != TrialRunner.Phase.Finished) return;
        Record(TrialRunner.Instance.Result);
        TrialRunner.Instance.Abort();
        if (++attempt >= spec.Attempts)
        {
            attempt = 0;
            scenarioIndex++;
        }
        StartAttempt();
    }

    private void StartAttempt()
    {
        while (scenarioIndex < spec.Scenarios.Count)
        {
            ResetWorld();
            string msg = TrialRunner.Instance.Start(spec.Scenarios[scenarioIndex]);
            attemptClock.Restart();
            if (msg.StartsWith("prepared")) return;
            Record("error: " + msg);
            scenarioIndex++;
        }
        Done = true;
        results.Dispose();
    }

    /// <summary>Ends the job after a harness exception, recording it against the current scenario.</summary>
    public void Fail(string reason)
    {
        if (Done) return;
        if (explore != null)
        {
            explore.Fail(reason);
            Done = true;
            return;
        }
        if (scenarioIndex < spec.Scenarios.Count) Record("crash: " + reason);
        Done = true;
        results.Dispose();
    }

    private void Record(string result)
    {
        TrialRunner t = TrialRunner.Instance;
        ScenarioConfig s = spec.Scenarios[scenarioIndex];
        double wall = attemptClock.Elapsed.TotalSeconds;
        var row = new
        {
            scenario = s.Name,
            boss = s.Boss,
            attempt,
            result,
            seconds = Math.Round(t.FightTicks / 60.0, 2),
            hits = t.Hits,
            damage_taken = t.DamageTaken,
            boss_hp_frac = Math.Round(t.BossHpFraction, 4),
            plan = t.Plan?.GetType().Name ?? $"none:{s.Controller}",
            loadout = s.Loadout,
            speedup = wall > 0 ? Math.Round(t.FightTicks / 60.0 / wall, 1) : 0,
            state = StateExport.Instance.RunPath,
        };
        string line = JsonSerializer.Serialize(row);
        results.WriteLine(line);
        Console.WriteLine("[BossPilot] " + line);
    }

    private static void ResetWorld()
    {
        foreach (NPC n in Main.npc)
            if (n.active && !n.townNPC) n.active = false;
        foreach (Projectile pr in Main.projectile) pr.active = false;
        foreach (Item it in Main.item) it.active = false;

        Player p = Main.LocalPlayer;
        if (p.dead)
        {
            p.dead = false;
            p.respawnTimer = 0;
            p.Spawn(PlayerSpawnContext.ReviveFromDeath);
        }
        for (int i = 0; i < Player.MaxBuffs; i++)
        {
            p.buffType[i] = 0;
            p.buffTime[i] = 0;
        }
        p.statLife = p.statLifeMax2;
        p.statMana = p.statManaMax2;
        p.immune = false;
        p.immuneTime = 0;
        p.velocity = Vector2.Zero;
    }
}
