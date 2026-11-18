# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
"""Run the explicitly selected part of a disposable local game test and retain each station's evidence."""

import argparse
from dataclasses import dataclass
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import time
from xml.etree import ElementTree

from scen_s2_measure import network_evidence
from scen_s3_recovery import disconnect_plan, has_plan

ENVIRONMENT = 'KCDMP_TEST_SOLO_ABEND'
REPORT_VERSION = 1
COHORTS = ('pair', 'reload', 'weatherload', 'disconnect', 'bot', 'summary')
REPLAY_CASES = ('weather-during-load', 'pickup-under-load', 'wolf-pack-fight', 'death-respawn',
                'knockdown', 'bow-shot', 'remote-walk-stairs', 'remote-fight-down-up')


@dataclass(frozen=True)
class Station:
    name: str
    cohort: str = 'pair'
    environment: str = ''
    flag: str = ''
    manual: bool = False
    fixture: str = ''


STATIONS = (
    Station('smoke'),
    Station('rpmeasure', environment='KCDMP_TEST_RP_MEASURE', flag='--test-rpmeasure', manual=True, fixture='route_measure'),
    Station('ownerhandover', environment='KCDMP_TEST_OWNER_HANDOVER', flag='--test-ownerhandover', manual=True),
    Station('lanes', environment='KCDMP_TEST_LANES', flag='--test-lanes'),
    Station('timeline', environment='KCDMP_TEST_TIMELINE', flag='--test-timeline', manual=True),
    Station('rpwalk', environment='KCDMP_TEST_RP_CONTROLLER', flag='--test-rp-controller', manual=True, fixture='route_walk'),
    Station('rpstance', environment='KCDMP_TEST_RP_CONTROLLER', flag='--test-rp-controller', manual=True),
    Station('rpfight', environment='KCDMP_TEST_RP_COMBAT', flag='--test-rp-combat', manual=True, fixture='bandit'),
    Station('rpdown', environment='KCDMP_TEST_RP_COMBAT', flag='--test-rp-combat', manual=True),
    Station('rpbow', environment='KCDMP_TEST_RP_COMBAT', flag='--test-rp-combat', manual=True),
    Station('pickup', environment='KCDMP_TEST_BASELINE_EVENTS', flag='--test-baseline-events', manual=True),
    Station('corpse', environment='KCDMP_TEST_BASELINE_EVENTS', flag='--test-baseline-events', manual=True),
    Station('dialog', environment='KCDMP_TEST_BASELINE_EVENTS', flag='--test-baseline-events', manual=True),
    Station('desync', environment='KCDMP_TEST_WORLD_RECOVERY', flag='--test-world-recovery'),
    Station('verdict'),
    Station('reload', 'reload', 'KCDMP_TEST_LOAD_SEQUENCE', '--test-load-sequence'),
    Station('weatherload', 'weatherload', 'KCDMP_TEST_LOAD_SEQUENCE', '--test-load-sequence'),
    Station('disconnect', 'disconnect', 'KCDMP_TEST_WORLD_RECOVERY', '--test-world-recovery'),
    Station('botjoiner', 'bot', 'KCDMP_TEST_BOT'),
)


def station_arguments(station, args):
    arguments = [station.flag, '--netz', args.netz] if station.flag else []
    if station.name == 'rpmeasure':
        arguments += ['--route', str(args.route_measure), '--spieler']
    elif station.name == 'rpwalk':
        arguments += ['--route', str(args.route_walk)]
    elif station.name == 'rpfight':
        arguments += ['--bandit', args.bandit]
    elif station.name == 'rpdown':
        arguments += ['--sender', 'both']
    elif station.name == 'rpbow':
        arguments += ['--weapon', args.weapon]
    elif station.cohort in ('reload', 'weatherload'):
        arguments += ['--playline', str(args.playline), '--save', args.save]
    return arguments


def missing_prerequisite(station, args, env, forwarded):
    if station.manual and args.unattended:
        return 'operator-not-present'
    if station.fixture and not getattr(args, station.fixture):
        return 'missing-' + station.fixture.replace('_', '-')
    if station.environment and env.get(station.environment) != '1':
        return 'test-environment-missing-' + station.environment
    if station.name == 'desync' and (forwarded.get(station.environment) != '1'
                                    or env.get('KCDMP_RIG_TEST_WORLD_RECOVERY') != '1'):
        return 'Agent-recovery-opt-in-missing'
    if station.name in ('rpmeasure', 'lanes') and forwarded.get(station.environment) != '1':
        return 'Agent-and-DLL-test-environment-missing-' + station.environment
    return None


