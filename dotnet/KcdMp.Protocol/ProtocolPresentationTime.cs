// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Diagnostics;

namespace KcdMp.Wire;

public static partial class Protocol
{
    public const ushort LinkCapPresentationTime = 0x0008;
    public const byte PresentationClockUp = 0x88, PresentationClockDown = 0x89;
    public const byte PresentationPoseDown = 0x8A, PresentationActionDown = 0x8B;
}

/// <summary>One value on the currently selected host agent's monotonic clock.</summary>
public readonly record struct PresentationStamp(ulong Epoch, ulong HostMs, long RelayToHostMs);

public readonly record struct PresentationClockSample(long ClientReceiveMs, long RelaySendMs, long ClientToRelayMs, long RoundTripMs)
{
    public bool FreshClient(long now) => now >= ClientReceiveMs && now - ClientReceiveMs <= PresentationClock.FreshMs;
    public bool FreshRelay(long now) => now >= RelaySendMs && now - RelaySendMs <= PresentationClock.FreshMs;
}

/// <summary>Presentation uses monotonic QPC milliseconds and a round-trip relay anchor, independent of the UTC display clock.</summary>
public static class PresentationClock
{
    public const int FreshMs = 10_000;
    public const int ProbeIntervalMs = 2_000;
    private const int MaxRoundTripMs = 2_000;
    private const int MaxSourceDistanceMs = FreshMs + MaxRoundTripMs;
    public const int QueryLength = 9, ConfirmLength = 17, ReplyLength = 24;

    public static long NowMs() => QpcToMs(Stopwatch.GetTimestamp());
    public static long QpcToMs(long qpc) => (long)((Int128)qpc * 1000 / Stopwatch.Frequency);

    public static bool TrySample(long t0, long t1, long t2, long t3, out PresentationClockSample sample)
    {
        sample = default;
        if (t0 <= 0 || t1 <= 0 || t2 < t1 || t3 < t0) return false;
        long clientSpan = t3 - t0, relaySpan = t2 - t1;
        if (clientSpan > MaxRoundTripMs || relaySpan > clientSpan) return false;
        long offset = (long)(((Int128)t1 - t0 + t2 - t3) / 2);
        sample = new(t3, t2, offset, clientSpan - relaySpan);
        return true;
    }

    public static bool TryUnwrap(uint stamp, long aroundMs, out long timeMs)
    {
        timeMs = 0;
        if (stamp == 0 || aroundMs <= 0) return false;
        int delta = unchecked((int)(stamp - (uint)aroundMs));
        if (delta is < -MaxSourceDistanceMs or > MaxSourceDistanceMs) return false;
        Int128 value = (Int128)aroundMs + delta;
        if (value <= 0 || value > long.MaxValue) return false;
        timeMs = (long)value;
        return true;
    }

    public static bool TryShift(long timeMs, long offsetMs, out long shifted)
    {
        Int128 value = (Int128)timeMs + offsetMs;
        shifted = 0;
        if (value <= 0 || value > long.MaxValue) return false;
        shifted = (long)value;
        return true;
    }

    public static byte[] BuildQuery(long t0) => BuildUp(0, t0, null);
    public static byte[] BuildConfirm(long t0, long t3) => BuildUp(1, t0, t3);

    private static byte[] BuildUp(byte kind, long t0, long? t3)
    {
        byte[] p = PresentationTimeWire.Frame(Protocol.PresentationClockUp, t3.HasValue ? ConfirmLength : QueryLength);
        p[3] = kind;
        BinaryPrimitives.WriteInt64LittleEndian(p.AsSpan(4), t0);
        if (t3.HasValue) BinaryPrimitives.WriteInt64LittleEndian(p.AsSpan(12), t3.Value);
        return p;
    }

    public static bool TryReadQuery(ReadOnlySpan<byte> payload, out long t0)
    {
        t0 = 0;
        if (payload.Length != QueryLength || payload[0] != 0) return false;
        return (t0 = BinaryPrimitives.ReadInt64LittleEndian(payload[1..])) > 0;
    }

    public static bool TryReadConfirm(ReadOnlySpan<byte> payload, out long t0, out long t3)
    {
        t0 = t3 = 0;
        if (payload.Length != ConfirmLength || payload[0] != 1) return false;
        t0 = BinaryPrimitives.ReadInt64LittleEndian(payload[1..]);
        t3 = BinaryPrimitives.ReadInt64LittleEndian(payload[9..]);
        return t0 > 0 && t3 >= t0;
    }

    public static byte[] BuildReply(long t0, long t1, long t2)
    {
        byte[] p = PresentationTimeWire.Frame(Protocol.PresentationClockDown, ReplyLength);
        BinaryPrimitives.WriteInt64LittleEndian(p.AsSpan(3), t0);
        BinaryPrimitives.WriteInt64LittleEndian(p.AsSpan(11), t1);
        BinaryPrimitives.WriteInt64LittleEndian(p.AsSpan(19), t2);
        return p;
    }
}

/// <summary>Receiver half of the QPC exchange. Scope belongs to one relay connection.</summary>
public sealed class PresentationClockClient
{
    private readonly object _gate = new();
    private long _query;
    private PresentationClockSample? _sample;

    public byte[] Begin(long ms)
    {
        if (ms <= 0) throw new ArgumentOutOfRangeException(nameof(ms));
        lock (_gate) _query = ms;
        return PresentationClock.BuildQuery(ms);
    }

