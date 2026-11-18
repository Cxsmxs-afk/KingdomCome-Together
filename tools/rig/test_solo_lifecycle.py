#!/usr/bin/env python3
# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
"""Offline lifecycle tests. No game, input, process termination or listener is started."""
import ctypes
import hashlib
import os
from pathlib import Path
import tempfile
import types
import unittest
from unittest.mock import Mock, patch

if os.name != 'nt':
    # Import the rig on non-Windows without invoking any Windows functions.
    ctypes.windll = types.SimpleNamespace(kernel32=Mock(), user32=Mock(), psapi=Mock())
    ctypes.WINFUNCTYPE = ctypes.CFUNCTYPE

import pair


class SoloLifecycleTests(unittest.TestCase):
    def setUp(self):
        directory = self.enterContext(tempfile.TemporaryDirectory())
        self.root = Path(directory)
        self.run_id = 'solo-test'
        self.lock = self.root / 'solo-abend.owner'
        self.hashes = self.root / 'runs' / self.run_id / 'real-saves-before.sha256'
        self.saves = self.root / 'own-saves'
        self.saves.mkdir()
        self.enterContext(patch.dict(os.environ, {'KCDMP_RIG_RUN': self.run_id}))
        self.enterContext(patch.multiple(pair, SOLO=True, SOLO_LOCK=str(self.lock), COPIES=str(self.root / 'copies')))
        self.enterContext(patch.multiple(pair.rig, RIG=directory, RUNS=str(self.root / 'runs'),
                STATE=str(self.root / 'state.json'), HASHES=str(self.hashes), SAVES_A=str(self.saves),
                note=Mock(), say=Mock()))

    def own(self):
        self.lock.write_text(self.run_id, encoding='utf-8')
        pair.rig.save_state({'solo_run': self.run_id})

    def test_foreign_or_unadmitted_down_never_stops_processes(self):
        with patch.object(pair.procs, 'stop') as stop:
            self.assertEqual(0, pair.down('abend'))
            self.lock.write_text('foreign-run', encoding='utf-8')
            pair.rig.save_state({'solo_run': 'foreign-run', 'a': {'pid': 48}})
            self.assertEqual(0, pair.down('abend'))
            self.assertEqual('foreign-run', self.lock.read_text(encoding='utf-8'))
            stop.assert_not_called()
            with self.assertRaisesRegex(RuntimeError, 'does not own'):
                pair.prepare()

    def test_owned_down_stops_its_own_record_and_holds_lease_for_checks(self):
        self.own()
        record = {'pid': 77, 'created': 123, 'image': 'the-fixture.exe'}
        pair.rig.save_state({'solo_run': self.run_id, 'a': record})
        def verify(_):
            self.assertEqual(self.run_id, self.lock.read_text(encoding='utf-8'))
            return True
        with patch.object(pair.procs, 'stop', return_value='stopped') as stop, \
             patch.object(pair.procs, 'matches', return_value=False), \
             patch.object(pair.rig, 'saves_ok', side_effect=verify), \
             patch.object(pair.mirror, 'verify', return_value=[]):
            self.assertEqual(0, pair.down('abend'))
        stop.assert_called_with(record)
        self.assertFalse(self.lock.exists())
        self.assertEqual({}, pair.rig.load_state())

    def test_manual_second_window_does_not_send_input(self):
        self.own()
        watch = types.SimpleNamespace(user_folder='KingdomCome2_B(1)', http_port=43123,
            menu=True, device_lost=[], poll=Mock())
        with patch.object(pair.rig, 'checks', return_value=[]), patch.object(pair, 'user_folder_refusal', return_value=None), \
             patch.object(pair.procs, 'start', return_value={'pid': 78}), patch.object(pair.procs, 'matches', return_value=False), \
             patch.object(pair, 'wait_line', return_value=('menu', 0)), patch.object(pair.procs, 'LogWatch', return_value=watch), \
             patch.object(pair.dialogs, 'answer_in_background') as input_hook:
            self.assertEqual(0, pair.up('B', manual_confirm=True))
            input_hook.assert_not_called()
        self.assertEqual(78, pair.rig.load_state()['b']['pid'])

    def test_real_saves_manifest_is_a_read_only_snapshot(self):
        self.own()
        seed = self.saves / 'local.whs'
        seed.write_bytes(b'fixture only')
        pair.capture_solo_save_hashes()
        digest = hashlib.sha256(seed.read_bytes()).hexdigest()
        self.assertEqual(digest + '  12  local.whs\n', self.hashes.read_text(encoding='utf-8'))
        self.assertEqual(b'fixture only', seed.read_bytes())
        seed.write_bytes(b'changed file')
        self.assertFalse(pair.rig.saves_ok('twin'))

    def test_replace_existing_fixture_hardlink_without_touching_its_real_inode(self):
        self.own()
        game_saves = self.root / 'saved-games'
        original = self.saves / 'original.whs'
        original.write_bytes(b'original data')
        new_save = self.root / 'fixture.whs'
        new_save.write_bytes(b'new test data')
        before = game_saves / 'KingdomCome2_A' / 'saves' / 'playline4' / 'save440.whs'
        before.parent.mkdir(parents=True)
        os.link(original, before)
        with patch.object(pair.mirror, 'SAVED_GAMES', str(game_saves)), \
             patch.dict(os.environ, {'KCDMP_RIG_SOLO_HOST_SAVE': str(new_save),
                                     'KCDMP_RIG_SOLO_JOINER_SAVE': str(new_save)}):
            pair.seed_solo_saves()
        self.assertEqual(b'original data', original.read_bytes())
        self.assertEqual(b'new test data', before.read_bytes())
        self.assertFalse(os.path.samefile(original, before))

    def test_loopback_readiness_and_exact_arguments(self):
        self.own()
        started = []
        def start(argv, cwd, env, role, log_path=None):
            started.extend(argv)
            Path(log_path).write_text(f'TCP listener bound to 127.0.0.1:{pair.RELAY_PORT}.\n'
                f'HTTP loopback ready on http://127.0.0.1:{pair.RELAY_HTTP}.\n', encoding='utf-8')
            return {'pid': 79}
        with patch.object(pair, 'loopback_supported', return_value=True), \
             patch.object(pair.procs, 'start', side_effect=start), patch.object(pair.procs, 'matches', side_effect=bool):
            self.assertEqual(0, pair.relay(profile='normal', loopback=True))
        self.assertEqual('127.0.0.1', started[started.index('--Tcp:BindAddress') + 1])
        self.assertEqual(f'http://127.0.0.1:{pair.RELAY_HTTP}', started[started.index('--urls') + 1])
        self.assertEqual('false', started[started.index('--Steam:Enabled') + 1])
        self.assertTrue(pair.rig.load_state()['relay']['loopback_ready'])

    def test_retry_does_not_reuse_an_unconfirmed_relay_or_another_fault_plan(self):
        self.own()
        relay = {'pid': 79, 'loopback': True, 'fault_plan': ''}
        state = {'solo_run': self.run_id, 'relay': relay}
        pair.rig.save_state(state)
        with patch.object(pair.procs, 'start') as start, patch.object(pair.procs, 'matches', side_effect=bool):
            self.assertEqual(2, pair.relay(profile='normal', loopback=True))
            relay['loopback_ready'] = True
            pair.rig.save_state(state)
            self.assertEqual(2, pair.relay('cut.json', loopback=True))
            self.assertEqual(0, pair.relay(profile='normal', loopback=True))
            start.assert_not_called()

    def test_old_relay_is_refused_and_apphost_target_support_is_checked(self):
        self.own()
        executable = self.root / 'KcdMpServer.exe'
        executable.write_bytes(b'KcdMpServer.dll\0')
        dll = executable.with_suffix('.dll')
        dll.write_bytes(b'legacy binary')
        self.assertFalse(pair.loopback_supported(str(executable)))
        with patch.object(pair.procs, 'start') as start:
            with self.assertRaisesRegex(RuntimeError, 'does not support'):
                pair.relay(loopback=True)
            start.assert_not_called()
        dll.write_bytes('|'.join(('Tcp:LoopbackOnly', 'TCP listener bound to {Address}:{Port}.',
            'HTTP loopback ready on {Url}.')).encode('utf-16-le'))
        self.assertTrue(pair.loopback_supported(str(executable)))


if __name__ == '__main__':
    unittest.main()
