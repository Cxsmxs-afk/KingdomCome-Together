# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
"""s2-2: verify the receiver's native ownership handovers for one partner figure."""

import argparse
from dataclasses import asdict, dataclass
import json
import os
from pathlib import Path
import re
import subprocess
import time

from scen_s2_measure import ASSIGNED_PEER, BOT_PEER, STATS, finite_position, network_evidence, peer_ids


OWNER_COUNT = re.compile(r'\b(rp_owner_n|owner_violations|owner_handovers|owner_denied_sets)=(\d+)(?= |$)')
OWNER_FIELDS = {'rp_owner_n', 'owner_violations', 'owner_handovers', 'owner_denied_sets'}
HOLD = re.compile(r'\bWO221-HOLD npc=kcd2mp_(\d+) ([\w-]+) -> ([\w-]+) .*\beid=(0x[0-9a-fA-F]+)\b.*\bframe=(\d+)\b')
VIOLATION = 'WO221-OWNER-VIOLATION '
WALK = frozenset(('writer', 'driver'))
SEAT = frozenset(('seat', 'activity'))
FALL = frozenset(('fall',))
MOUNT = frozenset(('mount',))
STAGES = (('sit', WALK, SEAT), ('leave-seat', SEAT, WALK), ('fall', WALK, FALL), ('stand', FALL, WALK))
HORSE_STAGES = (('mount', WALK, MOUNT), ('dismount', MOUNT, WALK))
DEATH_STAGE = (('death', WALK, FALL),)
SOURCE_SIT = r'\bWO164-SITSTATE enter obj=[0-9A-Fa-f]+ stance=3\b'
SOURCE_LEAVE = r'\bWO164-SITSTATE leave obj=[0-9A-Fa-f]+ stance=3\b'
SOURCE_STATUS = '''local name="%s"; local deadline=System.GetCurrTime()+%d; local function f()
if KCDOwnerRigTag~=name or System.GetCurrTime()>=deadline then return end
if player and player.actor then
  local ok,down=pcall(function() return player.actor:IsUnconscious() end)
  local alive,dead=pcall(function() return player.actor:IsDead() end)
  if alive and (dead==true or dead==1) then
    System.LogAlways("[OWNER-RIG "..name.."] dead=1")
  elseif ok and alive and (dead==false or dead==0) and (down==true or down==false or down==0 or down==1) then
    System.LogAlways("[OWNER-RIG "..name.."] ko="..((down==true or down==1) and "1" or "0"))
  end
  local has,mounted=pcall(function() return player.human:IsMounted() end)
  if has and (type(mounted)=="boolean" or mounted==0 or mounted==1) then
    System.LogAlways("[OWNER-RIG "..name.."] mounted="..((mounted==true or mounted==1) and "1" or "0"))
  end
end
Script.SetTimer(500,f)
end; KCDOwnerRigTag=name; f()'''


@dataclass(frozen=True)
class Handover:
    stage: str
    body: int
    frame: int
    outgoing: str
    incoming: str


def handovers(lines, peer, stages, earlier=()):
    found = list(earlier)
    for line in lines:
        match = HOLD.search(line)
        if not match or int(match[1]) != peer:
            continue
        body, frame = int(match[4], 16), int(match[5])
        if found and (body != found[0].body or frame < found[-1].frame):
            return []
        if len(found) < len(stages):
            stage, outgoing, incoming = stages[len(found)]
            if match[2] in outgoing and match[3] in incoming and (not found or frame > found[-1].frame):
                found.append(Handover(stage, body, frame, match[2], match[3]))
    return found


def owner_stats(lines, role):
    for line in reversed(lines):
        match = STATS.search(line)
        if match and match[1] == role:
            values = {key: int(value) for key, value in OWNER_COUNT.findall(match[2])}
            if OWNER_FIELDS <= values.keys():
                return values
    return {}


def owner_result(before, after, stages, observed):
    if before.get('owner_violations', 0) or after.get('owner_violations', 0):
        return 'owner-violations', {}
    if set(before) != OWNER_FIELDS or set(after) != OWNER_FIELDS:
        return 'owner-counters-unmeasured', {}
    delta = {key: after[key] - before[key] for key in sorted(OWNER_FIELDS)}
    if any(value < 0 for value in delta.values()):
        return 'owner-counters-reset', delta
    if delta['rp_owner_n'] <= 0:
        return 'no-new-owner-checks', delta
    if len(observed) != len(stages) or delta['owner_handovers'] < len(observed):
        return 'missing-figure-handover', delta
    return 'ok', delta


def bot_scenario(origin, seat, horse, death):
    x, y, z = (format(value, '.9g') for value in origin)
    lines = [f'at 0 stand {x} {y} {z} 0', 'at 2 move 1 0 3', f'at 7 activity stance 3 {seat}',
             'at 15 activity none', 'at 17 move 1 0 3', 'at 22 state combat=1',
             'at 25 vitals 20 30 knockeddown', 'at 25 state downed=1',
             'at 34 vitals 100 100', 'at 34 state downed=0 combat=0', 'at 36 move 1 0 3']
    if horse:
        lines += [f'at 42 ride {horse}', 'at 52 ride off', 'at 54 move 1 0 3']
    end = 60 if horse else 45
    if death:
        lines += [f'at {end} state combat=1', f'at {end + 3} death']
        end += 11
    return '\n'.join([*lines, f'end {end}', ''])


