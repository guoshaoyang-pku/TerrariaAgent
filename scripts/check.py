#!/usr/bin/env python3
"""No-game sanity checks for the Terraria Agent repo.

Validates configs/boss_matrix.json against the boss catalog and scenario
rules, and checks the BossPilot mod layout. Mirrors the C# tables in
mod/BossPilot/Common; keep both sides in sync.

Runs with the standard library only: python3 scripts/check.py
"""

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
MATRIX = ROOT / "configs" / "boss_matrix.json"
MOD = ROOT / "mod" / "BossPilot"

# Must match mod/BossPilot/Common/BossCatalog.cs
BOSSES = {
    "king_slime": 1, "eye_of_cthulhu": 2, "eater_of_worlds": 3,
    "brain_of_cthulhu": 3, "queen_bee": 4, "skeletron": 5, "deerclops": 5,
    "wall_of_flesh": 6, "queen_slime": 7, "twins": 8, "destroyer": 8,
    "skeletron_prime": 8, "plantera": 9, "golem": 10, "duke_fishron": 11,
    "empress_of_light": 11, "lunatic_cultist": 12, "moon_lord": 13,
}

# Must match the enum arrays in ScenarioConfig.cs
ARENAS = {"flat_platforms", "open_sky", "enclosed", "natural", "bridge"}
BIOMES = {"forest", "corruption", "crimson", "jungle", "underground_jungle",
          "ocean", "underworld", "dungeon", "hallow", "desert", "snow", "space"}
TIMES = {"day", "night"}
WEATHERS = {"clear", "rain", "sandstorm", "blizzard", "wind"}
LOADOUTS = {"stage_default", "min_preboss", "stage_plus"}
BUFFS = {"none", "standard"}

# Bosses that despawn (or enrage unreasonably) in daylight; day scenarios are
# rejected unless explicitly named as a daytime challenge (e.g. day Empress).
NIGHT_ONLY = {"eye_of_cthulhu", "skeletron", "twins", "destroyer", "skeletron_prime"}

failures = []


def check(ok, label):
    print(("PASS " if ok else "FAIL ") + label)
    if not ok:
        failures.append(label)


def check_matrix():
    data = json.loads(MATRIX.read_text(encoding="utf-8"))
    defaults = data.get("defaults", {})
    seen = set()

    check(set(data["bosses"]) == set(BOSSES), "boss matrix covers exactly the 18-catalog")

    for boss, spec in data["bosses"].items():
        scenarios = spec.get("scenarios", [])
        check(len(scenarios) >= 2, f"{boss}: at least two scenarios")
        for s in scenarios:
            merged = {**defaults, "max_duration_seconds": spec.get("max_duration_seconds", 300), **s}
            name = merged["name"]
            label = f"{boss}/{name}"
            check(name not in seen, f"{label}: unique name")
            seen.add(name)
            check(merged["arena"] in ARENAS, f"{label}: arena enum")
            check(merged["biome"] in BIOMES, f"{label}: biome enum")
            check(merged["time"] in TIMES, f"{label}: time enum")
            check(merged["weather"] in WEATHERS, f"{label}: weather enum")
            check(merged["loadout"] in LOADOUTS, f"{label}: loadout enum")
            check(merged["buffs"] in BUFFS, f"{label}: buffs enum")
            check(60 <= merged["max_duration_seconds"] <= 1200, f"{label}: duration sane")
            if merged["weather"] == "sandstorm":
                check(merged["biome"] == "desert", f"{label}: sandstorm needs desert")
            if merged["weather"] == "blizzard":
                check(merged["biome"] == "snow", f"{label}: blizzard needs snow")
            if boss in NIGHT_ONLY and merged["time"] == "day":
                check(name.startswith("day"), f"{label}: night-only boss day run must be an explicit daytime challenge")
            if boss == "wall_of_flesh":
                check(merged["biome"] == "underworld", f"{label}: WoF needs underworld")
            if boss == "empress_of_light" and merged["time"] == "day":
                check("daytime" in name, f"{label}: day empress must be named as such")


def check_mod_layout():
    required = [
        "build.txt", "description.txt", "BossPilot.cs", "BossPilot.csproj",
        "Common/BossCatalog.cs", "Common/ScenarioConfig.cs", "Common/TrialRunner.cs",
        "Common/ArenaBuilder.cs", "Common/StateExport.cs", "Common/ScriptPlayer.cs",
        "Common/BossPilotCommand.cs", "Localization/en-US_Mods.BossPilot.hjson",
    ]
    for rel in required:
        check((MOD / rel).is_file(), f"mod file exists: {rel}")
    for cs in sorted(MOD.rglob("*.cs")):
        text = cs.read_text(encoding="utf-8")
        check(text.count("{") == text.count("}"), f"braces balanced: {cs.name}")
        check(text.count("(") == text.count(")"), f"parens balanced: {cs.name}")


def main():
    check_matrix()
    check_mod_layout()
    if failures:
        print(f"\n{len(failures)} check(s) failed")
        sys.exit(1)
    print("\nall checks passed")


if __name__ == "__main__":
    main()
