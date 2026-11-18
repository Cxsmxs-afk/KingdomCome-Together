// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using KcdMp.Wire;

namespace KcdMp.Client;

public partial class GameBridge
{
    private sealed record TpConnection(Stream Stream, CancellationToken Token);
    private sealed record TpContext(TpConnection Connection, uint World, ulong Generation, long Serial, long LifeRevision, CancellationTokenSource Life)
    { public CancellationToken Token { get; } = Life.Token; }
    private sealed record TpTarget(float X, float Y, float Z, long SourceAt, TpContext Context, Func<bool>? CurrentOperation = null);
    private sealed record TpPending(ulong Nonce, long SourceAt, TpContext Context, TaskCompletionSource<TpTarget?> Reply);
    private readonly object _t3TpGate = new();
    private TpConnection? _t3TpConnection;
    private TpContext? _t3TpContext;
    private readonly Dictionary<byte, TpPending> _t3TpPending = new();
    private readonly Dictionary<byte, TpTarget> _t3TpCache = new();
    private readonly HashSet<byte> _t3TpResponding = new();
    private long _t3TpNonce, _t3TpOwnAt, _t3TpHostRefreshAt, _t3TpHistorySerial = -1;
    private byte _t3TpOwnFlags;
    private bool T3TpOwnCombat => Environment.TickCount64 - Interlocked.Read(ref _t3TpOwnAt) <= 2000 && (_t3TpOwnFlags & 1) != 0;
    private bool T3TpOwnKo => Environment.TickCount64 - Interlocked.Read(ref _t3TpOwnAt) <= 2000 && (_t3TpOwnFlags & 6) != 0;
    private void T3TpOwnSample(TeleportSnapshot? sample)
    {
        if (sample is not { Known: 15 } s || s.Generation != P1Lifecycle.Current.Generation) return;
        _t3TpOwnFlags = s.Flags; Interlocked.Exchange(ref _t3TpOwnAt, (long)Math.Min((ulong)Environment.TickCount64, s.CapturedTick64));
    }
    private readonly SemaphoreSlim _t3TpPrepare = new(1, 1);

    private TpConnection T3TpBeginReceive(Stream stream, CancellationToken ct)
    {
        T3TpInvalidate();
        lock (_t3TpGate) return _t3TpConnection = new(stream, ct);
    }

    private void T3TpEndReceive(TpConnection connection)
    {
        lock (_t3TpGate)
        {
            if (!ReferenceEquals(_t3TpConnection, connection)) return;
            _t3TpConnection = null; T3TpInvalidate();
        }
    }

    private void T3TpInvalidate()
    {
        lock (_t3TpGate)
        {
            if (_t3TpContext is { } old)
                _ = old.Life.CancelAsync().ContinueWith(t => { _ = t.Exception; old.Life.Dispose(); }, TaskScheduler.Default);
            _t3TpContext = null;
            foreach (var p in _t3TpPending.Values) p.Reply.TrySetResult(null);
            _t3TpPending.Clear(); _t3TpCache.Clear();
        }
    }

    private bool T3TpCurrent(TpContext context) => !context.Life.IsCancellationRequested &&
        ReferenceEquals(_t3TpConnection, context.Connection) && !context.Connection.Token.IsCancellationRequested &&
        context.World != 0 && context.World == _w206.World && context.Generation == P1Lifecycle.Current.Generation &&
        P1Lifecycle.Current.Confirmed && P1Lifecycle.Current.GameplaySent &&
        context.Serial == Interlocked.Read(ref _p1Serial) && context.LifeRevision == Interlocked.Read(ref _t3InputLifeRevision) && !_p1Loading && !_leaveInProgress && _where == GameWhere.World;

    private TpContext? T3TpGetContext(uint world)
    {
        lock (_t3TpGate)
        {
            if (world == 0 || world != _w206.World || _t3TpConnection is not { } connection || connection.Token.IsCancellationRequested ||
                P1Lifecycle.Current.Generation == 0 || !P1Lifecycle.Current.Confirmed || !P1Lifecycle.Current.GameplaySent || _p1Loading || _where != GameWhere.World || _leaveInProgress) return null;
            if (_t3TpContext is { } prior && T3TpCurrent(prior)) return prior;
            T3TpInvalidate();
            return _t3TpContext = new(connection, world, P1Lifecycle.Current.Generation, Interlocked.Read(ref _p1Serial), Interlocked.Read(ref _t3InputLifeRevision),
                CancellationTokenSource.CreateLinkedTokenSource(connection.Token));
        }
    }

    private TeleportProof T3TpProof(TpTarget target, TeleportSource source) => new(source, target.SourceAt + 2000,
        target.Context.Generation, () => T3TpCurrent(target.Context) && (target.CurrentOperation?.Invoke() ?? true));

