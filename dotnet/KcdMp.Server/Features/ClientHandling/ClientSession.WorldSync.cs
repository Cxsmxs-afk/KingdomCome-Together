// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using KcdMp.Wire;

namespace KcdMp.Server.Features.ClientHandling;

public partial class ClientSession
{
    private const int WorldCacheMaxRows = 4096;
    private const int WorldCacheMaxBytes = 4 * 1024 * 1024;
    private readonly object _worldSyncGate = new();
    private readonly CancellationTokenSource _worldSyncStop = new();
    private sealed record WorldImageRow(byte[] Payload, long ReceivedAt, long ArrivalOrder);
    private readonly Dictionary<(WorldBaselineSection Section, string Key), WorldImageRow> _worldImage = new();
    private readonly List<(WorldEventChannel Channel, byte Type, byte[] Body)> _worldBeforeOpen = [];
    private volatile bool _linkWorldSync;
    private Task? _worldSyncTick;
    private bool _worldBaselineApplied, _worldSyncFull, _worldSyncClosed;
    private byte _worldImageAuthority;
    private uint _worldImageEpoch, _nextWorldSnapshot;
    private WorldSyncScope _worldSyncScope;
    private ulong _worldMaxGeneration;
    private uint _worldSnapshot;
    private int _worldImageBytes, _worldLedgerParts, _worldBeforeOpenBytes, _worldBeforeOpenDropped;
    private byte[][] _worldBaseline = [];
    private int _worldBaselineNext;
    private WorldEventSendWindow? _worldEvents;
    private long _worldSent, _worldAcked, _worldRetx, _worldDropped, _worldImageArrival;

    internal void ObserveWorldSyncEpoch(byte authority, uint world)
    {
        if (world == 0) return;
        lock (_worldSyncGate)
        {
            if (_worldImageAuthority == authority && _worldImageEpoch == world) return;
            _worldImageAuthority = authority;
            _worldImageEpoch = world;
            _worldImage.Clear();
            _worldImageBytes = _worldLedgerParts = 0;
            _worldImageArrival = 0;
            ClearWorldBeforeOpenLocked();
            _worldSyncFull = false;
            RetireWorldSyncLocked();
        }
    }

    private void ClearWorldBeforeOpenLocked()
    {
        _worldBeforeOpen.Clear();
        _worldBeforeOpenBytes = _worldBeforeOpenDropped = 0;
    }

    private void RetireWorldSyncLocked()
    {
        _worldSyncScope = default;
        _worldSnapshot = 0;
        _worldBaseline = [];
        _worldBaselineNext = 0;
        _worldBaselineApplied = false;
        _worldEvents = null;
        _worldSent = _worldAcked = _worldRetx = _worldDropped = 0;
        _worldStatusAt = 0;
        ClearWorldRecoveryLocked();
    }

    // All inputs to this method have already passed the ordinary Relay
    // sender/authority/target checks. The wrapper never adds a new source.
    private bool ObserveWorldSyncDown(byte[] packet)
    {
        if (!_linkWorldSync || !WorldSyncCatalog.TryUnwrap(packet, out byte type, out var body)) return false;
        lock (_worldSyncGate)
        {
            if (_worldImageEpoch == 0) return false;
            if (type == Protocol.Disconnect && body.Length == 1)
                ForgetWorldBodyLocked(body[0]);
            if (WorldSyncCatalog.TryBaseline(type, body, _worldImageAuthority, out var section, out string key))
                RememberWorldRow(section, key, type, body, WorldObservationTime(packet));
            if (WorldSyncCatalog.EventChannel(type, body) is not { } channel) return false;
            if (_worldEvents is null)
            {
                if (!_worldSyncClosed)
                {
                    if (_worldBeforeOpen.Count >= WorldCacheMaxRows || _worldBeforeOpenBytes + body.Length > WorldCacheMaxBytes)
                        _worldBeforeOpenDropped++;
                    else { _worldBeforeOpen.Add((channel, type, body.ToArray())); _worldBeforeOpenBytes += body.Length; }
                }
                // Older paths still receive their ordinary wire packet. A
                // negotiated shared-world receiver subscribes before applying.
                return false;
            }
            if (_worldEvents.TryEnqueue(channel, type, body, out _)) return true;
            _worldDropped++;
            _logger.Warning("MP-WORLD-EVENT relay world={World} gen={Gen} snapshot={Snapshot} channel={Channel} type=0x{Type:X2} source={Source} target={Target} result=overflow",
                _worldSyncScope.HostWorld, _worldSyncScope.Generation, _worldSnapshot, channel, type, body[0], Id);
            // Retain every admitted event. A full window is reported as a
            // terminal fault rather than replacing one of its pending rows.
            EnqueueWire(WorldSyncStatusPacket());
            return true;
        }
    }

