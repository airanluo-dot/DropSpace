"""Match an explicit glass refresh probe to a PresentMon 2.x CSV from the same run.

Collect the preview PID with --process_id PID --v1_metrics --qpc_time. The ETW
session may start before or end after the probe.
    py scripts/analyze-glass-display.py refresh-YYYYMMDD-HHMMSS.json frames.csv
"""

import argparse
import bisect
import csv
import json
import math
import statistics
from pathlib import Path


def summarize(values):
    if not values:
        return None
    ordered = sorted(values)
    last = len(ordered) - 1
    return {
        "p50": round(ordered[math.floor(last * .50)], 3),
        "p95": round(ordered[math.floor(last * .95)], 3),
        "maximum": round(ordered[-1], 3),
        "mean": round(statistics.mean(ordered), 3),
    }


def analyze(refresh, presentmon):
    probe = json.loads(refresh.read_text(encoding="utf-8-sig"))
    chain = int(probe["swapChainAddress"], 16)
    frequency = probe["qpcFrequency"]
    samples = probe["presentSamples"]
    starts = [s["PresentStartQpc"] for s in samples]
    ends = [s["PresentEndQpc"] for s in samples]
    if not samples or any(end >= start for end, start in zip(ends, starts[1:])):
        raise ValueError("The probe has missing or overlapping glass Present calls.")

    with presentmon.open(encoding="utf-8-sig", newline="") as csvfile:
        rows = list(csv.DictReader(csvfile))
    chain_rows = [r for r in rows if r["Runtime"] == "DXGI" and int(r["SwapChainAddress"], 16) == chain]
    target = sorted((r for r in chain_rows if starts[0] <= int(r["QPCTime"]) <= ends[-1]),
                    key=lambda r: int(r["QPCTime"]))
    if not target:
        raise ValueError("No ETW records match both the glass swap chain and the probe time range.")
    if len({r["ProcessID"] for r in target}) != 1:
        raise ValueError("More than one process matched the address. Collect only the glass probe PID.")

    seen = set()
    dropped = invalid_display = 0
    display_age, submit_age, downstream, spacing = [], [], [], []
    for row in target:
        qpc = int(row["QPCTime"])
        index = bisect.bisect_right(starts, qpc) - 1
        if qpc > ends[index]:
            continue
        if index in seen:
            raise ValueError("Multiple ETW Present events matched the same native call.")
        seen.add(index)
        if row["Dropped"] != "0":
            dropped += 1
            continue
        until = float(row["msUntilDisplayed"])
        if not math.isfinite(until) or until <= 0:
            invalid_display += 1
            continue
        # WGC SystemRelativeTime uses 100 ns ticks with the same epoch as QPC.
        # Subtract in integers before converting to milliseconds.
        source_ticks = samples[index]["SourceTimeTicks"]
        age_ms = (qpc * 10_000_000 - source_ticks * frequency) / (frequency * 10_000)
        # Preserve the signed difference. In measured traces some WGC timestamps
        # fell slightly after CPU submission and before the reported display time.
        display_age.append(age_ms + until)
        submit_age.append(age_ms)
        downstream.append(until)
        interval = float(row["msBetweenDisplayChange"])
        if math.isfinite(interval) and interval > 0:
            spacing.append(interval)

    first = int(target[0]["QPCTime"])
    last = int(target[-1]["QPCTime"])
    return {
        "processId": int(target[0]["ProcessID"]), "swapChainAddress": probe["swapChainAddress"],
        "observedSeconds": round((last - first) / frequency, 3),
        "internalPresentsWithinSegment": sum(end >= first and start <= last for start, end in zip(starts, ends)),
        "otherChainRows": len(rows) - len(chain_rows), "outsideProbe": len(chain_rows) - len(target),
        "matchedPresents": len(seen), "unmatchedPresents": len(target) - len(seen),
        "droppedMatched": dropped, "missingDisplayTime": invalid_display, "displayedSamples": len(display_age),
        "sourceAheadOfPresent": sum(age < 0 for age in submit_age),
        "sourceToDisplayedMs": summarize(display_age), "sourceToPresentMs": summarize(submit_age),
        "presentToDisplayedMs": summarize(downstream), "displayIntervalMs": summarize(spacing),
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("refresh", type=Path, help="refresh JSON from the instrumented glass build")
    parser.add_argument("presentmon", type=Path, help="PresentMon CSV with v1 metrics and raw QPC")
    args = parser.parse_args()
    print(json.dumps(analyze(args.refresh, args.presentmon), indent=2))
