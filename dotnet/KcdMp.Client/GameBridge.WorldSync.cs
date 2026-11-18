// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Diagnostics;
using System.Threading.Channels;
using KcdMp.Wire;

namespace KcdMp.Client;

public partial class GameBridge
{
    private const int WorldSyncRows = 4096, WorldSyncBytes = 4 * 1024 * 1024;
    private sealed class WorldSyncReceiver(Stream stream, WorldSyncScope scope, CancellationToken link, CancellationToken generation)
    {
        public readonly Stream Stream = stream;
        public readonly WorldSyncScope Scope = scope;
        public readonly CancellationTokenSource Stop = CancellationTokenSource.CreateLinkedTokenSource(link, generation);
        public readonly Channel<(int Type, byte[] Body)> Inbox = Channel.CreateBounded<(int, byte[])>(new BoundedChannelOptions(8192)
            { SingleReader = true, SingleWriter = true });
        public readonly Dictionary<WorldBaselineSection, WorldBaselineChunk?[]> Sections = new();
        public readonly WorldRecoveryReceiver Recovery = new();
        public WorldEventReceiveWindow? Events;
        public Task? Work;
        public uint Snapshot;
        public int Rows, Bytes;
        public long BaselineAt, LastChunkAt, NextControlAt;
        public volatile bool Installed, ServerInstalled, Installing, Failed;
    }

    private WorldSyncReceiver? _worldReceiver;
    private readonly AsyncLocal<bool> _worldBaselineApplying = new();
    private bool WorldSyncEffective => LoadSequenceEnabled && W221On("baseline_events") &&
        (_w213Caps & Protocol.LinkCapWorldSync) != 0 && _combatRoleApplied &&
        (_isDamageAuthority ? _sharedWorld : JoinerSharedEffective);
    private bool WorldSyncReady => _worldBaselineApplying.Value || !WorldSyncEffective ||
        Volatile.Read(ref _worldReceiver) is { Installed: true } receiver && WorldSyncCurrent(receiver);

    private bool WorldSyncCurrent(WorldSyncReceiver receiver)
    {
        var s = _worldLoad.Current;
        return WorldSyncEffective && ReferenceEquals(Volatile.Read(ref _worldReceiver), receiver) &&
            !receiver.Stop.IsCancellationRequested && s.Generation == receiver.Scope.Generation &&
            _w206.World == receiver.Scope.HostWorld && s.Phase is not (WorldLoadPhase.Exit or WorldLoadPhase.Menu);
    }

    private void WorldSyncStop(Stream stream)
    {
        var active = Volatile.Read(ref _worldReceiver);
        if (active is null || !ReferenceEquals(active.Stream, stream) ||
            !ReferenceEquals(Interlocked.CompareExchange(ref _worldReceiver, null, active), active)) return;
        WorldSyncRetireStatsReceiver(active);
        active.Inbox.Writer.TryComplete();
        _ = DisposeWorldSyncReceiverAsync(active);
    }

    private static async Task DisposeWorldSyncReceiverAsync(WorldSyncReceiver receiver)
    {
        try
        {
            await receiver.Stop.CancelAsync();
            if (receiver.Work is { } work) await work;
        }
        finally { receiver.Stop.Dispose(); }
    }

    private void WorldSyncTick(CancellationToken ct)
    {
        var stream = _w213Stream;
        if (stream is null) return;
        var active = Volatile.Read(ref _worldReceiver);
        if (!WorldSyncEffective)
        {
            if (active is not null)
            {
                WorldSyncSend(active, WorldSyncOperation.Close, requireScope: false);
                WorldSyncStop(active.Stream);
            }
            return;
        }
        var phase = _worldLoad.Current;
        if (phase.Generation == 0 || _w206.World == 0 || phase.HostWorld != _w206.World ||
            phase.Phase is WorldLoadPhase.Menu or WorldLoadPhase.Exit)
        {
            if (active is not null) WorldSyncStop(active.Stream);
            return;
        }
        if (active is null || !WorldSyncCurrent(active))
        {
            if (active is not null) WorldSyncStop(active.Stream);
            var capture = LoadSequenceCapture();
            if (capture.Generation != phase.Generation || capture.Token.IsCancellationRequested) return;
            active = new WorldSyncReceiver(stream, new(_w206.World, phase.Generation), ct, capture.Token);
            WorldSyncTrackStatsReceiver(active);
            var receiver = active;
            active.Work = Task.Run(() => RunWorldSyncReceiverAsync(receiver));
            Volatile.Write(ref _worldReceiver, active);
        }

        long now = Environment.TickCount64;
        if (now < Volatile.Read(ref active.NextControlAt) || active.Failed) return;
        if (active.Installed)
        {
            if (!active.ServerInstalled)
            {
                Volatile.Write(ref active.NextControlAt, now + 2000);
                WorldSyncSend(active, WorldSyncOperation.BaselineApplied);
            }
            return;
        }
        if (active.Snapshot == 0)
        {
            Volatile.Write(ref active.NextControlAt, now + 500);
            WorldSyncSend(active, WorldSyncOperation.Open);
        }
        else if (LoadSequenceWritable && !active.Installing && now - Volatile.Read(ref active.LastChunkAt) >= 1500)
        {
            Volatile.Write(ref active.NextControlAt, now + 1500);
            WorldSyncSend(active, WorldSyncOperation.Ready);
        }
    }