    private void ForgetWorldBodyLocked(byte source)
    {
        foreach (var slot in _worldImage.Where(row => row.Key.Section == WorldBaselineSection.Body &&
            row.Value.Payload[1] == source).Select(row => row.Key).ToArray())
        {
            _worldImageBytes -= _worldImage[slot].Payload.Length;
            _worldImage.Remove(slot);
        }
    }

    private void RememberWorldRow(WorldBaselineSection section, string key, byte type, ReadOnlySpan<byte> body, long observedAt)
    {
        if (section == WorldBaselineSection.Loot && LootMsg.TryDecode(body[(1 + Protocol.JoinHeaderLen)..], out var msg) &&
            WorldSyncCatalog.TryLedgerPart(msg.Text, out int part, out int count))
        {
            if (part == 1 || _worldLedgerParts != count)
            {
                foreach (var old in _worldImage.Keys.Where(k => k.Section == WorldBaselineSection.Loot).ToArray())
                {
                    _worldImageBytes -= _worldImage[old].Payload.Length;
                    _worldImage.Remove(old);
                }
            }
            _worldLedgerParts = count;
        }
        var slot = (section, key);
        _worldImage.TryGetValue(slot, out var previous);
        int nextBytes = _worldImageBytes - (previous?.Payload.Length ?? 0) + 1 + body.Length;
        if (nextBytes > WorldCacheMaxBytes || (previous is null && _worldImage.Count >= WorldCacheMaxRows))
        { _worldSyncFull = true; return; }
        _worldImage[slot] = new([type, .. body], observedAt, ++_worldImageArrival);
        _worldImageBytes = nextBytes;
    }

    private bool HandleWorldSync(byte[] body)
    {
        if (!_linkWorldSync || !WorldSyncWire.TryReadSync(body, out var command)) return false;
        bool handled = HandleWorldSyncControl(command, out bool baselineAcked);
        if (baselineAcked && _linkWorldRecovery && _conn.RecoveryJoinerReady is { } ready &&
            !_clientHandler.IsDamageAuthority(this)) ready();
        return handled;
    }

    private bool HandleWorldSyncControl(WorldSyncCommand command, out bool baselineAcked)
    {
        baselineAcked = false;
        lock (_worldSyncGate)
        {
            if (command.Operation == WorldSyncOperation.Open)
            {
                if (_worldImageEpoch != command.Scope.HostWorld) return false;
                if (command.Scope.Generation < _worldMaxGeneration) return false;
                if (_worldSnapshot != 0 && _worldSyncScope == command.Scope)
                {
                    if (!_worldBaselineApplied && _worldBaselineNext == _worldBaseline.Length) _worldBaselineNext = 0;
                    return true;
                }
                if (_worldSnapshot != 0 && command.Scope.Generation < _worldSyncScope.Generation) return false;
                RetireWorldSyncLocked();
                _worldMaxGeneration = command.Scope.Generation;
                _worldSyncScope = command.Scope;
                _worldSnapshot = unchecked(++_nextWorldSnapshot);
                if (_worldSnapshot == 0) _worldSnapshot = unchecked(++_nextWorldSnapshot);
                _worldEvents = new WorldEventSendWindow(command.Scope, _worldSnapshot, capacity: 4096);
                _worldSyncClosed = false;
                _worldDropped = _worldBeforeOpenDropped;
                foreach (var pending in _worldBeforeOpen)
                    if (!_worldEvents.TryEnqueue(pending.Channel, pending.Type, pending.Body, out _)) _worldDropped++;
                ClearWorldBeforeOpenLocked();
                return true;
            }
            if (command.Scope != _worldSyncScope || _worldSnapshot == 0 ||
                (command.SnapshotId != _worldSnapshot && command.Operation is not (WorldSyncOperation.Close or WorldSyncOperation.Ready))) return false;
            if (command.Operation == WorldSyncOperation.Ready)
            {
                if (!_worldBaselineApplied && _worldBaseline.Length == 0 && !_worldSyncFull &&
                    _worldImage.Count(p => p.Key.Section == WorldBaselineSection.Loot) == _worldLedgerParts)
                    _worldBaseline = BuildWorldBaselineLocked();
                else if (!_worldBaselineApplied && _worldBaselineNext == _worldBaseline.Length)
                    _worldBaselineNext = 0;
                return true;
            }
            if (command.Operation == WorldSyncOperation.Close)
            {
                if (command.SnapshotId != 0 && command.SnapshotId != _worldSnapshot) return false;
                _worldSyncClosed = true;
                ClearWorldBeforeOpenLocked();
                RetireWorldSyncLocked();
                return true;
            }
            if (command.Operation == WorldSyncOperation.BaselineApplied)
            {
                if (_worldBaselineNext != _worldBaseline.Length || _worldSyncFull || _worldBaseline.Length == 0) return false;
                if (!_worldBaselineApplied)
                {
                    _worldBaselineApplied = true;
                    baselineAcked = true;
                    _logger.Information("MP-WORLD-BASELINE relay world={World} gen={Gen} snapshot={Snapshot} target={Target} result=acked",
                        command.Scope.HostWorld, command.Scope.Generation, command.SnapshotId, Id);
                }
                return true;
            }
            if (!_worldBaselineApplied || _worldEvents is null) return false;
            return AcceptWorldAckLocked(command);
        }
    }

