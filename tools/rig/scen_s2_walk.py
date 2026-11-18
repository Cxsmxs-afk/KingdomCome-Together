# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
"""s2-5: drive evidence for the remote Henry's engine movement and stance."""

import argparse
from dataclasses import dataclass
import json
import math
import os
from pathlib import Path
import re
import subprocess
import time
from uuid import UUID

from scen_s2_lanes import BOT_DONE, read_bot_log, wait_bot_ready
from scen_s2_measure import (ASSIGNED_PEER, BOT_PEER, STATS, Route, bot_plan, finite_position, natural_route,
                             network_evidence, parse_route, peer_ids)
from scen_s2_owner import VIOLATION, mark_native


GAITS = {'walk': (0.5, 2.4), 'run': (2.4, 4.0), 'sprint': (4.0, 6.01)}
CTRL = re.compile(r'\bMP-RP-CTRL figure=kcd2mp_(\d+) (.+)$')
STANCE = re.compile(r'\bMP-RP-CTRL-STANCE body=kcd2mp_(\d+) eid=(0x[0-9a-fA-F]+) owner=driver crouch=([01]) result=confirmed\b')
FIELD = re.compile(r'\b([a-z][a-z0-9_]*)=([\w-]+)(?= |$)')
CTRL_FIELDS = {'scope', 'connection', 'ctrl_frames', 'req_ok', 'req_fail', 'fallback_jump', 'fallback_mount',
               'fallback_ragdoll', 'fallback_air', 'hard_tp_join', 'hard_tp_respawn', 'hard_tp_travel',
               'hard_tp_unapproved', 'pulls', 'pull_observed_ms', 'pulls_per_min', 'gait_walk', 'gait_run', 'gait_sprint'}
TOTAL_FIELDS = {'rp_ctrl_frames', 'rp_req_ok', 'rp_hard_tp_unsafe', 'rp_owner_n', 'owner_violations'}
DRAWN = re.compile(r'\bdrawn=true hand=(\S+)')
DRAW_EID = re.compile(r'\beid=([0-9a-fA-F]+)\b')


@dataclass(frozen=True)
class WalkPlan:
    route: Route
    slope: tuple
    gaits: dict


def parse_walk_plan(value):
    if not isinstance(value, dict) or set(value) != {'points', 'stairs', 'slope', 'gaits'}:
        raise ValueError('rpwalk route needs points, stairs, slope and gaits')
    route = parse_route({key: value[key] for key in ('points', 'stairs')})
    points = {point[0]: point[1:] for point in route.points}

    def interval(limits, label):
        if (not isinstance(limits, list) or len(limits) != 2 or
                any(type(t) not in (int, float) or t not in points for t in limits) or limits[0] >= limits[1]):
            raise ValueError(f'{label} needs increasing times on existing route points')
        return tuple(limits)

    slope = interval(value['slope'], 'slope')
    if max(slope[0], route.stairs[0]) < min(slope[1], route.stairs[1]):
        raise ValueError('slope and stairs must be separate sections')
    if abs(points[slope[1]][2] - points[slope[0]][2]) < 0.5 or math.dist(points[slope[0]][:2], points[slope[1]][:2]) < 2:
        raise ValueError('slope needs at least 0.5 m of rise or fall over at least 2 m horizontally')
    if not isinstance(value['gaits'], dict) or set(value['gaits']) != GAITS.keys():
        raise ValueError('gaits must contain walk, run and sprint')
    gaits = {name: interval(value['gaits'][name], name) for name in GAITS}
    previous = 0
    for name, (start, stop) in gaits.items():
        if stop - start < 4 or start < previous:
            raise ValueError('gait sections must be ordered and last at least four seconds each')
        low, high = GAITS[name]
        for a, b in zip(route.points, route.points[1:]):
            if start <= a[0] < b[0] <= stop and not low <= math.dist(a[1:3], b[1:3]) / (b[0] - a[0]) < high:
                raise ValueError(f'{name} section has a route segment outside its speed range')
        previous = stop
    return WalkPlan(route, slope, gaits)


