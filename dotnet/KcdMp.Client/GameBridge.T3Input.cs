// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;

namespace KcdMp.Client;

public partial class GameBridge
{
    private int _t3InputBusy;
    private long _t3InputSerial, _t3InputNextMs;
    private long _t3InputLifeRevision;
    private ulong _t3InputLifeAuthority;
    private readonly object _t3InputStateGate = new();

    private void T3NoteInputLifeEdge(bool on)
    {
        lock (_t3InputStateGate)
        {
            if (_localDowned == on) return;
            _localDowned = on;
            _t3InputLifeRevision = Math.Max(_t3InputLifeRevision + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _t3InputLifeAuthority = P1Lifecycle.Current.Generation;
        }
        Interlocked.Exchange(ref _t3InputNextMs, 0);
    }

    // Real DLL menu/downed events feed this small independent control snapshot.
    // Repetition is idempotent and repairs a Lua module arriving after the first
    // callback. It never queries the engine or scans entities.
    private async Task T3SyncInputOwnersAsync(CancellationToken ct)
    {
        long now = Environment.TickCount64;
        if (now < Interlocked.Read(ref _t3InputNextMs) || Interlocked.CompareExchange(ref _t3InputBusy, 1, 0) != 0) return;
        try
        {
            Interlocked.Exchange(ref _t3InputNextMs, now + 1000);
            ulong generation = P1Lifecycle.Current.Generation;
            long serial = Math.Max(Interlocked.Read(ref _t3InputSerial) + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Interlocked.Exchange(ref _t3InputSerial, serial);
            bool downed; long lifeRevision;
            lock (_t3InputStateGate) { downed = _localDowned; lifeRevision = _t3InputLifeAuthority == generation ? _t3InputLifeRevision : 0; }
            string command = $"if KCD2MP_T3InputAgentState then KCD2MP_T3InputAgentState({(_w192MenuOpen ? "true" : "false")},{(downed ? "true" : "false")},{serial.ToString(CultureInfo.InvariantCulture)},\"{generation.ToString(CultureInfo.InvariantCulture)}\",{lifeRevision.ToString(CultureInfo.InvariantCulture)}) end";
            var result = await RunProcessorFrameAsync(token => _transport.ExecuteNowAsync(command, token), 2000, ct);
            if (result.Status is ProcessorFrameScope.Outcome.Faulted or ProcessorFrameScope.Outcome.TimedOut) T3Increment("input_owner_sync_failed");
        }
        finally { Interlocked.Exchange(ref _t3InputBusy, 0); }
    }
}
