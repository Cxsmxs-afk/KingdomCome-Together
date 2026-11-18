# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
"""s2-1: measure a fixed staircase route with a real receiver and a real or scripted sender."""

import argparse
from dataclasses import dataclass
import json
import math
import os
from pathlib import Path
import re
import statistics
import subprocess
import sys
import time


DURATION = 60
FAULT_FIRED = re.compile(r'RELAY-FAULT rpmeasure-netz-schlecht fired kind=lag role=joiner dir=both .*\bms=150 jitter_ms=35\b')
FAULT_MODE = re.compile(r'RELAY-FAULTS loaded plan=')
FAULT_DELAY = re.compile(r'RELAY-FAULTS .*\bplan=netz-schlecht\.json\b.*\bdelayed=([1-9]\d*)/')
STATS = re.compile(r'\bMP-T3-STATS origin=agent role=(host|joiner) (.+)$')
FIGURE = re.compile(r'\bMP-RP-MEASURE figure=kcd2mp_(\d+) gen=(\d+) clock=same_pc_qpc (.+)$')
ASSIGNED_PEER = re.compile(r'\bConnected! Assigned id=(\d+) \(protocol')
BOT_PEER = re.compile(r'^PEER connected id=(\d+) release=')
COUNTER = re.compile(r'\b((?:rp_[a-z0-9_]+)|owner_violations)=(\d+)(?= |$)')


@dataclass(frozen=True)
class Route:
    points: tuple
    stairs: tuple


def parse_route(value):
    def number(v):
        return type(v) in (int, float) and -100_000 <= v <= 100_000

    if not isinstance(value, dict) or set(value) != {'points', 'stairs'}:
        raise ValueError('route needs exactly points and stairs')
    points, stairs = value['points'], value['stairs']
    if not isinstance(points, list) or not 4 <= len(points) <= 40:
        raise ValueError('route needs 4–40 points')
    if any(not isinstance(p, list) or len(p) != 4 or not all(map(number, p)) for p in points):
        raise ValueError('each route point must be [seconds, x, y, z] with finite values')
    if points[0][0] != 0 or points[-1][0] != DURATION:
        raise ValueError('route must cover exactly 0–60 seconds')
    if not isinstance(stairs, list) or len(stairs) != 2 or not all(map(number, stairs)) or stairs[0] >= stairs[1]:
        raise ValueError('stairs must be [start seconds, end seconds]')
    if any(s not in {p[0] for p in points} for s in stairs):
        raise ValueError('mark both stairs endpoints as route points')
    rises = []
    for a, b in zip(points, points[1:]):
        dt = b[0] - a[0]
        horizontal = math.dist(a[1:3], b[1:3])
        dz = b[3] - a[3]
        if dt < 0.1 or horizontal / dt > 6 or abs(dz) / dt > 2 or (not horizontal and dz != 0):
            raise ValueError('route segments must be ordered and traverse the height without vertical jumps')
        if stairs[0] <= a[0] < b[0] <= stairs[1] and abs(dz) > 0.01:
            rises.append(dz)
    if len(rises) < 3 or not all(0.08 <= dz <= 0.35 for dz in rises) and not all(-0.35 <= dz <= -0.08 for dz in rises):
        raise ValueError('stairs need at least three rises or three descents of 0.08–0.35 m in one direction')
    return Route(tuple(map(tuple, points)), tuple(stairs))


def read_route(path):
    with open(path, 'rb') as file:
        data = file.read(16385)
    if len(data) > 16384:
        raise ValueError('route file exceeds 16 KiB')
    return parse_route(json.loads(data))


def bot_plan(route):
    lines = []
    yaw = 0.0
    for a, b in zip(route.points, route.points[1:]):
        t, x, y, z = a
        dt = b[0] - t
        speed = math.dist(a[1:3], b[1:3]) / dt
        if speed:
            yaw = math.atan2(x - b[1], b[2] - y)
        # Anchor each segment to the same waypoint as the game timer. A
        # scheduler tick near a turn must not accumulate positional drift.
        lines.append(f'at {t:.9g} stand {x:.9g} {y:.9g} {z:.9g} {yaw:.9g}')
        if speed:
            lines.append(f'at {t:.9g} move {speed:.9g} {yaw:.9g} {dt:.9g} {b[3]:.9g}')
    return '\n'.join([*lines, f'end {DURATION}', ''])


