// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;

namespace KcdMp.Wire;

public static partial class Protocol
{
    /// <summary>P1: pure lifecycle control, served off the game thread. Never carried inside a 0x2D batch.</summary>
    public const byte Wo216OpLifecycle = 168;
    public const byte Wo216LifecycleDisabled = 0, Wo216LifecycleLoad = 1, Wo216LifecycleGameplay = 2, Wo216LifecycleQuery = 3;
}

/// <summary>The current native state, including on a rejected stale mutation. RequestId correlates this reply, not its phase/generation.</summary>
public readonly record struct Wo216LifecycleReply(bool Accepted, byte Reason, byte Phase, bool Enabled, bool Active,
    ulong Generation, uint GameplayAgeMs, uint RequestId);

public static class Wo216LifecycleCodec
{
    public const int RequestArgsLength = 13;
    public const int AckPayloadLength = 19;
    public const int ReplyBodyLength = 4 + AckPayloadLength;

    /// <summary>After op168: [phase:1][generation:8 LE][requestId:4 LE]. Query ignores the requested generation.</summary>
    public static byte[] BuildArgs(byte phase, ulong generation, uint requestId)
    {
        if (phase > Protocol.Wo216LifecycleQuery) throw new ArgumentOutOfRangeException(nameof(phase));
        var args = new byte[RequestArgsLength];
        args[0] = phase;
        BinaryPrimitives.WriteUInt64LittleEndian(args.AsSpan(1), generation);
        BinaryPrimitives.WriteUInt32LittleEndian(args.AsSpan(9), requestId);
        return args;
    }

    /// <summary>0xAC body: [ok][seq][168][reason][phase][enabled][active][generation:8][gameplayAgeMs:4][requestId:4].</summary>
    public static bool TryReadReply(ReadOnlySpan<byte> body, out Wo216LifecycleReply reply)
    {
        reply = default;
        if (body.Length != ReplyBodyLength || body[0] > 1 || body[2] != Protocol.Wo216OpLifecycle
            || body[4] > Protocol.Wo216LifecycleGameplay || body[5] > 1 || body[6] > 1) return false;
        reply = new(body[0] == 1, body[3], body[4], body[5] == 1, body[6] == 1,
            BinaryPrimitives.ReadUInt64LittleEndian(body[7..]), BinaryPrimitives.ReadUInt32LittleEndian(body[15..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[19..]));
        return true;
    }
}
