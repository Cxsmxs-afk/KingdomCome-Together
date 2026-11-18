# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
"""Lane 2: observe typed melee on two real, joined copies in a disposable save."""

import argparse
from concurrent.futures import ThreadPoolExecutor
import json
import os
from pathlib import Path
import re
import time

import scen_s2_combat as combat
from scen_s2_measure import ASSIGNED_PEER, finite_position, network_evidence, peer_ids
from scen_s2_owner import VIOLATION, mark_native
from scen_s2_timeline import FIELD, number


ACTION_KINDS = {'swing': 1, 'block': 6, 'parry': 0x83, 'hitreact': 0x84, 'recoil': 0x85}
RECOIL = re.compile(r'\bMP-ACTION section=outbound kind=AvatarRecoil (.+)$')
ROW_RESULT = re.compile(r'\bMP-RP-MELEE-ROW (.+)$')
OUTBOUND_KINDS = {'Attack': (ACTION_KINDS['swing'],), 'BlockImpulse': (ACTION_KINDS['block'], ACTION_KINDS['parry'])}
DAMAGE_FIELDS = ('dmg_double', 'dmg_double_blocked', 'dmg_double_unconfirmed')
MELEE_UNAVAILABLE = 'melee_presentation_unavailable'
FF_READBACK = ('if KCD2MP and KCD2MP.w121 then System.LogAlways("[LANE2-FF] " .. '
               '(KCD2MP.w121.ffSession and "on" or "off")) end')


def recoil_stage(rig, sender, observed):
    stage = {'station': 'recoil', 'nearby': observed, 'required_kinds': [ACTION_KINDS['recoil']], 'source_actions': []}
    if not observed:
        return stage
    offset = sender.size(sender.alog)
    rig.say(f'Kopie {sender.id}', 'Mit dem Langschwert einen Stich ausführen, den der Bandit blockt. Auf den Rückprall achten.')
    until = time.monotonic() + 50
    while time.monotonic() < until:
        for line in sender.lines(sender.alog, offset):
            match = RECOIL.search(line)
            stamp = number(dict(FIELD.findall(match[1])), 'source_ms') if match else None
            if stamp is not None and stamp <= 0xFFFFFFFF:
                stage['source_actions'].append({'kind': ACTION_KINDS['recoil'], 'source_ms': stamp})
        if stage['source_actions']:
            break
        time.sleep(0.25)
    return stage


def withheld_rows(rows, reason):
    return {(kind, stamp) for row in rows if row.get('reason') == reason
            and (stamp := number(row, 'source_ms')) is not None
            for kind in OUTBOUND_KINDS.get(row.get('kind'), ())}


def measure(stages, agent, native_logs, peer):
    sent = [action for stage in stages for action in stage['source_actions']]
    row_results = []
    for line in agent:
        match = ROW_RESULT.search(line)
        if match:
            fields = dict(FIELD.findall(match[1]))
            if fields.get('peer') == str(peer):
                row_results.append(fields)
    paired = withheld_rows(row_results, 'paired-engine-entry-unverified')
    counters = withheld_rows(row_results, 'counter-engine-entry-unverified')

    def identity(row):
        return row['kind'], row['source_ms']

    unsupported = paired | counters
    ordinary = [row for row in sent if identity(row) not in unsupported]
    candidates = [combat.select_fight(sent, agent, combat.applied_actions(lines, peer, set(ACTION_KINDS.values())), peer)
                  for lines in native_logs]
    installed = max(candidates, key=len, default=[])
    unverified_install = {identity(row) for rows in candidates for row in rows if identity(row) in unsupported}
    installed = [row for row in installed if identity(row) not in unsupported]
    counts = {name: {'sent': len({row['source_ms'] for row in ordinary if row['kind'] == kind}),
                     'installed': sum(row['kind'] == kind for row in installed)}
              for name, kind in ACTION_KINDS.items()}
    for row in counts.values():
        row['status'] = ('red' if row['installed'] > row['sent'] else
                         'green' if row['sent'] and 100 * row['installed'] >= 95 * row['sent'] else 'grey')
    observed = {identity(row) for row in installed}
    station_checks = {stage['station']: set(stage['required_kinds']) <= {row['kind'] for row in stage['source_actions']
                                                                       if identity(row) in observed} for stage in stages}
    return {
        'counters': {'melee_swing_shown': counts['swing']['installed'],
                     'melee_block_shown': counts['block']['installed'] + counts['parry']['installed'],
                     'melee_hitreact_shown': counts['hitreact']['installed']},
        'per_kind': counts,
        'stations_confirmed': station_checks,
        'native_installs': installed,
        'row_results': row_results,
        'paired_attack': {'status': 'red' if paired & unverified_install else 'grey'},
        'master_strike': {'status': 'red' if counters & unverified_install else 'grey'},
        'unverified_ordinary_installs': sorted(unverified_install),
        'screen_observation': {'status': 'grey', 'reason': 'observer/video evidence is separate from the native action watch'},
    }


