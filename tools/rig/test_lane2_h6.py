# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
# GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
# content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
from contextlib import redirect_stdout
import io
import json
from pathlib import Path
import tempfile
import unittest

import scen_lane2_h6 as h6


BUILD = 'abcde01-2611190255'


def native_line(ms, body, launch='abcdef'):
    seconds, ms = divmod(ms % 86_400_000, 1000)
    hours, seconds = divmod(seconds, 3600)
    minutes, seconds = divmod(seconds, 60)
    return f'[{hours:02d}:{minutes:02d}:{seconds:02d}.{ms:03d}] t000042 L{launch} {body}'


def death_rows(deaths=30, tail_ms=60_000):
    start = 86_370_000  # One session through midnight.
    rows = [native_line(start, f'KCDMP.dll attached, pid=8 build={BUILD}'), native_line(start, 'WO221-ACTEND armed: ReleaseCombatActor=verified')]
    for death in range(1, deaths + 1):
        tick = start + death * 1000
        entity = f'eid=0x{death:X} attempt={death}'
        gate = f'MP-LANE2-H6 {entity} path=named lifecycle=4 connection=1'
        rows.extend(native_line(tick + index, f'{gate} stage={phase}') if phase else native_line(tick + index, detail)
                    for index, (phase, detail) in enumerate((
                        (None, f'WO221-ACTEND {entity} receipt=2 waiting=1 active=2 why=death'),
                        ('release-pending', ''), (None, f'WO221-ACTEND {entity} receipt=1 waiting=0 active=0 why=death'),
                        ('ready', ''), ('death-dispatched', ''), ('death-pending', ''), ('applied', ''))))
    rows.append(native_line(start + deaths * 1000 + 6 + tail_ms, 'FRAME window_s=60.0 frames=4000 fps=66 ours_us_p95=1800'))
    return rows


def agent(role):
    return [f'MP-BUILD version=0.65.2 build={BUILD}', 'MP-WO121 cfg avatar_gait=on friendly_fire=on ff_from=host',
            f'MP-T3-STATS origin=agent role={role} rp_owner_n=90 owner_violations=0']


