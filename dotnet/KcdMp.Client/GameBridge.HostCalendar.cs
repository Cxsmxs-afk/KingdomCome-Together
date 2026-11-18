// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Globalization;
using KcdMp.Wire;

namespace KcdMp.Client;

public partial class GameBridge
{
    private readonly SemaphoreSlim _hostCalendarGate = new(1, 1);
    private readonly SemaphoreSlim _hostCalendarSendGate = new(1, 1);
    private readonly object _hostCalendarLock = new();
    private HostCalendarSample? _hostCalendar;
    private HostCalendarSample? _hostCalendarBootstrap;
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
            string text = HostCalendar.Format(clock.WorldMs, rate, CalendarRelayNowMs, _clockOffsetMs.HasValue, joinId != 0 ? joinId : _hostJoin?.JoinId ?? 0);
            if (target is byte one) { T3SideGuard(life); await W164SendAsync(one, Protocol.W164Clock, text, linked.Token); T3SideGuard(life); }
            else foreach (byte peer in Wo134Peers()) { T3SideGuard(life); await W164SendAsync(peer, Protocol.W164Clock, text, linked.Token); T3SideGuard(life); }
            Interlocked.Increment(ref _w200ClockOut);
            return true;
        }
        finally { _hostCalendarSendGate.Release(); }
    }

    private (HostCalendarSample Sample, long Generation)? CacheHostCalendar(byte source, string text)
    {
        long generation = Interlocked.Read(ref _hostCalendarGeneration);
        byte expected = _jj?.Host ?? (_hostModeKnown ? _hostModeFrom : (byte)0xFF);
        if (_isDamageAuthority || expected == 0xFF || source != expected ||
            !HostCalendar.TryParse(text, Environment.TickCount64, CalendarRelayNowMs, _clockOffsetMs.HasValue,
                (_clockRttMedianMs ?? 0) / 2, out var sample)) return null;
        lock (_hostCalendarLock)
        {
            if (generation != Interlocked.Read(ref _hostCalendarGeneration)) return null;
            _hostCalendar = sample;   // kept at the menu, before the first host-world frame
            // A pre-hold periodic read can finish sending after the frozen offer sample.
            // Only the frozen snapshot may own this attempt's bootstrap, even when that older read carries its JoinId.
            if (sample.IsJoinSnapshot) _hostCalendarBootstrap = sample;
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

    private async Task UpdateHostCalendarAsync(HostCalendarSample sample, long generation)
    {
        ProcessorFrameScope.ThrowIfAbandoned();
        var life = T3SideLease();
        if (!T3SideReady)
        {
            _t3SideChannels.Put(T3Clock, new(0, Calendar: sample, CalendarGeneration: generation), life);
            T3Increment("deferred_sidechannel"); return;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ProcessorFrameScope.CurrentToken, life.Token);
        using var scope = ProcessorFrameScope.Link(linked.Token);
        await _hostCalendarGate.WaitAsync(linked.Token);
        try
        {
            T3SideGuard(life, true);
            if (!HostCalendarOwned || generation != Interlocked.Read(ref _hostCalendarGeneration) || !sample.Fresh(Environment.TickCount64)) return;
            lock (_hostCalendarLock) if (_hostCalendar != sample) return;
            // The native prehook leaves the player's own picker/sleep/wait alone. It retains the newest host target for the first frame after it.
            var r = await _combat.CalendarFollowAsync(1, sample.TargetMs(Environment.TickCount64), sample.Ratio, linked.Token);
            T3SideGuard(life, true);
            // The local pipe may reconnect while the relay and confirmed host world stay intact.
            // Native close clears its follower; only the already verified world may arm it again here.
            if (r is { Result: 0 } && generation == Interlocked.Read(ref _hostCalendarGeneration) &&
                _hostCalendarResumeEpoch != 0 && _hostCalendarResumeEpoch == _w206.World && _joinedWorld &&
                _jj is null && !_w211Holding && !_ownLoadExpected && !_loadGraceOwn)
            {
                if (!await ResumeHostAuthorityAsync(generation, linked.Token)) return;
                T3SideGuard(life, true);
                r = await _combat.CalendarFollowAsync(2, sample.TargetMs(Environment.TickCount64), sample.Ratio, linked.Token);
                T3SideGuard(life, true);
            }
            for (int i = 0; r is { Result: 2 } && i < 3 && !_localSkipActive && !_w140LocalSkipping; i++)
            {
                if (generation != Interlocked.Read(ref _hostCalendarGeneration)) return;
                await Task.Delay(50, linked.Token);
                T3SideGuard(life, true);
                r = await _combat.CalendarFollowAsync(3, ct: linked.Token);
                T3SideGuard(life, true);
            }
            if (r is not { Result: 1 } read || generation != Interlocked.Read(ref _hostCalendarGeneration)) return;
            RebaseCalendarPoll(read.AfterMs, sample.Ratio);
            double target = sample.TargetMs(Environment.TickCount64) / 1000.0;
            Wo210OnHostClock((uint)target, sample.Ratio, read.AfterMs / 1000.0 - target, _localSkipActive || _w140LocalSkipping);
        }
        finally { _hostCalendarGate.Release(); }
    }

    /// <summary>Arm the native pre-update latch before issuing the load, never from GameplayStarted.</summary>
    private async Task<bool> PrepareHostCalendarAsync(JoinerJoin join)
    {
        var life = T3SideLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ProcessorFrameScope.CurrentToken, life.Token);
        using var scope = ProcessorFrameScope.Link(linked.Token);
        T3SideGuard(life);
        long until = Environment.TickCount64 + 5000;
        HostCalendarSample? sample;
        do
        {
            lock (_hostCalendarLock) sample = _hostCalendarBootstrap;
            if (sample is { } got && got.ReadyForJoin(Environment.TickCount64, join.JoinId)) break;
            if (!ReferenceEquals(_jj, join) || join.Abandoned.Task.IsCompleted) return false;
            await Task.Delay(50, linked.Token);
            T3SideGuard(life);
        } while (Environment.TickCount64 < until);
        if (sample is not { } host || !host.ReadyForJoin(Environment.TickCount64, join.JoinId))
        {
            Console.WriteLine($"MP-CALENDAR join 0x{join.JoinId:x8}: no current host calendar -- load not issued");
            return false;
        }
        long generation = Interlocked.Read(ref _hostCalendarGeneration);
        await _hostCalendarGate.WaitAsync(linked.Token);
        try
        {
            T3SideGuard(life);
            if (generation != Interlocked.Read(ref _hostCalendarGeneration) || !ReferenceEquals(_jj, join) || join.Abandoned.Task.IsCompleted) return false;
            lock (_hostCalendarLock) if (_hostCalendarBootstrap is { } latest && latest.ReadyForJoin(Environment.TickCount64, join.JoinId)) host = latest;
            var r = await _combat.CalendarFollowAsync(2, host.TargetMs(Environment.TickCount64), host.Ratio, linked.Token);
            T3SideGuard(life);
            if (r is not { Result: 1 or 2 }) return false;
            if (generation != Interlocked.Read(ref _hostCalendarGeneration) || !ReferenceEquals(_jj, join) || join.Abandoned.Task.IsCompleted)
            {
                return false;
            }
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
        if (!await PrepareHostAuthorityAsync(join, linked.Token)) return false;
        T3SideGuard(life);
        long generation = Interlocked.Read(ref _hostCalendarGeneration);
        await _hostCalendarGate.WaitAsync(linked.Token);
        try
        {
            T3SideGuard(life);
            if (!HostCalendarOwned || generation != Interlocked.Read(ref _hostCalendarGeneration) || !ReferenceEquals(_jj, join) || join.Abandoned.Task.IsCompleted) return false;
            CalendarReadback? r = null;
            for (int i = 0; i < 5; i++)
            {
                r = await _combat.CalendarFollowAsync(3, ct: linked.Token);
                T3SideGuard(life);
                if (r is { Result: 1 } || generation != Interlocked.Read(ref _hostCalendarGeneration) || !ReferenceEquals(_jj, join) || join.Abandoned.Task.IsCompleted) break;
                await Task.Delay(50, linked.Token);
                T3SideGuard(life);
            }
            if (r is not { Result: 1 } read || generation != Interlocked.Read(ref _hostCalendarGeneration) || !ReferenceEquals(_jj, join) || join.Abandoned.Task.IsCompleted) return false;
            HostCalendarSample? host;
            lock (_hostCalendarLock) host = _hostCalendar;
            RebaseCalendarPoll(read.AfterMs, host?.Ratio ?? 0);
            _hostCalendarResumeEpoch = _w206.World;
            Console.WriteLine(FormattableString.Invariant($"MP-CALENDAR join 0x{join.JoinId:x8}: new-load pre-update latch verified calendar_ms={read.AfterMs} slews={_w200Slews}"));
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
        Interlocked.Increment(ref _hostCalendarGeneration);   // in-flight old samples cannot re-arm after this reset
        _hostCalendarArmed = false;
        await ResetHostAuthorityAsync(why, linked.Token);
        T3SideGuard(life);
        if (why != "role-change") _hostCalendarResumeEpoch = 0;
        lock (_hostCalendarLock) { _hostCalendar = null; _hostCalendarBootstrap = null; }
        _w210HostClock = null;
        await _hostCalendarGate.WaitAsync(linked.Token);
        try
        {
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
                !_joinedWorld || _isDamageAuthority || _ownLoadExpected || _loadGraceOwn || _jj is not null || !sample.Fresh(Environment.TickCount64)) return;
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