    private byte[][] BuildWorldBaselineLocked()
    {
        if (_worldSyncFull) { _worldDropped++; return []; }
        var packets = new List<byte[]>();
        foreach (var section in Enum.GetValues<WorldBaselineSection>())
        {
            var rows = _worldImage.Where(p => p.Key.Section == section).OrderBy(p => p.Key.Key, StringComparer.Ordinal)
                .Select(p => p.Value.Payload).ToArray();
            if (section == WorldBaselineSection.Loot && rows.Length != _worldLedgerParts)
                rows = [];
            if (rows.Length == 0) rows = [[]];
            AnchorWorldRecoveryLocked(section, rows);
            for (ushort index = 0; index < rows.Length; index++)
                packets.Add(WorldSyncWire.BuildBaseline(new(_worldSyncScope, _worldSnapshot, 1, section,
                    index, checked((ushort)rows.Length), rows[index])));
        }
        return packets.ToArray();
    }

    private bool AcceptWorldAckLocked(WorldSyncCommand command)
    {
        if (_worldEvents is null || !_worldEvents.TryAcknowledge(command, out int retired)) return false;
        if (retired > 0)
        {
            _worldAcked += retired;
            _logger.Information("MP-WORLD-EVENT relay world={World} gen={Gen} snapshot={Snapshot} channel={Channel} id={Event} target={Target} result=acked",
                command.Scope.HostWorld, command.Scope.Generation, command.SnapshotId, command.Channel, command.EventId, Id);
        }
        return true;
    }

    private byte[] WorldSyncStatusPacket() => WorldSyncStatusWire.Build(_worldSyncScope, _worldSnapshot, Id,
        new(_worldSent, _worldAcked, _worldRetx, _worldDropped));

    private async Task RunWorldSyncAsync()
    {
        try
        {
            using var tick = new PeriodicTimer(TimeSpan.FromMilliseconds(40));
            while (await tick.WaitForNextTickAsync(_worldSyncStop.Token))
            {
                lock (_worldSyncGate)
                {
                    if (_worldSnapshot == 0 || !_linkWorldSync) continue;
                    if (_sched is not null && _sched.EventBytes > 64 * 1024) continue;
                    for (int n = 0; n < 24 && _worldBaselineNext < _worldBaseline.Length; ++n)
                        EnqueueWire(_worldBaseline[_worldBaselineNext++]);
                    if (_worldBaselineApplied && _worldEvents is not null)
                    {
                        foreach (var send in _worldEvents.CollectDue(Environment.TickCount64, max: 32))
                        {
                            var item = send.Frame;
                            if (!EnqueueWire(WorldSyncWire.BuildEvent(item)))
                            {
                                _worldDropped++;
                                _logger.Warning("MP-WORLD-EVENT relay world={World} gen={Gen} snapshot={Snapshot} channel={Channel} id={Event} target={Target} result=write-queue-refused",
                                    item.Scope.HostWorld, item.Scope.Generation, item.SnapshotId, item.Channel, item.Id, Id);
                                break;
                            }
                            if (send.IsRetry) _worldRetx++;
                            else _worldSent++;
                            _logger.Information("MP-WORLD-EVENT relay world={World} gen={Gen} snapshot={Snapshot} channel={Channel} id={Event} type=0x{Type:X2} source={Source} target={Target} result={Result}",
                                item.Scope.HostWorld, item.Scope.Generation, item.SnapshotId, item.Channel, item.Id, item.Type, item.Payload[0], Id,
                                send.IsRetry ? "retry" : "sent");
                        }
                    }
                    long now = Environment.TickCount64;
                    SendWorldRecoveryLocked(now);
                    if (now >= _worldStatusAt)
                    {
                        _worldStatusAt = now + 2000;
                        EnqueueWire(WorldSyncStatusPacket());
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_worldSyncStop.IsCancellationRequested) { }
    }

    private long _worldStatusAt;
}
