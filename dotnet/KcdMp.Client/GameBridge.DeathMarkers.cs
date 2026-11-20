// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using KcdMp.Wire;
using System.Collections.Concurrent;
namespace KcdMp.Client;

public partial class GameBridge
{
    private readonly SemaphoreSlim _p5MarkerGate = new(1, 1);
    private readonly DeathMarkerRevisions _p5OwnRevisions = new(), _p5PeerRevisions = new();
    private ulong _p5ScopeGeneration;
    private uint _p5ScopeWorld;
    private bool _p5TypedMarkers;
    private int _p5ReannounceBusy;
    private long _p5ScopeSerial;
    private CancellationTokenSource? _p5MarkerLife, _p5LoadLife;
    private CancellationToken _p5MarkerToken;
    private void P5RetireOperations(bool cancelLoad = true)
    {
        Interlocked.Increment(ref _p5ScopeSerial); _p5ScopeGeneration = 0;
        if (cancelLoad)
        {
            var oldLoad = Interlocked.Exchange(ref _p5LoadLife, null);
            if (oldLoad is not null) _ = oldLoad.CancelAsync().ContinueWith(t => { _ = t.Exception; oldLoad.Dispose(); }, TaskScheduler.Default);
        }
        var old = Interlocked.Exchange(ref _p5MarkerLife, null);
        if (old is not null) _ = old.CancelAsync().ContinueWith(t => { _ = t.Exception; old.Dispose(); }, TaskScheduler.Default);
    }
    private string _p5LoadRefusal = "";
    private bool _p5LoadToPrivate, _p5RebindAfterLoad;
    private sealed record LocalMarkerWork(NativeDeathMarker Marker, Stream Stream, CancellationToken Token);
    private readonly ConcurrentDictionary<ulong, LocalMarkerWork> _p5LocalPending = new();
    private int _p5LocalWorker;
    private sealed record MarkerPeer(long Serial, bool Connected, CancellationTokenSource Life)
    { public CancellationToken Token { get; } = Life.Token; }
    private readonly ConcurrentDictionary<byte, MarkerPeer> _p5Peers = new();
    private long _p5PeerSerial;
    private long P5PeerAtRead(int type, byte[] payload)
    {
        if (payload.Length == 0) return 0;
        byte owner = payload[0];
        if (type is Protocol.Name or Protocol.Disconnect)
        {
            var next = new MarkerPeer(Interlocked.Increment(ref _p5PeerSerial), type == Protocol.Name, new());
            _p5Peers.AddOrUpdate(owner, next, (peerId, old) =>
            { _ = old.Life.CancelAsync().ContinueWith(t => { _ = t.Exception; old.Life.Dispose(); }, TaskScheduler.Default); return next; });
        }
        if (type is not (Protocol.DeathMarkerDown or Protocol.GraveAddDown or Protocol.GraveRemoveDown)) return 0;
        return _p5Peers.GetOrAdd(owner, _ => new(Interlocked.Increment(ref _p5PeerSerial), true, new())).Serial;
    }
    private void P5QueueLocalMarker(NativeDeathMarker marker, Stream stream, CancellationToken ct)
    {
        if (marker.Generation != P1Lifecycle.Current.Generation || marker.Fact.World != P5LoadedWorld ||
            !_p5OwnRevisions.Observe(_myGhostId, marker.Fact)) return;
        if (_p5LocalPending.Count >= 4096 && !_p5LocalPending.ContainsKey(marker.Fact.Id))
        { Console.WriteLine("P5-MARKER notice queue full; retained native descriptor will be repaired by the next snapshot"); return; }
        _p5LocalPending.AddOrUpdate(marker.Fact.Id, new LocalMarkerWork(marker, stream, ct), (_, old) => old.Marker.Fact.Revision > marker.Fact.Revision ? old : new(marker, stream, ct));
        P5StartLocalWorker();
    }
    private void P5StartLocalWorker()
    {
        if (Interlocked.CompareExchange(ref _p5LocalWorker, 1, 0) != 0) return;
        void Start() => _ = Task.Run(async () =>
        {
            try
            {
                while (_p5LocalPending.Keys.FirstOrDefault() is ulong id && id != 0 && _p5LocalPending.TryRemove(id, out var work))
                    await P5LocalMarkerAsync(work.Marker, work.Stream, work.Token);
            }
            finally { Interlocked.Exchange(ref _p5LocalWorker, 0); if (!_p5LocalPending.IsEmpty) P5StartLocalWorker(); }
        });
        if (ExecutionContext.IsFlowSuppressed()) Start(); else { using (ExecutionContext.SuppressFlow()) Start(); }
    }
    private uint P5LoadedWorld => _combatRoleApplied && _where == GameWhere.World && !OwnLoadExpected && !LoadGraceOwn && (!LoadSequenceEnabled || !LinkHeld)
        ? _isDamageAuthority && _sharedWorld ? _w206.World
          : _joinedWorld ? _jj is { InWorld: true } j ? j.HostWorldEpoch : _t3JoinedHostWorld : 0 : 0;

