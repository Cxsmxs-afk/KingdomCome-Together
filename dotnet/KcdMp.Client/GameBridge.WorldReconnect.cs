// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using KcdMp.Wire;

namespace KcdMp.Client;

public partial class GameBridge
{
    private sealed record ReconnectHold(long Serial, WorldReconnectAttempt Attempt, PresentationSession? OldLink, bool NeedsBaseline, bool HostAbsent);
    private sealed record ReconnectReload(ReconnectHold Hold, WorldSyncScope Target);
    private sealed record ReconnectHost(long Serial, WorldReconnectAttempt Attempt, bool NeedsBaseline)
    {
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly object _worldReconnectGate = new();
    private readonly Guid _worldReconnectInstance = Guid.NewGuid();
    private WorldReconnectFacts _worldReconnectFacts = new();
    private PresentationSession? _worldReconnectLink;
    private ReconnectHold? _worldReconnectHold;
    private ReconnectReload? _worldReconnectReload;
    private ReconnectHost? _worldReconnectHost;
    private WorldSyncScope? _worldReconnectHostReturn;

    private string WorldReconnectEpochProof(string text, uint world)
    {
        var s = _worldLoad.Current;
        return s is { Phase: WorldLoadPhase.Play, Generation: > 0 } && s.HostWorld == world
            ? WorldReconnectFacts.EpochProof(text, _worldReconnectInstance, s.Generation) : text;
    }

    private void WorldReconnectBegin(long serial, WorldLoadSnapshot held, bool sameRelay)
    {
        if (!WorldRecoveryEnabled || !held.LinkHeld) return;
        var scope = new WorldSyncScope(held.HostWorld, held.Generation);
        if (!scope.IsValid) return;
        var now = Environment.TickCount64;
        ReconnectHold pending;
        lock (_worldReconnectGate)
        {
            var prior = _worldReconnectFacts.Proof;
            if (!_hostModeKnown || prior?.Source != _hostModeFrom || prior?.World != held.HostWorld) prior = null;
            bool hostAbsent = HostAbsentPause;
            pending = new(serial, new(scope, prior, now, waitForHost: hostAbsent), sameRelay ? null : _worldReconnectLink,
                WorldSyncEffective, hostAbsent);
            _worldReconnectHold = pending;
            _worldReconnectReload = null;
            _worldReconnectFacts = new();
        }
        WorldReconnectLog("joiner", serial, scope, "started", 0);
    }

    private void WorldReconnectObserveAtRead(PresentationSession session, int type, byte[] payload)
    {
        if (!ReferenceEquals(Volatile.Read(ref _presentationSession), session)) return;
        if (type == Protocol.Disconnect && payload.Length == 1) HostRestartObserveDisconnect(payload[0]);
        if (type == Protocol.Disconnect && payload.Length == 1 && _joinedWorld && _jj is null &&
            _hostModeKnown && payload[0] == _hostModeFrom && !_leaveInProgress && !_gameQuitting)
        {
            EnterHostAbsentPause("host-left");
            _ = WorldReconnectHoldPeerAsync();
            return;
        }
        if (!WorldRecoveryEnabled) return;
        if (type is not (Protocol.JoinStatusDown or Protocol.W203Down)) return;
        lock (_worldReconnectGate)
        {
            if (_worldReconnectHold is { OldLink: { } old } && ReferenceEquals(old, session)) return;
            if (!ReferenceEquals(_worldReconnectLink, session))
            {
                _worldReconnectLink = session;
                _worldReconnectFacts = new();
            }
            if (!_worldReconnectFacts.Observe(type, payload, _myGhostId)) return;
        }
        WorldReconnectTryResolve();
    }

    private async Task WorldReconnectHoldPeerAsync()
    {
        try { await Wo211TryHoldAsync("host-link-lost", sameRelay: true); }
        catch (Exception ex) { Console.WriteLine($"MP-WORLD-RECONNECT hold_failed={ex.GetType().Name}"); }
    }

    private WorldReconnectAttempt? WorldReconnectPending(long serial)
    {
        if (!WorldRecoveryEnabled || _worldLoad.Current is not { Phase: WorldLoadPhase.Play, LinkHeld: true } current) return null;
        lock (_worldReconnectGate) return _worldReconnectHold is { } hold && hold.Serial == serial &&
            hold.Attempt.Scope == new WorldSyncScope(current.HostWorld, current.Generation) ? hold.Attempt : null;
    }

