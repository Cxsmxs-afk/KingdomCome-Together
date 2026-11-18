// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using KcdMp.Wire;

namespace KcdMp.Client;

public partial class GameBridge
{
    private readonly WorldSyncMetrics _worldSyncMetrics = new();

    private void WorldSyncTrackStatsReceiver(WorldSyncReceiver receiver) => _worldSyncMetrics.Track(receiver);

    private void WorldSyncRetireStatsReceiver(WorldSyncReceiver receiver)
    {
        if (_worldSyncMetrics.Retire(receiver) is { } sample)
            Console.WriteLine($"MP-WORLD-EVENT-SCOPE origin=agent role={LoadSequenceRole} section=events evt_scope_active=0 {T3FormatStats(sample.Fields())}");
    }

    private void WorldSyncBeginStats(WorldSyncReceiver receiver, uint snapshotId, byte recipient) =>
        _worldSyncMetrics.Begin(receiver, receiver.Scope, snapshotId, recipient);

    private void WorldSyncBaselineApplied(WorldSyncReceiver receiver, ushort version, long elapsedMs) =>
        _worldSyncMetrics.BaselineApplied(receiver, receiver.Scope, receiver.Snapshot, version, elapsedMs);

    private void WorldSyncEventApplied(WorldSyncReceiver receiver) =>
        _worldSyncMetrics.EventApplied(receiver, receiver.Scope, receiver.Snapshot);

    private void WorldSyncEventGap(WorldSyncReceiver receiver) =>
        WorldSyncLogEventFault(_worldSyncMetrics.EventFault(receiver, receiver.Scope, receiver.Snapshot, gap: true));

    private void WorldSyncEventDropped(WorldSyncReceiver receiver) =>
        WorldSyncLogEventFault(_worldSyncMetrics.EventFault(receiver, receiver.Scope, receiver.Snapshot, gap: false));

    private void WorldSyncRelayStats(WorldSyncReceiver receiver, WorldSyncStatus status) =>
        WorldSyncLogEventFault(_worldSyncMetrics.RecordSender(receiver, status));

    private void WorldSyncLogEventFault(WorldSyncMetrics.Reading? failure)
    {
        if (failure is { } sample)
            Console.WriteLine($"MP-WORLD-EVENT-FAULT origin=agent role={LoadSequenceRole} section=events {T3FormatStats(sample.Fields())}");
    }

    private string? T3WorldSyncStats() => _worldSyncMetrics.Snapshot is { } sample ? T3FormatStats(sample.Fields()) : null;
}

internal sealed class WorldSyncMetrics
{
    internal readonly record struct Reading(WorldSyncScope Scope, uint SnapshotId, byte Recipient, long Receiver)
    {
        public ushort BaselineVersion { get; init; }
        public long BaselineMs { get; init; }
        public long Applied { get; init; }
        public long Gap { get; init; }
        public long Dropped { get; init; }
        public WorldSyncSenderCounts? Sender { get; init; }

        public IEnumerable<KeyValuePair<string, long>> Fields()
        {
            yield return KeyValuePair.Create("evt_world", (long)Scope.HostWorld);
            yield return KeyValuePair.Create("evt_gen", (long)Math.Min(Scope.Generation, (ulong)long.MaxValue));
            yield return KeyValuePair.Create("evt_snapshot", (long)SnapshotId);
            yield return KeyValuePair.Create("evt_recipient", (long)Recipient);
            yield return KeyValuePair.Create("evt_receiver", Receiver);
            if (BaselineVersion != 0)
            {
                yield return KeyValuePair.Create("baseline_version", (long)BaselineVersion);
                yield return KeyValuePair.Create("baseline_ms", BaselineMs);
            }
            yield return KeyValuePair.Create("evt_applied", Applied);
            yield return KeyValuePair.Create("evt_gap", Gap);
            yield return KeyValuePair.Create("evt_dropped", Dropped);
            if (Sender is { } relay)
            {
                yield return KeyValuePair.Create("evt_sent", relay.Sent);
                yield return KeyValuePair.Create("evt_acked", relay.Acked);
                yield return KeyValuePair.Create("evt_retx", relay.Retx);
                yield return KeyValuePair.Create("evt_sender_dropped", relay.Dropped);
            }
        }
    }

    private readonly object _gate = new();
    private Reading? _reading;
    private object? _receiver;
    private long _receiverNumber;

    public Reading? Snapshot { get { lock (_gate) return _reading; } }

    // Wire snapshots can reuse their ID after reconnecting to a different Relay connection.
    public void Track(object receiver)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_receiver, receiver)) return;
            _receiver = receiver;
            _receiverNumber = Random.Shared.NextInt64(1, long.MaxValue);
            _reading = null;
        }
    }

    public Reading? Retire(object receiver)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_receiver, receiver)) return null;
            var previous = _reading;
            _receiver = null;
            _reading = null;
            return previous;
        }
    }

    public void Begin(object receiver, WorldSyncScope scope, uint snapshotId, byte recipient)
    {
        if (!scope.IsValid || snapshotId == 0) return;
        lock (_gate)
        {
            if (!ReferenceEquals(_receiver, receiver)) return;
            if (_reading is not { } old || old.Scope != scope || old.SnapshotId != snapshotId || old.Recipient != recipient)
                _reading = new Reading(scope, snapshotId, recipient, _receiverNumber);
        }
    }

    public void BaselineApplied(object receiver, WorldSyncScope scope, uint snapshotId, ushort version, long elapsedMs)
    {
        if (version == 0 || elapsedMs < 0) return;
        lock (_gate)
        {
            if (Matching(receiver, scope, snapshotId) is { BaselineVersion: 0 } sample)
                _reading = sample with { BaselineVersion = version, BaselineMs = elapsedMs };
        }
    }

    public void EventApplied(object receiver, WorldSyncScope scope, uint snapshotId)
    {
        lock (_gate)
        {
            if (Matching(receiver, scope, snapshotId) is { BaselineVersion: > 0 } sample)
                _reading = sample with { Applied = sample.Applied + 1 };
        }
    }

    public Reading? EventFault(object receiver, WorldSyncScope scope, uint snapshotId, bool gap)
    {
        lock (_gate)
        {
            if (Matching(receiver, scope, snapshotId) is not { } sample) return null;
            _reading = gap ? sample with { Gap = sample.Gap + 1 } : sample with { Dropped = sample.Dropped + 1 };
            return _reading;
        }
    }

    // Only the current recipient can attach Relay writer evidence to local application counters.
    public Reading? RecordSender(object receiver, WorldSyncStatus status)
    {
        lock (_gate)
        {
            if (Matching(receiver, status.Scope, status.SnapshotId) is not { } sample || sample.Recipient != status.Recipient)
                return null;
            var previous = sample.Sender.GetValueOrDefault();
            var counts = status.Counts;
            if (!counts.IsValid || counts.Sent < previous.Sent || counts.Acked < previous.Acked ||
                counts.Retx < previous.Retx || counts.Dropped < previous.Dropped)
                return null;
            _reading = sample with { Sender = counts };
            return counts.Dropped > previous.Dropped ? _reading : null;
        }
    }

    // Call while holding _gate.
    private Reading? Matching(object receiver, WorldSyncScope scope, uint snapshotId) =>
        ReferenceEquals(_receiver, receiver) && _reading is { } sample && sample.Scope == scope && sample.SnapshotId == snapshotId ? sample : null;
}
