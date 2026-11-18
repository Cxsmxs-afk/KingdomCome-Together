# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS).
# SPDX-License-Identifier: GPL-3.0-only
"""The regular launcher, installer and service defaults never start the bench."""
from pathlib import Path
import unittest


REPO = Path(__file__).resolve().parents[2]


class SoloBoundaries(unittest.TestCase):
    def test_solo_opt_in_is_absent_from_regular_launch_and_release_sources(self):
        launcher = (REPO / 'KCDMP_launcher').rglob('*.cs')
        installer = (REPO / 'installer').rglob('*.iss')
        config = (REPO / 'dotnet/KcdMp.Server').glob('*.json')
        packaging = (REPO / 'tools' / name for name in ('Build-Installer.ps1', 'Publish-Release.ps1'))
        files = [path for path in (*launcher, *installer, *config, *packaging)
                 if not {'bin', 'obj'}.intersection(path.relative_to(REPO).parts)]
        self.assertGreater(len(files), 10)
        for path in files:
            with self.subTest(path=path.relative_to(REPO)):
                contents = path.read_text(encoding='utf-8-sig').lower()
                self.assertNotIn('kcdmp_test_solo_abend', contents)
                self.assertNotIn('solo-abend.ps1', contents)
                self.assertNotIn('--test-solo-abend', contents)