def damage_evidence(before, after, receiver, stable):
    if any(row.get('dmg_double', 0) for stats in (before, after) for row in stats.values()):
        return {'status': 'red', 'deltas': {}}
    if not stable or receiver not in after or before.keys() != after.keys() or any(
            field not in row for stats in (before, after) for row in stats.values() for field in DAMAGE_FIELDS):
        return {'status': 'grey', 'deltas': {}}
    deltas = {role: {field: row[field] - before[role][field] for field in DAMAGE_FIELDS} for role, row in after.items()}
    if any(value < 0 for row in deltas.values() for value in row.values()):
        return {'status': 'grey', 'deltas': {}}
    measured = deltas[receiver]['dmg_double_blocked'] > 0 and all(not row['dmg_double_unconfirmed'] for row in after.values())
    return {'status': 'green' if measured else 'grey', 'deltas': deltas}


def unavailable_evidence(before, after, receiver, stable):
    start = before.get(receiver, {}).get(MELEE_UNAVAILABLE)
    end = after.get(receiver, {}).get(MELEE_UNAVAILABLE)
    if not stable or start is None or end is None or end < start:
        return {'status': 'grey', 'delta': None}
    return {'status': 'red' if end > start else 'green', 'delta': end - start}


def role_totals(lines, role):
    for line in reversed(lines):
        totals = combat.role_totals((line,), role)
        if totals:
            unavailable = number(dict(FIELD.findall(line)), MELEE_UNAVAILABLE)
            if unavailable is not None:
                totals[MELEE_UNAVAILABLE] = unavailable
            return totals
    return {}


def totals_after(copy, role, offset):
    pulse = copy.wait('a', offset, rf'MP-T3-STATS origin=agent role={role} .*\brp_actions_pending=0\b', 85)
    return role_totals(copy.lines(copy.alog, offset), role) if pulse else {}


def fresh_stats(copies, marks):
    with ThreadPoolExecutor(max_workers=len(copies)) as workers:
        jobs = {role: workers.submit(totals_after, copy, role, marks[role]) for role, copy in copies.items()}
        return {role: job.result() for role, job in jobs.items()}


