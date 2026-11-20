# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
"""Check scoped H6 evidence from two joined runs with the two players' roles swapped."""

import argparse
from collections import defaultdict
import json
from pathlib import Path
import re

from scen_s2_measure import STATS
from scen_s2_timeline import FIELD, number
from scen_s3_load import fresh_join


REQUIRED_DEATHS = 30
OBSERVATION_MS = 60_000
MAX_P95_US = 2_000
AGENT_BUILD = re.compile(r'\bMP-BUILD version=\S+ build=([a-z0-9-]+)\b', re.IGNORECASE)
NATIVE_BUILD = re.compile(r'\bKCDMP\.dll attached, pid=\d+ build=([a-z0-9-]+)\b', re.IGNORECASE)
NATIVE_LINE = re.compile(r'^\[(\d\d):(\d\d):(\d\d)\.(\d{3})\] t\d+ L([0-9a-f]{6}) (.+)$', re.IGNORECASE)
FRIENDLY_FIRE = re.compile(r'\b(?:MP-WO121 cfg .*friendly_fire|MP-FF (?:host sent|session) friendly_fire)=(on|off)\b')
EID = re.compile(r'0x[0-9a-fA-F]+\Z')
CRASH = re.compile(r'\bWO202-CRASH 0x[0-9a-fA-F]+\b')
PLAYBACK_STAGES = ('ready', 'death-dispatched', 'applied')
ABORTED = ('aborted-before-apply', 'aborted-unadmitted')
TERMINAL_STAGES = ('applied', 'uncertain', 'preparation-uncertain', 'prepare-observed-dead', 'stale', *ABORTED)
AWAITING_READBACK = ('death-pending', 'apply-uncertain')
FAILED_PREPARATION = ('preparation-not-started', 'preparation-uncertain', 'prepare-unchanged',
                      'prepare-uncertain', 'prepare-observed-dead', 'prepare-exception')
PHASES = {*PLAYBACK_STAGES, *TERMINAL_STAGES, *AWAITING_READBACK, *FAILED_PREPARATION,
          'release-pending', 'release-uncertain', 'apply-exception'}
FRAME_P95 = re.compile(r'\bFRAME .*\bframes=([1-9]\d*) .*\bours_us_p95=(\d+)\b')


def native_launch(lines):
    rows = []
    builds = []
    day = previous = 0
    for line in lines:
        match = NATIVE_LINE.match(line)
        if not match:
            continue
        hh, mm, ss, milliseconds = map(int, match.groups()[:4])
        tick = ((hh * 60 + mm) * 60 + ss) * 1000 + milliseconds
        if previous >= 23 * 3_600_000 and tick < 3_600_000:
            day += 86_400_000
        previous = tick
        launch, body = match[5], match[6]
        if build := NATIVE_BUILD.search(body):
            builds.append((launch, build[1]))
        rows.append((launch, day + tick, body))
    launches = {launch for launch, _, _ in rows}
    if len(builds) != 1 or launches != {builds[0][0]}:
        return None, []
    return builds[0][1], [(tick, body) for _, tick, body in rows]


def role_evidence(lines, role, build):
    builds = [match[1] for line in lines if (match := AGENT_BUILD.search(line))]
    ff = [match[1] for line in lines if (match := FRIENDLY_FIRE.search(line))]
    samples = [(match[1], dict(FIELD.findall(match[2]))) for line in lines if (match := STATS.search(line))]
    roles = {reported_role for reported_role, _ in samples}
    owner = next((row for reported_role, row in reversed(samples) if reported_role == role
                  and all(number(row, key) is not None for key in ('rp_owner_n', 'owner_violations'))), {})
    return dict(build_verified=builds == [build], role_verified=roles == {role}, friendly_fire=ff[-1] if ff else None,
                rp_owner_n=number(owner, 'rp_owner_n'), owner_violations=number(owner, 'owner_violations'))


