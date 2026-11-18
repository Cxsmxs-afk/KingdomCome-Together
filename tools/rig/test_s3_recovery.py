# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
"""Synthetic recovery logs for the Rig parser. The game is never started here."""

from pathlib import Path
import unittest

from scen_s3_events import Scope
from scen_s3_recovery import (
    CUT, CUT_STEP, SECTIONS, cut_markers, cut_network, digest_proof, digest_stats_failures,
    disconnect_plan, recovered_joiner, reconnect_stats_failures, recovery_stats, stable_host,
)


SCOPE = Scope(8, 4, 9)
CUT_LINES = [
    'MP-WORLD-BASELINE relay world=8 gen=4 snapshot=9 target=6 result=acked',
    f'RELAY-FAULT {CUT} fired kind=cut role=joiner dir=both at={CUT_STEP} via=baseline-applied from=joiner:6 t=40s for_s=10 targets=1',
    f'RELAY-FAULT {CUT} cut id=6 role=joiner dropped=0f/0B t=40s',
    f'RELAY-FAULT {CUT} ended reason=healed kind=cut t=50s active_s=10.0 frames=0 cuts=1 refused=1 effect=1 hollow=0',
]


def phase(name='Play', *, role='joiner', world=8, gen=4):
    return f'MP-LOAD-SEQUENCE role={role} gen={gen} world={world} phase={name}'


def reconnect(result, *, world=8, gen=4, ms=0, reload=0):
    return f'MP-WORLD-RECONNECT role=joiner attempt=1 world={world} gen={gen} result={result} reconnect_ms={ms} reload={reload}'


def fixture(elapsed=10_254):
    return ['Connected! Assigned id=6 (protocol v23)', phase(), 'MP-WORLD-BASELINE role=joiner world=8 gen=4 snapshot=9 version=1 result=applied',
            reconnect('started'), phase() + ' link_hold=1', 'Connected! Assigned id=6 (protocol v23)',
            phase() + ' link_hold=0', reconnect('same-world', ms=elapsed)]


def digest(state, rev, *, section='loot', snapshot=9):
    return f'MP-WORLD-DIGEST role=joiner world=8 gen=4 snapshot={snapshot} section={section} rev={rev} result={state}'


def counts(run=1):
    result = {'recovery_world': 8, 'recovery_gen': 4, 'recovery_run': run}
    for section in SECTIONS:
        result.update({f'{prefix}{section}': 0 for prefix in ('digest_mismatch_', 'resync_', 'resync_failed_')})
    return result


