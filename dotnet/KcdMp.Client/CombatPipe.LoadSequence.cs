// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;

namespace KcdMp.Client;

internal readonly record struct LoadSequenceNativeReply(bool Accepted, byte Phase, byte Flags, ulong Generation, uint RequestId, bool MainApplied)
{
    public static bool TryRead(ReadOnlySpan<byte> body, out LoadSequenceNativeReply reply)
    {
        reply = default;
        if (body.Length != 18 || body[0] > 1 || body[2] != CombatPipe.LoadSequenceOp || body[4] > 7 || (body[5] & 0x78) != 0) return false;
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(body[14..]);
        if (id == 0) return false;
        reply = new(body[0] == 1 && body[3] == 0, body[4], (byte)(body[5] & 7), BinaryPrimitives.ReadUInt64LittleEndian(body[6..]), id, (body[5] & 0x80) != 0);
        return true;
    }
}

public sealed partial class CombatPipe
{
    internal const byte LoadSequenceOp = 249, LoadSequenceDisabled = 7, LoadSequenceQuery = 8;

    internal async Task<LoadSequenceNativeReply?> LoadSequenceAsync(byte phase, byte flags, ulong generation, ushort holdSeconds = 0,
        CancellationToken ct = default)
    {
        byte[]? body = await SendControlAsync(LoadSequenceOp, id =>
        {
            var args = new byte[16];
            args[0] = phase;
            args[1] = flags;
            BinaryPrimitives.WriteUInt16LittleEndian(args.AsSpan(2), holdSeconds);
            BinaryPrimitives.WriteUInt64LittleEndian(args.AsSpan(4), generation);
            BinaryPrimitives.WriteUInt32LittleEndian(args.AsSpan(12), id);
            return args;
        }, ct);
        return body is not null && LoadSequenceNativeReply.TryRead(body, out var reply) ? reply : null;
    }
}
