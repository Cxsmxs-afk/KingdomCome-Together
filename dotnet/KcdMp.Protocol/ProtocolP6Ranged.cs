// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace KcdMp.Wire;

public static partial class Protocol
{
    public const byte W164MeasuredMissile = 28;
    public const int RangedCaptureMaxAgeMs = 2000, MeasuredMissileApplyBudgetMs = 2000;
}

public enum RangedWeaponKind : byte { Unknown, Bow, Crossbow }
public readonly record struct RangedCapture(byte Phase, byte Source, byte KnownMask, uint Sequence, ulong CapturedTick, Guid Left, Guid Right);
public sealed record RangedPollReply(ulong Generation, ulong Epoch, IReadOnlyList<RangedCapture> Rows);

public readonly record struct RangedStepV2(uint World, ulong Generation, ulong Epoch, uint Sequence, byte Phase,
    RangedWeaponKind Kind, Guid ItemClass, uint AgeMs, byte Source)
{
    public string Encode() => FormattableString.Invariant($"v2|{World}|{Generation}|{Epoch}|{Sequence}|{Phase}|{(byte)Kind}|{ItemClass:D}|{AgeMs:D4}|{Source}");
    public static bool TryDecode(string text, out RangedStepV2 step)
    {
        step = default; var f = text.Split('|');
        if (f.Length != 10 || f[0] != "v2" || !P6Numbers.U32(f[1], out uint world) || world == 0 ||
            !P6Numbers.U64(f[2], out ulong gen) || gen == 0 || !P6Numbers.U64(f[3], out ulong epoch) || epoch == 0 ||
            !P6Numbers.U32(f[4], out uint seq) || !byte.TryParse(f[5], NumberStyles.None, CultureInfo.InvariantCulture, out byte phase) || phase > 6 ||
            !byte.TryParse(f[6], NumberStyles.None, CultureInfo.InvariantCulture, out byte kind) || kind > 2 || !Guid.TryParseExact(f[7], "D", out Guid cls) ||
            !P6Numbers.U32(f[8], out uint age) || age > Protocol.RangedCaptureMaxAgeMs ||
            !byte.TryParse(f[9], NumberStyles.None, CultureInfo.InvariantCulture, out byte source) || source > 1 || (kind != 0 && (cls == Guid.Empty || source != 1))) return false;
        step = new(world, gen, epoch, seq, phase, (RangedWeaponKind)kind, cls, age, source); return true;
    }
}

public static class RangedCaptureCodec
{
    public const int HeaderSize = 18, RowSize = 48, MaxRows = 10;
    public static byte[] Request(bool on, ulong generation)
    { byte[] b = new byte[10]; b[0] = 2; b[1] = on ? (byte)1 : (byte)0; BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(2), generation); return b; }
    public static bool TryDecode(ReadOnlySpan<byte> p, out RangedPollReply reply)
    {
        reply = null!;
        if (p.Length < HeaderSize || p[0] != 2 || p[1] > MaxRows || p.Length != HeaderSize + p[1] * RowSize) return false;
        ulong gen = BinaryPrimitives.ReadUInt64LittleEndian(p[2..]), epoch = BinaryPrimitives.ReadUInt64LittleEndian(p[10..]);
        if (gen == 0 || epoch == 0) return false;
        var rows = new RangedCapture[p[1]];
        for (int i = 0; i < rows.Length; i++)
        {
            var b = p.Slice(HeaderSize + i * RowSize, RowSize);
            if (b[0] > 6 || b[1] > 1 || b[2] > 3 || b[3] != 0) return false;
            Guid left = new(b.Slice(16, 16)), right = new(b.Slice(32, 16));
            if (((b[2] & 1) == 0 && left != Guid.Empty) || ((b[2] & 2) == 0 && right != Guid.Empty)) return false;
            rows[i] = new(b[0], b[1], b[2], BinaryPrimitives.ReadUInt32LittleEndian(b[4..]), BinaryPrimitives.ReadUInt64LittleEndian(b[8..]), left, right);
        }
        reply = new(gen, epoch, rows); return true;
    }
}

