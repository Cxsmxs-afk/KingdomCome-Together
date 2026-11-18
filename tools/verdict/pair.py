#!/usr/bin/env python3
# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
"""Vergleicht die beiden T1-Berichte mit Python 3.10 oder neuer.

Für .tzst-Archive ist tar mit zstd-Unterstützung erforderlich.
Exit 0 = grün, 1 = gelb/rot/grau, 2 = Eingabe- oder Schreibfehler.
"""

import argparse
from dataclasses import dataclass, field
import html
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile


REPORT = "ERGEBNIS.txt"
PAIR_REPORT = "ERGEBNIS-PAAR.md"
HEADER = "KCDMP-ERGEBNIS\t1"
COLUMNS = "Regel\tStatus\tMesswert\tBeleg"
GREEN, GRAY, YELLOW, RED = "GRÜN", "GRAU", "GELB", "ROT"
STATUS_RANK = {status: rank for rank, status in enumerate((GREEN, GRAY, YELLOW, RED))}
CORE_RULE = "queue"
KNOWN_ROLES = ("host", "joiner")
MAX_REPORT_BYTES = 256 * 1024
RULE_ID = re.compile(r"[a-z][a-z0-9_]{0,95}")
ARCHIVE_EXTS = (".tzst", ".tar.zst", ".tar")
SPLIT_PART = re.compile(r"(.+)-part([1-9]\d*)of([1-9]\d*)(\.tzst|\.tar\.zst|\.tar)", re.IGNORECASE)
CONTROL_CHARS = re.compile(r"[\x00-\x08\x0b-\x1f\x7f-\x9f]")
MARKDOWN_CHARS = re.compile(r"[\\`*_[\]{}()!#|~]")


class BadReport(Exception):
    pass


@dataclass(frozen=True)
class Rule:
    status: str
    measurement: str
    evidence: str


@dataclass(frozen=True)
class Side:
    status: str
    rules: dict[str, Rule] = field(default_factory=dict)
    error: str | None = None
    role: str | None = None


def _worst(*statuses: str) -> str:
    return max(statuses, key=STATUS_RANK.__getitem__)


def _limited_read(stream) -> bytes:
    data = stream.read(MAX_REPORT_BYTES + 1)
    if len(data) > MAX_REPORT_BYTES:
        raise BadReport(f"{REPORT} ist zu groß.")
    return data


def _archive_report(path: Path) -> bytes:
    if match := SPLIT_PART.fullmatch(path.name):
        base, index, parts, suffix = match.groups()
        if index != "1":
            path = path.with_name(f"{base}-part1of{parts}{suffix}")
            if not path.is_file():
                raise BadReport("Der erste Bundle-Teil fehlt.")
    try:
        # -O sends the exact member to stdout. No archive paths are written to disk.
        with subprocess.Popen(
            ["tar", "-xOf", str(path.resolve()), "--", REPORT],
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
        ) as proc:
            try:
                data = _limited_read(proc.stdout)
            except BadReport:
                proc.kill()
                raise
            if proc.wait() != 0 or not data:
                raise BadReport(f"{REPORT} fehlt im Archiv oder ist nicht lesbar. Entpacke Bundle-Teil 1 und verwende den Ordner.")
            return data
    except FileNotFoundError as ex:
        raise BadReport("Das Programm tar fehlt. Entpacke Bundle-Teil 1 und verwende den Ordner.") from ex


def _report_bytes(path: Path) -> bytes:
    if path.is_dir():
        path = path / REPORT
    if not path.is_file():
        raise BadReport(f"{REPORT} fehlt oder ist keine reguläre Datei.")
    if path.name.lower().endswith(ARCHIVE_EXTS):
        return _archive_report(path)
    with path.open("rb") as stream:
        return _limited_read(stream)


def _parse(data: bytes) -> Side:
    try:
        lines = data.decode("utf-8-sig").splitlines()
    except UnicodeError as ex:
        raise BadReport(f"{REPORT} ist kein UTF-8-Text.") from ex
    if len(lines) < 4 or lines[0] != HEADER:
        raise BadReport(f"{REPORT} hat ein unbekanntes oder unvollständiges Format.")
    total = lines[1].split("\t")
    if len(total) != 2 or total[0] != "Gesamt" or total[1] not in STATUS_RANK:
        raise BadReport(f"{REPORT} enthält kein gültiges Gesamturteil.")

    columns = 2
    roles = []
    while columns < len(lines) and lines[columns].split("\t", 1)[0] == "Rolle":
        roles.append(lines[columns].partition("\t")[2])
        columns += 1
    role = roles[0] if len(roles) == 1 and roles[0] in KNOWN_ROLES else None
    if columns >= len(lines) - 1 or lines[columns] != COLUMNS:
        raise BadReport(f"{REPORT} hat ein unbekanntes oder unvollständiges Format.")

    rules = {}
    for line_no, line in enumerate(lines[columns + 1:], columns + 2):
        fields = line.split("\t")
        if len(fields) != 4 or CONTROL_CHARS.search(line):
            raise BadReport(f"{REPORT}, Zeile {line_no}: ungültige Messzeile.")
        name, status, measurement, evidence = fields
        if not RULE_ID.fullmatch(name) or name in rules or status not in STATUS_RANK:
            raise BadReport(f"{REPORT}, Zeile {line_no}: ungültige oder doppelte Regel.")
        if status == GREEN and measurement.strip() in ("", "–"):
            status = GRAY
        rules[name] = Rule(status, measurement, evidence)

    # Optionale graue Regeln bleiben Einzelergebnisse. Jede konkrete Warnung zählt.
    core = rules.get(CORE_RULE)
    status = _worst(total[1], core.status if core else GRAY,
                    *(rule.status for rule in rules.values() if rule.status in (YELLOW, RED)))
    return Side(status, rules, role=role)


