# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
"""Ordnet die Launcher- und Menü-Ereignisse aus zwei manuellen Sitzungen zu."""

import argparse
from pathlib import Path
import re

LAUNCHER_START = '=== Start KCD2 MP Launcher ==='
ROLE = re.compile(r'MP-LAUNCH session role selected \((host|join)\) '
                  r'source=(launcher|menu|launcher-menu) gamePid=([1-9]\d*) menuSeq=(\d+)\s*$')
PROBE = re.compile(r'Connection test via=direct kind=None reachable=True\b.*\bhostConnected=True\b')
GATE = re.compile(r'MP-LAUNCH connect gate closed \((host|joiner):')
DRIVER = re.compile(r'MP-W159 menu driver on \((host|joiner)\)\s*$')
AWAIT_DESTINATION = re.compile(r'MP-LAUNCH session role awaits a join destination menuSeq=([1-9]\d*)\s*$')
MODEL = re.compile(r'WO159-MENU model role=(host|join)\b')
PAGE = re.compile(r'WO159-MENU page=(root|host|join|style|prologue)\b')
EVENT = re.compile(r'\[KCD2-MP-EVT\] v1 ([1-9]\d*) w159 (\S+(?: \S+)*)\s*$')
RECEIPT = re.compile(r'WO159-MENU role receipt seq=([1-9]\d*) target=(host|join) accepted=(true|false)\b')
HOST_LOAD = re.compile(r'MP-W159 (load|new adventure): wh_sys_LoadGame ([0-4]) ([A-Za-z0-9_]+) -> sent\s*$')
HOST_NEW = re.compile(r'MP-W159 new adventure: (soldier|adviser|scout), .* prologue (watched|skipped)\s*$')
HOST_CONNECT = 'MP-W159 the world Start Game loaded has settled: CONNECT for the host'
FIRST_JOIN = re.compile(r'MP-W159 first-join answer sent to the agent: (bring|fresh:(?:soldier|adviser|scout))\s*$')
AGENT_ROLE = re.compile(r'MP-T3-STATS origin=agent role=(host|joiner)(?=\s|$)')
BUILD = re.compile(r'MP-BUILD version=([^\s]+) build=([^\s]+)')
OWNERS = re.compile(r'\b(owner_violations|rp_owner_n|owner_handovers|owner_denied_sets)=(\d+)\b')
ROUNDS = (('A-host', 'host'), ('B-join', 'join'), ('B-host', 'host'), ('A-join', 'join'))


