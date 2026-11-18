# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
"""S3-1. Observe load and weather on a disposable save through console, Agent and game logs."""

import argparse
from concurrent.futures import ThreadPoolExecutor
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import time

from scen_s2_measure import ASSIGNED_PEER, finite_position, network_evidence, peer_ids
from scen_s2_owner import VIOLATION, mark_native

PHASES = ('Menu', 'Offer', 'Load', 'Reconstruct', 'Settle', 'Play', 'Exit')
FIELDS = re.compile(r'(\w+)=([^\s]+)')
SAFE_SAVE = re.compile(r'[A-Za-z0-9_-]{1,48}\Z')
SAFE_WEATHER = re.compile(r'[A-Za-z0-9_]{1,48}\Z')
GAMEPLAY = r'^Gameplay started'
JOIN = re.compile(r'MP-JOIN joiner: join (0x[0-9a-fA-F]{8})\b')
READY = re.compile(r'ready sent for join (0x[0-9a-fA-F]{8})\b')
RESUME = re.compile(r'MP-JOIN resume join=(0x[0-9a-fA-F]{8}) reason=ready\b')
T4_WORLD = re.compile(r'BOT-T4 role=joiner ready=1 world=(\d+)\b')
WEATHER_EXECUTION = "ApplyWeather '%s' blend=0 (blending)"
TEST_ENV = 'KCDMP_TEST_LOAD_SEQUENCE'
LOAD_STATS = {'load_phase', 'load_gen', 'load_host_world', 'load_phase_native', 'load_native_gen',
              'load_native_observations', 'owner_violations'}


def rows(lines, marker, role=None):
    for index, line in enumerate(lines):
        if marker + ' ' not in line:
            continue
        fields = dict(FIELDS.findall(line.split(marker + ' ', 1)[1]))
        if role is None or fields.get('role') == role:
            yield index, fields


def number(fields, key):
    value = fields.get(key, '')
    if re.fullmatch(r'\d+|0x[0-9a-fA-F]+', value):
        return int(value, 16 if value.startswith('0x') else 10)
    return None


def transitions(lines, role):
    return [
        (index, fields['phase'], number(fields, 'gen'), number(fields, 'world'))
        for index, fields in rows(lines, 'MP-LOAD-SEQUENCE', role)
        if fields.get('phase') in PHASES and number(fields, 'gen') is not None and number(fields, 'world') is not None
    ]


def final_play(lines, role):
    recorded = transitions(lines, role)
    if not recorded or recorded[-1][1] != 'Play' or not recorded[-1][3]:
        return None
    terminal = recorded[-1]
    play_start = next((i for i, phase, gen, world in recorded
                       if phase == 'Play' and gen == terminal[2] and world == terminal[3]), terminal[0])
    return play_start, *terminal[1:]


def load_sequence(lines, role, previous=None):
    """One ordered load in its own local generation. During Offer, the world id can still be unknown."""
    events = transitions(lines, role)
    terminal = final_play(lines, role)
    detail = {'events': events, 'play': terminal, 'failures': []}
    failures = detail['failures']
    if terminal is None:
        failures.append('play-missing')
        return detail
    _, _, generation, world = terminal
    if previous is not None and (generation <= previous[2] or world == previous[3]):
        failures.append('play-reused-old-generation-or-world')
    phases = []
    for _, phase, gen, token in events:
        if gen != generation:
            if previous is not None and gen == previous[2] and phase == 'Exit' and not phases:
                continue
            if phase == 'Menu' and not phases and gen < generation:
                continue
            failures.append('unexpected-generation-switch')
            continue
        if phase in ('Reconstruct', 'Settle', 'Play') and token not in (0, world):
            failures.append('mixed-world-during-apply')
        if not phases or phases[-1] != phase:
            phases.append(phase)
    if any(PHASES.index(before) >= PHASES.index(after) for before, after in zip(phases, phases[1:])):
        failures.append('phase-order-invalid')
    required = {'Load', 'Reconstruct', 'Settle', 'Play'}
    if role == 'joiner':
        required.add('Offer')
    if required.difference(phases):
        failures.append('phases-missing:' + ','.join(phase for phase in PHASES if phase in required.difference(phases)))
    detail['failures'] = list(dict.fromkeys(failures))
    return detail


