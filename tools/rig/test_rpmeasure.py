# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
import json
import math
from pathlib import Path
import unittest
from urllib.parse import quote

import scen_s2_measure as measure


def staircase():
    # Entirely synthetic points used to check scheduling and serializer shape.
    return {'points': [[0, 0, 0, 0], [2, 0, 2, 0], [3, 0, 3, 0.15], [4, 0, 4, 0.30],
                       [5, 0, 5, 0.45], [20, 1, 8, 0.45], [60, 0, 0, 0.45]], 'stairs': [2, 5]}


class RouteTests(unittest.TestCase):
    def test_shared_waypoints_drive_exactly_one_minute(self):
        route = measure.parse_route(staircase())
        plan = measure.bot_plan(route)
        self.assertTrue(plan.startswith('at 0 stand 0 0 0 0\nat 0 move 1 0 2 0\n'))
        self.assertEqual(3, plan.count(' move 1 0 1 '))
        self.assertTrue(plan.endswith('end 60\n'))
        self.assertIn('stair1=2,stair2=5,drive=true', next(measure.route_commands(route, 'run123', True)))
        self.assertIn('stair1=2,stair2=5,drive=false', next(measure.route_commands(route, 'run123', False)))
        for command in measure.route_commands(route, '01234567890123456789', True):
            self.assertLess(len(quote('#System.LogAlways("[W206S0000000000000000]"); ' + command, safe='')), 1900)

    def test_reject_missing_stair_shapes_and_invalid_coordinates(self):
        for mutate in (lambda case: case['points'][-1].__setitem__(0, 59),
                       lambda case: case['points'][1].__setitem__(1, math.nan),
                       lambda case: case['points'][4].__setitem__(3, 1),
                       lambda case: case['points'][4].__setitem__(0, 4),
                       lambda case: case['stairs'].__setitem__(0, 2.5)):
            with self.subTest(mutate=mutate):
                case = staircase()
                mutate(case)
                with self.assertRaises(ValueError):
                    measure.parse_route(case)

    def test_comparable_joint_cycles_measure_a_ratio(self):
        def rows(speed, cycle):
            result = []
            for i in range(240):
                t = i / 20
                x = t * speed
                result.append((t, {'root': (x, 0, 0), 'lfoot': (x + 0.17 * math.sin(math.tau * t / cycle), 0, 0)}))
            return result

        n, ratio = measure.paired_steps(rows(1.5, 1.0), rows(1.5, 1.2))
        self.assertEqual(1, n)
        self.assertTrue(1150 <= ratio <= 1250)
        self.assertEqual((0, None), measure.paired_steps(rows(1.5, 1.0), rows(3, 1.2)))
        self.assertEqual((0, None), measure.paired_steps(rows(1.5, 1.0), []))


