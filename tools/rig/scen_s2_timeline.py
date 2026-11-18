# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
"""s2-4: pair the staged sender's swing with an engine-installed action and the same body's presented pose."""

import argparse
import json
import math
import os
from pathlib import Path
import re
import subprocess
import time
from uuid import UUID

from scen_s2_lanes import BOT_DONE, read_bot_log, wait_bot_ready
from scen_s2_measure import ASSIGNED_PEER, BOT_PEER, STATS, finite_position, network_evidence, peer_ids
from scen_s2_owner import VIOLATION, mark_native


# Same known combat row as the T4 default plan in AvatarPeer/Bot.cs.
BOT_SWING_ROW = UUID('3e27732b-b152-4a27-97d2-1ebba717a588')
BOT_SWINGS = 3
SOURCE_SWING = re.compile(r'\bMP-ACTION section=outbound kind=Attack\b.*\bms=(\d+)\b')
BOT_SWING = re.compile(r'^PEER t=\S+ attack\b.*\bms=(\d+)\b')
INBOUND = re.compile(r'\bMP-RP-TIMELINE-IN (.+)$')
INSTALLED = re.compile(r'\bMP-RP-(?:TIMELINE|COMBAT)-ACTION (.+)$')
FIGURE = re.compile(r'\bMP-RP-TIMELINE figure=kcd2mp_(\d+) (.+)$')
FIELD = re.compile(r'\b([a-z][a-z0-9_]*)=([\w-]+)(?= |$)')
METRICS = {'rp_buffer_ms', 'rp_extrap_ms_max', 'rp_late_samples', 'rp_action_pos_skew_ms_p95', 'rp_timeline_pose_n',
           'rp_timeline_skew_n', 'rp_owner_n', 'owner_violations'}


def swings(lines, pattern):
    return [int(match[1]) for line in lines if (match := pattern.search(line)) and int(match[1]) <= 0xFFFFFFFF]


def tagged_rows(lines, pattern, peer):
    for line in lines:
        match = pattern.search(line)
        if not match:
            continue
        row = dict(FIELD.findall(match[1]))
        if row.get('peer') == str(peer) and row.get('kind') == '1' and row.get('phase') == '1':
            yield row


def number(row, field):
    try:
        value = int(row[field])
        return value if value >= 0 else None
    except (KeyError, ValueError):
        return None


def paired_actions(sent, receiver_lines, native_lines, peer):
    sent = set(sent)
    forwarded = {}
    for row in tagged_rows(receiver_lines, INBOUND, peer):
        source_ms, seq, event = (number(row, key) for key in ('source_ms', 'seq', 'event_host_ms'))
        if row.get('result') == 'accepted' and source_ms in sent and seq is not None and event:
            forwarded[seq, event] = source_ms
    installed = {}
    for row in tagged_rows(native_lines, INSTALLED, peer):
        seq, event, pose, skew, scope, connection = (number(row, key) for key in (
            'seq', 'event_host_ms', 'pose_host_ms', 'skew_ms', 'scope', 'connection'))
        if skew is None and 'lag_ms' in row and event and pose:
            skew = abs(event - pose)
        identity = (seq, event)
        if row.get('result') != 'installed' or identity not in forwarded or not pose or skew != abs(event - pose):
            continue
        if not re.fullmatch(r'0x[0-9a-fA-F]+', row.get('eid', '')) or not int(row['eid'], 16):
            continue
        if scope is None or connection is None:
            continue
        sample = {'source_ms': forwarded[identity], 'seq': seq, 'event_host_ms': event, 'pose_host_ms': pose,
                  'skew_ms': skew, 'eid': int(row['eid'], 16), 'scope': scope, 'connection': connection}
        if identity in installed and installed[identity] != sample:
            return []
        installed[identity] = sample
    values = list(installed.values())
    scopes = {(row['eid'], row['scope'], row['connection']) for row in values}
    return values if len(scopes) <= 1 else []


def figure_pose(lines, peer, paired):
    if not paired:
        return {}
    identity = paired[0]
    fields = ('scope', 'connection', 'buffer_ms', 'extrap_ms_max', 'late_samples', 'pose_n', 'applied_host_ms', 'target_host_ms')
    for line in reversed(lines):
        match = FIGURE.search(line)
        if not match or int(match[1]) != peer:
            continue
        raw = dict(FIELD.findall(match[2]))
        eid = raw.get('eid', '')
        if not re.fullmatch(r'0x[0-9a-fA-F]+', eid) or int(eid, 16) != identity['eid']:
            continue
        values = {name: number(raw, name) for name in fields}
        if (all(value is not None for value in values.values()) and values['pose_n'] and values['applied_host_ms'] and
                values['connection'] == identity['connection'] and values['scope'] == identity['scope']):
            return {'eid': identity['eid'], **values}
    return {}


def role_stats(lines, role):
    for line in reversed(lines):
        match = STATS.search(line)
        if match and match[1] == role:
            row = dict(FIELD.findall(match[2]))
            fields = {key: number(row, key) for key in METRICS}
            if all(value is not None for value in fields.values()):
                return fields
    return {}


