// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Collections.Concurrent;
using System.Globalization;
using KcdMp.Wire;

namespace KcdMp.Client;

public partial class GameBridge
{
    private readonly SemaphoreSlim _hostCalendarGate = new(1, 1);
    private readonly SemaphoreSlim _hostCalendarSendGate = new(1, 1);
    private readonly object _hostCalendarLock = new();
    private sealed record JoinCalendarRequest(uint JoinId, byte Host, Stream Link);
    private JoinCalendarRequest? _joinCalendarRequest;
    private HostCalendarSample? _hostCalendar;
    private HostCalendarSample? _hostCalendarBootstrap;
    private uint _hostCalendarBootstrapWorld;
    private Stream? _hostCalendarBootstrapStream;
    private TaskCompletionSource? _hostCalendarChanged;
    private readonly ConcurrentDictionary<string, long> _joinCalendarDrops = new(StringComparer.Ordinal);
    private volatile bool _hostCalendarArmed;
    private long _hostCalendarGeneration;
    private uint _hostCalendarResumeEpoch;
    private bool HostCalendarOwned => _hostCalendarArmed && !_isDamageAuthority;
    private long CalendarRelayNowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)(_clockOffsetMs ?? 0);

    /// <summary>A real native reading, including a standing clock. No extrapolation from an old Lua poll while the host holds.</summary>
    private async Task<bool> SendHostCalendarAsync(byte? target = null, bool frozen = false, uint joinId = 0)
    {
        var life = T3SideLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ProcessorFrameScope.CurrentToken, life.Token);
        using var scope = ProcessorFrameScope.Link(linked.Token);
        T3SideGuard(life);
        // Serialize the complete read/send operation: an older periodic read must reach
        // the reliable channel before a later frozen snapshot, not after it.
        await _hostCalendarSendGate.WaitAsync(linked.Token);
        try
        {
            T3SideGuard(life);
            if (!W137Host || _localSkipActive || _w140LocalSkipping || _where != GameWhere.World || (!frozen && !T3SideReady)) return false;
            var read = await _combat.CalendarSnapshotAsync(linked.Token);
            T3SideGuard(life);
            if (_where != GameWhere.World) return false;
            if (read is not { } clock) return false;
            int rate = frozen || _w144ClockPaused ? 0 : (int)Math.Round(clock.Ratio * 1000);
            string text = HostCalendar.Format(clock.WorldMs, rate, CalendarRelayNowMs, _clockOffsetMs.HasValue, frozen ? joinId : 0);
            if (frozen)
            {
                if (_hostJoin is not { } join || joinId == 0 || join.JoinId != joinId || join.Joiner != target)
                {
                    Console.WriteLine($"MP-CALENDAR join 0x{joinId:x8}: snapshot send refused reason=retired-join");
                    return false;
                }
                if (!join.EngineHeld || clock.Ratio != 0)
                {
                    Console.WriteLine($"MP-CALENDAR join 0x{joinId:x8}: snapshot send refused reason=world-not-frozen");
                    return false;
                }
                await SendHeldJoinCalendarAsync(join, text, linked.Token);
                T3SideGuard(life);
                return true;
            }
            if (target is byte one) { T3SideGuard(life); await W164SendAsync(one, Protocol.W164Clock, text, linked.Token); T3SideGuard(life); }
            else foreach (byte peer in Wo134Peers()) { T3SideGuard(life); await W164SendAsync(peer, Protocol.W164Clock, text, linked.Token); T3SideGuard(life); }
            Interlocked.Increment(ref _w200ClockOut);
            return true;
        }
        finally { _hostCalendarSendGate.Release(); }
    }

    private bool TryParseHostCalendar(string text, out HostCalendarSample sample) =>
        HostCalendar.TryParse(text, Environment.TickCount64, CalendarRelayNowMs, _clockOffsetMs.HasValue,
            (_clockRttMedianMs ?? 0) / 2, out sample);

    // These two kinds are join control. The menu has neither a loaded generation nor a WorldSync baseline yet.
    private bool TryHandleJoinCalendarAtRead(in InFrame frame, Stream stream, CancellationToken ct)
    {
        byte[] payload = frame.Payload;
        const int kindOffset = 1 + Protocol.JoinHeaderLen;   // Down adds the relay-authenticated source byte
        if (payload.Length <= kindOffset) return false;
        byte kind = payload[kindOffset];
        bool clock = frame.Type == Protocol.W164Down && kind == Protocol.W164Clock;
        if (!clock && (frame.Type != Protocol.W203Down || kind != Protocol.W203JoinCalendarReceipt)) return false;
        if (!Protocol.TrySplitJoinDown(payload, out byte source, out byte target, out uint joinId, out var body) ||
            !LootMsg.TryDecode(body, out var message))
            return DropJoinCalendar(source, joinId, "invalid-frame");
        if (!clock)
        {
            if (target != _myGhostId) return DropJoinCalendar(source, message.Tok, "wrong-recipient");
            if (!uint.TryParse(message.Text, NumberStyles.None, CultureInfo.InvariantCulture, out uint epoch))
                return DropJoinCalendar(source, message.Tok, "invalid-receipt");
            ObserveJoinCalendarReceipt(source, message.Tok, epoch, stream);
            return true;
        }
        if (!TryParseHostCalendar(message.Text, out var sample)) return DropJoinCalendar(source, 0, "invalid-sample");
        if (!sample.IsJoinSnapshot) return joinId == 0 && sample.JoinId == 0
            ? false
            : DropJoinCalendar(source, joinId, "not-frozen");
        if (joinId != sample.JoinId) return DropJoinCalendar(source, sample.JoinId, "wrong-attempt-envelope");
        if (target != _myGhostId) return DropJoinCalendar(source, sample.JoinId, "wrong-recipient");

        bool duplicate;
        lock (_joinGate)
        {
            if (ct.IsCancellationRequested || !ReferenceEquals(stream, _wo122Stream))
                return DropJoinCalendar(source, sample.JoinId, "retired-link");
            if (!T3SideSource(source)) return DropJoinCalendar(source, sample.JoinId, "not-current-host");
            if (message.Tok == 0 || message.Tok != frame.Stamp || frame.Stamp != _w206.World)
                return DropJoinCalendar(source, sample.JoinId, "different-world");
            if (_joinCalendarRequest is not { } request || request.JoinId != sample.JoinId || request.Host != source ||
                !ReferenceEquals(request.Link, stream)) return DropJoinCalendar(source, sample.JoinId, "inactive-attempt");
            var joining = _jj;
            bool active = joining is not null
                ? joining.JoinId == sample.JoinId && joining.HostWorldEpoch == frame.Stamp && !joining.Abandoned.Task.IsCompleted
                : sample.JoinId == _joinOutId || sample.JoinId == _joinRx?.JoinId || sample.JoinId == _joinReceivedId;
            if (!active) return DropJoinCalendar(source, sample.JoinId, "inactive-attempt");
            lock (_hostCalendarLock)
            {
                duplicate = _hostCalendarBootstrap?.JoinId == sample.JoinId && _hostCalendarBootstrapWorld == frame.Stamp
                    && ReferenceEquals(stream, _hostCalendarBootstrapStream);
                if (!duplicate)
                {
                    _hostCalendarBootstrap = sample;
                    _hostCalendarBootstrapWorld = frame.Stamp;
                    _hostCalendarBootstrapStream = stream;
                    Interlocked.Increment(ref _w200ClockIn);
                    _hostCalendarChanged?.TrySetResult();
                    _hostCalendarChanged = null;
                }
            }
        }
        if (!duplicate)
            Console.WriteLine($"MP-CALENDAR join 0x{sample.JoinId:x8}: bootstrap accepted host={source} world={frame.Stamp} phase={_worldLoad.Current.Phase}");
        uint world = frame.Stamp;
        _ = Task.Run(() => SendJoinCalendarReceiptAsync(stream, source, sample.JoinId, world, ct));
        return true;
    }

    private bool JoinCalendarCurrent(JoinerJoin join, long generation) =>
        ReferenceEquals(_jj, join) && !join.Abandoned.Task.IsCompleted && join.HostWorldEpoch == _w206.World
        && Volatile.Read(ref _joinCalendarRequest) is { } request && request.JoinId == join.JoinId &&
        request.Host == join.Host && ReferenceEquals(request.Link, _wo122Stream)
        && (!LoadSequenceEnabled || _worldLoad.Current.Generation == join.LoadGeneration)
        && generation == Interlocked.Read(ref _hostCalendarGeneration);

    private bool JoinCalendarActuatorCurrent(JoinerJoin join) =>
        HostCalendarOwned && JoinCalendarCurrent(join, join.CalendarGeneration) &&
        join.CalendarPipeRevision == _combat.EdgeConnectionRevision;

    private void ClearJoinCalendarBootstrap()
    {
        lock (_hostCalendarLock)
        {
            _hostCalendarBootstrap = null;
            _hostCalendarBootstrapWorld = 0;
            _hostCalendarBootstrapStream = null;
            _hostCalendarChanged?.TrySetResult();
            _hostCalendarChanged = null;
        }
    }

    private void RetireJoinCalendarAtRead(byte? host, Stream stream, uint? joinId = null)
    {
        lock (_joinGate)
        {
            if (_joinCalendarRequest is not { } request || host is byte source && request.Host != source || !ReferenceEquals(request.Link, stream) ||
                joinId is uint aborted && request.JoinId != aborted) return;
            Volatile.Write(ref _joinCalendarRequest, null);
            ClearJoinCalendarBootstrap();
            if (_jj is { } active && active.JoinId == request.JoinId && active.Host == request.Host)
            {
                active.Abandoned.TrySetResult(true);
                if (_joinReceivedId == active.JoinId) _joinReceivedId = 0;
            }
            if (joinId is not null && _joinRx is null && _joinOutId == request.JoinId)
            {
                _joinOutId = 0;
                SetJoinUi("aborted", "The host stopped the join.");
            }
            Console.WriteLine($"MP-CALENDAR join 0x{request.JoinId:x8}: bootstrap retired reason={(host is null ? "relay-lost" : joinId is null ? "host-left" : "host-abort")}");
        }
    }

    private bool DropJoinCalendar(byte source, uint joinId, string reason)
    {
        long count = _joinCalendarDrops.AddOrUpdate(reason, 1, static (_, prior) => prior + 1);
        if (count <= 4 || count % 100 == 0)
            Console.WriteLine($"MP-CALENDAR join 0x{joinId:x8}: frame dropped source={source} reason={reason} n={count}");
        return true;
    }

    private async Task SendJoinCalendarReceiptAsync(Stream stream, byte host, uint joinId, uint world, CancellationToken ct)
    {
        try
        {
            if (!ReferenceEquals(stream, _wo122Stream) || world != _w206.World ||
                Volatile.Read(ref _joinCalendarRequest) is not { } request || request.JoinId != joinId || request.Host != host ||
                !ReferenceEquals(request.Link, stream))
            {
                DropJoinCalendar(host, joinId, "receipt-retired");
                return;
            }
            var receipt = new LootMsg(Protocol.W203JoinCalendarReceipt, joinId, world.ToString(CultureInfo.InvariantCulture));
            await WritePacketAsync(stream, receipt.BuildUp(Protocol.W203Up, host), ct);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            Console.WriteLine($"MP-CALENDAR join 0x{joinId:x8}: receipt not sent ({ex.GetType().Name})");
        }
    }

    private (HostCalendarSample Sample, long Generation)? CacheHostCalendar(byte source, string text)
    {
        long generation = Interlocked.Read(ref _hostCalendarGeneration);
        byte expected = _jj?.Host ?? (_hostModeKnown ? _hostModeFrom : (byte)0xFF);
        if (_isDamageAuthority || expected == 0xFF || source != expected ||
            !TryParseHostCalendar(text, out var sample) || sample.IsJoinSnapshot) return null;
        lock (_hostCalendarLock)
        {
            if (generation != Interlocked.Read(ref _hostCalendarGeneration)) return null;
            _hostCalendar = sample;
        }
        Interlocked.Increment(ref _w200ClockIn);
        return (sample, generation);
    }

    private Task ReceiveHostCalendarAsync(byte source, string text)
    {
        ProcessorFrameScope.ThrowIfAbandoned();
        var life = T3SideLease();
        if (CacheHostCalendar(source, text) is { } clock)
        {
            _t3SideChannels.Put(T3Clock, new(source, Calendar: clock.Sample, CalendarGeneration: clock.Generation), life);
            T3Increment("deferred_sidechannel");
        }
        return Task.CompletedTask;
    }

    private void RebaseCalendarPoll(long worldMs, double ratio)
    {
        if (worldMs < 0 || worldMs > HostCalendar.MaxWorldMs) return;
        _lastPolledWorldTime = (uint)(worldMs / 1000);
        _lastPollUtc = DateTime.UtcNow;
        _w200Ratio = ratio;
        _reloadConvergeTarget = null;
        _fastAdvanceActive = false;
        _suppressJumpUntilUtc = DateTime.UtcNow.AddSeconds(30);
    }

    private async Task<bool> UpdateHostCalendarAsync(HostCalendarSample sample, long generation)
    {
        ProcessorFrameScope.ThrowIfAbandoned();
        var life = T3SideLease();
        if (!T3SideReady)
        {
            _t3SideChannels.Put(T3Clock, new(0, Calendar: sample, CalendarGeneration: generation), life);
            T3Increment("deferred_sidechannel"); return false;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ProcessorFrameScope.CurrentToken, life.Token);
        using var scope = ProcessorFrameScope.Link(linked.Token);
        await _hostCalendarGate.WaitAsync(linked.Token);
        try
        {
            T3SideGuard(life, true);
            if (!HostCalendarOwned || generation != Interlocked.Read(ref _hostCalendarGeneration) || !sample.Fresh(Environment.TickCount64)) return false;
            lock (_hostCalendarLock) if (_hostCalendar != sample) return false;
            // The native prehook leaves the player's own picker/sleep/wait alone. It retains the newest host target for the first frame after it.
            var r = await _combat.CalendarFollowAsync(1, sample.TargetMs(Environment.TickCount64), sample.Ratio, linked.Token);
            T3SideGuard(life, true);
            // The local pipe may reconnect while the relay and confirmed host world stay intact.
            // Native close clears its follower; only the already verified world may arm it again here.
            if (r is { Result: 0 } && generation == Interlocked.Read(ref _hostCalendarGeneration) &&
                _hostCalendarResumeEpoch != 0 && _hostCalendarResumeEpoch == _w206.World && _joinedWorld &&
                _jj is null && !LinkHeld && !OwnLoadExpected && !LoadGraceOwn)
            {
                if (!await ResumeHostAuthorityAsync(generation, linked.Token)) return false;
                T3SideGuard(life, true);
                r = await _combat.CalendarFollowAsync(2, sample.TargetMs(Environment.TickCount64), sample.Ratio, linked.Token);
                T3SideGuard(life, true);
            }
            for (int i = 0; r is { Result: 2 } && i < 3 && !_localSkipActive && !_w140LocalSkipping; i++)
            {
                if (generation != Interlocked.Read(ref _hostCalendarGeneration)) return false;
                await Task.Delay(50, linked.Token);
                T3SideGuard(life, true);
                r = await _combat.CalendarFollowAsync(3, ct: linked.Token);
                T3SideGuard(life, true);
            }
            if (r is not { Result: 1 } read || generation != Interlocked.Read(ref _hostCalendarGeneration)) return false;
            RebaseCalendarPoll(read.AfterMs, sample.Ratio);
            double target = sample.TargetMs(Environment.TickCount64) / 1000.0;
            Wo210OnHostClock((uint)target, sample.Ratio, read.AfterMs / 1000.0 - target, _localSkipActive || _w140LocalSkipping);
            return Math.Abs(read.AfterMs / 1000.0 - target) <= 2;
        }
        finally { _hostCalendarGate.Release(); }
    }

    /// <summary>Arm the native pre-update latch before issuing the load, never from GameplayStarted.</summary>
    private async Task<bool> PrepareHostCalendarAsync(JoinerJoin join)
    {
        var life = T3SideLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ProcessorFrameScope.CurrentToken, life.Token, _wo122Ct);
        using var scope = ProcessorFrameScope.Link(linked.Token);
        T3SideGuard(life);
        long generation = Interlocked.Read(ref _hostCalendarGeneration);
        bool waitingLogged = false;
        HostCalendarSample host;
        while (true)
        {
            if (!JoinCalendarCurrent(join, generation)) return false;
            Task changed;
            lock (_hostCalendarLock)
            {
                if (_hostCalendarBootstrap is { } sample && _hostCalendarBootstrapWorld == join.HostWorldEpoch &&
                    ReferenceEquals(_hostCalendarBootstrapStream, _wo122Stream) && sample.ReadyForJoin(Environment.TickCount64, join.JoinId))
                {
                    host = sample;
                    break;
                }
                changed = (_hostCalendarChanged ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
            if (!waitingLogged)
            {
                waitingLogged = true;
                SetJoinUi("preparing", "Waiting for your host's world clock...");
                Console.WriteLine($"MP-CALENDAR join 0x{join.JoinId:x8}: waiting for the frozen host snapshot before load");
            }
            // The host already owns the waiting-ready deadline; a host abort/leave completes Abandoned.
            await Task.WhenAny(changed, join.Abandoned.Task).WaitAsync(linked.Token);
            T3SideGuard(life);
        }
        await _hostCalendarGate.WaitAsync(linked.Token);
        try
        {
            T3SideGuard(life);
            if (!JoinCalendarCurrent(join, generation)) return false;
            if (LoadSequenceEnabled)
            {
                var load = _worldLoad.Current;
                if (load.Generation != join.LoadGeneration || load.Phase != WorldLoadPhase.Offer ||
                    !await LoadSequenceSyncNativeAsync() || !JoinCalendarCurrent(join, generation) ||
                    _worldLoad.Current is not { Phase: WorldLoadPhase.Offer } offer ||
                    offer.HostWorld != join.HostWorldEpoch || offer.OwnIntentRevision != load.OwnIntentRevision)
                {
                    DropJoinCalendar(join.Host, join.JoinId, "native-offer-not-confirmed");
                    return false;
                }
            }
            long pipeRevision = _combat.EdgeConnectionRevision;
            var r = await _combat.CalendarFollowAsync(2, host.TargetMs(Environment.TickCount64), host.Ratio, linked.Token);
            T3SideGuard(life);
            if (r is not { Result: 1 or 2 })
            {
                Console.WriteLine($"MP-CALENDAR join 0x{join.JoinId:x8}: native calendar latch refused -- load not issued");
                return false;
            }
            if (!JoinCalendarCurrent(join, generation) || pipeRevision != _combat.EdgeConnectionRevision)
            {
                DropJoinCalendar(join.Host, join.JoinId, "calendar-actuator-retired");
                return false;
            }
            join.CalendarGeneration = generation;
            join.CalendarPipeRevision = pipeRevision;
            _hostCalendarArmed = true;
            _reloadConvergeTarget = null;
            Console.WriteLine(FormattableString.Invariant($"MP-CALENDAR join 0x{join.JoinId:x8}: pre-update latch armed before load host_ms={host.TargetMs(Environment.TickCount64)} ratio={host.Ratio:F3}"));
            return true;
        }
        finally { _hostCalendarGate.Release(); }
    }

    /// <summary>Read the new load's latch before Ready; the menu's old calendar is not sufficient.</summary>
    private async Task<bool> VerifyHostCalendarAsync(JoinerJoin join)
    {
        var life = T3SideLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ProcessorFrameScope.CurrentToken, life.Token);
        using var scope = ProcessorFrameScope.Link(linked.Token);
        T3SideGuard(life);
        if (!JoinCalendarActuatorCurrent(join) || !await PrepareHostAuthorityAsync(join, linked.Token)) return false;
        T3SideGuard(life);
        await _hostCalendarGate.WaitAsync(linked.Token);
        try
        {
            T3SideGuard(life);
            if (!JoinCalendarActuatorCurrent(join)) return false;
            CalendarReadback? r = null;
            for (int i = 0; i < 5; i++)
            {
                if (!JoinCalendarActuatorCurrent(join)) return false;
                r = await _combat.CalendarFollowAsync(3, ct: linked.Token);
                T3SideGuard(life);
                if (r is { Result: 1 }) break;
                await Task.Delay(50, linked.Token);
                T3SideGuard(life);
            }
            lock (_joinGate)
            {
                if (r is not { Result: 1 } || !JoinCalendarActuatorCurrent(join) || join.Phase != "post-load") return false;
                HostCalendarSample? host;
                lock (_hostCalendarLock) host = _hostCalendar;
                RebaseCalendarPoll(r.Value.AfterMs, host?.Ratio ?? 0);
                _hostCalendarResumeEpoch = join.HostWorldEpoch;
                join.CalendarVerified = true;
            }
            Console.WriteLine(FormattableString.Invariant($"MP-CALENDAR join 0x{join.JoinId:x8}: new-load pre-update latch verified calendar_ms={r.Value.AfterMs} slews={_w200Slews}"));
            return true;
        }
        finally { _hostCalendarGate.Release(); }
    }

    private async Task ResetHostCalendarAsync(string why)
    {
        ProcessorFrameScope.ThrowIfAbandoned();
        T3SideChannelsInvalidate(why);
        var life = T3SideLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ProcessorFrameScope.CurrentToken, life.Token);
        using var scope = ProcessorFrameScope.Link(linked.Token);
        lock (_joinGate)
        lock (_hostCalendarLock)
        {
            Interlocked.Increment(ref _hostCalendarGeneration);   // in-flight old samples cannot re-arm after this reset
            _hostCalendarArmed = false;
            _hostCalendar = null;
            _hostCalendarBootstrap = null;
            _hostCalendarBootstrapWorld = 0;
            _hostCalendarBootstrapStream = null;
            _hostCalendarChanged?.TrySetResult();
            _hostCalendarChanged = null;
        }
        if (why != "role-change") _hostCalendarResumeEpoch = 0;
        _w210HostClock = null;
        await _hostCalendarGate.WaitAsync(linked.Token);
        try
        {
            await ResetHostAuthorityAsync(why, linked.Token);
            T3SideGuard(life);
            await _combat.CalendarFollowAsync(0, ct: linked.Token);
            T3SideGuard(life);
            _w200Ratio = WorldTimeRatio;
        }
        catch (Exception ex) { Console.WriteLine($"MP-CALENDAR reset ({why}): {ex.GetType().Name}"); }
        finally { _hostCalendarGate.Release(); }
    }

    private async Task ResumeHostCalendarAsync(HostCalendarSample sample, long generation)
    {
        var life = T3SideLease();
        if (!T3SideReady) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ProcessorFrameScope.CurrentToken, life.Token);
        using var scope = ProcessorFrameScope.Link(linked.Token);
        T3SideGuard(life, true);
        await _hostCalendarGate.WaitAsync(linked.Token);
        try
        {
            T3SideGuard(life, true);
            if (generation != Interlocked.Read(ref _hostCalendarGeneration) || _hostCalendarResumeEpoch != _w206.World ||
                !_joinedWorld || _isDamageAuthority || OwnLoadExpected || LoadGraceOwn || _jj is not null || !sample.Fresh(Environment.TickCount64)) return;
            if (!await ResumeHostAuthorityAsync(generation, linked.Token)) return;
            T3SideGuard(life, true);
            var r = await _combat.CalendarFollowAsync(2, sample.TargetMs(Environment.TickCount64), sample.Ratio, linked.Token);
            T3SideGuard(life, true);
            if (r is { Result: 1 or 2 } && generation == Interlocked.Read(ref _hostCalendarGeneration))
            {
                _hostCalendarArmed = true;
                Console.WriteLine($"MP-CALENDAR rearmed in confirmed host world epoch {_hostCalendarResumeEpoch}");
            }
        }
        finally { _hostCalendarGate.Release(); }
    }

    private async Task ApplyHostCalendarSkipEndAsync(byte source, uint worldTime, DateTime receivedUtc, CancellationToken ct)
    {
        using var scope = ProcessorFrameScope.Link(ct);
        var life = T3SideLease();
        using var context = new T3SideContext(this, life);
        T3SideGuard(life);
        long generation = Interlocked.Read(ref _hostCalendarGeneration);
        byte expected = _jj?.Host ?? (_hostModeKnown ? _hostModeFrom : (byte)0xFF);
        if (source != expected) return;
        long transit = (long)Math.Clamp((DateTime.UtcNow - receivedUtc).TotalMilliseconds, 0, HostCalendarSample.RunningFreshMs);
        var sample = new HostCalendarSample((long)worldTime * 1000, 15000, Environment.TickCount64, transit);
        lock (_hostCalendarLock)
        {
            if (generation != Interlocked.Read(ref _hostCalendarGeneration)) return;
            _hostCalendar = sample;
        }
        await UpdateHostCalendarAsync(sample, generation);
        T3SideGuard(life);
        if (!T3SideReady) return;
        await RequestNpcResyncAsync(NpcResyncReason.Sleep, _resyncStream, ct);
        T3SideGuard(life, true);
    }
}
