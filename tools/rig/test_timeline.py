# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
import unittest

import scen_s2_timeline as timeline


def inbound(source=101, seq=5, peer=2, result='accepted'):
    return f'12:00:00.000 MP-RP-TIMELINE-IN peer={peer} kind=1 phase=1 seq={seq} source_ms={source} event_host_ms=5001 result={result}'


def installed(seq=5, peer=2, pose=5001, skew=0, result='installed', eid='0x1B', scope=7):
    return (f'MP-RP-TIMELINE-ACTION peer={peer} eid={eid} kind=1 phase=1 seq={seq} event_host_ms=5001 '
            f'pose_host_ms={pose} skew_ms={skew} result={result} reason=- scope={scope} connection=8')


class TimelineEvidenceTests(unittest.TestCase):
    def test_requires_exact_attempt_admission_and_engine_install(self):
        fixture = [installed(result='queued'), installed(result='unconfirmed'), installed(seq=8),
                   installed(peer=3), installed(pose=0, skew=5001), installed(pose=5004, skew=1)]
        self.assertEqual([], timeline.paired_actions([101], [inbound()], fixture, 2))
        self.assertEqual([], timeline.paired_actions([999], [inbound()], [installed()], 2))
        self.assertEqual([], timeline.paired_actions([101], [inbound(result='refused')], [installed()], 2))
        rows = timeline.paired_actions([101], [inbound()], fixture + [installed(pose=4990, skew=11)], 2)
        self.assertEqual([(101, 5, 11)], [(row['source_ms'], row['seq'], row['skew_ms']) for row in rows])

    def test_body_measurement_does_not_mix_figure_or_generation(self):
        source = [inbound(), inbound(source=102, seq=6)]
        self.assertEqual([], timeline.paired_actions([101, 102], source, [installed(), installed(seq=6, eid='0x1C')], 2))
        self.assertEqual([], timeline.paired_actions([101, 102], source, [installed(), installed(seq=6, scope=8)], 2))
        self.assertEqual([], timeline.paired_actions([101], source, [installed(), installed(eid='0x1C')], 2))
        rows = timeline.paired_actions([101], source, [installed()], 2)
        figure = ('MP-RP-TIMELINE figure=kcd2mp_2 eid=0x1B scope=7 connection=8 buffer_ms=81 extrap_ms_max=190 '
                  'late_samples=3 pose_n=19 applied_host_ms=5010 target_host_ms=5009')
        self.assertEqual(81, timeline.figure_pose([figure], 2, rows)['buffer_ms'])
        self.assertEqual({}, timeline.figure_pose([figure.replace('0x1B', '0x1C')], 2, rows))
        self.assertEqual({}, timeline.figure_pose([figure.replace('scope=7', 'scope=8')], 2, rows))

    def test_thresholds_and_incomplete_coverage(self):
        self.assertEqual(['grey', 'green', 'yellow', 'yellow', 'red'], [timeline.skew_color(v) for v in (None, 19, 20, 49, 50)])
        self.assertIsNone(timeline.skew_p95([]))
        self.assertEqual(18, timeline.skew_p95([{'skew_ms': v} for v in (8, 0, 18)]))
        rows = timeline.paired_actions([101], [inbound()], [installed()], 2)
        stats = dict.fromkeys(timeline.METRICS, 0)
        stats.update(rp_buffer_ms=60, rp_extrap_ms_max=200, rp_timeline_pose_n=14, rp_timeline_skew_n=1, rp_owner_n=20)
        self.assertEqual([], timeline.timeline_failures([101], rows, stats))
        self.assertEqual(['incomplete-swing-coverage'], timeline.timeline_failures([101, 102], rows, stats))
        self.assertEqual(['owner-violations'], timeline.timeline_failures([101], rows, dict(stats, owner_violations=1)))
        self.assertEqual(['timeline-or-owner-counters-unmeasured'], timeline.timeline_failures([101], rows, {}))
        self.assertEqual(['no-engine-installed-swing-with-paired-host-pose'], timeline.timeline_failures([101], [], stats))

    def test_staged_sources_are_distinct_from_received_actions(self):
        text = 'MP-ACTION section=outbound kind=Attack gen=9 row=abc ms=4294967295'
        self.assertEqual([4294967295], timeline.swings([inbound(), text], timeline.SOURCE_SWING))
        self.assertEqual([114], timeline.swings(['PEER t=11.8 attack row=abc type=Slash ms=114'], timeline.BOT_SWING))
        plan = timeline.bot_plan((1, 2, 3), timeline.BOT_SWING_ROW)
        self.assertTrue(plan.startswith('at 0 stand 1 2 3 0\n'))
        self.assertEqual(3, plan.count(' attack '))
        self.assertIn(f'at 8 move 1 0 4\nat 11.8 attack {timeline.BOT_SWING_ROW}\n', plan)


if __name__ == '__main__':
    unittest.main()