def fresh_join(agent, game):
    """Pair the joiner's most recent real request/ready with the host's matching resume."""
    requests = [match[1].lower() for line in agent if (match := JOIN.search(line))]
    ready = {match[1].lower() for line in agent if (match := READY.search(line))}
    resumed = {match[1].lower() for line in game if (match := RESUME.search(line))}
    return requests[-1] if requests and requests[-1] in ready & resumed else None


def weather_result(agent, game, profile, play):
    events = [(index, fields) for index, fields in rows(agent, 'MP-LOAD-WEATHER', 'joiner') if fields.get('profile') == profile]
    wrong_timing = []
    applied = []
    deferred = []
    for index, fields in events:
        same_generation = play and number(fields, 'gen') == play[2] and number(fields, 'world') == play[3]
        if fields.get('result') == 'applied':
            if same_generation and fields.get('phase') == 'Play' and index > play[0]:
                applied.append(index)
            else:
                wrong_timing.append(index)
        elif (fields.get('result') == 'deferred' and same_generation and index < play[0]
              and fields.get('phase') in ('Offer', 'Load', 'Reconstruct', 'Settle')):
            deferred.append(index)
    game_blend = any(WEATHER_EXECUTION % profile in line for line in game)
    failures = []
    if wrong_timing:
        failures.append('weather-applied-in-wrong-phase-or-world')
    if not deferred:
        failures.append('weather-during-load-unmeasured')
    if len(applied) != 1:
        failures.append('weather-after-play-missing-or-duplicate')
    if not game_blend:
        failures.append('weather-game-blend-unconfirmed')
    return {'profile': profile, 'events': events, 'game_blend': game_blend, 'failures': failures}


def role_stats(lines, role):
    load = {}
    owner = None
    for _, fields in rows(lines, 'MP-T3-STATS', role):
        if fields.get('origin') != 'agent':
            continue
        if fields.get('section') == 'load' and number(fields, 'load_gen') is not None:
            load = {key: number(fields, key) for key in fields
                    if key.startswith(('load_', 'stale_gen_')) and number(fields, key) is not None}
        if (value := number(fields, 'owner_violations')) is not None:
            owner = value
    if owner is not None:
        load['owner_violations'] = owner
    return load


def stale_delta(before, after):
    result = {}
    for key in ('stale_gen_dropped', 'stale_gen_native_dropped'):
        a, b = before.get(key), after.get(key)
        result[key] = b - a if a is not None and b is not None and b >= a else None
    return result


def stats_failures(stats, play, native_conflict):
    failures = []
    if native_conflict or stats.get('owner_violations', 0) > 0:
        failures.append('owner-violations')
    elif 'owner_violations' not in stats:
        failures.append('owner-counter-unmeasured')
    if (play is None or stats.get('load_phase') != PHASES.index('Play') or stats.get('load_gen') != play[2]
            or stats.get('load_host_world') != play[3] or stats.get('load_phase_native') != PHASES.index('Play')
            or stats.get('load_native_gen') != play[2] or not stats.get('load_native_observations', 0)):
        failures.append('play-not-observed-in-current-agent-and-native-generation')
    return failures


def wait_until(call, timeout, *, alive=None):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        value = call()
        if value:
            return value
        if alive is not None and not alive():
            break
        time.sleep(0.25)
    return None


def read_agent(copy, mark):
    return copy.lines(copy.alog, mark['a'])


def read_game(copy, mark):
    return copy.lines(copy.klog, mark['k'])


def observe_stats(copy, role, mark, play):
    result = {}

    def read():
        nonlocal result
        result = role_stats(read_agent(copy, mark), role)
        if result.get('owner_violations', 0) > 0:
            return True
        return LOAD_STATS <= result.keys() and (play is None or not stats_failures(result, play, False))

    wait_until(read, 90)
    return result


def stages(copies, before):
    participants = {}
    failures = []
    after = {role: copy.mark() for role, copy in copies}
    sequence = {role: load_sequence(read_agent(copy, before[role][1]), role, before[role][0]) for role, copy in copies}
    with ThreadPoolExecutor(max_workers=len(copies)) as workers:
        waits = {role: workers.submit(observe_stats, copy, role, after[role], sequence[role]['play'])
                 for role, copy in copies}
        stats_by_role = {role: wait.result() for role, wait in waits.items()}
    for role, copy in copies:
        initial, mark, native_marks, baseline = before[role]
        agent = read_agent(copy, mark)
        game = read_game(copy, mark)
        result = load_sequence(agent, role, initial)
        stats = stats_by_role[role]
        native_conflict = any(VIOLATION in line for path, offset in native_marks.items() for line in copy.lines(path, offset))
        result.update(gameplay=any(re.search(GAMEPLAY, line) for line in game), metrics=stats, stale_frame_delta=stale_delta(baseline, stats))
        result['failures'] += stats_failures(stats, result['play'], native_conflict)
        if not result['gameplay']:
            result['failures'].append('gameplay-start-unconfirmed')
        if any('falling back to http-debug-api' in line for line in agent):
            result['failures'].append('http-fallback')
        if not finite_position(copy.pos()):
            result['failures'].append('live-local-player-unconfirmed')
        participants[role] = result
        failures.extend(f'{role}:{reason}' for reason in result['failures'])
    return participants, failures


