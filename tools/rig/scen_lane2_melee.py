# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
"""Lane 2: observe typed melee on two real, joined copies in a disposable save."""

import argparse
from concurrent.futures import ThreadPoolExecutor
import json
import math
import os
from pathlib import Path
import re
import time

import scen_s2_combat as combat
from scen_s2_measure import ASSIGNED_PEER, finite_position, network_evidence, peer_ids
from scen_s2_owner import VIOLATION, mark_native
from scen_s2_timeline import FIELD, INBOUND, INSTALLED, number


ACTION_KINDS = {'swing': 1, 'block': 6, 'parry': 0x83, 'hitreact': 0x84, 'recoil': 0x85}
RECOIL = re.compile(r'\bMP-ACTION section=outbound kind=AvatarRecoil (.+)$')
ROW_RESULT = re.compile(r'\bMP-RP-MELEE-ROW (.+)$')
SWING_FALLBACK = re.compile(r'\bMP-ACTION section=inbound ghost=(\d+) kind=Attack seq=(\d+) dispatch=swing-queued\b')
OUTBOUND_KINDS = {'Attack': (ACTION_KINDS['swing'],), 'BlockImpulse': (ACTION_KINDS['block'], ACTION_KINDS['parry'])}
DAMAGE_FIELDS = ('dmg_double', 'dmg_double_blocked', 'dmg_double_unconfirmed')
MELEE_UNAVAILABLE = 'melee_presentation_unavailable'
FF_READBACK = ('if KCD2MP and KCD2MP.w121 then System.LogAlways("[LANE2-FF] " .. '
               '(KCD2MP.w121.ffSession and "on" or "off")) end')
LIVE_CASES = {
    'partner-hit': 'Einmal den Partner treffen. Der Partner hält still.',
    'partner-miss': 'Einmal sichtbar am Partner vorbeischlagen.',
    'npc-hit': 'Einmal den gewählten NPC treffen.',
    'npc-miss': 'Einmal sichtbar am gewählten NPC vorbeischlagen.',
    'npc-near-partner': 'Einmal den gewählten NPC neben dem Partner treffen. Der Partner hält still.',
    'npc-block': 'Einmal den gewählten NPC angreifen, während der NPC den Treffer blockt.',
    'exhaustion': 'Ein letzter Treffer auf den vorbereiteten Partner mit niedriger Ausdauer. Danach stillhalten.',
    'ko-rise': 'Ein echter letzter Treffer für den vorbereiteten KO. Danach das Aufstehen abwarten.',
    'death': 'Im Wegwerfstand den vorbereiteten letzten Treffer ausführen. Die bestehende Wiederherstellung abwarten.',
    'protected': 'Einmal den Partner im vorbereiteten Schutzfenster treffen.',
}
VITALS = '''local e=%s
if not e or not e.id or not e.actor or not e.soul then return end
local id=tostring(e.id):match("(%%x+)%%s*$"); if not id then return end
local h,st=-1,-1; local dead,ko=-1,-1
local function bit(v) if v==true or v==1 then return 1 elseif v==false or v==0 then return 0 end; return -1 end
pcall(function() h=e.actor:GetHealth() end); pcall(function() st=e.soul:GetState("stamina") end)
pcall(function() dead=bit(e.actor:IsDead()) end); pcall(function() ko=bit(e.actor:IsUnconscious()) end)
local p=e:GetWorldPos()
if type(h)=="number" and type(st)=="number" then
 System.LogAlways(string.format("[MP-LANE2-COMBAT] scope=%s phase=%s target=%s eid=0x%%s hp=%%.3f st=%%.3f dead=%%d ko=%%d game_s=%%.3f x=%%.3f y=%%.3f z=%%.3f",id,h,st,dead,ko,System.GetCurrTime(),p.x,p.y,p.z))
end'''
LIVE_FIELDS = re.compile(r'\b([a-z][a-z0-9_]*)=([^\s]+)')
HOST_CONTACT = re.compile(r'\bWO176-HOSTHIT victim=(0x[0-9A-Fa-f]+) outcome=(\w+)\b')
NPC_CONTACT = re.compile(r'\bMP-W173 hit npc=(\S+) by_ghost=(\d+) applied=engine verdict=(\S+)')
PVP = re.compile(r'\bMP-FF (.+)$')
PVP_APPLY = re.compile(r'\bWO121-HITS friendly-fire hit on the player: hp -([0-9.]+) st -([0-9.]+) unarmed=(\d+) from ghost (\d+) -> applied\b')
AUTH_READBACK = ('if KCD2MP and KCD2MP.wo102 then System.LogAlways("[LANE2-AUTH] " .. '
                 '(KCD2MP.wo102.authorityHost and KCD2MP.hitSensorOn and "on" or "off")) end')