def trace(launcher, game, agent, role):
    starts = [index for index, line in enumerate(launcher) if line.rstrip().endswith(LAUNCHER_START)]
    first = starts[-1] + 1 if starts else 0
    roles = [(index, match.groups()) for index, line in enumerate(launcher) if index >= first
             and (match := ROLE.search(line))]
    if not roles:
        return False, False, 'Keine bestätigte Launcher-Rolle'
    selected_at, (selected, source, pid, seq) = roles[-1]
    if selected != role:
        return False, False, f'Letzte bestätigte Launcher-Rolle war {selected}'

    # Limit the Direct test to this launch preparation or a later in-place role choice.
    launch_at = next(index for index, (_, _, game_pid, _) in roles if game_pid == pid)
    preceding = max((index for index, _ in roles if index < launch_at), default=first - 1)
    tested_direct = any(PROBE.search(line) for line in launcher[preceding + 1:selected_at])
    role_name = 'host' if role == 'host' else 'joiner'
    gates = [match[1] for line in launcher[preceding + 1:selected_at] if (match := GATE.search(line))]
    agent_roles = {match[1] for line in agent if (match := AGENT_ROLE.search(line))}
    if not gates or gates[-1] != role_name or agent_roles != {role_name}:
        return False, tested_direct, 'Passendes Connect-Gate oder Agent-Rolle fehlt'

    menu_request = int(seq)
    if (source == 'menu' and not menu_request) or (source == 'launcher' and menu_request):
        return False, tested_direct, 'Launcher-Rolle und Menükennung passen nicht'
    if source == 'launcher-menu' and not menu_request and role == 'join':
        waiting = [match[1] for line in launcher[launch_at:selected_at] if (match := AWAIT_DESTINATION.search(line))]
        if waiting:
            menu_request = int(waiting[-1])

    latest_model = None
    page_open = None
    received_request = False
    switched = menu_request == 0
    actions = []
    for line in game:
        if model := MODEL.search(line):
            latest_model = model[1]
        elif page := PAGE.search(line):
            page_open = page[1]
        elif receipt := RECEIPT.search(line):
            if received_request and int(receipt[1]) == menu_request and receipt[2] == role:
                switched = receipt[3] == ('false' if source == 'launcher-menu' and seq == '0' else 'true')
        elif event := EVENT.search(line):
            event_id = int(event[1])
            if event_id == menu_request:
                received_request = event[2] == f'role {role}'
                switched = False
                page_open = None
                actions.clear()
            elif switched and latest_model == role and event_id > menu_request:
                action = event[2]
                if (page_open == 'host' and role == 'host' and action.startswith('load ')
                        or page_open == 'prologue' and role == 'host' and action.startswith('newadv ')
                        or page_open == 'join' and role == 'join' and action == 'join bring'
                        or page_open == 'style' and role == 'join' and action.startswith('join fresh ')):
                    actions.append(action)
                elif action == 'cancel':
                    actions.clear()

    new_adv = None
    matched_load = False
    driver_role = None
    for line in launcher[selected_at + 1:]:
        if driver := DRIVER.search(line):
            driver_role = driver[1]
            matched_load = False
        elif driver_role != role_name:
            continue
        elif role == 'host' and (adv := HOST_NEW.search(line)):
            view = 'watch' if adv[2] == 'watched' else 'skip'
            new_adv = f'newadv {adv[1]} {view}'
        elif role == 'host' and (load := HOST_LOAD.search(line)):
            action = f'load {load[2]} {load[3]}' if load[1] == 'load' else new_adv
            if action in actions:
                matched_load = True
        elif role == 'host' and matched_load and HOST_CONNECT in line:
            return True, tested_direct, f'{role} ({source}), Welt geladen und Host gestartet'
        elif role == 'join' and (answer := FIRST_JOIN.search(line)):
            if f'join {answer[1].replace(":", " ")}' in actions:
                return True, tested_direct, f'{role} ({source}), Join-Wahl beim Agent bestätigt'
    return False, tested_direct, 'Kein passender Menütreiber mit bestätigter Weitergabe der Aktion'


def owner_status(lines, role):
    covered = False
    for line in lines:
        if (match := AGENT_ROLE.search(line)) and match[1] == role:
            fields = {key: int(value) for key, value in OWNERS.findall(line)}
            if fields.get('owner_violations', 0) > 0:
                return 'ROT'
            if (fields.get('owner_violations') == 0 and fields.get('rp_owner_n', 0) > 0
                    and {'owner_handovers', 'owner_denied_sets'} <= fields.keys()):
                covered = True
    return 'GRÜN' if covered else 'GRAU'


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('captures', type=Path, help='Ordner mit A-host, B-join, B-host und A-join')
    args = parser.parse_args(argv)
    all_roles = True
    direct_to_host = False
    builds = set()
    for folder, role in ROUNDS:
        root = args.captures / folder
        owner = 'GRAU'
        try:
            launcher = (root / 'launcher.log').read_text(encoding='utf-8-sig', errors='replace').splitlines()
            game = (root / 'kcd.log').read_text(encoding='utf-8-sig', errors='replace').splitlines()
            agent = (root / 'agent.log').read_text(encoding='utf-8-sig', errors='replace').splitlines()
            ok, direct, message = trace(launcher, game, agent, role)
            current_builds = {match.groups() for line in agent if (match := BUILD.search(line))}
            if len(current_builds) == 1:
                builds.update(current_builds)
            else:
                ok, message = False, 'Build-ID im Agent-Log fehlt oder ist mehrdeutig'
            owner = owner_status(agent, 'host' if role == 'host' else 'joiner')
        except OSError:
            ok, direct, message = False, False, 'launcher.log, kcd.log oder agent.log fehlt'
        print(f'{folder} {"GRÜN" if ok else "GRAU"} · {message} · Single-Owner {owner}')
        all_roles &= ok and owner != 'ROT'
        direct_to_host |= ok and direct and role == 'host'
    print(f'Direct-Test vor Host-Wahl {"GRÜN" if direct_to_host else "GRAU"}')
    print(f'Übereinstimmende Build-ID {"GRÜN" if len(builds) == 1 and all_roles else "GRAU"}')
    print('Gemeinsame Welt, Kalender und native Eigentümerbelege im Paarbericht getrennt prüfen.')
    return 0 if all_roles and direct_to_host and len(builds) == 1 else 1


if __name__ == '__main__':
    raise SystemExit(main())
