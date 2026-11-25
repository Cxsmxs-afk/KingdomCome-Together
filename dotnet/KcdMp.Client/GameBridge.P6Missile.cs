// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using KcdMp.Wire;
namespace KcdMp.Client;

public partial class GameBridge
{
    private sealed record P6MeasuredWork(Guid Soul, float Stamina, float Health, MeasuredMissileEvidence Evidence, P6Context Context, Func<Task> HostReplication);
    private readonly ConcurrentQueue<P6MeasuredWork> _p6MeasuredWork = new();
    private int _p6MeasuredWorker, _p6MeasuredCount;
    private readonly ConcurrentDictionary<MeasuredMissileKey, TaskCompletionSource<MeasuredMissileResult>> _p6AwaitingMissile = new();
    private sealed record P6Decision(MeasuredMissileRequest Request, ulong Deadline, TaskCompletionSource<MeasuredMissileResult> Completion);
    private readonly object _p6DecisionGate = new();
    private readonly Dictionary<(byte Peer, MeasuredMissileKey Key), P6Decision> _p6Decisions = new();
    private uint _p6DecisionWorld;
    private (uint World, ulong Gen, ulong Epoch) _p6OwnContactStream;
    private sealed record P6OwnContact(Guid Soul, int StaminaBits, int HealthBits, bool Died, ulong Tick);
    private readonly Dictionary<ulong, P6OwnContact> _p6OwnContactIds = new();
    private ulong _p6OwnContactFloor;

