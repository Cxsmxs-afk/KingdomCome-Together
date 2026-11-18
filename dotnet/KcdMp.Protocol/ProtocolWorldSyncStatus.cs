// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;

namespace KcdMp.Wire;

public static partial class Protocol
{
    public const byte WorldSyncStatusDown = 0x90;
}

public readonly record struct WorldSyncSenderCounts(long Sent, long Acked, long Retx, long Dropped)
{
    public bool IsValid => Sent >= 0 && Acked >= 0 && Acked <= Sent && Retx >= 0 && Dropped >= 0;
}

public readonly record struct WorldSyncStatus(WorldSyncScope Scope, uint SnapshotId, byte Recipient, WorldSyncSenderCounts Counts);

/// <summary>Relay-owned cumulative writer counters for one recipient and baseline snapshot.</summary>
public static class WorldSyncStatusWire
{
    public const int BodyBytes = 49;

    public static byte[] Build(WorldSyncScope scope, uint snapshotId, byte recipient, WorldSyncSenderCounts counts)
    {
        if (!scope.IsValid) throw new ArgumentOutOfRangeException(nameof(scope));
        if (snapshotId == 0) throw new ArgumentOutOfRangeException(nameof(snapshotId));
        if (!counts.IsValid) throw new ArgumentOutOfRangeException(nameof(counts));
        byte[] frame = PresentationTimeWire.Frame(Protocol.WorldSyncStatusDown, BodyBytes);
        var body = frame.AsSpan(3);
        BinaryPrimitives.WriteUInt32LittleEndian(body, scope.HostWorld);
        BinaryPrimitives.WriteUInt64LittleEndian(body[4..], scope.Generation);
        BinaryPrimitives.WriteUInt32LittleEndian(body[12..], snapshotId);
        body[16] = recipient;
        BinaryPrimitives.WriteInt64LittleEndian(body[17..], counts.Sent);
        BinaryPrimitives.WriteInt64LittleEndian(body[25..], counts.Acked);
        BinaryPrimitives.WriteInt64LittleEndian(body[33..], counts.Retx);
        BinaryPrimitives.WriteInt64LittleEndian(body[41..], counts.Dropped);
        return frame;
    }

    public static bool TryRead(ReadOnlySpan<byte> body, out WorldSyncStatus status)
    {
        status = default;
        if (body.Length != BodyBytes) return false;
        var scope = new WorldSyncScope(BinaryPrimitives.ReadUInt32LittleEndian(body), BinaryPrimitives.ReadUInt64LittleEndian(body[4..]));
        uint snapshot = BinaryPrimitives.ReadUInt32LittleEndian(body[12..]);
        var counts = new WorldSyncSenderCounts(BinaryPrimitives.ReadInt64LittleEndian(body[17..]),
            BinaryPrimitives.ReadInt64LittleEndian(body[25..]), BinaryPrimitives.ReadInt64LittleEndian(body[33..]),
            BinaryPrimitives.ReadInt64LittleEndian(body[41..]));
        if (!scope.IsValid || snapshot == 0 || !counts.IsValid) return false;
        status = new(scope, snapshot, body[16], counts);
        return true;
    }
}