LUA_WALK_OBSERVE = '''function RPWalkObserve(r,t,x,y,z)
local q=player:GetWorldPos()
local onroute=(q.x-x)^2+(q.y-y)^2<=4 and math.abs(q.z-z)<=0.6
if t>=r.slope[1] and t<=r.slope[2] then
  r.slope[3]=r.slope[3]+1
  if onroute then r.slope[4]=r.slope[4]+1 end
end
if not r.prev or t-r.prev[1]>=0.25 then
  if r.prev then
    local p=r.prev; local v=math.sqrt((q.x-p[2])^2+(q.y-p[3])^2)/(t-p[1])
    for _,g in ipairs(r.gaits) do
      if p[1]>=g[1]+0.5 and t<=g[2] then
        g[5]=g[5]+1; if onroute and v>=g[3] and v<g[4] then g[6]=g[6]+1 end
      end
    end
  end
  r.prev={t,q.x,q.y}
end
if t>=60 then
  local a,b,c=r.gaits[1],r.gaits[2],r.gaits[3]
  System.LogAlways(string.format("[RPWALK-DONE] %s %d %d %d %d %d %d %d %d",r.tag,r.slope[4],r.slope[3],a[6],a[5],b[6],b[5],c[6],c[5]))
end
end'''


def observe_walk(sender, plan):
    gait_rows = [f'{{{a:.9g},{b:.9g},{GAITS[name][0]},{GAITS[name][1]},0,0}}' for name, (a, b) in plan.gaits.items()]
    setup = (f'local r=RPMeasureRoute; r.slope={{{plan.slope[0]:.9g},{plan.slope[1]:.9g},0,0}}; '
             f'r.gaits={{{",".join(gait_rows)}}}; r.observe=RPWalkObserve')
    if not sender.lua(' '.join(LUA_WALK_OBSERVE.split())) or not sender.lua(setup):
        raise RuntimeError('could not start read-only route observation')


def natural_walk(S, sender, receiver, plan):
    tag = str(time.time_ns())
    offset = sender.size(sender.klog)
    for name, (a, b) in plan.gaits.items():
        S.rig.say(f'{name}: {a:g}–{b:g} s')
    S.rig.say(f'Hang: {plan.slope[0]:g}–{plan.slope[1]:g} s')
    travel, (step_n, ratio) = natural_route(S, sender, receiver, plan.route, tag, lambda: observe_walk(sender, plan))
    result = sender.wait('k', offset, r'\[RPWALK-DONE\] ' + tag + r' (\d+) (\d+) (\d+) (\d+) (\d+) (\d+) (\d+) (\d+)', 6)
    if not result:
        raise RuntimeError('no corresponding slope and gait readback from the source game')
    counts = list(map(int, result.groups()))
    travel['stages'] = {name: {'matched': counts[2 * i], 'samples': counts[2 * i + 1]}
                        for i, name in enumerate(('slope', *GAITS))}
    return travel, (step_n, ratio)


def parse_numbers(text, fields):
    raw = dict(FIELD.findall(text))
    if not all(raw.get(key, '').isascii() and raw[key].isdigit() for key in fields):
        return {}
    return {key: int(raw[key]) for key in fields}


def ctrl_rows(lines, peer):
    rows = []
    for line in lines:
        match = CTRL.search(line)
        if not match or int(match[1]) != peer:
            continue
        values = parse_numbers(match[2], CTRL_FIELDS)
        eid = re.search(r'\beid=(0x[0-9a-fA-F]+)\b', match[2])
        if values and eid and int(eid[1], 16):
            rows.append({'eid': int(eid[1], 16), **values})
    return rows


