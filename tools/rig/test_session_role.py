# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.

from contextlib import redirect_stdout
from io import StringIO
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest

import scen_session_role as rig

HOST_GATE = "MP-LAUNCH connect gate closed (host: the game's log) -- Load your save first"
JOIN_GATE = "MP-LAUNCH connect gate closed (joiner: the host's relay) -- Your host is not ready yet"
DIRECT = "Connection test via=direct kind=None reachable=True rttMs=18 connectMs=4 hostRelease=0.65.2 hostConnected=True"
BUILD = 'MP-BUILD version=0.65.2 build=synthetic-same-build'


def probe(role, *, in_menu=False):
    name = 'host' if role == 'host' else 'joiner'
    other = 'join' if role == 'host' else 'host'
    old_gate = JOIN_GATE if role == 'host' else HOST_GATE
    gate = HOST_GATE if role == 'host' else JOIN_GATE
    seq = 6 if in_menu else 0
    app = [rig.LAUNCHER_START, DIRECT]
    if in_menu:
        app += [old_gate, f'MP-LAUNCH session role selected ({other}) source=launcher gamePid=101 menuSeq=0',
                'MP-W159 menu driver on (joiner)' if role == 'host' else 'MP-W159 menu driver on (host)']
    app += [gate, f'MP-LAUNCH session role selected ({role}) source={"menu" if in_menu else "launcher"} gamePid=101 menuSeq={seq}']
    app.append(f'MP-W159 menu driver on ({name})')
    game = []
    if in_menu:
        game += [f'[KCD2-MP-EVT] v1 {seq} w159 role {role}', f'WO159-MENU role receipt seq={seq} target={role} accepted=true']
    game += [f'WO159-MENU model role={role} saves=2', f'WO159-MENU page={role} entries=4 (build 4)']
    if role == 'host':
        app += ['MP-W159 load: wh_sys_LoadGame 2 autosave118 -> sent', rig.HOST_CONNECT]
        game += ['[KCD2-MP-EVT] v1 8 w159 load 2 autosave118']
    else:
        app += ['MP-W159 first-join answer sent to the agent: bring']
        game += ['[KCD2-MP-EVT] v1 8 w159 join bring']
    agent = [BUILD, f'MP-T3-STATS origin=agent role={name} owner_violations=0 rp_owner_n=0 owner_handovers=0 owner_denied_sets=0']
    return app, game, agent


class SessionRoleEvidenceTests(unittest.TestCase):
    def test_each_role_has_direct_launcher_and_correlated_game_menu_paths(self):
        for role in ('host', 'join'):
            for switched in (False, True):
                with self.subTest(role=role, switched=switched):
                    self.assertEqual((True, True), rig.trace(*probe(role, in_menu=switched), role)[:2])

    def test_menu_switch_needs_exact_accepted_receipt(self):
        app, game, agent = probe('join', in_menu=True)
        for fake in ('seq=9 target=join accepted=true', 'seq=6 target=host accepted=true', 'seq=6 target=join accepted=false'):
            with self.subTest(fake=fake):
                changed = [line.replace('seq=6 target=join accepted=true', fake) for line in game]
                self.assertFalse(rig.trace(app, changed, agent, 'join')[0])

    def test_no_address_can_finish_the_same_request_in_launcher_menu(self):
        app, game, agent = probe('join', in_menu=True)
        selected = next(i for i, line in enumerate(app) if 'selected (join)' in line)
        app[selected] = 'MP-LAUNCH session role selected (join) source=launcher-menu gamePid=101 menuSeq=0'
        app.insert(selected - 1, 'MP-LAUNCH session role awaits a join destination menuSeq=6')
        game[1] = game[1].replace('accepted=true', 'accepted=false')
        self.assertTrue(rig.trace(app, game, agent, 'join')[0])
        game[0] = game[0].replace('v1 6', 'v1 5')
        self.assertFalse(rig.trace(app, game, agent, 'join')[0])

    def test_wrong_gate_or_driver_stale_page_missing_agent_and_only_dispatched_load_fail(self):
        app, game, agent = probe('host')
        self.assertFalse(rig.trace(app[:-1], game, agent, 'host')[0])
        self.assertFalse(rig.trace(app, [*game[:-1], 'WO159-MENU page=root entries=10', game[-1]], agent, 'host')[0])
        self.assertFalse(rig.trace([line.replace(HOST_GATE, JOIN_GATE) for line in app], game, agent, 'host')[0])
        self.assertFalse(rig.trace([line.replace('driver on (host)', 'driver on (joiner)') for line in app], game, agent, 'host')[0])
        self.assertFalse(rig.trace(app, game, probe('join')[2], 'host')[0])

    def test_direct_proof_cannot_use_an_older_launcher_or_a_version_mismatch(self):
        app, game, agent = probe('host')
        self.assertEqual((True, False), rig.trace([DIRECT, *app[0:1], *app[2:]], game, agent, 'host')[:2])
        invalid = [line.replace('kind=None', 'kind=VersionMismatch') for line in app]
        self.assertEqual((True, False), rig.trace(invalid, game, agent, 'host')[:2])
        no_host = [line.replace('hostConnected=True', 'hostConnected=False') for line in app]
        self.assertEqual((True, False), rig.trace(no_host, game, agent, 'host')[:2])

    def test_no_zero_owner_claim_without_typed_coverage_and_positive_counter_is_red(self):
        prefix = 'MP-T3-STATS origin=agent role=joiner '
        missing = prefix + 'owner_violations=0 rp_owner_n=12'
        self.assertEqual('GRAU', rig.owner_status([missing], 'joiner'))
        covered = missing + ' owner_handovers=0 owner_denied_sets=0'
        self.assertEqual('GRÜN', rig.owner_status([covered], 'joiner'))
        contradiction = prefix + 'owner_violations=1'
        self.assertEqual('ROT', rig.owner_status([contradiction, covered], 'joiner'))

    def test_all_four_captures_one_build_and_both_roles_are_required(self):
        with TemporaryDirectory() as directory, redirect_stdout(StringIO()) as report:
            for folder, role in rig.ROUNDS:
                capture = Path(directory) / folder
                capture.mkdir()
                for name, rows in zip(('launcher.log', 'kcd.log', 'agent.log'), probe(role)):
                    (capture / name).write_text('\n'.join(rows), encoding='utf-8')
            self.assertEqual(0, rig.main([directory]))
            self.assertIn('Single-Owner GRAU', report.getvalue())
            path = Path(directory) / 'A-join' / 'agent.log'
            path.write_text(path.read_text(encoding='utf-8').replace('synthetic-same-build', 'different-build'), encoding='utf-8')
            self.assertEqual(1, rig.main([directory]))
            path.unlink()
            self.assertEqual(1, rig.main([directory]))


if __name__ == '__main__':
    unittest.main()
