// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public sealed class WorldSyncStatsTests
{
    private static readonly WorldSyncScope Scope = new(0x44332211, 0xFFEEDDCCBBAA9988);

    [Fact]
    public void Relay_status_is_a_bounded_down_only_frame_with_explicit_recipient_scope()
    {
        var counts = new WorldSyncSenderCounts(15, 12, 2, 1);
        var frame = WorldSyncStatusWire.Build(Scope, 0x44332211, 0, counts);
        Assert.Equal(new byte[] { 0x90, 49, 0 }, frame[..3]);
        var body = frame[3..];
        Assert.Equal(49, body.Length);
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44, 0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF }, body[..12]);
        Assert.True(Protocol.IsKnownDown(Protocol.WorldSyncStatusDown));
        Assert.False(Protocol.IsKnownUp(Protocol.WorldSyncStatusDown));
        Assert.True(WorldSyncStatusWire.TryRead(body, out var status));
        Assert.Equal(new WorldSyncStatus(Scope, 0x44332211, 0, counts), status);
        Assert.False(WorldSyncStatusWire.TryRead(body[..^1], out _));
        Assert.False(WorldSyncStatusWire.TryRead([..body, 0], out _));
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(25), 16);
        Assert.False(WorldSyncStatusWire.TryRead(body, out _)); // No ACK ahead of the send count.
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(25), 12);
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(41), -1);
        Assert.False(WorldSyncStatusWire.TryRead(body, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorldSyncStatusWire.Build(default, 1, 0, counts));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorldSyncStatusWire.Build(Scope, 0, 0, counts));
    }

    [Fact]
    public void Only_the_current_recipient_snapshot_can_combine_install_apply_and_relay_evidence()
    {
        var metrics = new WorldSyncMetrics();
        Assert.Null(metrics.Snapshot);
        object receiver = new();
        metrics.Track(receiver);
        metrics.Begin(receiver, Scope, 1, 0);
        metrics.EventApplied(receiver, Scope, 1);
        Assert.Equal(0, metrics.Snapshot!.Value.Applied);
        var before = metrics.Snapshot.Value.Fields().ToDictionary();
        Assert.DoesNotContain("baseline_version", before.Keys);
        Assert.DoesNotContain("evt_sent", before.Keys);
        metrics.BaselineApplied(receiver, Scope, 2, 1, 9);
        Assert.Equal(0, metrics.Snapshot.Value.BaselineVersion);
        metrics.BaselineApplied(receiver, Scope, 1, 1, 0);
        metrics.EventApplied(receiver, Scope, 1);
        metrics.Begin(receiver, Scope, 1, 0);
        metrics.RecordSender(receiver, new WorldSyncStatus(Scope, 1, 1, new(1, 1, 0, 0)));
        Assert.Null(metrics.Snapshot.Value.Sender);
        metrics.RecordSender(receiver, new WorldSyncStatus(Scope, 1, 0, new(1, 1, 2, 0)));
        metrics.RecordSender(receiver, new WorldSyncStatus(Scope, 1, 0, new(1, 0, 2, 0)));
        var result = metrics.Snapshot.Value;
        Assert.Equal(1, result.BaselineVersion);
        Assert.Equal(0, result.BaselineMs);
        Assert.Equal(1, result.Applied);
        Assert.Equal(new WorldSyncSenderCounts(1, 1, 2, 0), result.Sender);
        Assert.Equal(1, metrics.EventFault(receiver, Scope, 1, gap: true)!.Value.Gap);
        Assert.Equal(1, metrics.EventFault(receiver, Scope, 1, gap: false)!.Value.Dropped);
        Assert.Equal(1, metrics.RecordSender(receiver, new WorldSyncStatus(Scope, 1, 0, new(1, 1, 2, 1)))!.Value.Sender!.Value.Dropped);
        metrics.Begin(receiver, Scope, 2, 0);
        metrics.EventApplied(receiver, Scope, 1);
        Assert.Null(metrics.EventFault(receiver, Scope, 1, gap: true));
        Assert.Null(metrics.RecordSender(receiver, new WorldSyncStatus(Scope, 1, 0, new(1, 1, 2, 1))));
        Assert.Equal(0, metrics.Snapshot.Value.Applied);
        Assert.Equal(0, metrics.Snapshot.Value.Gap);
        Assert.Null(metrics.Snapshot.Value.Sender);
    }

    [Fact]
    public void A_new_receiver_reusing_wire_ids_cannot_inherit_a_previous_install_or_ack()
    {
        var metrics = new WorldSyncMetrics();
        object oldReceiver = new(), newReceiver = new();
        metrics.Track(oldReceiver);
        metrics.Begin(oldReceiver, Scope, 1, 0);
        metrics.BaselineApplied(oldReceiver, Scope, 1, 1, 12);
        long retired = metrics.Retire(oldReceiver)!.Value.Receiver;
        Assert.Null(metrics.Snapshot);
        metrics.Track(newReceiver);
        metrics.Begin(newReceiver, Scope, 1, 0);
        metrics.BaselineApplied(oldReceiver, Scope, 1, 1, 24);
        metrics.RecordSender(oldReceiver, new WorldSyncStatus(Scope, 1, 0, new(1, 1, 0, 0)));
        Assert.Null(metrics.Retire(oldReceiver));
        Assert.Equal(0, metrics.Snapshot!.Value.BaselineVersion);
        Assert.Null(metrics.Snapshot.Value.Sender);
        Assert.NotEqual(retired, metrics.Snapshot.Value.Receiver);
    }
}