def native_logs(copy):
    return (os.path.join(copy.p['dll_dir'], f'kcdmp-native-{copy.id}.log'), copy.nlog)


def mark_native(copy):
    return {path: copy.size(path) for path in native_logs(copy)}


def spot_handovers(receiver, marks, peer, stages, earlier=()):
    best = []
    for path, offset in marks.items():
        if receiver.size(path) < offset:
            continue
        found = handovers(receiver.lines(path, offset), peer, stages, earlier)
        if len(found) > len(best):
            best = found
    return best


def player_handover(S, sender, receiver, peer, stages, timeout):
    tag = str(time.time_ns())
    # Sit/leave are the sender's real WO-164 activity capture. KO and mount are
    # read independently from the sender's local actor, with no replica writes.
    marker = r'\[OWNER-RIG ' + tag + r'\] '
    prompts = {
        'sit': ('a', SOURCE_SIT, 'Kurz gehen, dann auf eine Bank setzen und sitzen bleiben.'),
        'leave-seat': ('a', SOURCE_LEAVE, 'Aufstehen und ein paar Schritte gehen.'),
        'fall': ('k', marker + r'ko=1\b', 'Im Spiel kampfunfähig werden, zum Beispiel bei einer Prügelei.'),
        'stand': ('k', marker + r'ko=0\b', 'Auf das Aufwachen warten und dann ein paar Schritte gehen.'),
        'mount': ('k', marker + r'mounted=1\b', 'Auf das bereitstehende Pferd steigen und oben bleiben.'),
        'dismount': ('k', marker + r'mounted=0\b', 'Absteigen und ein paar Schritte gehen.'),
        'death': ('k', marker + r'dead=1\b', 'Im Wegwerfstand im Spiel sterben. Danach nicht neu laden.'),
    }
    source = []
    observed = []
    if not sender.lua(' '.join((SOURCE_STATUS % (tag, len(stages) * (timeout + 20) + 30)).split())):
        raise RuntimeError('the source console did not start the actor state probe')
    try:
        for i, (stage, _, _) in enumerate(stages):
            which, pattern, prompt = prompts[stage]
            marks = mark_native(receiver)
            offset = sender.size(sender.alog if which == 'a' else sender.klog)
            S.rig.say(f'Kopie {sender.id}', prompt)
            evidence = sender.wait(which, offset, pattern, timeout)
            if not evidence:
                break
            source.append({'stage': stage, 'evidence': evidence[0]})
            until = time.monotonic() + 20
            while time.monotonic() < until:
                found = spot_handovers(receiver, marks, peer, stages[:i + 1], observed)
                if len(found) > i:
                    observed = found
                    break
                time.sleep(0.25)
            if len(observed) <= i:
                break
    finally:
        sender.lua_nowait(f'if KCDOwnerRigTag=="{tag}" then KCDOwnerRigTag=nil end')
    return source, observed


def fresh_stats(copy, role, offset):
    if copy.wait('a', offset, r'MP-T3-STATS origin=agent role=' + role + r' .*\bowner_denied_sets=\d+', 85):
        return owner_stats(copy.lines(copy.alog, offset), role)
    return {}


