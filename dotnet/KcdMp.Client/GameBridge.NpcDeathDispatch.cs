// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using KcdMp.Wire;
using Receipt = KcdMp.Client.CombatPipe.NpcDeathReceipt;

namespace KcdMp.Client;

public partial class GameBridge
{
    private readonly record struct NpcDeathScope(long Life, ulong Lifecycle, ulong Load, uint World, long Pipe, Stream? Relay);
    private sealed class NpcDeathAttempt(ulong id, Guid soul, string? npc, Wo195BodyScope? body, NpcDeathScope scope,
        byte? source, long peer, string via)
    {
        public ulong Id { get; } = id;
        public Guid Soul { get; } = soul;
        public string? Npc { get; } = npc;
        public Wo195BodyScope? Body { get; } = body;
        public NpcDeathScope Scope { get; } = scope;
        public byte? Source { get; } = source;
        public long Peer { get; } = peer;
        public string Via { get; } = via;
        public Receipt? Result { get; set; }
        public long EchoUntil { get; set; } = Environment.TickCount64 + 6_000;
    }

    private long _npcDeathLife;
    private long _npcDeathAttemptId;
    private readonly SemaphoreSlim _npcDeathDispatchOrder = new(1, 1);
    private readonly object _npcDeathDispatchGate = new();
    private readonly Dictionary<Guid, NpcDeathAttempt> _npcDeathAttempts = new();
    private static readonly TimeSpan NpcDeathPollCadence = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan NpcDeathPollBudget = TimeSpan.FromSeconds(1);

    private void ResetNpcDeathDispatch()
    {
        lock (_npcDeathDispatchGate)
        {
            Interlocked.Increment(ref _npcDeathLife);
            _npcDeathAttempts.Clear();
            _npcDeathSent.Reset();
            _npcDeathAppliedUtc.Clear();
        }
    }

    private long NpcDeathPeerAtRead(int type, byte[] payload)
    {
        bool fatal = type == Protocol.NpcDamageDown && payload.Length > Protocol.NpcDamageFixedTail &&
            (payload[^1] & Protocol.NpcDamageFlagFatal) != 0;
        bool transition = type == Protocol.NpcStateDown && payload.Length > 1 &&
            2 + payload[1] + Protocol.NpcStateFlagsOffset < payload.Length &&
            (payload[2 + payload[1] + Protocol.NpcStateFlagsOffset] & Protocol.NpcStateFlagDead) != 0;
        if (payload.Length == 0 || !(type == Protocol.DeathDown || fatal || transition)) return 0;
        return _p5Peers.TryGetValue(payload[0], out var peer) && peer.Connected ? peer.Serial : -1;
    }

    private NpcDeathScope CurrentNpcDeathScope() => new(Interlocked.Read(ref _npcDeathLife), P1Lifecycle.Current.Generation,
        _worldLoad.Current.Generation, _w206.World, _combat.EdgeConnectionRevision, _resyncStream);

    private bool NpcDeathPeerCurrent(byte source, long serial) =>
        _p5Peers.TryGetValue(source, out var peer) && peer.Connected && peer.Serial == serial &&
        !peer.Token.IsCancellationRequested;

    private bool NpcDeathScopeCurrent(NpcDeathScope scope, byte? source, long peer)
    {
        var lifecycle = P1Lifecycle.Current;
        return scope == CurrentNpcDeathScope() && _where == GameWhere.World && !_leaveInProgress && !_p1Loading &&
            LoadSequenceWritable && !LinkHeld && _combat.IsConnected && scope.Lifecycle != 0 &&
            lifecycle is { Confirmed: true, GameplaySent: true } &&
            (source is null || (_npcDeathSyncEnabled && scope.Relay is not null &&
                NpcDeathPeerCurrent(source.Value, peer)));
    }

    private Task<Receipt> ApplyRemoteNpcDeathAsync(string npc, Guid? knownLocalGuid, byte source, string via,
        CancellationToken ct, bool bypassDedupe = false, long peerSerial = 0) =>
        ApplyOwnedNpcDeathAsync(npc, knownLocalGuid, source, via, ct, bypassDedupe, peerSerial);