    private bool WorldSyncAtRead(int type, byte[] body, Stream stream)
    {
        bool recovery = type is Protocol.WorldDigestDown or Protocol.WorldRepairDown;
        if (!recovery && type is not (Protocol.WorldBaselineDown or Protocol.WorldEventDown or Protocol.WorldSyncStatusDown))
            return false;
        if (recovery && !WorldRecoveryWireEffective) return true;
        var active = Volatile.Read(ref _worldReceiver);
        if (active is null || !ReferenceEquals(active.Stream, stream) || !WorldSyncCurrent(active) || active.Failed) return true;
        if (active.Inbox.Writer.TryWrite((type, body)) || recovery) return true;
        active.Failed = true;
        WorldSyncEventDropped(active);
        Console.WriteLine($"MP-WORLD-EVENT-FAULT role={LoadSequenceRole} world={active.Scope.HostWorld} gen={active.Scope.Generation} snapshot={active.Snapshot} kind=receiver-overflow");
        return true;
    }

    private void WorldSyncSend(WorldSyncReceiver receiver, WorldSyncOperation operation, WorldEventChannel channel = 0, ulong id = 0, bool requireScope = true,
        WorldEventFrame? acknowledged = null)
    {
        if (_w213Writer is not { } writer || !ReferenceEquals(_w213Stream, receiver.Stream)) return;
        uint snapshot = operation is WorldSyncOperation.Open or WorldSyncOperation.Ready or WorldSyncOperation.Close ? 0 : receiver.Snapshot;
        if (requireScope && !WorldSyncCurrent(receiver)) return;
        var packet = WorldSyncWire.BuildSync(new(operation, receiver.Scope, snapshot, channel, id));
        try
        {
            writer.Enqueue(packet, requireScope ? () => WorldSyncCurrent(receiver) : null,
                acknowledged is { } item ? () => WorldSyncEventLine(item, "ack-written") : null);
        }
        catch (IOException) { } // the link monitor is responsible for the close
    }

