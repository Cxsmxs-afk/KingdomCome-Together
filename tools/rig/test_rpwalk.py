# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
import unittest
from urllib.parse import quote

import scen_s2_walk as walk


def route():
    return {'points': [[0, 0, 0, 0], [2, 0, 2, 0], [3, 0, 3, 0.15], [4, 0, 4, 0.30], [5, 0, 5, 0.45],
                       [15, 0, 15, 1.55], [20, 0, 20, 1.55], [35, 0, 65, 1.55], [45, 0, 110, 1.55], [60, 0, 110, 1.55]],
            'stairs': [2, 5], 'slope': [5, 15], 'gaits': {'walk': [0, 20], 'run': [20, 35], 'sprint': [35, 45]}}


def controller(peer=2, eid='0x1B', **updates):
    fields = dict.fromkeys(walk.CTRL_FIELDS, 0)
    fields.update(req_ok=600, scope=3, connection=5, ctrl_frames=600, pull_observed_ms=59000,
                  gait_walk=200, gait_run=200, gait_sprint=200)
    fields.update(updates)
    return f'MP-RP-CTRL figure=kcd2mp_{peer} eid={eid} ' + ' '.join(f'{k}={v}' for k, v in fields.items())


class WalkTests(unittest.TestCase):
    def test_same_route_has_separate_stairs_and_slope_and_three_gaits(self):
        plan = walk.parse_walk_plan(route())
        script = walk.bot_plan(plan.route)
        self.assertEqual((5, 15), plan.slope)
        self.assertIn('at 20 move 3 0 15', script)
        self.assertIn('at 35 move 4.5 0 10', script)
        self.assertTrue(script.endswith('end 60\n'))
        self.assertLess(len(quote('#System.LogAlways("[W206S0]"); ' + ' '.join(walk.LUA_WALK_OBSERVE.split()), safe='')), 1900)
        for modify in (lambda value: value.update(slope=[2, 5]),
                       lambda value: value.update(slope=[15, 20]),
                       lambda value: value['gaits'].update(sprint=[35, 60])):
            with self.subTest(modify=modify):
                invalid = route()
                modify(invalid)
                with self.assertRaises(ValueError):
                    walk.parse_walk_plan(invalid)

    def test_same_figure_gait_delta_and_replaced_body(self):
        before = walk.ctrl_rows([controller(req_ok=500, gait_sprint=180), controller(peer=3)], 2)
        after = walk.ctrl_rows([controller(), controller(peer=3)], 2)
        body, delta = walk.scoped_delta(before, after)
        self.assertEqual(0x1B, body['eid'])
        self.assertEqual(100, delta['req_ok'])
        self.assertEqual(20, delta['gait_sprint'])
        self.assertEqual(({}, {}), walk.scoped_delta(before, walk.ctrl_rows([controller(), controller(eid='0x1C')], 2)))
        self.assertEqual(({}, {}), walk.scoped_delta([], walk.ctrl_rows([controller(), controller(req_ok=2), controller()], 2)))
        self.assertEqual(({}, {}), walk.scoped_delta(after, before))

    def test_thresholds_use_raw_pull_time_and_unknown_steps_stay_ungraded(self):
        body, delta = walk.scoped_delta([], walk.ctrl_rows([controller()], 2))
        totals = {'rp_ctrl_frames': 600, 'rp_req_ok': 600, 'rp_owner_n': 600, 'owner_violations': 0}
        self.assertEqual([], walk.checks_for('rpwalk', body, delta, totals, {}, (0, None), True))
        self.assertEqual(['pull-rate-at-least-0.2-per-minute'],
                         walk.checks_for('rpwalk', body, dict(delta, pulls=1, pull_observed_ms=300000), totals, {}, (1, 1100), True))
        self.assertEqual(['step-ratio-out-of-target'], walk.checks_for('rpwalk', body, delta, totals, {}, (1, 899), True))
        self.assertEqual(['unapproved-hard-placement'], walk.checks_for('rpwalk', dict(body, hard_tp_unapproved=1), delta, totals, {}, (0, None), True))

    def test_requires_real_crouch_and_draw_on_same_body(self):
        intent = 'MP-RP-CTRL-STANCE body=kcd2mp_2 eid=0x1B owner=driver crouch=1 result=unconfirmed'
        good = intent.replace('unconfirmed', 'confirmed')
        self.assertEqual([(0x1B, 1)], walk.confirmed_stances([intent, good, good.replace('_2', '_3')], 2))
        self.assertEqual(0xF00101B, walk.weapon_eid('drawn=true hand=040038 eid=0F00101B'))
        body, delta = walk.scoped_delta([], walk.ctrl_rows([controller()], 2))
        totals = {'rp_ctrl_frames': 600, 'rp_req_ok': 600, 'rp_owner_n': 600, 'owner_violations': 0}
        travel = {'edges': [{'body_eid': 0x1B}, {'body_eid': 0x1B}], 'source_draw_emitted': True,
                  'figure_draw_observed': True, 'figure_weapon_eid': 0x1B}
        self.assertEqual([], walk.checks_for('rpstance', body, delta, totals, travel, (0, None), True))
        self.assertEqual(['draw-unconfirmed'], walk.checks_for('rpstance', body, delta, totals,
                         dict(travel, figure_weapon_eid=0xAA), (0, None), True))


if __name__ == '__main__':
    unittest.main()
