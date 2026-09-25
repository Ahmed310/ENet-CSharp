#!/usr/bin/env python3
"""
Compares two benchmark result directories (each with a summary.json from make_report.py), cell by cell,
to check that conclusions do not depend on when the runs happened.

  python compare_runs.py --a results-pass2 --b results-pass3 [--a-linux dir --b-linux dir]

For every cell it prints both medians and the change. A change counts as noise when it is smaller than
the larger of the two passes' own run-to-run spreads. The ratios the report relies on (2.7.0 vs no pool,
2.7.0 vs 2.6.1) are compared the same way.
"""

import argparse
import json
import os
import statistics


def load(directory):
    with open(os.path.join(directory, "summary.json"), encoding="utf-8") as handle:
        return json.load(handle)


def key(cell):
    return (cell["mode"], cell["build"], cell["size"], cell["threads"])


def cells_by_key(summary, kind):
    return {key(c): c for c in summary.get(f"bench_{kind}", []) if c.get("pairsPerSecond")}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--a", required=True)
    parser.add_argument("--b", required=True)
    parser.add_argument("--json", help="write the comparison here")
    args = parser.parse_args()

    a, b = load(args.a), load(args.b)
    report = {"cells": [], "ratios": [], "ambient": {}}

    for kind in ("managed", "native", "linux"):
        ca, cb = cells_by_key(a, kind), cells_by_key(b, kind)

        if not ca or not cb:
            continue

        ambient = lambda cells: statistics.median([c["ambientCpuPercent"] for c in cells.values() if c.get("ambientCpuPercent") is not None] or [float("nan")])
        report["ambient"][kind] = {"a": ambient(ca), "b": ambient(cb)}

        print(f"\n== {kind}: median M pairs/s, A -> B (change; noise band = larger run-to-run spread)")

        for k in sorted(set(ca) & set(cb), key=lambda k: (k[0], int(k[2].split("-")[0]), k[3], k[1])):
            x, y = ca[k], cb[k]
            change = (y["pairsPerSecond"] - x["pairsPerSecond"]) / x["pairsPerSecond"] * 100
            band = max(x["spreadPercent"], y["spreadPercent"])
            beyond = abs(change) > band
            report["cells"].append({"kind": kind, "mode": k[0], "build": k[1], "size": k[2], "threads": k[3], "a": x["pairsPerSecond"], "b": y["pairsPerSecond"],
                "changePercent": change, "noiseBandPercent": band, "beyondNoise": beyond})
            print(f"  {k[1]:<16} {k[2]:>5} B x{k[3]}  {x['pairsPerSecond'] / 1e6:7.1f} -> {y['pairsPerSecond'] / 1e6:7.1f}  {change:+6.1f}%  (band {band:4.1f}%){'  <-- beyond noise' if beyond else ''}")

        for other in ("nopool", "v2.6.1"):
            print(f"\n   2.7.0 / {other} ratio, A -> B")

            for k in sorted(set(ca) & set(cb), key=lambda k: (k[0], int(k[2].split("-")[0]), k[3])):
                if k[1] != "fixed":
                    continue

                o = (k[0], other, k[2], k[3])

                if o not in ca or o not in cb:
                    continue

                ra = ca[k]["pairsPerSecond"] / ca[o]["pairsPerSecond"]
                rb = cb[k]["pairsPerSecond"] / cb[o]["pairsPerSecond"]
                same_side = (ra >= 1) == (rb >= 1)
                report["ratios"].append({"kind": kind, "mode": k[0], "versus": other, "size": k[2], "threads": k[3], "a": ra, "b": rb, "sameConclusion": same_side})
                print(f"     {k[0]:<12} {k[2]:>5} B x{k[3]}  {ra:5.2f}x -> {rb:5.2f}x{'' if same_side else '  <-- conclusion flips'}")

    changes = [abs(c["changePercent"]) for c in report["cells"]]
    flips = [r for r in report["ratios"] if not r["sameConclusion"]]
    beyond = [c for c in report["cells"] if c["beyondNoise"]]

    print(f"\ncells compared: {len(report['cells'])}; median |change| {statistics.median(changes):.1f}%; beyond their noise band: {len(beyond)}; ratio conclusions that flip: {len(flips)}")
    print("ambient machine CPU (median per pass):", {k: f"{v['a']:.0f}% -> {v['b']:.0f}%" for k, v in report["ambient"].items()})

    if args.json:
        with open(args.json, "w", encoding="utf-8") as handle:
            json.dump(report, handle, indent=2)


if __name__ == "__main__":
    main()
