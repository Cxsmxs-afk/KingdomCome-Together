# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
"""S3-2 tests with authenticated Relay receipts and real game application."""

import argparse
from dataclasses import dataclass
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time

from scen_s2_measure import ASSIGNED_PEER, BOT_PEER, finite_position, network_evidence, peer_ids
from scen_s2_owner import VIOLATION, mark_native
from scen_s3_load import final_play, number, read_agent, rows, stop_bot, wait_until

TEST_ENV = 'KCDMP_TEST_BASELINE_EVENTS'
BASELINE = 'MP-WORLD-BASELINE'
EVENT = 'MP-WORLD-EVENT'
RELAY_EVENT = 'MP-WORLD-EVENT relay'
BOT_HOST_PEER = re.compile(r'BOT-T4 role=host started=1 peer=(\d+)\b')
BOT_READY = re.compile(r'BOT-T4 role=(?:joiner|host) ready=1\b')
REQUIRED_ZERO = {'evt_gap', 'evt_dropped', 'evt_sender_dropped', 'owner_violations'}
COVERAGE = ('evt_sent', 'evt_acked', 'evt_applied')
CASE_CHANNEL = {'pickup': 'Loot', 'corpse': 'Corpse', 'dialog': 'Dialog'}
CASE_COUNT = {'pickup': 1, 'corpse': 1, 'dialog': 2}


@dataclass(frozen=True)
class Scope:
    world: int
    generation: int
    snapshot: int

    @classmethod
    def parse(cls, fields, prefix=''):
        values = [number(fields, prefix + key) for key in ('world', 'gen', 'snapshot')]
        return cls(*values) if all(values) else None


def applied_baseline(lines, role):
    samples = [Scope.parse(fields) for _, fields in rows(lines, BASELINE, role)
               if fields.get('result') == 'applied' and number(fields, 'version') == 1]
    return samples[-1] if samples else None


def event_fields(lines, marker, scope, channel, source, target, *, role=None):
    for index, fields in rows(lines, marker, role):
        if channel != fields.get('channel') or scope != Scope.parse(fields) or number(fields, 'source') != source:
            continue
        if marker == RELAY_EVENT and number(fields, 'target') != target:
            continue
        event_id, wire_type = number(fields, 'id'), number(fields, 'type')
        if event_id and wire_type and wire_type <= 255:
            yield index, (event_id, wire_type), fields


def relay_baseline_acked(lines, scope, target):
    return any(fields.get('result') == 'acked' and Scope.parse(fields) == scope and number(fields, 'target') == target
               for _, fields in rows(lines, BASELINE + ' relay'))


def event_proof(agent, relay, scope, channel, source, target, minimum, *, role, accepts):
    """Match physical scope from Relay send through per-event game application to cumulative ACK."""
    game = list(event_fields(agent, EVENT, scope, channel, source, target, role=role))
    applied = [(index, key, fields) for index, key, fields in game
               if fields.get('result') == 'applied' and accepts(fields)]
    local_acks = {key: index for index, key, fields in game if fields.get('result') == 'ack-written'}
    sent = {key: index for index, key, fields in event_fields(relay, RELAY_EVENT, scope, channel, source, target)
            if fields.get('result') == 'sent'}
    acked = [(index, number(fields, 'id')) for index, fields in rows(relay, RELAY_EVENT)
             if channel == fields.get('channel') and scope == Scope.parse(fields) and number(fields, 'target') == target
             and fields.get('result') == 'acked' and number(fields, 'id')]
    failures = []
    ids = [key[0] for _, key, _ in applied]
    if len(ids) < minimum:
        failures.append('expected-game-application-unmeasured')
    if any(before >= after for before, after in zip(ids, ids[1:])):
        failures.append('duplicate-or-out-of-order-game-application')
    covering = []
    for index, key, _ in applied:
        ack_id = next((highest for pos, highest in acked if highest >= key[0] and pos > sent.get(key, float('inf'))), None)
        if ack_id is None or local_acks.get(key, -1) <= index:
            failures.append('authenticated-receipt-missing')
        covering.append(ack_id)
    return {'events': [fields for _, _, fields in applied], 'relay_ack_through': covering, 'failures': list(dict.fromkeys(failures))}


def votes_proof(lines, result, scope, source):
    by_id = {}
    for _, fields in rows(lines, 'MP-W204-VOTE-APPLIED'):
        if number(fields, 'peer') == source and number(fields, 'world_gen') == scope.generation:
            index = fields.get('index', '')
            if re.fullmatch(r'-?\d+', index):
                by_id[number(fields, 'event_id')] = (number(fields, 'cid'), number(fields, 'decision'), int(index))
    votes = [by_id.get(number(fields, 'id')) for fields in result['events']]
    if len(votes) != 2 or any(vote is None or vote[0] is None or vote[1] is None or vote[2] < 0 for vote in votes):
        return None
    if votes[0][:2] != votes[1][:2] or votes[0][2] == votes[1][2]:
        return None
    return votes


