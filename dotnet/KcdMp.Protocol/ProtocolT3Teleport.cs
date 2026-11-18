// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Globalization;

namespace KcdMp.Wire;

public static partial class Protocol
{
    public const byte W203TpSnapshotRequest = 13, W203TpSnapshotResponse = 14;
    public const byte T3OpPlayerSnapshot = 251;
}

public enum TeleportSource : byte
{
    Unknown, Leash, Respawn, Unstuck, Join, LeashSettle, VideoLanding, VideoRollback, SceneNudge
}

public readonly record struct TeleportSnapshot(float X, float Y, float Z, byte Known, byte Flags, ulong Generation, ulong CapturedTick64)
{
    public bool Safe => Known == 15 && (Flags & 7) == 0 && float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z);
}

public readonly record struct TeleportChallenge(ulong Nonce, uint World, TeleportSource Source);
public readonly record struct TeleportAnswer(ulong Nonce, uint World, bool Ok, float X, float Y, float Z, byte Known, byte Flags);

public static class TeleportSnapshotCodec
{
    public const int TextMax = 200;
    public static bool ValidSource(TeleportSource source) => source is >= TeleportSource.Leash and <= TeleportSource.SceneNudge;
    public static string Build(TeleportChallenge q) => FormattableString.Invariant($"1|{q.Nonce}|{q.World}|{(byte)q.Source}");
    public static string Build(TeleportAnswer a) => FormattableString.Invariant($"1|{a.Nonce}|{a.World}|{(a.Ok ? 1 : 0)}|{a.Known}|{a.Flags}|{a.X:R}|{a.Y:R}|{a.Z:R}");
    public static bool TryChallenge(string text, out TeleportChallenge q)
    {
        q = default;
        if (text.Length > TextMax) return false;
        var p = text.Split('|');
        if (p.Length != 4 || p[0] != "1" || !ulong.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out ulong nonce) || nonce == 0 ||
            !uint.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out uint world) || world == 0 ||
            !byte.TryParse(p[3], NumberStyles.None, CultureInfo.InvariantCulture, out byte source) || !ValidSource((TeleportSource)source)) return false;
        q = new(nonce, world, (TeleportSource)source); return true;
    }
    public static bool TryAnswer(string text, out TeleportAnswer a)
    {
        a = default;
        if (text.Length > TextMax) return false;
        var p = text.Split('|');
        if (p.Length != 9 || p[0] != "1" || !ulong.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out ulong nonce) || nonce == 0 ||
            !uint.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out uint world) || world == 0 ||
            !byte.TryParse(p[3], out byte ok) || ok > 1 || !byte.TryParse(p[4], out byte known) || known > 15 ||
            !byte.TryParse(p[5], out byte flags) || flags > 7 ||
            !float.TryParse(p[6], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) || !float.IsFinite(x) ||
            !float.TryParse(p[7], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) || !float.IsFinite(y) ||
            !float.TryParse(p[8], NumberStyles.Float, CultureInfo.InvariantCulture, out float z) || !float.IsFinite(z)) return false;
        a = new(nonce, world, ok == 1, x, y, z, known, flags); return true;
    }
    public static bool TryNative(ReadOnlySpan<byte> p, out TeleportSnapshot sample)
    {
        sample = default;
        if (p.Length != 32 || p[0] != 1 || p[1] > 15 || p[2] > 7 || p[3] != 0) return false;
        sample = new(BinaryPrimitives.ReadSingleLittleEndian(p[4..]), BinaryPrimitives.ReadSingleLittleEndian(p[8..]), BinaryPrimitives.ReadSingleLittleEndian(p[12..]),
            p[1], p[2], BinaryPrimitives.ReadUInt64LittleEndian(p[16..]), BinaryPrimitives.ReadUInt64LittleEndian(p[24..]));
        return float.IsFinite(sample.X) && float.IsFinite(sample.Y) && float.IsFinite(sample.Z);
    }
}
