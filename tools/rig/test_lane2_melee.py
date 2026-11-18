# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
import unittest

import scen_lane2_melee as lane


def damage(blocked=0, double=0, unconfirmed=0):
    return dict(dmg_double=double, dmg_double_blocked=blocked, dmg_double_unconfirmed=unconfirmed)


class Lane2EvidenceTests(unittest.TestCase):
    def setUp(self):
        kinds = list(lane.ACTION_KINDS.values())
        sent = [{'kind': kind, 'source_ms': stamp} for stamp, kind in enumerate(kinds, 10)]
        self.stages = [{'station': 'fixture', 'required_kinds': kinds, 'source_actions': sent}]
        self.agent = [f'MP-RP-TIMELINE-IN peer=2 kind={kind} phase=1 seq={seq} source_ms={seq + 10} '
                      f'event_host_ms={seq + 100} result=accepted' for seq, kind in enumerate(kinds)]
        self.native = [f'MP-RP-COMBAT-ACTION peer=2 eid=0x1B kind={kind} phase=1 seq={seq} '
                       f'event_host_ms={seq + 100} lag_ms=20 result={result} scope=7 connection=8'
                       for seq, kind in enumerate(kinds) for result in ('in', 'installed')]

    def test_counters_require_native_install_on_the_admitted_body(self):
        report = lane.measure(self.stages, self.agent, [self.native], 2)
        self.assertEqual(dict(melee_swing_shown=1, melee_block_shown=2, melee_hitreact_shown=1), report['counters'])
        self.assertEqual(1, report['per_kind']['recoil']['installed'])
        self.assertTrue(report['stations_confirmed']['fixture'])
        self.assertEqual('grey', report['screen_observation']['status'])

        no_install = lane.measure(self.stages, self.agent, [self.native[::2]], 2)
        self.assertTrue(all(value == 0 for value in no_install['counters'].values()))
        self.assertFalse(no_install['stations_confirmed']['fixture'])
        wrong_body = [self.native[0], self.native[1].replace('eid=0x1B', 'eid=0xCC'), *self.native[2:]]
        self.assertEqual(0, lane.measure(self.stages, self.agent, [wrong_body], 2)['counters']['melee_swing_shown'])

    def test_paired_swing_and_block_and_master_strike_get_no_ordinary_credit(self):
        for kind, stamp, table, reason, verdict in (
            ('Attack', 10, 'sync_attack', 'paired', 'paired_attack'),
            ('BlockImpulse', 11, 'sync_attack', 'paired', 'paired_attack'),
            ('BlockImpulse', 12, 'sync_attack', 'paired', 'paired_attack'),
            ('BlockImpulse', 12, 'perfect_block', 'counter', 'master_strike'),
        ):
            with self.subTest(kind=kind, stamp=stamp, reason=reason):
                marker = (f'MP-RP-MELEE-ROW peer=2 kind={kind} source_ms={stamp} table=combat_action_{table} '
                          f'result=grey reason={reason}-engine-entry-unverified')
                notes = [*self.agent, marker]
                native_index = 2 * (stamp - 10)
                denied = self.native[:native_index] + self.native[native_index + 2:]
                self.assertEqual('grey', lane.measure(self.stages, notes, [denied], 2)[verdict]['status'])
                unexpected = lane.measure(self.stages, notes, [self.native], 2)
                self.assertEqual('red', unexpected[verdict]['status'])
                self.assertNotIn(stamp, [row['source_ms'] for row in unexpected['native_installs']])

    def test_damage_guard_requires_fresh_receiver_delta_and_stable_scope(self):
        before = {'host': damage(7), 'joiner': damage(6)}
        started = {'host': damage(8), 'joiner': damage(6)}
        observed = {'host': damage(7), 'joiner': damage(7)}
        self.assertEqual('grey', lane.damage_evidence(before, before, 'joiner', True)['status'])
        self.assertEqual('grey', lane.damage_evidence(before, started, 'joiner', True)['status'])
        self.assertEqual('grey', lane.damage_evidence(before, observed, 'joiner', False)['status'])
        self.assertEqual('grey', lane.damage_evidence(before, {'host': {}, 'joiner': damage(7)}, 'joiner', True)['status'])
        self.assertEqual('grey', lane.damage_evidence(before, dict(observed, host=damage(5)), 'joiner', True)['status'])
        self.assertEqual('grey', lane.damage_evidence(before, dict(observed, host=damage(7, unconfirmed=1)), 'joiner', True)['status'])
        self.assertEqual('red', lane.damage_evidence(before, dict(observed, host=damage(7, double=1)), 'joiner', False)['status'])
        report = lane.damage_evidence(before, observed, 'joiner', True)
        self.assertEqual('green', report['status'])
        self.assertEqual(1, report['deltas']['joiner']['dmg_double_blocked'])

    def test_unavailable_counter_is_optional_and_read_from_the_selected_role_pulse(self):
        fields = ' '.join(f'{key}=0' for key in sorted(lane.combat.TOTALS))
        host = f'MP-T3-STATS origin=agent role=host {fields}'
        joiner = f'MP-T3-STATS origin=agent role=joiner {fields} {lane.MELEE_UNAVAILABLE}=3'
        incomplete_host = f'MP-T3-STATS origin=agent role=host {lane.MELEE_UNAVAILABLE}=4'
        self.assertNotIn(lane.MELEE_UNAVAILABLE, lane.role_totals([host, joiner, incomplete_host], 'host'))
        complete_host = f'{host} {lane.MELEE_UNAVAILABLE}=0'
        self.assertEqual(0, lane.role_totals([complete_host, joiner, incomplete_host], 'host')[lane.MELEE_UNAVAILABLE])
        self.assertEqual(3, lane.role_totals([complete_host, joiner, incomplete_host], 'joiner')[lane.MELEE_UNAVAILABLE])

    def test_unavailable_delta_requires_both_receiver_pulses_in_one_scope(self):
        before = {'host': {lane.MELEE_UNAVAILABLE: 1}, 'joiner': {lane.MELEE_UNAVAILABLE: 2}}
        after = {'host': {lane.MELEE_UNAVAILABLE: 1}, 'joiner': {lane.MELEE_UNAVAILABLE: 5}}
        self.assertEqual({'status': 'green', 'delta': 0}, lane.unavailable_evidence(before, after, 'host', True))
        self.assertEqual({'status': 'red', 'delta': 3}, lane.unavailable_evidence(before, after, 'joiner', True))
        for start, end, stable in ((before, after, False), ({}, after, True), (before, {}, True), (after, before, True)):
            self.assertEqual({'status': 'grey', 'delta': None}, lane.unavailable_evidence(start, end, 'joiner', stable))


if __name__ == '__main__':
    unittest.main()
