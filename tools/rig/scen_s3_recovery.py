# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
"""Observe real relay loss and a scoped digest repair on the disposable game pair."""

import argparse
import json
import os
from pathlib import Path
import re
import time

from scen_s2_measure import ASSIGNED_PEER, FAULT_DELAY, FAULT_FIRED, finite_position, network_evidence
from scen_s2_owner import VIOLATION, mark_native
from scen_s3_events import Scope, applied_baseline, bot_host, relay_baseline_acked
from scen_s3_load import final_play, number, read_agent, role_stats, rows, stop_bot, transitions, wait_until

TEST_ENV = 'KCDMP_TEST_WORLD_RECOVERY'
CUT = 's33-recovery-cut'
CUT_STEP = 'step:recovery-joiner-ready'
CUT_SECONDS = 10
RECONNECT_LIMIT_MS = 30_000
SECTIONS = ('body', 'npc', 'corpse', 'loot', 'clock', 'weather')
RECONNECT = 'MP-WORLD-RECONNECT'
DIGEST = 'MP-WORLD-DIGEST'
GAME_LOAD = re.compile(r"^Gameplay started|Loading saved game|\[CryAction\] LoadGame:")


def disconnect_plan(profile, poor_plan):
    plan = json.loads(Path(poor_plan).read_text(encoding='utf-8')) if profile == 'schlecht' else {'seed': 221, 'rules': []}
    plan['rules'].append({'name': CUT, 'kind': 'cut', 'role': 'joiner', 'dir': 'both', 'at': CUT_STEP, 'for_s': CUT_SECONDS})
    return plan


def relay_run(pair, argv):
    parser = argparse.ArgumentParser(prog='pair.py relay')
    parser.add_argument('--test-world-recovery', choices=('disconnect',), required=True)
    parser.add_argument('--netz', choices=('normal', 'schlecht'), default='normal')
    args = parser.parse_args(argv)
    if os.environ.get(TEST_ENV) != '1':
        parser.error(f'{TEST_ENV}=1 and --test-world-recovery=disconnect are required')
    plan = disconnect_plan(args.netz, pair.POOR_NETWORK)
    current = pair.state().get('relay')
    if pair.procs.matches(current):
        if not has_plan(current, plan):
            parser.error('the running Relay has another plan. Stop it before starting the disconnect test')
        return 0
    path = Path(pair.rig.run_dir(), f's33-disconnect-{args.netz}.json').resolve()
    path.write_text(json.dumps(plan, indent=2) + '\n', encoding='utf-8')
    return pair.relay(str(path))


def has_plan(relay, expected):
    try:
        return json.loads(Path(relay['fault_plan']).read_text(encoding='utf-8')) == expected
    except (OSError, ValueError, TypeError, KeyError):
        return False


def cut_markers(lines):
    marker = f'RELAY-FAULT {CUT}'
    fired = [fields for _, fields in rows(lines, marker + ' fired')]
    cut = [fields for _, fields in rows(lines, marker + ' cut')]
    healed = [fields for _, fields in rows(lines, marker + ' ended')]
    if len(fired) != 1 or len(cut) != 1 or len(healed) != 1:
        return None
    started, link, ended = fired[0], cut[0], healed[0]
    if (started.get('kind') != 'cut' or started.get('role') != 'joiner' or started.get('at') != CUT_STEP
            or number(started, 'for_s') != CUT_SECONDS or number(started, 'targets') != 1
            or started.get('via') != 'baseline-applied' or started.get('from') != 'joiner:' + str(number(link, 'id'))
            or link.get('role') != 'joiner' or number(link, 'id') is None
            or ended.get('reason') != 'healed' or ended.get('kind') != 'cut'
            or number(ended, 'cuts') != 1 or number(ended, 'hollow') != 0):
        return None
    try:
        elapsed = float(ended['active_s'])
    except (KeyError, ValueError):
        return None
    if not CUT_SECONDS <= elapsed <= CUT_SECONDS + 0.5:
        return None
    return {'started': started, 'cut': link, 'healed': ended, 'peer': number(link, 'id'), 'seconds': elapsed}