RELEVANT_AGENT = ('MP-RP-MELEE-ROW', 'MP-RP-TIMELINE-IN', 'MP-RP-KO-IN', 'MP-FF', 'MP-W173 hit')
RELEVANT_NATIVE = ('MP-RP-COMBAT-ACTION', 'MP-RP-KO', 'MP-RP-DAMAGE', 'WO121-HITS', 'WO173-HIT', 'WO176-HOSTHIT',
                   'WO208-DEF', 'WO154-DOWN', 'MP-RESPAWN downed', 'MP-RESPAWN done', 'T3-RECOVERY', 'T3-DEATHCAUSE')


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


def unavailable_receipts(lines, peer):
    seen = {}
    for line in lines:
        match = INBOUND.search(line) or ROW_RESULT.search(line)
        if match:
            row = dict(FIELD.findall(match[1]))
            seq = number(row, 'seq')
            source_ms = number(row, 'source_ms')
            if (row.get('peer') == str(peer) and row.get('result') == 'unavailable'
                    and row.get('reason') == 'typed-provider-unavailable' and seq is not None and source_ms is not None):
                seen[seq, source_ms] = row
    return list(seen.values())


def intentional_unavailable(S, sender, receiver_pos):
    nearby = combat.wait_near(sender, receiver_pos)
    stage = {'station': 'intentionally-unavailable', 'nearby': nearby, 'required_kinds': [ACTION_KINDS['swing']], 'source_actions': []}
    if nearby:
        offset = sender.size(sender.alog)
        S.rig.say(f'Kopie {sender.id}', 'Im vorbereiteten Unavailable-Testzustand einmal in die Luft schlagen. Der Partner bleibt außer Reichweite.')
        sender.wait('a', offset, r'\bMP-ACTION section=outbound kind=Attack\b', 50)
        stage['source_actions'] = [row for row in combat.fight_sent(sender.lines(sender.alog, offset), False)
                                   if row['kind'] == ACTION_KINDS['swing']]
    return [stage]


def negative_unavailable_evidence(stages, receipts, agent, native_logs, peer, stable):
    sent = {row['source_ms'] for stage in stages for row in stage['source_actions']}
    matched = [row for row in receipts if number(row, 'source_ms') in sent and row.get('kind') in ('Attack', '1')]
    seqs = {number(row, 'seq') for row in matched}
    unexpected = []
    for line in agent:
        if fallback := SWING_FALLBACK.search(line):
            if int(fallback[1]) == peer and int(fallback[2]) in seqs:
                unexpected.append(line)
        if receipt := INBOUND.search(line):
            row = dict(FIELD.findall(receipt[1]))
            if row.get('peer') == str(peer) and number(row, 'seq') in seqs and row.get('result') == 'accepted':
                unexpected.append(line)
    for lines in native_logs:
        for line in lines:
            if result := INSTALLED.search(line):
                row = dict(FIELD.findall(result[1]))
                if row.get('peer') == str(peer) and number(row, 'seq') in seqs and row.get('result') in ('queued', 'installed'):
                    unexpected.append(line)
    covered = stable and len(sent) == len(matched) == 1 and any(native_logs)
    status = 'red' if unexpected else 'green' if covered else 'grey'
    return {'status': status, 'count': len(matched), 'events': matched, 'unexpected_playback': unexpected}


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


def check_pair(S, copies):
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
    return state, ids


