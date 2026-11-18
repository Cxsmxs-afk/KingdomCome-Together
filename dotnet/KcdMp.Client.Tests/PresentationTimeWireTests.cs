// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public sealed class PresentationTimeWireTests
{
    [Fact]
    public void A_fresh_matched_exchange_projects_onto_the_selected_host_and_reset_retires_the_clock()
    {
        var clock = new PresentationClockClient();
        var query = clock.Begin(1000);
        Assert.Equal(Protocol.PresentationClockUp, query[0]);
        Assert.True(PresentationClock.TryReadQuery(query.AsSpan(3), out long t0));
        Assert.Equal(1000, t0);
        Assert.False(clock.TryHostAt(1028, -800, out _));

        Assert.False(clock.TryComplete(PresentationClock.BuildReply(999, 5010, 5018).AsSpan(3), 1028, out _));
        var reply = PresentationClock.BuildReply(t0, 5010, 5018);
        Assert.Equal(Protocol.PresentationClockDown, reply[0]);
        Assert.True(clock.TryComplete(reply.AsSpan(3), 1028, out var confirm));
        Assert.Equal(Protocol.PresentationClockUp, confirm[0]);
        Assert.True(PresentationClock.TryReadConfirm(confirm.AsSpan(3), out long echoed, out long received));
        Assert.Equal((1000, 1028), (echoed, received));
        Assert.False(clock.TryComplete(reply.AsSpan(3), 1028, out _));
        Assert.True(clock.TryHostAt(1034, -800, out ulong host));
        Assert.Equal(4234ul, host);
        Assert.False(clock.TryHostAt(1027, -800, out _));
        Assert.False(clock.TryHostAt(1028 + PresentationClock.FreshMs + 1, -800, out _));

        clock.Reset();
        Assert.False(clock.TryHostAt(1034, -800, out _));
    }

    [Fact]
    public void Wrapped_sender_milliseconds_resolve_only_near_the_fresh_source_epoch()
    {
        long boundary = 1L << 32;
        Assert.True(PresentationClock.TryUnwrap(0xFFFFFFFE, boundary + 25, out long previous));
        Assert.Equal(boundary - 2, previous);
        Assert.True(PresentationClock.TryUnwrap(8, boundary - 25, out long next));
        Assert.Equal(boundary + 8, next);
        Assert.False(PresentationClock.TryUnwrap(0, boundary + 1, out _));
        Assert.False(PresentationClock.TryUnwrap(900000, boundary + 1, out _));
        Assert.False(PresentationClock.TrySample(1000, 5000, 5020, 1010, out _));
        Assert.False(PresentationClock.TrySample(1000, 5000, 5001, 4001, out _));
    }

    [Fact]
    public void Timing_envelopes_preserve_legacy_clock_state_and_action_phase_with_exact_type_and_length()
    {
        byte[] ghost = new byte[Protocol.GhostPayloadLenV8Max];
        ghost[0] = 9;
        ghost[17] = Protocol.PositionFlagBodyState2 | Protocol.PositionFlagSenderMs;
        ghost[21] = 13;
        BinaryPrimitives.WriteUInt32LittleEndian(ghost.AsSpan(Protocol.GhostPayloadLenV8), 1234);
        var stamp = new PresentationStamp(2, 1UL << 40, -77);
        byte[] wire = PresentationTimeWire.Wrap(Protocol.Ghost, ghost, stamp);
        Assert.Equal(Protocol.PresentationPoseDown, wire[0]);
        Assert.True(PresentationTimeWire.TryRead(wire[0], wire.AsSpan(3), out var decoded, out byte inner, out int offset));
        Assert.Equal((stamp, Protocol.Ghost, 25), (decoded, inner, offset));
        Assert.Equal(ghost, wire[(3 + offset)..]);
        Assert.True(PresentationTimeWire.TrySourceStamp(Protocol.Ghost, ghost, out uint tick));
        Assert.Equal(1234u, tick);
        Assert.False(PresentationTimeWire.TryRead(Protocol.PresentationActionDown, wire.AsSpan(3), out _, out _, out _));
        Assert.False(PresentationTimeWire.TryRead(wire[0], wire.AsSpan(3, wire.Length - 4), out _, out _, out _));
        Assert.False(PresentationTimeWire.TrySourceStamp(Protocol.Ghost, ghost.AsSpan(0, 18), out _));
        Assert.False(PresentationTimeWire.TrySourceStamp(Protocol.Ghost, new byte[18], out _));

        var action = new byte[1 + Protocol.ActionUpHeaderLen + Protocol.SenderMsLen];
        action[0] = 9;
        action[1] = (byte)ActionKind.Jump;
        action[4] = (byte)ActionPhase.Commit;
        action[9] = Protocol.SenderMsLen;
        BinaryPrimitives.WriteUInt32LittleEndian(action.AsSpan(10), 1234);
        byte[] eventWire = PresentationTimeWire.Wrap(Protocol.ActionDown, action, stamp);
        Assert.Equal(Protocol.PresentationActionDown, eventWire[0]);
        Assert.True(PresentationTimeWire.TryRead(eventWire[0], eventWire.AsSpan(3), out decoded, out inner, out offset));
        Assert.Equal((stamp, Protocol.ActionDown, 25), (decoded, inner, offset));
        Assert.Equal(action, eventWire[(3 + offset)..]);
        action[1] = (byte)ActionKind.NpcResync;
        Assert.False(PresentationTimeWire.TrySourceStamp(Protocol.ActionDown, action, out _));
    }
}