class EvidenceTests(unittest.TestCase):
    fired = ('relay: RELAY-FAULT rpmeasure-netz-schlecht fired kind=lag role=joiner dir=both '
             'at=t:+0 via=time t=0.0s for_s=0 ms=150 jitter_ms=35 seed=221 targets=0')

    def test_requires_running_current_relay_with_matching_fired_plan(self):
        plan = Path(__file__).with_name('netz-schlecht.json')
        rule = json.loads(plan.read_text(encoding='utf-8'))['rules'][0]
        self.assertEqual(('joiner', 'both', 150, 35), (rule['role'], rule['dir'], rule['ms'], rule['jitter_ms']))
        rec = {'fault_plan': str(plan), 'relay_log': 'relay.log', 'relay_log_offset': 105}
        trace = [('relay.log', 0, self.fired), ('relay.log', 105, self.fired)]
        read = lambda path, offset: [line for name, start, line in trace if path == name and start >= offset]
        self.assertEqual(self.fired, measure.network_evidence(rec, plan, 'schlecht', bool, read)['relay_evidence'])
        with self.assertRaisesRegex(RuntimeError, 'no frames actually delayed'):
            measure.network_evidence(rec, plan, 'schlecht', bool, read, require_delay=True)
        trace.append(('relay.log', 300, 'RELAY-FAULTS t=10s final=0 plan=netz-schlecht.json seed=221 delayed=42/111B'))
        self.assertEqual(42, measure.network_evidence(rec, plan, 'schlecht', bool, read, require_delay=True)['relay_delayed_frames'])
        trace.pop()
        trace.pop()
        self.assertIsNone(measure.network_evidence(rec, plan, 'schlecht', bool, read)['relay_evidence'])
        with self.assertRaisesRegex(RuntimeError, 'did not fire'):
            measure.network_evidence(rec, plan, 'schlecht', bool, read, require_delay=True)
        with self.assertRaisesRegex(RuntimeError, 'before the join'):
            measure.network_evidence(rec, plan, 'normal', bool, read)

    def test_normal_profile_refuses_armed_fault_mode(self):
        rec = {'fault_plan': '', 'relay_log': 'relay.log', 'relay_log_offset': 12}
        with self.assertRaisesRegex(RuntimeError, 'active relay fault mode'):
            measure.network_evidence(rec, '/plan', 'normal', bool, lambda _path, _offset: ['RELAY-FAULTS loaded plan=fault.json'])

    def test_fresh_sample_evidence_uses_current_role_and_source(self):
        rows = ['MP-T3-STATS origin=agent role=host rp_e2e_n=15 rp_e2e_ms_p95=165 owner_violations=0',
                'MP-T3-STATS origin=dll role=host rp_e2e_n=12 rp_e2e_ms_p95=175',
                'MP-T3-STATS origin=agent role=joiner rp_e2e_n=23 rp_e2e_ms_p95=176',
                'MP-T3-STATS origin=agent role=host rp_e2e_n=27 rp_e2e_ms_p95=171 owner_violations=1']
        self.assertEqual({'rp_e2e_n': 27, 'rp_e2e_ms_p95': 171, 'owner_violations': 1}, measure.last_stats(rows, 'host'))
        self.assertEqual({}, measure.last_stats(['MP-T3-STATS origin=agent role=host tp_total=1'], 'host'))

    def test_sender_ids_come_from_real_and_bot_connection_logs(self):
        agent = ['16:51:44.267 Connected! Assigned id=6 (protocol v6)',
                 '16:52:10.341 Connected! Assigned id=255 (protocol v6)',
                 '16:52:13.149 Connected! Assigned id=255 (protocol v6)',
                 '16:52:17.652 Connected! Assigned id=256 (protocol v6)']
        bot = ['PEER connected id=8 release=dev protocol=v6 steps=4',
               'BOT-T4 role=joiner ready=1', 'BOT-T4 role=joiner complete=1 poses=1200 actions=0 ranged=0']
        self.assertEqual([6, 255, 255], measure.peer_ids(agent, measure.ASSIGNED_PEER))
        self.assertEqual([8], measure.peer_ids(bot, measure.BOT_PEER))
        self.assertEqual([], measure.peer_ids(bot, measure.ASSIGNED_PEER))

    def test_figure_evidence_requires_positive_same_pc_samples_for_the_sender(self):
        def row(peer, generation, count, clock='same_pc_qpc', final=0):
            return (f'MP-RP-MEASURE figure=kcd2mp_{peer} gen={generation} clock={clock} '
                    f'proof=writer final={final} rp_e2e_ms_p95=170 rp_e2e_n={count}')

        unrelated = [row(2, 3, 209), row(8, 3, 0), row(8, 3, 350, 'unknown')]
        self.assertIsNone(measure.figure_stats(unrelated, 8))
        fresh = unrelated + [row(8, 3, 40), row(8, 3, 1180, final=1)]
        self.assertEqual({'figure': 'kcd2mp_8', 'world_generation': 3, 'rp_e2e_ms_p95': 170, 'rp_e2e_n': 1180},
                         measure.figure_stats(fresh, 8))
        self.assertIsNone(measure.figure_stats(fresh + [row(8, 3, 4)], 8))
        self.assertIsNone(measure.figure_stats(fresh + [row(8, 4, 1330)], 8))

    def test_receiver_requires_finite_in_world_position(self):
        self.assertTrue(measure.finite_position((0, 0, -1)))
        for position in (None, (), (1, 2), (1, 2, math.nan), (1, 2, math.inf)):
            self.assertFalse(measure.finite_position(position))


if __name__ == '__main__':
    unittest.main()