    private Func<bool> T3TpOperationGuard()
    {
        var connection = _t3TpConnection; uint world = _w206.World;
        ulong generation = P1Lifecycle.Current.Generation;
        long serial = Interlocked.Read(ref _p1Serial), life = Interlocked.Read(ref _t3InputLifeRevision);
        return () => connection is not null && ReferenceEquals(_t3TpConnection, connection) && !connection.Token.IsCancellationRequested &&
            world != 0 && world == _w206.World && generation == P1Lifecycle.Current.Generation && serial == Interlocked.Read(ref _p1Serial) &&
            life == Interlocked.Read(ref _t3InputLifeRevision) && !_p1Loading && !_leaveInProgress && _where == GameWhere.World;
    }

    private async Task<bool> T3TpPrepareAsync(CancellationToken ct)
    {
        if (!_isDamageAuthority && !_joinedWorld && _jj?.Phase != "post-load") return false;
        if (_p1Loading || _where != GameWhere.World) return false;
        if (_isDamageAuthority && _p1GameplayMs < 0 && _transport is LogTailGameTransport tail)
        {
            var historyGuard = T3TpOperationGuard();
            long historySerial = Interlocked.Read(ref _p1Serial);
            if (Interlocked.Exchange(ref _t3TpHistorySerial, historySerial) != historySerial)
            {
                bool ready = await Task.Run(() => GameplayReadyHistory.Scan(tail.LogPath), ct);
                if (ready && historyGuard() && historySerial == Interlocked.Read(ref _p1Serial))
                { _p1GameplayMs = Environment.TickCount64; _p1GameplayWorld = _w206.World; }
            }
        }
        if (!P1HasGameplay) return false;
        await _t3TpPrepare.WaitAsync(ct);
        try
        {
            long serial = Interlocked.Read(ref _p1Serial);
            if (_isDamageAuthority && P1Lifecycle.Current.Confirmed && Environment.TickCount64 >= _t3TpHostRefreshAt)
            {
                _t3TpHostRefreshAt = Environment.TickCount64 + 1000;
                if (!await P1Lifecycle.RefreshAsync(ct))
                {
                    if (!P1Lifecycle.Current.Confirmed)
                    {
                        T3TpInvalidate(); Interlocked.Increment(ref _p1Serial);
                        _p1GameplayMs = -1; _p1GameplayWorld = 0;
                    }
                    return false;
                }
            }
            if (!P1Lifecycle.Current.Confirmed || P1Lifecycle.Current.Generation == 0)
                if (!await P1Lifecycle.PendingAsync(P1JoinerIntent && _hostAuthorityEnabled, false, ct)) return false;
            if (_p1Loading || !P1HasGameplay || serial != Interlocked.Read(ref _p1Serial)) return false;
            if (!P1Lifecycle.Current.GameplaySent && !await P1Lifecycle.GameplayAsync(P1Lifecycle.Current.Generation, ct)) return false;
            return !_p1Loading && P1HasGameplay && serial == Interlocked.Read(ref _p1Serial);
        }
        finally { _t3TpPrepare.Release(); }
    }

    private async Task<TpTarget?> T3TpPeerAsync(byte peer, TeleportSource source, uint world, CancellationToken ct = default)
    {
        using var caller = ProcessorFrameScope.Link(ct);
        var origin = T3TpOperationGuard();
        if (!await T3TpPrepareAsync(caller.Token) || !origin()) { T3Increment("tp_snapshot_refused"); return null; }
        TpPending pending; bool created;
        lock (_t3TpGate)
        {
            var context = T3TpGetContext(world);
            if (context is null || peer == _myGhostId || peer == 0xFF) return null;
            if (_t3TpCache.TryGetValue(peer, out var cached) && T3TpCurrent(cached.Context) && Environment.TickCount64 - cached.SourceAt < 1000) return cached;
            if (_t3TpPending.TryGetValue(peer, out pending!) && T3TpCurrent(pending.Context)) created = false;
            else
            {
                if (_t3TpPending.Count >= 4) return null;
                pending = new(unchecked((ulong)Interlocked.Increment(ref _t3TpNonce)), Environment.TickCount64, context,
                    new(TaskCreationOptions.RunContinuationsAsynchronously));
                _t3TpPending[peer] = pending; created = true;
            }
        }
        if (created)
        {
            void Start() => _ = Task.Run(() => T3TpChallengeAsync(peer, source, pending));
            if (ExecutionContext.IsFlowSuppressed()) Start(); else { using (ExecutionContext.SuppressFlow()) Start(); }
        }
        return await pending.Reply.Task.WaitAsync(caller.Token);
    }