def snapshot(copy, role, *, initial_play):
    mark = copy.mark()
    lines = copy.lines(copy.alog, 0)
    phase = final_play(lines, role)
    if not initial_play:
        # The new Agent rotates agent.log on launch. The existing game log continues.
        mark['a'] = 0
        lines = []
    if initial_play and (phase is None or not finite_position(copy.pos())):
        raise RuntimeError(f'{role}: a live local player in Play is required before reloading')
    return phase if initial_play else None, mark, mark_native(copy), role_stats(lines, role)


def request_reload(S, host, args):
    why = S.pair.user_folder_refusal(host.id)
    if why:
        raise RuntimeError(f'copy {host.id}: {why}')
    folder = S.pair.cfg_user_folder(host.p['root'])
    save = Path(S.pair.mirror.SAVED_GAMES, folder, 'saves', f'playline{args.playline}', args.save + '.whs')
    if not save.is_file() or save.stat().st_size == 0:
        raise RuntimeError(f'the disposable save was not found in the host copy: {save.name}')
    try:
        host.cmd(f'wh_sys_LoadGame {args.playline} {args.save}')
    except (OSError, TimeoutError) as error:
        S.rig.note('load-request', copy=host.id, detail=str(error))
        # A busy console can lose its response while the actual load has already begun.


def wait_for_play(copy, role, mark):
    return copy.wait('a', mark['a'], r'MP-LOAD-SEQUENCE role=' + role + r' .*\bphase=Play\b', 300)


def bot_join(S, run, label, origin):
    if not finite_position(origin):
        raise RuntimeError('the Host cannot provide a finite player position')
    plan = Path(run.dir, label + '-plan.txt')
    x, y, z = origin
    plan.write_text(f'at 0 stand {x + 2:.3f} {y:.3f} {z:.3f} 0\nend 3\n', encoding='utf-8')
    logfile = Path(run.dir, label + '.log')
    command = S.bot_joiner_command(run, '--scenario', str(plan.resolve()), '--duration', '120')
    with logfile.open('w', encoding='utf-8') as stream:
        result = subprocess.run(command, stdout=stream, stderr=subprocess.STDOUT)
    lines = logfile.read_text(encoding='utf-8', errors='replace').splitlines()
    worlds = [int(match[1]) for line in lines if (match := T4_WORLD.search(line))]
    completed = not result.returncode and len(worlds) == 1 and any('BOT-T4 role=joiner complete=1 ' in line for line in lines)
    return (worlds[0] if completed else None), logfile


def stop_bot(process):
    if process.poll() is not None:
        return
    # The wrapper and dotnet child share a new process group, so the relay peer also stops.
    process.send_signal(signal.CTRL_BREAK_EVENT)
    process.wait(timeout=30)


def bot_weather(S, run, args, joiner):
    tool = Path(S.HERE, '..', 'bothost', 'bot-joiner').resolve()
    command = [sys.executable, '-X', 'utf8', str(tool), '--test-bot', '--test-load-sequence', '--role', 'host',
               '--port', str(S.pair.RELAY_PORT), '--save', str(args.bot_save.resolve()),
               '--world-time', str(args.world_time), '--duration', '600', '--weather-during-load', args.profile]
    if args.wire_version:
        command += ['--wire-version', args.wire_version]
    logfile = Path(run.dir, 'bot-t4.log')
    with logfile.open('w', encoding='utf-8') as stream:
        process = subprocess.Popen(command, stdout=stream, stderr=subprocess.STDOUT,
                                   creationflags=subprocess.CREATE_NEW_PROCESS_GROUP)
    def alive():
        return process.poll() is None

    def started(timeout):
        return wait_until(lambda: any('BOT-T4 role=host started=1 ' in line for line in S.Copy.lines(logfile, 0)),
                          min(timeout, 120), alive=alive)

    try:
        status = S.pair.join_from_menu(joiner.id, joiner.p, S.pair.state(), joiner.id.lower(), joiner.port,
                                      wait_host=started)
        if status:
            raise RuntimeError('the real Joiner could not connect to the test Bot-Host')
        mark = {'a': 0}
        if not wait_for_play(joiner, 'joiner', mark):
            raise RuntimeError('the real Joiner did not reach Play with the test Bot-Host')
        if not wait_until(lambda: any('BOT-T4 role=host ready=1 ' in line for line in S.Copy.lines(logfile, 0)),
                          30, alive=alive):
            raise RuntimeError('the test Bot-Host did not receive Ready')
        return process, logfile
    except BaseException:
        stop_bot(process)
        raise


