# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
"""Synthetic acceptance logs for S3-1. These fixtures do not measure game behavior."""

import unittest

from scen_s3_load import final_play, fresh_join, load_sequence, role_stats, stale_delta, stats_failures, weather_result, weather_sent

WEATHER = 'foggy_drizzly_light'
BLEND = f"[KCD2MP] ApplyWeather '{WEATHER}' blend=0 (blending)"


def phase(name, *, role='joiner', gen=4, world=21):
    return f'MP-LOAD-SEQUENCE role={role} gen={gen} world={world} phase={name}'


def weather(result, name, *, gen=4, world=21):
    return f'MP-LOAD-WEATHER role=joiner gen={gen} world={world} phase={name} profile={WEATHER} result={result}'


class LoadSequenceTests(unittest.TestCase):
    def test_two_real_roles_have_distinct_local_generations(self):
        host = [phase(name, role='host', gen=2) for name in ('Load', 'Reconstruct', 'Settle', 'Play')]
        joiner = [phase(name) for name in ('Offer', 'Load', 'Reconstruct', 'Settle', 'Play')]
        host_play = load_sequence(host, 'host', (0, 'Play', 1, 20))
        joiner_play = load_sequence(joiner, 'joiner', (0, 'Play', 3, 20))
        self.assertEqual([], host_play['failures'])
        self.assertEqual([], joiner_play['failures'])
        self.assertEqual(host_play['play'][3], joiner_play['play'][3])
        self.assertNotEqual(host_play['play'][2], joiner_play['play'][2])

    def test_same_phase_world_and_link_observations_do_not_duplicate_transitions(self):
        lines = [phase('Load', role='host', world=0), phase('Load', role='host'),
                 phase('Reconstruct', role='host'), phase('Settle', role='host'), phase('Play', role='host')]
        lines.append(phase('Play', role='host') + ' link_hold=0')
        report = load_sequence(lines, 'host', (0, 'Play', 3, 20))
        self.assertEqual([], report['failures'])
        self.assertEqual(4, report['play'][0])

    def test_bad_order_and_crossed_world_fail_even_with_terminal_play(self):
        lines = [phase('Offer'), phase('Load'), phase('Settle', world=20), phase('Reconstruct'), phase('Play')]
        self.assertEqual(['mixed-world-during-apply', 'phase-order-invalid'], load_sequence(lines, 'joiner')['failures'])

    def test_old_or_interrupted_generation_cannot_confirm_reload(self):
        lines = [phase(name) for name in ('Offer', 'Load', 'Reconstruct', 'Settle', 'Play')]
        previous = (0, 'Play', 4, 20)
        self.assertIn('play-reused-old-generation-or-world', load_sequence(lines, 'joiner', previous)['failures'])
        lines.insert(2, phase('Load', gen=3, world=20))
        self.assertIn('unexpected-generation-switch', load_sequence(lines, 'joiner', (0, 'Play', 3, 20))['failures'])
        self.assertIsNone(final_play(lines + [phase('Exit')], 'joiner'))

    def test_host_resume_must_match_the_latest_ready_id(self):
        agent = ['MP-JOIN joiner: join 0x00000001', 'ready sent for join 0x00000001', 'MP-JOIN joiner: join 0x00000002']
        host = ['MP-JOIN resume join=0x00000001 reason=ready']
        self.assertIsNone(fresh_join(agent, host))
        agent.append('ready sent for join 0x00000002')
        host.append('MP-JOIN resume join=0x00000002 reason=ready')
        self.assertEqual('0x00000002', fresh_join(agent, host))

    def test_weather_requires_matching_held_then_applied_and_lua_blend(self):
        lines = [phase('Offer'), phase('Load'), weather('deferred', 'Load'), phase('Reconstruct'),
                 phase('Settle'), phase('Play'), weather('applied', 'Play')]
        play = final_play(lines, 'joiner')
        self.assertEqual([], weather_result(lines, [BLEND], WEATHER, play)['failures'])
        self.assertIn('weather-game-blend-unconfirmed', weather_result(lines, [], WEATHER, play)['failures'])
        self.assertIn('weather-after-play-missing-or-duplicate',
                      weather_result(lines + [weather('applied', 'Play')], [BLEND], WEATHER, play)['failures'])

    def test_early_apply_and_old_world_apply_fail_without_counting_old_defer(self):
        lines = [phase('Load'), weather('deferred', 'Load', gen=3, world=20),
                 weather('applied', 'Load'), phase('Play'), weather('applied', 'Play', gen=3, world=20)]
        result = weather_result(lines, [BLEND], WEATHER, final_play(lines, 'joiner'))
        self.assertIn('weather-during-load-unmeasured', result['failures'])
        self.assertIn('weather-applied-in-wrong-phase-or-world', result['failures'])

    def test_weather_sender_must_physically_send_to_matching_join_and_world(self):
        marker = 'MP-LOAD-WEATHER-TEST'
        sent = f'{marker} role=host gen=4 world=21 peer=7 join=0x00000042 profile={WEATHER} blend=0 result=sent'
        armed = sent.replace('result=sent', 'result=armed')
        play = final_play([phase('Play', role='host')], 'host')
        self.assertIsNone(weather_sent([armed], marker, WEATHER, play, '0x00000042', 7))
        self.assertIsNone(weather_sent([sent], marker, WEATHER, play, '0x00000042', 6))
        self.assertIsNone(weather_sent([sent, sent], marker, WEATHER, play, '0x00000042', 7))
        self.assertEqual('sent', weather_sent([armed, sent], marker, WEATHER, play, '0x00000042', 7)['result'])

    def test_stats_use_latest_scope_and_keep_stale_counters_separate(self):
        prefix = 'MP-T3-STATS origin=agent role=joiner '
        lines = [prefix + 'owner_violations=0', prefix + 'section=load load_gen=3 load_phase=5',
                 prefix + 'section=load load_gen=4 load_host_world=21 load_phase=5 load_native_gen=4 load_phase_native=5 '
                 'load_native_observations=4 stale_gen_dropped=5 stale_gen_native_dropped=9']
        after = role_stats(lines, 'joiner')
        play = final_play([phase('Play')], 'joiner')
        self.assertEqual([], stats_failures(after, play, False))
        self.assertEqual({'stale_gen_dropped': 2, 'stale_gen_native_dropped': None},
                         stale_delta({'stale_gen_dropped': 3}, after))
        self.assertIn('owner-violations', stats_failures(after, play, True))
        self.assertIn('play-not-observed-in-current-agent-and-native-generation',
                      stats_failures({**after, 'load_native_gen': 3}, play, False))


if __name__ == '__main__':
    unittest.main()