def bot_votes_proof(lines, votes, result, source, target):
    first, second = votes
    token_list = tuple(number(fields, 'token') for fields in result['events'])
    keys = ('peer', 'host', 'cid', 'decision', 'first', 'second', 'first_token', 'second_token')
    expected = (source, target, first[0], first[1], first[2], second[2], *token_list)
    return all(token_list) and any(fields.get('dialog_votes') == 'sent' and
                                   tuple(number(fields, key) for key in keys) == expected
                                   for _, fields in rows(lines, 'BOT-T4', 'joiner'))


def loot_game_proof(lines, result, case):
    pattern = (r'WO134-ITEM ok tok=(\d+) cls=\S+' if case == 'pickup'
               else r'\bw217_tack (\d+) [^\s]+ applied\b')
    applied_tokens = {int(match[1]) for line in lines if (match := re.search(pattern, line))}
    return bool(result['events']) and all(number(row, 'token') in applied_tokens for row in result['events'])


def role_stats(lines, role, scope):
    result = {}
    owner = None
    for _, fields in rows(lines, 'MP-T3-STATS', role):
        if fields.get('origin') != 'agent':
            continue
        if fields.get('section') == 'events' and Scope.parse(fields, prefix='evt_') == scope:
            result = {key: number(fields, key) for key in fields if key.startswith(('evt_', 'baseline_'))
                      and number(fields, key) is not None}
        if (value := number(fields, 'owner_violations')) is not None:
            owner = value
    if owner is not None:
        result['owner_violations'] = owner
    return result


def stats_failures(stats, minimum=1):
    failures = [key + '-positive' for key in sorted(REQUIRED_ZERO) if stats.get(key, 0) > 0]
    if REQUIRED_ZERO.difference(stats):
        failures.append('zero-counters-unmeasured')
    if (stats.get('baseline_version') != 1 or 'baseline_ms' not in stats
            or any(stats.get(key, 0) < minimum for key in COVERAGE)):
        failures.append('current-baseline-or-event-counter-unmeasured')
    return failures


def participant_peer(copy):
    ids = peer_ids(copy.lines(copy.alog, 0), ASSIGNED_PEER)
    return ids[-1] if ids else None


def wait_baseline(copy, role):
    return wait_until(lambda: applied_baseline(copy.lines(copy.alog, 0), role), 150)


def wait_stats(copy, role, scope, mark, minimum):
    result = {}

    def read():
        nonlocal result
        result = role_stats(read_agent(copy, mark), role, scope)
        return any(result.get(key, 0) > 0 for key in REQUIRED_ZERO) or not stats_failures(result, minimum)

    wait_until(read, 90)
    return result


def no_connection_change(copy, mark):
    return copy.size(copy.alog) >= mark['a'] and not peer_ids(read_agent(copy, mark), ASSIGNED_PEER)


def launch_bot(command, logfile):
    with logfile.open('w', encoding='utf-8') as stream:
        return subprocess.Popen(command, stdout=stream, stderr=subprocess.STDOUT,
                                creationflags=subprocess.CREATE_NEW_PROCESS_GROUP)


def bot_host(S, run, args, *, fixture_flag='--test-baseline-events'):
    tool = Path(S.HERE, '..', 'bothost', 'bot-joiner').resolve()
    command = [sys.executable, '-X', 'utf8', str(tool), '--test-bot', fixture_flag, '--role', 'host',
               '--port', str(S.pair.RELAY_PORT), '--save', str(args.bot_save.resolve()), '--world-time', str(args.world_time),
               '--duration', str(args.timeout + 300)]
    if args.bot_plan:
        command += ['--plan', str(args.bot_plan.resolve())]
    if args.wire_version:
        command += ['--wire-version', args.wire_version]
    logfile = Path(run.dir, 'bot-t4.log')
    process = launch_bot(command, logfile)

    def started(timeout):
        return wait_until(lambda: peer_ids(S.Copy.lines(logfile, 0), BOT_HOST_PEER),
                          min(timeout, 120), alive=lambda: process.poll() is None)

    joiner = run.b
    try:
        status = S.pair.join_from_menu(joiner.id, joiner.p, S.pair.state(), joiner.id.lower(), joiner.port, wait_host=started)
        if status:
            raise RuntimeError('the real Joiner could not connect to the T4 fixture Host')
        ids = wait_until(lambda: peer_ids(S.Copy.lines(logfile, 0), BOT_HOST_PEER), 20)
        if not ids or len(ids) != 1 or not wait_until(lambda: any(BOT_READY.search(line) for line in S.Copy.lines(logfile, 0)),
                                                       300, alive=lambda: process.poll() is None):
            raise RuntimeError('the T4 fixture Host did not finish the real Joiner transfer')
        return process, logfile, ids[0]
    except BaseException:
        stop_bot(process)
        raise


