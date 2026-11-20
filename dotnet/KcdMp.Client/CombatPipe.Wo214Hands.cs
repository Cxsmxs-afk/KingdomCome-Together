// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;

namespace KcdMp.Client;

internal readonly record struct Wo214NativeHands(byte Known, Guid Left, Guid Right)
{
    public bool ConfirmedEmpty => Known == 3 && Left == Guid.Empty && Right == Guid.Empty;

    public bool Holds(Guid cls) => cls != Guid.Empty &&
        (((Known & 1) != 0 && Left == cls) || ((Known & 2) != 0 && Right == cls));
}

internal readonly record struct Wo214HandDeclaration(byte Flags, uint World, long Session);

public sealed partial class CombatPipe
{
    private const byte RangedHandEdge = 0x41, RangedHandReply = 0xBB;

    internal static void PutRangedHandFence(Span<byte> body, long peerSerial, int bodyGeneration, uint birth, long revision)
    {
        BinaryPrimitives.WriteInt64LittleEndian(body, peerSerial);
        BinaryPrimitives.WriteInt32LittleEndian(body[8..], bodyGeneration);
        BinaryPrimitives.WriteUInt32LittleEndian(body[12..], birth);
        BinaryPrimitives.WriteInt64LittleEndian(body[16..], revision);
    }

    internal async Task<Wo214NativeHands?> RequestRangedHandAsync(byte peer, uint eid, long peerSerial,
        int bodyGeneration, uint birth, long revision, Guid expected, long connection, Func<bool> current, CancellationToken ct,
        Wo214HandDeclaration? declaration = null)
    {
        var body = new byte[declaration.HasValue ? 60 : 47];
        body[0] = 2;
        body[1] = declaration.HasValue ? (byte)0 : (byte)1;
        body[2] = peer;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(3), eid);
        PutRangedHandFence(body.AsSpan(7), peerSerial, bodyGeneration, birth, revision);
        expected.TryWriteBytes(body.AsSpan(31));
        if (declaration is { } desired)
        {
            body[47] = desired.Flags;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(48), desired.World);
            BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(52), desired.Session);
        }
        var reply = await EdgeRequestAsync(RangedHandEdge, body, RangedHandReply, EdgeLane.Realtime, ct, connection, current);
        if (reply.Outcome != EdgeOutcome.Completed || reply.Payload is not [1, 0, 2, 0, <= 3, ..] || reply.Payload.Length != 37)
            return null;
        var hands = new Wo214NativeHands(reply.Payload[4], new Guid(reply.Payload.AsSpan(5, 16)), new Guid(reply.Payload.AsSpan(21, 16)));
        if (declaration.HasValue && hands != default) return null;
        return hands;
    }
}
