// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
namespace KcdMp.Wire;

public enum WorldEventOffer : byte { Buffered, DuplicateApplied, DuplicatePending, WrongScope, OutOfWindow, Conflict, Invalid }

public readonly record struct WorldEventTransmission(WorldEventFrame Frame, bool IsRetry);

/// <summary>A reliable per-recipient send window. Event IDs start at one independently in each channel.</summary>
public sealed class WorldEventSendWindow
{
    internal const int Channels = (int)WorldEventChannel.Dialog;
    internal const int DefaultBytes = 4 * 1024 * 1024;
    private sealed class Pending(WorldEventFrame frame)
    {
        public readonly WorldEventFrame Frame = frame;
        public long? AttemptedAtMs;
    }

    private sealed class Lane
    {
        public readonly SortedDictionary<ulong, Pending> Pending = new();
        public ulong Enqueued, Attempted, Acknowledged;
    }

    private readonly object _gate = new();
    private readonly Lane[] _lanes = Enumerable.Range(0, Channels).Select(_ => new Lane()).ToArray();
    private readonly int _capacity, _retryMs, _maxBytes;
    private int _bytes, _nextLane;
    public WorldSyncScope Scope { get; }
    public uint SnapshotId { get; }

    public WorldEventSendWindow(WorldSyncScope scope, uint snapshotId, int capacity = 256, int retryMs = 350, int maxBytes = DefaultBytes)
    {
        ValidateLimits(scope, snapshotId, capacity, maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retryMs);
        Scope = scope;
        SnapshotId = snapshotId;
        _capacity = capacity;
        _retryMs = retryMs;
        _maxBytes = maxBytes;
    }

    /// <summary>Backpressure preserves both the queued frames and the next sequence number.</summary>
    public bool TryEnqueue(WorldEventChannel channel, byte type, ReadOnlySpan<byte> payload, out WorldEventFrame frame)
    {
        frame = default;
        if (!WorldSyncWire.IsChannel(channel) || !WorldSyncWire.IsEventType(type) || payload.Length > WorldSyncWire.MaxEventPayloadBytes)
            return false;
        lock (_gate)
        {
            var lane = _lanes[LaneIndex(channel)];
            if (lane.Pending.Count >= _capacity || lane.Enqueued == ulong.MaxValue || payload.Length > _maxBytes - _bytes)
                return false;
            ulong id = ++lane.Enqueued;
            var stored = new WorldEventFrame(Scope, SnapshotId, channel, id, type, payload.ToArray());
            lane.Pending.Add(id, new(stored));
            _bytes += payload.Length;
            frame = Copy(stored);
            return true;
        }
    }

    /// <summary>Reserves attempted sends so an ACK that arrives during the physical write can be accepted.</summary>
    public IReadOnlyList<WorldEventTransmission> CollectDue(long nowMs, int max = 64)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nowMs);
        ArgumentOutOfRangeException.ThrowIfNegative(max);
        var attempts = new List<WorldEventTransmission>();
        lock (_gate)
        {
            int idleLanes = 0;
            while (attempts.Count < max && idleLanes < Channels)
            {
                var lane = _lanes[_nextLane];
                _nextLane = (_nextLane + 1) % Channels;
                var pending = lane.Pending.Values.FirstOrDefault(row => row.AttemptedAtMs is not long before ||
                    (nowMs >= before && nowMs - before >= _retryMs));
                if (pending is null)
                {
                    idleLanes++;
                    continue;
                }
                idleLanes = 0;
                attempts.Add(new(Copy(pending.Frame), pending.AttemptedAtMs.HasValue));
                pending.AttemptedAtMs = nowMs;
                lane.Attempted = Math.Max(lane.Attempted, pending.Frame.Id);
            }
        }
        return attempts;
    }

    public bool TryAcknowledge(WorldSyncCommand acknowledgement, out int retired)
    {
        retired = 0;
        return acknowledgement.Operation == WorldSyncOperation.EventApplied && acknowledgement.Scope == Scope &&
            acknowledgement.SnapshotId == SnapshotId && TryAcknowledge(acknowledgement.Channel, acknowledgement.EventId, out retired);
    }

    public bool TryAcknowledge(WorldEventChannel channel, ulong eventId) => TryAcknowledge(channel, eventId, out _);

    public bool TryAcknowledge(WorldEventChannel channel, ulong eventId, out int retired)
    {
        retired = 0;
        if (!WorldSyncWire.IsChannel(channel) || eventId == 0) return false;
        lock (_gate)
        {
            var lane = _lanes[LaneIndex(channel)];
            if (eventId > lane.Attempted) return false;
            if (eventId <= lane.Acknowledged) return true;
            var finished = lane.Pending.Keys.TakeWhile(id => id <= eventId).ToArray();
            foreach (ulong id in finished)
            {
                lane.Pending.Remove(id, out var entry);
                _bytes -= entry!.Frame.Payload.Length;
            }
            lane.Acknowledged = eventId;
            retired = finished.Length;
            return true;
        }
    }

    internal static int LaneIndex(WorldEventChannel channel) => (int)channel - 1;
    internal static WorldEventFrame Copy(WorldEventFrame frame) => frame with { Payload = frame.Payload.ToArray() };

    internal static void ValidateLimits(WorldSyncScope scope, uint snapshotId, int capacity, int maxBytes)
    {
        if (!scope.IsValid) throw new ArgumentException("A positive world and generation are required.", nameof(scope));
        ArgumentOutOfRangeException.ThrowIfZero(snapshotId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, 4096);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxBytes, DefaultBytes);
    }
}