    private async Task T3TpChallengeAsync(byte peer, TeleportSource source, TpPending pending)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(pending.Context.Token);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, pending.SourceAt + 2000 - Environment.TickCount64)));
            await T3TpSendAsync(pending.Context, peer, Protocol.W203TpSnapshotRequest,
                TeleportSnapshotCodec.Build(new TeleportChallenge(pending.Nonce, pending.Context.World, source)), deadline.Token);
            await pending.Reply.Task.WaitAsync(deadline.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            if (T3TpCurrent(pending.Context) && Environment.TickCount64 >= pending.SourceAt + 2000) T3Increment("tp_snapshot_timeouts");
            else T3Increment("tp_snapshot_refused");
        }
        finally
        {
            lock (_t3TpGate)
                if (_t3TpPending.TryGetValue(peer, out var current) && ReferenceEquals(current, pending)) _t3TpPending.Remove(peer);
            pending.Reply.TrySetResult(null);
        }
    }

    private Task T3TpSendAsync(TpContext context, byte peer, byte kind, string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!T3TpCurrent(context)) throw new OperationCanceledException("teleport snapshot world changed", ct);
        return WritePacketAsync(context.Connection.Stream, new LootMsg(kind, 0, text).BuildUp(Protocol.W203Up, peer), ct);
    }

    // Reader only: no game/HTTP awaits. An unsolicited or old nonce can never refresh the cache.
    private bool T3TpTakeAtRead(int type, byte[] payload, uint stamp)
    {
        if (type == Protocol.Disconnect) { T3TpInvalidate(); return false; }
        if (type != Protocol.W203Down || payload.Length < 1 + Protocol.JoinHeaderLen || !LootMsg.TryDecode(payload.AsSpan(1 + Protocol.JoinHeaderLen), out var m) ||
            m.Kind is not (Protocol.W203TpSnapshotRequest or Protocol.W203TpSnapshotResponse)) return false;
        byte peer = payload[0];
        if (m.Kind == Protocol.W203TpSnapshotResponse)
        {
            if (!TeleportSnapshotCodec.TryAnswer(m.Text, out var answer)) return true;
            lock (_t3TpGate)
            {
                if (!_t3TpPending.TryGetValue(peer, out var pending) || answer.Nonce != pending.Nonce || stamp != pending.Context.World ||
                    answer.World != pending.Context.World || !T3TpCurrent(pending.Context)) return true;
                TpTarget? target = null;
                if (answer.Ok && answer.Known == 15 && answer.Flags == 0 && Environment.TickCount64 <= pending.SourceAt + 2000)
                {
                    target = new(answer.X, answer.Y, answer.Z, pending.SourceAt, pending.Context);
                    _t3TpCache[peer] = target;
                }
                if (target is null) T3Increment("tp_snapshot_refused");
                pending.Reply.TrySetResult(target);
            }
            return true;
        }
        if (!TeleportSnapshotCodec.TryChallenge(m.Text, out var challenge) || stamp == 0 || stamp != challenge.World || stamp != _w206.World) return true;
        TpContext? context = T3TpGetContext(challenge.World);
        if (context is null) return true;
        lock (_t3TpGate) if (_t3TpResponding.Count >= 4 || !_t3TpResponding.Add(peer)) return true;
        void Start() => _ = Task.Run(() => T3TpRespondAsync(context, peer, challenge));
        if (ExecutionContext.IsFlowSuppressed()) Start(); else { using (ExecutionContext.SuppressFlow()) Start(); }
        return true;
    }

    private async Task T3TpRespondAsync(TpContext context, byte peer, TeleportChallenge challenge)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.Token); deadline.CancelAfter(1800);
            var sample = await _combat.ReadTeleportSnapshotAsync(context.Generation, deadline.Token);
            if (!T3TpCurrent(context)) return;
            T3TpOwnSample(sample);
            bool ok = sample is { Safe: true };
            var s = sample.GetValueOrDefault();
            await T3TpSendAsync(context, peer, Protocol.W203TpSnapshotResponse,
                TeleportSnapshotCodec.Build(new TeleportAnswer(challenge.Nonce, challenge.World, ok, s.X, s.Y, s.Z, s.Known, s.Flags)), deadline.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
        finally { lock (_t3TpGate) _t3TpResponding.Remove(peer); }
    }

    private async Task<TpTarget?> T3TpLocalAsync(CancellationToken ct = default)
    {
        try
        {
            var origin = T3TpOperationGuard();
            if (!await T3TpPrepareAsync(ct) || !origin()) return null;
            var context = T3TpGetContext(_w206.World); if (context is null) return null;
            long start = Environment.TickCount64;
            using var life = CancellationTokenSource.CreateLinkedTokenSource(ct, context.Token); life.CancelAfter(2000);
            var sample = await _combat.ReadTeleportSnapshotAsync(context.Generation, life.Token);
            if (T3TpCurrent(context)) T3TpOwnSample(sample);
            return sample is { Safe: true } s && T3TpCurrent(context) && Environment.TickCount64 <= start + 2000 ? new(s.X, s.Y, s.Z, start, context) : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    private async Task<CombatPipe.JoinPlaceReport?> T3TpPlaceAsync(TpTarget target, float dist, TeleportSource source, CancellationToken ct = default)
    {
        try
        {
            using var life = CancellationTokenSource.CreateLinkedTokenSource(ct, target.Context.Token);
            var proof = T3TpProof(target, source);
            if (!proof.Valid || _localDowned) return null;
            return await _combat.JoinPlaceAsync(target.X, target.Y, target.Z, dist, proof, life.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }
}