    private Func<bool> P5Fence(Stream stream, CancellationToken token, uint world, ulong generation, long serial) =>
        () => !token.IsCancellationRequested && ReferenceEquals(stream, _wo122Stream) && world != 0 && world == P5LoadedWorld &&
            generation == P1Lifecycle.Current.Generation && serial == Interlocked.Read(ref _p5ScopeSerial) && !_p1Loading;

    private async Task<bool> P5EnsureScopeAsync(CancellationToken ct)
    {
        uint world = P5LoadedWorld;
        if (world == 0 || !await T3TpPrepareAsync(ct)) return false;
        ulong generation = P1Lifecycle.Current.Generation; long serial = Interlocked.Read(ref _p5ScopeSerial);
        if (_p5ScopeGeneration == generation && _p5ScopeWorld == world) return true;
        var ack = await _combat.DeathMarkerControlAsync(DeathMarkerAction.SetLoadedScope, DeathMarkerKind.Grave, 0, generation, world, ct: ct);
        ct.ThrowIfCancellationRequested(); ProcessorFrameScope.ThrowIfAbandoned();
        if (ack is not { Safe: true } || world != P5LoadedWorld || generation != P1Lifecycle.Current.Generation || serial != Interlocked.Read(ref _p5ScopeSerial)) return false;
        if (_p5ScopeWorld != 0 && _p5ScopeWorld != world) { _p5OwnRevisions.Clear(); _p5PeerRevisions.Clear(); _p5LocalPending.Clear(); }
        var oldLife = _p5MarkerLife;
        _p5MarkerLife = CancellationTokenSource.CreateLinkedTokenSource(_wo122Ct);
        _p5MarkerToken = _p5MarkerLife.Token;
        if (oldLife is not null) _ = oldLife.CancelAsync().ContinueWith(t => { _ = t.Exception; oldLife.Dispose(); }, TaskScheduler.Default);
        _p5TypedMarkers = true; _p5ScopeGeneration = generation; _p5ScopeWorld = world; return true;
    }

