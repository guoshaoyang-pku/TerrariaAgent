#!/usr/bin/env python3
"""Run exploration tasks headless and in parallel on tModLoader dedicated servers.

Same harness as run_trials.py (one `-server -nosteam` process per worker, own save dir, world copy,
free port; BossPilot's HeadlessSim drives the game loop), but the job is {"kind": "explore", ...}:
ExploreRunner navigates the natural world to landmarks and gathers resources. Results land in
.runs/<run>/w<k>/out/results.jsonl (one row per task attempt), traces in trace-*.jsonl, the
world summary in world_info.json, and a summary table at the end.

Usage:
    python3 scripts/run_explore.py --tasks ocean_left dungeon copper chests3 --attempts 2
    python3 scripts/run_explore.py --all --parallel 2 --spawns
    python3 scripts/run_explore.py --job my_job.json
    python3 scripts/run_explore.py --list
"""

import argparse
import json
import shutil
import subprocess
import sys
import time
from collections import defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import run_trials as rt  # noqa: E402  (build, world template, server command, env)

CATALOG = Path(__file__).resolve().parent / "explore_tasks.json"
MAX_PARALLEL = 2  # shared machine


def load_catalog():
    return json.loads(CATALOG.read_text(encoding="utf-8"))


def ensure_world(tmod, seed):
    """Template world for a seed: the boss harness's arena.wld for its seed, else a new small world."""
    if seed == 20261028:
        return rt.ensure_world(tmod, seed)
    d = rt.RUNS / f"_world_{seed}"
    wld = d / "Worlds" / "explore.wld"
    if wld.exists():
        return wld
    rt.stage_save_dir(d, tmod)
    job = d / "empty_job.json"
    job.write_text('{"scenarios": []}\n')
    print(f"generating world (seed {seed}) ...", flush=True)
    with open(d / "server.log", "w") as log:
        subprocess.run(rt.server_cmd(d, ["-autocreate", "1", "-worldname", "explore", "-seed", str(seed), "-world", str(wld)]),
                       cwd=rt.TML, env=rt.env(BOSSPILOT_JOB=job, BOSSPILOT_OUT=d / "out"),
                       stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT, timeout=900)
    if not wld.exists():
        sys.exit(f"world generation failed, see {d / 'server.log'}")
    return wld


def launch(k, run_dir, job, tmod, wld, retry=0):
    d = run_dir / f"w{k}"
    if retry == 0:
        rt.stage_save_dir(d, tmod)
        (d / "Worlds").mkdir(exist_ok=True)
        for ext in (".wld", ".twld"):
            if wld.with_suffix(ext).exists():
                shutil.copy2(wld.with_suffix(ext), d / "Worlds" / ("world" + ext))
    jf = d / "job.json"
    jf.write_text(json.dumps(job, ensure_ascii=False, indent=1))
    log = open(d / ("server.log" if retry == 0 else f"server.retry{retry}.log"), "w")
    p = subprocess.Popen(rt.server_cmd(d, ["-world", str(d / "Worlds" / "world.wld")]),
                         cwd=rt.TML, env=rt.env(BOSSPILOT_JOB=jf, BOSSPILOT_OUT=d / "out"),
                         stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT)
    return p, log, d