    private void P6QueueMeasuredHit(Guid soul, float stamina, float health, MeasuredMissileEvidence evidence, Func<Task> hostReplication)
    {
        var context = P6GetContext();
        if (context is null || context.Generation != evidence.Generation || !P6Numbers.Deltas(health, stamina) ||
            unchecked((ulong)Environment.TickCount64) < evidence.CapturedTick || unchecked((ulong)Environment.TickCount64) - evidence.CapturedTick > 2000)
        { T3Increment("ranged_missile_refused"); return; }
        lock (_p6StateGate)
        {
            var prior = _p6OwnContactStream;
            if (prior.World == context.World && (evidence.Generation < prior.Gen || (evidence.Generation == prior.Gen && evidence.Epoch < prior.Epoch)))
            { T3Increment("ranged_missile_duplicate"); return; }
            if (prior != (context.World, evidence.Generation, evidence.Epoch))
            { _p6OwnContactIds.Clear(); _p6OwnContactFloor = 0; _p6OwnContactStream = (context.World, evidence.Generation, evidence.Epoch); }
            ulong now = unchecked((ulong)Environment.TickCount64);
            foreach (var old in _p6OwnContactIds.Where(p => now > p.Value.Tick + 2000).ToArray())
            { _p6OwnContactIds.Remove(old.Key); _p6OwnContactFloor = Math.Max(_p6OwnContactFloor, old.Key); }
            var fact = new P6OwnContact(soul, BitConverter.SingleToInt32Bits(stamina), BitConverter.SingleToInt32Bits(health), evidence.Died, evidence.CapturedTick);
            if (_p6OwnContactIds.TryGetValue(evidence.ContactId, out var existing))
            { T3Increment(existing == fact ? "ranged_missile_duplicate" : "ranged_missile_conflict"); return; }
            if (evidence.ContactId <= _p6OwnContactFloor) { T3Increment("ranged_missile_duplicate"); return; }
            if (_p6OwnContactIds.Count >= 256)
            { _p6OwnContactFloor = Math.Max(_p6OwnContactFloor, evidence.ContactId); T3Increment("ranged_missile_refused"); return; }
            _p6OwnContactIds.Add(evidence.ContactId, fact);
        }
        if (Interlocked.Increment(ref _p6MeasuredCount) > 128)
        { Interlocked.Decrement(ref _p6MeasuredCount); T3Increment("ranged_missile_refused"); return; }
        _p6MeasuredWork.Enqueue(new(soul, stamina, health, evidence, context, hostReplication));
        if (Interlocked.CompareExchange(ref _p6MeasuredWorker, 1, 0) != 0) return;
        void Start() => _ = Task.Run(P6MeasuredWorkerAsync);
        if (ExecutionContext.IsFlowSuppressed()) Start(); else { using (ExecutionContext.SuppressFlow()) Start(); }
    }
    private async Task P6MeasuredWorkerAsync()
    {
        try
        {
            while (_p6MeasuredWork.TryDequeue(out var work))
            {
                Interlocked.Decrement(ref _p6MeasuredCount);
                await RunProcessorFrameAsync(token => P6PublishMeasuredAsync(work, token), 2000, work.Context.Cancellation);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _p6MeasuredWorker, 0);
            if (!_p6MeasuredWork.IsEmpty && Interlocked.CompareExchange(ref _p6MeasuredWorker, 1, 0) == 0) _ = Task.Run(P6MeasuredWorkerAsync);
        }
    }
    private async Task P6PublishMeasuredAsync(P6MeasuredWork work, CancellationToken ct)
    {
        var context = work.Context;
        bool SameSourceEpoch() { lock (_p6StateGate) return _p6OwnContactStream == (context.World, work.Evidence.Generation, work.Evidence.Epoch); }
        bool Current() => P6Current(context) && !_localDowned && unchecked((ulong)Environment.TickCount64) >= work.Evidence.CapturedTick &&
            unchecked((ulong)Environment.TickCount64) - work.Evidence.CapturedTick <= 2000 && SameSourceEpoch();
        if (!Current()) { T3Increment("ranged_missile_refused"); return; }
        if (_isDamageAuthority)
        {
            // This contact already changed the real host NPC. Replicate it once; never apply it again on the host.
            await work.HostReplication();
            if (P6Current(context)) { T3Increment("ranged_missile_local_host"); T3Increment("ranged_hits_verdict"); }
            return;
        }
        string? name = await ResolveSoulNameAsync(work.Soul, ct);
        if (name is not null && _npcReplicaOrig.TryGetValue(name, out var original)) name = original;
        if (!Current() || name is null || name.StartsWith("kcd2mp_", StringComparison.Ordinal) || !W164Text.IsNpcName(name)) { T3Increment("ranged_missile_refused"); return; }
        // Existing location/host-body guard remains. Never replace a measured amount with the imm-floor estimate.
        var gate = await Wo131GateOutboundAsync(work.Soul, name, work.Health, false);
        if (!Current() || !gate.Send) { T3Increment("ranged_missile_refused"); return; }
        var request = new MeasuredMissileRequest(new(context.World, work.Evidence.Generation, work.Evidence.Epoch, work.Evidence.ContactId), name, work.Health, work.Stamina);
        var completion = new TaskCompletionSource<MeasuredMissileResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_p6AwaitingMissile.Count >= 128 || !_p6AwaitingMissile.TryAdd(request.Key, completion)) { T3Increment("ranged_missile_duplicate"); return; }
        if (!await P6SendAsync(context, Protocol.JoinTargetHost, Protocol.W164MeasuredMissile, request.Encode(), Current, ct, () => T3Increment("ranged_missile_requests")))
        { _p6AwaitingMissile.TryRemove(request.Key, out _); T3Increment("ranged_missile_uncertain"); return; }
        // Receipt waiting never holds the FIFO source worker or the processor. Only ContextCT outlives the current frame.
        _ = P6WaitForMissileAckAsync(request.Key, completion, context.Cancellation);
    }
    private async Task P6WaitForMissileAckAsync(MeasuredMissileKey key, TaskCompletionSource<MeasuredMissileResult> completion, CancellationToken ct)
    {
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), ct); }
        catch (TimeoutException) { T3Increment("ranged_missile_ack_timeout"); }
        catch (OperationCanceledException) { }
        finally { _p6AwaitingMissile.TryRemove(key, out _); }
        // No fresh ID, legacy send or melee fallback after an ambiguous outcome.
    }
    private static bool P6SameFact(MeasuredMissileRequest a, MeasuredMissileRequest b) => a.Key == b.Key && a.Npc == b.Npc &&
        BitConverter.SingleToInt32Bits(a.Health) == BitConverter.SingleToInt32Bits(b.Health) && BitConverter.SingleToInt32Bits(a.Stamina) == BitConverter.SingleToInt32Bits(b.Stamina);
    private void P6OnMissileResult(byte src, string text, P6Context context)
    {
        if (_isDamageAuthority || !_hostModeKnown || src != _hostModeFrom || !P6Current(context) ||
            !MeasuredMissileResult.TryDecode(text, out var result) || result.Key.World != context.World || result.Key.SourceGeneration != context.Generation)
        { T3Increment("ranged_malformed"); return; }
        if (!_p6AwaitingMissile.TryGetValue(result.Key, out var completion) || !completion.TrySetResult(result)) return;
        if (result.Status != MeasuredMissileStatus.Accepted) T3Increment("ranged_missile_refused");
        Console.WriteLine($"P6-MISSILE host_result={result.Status} contact={result.Key.ContactId} mode=measured_missile_accept (not an engine missile rejudge)");
    }
    private async Task P6OnMissileRequestAsync(byte source, string text, InFrame frame, P6Context context, CancellationToken ct)
    {
        if (!_isDamageAuthority || !MeasuredMissileRequest.TryDecode(text, out var request)) { T3Increment("ranged_malformed"); return; }
        if (source >= 16 || frame.Stamp == 0 || request.Key.World != frame.Stamp || frame.Stamp != context.World) { T3Increment("ranged_stale"); return; }
        P6Decision decision; bool first;
        lock (_p6DecisionGate)
        {
            if (_p6DecisionWorld != context.World) { _p6Decisions.Clear(); _p6DecisionWorld = context.World; }
            if (_p6Decisions.TryGetValue((source, request.Key), out decision!))
            {
                first = false;
                if (!P6SameFact(decision.Request, request))
                { T3Increment("ranged_missile_conflict"); decision = new(request, 0, new(TaskCreationOptions.RunContinuationsAsynchronously)); decision.Completion.SetResult(new(request.Key, MeasuredMissileStatus.Conflict, 0, 0, 0)); }
                else T3Increment("ranged_missile_duplicate");
            }
            else
            {
                first = _p6Decisions.Count < 4096;
                decision = new(request, unchecked((ulong)frame.P6Deadline), new(TaskCreationOptions.RunContinuationsAsynchronously));
                if (first) _p6Decisions.Add((source, request.Key), decision);
                else decision.Completion.SetResult(new(request.Key, MeasuredMissileStatus.Capacity, 0, 0, 0));
            }
        }
        if (first)
        {
            MeasuredMissileResult result;
            try { result = await P6DecideMissileAsync(source, request, frame.P6PeerSerial, decision.Deadline, context, ct); }
            catch (OperationCanceledException) { result = new(request.Key, MeasuredMissileStatus.Uncertain, 0, 0, 0); }
            catch (Exception ex) { Console.WriteLine($"P6-MISSILE uncertain {ex.GetType().Name}"); result = new(request.Key, MeasuredMissileStatus.Uncertain, 0, 0, 0); }
            decision.Completion.TrySetResult(result);
            if (result.Status == MeasuredMissileStatus.Accepted) { T3Increment("ranged_missile_measured_accept"); T3Increment("ranged_hits_verdict"); }
            else if (result.Status == MeasuredMissileStatus.Uncertain) T3Increment("ranged_missile_uncertain");
            else T3Increment("ranged_missile_refused");
        }
        var final = await decision.Completion.Task.WaitAsync(ct);
        await P6SendAsync(context, source, Protocol.W164RealHit, final.Encode(), () => P6PeerCurrent(source, frame.P6PeerSerial), ct);
    }
    private async Task<MeasuredMissileResult> P6DecideMissileAsync(byte source, MeasuredMissileRequest request, long peer, ulong deadline, P6Context context, CancellationToken ct)
    {
        MeasuredMissileResult Refused(MeasuredMissileStatus status) => new(request.Key, status, 0, 0, 0);
        bool Current() => _isDamageAuthority && P6Current(context) && P6PeerCurrent(source, peer);
        if (!Current() || !_npcAttribution) return Refused(MeasuredMissileStatus.Scope);
        if (deadline == 0 || unchecked((ulong)Environment.TickCount64) > deadline) return Refused(MeasuredMissileStatus.Expired);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, context.Cancellation, _p5Peers[source].Token);
        long left = unchecked((long)deadline) - Environment.TickCount64;
        if (left <= 0) return Refused(MeasuredMissileStatus.Expired);
        linked.CancelAfter(TimeSpan.FromMilliseconds(left));
        if (!await P5EnsureScopeAsync(linked.Token) || !Current()) return Refused(MeasuredMissileStatus.Scope);
        Guid? soul = await ResolveLocalSoulGuidAsync(request.Npc, linked.Token);
        if (!Current()) return Refused(MeasuredMissileStatus.Scope);
        if (soul is not Guid victim || _dmgGuard.CheckInbound(request.Npc, victim) != NpcDamageGuard.Inbound.Apply ||
            !_ghostEntityIds.TryGetValue(source.ToString(), out uint avatar) || avatar == 0) return Refused(MeasuredMissileStatus.Victim);
        var reply = await _combat.AcceptMeasuredMissileAsync(source, request, victim, avatar, context.Generation, deadline, Current, linked.Token);
        if (reply is not { } result) return Refused(MeasuredMissileStatus.Uncertain);
        if (result.Status == MeasuredMissileStatus.Accepted)
        {
            _dmgGuard.NoteInboundApplied(request.Npc, result.Health, result.Stamina, false, DateTime.UtcNow);
            _w173Touched[request.Npc] = Environment.TickCount64; // existing canonical host down/up/death watcher, including third peers
            Wo191NoteFight(request.Npc, "measured-missile-accepted");
        }
        Console.WriteLine($"P6-MISSILE host_result={result.Status} npc={request.Npc} contact={request.Key.ContactId} mode=measured_missile_accept");
        return result;
    }
}
