# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
"""Check bow retention, repeated sheathing and the receiver's clear in one exercise's agent logs."""

import argparse
from pathlib import Path
import re
from uuid import UUID


BOW = re.compile(r'\bMP-LANE1-BOW (out|in|reset) (.+)$')
HAND = re.compile(r'\bWO214-HAND own (.+?) -> sent\b(?: to (\d+)\b)?')
STATS = re.compile(r'\bMP-T3-STATS origin=agent role=(?:host|joiner) (.+)$')
FIELD = re.compile(r'(\w+)=([^\s]+)')
EMPTY_COUNTER = 'bow_hand_empty_while_drawn'
EMPTY_CLASS = str(UUID(int=0))
CAPTURE_MAX_AGE_MS = 2000


def number(fields, key):
    value = fields.get(key, '')
    return int(value) if re.fullmatch(r'[0-9]{1,19}', value) else None


def inspect_sender(lines, bow_class):
    outgoing = 0
    capture = None  # latest capture tick, class and drawn phase observed in this hold
    invalidated_at = -1
    first_retention = None  # line and age of the first qualifying heartbeat in the exercise
    retained_hold = None  # line and age of a qualifying heartbeat in the current hold
    sheathe_revision = None
    repeated_sheathe = None
    last_zero_line = 0
    failures = []
    for line_number, line in enumerate(lines, 1):
        hand = HAND.search(line)
        if hand:
            fields = dict(FIELD.findall(hand[1]))
            cls = fields.get('cls', '').lower()
            drawn = fields.get('drawn')
            other_class = cls not in ('', '-', EMPTY_CLASS, bow_class)
            if drawn == '0' or other_class:
                if capture is not None:
                    invalidated_at = capture[0]
                capture = None
            elif (drawn == '1' and cls in ('-', EMPTY_CLASS) and hand[2] != '0'
                  and capture is not None and capture[1] == bow_class):
                failures.append(f'sender line {line_number} sends an empty drawn bow class')
            if other_class or (drawn == '1' and capture is None):
                retained_hold = sheathe_revision = None

        bow = BOW.search(line)
        if bow and bow[1] == 'reset':
            fields = dict(FIELD.findall(bow[2]))
            reset_at = number(fields, 'at_ms')
            if reset_at is not None:
                invalidated_at = max(invalidated_at, reset_at)
            elif capture is not None:
                invalidated_at = capture[0]
            capture = None
            if fields.get('reason') != 'sheathed':
                retained_hold = sheathe_revision = None
        elif bow and bow[1] == 'out':
            fields = dict(FIELD.findall(bow[2]))
            cls = fields.get('cls', '').lower()
            source = fields.get('source')
            capture_ms = number(fields, 'capture_ms')
            if source == 'wo214' and fields.get('drawn') == '0' and cls == '-':
                if capture is not None:
                    invalidated_at = capture[0]
                capture = None
                revision = number(fields, 'revision')
                if retained_hold is not None and revision is not None:
                    if fields.get('repeat') == '0':
                        sheathe_revision = revision
                    elif fields.get('repeat') == '1' and revision == sheathe_revision and repeated_sheathe is None:
                        repeated_sheathe = (revision, retained_hold)
            elif cls in ('-', EMPTY_CLASS):
                failures.append(f'sender line {line_number} reports an empty drawn bow class')
            elif source == 'native':
                drawn_phase = cls == bow_class and ('phase' not in fields or number(fields, 'phase') in (1, 2, 3))
                if drawn_phase:
                    outgoing += 1
                if (capture_ms is not None and capture_ms > invalidated_at
                        and (capture is None or capture_ms >= capture[0])):
                    if capture is None or capture[1] != cls:
                        retained_hold = sheathe_revision = None
                    same_drawn_hold = capture is not None and capture[1] == cls and capture[2]
                    capture = (capture_ms, cls, drawn_phase or same_drawn_hold)
            elif source == 'wo214' and cls == bow_class and capture == (capture_ms, cls, True):
                age_ms = number(fields, 'age_ms')
                if age_ms is not None and age_ms > CAPTURE_MAX_AGE_MS and retained_hold is None:
                    retained_hold = (line_number, age_ms)
                    if first_retention is None:
                        first_retention = retained_hold

        stats = STATS.search(line)
        if stats:
            count = dict(FIELD.findall(stats[1])).get(EMPTY_COUNTER)
            if count is None:
                continue
            if count != '0':
                failures.append(f'sender line {line_number} reports {EMPTY_COUNTER}={count}')
            else:
                last_zero_line = line_number

    retention = repeated_sheathe[1] if repeated_sheathe else first_retention
    checked_age = retention[1] if retention and last_zero_line > retention[0] else None
    return outgoing, retention, checked_age, repeated_sheathe, failures