def cut_network(lines, profile):
    cut = cut_markers(lines)
    lag = next((line for line in lines if FAULT_FIRED.search(line)), None)
    delayed = max((int(m[1]) for line in lines if (m := FAULT_DELAY.search(line))), default=0)
    failures = []
    if cut is None:
        failures.append('ten-second-relay-cut-unconfirmed')
    if profile == 'schlecht' and (not lag or not delayed):
        failures.append('lag-and-jitter-unmeasured')
    if profile == 'normal' and lag:
        failures.append('unexpected-lag-rule')
    return {'profile': profile, 'cut': cut, 'relay_lag': lag, 'delayed': delayed, 'failures': failures}


def handshakes(lines):
    return [(i, int(m[1])) for i, line in enumerate(lines) if (m := ASSIGNED_PEER.search(line))]


def play_window(lines, role, scope, start, end):
    relevant = transitions(lines, role)
    last_before = [event for event in relevant if event[0] < start]
    if not last_before or last_before[-1][1:] != ('Play', scope[1], scope[0]):
        return False
    return all((phase, gen, world) == ('Play', scope[1], scope[0])
               for i, phase, gen, world in relevant if start <= i <= end)


def recovered_joiner(agent, relay, cut):
    failures = []
    started = [(index, fields) for index, fields in rows(agent, RECONNECT, 'joiner') if fields.get('result') == 'started']
    restored = [(index, fields) for index, fields in rows(agent, RECONNECT, 'joiner') if fields.get('result') == 'same-world']
    if len(started) != 1 or len(restored) != 1:
        return {'failures': ['one-scoped-reconnect-unconfirmed']}
    index, before = started[0]
    terminal, after = restored[0]
    scope = number(before, 'world'), number(before, 'gen')
    attempt, elapsed = number(before, 'attempt'), number(after, 'reconnect_ms')
    if (not all(scope) or not attempt or terminal <= index or number(after, 'attempt') != attempt or
            (number(after, 'world'), number(after, 'gen')) != scope):
        failures.append('reconnect-world-or-generation-changed')
    if not elapsed or elapsed > RECONNECT_LIMIT_MS:
        failures.append('reconnect-outside-30s')
    if any(number(fields, 'reload') != 0 for fields in (before, after)) or any(
            fields.get('result') in ('reload', 'different-world', 'timeout') for _, fields in rows(agent, RECONNECT, 'joiner')):
        failures.append('engine-reload-or-unresolved-reconnect')
    if not play_window(agent, 'joiner', scope, index, len(agent)):
        failures.append('joiner-left-play-or-changed-generation')
    connections = handshakes(agent)
    if (len(connections) != 2 or connections[0][0] >= index or not index < connections[1][0] < terminal
            or cut is None or connections[0][1] != cut['peer']):
        failures.append('cut-or-fresh-authenticated-joiner-unconfirmed')
    baseline = applied_baseline(agent[:index], 'joiner')
    # A later reconnect can reuse the numerical peer ID, so only admit a baseline ACK before the actual cut.
    fired = next((i for i, line in enumerate(relay) if f'RELAY-FAULT {CUT} fired ' in line), 0)
    if (baseline is None or (baseline.world, baseline.generation) != scope or
            not connections or not relay_baseline_acked(relay[:fired], baseline, connections[0][1])):
        failures.append('pre-cut-applied-baseline-unconfirmed')
    return {'world': scope[0], 'generation': scope[1], 'attempt': attempt, 'reconnect_ms': elapsed,
            'connections': connections, 'failures': failures}


def stable_host(lines, world):
    relevant = transitions(lines, 'host')
    play = next((event for event in relevant if event[1] == 'Play' and event[3] == world), None)
    if play is None or len(handshakes(lines)) != 1:
        return None
    if not play_window(lines, 'host', (world, play[2]), play[0] + 1, len(lines)):
        return None
    return {'world': world, 'generation': play[2], 'peer': handshakes(lines)[0][1]}


