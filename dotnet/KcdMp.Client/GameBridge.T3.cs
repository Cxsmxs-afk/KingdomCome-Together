// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Collections.Concurrent;
using System.Globalization;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using KcdMp.Wire;

namespace KcdMp.Client;

public partial class GameBridge
{
    private int _t3ProcFrameTimeoutMs = 2000;
    private readonly ConcurrentDictionary<string, long> _t3Counters = new(StringComparer.Ordinal);
    private long _t3StatsNextMs;
    private int _t3SideDrainBusy, _t3StatsBusy;
    private void T3Increment(string name, long amount = 1) => _t3Counters.AddOrUpdate(name, amount, (_, n) => n + amount);
    private void T3Max(string name, long value) => _t3Counters.AddOrUpdate(name, value, (_, n) => Math.Max(n, value));

    private sealed class T3FrameAccounting
    {
        private readonly W207Frame _frame;
        private int _ended;
        public T3FrameAccounting(InFrame frame)
        {
            Wo207Trace.Dequeued(frame.Type);
            _frame = new(frame.Type, frame.Arrival, Stopwatch.GetTimestamp());
        }
        public void End(int kind)
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0) Wo207End(_frame, kind);
        }
    }

    private void T3ProcessorTimeout(string? value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int ms) || ms < 50 || ms > 30000) return;
        Volatile.Write(ref _t3ProcFrameTimeoutMs, ms);
        Console.WriteLine($"MP-T3-CONFIG proc_frame_timeout_ms={ms}");
    }

    private void T3FrameResult(int kind, ProcessorFrameScope.Result result)
    {
        if (result.Status is ProcessorFrameScope.Outcome.Completed or ProcessorFrameScope.Outcome.SessionCancelled) return;
        T3Increment("proc_frames_abandoned");
        if (result.Status == ProcessorFrameScope.Outcome.TimedOut)
        {
            T3Increment("proc_stalls"); T3Max("proc_stall_max_ms", result.ElapsedMs);
        }
        else T3Increment("proc_frame_faults");
        Console.WriteLine($"MP-T3-PROC abandoned type={kind} elapsed_ms={result.ElapsedMs} reason={result.Status} error={result.Error?.GetType().Name ?? "-"}; processor continues");
    }

    // Independent of ProcessFramesAsync. A blocked frame cannot hold load replay,
    // deferred weather or the diagnostic heartbeat hostage.
    private void T3StartIndependentTick(ChannelWriter<InFrame> writer, CancellationToken ct)
    {
        async Task Loop()
        {
            try
            {
                _ = T3ReportConfigAsync(ct); // configuration I/O can never hold the replay clock
                while (!ct.IsCancellationRequested && ReferenceEquals(_frameWriter, writer))
                {
                    try
                    {
                        // Native work spawned by replay inherits this receive lease;
                        // EOF revokes it before a queued late write can reach the game.
                        await ProcessorFrameScope.RunAsync(_ =>
                        {
                            Wo136ReplayIfReleased(writer, ct);
                            return Task.CompletedTask;
                        }, Volatile.Read(ref _t3ProcFrameTimeoutMs), ct);
                        _ = T3RunSideChannelDrainAsync(ct);
                        T3StatsTick(ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        T3Increment("proc_tick_faults");
                        Console.WriteLine($"MP-T3-TICK iteration failed error={ex.GetType().Name}; clock continues");
                    }
                    await Task.Delay(100, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { Console.WriteLine($"MP-T3-TICK stopped error={ex.GetType().Name}"); }
        }
        if (ExecutionContext.IsFlowSuppressed()) _ = Task.Run(Loop);
        else { using (ExecutionContext.SuppressFlow()) _ = Task.Run(Loop); }
    }

    private async Task T3ReportConfigAsync(CancellationToken ct)
    {
        var result = await ProcessorFrameScope.RunAsync(token => _transport.ExecuteNowAsync(
            "if KCD2MP_T3ReportCfg then KCD2MP_T3ReportCfg() end", token), 2000, ct);
        if (result.Status is ProcessorFrameScope.Outcome.Faulted or ProcessorFrameScope.Outcome.TimedOut)
            Console.WriteLine($"MP-T3-CONFIG report unavailable reason={result.Status}");
    }

    private async Task T3RunSideChannelDrainAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _t3SideDrainBusy, 1, 0) != 0) return;
        try
        {
            var result = await ProcessorFrameScope.RunAsync(token => T3DrainSideChannels(token),
                Volatile.Read(ref _t3ProcFrameTimeoutMs), ct);
            T3FrameResult(Protocol.W164Down, result);
        }
        finally { Interlocked.Exchange(ref _t3SideDrainBusy, 0); }
    }

    private void T3StatsTick(CancellationToken ct)
    {
        long now = Environment.TickCount64;
        if (now < Interlocked.Read(ref _t3StatsNextMs)) return;
        Interlocked.Exchange(ref _t3StatsNextMs, now + 60000);
        foreach (string key in new[] { "proc_stalls", "proc_stall_max_ms", "proc_frames_abandoned", "deferred_sidechannel", "proc_frame_faults", "proc_tick_faults" })
            _t3Counters.TryAdd(key, 0);
        _t3Counters["w136_held"] = Interlocked.Read(ref _w136Held);
        _t3Counters["w136_replayed"] = Interlocked.Read(ref _w136Replayed);
        _t3Counters["proc_lua_abandoned"] = _httpForMenu?.AbandonedStatements ?? 0;
        _t3Counters["proc_outbound_abandoned"] = _w213Writer?.Scheduler.CancelledDropped ?? 0;
        string fields = string.Join(' ', _t3Counters.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => p.Key + "=" + p.Value.ToString(CultureInfo.InvariantCulture)));
        byte role = W137Host ? (byte)1 : W137Joiner || _jj is not null ? (byte)2 : (byte)0;
        Console.WriteLine($"MP-T3-STATS origin=agent role={(role == 1 ? "host" : role == 2 ? "joiner" : "off")} {fields}");
        if (role != 0) _ = T3SendNativeStatsAsync(role, fields, ct);
    }

    private async Task T3SendNativeStatsAsync(byte role, string fields, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _t3StatsBusy, 1, 0) != 0) return;
        try
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stop.CancelAfter(2000);
            byte[] text = Encoding.ASCII.GetBytes(fields);
            byte[] payload = new byte[text.Length + 1]; payload[0] = role; text.CopyTo(payload, 1);
            await _combat.Wo163Async(Protocol.T3OpStats, payload, stop.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        { Console.WriteLine($"MP-T3-STATS native mirror unavailable: {ex.GetType().Name}"); }
        finally { Interlocked.Exchange(ref _t3StatsBusy, 0); }
    }
}
