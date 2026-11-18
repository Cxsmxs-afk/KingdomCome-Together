# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
"""Portable checks of cohort selection and evidence rules. These tests do not start games."""

from contextlib import redirect_stderr
import io
import json
import os
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch
from xml.sax.saxutils import quoteattr

from scen_solo_abend import (
    ENVIRONMENT, REPLAY_CASES, STATIONS, current_games, fresh_report, missing_prerequisite, read_report, record,
    replay_counts, run, run_station, smoke, station_arguments, summary_code, write_report,
)


def settings(**overrides):
    return SimpleNamespace(**{'netz': 'schlecht', 'route_measure': None, 'route_walk': None, 'bandit': None,
                              'playline': 4, 'save': 'solo_host', 'weapon': 'crossbow', 'unattended': False, **overrides})


def station(name):
    return next(item for item in STATIONS if item.name == name)


def fake_pair(*, both=True, safe=True):
    def state():
        return {'a': 1, 'agent_a': 1, 'b': both, 'agent_b': both, 'relay': 1}

    return SimpleNamespace(pair=SimpleNamespace(HOST_CID='A', JOIN_CID='B', state=state, solo_guard=Mock(),
                                              user_folder_refusal=Mock(return_value=None if safe else 'real profile')),
                           procs=SimpleNamespace(matches=bool))


