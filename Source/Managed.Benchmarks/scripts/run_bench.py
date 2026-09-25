#!/usr/bin/env python3
"""
Allocator benchmark matrix: every build x payload size x thread count, through the managed API
(enet-bench, the path FigNet takes) and natively (pool_bench, no P/Invoke), repeated --runs times.
Builds are rotated inside every run so drift (thermals, background load) spreads across all of them.

  python run_bench.py --bench enet-bench.exe --native-bench pool_bench.exe \
      --build v2.6.1=enet-v2.6.1.dll@1 --build fixed=enet-fixed.dll --build nopool=enet-nopool.dll \
      --out results/
"""

import argparse
import os
import time

from enetbench import append_jsonl, exit_code_name, parse_build, run_json, wait_for_quiet


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--bench", required=True, help="enet-bench executable")
    parser.add_argument("--native-bench", help="pool_bench executable (optional)")
    parser.add_argument("--build", action="append", required=True, help="label=path[@max_threads]")
    parser.add_argument("--sizes", default="53,181,1024,1400")
    parser.add_argument("--threads", default="1,2,4")
    parser.add_argument("--runs", type=int, default=5)
    parser.add_argument("--seconds", type=float, default=3)
    parser.add_argument("--warmup", type=float, default=1)
    parser.add_argument("--held", type=int, default=8, help="packets each thread holds per burst")
    parser.add_argument("--cold", action="store_true", help="drain the thread's pool cache after every burst: every create is a first creation")
    parser.add_argument("--handoff", default="", help="comma-separated build labels to also run the handoff mode for")
    parser.add_argument("--handoff-threads", default="2,4")
    parser.add_argument("--only-sizes-for", default="", help="label:size1/size2,... restricts sizes for a build")
    parser.add_argument("--quiet-below", type=float, default=15, help="wait before each cell until machine CPU is below this percent (0 disables)")
    parser.add_argument("--quiet-max-wait", type=float, default=600)
    parser.add_argument("--out", required=True)
    args = parser.parse_args()

    builds = [parse_build(spec) for spec in args.build]
    sizes = [int(s) for s in args.sizes.split(",")]
    thread_counts = [int(t) for t in args.threads.split(",")]
    handoff = [label for label in args.handoff.split(",") if label]
    restricted = {}

    for item in filter(None, args.only_sizes_for.split(",")):
        label, _, values = item.partition(":")
        restricted[label] = {int(v) for v in values.split("/")}

    os.makedirs(args.out, exist_ok=True)

    managed_path = os.path.join(args.out, "bench-managed.jsonl")
    native_path = os.path.join(args.out, "bench-native.jsonl")
    timeout = args.seconds + args.warmup + 60

    total = 0
    started = time.time()

    for run in range(1, args.runs + 1):
        rotation = builds[(run - 1) % len(builds):] + builds[:(run - 1) % len(builds)]

        for size in sizes:
            for threads in thread_counts:
                for build in rotation:
                    if build["max_threads"] is not None and threads > build["max_threads"]:
                        continue

                    if build["label"] in restricted and size not in restricted[build["label"]]:
                        continue

                    ambient = wait_for_quiet(args.quiet_below, args.quiet_max_wait) if args.quiet_below > 0 else None
                    tags = {"build": build["label"], "run": run, "threadsRequested": threads, "ambientCpuPercent": ambient}

                    result, code, stderr = run_json([args.bench, "--mode", "churn", "--lib", build["path"], "--label", build["label"],
                        "--threads", str(threads), "--size", str(size), "--seconds", str(args.seconds), "--warmup", str(args.warmup),
                        "--held", str(args.held), *(["--cold"] if args.cold else [])], timeout)
                    append_jsonl(managed_path, {**tags, **(result or {"size": str(size), "threads": threads}), "exit": exit_code_name(code)})

                    if args.native_bench:
                        result, code, stderr = run_json([args.native_bench, build["path"], str(threads), str(size), str(args.seconds), str(args.held), *(["cold"] if args.cold else [])], timeout)
                        append_jsonl(native_path, {**tags, **(result or {"size": str(size), "threads": threads}), "exit": exit_code_name(code)})

                    total += 1

                    print(f"[{time.time() - started:6.0f}s] run {run} size {size:4d} threads {threads} {build['label']:<14} {exit_code_name(code)}  ambient {ambient if ambient is not None else -1:.0f}%", flush=True)

        for label in handoff:
            build = next(b for b in builds if b["label"] == label)

            for threads in [int(t) for t in args.handoff_threads.split(",")]:
                if args.quiet_below > 0:
                    wait_for_quiet(args.quiet_below, args.quiet_max_wait)

                result, code, stderr = run_json([args.bench, "--mode", "handoff", "--lib", build["path"], "--label", label,
                    "--threads", str(threads), "--size", "181", "--seconds", str(args.seconds), "--warmup", str(args.warmup)], timeout)
                append_jsonl(managed_path, {"build": label, "run": run, "threadsRequested": threads, **(result or {"mode": "handoff"}), "exit": exit_code_name(code)})

    print(f"done: {total} cells in {time.time() - started:.0f}s")


if __name__ == "__main__":
    main()
