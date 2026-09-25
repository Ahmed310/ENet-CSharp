#!/usr/bin/env python3
"""
Turns benchmark, repro and stress output into report.md and summary.json (no third-party packages).

  python make_report.py --results results/ [--linux results/linux] [--baseline v2.6.1 --candidate fixed --control nopool]
"""

import argparse
import csv
import glob
import json
import os
import statistics
from collections import defaultdict


VARIANTS = ("fixed-batch1", "fixed-batch256", "fixed-batch1024")

# Blocks a thread cache may hold (ENET_POOL_MAX_RETAINED); 2.6.x used one 576-block process-wide list
CACHE_CAP = 128


def load_jsonl(path):
    if not os.path.exists(path):
        return []

    with open(path, encoding="utf-8") as handle:
        return [json.loads(line) for line in handle if line.strip()]


def median(values):
    return statistics.median(values) if values else None


def spread(values):
    """(max - min) / median, as a percentage: how much the runs disagree."""
    if len(values) < 2:
        return 0.0

    m = statistics.median(values)

    return (max(values) - min(values)) / m * 100.0 if m else 0.0


def slope_per_minute(xs, ys):
    """Least-squares slope of ys over xs (seconds), per minute."""
    if len(xs) < 3:
        return 0.0

    mx, my = statistics.fmean(xs), statistics.fmean(ys)
    denominator = sum((x - mx) ** 2 for x in xs)

    return sum((x - mx) * (y - my) for x, y in zip(xs, ys)) / denominator * 60.0 if denominator else 0.0


def summarize_bench(records):
    """Groups runs by (mode, build, size, threads) and reduces them to medians."""
    groups = defaultdict(list)

    for record in records:
        if record.get("exit") != "ok" or "pairsPerSecond" not in record:
            groups[(record.get("mode", "churn"), record["build"], str(record.get("size")), int(record.get("threadsRequested", record.get("threads", 0))))].append(None)
            continue

        groups[(record.get("mode", "churn"), record["build"], str(record["size"]), int(record["threads"]))].append(record)

    cells = []

    for (mode, build, size, threads), runs in sorted(groups.items(), key=lambda item: (item[0][0], int(item[0][2].split("-")[0]), item[0][3], item[0][1])):
        ok = [run for run in runs if run is not None]
        rates = [run["pairsPerSecond"] for run in ok]
        cell = {
            "mode": mode, "build": build, "size": size, "threads": threads, "runs": len(runs), "failedRuns": len(runs) - len(ok),
            "pairsPerSecond": median(rates), "pairsPerSecondMin": min(rates) if rates else None, "pairsPerSecondMax": max(rates) if rates else None,
            "spreadPercent": spread(rates),
            "nsPerPair": median([run["nsPerPair"] for run in ok if "nsPerPair" in run]),
        }

        bursts = [run["burst"] for run in ok if run.get("burst")]

        if bursts:
            cell["burstP50Ns"] = median([b["p50Ns"] for b in bursts])
            cell["burstP99Ns"] = median([b["p99Ns"] for b in bursts])
            cell["burstP999Ns"] = median([b["p999Ns"] for b in bursts])
            cell["timerResolutionNs"] = bursts[0].get("timerResolutionNs")

        pools = [run["pool"] for run in ok if run.get("pool")]

        if pools:
            cell["hitRate"] = median([p["hitRate"] for p in pools])

        ambient = [run["ambientCpuPercent"] for run in ok if run.get("ambientCpuPercent") is not None]

        if ambient:
            cell["ambientCpuPercent"] = median(ambient)
            cell["ambientCpuPercentMax"] = max(ambient)

        working_sets = [run["peakWorkingSetMB"] for run in ok if "peakWorkingSetMB" in run]

        if working_sets:
            cell["peakWorkingSetMB"] = median(working_sets)

        cells.append(cell)

    return cells


def find(cells, mode, build, size, threads):
    return next((c for c in cells if c["mode"] == mode and c["build"] == build and c["size"] == str(size) and c["threads"] == threads), None)


def compare(cells, mode, a, b):
    """Speedup of build a over build b for every (size, threads) both have."""
    rows = []

    for cell in cells:
        if cell["mode"] != mode or cell["build"] != a or not cell["pairsPerSecond"]:
            continue

        other = find(cells, mode, b, cell["size"], cell["threads"])

        if other and other["pairsPerSecond"]:
            rows.append({"size": cell["size"], "threads": cell["threads"], a: cell["pairsPerSecond"], b: other["pairsPerSecond"], "speedup": cell["pairsPerSecond"] / other["pairsPerSecond"]})

    return rows


