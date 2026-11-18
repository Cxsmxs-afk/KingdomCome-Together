# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
"""Observe two operator-guided T5 runs started through the game console."""

import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
from uuid import UUID

from scen_s3_load import final_play, number, read_agent, rows, wait_until
from scen_solo_abend import current_games

TEST_ENV = 'KCDMP_TEST_GUIDED_RUN'
MARKER = 'MP-TESTLAUF'
COMMAND = 'mp_testlauf --test-guided-run start'
STATIONS = ('See', 'Walk', 'Fight', 'Down', 'Up', 'Reload', 'Disconnect')
EVIDENCE = ('ManualYes', 'ManualNo', 'Observed')
RUN_ID = re.compile(r'[0-9a-f]{32}\Z')


def transcript(lines, role, scope):
    entries = list(rows(lines, MARKER, role))
    starts = [(i, fields) for i, fields in entries if fields.get('result') == 'started']
    if not starts:
        return {'status': 'waiting-start'}
    if len(starts) != 1:
        return {'status': 'failed', 'reason': 'mehrere frische Starts'}
    offset, start = starts[0]
    identity = start.get('run', '')
    if not RUN_ID.fullmatch(identity) or UUID(hex=identity).int == 0 or reading_scope(start) != scope:
        return {'status': 'failed', 'reason': 'Startkennung oder Ausgangswelt passt nicht'}

    detail = {'status': 'active', 'role': role, 'run': identity, 'start': scope, 'stations': []}
    previous = scope
    completed = False
    collected = False
    for i, fields in entries:
        if i <= offset or fields.get('run') != identity:
            continue
        event = fields.get('result')
        if event == 'station':
            pos = len(detail['stations'])
            evidence = fields.get('evidence')
            world = reading_scope(fields)
            if (completed or pos >= len(STATIONS) or fields.get('station') != STATIONS[pos]
                    or evidence not in EVIDENCE or (pos < 5 and evidence == 'Observed')):
                return {**detail, 'status': 'failed', 'reason': 'ungültige Stationsfolge'}
            if pos == 5:
                if world == previous and evidence != 'ManualNo' or world != previous and not all(
                    old is not None and new is not None and old < new for old, new in zip(previous, world)
                ):
                    return {**detail, 'status': 'failed', 'reason': 'unpassender Ladewechsel'}
            elif world != previous:
                return {**detail, 'status': 'failed', 'reason': 'Station hat Welt oder Generation gewechselt'}
            detail['stations'].append({'station': STATIONS[pos], 'evidence': evidence, 'scope': world})
            previous = world
        elif event == 'complete':
            if completed or len(detail['stations']) != len(STATIONS) or number(fields, 'stations') != len(STATIONS):
                return {**detail, 'status': 'failed', 'reason': 'unvollständiger oder doppelter Abschluss'}
            completed = True
            detail['status'] = 'completed'
        elif event == 'collected':
            if not completed or collected:
                return {**detail, 'status': 'failed', 'reason': 'Sammlung ohne eindeutigen vorherigen Abschluss'}
            collected = True
            detail['status'] = 'collected'
        elif event in ('aborted', 'collection-failed', 'failed'):
            return {**detail, 'status': 'failed', 'reason': event}
    return detail


def reading_scope(fields):
    return number(fields, 'world'), number(fields, 'gen')


def json_receipt(path, proof):
    data = json.loads(path.read_text(encoding='utf-8-sig'))
    if (not isinstance(data, dict) or UUID(str(data.get('runId', ''))).hex != proof['run']
            or data.get('role') != proof['role'] or (data.get('startWorld'), data.get('startGeneration')) != proof['start']):
        raise ValueError('Der Testlauf-Bericht gehört zu einer anderen Rolle oder Ausgangswelt.')
    recorded = data.get('stations')
    if not isinstance(recorded, list) or len(recorded) != len(STATIONS):
        raise ValueError('Im Testlauf-Bericht fehlen Stationen.')
    for row, wanted in zip(recorded, proof['stations']):
        if (not isinstance(row, dict) or row.get('station') != wanted['station'].lower()
                or row.get('evidence') != wanted['evidence'][0].lower() + wanted['evidence'][1:]
                or (row.get('world'), row.get('generation')) != wanted['scope']):
            raise ValueError('Die Stationsdaten passen nicht zum frischen Agent-Protokoll.')
        if wanted['station'] == 'Reload' and wanted['evidence'] == 'Observed' and not row.get('baselineSnapshot'):
            raise ValueError('Der technischen Ladestation fehlt die Baseline.')
        if wanted['station'] == 'Disconnect' and wanted['evidence'] == 'Observed':
            elapsed = row.get('reconnectMs')
            if type(elapsed) is not int or not 0 <= elapsed <= 30_000:
                raise ValueError('Der technischen Trennstation fehlt die Wiederverbindung im Messfenster.')
    return data


def local_verdict(path, proof):
    lines = path.read_text(encoding='utf-8-sig').splitlines()
    roles = [line.split('\t') for line in lines if line.startswith('Rolle\t')]
    rule = [line.split('\t') for line in lines if line.startswith('guided_run_protocol\t')]
    expected = 'ROT' if any(row['evidence'] == 'ManualNo' for row in proof['stations']) else 'GRÜN'
    if (len(lines) < 5 or lines[0] != 'KCDMP-ERGEBNIS\t1' or roles != [['Rolle', proof['role']]]
            or len(rule) != 1 or len(rule[0]) != 4 or rule[0][1] != expected
            or not rule[0][2].startswith('7/7 Stationen ')):
        raise ValueError('Die lokale T1-Ampel bestätigt diesen Testlauf und seine Bewertungen nicht.')
    return {'guided_run_protocol': rule[0][1], 'measurement': rule[0][2]}


