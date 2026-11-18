// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;

namespace KcdMp.Wire;

public static partial class Protocol
{
    public const byte DeathMarkerUp = 0x86, DeathMarkerDown = 0x87;
    public const int DeathMarkerUpLen = 35, DeathMarkerDownLen = 36;
    public const byte DeathMarkerEvent = 0xB0, DeathMarkerListOp = 252, DeathMarkerControlOp = 253;
}
public enum DeathMarkerKind : byte { Grave, MarkerOnly }
public enum DeathMarkerAction : byte { Remove, Add, ClearOwner, SetLoadedScope, PrepareLoad }
public readonly record struct DeathMarkerFact(DeathMarkerAction Action, DeathMarkerKind Kind, uint World,
    ulong Id, ulong Revision, float X, float Y, float Z);
public readonly record struct NativeDeathMarker(ulong Generation, DeathMarkerFact Fact);
public readonly record struct DeathMarkerAck(DeathMarkerAction Action, byte Status, ulong Generation, uint World)
{ public bool Safe => Status == 0; }
public sealed record DeathMarkerPage(ulong Generation, uint World, uint Token, ushort Next, bool Done, IReadOnlyList<DeathMarkerFact> Rows);

public static class DeathMarkerCodec
{
    public const int PageRows = 16, MaxRows = 128;
    public static bool Valid(DeathMarkerFact f) => f.Action is DeathMarkerAction.Add or DeathMarkerAction.Remove &&
        f.Kind is DeathMarkerKind.Grave or DeathMarkerKind.MarkerOnly && f.World != 0 && f.Id != 0 && f.Revision != 0 &&
        float.IsFinite(f.X) && float.IsFinite(f.Y) && float.IsFinite(f.Z);
    public static byte[] BuildUp(DeathMarkerFact f)
    {
        if (!Valid(f)) throw new ArgumentException("invalid death marker fact", nameof(f));
        byte[] p = new byte[3 + Protocol.DeathMarkerUpLen]; p[0] = Protocol.DeathMarkerUp;
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(1), Protocol.DeathMarkerUpLen);
        Write(p.AsSpan(3), f); return p;
    }
    private static void Write(Span<byte> p, DeathMarkerFact f)
    {
        p[0] = 1; p[1] = (byte)f.Action; p[2] = (byte)f.Kind;
        BinaryPrimitives.WriteUInt32LittleEndian(p[3..], f.World);
        BinaryPrimitives.WriteUInt64LittleEndian(p[7..], f.Id); BinaryPrimitives.WriteUInt64LittleEndian(p[15..], f.Revision);
        BinaryPrimitives.WriteSingleLittleEndian(p[23..], f.X); BinaryPrimitives.WriteSingleLittleEndian(p[27..], f.Y); BinaryPrimitives.WriteSingleLittleEndian(p[31..], f.Z);
    }
    public static bool TryRead(ReadOnlySpan<byte> p, out DeathMarkerFact fact)
    {
        fact = default;
        if (p.Length != Protocol.DeathMarkerUpLen || p[0] != 1) return false;
        fact = new((DeathMarkerAction)p[1], (DeathMarkerKind)p[2], BinaryPrimitives.ReadUInt32LittleEndian(p[3..]),
            BinaryPrimitives.ReadUInt64LittleEndian(p[7..]), BinaryPrimitives.ReadUInt64LittleEndian(p[15..]),
            BinaryPrimitives.ReadSingleLittleEndian(p[23..]), BinaryPrimitives.ReadSingleLittleEndian(p[27..]), BinaryPrimitives.ReadSingleLittleEndian(p[31..]));
        return Valid(fact);
    }
    public static bool TryNativeEvent(ReadOnlySpan<byte> p, out NativeDeathMarker marker)
    {
        marker = default;
        if (p.Length != 43 || p[0] != 1) return false;
        ulong generation = BinaryPrimitives.ReadUInt64LittleEndian(p[3..]);
        var f = new DeathMarkerFact((DeathMarkerAction)p[1], (DeathMarkerKind)p[2], BinaryPrimitives.ReadUInt32LittleEndian(p[11..]),
            BinaryPrimitives.ReadUInt64LittleEndian(p[15..]), BinaryPrimitives.ReadUInt64LittleEndian(p[23..]),
            BinaryPrimitives.ReadSingleLittleEndian(p[31..]), BinaryPrimitives.ReadSingleLittleEndian(p[35..]), BinaryPrimitives.ReadSingleLittleEndian(p[39..]));
        if (generation == 0 || !Valid(f)) return false;
        marker = new(generation, f); return true;
    }
    public static byte[] ControlArgs(DeathMarkerAction action, DeathMarkerKind kind, byte owner, ulong gen, uint world,
        ulong id = 0, ulong revision = 0, float x = 0, float y = 0, float z = 0)
    {
        if (action > DeathMarkerAction.PrepareLoad || kind > DeathMarkerKind.MarkerOnly || gen == 0 || !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
            throw new ArgumentException("invalid death marker control");
        byte[] p = new byte[44]; p[0] = 1; p[1] = (byte)action; p[2] = (byte)kind; p[3] = owner;
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(4), gen); BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(12), world);
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(16), id); BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(24), revision);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(32), x); BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(36), y); BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(40), z);
        return p;
    }
    public static bool TryAck(ReadOnlySpan<byte> p, out DeathMarkerAck ack)
    {
        ack = default;
        if (p.Length != 16 || p[0] != 1 || p[1] > 4 || p[2] > 4 || p[3] != 0) return false;
        ack = new((DeathMarkerAction)p[1], p[2], BinaryPrimitives.ReadUInt64LittleEndian(p[4..]), BinaryPrimitives.ReadUInt32LittleEndian(p[12..])); return true;
    }
    public static byte[] PageArgs(ulong gen, uint world, uint token, ushort cursor)
    {
        byte[] p = new byte[19]; p[0] = 1; BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(1), gen);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(9), world); BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(13), token);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(17), cursor); return p;
    }
    public static bool TryPage(ReadOnlySpan<byte> p, out DeathMarkerPage? page)
    {
        page = null;
        if (p.Length < 21 || p[0] != 1 || p[19] > PageRows || p[20] > 1 || p.Length != 21 + p[19] * 29) return false;
        ulong gen = BinaryPrimitives.ReadUInt64LittleEndian(p[1..]); uint world = BinaryPrimitives.ReadUInt32LittleEndian(p[9..]);
        uint token = BinaryPrimitives.ReadUInt32LittleEndian(p[13..]); ushort next = BinaryPrimitives.ReadUInt16LittleEndian(p[17..]);
        if (gen == 0 || world == 0 || token == 0 || next > MaxRows) return false;
        var rows = new List<DeathMarkerFact>(p[19]);
        for (int i = 0; i < p[19]; i++)
        {
            var row = p[(21 + i * 29)..];
            var f = new DeathMarkerFact(DeathMarkerAction.Add, (DeathMarkerKind)row[0], world, BinaryPrimitives.ReadUInt64LittleEndian(row[1..]),
                BinaryPrimitives.ReadUInt64LittleEndian(row[9..]), BinaryPrimitives.ReadSingleLittleEndian(row[17..]), BinaryPrimitives.ReadSingleLittleEndian(row[21..]), BinaryPrimitives.ReadSingleLittleEndian(row[25..]));
            if (!Valid(f)) return false; rows.Add(f);
        }
        page = new(gen, world, token, next, p[20] == 1, rows); return true;
    }
}
