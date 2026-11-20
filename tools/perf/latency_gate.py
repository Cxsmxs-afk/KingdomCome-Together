#!/usr/bin/env python3
# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
"""Score paired observations on an already aligned monotonic timeline."""

import argparse
import json
import math
from pathlib import Path


METRICS = ("movement_visible", "action_visible", "isolated_hit_effect")
DIRECTIONS = ("host_to_joiner", "joiner_to_host")


def nonnegative(value):
    return type(value) in (int, float) and math.isfinite(value) and value >= 0


def require(condition, reason):
    if not condition:
        raise ValueError(reason)


def distribution(values):
    if not values:
        return {"n": 0, "p50_ms": None, "p95_ms": None, "p99_ms": None, "max_ms": None}
    values = sorted(values)
    return {**{f"p{p}_ms": values[math.ceil(len(values) * p / 100) - 1] for p in (50, 95, 99)},
            "n": len(values), "max_ms": values[-1]}


def latency(samples, clock_error_ms):
    require(isinstance(samples, list), "samples must be an array")
    durations = []
    ids = set()
    for sample in samples:
        require(isinstance(sample, dict), "a sample must be an object")
        sample_id = sample.get("id")
        require(isinstance(sample_id, str) and sample_id and sample_id not in ids, "missing or duplicate sample ID")
        ids.add(sample_id)
        start, end = sample.get("capture_ms"), sample.get("effect_ms")
        require(nonnegative(start), "capture_ms must be finite monotonic time")
        if end is not None:
            require(nonnegative(end) and end >= start, "effect_ms precedes capture_ms or is invalid")
            durations.append(end - start)
    out = distribution(durations)
    upper = None if not durations else out["p95_ms"] + clock_error_ms
    missing = len(samples) - len(durations)
    return {**out, "attempts": len(samples), "missing": missing, "p95_upper_ms": upper,
            "lan_gate": "open" if missing or upper is None else "pass" if upper <= 40 else "fail",
            "target_p95_under_30": upper is not None and upper < 30 and missing == 0}


def score(run, build):
    require(isinstance(run, dict), "a run must be an object")
    pcs = run.get("pcs")
    require(isinstance(pcs, dict) and set(pcs) == {"A", "B"} and run.get("host_pc") in ("A", "B"),
            "identify PCs A and B and their host assignment")
    require(all(isinstance(pc, dict) and pc.get("build") == build for pc in pcs.values()),
            "both PCs must report the selected build")
    require(run.get("transport") in ("tcp", "steam") and run.get("players") in (2, 3, 4) and
            isinstance(run.get("profile"), str) and run["profile"], "invalid network profile")
    clock = run.get("clock")
    require(isinstance(clock, dict) and isinstance(clock.get("method"), str) and clock["method"] and
            nonnegative(clock.get("error_ms")), "provide the monotonic clock alignment and its error bound")
    rtt = run.get("rtt_ms", [])
    require(isinstance(rtt, list) and all(map(nonnegative, rtt)), "RTT samples must be finite millisecond values")
    net = {key: run.get(key) for key in ("jitter_ms", "loss_percent")}
    require(all(value is None or nonnegative(value) for value in net.values()) and
            (net["loss_percent"] is None or net["loss_percent"] <= 100), "invalid jitter or loss")
    lan = run["profile"] == "reference_lan" and run.get("source") == "live" and bool(rtt) and max(rtt) <= 10 and all(
        nonnegative(pc.get("fps_min")) and pc["fps_min"] >= 60 for pc in pcs.values())
    samples = run.get("directions")
    require(isinstance(samples, dict) and set(samples) == set(DIRECTIONS), "capture both sender directions")
    metrics = {}
    for direction in DIRECTIONS:
        series = samples[direction]
        require(isinstance(series, dict) and set(series) == set(METRICS), "capture each latency metric separately")
        metrics[direction] = {name: latency(series[name], clock["error_ms"]) for name in METRICS}
        if not lan:
            for result in metrics[direction].values():
                result["lan_gate"] = "outside_reference_conditions"
                result["target_p95_under_30"] = None
    frame = {}
    for key, pc in pcs.items():
        values = {k: pc.get(k) for k in ("ours_us_p95", "rp_owner_n", "owner_violations", "extra_slow_frames")}
        require(all(v is None or nonnegative(v) for v in [*values.values(), pc.get("fps_min")]),
                "invalid frame or owner measurement")
        if run.get("source") != "live" or None in values.values() or not values["rp_owner_n"]:
            state = "open"
        else:
            state = "pass" if (values["ours_us_p95"] < 2000 and
                               values["owner_violations"] == 0 and values["extra_slow_frames"] == 0) else "fail"
        frame[key] = {**values, "fps_min": pc.get("fps_min"), "gate": state}
    return {**{key: run[key] for key in ("host_pc", "transport", "players", "profile")},
            "reference_conditions": lan, "clock": clock, "rtt": distribution(rtt), **net,
            "directions": metrics, "frame": frame}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--build", required=True)
    parser.add_argument("captures", nargs="+", type=Path)
    args = parser.parse_args()
    try:
        runs = [score(json.loads(file.read_text(encoding="utf-8")), args.build) for file in args.captures]
    except (OSError, ValueError) as error:
        parser.error(str(error))
    print(json.dumps({"build": args.build, "runs": runs}, indent=2, allow_nan=False))
    gates = [frame["gate"] for run in runs for frame in run["frame"].values()]
    gates += [metric["lan_gate"] for run in runs for direction in run["directions"].values()
              for metric in direction.values() if run["reference_conditions"]]
    if "fail" in gates:
        return 1
    groups = {}
    for run in runs:
        if run["reference_conditions"]:
            groups.setdefault((run["transport"], run["players"]), set()).add(run["host_pc"])
    return 0 if groups and all(roles == {"A", "B"} for roles in groups.values()) and all(
        gate == "pass" for gate in gates) else 2


if __name__ == "__main__":
    raise SystemExit(main())
