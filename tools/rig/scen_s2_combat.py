# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
"""s2-6: match staged combat actions to an observed native result on one partner figure."""

import argparse
from concurrent.futures import ThreadPoolExecutor
import json
import math
import os
from pathlib import Path
import re
import subprocess
import time

from scen_s2_lanes import BOT_DONE, read_bot_log, wait_bot_ready
from scen_s2_measure import ASSIGNED_PEER, BOT_PEER, STATS, finite_position, network_evidence, peer_ids
from scen_s2_owner import SOURCE_STATUS, VIOLATION, mark_native
from scen_s2_timeline import BOT_SWING_ROW, FIELD, INBOUND, number
from scen_s2_walk import weapon_class


NATIVE = re.compile(r'\bMP-RP-COMBAT-ACTION (.+)$')
REAL_FIGHT = re.compile(r'\bMP-ACTION section=outbound kind=(Attack|BlockImpulse|AvatarHit) (.+)$')
SCRIPTED_FIGHT = re.compile(r'^PEER t=\S+ (attack|block_impulse|hit_reaction) (.+)$')
REAL_FIRE = re.compile(r'\bWO201-RANGED out firing weapon=(Bow|Crossbow) item=(\S+) seq=(\d+) epoch=(\d+)\b')
SCRIPTED_FIRE = re.compile(r'^PEER t=\S+ ranged phase=fire weapon=(Bow|Crossbow) item=(\S+) seq=(\d+) epoch=(\d+)\b')
SCRIPTED_DOWN = re.compile(r'^PEER t=\S+ player_state down=([01]) source_ms=(\d+)\b')
KO_IN = re.compile(r'\bMP-RP-KO-IN (.+)$')
RANGED_IN = re.compile(r'\bMP-RP-RANGED-IN (.+)$')
BANDIT = re.compile(r'[A-Za-z0-9_./\[\]-]{1,120}')
FIGHT_KINDS = {1, 6, 131, 132}
KO = 129
RANGED = 130
WEAPONS = {'bow': 1, 'crossbow': 2}
TOTALS = {'rp_actions_in', 'rp_actions_played', 'rp_actions_pending', 'rp_action_confirmed_n', 'rp_action_lag_ms_p95',
          'rp_ko_up_n', 'rp_ko_up_lag_ms_p95', 'dmg_double_blocked', 'dmg_double', 'dmg_double_unconfirmed', 'rp_owner_n', 'owner_violations'}


def fight_sent(lines, bot):
    result = []
    for line in lines:
        match = (SCRIPTED_FIGHT if bot else REAL_FIGHT).search(line)
        if not match:
            continue
        fields = dict(FIELD.findall(match[2]))
        stamp = number(fields, 'source_ms')
        if stamp is None:
            stamp = number(fields, 'ms')
        if stamp is None or stamp > 0xFFFFFFFF:
            continue
        if match[1] in ('attack', 'Attack'):
            kind = 1
        elif match[1] in ('block_impulse', 'BlockImpulse'):
            flags = number(fields, 'perfect' if bot else 'flags')
            kind = 131 if flags is not None and flags & 1 else 6
        else:
            kind = 132
        result.append({'kind': kind, 'source_ms': stamp})
    return result


def fired(lines, bot, weapon):
    rx = SCRIPTED_FIRE if bot else REAL_FIRE
    return [{'weapon': match[1].lower(), 'item': match[2], 'seq': int(match[3]), 'epoch': int(match[4])}
            for line in lines if (match := rx.search(line)) and match[1].lower() == weapon]


def key(row):
    return tuple(row[field] for field in ('kind', 'phase', 'seq', 'event_host_ms'))


def combat_rows(lines, peer, allowed):
    for line in lines:
        match = NATIVE.search(line)
        if not match:
            continue
        raw = dict(FIELD.findall(match[1]))
        if raw.get('peer') != str(peer) or number(raw, 'kind') not in allowed or raw.get('result') not in ('in', 'installed'):
            continue
        required = ('kind', 'phase', 'seq', 'event_host_ms', 'scope', 'connection')
        row = {name: number(raw, name) for name in required}
        if any(value is None for value in row.values()) or row['phase'] != 1 or not row['event_host_ms']:
            continue
        eid = raw.get('eid', '')
        if not re.fullmatch(r'0x[0-9A-Fa-f]+', eid) or not int(eid, 16):
            continue
        row.update(eid=int(eid, 16), result=raw['result'])
        for extra in ('lag_ms', 'down', 'weapon', 'ranged_phase'):
            value = number(raw, extra)
            if value is not None:
                row[extra] = value
        if row['result'] == 'installed' and 'lag_ms' not in row:
            continue
        yield row