def bot_joiner(S, run, args):
    if not finite_position(run.a.pos()):
        raise RuntimeError('the real Host must have a live player position')
    logfile = Path(run.dir, 'bot-t4.log')
    command = S.bot_joiner_command(run, '--duration', str(args.timeout + 300), '--test-baseline-events', '--dialog-votes')
    process = launch_bot(command, logfile)

    def ready():
        lines = S.Copy.lines(logfile, 0)
        ids = peer_ids(lines, BOT_PEER)
        return ids if len(ids) == 1 and any(BOT_READY.search(line) for line in lines) else None

    try:
        ids = wait_until(ready, 180, alive=lambda: process.poll() is None)
        if not ids:
            raise RuntimeError('the T4 Bot-Joiner could not finish its staged transfer')
        return process, logfile, ids[0]
    except BaseException:
        stop_bot(process)
        raise


def operator_step(S, case, bot):
    if case == 'pickup':
        S.rig.say('Jetzt als Joiner einen losen Gegenstand im Spiel aufheben. Warten, bis er im eigenen Inventar liegt.')
    elif case == 'corpse':
        S.rig.say('Jetzt als Joiner die vereinbarte Leiche öffnen und genau einen Gegenstand daraus ins eigene Inventar nehmen.')
    elif bot:
        S.rig.say('Jetzt als Host mit einem NPC sprechen. Die Antwortauswahl mit mindestens zwei Optionen offen lassen.')
    else:
        S.rig.say('Jetzt als Host einen NPC ansprechen. Der Joiner folgt und stimmt für zwei verschiedene Antworten ab.')
        S.rig.say('Der Host lässt die Antwortauswahl offen, bis beide Bestätigungen angekommen sind.')


def accept_event(case, fields):
    # The relay classifies gameplay channels after validating the inner type.
    return fields.get('kind') in {'pickup': ('item-result',), 'corpse': ('take-verdict',), 'dialog': ('vote',)}[case]


