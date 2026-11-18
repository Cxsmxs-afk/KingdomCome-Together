# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
"""Synthetic per-role transcripts and collector paths. No game is opened."""

import json
from pathlib import Path
import tempfile
import unittest
from uuid import UUID

from scen_guided_run import EVIDENCE, STATIONS, collected_bundle, json_receipt, local_verdict, transcript

IDENTITY = '0123456789abcdef0123456789abcdef'
OTHER = '110123456789abcdef0123456789abcd'
SCOPE = (17, 2)


def marker(kind, more='', role='host', run=IDENTITY):
    return f'MP-TESTLAUF role={role} run={run} result={kind} {more}'.rstrip()


def events(role='host', answers=None):
    if answers is None:
        answers = ['ManualYes'] * 5 + ['Observed', 'Observed']
    result = [marker('started', 'world=17 gen=2', role)]
    for index, (station, evidence) in enumerate(zip(STATIONS, answers)):
        world, gen = SCOPE if index < 5 else (18, 3)
        result.append(marker('station', f'station={station} evidence={evidence} world={world} gen={gen}', role))
    return result + [marker('complete', 'stations=7', role), marker('collected', role=role)]


def bundle_receipt(proof):
    return {'runId': str(UUID(hex=proof['run'])), 'role': proof['role'],
            'startWorld': proof['start'][0], 'startGeneration': proof['start'][1],
            'stations': [{'station': row['station'].lower(), 'evidence': row['evidence'][0].lower() + row['evidence'][1:],
                          'world': row['scope'][0], 'generation': row['scope'][1],
                          'baselineSnapshot': 5 if row['station'] == 'Reload' else None,
                          'reconnectMs': 10_640 if row['station'] == 'Disconnect' else None}
                         for row in proof['stations']]}


def verdict(role, status):
    return '\n'.join(('KCDMP-ERGEBNIS\t1', f'Gesamt\t{status}', f'Rolle\t{role}',
                      'Regel\tStatus\tMesswert\tBeleg',
                      f'guided_run_protocol\t{status}\t7/7 Stationen protokolliert\ttestlauf-beleg.txt'))


class GuidedRigTests(unittest.TestCase):
    def test_all_seven_stations_match_role_scope_and_terminal_markers(self):
        for role in ('host', 'joiner'):
            result = transcript(events(role), role, SCOPE)
            self.assertEqual('collected', result['status'])
            self.assertEqual(IDENTITY, result['run'])
            self.assertEqual(list(STATIONS), [row['station'] for row in result['stations']])
            self.assertTrue(all(row['evidence'] in EVIDENCE for row in result['stations']))

    def test_fresh_start_is_required_and_a_different_runs_export_cannot_finish(self):
        self.assertEqual('waiting-start', transcript(events()[1:], 'host', SCOPE)['status'])
        altered = events()[:-1] + [marker('collected', run=OTHER)]
        self.assertEqual('completed', transcript(altered, 'host', SCOPE)['status'])
        altered = events() + [marker('started', 'world=17 gen=2', run=OTHER)]
        self.assertEqual('failed', transcript(altered, 'host', SCOPE)['status'])
        self.assertEqual('failed', transcript(events()[:-2] + [events()[-1]], 'host', SCOPE)['status'])

    def test_wrong_station_order_or_mismatched_scope_fails_closed(self):
        logs = events()
        wrong_station = logs[:1] + [logs[2], logs[1]] + logs[3:]
        self.assertEqual('failed', transcript(wrong_station, 'host', SCOPE)['status'])
        wrong_world = logs[:-3] + [logs[-3].replace('world=18', 'world=20')] + logs[-2:]
        self.assertEqual('failed', transcript(wrong_world, 'host', SCOPE)['status'])
        self.assertEqual('failed', transcript(logs, 'host', (17, 8))['status'])
        self.assertEqual('failed', transcript(logs[:-1] + [marker('collection-failed')], 'host', SCOPE)['status'])

    def test_export_requires_fresh_report_t1_scope_and_a_real_part(self):
        proof = transcript(events(), 'host', SCOPE)
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            result = root / f'testlauf-20261118-190000-host-{IDENTITY}'
            result.mkdir()
            (result / 'TESTLAUF.json').write_text(json.dumps(bundle_receipt(proof)), encoding='utf-8')
            (result / 'ERGEBNIS.txt').write_text(verdict('host', 'GRÜN'), encoding='utf-8')
            with self.assertRaisesRegex(ValueError, 'kein gepacktes'):
                collected_bundle(root, proof)
            (result / 'bundle.tzst').write_bytes(b'synthetic-fixture')
            self.assertEqual(['bundle.tzst'], collected_bundle(root, proof)['parts'])
            (result / 'ERGEBNIS.txt').write_text(verdict('joiner', 'GRÜN'), encoding='utf-8')
            with self.assertRaisesRegex(ValueError, 'lokale T1'):
                collected_bundle(root, proof)

    def test_collector_cannot_render_a_manual_no_as_green_or_claim_fast_reconnect_without_timing(self):
        proof = transcript(events(answers=['ManualNo'] + ['ManualYes'] * 4 + ['Observed'] * 2), 'host', SCOPE)
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp, 'ERGEBNIS.txt')
            path.write_text(verdict('host', 'GRÜN'), encoding='utf-8')
            with self.assertRaises(ValueError):
                local_verdict(path, proof)
            path.write_text(verdict('host', 'ROT'), encoding='utf-8')
            self.assertEqual('ROT', local_verdict(path, proof)['guided_run_protocol'])
            data = bundle_receipt(proof)
            data['stations'][-1]['reconnectMs'] = 30_001
            path.write_text(json.dumps(data), encoding='utf-8')
            with self.assertRaisesRegex(ValueError, 'Messfenster'):
                json_receipt(path, proof)


if __name__ == '__main__':
    unittest.main()