def same_body(proofs):
    return len({(entry['eid'], entry['scope'], entry['connection']) for entry in proofs}) <= 1


def applied_actions(lines, peer, kinds):
    admitted = {}
    installed = {}
    for row in combat_rows(lines, peer, kinds):
        identity = key(row)
        if row['result'] == 'in':
            if identity in admitted and admitted[identity] != row:
                return []
            admitted[identity] = row
        elif identity in admitted and same_body([admitted[identity], row]):
            if identity in installed:
                if not same_body([installed[identity], row]):
                    return []
                continue
            installed[identity] = row
    return list(installed.values()) if same_body([*admitted.values(), *installed.values()]) else []


def select_fight(sent, agent, native, peer):
    expected = {(row['kind'], row['source_ms']) for row in sent}
    by_action = {}
    for line in agent:
        match = INBOUND.search(line)
        if not match:
            continue
        raw = dict(FIELD.findall(match[1]))
        if raw.get('peer') != str(peer) or raw.get('result') != 'accepted':
            continue
        fields = {name: number(raw, name) for name in ('kind', 'phase', 'seq', 'event_host_ms', 'source_ms')}
        if (fields['kind'], fields['source_ms']) in expected and all(value is not None for value in fields.values()):
            by_action[key(fields)] = fields['source_ms']
    return [dict(row, source_ms=by_action[key(row)]) for row in native if key(row) in by_action]


def typed_receipts(agent, pattern, peer):
    for line in agent:
        match = pattern.search(line)
        if not match:
            continue
        values = dict(FIELD.findall(match[1]))
        if values.get('peer') == str(peer) and values.get('result') == 'accepted':
            yield values


def native_identity(row):
    return row['seq'], row['event_host_ms'], row['eid']


def receipt_identity(row):
    seq, stamp = number(row, 'seq'), number(row, 'event_host_ms')
    eid = row.get('eid', '')
    if seq is None or not stamp or not re.fullmatch(r'0x[0-9a-fA-F]+', eid):
        return None
    return seq & 0xFFFF, stamp, int(eid, 16)


def role_totals(lines, role):
    for line in reversed(lines):
        match = STATS.search(line)
        if match and match[1] == role:
            raw = dict(FIELD.findall(match[2]))
            fields = {name: number(raw, name) for name in TOTALS}
            if all(value is not None for value in fields.values()):
                return fields
    return {}


def health_checks(totals, staged, seen):
    if totals.get('dmg_double', 0):
        return ['double-damage-observed']
    if totals.get('owner_violations', 0):
        return ['owner-violations']
    if not totals or not totals['rp_owner_n'] or not totals['rp_action_confirmed_n'] or not totals['rp_actions_in']:
        return ['combat-or-owner-counters-unmeasured']
    if totals['rp_actions_pending'] or totals['rp_actions_played'] > totals['rp_actions_in']:
        return ['combat-counters-unsettled']
    if seen > staged:
        return ['duplicate-staged-native-actions']
    if totals['rp_action_confirmed_n'] < seen or totals['rp_actions_played'] < seen:
        return ['native-confirmations-missing-from-counters']
    if not staged or 100 * seen < 95 * staged:
        return ['staged-native-action-coverage-below-95-percent']
    return []


def stand(at, position, yaw):
    values = ' '.join(format(axis, '.9g') for axis in (*position, yaw))
    return f'at {at} stand {values}'