def scoped_delta(before, after):
    if not after:
        return {}, {}
    current = after[-1]
    identity = tuple(current[key] for key in ('eid', 'scope', 'connection'))
    if any(tuple(row[key] for key in ('eid', 'scope', 'connection')) != identity for row in after):
        return {}, {}
    baseline = next((row for row in reversed(before) if tuple(row[key] for key in ('eid', 'scope', 'connection')) == identity), {})
    counters = CTRL_FIELDS - {'scope', 'connection', 'pulls_per_min'}
    if any(new[key] < old[key] for old, new in zip(after, after[1:]) for key in counters):
        return {}, {}
    delta = {key: current[key] - baseline.get(key, 0) for key in sorted(counters)}
    return (current, delta) if all(value >= 0 for value in delta.values()) else ({}, {})


def fresh_body(receiver, marks, peer):
    for path, offset in marks.items():
        if receiver.size(path) < offset:
            continue
        after = ctrl_rows(receiver.lines(path, offset), peer)
        if after:
            # `lines` starts at the requested byte, so use only complete rows before the boundary.
            with open(path, 'rb') as stream:
                before_bytes = stream.read(offset).rpartition(b'\n')[0]
            before = ctrl_rows(before_bytes.decode('utf-8', 'replace').splitlines(), peer)
            return scoped_delta(before, after)
    return {}, {}


def role_totals(lines, role):
    for line in reversed(lines):
        match = STATS.search(line)
        if match and match[1] == role and (values := parse_numbers(match[2], TOTAL_FIELDS)):
            return values
    return {}


def confirmed_stances(lines, peer):
    return [(int(match[2], 16), int(match[3])) for line in lines
            if (match := STANCE.search(line)) and int(match[1]) == peer]


def wait_crouch(receiver, marks, peer, wanted, timeout=12):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        for path, offset in marks.items():
            for eid, state in confirmed_stances(receiver.lines(path, offset), peer):
                if state == wanted:
                    return eid
        time.sleep(0.25)
    return None


def wait_draw(copy, code, tag, timeout=14):
    end = time.monotonic() + timeout
    latest = None
    while time.monotonic() < end:
        latest = copy.ask(code, tag)
        match = DRAWN.search(latest or '')
        if match and match[1] != '-':
            return latest, True
        time.sleep(0.25)
    return latest, False


def figure_weapon_probe(S, peer):
    # Entity id as userdata hex, as in the ghost's own spawn notification.
    return (S.HAND_OF + f'local f=System.GetEntityByName("kcd2mp_{peer}"); local out="none"; '
            'if f and f.human then local d="?"; pcall(function() d=tostring(f.human:IsWeaponDrawn()) end); '
            'out="drawn="..d.." hand="..hand_of(f.human).." eid="..(tostring(f.id):match("(%x+)%s*$") or "-") end; '
            'System.LogAlways("[W214F] "..out)')


def weapon_eid(readback):
    match = DRAW_EID.search(readback or '')
    return int(match[1], 16) if match else None


def player_stance(S, sender, receiver, peer):
    sender.lua('pcall(function() player.human:HolsterWeapon() end)')
    edges = []
    for state, turn in ((1, 'on'), (0, 'off')):
        marks = mark_native(receiver)
        offset = sender.size(sender.alog)
        sender.cmd('mp_w135_check crouch ' + turn)
        source = sender.wait('a', offset, rf"MP-WO135 check: the player's own crouch setter {turn} -> called", 10)
        eid = wait_crouch(receiver, marks, peer, state) if source else None
        edges.append({'crouch': state, 'source_request': bool(source), 'body_eid': eid})
    figure_probe = figure_weapon_probe(S, peer)
    previous_draw = sender.ask(S.OWN_WEAPON_Q, 'W214O')
    previous_figure = receiver.ask(figure_probe, 'W214F')
    sender.lua('pcall(function() player.human:DrawWeapon() end)')
    source_draw, source_ok = wait_draw(sender, S.OWN_WEAPON_Q, 'W214O')
    fig_draw, fig_ok = wait_draw(receiver, figure_probe, 'W214F')
    return {'edges': edges, 'source_weapon': source_draw, 'source_draw_observed': source_ok and 'drawn=false' in (previous_draw or ''),
            'figure_weapon': fig_draw, 'figure_draw_observed': fig_ok and 'drawn=false' in (previous_figure or '') and
            weapon_eid(fig_draw) == weapon_eid(previous_figure), 'figure_weapon_eid': weapon_eid(fig_draw)}