def digest_proof(lines, scope, section):
    matching = [(index, fields) for index, fields in rows(lines, DIGEST, 'joiner') if Scope.parse(fields) == scope]
    lookup = lambda fields: fields.get('section', '').lower()
    corruption = [(index, fields) for index, fields in matching if fields.get('result') == 'corrupted']
    mismatch = [(index, fields) for index, fields in matching if lookup(fields) == section and fields.get('result') == 'mismatch']
    repair = [(index, fields) for index, fields in matching if lookup(fields) == section and fields.get('result') == 'repaired']
    unrelated = [fields for _, fields in matching if lookup(fields) != section and fields.get('result') == 'repaired']
    failures = []
    if len(corruption) != 1 or lookup(corruption[0][1]) != section or number(corruption[0][1], 'rev') != 0:
        failures.append('selected-test-corruption-unconfirmed')
    mismatch_rev = number(mismatch[0][1], 'rev') if len(mismatch) == 1 else None
    repair_rev = number(repair[0][1], 'rev') if len(repair) == 1 else None
    if (len(corruption) != 1 or not mismatch_rev or not repair_rev
            or not corruption[0][0] < mismatch[0][0] < repair[0][0]
            or repair_rev < mismatch_rev or (section == 'loot' and repair_rev != mismatch_rev)):
        failures.append('one-matching-section-repair-unconfirmed')
    if not repair_rev or not any(index > repair[0][0] and lookup(fields) == section and fields.get('result') == 'checked'
                                 and number(fields, 'rev') == repair_rev for index, fields in matching):
        failures.append('matching-digest-after-repair-unconfirmed')
    if unrelated:
        failures.append('cross-section-repair')
    if any(fields.get('result') == 'rejected' for _, fields in matching):
        failures.append('digest-or-repair-rejected')
    neighbors_checked = sorted({lookup(fields) for _, fields in matching
                                if lookup(fields) != section and fields.get('result') == 'checked'})
    return {'section': section, 'corruption': [fields for _, fields in corruption],
            'mismatches': [fields for _, fields in mismatch], 'repairs': [fields for _, fields in repair],
            'other_repairs': unrelated, 'other_checked_sections': neighbors_checked, 'failures': failures}


def recovery_stats(lines, scope):
    for _, fields in reversed(list(rows(lines, 'MP-T3-STATS', 'joiner'))):
        if (fields.get('origin') == 'agent' and fields.get('section') == 'recovery'
                and (number(fields, 'recovery_world'), number(fields, 'recovery_gen')) == scope
                and number(fields, 'recovery_run')):
            return {key: number(fields, key) for key in fields if number(fields, key) is not None}
    return {}


def reconnect_stats_failures(stats, elapsed):
    return [] if (elapsed and stats.get('reconnect_ms') == elapsed and stats.get('reconnect_success') == 1
                  and stats.get('reconnect_same_world') == 1 and stats.get('reconnect_timeout') == 0
                  and stats.get('reconnect_reload') == 0) else ['scoped-reconnect-counters-unconfirmed']


def digest_stats_failures(stats, before, section):
    if not stats or not before or stats.get('recovery_run') != before.get('recovery_run'):
        return ['scoped-digest-counters-unconfirmed']
    keys = [prefix + area for area in SECTIONS for prefix in ('digest_mismatch_', 'resync_', 'resync_failed_')]
    if any(key not in before or key not in stats or stats[key] < before[key] for key in keys):
        return ['scoped-digest-counters-unconfirmed']
    changes = {key: stats[key] - before[key] for key in keys}
    if (changes['digest_mismatch_' + section] != 1 or changes['resync_' + section] != 1
            or any(changes['resync_failed_' + area] for area in SECTIONS)
            or any(changes['resync_' + area] for area in SECTIONS if area != section)):
        return ['unexpected-digest-or-repair-counters']
    return []


def all_agent(copy):
    return copy.lines(copy.alog, 0)


def owner_evidence(copy, role, offset, native_marks):
    if any(VIOLATION in line for path, mark in native_marks.items() for line in copy.lines(path, mark)):
        return 'owner-violations'
    sample = role_stats(copy.lines(copy.alog, offset), role)
    if 'owner_violations' not in sample:
        return 'owner-counter-unmeasured'
    return 'owner-violations' if sample['owner_violations'] > 0 else None