LUA_TICK = '''function RPMeasureTick(r)
if RPMeasureRoute~=r or player~=r.player then return end
local t=math.min(60,System.GetCurrTime()-r.start)
while r.i<#r.p-1 and r.p[r.i+1][1]<=t do r.i=r.i+1 end
local a,b=r.p[r.i],r.p[r.i+1]; local u=(t-a[1])/(b[1]-a[1])
local x,y,z=a[2]+u*(b[2]-a[2]),a[3]+u*(b[3]-a[3]),a[4]+u*(b[4]-a[4])
if r.drive then
  local dx,dy=b[2]-a[2],b[3]-a[3]
  if dx~=0 or dy~=0 then player:SetWorldAngles({x=0,y=0,z=math.atan2(-dx,dy)}) end
  player:SetWorldPos({x=x,y=y,z=z})
end
if t>=r.stair1 and t<=r.stair2 then
  local q=player:GetWorldPos(); r.stairs=r.stairs+1
  if (q.x-x)^2+(q.y-y)^2<=4 and math.abs(q.z-z)<=0.6 then r.near=r.near+1 end
end
if t<60 then Script.SetTimer(50,function() RPMeasureTick(r) end); return end
local q=player:GetWorldPos()
System.LogAlways(string.format("[RPMEASURE-DONE] %s %d %d %.4f",r.tag,r.near,r.stairs,(q.x-x)^2+(q.y-y)^2+(q.z-z)^2))
RPMeasureRoute=nil
end'''


def route_commands(route, tag, drive):
    yield f'RPMeasureRoute={{tag="{tag}",p={{}},stair1={route.stairs[0]:.9g},stair2={route.stairs[1]:.9g},drive={str(drive).lower()}}}'
    for start in range(0, len(route.points), 6):
        yield '; '.join(f'RPMeasureRoute.p[{i + 1}]={{{",".join(format(v, ".9g") for v in p)}}}'
                        for i, p in enumerate(route.points[start:start + 6], start))
    yield ' '.join(LUA_TICK.split())


def finite_position(position):
    return position is not None and len(position) == 3 and all(map(math.isfinite, position))


def game_route(sender, route, tag, drive, say, arm_window):
    if sender.port is None:
        raise RuntimeError('source game console is unavailable')
    original = sender.pos()
    if not finite_position(original) or math.dist(original, route.points[0][1:]) > 3:
        raise RuntimeError('source player must be within 3 m of the first waypoint')
    try:
        for command in route_commands(route, tag, drive):
            if not sender.lua(command): raise RuntimeError('route preparation was not acknowledged')
        arm_window()
        off = sender.size(sender.klog)
        start = 'local r=RPMeasureRoute; if r and not r.start then r.start=System.GetCurrTime(); r.player=player; r.i=1; r.near=0; r.stairs=0; RPMeasureTick(r) end'
        if not sender.lua(start): raise RuntimeError('route start was not acknowledged')
        if not drive: say('60 s laufen. Start jetzt.', 'Treppenabschnitt bei %.1f bis %.1f s' % route.stairs)
        done = sender.wait('k', off, r'\[RPMEASURE-DONE\] ' + tag + r' (\d+) (\d+) ([\d.]+)', 80)
        if not done: raise RuntimeError('the game did not complete its 60 s route')
        near, total, distance_sq = int(done[1]), int(done[2]), float(done[3])
        if total < 3 or near / total < 0.75 or distance_sq > 9:
            raise RuntimeError(f'route not covered: {near}/{total} stair frames, finish error {math.sqrt(distance_sq):.2f} m')
        return {'stairstep_readbacks': near, 'stairstep_samples': total, 'finish_error_m': math.sqrt(distance_sq)}
    finally:
        sender.lua_nowait(f'if RPMeasureRoute and RPMeasureRoute.tag=="{tag}" then RPMeasureRoute=nil end')


