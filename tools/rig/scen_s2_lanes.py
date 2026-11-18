# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
"""s2-3: exercise the native real-time and bulk queues during a live test session."""

import argparse
from concurrent.futures import ThreadPoolExecutor
import json
import os
from pathlib import Path
import re
import subprocess
import time

from scen_s2_measure import ASSIGNED_PEER, BOT_PEER, STATS, finite_position, network_evidence, peer_ids
from scen_s2_owner import VIOLATION, mark_native


FRAMES = 120
RT_PER_FRAME = 30
BULK_PER_FRAME = 50
FIELD = re.compile(r'\b([a-z][a-z0-9_]*)=(\d+)(?= |$)')
STATS_FIELDS = {'edge_rt_age_ms_p95', 'edge_rt_age_ms_max', 'edge_bulk_age_ms_p95', 'pipe_busy_replies',
                'proc_awaits_per_frame_max', 'inproc_fallbacks', 'http_calls_rt', 'owner_violations',
                'edge_rt_n', 'edge_bulk_n', 'edge_proc_n', 'rp_owner_n'}
ZERO_STATS = {'proc_awaits_per_frame_max', 'http_calls_rt', 'owner_violations'}
COVERAGE = ('edge_rt_n', 'edge_bulk_n', 'edge_proc_n', 'rp_owner_n')
BOT_READY = 'BOT-T4 role=joiner ready=1 '
BOT_DONE = 'BOT-T4 role=joiner complete=1 '
PROBE = re.compile(r'\bMP-LANES nonce=(\d+) (.+)$')
PROBE_FIELDS = {'frames', 'rt_offered', 'bulk_offered', 'rt', 'bulk', 'busy', 'rt_before_bulk', 'ordered', 'valid',
                'rt_p95_ms', 'rt_max_ms', 'bulk_p95_ms'}


def rows_for_role(lines, role):
    for line in reversed(lines):
        match = STATS.search(line)
        if match and match[1] == role:
            values = {key: int(value) for key, value in FIELD.findall(match[2])}
            if STATS_FIELDS <= values.keys():
                return {key: values[key] for key in sorted(STATS_FIELDS | {'rp_agent_hop_n', 'rp_agent_hop_ms_p95'}) if key in values}
    return {}


def verdict_stats(stats):
    if not STATS_FIELDS <= stats.keys() or not all(stats[key] for key in COVERAGE):
        return ['lane-stats-unmeasured']
    failures = [key for key in sorted(ZERO_STATS) if stats[key]]
    if stats['edge_rt_age_ms_p95'] >= 34:
        failures.append('rt-p95-at-least-34ms')
    return failures


def bot_plan(position):
    x, y, z = (format(value, '.9g') for value in position)
    lines = [f'at 0 stand {x} {y} {z} 0']
    for start in range(2, 86, 8):
        lines += [f'at {start} move 0.5 0 4', f'at {start + 4} move 0.5 3.14159265 4']
    return '\n'.join([*lines, 'end 92', ''])


def read_bot_log(log):
    return log.read_text(encoding='utf-8', errors='replace').splitlines()


def wait_bot_ready(bot, log):
    deadline = time.monotonic() + 100
    while time.monotonic() < deadline and bot.poll() is None:
        lines = read_bot_log(log)
        ids = peer_ids(lines, BOT_PEER)
        if len(ids) == 1 and any(line.startswith(BOT_READY) for line in lines):
            return ids[0]
        time.sleep(0.25)
    raise RuntimeError('the T4 sender did not become ready with one assigned relay ID')


def fresh_stats(copy, role, offset):
    if copy.wait('a', offset, r'MP-T3-STATS origin=agent role=' + role + r' .*\bedge_rt_n=\d+', 90):
        return rows_for_role(copy.lines(copy.alog, offset), role)
    return {}


def probe_result(lines, nonce):
    for line in reversed(lines):
        match = PROBE.search(line)
        if match and int(match[1]) == nonce:
            fields = {key: int(value) for key, value in FIELD.findall(match[2])}
            if PROBE_FIELDS <= fields.keys():
                return fields
    return {}


def verdict_probe(probe, frames):
    if not PROBE_FIELDS <= probe.keys():
        return ['native-probe-unmeasured']
    failures = []
    if probe['valid'] != 1:
        failures.append('native-probe-scope-changed')
    if probe['frames'] != frames or probe['rt_offered'] != frames * RT_PER_FRAME or probe['bulk_offered'] != frames * BULK_PER_FRAME:
        failures.append('wrong-frame-workload')
    if probe['rt'] != probe['rt_offered']:
        failures.append('incomplete-rt-workload')
    if probe['bulk'] != probe['bulk_offered'] or probe['ordered'] != 1:
        failures.append('reliable-bulk-missing-or-out-of-order')
    if not 0 < probe['rt_before_bulk'] <= probe['bulk']:
        failures.append('no-observed-rt-priority')
    if probe['rt_p95_ms'] >= 34:
        failures.append('probe-rt-p95-at-least-34ms')
    return failures


def drive(copy, nonce, frames, marks):
    offset = copy.size(copy.alog)
    copy.cmd(f'mp_lanes --test-lanes {nonce} {frames}')
    ack = rf'\bMP-LANES agent nonce={nonce} frames={frames} started=1\b'
    if not copy.wait('a', offset, ack, 15):
        return {}, 'no-matching-agent-ack'

    deadline = time.monotonic() + 65
    while time.monotonic() < deadline:
        for path, mark in marks.items():
            if copy.size(path) < mark:
                continue
            fields = probe_result(copy.lines(path, mark), nonce)
            if fields:
                return fields, ''
        time.sleep(0.25)
    return {}, 'no-matching-native-completion'


