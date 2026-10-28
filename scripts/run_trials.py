#!/usr/bin/env python3
"""Run boss trials headless and in parallel on tModLoader dedicated servers.

Each worker is one `tModLoader.dll -server -nosteam` process with its own save
directory, world copy and job file; BossPilot's HeadlessSim drives the game
loop without a window and without the 60 Hz cap. Results land in
.runs/<run>/w<k>/out/results.jsonl and are summarised at the end.

Usage:
    python3 scripts/run_trials.py --boss king_slime --attempts 5
    python3 scripts/run_trials.py --scenario ks_flat_day eoc_flat_night --parallel 2
    python3 scripts/run_trials.py --all --parallel 12
"""

import argparse
import json
import os
import shutil
import socket
import subprocess
import sys
import time
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
RUNS = ROOT / ".runs"
TML = Path.home() / "Library/Application Support/Steam/steamapps/common/tModLoader"
DOTNET_ROOT = Path.home() / ".dotnet"  # tModLoader stable needs .NET 8; Homebrew dotnet is .NET 10 only
DOTNET = DOTNET_ROOT / "dotnet"
BUILD = RUNS / "_build"
WORLD = RUNS / "_world"


def env(**extra):
    e = dict(os.environ, DOTNET_ROOT=str(DOTNET_ROOT), DOTNET_CLI_TELEMETRY_OPTOUT="1")
    e["PATH"] = f"{DOTNET_ROOT}:{e['PATH']}"
    e.update({k: str(v) for k, v in extra.items()})
    return e


def load_scenarios():
    data = json.loads((ROOT / "configs" / "boss_matrix.json").read_text(encoding="utf-8"))
    out = []
    for boss, spec in data["bosses"].items():
        for s in spec["scenarios"]:
            merged = {**data["defaults"], "boss": boss,
                      "max_duration_seconds": spec.get("max_duration_seconds", 300), **s}
            if merged.pop("enabled", True):
                out.append(merged)
    return out


def build_mod():
    """Build BossPilot into .runs/_build (never the user's tModLoader Mods folder)."""
    BUILD.mkdir(parents=True, exist_ok=True)
    cmd = [str(DOTNET), "build", str(ROOT / "mod" / "BossPilot"), "-c", "Release", "-v", "q", "-nologo",
           f"-p:tMLTargets={TML / 'tMLMod.targets'}",
           f"-p:ExtraBuildModFlags=-tmlsavedirectory {BUILD}"]
    r = subprocess.run(cmd, env=env(), capture_output=True, text=True)
    tmod = BUILD / "Mods" / "BossPilot.tmod"
    if r.returncode != 0 or not tmod.exists():
        sys.exit(f"build failed:\n{r.stdout[-4000:]}\n{r.stderr[-2000:]}")
    return tmod


def free_port():
    # Several runners (one per subagent worktree) share this machine; never hard-code ports.
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def server_cmd(save_dir, world_args):
    return [str(DOTNET), str(TML / "tModLoader.dll"), "-server", "-nosteam",
            "-tmlsavedirectory", str(save_dir), "-port", str(free_port()), "-players", "1", *world_args]


def stage_save_dir(d, tmod):
    (d / "Mods").mkdir(parents=True, exist_ok=True)
    shutil.copy2(tmod, d / "Mods" / "BossPilot.tmod")
    (d / "Mods" / "enabled.json").write_text('["BossPilot"]\n')


def ensure_world(tmod, seed):
    """Generate the small template world once (an empty job makes the server exit after worldgen)."""
    wld = WORLD / "Worlds" / "arena.wld"
    if wld.exists():
        return wld
    stage_save_dir(WORLD, tmod)
    job = WORLD / "empty_job.json"
    job.write_text('{"scenarios": []}\n')
    print(f"generating template world (seed {seed}) ...", flush=True)
    with open(WORLD / "server.log", "w") as log:
        subprocess.run(server_cmd(WORLD, ["-autocreate", "1", "-worldname", "arena", "-seed", str(seed),
                                                 "-world", str(wld)]),
                       cwd=TML, env=env(BOSSPILOT_JOB=job, BOSSPILOT_OUT=WORLD / "out"),
                       stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT, timeout=900)
    if not wld.exists():
        sys.exit(f"world generation failed, see {WORLD / 'server.log'}")
    return wld