def disconnect(S, run, args, relay, project):
    joiner = run.b
    relay_lines = lambda: S.Copy.lines(relay['relay_log'], relay['relay_log_offset'])
    if not has_plan(relay, disconnect_plan(args.netz, S.pair.POOR_NETWORK)):
        raise RuntimeError('start Relay with pair.py relay --test-world-recovery=disconnect and the same --netz before connecting')
    started = wait_until(lambda: any(fields.get('result') == 'started'
                                     for _, fields in rows(all_agent(joiner), RECONNECT, 'joiner')), args.timeout)
    connection = joiner.mark()
    if started:
        wait_until(lambda: any(fields.get('result') in ('same-world', 'reload', 'different-world', 'timeout')
                               for _, fields in rows(all_agent(joiner), RECONNECT, 'joiner')), 40)
    lines = relay_lines()
    network = cut_network(lines, args.netz)
    result = recovered_joiner(all_agent(joiner), lines, network['cut'])
    failures = network['failures'] + result['failures']
    if any(GAME_LOAD.search(line) for line in joiner.lines(joiner.klog, connection['k'])):
        failures.append('joiner-engine-load-during-observation')
    if not finite_position(joiner.pos()):
        failures.append('joiner-local-player-unmeasured-after-reconnect')
    host = None
    if not args.bot:
        host = stable_host(all_agent(run.a), result.get('world'))
        if host is None or not finite_position(run.a.pos()):
            failures.append('host-did-not-remain-in-the-same-live-world')
    elif project.poll() is not None:
        failures.append('t4-host-process-ended-while-the-joiner-recovered')
    scope = result.get('world'), result.get('generation')
    elapsed = result.get('reconnect_ms')

    def counters():
        current = recovery_stats(all_agent(joiner), scope)
        return current if current and not reconnect_stats_failures(current, elapsed) else None

    metrics = (wait_until(counters, 10) if elapsed else None) or recovery_stats(all_agent(joiner), scope)
    failures.extend(reconnect_stats_failures(metrics, elapsed))
    return {'joiner': result, 'host': host, 'network': network, 'metrics': metrics, 'failures': failures}


def desync(S, run, args, relay):
    joiner = run.b
    network_evidence(relay, S.pair.POOR_NETWORK, args.netz, S.procs.matches, S.Copy.lines)
    scope = wait_until(lambda: applied_baseline(all_agent(joiner), 'joiner'), args.timeout)
    play = final_play(all_agent(joiner), 'joiner')
    if scope is None or not play or play[2:] != (scope.generation, scope.world) or not finite_position(joiner.pos()):
        raise RuntimeError('the live Joiner is missing a matching scoped baseline and Play')
    stats_scope = scope.world, scope.generation
    prior = wait_until(lambda: recovery_stats(all_agent(joiner), stats_scope), 90)
    if prior is None:
        raise RuntimeError('the scoped digest counters were never observed before the test mutation')
    mark = joiner.mark()
    try:
        joiner.cmd(f'mp_test_world_recovery {args.section}')
    except OSError as error:
        S.rig.note('recovery-test-command', copy=joiner.id, detail=str(error))
    result = {}

    def sample():
        nonlocal result
        result = digest_proof(read_agent(joiner, mark), scope, args.section)
        return not result['failures']

    if not wait_until(sample, args.timeout):
        sample()
    phases = transitions(read_agent(joiner, mark), 'joiner')
    if applied_baseline(all_agent(joiner), 'joiner') != scope or any(
            (phase, gen, world) != ('Play', scope.generation, scope.world) for _, phase, gen, world in phases):
        result['failures'].append('reload-or-scope-replaced-during-targeted-repair')
    if any(GAME_LOAD.search(line) for line in joiner.lines(joiner.klog, mark['k'])):
        result['failures'].append('engine-loaded-during-targeted-repair')
    if len(handshakes(read_agent(joiner, mark))) > 0:
        result['failures'].append('reconnect-during-targeted-repair')
    if not args.bot and stable_host(all_agent(run.a), scope.world) is None:
        result['failures'].append('host-world-changed-during-targeted-repair')
    def counters():
        current = recovery_stats(read_agent(joiner, mark), stats_scope)
        return current if current and not digest_stats_failures(current, prior, args.section) else None

    metrics = wait_until(counters, 90) or recovery_stats(read_agent(joiner, mark), stats_scope)
    result['metrics'] = metrics
    result['failures'].extend(digest_stats_failures(metrics, prior, args.section))
    result['baseline'] = vars(scope)
    result['network'] = network_evidence(relay, S.pair.POOR_NETWORK, args.netz, S.procs.matches, S.Copy.lines, require_delay=True)
    return result


