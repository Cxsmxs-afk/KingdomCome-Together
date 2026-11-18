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
    private static async Task Reply(Stream stream, byte seq, byte op, CancellationToken ct, bool truncated = false, bool worldStats = false)
    {
        byte[] payload = op == Protocol.Wo216OpBrains ? Encoding.ASCII.GetBytes(truncated ? "B decision_blocked=999 npc_hidden=999" : Snapshot) : [];
        if (worldStats)
        {
            int count = truncated ? 18 : 19;
            payload = new byte[2 + count * 9]; payload[0] = 1; payload[1] = (byte)count;
            for (int i = 0; i < count; i++) { payload[2 + i * 9] = (byte)i; BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(3 + i * 9), (ulong)(10 + i)); }
        }
        byte[] body = [1, seq, op, 0, .. payload];
        byte[] frame = [0xAC, (byte)body.Length, (byte)(body.Length >> 8), .. body];
        await stream.WriteAsync(frame, ct);
    }

    [Theory]
    [InlineData(true, 1, false)]
    [InlineData(false, 2, false)]
    [InlineData(false, 2, true)]
    public async Task Both_roles_mirror_complete_snapshots_and_preserve_old_gauges_when_native_status_is_truncated(bool host, byte role, bool truncated)
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
        var counters = (ConcurrentDictionary<string, long>)typeof(GameBridge).GetField("_t3Counters", Private)!.GetValue(b)!;
        counters["postload_timeouts"] = 3;
        counters["decision_blocked"] = 9; counters["npc_hidden"] = 8;
        counters["tp_total"] = 7;
        Task report = (Task)typeof(GameBridge).GetMethod("T3PublishStatsAsync", Private)!.Invoke(b, [stop.Token])!;
        Assert.Equal(new byte[] { Protocol.Wo216OpBrains }, await Read(server, stop.Token));
        await Reply(server, 1, Protocol.Wo216OpBrains, stop.Token, truncated);
        Assert.Equal(new byte[] { Protocol.T3OpStats }, await Read(server, stop.Token));
        await Reply(server, 2, Protocol.T3OpStats, stop.Token, truncated, worldStats: true);
        var mirror = await Read(server, stop.Token);
        Assert.Equal(Protocol.T3OpStats, mirror[0]); Assert.Equal(role, mirror[1]);
        string fields = Encoding.ASCII.GetString(mirror, 2, mirror.Length - 2);
        Assert.True(T3AuthorityCounters.TryRead(fields, out var actual));
        Assert.Equal(truncated ? 9 : 44, actual["decision_blocked"]);
        Assert.Equal(truncated ? 8 : 60, actual["npc_hidden"]);
        Assert.Contains("postload_timeouts=3", fields);
        foreach (string key in new[] { "pickup_asks", "pickup_optimistic", "pickup_rollbacks", "pickup_unknown_kept", "ask_timeouts", "ask_latency_ms_p95", "take_busy_refusals", "input_held_max_s", "input_releases", "auto_rejoins_in_world" })
            Assert.Contains(key + "=0", fields);
        Assert.Equal(truncated ? 1 : 0, counters["authority_stats_unreadable"]);
        Assert.Equal(truncated ? 1 : 0, counters["world_stats_unreadable"]);
        Assert.Contains("tp_total=" + (truncated ? 7 : 10), fields);
        Assert.Contains("falldeath_after_tp=" + (truncated ? 0 : 24), fields);
        await Reply(server, 3, Protocol.T3OpStats, stop.Token);
        await report.WaitAsync(stop.Token);
        var gate = (SemaphoreSlim)typeof(CombatPipe).GetField("_gate", Private)!.GetValue(pipe)!;
        await gate.WaitAsync(stop.Token); gate.Release(); // the fire-and-forget mirror has consumed its ACK
    }
}
