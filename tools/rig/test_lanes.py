# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
import unittest

import scen_s2_lanes as lanes


def native_row(nonce, frames=120):
    return (f'MP-LANES nonce={nonce} frames={frames} rt_offered={frames * 30} bulk_offered={frames * 50} '
            f'rt={frames * 30} bulk={frames * 50} busy=0 rt_before_bulk=4000 ordered=1 valid=1 '
            'rt_p95_ms=33 rt_max_ms=38 bulk_p95_ms=96')


class LaneEvidenceTests(unittest.TestCase):
    def test_requires_fresh_matching_native_probe_shape(self):
        lines = [native_row(7), native_row(9), 'MP-LANES agent nonce=8 frames=120 started=1']
        self.assertEqual({}, lanes.probe_result(lines, 8))
        self.assertEqual(6000, lanes.probe_result(lines, 7)['bulk'])
        self.assertEqual(['native-probe-unmeasured'], lanes.verdict_probe({}, 120))

    def test_checks_each_frame_workload_and_ordered_completion(self):
        measured = lanes.probe_result([native_row(7)], 7)
        self.assertEqual([], lanes.verdict_probe(measured, 120))
        for key, value, reason in (('frames', 119, 'wrong-frame-workload'),
                                   ('valid', 0, 'native-probe-scope-changed'),
                                   ('rt_offered', 3000, 'wrong-frame-workload'),
                                   ('rt', 3599, 'incomplete-rt-workload'),
                                   ('bulk', 5999, 'reliable-bulk-missing-or-out-of-order'),
                                   ('ordered', 0, 'reliable-bulk-missing-or-out-of-order'),
                                   ('rt_before_bulk', 0, 'no-observed-rt-priority'),
                                   ('rt_p95_ms', 34, 'probe-rt-p95-at-least-34ms')):
            with self.subTest(key=key):
                self.assertIn(reason, lanes.verdict_probe(dict(measured, **{key: value}), 120))

    def test_requires_positive_coverage_and_zero_realtime_side_effects(self):
        stats = dict.fromkeys(lanes.STATS_FIELDS, 0)
        stats.update(dict.fromkeys(lanes.COVERAGE, 25), edge_rt_age_ms_p95=33, edge_rt_age_ms_max=68,
                     inproc_fallbacks=2, pipe_busy_replies=3)
        fields = ' '.join(f'{key}={value}' for key, value in stats.items())
        rows = ['MP-T3-STATS origin=agent role=host ' + fields,
                'MP-T3-STATS origin=dll role=joiner ' + fields,
                'MP-T3-STATS origin=agent role=joiner edge_rt_age_ms_p95=0']
        self.assertEqual(stats, lanes.rows_for_role(rows, 'host'))
        self.assertEqual({}, lanes.rows_for_role(rows, 'joiner'))
        self.assertEqual([], lanes.verdict_stats(stats))
        self.assertEqual(['lane-stats-unmeasured'], lanes.verdict_stats(dict(stats, edge_proc_n=0)))
        self.assertEqual(['rt-p95-at-least-34ms'], lanes.verdict_stats(dict(stats, edge_rt_age_ms_p95=34)))
        for zero in lanes.ZERO_STATS:
            self.assertEqual([zero], lanes.verdict_stats(dict(stats, **{zero: 1})))

    def test_bot_work_continues_for_probe_window(self):
        plan = lanes.bot_plan((5, 6, 7))
        self.assertTrue(plan.startswith('at 0 stand 5 6 7 0\n'))
        self.assertTrue(plan.endswith('at 86 move 0.5 3.14159265 4\nend 92\n'))


if __name__ == '__main__':
    unittest.main()