    private async Task P5LocalMarkerAsync(NativeDeathMarker marker, Stream stream, CancellationToken ct)
    {
        try
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, _p5MarkerToken); ct = operation.Token;
            var fact = marker.Fact; long serial = Interlocked.Read(ref _p5ScopeSerial);
            var current = P5Fence(stream, ct, fact.World, marker.Generation, serial);
            if (!current() || !_p5OwnRevisions.Current(_myGhostId, fact)) return;
            await _p5MarkerGate.WaitAsync(ct);
            try
            {
                if (!current() || !_p5OwnRevisions.Current(_myGhostId, fact)) return;
                await WritePacketAsync(stream, DeathMarkerCodec.BuildUp(fact), ct);
                if (current()) _p5OwnRevisions.Applied(_myGhostId, fact);
            }
            finally { _p5MarkerGate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.WriteLine($"P5-MARKER local notification failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    private async Task P5AnnounceMarkersAsync(Stream stream, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _p5ReannounceBusy, 1, 0) != 0) return;
        try
        {
            if (!await P5EnsureScopeAsync(ct)) return;
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, _p5MarkerToken); ct = operation.Token;
            uint world = P5LoadedWorld; ulong generation = P1Lifecycle.Current.Generation; long serial = Interlocked.Read(ref _p5ScopeSerial);
            var current = P5Fence(stream, ct, world, generation, serial);
            var list = await _combat.ListDeathMarkersAsync(generation, world, ct);
            if (list is null || !current()) return;
            // Heartbeat is repair, so equal revisions may be republished; a newer local Remove always wins.
            foreach (var fact in list)
            {
                if (!current()) return;
                bool accepted = _p5OwnRevisions.Observe(_myGhostId, fact);
                if (!accepted && !_p5OwnRevisions.Current(_myGhostId, fact)) continue;
                await _p5MarkerGate.WaitAsync(ct);
                try
                {
                    if (!current() || !_p5OwnRevisions.Current(_myGhostId, fact)) continue;
                    await WritePacketAsync(stream, DeathMarkerCodec.BuildUp(fact), ct);
                    _p5OwnRevisions.Applied(_myGhostId, fact);
                }
                finally { _p5MarkerGate.Release(); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.WriteLine($"P5-MARKER reannounce failed: {ex.GetType().Name}: {ex.Message}"); }
        finally { Interlocked.Exchange(ref _p5ReannounceBusy, 0); }
    }

    private async Task<bool> P5ReceiveMarkerAsync(byte owner, DeathMarkerFact fact, uint receiveWorld, long receivedPeer, CancellationToken ct)
    {
        if (!_p5Peers.TryGetValue(owner, out var peer) || !peer.Connected || peer.Serial != receivedPeer) return false;
        using var peerLife = CancellationTokenSource.CreateLinkedTokenSource(ct, peer.Token); ct = peerLife.Token;
        if (receiveWorld == 0 || receiveWorld != fact.World || fact.World != P5LoadedWorld || ct.IsCancellationRequested) return false;
        if (!_p5PeerRevisions.Observe(owner, fact)) return _p5PeerRevisions.IsApplied(owner, fact);
        ulong generation = P1Lifecycle.Current.Generation; long serial = Interlocked.Read(ref _p5ScopeSerial);
        if (_wo122Stream is not Stream stream) return false;
        var current = P5Fence(stream, ct, fact.World, generation, serial);
        await _p5MarkerGate.WaitAsync(ct);
        try
        {
            if (!current() || !_p5PeerRevisions.Current(owner, fact) || !await P5EnsureScopeAsync(ct) || !current()) return false;
            if (_p5PeerRevisions.IsApplied(owner, fact)) return true;
            var ack = await _combat.DeathMarkerControlAsync(fact.Action, fact.Kind, owner, generation, fact.World, fact.Id, fact.Revision, fact.X, fact.Y, fact.Z, ct);
            if (ack is not { Safe: true } || !current()) return false;
            _p5PeerRevisions.Applied(owner, fact);
            return _p5PeerRevisions.IsApplied(owner, fact);
        }
        finally { _p5MarkerGate.Release(); }
    }

    private Task P5ReceiveLegacyMarkerAsync(byte owner, bool add, ulong id, float x, float y, float z, uint receivedWorld, long receivedPeer, CancellationToken ct)
    {
        if (receivedWorld == 0 || receivedWorld != P5LoadedWorld || id == 0 || !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) return Task.CompletedTask;
        var previous = _p5PeerRevisions.Latest(owner, receivedWorld, id);
        if (previous is { Kind: DeathMarkerKind.MarkerOnly }) return Task.CompletedTask;
        if (add && previous is { Action: DeathMarkerAction.Remove }) return Task.CompletedTask; // legacy IDs are never reused
        ulong revision = add ? previous?.Revision ?? 1 : Math.Max(previous?.Revision ?? 0, 1) + 1;
        return P5ReceiveMarkerAsync(owner, new(add ? DeathMarkerAction.Add : DeathMarkerAction.Remove, DeathMarkerKind.Grave, receivedWorld, id, revision, x, y, z), receivedWorld, receivedPeer, ct);
    }

    private async Task P5ClearPeerAsync(byte owner, CancellationToken ct = default)
    {
        _p5PeerRevisions.Clear(owner);
        ulong generation = P1Lifecycle.Current.Generation;
        if (generation == 0 || _p5ScopeWorld == 0) return;
        try { await _combat.DeathMarkerControlAsync(DeathMarkerAction.ClearOwner, DeathMarkerKind.Grave, owner, generation, _p5ScopeWorld, ct: ct); }
        catch (OperationCanceledException) { }
    }

    // This ACK is an actual main-thread detach, unlike the off-main P1 load latch.
    private async Task<bool> P5PrepareLoadAsync(CancellationToken ct)
    {
        _p5LoadRefusal = "";
        ulong gen = P1Lifecycle.Current.Generation; uint cleanupWorld = _p5ScopeWorld;
        long serial = Interlocked.Increment(ref _p5ScopeSerial); Stream? stream = _wo122Stream;
        var ack = gen == 0 ? null : await _combat.DeathMarkerControlAsync(DeathMarkerAction.PrepareLoad, DeathMarkerKind.Grave, 0, gen, cleanupWorld, ct: ct);
        // A cold agent may not know the old native scope. This is cleanup only: do not turn the hint into a loaded-world receipt.
        if (ack is { Status: 2 } hint && hint.World != cleanupWorld && gen == P1Lifecycle.Current.Generation && serial == Interlocked.Read(ref _p5ScopeSerial) && ReferenceEquals(stream, _wo122Stream))
            ack = await _combat.DeathMarkerControlAsync(DeathMarkerAction.PrepareLoad, DeathMarkerKind.Grave, 0, gen, hint.World, ct: ct);
        ct.ThrowIfCancellationRequested(); ProcessorFrameScope.ThrowIfAbandoned();
        if (ack is { Safe: true } && gen == P1Lifecycle.Current.Generation && serial == Interlocked.Read(ref _p5ScopeSerial) && ReferenceEquals(stream, _wo122Stream))
        { _p5ScopeGeneration = 0; return true; }
        _p5LoadRefusal = ack is { Status: 3 }
            ? "Laden angehalten: Kartenmarker konnten nicht sicher entfernt werden. Bitte das Spiel vollständig neu starten und danach erneut verbinden. / Loading paused: map markers could not be detached safely. Please fully restart the game, then reconnect."
            : "Laden angehalten: Kartenmarker konnten nicht sicher entfernt werden. Bitte erneut versuchen. / Loading paused: map markers could not be detached safely. Please try again.";
        SetJoinUi("failed", _p5LoadRefusal); Console.WriteLine($"P5-LOAD-BLOCK status={ack?.Status.ToString() ?? "unavailable"} {_p5LoadRefusal}"); return false;
    }

    private async Task P5AfterGameplayAsync(CancellationToken ct)
    {
        if (!_p5RebindAfterLoad || _p1Loading || !P1Lifecycle.Current.GameplaySent || !P1Lifecycle.Current.Confirmed) return;
        if (!_p5LoadToPrivate)
        {
            _p5RebindAfterLoad = false;
            if (_wo122Stream is Stream stream) await P5AnnounceMarkersAsync(stream, ct);
            return;
        }
        ulong gen = P1Lifecycle.Current.Generation; long serial = Interlocked.Read(ref _p5ScopeSerial);
        var ack = await _combat.DeathMarkerControlAsync(DeathMarkerAction.SetLoadedScope, DeathMarkerKind.Grave, 0, gen, 0, ct: ct);
        if (ack is { Safe: true } && gen == P1Lifecycle.Current.Generation && serial == Interlocked.Read(ref _p5ScopeSerial))
        {
            _p5RebindAfterLoad = false; _p5LoadToPrivate = false;
            _p5ScopeGeneration = gen; _p5ScopeWorld = 0; _p5TypedMarkers = true;
            _p5OwnRevisions.Clear(); _p5PeerRevisions.Clear(); _p5LocalPending.Clear();
        }
    }

    private Task<bool> P5LoadGameAsync(int playline, string name, string reason, CancellationToken ct = default) =>
        P5LoadGameCoreAsync(playline, name, reason, null, ct);

    private async Task<bool> P5LoadGameCoreAsync(int playline, string name, string reason, Func<bool>? authorizeDispatch,
        CancellationToken ct = default, Func<IDisposable?>? prepareDispatch = null)
    {
        if (ct == default) ct = _wo122Ct;
        bool own = reason is "leave-command" or "exit-to-menu";
        bool previousOwn = _ownLoadExpected, previousGrace = _loadGraceOwn, submitted = false;
        CancellationTokenSource? loadLife = null;
        var previous = _worldLoad.Current;
        WorldLoadSnapshot prepared = previous;
        WorldLoadSnapshot dispatchScope = default;
        if (own) // the P1 pending mode must already know this is the private-world load
        {
            if (!LoadSequenceEnabled) _ownLoadExpected = true;
            else if (!_worldLoad.SetOwnIntent(previous.Generation, WorldLoadOwnIntent.ExpectedPrivate, out prepared, out previous))
                return false;
            else _ = LoadSequenceSyncNativeAsync();
        }
        try
        {
            P5RetireOperations();
            if (!await P1BeforeModLoadAsync(reason, ct)) return false;
            ulong generation = P1Lifecycle.Current.Generation; long serial = Interlocked.Read(ref _p5ScopeSerial);
            string lua = $"if KCD2MP and KCD2MP.hostAuthority and tostring(KCD2MP.hostAuthority.generation)==\"{generation}\" and KCD2MP_Wo124LoadGame then KCD2MP_Wo124LoadGame({playline},\"{EscapeLua(name)}\",\"{EscapeLua(reason)}\") end";
            if (generation != P1Lifecycle.Current.Generation || serial != Interlocked.Read(ref _p5ScopeSerial)) return false;
            if (LoadSequenceEnabled)
            {
                // The offer or private intent must already have closed the
                // DLL before the actual engine load is requested.
                if (!await LoadSequenceSyncNativeAsync()) return false;
                var current = _worldLoad.Current;
                if (current.Generation != prepared.Generation || current.OwnIntentRevision != prepared.OwnIntentRevision) return false;
                if (authorizeDispatch is null)
                    LoadSequenceBeginLoad();
                else if (own)
                {
                    if (!LoadSequenceChange(s => s.Generation == prepared.Generation && s.OwnIntentRevision == prepared.OwnIntentRevision &&
                        _worldLoad.TryBegin(s.Generation, WorldLoadStart.LocalLoad, out dispatchScope))) return false;
                }
                else if (!LoadSequenceChange(s => s.Generation == prepared.Generation &&
                    s.HostWorld == prepared.HostWorld && s.OwnIntentRevision == prepared.OwnIntentRevision &&
                    s.Phase == WorldLoadPhase.Offer && _worldLoad.TryAdvance(s, WorldLoadPhase.Load)))
                    return false;
                else dispatchScope = prepared;
            }
            var dispatchLife = loadLife = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (authorizeDispatch is null)
            {
                MarkSubmitted();
                await _transport.ExecuteNowAsync(lua, dispatchLife.Token);
                return true;
            }

            Task? Admit(Func<Task> send)
            {
                if (dispatchLife.IsCancellationRequested) return null;
                // Read and journal the file without holding the dispatch lock.
                // Keep the checked handle open across the synchronous handoff.
                using var verifiedSave = prepareDispatch?.Invoke();
                if (prepareDispatch is not null && verifiedSave is null) return null;

                // Read retirement uses the same gate. Only the synchronous
                // transport handoff is in the critical section, never its wait.
                lock (_joinGate)
                {
                    if (dispatchLife.IsCancellationRequested || generation != P1Lifecycle.Current.Generation ||
                        serial != Interlocked.Read(ref _p5ScopeSerial)) return null;
                    if (LoadSequenceEnabled)
                    {
                        var load = _worldLoad.Current;
                        if (load.Generation != dispatchScope.Generation || load.HostWorld != dispatchScope.HostWorld ||
                            load.OwnIntentRevision != dispatchScope.OwnIntentRevision || load.Phase != WorldLoadPhase.Load)
                            return null;
                    }
                    if (!authorizeDispatch()) return null;
                    MarkSubmitted();
                    return send();
                }
            }

            void MarkSubmitted()
            {
                _p5LoadLife = dispatchLife;
                _p5RebindAfterLoad = true;
                _p5LoadToPrivate = own;
                if (own) _w207LeaveTarget = name;
                submitted = true; // the game may still read the file if HTTP later fails
            }

            return await (_transport switch
            {
                HttpGameTransport http => http.ExecuteAdmittedNowAsync(lua, Admit, dispatchLife.Token),
                LogTailGameTransport tail => tail.ExecuteAdmittedNowAsync(lua, Admit, dispatchLife.Token),
                _ => Task.FromResult(false)
            });
        }
        finally
        {
            if (!submitted)
            {
                loadLife?.Dispose();
                if (!LoadSequenceEnabled) { _ownLoadExpected = previousOwn; _loadGraceOwn = previousGrace; }
                else if (own && (_worldLoad.TryRestoreOwnIntent(prepared, previous) ||
                    _worldLoad.TryUpdateOwnIntent(dispatchScope, previous.OwnIntent, previous.OwnIntentDeadlineMs))) _ = LoadSequenceSyncNativeAsync();
            }
        }
    }
}