    private async Task RunWorldSyncReceiverAsync(WorldSyncReceiver receiver)
    {
        var ct = receiver.Stop.Token;
        try
        {
            await foreach (var (type, body) in receiver.Inbox.Reader.ReadAllAsync(ct))
            {
                if (!WorldSyncCurrent(receiver) || receiver.Failed) return;
                if (type is Protocol.WorldDigestDown or Protocol.WorldRepairDown)
                {
                    if (WorldRecoveryWireEffective && receiver.Installed)
                        await ReceiveWorldRecoveryAsync(receiver, type, body, ct);
                }
                else if (type == Protocol.WorldSyncStatusDown)
                {
                    if (WorldSyncStatusWire.TryRead(body, out var status) && status.Scope == receiver.Scope &&
                        status.Recipient == _myGhostId && WorldSyncSnapshot(receiver, status.SnapshotId))
                        WorldSyncRelayStats(receiver, status);
                }
                else if (type == Protocol.WorldBaselineDown)
                {
                    if (WorldSyncWire.TryReadBaseline(body, out var chunk) && chunk.Scope == receiver.Scope &&
                        WorldSyncSnapshot(receiver, chunk.SnapshotId))
                        await ReceiveWorldBaselineAsync(receiver, chunk, ct);
                }
                else if (WorldSyncWire.TryReadEvent(body, out var item) && item.Scope == receiver.Scope &&
                    receiver.Snapshot == item.SnapshotId && receiver.Installed)
                {
                    receiver.ServerInstalled = true;
                    await ReceiveWorldEventAsync(receiver, item, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || !WorldSyncCurrent(receiver)) { }
        catch (Exception ex)
        {
            receiver.Failed = true;
            WorldSyncEventDropped(receiver);
            Console.WriteLine($"MP-WORLD-EVENT-FAULT role={LoadSequenceRole} world={receiver.Scope.HostWorld} gen={receiver.Scope.Generation} snapshot={receiver.Snapshot} kind={ex.GetType().Name}");
        }
    }

    private bool WorldSyncSnapshot(WorldSyncReceiver receiver, uint id)
    {
        if (receiver.Snapshot != 0) return receiver.Snapshot == id;
        receiver.Events = new WorldEventReceiveWindow(receiver.Scope, id, capacity: WorldSyncRows);
        receiver.Snapshot = id;
        WorldSyncBeginStats(receiver, id, _myGhostId);
        return true;
    }

    private async Task ReceiveWorldBaselineAsync(WorldSyncReceiver receiver, WorldBaselineChunk chunk, CancellationToken ct)
    {
        if (receiver.Installed)
        {
            WorldSyncSend(receiver, WorldSyncOperation.BaselineApplied);
            return;
        }
        Volatile.Write(ref receiver.LastChunkAt, Environment.TickCount64);
        if (receiver.BaselineAt == 0) receiver.BaselineAt = Stopwatch.GetTimestamp();
        if (!receiver.Sections.TryGetValue(chunk.Section, out var rows))
        {
            if (chunk.Count > WorldSyncRows) { WorldSyncRejectBaseline(receiver, "parts"); return; }
            receiver.Sections.Add(chunk.Section, rows = new WorldBaselineChunk?[chunk.Count]);
        }
        if (rows.Length != chunk.Count) { WorldSyncRejectBaseline(receiver, "part-count"); return; }
        if (rows[chunk.Index] is { } previous)
        {
            if (!previous.Payload.AsSpan().SequenceEqual(chunk.Payload)) WorldSyncRejectBaseline(receiver, "conflicting-part");
            return;
        }
        if (++receiver.Rows > WorldSyncRows + 6 || (receiver.Bytes += chunk.Payload.Length) > WorldSyncBytes)
        { WorldSyncRejectBaseline(receiver, "size"); return; }
        rows[chunk.Index] = chunk;
        if (receiver.Sections.Count != 6 || receiver.Sections.Values.Any(section => section.Any(row => !row.HasValue))) return;

        receiver.Installing = true;
        var complete = receiver.Sections.OrderBy(kv => kv.Key).SelectMany(kv => kv.Value).Select(row => row!.Value).ToArray();
        if (!await InstallWorldBaselineAsync(receiver, complete, ct))
        {
            if (WorldSyncCurrent(receiver)) WorldSyncRejectBaseline(receiver, "application-unconfirmed");
            return;
        }
        if (WorldRecoveryWireEffective) receiver.Recovery.Install(complete);
        receiver.Installed = true;
        receiver.Installing = false;
        WorldSyncBaselineApplied(receiver, chunk.Version, (long)Stopwatch.GetElapsedTime(receiver.BaselineAt).TotalMilliseconds);
        Console.WriteLine($"MP-WORLD-BASELINE role={LoadSequenceRole} world={receiver.Scope.HostWorld} gen={receiver.Scope.Generation} snapshot={receiver.Snapshot} version={chunk.Version} rows={receiver.Rows} result=applied");
        WorldSyncSend(receiver, WorldSyncOperation.BaselineApplied);
    }

    private void WorldSyncRejectBaseline(WorldSyncReceiver receiver, string reason)
    {
        receiver.Failed = true;
        Console.WriteLine($"MP-WORLD-BASELINE role={LoadSequenceRole} world={receiver.Scope.HostWorld} gen={receiver.Scope.Generation} snapshot={receiver.Snapshot} result=unconfirmed reason={reason}");
    }

    private InFrame WorldSyncFrame(WorldSyncScope scope, byte type, byte[] payload, CancellationToken ct) =>
        new(type, payload, Stopwatch.GetTimestamp(), scope.HostWorld)
        {
            LoadScoped = true, LoadGeneration = scope.Generation, LoadCancellation = ct,
            LootGeneration = P1Lifecycle.Current.Generation, MarkerPeerSerial = P5PeerAtRead(type, payload)
        };

    private async Task<bool> InstallWorldBaselineAsync(WorldSyncReceiver receiver, WorldBaselineChunk[] all, CancellationToken ct, bool recovery = false)
    {
        byte authority = _isDamageAuthority ? _myGhostId : _hostModeFrom;
        foreach (var chunk in all)
        {
            if (chunk.Version != 1 || (chunk.Payload.Length != 0 &&
                (!WorldSyncCatalog.TryBaseline(chunk.Payload[0], chunk.Payload.AsSpan(1), authority, out var section, out _) ||
                    section != chunk.Section))) return false;
        }
        _worldBaselineApplying.Value = true;
        try
        {
            if (all.Any(chunk => chunk.Section == WorldBaselineSection.Loot) &&
                !await RetryBaselineStateAsync(receiver, () => StageWorldBaselineAsync(all, ct), ct)) return false;
            foreach (var chunk in all)
            {
                if (chunk.Payload.Length == 0) continue;
                if (!WorldSyncCurrent(receiver) || !LoadSequenceWritable) return false;
                byte type = chunk.Payload[0];
                byte[] payload = chunk.Payload[1..];
                var frame = WorldSyncFrame(receiver.Scope, type, payload, ct);
                if (!await RetryBaselineStateAsync(receiver, () => recovery
                    ? ApplyWorldRecoveryRowAsync(chunk, frame, ct)
                    : ApplyWorldBaselineAsync(chunk, frame, ct), ct)) return false;
            }
        }
        finally { _worldBaselineApplying.Value = false; }
        return WorldSyncCurrent(receiver) && LoadSequenceWritable;
    }

    // Baseline rows express absolute state. The ordered event path below never
    // retries an application that might already have performed a relative effect.
    private async Task<bool> RetryBaselineStateAsync(WorldSyncReceiver receiver, Func<Task<bool>> apply, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 5 && WorldSyncCurrent(receiver) && LoadSequenceWritable; attempt++)
        {
            if (await apply()) return true;
            if (attempt < 4) await Task.Delay(200, ct);
        }
        return false;
    }

    private async Task ReceiveWorldEventAsync(WorldSyncReceiver receiver, WorldEventFrame item, CancellationToken ct)
    {
        var window = receiver.Events!;
        if (WorldSyncCatalog.EventChannel(item.Type, item.Payload) != item.Channel)
        {
            receiver.Failed = true;
            WorldSyncEventDropped(receiver);
            return;
        }
        var offered = window.Offer(item);
        if (offered is WorldEventOffer.DuplicateApplied)
        {
            WorldSyncSend(receiver, WorldSyncOperation.EventApplied, item.Channel, window.LastApplied(item.Channel), acknowledged: item);
            return;
        }
        if (offered is WorldEventOffer.OutOfWindow or WorldEventOffer.Conflict or WorldEventOffer.Invalid)
        {
            receiver.Failed = true;
            WorldSyncEventGap(receiver);
            return;
        }
        while (WorldSyncCurrent(receiver) && LoadSequenceWritable && window.TryTake(item.Channel, out var next))
        {
            var frame = WorldSyncFrame(receiver.Scope, next.Type, next.Payload, ct);
            bool applied = await ApplyWorldEventAsync(next, frame, ct);
            if (!WorldSyncCurrent(receiver)) return;
            if (!applied)
            {
                // A missing application receipt is ambiguous. Keep the in-flight id
                // pinned so a retry cannot repeat an inventory/corpse side effect.
                receiver.Failed = true;
                WorldSyncEventDropped(receiver);
                WorldSyncEventLine(next, "unconfirmed");
                return;
            }
            if (!window.Complete(next.Channel, next.Id, applied: true)) return;
            WorldSyncEventApplied(receiver);
            WorldSyncEventLine(next, "applied");
            WorldSyncSend(receiver, WorldSyncOperation.EventApplied, next.Channel, next.Id, acknowledged: next);
        }
    }

    private void WorldSyncEventLine(WorldEventFrame item, string result)
    {
        string kind = "-";
        uint token = 0;
        if (item.Payload.Length > 1 + Protocol.JoinHeaderLen)
        {
            byte sub = item.Payload[1 + Protocol.JoinHeaderLen];
            if (item.Type == Protocol.LootHostDown) kind = Protocol.LootHostName(sub);
            else if (item.Type == Protocol.ConvDown && sub == Protocol.ConvVote) kind = "vote";
            if (LootMsg.TryDecode(item.Payload.AsSpan(1 + Protocol.JoinHeaderLen), out var message)) token = message.Tok;
        }
        Console.WriteLine($"MP-WORLD-EVENT role={LoadSequenceRole} world={item.Scope.HostWorld} gen={item.Scope.Generation} snapshot={item.SnapshotId} channel={item.Channel} id={item.Id} type=0x{item.Type:X2} kind={kind} token={token} source={item.Payload[0]} result={result}");
    }
}