def stance_plan(position, weapon):
    x, y, z = (format(value, '.9g') for value in position)
    return '\n'.join((f'at 0 stand {x} {y} {z} 0', f'at 2 appearance {weapon}', 'at 5 move 1 0 2',
                      'at 10 state crouch=1', 'at 12 move 0.65 3.14159265 3', 'at 18 state crouch=0',
                      'at 24 draw', 'end 43', ''))


def bot_stance(S, bot, log, receiver, peer, marks):
    figure_probe = figure_weapon_probe(S, peer)
    baseline = time.monotonic() + 16
    previous_figure = None
    while time.monotonic() < baseline and bot.poll() is None:
        previous_figure = receiver.ask(figure_probe, 'W214F')
        if 'drawn=false' in (previous_figure or ''):
            break
        time.sleep(0.25)
    end = time.monotonic() + 33
    while time.monotonic() < end and bot.poll() is None:
        if any(re.search(r'^PEER t=\S+ draw$', line) for line in read_bot_log(log)):
            break
        time.sleep(0.25)
    lines = read_bot_log(log)
    source_ok = any(re.search(r'^PEER t=\S+ draw$', line) for line in lines)
    source_states = [line for line in lines if re.search(r'^PEER t=\S+ state\b', line)]
    transitions = []
    for path, offset in marks.items():
        transitions = confirmed_stances(receiver.lines(path, offset), peer)
        if transitions:
            break
    edges = []
    for state in (1, 0):
        eid = None
        while transitions:
            body, observed = transitions.pop(0)
            if observed == state:
                eid = body
                break
        edges.append({'crouch': state, 'body_eid': eid})
    fig_draw, fig_ok = wait_draw(receiver, figure_probe, 'W214F')
    return {'edges': edges, 'source_intent_lines': source_states, 'source_draw_emitted': source_ok,
            'figure_weapon': fig_draw, 'figure_draw_observed': fig_ok and 'drawn=false' in (previous_figure or '') and
            weapon_eid(fig_draw) == weapon_eid(previous_figure), 'figure_weapon_eid': weapon_eid(fig_draw)}


def checked_gaits(travel):
    checks = []
    for name, row in travel['stages'].items():
        required_percent = 75 if name == 'slope' else 60
        if row['samples'] < 3 or row['matched'] * 100 < required_percent * row['samples']:
            checks.append(f'source-{name}-unconfirmed')
    return checks


def checks_for(name, body, delta, totals, travel, step, bot):
    if totals.get('owner_violations', 0):
        return ['owner-violations']
    if totals.get('rp_hard_tp_unsafe', 0) or body.get('hard_tp_unapproved', 0):
        return ['unapproved-hard-placement']
    failures = []
    if not body or not delta.get('req_ok') or not all(totals.get(key) for key in ('rp_owner_n', 'rp_ctrl_frames', 'rp_req_ok')):
        failures.append('controller-or-owner-coverage-unmeasured')
    if name == 'rpwalk':
        if not bot:
            failures.extend(checked_gaits(travel))
        if any(not delta.get('gait_' + name) for name in GAITS):
            failures.append('controller-gaits-unconfirmed')
        if not delta.get('pull_observed_ms'):
            failures.append('pull-rate-unmeasured')
        elif delta['pulls'] * 300_000 >= delta['pull_observed_ms']:
            failures.append('pull-rate-at-least-0.2-per-minute')
        if step[0] and not 900 <= step[1] <= 1100:
            failures.append('step-ratio-out-of-target')
    else:
        if [edge['body_eid'] for edge in travel['edges']] != [body.get('eid')] * 2 or not body:
            failures.append('native-crouch-or-rise-unconfirmed')
        if (not travel.get('figure_draw_observed') or travel.get('figure_weapon_eid') != body.get('eid') or
                not travel.get('source_draw_emitted' if bot else 'source_draw_observed')):
            failures.append('draw-unconfirmed')
    return failures


