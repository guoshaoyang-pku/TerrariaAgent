#!/usr/bin/env python3
"""Flatten one boss's scenarios from configs/boss_matrix.json into individual
scenario JSON files for the BossPilot mod's autorun / /bosspilot start flow.

Usage:
    python3 scripts/emit_scenarios.py            # all bosses -> dist dir
    python3 scripts/emit_scenarios.py king_slime # one boss
"""

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "dist" / "scenarios"


def main():
    data = json.loads((ROOT / "configs" / "boss_matrix.json").read_text(encoding="utf-8"))
    defaults = data["defaults"]
    wanted = set(sys.argv[1:])

    OUT.mkdir(parents=True, exist_ok=True)
    count = 0
    for boss, spec in data["bosses"].items():
        if wanted and boss not in wanted:
            continue
        for s in spec["scenarios"]:
            merged = {**defaults, "boss": boss,
                      "max_duration_seconds": spec.get("max_duration_seconds", 300), **s}
            if not merged.pop("enabled", True):
                continue
            (OUT / f"{merged['name']}.json").write_text(
                json.dumps(merged, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
            count += 1
    print(f"wrote {count} scenario(s) to {OUT}")


if __name__ == "__main__":
    main()