def native_evidence(rows):
    history = defaultdict(list)
    tracker = defaultdict(list)
    red = []
    grey = []
    for index, (tick, line) in enumerate(rows):
        if CRASH.search(line):
            red.append('native-crash')
        if line.startswith('MP-LANE2-H6 '):
            fields = dict(FIELD.findall(line))
            attempt = number(fields, 'attempt')
            if not attempt or not EID.fullmatch(fields.get('eid', '')) or fields.get('stage') not in PHASES:
                grey.append('unreadable-h6-marker')
                continue
            history[attempt].append((index, tick, fields))
        if line.startswith('WO221-ACTEND ') and ' attempt=' in line:
            fields = dict(FIELD.findall(line))
            tracker[number(fields, 'attempt')].append((index, fields))

    proved = []
    body_ids = set()
    for attempt, entries in history.items():
        phases = [fields['stage'] for _, _, fields in entries]
        identity = {(fields.get('eid'), fields.get('lifecycle'), fields.get('connection'), fields.get('path'))
                    for _, _, fields in entries}
        if len(identity) != 1:
            red.append(f'attempt-{attempt}-scope-or-body-changed')
            continue
        eid, lifecycle, connection, path = identity.pop()
        if not lifecycle or not lifecycle.isdecimal() or not connection or not connection.isdecimal() or path not in ('named', 'guid'):
            grey.append(f'attempt-{attempt}-scope-unmeasured')
            continue
        terminal = [phase for phase in phases if phase in TERMINAL_STAGES]
        if any(phase in FAILED_PREPARATION for phase in phases) and (
                'death-dispatched' in phases or 'applied' in terminal):
            red.append(f'attempt-{attempt}-mutation-after-failed-preparation')
            continue
        aborted = next((phase for phase in terminal if phase in ABORTED), None)
        if aborted:
            red.append(f'attempt-{attempt}-{aborted}')
            continue
        if len(terminal) > 1 or phases.count('death-dispatched') > 1 or phases.count('ready') > 1:
            red.append(f'attempt-{attempt}-duplicate-terminal-or-dispatch')
            continue
        if terminal != ['applied']:
            grey.append(f'attempt-{attempt}-death-unconfirmed')
            continue
        if 'death-dispatched' not in phases:
            grey.append(f'attempt-{attempt}-already-dead-or-dispatch-unmeasured')
            continue
        ordered = list(phases)
        if ordered[0] == 'release-pending':
            ordered.pop(0)
        if ordered[-2] in AWAITING_READBACK:
            ordered.pop(-2)
        if ordered[-2] == 'apply-exception':
            ordered.pop(-2)
        if ordered != list(PLAYBACK_STAGES):
            red.append(f'attempt-{attempt}-mutation-without-ordered-release')
            continue
        tracked = tracker[attempt]
        ready_index = entries[phases.index('ready')][0]
        if not tracked or any(fields.get('eid') != eid for _, fields in tracked):
            grey.append(f'attempt-{attempt}-tracker-body-unmeasured')
            continue
        phase_receipts = [fields.get('receipt') for _, fields in tracked]
        ready_track = [index for index, fields in tracked if fields.get('receipt') == '1']
        if len(ready_track) != 1 or ready_track[0] >= ready_index or any(status not in ('1', '2') for status in phase_receipts):
            red.append(f'attempt-{attempt}-native-release-not-confirmed')
            continue
        waiting, active = (number(tracked[0][1], key) for key in ('waiting', 'active'))
        if not waiting or not active:
            grey.append(f'attempt-{attempt}-waiting-and-active-unmeasured')
            continue
        body = (connection, lifecycle, eid)
        if body in body_ids:
            grey.append(f'attempt-{attempt}-new-body-binding-unmeasured')
            continue
        body_ids.add(body)
        proved.append(dict(attempt=attempt, eid=eid, lifecycle=lifecycle, connection=connection, path=path,
                           waiting=waiting, active=active, at_ms=entries[-1][1]))

    if len(proved) < REQUIRED_DEATHS:
        grey.append('fewer-than-30-qualified-deaths')
    observed = peak = None
    if proved:
        scopes = {(row['connection'], row['lifecycle']) for row in proved}
        if len(scopes) != 1:
            red.append('h6-run-scope-changed')
        first, last = min(row['at_ms'] for row in proved), max(row['at_ms'] for row in proved)
        start = min(entries[0][1] for entries in history.values())
        if any(start <= tick <= last + OBSERVATION_MS and line == 'PIPE: agent disconnected' for tick, line in rows):
            red.append('native-pipe-disconnected-during-h6')
        frames = [(tick, int(match[2])) for tick, line in rows if tick >= first and (match := FRAME_P95.search(line))]
        if frames:
            observed = max(tick for tick, _ in frames) - last
            peak = max(p95 for _, p95 in frames)
        if observed is None or observed < OBSERVATION_MS:
            grey.append('sixty-second-tail-unmeasured')
        if peak is not None and peak >= MAX_P95_US:
            red.append('frame-p95-out-of-budget')
    armed = any('WO221-ACTEND armed:' in line for _, line in rows)
    if not armed:
        grey.append('release-hook-unmeasured')
    return dict(status='red' if red else 'grey' if grey else 'green', qualified_deaths=proved, tail_ms=observed,
                ours_us_p95_max=peak, release_hook_armed=armed, red=list(dict.fromkeys(red)), grey=list(dict.fromkeys(grey)))