    private void WorldReconnectTryResolve()
    {
        ReconnectHold pending;
        WorldReconnectProof proof;
        PresentationSession link;
        lock (_worldReconnectGate)
        {
            if (!WorldRecoveryEnabled || _w211LeaveNow || _worldReconnectHold is not { } h || _worldReconnectFacts.Proof is not { } p ||
                _worldReconnectLink is not { } l || !ReferenceEquals(Volatile.Read(ref _presentationSession), l) ||
                l.Lifetime.IsCancellationRequested) return;
            (pending, proof, link) = (h, p, l);
        }
        var decision = pending.Attempt.Decide(_worldLoad.Current, proof, Environment.TickCount64);
        if (decision == WorldReconnectDecision.DifferentWorld && (pending.HostAbsent || !T3HostWorldChanged || _w206.World != proof.World)) return;
        if (decision is WorldReconnectDecision.SameWorld or WorldReconnectDecision.DifferentWorld && pending.Attempt.TryClaim(decision))
            _ = WorldReconnectResolveAsync(pending, proof, link, decision);
    }

    private async Task WorldReconnectResolveAsync(ReconnectHold pending, WorldReconnectProof proof, PresentationSession link, WorldReconnectDecision decision)
    {
        bool same = decision == WorldReconnectDecision.SameWorld;
        bool moved = false;
        var scope = pending.Attempt.Scope;
        try
        {
            bool CurrentProof()
            {
                lock (_worldReconnectGate) return WorldRecoveryEnabled && !_w211LeaveNow && ReferenceEquals(_worldReconnectHold, pending) &&
                    ReferenceEquals(Volatile.Read(ref _presentationSession), link) && !link.Lifetime.IsCancellationRequested &&
                    ReferenceEquals(_worldReconnectLink, link) && _worldReconnectFacts.Proof == proof;
            }
            if (!CurrentProof() || pending.Attempt.Decide(_worldLoad.Current, proof, Environment.TickCount64) != decision) return;
            moved = await Wo211EndHoldAsync(pending.Serial, proof.World, resumed: same,
                same ? "the authenticated host returned to the retained world" : "the authenticated host now has a newer world",
                expectedGeneration: scope.Generation, reconnectProof: CurrentProof, notify: false);
            if (!moved) return;
            if (same)
            {
                if (pending.HostAbsent && _w211LeaveNow)
                {
                    WorldReconnectForget(pending);
                    await LeaveSharedWorldAsync("host-left", "You left your partner's world.");
                    return;
                }
                bool applied = await WorldReconnectConfirmUnparkAsync(pending, link);
                long elapsed = pending.Attempt.Elapsed(Environment.TickCount64);
                if (applied && CurrentProof() && (pending.HostAbsent || elapsed <= WorldReconnectAttempt.WindowMs))
                {
                    if (pending.HostAbsent) CompleteHostAbsentResume(proof.World, proof.Instance);
                    WorldReconnectFinish(pending, "same-world", elapsed, same: true, success: true);
                    Interlocked.Increment(ref _w211ResumedInPlace);
                    _ = ExecLuaAsync("KCD2MP_ShowNativeToast(\"Reconnected to your host.\")");
                    await Wo210RefreshAfterReconnectAsync();
                }
                else if (pending.HostAbsent)
                {
                    WorldReconnectForget(pending);
                    if (HostAbsentPause && !_w211LeaveNow && !_leaveInProgress)
                        await Wo211TryHoldAsync("host-return-unconfirmed", sameRelay: true);
                }
                else if (elapsed >= WorldReconnectAttempt.WindowMs) WorldReconnectFinish(pending, "timeout", elapsed, same: true, timeout: true);
                else WorldReconnectForget(pending);
                return;
            }
            var target = _worldLoad.Current;
            if (target.Phase != WorldLoadPhase.Offer || target.Generation == scope.Generation || target.HostWorld != proof.World)
            { WorldReconnectForget(pending); return; }
            lock (_worldReconnectGate)
            {
                if (!ReferenceEquals(_worldReconnectHold, pending)) return;
                _worldReconnectHold = null;
                _worldReconnectReload = new(pending, new(target.HostWorld, target.Generation));
            }
            WorldReconnectLog("joiner", pending.Serial, new(target.HostWorld, target.Generation), "different-world", pending.Attempt.Elapsed(Environment.TickCount64));
            Wo211ContinueWithNewWorld();
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException)
        {
            if (moved) WorldReconnectForget(pending);
        }
        finally
        {
            if (!moved) pending.Attempt.TryRelease(decision);
        }
    }