def skew_p95(actions):
    values = sorted(row['skew_ms'] for row in actions)
    return values[math.ceil(len(values) * 0.95) - 1] if values else None


def skew_color(value):
    if value is None:
        return 'grey'
    if value < 20:
        return 'green'
    return 'yellow' if value < 50 else 'red'


def timeline_failures(sent, actions, stats):
    if stats.get('owner_violations', 0):
        return ['owner-violations']
    if not sent:
        return ['no-staged-swing']
    if not actions:
        return ['no-engine-installed-swing-with-paired-host-pose']
    failures = []
    if {row['source_ms'] for row in actions} != set(sent):
        failures.append('incomplete-swing-coverage')
    if not stats.get('rp_timeline_pose_n') or stats.get('rp_timeline_skew_n', 0) < len(actions) or not stats.get('rp_owner_n'):
        failures.append('timeline-or-owner-counters-unmeasured')
    if stats and not 60 <= stats['rp_buffer_ms'] <= 120:
        failures.append('buffer-outside-60-to-120ms')
    if stats and stats['rp_extrap_ms_max'] > 200:
        failures.append('extrapolation-exceeds-200ms')
    if skew_color(skew_p95(actions)) != 'green':
        failures.append('swing-p95-at-least-20ms')
    return failures


def bot_plan(position, row):
    x, y, z = (format(value, '.9g') for value in position)
    lines = [f'at 0 stand {x} {y} {z} 0', 'at 2 state combat=1']
    for i in range(BOT_SWINGS):
        start = 8 + i * 8
        yaw = '0' if i % 2 == 0 else '3.14159265'
        lines += [f'at {start} move 1 {yaw} 4', f'at {start + 3.8:g} attack {row}']
    return '\n'.join([*lines, 'end 95', ''])


def player_route(S, sender, start):
    S.rig.say(f'Kopie {sender.id}', 'Gehe mit gezogener Waffe mindestens zwei Meter und warte auf das Signal.')
    until = time.monotonic() + 45
    while time.monotonic() < until:
        position = sender.pos()
        if finite_position(position) and math.dist(start[:2], position[:2]) >= 2:
            offset = sender.size(sender.alog)
            S.rig.say(f'Kopie {sender.id}', 'Jetzt einen normalen Schlag ausführen.')
            match = sender.wait('a', offset, SOURCE_SWING, 35)
            return [int(match[1])] if match else [], math.dist(start[:2], position[:2])
        time.sleep(0.25)
    return [], None


def bot_swings(bot, log):
    deadline = time.monotonic() + 45
    while time.monotonic() < deadline and bot.poll() is None:
        sent = swings(read_bot_log(log), BOT_SWING)
        if len(sent) >= BOT_SWINGS:
            return sent
        time.sleep(0.25)
    return swings(read_bot_log(log), BOT_SWING)


def wait_pairs(receiver, offset, marks, sent, peer):
    found = []
    if not sent:
        return found
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        found = current_pairs(receiver, offset, marks, sent, peer)
        if {action['source_ms'] for action in found} == set(sent):
            return found
        time.sleep(0.25)
    return found


def current_pairs(receiver, offset, marks, sent, peer):
    forwarded = receiver.lines(receiver.alog, offset)
    per_log = [paired_actions(sent, forwarded, receiver.lines(path, start), peer)
               for path, start in marks.items() if receiver.size(path) >= start]
    return max(per_log, key=len, default=[])


