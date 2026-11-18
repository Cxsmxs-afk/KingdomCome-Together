# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
"""Check B1 bow hand requests using two agent logs from the same exercise."""

import argparse
from pathlib import Path
import re
from uuid import UUID


BOW = re.compile(r'\bMP-LANE1-BOW (out|in) (.+)$')
STATS = re.compile(r'\bMP-T3-STATS origin=agent role=(?:host|joiner) (.+)$')
FIELD = re.compile(r'(\w+)=([^\s]+)')
EMPTY_COUNTER = 'bow_hand_empty_while_drawn'
EMPTY_CLASS = str(UUID(int=0))


def inspect_sender(lines, bow_class):
    outgoing = 0
    checked_zero = False
    failures = []
    for line_number, line in enumerate(lines, 1):
        bow = BOW.search(line)
        if bow and bow[1] == 'out':
            fields = dict(FIELD.findall(bow[2]))
            cls = fields.get('cls', '').lower()
            if cls in ('-', EMPTY_CLASS):
                failures.append(f'sender line {line_number} reports an empty drawn bow class')
            if cls == bow_class and fields.get('source') == 'native':
                outgoing += 1

        stats = STATS.search(line)
        if stats:
            count = dict(FIELD.findall(stats[1])).get(EMPTY_COUNTER)
            if count is None:
                continue
            if count != '0':
                failures.append(f'sender line {line_number} reports {EMPTY_COUNTER}={count}')
            elif outgoing:
                checked_zero = True
    return outgoing, checked_zero, failures


def receiver_requests(lines, bow_class, source_peer):
    count = 0
    for line in lines:
        bow = BOW.search(line)
        if not bow or bow[1] != 'in':
            continue
        fields = dict(FIELD.findall(bow[2]))
        peer = fields.get('peer', '')
        if (fields.get('cls', '').lower() == bow_class and fields.get('result') == 'hand-requested'
                and re.fullmatch(r'[0-9]+', peer) and (source_peer is None or int(peer) == source_peer)):
            count += 1
    return count


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
            outgoing, checked_zero, failures = inspect_sender(sender, bow_class)
        with args.receiver_log.open(encoding='utf-8-sig', errors='replace') as receiver:
            requests = receiver_requests(receiver, bow_class, args.source_peer)
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
    if not checked_zero:
        missing.append(f'zero {EMPTY_COUNTER} snapshot after an outgoing bow row')
    if missing:
        print('INCONCLUSIVE missing ' + ', '.join(missing))
        return 2
    print(f'PASS cls={bow_class} outgoing={outgoing} hand_requests={requests} {EMPTY_COUNTER}=0')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