public readonly record struct MeasuredMissileEvidence(ulong ContactId, ulong Generation, ulong Epoch, ulong CapturedTick)
{
    public bool Died { get; init; }
    public static bool TryDecode(ReadOnlySpan<byte> b, out MeasuredMissileEvidence evidence)
    {
        evidence = default;
        if (b.Length != 61 || b[27] != 1 || b[28] != 7 || b[24] > 1 || b[25] != 1 || b[26] != 0) return false;
        float st = BinaryPrimitives.ReadSingleLittleEndian(b[16..]), hp = BinaryPrimitives.ReadSingleLittleEndian(b[20..]);
        if (!P6Numbers.Deltas(hp, st)) return false;
        evidence = new(BinaryPrimitives.ReadUInt64LittleEndian(b[29..]), BinaryPrimitives.ReadUInt64LittleEndian(b[37..]),
            BinaryPrimitives.ReadUInt64LittleEndian(b[45..]), BinaryPrimitives.ReadUInt64LittleEndian(b[53..])) { Died = b[24] != 0 };
        return evidence.ContactId != 0 && evidence.Generation != 0 && evidence.Epoch != 0 && evidence.CapturedTick != 0;
    }
}

public readonly record struct MeasuredMissileKey(uint World, ulong SourceGeneration, ulong Epoch, ulong ContactId);
public readonly record struct MeasuredMissileRequest(MeasuredMissileKey Key, string Npc, float Health, float Stamina)
{
    public string Encode() => FormattableString.Invariant($"m1|{Key.World}|{Key.SourceGeneration}|{Key.Epoch}|{Key.ContactId}|7|{Npc}|{Health:R}|{Stamina:R}");
    public static bool TryDecode(string text, out MeasuredMissileRequest request)
    {
        request = default; var f = text.Split('|');
        if (f.Length != 9 || f[0] != "m1" || f[5] != "7" || !P6Numbers.Key(f, out var key) || !W164Text.IsNpcName(f[6]) ||
            !P6Numbers.Float(f[7], out float hp) || !P6Numbers.Float(f[8], out float st) || !P6Numbers.Deltas(hp, st)) return false;
        request = new(key, f[6], hp, st); return true;
    }
}
public enum MeasuredMissileStatus : byte { Accepted, Scope, Victim, Arranged, Invalid, Uncertain, Capacity, Conflict, Expired }
public readonly record struct MeasuredMissileResult(MeasuredMissileKey Key, MeasuredMissileStatus Status, byte Steps, float Health, float Stamina)
{
    public string Encode() => FormattableString.Invariant($"m1|{Key.World}|{Key.SourceGeneration}|{Key.Epoch}|{Key.ContactId}|{(byte)Status}|{Health:R}|{Stamina:R}");
    public static bool TryDecode(string text, out MeasuredMissileResult result)
    {
        result = default; var f = text.Split('|');
        if (f.Length != 8 || f[0] != "m1" || !P6Numbers.Key(f, out var key) ||
            !byte.TryParse(f[5], NumberStyles.None, CultureInfo.InvariantCulture, out byte status) || status > 8 ||
            !P6Numbers.Float(f[6], out float hp) || !P6Numbers.Float(f[7], out float st) || !P6Numbers.Values(hp, st)) return false;
        result = new(key, (MeasuredMissileStatus)status, 0, hp, st); return true;
    }
}