def empty_row(station):
    return {'name': station.name, 'cohort': station.cohort, 'status': 'skip', 'reason': 'cohort-not-run'}


def fresh_report(profile):
    return {'schema': REPORT_VERSION, 'scenario': 'abend', 'netz': profile,
            'stations': [empty_row(station) for station in STATIONS],
            'replay': {'status': 'ci-only', 'fixtures': list(REPLAY_CASES)}}


def read_report(path, profile):
    if not path.exists():
        return fresh_report(profile)
    report = json.loads(path.read_text(encoding='utf-8'))
    if (not isinstance(report, dict) or report.get('schema') != REPORT_VERSION or report.get('scenario') != 'abend'
            or report.get('netz') != profile
            or not isinstance(report.get('stations'), list)):
        raise ValueError('the report belongs to another scenario or network profile')
    names = [(row.get('name'), row.get('cohort')) for row in report['stations'] if isinstance(row, dict)]
    if (names != [(station.name, station.cohort) for station in STATIONS]
            or any(row.get('status') not in ('pass', 'fail', 'skip') for row in report['stations'])):
        raise ValueError('the report does not contain the current station inventory')
    return report


def write_report(path, report):
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(mode='w', encoding='utf-8', dir=path.parent, prefix='.abend-', delete=False) as stream:
        temporary = Path(stream.name)
        try:
            json.dump(report, stream, indent=2, ensure_ascii=False)
            stream.write('\n')
        except Exception:
            stream.close()
            temporary.unlink(missing_ok=True)
            raise
    try:
        temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)


def record(report, row):
    for i, existing in enumerate(report['stations']):
        if existing['name'] == row['name']:
            report['stations'][i] = row
            return
    raise ValueError('unknown station ' + row['name'])


def result_counts(report):
    return {status: sum(row['status'] == status for row in report['stations'])
            for status in ('pass', 'fail', 'skip')}


def summary_code(report):
    counts = result_counts(report)
    if counts['fail'] or report.get('replay', {}).get('status') == 'fail':
        return 1
    if counts['skip'] or report.get('paired_verdict', {}).get('status') != 'available':
        return 2
    return 0


def current_games(S, cohort):
    S.pair.solo_guard()
    state = S.pair.state()
    pairs = (S.pair.HOST_CID,) if cohort == 'bot' else (S.pair.HOST_CID, S.pair.JOIN_CID)
    for cid in pairs:
        if not S.procs.matches(state.get(cid.lower())) or not S.procs.matches(state.get('agent_' + cid.lower())):
            raise RuntimeError(f'Kopie {cid} und ihr Agent müssen im Rig laufen.')
        reason = S.pair.user_folder_refusal(cid)
        if reason:
            raise RuntimeError(f'Kopie {cid} hat kein isoliertes Spielprofil. {reason}')
    if cohort == 'bot':
        peer = S.pair.JOIN_CID.lower()
        if S.procs.matches(state.get(peer)) or S.procs.matches(state.get('agent_' + peer)):
            raise RuntimeError('Für den T4 Bot-Joiner muss die reale Joiner-Kopie beendet sein.')
    return state.get('relay')


def verify_relay(S, cohort, profile, relay, *, finished=False):
    if cohort == 'disconnect':
        if not S.procs.matches(relay) or not has_plan(relay, disconnect_plan(profile, S.pair.POOR_NETWORK)):
            raise RuntimeError('Der Relay-Prozess muss mit dem separaten Zehn-Sekunden-Schnitt gestartet sein.')
    else:
        network_evidence(relay, S.pair.POOR_NETWORK, profile, S.procs.matches, S.Copy.lines, require_delay=finished)


