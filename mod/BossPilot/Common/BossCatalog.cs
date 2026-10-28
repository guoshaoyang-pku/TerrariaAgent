using System.Collections.Generic;

namespace BossPilot.Common;

/// <summary>
/// Vanilla boss NPC IDs for Terraria 1.4.4.
/// Values follow the vanilla NPCID table; verify against the decompiled
/// kernel sources (third_party/tModLoader) once tModLoader setup has run.
/// Multi-part bosses list every part that must be tracked.
/// </summary>
public static class BossCatalog
{
    public record BossEntry(string Name, int NpcId, int[] ExtraPartIds, int ProgressionOrder, bool Hardmode);

    public static readonly IReadOnlyDictionary<string, BossEntry> Bosses = new Dictionary<string, BossEntry>
    {
        ["king_slime"]       = new("king_slime",       50,  [],                  1,  false),
        ["eye_of_cthulhu"]   = new("eye_of_cthulhu",   4,   [],                  2,  false),
        ["eater_of_worlds"]  = new("eater_of_worlds",  13,  [14, 15],            3,  false),
        ["brain_of_cthulhu"] = new("brain_of_cthulhu", 266, [],                  3,  false),
        ["queen_bee"]        = new("queen_bee",        222, [],                  4,  false),
        ["skeletron"]        = new("skeletron",        35,  [36],                5,  false),
        ["deerclops"]        = new("deerclops",        668, [],                  5,  false),
        ["wall_of_flesh"]    = new("wall_of_flesh",    113, [114],               6,  false),
        ["queen_slime"]      = new("queen_slime",      657, [],                  7,  true),
        ["twins"]            = new("twins",            125, [126],               8,  true),
        ["destroyer"]        = new("destroyer",        134, [135, 136],          8,  true),
        ["skeletron_prime"]  = new("skeletron_prime",  127, [128, 129, 130, 131], 8, true),
        ["plantera"]         = new("plantera",         262, [263, 264],          9,  true),
        ["golem"]            = new("golem",            245, [246, 247, 248],     10, true),
        ["duke_fishron"]     = new("duke_fishron",     370, [],                  11, true),
        ["empress_of_light"] = new("empress_of_light", 636, [],                  11, true),
        ["lunatic_cultist"]  = new("lunatic_cultist",  439, [440],               12, true),
        ["moon_lord"]        = new("moon_lord",        398, [396, 397],          13, true),
    };

    public static bool TryGet(string name, out BossEntry entry) => Bosses.TryGetValue(name, out entry);
}