def read_side(path: Path) -> Side:
    try:
        return _parse(_report_bytes(path))
    except BadReport as ex:
        return Side(GRAY, error=str(ex))
    except OSError:
        return Side(GRAY, error=f"{REPORT} konnte nicht gelesen werden.")


def _role_coverage(host: Side, joiner: Side) -> str:
    return GREEN if (host.role, joiner.role) == KNOWN_ROLES else GRAY


def combine(host: Side, joiner: Side) -> str:
    coverage = GREEN if host.rules.keys() == joiner.rules.keys() else GRAY
    return _worst(host.status, joiner.status, coverage, _role_coverage(host, joiner))


def _cell(value: str) -> str:
    return MARKDOWN_CHARS.sub(lambda match: "\\" + match[0], html.escape(value, quote=False))


def _rule_cell(rule: Rule | None) -> str:
    if rule is None:
        return "**GRAU** · Keine Messzeile"
    return " · ".join([f"**{rule.status}**", *(_cell(text) for text in (rule.measurement, rule.evidence) if text)])


def _render(host: Side, joiner: Side) -> str:
    lines = [
        "# Ergebnis für Host und Joiner",
        "",
        f"**Gesamt {combine(host, joiner)}**",
        "",
        f"Host **{host.status}** · Joiner **{joiner.status}**",
    ]
    for role, side in zip(KNOWN_ROLES, (host, joiner)):
        label = role.capitalize()
        if side.error:
            lines.extend(["", f"- **{label}** {_cell(side.error)}"])
        elif side.role is None:
            lines.extend(["", f"- **{label}** Die Rolle ist unbekannt oder mehrdeutig."])
        elif side.role != role:
            lines.extend(["", f"- **{label}** Dieser Bericht gehört zur Rolle {side.role.capitalize()}."])
    if host.rules.keys() != joiner.rules.keys():
        lines.extend(["", "Die Berichte enthalten unterschiedliche Regeln. Fehlende Messzeilen sind grau."])
    if host.rules or joiner.rules:
        lines.extend(["", "| Regel | Host | Joiner | Paar |", "| --- | --- | --- | --- |"])
        role_coverage = _role_coverage(host, joiner)
        for name in sorted(host.rules.keys() | joiner.rules.keys() | {CORE_RULE}):
            left, right = host.rules.get(name), joiner.rules.get(name)
            status = _worst(left.status if left else GRAY, right.status if right else GRAY, role_coverage)
            lines.append(f"| `{name}` | {_rule_cell(left)} | {_rule_cell(right)} | **{status}** |")
    return "\n".join(lines) + "\n"


def _write_report(path: Path, report: str) -> None:
    stream = tempfile.NamedTemporaryFile(
        mode="w", encoding="utf-8", newline="\n", dir=path.parent, prefix=f".{path.name}-", delete=False
    )
    staged = Path(stream.name)
    try:
        with stream:
            stream.write(report)
        os.replace(staged, path)
    finally:
        staged.unlink(missing_ok=True)


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("host", type=Path, help="ERGEBNIS.txt, Bundle-Ordner oder Bundle-Archiv des Hosts")
    parser.add_argument("joiner", type=Path, help="ERGEBNIS.txt, Bundle-Ordner oder Bundle-Archiv des Joiners")
    args = parser.parse_args(argv)
    host, joiner = read_side(args.host), read_side(args.joiner)
    status = combine(host, joiner)
    try:
        _write_report(Path(PAIR_REPORT), _render(host, joiner))
    except OSError as ex:
        print(f"{PAIR_REPORT} konnte nicht geschrieben werden ({ex}).", file=sys.stderr)
        return 2
    print(f"{PAIR_REPORT} · {status}")
    for role, side in (("Host", host), ("Joiner", joiner)):
        if side.error:
            print(f"{role} - {side.error}", file=sys.stderr)
    if host.error or joiner.error:
        return 2
    return 0 if status == GREEN else 1


if __name__ == "__main__":
    sys.exit(main())