def exercise(S, run, case, args):
    dialog = case == 'dialog'
    role = 'host' if dialog else 'joiner'
    receiver, sender = (run.a, run.b) if dialog else (run.b, run.a)
    copies = (receiver,) if args.bot else (run.a, run.b)
    state = S.pair.state()
    if any(copy.port is None or not S.procs.matches(state.get(copy.id.lower())) for copy in copies):
        raise RuntimeError('every real game must be recorded as running in the rig')
    if args.bot and S.procs.matches(state.get(sender.id.lower())):
        raise RuntimeError('stop the game replaced by the T4 peer')
    relay = state.get('relay')
    network_evidence(relay, S.pair.POOR_NETWORK, args.netz, S.procs.matches, S.Copy.lines)
    if args.bot and not dialog and S.procs.matches(state.get('agent_' + receiver.id.lower())):
        raise RuntimeError('start the real Joiner at its main menu without an Agent for Bot-Host events')

    process = None
    bot_log = None
    try:
        if args.bot:
            process, bot_log, source = (bot_joiner if dialog else bot_host)(S, run, args)
        else:
            source = participant_peer(sender)
        scope = wait_baseline(receiver, role)
        target = participant_peer(receiver)
        if scope is None or source is None or target is None or source == target:
            raise RuntimeError('a real applied baseline and both authenticated relay identities are required before the station')
        play = final_play(receiver.lines(receiver.alog, 0), role)
        if not play or (play[2], play[3]) != (scope.generation, scope.world) or not finite_position(receiver.pos()):
            raise RuntimeError('the real receiver must be in Play for the baseline world and generation')
        if not wait_until(lambda: relay_baseline_acked(S.Copy.lines(relay['relay_log'], relay['relay_log_offset']),
                                                     scope, target), 30):
            raise RuntimeError('the Relay did not acknowledge the applied baseline in the receiver scope')
        marks = {copy.id: copy.mark() for copy in copies}
        native_marks = {copy.id: mark_native(copy) for copy in copies}
        relay_off = S.Copy.size(relay['relay_log'])
        operator_step(S, case, args.bot)
        result = {}

        def observe():
            nonlocal result
            result = event_proof(read_agent(receiver, marks[receiver.id]), S.Copy.lines(relay['relay_log'], relay_off),
                                 scope, CASE_CHANNEL[case], source, target, CASE_COUNT[case],
                                 role=role, accepts=lambda fields: accept_event(case, fields))
            return not result['failures']

        if not wait_until(observe, args.timeout, alive=(lambda: process.poll() is None) if process else None):
            observe()
        stats = wait_stats(receiver, role, scope, receiver.mark(), CASE_COUNT[case])
        # Capture late duplicates or violations before writing the artifact.
        observe()
        failures = result['failures'] + stats_failures(stats, CASE_COUNT[case])
        votes = None
        if dialog:
            votes = votes_proof(read_agent(receiver, marks[receiver.id]), result, scope, source)
            if votes is None:
                failures.append('two-distinct-applied-votes-in-one-decision-unmeasured')
            elif args.bot and not bot_votes_proof(S.Copy.lines(bot_log, 0), votes, result, source, target):
                failures.append('t4-votes-do-not-match-game-application')
        elif not loot_game_proof(receiver.lines(receiver.klog, marks[receiver.id]['k']), result, case):
            failures.append('successful-game-inventory-outcome-unmeasured')
        for copy in copies:
            agent = read_agent(copy, marks[copy.id])
            if any('falling back to http-debug-api' in line for line in agent):
                failures.append(f'{copy.id}:http-fallback')
            if not no_connection_change(copy, marks[copy.id]):
                failures.append(f'{copy.id}:replaced-participant')
            if any(VIOLATION in line for path, mark in native_marks[copy.id].items() for line in copy.lines(path, mark)):
                failures.append(f'{copy.id}:native-owner-violation')
        if applied_baseline(receiver.lines(receiver.alog, 0), role) != scope:
            failures.append('baseline-replaced-during-station')
        network = network_evidence(relay, S.pair.POOR_NETWORK, args.netz, S.procs.matches, S.Copy.lines, require_delay=True)
        bundles = S.pair.collect(f'{case}-{time.time_ns()}', write_verdict=True,
                                 copies=tuple(copy.id for copy in copies), bot_log=bot_log)
        detail = {'baseline': vars(scope), 'role': role, 'source': source, 'target': target, 'bot': args.bot,
                  'network': network, 'events': result['events'], 'relay_ack_through': result['relay_ack_through'],
                  'votes': votes, 'metrics': stats,
                  'failures': failures, 'bundles': {cid: folder + '.packed' for cid, folder in bundles.items()}}
        Path(run.dir, f'{case}.json').write_text(json.dumps(detail, indent=2), encoding='utf-8')
        return not failures, failures[0] if failures else 'ok', detail
    finally:
        if process is not None:
            stop_bot(process)


def run(S, case, argv):
    parser = argparse.ArgumentParser(prog=f'scenarios.py {case}', description=__doc__)
    parser.add_argument('--test-baseline-events', action='store_true')
    parser.add_argument('--netz', choices=('normal', 'schlecht'), default='normal')
    parser.add_argument('--bot', action='store_true')
    parser.add_argument('--timeout', type=int, default=120, help='seconds to wait for the staged gameplay action')
    if case != 'dialog':
        parser.add_argument('--bot-save', type=Path)
        parser.add_argument('--bot-plan', type=Path, help='external fixture plan holding the matching pickup or corpse')
        parser.add_argument('--world-time', type=float)
        parser.add_argument('--wire-version')
    args = parser.parse_args(argv)
    if not args.test_baseline_events or os.environ.get(TEST_ENV) != '1':
        parser.error(f'{TEST_ENV}=1 and --test-baseline-events are required')
    if not 30 <= args.timeout <= 480:
        parser.error('--timeout must be between 30 and 480 seconds')
    if args.bot and os.environ.get('KCDMP_TEST_BOT') != '1':
        parser.error('--bot requires KCDMP_TEST_BOT=1')
    if args.bot and case != 'dialog':
        if (args.bot_save is None or not args.bot_save.is_file() or args.bot_plan is None or not args.bot_plan.is_file()
                or args.world_time is None or not 0 <= args.world_time < float('inf')):
            parser.error('--bot needs --bot-save, --bot-plan and --world-time from the external test fixture')
    record = S.Run(case, include_joiner=not args.bot)
    if args.bot and case != 'dialog':
        record.copies = (record.b,)
    if args.bot or record.step('join', S.s_join(record)):
        record.step(case, lambda: exercise(S, record, case, args))
    return record.finish()