    public bool TryComplete(ReadOnlySpan<byte> body, long now, out byte[] confirmation)
    {
        confirmation = [];
        if (body.Length != PresentationClock.ReplyLength) return false;
        long t0 = BinaryPrimitives.ReadInt64LittleEndian(body);
        long t1 = BinaryPrimitives.ReadInt64LittleEndian(body[8..]);
        long t2 = BinaryPrimitives.ReadInt64LittleEndian(body[16..]);
        lock (_gate)
        {
            if (t0 != _query || !PresentationClock.TrySample(t0, t1, t2, now, out var sample)) return false;
            _query = 0;
            _sample = sample;
            confirmation = PresentationClock.BuildConfirm(t0, now);
            return true;
        }
    }

    public bool TryHostAt(long localArrivalMs, long relayToHostMs, out ulong hostMs)
    {
        hostMs = 0;
        lock (_gate)
        {
            if (_sample is not { } sample || !sample.FreshClient(localArrivalMs) ||
                !PresentationClock.TryShift(localArrivalMs, sample.ClientToRelayMs, out long relay) ||
                !PresentationClock.TryShift(relay, relayToHostMs, out long host)) return false;
            hostMs = (ulong)host;
            return true;
        }
    }

    public void Reset()
    {
        lock (_gate) { _query = 0; _sample = null; }
    }
}

/// <summary>Timing prefix around an unchanged relay body. The old type and its receiver fences remain visible on extraction.</summary>
public static class PresentationTimeWire
{
    public const int HeaderLength = 25;
    private const int ActionHeaderLength = 1 + Protocol.ActionUpHeaderLen;

    internal static byte[] Frame(byte type, int size)
    {
        byte[] frame = new byte[3 + size];
        frame[0] = type;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1), checked((ushort)size));
        return frame;
    }

    public static byte[] Wrap(byte innerType, ReadOnlySpan<byte> payload, PresentationStamp stamp)
    {
        byte type = innerType == Protocol.ActionDown ? Protocol.PresentationActionDown : Protocol.PresentationPoseDown;
        byte[] frame = Frame(type, HeaderLength + payload.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(3), stamp.Epoch);
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(11), stamp.HostMs);
        BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(19), stamp.RelayToHostMs);
        frame[27] = innerType;
        payload.CopyTo(frame.AsSpan(3 + HeaderLength));
        return frame;
    }

    public static bool TryRead(int type, ReadOnlySpan<byte> body, out PresentationStamp stamp, out byte innerType, out int payloadOffset)
    {
        stamp = default;
        innerType = 0;
        payloadOffset = 0;
        if (body.Length <= HeaderLength) return false;
        byte inner = body[24];
        bool correctType = type switch
        {
            Protocol.PresentationPoseDown => inner is Protocol.Ghost or Protocol.NpcStateDown,
            Protocol.PresentationActionDown => inner == Protocol.ActionDown,
            _ => false
        };
        if (!correctType || !TrySourceStamp(inner, body[HeaderLength..], out _)) return false;
        ulong epoch = BinaryPrimitives.ReadUInt64LittleEndian(body);
        ulong hostMs = BinaryPrimitives.ReadUInt64LittleEndian(body[8..]);
        if (epoch == 0 || hostMs == 0 || hostMs > long.MaxValue) return false;
        stamp = new(epoch, hostMs, BinaryPrimitives.ReadInt64LittleEndian(body[16..]));
        innerType = inner;
        payloadOffset = HeaderLength;
        return true;
    }

    public static bool TryPositionStamp(byte flags, ReadOnlySpan<byte> tail, out uint stamp)
    {
        stamp = 0;
        if ((flags & Protocol.PositionFlagSenderMs) == 0) return false;
        bool state = (flags & Protocol.PositionFlagBodyState2) != 0 && tail.Length >= Protocol.BodyState2Len;
        if (state) tail = tail[Protocol.BodyState2Len..];
        return tail.Length == Protocol.SenderMsLen && (stamp = BinaryPrimitives.ReadUInt32LittleEndian(tail)) != 0;
    }

    public static bool TrySourceStamp(int type, ReadOnlySpan<byte> body, out uint stamp)
    {
        stamp = 0;
        switch (type)
        {
            case Protocol.Ghost:
                return Protocol.IsGhostPayloadLen(body.Length) && TryPositionStamp(body[17], body[Protocol.GhostPayloadLen..], out stamp);
            case Protocol.NpcStateDown:
                if (body.Length < 3 || body[1] is 0 or > Protocol.MaxNpcNameLen ||
                    body.Length != 2 + body[1] + Protocol.NpcStateFixedTail) return false;
                stamp = BinaryPrimitives.ReadUInt32LittleEndian(body[^Protocol.SenderMsLen..]);
                return stamp != 0;
            case Protocol.ActionDown:
                return body.Length > 0 && TryActionStamp(body[1..], out stamp);
            default:
                return false;
        }
    }

    public static bool TryActionStamp(ReadOnlySpan<byte> upstream, out uint stamp)
    {
        stamp = 0;
        if (upstream.Length < Protocol.ActionUpHeaderLen || upstream[Protocol.ActionUpHeaderLen - 1] != upstream.Length - Protocol.ActionUpHeaderLen)
            return false;
        var action = upstream[Protocol.ActionUpHeaderLen..];
        bool stamped = (ActionKind)upstream[0] switch
        {
            ActionKind.Attack => AttackEvent.TryFromBytes(action, out _),
            ActionKind.Jump => action.Length == Protocol.SenderMsLen,
            ActionKind.Emote => action.Length == EmoteId.PayloadLen,
            ActionKind.BlockImpulse or ActionKind.Dodge or ActionKind.NpcAttack or ActionKind.NpcHit or
                ActionKind.AvatarHit or ActionKind.AvatarRecoil or ActionKind.AvatarClinch => RowEvent.TryFromBytes(action, out _),
            _ => false
        };
        return stamped && (stamp = BinaryPrimitives.ReadUInt32LittleEndian(action)) != 0;
    }
}