def exercise(S, run, name, args, plan):
    sender, receiver, role = (run.a, run.b, 'joiner') if args.sender == 'host' else (run.b, run.a, 'host')
    copies = (receiver,) if args.bot else (receiver, sender)
    state = S.pair.state()
    if args.bot and S.procs.matches(state.get(run.b.id.lower())):
        raise RuntimeError('the Bot-Joiner replaces the second game, which must be stopped')
    positions = {}
    for copy in copies:
        if not S.procs.matches(state.get(copy.id.lower())) or copy.port is None:
            raise RuntimeError(f'copy {copy.id} must be running in the game')
        position = copy.pos()
        if not finite_position(position):
            raise RuntimeError(f'copy {copy.id} did not return finite local player coordinates')
        positions[copy.id] = position

    relay = state.get('relay')
    network_evidence(relay, S.pair.POOR_NETWORK, args.netz, S.procs.matches, S.Copy.lines)
    native_marks = {copy.id: mark_native(copy) for copy in copies}
    agent_marks = {copy.id: copy.size(copy.alog) for copy in copies}
    bot = None
    bot_log = None
    step = (0, None)
    if args.bot:
        if name == 'rpwalk':
            if math.dist(positions[receiver.id], plan.route.points[0][1:]) > 25:
                raise RuntimeError('host must be near the walk route start')
            script = bot_plan(plan.route)
        else:
            x, y, z = positions[receiver.id]
            script = stance_plan((x + 2, y, z), args.weapon_class)
        plan_file = Path(run.dir, name + '-bot.txt')
        plan_file.write_text(script, encoding='utf-8')
        bot_log = Path(run.dir, 'bot-t4.log')
        with bot_log.open('w', encoding='utf-8') as stream:
            command = S.bot_joiner_command(run, '--scenario', str(plan_file.resolve()), '--duration', '175')
            bot = subprocess.Popen(command, stdout=stream, stderr=subprocess.STDOUT)

    try:
        if bot is not None:
            peer = wait_bot_ready(bot, bot_log)
            if name == 'rpwalk':
                bot.wait()
                travel = {'source': 'T4 scripted coordinates and velocities', 'duration_s': 60}
            else:
                travel = bot_stance(S, bot, bot_log, receiver, peer, native_marks[receiver.id])
        else:
            ids = peer_ids(sender.lines(sender.alog, 0), ASSIGNED_PEER)
            if not ids:
                raise RuntimeError('source Agent has no assigned Relay ID')
            peer = ids[-1]
            if name == 'rpwalk':
                travel, step = natural_walk(S, sender, receiver, plan)
            else:
                travel = player_stance(S, sender, receiver, peer)
        body, delta = fresh_body(receiver, native_marks[receiver.id], peer)
        offset = receiver.size(receiver.alog)
        pulse = receiver.wait('a', offset, rf'MP-T3-STATS origin=agent role={role} .*\brp_req_ok=\d+', 85)
        totals = role_totals(receiver.lines(receiver.alog, offset), role) if pulse else {}
    finally:
        if bot is not None:
            bot.wait()

    net = network_evidence(relay, S.pair.POOR_NETWORK, args.netz, S.procs.matches, S.Copy.lines, require_delay=True)
    failures = checks_for(name, body, delta, totals, travel, step, args.bot)
    if any(copy.size(copy.alog) < agent_marks[copy.id] or
           peer_ids(copy.lines(copy.alog, agent_marks[copy.id]), ASSIGNED_PEER) for copy in copies):
        failures.append('replaced-participant')
    if any(VIOLATION in line for copy in copies for path, mark in native_marks[copy.id].items() for line in copy.lines(path, mark)):
        failures.append('native-owner-violation')
    if bot is not None:
        lines = read_bot_log(bot_log)
        if (bot.returncode or peer_ids(lines, BOT_PEER) != [peer] or
                'BOT-T4 role=joiner presentation_clock=ready' not in lines or not any(line.startswith(BOT_DONE) for line in lines)):
            failures.append('t4-sender-incomplete-or-clock-unmeasured')

    step_attachment = None
    if step[0] and body and not any(reason in failures for reason in ('replaced-participant', 'native-owner-violation')):
        step_log = Path(run.dir, 'rp-step.log')
        step_log.write_text(f'MP-RP-STEP rp_step_n={step[0]} rp_step_ratio={step[1]}\n', encoding='utf-8')
        step_attachment = (receiver.id, step_log)
    bundles = S.pair.collect(name + '-%d' % time.time_ns(), write_verdict=True, copies=tuple(copy.id for copy in copies),
                             bot_log=bot_log, rp_step=step_attachment)
    report = {'sender': 'scripted-t4' if args.bot else args.sender, 'sender_peer': peer, 'receiver': role, 'network': net,
              'source': travel, 'body': body, 'body_delta': delta, 'stats': totals, 'step_n': step[0],
              'step_ratio_permille': step[1], 'step_measured': step_attachment is not None,
              'failures': failures, 'bundles': {cid: folder + '.packed' for cid, folder in bundles.items()}}
    Path(run.dir, name + '.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    return not failures, failures[0] if failures else 'ok', report


def weapon_class(value):
    try:
        item = UUID(value)
    except ValueError as error:
        raise argparse.ArgumentTypeError('expected a local melee item class GUID') from error
    if not item.int:
        raise argparse.ArgumentTypeError('choose a nonzero melee item class GUID')
    return item


def run(S, name, argv):
    parser = argparse.ArgumentParser(prog=f'scenarios.py {name}', description=__doc__)
    parser.add_argument('--test-rp-controller', action='store_true')
    parser.add_argument('--netz', choices=('normal', 'schlecht'), default='normal')
    parser.add_argument('--sender', choices=('host', 'joiner'), default='joiner')
    parser.add_argument('--bot', action='store_true', help='use the T4 Bot-Joiner with one real host')
    if name == 'rpwalk':
        parser.add_argument('--route', type=Path, required=True, help='local JSON route with stairs, slope and gait ranges')
    else:
        parser.add_argument('--weapon-class', type=weapon_class, help='local melee item class GUID for the Bot-Joiner')
    args = parser.parse_args(argv)
    if not args.test_rp_controller or os.environ.get('KCDMP_TEST_RP_CONTROLLER') != '1':
        parser.error('KCDMP_TEST_RP_CONTROLLER=1 and --test-rp-controller are required')
    if args.bot and (args.sender != 'joiner' or os.environ.get('KCDMP_TEST_BOT') != '1'):
        parser.error('--bot requires KCDMP_TEST_BOT=1 and a scripted joiner')
    plan = None
    if name == 'rpwalk':
        try:
            with args.route.open('rb') as stream:
                data = stream.read(16_385)
            if len(data) > 16_384:
                raise ValueError('route file exceeds 16 KiB')
            plan = parse_walk_plan(json.loads(data))
        except (OSError, ValueError) as error:
            parser.error(str(error))
    elif args.bot != (args.weapon_class is not None):
        parser.error('--weapon-class is required for --bot and scoped to that scenario')
    record = S.Run(name, include_joiner=not args.bot)
    if args.bot or record.step('join', S.s_join(record)):
        record.step(name, lambda: exercise(S, record, name, args, plan))
    return record.finish()
