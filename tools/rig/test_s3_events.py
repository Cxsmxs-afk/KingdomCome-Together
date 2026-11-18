# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
"""Synthetic receipt logs. These tests do not launch the game."""

import unittest

from scen_s3_events import Scope, accept_event, applied_baseline, bot_votes_proof, event_proof, loot_game_proof, relay_baseline_acked, role_stats, stats_failures, votes_proof

SCOPE = Scope(13, 7, 9)
KEY = 'world=13 gen=7 snapshot=9'
LOOT = 'channel=Loot type=0x5F source=3'
DIALOG = 'channel=Dialog type=0x63 source=3'


def applied(event_id, result='applied', *, key=KEY, event=LOOT, kind='item-result', token=42):
    return f'MP-WORLD-EVENT role=joiner {key} {event} id={event_id} kind={kind} token={token} result={result}'


def relay_sent(event_id, *, key=KEY, event=LOOT, target=6, result='sent'):
    return f'MP-WORLD-EVENT relay {key} {event} id={event_id} target={target} result={result}'


def relay_ack(through, *, key=KEY, target=6, channel='Loot'):
    return f'MP-WORLD-EVENT relay {key} channel={channel} id={through} target={target} result=acked'


def proof(agent, relay, *, case='pickup', channel='Loot', minimum=1):
    return event_proof(agent, relay, SCOPE, channel, 3, 6, minimum,
                       role='joiner', accepts=lambda fields: accept_event(case, fields))


class EventReceiptTests(unittest.TestCase):
    def test_baseline_requires_same_scope_target_and_applied_version(self):
        lines = [f'MP-WORLD-BASELINE role=joiner {KEY} version=1 result=applied']
        relay = [f'MP-WORLD-BASELINE relay {KEY} target=6 result=acked']
        self.assertEqual(SCOPE, applied_baseline(lines, 'joiner'))
        self.assertTrue(relay_baseline_acked(relay, SCOPE, 6))
        self.assertFalse(relay_baseline_acked(relay, SCOPE, 5))
        self.assertIsNone(applied_baseline([lines[0].replace('version=1', 'version=2')], 'joiner'))

    def test_legacy_send_or_wrong_source_cannot_replace_authenticated_send(self):
        game = [applied(21), applied(21, 'ack-written')]
        sent = relay_sent(21)
        self.assertEqual([], proof(game, [sent, relay_ack(21)])['failures'])
        self.assertIn('authenticated-receipt-missing',
                      proof(game, [sent.replace('source=3', 'source=9'), relay_ack(21)])['failures'])
        self.assertIn('authenticated-receipt-missing',
                      proof(game, [relay_sent(21, result='retry'), relay_ack(21)])['failures'])

    def test_ordering_requires_local_apply_before_ack_and_monotonic_ids(self):
        self.assertIn('authenticated-receipt-missing',
                      proof([applied(21, 'ack-written'), applied(21)], [relay_sent(21), relay_ack(21)])['failures'])
        game = [applied(21), applied(21), applied(21, 'ack-written')]
        self.assertIn('duplicate-or-out-of-order-game-application',
                      proof(game, [relay_sent(21), relay_ack(21)])['failures'])

    def test_cumulative_ack_must_be_from_the_same_snapshot_and_after_send(self):
        game = [applied(21), applied(21, 'ack-written'), applied(23), applied(23, 'ack-written')]
        sent = [relay_sent(21), relay_sent(23)]
        result = proof(game, sent + [relay_ack(23)], minimum=2)
        self.assertEqual([], result['failures'])
        self.assertEqual([23, 23], result['relay_ack_through'])
        for bad in (sent + [relay_ack(23, key=KEY.replace('snapshot=9', 'snapshot=8'))],
                    sent + [relay_ack(23, target=5)], [relay_ack(23)] + sent):
            self.assertIn('authenticated-receipt-missing', proof(game, bad, minimum=2)['failures'])

    def test_loot_requires_normal_confirmed_item_or_corpse_receipt(self):
        game = [applied(21), applied(21, 'ack-written')]
        result = proof(game, [relay_sent(21), relay_ack(21)])
        self.assertTrue(loot_game_proof(['WO134-ITEM ok tok=42 cls=item'], result, 'pickup'))
        self.assertFalse(loot_game_proof(['WO134-ITEM ok tok=43 cls=item'], result, 'pickup'))
        self.assertFalse(loot_game_proof(['WO134-ITEM unknown tok=42 cls=item'], result, 'pickup'))
        self.assertTrue(loot_game_proof(['[KCD2MP] w217_tack 42 sample_body applied'], result, 'corpse'))
        self.assertFalse(loot_game_proof(['[KCD2MP] w217_tack 42 sample_body cancel'], result, 'corpse'))
        self.assertIn('expected-game-application-unmeasured',
                      proof([applied(21, kind='item-gone')], [relay_sent(21), relay_ack(21)])['failures'])

    def test_dialog_requires_two_distinct_rows_on_one_live_decision(self):
        events = [applied(i, event=DIALOG, kind='vote') for i in (21, 23)]
        events += [applied(i, 'ack-written', event=DIALOG, kind='vote') for i in (21, 23)]
        result = proof(events, [relay_sent(i, event=DIALOG) for i in (21, 23)] + [relay_ack(23, channel='Dialog')],
                       case='dialog', channel='Dialog', minimum=2)
        lines = [f'MP-W204-VOTE-APPLIED peer=3 cid=8 decision=12 index={row} event_id={i} world_gen=7'
                 for row, i in enumerate((21, 23))]
        self.assertEqual([], result['failures'])
        self.assertEqual([(8, 12, 0), (8, 12, 1)], votes_proof(lines, result, SCOPE, 3))
        self.assertIsNone(votes_proof([lines[0], lines[1].replace('decision=12', 'decision=13')], result, SCOPE, 3))
        self.assertIsNone(votes_proof([lines[0], lines[1].replace('index=1', 'index=0')], result, SCOPE, 3))

    def test_t4_vote_send_must_match_receiver_transaction_tokens(self):
        votes = [(8, 12, 0), (8, 12, 1)]
        result = {'events': [{'token': '42'}, {'token': '43'}]}
        line = ('BOT-T4 role=joiner dialog_votes=sent peer=3 host=6 cid=8 decision=12 first=0 '
                'second=1 first_token=42 second_token=43')
        self.assertTrue(bot_votes_proof([line], votes, result, 3, 6))
        self.assertFalse(bot_votes_proof([line.replace('second_token=43', 'second_token=44')], votes, result, 3, 6))

    def test_fresh_scoped_stats_and_nonzero_gaps_are_not_interchangeable(self):
        base = 'MP-T3-STATS origin=agent role=joiner '
        current = base + ('section=events evt_world=13 evt_gen=7 evt_snapshot=9 '
                          'baseline_version=1 baseline_ms=100 evt_sent=1 evt_acked=1 evt_applied=1 evt_gap=0 evt_dropped=0 evt_sender_dropped=0')
        stats = role_stats([base + 'owner_violations=0', current, current.replace('evt_gen=7', 'evt_gen=8')], 'joiner', SCOPE)
        self.assertEqual([], stats_failures(stats))
        self.assertIn('evt_gap-positive', stats_failures({**stats, 'evt_gap': 1}))
        self.assertIn('current-baseline-or-event-counter-unmeasured', stats_failures(stats, 2))
        self.assertIn('zero-counters-unmeasured', stats_failures({'evt_applied': 1, 'baseline_version': 1}))


if __name__ == '__main__':
    unittest.main()