    private async Task<bool> WorldReconnectConfirmUnparkAsync(ReconnectHold pending, PresentationSession link)
    {
        long remaining = pending.Attempt.Remaining(Environment.TickCount64);
        if (remaining == 0) return false;
        var scope = pending.Attempt.Scope;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(link.Lifetime);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(remaining));
        bool nativeReady = false;
        try
        {
            while (WorldRecoveryEnabled && _worldLoad.CanApply(scope.Generation) && _worldLoad.Current.HostWorld == scope.HostWorld &&
                ReferenceEquals(Volatile.Read(ref _presentationSession), link))
            {
                if (!nativeReady)
                {
                    var ack = await _combat.LoadSequenceAsync(CombatPipe.LoadSequenceQuery, 0, 0, ct: deadline.Token);
                    nativeReady = ack is { Accepted: true, MainApplied: true, Flags: 0 } reply && reply.Generation == scope.Generation &&
                        reply.Phase == (byte)WorldLoadPhase.Play;
                }
                if (nativeReady && WorldReconnectGameplayReady(scope, link.Relay, pending.NeedsBaseline)) return true;
                await Task.Delay(50, deadline.Token);
            }
        }
        catch (OperationCanceledException) { }
        return false;
    }

    private bool WorldReconnectGameplayReady(WorldSyncScope scope, Stream stream, bool needsBaseline)
    {
        if (!_combatRoleApplyComplete) return false;
        if (!WorldSyncEffective) return !needsBaseline;
        return Volatile.Read(ref _worldReceiver) is { Installed: true } receiver && receiver.Scope == scope &&
            ReferenceEquals(receiver.Stream, stream) && WorldSyncReady;
    }

    private bool WorldReconnectTimeout(long serial, bool playerLeft)
    {
        ReconnectHold pending;
        lock (_worldReconnectGate)
        {
            if (_worldReconnectHold is not { } hold || hold.Serial != serial || !hold.Attempt.TryClaim(WorldReconnectDecision.Timeout)) return false;
            pending = hold;
        }
        long elapsed = pending.Attempt.Elapsed(Environment.TickCount64);
        if (playerLeft) WorldReconnectForget(pending);
        else WorldReconnectFinish(pending, "timeout", elapsed, timeout: true);
        return true;
    }

    private bool WorldReconnectForget(ReconnectHold pending)
    {
        lock (_worldReconnectGate)
        {
            if (!ReferenceEquals(_worldReconnectHold, pending)) return false;
            _worldReconnectHold = null;
            return true;
        }
    }

    private void WorldReconnectFinish(ReconnectHold pending, string result, long elapsed, bool same = false, bool success = false, bool timeout = false)
    {
        if (!WorldReconnectForget(pending)) return;
        var scope = pending.Attempt.Scope;
        WorldReconnectLog("joiner", pending.Serial, scope, result, elapsed);
        WorldRecoveryReconnectSample(scope.HostWorld, scope.Generation, elapsed, same, success, timeout, reloaded: false);
    }

    private void WorldReconnectNoteEngineLoad(ulong generation, uint world)
    {
        ReconnectReload pending;
        lock (_worldReconnectGate)
        {
            if (!WorldRecoveryEnabled || _worldReconnectReload is not { } reload || reload.Target != new WorldSyncScope(world, generation)) return;
            pending = reload;
            _worldReconnectReload = null;
        }
        long elapsed = pending.Hold.Attempt.Elapsed(Environment.TickCount64);
        WorldReconnectLog("joiner", pending.Hold.Serial, pending.Target, "reload", elapsed, reload: true);
        WorldRecoveryReconnectSample(world, generation, elapsed, sameWorld: false, success: false, timeout: false, reloaded: true);
    }

    private void WorldReconnectHostLost(CancellationToken lifetime)
    {
        if (!_isDamageAuthority || !_sharedWorld) return;
        var s = _worldLoad.Current;
        var scope = new WorldSyncScope(s.HostWorld, s.Generation);
        if (!scope.IsValid || s.Phase != WorldLoadPhase.Play || !_worldLoad.CanApply(s.Generation)) return;
        ReconnectHost pending;
        lock (_worldReconnectGate)
        {
            _worldReconnectHostReturn = scope;
            if (!WorldRecoveryEnabled || _worldReconnectHost is not null) return;
            pending = new(Interlocked.Increment(ref _w211HoldSerial), new(scope, null, Environment.TickCount64), WorldSyncEffective);
            _worldReconnectHost = pending;
        }
        WorldReconnectLog("host", pending.Serial, scope, "started", 0);
        _ = WorldReconnectHostDeadlineAsync(pending, lifetime);
    }

