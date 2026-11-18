// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;

namespace KcdMp.Client;

public partial class GameBridge
{
    private readonly object _worldRecoveryStatsGate = new();
    private WorldRecoverySample? _worldRecoverySample;
    private static long _nextWorldRecoveryRun = DateTime.UtcNow.Ticks;
    private const byte WorldRecoveryStatsOp = 248;
    private const int RecoverySectionCount = 6, RecoveryAttemptFields = 5;
    private const int RecoveryAttemptStart = RecoverySectionCount * 2, RecoveryCheckedStart = RecoveryAttemptStart + RecoveryAttemptFields;
    private const int RecoveryFailedStart = RecoveryCheckedStart + RecoverySectionCount, RecoveryFieldCount = RecoveryFailedStart + RecoverySectionCount;

    private enum RecoveryMeasure { Mismatch = 0, Repaired = RecoverySectionCount, Checked = RecoveryCheckedStart, Failed = RecoveryFailedStart }

    private sealed class WorldRecoverySample(WorldSyncScope scope)
    {
        public readonly WorldSyncScope Scope = scope;
        public readonly long Run = Interlocked.Increment(ref _nextWorldRecoveryRun);
        public readonly long[] Values = new long[RecoveryFieldCount];
    }

    private WorldRecoverySample WorldRecoverySampleLocked(WorldSyncScope scope)
    {
        if (_worldRecoverySample is not { } sample || sample.Scope != scope)
            _worldRecoverySample = sample = new(scope);
        return sample;
    }

    private void WorldRecoveryCount(WorldSyncReceiver receiver, WorldBaselineSection section, RecoveryMeasure measure)
    {
        lock (_worldRecoveryStatsGate)
        {
            if (!WorldSyncCurrent(receiver) || !WorldRecoveryWireEffective) return;
            WorldRecoverySampleLocked(receiver.Scope).Values[(int)measure + (int)section - 1]++;
        }
    }

    private void WorldRecoveryReconnectSample(uint world, ulong generation, long elapsedMs,
        bool sameWorld, bool success, bool timeout, bool reloaded)
    {
        var scope = new WorldSyncScope(world, generation);
        if (!scope.IsValid) return;
        lock (_worldRecoveryStatsGate)
        {
            var sample = WorldRecoverySampleLocked(scope);
            var values = sample.Values.AsSpan(RecoveryAttemptStart, RecoveryAttemptFields);
            values[0] = Math.Max(0, elapsedMs);
            values[1] = sameWorld ? 1 : 0;
            values[2] = success ? 1 : 0;
            values[3] = timeout ? 1 : 0;
            values[4] = reloaded ? 1 : 0;
        }
        T3WorldRecoveryReport(LoadSequenceRole, CancellationToken.None, mirror: false);
    }

    private void T3WorldRecoveryReport(string roleName, CancellationToken ct, bool mirror = true)
    {
        if (roleName == "off") return;
        WorldSyncScope scope;
        long run;
        long[] values;
        lock (_worldRecoveryStatsGate)
        {
            if (_worldRecoverySample is not { } sample) return;
            scope = sample.Scope;
            run = sample.Run;
            values = (long[])sample.Values.Clone();
        }

        var fields = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["recovery_world"] = scope.HostWorld,
            ["recovery_gen"] = (long)scope.Generation,
            ["recovery_run"] = run,
            ["reconnect_ms"] = values[RecoveryAttemptStart],
            ["reconnect_same_world"] = values[RecoveryAttemptStart + 1],
            ["reconnect_success"] = values[RecoveryAttemptStart + 2],
            ["reconnect_timeout"] = values[RecoveryAttemptStart + 3],
            ["reconnect_reload"] = values[RecoveryAttemptStart + 4]
        };
        for (int index = 0; index < RecoverySectionCount; index++)
        {
            string area = WorldRecoveryArea((WorldBaselineSection)(index + 1));
            fields.Add("digest_checked_" + area, values[(int)RecoveryMeasure.Checked + index]);
            fields.Add("digest_mismatch_" + area, values[(int)RecoveryMeasure.Mismatch + index]);
            fields.Add("resync_" + area, values[(int)RecoveryMeasure.Repaired + index]);
            fields.Add("resync_failed_" + area, values[(int)RecoveryMeasure.Failed + index]);
        }
        Console.WriteLine($"MP-T3-STATS origin=agent role={roleName} section=recovery {T3FormatStats(fields)}");
        if (mirror) _ = T3MirrorWorldRecoveryAsync(scope, run, values, ct);
    }

    private async Task T3MirrorWorldRecoveryAsync(WorldSyncScope scope, long run, long[] values, CancellationToken ct)
    {
        var current = _worldLoad.Current;
        if (current.HostWorld != scope.HostWorld || current.Generation != scope.Generation ||
            current.Phase != WorldLoadPhase.Play || !_combat.IsConnected) return;
        byte[] payload = new byte[21 + values.Length * 8];
        payload[0] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(1), scope.Generation);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(9), scope.HostWorld);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(13), run);
        for (int index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(21 + index * 8), values[index]);
        try
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stop.CancelAfter(2000);
            var reply = await _combat.Wo163Async(WorldRecoveryStatsOp, payload, stop.Token);
            if (reply is not { Ok: true } || !reply.Value.Payload.AsSpan().SequenceEqual(payload))
                Console.WriteLine("MP-T3-STATS recovery mirror unavailable");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or TimeoutException)
        { Console.WriteLine($"MP-T3-STATS recovery mirror unavailable: {ex.GetType().Name}"); }
    }
}
