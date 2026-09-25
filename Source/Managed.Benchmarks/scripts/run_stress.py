#!/usr/bin/env python3
"""
Local multi-process network stress with telemetry, one build at a time: a relay server (state and
voice listeners, each on its own network thread, plus a voice mixer thread) and --clients client
processes, each with a state socket and a voice socket on two network threads, like a game client
running Entangle and FnVoice. Some clients drop and reconnect their voice socket on a new thread every
--reconnect-every seconds. Every process writes one telemetry CSV row per second.

  python run_stress.py --bench enet-bench.exe --build fixed=enet-fixed.dll --clients 6 --seconds 600 --scale 10 --out results/stress
"""

import argparse
import json
import os
import subprocess
import time

from enetbench import exit_code_name, parse_build


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--bench", required=True)
    parser.add_argument("--build", action="append", required=True, help="label=path")
    parser.add_argument("--clients", type=int, default=6)
    parser.add_argument("--seconds", type=float, default=600)
    parser.add_argument("--scale", type=float, default=10, help="message-rate multiplier over a realistic client (1 = 50 Hz voice, 20 Hz state, 30 Hz pose)")
    parser.add_argument("--reconnect-clients", type=int, default=2)
    parser.add_argument("--reconnect-every", type=float, default=20)
    parser.add_argument("--port", type=int, default=27500)
    parser.add_argument("--out", required=True)
    args = parser.parse_args()

    for index, build in enumerate(parse_build(spec) for spec in args.build):
        directory = os.path.join(args.out, build["label"])
        port = args.port + index * 10
        os.makedirs(directory, exist_ok=True)

        common = ["--lib", build["path"], "--label", build["label"], "--port", str(port)]
        processes = []
        started = time.time()

        server = subprocess.Popen([args.bench, "--mode", "stress-server", *common, "--seconds", str(args.seconds + 10),
            "--telemetry", os.path.join(directory, "server.csv")], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        processes.append(("server", 0, server))
        time.sleep(1.0)

        for instance in range(1, args.clients + 1):
            extra = ["--reconnect-every", str(args.reconnect_every)] if instance <= args.reconnect_clients else []
            client = subprocess.Popen([args.bench, "--mode", "stress-client", *common, "--instance", str(instance), "--seconds", str(args.seconds),
                "--scale", str(args.scale), "--telemetry", os.path.join(directory, f"client-{instance}.csv"), *extra],
                stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            processes.append(("client", instance, client))
            time.sleep(0.2)

        print(f"{build['label']}: server + {args.clients} clients running for {args.seconds:.0f}s (scale {args.scale})", flush=True)

        results = []

        for role, instance, process in processes:
            try:
                stdout, stderr = process.communicate(timeout=args.seconds + 120)
                code = process.returncode
            except subprocess.TimeoutExpired:
                process.kill()
                stdout, stderr = process.communicate()
                code = None

            summary = None

            for line in reversed(stdout.splitlines()):
                if line.startswith("{"):
                    summary = json.loads(line)
                    break

            results.append({
                "role": role, "instance": instance, "exit": exit_code_name(code), "ok": code == 0,
                "endedAfterSeconds": time.time() - started, "summary": summary,
                "stderrTail": stderr.strip().splitlines()[-12:] if stderr.strip() else [],
            })

            print(f"  {role} {instance}: {exit_code_name(code)}", flush=True)

        with open(os.path.join(directory, "run.json"), "w", encoding="utf-8") as handle:
            json.dump({"build": build["label"], "lib": build["path"], "clients": args.clients, "seconds": args.seconds, "scale": args.scale,
                "reconnectClients": args.reconnect_clients, "reconnectEvery": args.reconnect_every, "processes": results}, handle, indent=2)

        time.sleep(2.0)


if __name__ == "__main__":
    main()