def parse_vitals(line, scope, phase, target):
    fields = dict(LIVE_FIELDS.findall(line or ''))
    if (fields.get('scope'), fields.get('phase'), fields.get('target')) != (scope, phase, target):
        return None
    if not re.fullmatch(r'0x[0-9A-Fa-f]+', fields.get('eid', '')):
        return None
    try:
        eid = int(fields['eid'], 16)
        hp, st, game_s = (float(fields[name]) for name in ('hp', 'st', 'game_s'))
        pos = tuple(float(fields[axis]) for axis in ('x', 'y', 'z'))
    except (KeyError, ValueError):
        return None
    if not eid or not finite_position(pos) or not all(math.isfinite(value) and value >= 0 for value in (hp, st, game_s)):
        return None
    return dict(eid=eid, hp=hp, st=st, game_s=game_s, pos=pos, dead=number(fields, 'dead'), ko=number(fields, 'ko'), raw=line)


def snapshot(copy, entity, scope, phase, target):
    lua = ' '.join((VITALS % (entity, scope, phase, target)).split())
    return parse_vitals(copy.ask(lua, 'MP-LANE2-COMBAT', 2), scope, phase, target)


def window_deltas(before, after):
    return {target: {'hp': round(sample['hp'] - after[target]['hp'], 3),
                     'st': round(sample['st'] - after[target]['st'], 3),
                     'window_game_s': round(after[target]['game_s'] - sample['game_s'], 3)}
            for target, sample in before.items() if sample and after.get(target)
            and sample['eid'] == after[target]['eid'] and sample['game_s'] < after[target]['game_s']}


def partner_contacts(outgoing, incoming, attacker, victim):
    sent, received = [], []
    for lines, kind, matches, direction, results in (
            (outgoing, 'out', {'ghost': str(victim)}, sent, ('sent',)),
            (incoming, 'in', {'attacker': str(attacker), 'victim': str(victim)}, received,
             ('applied', 'acknowledged', 'protected_consumed'))):
        for line in lines:
            if match := PVP.search(line):
                row = dict(LIVE_FIELDS.findall(match[1]))
                if (row.get('dir') == kind and row.get('session') == 'on' and row.get('missile') == '0'
                        and row.get('result') in results and all(row.get(key) == value for key, value in matches.items())):
                    direction.append(line)
    return sent, received


def authority_contacts(send_role, actor, bandit, eid, host_agent, host_native):
    if send_role == 'host':
        return [(match[2], line) for line in host_native if (match := HOST_CONTACT.search(line)) and int(match[1], 16) == eid]
    return [(match[3], line) for line in host_agent if (match := NPC_CONTACT.search(line))
            and match[1] == bandit and int(match[2]) == actor]


def trace_after(copy, agent_start, native_starts):
    agent = copy.lines(copy.alog, agent_start)
    files = [copy.lines(path, offset) for path, offset in native_starts.items() if copy.size(path) >= offset]
    return agent, max(files, key=len, default=[])


def case_signal(case, before, after, sent, received, protected, npc_hits, receiver_native, source_peer):
    if case == 'npc-miss' and len(npc_hits) == 1 and npc_hits[0][0] == 'missed':
        return 'npc-miss-observed'
    if case in ('partner-miss', 'npc-miss'):
        return 'miss-needs-observer'
    if case.startswith('npc-'):
        expected = ('blocked', 'pb') if case == 'npc-block' else ('hit',)
        if len(npc_hits) != 1 or npc_hits[0][0] not in expected:
            return 'npc-outcome-unmeasured'
        if case == 'npc-near-partner' and not (before.get('partner') and before.get('npc') and
                                              math.dist(before['partner']['pos'], before['npc']['pos']) <= 3.0):
            return 'npc-partner-proximity-unmeasured'
        return 'npc-outcome-observed'
    if len(sent) != 1 or len(received) != 1:
        return 'partner-contact-unmeasured'
    outbound = dict(LIVE_FIELDS.findall(sent[0]))
    inbound = dict(LIVE_FIELDS.findall(received[0]))
    fields = ('hp', 'st', 'unarmed')
    if any(key not in outbound or outbound[key] != inbound.get(key) for key in fields):
        return 'partner-contact-values-mismatch'
    applied = [match for line in receiver_native if (match := PVP_APPLY.search(line)) and int(match[4]) == source_peer]
    if case == 'protected':
        return 'protected-consumed-zero' if len(protected) == 1 and not applied else 'protection-unmeasured'
    if protected or inbound.get('result') == 'protected_consumed':
        return 'protected-instead-of-applied'
    if len(applied) != 1 or applied[0].groups()[:3] != tuple(inbound[key] for key in fields):
        return 'partner-damage-apply-unmeasured'
    if case == 'exhaustion':
        return 'exhaustion-observed' if (before.get('partner') and after.get('partner')
                                         and before['partner']['st'] > 0 and after['partner']['st'] == 0) else 'exhaustion-unmeasured'
    if case == 'ko-rise':
        down = next((i for i, line in enumerate(receiver_native) if 'WO154-DOWN the local player is DOWN' in line), None)
        up = down is not None and any('WO154-DOWN the local player is up again' in line for line in receiver_native[down + 1:])
        died = any('MP-RESPAWN downed kind=death' in line for line in receiver_native)
        real_ko = (after.get('partner') and after['partner']['ko'] == 1 and after['partner']['dead'] == 0
                   or any('MP-RESPAWN knockdown: the player is unconscious' in line for line in receiver_native))
        return 'ko-rise-observed' if up and real_ko and not died else 'ko-rise-unmeasured'
    if case == 'death':
        death = next((i for i, line in enumerate(receiver_native) if 'MP-RESPAWN downed kind=death' in line), None)
        done = next((i for i, line in enumerate(receiver_native) if death is not None and i > death
                     and 'MP-RESPAWN done kind=death' in line), None)
        restored = done is not None and any('T3-RECOVERY armed' in line for line in receiver_native[death + 1:done])
        return 'death-recovery-observed' if restored else 'death-recovery-unmeasured'
    return 'partner-application-observed'


