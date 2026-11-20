# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
import importlib.util
import json
import math
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "perf" / "latency_gate.py"
spec = importlib.util.spec_from_file_location("latency_gate", SCRIPT)
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)


def capture(host="A"):
    def phases():
        return {metric: [{"id": "one", "capture_ms": 1000, "effect_ms": 1038}] for metric in gate.METRICS}
    return {"pcs": {pc: {"build": "current", "fps_min": 60, "ours_us_p95": 1300, "rp_owner_n": 20,
                         "owner_violations": 0, "extra_slow_frames": 0} for pc in ("A", "B")},
            "host_pc": host, "players": 2, "transport": "tcp", "profile": "reference_lan", "source": "live",
            "clock": {"method": "aligned monotonic", "error_ms": 2}, "rtt_ms": [3, 10],
            "directions": {direction: phases() for direction in gate.DIRECTIONS}}


class GateTests(unittest.TestCase):
    def test_independent_metrics_include_clock_error_at_the_limit(self):
        record = capture()
        record["directions"]["host_to_joiner"]["isolated_hit_effect"][0]["effect_ms"] = 1039
        record["directions"]["joiner_to_host"]["action_visible"][0]["effect_ms"] = 1027
        results = gate.score(record, "current")["directions"]
        self.assertEqual("fail", results["host_to_joiner"]["isolated_hit_effect"]["lan_gate"])
        movement = results["host_to_joiner"]["movement_visible"]
        self.assertEqual((38, 38, 38, 38, 40), tuple(movement[key] for key in
                         ("p50_ms", "p95_ms", "p99_ms", "max_ms", "p95_upper_ms")))
        self.assertEqual("pass", movement["lan_gate"])
        self.assertTrue(results["joiner_to_host"]["action_visible"]["target_p95_under_30"])

    def test_missing_effects_and_owner_observations_stay_open(self):
        record = capture()
        record["directions"]["host_to_joiner"]["action_visible"].append({"id": "two", "capture_ms": 1100, "effect_ms": None})
        record["pcs"]["A"]["rp_owner_n"] = 0
        result = gate.score(record, "current")
        action = result["directions"]["host_to_joiner"]["action_visible"]
        self.assertEqual((2, 1, 1, "open"), (action["attempts"], action["n"], action["missing"], action["lan_gate"]))
        self.assertEqual("open", result["frame"]["A"]["gate"])

    def test_fixture_network_or_fps_cannot_satisfy_reference_conditions(self):
        for update in ({"source": "fixture"}, {"rtt_ms": [11]}, {"rtt_ms": []}, {"profile": "delay"}):
            record = capture()
            record.update(update)
            self.assertFalse(gate.score(record, "current")["reference_conditions"])
        record = capture()
        record["pcs"]["B"]["fps_min"] = 50
        self.assertFalse(gate.score(record, "current")["reference_conditions"])

    def test_incompatible_build_and_invalid_times_fail_admission(self):
        with self.assertRaisesRegex(ValueError, "selected build"):
            gate.score(capture(), "older")
        for time in (999, -1, math.nan):
            with self.subTest(time=time), self.assertRaisesRegex(ValueError, "effect_ms"):
                gate.latency([{"id": "one", "capture_ms": 1000, "effect_ms": time}], 0)
        with self.assertRaisesRegex(ValueError, "duplicate"):
            row = {"id": "same", "capture_ms": 1000, "effect_ms": 1001}
            gate.latency([row, row], 0)

    def test_frame_and_owner_failures_have_independent_gates(self):
        for key, value in (("ours_us_p95", 2000), ("owner_violations", 1), ("extra_slow_frames", 1)):
            record = capture()
            record["pcs"]["A"][key] = value
            self.assertEqual("fail", gate.score(record, "current")["frame"]["A"]["gate"])

    def test_cli_requires_an_actual_role_swap_in_the_same_profile_group(self):
        with tempfile.TemporaryDirectory() as temp:
            a, b = (Path(temp) / name for name in ("host-a.json", "host-b.json"))
            a.write_text(json.dumps(capture()), encoding="utf-8")
            record = capture("B")
            def run(*paths):
                return subprocess.run([sys.executable, str(SCRIPT), "--build", "current", *paths],
                                      capture_output=True, text=True, check=False).returncode
            self.assertEqual(2, run(a))
            b.write_text(json.dumps({**record, "transport": "steam"}), encoding="utf-8")
            self.assertEqual(2, run(a, b))
            b.write_text(json.dumps(record), encoding="utf-8")
            self.assertEqual(0, run(a, b))


if __name__ == "__main__":
    unittest.main()