def exercise(S, run, args):
    sender, receiver, role = (run.a, run.b, 'joiner') if args.sender == 'host' else (run.b, run.a, 'host')
    copies = (receiver,) if args.bot else (receiver, sender)
    state = S.pair.state()
    if args.bot and S.procs.matches(state.get(run.b.id.lower())):
        raise RuntimeError('the Bot-Joiner replaces the second game, which must be stopped')
    for copy in copies:
        if not S.procs.matches(state.get(copy.id.lower())) or copy.port is None or not finite_position(copy.pos()):
            raise RuntimeError(f'copy {copy.id} must be running with an in-world local player')
    relay = state.get('relay')
    network_evidence(relay, S.pair.POOR_NETWORK, args.netz, S.procs.matches, S.Copy.lines)
    S.rig.say('Warte auf die Ausgangswerte der Besitzerprüfung.')
    before = fresh_stats(receiver, role, receiver.size(receiver.alog))
    stages = STAGES + (HORSE_STAGES if args.reiten or args.horse else ())
    if args.tod:
        stages += DEATH_STAGE
    native_marks = {copy.id: mark_native(copy) for copy in copies}
    agent_marks = {copy.id: copy.size(copy.alog) for copy in copies}
    bot_log = None
    if args.bot:
        plan = Path(run.dir, 'ownerhandover-bot.txt')
        plan.write_text(bot_scenario(args.origin, args.seat, args.horse, args.tod), encoding='utf-8')
        bot_log = Path(run.dir, 'bot-t4.log')
        with bot_log.open('w', encoding='utf-8') as stream:
            result = subprocess.run(S.bot_joiner_command(run, '--scenario', str(plan.resolve()), '--duration', '240'),
                                    stdout=stream, stderr=subprocess.STDOUT)
        lines = bot_log.read_text(encoding='utf-8', errors='replace').splitlines()
        ids = peer_ids(lines, BOT_PEER)
        if result.returncode or len(ids) != 1 or not any(line.startswith('BOT-T4 role=joiner complete=1 ') for line in lines):
            raise RuntimeError(f'the T4 sender did not complete one connected scenario (exit {result.returncode})')
        peer, source, observed = ids[0], [{'stage': 'script', 'evidence': 'T4 script completed'}], []
    else:
        ids = peer_ids(sender.lines(sender.alog, 0), ASSIGNED_PEER)
        if not ids:
            raise RuntimeError('source Agent has no assigned Relay ID')
        peer = ids[-1]
        source, observed = player_handover(S, sender, receiver, peer, stages, args.phase_timeout)

    after = fresh_stats(receiver, role, receiver.size(receiver.alog))
    full_trace = spot_handovers(receiver, native_marks[receiver.id], peer, stages)
    if args.bot or len(full_trace) < len(observed):
        observed = full_trace
    network = network_evidence(relay, S.pair.POOR_NETWORK, args.netz, S.procs.matches, S.Copy.lines, require_delay=True)
    stable = all(copy.size(copy.alog) >= agent_marks[copy.id] and
                 not peer_ids(copy.lines(copy.alog, agent_marks[copy.id]), ASSIGNED_PEER) for copy in copies)
    reason, delta = owner_result(before, after, stages, observed)
    violation_copies = [copy.id for copy in copies if any(VIOLATION in line for path, offset in native_marks[copy.id].items()
                                                        for line in copy.lines(path, offset))]
    if violation_copies:
        reason = 'owner-violations'
    elif reason == 'ok' and not stable:
        reason = 'replaced-participant'
    bundles = S.pair.collect('ownerhandover-%d' % time.time_ns(), write_verdict=True,
                             copies=tuple(copy.id for copy in copies), bot_log=bot_log)
    report = {'sender': 'scripted-t4' if args.bot else 'real-player', 'sender_peer': peer,
              'receiver': role, 'network': network, 'stable_link': stable, 'source_evidence': source,
              'required_stages': [stage[0] for stage in stages], 'handovers': [asdict(value) for value in observed],
              'before': before, 'after': after, 'delta': delta, 'violation_copies': violation_copies,
              'result': reason, 'bundles': {cid: folder + '.packed' for cid, folder in bundles.items()}}
    Path(run.dir, 'ownerhandover.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    return reason == 'ok', reason, report


def origin_arg(value):
    try:
        position = tuple(map(float, value.split(',')))
    except ValueError as error:
        raise argparse.ArgumentTypeError('expected x,y,z') from error
    if not finite_position(position) or any(abs(axis) > 100_000 for axis in position):
        raise argparse.ArgumentTypeError('expected three finite world coordinates x,y,z')
    return position


def run(S, argv):
    parser = argparse.ArgumentParser(prog='scenarios.py ownerhandover', description=__doc__)
    parser.add_argument('--test-ownerhandover', action='store_true')
    parser.add_argument('--netz', choices=('normal', 'schlecht'), default='normal')
    parser.add_argument('--sender', choices=('host', 'joiner'), default='joiner')
    parser.add_argument('--phase-timeout', type=int, choices=range(15, 241), metavar='15–240', default=90)
    parser.add_argument('--reiten', action='store_true', help='also require mount and dismount from the real player')
    parser.add_argument('--tod', action='store_true', help='also require a terminal death handover in the disposable game')
    parser.add_argument('--bot', action='store_true')
    parser.add_argument('--origin', type=origin_arg, help='T4 starting coordinates before the test bench, as x,y,z')
    parser.add_argument('--seat', help='T4 actual seat GUID from the test save, as 16 hex digits')
    parser.add_argument('--horse', help='T4 optional horse entity name in the test save')
    args = parser.parse_args(argv)
    if not args.test_ownerhandover or os.environ.get('KCDMP_TEST_OWNER_HANDOVER') != '1':
        parser.error('KCDMP_TEST_OWNER_HANDOVER=1 and --test-ownerhandover are required')
    if args.bot:
        if os.environ.get('KCDMP_TEST_BOT') != '1' or args.sender != 'joiner' or args.reiten:
            parser.error('--bot requires KCDMP_TEST_BOT=1 and a scripted joiner. Use --horse to test riding.')
        if args.origin is None or not re.fullmatch(r'[0-9a-fA-F]{16}', args.seat or '') or not int(args.seat, 16):
            parser.error('--bot requires --origin and an actual nonzero --seat GUID')
        if args.horse and (args.horse == 'off' or not re.fullmatch(r'\S{1,120}', args.horse)):
            parser.error('--horse requires one horse entity name')
    elif args.origin is not None or args.seat or args.horse:
        parser.error('--origin, --seat and --horse are for --bot')
    record = S.Run('ownerhandover', include_joiner=not args.bot)
    if args.bot or record.step('join', S.s_join(record)):
        record.step('ownerhandover', lambda: exercise(S, record, args))
    return record.finish()