def network_evidence(relay, poor_plan, profile, is_running, read, *, require_delay=False):
    if not is_running(relay):
        raise RuntimeError('the recorded test relay is not running')
    expected = os.path.normcase(os.path.realpath(poor_plan)) if profile == 'schlecht' else ''
    actual = relay.get('fault_plan')
    if actual:
        actual = os.path.normcase(os.path.realpath(actual))
    if actual != expected:
        raise RuntimeError(f'start the relay with pair.py relay --netz {profile} before the join')
    lines = read(relay['relay_log'], relay['relay_log_offset'])
    fired = next((line for line in lines if FAULT_FIRED.search(line)), None)
    if profile == 'schlecht' and require_delay and not fired:
        raise RuntimeError('the 150 ms / 35 ms jitter relay rule did not fire in this relay launch')
    if profile == 'normal' and any(FAULT_MODE.search(line) for line in lines):
        raise RuntimeError('normal profile has active relay fault mode')
    delayed = max((int(m[1]) for line in lines if (m := FAULT_DELAY.search(line))), default=0)
    if profile == 'schlecht' and require_delay and delayed <= 0:
        raise RuntimeError('the relay reported no frames actually delayed in this launch')
    return {'profile': profile, 'relay_evidence': fired, 'relay_delayed_frames': delayed}


def last_stats(lines, role):
    for line in reversed(lines):
        match = STATS.search(line)
        if match and match[1] == role:
            values = {k: int(v) for k, v in COUNTER.findall(match[2])}
            if 'rp_e2e_n' in values:
                return values
    return {}


def peer_ids(lines, pattern):
    return [int(match[1]) for line in lines if (match := pattern.search(line)) and int(match[1]) <= 255]


def figure_stats(lines, peer):
    generation = None
    latest = {}
    for line in lines:
        match = FIGURE.search(line)
        if match and int(match[1]) == peer:
            values = {key: int(value) for key, value in COUNTER.findall(match[3])}
            count = values.get('rp_e2e_n', 0)
            if generation is not None and (int(match[2]) != generation or count < latest.get('rp_e2e_n', 0)):
                return None
            generation, latest = int(match[2]), values
    if not latest.get('rp_e2e_n', 0):
        return None
    return {'figure': f'kcd2mp_{peer}', 'world_generation': generation, **latest}


def paired_steps(source_rows, receiver_rows):
    import scoreboard

    def observed(rows):
        results = []
        for segment in scoreboard.stretches(rows):
            cycle = scoreboard.period(segment)
            if cycle:
                results.append((statistics.mean(row[2] for row in segment), cycle))
        return results

    ratios = []
    remote = observed(receiver_rows)
    for speed, cycle in observed(source_rows):
        if not remote: break
        index = min(range(len(remote)), key=lambda i: abs(remote[i][0] - speed))
        if abs(remote[index][0] - speed) <= 0.15 * speed:
            _, received_cycle = remote.pop(index)
            ratios.append(round(received_cycle / cycle * 1000))
    return ((len(ratios), round(statistics.median(ratios))) if ratios else (0, None))


def natural_route(S, sender, receiver, route, tag, arm_window):
    import scoreboard

    name_tag = 'RPMEASURE-SOURCE'
    name = sender.ask('System.LogAlways("[' + name_tag + '] " .. tostring(player:GetName()))', name_tag)
    agent_offsets = {copy.id: copy.size(copy.alog) for copy in (sender, receiver)}
    ids = peer_ids(sender.lines(sender.alog, 0), ASSIGNED_PEER)
    offsets = {}
    try:
        for copy in (sender, receiver):
            if all(copy.lua(chunk) for chunk in S.bones_chunks()) and copy.lua('W206Bones_Start(10,50,"root,lfoot,rfoot")'):
                offsets[copy.id] = copy.size(copy.klog)
        journey = game_route(sender, route, tag, False, S.rig.say, arm_window)
        ends = {copy.id: copy.size(copy.klog) for copy in (sender, receiver)}
    finally:
        for copy in (sender, receiver): copy.lua_nowait('if W206Bones_Stop then W206Bones_Stop() end')
    if not name or not ids or len(offsets) != 2 or any(ends[cid] < offset for cid, offset in offsets.items()):
        return journey, (0, None)
    # A replaced connection cannot be attributed to the former figure name.
    for copy in (sender, receiver):
        start = agent_offsets[copy.id]
        if copy.size(copy.alog) < start or any('Connected! Assigned id=' in line for line in copy.lines(copy.alog, start)):
            return journey, (0, None)
    source, _ = scoreboard.bone_rows(sender.klog, offsets[sender.id], ends[sender.id])
    display, _ = scoreboard.bone_rows(receiver.klog, offsets[receiver.id], ends[receiver.id])
    return journey, paired_steps(source.get(name, []), display.get(f'kcd2mp_{ids[-1]}', []))