def check_pair(host, joiner, build):
    host_build, host_native = native_launch(host['native'])
    joiner_build, joiner_native = native_launch(joiner['native'])
    role_checks = {role: role_evidence(logs['agent'], role, build) for role, logs in (('host', host), ('joiner', joiner))}
    join_id = fresh_join(joiner['agent'], host['game'])
    evidence = native_evidence(joiner_native)
    red = list(evidence['red'])
    grey = list(evidence['grey'])
    if host_build != build or joiner_build != build or not all(row['build_verified'] for row in role_checks.values()):
        grey.append('same-agent-and-dll-build-unverified')
    if not join_id or not all(row['role_verified'] for row in role_checks.values()):
        grey.append('matching-join-or-roles-unverified')
    if not all(row['friendly_fire'] == 'on' for row in role_checks.values()):
        grey.append('friendly-fire-on-unverified')
    if any(row['owner_violations'] for row in role_checks.values()):
        red.append('owner-violations')
    if not all(row['rp_owner_n'] and row['owner_violations'] is not None for row in role_checks.values()):
        grey.append('owner-counters-unmeasured')
    if any(CRASH.search(line) for _, line in host_native):
        red.append('host-native-crash')
    host_p95 = next((int(match[2]) for _, line in reversed(host_native) if (match := FRAME_P95.search(line))), None)
    if host_p95 is None:
        grey.append('host-frame-p95-unmeasured')
    elif host_p95 >= MAX_P95_US:
        red.append('host-frame-p95-out-of-budget')
    # The older post-fall log names an NPC but has no per-attempt ID. Keep it as supplementary evidence.
    falls = [line.split('WO195-FALL ', 1)[1] for line in joiner['agent'] if 'WO195-FALL ' in line]
    return dict(evidence, status='red' if red else 'grey' if grey else 'green', red=red, grey=grey,
                roles=role_checks, join_id=join_id, host_ours_us_p95=host_p95, uncorrelated_fall_diagnostics=falls)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--build', required=True, help='exact candidate Build-ID from MP-BUILD and the DLL')
    names = ('a-host', 'b-joiner', 'b-host', 'a-joiner')
    for name in names:
        parser.add_argument(f'--{name}', nargs=3, required=True, type=Path, metavar=('AGENT', 'NATIVE', 'GAME'))
    args = parser.parse_args(argv)
    copies = {}
    paths_seen = set()
    for name in names:
        paths = getattr(args, name.replace('-', '_'))
        for path in paths:
            if path.resolve() in paths_seen:
                parser.error('each role and launch needs its own log files')
            paths_seen.add(path.resolve())
        try:
            copies[name] = {kind: path.read_text(encoding='utf-8-sig', errors='replace').splitlines()
                            for kind, path in zip(('agent', 'native', 'game'), paths)}
        except OSError as error:
            parser.error(str(error))
    runs = {f'{host}/{joiner}': check_pair(copies[host], copies[joiner], args.build)
            for host, joiner in (('a-host', 'b-joiner'), ('b-host', 'a-joiner'))}
    values = list(runs.values())
    distinct_joins = bool(values[0]['join_id'] and values[1]['join_id'] and values[0]['join_id'] != values[1]['join_id'])
    green = distinct_joins and all(row['status'] == 'green' for row in values)
    status = 'red' if any(row['status'] == 'red' for row in values) else 'green' if green else 'grey'
    print(json.dumps(dict(status=status, build=args.build, distinct_joins=distinct_joins, runs=runs,
                          operator_evidence='distance, screens and physical PC labels must be witnessed'), indent=2))
    return 0 if green else 1


if __name__ == '__main__':
    raise SystemExit(main())