public static class MeasuredMissileCodec
{
    public const int TailSize = 50, ReplySize = 40;
    // Arguments after op18. Original melee layout is unchanged; the extension has a separate discriminator and strict length.
    public static byte[] BuildArgs(byte source, MeasuredMissileRequest request, Guid victim, uint avatar, ulong hostGeneration, ulong deadline)
    {
        if (source >= 16 || request.Key.World == 0 || request.Key.SourceGeneration == 0 || request.Key.Epoch == 0 || request.Key.ContactId == 0 ||
            !W164Text.IsNpcName(request.Npc) || !P6Numbers.Deltas(request.Health, request.Stamina) || victim == Guid.Empty || avatar == 0 || hostGeneration == 0 || deadline == 0)
            throw new ArgumentException("Invalid measured missile receipt");
        byte[] name = Encoding.ASCII.GetBytes(request.Npc); byte[] a = new byte[8 + 30 + name.Length + TailSize];
        a[0] = 255; a[1] = 255; a[7] = 128;
        int o = 8; victim.TryWriteBytes(a.AsSpan(o)); BinaryPrimitives.WriteSingleLittleEndian(a.AsSpan(o + 16), request.Stamina);
        BinaryPrimitives.WriteSingleLittleEndian(a.AsSpan(o + 20), request.Health); a[o + 24] = Protocol.NpcDamageFlagAttributed | Protocol.DamageFlagSuppressHitReaction;
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(o + 25), avatar); a[o + 29] = (byte)name.Length; name.CopyTo(a, o + 30);
        o += 30 + name.Length; "P6M1"u8.CopyTo(a.AsSpan(o)); a[o + 4] = source; a[o + 5] = 7;
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(o + 6), request.Key.World); BinaryPrimitives.WriteUInt64LittleEndian(a.AsSpan(o + 10), hostGeneration);
        BinaryPrimitives.WriteUInt64LittleEndian(a.AsSpan(o + 18), request.Key.SourceGeneration); BinaryPrimitives.WriteUInt64LittleEndian(a.AsSpan(o + 26), request.Key.Epoch);
        BinaryPrimitives.WriteUInt64LittleEndian(a.AsSpan(o + 34), request.Key.ContactId); BinaryPrimitives.WriteUInt64LittleEndian(a.AsSpan(o + 42), deadline); return a;
    }
    public static bool TryReadReply(ReadOnlySpan<byte> p, byte source, MeasuredMissileKey key, out MeasuredMissileResult result)
    {
        result = default;
        if (p.Length != ReplySize || p[0] != 1 || p[1] > 8 || p[3] != source || BinaryPrimitives.ReadUInt32LittleEndian(p[4..]) != key.World ||
            BinaryPrimitives.ReadUInt64LittleEndian(p[8..]) != key.SourceGeneration || BinaryPrimitives.ReadUInt64LittleEndian(p[16..]) != key.Epoch ||
            BinaryPrimitives.ReadUInt64LittleEndian(p[24..]) != key.ContactId) return false;
        float hp = BinaryPrimitives.ReadSingleLittleEndian(p[32..]), st = BinaryPrimitives.ReadSingleLittleEndian(p[36..]);
        if (!P6Numbers.Values(hp, st) || (p[1] == 0 && (p[2] & 1) == 0)) return false;
        result = new(key, (MeasuredMissileStatus)p[1], p[2], hp, st); return true;
    }
}
public static class P6Numbers
{
    public static bool U32(string s, out uint n) => uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out n);
    public static bool U64(string s, out ulong n) => ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out n);
    public static bool Float(string s, out float n) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out n);
    public static bool Values(float hp, float st) => float.IsFinite(hp) && float.IsFinite(st) && hp is >= 0 and <= 999.9f && st is >= 0 and <= 999.9f;
    public static bool Deltas(float hp, float st) => Values(hp, st) && (hp > 0 || st > 0);
    public static bool Key(string[] f, out MeasuredMissileKey key)
    { key = default; if (!U32(f[1], out uint w) || w == 0 || !U64(f[2], out ulong g) || g == 0 || !U64(f[3], out ulong e) || e == 0 || !U64(f[4], out ulong id) || id == 0) return false; key = new(w, g, e, id); return true; }
}