def summarize_repro(records):
    groups = defaultdict(list)

    for record in records:
        groups[(record["build"], record["threads"])].append(record)

    rows = []

    for (build, threads), runs in sorted(groups.items()):
        crashes = [r for r in runs if not r["survived"]]
        rows.append({
            "build": build, "threads": threads, "runs": len(runs), "survived": len(runs) - len(crashes), "targetSeconds": runs[0]["targetSeconds"],
            "exits": sorted({r["exit"] for r in crashes}),
            "medianSecondsToCrash": median([r["livedSeconds"] for r in crashes]),
            "secondsToCrash": [round(r["livedSeconds"], 1) for r in crashes],
            "frames": sorted({r["frame"] for r in crashes if r["frame"]}),
        })

    return rows


def read_csv(path):
    with open(path, encoding="utf-8") as handle:
        return [row for row in csv.DictReader(handle)]


def summarize_stress(directory):
    runs = []

    for run_path in sorted(glob.glob(os.path.join(directory, "*", "run.json"))):
        with open(run_path, encoding="utf-8") as handle:
            run = json.load(handle)

        folder = os.path.dirname(run_path)
        processes = []

        for process in run["processes"]:
            name = "server.csv" if process["role"] == "server" else f"client-{process['instance']}.csv"
            path = os.path.join(folder, name)
            rows = read_csv(path) if os.path.exists(path) else []
            numeric = lambda key: [float(row[key]) for row in rows if row.get(key) not in (None, "")]
            elapsed = numeric("elapsed_s")
            working_set = numeric("working_set_mb")
            steady = [(t, w) for t, w in zip(elapsed, working_set) if t >= 60]
            hits, misses = numeric("pool_hits"), numeric("pool_misses")
            sent = [a + b for a, b in zip(numeric("state_sent"), numeric("voice_sent"))]
            received = [a + b for a, b in zip(numeric("state_received"), numeric("voice_received"))]
            rate = lambda series: [(series[i] - series[i - 1]) / max(1e-9, elapsed[i] - elapsed[i - 1]) for i in range(1, len(series))]
            sent_rate, received_rate = rate(sent), rate(received)
            last = rows[-1] if rows else {}
            summary = process.get("summary") or {}
            reconnects = float(last.get("reconnects", 0) or 0)
            disconnects = float(last.get("disconnects", 0) or 0)
            timeouts = float(last.get("timeouts", 0) or 0)
            corrupt = float(last.get("state_corrupt", 0) or 0) + float(last.get("voice_corrupt", 0) or 0)

            # A client's planned voice reconnects each account for one disconnect; the server sees one per client session
            unplanned = timeouts + (max(0.0, disconnects - reconnects) if process["role"] == "client" else 0.0)

            processes.append({
                "role": process["role"], "instance": process["instance"], "exit": process["exit"], "ok": process["ok"],
                "livedSeconds": elapsed[-1] if elapsed else 0.0, "rows": len(rows),
                "corrupt": corrupt, "timeouts": timeouts, "disconnects": disconnects, "reconnects": reconnects, "unplannedDrops": unplanned,
                "sentPerSecondMean": statistics.fmean(sent_rate) if sent_rate else 0.0,
                "receivedPerSecondMean": statistics.fmean(received_rate) if received_rate else 0.0,
                "receivedPerSecondP99": sorted(received_rate)[int(0.99 * (len(received_rate) - 1))] if received_rate else 0.0,
                "poolHitRate": hits[-1] / (hits[-1] + misses[-1]) if hits and hits[-1] + misses[-1] > 0 else None,
                "poolRetainedMax": max(numeric("pool_retained"), default=0), "poolCachesMax": max(numeric("pool_caches"), default=0),
                "poolOrphanedMax": max(numeric("pool_orphaned"), default=0),
                "cpuPercentMean": statistics.fmean(numeric("cpu_pct")[1:]) if len(rows) > 1 else 0.0,
                "workingSetStartMB": working_set[0] if working_set else None, "workingSetEndMB": working_set[-1] if working_set else None,
                "workingSetMaxMB": max(working_set, default=None),
                "workingSetSlopeMBPerMinute": slope_per_minute([t for t, _ in steady], [w for _, w in steady]),
                "stderrTail": process.get("stderrTail", [])[-4:],
            })

        crashed = [p for p in processes if not p["ok"]]
        verdict = {
            "noCrash": not crashed,
            "noCorruption": all(p["corrupt"] == 0 for p in processes),
            "noUnplannedDisconnect": all(p["unplannedDrops"] == 0 for p in processes),
            # The pool's own memory: every thread cache stays under its cap and parked blocks under one cache's worth.
            # (Working set is reported but not judged: in a 10-minute window it is dominated by the .NET GC heap.)
            "poolMemoryBounded": all(p["poolRetainedMax"] <= CACHE_CAP * max(1, p["poolCachesMax"]) and p["poolOrphanedMax"] <= CACHE_CAP * max(1, p["poolCachesMax"]) for p in processes),
        }
        verdict["pass"] = all(verdict.values())

        runs.append({"build": run["build"], "clients": run["clients"], "seconds": run["seconds"], "scale": run["scale"],
            "reconnectClients": run["reconnectClients"], "reconnectEvery": run["reconnectEvery"],
            "firstCrashSeconds": min((p["livedSeconds"] for p in crashed), default=None), "verdict": verdict, "processes": processes})

    return runs