def participant_ids(copy):
    return peer_ids(copy.lines(copy.alog, 0), ASSIGNED_PEER)


def exercise(S, run, args):
    copies = [(run.a, 'host')] if args.bot else [(run.a, 'host'), (run.b, 'joiner')]
    state = S.pair.state()
    if args.bot and S.procs.matches(state.get(run.b.id.lower())):
        raise RuntimeError('the Bot-Joiner replaces the second game, which must be stopped')
    positions = {}
    for copy, _ in copies:
        if not S.procs.matches(state.get(copy.id.lower())) or copy.port is None:
            raise RuntimeError(f'copy {copy.id} must be running with an in-world local player')
        position = copy.pos()
        if not finite_position(position) or not participant_ids(copy):
            raise RuntimeError(f'copy {copy.id} needs a live player and an assigned relay ID')
        positions[copy.id] = position

    relay = state.get('relay')
    network_evidence(relay, S.pair.POOR_NETWORK, args.netz, S.procs.matches, S.Copy.lines)
    native_marks = {copy.id: mark_native(copy) for copy, _ in copies}
    agent_marks = {copy.id: copy.size(copy.alog) for copy, _ in copies}
    nonce = time.time_ns() & 0xFFFFFFFF or 1
    bot_log = None
    bot = None
    bot_peer = None
    if args.bot:
        x, y, z = positions[run.a.id]
        plan = Path(run.dir, 'lanes-bot.txt')
        plan.write_text(bot_plan((x + 2, y, z)), encoding='utf-8')
        bot_log = Path(run.dir, 'bot-t4.log')
        with bot_log.open('w', encoding='utf-8') as stream:
            bot = subprocess.Popen(S.bot_joiner_command(run, '--scenario', str(plan.resolve()), '--duration', '180'),
                                   stdout=stream, stderr=subprocess.STDOUT)
    try:
        if bot is not None:
            bot_peer = wait_bot_ready(bot, bot_log)
        S.rig.say('Spurentest läuft. Beide Henrys bewegen sich im selben Gebiet.')
        with ThreadPoolExecutor(max_workers=len(copies)) as workers:
            jobs = {copy.id: workers.submit(drive, copy, nonce, FRAMES, native_marks[copy.id]) for copy, _ in copies}
            probes = {cid: job.result() for cid, job in jobs.items()}
            bot_during_probe = bot is None or bot.poll() is None
            offsets = {copy.id: copy.size(copy.alog) for copy, _ in copies}
            jobs = {copy.id: workers.submit(fresh_stats, copy, role, offsets[copy.id]) for copy, role in copies}
            stats = {cid: job.result() for cid, job in jobs.items()}
    finally:
        if bot is not None:
            bot.wait()

    network = network_evidence(relay, S.pair.POOR_NETWORK, args.netz, S.procs.matches, S.Copy.lines, require_delay=True)
    participants = {}
    failures = []
    for copy, role in copies:
        fields, error = probes[copy.id]
        summary = stats[copy.id]
        checks = ([error] if error else verdict_probe(fields, FRAMES)) + verdict_stats(summary)
        start = agent_marks[copy.id]
        if copy.size(copy.alog) < start or peer_ids(copy.lines(copy.alog, start), ASSIGNED_PEER):
            checks.append('replaced-participant')
        if any(VIOLATION in line for path, offset in native_marks[copy.id].items() for line in copy.lines(path, offset)):
            checks.append('native-owner-violation')
        participants[copy.id] = {'role': role, 'probe': fields, 'stats': summary, 'failures': checks}
        if summary.get('rp_agent_hop_n', 0) and summary.get('rp_agent_hop_ms_p95', 0) > 20:
            participants[copy.id]['agent_hop_followup'] = 'Agent-Hop im Spiel untersuchen, bevor über Netz-Empfang in der DLL entschieden wird.'
        failures.extend(f'{role}:{reason}' for reason in checks)

    if bot is not None and (not bot_during_probe or bot.returncode or not any(line.startswith(BOT_DONE) for line in read_bot_log(bot_log))):
        failures.append('bot-did-not-overlap-and-complete')
    bundles = S.pair.collect('lanes-%d' % time.time_ns(), write_verdict=True, copies=tuple(copy.id for copy, _ in copies), bot_log=bot_log)
    report = {'nonce': nonce, 'frames_requested': FRAMES, 'rt_per_frame': RT_PER_FRAME, 'bulk_per_frame': BULK_PER_FRAME,
              'peer': 'scripted-t4' if args.bot else 'real-pair', 'bot_peer': bot_peer, 'network': network,
              'participants': participants, 'failures': failures, 'bundles': {cid: folder + '.packed' for cid, folder in bundles.items()}}
    Path(run.dir, 'lanes.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    return not failures, failures[0] if failures else 'ok', report


def run(S, argv):
    parser = argparse.ArgumentParser(prog='scenarios.py lanes', description=__doc__)
    parser.add_argument('--test-lanes', action='store_true')
    parser.add_argument('--netz', choices=('normal', 'schlecht'), default='normal')
    parser.add_argument('--bot', action='store_true', help='use the T4 Bot-Joiner with one real host')
    args = parser.parse_args(argv)
    if not args.test_lanes or os.environ.get('KCDMP_TEST_LANES') != '1':
        parser.error('KCDMP_TEST_LANES=1 and --test-lanes are required')
    if args.bot and os.environ.get('KCDMP_TEST_BOT') != '1':
        parser.error('--bot requires KCDMP_TEST_BOT=1')
    record = S.Run('lanes', include_joiner=not args.bot)
    if args.bot or record.step('join', S.s_join(record)):
        record.step('lanes', lambda: exercise(S, record, args))
    return record.finish()