def arm_weather(S, host, mark, profile):
    try:
        host.cmd('mp_test_weatherload --test-load-sequence ' + profile)
    except OSError as error:
        S.rig.note('load-weather-request', copy=host.id, detail=str(error))
    pattern = r'MP-LOAD-WEATHER-TEST role=host .*profile=' + re.escape(profile) + r' result=armed\b'
    if not host.wait('a', mark['a'], pattern, 15):
        raise RuntimeError('the live disposable Host did not arm a weather test')


def weather_sent(lines, marker, profile, play, join, peer, *, bot=False):
    result_key = 'load_weather' if bot else 'result'
    sent = [fields for _, fields in rows(lines, marker, 'host')
            if fields.get(result_key) == 'sent' and fields.get('profile') == profile]
    if (len(sent) != 1 or not play or not join or peer is None or number(sent[0], 'world') != play[3]
            or number(sent[0], 'peer') != peer or sent[0].get('join') != join
            or not bot and (number(sent[0], 'gen') != play[2] or number(sent[0], 'blend') != 0)):
        return None
    return sent[0]


def exercise(S, run, name, args):
    is_weather = name == 'weatherload'
    copies = ([('joiner', run.b)] if args.bot and is_weather else [('host', run.a)] if args.bot
              else [('host', run.a), ('joiner', run.b)])
    state = S.pair.state()
    for _, copy in copies:
        if copy.port is None or not S.procs.matches(state.get(copy.id.lower())):
            raise RuntimeError(f'copy {copy.id}: a recorded real game instance is required')
    if args.bot and S.procs.matches(state.get((run.a if is_weather else run.b).id.lower())):
        raise RuntimeError('stop the game that T4 replaces before starting this scenario')
    relay = state.get('relay')
    network_evidence(relay, S.pair.POOR_NETWORK, args.netz, S.procs.matches, S.Copy.lines)
    if args.bot and is_weather and S.procs.matches(state.get('agent_' + run.b.id.lower())):
        raise RuntimeError('start the real Joiner at its main menu without an Agent for Bot-Host weatherload')

    before = {role: snapshot(copy, role, initial_play=not (args.bot and is_weather)) for role, copy in copies}
    bot = None
    bot_log = None
    bot_proof = {}
    try:
        if args.bot and is_weather:
            bot, bot_log = bot_weather(S, run, args, run.b)
        elif args.bot:
            old_world, old_log = bot_join(S, run, 'bot-before', run.a.pos())
            before['host'] = snapshot(run.a, 'host', initial_play=True)
            request_reload(S, run.a, args)
            if not wait_for_play(run.a, 'host', before['host'][1]):
                raise RuntimeError('real Host did not reach Play after reload')
            new_world, bot_log = bot_join(S, run, 'bot-after', run.a.pos())
            bot_proof = {'old_world': old_world, 'new_world': new_world, 'first_log': str(old_log),
                         'mode': 'two-fresh-t4-staged-transfers'}
        else:
            if is_weather:
                arm_weather(S, run.a, before['host'][1], args.profile)
            request_reload(S, run.a, args)
            for role, copy in copies:
                wait_for_play(copy, role, before[role][1])

        participants, failures = stages(copies, before)
        host_play = participants.get('host', {}).get('play')
        joiner_play = participants.get('joiner', {}).get('play')
        if not args.bot:
            if not host_play or not joiner_play or host_play[3] != joiner_play[3]:
                failures.append('host-and-joiner-world-do-not-match')
            join = fresh_join(read_agent(run.b, before['joiner'][1]), read_game(run.a, before['host'][1]))
            if not join:
                failures.append('new-join-ready-and-resume-unconfirmed')
        elif is_weather:
            join = None
        else:
            join = None
            if (not host_play or not old_world or not new_world or old_world == new_world
                    or new_world != host_play[3] or old_world != before['host'][0][3]):
                failures.append('t4-staged-worlds-unconfirmed')

        weather = None
        if is_weather:
            weather = weather_result(read_agent(run.b, before['joiner'][1]), read_game(run.b, before['joiner'][1]),
                                     args.profile, joiner_play)
            failures.extend(weather['failures'])
            agent = read_agent(run.b, before['joiner'][1])
            if args.bot:
                ids = peer_ids(agent, ASSIGNED_PEER)
                requests = [match[1].lower() for line in agent if (match := JOIN.search(line))]
                join = requests[-1] if requests else None
                peer = ids[-1] if len(ids) == 1 else None
                sent = weather_sent(S.Copy.lines(bot_log, 0), 'BOT-T4', args.profile, joiner_play, join, peer, bot=True)
                bot_proof = {'sent': sent, 'mode': 't4-test-host'}
            else:
                ids = peer_ids(run.b.lines(run.b.alog, 0), ASSIGNED_PEER)
                peer = ids[-1] if ids else None
                sent = weather_sent(read_agent(run.a, before['host'][1]), 'MP-LOAD-WEATHER-TEST',
                                    args.profile, host_play, join, peer)
            weather['sent'] = sent
            if sent is None:
                failures.append('weather-source-unconfirmed')

        network = network_evidence(relay, S.pair.POOR_NETWORK, args.netz, S.procs.matches, S.Copy.lines, require_delay=True)
        bundles = S.pair.collect(f'{name}-{time.time_ns()}', write_verdict=True,
                                 copies=tuple(copy.id for _, copy in copies), bot_log=bot_log)
        detail = {'participants': participants, 'join': join, 'bot': bot_proof, 'weather': weather, 'network': network,
                  'failures': failures, 'bundles': {cid: folder + '.packed' for cid, folder in bundles.items()}}
        Path(run.dir, f'{name}.json').write_text(json.dumps(detail, indent=2), encoding='utf-8')
        return not failures, failures[0] if failures else 'ok', detail
    finally:
        if bot is not None:
            stop_bot(bot)