def fmt_rate(value):
    return "-" if value is None else f"{value / 1e6:.2f} M/s"


def markdown(summary, baseline, candidate, control):
    lines = ["# ENet-CSharp-FigNet 2.7.0: pool thread-safety validation", ""]
    environment = summary.get("environment", {})

    if environment:
        lines += [f"Machine: {environment.get('os')}, {environment.get('processors')} logical processors.", ""]

    repro = summary.get("repro", [])

    if repro:
        lines += ["## Crash repro (bragvr-gdd#351 loop: threads x 8 held packets of 1-180 B)", "",
            "| Build | Threads | Survived / runs | Target | Crash exits | Median time to crash |", "|---|---|---|---|---|---|"]

        for row in repro:
            crash = "-" if row["medianSecondsToCrash"] is None else "< 0.1 s" if row["medianSecondsToCrash"] < 0.1 else f"{row['medianSecondsToCrash']:.1f} s"
            lines.append(f"| {row['build']} | {row['threads']} | {row['survived']} / {row['runs']} | {row['targetSeconds']:.0f} s | {', '.join(row['exits']) or '-'} | {crash} |")

        lines.append("")

    for kind in ("managed", "native", "linux"):
        cells = summary.get(f"bench_{kind}", [])

        if not cells:
            continue

        title = {"managed": "Managed API throughput (Windows, .NET 10, what FigNet calls)",
            "native": "Native throughput (Windows, MSVC, no P/Invoke)", "linux": "Native throughput (Linux container, glibc)"}[kind]
        lines += [f"## {title}", "", "Median create+destroy pairs per second over all runs (spread = (max - min) / median).", "",
            "| Size (B) | Threads | Build | Median | ns / pair | Spread | Burst p50 / p99 / p99.9 (ns, 16 ops) | Hit rate |", "|---|---|---|---|---|---|---|---|"]

        for cell in cells:
            if cell["mode"] not in ("churn", "native-churn"):
                continue

            burst = f"{cell['burstP50Ns']:.0f} / {cell['burstP99Ns']:.0f} / {cell['burstP999Ns']:.0f}" if "burstP50Ns" in cell else "-"
            hit = f"{cell['hitRate'] * 100:.2f}%" if cell.get("hitRate") is not None else "-"
            failed = f" ({cell['failedRuns']} failed)" if cell["failedRuns"] else ""
            ns = f"{cell['nsPerPair']:.1f}" if cell.get("nsPerPair") else "-"
            lines.append(f"| {cell['size']} | {cell['threads']} | {cell['build']}{failed} | {fmt_rate(cell['pairsPerSecond'])} | {ns} | {cell['spreadPercent']:.1f}% | {burst} | {hit} |")

        lines.append("")

        for other in (control, baseline, *VARIANTS):
            rows = summary.get(f"speedup_{kind}_{candidate}_vs_{other}", [])

            if rows:
                lines += [f"**{candidate} vs {other}** (x = {candidate} / {other}): " + ", ".join(f"{r['size']} B x{r['threads']}t: **{r['speedup']:.2f}x**" for r in rows), ""]

        handoff = [c for c in cells if c["mode"] == "handoff"]

        if handoff:
            lines += ["Cross-thread handoff (producers create, consumers destroy, 181 B): " +
                ", ".join(f"{c['build']} {c['threads']}t {fmt_rate(c['pairsPerSecond'])}" for c in handoff), ""]

    rule = summary.get("decisionRule")

    if rule:
        lines += ["## Decision rule from the issue", "", f"Keep the pool only if {candidate} beats {control} at 2 threads for 53 B and 181 B payloads.", ""]

        for row in rule["rows"]:
            lines.append(f"- {row['kind']} {row['size']} B, 2 threads: {candidate} {fmt_rate(row['candidate'])} vs {control} {fmt_rate(row['control'])} = **{row['speedup']:.2f}x** {'PASS' if row['pass'] else 'FAIL'}")

        lines += ["", f"**Verdict: {'keep the pool' if rule['pass'] else 'use ENET_NO_POOL'}**", ""]

    for run in summary.get("stress", []):
        verdict = run["verdict"]
        lines += [f"## Stress: {run['build']} ({run['clients']} clients + server, {run['seconds']:.0f} s, rate x{run['scale']:g}, "
            f"{run['reconnectClients']} clients reconnecting voice every {run['reconnectEvery']:g} s)", "",
            f"**{'PASS' if verdict['pass'] else 'FAIL'}**: " + ", ".join(f"{k} {'yes' if v else 'NO'}" for k, v in verdict.items() if k != "pass") +
            (f"; first crash after {run['firstCrashSeconds']:.1f} s" if run["firstCrashSeconds"] is not None else ""), "",
            "| Process | Exit | Lived | Recv/s mean (p99) | Sent/s | Corrupt | Unplanned drops | Reconnects | Pool hit | Retained max | CPU mean | WS start > end (max) | WS slope |",
            "|---|---|---|---|---|---|---|---|---|---|---|---|---|"]

        for p in run["processes"]:
            hit = f"{p['poolHitRate'] * 100:.2f}%" if p["poolHitRate"] is not None else "-"
            ws = f"{p['workingSetStartMB']:.1f} > {p['workingSetEndMB']:.1f} ({p['workingSetMaxMB']:.1f})" if p["workingSetStartMB"] is not None else "-"
            lines.append(f"| {p['role']} {p['instance']} | {p['exit']} | {p['livedSeconds']:.0f} s | {p['receivedPerSecondMean']:.0f} ({p['receivedPerSecondP99']:.0f}) | "
                f"{p['sentPerSecondMean']:.0f} | {p['corrupt']:.0f} | {p['unplannedDrops']:.0f} | {p['reconnects']:.0f} | {hit} | {p['poolRetainedMax']:.0f} | "
                f"{p['cpuPercentMean']:.1f}% | {ws} | {p['workingSetSlopeMBPerMinute']:+.3f} MB/min |")

        lines.append("")

    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--results", required=True)
    parser.add_argument("--linux", help="directory holding native-bench-linux.jsonl")
    parser.add_argument("--baseline", default="v2.6.1")
    parser.add_argument("--candidate", default="fixed")
    parser.add_argument("--control", default="nopool")
    args = parser.parse_args()

    managed = load_jsonl(os.path.join(args.results, "bench-managed.jsonl"))
    native = load_jsonl(os.path.join(args.results, "bench-native.jsonl"))
    linux = load_jsonl(os.path.join(args.linux, "native-bench-linux.jsonl")) if args.linux else []

    for record in linux:
        record.setdefault("threadsRequested", record.get("threads"))
        record.setdefault("exit", "ok")

    summary = {"environment": next(({"os": r.get("os"), "processors": r.get("processors")} for r in managed if r.get("os")), {})}

    for kind, records in (("managed", managed), ("native", native), ("linux", linux)):
        cells = summarize_bench(records)
        summary[f"bench_{kind}"] = cells

        for other in (args.control, args.baseline, *VARIANTS):
            mode = "churn" if kind == "managed" else "native-churn"
            summary[f"speedup_{kind}_{args.candidate}_vs_{other}"] = compare(cells, mode, args.candidate, other)

    rule_rows = []

    for kind, mode in (("managed", "churn"), ("native", "native-churn"), ("linux", "native-churn")):
        for size in (53, 181):
            candidate = find(summary[f"bench_{kind}"], mode, args.candidate, size, 2)
            control = find(summary[f"bench_{kind}"], mode, args.control, size, 2)

            if candidate and control and candidate["pairsPerSecond"] and control["pairsPerSecond"]:
                speedup = candidate["pairsPerSecond"] / control["pairsPerSecond"]
                rule_rows.append({"kind": kind, "size": size, "candidate": candidate["pairsPerSecond"], "control": control["pairsPerSecond"], "speedup": speedup, "pass": speedup > 1.0})

    if rule_rows:
        summary["decisionRule"] = {"rows": rule_rows, "pass": all(row["pass"] for row in rule_rows)}

    summary["repro"] = summarize_repro(load_jsonl(os.path.join(args.results, "repro.jsonl")))
    summary["stress"] = summarize_stress(os.path.join(args.results, "stress"))

    with open(os.path.join(args.results, "summary.json"), "w", encoding="utf-8") as handle:
        json.dump(summary, handle, indent=2)

    report = markdown(summary, args.baseline, args.candidate, args.control)

    with open(os.path.join(args.results, "report.md"), "w", encoding="utf-8") as handle:
        handle.write(report)

    print(report)


if __name__ == "__main__":
    main()