def launch(k, run_dir, scenarios, attempts, tmod, wld):
    d = run_dir / f"w{k}"
    stage_save_dir(d, tmod)
    (d / "Worlds").mkdir(exist_ok=True)
    for ext in (".wld", ".twld"):
        if wld.with_suffix(ext).exists():
            shutil.copy2(wld.with_suffix(ext), d / "Worlds" / ("arena" + ext))
    job = d / "job.json"
    job.write_text(json.dumps({"scenarios": scenarios, "attempts": attempts}, ensure_ascii=False, indent=1))
    log = open(d / "server.log", "w")
    # tML resolves its Libraries relative to cwd, so all servers share TML/tModLoader-Logs
    # (it picks server2.log, server3.log... for logs held open by another server).
    p = subprocess.Popen(server_cmd(d, ["-world", str(d / "Worlds" / "arena.wld")]),
                         cwd=TML, env=env(BOSSPILOT_JOB=job, BOSSPILOT_OUT=d / "out"),
                         stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT)
    return p, log, d


def port_taken(d):
    results = d / "out" / "results.jsonl"
    if results.exists() and results.stat().st_size:
        return False
    return "Address already in use" in (d / "server.log").read_text(errors="replace")


def summarise(run_dir):
    rows = []
    for f in sorted(run_dir.glob("w*/out/results.jsonl")):
        rows += [json.loads(line) for line in f.read_text().splitlines() if line.strip()]
    by = defaultdict(list)
    for r in rows:
        by[r["scenario"]].append(r)
    print(f"\n{'scenario':28} {'att':>3} {'win':>3} {'nohit':>5} {'best_s':>7} {'min_hits':>8} {'hp_left':>7}  plan")
    summary = {}
    for name, rs in sorted(by.items()):
        wins = [r for r in rs if r["result"] == "victory"]
        nohit = [r for r in wins if r["hits"] == 0]
        best = min((r["seconds"] for r in wins), default=None)
        summary[name] = {"attempts": len(rs), "wins": len(wins), "nohit_wins": len(nohit), "best_seconds": best,
                         "results": [r["result"] for r in rs], "min_hits": min(r["hits"] for r in rs),
                         "min_hp_left": min(r["boss_hp_frac"] for r in rs), "plan": rs[0]["plan"]}
        s = summary[name]
        print(f"{name:28} {s['attempts']:>3} {s['wins']:>3} {s['nohit_wins']:>5} "
              f"{best if best is not None else '-':>7} {s['min_hits']:>8} {s['min_hp_left']:>7.2f}  {s['plan']}")
    (run_dir / "summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=1))
    return summary


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--boss", nargs="*", default=[])
    ap.add_argument("--scenario", nargs="*", default=[])
    ap.add_argument("--all", action="store_true")
    ap.add_argument("--attempts", type=int, default=3)
    ap.add_argument("--parallel", type=int, default=max(1, (os.cpu_count() or 4) - 4))
    ap.add_argument("--timeout", type=int, default=3600, help="seconds per worker")
    ap.add_argument("--run", default=time.strftime("%Y%m%d-%H%M%S"))
    ap.add_argument("--seed", type=int, default=20261028)
    ap.add_argument("--no-build", action="store_true")
    a = ap.parse_args()

    scenarios = [s for s in load_scenarios()
                 if a.all or s["boss"] in a.boss or s["name"] in a.scenario]
    if not scenarios:
        sys.exit("no scenarios selected (use --boss, --scenario or --all)")

    tmod = BUILD / "Mods" / "BossPilot.tmod" if a.no_build else build_mod()
    wld = ensure_world(tmod, a.seed)
    run_dir = RUNS / a.run
    run_dir.mkdir(parents=True, exist_ok=True)

    n = min(a.parallel, len(scenarios))
    chunks = [scenarios[k::n] for k in range(n)]
    print(f"run {run_dir.name}: {len(scenarios)} scenarios x {a.attempts} attempts on {n} servers", flush=True)
    workers = []
    for k, chunk in enumerate(chunks):
        workers.append(launch(k, run_dir, chunk, a.attempts, tmod, wld))
        time.sleep(3)  # stagger startup so two servers never race for the same log file name

    deadline = time.time() + a.timeout
    retries = [0] * n
    while True:
        for k, (p, log, d) in enumerate(workers):
            # free_port() releases its probe socket before the server binds it, so another runner on
            # this machine can take the port first; the server then exits before running any attempt.
            if p.poll() is not None and retries[k] < 3 and port_taken(d):
                log.close()
                retries[k] += 1
                print(f"{d.name}: port taken, relaunching ({retries[k]}/3)", flush=True)
                workers[k] = launch(k, run_dir, chunks[k], a.attempts, tmod, wld)
        if all(p.poll() is not None for p, _, _ in workers):
            break
        if time.time() > deadline:
            for p, _, d in workers:
                if p.poll() is None:
                    p.kill()
                    print(f"timeout: killed {d.name}")
            break
        time.sleep(2)
    for p, log, d in workers:
        log.close()
        if p.returncode not in (0, None):
            print(f"{d.name} exited {p.returncode}, see {d / 'server.log'}")
    summarise(run_dir)
    print(f"\nresults: {run_dir}")


if __name__ == "__main__":
    main()