def fight_plan(partner, bandit, melee, swing, block, parry, hit):
    start = (partner[0] + 2, partner[1], partner[2])
    finish = (bandit[0] + 2, bandit[1], bandit[2])
    yaw = math.atan2(start[0] - finish[0], finish[1] - start[1])
    step = math.dist(start[:2], finish[:2]) / 12
    lines = [stand(0, start, math.pi / 2), f'at 1 appearance {melee}', 'at 3 state combat=1', 'at 5 draw',
             f'at 7 attack {swing}', f'at 10 block_impulse {block}']
    if step > 0:
        lines.append(f'at 14 move {step:.9g} {yaw:.9g} 12 {finish[2]:.9g}')
    lines.extend([stand(27, finish, math.pi / 2), f'at 31 attack {swing}', f'at 34 block_impulse {parry} perfect',
                  f'at 37 hit_reaction {hit}', 'end 52', ''])
    return '\n'.join(lines)


def down_plan(start):
    return '\n'.join([stand(0, start, 0), 'at 4 move 1 0 3', 'at 10 vitals 20 30 knockout', 'at 10 state downed=1',
                      'at 22 vitals 100 100', 'at 22 state downed=0', 'at 26 move 1 3.14159265 3', 'end 45', ''])


def bow_plan(start, weapon):
    lines = [stand(0, start, 0), 'at 3 bow_equip', 'at 9 ranged charge', 'at 12 ranged aim', 'at 15 ranged fire']
    if weapon == 'bow':
        lines.append('at 20 ranged reload')
    return '\n'.join([*lines, 'end 45', ''])


def bandit_position(copy, bandit):
    query = (f'local e=System.GetEntityByName("{bandit}"); if e and e.actor and not e.actor:IsDead() then '
             'local p=e:GetWorldPos(); System.LogAlways(string.format("[RPFIGHT-BANDIT] %.3f %.3f %.3f",p.x,p.y,p.z)) end')
    readback = copy.ask(query, 'RPFIGHT-BANDIT')
    position = tuple(map(float, readback.split())) if readback else None
    if not finite_position(position):
        raise RuntimeError('the named living bandit must be loaded on the real host')
    return position


def wait_near(copy, target, timeout=40):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        position = copy.pos()
        if finite_position(position) and math.dist(position, target) <= 6:
            return True
        time.sleep(0.25)
    return False


def player_fight(S, sender, receiver_pos, host, bandit):
    stages = []
    for name, need, prompt in (
            ('partner', {1, 6}, 'Beim Partner einen Schlag ausführen und seinen Gegenschlag normal blocken.'),
            ('bandit', {1, 131, 132}, 'Beim Banditen einen Schlag, eine Parade und eine echte Trefferreaktion auslösen.')):
        target = receiver_pos if name == 'partner' else bandit_position(host, bandit)
        S.rig.say(f'Kopie {sender.id}', 'Zum Partner gehen.' if name == 'partner' else f'Zum Banditen {bandit} gehen.')
        nearby = wait_near(sender, target)
        actions = []
        if nearby:
            offset = sender.size(sender.alog)
            S.rig.say(f'Kopie {sender.id}', prompt)
            until = time.monotonic() + 50
            while time.monotonic() < until:
                actions = fight_sent(sender.lines(sender.alog, offset), False)
                if need <= {row['kind'] for row in actions}:
                    break
                time.sleep(0.25)
        stages.append({'station': name, 'nearby': nearby, 'required_kinds': sorted(need), 'source_actions': actions})
    return stages


def player_down(S, sender):
    tag = str(time.time_ns())
    marker = r'\[OWNER-RIG ' + tag + r'\] ko='
    offset = sender.size(sender.klog)
    if not sender.lua(' '.join((SOURCE_STATUS % (tag, 190)).split())):
        raise RuntimeError('the source console did not install its read-only KO probe')
    result = []
    try:
        if not sender.wait('k', offset, marker + r'0\b', 10):
            return result
        for value, text in ((1, 'Im Wegwerfstand bei einer Prügelei KO gehen.'),
                            (0, 'Auf das Aufwachen warten und am selben Ort aufstehen.')):
            offset = sender.size(sender.klog)
            S.rig.say(f'Kopie {sender.id}', text)
            match = sender.wait('k', offset, marker + str(value) + r'\b', 80)
            if not match:
                break
            result.append({'down': value, 'source_readback': match[0]})
        return result
    finally:
        sender.lua_nowait(f'if KCDOwnerRigTag=="{tag}" then KCDOwnerRigTag=nil end')


