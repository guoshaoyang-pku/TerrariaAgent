#!/bin/bash
# run_trial.sh <scenario-name> [timeout-seconds]
# Fully unattended BossPilot trial: kills tML, restores the pristine world,
# rebuilds the mod, stages the scenario as autorun, launches, and polls the
# log for the result. Requires /tmp/PilotLab.{wld,twld}.pristine snapshots.
set -u
SCENARIO="${1:?scenario name}"
TIMEOUT="${2:-420}"
REPO="/Users/guoshaoyang/Desktop/workdir/Ideas/Terraria_Agent"
TML_DATA="$HOME/Library/Application Support/Terraria/tModLoader"
TML_GAME="$HOME/Library/Application Support/Steam/steamapps/common/tModLoader"
BPDIR="$TML_DATA/Mods/BossPilot"
LOG="/tmp/bosspilot_trial.log"

export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"

pkill -9 -f 'tModLoader.dll' 2>/dev/null; sleep 3

# restore pristine world (kill -9 between runs can corrupt the save)
cp /tmp/PilotLab.wld.pristine "$TML_DATA/Worlds/PilotLab.wld"
cp /tmp/PilotLab.twld.pristine "$TML_DATA/Worlds/PilotLab.twld"

cd "$TML_DATA/ModSources/BossPilot" || exit 1
dotnet build -v q --no-incremental 2>&1 | grep -E 'error CS|Error\(s\)' | grep -v '0 Error' && exit 1

python3 "$REPO/scripts/emit_scenarios.py" >/dev/null
mkdir -p "$BPDIR/scenarios"
cp "$REPO/dist/scenarios/$SCENARIO.json" "$BPDIR/scenarios/" || { echo "no such scenario: $SCENARIO"; exit 1; }
cp "$REPO/dist/scenarios/$SCENARIO.json" "$BPDIR/autorun.json"
echo "1.1.1.12345" > "$BPDIR/bootstrap.flag"
rm -f "$BPDIR/autorun.done.json" "$BPDIR/bootstrap.done.flag"

cd "$TML_GAME" || exit 1
nohup ./start-tModLoader.sh > "$LOG" 2>&1 &

DEADLINE=$((SECONDS + TIMEOUT))
while [ $SECONDS -lt $DEADLINE ]; do
  if grep -qE '\[BossPilot\] .*: (victory|death|timeout)' "$LOG" 2>/dev/null; then
    RESULT=$(grep -oE '\[BossPilot\] .*: (victory|death|timeout)' "$LOG" | tail -1)
    echo "RESULT: $RESULT"
    pkill -9 -f 'tModLoader.dll' 2>/dev/null
    exit 0
  fi
  if grep -q 'Fatal Error' "$LOG" 2>/dev/null; then
    echo "RESULT: crash"; grep -A5 'Fatal Error' "$LOG" | head -10
    pkill -9 -f 'tModLoader.dll' 2>/dev/null
    exit 1
  fi
  sleep 5
done
echo "RESULT: harness-timeout"
pkill -9 -f 'tModLoader.dll' 2>/dev/null
exit 1