def smoke(S, forwarded):
    test = S.Run('smoke')
    steps = S.smoke_plan(test)
    missing_skip_gate = forwarded.get('KCDMP_TEST_W206') != '1' and os.environ.get('KCDMP_RIG_RESEARCH') != '1'
    skipped = []
    for label, check in steps:
        if missing_skip_gate and label in ('sleep', 'wait'):
            skipped.append(label)
            continue
        if not test.step(label, check) and (label == 'join' or test.steps[-1]['reason'] == 'copy-exited'):
            break
    return test.finish(skipped=skipped), skipped


def fresh_evidence(run_dir, station):
    files = {}
    for path in Path(run_dir).glob(f'scenario-{station}-*/result.json'):
        try:
            files[str(path.resolve())] = path.stat().st_mtime_ns
        except FileNotFoundError:
            pass
    return files


def run_station(S, station, args, relay, forwarded, run_dir):
    row = empty_row(station)
    row['run_dir'] = str(run_dir)
    prerequisite = missing_prerequisite(station, args, os.environ, forwarded)
    if prerequisite:
        row['reason'] = prerequisite
        return row
    previous = fresh_evidence(run_dir, station.name)
    skipped = []
    start = time.monotonic()
    try:
        current_relay = current_games(S, station.cohort)
        if current_relay != relay or not S.procs.matches(current_relay):
            raise RuntimeError('Die bestätigte Relay-Sitzung wurde seit dem Start der Gruppe ersetzt oder beendet.')
        command = station_arguments(station, args)
        if station.name == 'smoke':
            status, skipped = smoke(S, forwarded)
        elif station.name == 'botjoiner':
            test = S.Run('botjoiner', include_joiner=False)
            test.step('bot-joiner', S.s_bot_joiner(test))
            status = test.finish()
            if status == 0:
                verify_relay(S, station.cohort, args.netz, relay, finished=True)
        elif station.name == 'verdict':
            test = S.Run('verdict')
            if test.step('join', S.s_join(test)):
                test.step('verdict', S.s_verdict(test))
            status = test.finish()
        else:
            status = S.run(station.name, command)
        row['exit'] = status
        if skipped and status == 2:
            row.update(status='skip', reason='smoke-sleep-and-wait-test-gate-missing', skipped_steps=skipped)
        elif status != 0:
            row.update(status='fail', reason='scenario-failed')
        else:
            row.update(status='pass', reason='ok')
    except SystemExit as error:
        row.update(status='fail', reason='scenario-arguments-refused', detail=str(error))
    except (OSError, ValueError, RuntimeError) as error:
        row.update(status='fail', reason='scenario-could-not-complete', detail=str(error))
    finally:
        row['seconds'] = round(time.monotonic() - start, 1)
        current = fresh_evidence(run_dir, station.name)
        row['evidence'] = sorted(path for path, stamp in current.items() if previous.get(path) != stamp)
    if row['status'] == 'pass' and not any(passed_result(path, station.name) for path in row['evidence']):
        row.update(status='fail', reason='fresh-passing-result-missing')
    return row


def passed_result(path, station):
    try:
        result = json.loads(Path(path).read_text(encoding='utf-8'))
    except (OSError, ValueError):
        return False
    return isinstance(result, dict) and result.get('scenario') == station and result.get('pass') is True


def replay_counts(files):
    passed_names = set()
    totals = {key: 0 for key in ('executed', 'passed', 'failed')}
    for path in files:
        tree = ElementTree.parse(path)
        for counters in tree.findall('.//{*}ResultSummary/{*}Counters'):
            for key in totals:
                totals[key] += int(counters.attrib[key])
        for result in tree.findall('.//{*}UnitTestResult'):
            label = result.get('testName', '')
            if result.get('outcome') != 'Passed' or 'ReplayScenarioTests.' not in label:
                continue
            match = re.search(r'\bname:\s*["\']([a-z-]+)["\']', label)
            if match:
                passed_names.add(match[1])
    totals['passed_fixtures'] = sorted(name for name in REPLAY_CASES if name in passed_names)
    return totals