class SoloAbendTests(unittest.TestCase):
    def test_every_live_scenario_is_listed_and_mutating_cohorts_are_separate(self):
        expected = {'smoke', 'rpmeasure', 'ownerhandover', 'lanes', 'timeline', 'rpwalk', 'rpstance', 'rpfight',
                    'rpdown', 'rpbow', 'pickup', 'corpse', 'dialog', 'desync', 'verdict', 'reload',
                    'weatherload', 'disconnect', 'botjoiner'}
        self.assertEqual(expected, {item.name for item in STATIONS})
        self.assertEqual(len(expected), len(STATIONS))
        for name in ('reload', 'weatherload', 'disconnect'):
            self.assertEqual(name, station(name).cohort)
        self.assertEqual('bot', station('botjoiner').cohort)
        self.assertEqual('ci-only', fresh_report('normal')['replay']['status'])

    def test_only_expected_arguments_reach_prepared_stations(self):
        args = settings(route_measure=Path('stair route.json'), bandit='test_bandit')
        self.assertEqual(['--test-rpmeasure', '--netz', 'schlecht', '--route', 'stair route.json', '--spieler'],
                         station_arguments(station('rpmeasure'), args))
        self.assertEqual(['--test-world-recovery', '--netz', 'schlecht'], station_arguments(station('disconnect'), args))
        self.assertEqual(['--playline', '4', '--save', 'solo_host'], station_arguments(station('weatherload'), args)[-4:])
        self.assertEqual(['--sender', 'both'], station_arguments(station('rpdown'), args)[-2:])

    def test_fixture_and_operator_skips_are_not_passes(self):
        args = settings()
        self.assertEqual('missing-route-measure', missing_prerequisite(station('rpmeasure'), args, {}, {}))
        args = settings(route_measure=Path('route.json'))
        self.assertIn('test-environment-missing', missing_prerequisite(station('rpmeasure'), args, {}, {}))
        env = {'KCDMP_TEST_RP_MEASURE': '1'}
        self.assertIn('Agent-and-DLL', missing_prerequisite(station('rpmeasure'), args, env, {}))
        self.assertIsNone(missing_prerequisite(station('rpmeasure'), args, env, env))
        self.assertEqual('operator-not-present', missing_prerequisite(station('ownerhandover'), settings(unattended=True), {}, {}))
        self.assertIsNone(missing_prerequisite(station('smoke'), settings(unattended=True), {}, {}))

    def test_preflight_protects_game_profiles_ownership_and_single_bot_peer(self):
        S = fake_pair()
        self.assertEqual(1, current_games(S, 'pair'))
        S.pair.solo_guard.assert_called_once()
        with self.assertRaisesRegex(RuntimeError, 'Joiner-Kopie'):
            current_games(S, 'bot')
        self.assertEqual(1, current_games(fake_pair(both=False), 'bot'))
        with self.assertRaisesRegex(RuntimeError, 'isoliertes Spielprofil'):
            current_games(fake_pair(safe=False), 'pair')

    def test_smoke_calls_the_known_direct_plan_and_keeps_partial_artifact_non_passing(self):
        probe = SimpleNamespace(step=Mock(return_value=True), finish=Mock(return_value=2))
        S = SimpleNamespace(Run=Mock(return_value=probe), run=Mock(side_effect=AssertionError),
                            smoke_plan=Mock(return_value=[(name, Mock()) for name in ('join', 'walk30', 'sleep', 'wait')]))
        with patch.dict(os.environ, {'KCDMP_RIG_RESEARCH': ''}):
            self.assertEqual((2, ['sleep', 'wait']), smoke(S, {}))
        self.assertEqual(['join', 'walk30'], [call.args[0] for call in probe.step.call_args_list])
        probe.finish.assert_called_once_with(skipped=['sleep', 'wait'])
        S.run.assert_not_called()

    def test_result_is_merged_and_needs_t1_report_before_overall_pass(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp, 'abend.json')
            result = read_report(path, 'normal')
            self.assertEqual(2, summary_code(result))
            record(result, {'name': 'reload', 'cohort': 'reload', 'status': 'pass'})
            write_report(path, result)
            reloaded = read_report(path, 'normal')
            self.assertEqual('pass', next(row for row in reloaded['stations'] if row['name'] == 'reload')['status'])
            self.assertEqual(2, summary_code(reloaded))
            for row in reloaded['stations']:
                row['status'] = 'pass'
            self.assertEqual(2, summary_code(reloaded))
            reloaded['paired_verdict'] = {'status': 'available'}
            self.assertEqual(0, summary_code(reloaded))
            reloaded['replay']['status'] = 'fail'
            self.assertEqual(1, summary_code(reloaded))
            with self.assertRaises(ValueError):
                read_report(path, 'schlecht')

    def test_return_zero_on_an_old_artifact_never_passes(self):
        with tempfile.TemporaryDirectory() as temp:
            folder = Path(temp)
            child = folder / 'scenario-lanes-previous'
            child.mkdir()
            (child / 'result.json').write_text(json.dumps({'scenario': 'lanes', 'pass': True}), encoding='utf-8')
            S = fake_pair()
            S.run = Mock(return_value=0)
            env = {'KCDMP_TEST_LANES': '1'}
            with patch.dict(os.environ, env):
                row = run_station(S, station('lanes'), settings(), 1, env, folder)
                self.assertEqual('fresh-passing-result-missing', row['reason'])
                expected = folder / 'scenario-lanes-new'
                expected.mkdir()
                artifact = expected / 'result.json'

                def emit_result(*unused):
                    artifact.write_text(json.dumps({'scenario': 'lanes', 'pass': True}), encoding='utf-8')
                    return 0

                S.run.side_effect = emit_result
                row = run_station(S, station('lanes'), settings(), 1, env, folder)
                self.assertEqual('pass', row['status'])
                self.assertEqual([str(artifact)], row['evidence'])

    def test_summary_can_use_explicit_report_without_touching_runtime_and_gate_requires_both(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp, 'abend.json')
            S = SimpleNamespace(rig=SimpleNamespace(run_dir=Mock(side_effect=AssertionError), say=Mock()))
            argv = ['--cohort', 'summary', '--output', str(output)]
            with patch.dict(os.environ, {ENVIRONMENT: ''}), redirect_stderr(io.StringIO()):
                with self.assertRaises(SystemExit):
                    run(S, argv + ['--test-solo-abend'])
            with patch.dict(os.environ, {ENVIRONMENT: '1'}):
                self.assertEqual(2, run(S, argv + ['--test-solo-abend']))
            self.assertTrue(output.is_file())
            S.rig.run_dir.assert_not_called()

    def test_replay_trx_counts_each_exact_named_twin_only_from_that_suite(self):
        rows = []
        for name in REPLAY_CASES:
            test_name = f'Game.ReplayScenarioTests.the_contract(name: "{name}", expectedViolation: "synthetic")'
            rows.append(f'<UnitTestResult testName={quoteattr(test_name)} outcome="Passed"/>')
        for invalid in (f'OtherSuite.the_contract(name: "{REPLAY_CASES[0]}")',
                        'Game.ReplayScenarioTests.the_contract(name: "extra-test")'):
            rows.append(f'<UnitTestResult testName={quoteattr(invalid)} outcome="Passed"/>')
        with tempfile.TemporaryDirectory() as temp:
            file = Path(temp, 'replay.trx')
            def write(items):
                results = ''.join(items)
                file.write_text(f'<Run xmlns="http://example.com/trx"><Results>{results}</Results>'
                                '<ResultSummary><Counters executed="8" passed="8" failed="0"/></ResultSummary></Run>', encoding='utf-8')
            write(rows)
            self.assertEqual(sorted(REPLAY_CASES), replay_counts([file])['passed_fixtures'])
            write(rows[1:])
            self.assertNotIn(REPLAY_CASES[0], replay_counts([file])['passed_fixtures'])


if __name__ == '__main__':
    unittest.main()
