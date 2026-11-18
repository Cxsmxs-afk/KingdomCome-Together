# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
import unittest

import scen_s2_combat as combat


def row(kind, seq, stamp, result, eid='0x1B', **fields):
    suffix = ' '.join(f'{name}={value}' for name, value in fields.items())
    return (f'MP-RP-COMBAT-ACTION peer=2 eid={eid} kind={kind} phase=1 seq={seq} event_host_ms={stamp} '
            f'pose_host_ms=50 lag_ms=20 result={result} reason=- scope=7 connection=8 {suffix}').rstrip()


class CombatEvidenceTests(unittest.TestCase):
    def test_requires_native_admission_and_install_on_same_body(self):
        rows = [row(1, 22, 100, 'in'), row(1, 22, 100, 'queued'), row(1, 22, 100, 'unconfirmed'), row(1, 22, 100, 'installed', '0xCA')]
        self.assertEqual([], combat.applied_actions(rows, 2, {1}))
        self.assertEqual([], combat.applied_actions([row(1, 22, 100, 'installed')], 2, {1}))
        rows.append(row(1, 22, 100, 'installed'))
        self.assertEqual(1, len(combat.applied_actions(rows, 2, {1})))
        rows.append(row(1, 22, 100, 'installed'))
        self.assertEqual(1, len(combat.applied_actions(rows, 2, {1})))
        rows.append(row(1, 23, 200, 'in', '0xCA'))
        self.assertEqual([], combat.applied_actions(rows, 2, {1}))

    def test_fight_matches_source_timestamp_and_native_kind(self):
        staged = combat.fight_sent(['MP-ACTION section=outbound kind=Attack gen=1 ms=13 source_ms=13',
                                   'MP-ACTION section=outbound kind=BlockImpulse gen=1 row=test flags=1 source_ms=19'], False)
        self.assertEqual([(1, 13), (131, 19)], [(entry['kind'], entry['source_ms']) for entry in staged])
        native = combat.applied_actions([row(131, 1, 145, 'in'), row(131, 1, 145, 'installed')], 2, {131})
        receipt = 'MP-RP-TIMELINE-IN peer=2 kind=131 phase=1 seq=1 source_ms=19 event_host_ms=145 result=accepted'
        self.assertEqual(19, combat.select_fight(staged, [receipt], native, 2)[0]['source_ms'])
        self.assertEqual([], combat.select_fight(staged, [receipt.replace('source_ms=19', 'source_ms=13')], native, 2))

    def test_ko_receipts_match_order_and_ranged_receipts_match_exact_p6_type(self):
        rows = [row(combat.KO, 1, 120, kind, down=1) for kind in ('in', 'installed')]
        rows += [row(combat.KO, 2, 300, kind, down=0) for kind in ('in', 'installed')]
        installed = combat.applied_actions(rows, 2, {combat.KO})
        receipts = ['MP-RP-KO-IN peer=2 eid=0x1B down=1 seq=1 event_host_ms=120 result=accepted',
                    'MP-RP-KO-IN peer=2 eid=0x1B down=0 seq=2 event_host_ms=300 result=accepted']
        self.assertTrue(combat.staged_proof('rpdown', [{'down': 1}, {'down': 0}], installed, receipts, 2, None)[2])
        self.assertFalse(combat.staged_proof('rpdown', [{'down': 1}, {'down': 0}], installed, receipts[:1], 2, None)[2])
        self.assertFalse(combat.staged_proof('rpdown', [{'down': 1}, {'down': 0}, {'down': 1}], installed, receipts, 2, None)[2])
        self.assertEqual((2, [], False), combat.staged_proof('rpdown', [], [], receipts, 2, None))

        installed = combat.applied_actions([row(combat.RANGED, 7, 150, mode, weapon=2, ranged_phase=3) for mode in ('in', 'installed')], 2, {combat.RANGED})
        receipt = 'MP-RP-RANGED-IN peer=2 eid=0x1B weapon=Crossbow phase=3 seq=65543 event_host_ms=150 result=accepted'
        source = [{'seq': 65543, 'epoch': 1}]
        self.assertTrue(combat.staged_proof('rpbow', source, installed, [receipt], 2, 'crossbow')[2])
        self.assertFalse(combat.staged_proof('rpbow', source, installed, [receipt], 2, 'bow')[2])
        self.assertFalse(combat.staged_proof('rpbow', source, installed, [receipt.replace('seq=65543', 'seq=7')], 2, 'crossbow')[2])
        self.assertEqual((2, installed, True), combat.staged_proof('rpbow', source + [{'seq': 65544, 'epoch': 1}], installed,
                                                                     [receipt], 2, 'crossbow'))
        self.assertEqual((1, [], False), combat.staged_proof('rpbow', source, installed, [], 2, 'crossbow'))

    def test_p6_real_firing_log_and_scripted_fire_log_are_distinct(self):
        real = 'WO201-RANGED out firing weapon=Crossbow item=fixture seq=65543 epoch=25'
        bot = 'PEER t=15.0 ranged phase=fire weapon=Crossbow item=fixture seq=65543 epoch=25 source_ms=923'
        self.assertEqual([{'weapon': 'crossbow', 'item': 'fixture', 'seq': 65543, 'epoch': 25}], combat.fired([real, bot], False, 'crossbow'))
        self.assertEqual(combat.fired([real], False, 'crossbow'), combat.fired([bot], True, 'crossbow'))
        self.assertEqual([], combat.fired([real], True, 'crossbow'))

    def test_active_coverage_exact_threshold_and_confirmed_conflict(self):
        totals = dict.fromkeys(combat.TOTALS, 0)
        totals.update(rp_owner_n=30, rp_actions_in=20, rp_actions_played=19, rp_action_confirmed_n=19)
        self.assertEqual([], combat.health_checks(totals, 20, 19))
        self.assertEqual(['staged-native-action-coverage-below-95-percent'], combat.health_checks(totals, 20, 18))
        self.assertEqual(['duplicate-staged-native-actions'], combat.health_checks(totals, 20, 21))
        self.assertEqual(['double-damage-observed'], combat.health_checks(dict(totals, dmg_double=1), 20, 20))
        self.assertEqual(['combat-counters-unsettled'], combat.health_checks(dict(totals, rp_actions_pending=1), 20, 20))

    def test_scripts_keep_p6_weapon_subtypes_and_synthetic_reaction_separate(self):
        fixture = combat.fight_plan((0, 0, 2), (1, 15, 3), 'weapon', 'attack', 'block', 'parry', 'hit')
        self.assertIn('block_impulse parry perfect', fixture)
        self.assertIn('hit_reaction hit', fixture)
        self.assertNotIn(' npchit ', fixture)
        self.assertNotIn(' hit ', fixture.replace('hit_reaction hit', ''))
        self.assertIn('vitals 20 30 knockout', combat.down_plan((0, 0, 2)))
        self.assertIn('ranged reload', combat.bow_plan((0, 0, 2), 'bow'))
        self.assertNotIn('ranged reload', combat.bow_plan((0, 0, 2), 'crossbow'))


if __name__ == '__main__':
    unittest.main()