def exercise(S, run, args, send_role):
    sender, receiver = (run.a, run.b) if send_role == 'host' else (run.b, run.a)
    copies = {'host': run.a, 'joiner': run.b}
    receive_role = 'joiner' if send_role == 'host' else 'host'
    state = S.pair.state()
    ids = {}
    for role, copy in copies.items():
        if not S.procs.matches(state.get(copy.id.lower())) or copy.port is None:
            raise RuntimeError(f'copy {copy.id} must be running in the game')
        if copy.ask(FF_READBACK, 'LANE2-FF') != 'on':
            raise RuntimeError(f'copy {copy.id} did not report Friendly Fire on')
        assigned = peer_ids(copy.lines(copy.alog, 0), ASSIGNED_PEER)
        if not assigned:
            raise RuntimeError(f'copy {copy.id} has no assigned Relay ID')
        ids[role] = assigned[-1]
    receiver_pos = receiver.pos()
    if not finite_position(receiver_pos):
        raise RuntimeError('the receiver did not return finite local player coordinates')

    relay = state.get('relay')
    network_evidence(relay, S.pair.POOR_NETWORK, args.netz, S.procs.matches, S.Copy.lines)
    start_marks = {role: copy.size(copy.alog) for role, copy in copies.items()}
    S.rig.say('T3-Ausgangswerte werden gelesen. Mit den Aktionen bis zur Ansage warten.')
    before = fresh_stats(copies, start_marks)
    agent_marks = {role: copy.size(copy.alog) for role, copy in copies.items()}
    native_marks = {role: mark_native(copy) for role, copy in copies.items()}
    S.rig.say(f'Kopie {receiver.id}', 'Die Partnerfigur beobachten und Schlag, Block, Parade, Treffer und Rückprall im Bild festhalten.')
    S.rig.say('Beim Partner soll der Sender zuerst treffen. Danach führt der Empfänger den Gegenschlag für den Block aus.')
    stages = combat.player_fight(S, sender, receiver_pos, run.a, args.bandit)
    stages.append(recoil_stage(S.rig, sender, stages[-1]['nearby']))

    S.rig.say('Die T3-Zähler werden gelesen. Keine weiteren Kampfaktionen ausführen.')
    stats = fresh_stats(copies, {role: copy.size(copy.alog) for role, copy in copies.items()})
    network = network_evidence(relay, S.pair.POOR_NETWORK, args.netz, S.procs.matches, S.Copy.lines, require_delay=True)
    agent = {role: copy.lines(copy.alog, agent_marks[role]) for role, copy in copies.items()}
    native = {role: [copy.lines(path, offset) for path, offset in native_marks[role].items() if copy.size(path) >= offset]
              for role, copy in copies.items()}
    report = measure(stages, agent[receive_role], native[receive_role], ids[send_role])
    counts = report['per_kind']
    planned = sum(row['sent'] for row in counts.values())
    failures = combat.health_checks(stats[receive_role], planned, len(report['native_installs']))
    if any(row['status'] != 'green' for row in counts.values()) or not all(report['stations_confirmed'].values()):
        failures.append('required-melee-actions-unmeasured-or-duplicated')
    if report['unverified_ordinary_installs']:
        failures.append('paired-or-counter-action-played-as-ordinary-melee')
    replaced = any(copy.size(copy.alog) < start_marks[role] or not S.procs.matches(state.get(copy.id.lower()))
                   or peer_ids(copy.lines(copy.alog, start_marks[role]), ASSIGNED_PEER) for role, copy in copies.items())
    if replaced:
        failures.append('replaced-participant')
    changed_world = any('MP-LOAD-SEQUENCE role=' in line for lines in agent.values() for line in lines)
    if changed_world:
        failures.append('world-lifecycle-changed')
    stable = not replaced and not changed_world
    damage = damage_evidence(before, stats, receive_role, stable)
    if damage['status'] != 'green':
        failures.append('double-damage-observed' if damage['status'] == 'red' else 'double-damage-guard-unmeasured')
    if any(row.get('owner_violations', 0) for row in stats.values()) or any(
            VIOLATION in line for logs in native.values() for lines in logs for line in lines):
        failures.append('owner-violations')
    report.update(sender=send_role, receiver=receive_role, sender_peer=ids[send_role], stages=stages,
                  t3_before=before, t3_stats=stats, damage_guard=damage['status'], damage_deltas=damage['deltas'],
                  melee_presentation_unavailable=unavailable_evidence(before, stats, receive_role, stable),
                  failures=list(dict.fromkeys(failures)), network=network)
    path = Path(run.dir, f'lane2-melee-{send_role}.json')
    path.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    bundles = S.pair.collect(f'lane2-melee-{send_role}-{time.time_ns()}', write_verdict=True)
    report['bundles'] = {cid: bundle + '.packed' for cid, bundle in bundles.items()}
    path.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    return not failures, failures[0] if failures else 'ok', {'report': str(path), **report['counters'], 'damage_guard': damage['status']}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--test-rp-combat', action='store_true')
    parser.add_argument('--netz', choices=('normal', 'schlecht'), default='normal')
    parser.add_argument('--sender', choices=('host', 'joiner', 'both'), default='joiner')
    parser.add_argument('--bandit', required=True, help='entity name of a living bandit in the host test world')
    args = parser.parse_args(argv)
    if not args.test_rp_combat or os.environ.get('KCDMP_TEST_RP_COMBAT') != '1':
        parser.error('KCDMP_TEST_RP_COMBAT=1 and --test-rp-combat are required')
    if not combat.BANDIT.fullmatch(args.bandit):
        parser.error('use the in-world entity name of the bandit')
    if os.name != 'nt':
        parser.error('the live scenario requires the Windows KCD II Modding Tools rig')

    import scenarios

    run = scenarios.Run('lane2-melee')
    if run.step('join', scenarios.s_join(run)):
        senders = ('host', 'joiner') if args.sender == 'both' else (args.sender,)
        for role in senders:
            run.step(f'lane2-melee-{role}', lambda: exercise(scenarios, run, args, role))
            if run.steps[-1]['reason'] == 'copy-exited':
                break
    return run.finish()


if __name__ == '__main__':
    raise SystemExit(main())
