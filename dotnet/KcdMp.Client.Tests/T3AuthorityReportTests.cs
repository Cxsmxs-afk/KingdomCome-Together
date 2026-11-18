// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Buffers.Binary;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public class T3AuthorityReportTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private const string Snapshot = "B gate_armed_at_s=5 decision_blocked=44 decision_blocked_during_load=0 npc_near=100 npc_bound=40 npc_hidden=60 hide_failsafe_on=0 unhidden_unbound_10s=3 decision_gate=armed";
    private static void Set(GameBridge b, string key, object value) => typeof(GameBridge).GetField(key, Private)!.SetValue(b, value);
    private static async Task<byte[]> Read(Stream stream, CancellationToken ct)
    {
        var header = new byte[3]; await stream.ReadExactlyAsync(header, ct);
        Assert.Equal(0x2B, header[0]);
        var body = new byte[header[1] | header[2] << 8]; await stream.ReadExactlyAsync(body, ct); return body;
    }
    private static async Task Reply(Stream stream, byte seq, byte op, CancellationToken ct, bool truncated = false,
        bool worldStats = false, int statsFields = 30, bool timeline = false, bool controller = false, bool combat = false,
        bool load = false, bool refused = false)
    {
        byte[] payload = op == Protocol.Wo216OpBrains ? Encoding.ASCII.GetBytes(truncated ? "B decision_blocked=999 npc_hidden=999" : Snapshot) : [];
        if (worldStats)
        {
            int count = truncated ? 29 : statsFields;
            payload = new byte[2 + count * 9]; payload[0] = 1; payload[1] = (byte)count;
            for (int i = 0; i < count; i++) { payload[2 + i * 9] = (byte)i; BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(3 + i * 9), (ulong)(10 + i)); }
        }
        if (timeline || controller || combat || load)
        {
            ulong[] values = timeline ? [86, 42, 2, 18, 360, 4] : controller
                ? [1500, 1499, 1, 2, 3, 4, 5, 6, 7, 8, 0, 199, 1, 301508] : combat
                ? [20, 19, 0, 24, 19, 3, 0, 81, 2, 1] : [1, 5, 11, 22, 333, 444, 555, 666, 0, 3, 9];
            payload = new byte[2 + values.Length * 9]; payload[0] = 1; payload[1] = (byte)values.Length;
            for (int i = 0; i < values.Length; i++)
            {
                payload[2 + i * 9] = (byte)i;
                BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(3 + i * 9), values[i]);
            }
        }
        byte[] body = [(byte)(refused ? 0 : 1), seq, op, (byte)(refused ? 8 : 0), .. payload];
        byte[] frame = [0xAC, (byte)body.Length, (byte)(body.Length >> 8), .. body];
        await stream.WriteAsync(frame, ct);
    }

    [Theory]
    [InlineData(true, 1, false, 30)]
    [InlineData(false, 2, false, 30)]
    [InlineData(false, 2, true, 30)]
    [InlineData(false, 2, false, 24)]
    [InlineData(false, 2, false, 19)]
    [InlineData(true, 1, false, 47)]
    [InlineData(false, 2, false, 47)]
    [InlineData(false, 2, false, 46)]
    [InlineData(false, 2, false, 48)]
    [InlineData(true, 1, false, 49)]
    [InlineData(false, 2, false, 49)]
    [InlineData(true, 1, false, 55)]
    [InlineData(false, 2, false, 55)]
    [InlineData(false, 2, false, 56)]
    [InlineData(true, 1, false, 55, true)]
    [InlineData(false, 2, false, 55, true)]
    [InlineData(true, 1, false, 55, true, true)]
    [InlineData(false, 2, false, 55, true, true)]
    [InlineData(true, 1, false, 55, true, true, true)]
    [InlineData(false, 2, false, 55, true, true, true)]
    [InlineData(true, 1, false, 55, true, true, true, true)]
    [InlineData(false, 2, false, 55, true, true, true, true)]
    public async Task Both_roles_mirror_complete_snapshots_and_preserve_old_gauges_when_native_status_is_truncated(bool host, byte role,
        bool truncated, int statsFields, bool controllerSupported = false, bool combatSupported = false, bool loadSupported = false,
        bool events = false)
    {
        string name = "t3r-" + Guid.NewGuid().ToString("N")[..16];
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accepted = server.WaitForConnectionAsync(stop.Token);
        await using var pipe = new CombatPipe(name) { BatchOn = false };
        Assert.True(await pipe.EnsureConnectedAsync(stop.Token)); await accepted;
        var b = new GameBridge(new ClientConfig());
        await ((CombatPipe)typeof(GameBridge).GetField("_combat", Private)!.GetValue(b)!).DisposeAsync();
        Set(b, "_combat", pipe); Set(b, "_combatRoleApplied", true); Set(b, "_isDamageAuthority", host);
        Set(b, "_sharedWorld", true); Set(b, "_hostAuthority", true); Set(b, "_joinedWorld", true);
        Set(b, "_hostModeKnown", true); Set(b, "_hostSharedWorld", true);
        long eventReceiver = 0;
        if (loadSupported)
        {
            Set(b, "_loadSequenceRuntime", true);
            Set(b, "_staleGenerationDropped", 2L);
            var load = (WorldLoadSequence)typeof(GameBridge).GetField("_worldLoad", Private)!.GetValue(b)!;
            Assert.True(load.TryBegin(0, WorldLoadStart.AdoptLoadedWorld, hostWorld: 58));
        }
        if (events)
        {
            var metrics = (WorldSyncMetrics)typeof(GameBridge).GetField("_worldSyncMetrics", Private)!.GetValue(b)!;
            var scope = new WorldSyncScope(58, 18);
            object receiver = new();
            metrics.Track(receiver);
            metrics.Begin(receiver, scope, 17, role);
            metrics.BaselineApplied(receiver, scope, 17, 1, 24);
            metrics.EventApplied(receiver, scope, 17);
            metrics.RecordSender(receiver, new WorldSyncStatus(scope, 17, role, new(1, 1, 2, 0)));
            eventReceiver = metrics.Snapshot!.Value.Receiver;
        }
        var counters = (ConcurrentDictionary<string, long>)typeof(GameBridge).GetField("_t3Counters", Private)!.GetValue(b)!;
        counters["postload_timeouts"] = 3;
        counters["decision_blocked"] = 9; counters["npc_hidden"] = 8;
        counters["tp_total"] = 7;
        counters["corpses_visible"] = 6; counters["markers_off_session"] = 0;
        counters["ranged_native_unknown_hand"] = 42;
        Task report = (Task)typeof(GameBridge).GetMethod("T3PublishStatsAsync", Private)!.Invoke(b, [stop.Token])!;
        Assert.Equal(new byte[] { Protocol.Wo216OpBrains }, await Read(server, stop.Token));
        await Reply(server, 1, Protocol.Wo216OpBrains, stop.Token, truncated);
        Assert.Equal(new byte[] { Protocol.T3OpStats }, await Read(server, stop.Token));
        await Reply(server, 2, Protocol.T3OpStats, stop.Token, truncated, worldStats: true, statsFields: statsFields);
        byte mirrorSequence = 3;
        bool timelineSupported = statsFields == 55;
        if (statsFields >= 55)
        {
            Assert.Equal(new byte[] { Protocol.T3OpStats, Protocol.T3TimelineStatsCommand }, await Read(server, stop.Token));
            await Reply(server, 3, Protocol.T3OpStats, stop.Token, timeline: timelineSupported, refused: !timelineSupported);
            mirrorSequence++;
        }
        if (timelineSupported)
        {
            Assert.Equal(new byte[] { Protocol.T3OpStats, Protocol.T3ControllerStatsCommand }, await Read(server, stop.Token));
            await Reply(server, 4, Protocol.T3OpStats, stop.Token, controller: controllerSupported, refused: !controllerSupported);
            mirrorSequence++;
        }
        if (controllerSupported)
        {
            Assert.Equal(new byte[] { Protocol.T3OpStats, Protocol.T3CombatStatsCommand }, await Read(server, stop.Token));
            await Reply(server, 5, Protocol.T3OpStats, stop.Token, combat: combatSupported, refused: !combatSupported);
            mirrorSequence++;
        }
        if (combatSupported)
        {
            Assert.Equal(new byte[] { Protocol.T3OpStats, Protocol.T3LoadStatsCommand }, await Read(server, stop.Token));
            await Reply(server, mirrorSequence++, Protocol.T3OpStats, stop.Token, load: loadSupported, refused: !loadSupported);
        }
        if (loadSupported)
        {
            var loadMirror = await Read(server, stop.Token);
            Assert.Equal(new byte[] { Protocol.T3OpStats, Protocol.T3LoadStatsCommand, role }, loadMirror[..3]);
            var part = Encoding.ASCII.GetString(loadMirror, 3, loadMirror.Length - 3);
            Assert.Contains("load_gen=1 ", part);
            Assert.Contains("load_host_world=58 ", part);
            Assert.Contains("load_phase=5 ", part);
            Assert.Contains("load_phase_ms_menu=", part);
            Assert.Contains("load_native_phase_ms_load=333 ", part);
            Assert.Contains("load_phase_native=5 ", part);
            Assert.Contains("load_native_observations=9 ", part);
            Assert.Contains("stale_gen_dropped=2 ", part);
            Assert.EndsWith("stale_gen_native_dropped=3", part);
            Assert.DoesNotContain("owner_violations=", part);
            await Reply(server, mirrorSequence++, Protocol.T3OpStats, stop.Token);
        }
        if (events)
        {
            var eventMirror = await Read(server, stop.Token);
            Assert.Equal(new byte[] { Protocol.T3OpStats, Protocol.T3WorldSyncStatsMirror, role }, eventMirror[..3]);
            string part = Encoding.ASCII.GetString(eventMirror, 3, eventMirror.Length - 3);
            Assert.Equal("baseline_ms=24 baseline_version=1 evt_acked=1 evt_applied=1 evt_dropped=0 evt_gap=0 evt_gen=18 " +
                $"evt_receiver={eventReceiver} evt_recipient={role} evt_retx=2 evt_sender_dropped=0 evt_sent=1 evt_snapshot=17 evt_world=58", part);
            await Reply(server, mirrorSequence++, Protocol.T3OpStats, stop.Token);
        }
        var mirror = await Read(server, stop.Token);
        Assert.Equal(Protocol.T3OpStats, mirror[0]); Assert.Equal(role, mirror[1]);
        Assert.InRange(mirror.Length, 4, 6402); // actual complete generic body, including op and role
        string fields = Encoding.ASCII.GetString(mirror, 2, mirror.Length - 2);
        Assert.InRange(fields.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length, 1, 160);
        Assert.True(T3AuthorityCounters.TryRead(fields, out var actual));
        Assert.DoesNotContain("load_phase=", fields);
        Assert.DoesNotContain("evt_applied=", fields);
        Assert.Equal(truncated ? 9 : 44, actual["decision_blocked"]);
        Assert.Equal(truncated ? 8 : 60, actual["npc_hidden"]);
        Assert.Contains("postload_timeouts=3", fields);
        foreach (string key in new[] { "pickup_asks", "pickup_optimistic", "pickup_rollbacks", "pickup_unknown_kept", "ask_timeouts", "ask_latency_ms_p95", "take_busy_refusals", "input_held_max_s", "input_releases", "auto_rejoins_in_world" })
            Assert.Contains(key + "=0", fields);
        Assert.Equal(truncated ? 1 : 0, counters["authority_stats_unreadable"]);
        bool worldUnavailable = truncated || statsFields is 46 or 48;
        Assert.Equal(worldUnavailable ? 1 : 0, counters["world_stats_unreadable"]);
        bool corpseUnavailable = worldUnavailable || statsFields < 24;
        bool rangedUnavailable = worldUnavailable || statsFields < 30;
        Assert.Equal(corpseUnavailable ? 1 : 0, counters["corpse_stats_unreadable"]);
        Assert.Equal(rangedUnavailable ? 1 : 0, counters["ranged_stats_unreadable"]);
        Assert.Contains("tp_total=" + (worldUnavailable ? 7 : 10), fields);
        Assert.Contains("falldeath_after_tp=" + (worldUnavailable ? 0 : 24), fields);
        Assert.Contains("corpses_visible=" + (corpseUnavailable ? 6 : 29), fields);
        Assert.Contains("grave_markers_added=" + (corpseUnavailable ? 0 : 31), fields);
        Assert.Contains("grave_marker_fallbacks=" + (corpseUnavailable ? 0 : 32), fields);
        Assert.Contains("markers_off_session=" + (corpseUnavailable ? 0 : 33), fields);
        Assert.Contains("ranged_native_unknown_hand=" + (rangedUnavailable ? 42 : 36), fields);
        foreach (string key in new[] { "ranged_out", "ranged_in", "ranged_kind_unknown", "ranged_bow_played", "ranged_hits_verdict" }) Assert.Contains(key + "=0", fields);
        foreach (string key in new[] { "lua_corpse_visible", "lua_corpse_hidden_blocked", "lua_corpse_unknown_kept" }) Assert.Contains(key + "=0", fields);
        if (statsFields >= 47 && !worldUnavailable)
        {
            Assert.Contains("rp_e2e_ms_p95=41", fields);
            Assert.Contains("rp_step_ratio=48", fields);
            Assert.Contains("owner_violations=50", fields);
            Assert.Contains("rp_owner_n=56", fields);
        }
        else Assert.DoesNotContain("rp_e2e_n=", fields);
        if (statsFields >= 49 && !worldUnavailable)
        {
            Assert.Contains("owner_handovers=57", fields);
            Assert.Contains("owner_denied_sets=58", fields);
        }
        else
        {
            Assert.DoesNotContain("owner_handovers=", fields);
            Assert.DoesNotContain("owner_denied_sets=", fields);
        }
        if (timelineSupported)
        {
            Assert.Contains("rp_buffer_ms=86", fields);
            Assert.Contains("rp_extrap_ms_max=42", fields);
            Assert.Contains("rp_late_samples=2", fields);
            Assert.Contains("rp_action_pos_skew_ms_p95=18", fields);
            Assert.Contains("rp_timeline_pose_n=360", fields);
            Assert.Contains("rp_timeline_skew_n=4", fields);
        }
        else Assert.DoesNotContain("rp_timeline_skew_n=", fields);
        if (controllerSupported)
        {
            Assert.Contains("rp_ctrl_frames=1500", fields);
            Assert.Contains("rp_req_ok=1499", fields);
            Assert.Contains("rp_req_fail=1", fields);
            Assert.Contains("rp_fallback_by_ragdoll=4", fields);
            Assert.Contains("rp_hard_tp_by_travel=8", fields);
            Assert.Contains("rp_hard_tp_unsafe=0", fields);
            Assert.Contains("rp_pulls_per_min=199", fields);
            Assert.Contains("rp_pull_n=1", fields);
            Assert.Contains("rp_pull_observed_ms=301508", fields);
        }
        else Assert.DoesNotContain("rp_pull_observed_ms=", fields);
        if (combatSupported)
        {
            Assert.Contains("rp_actions_in=20", fields);
            Assert.Contains("rp_actions_played=19", fields);
            Assert.Contains("rp_actions_pending=0", fields);
            Assert.Equal("rp_action_lag_ms_p95=24", Assert.Single(fields.Split(' '), key => key.StartsWith("rp_action_lag_ms_p95=", StringComparison.Ordinal)));
            Assert.Contains("rp_action_confirmed_n=19", fields);
            Assert.Contains("dmg_double_blocked=3", fields);
            Assert.Contains("dmg_double=0", fields);
            Assert.Contains("rp_ko_up_lag_ms_p95=81", fields);
            Assert.Contains("rp_ko_up_n=2", fields);
            Assert.Contains("dmg_double_unconfirmed=1", fields);
        }
        else
        {
            Assert.DoesNotContain("rp_action_confirmed_n=", fields);
            if (statsFields >= 47 && !worldUnavailable) Assert.Contains("rp_action_lag_ms_p95=49", fields);
        }
        await Reply(server, mirrorSequence, Protocol.T3OpStats, stop.Token);
        await report.WaitAsync(stop.Token);
        var gate = (SemaphoreSlim)typeof(CombatPipe).GetField("_gate", Private)!.GetValue(pipe)!;
        await gate.WaitAsync(stop.Token); gate.Release(); // the fire-and-forget mirror has consumed its ACK
    }

    [Theory]
    [InlineData("1", "on", 1)]
    [InlineData("1", "off", 0)]
    [InlineData(null, "on", null)]
    [InlineData("true", "on", null)]
    [InlineData("1", "on extra", null)]
    public async Task Presentation_clock_command_is_test_gated_before_writing_to_the_pipe(string? environment, string command, int? mode)
    {
        const string variable = "KCDMP_TEST_RP_MEASURE";
        string? previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, environment);
            string name = "rpt-" + Guid.NewGuid().ToString("N")[..16];
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var accepted = server.WaitForConnectionAsync(stop.Token);
            await using var pipe = new CombatPipe(name) { BatchOn = false };
            Assert.True(await pipe.EnsureConnectedAsync(stop.Token));
            await accepted;
            var bridge = new GameBridge(new ClientConfig());
            await ((CombatPipe)typeof(GameBridge).GetField("_combat", Private)!.GetValue(bridge)!).DisposeAsync();
            Set(bridge, "_combat", pipe);
            var change = (Task)typeof(GameBridge).GetMethod("T3SetPresentationClockAsync", Private)!.Invoke(bridge, [command])!;
            if (mode is int value)
            {
                Assert.Equal(new byte[] { Protocol.T3OpStats, 3, (byte)value }, await Read(server, stop.Token));
                await Reply(server, 1, Protocol.T3OpStats, stop.Token);
            }
            await change.WaitAsync(stop.Token);
            if (!mode.HasValue)
            {
                using var silence = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(server, silence.Token));
            }
        }
        finally { Environment.SetEnvironmentVariable(variable, previous); }
    }
}