def collected_bundle(root, proof):
    suffix = f"{proof['role']}-{proof['run']}"
    pattern = re.compile(r'testlauf-\d{8}-\d{6}-' + re.escape(suffix) + r'\Z')
    candidates = [folder for folder in root.glob(f'testlauf-*-{suffix}') if pattern.fullmatch(folder.name) and folder.is_dir()]
    if len(candidates) != 1:
        raise ValueError('Für die frische Testlaufkennung fehlt ein eindeutiger Ausgabeordner.')
    bundle = candidates[0]
    json_receipt(bundle / 'TESTLAUF.json', proof)
    parts = sorted(path.name for path in bundle.glob('bundle*.tzst') if path.is_file() and path.stat().st_size > 0)
    if not parts:
        raise ValueError('LogPack hat kein gepacktes Bundle abgelegt.')
    return {**local_verdict(bundle / 'ERGEBNIS.txt', proof), 'folder': str(bundle), 'parts': parts}


def exercise(S, run, root, timeout):
    relay = current_games(S, 'pair')
    if not S.procs.matches(relay):
        raise RuntimeError('Der aufgezeichnete Relay-Prozess muss laufen.')
    active = (('host', run.a), ('joiner', run.b))
    state = S.pair.state()
    participants = [state.get(key) for _, copy in active for key in (copy.id.lower(), 'agent_' + copy.id.lower())]

    def alive():
        return all(S.procs.matches(participant) for participant in participants)

    scopes = {}
    for role, copy in active:
        play = final_play(copy.lines(copy.alog, 0), role)
        if not play:
            raise RuntimeError(f'{role}: keine Spielgeneration mit Phase Play beobachtet')
        scopes[role] = play[3], play[2]
    if scopes['host'][0] != scopes['joiner'][0]:
        raise RuntimeError('Host und Joiner müssen vor dem Testlauf in derselben Welt spielen.')

    marks = {role: copy.mark() for role, copy in active}
    for role, copy in active:
        try:
            copy.cmd(COMMAND)
        except OSError as error:
            S.rig.note('guided-run-start-console', copy=copy.id, detail=str(error))

    results = {}

    def read():
        for role, copy in active:
            results[role] = transcript(read_agent(copy, marks[role]), role, scopes[role])
        return any(data['status'] == 'failed' for data in results.values())

    def both_started():
        return read() or all(data['status'] != 'waiting-start' for data in results.values())

    if not wait_until(both_started, 25, alive=alive) or any(data['status'] == 'failed' for data in results.values()):
        return False, 'kein gemeinsamer frischer Testlauf', results
    S.rig.say('Beide Testläufe gestartet. Stationen im Spiel ausführen und die Konsolenmeldungen beachten.')
    S.rig.say('Auf beiden Kopien mit mp_testlauf --test-guided-run weiter ja oder weiter nein protokollieren.')

    def finished():
        return read() or all(data['status'] == 'collected' for data in results.values())

    if not wait_until(finished, timeout, alive=alive) or any(data['status'] != 'collected' for data in results.values()):
        return False, 'Testlauf oder Sammlung nicht vollständig beobachtet', results
    if results['host']['run'] == results['joiner']['run']:
        return False, 'beide Ausgaben haben dieselbe lokale Testlaufkennung', results
    for role, _ in active:
        results[role]['bundle'] = collected_bundle(root, results[role])

    tool = Path(S.HERE, '..', 'verdict', 'pair.py').resolve()
    command = [sys.executable, '-X', 'utf8', str(tool), *(results[role]['bundle']['folder'] for role, _ in active)]
    paired = subprocess.run(command, cwd=run.dir, check=False, timeout=90)
    detail = {'roles': results, 'pair_verdict': str(Path(run.dir, 'ERGEBNIS-PAAR.md')), 'pair_exit': paired.returncode}
    return paired.returncode == 0, 'ok' if paired.returncode == 0 else 'Paar-Ampel nicht grün', detail


def run(S, argv):
    parser = argparse.ArgumentParser(prog='scenarios.py testlauf', description=__doc__)
    parser.add_argument('--test-guided-run', action='store_true')
    parser.add_argument('--timeout', type=int, default=1200, help='seconds to finish both operator-guided runs')
    args = parser.parse_args(argv)
    if not args.test_guided_run or os.environ.get(TEST_ENV) != '1' or S.pair.test_env().get(TEST_ENV) != '1':
        parser.error(f'--test-guided-run and {TEST_ENV}=1 in the shell and KCDMP_RIG_TEST_ENV are required')
    if not 30 <= args.timeout <= 3600:
        parser.error('--timeout must be between 30 and 3600 seconds')
    local = os.environ.get('LOCALAPPDATA')
    if not local or not Path(local).is_absolute():
        parser.error('LOCALAPPDATA must name the local collector root')
    try:
        if not S.procs.matches(current_games(S, 'pair')):
            raise RuntimeError('Der aufgezeichnete Relay-Prozess muss laufen.')
    except (OSError, RuntimeError) as error:
        parser.error(str(error))
    record = S.Run('testlauf')
    if record.step('join', S.s_join(record, secs=25)):
        record.step('testlauf', lambda: exercise(S, record, Path(local, 'KCDMP', 'Testlauf'), args.timeout))
    return record.finish()
