# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
import io
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile
import tempfile
import unittest

import pair


def verdict(status, *rows):
    fields = [pair.HEADER, f"Gesamt\t{status}", pair.COLUMNS]
    fields.extend("\t".join(row) for row in rows)
    return ("\n".join(fields) + "\n").encode("utf-8")


QUEUE_OK = ("queue", pair.GREEN, "34 ms", "MP-W207-Q")
QUEUE_STALL = ("queue", pair.RED, "230000 ms", "MP-W207-Q")
NO_OWNERS = ("owner_violations", pair.GRAY, "–", "Messung fehlt")


class PairTests(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.root = Path(tmp.name)
        self.host = self.root / "Host Ordner"
        self.host.mkdir()
        self.joiner = self.root / "Joiner Ergebnis.txt"

    def write_sides(self, host, joiner):
        (self.host / pair.REPORT).write_bytes(host)
        self.joiner.write_bytes(joiner)

    def run_pair(self, host=None, joiner=None):
        result = subprocess.run(
            [sys.executable, str(Path(pair.__file__).resolve()), str(host or self.host), str(joiner or self.joiner)],
            cwd=self.root, capture_output=True, text=True, check=False,
        )
        return result, (self.root / pair.PAIR_REPORT).read_text(encoding="utf-8")

    def test_two_clean_reports_allow_optional_gray_rows(self):
        report = verdict(pair.GREEN, QUEUE_OK, NO_OWNERS)
        with_bom = b"\xef\xbb\xbf" + report.replace(b"\n", b"\r\n")
        self.write_sides(report, with_bom)
        result, markdown = self.run_pair()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("**Gesamt GRÜN**", markdown)
        self.assertIn("| `owner_violations` | **GRAU**", markdown)
        self.assertIn("| `queue` | **GRÜN** · 34 ms", markdown)

    def test_queue_stall_beats_gray_and_yellow(self):
        alert = ("queue", pair.YELLOW, "750 ms", "MP-W207-Q")
        self.write_sides(verdict(pair.RED, QUEUE_STALL, NO_OWNERS), verdict(pair.YELLOW, alert))
        result, markdown = self.run_pair()
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("**Gesamt ROT**", markdown)
        self.assertIn("**ROT** · 230000 ms", markdown)
        self.assertIn("**GELB** · 750 ms", markdown)

    def test_disagreeing_rule_sets_are_gray_and_escape_evidence(self):
        extra = ("extra", pair.GREEN, "<Tag> | 22", "[Text](url)")
        self.write_sides(verdict(pair.GREEN, QUEUE_OK, extra), verdict(pair.GREEN, QUEUE_OK))
        result, markdown = self.run_pair()
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("**Gesamt GRAU**", markdown)
        self.assertIn(r"&lt;Tag&gt; \| 22 · \[Text\]\(url\)", markdown)
        self.assertIn("**GRAU** · Keine Messzeile | **GRAU**", markdown)

    def test_missing_or_gray_queue_cannot_claim_green(self):
        owners = ("owner_violations", pair.GREEN, "0", "MP-T3-STATS")
        for queue in ([], [("queue", pair.GRAY, "–", "")], [("queue", pair.GREEN, "", "")]):
            with self.subTest(queue=queue):
                report = verdict(pair.GREEN, owners, *queue)
                self.write_sides(report, report)
                result, markdown = self.run_pair()
                self.assertEqual(result.returncode, 1, result.stderr)
                self.assertIn("**Gesamt GRAU**", markdown)
                self.assertIn("| `queue` | **GRAU**", markdown)

    def test_invalid_sources_are_reported_instead_of_reusing_an_old_green_report(self):
        report = verdict(pair.GREEN, QUEUE_OK)
        self.write_sides(report, report)
        result, markdown = self.run_pair()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("**Gesamt GRÜN**", markdown)
        (self.host / pair.REPORT).unlink()
        self.joiner.write_bytes(verdict(pair.GREEN, QUEUE_OK, QUEUE_OK))
        result, markdown = self.run_pair()
        self.assertEqual(result.returncode, 2, result.stderr)
        self.assertIn("Host - ERGEBNIS.txt fehlt", result.stderr)
        self.assertIn("Joiner - ERGEBNIS.txt, Zeile 5", result.stderr)
        self.assertIn("**Gesamt GRAU**", markdown)
        self.assertNotIn("**Gesamt GRÜN**", markdown)
        self.assertEqual(list(self.root.glob(".ERGEBNIS-PAAR.md-*")), [])

    def test_bad_header_and_oversize_refused(self):
        for data in (verdict(pair.GREEN, QUEUE_OK).replace(b"ERGEBNIS\t1", b"ERGEBNIS\t2"),
                     b"x" * (pair.MAX_REPORT_BYTES + 1), b"\xff\xff"):
            with self.subTest(head=data[:20]):
                self.joiner.write_bytes(data)
                side = pair.read_side(self.joiner)
                self.assertEqual(side.status, pair.GRAY)
                self.assertTrue(side.error)

    @unittest.skipUnless(shutil.which("tar"), "tar unavailable")
    def test_reads_only_expected_root_archive_member(self):
        archive = self.root / "Bundle Host.tar"
        self.joiner.write_bytes(verdict(pair.GREEN, QUEUE_OK))
        with tarfile.open(archive, "w") as handle:
            for name, data in ((pair.REPORT, verdict(pair.GREEN, QUEUE_OK)), ("outside.txt", b"no extraction")):
                entry = tarfile.TarInfo(name)
                entry.size = len(data)
                handle.addfile(entry, io.BytesIO(data))
        result, markdown = self.run_pair(archive)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("**Gesamt GRÜN**", markdown)
        self.assertFalse((self.root / "outside.txt").exists())

    @unittest.skipUnless(shutil.which("tar") and shutil.which("zstd"), "tar or zstd unavailable")
    def test_reads_part_one_of_split_tzst(self):
        archive = self.root / "Bundle-part1of2.tar"
        data = verdict(pair.GREEN, QUEUE_OK)
        self.joiner.write_bytes(data)
        with tarfile.open(archive, "w") as handle:
            entry = tarfile.TarInfo(pair.REPORT)
            entry.size = len(data)
            handle.addfile(entry, io.BytesIO(data))
        part_one = archive.with_suffix(".tzst")
        subprocess.run(["zstd", "-q", "-o", str(part_one), str(archive)], check=True)
        part_two = self.root / "Bundle-part2of2.tzst"
        part_two.touch()
        result, markdown = self.run_pair(part_two)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("**Gesamt GRÜN**", markdown)
        part_one.unlink()
        self.assertIn("Der erste Bundle-Teil fehlt", pair.read_side(part_two).error)


if __name__ == "__main__":
    unittest.main()