class H6EvidenceTests(unittest.TestCase):
    def check(self, lines):
        build, native = h6.native_launch(lines)
        self.assertEqual(BUILD, build)
        return h6.native_evidence(native)

    def test_thirty_exact_ordered_deaths_and_a_sixty_second_engine_tail(self):
        result = self.check(death_rows())
        self.assertEqual('green', result['status'])
        self.assertEqual(30, len(result['qualified_deaths']))
        self.assertEqual(60_000, result['tail_ms'])
        self.assertEqual('grey', self.check(death_rows(29))['status'])
        self.assertEqual('grey', self.check(death_rows(tail_ms=59_999))['status'])

    def test_pipe_abort_inside_the_tail_fails_even_when_frames_continue(self):
        rows = death_rows()
        rows.insert(-1, native_line(86_401_000, 'PIPE: agent disconnected'))
        self.assertIn('native-pipe-disconnected-during-h6', self.check(rows)['red'])
        shutdown = native_line(86_461_000, 'PIPE: agent disconnected')
        self.assertEqual('green', self.check([*death_rows(), shutdown])['status'])

    def test_thirty_deaths_cannot_be_combined_across_connections(self):
        rows = death_rows()
        split = next(i for i, row in enumerate(rows) if 'attempt=16 ' in row)
        rows[split:] = [row.replace('connection=1', 'connection=2') for row in rows[split:]]
        self.assertIn('h6-run-scope-changed', self.check(rows)['red'])

    def test_uncertain_dispatch_can_only_close_on_the_same_single_dispatch(self):
        rows = [row.replace('stage=death-pending', 'stage=apply-uncertain') if 'attempt=1 ' in row else row
                for row in death_rows()]
        self.assertEqual('green', self.check(rows)['status'])
        dispatched = next(i for i, row in enumerate(rows) if 'attempt=1 ' in row and 'stage=death-dispatched' in row)
        rows.insert(dispatched + 1, rows[dispatched])
        self.assertIn('attempt-1-duplicate-terminal-or-dispatch', self.check(rows)['red'])

    def test_a_confirmed_abort_is_separate_from_the_regular_thirty(self):
        rows = death_rows()
        first = next(i for i, row in enumerate(rows) if 'attempt=1 ' in row and 'stage=release-pending' in row)
        rows[first + 1:first + 6] = [rows[first + 5].replace('stage=applied', 'stage=aborted-before-apply')]
        report = self.check(rows)
        self.assertIn('attempt-1-aborted-before-apply', report['red'])
        self.assertEqual(29, len(report['qualified_deaths']))

    def test_failed_preparation_cannot_count_or_later_dispatch_for_the_same_attempt(self):
        rows = death_rows()
        dispatched = next(i for i, row in enumerate(rows) if 'attempt=1 ' in row and 'stage=death-dispatched' in row)
        for marker in ('preparation-not-started', 'preparation-uncertain'):
            failed_preparation = rows[dispatched].replace('stage=death-dispatched', f'stage={marker}')
            incomplete = [*rows[:dispatched], failed_preparation, *rows[dispatched + 3:]]
            report = self.check(incomplete)
            self.assertEqual(29, len(report['qualified_deaths']))
            self.assertIn('attempt-1-death-unconfirmed', report['grey'])
            self.assertNotIn('unreadable-h6-marker', report['grey'])
            self.assertIn('attempt-1-mutation-after-failed-preparation',
                          self.check([*rows[:dispatched], failed_preparation, *rows[dispatched:]])['red'])

    def test_ready_is_required_before_dispatch_and_isdead(self):
        rows = death_rows()
        ready = next(i for i, row in enumerate(rows) if 'attempt=1 ' in row and 'stage=ready' in row)
        rows[ready], rows[ready + 1] = rows[ready + 1], rows[ready]
        self.assertIn('attempt-1-mutation-without-ordered-release', self.check(rows)['red'])
        rows = death_rows()
        applied = next(row for row in rows if 'attempt=1 ' in row and 'stage=applied' in row)
        rows.insert(rows.index(applied) + 1, applied)
        self.assertIn('attempt-1-duplicate-terminal-or-dispatch', self.check(rows)['red'])

    def test_missing_active_action_or_dispatch_cannot_be_counted(self):
        rows = death_rows()
        rows[2] = rows[2].replace('active=2', 'active=0')
        self.assertIn('attempt-1-waiting-and-active-unmeasured', self.check(rows)['grey'])
        rows = [row for row in death_rows() if not ('attempt=1 ' in row and 'stage=death-dispatched' in row)]
        self.assertIn('attempt-1-already-dead-or-dispatch-unmeasured', self.check(rows)['grey'])

    def test_stale_body_scope_and_frame_budget_are_rejected(self):
        rows = death_rows()
        old = next(i for i, row in enumerate(rows) if 'attempt=1 ' in row and 'stage=applied' in row)
        rows[old] = rows[old].replace('lifecycle=4', 'lifecycle=5')
        self.assertIn('attempt-1-scope-or-body-changed', self.check(rows)['red'])
        rows = death_rows()
        rows[-1] = rows[-1].replace('ours_us_p95=1800', 'ours_us_p95=2000')
        self.assertIn('frame-p95-out-of-budget', self.check(rows)['red'])

    def test_pair_requires_same_build_role_and_a_shared_fresh_join(self):
        host_native = [native_line(0, f'KCDMP.dll attached, pid=6 build={BUILD}'),
                       native_line(60_000, 'FRAME window_s=60.0 frames=4000 fps=66 ours_us_p95=1700')]
        host = dict(agent=agent('host'), native=host_native, game=['MP-JOIN resume join=0xabcdef13 reason=ready paused_s=2'])
        joiner = dict(agent=agent('joiner') + ['MP-JOIN joiner: join 0xabcdef13 begin', 'ready sent for join 0xabcdef13'],
                      native=death_rows(), game=[])
        self.assertEqual('green', h6.check_pair(host, joiner, BUILD)['status'])
        self.assertEqual('grey', h6.check_pair(dict(host, game=[]), joiner, BUILD)['status'])
        self.assertEqual('grey', h6.check_pair(host, dict(joiner, agent=agent('host')), BUILD)['status'])
        self.assertEqual('grey', h6.check_pair(host, joiner, 'other-build')['status'])
        restarted = [*joiner['native'], native_line(65_000, f'KCDMP.dll attached, pid=9 build={BUILD}', 'fedcba')]
        self.assertEqual('grey', h6.check_pair(host, dict(joiner, native=restarted), BUILD)['status'])

    def test_cli_requires_both_actual_join_ids_and_reports_both_receiver_roles(self):
        with tempfile.TemporaryDirectory() as out:
            args = ['--build', BUILD]
            for name, join in (('a-host', 'abcdef13'), ('b-joiner', 'abcdef13'), ('b-host', 'abcdef14'), ('a-joiner', 'abcdef14')):
                role = name.split('-')[1]
                role_agent = agent(role)
                if role == 'joiner':
                    role_agent += [f'MP-JOIN joiner: join 0x{join} begin', f'ready sent for join 0x{join}']
                host_game = [f'MP-JOIN resume join=0x{join} reason=ready'] if role == 'host' else []
                args.append(f'--{name}')
                for kind, data in (('agent', role_agent), ('native', death_rows()), ('game', host_game)):
                    path = Path(out, f'{name}-{kind}.log')
                    path.write_text('\n'.join(data), encoding='utf-8')
                    args.append(str(path))
            output = io.StringIO()
            with redirect_stdout(output):
                self.assertEqual(0, h6.main(args))
            result = json.loads(output.getvalue())
            self.assertTrue(result['distinct_joins'])
            self.assertEqual({'a-host/b-joiner', 'b-host/a-joiner'}, set(result['runs']))
            Path(out, 'a-joiner-agent.log').write_text('\n'.join(agent('joiner')), encoding='utf-8')
            output = io.StringIO()
            with redirect_stdout(output):
                self.assertEqual(1, h6.main(args))
            self.assertEqual('grey', json.loads(output.getvalue())['status'])


if __name__ == '__main__':
    unittest.main()
