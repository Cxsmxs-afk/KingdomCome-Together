// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public sealed class WorldRecoveryWireTests
{
    private static readonly WorldSyncScope Scope = new(73, 9);
    private const uint Snapshot = 5;
    private const WorldBaselineSection Section = WorldBaselineSection.Loot;

    [Fact]
    public void Digest_has_a_canonical_framed_encoding_and_independent_capability()
    {
        var hash = Hash([[0x11, 0, 0x22], [0x33]]);
        const string expected = "1093E2773EF7F4616575D004FC9D7D70DB4C2426514C8B43EBB1FFC89BB8A1F9";
        Assert.Equal(expected, hash.ToString());
        var digest = new WorldRecoveryDigest(Scope, Snapshot, Section, 11, hash);
        byte[] frame = WorldRecoveryWire.BuildDigest(digest);
        Assert.Equal(new byte[] { 0x91, 57, 0 }, frame[..3]);
        Assert.Equal(Convert.FromHexString(expected), frame[28..]);
        Assert.True(WorldRecoveryWire.TryReadDigest(frame.AsSpan(3), out var read));
        Assert.Equal(digest, read);

        var request = new WorldRecoveryRequest(read);
        byte[] query = WorldRecoveryWire.BuildRequest(request);
        Assert.Equal(new byte[] { 0x92, 57, 0 }, query[..3]);
        Assert.Equal(frame[3..], query[3..]);
        Assert.True(WorldRecoveryWire.TryReadRequest(query.AsSpan(3), out var result));
        Assert.Equal(request, result);

        Assert.Equal(0x0040, Protocol.LinkCapWorldRecovery);
        Assert.True((Protocol.LinkCapsAll & Protocol.LinkCapWorldRecovery) != 0);
        Assert.True(Protocol.IsKnownDown(Protocol.WorldDigestDown));
        Assert.True(Protocol.IsKnownDown(Protocol.WorldRepairDown));
        Assert.False(Protocol.IsKnownUp(Protocol.WorldDigestDown));
        Assert.False(Protocol.IsKnownDown(Protocol.WorldRepairUp));
        Assert.True(Protocol.IsUpLenOk(Protocol.WorldRepairUp, 57));
        Assert.False(Protocol.IsUpLenOk(Protocol.WorldRepairUp, 56));
        Assert.False(Protocol.IsUpLenOk(Protocol.WorldRepairUp, 58));
    }

    [Fact]
    public void Digest_binds_scope_section_chunk_order_and_length_without_concatenation_aliases()
    {
        byte[][] ordered = [[1, 2], [3]];
        var original = Hash(ordered);
        Assert.NotEqual(original, Hash([[1], [2, 3]]));
        Assert.NotEqual(original, Hash([[3], [1, 2]]));
        Assert.NotEqual(original, Hash(ordered, scope: Scope with { HostWorld = 74 }));
        Assert.NotEqual(original, Hash(ordered, scope: Scope with { Generation = 10 }));
        Assert.NotEqual(original, Hash(ordered, snapshot: 6));
        Assert.NotEqual(original, Hash(ordered, section: WorldBaselineSection.Corpse));
        Assert.NotEqual(original, Hash([[]]));
        Assert.False(WorldRecoveryWire.TryComputeHash(default, Snapshot, Section, ordered, out _));
        Assert.False(WorldRecoveryWire.TryComputeHash(Scope, 0, Section, ordered, out _));
        Assert.False(WorldRecoveryWire.TryComputeHash(Scope, Snapshot, 0, ordered, out _));
        Assert.False(WorldRecoveryWire.TryComputeHash(Scope, Snapshot, Section, [], out _));
        Assert.False(WorldRecoveryWire.TryComputeHash(Scope, Snapshot, Section, [[1], []], out _));
        Assert.False(WorldRecoveryWire.TryComputeHash(Scope, Snapshot, Section, [null!], out _));
    }

    [Fact]
    public void Repair_carries_only_one_checked_section_and_rejects_truncated_or_overbudget_payloads()
    {
        var digest = new WorldRecoveryDigest(Scope, Snapshot, Section, 11, Hash([[91, 92, 93], [2]]));
        var frame = WorldRecoveryWire.BuildChunk(new(digest, 0, 2, [91, 92, 93]));
        Assert.Equal(new byte[] { 0x93, 64, 0 }, frame[..3]);
        Assert.True(WorldRecoveryWire.TryReadChunk(frame.AsSpan(3), out var parsed));
        Assert.Equal(digest, parsed.Digest);
        Assert.Equal(0, parsed.Index);
        Assert.Equal(2, parsed.Count);
        Assert.Equal(new byte[] { 91, 92, 93 }, parsed.Payload);
        Assert.False(WorldRecoveryWire.TryReadChunk(frame.AsSpan(3, WorldRecoveryWire.ChunkHeaderBytes - 1), out _));
        Assert.False(WorldRecoveryWire.TryReadChunk(frame.AsSpan(3, WorldRecoveryWire.ChunkHeaderBytes), out _));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(3 + WorldRecoveryWire.DigestBytes), 2);
        Assert.False(WorldRecoveryWire.TryReadChunk(frame.AsSpan(3), out _));
        Assert.Throws<ArgumentException>(() => WorldRecoveryWire.BuildChunk(new(digest, 0, 0, [1])));
        Assert.Throws<ArgumentException>(() => WorldRecoveryWire.BuildChunk(new(digest, 0, 2, [])));
        Assert.Throws<ArgumentException>(() => WorldRecoveryWire.BuildChunk(new(digest, 0, 1, new byte[WorldSyncWire.MaxBaselineChunkBytes + 1])));
        var empty = WorldRecoveryWire.BuildChunk(new(digest with { Hash = Hash([[]]) }, 0, 1, []));
        Assert.True(WorldRecoveryWire.TryReadChunk(empty.AsSpan(3), out var cleared));
        Assert.Empty(cleared.Payload);

        var maxRow = new byte[WorldSyncWire.MaxBaselineChunkBytes];
        var whole = Enumerable.Repeat(maxRow, WorldRecoveryWire.MaxSectionBytes / maxRow.Length).ToArray();
        Assert.True(WorldRecoveryWire.TryComputeHash(Scope, Snapshot, Section, whole, out _));
        Assert.False(WorldRecoveryWire.TryComputeHash(Scope, Snapshot, Section, [.. whole, [1]], out _));
        Assert.False(WorldRecoveryWire.TryComputeHash(Scope, Snapshot, Section, [new byte[maxRow.Length + 1]], out _));
    }

    [Fact]
    public void Malformed_or_nested_recovery_commands_never_become_ordered_events()
    {
        var digest = new WorldRecoveryDigest(Scope, Snapshot, Section, 11, Hash([[]]));
        byte[] body = WorldRecoveryWire.BuildDigest(digest)[3..];
        Assert.False(WorldRecoveryWire.TryReadDigest(body[..^1], out _));
        Assert.False(WorldRecoveryWire.TryReadRequest([.. body, 0], out _));
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(17), 0);
        Assert.False(WorldRecoveryWire.TryReadDigest(body, out _));
        Assert.Throws<ArgumentException>(() => WorldRecoveryWire.BuildRequest(new(digest with { Revision = 0 })));
        Assert.Throws<ArgumentException>(() => WorldRecoveryWire.BuildDigest(digest with { Section = 0 }));

        foreach (byte type in new[] { Protocol.WorldDigestDown, Protocol.WorldRepairUp, Protocol.WorldRepairDown })
        {
            Assert.False(WorldSyncWire.IsEventType(type));
            var item = new WorldEventFrame(Scope, Snapshot, WorldEventChannel.Loot, 1, type, [1]);
            Assert.Throws<ArgumentException>(() => WorldSyncWire.BuildEvent(item));
            byte[] eventBody = WorldSyncWire.BuildEvent(item with { Type = Protocol.LootHostDown })[3..];
            eventBody[25] = type;
            Assert.False(WorldSyncWire.TryReadEvent(eventBody, out _));
        }
    }

    private static WorldRecoveryHash Hash(byte[][] payloads, WorldSyncScope? scope = null, uint snapshot = Snapshot,
        WorldBaselineSection section = Section)
    {
        Assert.True(WorldRecoveryWire.TryComputeHash(scope ?? Scope, snapshot, section, payloads, out var hash));
        return hash;
    }
}