def player_bow(S, sender, weapon):
    offset = sender.size(sender.alog)
    S.rig.say(f'Kopie {sender.id}', 'Bogen ziehen und einen Pfeil abgeben.' if weapon == 'bow' else 'Armbrust laden und einen Bolzen abgeben.')
    sender.wait('a', offset, rf'\bWO201-RANGED out firing weapon={weapon.title()}\b', 50)
    return fired(sender.lines(sender.alog, offset), False, weapon)


def bot_source(name, bot_log, weapon):
    lines = read_bot_log(bot_log)
    if name == 'rpfight':
        actions = fight_sent(lines, True)
        return [{'station': 'host', 'required_kinds': [1, 6], 'source_actions': actions[:2]},
                {'station': 'bandit', 'required_kinds': [1, 131, 132], 'source_actions': actions[2:]}]
    if name == 'rpdown':
        return [{'down': int(match[1]), 'source_ms': int(match[2])} for line in lines if (match := SCRIPTED_DOWN.search(line))]
    return fired(lines, True, weapon)


def staged_proof(name, source, native, agent, peer, weapon):
    if name == 'rpfight':
        sent = [action for stage in source for action in stage['source_actions']]
        proof = select_fight(sent, agent, native, peer)
        complete = all(set(stage['required_kinds']) <= {row['kind'] for row in stage['source_actions']} for stage in source)
        required = FIGHT_KINDS <= {row['kind'] for row in proof}
        return len({(row['kind'], row['source_ms']) for row in sent}), proof, complete and required
    if name == 'rpdown':
        receipts = {receipt_identity(row): number(row, 'down') for row in typed_receipts(agent, KO_IN, peer)}
        proof = [entry for entry in native if entry.get('down') in (1, 0) and receipts.get(native_identity(entry)) == entry['down']]
        return 2, proof, [entry['down'] for entry in source] == [1, 0] and [entry['down'] for entry in proof] == [1, 0]
    expected = {(row['seq'], row['epoch']) for row in source}
    sequences = {seq for seq, _ in expected}
    receipts = {receipt_identity(row) for row in typed_receipts(agent, RANGED_IN, peer)
                if row.get('weapon') == weapon.title() and number(row, 'phase') == 3 and number(row, 'seq') in sequences}
    proof = [entry for entry in native if entry.get('weapon') == WEAPONS[weapon] and entry.get('ranged_phase') == 3 and
             native_identity(entry) in receipts]
    return len(expected), proof, bool(expected and proof)


def receiver_evidence(copy, offsets, peer, name, source, weapon):
    agent = copy.lines(copy.alog, offsets['agent'])
    kinds = {'rpfight': FIGHT_KINDS, 'rpdown': {KO}, 'rpbow': {RANGED}}[name]
    values = []
    for path, offset in offsets['native'].items():
        if copy.size(path) >= offset:
            native = applied_actions(copy.lines(path, offset), peer, kinds)
            values.append(staged_proof(name, source, native, agent, peer, weapon))
    return max(values, key=lambda value: len(value[1]), default=(0, [], False))


def totals_after(copy, role, offset):
    pulse = copy.wait('a', offset, rf'MP-T3-STATS origin=agent role={role} .*\brp_actions_pending=0\b', 85)
    return role_totals(copy.lines(copy.alog, offset), role) if pulse else {}