    private async Task<Receipt> ApplyOwnedNpcDeathAsync(string? npc, Guid? knownSoul, byte? source, string via,
        CancellationToken ct, bool bypassDedupe, long peerSerial = 0)
    {
        var scope = CurrentNpcDeathScope();
        if (source is byte sender && peerSerial == 0 && _p5Peers.TryGetValue(sender, out var live)) peerSerial = live.Serial;
        bool Current() => NpcDeathScopeCurrent(scope, source, peerSerial) && !ct.IsCancellationRequested &&
            ProcessorFrameScope.Capture()?.IsAbandoned != true;
        if (!Current()) return RefuseNpcDeath(via, "scope-not-current", npc);

        Wo195BodyScope? body = null;
        if (npc is not null)
        {
            if (!_npcEntityIds.TryGetValue(npc, out uint eid) || eid == 0)
                return RefuseNpcDeath(via, "no-current-entity", npc);
            knownSoul ??= await ResolveLocalSoulGuidAsync(npc, ct);
            body = Wo195CaptureBody(npc, scope.Life);
            if (body is not { } binding || binding.Eid != eid || binding.Soul != knownSoul)
                return RefuseNpcDeath(via, "no-current-soul-binding", npc);
        }
        if (source is not null) await RefreshPlayerIdentityAsync(force: false, ct);
        if (!Current()) return RefuseNpcDeath(via, "scope-retired", npc);
        if (knownSoul is not Guid soul || soul == Guid.Empty || soul == PlayerHenrySharedSoulGuid ||
            _dmgGuard.CheckInbound(npc ?? "", soul) != NpcDamageGuard.Inbound.Apply)
            return RefuseNpcDeath(via, "no-npc-soul", npc);

        Receipt result;
        NpcDeathAttempt attempt;
        await _npcDeathDispatchOrder.WaitAsync(ct);
        try
        {
            if (!Current() || (body is { } concrete && !Wo195BodyCurrent(npc!, concrete))) return Receipt.Stale;
            NpcDeathAttempt? existing;
            Receipt? previous;
            lock (_npcDeathDispatchGate)
            {
                _npcDeathAttempts.TryGetValue(soul, out existing);
                previous = existing?.Result;
            }
            if (existing?.Scope == scope &&
                (body is null || existing.Body is null || existing.Body.Value.Eid == body.Value.Eid))
            {
                if (previous != Receipt.Applied || !bypassDedupe || npc is null || body is not { } revived ||
                    !await NpcDeathBodyReadsAliveAsync(npc, revived, Current))
                    return previous ?? Receipt.Pending;
            }

            attempt = new(unchecked((ulong)Interlocked.Increment(ref _npcDeathAttemptId)), soul, npc, body, scope,
                source, peerSerial, via);
            lock (_npcDeathDispatchGate)
            {
                if (!Current() || (body is { } candidate && !Wo195BodyCurrent(npc!, candidate))) return Receipt.Stale;
                _npcDeathAttempts[soul] = attempt;
            }
            ReceiptLog(attempt, "submitted");
            result = await ObserveNpcDeathAsync(attempt, ct);
            if (result != Receipt.Pending) result = EndNpcDeathAttempt(attempt, result);
        }
        finally { _npcDeathDispatchOrder.Release(); }

        if (result == Receipt.Pending)
        {
            ReceiptLog(attempt, "pending");
            var frame = ProcessorFrameScope.Capture();
            Task<Receipt> pending = PollNpcDeathAsync(attempt, frame?.ParentToken ?? ct);
            // The frame processor can continue while the engine finishes on a
            // later frame. Other callers can wait for the confirmed result.
            return frame is null ? await pending : Receipt.Pending;
        }
        return result;
    }

    private bool NpcDeathAttemptCurrent(NpcDeathAttempt attempt)
    {
        lock (_npcDeathDispatchGate)
            return _npcDeathAttempts.TryGetValue(attempt.Soul, out var saved) && ReferenceEquals(saved, attempt) &&
                attempt.Result is null && ProcessorFrameScope.Capture()?.IsAbandoned != true &&
                NpcDeathScopeCurrent(attempt.Scope, attempt.Source, attempt.Peer) &&
                (attempt.Body is not { } body || Wo195BodyCurrent(attempt.Npc!, body));
    }

    private async Task<Receipt> ObserveNpcDeathAsync(NpcDeathAttempt attempt, CancellationToken ct)
    {
        try
        {
            return await _combat.ApplyOwnedNpcDeathAsync(attempt.Soul, attempt.Body?.Eid ?? 0,
                attempt.Scope.Lifecycle, attempt.Id, () => !ct.IsCancellationRequested && NpcDeathAttemptCurrent(attempt), ct);
        }
        catch (OperationCanceledException)
        {
            return NpcDeathAttemptCurrent(attempt) ? Receipt.Uncertain : Receipt.Stale;
        }
    }

