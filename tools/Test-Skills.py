#!/usr/bin/env python3
# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
"""Check the repository skills from the working tree used by the simulator."""

from pathlib import Path
import re
import sys


REQUIRED_SKILLS = (
    "kct-kontext", "kct-fix-workflow", "kct-performance", "kct-log-analyse", "kct-coop-vergleich"
)
CODE_SPAN = re.compile(r"(?<!`)(`+)([^`\n]+)\1(?!`)")
CODE_FENCE = re.compile(r"^[ \t]*(`{3,})(.*)$")
REPO_PATH = re.compile(
    r"""(?<![\w./-])(?:\./)?((?:dotnet|native|tools|kdcmp|KCDMP_launcher|docs|\.github)/[^\s`'"<>,:;|()#]+)"""
)
PENDING_PATH = re.compile(r"<!-- skill-path-pending: (\S+) -->")


def field_value(value: str, continuation: list[str]) -> str:
    value = value.strip()
    if value.startswith(("'", '"')):
        quoted = re.fullmatch(r"""(?:"((?:[^"\\]|\\.)*)"|'((?:[^']|'')*)')(?:[ \t]+#.*)?""", value)
        return next((part.strip() for part in quoted.groups() if part is not None), "") if quoted else ""

    value = re.split(r"[ \t]+#", value, maxsplit=1)[0].rstrip()
    if re.fullmatch(r"[>|](?:[1-9][+-]?|[+-][1-9]?|)", value):
        block = []
        for line in continuation:
            if line and not line[0].isspace():
                break
            if line.strip():
                block.append(line.strip())
        return " ".join(block)
    return value


def check_frontmatter(skill: Path, lines: list[str]) -> list[str]:
    if not lines or lines[0] != "---" or "---" not in lines[1:]:
        return ["missing opening or closing YAML front matter delimiter"]

    header = lines[1:lines.index("---", 1)]
    errors = []
    for field in ("name", "description"):
        values = [field_value(m[1], header[index + 1:]) for index, line in enumerate(header)
                  if (m := re.fullmatch(rf"{field}:[ \t]*(.*)", line))]
        if len(values) != 1:
            errors.append(f"expected one {field} field, found {len(values)}")
            continue
        value = values[0]
        if not value or value.lower() in {"null", "~"} or value.startswith("#"):
            errors.append(f"{field} must have a value")
        elif field == "name" and value != skill.parent.name:
            errors.append(f"name {value!r} must match folder {skill.parent.name!r}")
    return errors


def check_paths(root: Path, lines: list[str]) -> list[str]:
    errors = []
    fence_size = 0
    for number, line in enumerate(lines, start=1):
        fence = CODE_FENCE.match(line)
        if fence and not fence_size:
            fence_size = len(fence[1])
            continue
        if fence and len(fence[1]) >= fence_size and not fence[2].strip():
            fence_size = 0
            continue
        pending = set(PENDING_PATH.findall(line))
        code_blocks = [line] if fence_size else [match[2] for match in CODE_SPAN.finditer(line)]
        for code in code_blocks:
            for path in REPO_PATH.findall(code):
                candidate = Path(path)
                if ".." in candidate.parts:
                    errors.append(f"line {number}: {path!r} escapes its repository path")
                elif path not in pending and not any(match.exists() for match in root.glob(path)):
                    errors.append(f"line {number}: repository path {path!r} does not exist")
    return errors


def main() -> int:
    root = Path(__file__).resolve().parent.parent
    skills_root = root / ".agents" / "skills"
    skills = sorted(skills_root.glob("*/SKILL.md"))
    failed = False
    for name in REQUIRED_SKILLS:
        expected = skills_root / name / "SKILL.md"
        if not expected.is_file():
            print(f"{expected.relative_to(root)}: required skill missing", file=sys.stderr)
            failed = True

    for skill in skills:
        try:
            lines = skill.read_text(encoding="utf-8-sig").splitlines()
        except (OSError, UnicodeError) as error:
            print(f"{skill.relative_to(root)}: {error}", file=sys.stderr)
            failed = True
            continue
        for error in check_frontmatter(skill, lines) + check_paths(root, lines):
            print(f"{skill.relative_to(root)}: {error}", file=sys.stderr)
            failed = True

    if failed:
        return 1
    print(f"Skills check PASS: {len(skills)} repository skills checked")
    return 0


if __name__ == "__main__":
    sys.exit(main())