def summarise(run_dir):
    rows = []
    for f in sorted(run_dir.glob("w*/out/results.jsonl")):
        rows += [json.loads(line) for line in f.read_text().splitlines() if line.strip()]
    by = defaultdict(list)
    for r in rows:
        by[r["task"]].append(r)
    print(f"\n{'task':22} {'ok':>5} {'mean_s':>7} {'best_s':>7} {'dmg':>5} {'dug':>5} {'blk':>4} {'replan':>6} {'x':>5}  items / result")
    summary = {}
    for name, rs in by.items():
        ok = [r for r in rs if r["success"]]
        secs = [r["seconds"] for r in ok]
        items = defaultdict(int)
        for r in ok:
            for k, v in r["items_gained"].items():
                items[k] += v
        mean_items = {k: round(v / len(ok), 1) for k, v in sorted(items.items(), key=lambda kv: -kv[1])} if ok else {}
        s = summary[name] = {
            "attempts": len(rs), "successes": len(ok),
            "mean_seconds": round(sum(secs) / len(secs), 1) if secs else None,
            "best_seconds": min(secs) if secs else None,
            "mean_damage": round(sum(r["damage_taken"] for r in rs) / len(rs), 1),
            "mean_tiles_dug": round(sum(r["tiles_dug"] for r in rs) / len(rs), 1),
            "mean_blocks": round(sum(r["blocks_placed"] for r in rs) / len(rs), 1),
            "mean_replans": round(sum(r["replans"] for r in rs) / len(rs), 1),
            "mean_speedup": round(sum(r["speedup"] for r in rs) / len(rs), 1),
            "mean_items_on_success": mean_items,
            "results": [r["result"] for r in rs],
            "spawns": rs[0]["spawns"],
        }
        top = ", ".join(f"{k} {v}" for k, v in list(mean_items.items())[:3])
        fails = sorted({r for r in s["results"] if r != "success"})
        print(f"{name:22} {s['successes']:>2}/{s['attempts']:<2} {s['mean_seconds'] if secs else '-':>7} "
              f"{s['best_seconds'] if secs else '-':>7} {s['mean_damage']:>5} {s['mean_tiles_dug']:>5} {s['mean_blocks']:>4} "
              f"{s['mean_replans']:>6} {s['mean_speedup']:>5}  {top}{'  | ' + '; '.join(fails) if fails else ''}")
    (run_dir / "summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=1))
    exc = {}
    for f in sorted(run_dir.glob("w*/out/exception_counts.json")):
        for k, v in json.loads(f.read_text()).items():
            exc[k] = exc.get(k, 0) + v
    if exc:
        print("\nexceptions:", json.dumps(exc, indent=1))
    return summary


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--tasks", nargs="*", default=[], help="task names from scripts/explore_tasks.json")
    ap.add_argument("--all", action="store_true", help="every task in the catalog")
    ap.add_argument("--job", help="explicit explore job JSON (tasks, kit, spawns...)")
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--attempts", type=int, default=1)
    ap.add_argument("--parallel", type=int, default=MAX_PARALLEL)
    ap.add_argument("--spawns", action="store_true", help="enable natural hostile spawns")
    ap.add_argument("--chain", action="store_true", help="run tasks in order on one server, each continuing from the last")
    ap.add_argument("--timeout", type=int, default=5400, help="wall seconds per worker")
    ap.add_argument("--run", default="explore-" + time.strftime("%Y%m%d-%H%M%S"))
    ap.add_argument("--seed", type=int, default=20261028)
    ap.add_argument("--no-build", action="store_true")
    a = ap.parse_args()

    cat = load_catalog()
    if a.list:
        for name, t in cat["tasks"].items():
            print(f"{name:22} {json.dumps(t, ensure_ascii=False)}")
        return

    if a.job:
        base = json.loads(Path(a.job).read_text(encoding="utf-8"))
        tasks = base["tasks"]
    else:
        names = list(cat["tasks"]) if a.all else a.tasks
        unknown = [n for n in names if n not in cat["tasks"]]
        if unknown or not names:
            sys.exit(f"unknown or no tasks: {unknown} (see --list)")
        tasks = [{"name": n, **cat["tasks"][n]} for n in names]
        base = {"kit": cat.get("kit", {})}
    if a.chain:
        tasks = [dict(t, start="spawn" if i == 0 else "continue") for i, t in enumerate(tasks)]
    base = {**base, "kind": "explore", "attempts": a.attempts, "spawns": a.spawns or base.get("spawns", False)}

    tmod = rt.BUILD / "Mods" / "BossPilot.tmod" if a.no_build else rt.build_mod()
    wld = ensure_world(tmod, a.seed)
    run_dir = rt.RUNS / a.run
    run_dir.mkdir(parents=True, exist_ok=True)

    n = 1 if a.chain else max(1, min(a.parallel, MAX_PARALLEL, len(tasks)))
    chunks = [tasks] if a.chain else [tasks[k::n] for k in range(n)]
    # Attempts are flattened into the task list (labelled), so a killed server can resume where its rows stop.
    plans = [[dict(t, attempt=p) for p in range(a.attempts) for t in chunk] for chunk in chunks]
    base["attempts"] = 1
    print(f"run {run_dir.name}: {len(tasks)} tasks x {a.attempts} attempts on {n} servers "
          f"(seed {a.seed}, spawns {'on' if base['spawns'] else 'off'})", flush=True)
    workers = {}
    for k, plan in enumerate(plans):
        workers[k] = (*launch(k, run_dir, {**base, "tasks": plan}, tmod, wld), 0)
        time.sleep(3)  # stagger startup so two servers never race for the same log file name

    deadline = time.time() + a.timeout
    while any(w[3] < 99 for w in workers.values()):
        if time.time() > deadline:
            for p, _, d, _ in workers.values():
                if p.poll() is None:
                    p.kill()
                    print(f"timeout: killed {d.name}")
            break
        time.sleep(2)
        rt.check_mod_loaded([(p, log, d) for p, log, d, _ in workers.values()])
        for k, (p, log, d, retry) in list(workers.items()):
            if p.poll() is None or retry >= 99:
                continue
            # Killed, crashed, or never started (free-port race: "Address already in use" exits with 0): resume.
            res = d / "out" / "results.jsonl"
            done = len([l for l in res.read_text().splitlines() if l.strip()]) if res.exists() else 0
            rest = plans[k][done:]
            if not rest:
                workers[k] = (p, log, d, 99)
                log.close()
                continue
            log.close()
            if retry >= 4:
                print(f"{d.name} exited {p.returncode} after {done}/{len(plans[k])} rows, see {d / 'server.log'}")
                workers[k] = (p, log, d, 99)
                continue
            print(f"{d.name} exited {p.returncode} after {done}/{len(plans[k])} rows; resuming (retry {retry + 1})", flush=True)
            time.sleep(5 + 5 * retry)
            np_, nlog, _ = launch(k, run_dir, {**base, "tasks": rest}, tmod, wld, retry + 1)
            workers[k] = (np_, nlog, d, retry + 1)
    for p, log, d, _ in workers.values():
        if not log.closed:
            log.close()
    summarise(run_dir)
    print(f"\nresults: {run_dir}")


if __name__ == "__main__":
    main()