def run_replay(S, args, output):
    folder = Path(tempfile.mkdtemp(prefix='abend-replay-', dir=output.parent))
    log = folder / 'dotnet-test.log'
    root = Path(S.HERE).resolve().parents[1]
    command = [args.dotnet, 'test', str(root / 'dotnet' / 'KcdMp.Client.Tests' / 'KcdMp.Client.Tests.csproj'),
               '--configuration', 'Release', '--nologo', '--filter', 'FullyQualifiedName~ReplayScenarioTests',
               '--logger', 'trx', '--results-directory', str(folder)]
    result = {'status': 'fail', 'fixtures': list(REPLAY_CASES), 'log': str(log)}
    try:
        with log.open('w', encoding='utf-8') as stream:
            process = subprocess.run(command, cwd=root, stdout=stream, stderr=subprocess.STDOUT, timeout=900)
        files = sorted(folder.glob('*.trx'))
        counts = replay_counts(files)
        result.update(counts, files=[str(path) for path in files], exit=process.returncode)
        if (process.returncode == 0 and counts['passed'] == counts['executed'] and not counts['failed']
                and set(counts['passed_fixtures']) == set(REPLAY_CASES)):
            result['status'] = 'pass'
    except (OSError, ValueError, KeyError, ElementTree.ParseError, subprocess.TimeoutExpired) as error:
        result['detail'] = str(error)
    return result


def run(S, argv):
    parser = argparse.ArgumentParser(prog='scenarios.py abend', description=__doc__)
    parser.add_argument('--test-solo-abend', action='store_true')
    parser.add_argument('--cohort', choices=COHORTS, default='pair')
    parser.add_argument('--netz', choices=('normal', 'schlecht'), default='normal')
    parser.add_argument('--output', type=Path)
    parser.add_argument('--playline', type=int, default=4)
    parser.add_argument('--save', default='save440')
    parser.add_argument('--route-measure', type=Path)
    parser.add_argument('--route-walk', type=Path)
    parser.add_argument('--bandit')
    parser.add_argument('--weapon', choices=('bow', 'crossbow'), default='bow')
    parser.add_argument('--unattended', action='store_true')
    parser.add_argument('--run-replay', action='store_true')
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--pair-verdict', type=Path)
    args = parser.parse_args(argv)
    if not args.test_solo_abend or os.environ.get(ENVIRONMENT) != '1':
        parser.error(f'{ENVIRONMENT}=1 and --test-solo-abend are required')
    if not 1 <= args.playline <= 255 or not re.fullmatch(r'[A-Za-z0-9_-]{1,48}', args.save):
        parser.error('--playline and --save must identify a disposable save in the Host copy')
    if args.cohort != 'summary' and (args.run_replay or args.pair_verdict):
        parser.error('--run-replay and --pair-verdict belong to --cohort summary')

    selected_output = args.output or os.environ.get('KCDMP_RIG_SOLO_REPORT')
    run_dir = None if args.cohort == 'summary' and selected_output else Path(S.rig.run_dir()).resolve()
    output = Path(selected_output).resolve() if selected_output else run_dir / 'abend.json'
    try:
        report = read_report(output, args.netz)
    except (OSError, ValueError) as error:
        parser.error(str(error))

    if args.cohort == 'summary':
        if args.run_replay:
            output.parent.mkdir(parents=True, exist_ok=True)
            report['replay'] = run_replay(S, args, output)
        if args.pair_verdict:
            report['paired_verdict'] = {'path': str(args.pair_verdict.resolve())}
        current_verdict = report.setdefault('paired_verdict', {})
        path = current_verdict.get('path')
        current_verdict['status'] = 'available' if path and Path(path).is_file() and Path(path).stat().st_size > 0 else 'missing'
        report['counts'] = result_counts(report)
        write_report(output, report)
        S.rig.say('ABEND', str(output), json.dumps(report['counts']))
        return summary_code(report)

    chosen = [station for station in STATIONS if station.cohort == args.cohort]
    forwarded = S.pair.test_env()
    preflight = None
    relay = None
    try:
        relay = current_games(S, args.cohort)
        verify_relay(S, args.cohort, args.netz, relay)
    except (OSError, ValueError, RuntimeError) as error:
        preflight = str(error)
    failed = False
    for station in chosen:
        S.rig.say('ABEND', station.name)
        if preflight:
            row = {**empty_row(station), 'status': 'fail', 'reason': 'cohort-preflight-failed', 'detail': preflight}
        else:
            row = run_station(S, station, args, relay, forwarded, run_dir)
        S.rig.say('ABEND', station.name, row['status'], row['reason'])
        record(report, row)
        write_report(output, report)
        failed |= row['status'] == 'fail'
    return 1 if failed else 0
