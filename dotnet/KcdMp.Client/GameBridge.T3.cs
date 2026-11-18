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
    private static readonly string[] T3RangedKeys = [
        "ranged_out", "ranged_in", "ranged_kind_unknown", "ranged_bow_played", "ranged_hits_verdict",
        "ranged_peer_tx", "ranged_capture_kind_unknown", "ranged_poll_started", "ranged_poll_busy", "ranged_poll_refused", "ranged_poll_timeouts",
        "ranged_malformed", "ranged_stale", "ranged_duplicate", "ranged_stream_reset", "ranged_phase_unrenderable", "ranged_animation_refused",
        "ranged_missile_requests", "ranged_missile_measured_accept", "ranged_missile_host_verdict_queued", "ranged_missile_local_host",
        "ranged_missile_refused", "ranged_missile_duplicate", "ranged_missile_conflict", "ranged_missile_uncertain", "ranged_missile_ack_timeout",
        "ranged_stats_unreadable"
    ];
    private void T3Increment(string name, long amount = 1) => _t3Counters.AddOrUpdate(name, amount, (_, n) => n + amount);
    private void T3Max(string name, long value) => _t3Counters.AddOrUpdate(name, value, (_, n) => Math.Max(n, value));

    private void T3LuaStats(string kind, string? fields)
    {
        if (!T3LootCounters.TryRead(kind, fields, out var values))
        { T3Increment(kind == "t3_corpse_stats" ? "corpse_lua_stats_unreadable" : "loot_stats_unreadable"); return; }
        foreach (var field in values) _t3Counters[field.Key] = field.Value;
    }

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

    private async Task T3SetPresentationClockAsync(string value)
    {
        if (value is not ("on" or "off")) return;
        if (Environment.GetEnvironmentVariable("KCDMP_TEST_RP_MEASURE") != "1")
        {
            Console.WriteLine("MP-RP-MEASURE clock=off status=disabled");
            return;
        }

        try
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            byte mode = value == "on" ? (byte)1 : (byte)0;
            var reply = await _combat.Wo163Async(Protocol.T3OpStats, [Protocol.T3PresentationClockCommand, mode], stop.Token);
            Console.WriteLine($"MP-RP-MEASURE clock={value} status={(reply is { Ok: true } ? "ok" : "refused")}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"MP-RP-MEASURE clock={value} status=unavailable reason={ex.GetType().Name}");
        }
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
                        LoadSequenceTick();
                        // Native work spawned by replay inherits this receive lease;
                        // EOF revokes it before a queued late write can reach the game.
                        await ProcessorFrameScope.RunAsync(_ =>
                        {
                            Wo136ReplayIfReleased(writer, ct);
                            return Task.CompletedTask;
                        }, Volatile.Read(ref _t3ProcFrameTimeoutMs), ct);
                        _ = T3RunSideChannelDrainAsync(ct);
                        _ = T3SyncInputOwnersAsync(ct);
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
        _ = T3PublishStatsAsync(ct); // neither status I/O nor mirroring can hold replay
    }

    private async Task T3PublishStatsAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _t3StatsBusy, 1, 0) != 0) return;
        try
        {
            foreach (string key in T3AuthorityCounters.NativeKeys) _t3Counters.TryAdd(key, 0);
            for (int i = 0; i < T3NativeCounters.PresentationStart; i++) _t3Counters.TryAdd(T3NativeCounters.Keys[i], 0);
            _t3Counters.TryAdd("postload_timeouts", 0);
            _t3Counters.TryAdd("authority_stats_unreadable", 0);
            _t3Counters.TryAdd("world_stats_unreadable", 0);
            _t3Counters.TryAdd("corpse_stats_unreadable", 0);
            _t3Counters.TryAdd("ranged_stats_unreadable", 0);
            byte role = W137Host ? (byte)1 : W137Joiner || _jj is not null ? (byte)2 : (byte)0;
            Dictionary<string, long>? nativeSnapshot = null;
            IReadOnlyDictionary<string, long>? timelineSnapshot = null;
            IReadOnlyDictionary<string, long>? controllerSnapshot = null;
            IReadOnlyDictionary<string, long>? combatSnapshot = null;
            IReadOnlyDictionary<string, long>? loadSnapshot = null;
            if (role != 0)
            {
                var result = await ProcessorFrameScope.RunAsync(async token =>
                {
                    var native = await _combat.Wo163Async(Protocol.Wo216OpBrains, [], token);
                    ProcessorFrameScope.ThrowIfAbandoned();
                    if (native is { Ok: true } b && T3AuthorityCounters.TryRead(Encoding.ASCII.GetString(b.Payload), out var values))
                        foreach (var field in values) _t3Counters[field.Key] = field.Value;
                    else T3Increment("authority_stats_unreadable");
                }, 2000, ct);
                if (result.Status is ProcessorFrameScope.Outcome.Faulted or ProcessorFrameScope.Outcome.TimedOut)
                    T3Increment("authority_stats_unreadable");
                Dictionary<string, long>? nativeCounters = null;
                var worldStats = await ProcessorFrameScope.RunAsync(async token =>
                {
                    var native = await _combat.Wo163Async(Protocol.T3OpStats, [], token);
                    ProcessorFrameScope.ThrowIfAbandoned();
                    if (native is { Ok: true } b && T3NativeCounters.TryRead(b.Payload, out var values))
                    {
                        for (int i = 0; i < T3NativeCounters.PresentationStart; i++)
                        {
                            string key = T3NativeCounters.Keys[i];
                            if (values.TryGetValue(key, out long value)) _t3Counters[key] = value;
                        }
                        nativeCounters = values;
                        if (!values.ContainsKey("markers_off_session")) T3Increment("corpse_stats_unreadable");
                        if (!values.ContainsKey("ranged_native_missing_actor")) T3Increment("ranged_stats_unreadable");
                    }
                    else { T3Increment("world_stats_unreadable"); T3Increment("corpse_stats_unreadable"); T3Increment("ranged_stats_unreadable"); }
                }, 2000, ct);
                if (worldStats.Status is ProcessorFrameScope.Outcome.Faulted or ProcessorFrameScope.Outcome.TimedOut)
                { T3Increment("world_stats_unreadable"); T3Increment("corpse_stats_unreadable"); T3Increment("ranged_stats_unreadable"); }
                if (worldStats.Status == ProcessorFrameScope.Outcome.Completed && nativeCounters?.ContainsKey("rp_e2e_n") == true)
                    nativeSnapshot = nativeCounters;
                // A DLL that implements the previous edge snapshot may also
                // support the independent timeline page. Refusal leaves it absent.
                if (nativeSnapshot?.ContainsKey("edge_rt_n") == true)
                    timelineSnapshot = await T3ReadNativePageAsync(Protocol.T3TimelineStatsCommand, T3NativeCounters.TryReadTimeline, ct);
                if (timelineSnapshot is not null)
                    controllerSnapshot = await T3ReadNativePageAsync(Protocol.T3ControllerStatsCommand, T3NativeCounters.TryReadController, ct);
                if (controllerSnapshot is not null)
                    combatSnapshot = await T3ReadNativePageAsync(Protocol.T3CombatStatsCommand, T3NativeCounters.TryReadCombat, ct);
                if (combatSnapshot is not null)
                    loadSnapshot = await T3ReadNativePageAsync(Protocol.T3LoadStatsCommand, T3NativeCounters.TryReadLoad, ct);
            }
            ct.ThrowIfCancellationRequested();
            T3EmitStats(role, ct, nativeSnapshot, timelineSnapshot, controllerSnapshot, combatSnapshot, loadSnapshot);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { Console.WriteLine($"MP-T3-STATS report unavailable: {ex.GetType().Name}"); }
        finally { Interlocked.Exchange(ref _t3StatsBusy, 0); }
    }

    private delegate bool ReadT3Page(ReadOnlySpan<byte> payload, out Dictionary<string, long> counters);

    private async Task<IReadOnlyDictionary<string, long>?> T3ReadNativePageAsync(byte command, ReadT3Page read, CancellationToken ct)
    {
        Dictionary<string, long>? snapshot = null;
        var result = await ProcessorFrameScope.RunAsync(async token =>
        {
            var reply = await _combat.Wo163Async(Protocol.T3OpStats, [command], token);
            ProcessorFrameScope.ThrowIfAbandoned();
            if (reply is { Ok: true } b && read(b.Payload, out var values))
                snapshot = values;
        }, 2000, ct);
        return result.Status == ProcessorFrameScope.Outcome.Completed ? snapshot : null;
    }

    private void T3EmitStats(byte role, CancellationToken ct, IReadOnlyDictionary<string, long>? nativeSnapshot,
        IReadOnlyDictionary<string, long>? timelineSnapshot, IReadOnlyDictionary<string, long>? controllerSnapshot,
        IReadOnlyDictionary<string, long>? combatSnapshot, IReadOnlyDictionary<string, long>? loadSnapshot)
    {
        foreach (string key in T3LootCounters.LootKeys.Concat(T3LootCounters.InputKeys).Concat(T3LootCounters.CorpseKeys).Concat(T3RangedKeys).Concat(new[] {
            "auto_rejoins_in_world", "loot_fastlane_frames", "loot_fastlane_max_wait_ms", "loot_fastlane_unknown_epoch", "loot_fastlane_stale", "loot_stats_unreadable", "corpse_lua_stats_unreadable" }))
            _t3Counters.TryAdd(key, 0);
        foreach (string key in new[] { "proc_stalls", "proc_stall_max_ms", "proc_frames_abandoned", "deferred_sidechannel", "proc_frame_faults", "proc_tick_faults", "engine_teleport_lines", "input_owner_sync_failed", "tp_snapshot_timeouts", "tp_snapshot_refused" })
            _t3Counters.TryAdd(key, 0);
        _t3Counters["w136_held"] = Interlocked.Read(ref _w136Held);
        _t3Counters["w136_replayed"] = Interlocked.Read(ref _w136Replayed);
        _t3Counters["proc_lua_abandoned"] = _httpForMenu?.AbandonedStatements ?? 0;
        _t3Counters["proc_outbound_abandoned"] = _w213Writer?.Scheduler.CancelledDropped ?? 0;
        IEnumerable<KeyValuePair<string, long>> counters = _t3Counters.Concat([
            KeyValuePair.Create("proc_awaits_per_frame_max", (long)Volatile.Read(ref _edgeProcAwaitsPerFrameMax)),
            KeyValuePair.Create("inproc_fallbacks", Interlocked.Read(ref _edgeInprocFallbacks)),
            KeyValuePair.Create("http_calls_rt", Interlocked.Read(ref _edgeHttpCallsRt)),
            KeyValuePair.Create("edge_proc_n", Interlocked.Read(ref _edgeFramesAdmitted))]);
        // The combat page supplies its own action latency measured at actual
        // playback. Older snapshot latency stays available with older DLLs.
        if (nativeSnapshot is not null)
            counters = counters.Concat(T3NativeCounters.Keys.Skip(T3NativeCounters.PresentationStart)
                .Where(key => nativeSnapshot.ContainsKey(key) && combatSnapshot?.ContainsKey(key) != true)
                .Select(key => KeyValuePair.Create(key, nativeSnapshot[key])));
        if (timelineSnapshot is not null) counters = counters.Concat(timelineSnapshot);
        if (controllerSnapshot is not null) counters = counters.Concat(controllerSnapshot);
        if (combatSnapshot is not null) counters = counters.Concat(combatSnapshot);
        string fields = T3FormatStats(counters);
        string roleName = role == 1 ? "host" : role == 2 ? "joiner" : "off";
        string? loadFields = T3LoadStats(loadSnapshot);
        if (loadFields is not null)
            Console.WriteLine($"MP-T3-STATS origin=agent role={roleName} section=load {loadFields}");
        Console.WriteLine($"MP-T3-STATS origin=agent role={roleName} {fields}");
        if (role != 0) _ = T3SendNativeStatsAsync(role, fields, loadSnapshot is null ? null : loadFields, ct);
    }

    private static string T3FormatStats(IEnumerable<KeyValuePair<string, long>> counters) =>
        string.Join(' ', counters.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key + "=" + pair.Value.ToString(CultureInfo.InvariantCulture)));

    private string? T3LoadStats(IReadOnlyDictionary<string, long>? native)
    {
        var fields = new Dictionary<string, long>(StringComparer.Ordinal);
        if (native is not null)
            foreach (var (key, value) in native)
                fields.Add(key, value);

        if (LoadSequenceEnabled)
        {
            var before = _worldLoad.Current;
            var durations = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var phase in Enum.GetValues<WorldLoadPhase>())
                durations.Add("load_phase_ms_" + phase.ToString().ToLowerInvariant(), _worldLoad.PhaseMilliseconds(phase));
            var after = _worldLoad.Current;
            if (before.Generation == after.Generation && before.Phase == after.Phase)
            {
                fields.Add("load_gen", (long)Math.Min(after.Generation, (ulong)long.MaxValue));
                fields.Add("load_host_world", after.HostWorld);
                fields.Add("load_phase", (long)after.Phase);
                foreach (var (key, value) in durations) fields.Add(key, value);
            }
            fields.Add("stale_gen_dropped", Interlocked.Read(ref _staleGenerationDropped));
        }

        return fields.Count == 0 ? null : T3FormatStats(fields);
    }

    private async Task T3SendNativeStatsAsync(byte role, string fields, string? loadFields, CancellationToken ct)
    {
        if (loadFields is not null) await T3MirrorStatsAsync(role, loadFields, ct, load: true);
        await T3MirrorStatsAsync(role, fields, ct, load: false);
    }

    private async Task T3MirrorStatsAsync(byte role, string fields, CancellationToken ct, bool load)
    {
        try
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stop.CancelAfter(2000);
            byte[] text = Encoding.ASCII.GetBytes(fields);
            byte[] payload = load ? [Protocol.T3LoadStatsCommand, role, .. text] : [role, .. text];
            var reply = await _combat.Wo163Async(Protocol.T3OpStats, payload, stop.Token);
            if (reply is not { Ok: true })
                Console.WriteLine("MP-T3-STATS native mirror unavailable: no accepted reply");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        { Console.WriteLine($"MP-T3-STATS native mirror unavailable: {ex.GetType().Name}"); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