    private bool WorldReconnectCanAnnounceReturningHost()
    {
        if (!config.IsHosting || !_sharedWorld || _joinedWorld || _leaveInProgress || _gameQuitting) return false;
        var state = _worldLoad.Current;
        if (state.Phase != WorldLoadPhase.Play || !_worldLoad.CanApply(state.Generation) || _w206.World != state.HostWorld)
            return false;
        lock (_worldReconnectGate)
            return _worldReconnectHostReturn is { } scope && scope == new WorldSyncScope(state.HostWorld, state.Generation);
    }

    private async Task WorldReconnectHostDeadlineAsync(ReconnectHost pending, CancellationToken lifetime)
    {
        try { await pending.Finished.Task.WaitAsync(TimeSpan.FromMilliseconds(WorldReconnectAttempt.WindowMs + 1), lifetime); }
        catch (TimeoutException) { WorldReconnectHostFinish(pending, success: false); }
        catch (OperationCanceledException) { return; }
    }

    private void WorldReconnectHostRoleConfirmed()
    {
        ReconnectHost? pending;
        bool returned;
        lock (_worldReconnectGate)
        {
            returned = _isDamageAuthority && _worldReconnectHostReturn is not null;
            if (returned) _worldReconnectHostReturn = null;
            pending = _worldReconnectHost;
        }
        if (returned || _isDamageAuthority && Interlocked.Exchange(ref _hostRestartRequestedAt, 0) != 0)
            _ = Wo206AnnounceAsync("host-return");
        if (pending is null || !_isDamageAuthority || !_sharedWorld || !_combatRoleApplyComplete ||
            _wo121Stream is not { } stream || !pending.Attempt.TryClaim(WorldReconnectDecision.SameWorld)) return;
        _ = WorldReconnectHostReadyAsync(pending, stream, _wo121Ct);
    }

    private async Task WorldReconnectHostReadyAsync(ReconnectHost pending, Stream stream, CancellationToken ct)
    {
        var scope = pending.Attempt.Scope;
        try
        {
            while (WorldRecoveryEnabled && _isDamageAuthority && _sharedWorld && _worldLoad.Current.HostWorld == scope.HostWorld &&
                _worldLoad.CanApply(scope.Generation) && ReferenceEquals(_wo121Stream, stream) &&
                pending.Attempt.Elapsed(Environment.TickCount64) <= WorldReconnectAttempt.WindowMs)
            {
                if (WorldReconnectGameplayReady(scope, stream, pending.NeedsBaseline))
                { WorldReconnectHostFinish(pending, success: true); return; }
                await Task.Delay(50, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally { pending.Attempt.TryRelease(WorldReconnectDecision.SameWorld); }
    }

    private void WorldReconnectHostFinish(ReconnectHost pending, bool success)
    {
        lock (_worldReconnectGate)
        {
            if (!ReferenceEquals(_worldReconnectHost, pending)) return;
            _worldReconnectHost = null;
            pending.Finished.TrySetResult();
        }
        if (!WorldRecoveryEnabled) return;
        var scope = pending.Attempt.Scope;
        long elapsed = pending.Attempt.Elapsed(Environment.TickCount64);
        success = success && elapsed <= WorldReconnectAttempt.WindowMs;
        WorldReconnectLog("host", pending.Serial, scope, success ? "same-world" : "timeout", elapsed);
        WorldRecoveryReconnectSample(scope.HostWorld, scope.Generation, elapsed, sameWorld: success, success: success, timeout: !success, reloaded: false);
    }

    private void WorldReconnectLog(string role, long serial, WorldSyncScope scope, string result, long elapsed, bool reload = false)
    {
        Console.WriteLine(FormattableString.Invariant($"MP-WORLD-RECONNECT role={role} attempt={serial} world={scope.HostWorld} gen={scope.Generation} result={result} reconnect_ms={elapsed} reload={(reload ? 1 : 0)}"));
        GuidedRunObserveReconnect(role, serial, scope, result, elapsed, reload);
    }
}