def run(S, name, argv):
    parser = argparse.ArgumentParser(prog=f'scenarios.py {name}', description=__doc__)
    parser.add_argument('--netz', choices=('normal', 'schlecht'), default='normal')
    parser.add_argument('--test-load-sequence', action='store_true')
    parser.add_argument('--bot', action='store_true', help='use T4 for one endpoint')
    parser.add_argument('--playline', type=int)
    parser.add_argument('--save', help='test-save basename in the real Host copy')
    if name == 'weatherload':
        parser.add_argument('--profile', default='foggy_drizzly_light')
        parser.add_argument('--bot-save', type=Path, help='external disposable .whs for the T4 Host')
        parser.add_argument('--world-time', type=float, help='Calendar.GetWorldTime() of the T4 Host save')
        parser.add_argument('--wire-version', help='override the T4 Host VERSION[+BUILD_ID] to match the rig release')
    args = parser.parse_args(argv)
    if not args.test_load_sequence or os.environ.get(TEST_ENV) != '1':
        parser.error(f'{TEST_ENV}=1 and --test-load-sequence are required')
    if args.bot and os.environ.get('KCDMP_TEST_BOT') != '1':
        parser.error('--bot requires KCDMP_TEST_BOT=1')
    if name == 'weatherload' and not SAFE_WEATHER.fullmatch(args.profile):
        parser.error('--profile must be a game weather profile name')
    if args.bot and name == 'weatherload':
        if args.bot_save is None or not args.bot_save.is_file() or args.world_time is None or not 0 <= args.world_time < float('inf'):
            parser.error('--bot needs --bot-save and --world-time from a disposable test save')
    elif args.playline is None or not 1 <= args.playline <= 255 or not SAFE_SAVE.fullmatch(args.save or ''):
        parser.error('--playline and --save must identify a disposable save in the real Host copy')

    record = S.Run(name, include_joiner=not args.bot)
    if args.bot and name == 'weatherload':
        record.copies = (record.b,)
    if args.bot or record.step('join', S.s_join(record)):
        record.step(name, lambda: exercise(S, record, name, args))
    return record.finish()
