# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
import unittest

import scen_s2_owner as owner


def hold(peer, frm, to, frame, eid='001B'):
    return f'[12:00:00.000] WO221-HOLD npc=kcd2mp_{peer} {frm} -> {to} (held 1.2 s) eid=0x{eid} frame={frame}'


TRANSFERS = [hold(7, 'writer', 'seat', 12), hold(7, 'seat', 'writer', 28),
             hold(7, 'writer', 'fall', 43), hold(7, 'fall', 'writer', 95)]


class OwnerEvidenceTests(unittest.TestCase):
    def test_requires_receiver_transfers_in_order_for_one_partner_body(self):
        other = hold(4, 'writer', 'seat', 8)
        rows = [other, *TRANSFERS, hold(4, 'seat', 'writer', 99)]
        transfers = owner.handovers(rows, 7, owner.STAGES)
        self.assertEqual(['sit', 'leave-seat', 'fall', 'stand'], [value.stage for value in transfers])
        self.assertEqual([27] * 4, [value.body for value in transfers])
        self.assertEqual([], owner.handovers(rows + [hold(7, 'writer', 'fall', 102, '002A')], 7, owner.STAGES))
        self.assertEqual([], owner.handovers(rows + [hold(7, 'writer', 'mount', 2)], 7, owner.STAGES))
        self.assertEqual(1, len(owner.handovers(reversed(TRANSFERS), 7, owner.STAGES)))
        self.assertEqual(1, len(owner.handovers([TRANSFERS[0], hold(7, 'seat', 'writer', 12)], 7, owner.STAGES)))
        self.assertEqual([], owner.handovers(['BOT-T4 role=joiner complete=1 poses=70'], 7, owner.STAGES))

    def test_distinguishes_zero_violations_from_no_setter_coverage(self):
        before = {'rp_owner_n': 40, 'owner_violations': 0, 'owner_handovers': 2, 'owner_denied_sets': 1}
        after = {'rp_owner_n': 70, 'owner_violations': 0, 'owner_handovers': 6, 'owner_denied_sets': 2}
        transfers = owner.handovers(TRANSFERS, 7, owner.STAGES)
        self.assertEqual('ok', owner.owner_result(before, after, owner.STAGES, transfers)[0])
        self.assertEqual('owner-violations', owner.owner_result(before, dict(after, owner_violations=1), owner.STAGES, transfers)[0])
        self.assertEqual('owner-counters-unmeasured', owner.owner_result(before, {}, owner.STAGES, transfers)[0])
        self.assertEqual('owner-counters-reset', owner.owner_result(after, before, owner.STAGES, transfers)[0])
        self.assertEqual('no-new-owner-checks', owner.owner_result(before, before, owner.STAGES, transfers)[0])
        self.assertEqual('missing-figure-handover', owner.owner_result(before, after, owner.STAGES, transfers[:-1])[0])

    def test_selects_only_complete_t3_stats_for_receiver_role(self):
        full = 'rp_owner_n=3 owner_violations=0 owner_handovers=2 owner_denied_sets=1'
        rows = ['MP-T3-STATS origin=agent role=host ' + full,
                'MP-T3-STATS origin=agent role=joiner owner_violations=0',
                'MP-T3-STATS origin=dll role=joiner ' + full]
        self.assertEqual(3, owner.owner_stats(rows, 'host')['rp_owner_n'])
        self.assertEqual({}, owner.owner_stats(rows, 'joiner'))

    def test_t4_script_encodes_real_object_ids_and_optional_stages(self):
        plan = owner.bot_scenario((1.0, 2.0, 3.0), '0123456789ABCDEF', 'horse[rig]', True)
        self.assertIn('at 7 activity stance 3 0123456789ABCDEF\n', plan)
        self.assertIn('at 15 activity none\n', plan)
        self.assertIn('at 25 vitals 20 30 knockeddown\n', plan)
        self.assertIn('at 34 state downed=0 combat=0\n', plan)
        self.assertIn('at 42 ride horse[rig]\nat 52 ride off\n', plan)
        self.assertTrue(plan.endswith('at 63 death\nend 71\n'))
        plan = owner.bot_scenario((1, 2, 3), '0123456789ABCDEF', None, False)
        self.assertNotIn(' ride ', plan)
        self.assertTrue(plan.endswith('end 45\n'))


if __name__ == '__main__':
    unittest.main()