def observe_combat(S, run, args, send_role):
    case = args.combat_case
    receive_role = 'joiner' if send_role == 'host' else 'host'
    copies = {'host': run.a, 'joiner': run.b}
    sender, receiver = copies[send_role], copies[receive_role]
    state, ids = check_pair(S, copies)
    if run.a.ask(AUTH_READBACK, 'LANE2-AUTH') != 'on':
        raise RuntimeError('host damage authority was not confirmed')
    relay = state.get('relay')
    network_evidence(relay, S.pair.POOR_NETWORK, args.netz, S.procs.matches, S.Copy.lines)
    targets = {'partner': (receiver, 'player')}
    if args.bandit:
        targets['npc'] = (run.a, f'System.GetEntityByName("{args.bandit}")')
    target = 'npc' if case.startswith('npc-') else 'partner'
    scope = str(time.time_ns())

    def take(phase):
        # Host can be both the real partner target and the local NPC authority. Keep its console calls serial.
        return {name: snapshot(copy, entity, scope, phase, name) for name, (copy, entity) in targets.items()}

    before = take('before')
    marks = {role: copy.size(copy.alog) for role, copy in copies.items()}
    native_marks = {role: mark_native(copy) for role, copy in copies.items()}
    S.rig.say(f'Kopie {sender.id}', LIVE_CASES[case])
    source = sender.wait('a', marks[send_role], r'\bMP-ACTION section=outbound kind=Attack\b', 45)
    if source:
        if target == 'partner' and case != 'partner-miss':
            receiver.wait('a', marks[receive_role], rf'\bMP-FF dir=in attacker={ids[send_role]} victim={ids[receive_role]}\b', 4)
        elif target == 'npc' and case != 'npc-miss' and before.get('npc'):
            deadline = time.monotonic() + 4
            while time.monotonic() < deadline:
                host_agent, host_native = trace_after(run.a, marks['host'], native_marks['host'])
                if authority_contacts(send_role, ids[send_role], args.bandit, before['npc']['eid'], host_agent, host_native):
                    break
                time.sleep(0.05)
        else:
            time.sleep(2)
    after = take('after')
    if source and case in ('ko-rise', 'death'):
        terminal = 'up again' if case == 'ko-rise' else 'MP-RESPAWN done kind=death'
        deadline = time.monotonic() + 45
        while time.monotonic() < deadline:
            _, receiver_native = trace_after(receiver, marks[receive_role], native_marks[receive_role])
            if any(terminal in line for line in receiver_native):
                break
            time.sleep(0.25)
    traces = {role: trace_after(copy, marks[role], native_marks[role]) for role, copy in copies.items()}
    sent, received = partner_contacts(traces[send_role][0], traces[receive_role][0], ids[send_role], ids[receive_role])
    protected = [line for line in traces[receive_role][1]
                 if re.search(rf'\bT3-RECOVERY hit=Pvp decision=protected_consumed applied_hp=0 applied_st=0 peer={ids[send_role]}\b', line)]
    source_actions = [row for row in combat.fight_sent(traces[send_role][0], False) if row['kind'] == ACTION_KINDS['swing']]
    deltas = window_deltas(before, after)
    world_changed = any('MP-LOAD-SEQUENCE role=' in line or ASSIGNED_PEER.search(line)
                        for lines, _ in traces.values() for line in lines)
    ff_after = all(copy.ask(FF_READBACK, 'LANE2-FF') == 'on' for copy in copies.values())
    host_authority = run.a.ask(AUTH_READBACK, 'LANE2-AUTH') == 'on'
    wrong_partner = target == 'npc' and bool(sent or received)
    stable = ff_after and host_authority and not world_changed
    npc_hits = authority_contacts(send_role, ids[send_role], args.bandit, before['npc']['eid'], *traces['host']) if before.get('npc') else []
    signal = case_signal(case, before, after, sent, received, protected, npc_hits, traces[receive_role][1], ids[send_role])
    network = network_evidence(relay, S.pair.POOR_NETWORK, args.netz, S.procs.matches, S.Copy.lines, require_delay=True)
    report = dict(case=case, scope=scope, sender=send_role, receiver=receive_role, target=target, peers=ids,
                  health_stamina_before=before, health_stamina_after=after, window_deltas=deltas, source_actions=source_actions, signal=signal,
                  wrong_partner_contact=wrong_partner, friendly_fire_after=ff_after, host_authority_after=host_authority,
                  partner_sent=sent, partner_acknowledgements=received, protected_consumed_zero=protected, npc_authority_contact=npc_hits,
                  exact_event_readback={'status': 'grey', 'reason': 'window snapshots have no shared engine contact id'}, network=network,
                  traces={role: {'agent': [line for line in alog if any(key in line for key in RELEVANT_AGENT)],
                                 'native': [line for line in nlog if any(key in line for key in RELEVANT_NATIVE)]}
                          for role, (alog, nlog) in traces.items()})
    path = Path(run.dir, f'lane2-combat-{case}-{send_role}.json')
    path.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    bundles = S.pair.collect(f'lane2-combat-{case}-{send_role}-{scope}', write_verdict=True)
    report['bundles'] = {cid: bundle + '.packed' for cid, bundle in bundles.items()}
    path.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    reason = 'wrong-partner-contact' if wrong_partner else 'isolated-window-unmeasured'
    if stable and len(source_actions) == 1 and target in deltas and not wrong_partner:
        observed = signal.endswith('-observed') or signal == 'protected-consumed-zero'
        return observed, signal, {'report': str(path), 'exact_event_readback': 'grey'}
    return False, reason, {'report': str(path), 'exact_event_readback': 'grey'}


