using System.Text.Json;
using System.Text.Json.Serialization;

namespace BossPilot.Common;

/// <summary>
/// One boss-challenge scenario, decoded from configs/boss_matrix.json.
/// Arena / time / weather / loadout variations are scenario axes; the runner
/// executes every enabled scenario and records the outcome per attempt.
/// </summary>
public sealed class ScenarioConfig
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("boss")] public required string Boss { get; init; }
    [JsonPropertyName("arena")] public string Arena { get; init; } = "flat_platforms";
    [JsonPropertyName("biome")] public string Biome { get; init; } = "forest";
    [JsonPropertyName("time")] public string Time { get; init; } = "night";
    [JsonPropertyName("weather")] public string Weather { get; init; } = "clear";
    [JsonPropertyName("loadout")] public string Loadout { get; init; } = "stage_default";
    [JsonPropertyName("buffs")] public string Buffs { get; init; } = "none";
    [JsonPropertyName("max_duration_seconds")] public double MaxDurationSeconds { get; init; } = 300;
    [JsonPropertyName("rescue_budget_ms")] public int RescueBudgetMs { get; init; } = 16;
    [JsonPropertyName("enabled")] public bool Enabled { get; init; } = true;

    public static readonly string[] Arenas = ["flat_platforms", "open_sky", "enclosed", "natural", "bridge"];
    public static readonly string[] Biomes = ["forest", "corruption", "crimson", "jungle", "underground_jungle", "ocean", "underworld", "dungeon", "hallow", "desert", "snow", "space"];
    public static readonly string[] Times = ["day", "night"];
    public static readonly string[] Weathers = ["clear", "rain", "sandstorm", "blizzard", "wind"];
    public static readonly string[] Loadouts = ["stage_default", "min_preboss", "stage_plus"];
    public static readonly string[] BuffsOptions = ["none", "standard"];

    public static ScenarioConfig Parse(string json) =>
        JsonSerializer.Deserialize<ScenarioConfig>(json, new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
        ?? throw new JsonException("empty scenario");
}
