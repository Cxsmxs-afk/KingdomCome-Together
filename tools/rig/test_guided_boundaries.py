# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS).
# SPDX-License-Identifier: GPL-3.0-only
"""Check that the guided run is absent from ordinary launch and release sources."""

from codecs import BOM_UTF16_BE, BOM_UTF16_LE
from pathlib import Path
import re
import unittest


REPO = Path(__file__).resolve().parents[2]
OPT_IN = re.compile(r'kcdmp_test_guided_run|--test-guided-run|\bmp_testlauf\b', re.I)
LAUNCH_SOURCE = {'.bat', '.cfg', '.cmd', '.config', '.cs', '.csproj', '.ini', '.iss',
                 '.js', '.json', '.lua', '.ps1', '.razor', '.sh', '.toml', '.txt',
                 '.xml', '.yaml', '.yml'}


class GuidedBoundaries(unittest.TestCase):
    def test_scanner_detects_each_activation_entry(self):
        for source in ('$env:KCDMP_TEST_GUIDED_RUN = "1"',
                       '{ "args": "--test-guided-run" }',
                       'ExecuteCommand("mp_testlauf")'):
            with self.subTest(source=source):
                self.assertRegex(source, OPT_IN)
        self.assertNotRegex('TestRunDuration = 30', OPT_IN)

    def test_ordinary_launch_and_release_sources_cannot_activate_a_guided_run(self):
        launch = [path for name in ('KCDMP_launcher', 'installer')
                  for path in (REPO / name).rglob('*')
                  if path.suffix.lower() in LAUNCH_SOURCE and path.is_file()
                  and not {'bin', 'obj'}.intersection(path.relative_to(REPO).parts)]
        configs = [path for name in ('KcdMp.Client', 'KcdMp.Server', 'KcdMp.MasterServer')
                   for path in (REPO / 'dotnet' / name).glob('*.json')]
        agent_start = REPO / 'dotnet/KcdMp.Client/Program.cs'
        packaging = [REPO / 'tools' / name
                     for name in ('Build-Installer.ps1', 'Publish-Release.ps1')]
        self.assertIn(REPO / 'KCDMP_launcher/Program.cs', launch)
        self.assertIn(REPO / 'installer/KCDMP.iss', launch)
        self.assertIn(REPO / 'dotnet/KcdMp.Server/appsettings.json', configs)
        for path in (*launch, agent_start, *configs, *packaging):
            with self.subTest(path=path.relative_to(REPO)):
                source = path.read_bytes()
                encoding = 'utf-16' if source.startswith((BOM_UTF16_LE, BOM_UTF16_BE)) else 'utf-8-sig'
                self.assertIsNone(OPT_IN.search(source.decode(encoding)))