    private async Task<Receipt> PollNpcDeathAsync(NpcDeathAttempt attempt, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(NpcDeathPollBudget);
        Receipt final = Receipt.Uncertain;
        try
        {
            while (NpcDeathAttemptCurrent(attempt))
            {
                await Task.Delay(NpcDeathPollCadence, budget.Token);
                Receipt receipt = await ObserveNpcDeathAsync(attempt, budget.Token);
                if (receipt == Receipt.Pending) continue;
                final = receipt;
                return EndNpcDeathAttempt(attempt, final);
            }
            final = Receipt.Stale;
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested) { }
        return EndNpcDeathAttempt(attempt, final);
    }

    private Receipt EndNpcDeathAttempt(NpcDeathAttempt attempt, Receipt receipt)
    {
        bool markRemote = false;
        lock (_npcDeathDispatchGate)
        {
            if (!NpcDeathAttemptCurrent(attempt)) receipt = Receipt.Stale;
            attempt.Result = receipt;
            attempt.EchoUntil = Environment.TickCount64 + 5_000;
            if (receipt == Receipt.Applied && attempt.Npc is { } npc && attempt.Body is { } body)
            {
                if (attempt.Source is not null)
                {
                    DateTime now = DateTime.UtcNow;
                    _npcDeathAppliedUtc[npc] = now;
                    _dmgGuard.NoteInboundApplied(npc, 0f, 0f, fatal: true, now);
                    Wo206NoteApplied("dead", npc, Wo206DeathCause(attempt.Via), null, "dead");
                    markRemote = true;
                }
                Wo195OnDeathConfirmed(npc, body, attempt.Id, attempt.Via);
            }
        }
        if (markRemote) _ = MarkRemoteNpcDeathAsync(attempt, attempt.Npc!, attempt.Body!.Value);
        ReceiptLog(attempt, receipt.ToString().ToLowerInvariant());
        return receipt;
    }

    private async Task MarkRemoteNpcDeathAsync(NpcDeathAttempt attempt, string npc, Wo195BodyScope body)
    {
        try
        {
            if (!NpcDeathScopeCurrent(attempt.Scope, attempt.Source, attempt.Peer) || !Wo195BodyCurrent(npc, body)) return;
            string name = EscapeLua(npc), via = EscapeLua(attempt.Via);
            await GameCalls.ExecuteNowAsync(
                $"local e=System.GetEntityByName(\"{name}\"); " +
                $"local id=e and string.match(tostring(e.id), \"(%x+)%s*$\"); " +
                $"if id and tonumber(id,16)=={body.Eid} and e.actor and e.actor:IsDead()==true and KCD2MP_NpcRemoteDeath " +
                $"then KCD2MP_NpcRemoteDeath(\"{name}\",\"{via}\") end");
        }
        catch (Exception ex) { Console.WriteLine($"[npcdeath] Lua remote-death mark failed for '{npc}': {ex.Message}"); }
    }

    private async Task<bool> NpcDeathBodyReadsAliveAsync(string npc, Wo195BodyScope body, Func<bool> current)
    {
        bool Current() => current() && Wo195BodyCurrent(npc, body);
        var r = await _combat.Wo195ScopedAsync(Protocol.Wo195OpCheck, Wo195BodyRequest(body), Current);
        return Current() && r is { Ok: true, Payload: [0, _, 1, ..] };
    }

    private bool NpcDeathIsRemoteEcho(Guid soul)
    {
        lock (_npcDeathDispatchGate)
            return _npcDeathAttempts.TryGetValue(soul, out var attempt) && attempt.Source is not null &&
                attempt.Result != Receipt.Stale && Environment.TickCount64 < attempt.EchoUntil &&
                attempt.Scope == CurrentNpcDeathScope() &&
                (attempt.Body is not { } body || Wo195MappedBodyCurrent(attempt.Npc!, body));
    }

    private static Receipt RefuseNpcDeath(string via, string reason, string? npc)
    {
        Console.WriteLine($"MP-LANE2-H6 route={Wo195Rules.PathTag(via)} npc={npc ?? "?"} phase=stale reason={reason}");
        return Receipt.Stale;
    }

    private static void ReceiptLog(NpcDeathAttempt attempt, string phase) =>
        Console.WriteLine($"MP-LANE2-H6 route={Wo195Rules.PathTag(attempt.Via)} attempt={attempt.Id} eid=0x{attempt.Body?.Eid ?? 0:X} life={attempt.Scope.Lifecycle} load={attempt.Scope.Load} world={attempt.Scope.World} phase={phase}");
}