def exercise(S, run, case, args):
    parts = [('joiner', run.b)] if args.bot else [('host', run.a), ('joiner', run.b)]
    copies = tuple(copy.id for _, copy in parts)
    state = S.pair.state()
    if any(copy.port is None or not S.procs.matches(state.get(copy.id.lower())) for _, copy in parts):
        raise RuntimeError('every real game must be recorded as running in the Rig')
    if args.bot and (S.procs.matches(state.get(run.a.id.lower())) or S.procs.matches(state.get('agent_' + run.b.id.lower()))):
        raise RuntimeError('start only the real Joiner at its main menu without an Agent for the T4 Host')
    relay = state.get('relay')
    if not S.procs.matches(relay):
        raise RuntimeError('the recorded local test Relay is not running')
    if case == 'disconnect' and not has_plan(relay, disconnect_plan(args.netz, S.pair.POOR_NETWORK)):
        raise RuntimeError('the local Relay is missing the active disconnect scenario plan')
    project = bot_log = None
    try:
        if args.bot:
            project, bot_log, _ = bot_host(S, run, args, fixture_flag='--test-world-recovery')
        native_marks = {copy.id: mark_native(copy) for _, copy in parts}
        result = disconnect(S, run, args, relay, project) if case == 'disconnect' else desync(S, run, args, relay)
        offsets = {copy.id: copy.size(copy.alog) for _, copy in parts}
        for role, copy in parts:
            copy.wait('a', offsets[copy.id], rf'MP-T3-STATS origin=agent role={role} .*\bowner_violations=\d+', 75)
            owner = owner_evidence(copy, role, offsets[copy.id], native_marks[copy.id])
            if owner:
                result['failures'].append(f'{role}:{owner}')
            if any('falling back to http-debug-api' in line for line in all_agent(copy)):
                result['failures'].append(f'{role}:http-fallback')
        bundles = S.pair.collect(f'{case}-{time.time_ns()}', write_verdict=True, copies=copies, bot_log=bot_log)
        result.update(bot=args.bot, bundles={cid: folder + '.packed' for cid, folder in bundles.items()})
        Path(run.dir, f'{case}.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
        return not result['failures'], result['failures'][0] if result['failures'] else 'ok', result
    finally:
        if project is not None:
            stop_bot(project)


def run(S, case, argv):
    parser = argparse.ArgumentParser(prog=f'scenarios.py {case}', description=__doc__)
    parser.add_argument('--test-world-recovery', action='store_true')
    parser.add_argument('--netz', choices=('normal', 'schlecht'), default='normal')
    parser.add_argument('--timeout', type=int, default=130)
    parser.add_argument('--bot', action='store_true')
    parser.add_argument('--bot-save', type=Path)
    parser.add_argument('--bot-plan', type=Path)
    parser.add_argument('--world-time', type=float)
    parser.add_argument('--wire-version')
    if case == 'desync':
        parser.add_argument('--section', choices=SECTIONS, default='loot')
    args = parser.parse_args(argv)
    if not args.test_world_recovery or os.environ.get(TEST_ENV) != '1':
        parser.error(f'{TEST_ENV}=1 and --test-world-recovery are required')
    if not 30 <= args.timeout <= 480:
        parser.error('--timeout must be between 30 and 480 seconds')
    if args.bot and (os.environ.get('KCDMP_TEST_BOT') != '1' or args.bot_save is None or not args.bot_save.is_file()
                     or args.world_time is None or not 0 <= args.world_time < float('inf')):
        parser.error('--bot needs KCDMP_TEST_BOT=1, --bot-save and --world-time')
    record = S.Run(case, include_joiner=not args.bot)
    if args.bot:
        record.copies = (record.b,)
    if case == 'disconnect' or args.bot or record.step('join', S.s_join(record)):
        record.step(case, lambda: exercise(S, record, case, args))
    return record.finish()