def clock(copy, turn):
    offset = copy.size(copy.alog)
    copy.cmd('mp_rp_measure_clock ' + turn)
    if not copy.wait('a', offset, r'MP-RP-MEASURE clock=' + turn + r' status=ok\b', 12):
        raise RuntimeError('shared clock setting did not receive DLL confirmation: ' + turn)


def measure(S, run, args, route):
    sender, receiver = (run.a, run.b) if args.sender == 'host' else (run.b, run.a)
    role = 'joiner' if args.sender == 'host' else 'host'
    copies = (run.a.id,) if args.bot else (run.a.id, run.b.id)
    state = S.pair.state()
    if receiver.port is None or any(not S.procs.matches(state.get(cid.lower())) for cid in copies):
        raise RuntimeError('every real game must be recorded as running in this same-PC rig')
    if args.bot and S.procs.matches(state.get(run.b.id.lower())):
        raise RuntimeError('the Bot-Joiner replaces the second game, which must be stopped')
    relay = state.get('relay')
    network_evidence(relay, S.pair.POOR_NETWORK, args.netz, S.procs.matches, S.Copy.lines)
    source = None
    armed = False
    figure_offsets = {}
    link_offsets = {}

    def arm_window():
        nonlocal armed, source
        if not finite_position(receiver.pos()):
            raise RuntimeError('receiver must return a finite position for an in-world local player')
        for copy in (receiver,) if args.bot else (sender, receiver):
            link_offsets[copy.id] = copy.size(copy.alog)
        if not args.bot:
            ids = peer_ids(sender.lines(sender.alog, 0), ASSIGNED_PEER)
            if not ids: raise RuntimeError('source Agent has no assigned Relay ID')
            source = ids[-1]
        # Clock-on resets the DLL ledger, so this route's counter starts at zero.
        armed = True
        clock(receiver, 'on')
        native_log = os.path.join(receiver.p['dll_dir'], f'kcdmp-native-{receiver.id}.log')
        figure_offsets.update((path, receiver.size(path)) for path in (native_log, receiver.nlog))

    try:
        bot_log = None
        rp_step = None
        if args.bot:
            plan = Path(run.dir, 'rpmeasure-route.txt')
            plan.write_text(bot_plan(route), encoding='utf-8')
            bot_log = Path(run.dir, 'bot-t4.log')
            command = S.bot_joiner_command(run, '--scenario', str(plan.resolve()), '--duration', '240')
            arm_window()
            with bot_log.open('w', encoding='utf-8') as stream:
                result = subprocess.run(command, stdout=stream, stderr=subprocess.STDOUT)
            bot_lines = bot_log.read_text(encoding='utf-8', errors='replace').splitlines()
            if result.returncode or not any(line.startswith('BOT-T4 role=joiner complete=1 ') for line in bot_lines):
                raise RuntimeError(f'the T4 route did not complete (exit {result.returncode})')
            ids = peer_ids(bot_lines, BOT_PEER)
            if len(ids) != 1: raise RuntimeError('the T4 route must have one assigned Relay ID')
            source = ids[0]
            journey = {'bot_log': str(bot_log)}
        elif args.spieler:
            journey, (n, ratio) = natural_route(S, sender, receiver, route, str(time.time_ns()), arm_window)
            if n:
                step_log = Path(run.dir, 'rp-step.log')
                step_log.write_text(f'MP-RP-STEP rp_step_n={n} rp_step_ratio={ratio}\n', encoding='utf-8')
                rp_step = (receiver.id, step_log)
            journey['step_n'] = n
            journey['step_ratio_permille'] = ratio
        else:
            journey = game_route(sender, route, str(time.time_ns()), True, S.rig.say, arm_window)

        # Include a report made after the route, even when a heartbeat landed
        # during it. The bundle's T1 verdict still covers the whole game launch.
        offset = receiver.size(receiver.alog)
        pulse = receiver.wait('a', offset, r'MP-T3-STATS origin=agent role=' + role + r' .*\brp_e2e_n=\d+', 90)
        latest = last_stats(receiver.lines(receiver.alog, offset), role) if pulse else {}
        figure = None
        for path, before in figure_offsets.items():
            figure = figure_stats(receiver.lines(path, before), source)
            if figure: break
        stable_link = all(copy.size(copy.alog) >= link_offsets[copy.id] and
                          not peer_ids(copy.lines(copy.alog, link_offsets[copy.id]), ASSIGNED_PEER)
                          for copy in ((receiver,) if args.bot else (sender, receiver)))
        network = network_evidence(relay, S.pair.POOR_NETWORK, args.netz, S.procs.matches, S.Copy.lines, require_delay=True)
        bundles = S.pair.collect('rpmeasure-%d' % time.time_ns(), write_verdict=True, copies=copies, bot_log=bot_log, rp_step=rp_step)
        packed = {cid: os.path.abspath(folder + '.packed') for cid, folder in bundles.items()}
        detail = {'sender': args.sender, 'sender_peer': source, 'receiver': role, 'duration_s': DURATION, 'network': network,
                  'route': journey, 'metrics': latest, 'figure': figure, 'stable_link': stable_link, 'bundles': packed}
        if not args.bot:
            tool = Path(S.HERE, '..', 'verdict', 'pair.py').resolve()
            result = subprocess.run([sys.executable, '-X', 'utf8', str(tool), packed[run.a.id], packed[run.b.id]], cwd=run.dir)
            if result.returncode > 1: raise RuntimeError('the pair report could not be written')
            detail['pair_report'] = os.path.join(run.dir, 'ERGEBNIS-PAAR.md')
        Path(run.dir, 'rpmeasure.json').write_text(json.dumps(detail, indent=2), encoding='utf-8')
        if latest.get('owner_violations', 0) > 0: return False, 'owner-violations', detail
        if not stable_link: return False, 'replaced-route-participant', detail
        if not latest.get('rp_e2e_n', 0): return False, 'no-new-rp-e2e-samples', detail
        if not figure: return False, 'no-new-matching-figure-samples', detail
        return True, 'ok', detail
    finally:
        failed = sys.exc_info()[0] is not None
        try:
            if armed: clock(receiver, 'off')
        except (OSError, RuntimeError) as error:
            if not failed: raise
            S.rig.say('rpmeasure cleanup:', str(error))