/// <summary>An ordered apply window. Only successful completion advances the cumulative acknowledgement.</summary>
public sealed class WorldEventReceiveWindow
{
    private sealed class Lane
    {
        public readonly Dictionary<ulong, WorldEventFrame> Pending = new();
        public ulong Applied, InApply;
    }

    private readonly object _gate = new();
    private readonly Lane[] _lanes = Enumerable.Range(0, WorldEventSendWindow.Channels).Select(_ => new Lane()).ToArray();
    private readonly int _capacity, _maxBytes;
    private int _bytes;
    public WorldSyncScope Scope { get; }
    public uint SnapshotId { get; }

    public WorldEventReceiveWindow(WorldSyncScope scope, uint snapshotId, int capacity = 256, int maxBytes = WorldEventSendWindow.DefaultBytes)
    {
        WorldEventSendWindow.ValidateLimits(scope, snapshotId, capacity, maxBytes);
        Scope = scope;
        SnapshotId = snapshotId;
        _capacity = capacity;
        _maxBytes = maxBytes;
    }

    public WorldEventOffer Offer(WorldEventFrame frame)
    {
        if (frame.Scope != Scope || frame.SnapshotId != SnapshotId) return WorldEventOffer.WrongScope;
        if (!WorldSyncWire.IsChannel(frame.Channel) || !WorldSyncWire.IsEventType(frame.Type) || frame.Id == 0 ||
            frame.Payload is null || frame.Payload.Length > WorldSyncWire.MaxEventPayloadBytes) return WorldEventOffer.Invalid;
        lock (_gate)
        {
            var lane = _lanes[WorldEventSendWindow.LaneIndex(frame.Channel)];
            if (frame.Id <= lane.Applied) return WorldEventOffer.DuplicateApplied;
            if (lane.Pending.TryGetValue(frame.Id, out var existing))
                return existing.Type == frame.Type && existing.Payload.AsSpan().SequenceEqual(frame.Payload)
                    ? WorldEventOffer.DuplicatePending : WorldEventOffer.Conflict;
            if (frame.Id - lane.Applied > (ulong)_capacity || frame.Payload.Length > _maxBytes - _bytes)
                return WorldEventOffer.OutOfWindow;
            lane.Pending.Add(frame.Id, WorldEventSendWindow.Copy(frame));
            _bytes += frame.Payload.Length;
            return WorldEventOffer.Buffered;
        }
    }

    public bool TryTake(WorldEventChannel channel, out WorldEventFrame frame)
    {
        frame = default;
        if (!WorldSyncWire.IsChannel(channel)) return false;
        lock (_gate)
        {
            var lane = _lanes[WorldEventSendWindow.LaneIndex(channel)];
            if (lane.InApply != 0 || lane.Applied == ulong.MaxValue || !lane.Pending.TryGetValue(lane.Applied + 1, out var next))
                return false;
            lane.InApply = next.Id;
            frame = WorldEventSendWindow.Copy(next);
            return true;
        }
    }

    /// <summary>Set applied false only when the engine confirms no effect, so the exact event can be attempted again.</summary>
    public bool Complete(WorldEventChannel channel, ulong eventId, bool applied)
    {
        if (!WorldSyncWire.IsChannel(channel) || eventId == 0) return false;
        lock (_gate)
        {
            var lane = _lanes[WorldEventSendWindow.LaneIndex(channel)];
            if (lane.InApply != eventId) return false;
            if (applied)
            {
                lane.Pending.Remove(eventId, out var completed);
                _bytes -= completed.Payload.Length;
                lane.Applied = eventId;
            }
            lane.InApply = 0;
            return true;
        }
    }

    public ulong LastApplied(WorldEventChannel channel)
    {
        if (!WorldSyncWire.IsChannel(channel)) return 0;
        lock (_gate) return _lanes[WorldEventSendWindow.LaneIndex(channel)].Applied;
    }
}
