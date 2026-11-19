# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

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

    def test_regular_exercise_fails_on_fresh_provider_unavailable_for_both_receivers(self):
        class Copy:
            def __init__(self, index, log, native):
                self.id = str(index)
                self.alog = 'agent'
                self.port = 1
                self.agent = [f'Connected! Assigned id={index} (protocol 10)', *log]
                self.native = native

            def ask(self, *_): return 'on'
            def pos(self): return (0.0, 0.0, 0.0)
            def size(self, _): return 1
            def lines(self, path, offset): return (self.agent if path == 'agent' else self.native)[offset:]

        healthy = dict.fromkeys(lane.combat.TOTALS, 0)
        healthy.update(rp_owner_n=5, rp_action_confirmed_n=5, rp_actions_played=5, rp_actions_in=5)
        baseline = {role: dict(healthy, **{lane.MELEE_UNAVAILABLE: 0}) for role in ('host', 'joiner')}
        unavailable = ('MP-RP-MELEE-ROW peer=2 kind=Attack seq=9 source_ms=88 table=combat_action_attack '
                       'result=unavailable reason=typed-provider-unavailable')
        io = SimpleNamespace(pair=SimpleNamespace(state=lambda: {}, POOR_NETWORK='', collect=lambda *_a, **_k: {}),
                             procs=SimpleNamespace(matches=lambda _: True), rig=SimpleNamespace(say=lambda *_: None), Copy=Copy)
        args = SimpleNamespace(netz='normal', bandit='bandit', expect_unavailable=False)
        for sender, receiver in (('host', 'joiner'), ('joiner', 'host')):
            with self.subTest(receiver=receiver), tempfile.TemporaryDirectory() as path:
                totals = {role: dict(row) for role, row in baseline.items()}
                totals[receiver]['dmg_double_blocked'] = 1
                totals[receiver][lane.MELEE_UNAVAILABLE] = 1
                remote = Copy(1, [*self.agent, unavailable], ['ignored', *self.native])
                source = Copy(2, [], [])
                pair = dict(a=source, b=remote) if sender == 'host' else dict(a=remote, b=source)
                with patch.object(lane, 'network_evidence', return_value={}), \
                     patch.object(lane, 'mark_native', return_value={'native': 1}), \
                     patch.object(lane, 'fresh_stats', side_effect=[baseline, totals]), \
                     patch.object(lane.combat, 'player_fight', return_value=[dict(self.stages[0], nearby=True)]), \
                     patch.object(lane, 'recoil_stage', return_value={'station': 'recoil', 'required_kinds': [], 'source_actions': []}):
                    ok, why, _ = lane.exercise(io, SimpleNamespace(dir=path, **pair), args, sender)
                self.assertFalse(ok)
                self.assertEqual('melee-presentation-unavailable', why)

    def test_negative_unavailable_requires_one_real_receipt_and_rejects_fallback(self):
        stage = {'source_actions': [{'kind': 1, 'source_ms': 10}]}
        marker = ('MP-RP-MELEE-ROW peer=2 kind=Attack seq=4 source_ms=10 table=combat_action_attack '
                  'result=unavailable reason=typed-provider-unavailable')
        submit = ('MP-RP-TIMELINE-IN peer=2 kind=1 phase=1 seq=4 source_ms=10 event_host_ms=115 '
                  'result=unavailable reason=typed-provider-unavailable')
        receipts = lane.unavailable_receipts([marker, submit, marker.replace('peer=2', 'peer=3')], 2)
        self.assertEqual(1, len(receipts))
        observed = lane.negative_unavailable_evidence([stage], receipts, [marker, submit], [['FRAME']], 2, True)
        self.assertEqual(('green', 1), (observed['status'], observed['count']))
        unknown = lane.negative_unavailable_evidence([stage], receipts, [marker], [], 2, True)
        self.assertEqual('grey', unknown['status'])
        fallback = 'MP-ACTION section=inbound ghost=2 kind=Attack seq=4 dispatch=swing-queued rsid=5'
        self.assertEqual('red', lane.negative_unavailable_evidence([stage], receipts, [marker, fallback], [['FRAME']], 2, True)['status'])
        native = 'MP-RP-COMBAT-ACTION peer=2 eid=0x1B kind=1 seq=4 event_host_ms=115 result=installed'
        self.assertEqual('red', lane.negative_unavailable_evidence([stage], receipts, [marker], [[native]], 2, True)['status'])

    def test_live_vitals_require_fresh_identity_and_keep_raw_values(self):
        before = 'scope=7 phase=before target=partner eid=0x1B hp=85.5 st=42.5 dead=0 ko=0 game_s=10 x=0 y=1 z=2'
        after = 'scope=7 phase=after target=partner eid=0x1B hp=80.0 st=18.5 dead=0 ko=0 game_s=10.5 x=0 y=1 z=2'
        first = lane.parse_vitals(before, '7', 'before', 'partner')
        second = lane.parse_vitals(after, '7', 'after', 'partner')
        self.assertEqual(before, first['raw'])
        self.assertEqual({'partner': dict(hp=5.5, st=24.0, window_game_s=0.5)},
                         lane.window_deltas({'partner': first}, {'partner': second}))
        self.assertEqual({}, lane.window_deltas({'partner': first}, {'partner': dict(second, eid=5)}))
        self.assertEqual({}, lane.window_deltas({'partner': first}, {'partner': dict(second, game_s=9.0)}))
        self.assertIsNone(lane.parse_vitals(after, '8', 'after', 'partner'))
        self.assertIsNone(lane.parse_vitals(after.replace('hp=80.0', 'hp=nan'), '7', 'after', 'partner'))

    def test_live_contact_separates_protected_zero_from_damage_apply(self):
        sender = 'MP-FF dir=out victim_eid=0x31 ghost=1 hp=4.00 st=5.00 unarmed=0 missile=0 session=on result=sent'
        receiver = 'MP-FF dir=in attacker=2 victim=1 hp=4.00 st=5.00 unarmed=0 missile=0 material=0 session=on result=applied reason=ok'
        sent, received = lane.partner_contacts([sender, sender.replace('ghost=1', 'ghost=4')], [receiver], 2, 1)
        self.assertEqual(([sender], [receiver]), (sent, received))
        guard = 'T3-RECOVERY hit=Pvp decision=protected_consumed applied_hp=0 applied_st=0 peer=2'
        applied = 'WO121-HITS friendly-fire hit on the player: hp -4.00 st -5.00 unarmed=0 from ghost 2 -> applied'
        for result in ('applied', 'acknowledged'):
            reply = receiver.replace('result=applied', f'result={result}')
            sent, received = lane.partner_contacts([sender], [reply], 2, 1)
            self.assertEqual(([sender], [reply]), (sent, received))
            self.assertEqual('partner-damage-apply-unmeasured', lane.case_signal('partner-hit', {}, {}, sent, received, [], [], [], 2))
            self.assertEqual('partner-application-observed', lane.case_signal('partner-hit', {}, {}, sent, received, [], [], [applied], 2))
        protected_reply = receiver.replace('result=applied', 'result=protected_consumed')
        sent, received = lane.partner_contacts([sender], [protected_reply], 2, 1)
        self.assertEqual(([sender], [protected_reply]), (sent, received))
        self.assertEqual('protected-consumed-zero', lane.case_signal('protected', {}, {}, sent, received, [guard], [], [], 2))
        self.assertEqual('protection-unmeasured', lane.case_signal('protected', {}, {}, sent, received, [guard], [], [applied], 2))
        self.assertEqual('protected-instead-of-applied', lane.case_signal('partner-hit', {}, {}, sent, received, [], [], [], 2))

    def test_npc_contact_uses_the_hosts_engine_and_target_identity(self):
        local = 'WO176-HOSTHIT victim=0x1B outcome=blocked rec=[blk 1 second 0 pb 0 broken 0]'
        remote = 'MP-W173 hit npc=bandit by_ghost=2 applied=engine verdict=hit reaction=engine seq=8'
        self.assertEqual([('blocked', local)], lane.authority_contacts('host', 1, 'bandit', 0x1B, [remote], [local]))
        self.assertEqual([], lane.authority_contacts('host', 1, 'bandit', 0x1C, [remote], [local]))
        self.assertEqual([('hit', remote)], lane.authority_contacts('joiner', 2, 'bandit', 0x1B, [remote], [local]))
        self.assertEqual([], lane.authority_contacts('joiner', 3, 'bandit', 0x1B, [remote], [local]))


if __name__ == '__main__':
    unittest.main()