def exercise(S, run, args):
    sender, receiver, role = (run.a, run.b, 'joiner') if args.sender == 'host' else (run.b, run.a, 'host')
    copies = (receiver,) if args.bot else (receiver, sender)
    state = S.pair.state()
    if args.bot and S.procs.matches(state.get(run.b.id.lower())):
        raise RuntimeError('the Bot-Joiner replaces the second game, which must be stopped')
    positions = {}
    for copy in copies:
        if not S.procs.matches(state.get(copy.id.lower())) or copy.port is None:
            raise RuntimeError(f'copy {copy.id} must be running with an in-world local player')
        position = copy.pos()
        if not finite_position(position):
            raise RuntimeError(f'copy {copy.id} did not return finite player coordinates')
        positions[copy.id] = position

    relay = state.get('relay')
    network_evidence(relay, S.pair.POOR_NETWORK, args.netz, S.procs.matches, S.Copy.lines)
    native_marks = {copy.id: mark_native(copy) for copy in copies}
    agent_marks = {copy.id: copy.size(copy.alog) for copy in copies}
    bot = None
    bot_log = None
    if args.bot:
        x, y, z = positions[receiver.id]
        plan = Path(run.dir, 'timeline-bot.txt')
        plan.write_text(bot_plan((x + 2, y, z), args.attack_row), encoding='utf-8')
        bot_log = Path(run.dir, 'bot-t4.log')
        with bot_log.open('w', encoding='utf-8') as stream:
            command = S.bot_joiner_command(run, '--scenario', str(plan.resolve()), '--duration', '175')
            bot = subprocess.Popen(command, stdout=stream, stderr=subprocess.STDOUT)

    try:
        if bot is not None:
            peer = wait_bot_ready(bot, bot_log)
            sent = bot_swings(bot, bot_log)
            distance = None
        else:
            ids = peer_ids(sender.lines(sender.alog, 0), ASSIGNED_PEER)
            if not ids:
                raise RuntimeError('source Agent has no assigned Relay ID')
            peer = ids[-1]
            sent, distance = player_route(S, sender, positions[sender.id])
        wait_pairs(receiver, agent_marks[receiver.id], native_marks[receiver.id], sent, peer)
        bot_still_joined = bot is None or bot.poll() is None
        offset = receiver.size(receiver.alog)
        pulse = receiver.wait('a', offset, rf'MP-T3-STATS origin=agent role={role} .*\brp_timeline_skew_n=\d+', 85)
        stats = role_stats(receiver.lines(receiver.alog, offset), role) if pulse else {}
    finally:
        if bot is not None:
            bot.wait()

    network = network_evidence(relay, S.pair.POOR_NETWORK, args.netz, S.procs.matches, S.Copy.lines, require_delay=True)
    actions = current_pairs(receiver, agent_marks[receiver.id], native_marks[receiver.id], sent, peer)
    per_figure = next((facts for path, start in native_marks[receiver.id].items()
                       if (facts := figure_pose(receiver.lines(path, start), peer, actions))), {})
    failures = timeline_failures(sent, actions, stats)
    if any(copy.size(copy.alog) < agent_marks[copy.id] or
           peer_ids(copy.lines(copy.alog, agent_marks[copy.id]), ASSIGNED_PEER) for copy in copies):
        failures.append('replaced-participant')
    if any(VIOLATION in line for copy in copies for path, start in native_marks[copy.id].items() for line in copy.lines(path, start)):
        failures.append('native-owner-violation')
    bot_lines = read_bot_log(bot_log) if bot is not None else []
    bot_clock_ready = None if bot is None else 'BOT-T4 role=joiner presentation_clock=ready' in bot_lines
    if bot_clock_ready is False:
        failures.append('bot-host-time-unmeasured')
    if bot is not None and (bot.returncode or not bot_still_joined or len(sent) != BOT_SWINGS or
                            peer_ids(bot_lines, BOT_PEER) != [peer] or not any(line.startswith(BOT_DONE) for line in bot_lines)):
        failures.append('bot-did-not-complete-staged-swings')

    bundles = S.pair.collect('timeline-%d' % time.time_ns(), write_verdict=True,
                             copies=tuple(copy.id for copy in copies), bot_log=bot_log)
    p95 = skew_p95(actions)
    report = {'sender': 'scripted-t4' if args.bot else args.sender, 'sender_peer': peer, 'receiver': role,
              'walk_m': distance, 'bot_clock_ready': bot_clock_ready, 'source_swing_ms': sent,
              'installed_swing_samples': actions, 'body_pose_sample_stats': per_figure, 'skew_p95_ms': p95,
              'sample_color': skew_color(p95), 'stats': stats, 'network': network, 'failures': failures,
              'bundles': {cid: folder + '.packed' for cid, folder in bundles.items()}}
    Path(run.dir, 'timeline.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    return not failures, failures[0] if failures else 'ok', report


def attack_row(value):
    try:
        row = UUID(value)
    except ValueError as error:
        raise argparse.ArgumentTypeError('expected a melee action row GUID') from error
    if not row.int:
        raise argparse.ArgumentTypeError('choose a nonzero melee action row GUID')
    return row


def run(S, argv):
    parser = argparse.ArgumentParser(prog='scenarios.py timeline', description=__doc__)
    parser.add_argument('--test-timeline', action='store_true')
    parser.add_argument('--netz', choices=('normal', 'schlecht'), default='normal')
    parser.add_argument('--sender', choices=('host', 'joiner'), default='joiner')
    parser.add_argument('--bot', action='store_true', help='use the T4 Bot-Joiner with one real host')
    parser.add_argument('--attack-row', type=attack_row, default=BOT_SWING_ROW, help='optional T4 melee action row GUID')
    args = parser.parse_args(argv)
    if not args.test_timeline or os.environ.get('KCDMP_TEST_TIMELINE') != '1':
        parser.error('KCDMP_TEST_TIMELINE=1 and --test-timeline are required')
    if args.bot and (args.sender != 'joiner' or os.environ.get('KCDMP_TEST_BOT') != '1'):
        parser.error('--bot requires KCDMP_TEST_BOT=1 and a scripted joiner')
    record = S.Run('timeline', include_joiner=not args.bot)
    if args.bot or record.step('join', S.s_join(record)):
        record.step('timeline', lambda: exercise(S, record, args))
    return record.finish()