def inspect_receiver(lines, bow_class, source_peer):
    requests = 0
    cleared = set()
    requested = set()
    for line in lines:
        bow = BOW.search(line)
        if not bow or bow[1] != 'in':
            continue
        fields = dict(FIELD.findall(bow[2]))
        peer = number(fields, 'peer')
        if peer is None or peer > 255 or (source_peer is not None and peer != source_peer):
            continue
        cls = fields.get('cls', '').lower()
        result = fields.get('result')
        if result == 'hand-requested':
            cleared.discard(peer)
            if cls == bow_class:
                requests += 1
                requested.add(peer)
            else:
                requested.discard(peer)
        elif result == 'hand-sheathed' and cls == '-' and peer in requested:
            cleared.add(peer)
            requested.remove(peer)
    return requests, cleared


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--sender-log', type=Path, required=True)
    parser.add_argument('--receiver-log', type=Path, required=True)
    parser.add_argument('--bow-class', type=UUID, required=True)
    parser.add_argument('--source-peer', type=int, help='sender peer id on the receiver')
    args = parser.parse_args(argv)
    if args.bow_class.int == 0:
        parser.error('--bow-class must be a nonzero UUID')
    if args.source_peer is not None and not 0 <= args.source_peer <= 255:
        parser.error('--source-peer must be between 0 and 255')

    bow_class = str(args.bow_class)
    try:
        if args.sender_log.samefile(args.receiver_log):
            parser.error('sender and receiver must be separate agent logs')
        with args.sender_log.open(encoding='utf-8-sig', errors='replace') as sender:
            outgoing, retention, checked_age, repeated_sheathe, failures = inspect_sender(sender, bow_class)
        with args.receiver_log.open(encoding='utf-8-sig', errors='replace') as receiver:
            requests, cleared = inspect_receiver(receiver, bow_class, args.source_peer)
    except OSError as error:
        print(f'INCONCLUSIVE {error}')
        return 2

    if failures:
        print('FAIL ' + ', '.join(failures))
        return 1
    missing = []
    if not outgoing:
        missing.append('sender bow class')
    if not requests:
        missing.append('receiver hand request for that class')
    if retention is None:
        missing.append(f'WO-214 heartbeat carrying the latest measured class after {CAPTURE_MAX_AGE_MS} ms in the same drawn hold')
    if checked_age is None:
        missing.append(f'zero {EMPTY_COUNTER} snapshot after a retained heartbeat')
    if repeated_sheathe is None:
        missing.append('initial and repeated WO-214 sheathe writes for the same revision after retention')
    if not cleared:
        missing.append('receiver hand clear after requesting that class from the same peer')
    if missing:
        print('INCONCLUSIVE missing ' + ', '.join(missing))
        return 2
    print(f'PASS cls={bow_class} outgoing={outgoing} hand_requests={requests} '
          f'retained_age_ms={checked_age} sheathe_revision={repeated_sheathe[0]} '
          f'sheathed_peers={",".join(map(str, sorted(cleared)))} {EMPTY_COUNTER}=0')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
