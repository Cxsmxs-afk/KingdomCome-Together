// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;

namespace KcdMp.Wire;

public static partial class Protocol
{
    public const ushort LinkCapWorldSync = 0x0020;
    public const byte WorldBaselineDown = 0x8D, WorldEventDown = 0x8E, WorldSyncUp = 0x8F;
}

public readonly record struct WorldSyncScope(uint HostWorld, ulong Generation)
{
    public bool IsValid => HostWorld != 0 && Generation != 0;
}

public enum WorldBaselineSection : byte { Body = 1, Npc, Corpse, Loot, Clock, Weather }
public enum WorldEventChannel : byte { Body = 1, Npc, Corpse, Loot, Clock, Weather, Dialog }
public enum WorldSyncOperation : byte { Open = 1, BaselineApplied, EventApplied, Close, Ready }

public readonly record struct WorldBaselineChunk(WorldSyncScope Scope, uint SnapshotId, ushort Version,
    WorldBaselineSection Section, ushort Index, ushort Count, byte[] Payload);

public readonly record struct WorldEventFrame(WorldSyncScope Scope, uint SnapshotId, WorldEventChannel Channel,
    ulong Id, byte Type, byte[] Payload);

public readonly record struct WorldSyncCommand(WorldSyncOperation Operation, WorldSyncScope Scope, uint SnapshotId,
    WorldEventChannel Channel = 0, ulong EventId = 0);

public static class WorldSyncWire
{
    public const ushort BaselineVersion = 1;
    public const int MaxBaselineChunkBytes = 16 * 1024, MaxBaselineChunks = 4096;
    public const int BaselineHeaderBytes = 23, EventHeaderBytes = 26, SyncBytes = 26;
    public const int MaxEventPayloadBytes = ushort.MaxValue - EventHeaderBytes;

    public static bool IsChannel(WorldEventChannel channel) => channel is >= WorldEventChannel.Body and <= WorldEventChannel.Dialog;
    private static bool IsSection(WorldBaselineSection section) => section is >= WorldBaselineSection.Body and <= WorldBaselineSection.Weather;
    public static bool IsEventType(byte type) => type is not
        (0 or Protocol.WorldBaselineDown or Protocol.WorldEventDown or Protocol.WorldSyncUp or Protocol.WorldSyncStatusDown);

    public static byte[] BuildBaseline(WorldBaselineChunk chunk)
    {
        if (!chunk.Scope.IsValid || chunk.SnapshotId == 0 || chunk.Version != BaselineVersion || !IsSection(chunk.Section) || chunk.Payload is null ||
            !ValidChunk(chunk.Index, chunk.Count, chunk.Payload.Length)) throw new ArgumentException("Invalid baseline chunk.", nameof(chunk));
        byte[] frame = PresentationTimeWire.Frame(Protocol.WorldBaselineDown, BaselineHeaderBytes + chunk.Payload.Length);
        var body = frame.AsSpan(3);
        WriteScope(body, chunk.Scope);
        BinaryPrimitives.WriteUInt32LittleEndian(body[12..], chunk.SnapshotId);
        BinaryPrimitives.WriteUInt16LittleEndian(body[16..], chunk.Version);
        body[18] = (byte)chunk.Section;
        BinaryPrimitives.WriteUInt16LittleEndian(body[19..], chunk.Index);
        BinaryPrimitives.WriteUInt16LittleEndian(body[21..], chunk.Count);
        chunk.Payload.CopyTo(body[BaselineHeaderBytes..]);
        return frame;
    }

    public static bool TryReadBaseline(ReadOnlySpan<byte> body, out WorldBaselineChunk chunk)
    {
        chunk = default;
        if (body.Length < BaselineHeaderBytes) return false;
        var scope = ReadScope(body);
        uint snapshot = BinaryPrimitives.ReadUInt32LittleEndian(body[12..]);
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(body[16..]);
        var section = (WorldBaselineSection)body[18];
        ushort index = BinaryPrimitives.ReadUInt16LittleEndian(body[19..]);
        ushort count = BinaryPrimitives.ReadUInt16LittleEndian(body[21..]);
        var payload = body[BaselineHeaderBytes..];
        if (!scope.IsValid || snapshot == 0 || version != BaselineVersion || !IsSection(section) || !ValidChunk(index, count, payload.Length)) return false;
        chunk = new(scope, snapshot, version, section, index, count, payload.ToArray());
        return true;
    }