def run(S, argv):
    parser = argparse.ArgumentParser(prog='scenarios.py rpmeasure', description=__doc__)
    parser.add_argument('--route', type=Path, required=True, help='local JSON file with validated staircase waypoints')
    parser.add_argument('--netz', choices=('normal', 'schlecht'), default='normal')
    parser.add_argument('--sender', choices=('host', 'joiner'), default='joiner')
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument('--bot', action='store_true', help='replace the game joiner with T4 Bot-Joiner')
    mode.add_argument('--spieler', action='store_true', help='measure the real player walking the route in the two-game pair')
    parser.add_argument('--test-rpmeasure', action='store_true')
    args = parser.parse_args(argv)
    if not args.test_rpmeasure or os.environ.get('KCDMP_TEST_RP_MEASURE') != '1':
        parser.error('KCDMP_TEST_RP_MEASURE=1 and --test-rpmeasure are required')
    if args.bot and (args.sender != 'joiner' or os.environ.get('KCDMP_TEST_BOT') != '1'):
        parser.error('--bot uses --sender joiner and requires KCDMP_TEST_BOT=1')
    try:
        route = read_route(args.route)
    except (OSError, ValueError) as error:
        parser.error(str(error))
    record = S.Run('rpmeasure', include_joiner=not args.bot)
    if args.bot or record.step('join', S.s_join(record)):
        record.step('rpmeasure', lambda: measure(S, record, args, route))
    return record.finish()
