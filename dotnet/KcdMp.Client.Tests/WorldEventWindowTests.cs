// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public sealed class WorldEventWindowTests
{
    private static readonly WorldSyncScope Scope = new(0x04030201, 0xC8C7C6C5C4C3C2C1ul);
    private const uint SnapshotId = 0x08070605;
    private const WorldEventChannel Loot = WorldEventChannel.Loot, Dialog = WorldEventChannel.Dialog;

    private static WorldEventFrame Event(ulong id, byte marker, WorldEventChannel channel = Loot) =>
        new(Scope, SnapshotId, channel, id, Protocol.LootHostDown, [marker]);

    private static WorldSyncCommand Ack(WorldEventChannel channel, ulong id) =>
        new(WorldSyncOperation.EventApplied, Scope, SnapshotId, channel, id);

    [Fact]
    public void Baseline_wire_preserves_version_world_and_exact_chunk_identity()
    {
        var chunk = new WorldBaselineChunk(Scope, SnapshotId, 1, WorldBaselineSection.Corpse, 1, 2, [0xAB, 0xCD]);
        var packet = WorldSyncWire.BuildBaseline(chunk);
        byte[] expected = [0x8D, 25, 0, 1, 2, 3, 4, 0xC1, 0xC2, 0xC3, 0xC4, 0xC5, 0xC6, 0xC7, 0xC8,
            5, 6, 7, 8, 1, 0, 3, 1, 0, 2, 0, 0xAB, 0xCD];
        Assert.Equal(expected, packet);
        Assert.True(WorldSyncWire.TryReadBaseline(packet.AsSpan(3), out var parsed));
        Assert.Equal(chunk with { Payload = parsed.Payload }, parsed);
        Assert.Equal(chunk.Payload, parsed.Payload);

        packet[19] = 2;
        Assert.False(WorldSyncWire.TryReadBaseline(packet.AsSpan(3), out _));
        Assert.False(WorldSyncWire.TryReadBaseline(expected.AsSpan(3, WorldSyncWire.BaselineHeaderBytes), out _));
        Assert.False(WorldSyncWire.TryReadBaseline(expected.AsSpan(3, 22), out _));
        Assert.Throws<ArgumentException>(() => WorldSyncWire.BuildBaseline(chunk with { Index = 2 }));
        Assert.Throws<ArgumentException>(() => WorldSyncWire.BuildBaseline(chunk with { Payload = new byte[WorldSyncWire.MaxBaselineChunkBytes + 1] }));
        var empty = WorldSyncWire.BuildBaseline(chunk with { Index = 0, Count = 1, Payload = [] });
        Assert.True(WorldSyncWire.TryReadBaseline(empty.AsSpan(3), out parsed));
        Assert.Empty(parsed.Payload);
        var largeWorld = WorldSyncWire.BuildBaseline(chunk with { Index = 4095, Count = 4096 });
        Assert.True(WorldSyncWire.TryReadBaseline(largeWorld.AsSpan(3), out parsed));
        Assert.Equal(4095, parsed.Index);
        Assert.Throws<ArgumentException>(() => WorldSyncWire.BuildBaseline(chunk with { Count = 4097 }));
    }

    [Fact]
    public void Event_wire_carries_old_bytes_untouched_and_correlates_the_full_u64_id()
    {
        var frame = Event(0x1817161514131211ul, 0xDE);
        var packet = WorldSyncWire.BuildEvent(frame);
        Assert.Equal(Protocol.WorldEventDown, packet[0]);
        Assert.Equal(27, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(1)));
        Assert.Equal(new byte[] { (byte)Loot, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, Protocol.LootHostDown, 0xDE }, packet[19..]);
        Assert.True(WorldSyncWire.TryReadEvent(packet.AsSpan(3), out var parsed));
        Assert.Equal(frame with { Payload = parsed.Payload }, parsed);
        Assert.Equal(frame.Payload, parsed.Payload);
        Assert.False(WorldSyncWire.TryReadEvent(packet.AsSpan(3, 25), out _));
        packet[28] = Protocol.WorldSyncUp;
        Assert.False(WorldSyncWire.TryReadEvent(packet.AsSpan(3), out _));
        packet[28] = Protocol.WorldSyncStatusDown;
        Assert.False(WorldSyncWire.TryReadEvent(packet.AsSpan(3), out _));
        Assert.Throws<ArgumentException>(() => WorldSyncWire.BuildEvent(frame with { Scope = Scope with { Generation = 0 } }));
    }

    [Fact]
    public void Sync_acknowledgement_requires_an_exact_positive_scope_snapshot_channel_and_id()
    {
        var command = Ack(Dialog, ulong.MaxValue);
        byte[] packet = WorldSyncWire.BuildSync(command);
        Assert.Equal(Protocol.WorldSyncUp, packet[0]);
        Assert.True(WorldSyncWire.TryReadSync(packet.AsSpan(3), out var parsed));
        Assert.Equal(command, parsed);
        Assert.False(WorldSyncWire.TryReadSync(packet.AsSpan(3, 25), out _));
        packet[16] = 0;
        packet[17] = 0;
        packet[18] = 0;
        packet[19] = 0;
        Assert.False(WorldSyncWire.TryReadSync(packet.AsSpan(3), out _));

        var open = WorldSyncWire.BuildSync(new(WorldSyncOperation.Open, Scope, 0));
        Assert.True(WorldSyncWire.TryReadSync(open.AsSpan(3), out parsed));
        Assert.Equal(WorldSyncOperation.Open, parsed.Operation);
        var ready = WorldSyncWire.BuildSync(new(WorldSyncOperation.Ready, Scope, 0));
        Assert.Equal(WorldSyncWire.SyncBytes, ready.Length - 3);
        Assert.True(WorldSyncWire.TryReadSync(ready.AsSpan(3), out parsed));
        Assert.Equal(WorldSyncOperation.Ready, parsed.Operation);
        Assert.Throws<ArgumentException>(() => WorldSyncWire.BuildSync(parsed with { SnapshotId = SnapshotId }));
        Assert.Throws<ArgumentException>(() => WorldSyncWire.BuildSync(command with { Operation = WorldSyncOperation.BaselineApplied }));
        Assert.Throws<ArgumentException>(() => WorldSyncWire.BuildSync(command with { EventId = 0 }));
    }

    [Fact]
    public void Reordering_and_a_lost_ack_retransmit_without_reapplying_an_effect_or_advancing_prematurely()
    {
        var sender = new WorldEventSendWindow(Scope, SnapshotId, retryMs: 30);
        var receiver = new WorldEventReceiveWindow(Scope, SnapshotId);
        Assert.True(sender.TryEnqueue(Loot, Protocol.LootHostDown, [0xA1], out _));
        Assert.True(sender.TryEnqueue(Loot, Protocol.LootHostDown, [0xA2], out _));
        Assert.False(sender.TryAcknowledge(Ack(Loot, 1), out _));
        var first = sender.CollectDue(5);
        Assert.Equal(new ulong[] { 1, 2 }, first.Select(row => row.Frame.Id));
        Assert.All(first, row => Assert.False(row.IsRetry));
        Assert.Equal(WorldEventOffer.Buffered, receiver.Offer(first[1].Frame));
        Assert.False(receiver.TryTake(Loot, out _));
        Assert.Equal(0ul, receiver.LastApplied(Loot));

        Assert.Empty(sender.CollectDue(34));
        var resend = sender.CollectDue(35);
        Assert.All(resend, row => Assert.True(row.IsRetry));
        Assert.Equal(WorldEventOffer.Buffered, receiver.Offer(resend[0].Frame));
        Assert.Equal(WorldEventOffer.DuplicatePending, receiver.Offer(resend[1].Frame));
        Assert.True(receiver.TryTake(Loot, out var firstApply));
        Assert.Equal(1ul, firstApply.Id);
        Assert.False(receiver.TryTake(Loot, out _));
        Assert.False(receiver.Complete(Loot, 2, true));
        Assert.Equal(0ul, receiver.LastApplied(Loot));
        Assert.True(receiver.Complete(Loot, firstApply.Id, false));
        Assert.True(receiver.TryTake(Loot, out firstApply));
        Assert.True(receiver.Complete(Loot, firstApply.Id, true));
        Assert.Equal(1ul, receiver.LastApplied(Loot));
        Assert.True(receiver.TryTake(Loot, out var secondApply));
        Assert.Equal(2ul, secondApply.Id);
        Assert.True(receiver.Complete(Loot, secondApply.Id, true));

        var afterLostAck = sender.CollectDue(65);
        foreach (var duplicate in afterLostAck)
        {
            Assert.Equal(WorldEventOffer.DuplicateApplied, receiver.Offer(duplicate.Frame));
            Assert.False(receiver.TryTake(Loot, out _));
        }
        Assert.True(sender.TryAcknowledge(Ack(Loot, receiver.LastApplied(Loot)), out int retired));
        Assert.Equal(2, retired);
        Assert.True(sender.TryAcknowledge(Ack(Loot, 1), out retired));
        Assert.Equal(0, retired);
        Assert.Empty(sender.CollectDue(1000));
    }

    [Fact]
    public void Full_windows_report_backpressure_and_retain_the_missing_head_and_sequence_numbers()
    {
        var sender = new WorldEventSendWindow(Scope, SnapshotId, capacity: 2, maxBytes: 2);
        var receiver = new WorldEventReceiveWindow(Scope, SnapshotId, capacity: 2, maxBytes: 2);
        Assert.True(sender.TryEnqueue(Loot, Protocol.LootHostDown, [10], out var first));
        Assert.True(sender.TryEnqueue(Loot, Protocol.LootHostDown, [20], out var second));
        Assert.False(sender.TryEnqueue(Loot, Protocol.LootHostDown, [30], out _));
        Assert.False(sender.TryEnqueue(Dialog, Protocol.ConvDown, [30], out _));
        Assert.Equal(WorldEventOffer.OutOfWindow, receiver.Offer(Event(3, 30)));
        Assert.Equal(WorldEventOffer.Buffered, receiver.Offer(second));
        Assert.Equal(WorldEventOffer.Buffered, receiver.Offer(first));
        Assert.Equal(WorldEventOffer.Conflict, receiver.Offer(Event(1, 99)));
        Assert.True(receiver.TryTake(Loot, out var head));
        Assert.Equal(new byte[] { 10 }, head.Payload);
        Assert.True(receiver.Complete(Loot, 1, true));

        var attempt = Assert.Single(sender.CollectDue(1, max: 1));
        Assert.Equal(1ul, attempt.Frame.Id);
        Assert.False(sender.TryAcknowledge(Ack(Loot, 2), out _));
        Assert.True(sender.TryAcknowledge(Ack(Loot, 1), out _));
        Assert.True(sender.TryEnqueue(Loot, Protocol.LootHostDown, [30], out var third));
        Assert.Equal(3ul, third.Id);
        Assert.Equal(WorldEventOffer.Buffered, receiver.Offer(third));
        Assert.True(receiver.TryTake(Loot, out var next));
        Assert.Equal(2ul, next.Id);
    }

    [Fact]
    public void A_snapshot_or_generation_change_and_mutated_byte_arrays_cannot_replace_queued_data()
    {
        var sender = new WorldEventSendWindow(Scope, SnapshotId);
        var receiver = new WorldEventReceiveWindow(Scope, SnapshotId);
        byte[] data = [11];
        Assert.True(sender.TryEnqueue(Loot, Protocol.LootHostDown, data, out var returned));
        data[0] = 99;
        returned.Payload[0] = 98;
        var attempt = Assert.Single(sender.CollectDue(0));
        Assert.Equal(11, attempt.Frame.Payload[0]);
        Assert.Equal(WorldEventOffer.WrongScope, receiver.Offer(attempt.Frame with { SnapshotId = 999 }));
        Assert.Equal(WorldEventOffer.WrongScope, receiver.Offer(attempt.Frame with { Scope = Scope with { Generation = 1 } }));
        Assert.Equal(WorldEventOffer.Buffered, receiver.Offer(attempt.Frame));
        attempt.Frame.Payload[0] = 97;
        Assert.True(receiver.TryTake(Loot, out var apply));
        Assert.Equal(11, apply.Payload[0]);
        apply.Payload[0] = 96;
        Assert.True(receiver.Complete(Loot, 1, false));
        Assert.True(receiver.TryTake(Loot, out apply));
        Assert.Equal(11, apply.Payload[0]);
        Assert.False(sender.TryAcknowledge(Ack(Loot, 1) with { SnapshotId = 999 }, out _));
        Assert.False(sender.TryAcknowledge(Ack(Loot, 1) with { Scope = Scope with { HostWorld = 999 } }, out _));
        Assert.Equal(11, Assert.Single(sender.CollectDue(350)).Frame.Payload[0]);
    }

    [Fact]
    public void Sends_and_application_leases_advance_independently_in_each_channel()
    {
        var sender = new WorldEventSendWindow(Scope, SnapshotId, capacity: 32);
        Parallel.For(0, 32, i => Assert.True(sender.TryEnqueue(Loot, Protocol.LootHostDown, [(byte)i], out _)));
        Assert.True(sender.TryEnqueue(Dialog, Protocol.ConvDown, [1], out var dialog));
        Assert.Equal(1ul, dialog.Id);
        var first = sender.CollectDue(0, max: 2);
        Assert.Equal(new[] { Loot, Dialog }, first.Select(row => row.Frame.Channel));
        Assert.Equal(new ulong[] { 1, 1 }, first.Select(row => row.Frame.Id));
        var rest = sender.CollectDue(0);
        Assert.Equal(Enumerable.Range(2, 31).Select(i => (ulong)i), rest.Select(row => row.Frame.Id));

        var receiver = new WorldEventReceiveWindow(Scope, SnapshotId);
        Assert.Equal(WorldEventOffer.Buffered, receiver.Offer(first[0].Frame));
        Assert.Equal(WorldEventOffer.Buffered, receiver.Offer(first[1].Frame));
        int owners = 0;
        Parallel.For(0, 32, i => { if (receiver.TryTake(Loot, out _)) Interlocked.Increment(ref owners); });
        Assert.Equal(1, owners);
        Assert.True(receiver.TryTake(Dialog, out var independent));
        Assert.True(receiver.Complete(Dialog, independent.Id, true));
        Assert.Equal(0ul, receiver.LastApplied(Loot));
        Assert.Equal(1ul, receiver.LastApplied(Dialog));
    }
}