    private static bool ValidChunk(ushort index, ushort count, int size) => count is > 0 and <= MaxBaselineChunks &&
        index < count && size <= MaxBaselineChunkBytes && (size != 0 || count == 1);

    public static byte[] BuildEvent(WorldEventFrame value)
    {
        if (value.Payload is null || !ValidEvent(value.Scope, value.SnapshotId, value.Channel, value.Id, value.Type, value.Payload.Length))
            throw new ArgumentException("Invalid ordered event.", nameof(value));
        byte[] frame = PresentationTimeWire.Frame(Protocol.WorldEventDown, EventHeaderBytes + value.Payload.Length);
        var body = frame.AsSpan(3);
        WriteScope(body, value.Scope);
        BinaryPrimitives.WriteUInt32LittleEndian(body[12..], value.SnapshotId);
        body[16] = (byte)value.Channel;
        BinaryPrimitives.WriteUInt64LittleEndian(body[17..], value.Id);
        body[25] = value.Type;
        value.Payload.CopyTo(body[EventHeaderBytes..]);
        return frame;
    }

    public static bool TryReadEvent(ReadOnlySpan<byte> body, out WorldEventFrame value)
    {
        value = default;
        if (body.Length < EventHeaderBytes) return false;
        var scope = ReadScope(body);
        uint snapshot = BinaryPrimitives.ReadUInt32LittleEndian(body[12..]);
        var channel = (WorldEventChannel)body[16];
        ulong id = BinaryPrimitives.ReadUInt64LittleEndian(body[17..]);
        byte type = body[25];
        var payload = body[EventHeaderBytes..];
        if (!ValidEvent(scope, snapshot, channel, id, type, payload.Length)) return false;
        value = new(scope, snapshot, channel, id, type, payload.ToArray());
        return true;
    }

    private static bool ValidEvent(WorldSyncScope scope, uint snapshot, WorldEventChannel channel, ulong id, byte type, int size) =>
        scope.IsValid && snapshot != 0 && IsChannel(channel) && id != 0 && IsEventType(type) && size <= MaxEventPayloadBytes;

    public static byte[] BuildSync(WorldSyncCommand command)
    {
        if (!ValidCommand(command)) throw new ArgumentException("Invalid world sync command.", nameof(command));
        byte[] frame = PresentationTimeWire.Frame(Protocol.WorldSyncUp, SyncBytes);
        var body = frame.AsSpan(3);
        body[0] = (byte)command.Operation;
        WriteScope(body[1..], command.Scope);
        BinaryPrimitives.WriteUInt32LittleEndian(body[13..], command.SnapshotId);
        body[17] = (byte)command.Channel;
        BinaryPrimitives.WriteUInt64LittleEndian(body[18..], command.EventId);
        return frame;
    }

    public static bool TryReadSync(ReadOnlySpan<byte> body, out WorldSyncCommand command)
    {
        command = default;
        if (body.Length != SyncBytes) return false;
        var candidate = new WorldSyncCommand((WorldSyncOperation)body[0], ReadScope(body[1..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[13..]), (WorldEventChannel)body[17],
            BinaryPrimitives.ReadUInt64LittleEndian(body[18..]));
        if (!ValidCommand(candidate)) return false;
        command = candidate;
        return true;
    }

    private static bool ValidCommand(WorldSyncCommand command)
    {
        if (!command.Scope.IsValid) return false;
        if (command.Operation == WorldSyncOperation.EventApplied)
            return command.SnapshotId != 0 && IsChannel(command.Channel) && command.EventId != 0;
        if (command.Channel != 0 || command.EventId != 0) return false;
        return command.Operation switch
        {
            WorldSyncOperation.Open or WorldSyncOperation.Ready => command.SnapshotId == 0,
            WorldSyncOperation.BaselineApplied => command.SnapshotId != 0,
            WorldSyncOperation.Close => true,
            _ => false
        };
    }

    private static WorldSyncScope ReadScope(ReadOnlySpan<byte> body) =>
        new(BinaryPrimitives.ReadUInt32LittleEndian(body), BinaryPrimitives.ReadUInt64LittleEndian(body[4..]));

    private static void WriteScope(Span<byte> body, WorldSyncScope scope)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(body, scope.HostWorld);
        BinaryPrimitives.WriteUInt64LittleEndian(body[4..], scope.Generation);
    }
}
