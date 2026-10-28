using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BossPilot.Explore;

/// <summary>
/// An exploration job ({"kind": "explore", ...}), run by ExploreRunner on the natural world.
/// One attempt is one pass over the task list; a task that starts at spawn first restores the
/// world snapshot taken at job start, so attempts are repeatable.
/// </summary>
public sealed class ExploreJob
{
    [JsonPropertyName("kind")] public string Kind { get; init; } = "explore";
    [JsonPropertyName("tasks")] public List<ExploreTask> Tasks { get; init; } = [];
    [JsonPropertyName("attempts")] public int Attempts { get; init; } = 1;
    /// <summary>Natural hostile spawns. Off = the Spec's early "small dodges avoid monsters" simplification.</summary>
    [JsonPropertyName("spawns")] public bool Spawns { get; init; }
    [JsonPropertyName("kit")] public KitSpec Kit { get; init; } = new();
    /// <summary>"day" or "night" at the start of every spawn-started task.</summary>
    [JsonPropertyName("time")] public string Time { get; init; } = "day";
    /// <summary>Write a 6 Hz position trace per attempt.</summary>
    [JsonPropertyName("trace")] public bool Trace { get; init; } = true;
    /// <summary>Write the world summary (landmarks, biome spans, chest/ore counts) at job start.</summary>
    [JsonPropertyName("world_info")] public bool WorldInfo { get; init; } = true;

    public static ExploreJob Parse(string json) =>
        JsonSerializer.Deserialize<ExploreJob>(json, new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
        ?? throw new JsonException("empty explore job");
}

/// <summary>
/// One exploration task. Types:
/// landmark (target: ocean_left, ocean_right, dungeon, jungle, snow, desert, evil, corruption, crimson,
/// underground, cavern, underworld, spawn), goto (x, y in tiles), ore (ores: copper, tin, iron, lead,
/// silver, tungsten, gold, platinum, demonite, crimtane, meteorite, hellstone, obsidian; count: ore items),
/// chests (count: chests looted), life_crystal (count), recall (magic mirror home).
/// </summary>
public sealed class ExploreTask
{
    [JsonPropertyName("name")] public string Name { get; init; }
    [JsonPropertyName("type")] public string Type { get; init; } = "landmark";
    [JsonPropertyName("target")] public string Target { get; init; }
    [JsonPropertyName("ores")] public List<string> Ores { get; init; } = [];
    [JsonPropertyName("count")] public int Count { get; init; } = 1;
    [JsonPropertyName("x")] public int? X { get; init; }
    [JsonPropertyName("y")] public int? Y { get; init; }
    [JsonPropertyName("max_seconds")] public double MaxSeconds { get; init; } = 600;
    /// <summary>"spawn" (restore world, respawn, fresh kit) or "continue" (keep position, inventory and world).</summary>
    [JsonPropertyName("start")] public string Start { get; init; } = "spawn";
    /// <summary>Per-task override of the job's hostile-spawn switch.</summary>
    [JsonPropertyName("spawns")] public bool? Spawns { get; init; }
    /// <summary>Attempt label for the result row (runners flatten attempts into the task list).</summary>
    [JsonPropertyName("attempt")] public int? Attempt { get; init; }

    public string Label => Name ?? (Type == "landmark" ? Target : Type);
}

/// <summary>
/// Early-game exploration kit. Defaults are what a pre-boss speedrun plausibly holds after the first
/// surface/underground sweep: a gold-tier pickaxe, Hermes Boots and Cloud in a Bottle (common chest
/// loot), a Grappling Hook (iron chain + hook), a Magic Mirror, building blocks, platforms and rope.
/// </summary>
public sealed class KitSpec
{
    [JsonPropertyName("pickaxe")] public string Pickaxe { get; init; } = "GoldPickaxe";
    [JsonPropertyName("life")] public int Life { get; init; } = 200;
    [JsonPropertyName("boots")] public bool Boots { get; init; } = true;
    [JsonPropertyName("cloud")] public bool Cloud { get; init; } = true;
    [JsonPropertyName("hook")] public bool Hook { get; init; } = true;
    [JsonPropertyName("mirror")] public bool Mirror { get; init; } = true;
    [JsonPropertyName("blocks")] public int Blocks { get; init; } = 200;
    [JsonPropertyName("platforms")] public int Platforms { get; init; } = 99;
    [JsonPropertyName("torches")] public int Torches { get; init; } = 99;
    [JsonPropertyName("rope")] public int Rope { get; init; } = 99;
    [JsonPropertyName("potions")] public int Potions { get; init; } = 5;
    [JsonPropertyName("weapon")] public string Weapon { get; init; } = "GoldBroadsword";
}