def exercise(S, run, args, send_role):
    sender, receiver = (run.a, run.b) if send_role == 'host' else (run.b, run.a)
    copies = {'host': run.a, 'joiner': run.b}
    receive_role = 'joiner' if send_role == 'host' else 'host'
    state, ids = check_pair(S, copies)
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
    if args.expect_unavailable:
        stages = intentional_unavailable(S, sender, receiver_pos)
    else:
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
    failures = []
    replaced = any(copy.size(copy.alog) < start_marks[role] or not S.procs.matches(state.get(copy.id.lower()))
                   or peer_ids(copy.lines(copy.alog, start_marks[role]), ASSIGNED_PEER) for role, copy in copies.items())
    if replaced:
        failures.append('replaced-participant')
    changed_world = any('MP-LOAD-SEQUENCE role=' in line for lines in agent.values() for line in lines)
    if changed_world:
        failures.append('world-lifecycle-changed')
    stable = not replaced and not changed_world
    unavailable = unavailable_evidence(before, stats, receive_role, stable)
    receipts = unavailable_receipts(agent[receive_role], ids[send_role])
    if args.expect_unavailable:
        negative = negative_unavailable_evidence(stages, receipts, agent[receive_role], native[receive_role], ids[send_role], stable)
        report['intentional_negative'] = negative
        if negative['status'] != 'green':
            failures.append('negative-unavailable-playback-observed' if negative['status'] == 'red' else 'negative-unavailable-unmeasured')
    else:
        if receipts or unavailable['status'] == 'red':
            unavailable['status'] = 'red'
            failures.append('melee-presentation-unavailable')
        elif unavailable['status'] == 'grey':
            failures.append('melee-presentation-unavailable-unmeasured')
        counts = report['per_kind']
        planned = sum(row['sent'] for row in counts.values())
        failures.extend(combat.health_checks(stats[receive_role], planned, len(report['native_installs'])))
        if any(row['status'] != 'green' for row in counts.values()) or not all(report['stations_confirmed'].values()):
            failures.append('required-melee-actions-unmeasured-or-duplicated')
        if report['unverified_ordinary_installs']:
            failures.append('paired-or-counter-action-played-as-ordinary-melee')
    damage = damage_evidence(before, stats, receive_role, stable)
    if damage['status'] == 'red':
        failures.append('double-damage-observed')
    elif not args.expect_unavailable and damage['status'] == 'grey':
        failures.append('double-damage-guard-unmeasured')
    if any(row.get('dmg_double_unconfirmed', 0) for row in stats.values()):
        failures.append('damage-application-unconfirmed')
    if any(row.get('owner_violations', 0) for row in stats.values()) or any(
            VIOLATION in line for logs in native.values() for lines in logs for line in lines):
        failures.append('owner-violations')
    report_name = 'lane2-melee-unavailable-neg' if args.expect_unavailable else 'lane2-melee'
    report.update(sender=send_role, receiver=receive_role, sender_peer=ids[send_role], stages=stages,
                  t3_before=before, t3_stats=stats, damage_guard=damage['status'], damage_deltas=damage['deltas'],
                  melee_presentation_unavailable={**unavailable, 'events': receipts},
                  failures=list(dict.fromkeys(failures)), network=network)
    path = Path(run.dir, f'{report_name}-{send_role}.json')
    path.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    bundles = S.pair.collect(f'{report_name}-{send_role}-{time.time_ns()}', write_verdict=True)
    report['bundles'] = {cid: bundle + '.packed' for cid, bundle in bundles.items()}
    path.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    return not failures, failures[0] if failures else 'ok', {'report': str(path), **report['counters'], 'damage_guard': damage['status']}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--test-rp-combat', action='store_true')
    parser.add_argument('--netz', choices=('normal', 'schlecht'), default='normal')
    parser.add_argument('--sender', choices=('host', 'joiner', 'both'), default='joiner')
    parser.add_argument('--bandit', help='entity name of a living bandit in the host test world')
    modes = parser.add_mutually_exclusive_group()
    modes.add_argument('--expect-unavailable', action='store_true', help='record the separate negative case with a prepared unavailable provider')
    modes.add_argument('--combat-case', choices=LIVE_CASES, help='record one isolated live contact and real vital readbacks')
    args = parser.parse_args(argv)
    if not args.test_rp_combat or os.environ.get('KCDMP_TEST_RP_COMBAT') != '1':
        parser.error('KCDMP_TEST_RP_COMBAT=1 and --test-rp-combat are required')
    if not args.expect_unavailable and (args.combat_case is None or args.combat_case.startswith('npc-')) and not args.bandit:
        parser.error('--bandit is required for the regular scenario and NPC combat cases')
    if args.bandit and not combat.BANDIT.fullmatch(args.bandit):
        parser.error('use the in-world entity name of the bandit')
    if os.name != 'nt':
        parser.error('the live scenario requires the Windows KCD II Modding Tools rig')

    import scenarios

    if args.combat_case:
        name, scenario = f'lane2-combat-{args.combat_case}', observe_combat
    else:
        name, scenario = ('lane2-melee-unavailable-neg' if args.expect_unavailable else 'lane2-melee'), exercise
    run = scenarios.Run(name)
    if run.step('join', scenarios.s_join(run)):
        senders = ('host', 'joiner') if args.sender == 'both' else (args.sender,)
        for role in senders:
            run.step(f'{name}-{role}', lambda: scenario(scenarios, run, args, role))
            if run.steps[-1]['reason'] == 'copy-exited':
                break
    return run.finish()


if __name__ == '__main__':
    raise SystemExit(main())
