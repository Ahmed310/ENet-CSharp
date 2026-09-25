#!/usr/bin/env python3
"""
The bragvr-gdd#351 crash repro, timed: N threads each hold 8 packets of 1-180 bytes and destroy them,
for up to --seconds, --runs times per build. Records the exit code and how long the process lived
(from the harness's 100 ms heartbeat). A build passes when every run survives the full duration.

  python run_repro.py --bench enet-bench.exe --build v2.6.1=enet-v2.6.1.dll --build fixed=enet-fixed.dll --out results/
"""

import argparse
import os
import subprocess
import time

from enetbench import append_jsonl, exit_code_name, parse_build


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--bench", required=True)
    parser.add_argument("--build", action="append", required=True, help="label=path")
    parser.add_argument("--threads", default="2")
    parser.add_argument("--runs", type=int, default=5)
    parser.add_argument("--seconds", type=float, default=60)
    parser.add_argument("--out", required=True)
    args = parser.parse_args()

    os.makedirs(args.out, exist_ok=True)
    path = os.path.join(args.out, "repro.jsonl")

    for build in [parse_build(spec) for spec in args.build]:
        for threads in [int(t) for t in args.threads.split(",")]:
            for run in range(1, args.runs + 1):
                started = time.time()
                command = [args.bench, "--mode", "churn", "--lib", build["path"], "--label", build["label"], "--threads", str(threads),
                    "--size-min", "1", "--size-max", "180", "--seconds", str(args.seconds), "--warmup", "0", "--heartbeat", "--verify"]

                try:
                    completed = subprocess.run(command, capture_output=True, text=True, timeout=args.seconds + 120)
                    code, stderr = completed.returncode, completed.stderr
                except subprocess.TimeoutExpired:
                    code, stderr = None, ""

                wall = time.time() - started
                alive = [float(line.split()[1]) for line in stderr.splitlines() if line.startswith("alive ")]
                fault = next((line.strip() for line in stderr.splitlines() if "0xC0000" in line or "Fatal error" in line or "corrupt" in line.lower()), "")
                frame = next((line.strip() for line in stderr.splitlines() if line.strip().startswith("at ENet.Native.")), "")

                record = {
                    "build": build["label"], "threads": threads, "run": run, "exit": exit_code_name(code),
                    "survived": code == 0, "livedSeconds": alive[-1] if alive else 0.0, "wallSeconds": wall,
                    "targetSeconds": args.seconds, "fault": fault, "frame": frame,
                }

                append_jsonl(path, record)

                print(f"{build['label']:<10} threads {threads} run {run}: {record['exit']:<32} lived {record['livedSeconds']:6.1f}s  {frame}", flush=True)


if __name__ == "__main__":
    main()