class RecoveryRigTests(unittest.TestCase):
    def test_plan_contains_exact_cut_and_preserves_poor_lag(self):
        poor = Path(__file__).with_name('netz-schlecht.json')
        normal_rules = disconnect_plan('normal', poor)['rules']
        self.assertEqual([{'name': CUT, 'kind': 'cut', 'role': 'joiner', 'dir': 'both',
                           'at': CUT_STEP, 'for_s': 10}], normal_rules)
        poor_rules = disconnect_plan('schlecht', poor)['rules']
        self.assertEqual((150, 35), (poor_rules[0]['ms'], poor_rules[0]['jitter_ms']))
        self.assertEqual(normal_rules[0], poor_rules[1])

    def test_cut_requires_observed_end_and_correct_connection(self):
        self.assertEqual(6, cut_markers(CUT_LINES)['peer'])
        self.assertIsNone(cut_markers(CUT_LINES[:-1]))
        self.assertIsNone(cut_markers(CUT_LINES[:-1] + [CUT_LINES[-1].replace('hollow=0', 'hollow=1')]))
        self.assertIsNone(cut_markers(CUT_LINES[:-1] + [CUT_LINES[-1].replace('10.0', '9.9')]))
        wrong = [line.replace('from=joiner:6', 'from=joiner:7') for line in CUT_LINES]
        self.assertIsNone(cut_markers(wrong))
        self.assertEqual([], cut_network(CUT_LINES, 'normal')['failures'])
        self.assertIn('lag-and-jitter-unmeasured', cut_network(CUT_LINES, 'schlecht')['failures'])

    def test_reuse_of_same_peer_number_is_a_fresh_authenticated_connection(self):
        result = recovered_joiner(fixture(), CUT_LINES, cut_markers(CUT_LINES))
        self.assertEqual([], result['failures'])
        self.assertEqual((8, 4, 10_254), (result['world'], result['generation'], result['reconnect_ms']))
        self.assertEqual([], recovered_joiner(fixture(30_000), CUT_LINES, cut_markers(CUT_LINES))['failures'])
        self.assertIn('reconnect-outside-30s', recovered_joiner(fixture(30_001), CUT_LINES, cut_markers(CUT_LINES))['failures'])

    def test_wrong_scope_reload_or_delayed_baseline_ack_cannot_pass(self):
        cut = cut_markers(CUT_LINES)
        for terminal in (reconnect('same-world', world=9, ms=12_000), reconnect('same-world', gen=5, ms=12_000)):
            lines = fixture()[:-1] + [terminal]
            self.assertIn('reconnect-world-or-generation-changed', recovered_joiner(lines, CUT_LINES, cut)['failures'])
        lines = fixture()[:-1] + [phase('Load', gen=5), reconnect('same-world', ms=12_000, reload=1)]
        result = recovered_joiner(lines, CUT_LINES, cut)
        self.assertIn('engine-reload-or-unresolved-reconnect', result['failures'])
        self.assertIn('joiner-left-play-or-changed-generation', result['failures'])
        self.assertIn('pre-cut-applied-baseline-unconfirmed',
                      recovered_joiner(fixture(), CUT_LINES[1:] + CUT_LINES[:1], cut)['failures'])

    def test_host_stays_on_its_own_generation_of_the_same_world(self):
        lines = ['Connected! Assigned id=2 (protocol v23)', phase(role='host', gen=1)]
        self.assertEqual({'world': 8, 'generation': 1, 'peer': 2}, stable_host(lines, 8))
        self.assertIsNone(stable_host(lines + [phase('Load', role='host', gen=2)], 8))
        self.assertIsNone(stable_host(lines + [lines[0]], 8))

    def test_target_repair_preserves_causal_order_and_exact_revision(self):
        lines = [digest('corrupted', 0), digest('mismatch', 13), digest('repaired', 13), digest('checked', 13),
                 digest('checked', 14, section='npc'), digest('repaired', 13, section='corpse', snapshot=8)]
        result = digest_proof(lines, SCOPE, 'loot')
        self.assertEqual([], result['failures'])
        self.assertEqual(['npc'], result['other_checked_sections'])
        self.assertIn('selected-test-corruption-unconfirmed', digest_proof(lines[1:], SCOPE, 'loot')['failures'])
        self.assertIn('matching-digest-after-repair-unconfirmed',
                      digest_proof(lines[:3], SCOPE, 'loot')['failures'])
        self.assertIn('cross-section-repair',
                      digest_proof(lines + [digest('repaired', 14, section='corpse')], SCOPE, 'loot')['failures'])
        self.assertIn('one-matching-section-repair-unconfirmed',
                      digest_proof(lines[:2] + [digest('repaired', 15)] + lines[3:], SCOPE, 'loot')['failures'])

    def test_live_section_repair_can_publish_a_newer_absolute_revision(self):
        lines = [digest(state, rev, section='clock') for state, rev in
                 (('corrupted', 0), ('mismatch', 13), ('repaired', 15), ('checked', 15))]
        self.assertEqual([], digest_proof(lines, SCOPE, 'clock')['failures'])
        stale = lines[:-1] + [digest('checked', 14, section='clock')]
        self.assertIn('matching-digest-after-repair-unconfirmed', digest_proof(stale, SCOPE, 'clock')['failures'])

    def test_counters_are_separate_by_scope_run_and_section(self):
        before = counts()
        after = {**before, 'digest_mismatch_loot': 1, 'resync_loot': 1}
        self.assertEqual([], digest_stats_failures(after, before, 'loot'))
        self.assertIn('unexpected-digest-or-repair-counters', digest_stats_failures({**after, 'resync_body': 1}, before, 'loot'))
        self.assertIn('scoped-digest-counters-unconfirmed', digest_stats_failures({**after, 'recovery_run': 2}, before, 'loot'))
        stats = {**after, 'reconnect_ms': 10_254, 'reconnect_success': 1, 'reconnect_same_world': 1,
                 'reconnect_timeout': 0, 'reconnect_reload': 0}
        prefix = 'MP-T3-STATS origin=agent role=joiner section=recovery '
        observed = recovery_stats([prefix + ' '.join(f'{k}={v}' for k, v in stats.items())], (8, 4))
        self.assertEqual([], reconnect_stats_failures(observed, 10_254))
        self.assertTrue(reconnect_stats_failures(observed, 10_255))
        self.assertEqual({}, recovery_stats([prefix + ' '.join(f'{k}={v}' for k, v in stats.items())], (8, 5)))


if __name__ == '__main__':
    unittest.main()
