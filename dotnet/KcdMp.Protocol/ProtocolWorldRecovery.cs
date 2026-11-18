// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace KcdMp.Wire;

public static partial class Protocol
{
    public const ushort LinkCapWorldRecovery = 0x0040;
    public const byte WorldDigestDown = 0x91, WorldRepairUp = 0x92, WorldRepairDown = 0x93;
}

/// <summary>SHA-256 bytes held by value so a later buffer write cannot change an accepted digest.</summary>
public readonly record struct WorldRecoveryHash(ulong A, ulong B, ulong C, ulong D)
{
    public const int Bytes = 32;

    internal static WorldRecoveryHash Read(ReadOnlySpan<byte> bytes) =>
        new(BinaryPrimitives.ReadUInt64LittleEndian(bytes), BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]));

    internal void Write(Span<byte> bytes)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, A);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], B);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], C);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[24..], D);
    }

    public override string ToString()
    {
        Span<byte> bytes = stackalloc byte[Bytes];
        Write(bytes);
        return Convert.ToHexString(bytes);
    }
}

public readonly record struct WorldRecoveryDigest(WorldSyncScope Scope, uint SnapshotId, WorldBaselineSection Section,
    ulong Revision, WorldRecoveryHash Hash);

public readonly record struct WorldRecoveryRequest(WorldRecoveryDigest Expected);

public readonly record struct WorldRecoveryChunk(WorldRecoveryDigest Digest, ushort Index, ushort Count, byte[] Payload);

public static class WorldRecoveryWire
{
    public const int DigestBytes = 25 + WorldRecoveryHash.Bytes;
    public const int RequestBytes = DigestBytes;
    public const int ChunkHeaderBytes = DigestBytes + 4;
    public const int MaxSectionBytes = 4 * 1024 * 1024;

    public static byte[] BuildDigest(WorldRecoveryDigest value) =>
        BuildAnnouncement(Protocol.WorldDigestDown, value);

    public static byte[] BuildRequest(WorldRecoveryRequest value) =>
        BuildAnnouncement(Protocol.WorldRepairUp, value.Expected);

    public static bool TryReadDigest(ReadOnlySpan<byte> body, out WorldRecoveryDigest value)
    {
        value = default;
        if (body.Length != DigestBytes) return false;
        var candidate = ReadDigest(body);
        if (!ValidDigest(candidate)) return false;
        value = candidate;
        return true;
    }

    public static bool TryReadRequest(ReadOnlySpan<byte> body, out WorldRecoveryRequest value)
    {
        value = default;
        if (!TryReadDigest(body, out var expected)) return false;
        value = new(expected);
        return true;
    }

    public static byte[] BuildChunk(WorldRecoveryChunk value)
    {
        if (!ValidDigest(value.Digest) || value.Payload is null ||
            !WorldSyncWire.ValidChunk(value.Index, value.Count, value.Payload.Length))
            throw new ArgumentException("Invalid world repair chunk.", nameof(value));
        byte[] frame = PresentationTimeWire.Frame(Protocol.WorldRepairDown, ChunkHeaderBytes + value.Payload.Length);
        var body = frame.AsSpan(3);
        WriteDigest(body, value.Digest);
        BinaryPrimitives.WriteUInt16LittleEndian(body[DigestBytes..], value.Index);
        BinaryPrimitives.WriteUInt16LittleEndian(body[(DigestBytes + 2)..], value.Count);
        value.Payload.CopyTo(body[ChunkHeaderBytes..]);
        return frame;
    }

    public static bool TryReadChunk(ReadOnlySpan<byte> body, out WorldRecoveryChunk value)
    {
        value = default;
        if (body.Length < ChunkHeaderBytes) return false;
        var digest = ReadDigest(body);
        ushort index = BinaryPrimitives.ReadUInt16LittleEndian(body[DigestBytes..]);
        ushort count = BinaryPrimitives.ReadUInt16LittleEndian(body[(DigestBytes + 2)..]);
        var payload = body[ChunkHeaderBytes..];
        if (!ValidDigest(digest) || !WorldSyncWire.ValidChunk(index, count, payload.Length)) return false;
        value = new(digest, index, count, payload.ToArray());
        return true;
    }

    /// <summary>Hashes the exact checked section payloads in chunk-index order. A section with no rows has one empty chunk.</summary>
    public static bool TryComputeHash(WorldSyncScope scope, uint snapshotId, WorldBaselineSection section,
        IReadOnlyList<byte[]> orderedPayloads, out WorldRecoveryHash hash)
    {
        hash = default;
        if (!scope.IsValid || snapshotId == 0 || !WorldSyncWire.IsSection(section) || orderedPayloads is null ||
            orderedPayloads.Count is < 1 or > WorldSyncWire.MaxBaselineChunks)
            return false;

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData("KCDMP-WORLD-RECOVERY/V1"u8);
        Span<byte> header = stackalloc byte[19];
        WorldSyncWire.WriteScope(header, scope);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], snapshotId);
        header[16] = (byte)section;
        ushort count = (ushort)orderedPayloads.Count;
        BinaryPrimitives.WriteUInt16LittleEndian(header[17..], count);
        sha.AppendData(header);

        Span<byte> row = stackalloc byte[6];
        int total = 0;
        for (ushort index = 0; index < count; index++)
        {
            var payload = orderedPayloads[index];
            if (payload is null || !WorldSyncWire.ValidChunk(index, count, payload.Length) ||
                payload.Length > MaxSectionBytes - total)
                return false;
            total += payload.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(row, index);
            BinaryPrimitives.WriteUInt32LittleEndian(row[2..], (uint)payload.Length);
            sha.AppendData(row);
            sha.AppendData(payload);
        }

        hash = WorldRecoveryHash.Read(sha.GetHashAndReset());
        return true;
    }

    private static bool ValidDigest(WorldRecoveryDigest value) =>
        value.Scope.IsValid && value.SnapshotId != 0 && WorldSyncWire.IsSection(value.Section) && value.Revision != 0;

    private static byte[] BuildAnnouncement(byte type, WorldRecoveryDigest value)
    {
        if (!ValidDigest(value)) throw new ArgumentException("Invalid world recovery digest.", nameof(value));
        byte[] frame = PresentationTimeWire.Frame(type, DigestBytes);
        WriteDigest(frame.AsSpan(3), value);
        return frame;
    }

    private static WorldRecoveryDigest ReadDigest(ReadOnlySpan<byte> body) =>
        new(WorldSyncWire.ReadScope(body), BinaryPrimitives.ReadUInt32LittleEndian(body[12..]), (WorldBaselineSection)body[16],
            BinaryPrimitives.ReadUInt64LittleEndian(body[17..]), WorldRecoveryHash.Read(body[25..]));

    private static void WriteDigest(Span<byte> body, WorldRecoveryDigest value)
    {
        WorldSyncWire.WriteScope(body, value.Scope);
        BinaryPrimitives.WriteUInt32LittleEndian(body[12..], value.SnapshotId);
        body[16] = (byte)value.Section;
        BinaryPrimitives.WriteUInt64LittleEndian(body[17..], value.Revision);
        value.Hash.Write(body[25..]);
    }
}