def exercise(S, run, name, args, send_role):
    sender, receiver, receive_role = (run.a, run.b, 'joiner') if send_role == 'host' else (run.b, run.a, 'host')
    copies = ((receiver, receive_role),) if args.bot else ((receiver, receive_role), (sender, send_role))
    state = S.pair.state()
    if args.bot and S.procs.matches(state.get(run.b.id.lower())):
        raise RuntimeError('the Bot-Joiner replaces the second game, which must be stopped')
    positions = {}
    for copy, _ in copies:
        if not S.procs.matches(state.get(copy.id.lower())) or copy.port is None:
            raise RuntimeError(f'copy {copy.id} must be running in the game')
        position = copy.pos()
        if not finite_position(position):
            raise RuntimeError(f'copy {copy.id} did not return finite local player coordinates')
        positions[copy.id] = position

    relay = state.get('relay')
    network_evidence(relay, S.pair.POOR_NETWORK, args.netz, S.procs.matches, S.Copy.lines)
    marks = {copy.id: {'agent': copy.size(copy.alog), 'native': mark_native(copy)} for copy, _ in copies}
    bot = None
    bot_log = None
    if args.bot:
        x, y, z = positions[receiver.id]
        options = []
        if name == 'rpfight':
            target = bandit_position(receiver, args.bandit)
            if math.dist(target[:2], (x, y)) > 40 or abs(target[2] - z) > 3:
                raise RuntimeError('place the real host near the bandit with an open path between them')
            script = fight_plan((x, y, z), target, args.melee_class, args.attack_row, args.block_row, args.parry_row, args.hit_row)
        elif name == 'rpdown':
            script = down_plan((x + 2, y, z))
        else:
            tables = Path(receiver.p['root'], 'Data', 'Tables.pak')
            if not tables.is_file():
                raise RuntimeError('the RP bow fixture needs the local Tables.pak to verify the ranged item kind')
            options = ['--ranged-kind', args.weapon, '--tables-pak', str(tables.resolve())]
            if args.item_class is not None:
                options += ['--bow-class', str(args.item_class)]
            script = bow_plan((x + 2, y, z), args.weapon)
        plan = Path(run.dir, name + '-bot.txt')
        plan.write_text(script, encoding='utf-8')
        bot_log = Path(run.dir, 'bot-t4.log')
        with bot_log.open('w', encoding='utf-8') as stream:
            cmd = S.bot_joiner_command(run, '--scenario', str(plan.resolve()), '--duration', '175', *options)
            bot = subprocess.Popen(cmd, stdout=stream, stderr=subprocess.STDOUT)

    try:
        if bot is not None:
            peer = wait_bot_ready(bot, bot_log)
            bot.wait()
            source = bot_source(name, bot_log, args.weapon)
        else:
            ids = peer_ids(sender.lines(sender.alog, 0), ASSIGNED_PEER)
            if not ids:
                raise RuntimeError('source Agent has no assigned Relay ID')
            peer = ids[-1]
            if name == 'rpfight':
                source = player_fight(S, sender, positions[receiver.id], run.a, args.bandit)
            elif name == 'rpdown':
                source = player_down(S, sender)
            else:
                source = player_bow(S, sender, args.weapon)

        until = time.monotonic() + 15
        while time.monotonic() < until:
            planned, proof, required = receiver_evidence(receiver, marks[receiver.id], peer, name, source, args.weapon)
            if required and len(proof) >= planned:
                break
            time.sleep(0.25)
        with ThreadPoolExecutor(max_workers=len(copies)) as workers:
            jobs = {copy.id: workers.submit(totals_after, copy, role, copy.size(copy.alog)) for copy, role in copies}
            totals = {cid: job.result() for cid, job in jobs.items()}
    finally:
        if bot is not None:
            bot.wait()

    network = network_evidence(relay, S.pair.POOR_NETWORK, args.netz, S.procs.matches, S.Copy.lines, require_delay=True)
    planned, proof, required = receiver_evidence(receiver, marks[receiver.id], peer, name, source, args.weapon)
    primary = totals[receiver.id]
    failures = health_checks(primary, planned, len(proof))
    if not required:
        failures.append('required-native-actions-unmeasured')
    if name == 'rpdown' and primary.get('rp_ko_up_n', 0) < 2:
        failures.append('ko-and-rise-latency-unmeasured')
    if any(row.get('dmg_double', 0) for row in totals.values()) and 'double-damage-observed' not in failures:
        failures.append('double-damage-observed')
    if any(not row for row in totals.values()):
        failures.append('local-damage-counters-unmeasured')
    if any(row.get('dmg_double_unconfirmed', 0) for row in totals.values()):
        failures.append('damage-application-unconfirmed')
    if any(row.get('owner_violations', 0) for row in totals.values()) and 'owner-violations' not in failures:
        failures.append('owner-violations')
    if any(copy.size(copy.alog) < marks[copy.id]['agent'] or
           peer_ids(copy.lines(copy.alog, marks[copy.id]['agent']), ASSIGNED_PEER) for copy, _ in copies):
        failures.append('replaced-participant')
    if any(VIOLATION in line for copy, _ in copies for path, offset in marks[copy.id]['native'].items() for line in copy.lines(path, offset)):
        failures.append('native-owner-violation')
    if bot is not None:
        lines = read_bot_log(bot_log)
        if (bot.returncode or peer_ids(lines, BOT_PEER) != [peer] or 'BOT-T4 role=joiner presentation_clock=ready' not in lines or
                not any(line.startswith(BOT_DONE) for line in lines)):
            failures.append('t4-sender-incomplete-or-clock-unmeasured')

    bundles = S.pair.collect(name + '-%d' % time.time_ns(), write_verdict=True, copies=tuple(copy.id for copy, _ in copies), bot_log=bot_log)
    damage_guard_measured = (all(row and not row['dmg_double_unconfirmed'] for row in totals.values()) and
                             any(row['dmg_double_blocked'] for row in totals.values()))
    report = {'sender': 'scripted-t4' if args.bot else send_role, 'sender_peer': peer, 'receiver': receive_role,
              'source': source, 'expected_actions': planned, 'native_installs': proof,
              'sample_coverage_pct': round(100 * len(proof) / planned, 1) if planned else None,
              'damage_guard_measured': damage_guard_measured,
              'stats': totals, 'network': network, 'failures': failures,
              'bundles': {cid: path + '.packed' for cid, path in bundles.items()}}
    Path(run.dir, f'{name}-{send_role}.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    return not failures, failures[0] if failures else 'ok', report


def run(S, name, argv):
    parser = argparse.ArgumentParser(prog=f'scenarios.py {name}', description=__doc__)
    parser.add_argument('--test-rp-combat', action='store_true')
    parser.add_argument('--netz', choices=('normal', 'schlecht'), default='normal')
    parser.add_argument('--sender', choices=('host', 'joiner', 'both') if name == 'rpdown' else ('host', 'joiner'))
    parser.add_argument('--bot', action='store_true')
    parser.set_defaults(weapon=None)
    if name == 'rpfight':
        parser.add_argument('--bandit', required=True, help='entity name of a living bandit in the host test world')
        parser.add_argument('--melee-class', type=weapon_class, help='local melee item GUID for the T4 Bot-Joiner')
        parser.add_argument('--attack-row', type=weapon_class, default=BOT_SWING_ROW)
        for action in ('block', 'parry', 'hit'):
            parser.add_argument(f'--{action}-row', type=weapon_class, help=f'local T4 action row GUID for {action}')
    if name == 'rpbow':
        parser.add_argument('--weapon', choices=WEAPONS, default='bow')
        parser.add_argument('--item-class', type=weapon_class, help='optional T4 ranged item GUID, verified against Tables.pak')
    args = parser.parse_args(argv)
    if not args.test_rp_combat or os.environ.get('KCDMP_TEST_RP_COMBAT') != '1':
        parser.error('KCDMP_TEST_RP_COMBAT=1 and --test-rp-combat are required')
    senders = ('host', 'joiner') if args.sender == 'both' or (args.sender is None and name == 'rpdown' and not args.bot) else (args.sender or 'joiner',)
    if args.bot and (senders != ('joiner',) or os.environ.get('KCDMP_TEST_BOT') != '1'):
        parser.error('--bot requires KCDMP_TEST_BOT=1 and a scripted joiner')
    if name == 'rpfight':
        if not BANDIT.fullmatch(args.bandit):
            parser.error('use the in-world entity name of the bandit')
        if args.bot and not all((args.melee_class, args.block_row, args.parry_row, args.hit_row)):
            parser.error('--bot uses --melee-class, --block-row, --parry-row and --hit-row from the local test build')
    if name == 'rpbow' and args.item_class is not None and not args.bot:
        parser.error('--item-class applies to the local T4 item fixture')
    record = S.Run(name, include_joiner=not args.bot)
    if args.bot or record.step('join', S.s_join(record)):
        for role in senders:
            record.step(f'{name}-{role}', lambda: exercise(S, record, name, args, role))
    return record.finish()
