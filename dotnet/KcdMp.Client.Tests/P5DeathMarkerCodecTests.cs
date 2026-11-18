// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using KcdMp.Wire;
using Xunit;

namespace KcdMp.Client.Tests;

public sealed class P5DeathMarkerCodecTests
{
    private static DeathMarkerFact Fact(DeathMarkerAction action = DeathMarkerAction.Add, ulong revision = 1,
        uint world = 7, ulong id = 0x1122334455667788) => new(action, DeathMarkerKind.MarkerOnly, world, id, revision, 12.25f, -4.5f, 99.75f);
    private static byte[] Body(DeathMarkerFact fact) => DeathMarkerCodec.BuildUp(fact)[3..];
    private static byte[] Native(DeathMarkerFact fact, ulong generation = 0x0102030405060708)
    {
        byte[] p = new byte[43]; p[0] = 1; p[1] = (byte)fact.Action; p[2] = (byte)fact.Kind;
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(3), generation);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(11), fact.World);
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(15), fact.Id);
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(23), fact.Revision);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(31), fact.X);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(35), fact.Y);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(39), fact.Z); return p;
    }
    private static byte[] Page(params DeathMarkerFact[] rows)
    {
        byte[] p = new byte[21 + rows.Length * 29]; p[0] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(1), 10);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(9), 7);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(13), 123);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(17), (ushort)rows.Length);
        p[19] = (byte)rows.Length; p[20] = 1;
        for (int i = 0; i < rows.Length; i++)
        {
            var row = p.AsSpan(21 + 29 * i); var f = rows[i]; row[0] = (byte)f.Kind;
            BinaryPrimitives.WriteUInt64LittleEndian(row[1..], f.Id); BinaryPrimitives.WriteUInt64LittleEndian(row[9..], f.Revision);
            BinaryPrimitives.WriteSingleLittleEndian(row[17..], f.X); BinaryPrimitives.WriteSingleLittleEndian(row[21..], f.Y);
            BinaryPrimitives.WriteSingleLittleEndian(row[25..], f.Z);
        }
        return p;
    }

    [Theory]
    [InlineData(DeathMarkerAction.Add, DeathMarkerKind.Grave)]
    [InlineData(DeathMarkerAction.Add, DeathMarkerKind.MarkerOnly)]
    [InlineData(DeathMarkerAction.Remove, DeathMarkerKind.Grave)]
    [InlineData(DeathMarkerAction.Remove, DeathMarkerKind.MarkerOnly)]
    public void Wire_facts_keep_type_world_full_width_identity_revision_and_coordinates(DeathMarkerAction action, DeathMarkerKind kind)
    {
        var fact = Fact(action, ulong.MaxValue) with { Kind = kind, World = uint.MaxValue };
        byte[] up = DeathMarkerCodec.BuildUp(fact);
        Assert.Equal(38, up.Length); Assert.Equal(Protocol.DeathMarkerUp, up[0]);
        Assert.Equal(35, BinaryPrimitives.ReadUInt16LittleEndian(up.AsSpan(1)));
        Assert.Equal(36, Protocol.DeathMarkerDownLen);
        Assert.True(DeathMarkerCodec.TryRead(up.AsSpan(3), out var read)); Assert.Equal(fact, read);
        Assert.False(DeathMarkerCodec.TryRead(up, out _)); // envelope is never mistaken for fact bytes
        Assert.Equal(0x88, up[3 + 7]); Assert.Equal(0x11, up[3 + 14]);
    }

    [Fact]
    public void Wire_decoder_refuses_every_truncation_trailing_byte_version_unknown_enum_and_nonfinite_position()
    {
        byte[] p = Body(Fact());
        for (int n = 0; n < p.Length; n++) Assert.False(DeathMarkerCodec.TryRead(p.AsSpan(0, n), out _));
        Assert.False(DeathMarkerCodec.TryRead([.. p, 0], out _));
        foreach (int at in new[] { 0, 1, 2 }) { var bad = p.ToArray(); bad[at] = 255; Assert.False(DeathMarkerCodec.TryRead(bad, out _)); }
        foreach (int at in new[] { 3, 7, 15 }) { var bad = p.ToArray(); bad.AsSpan(at, at == 3 ? 4 : 8).Clear(); Assert.False(DeathMarkerCodec.TryRead(bad, out _)); }
        foreach (int at in new[] { 23, 27, 31 }) foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        { var bad = p.ToArray(); BinaryPrimitives.WriteSingleLittleEndian(bad.AsSpan(at), invalid); Assert.False(DeathMarkerCodec.TryRead(bad, out _)); }
    }

    [Fact]
    public void Invalid_facts_cannot_be_encoded_as_reliable_facts()
    {
        foreach (var bad in new[] { Fact() with { World = 0 }, Fact() with { Id = 0 }, Fact() with { Revision = 0 },
            Fact() with { Action = DeathMarkerAction.ClearOwner }, Fact() with { Kind = (DeathMarkerKind)2 }, Fact() with { X = float.NaN } })
        { Assert.False(DeathMarkerCodec.Valid(bad)); Assert.Throws<ArgumentException>(() => DeathMarkerCodec.BuildUp(bad)); }
    }

    [Fact]
    public void Native_event_requires_exact_43_bytes_and_a_real_generation()
    {
        var expected = Fact(DeathMarkerAction.Remove, 2); byte[] p = Native(expected);
        Assert.True(DeathMarkerCodec.TryNativeEvent(p, out var read)); Assert.Equal(expected, read.Fact); Assert.Equal(0x0102030405060708ul, read.Generation);
        for (int n = 0; n < p.Length; n++) Assert.False(DeathMarkerCodec.TryNativeEvent(p.AsSpan(0, n), out _));
        Assert.False(DeathMarkerCodec.TryNativeEvent([.. p, 0], out _));
        Assert.False(DeathMarkerCodec.TryNativeEvent(Native(expected, 0), out _));
        p[2] = 255; Assert.False(DeathMarkerCodec.TryNativeEvent(p, out _));
        p = Native(expected); BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(39), float.NaN);
        Assert.False(DeathMarkerCodec.TryNativeEvent(p, out _));
    }

    [Fact]
    public void Page_and_control_request_bytes_preserve_the_native_protocol_offsets()
    {
        byte[] q = DeathMarkerCodec.PageArgs(11, 7, 123, 64); Assert.Equal(19, q.Length); Assert.Equal(1, q[0]);
        Assert.Equal(11ul, BinaryPrimitives.ReadUInt64LittleEndian(q.AsSpan(1))); Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan(9)));
        Assert.Equal(123u, BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan(13))); Assert.Equal(64, BinaryPrimitives.ReadUInt16LittleEndian(q.AsSpan(17)));
        var f = Fact(DeathMarkerAction.Remove, 2);
        byte[] c = DeathMarkerCodec.ControlArgs(f.Action, f.Kind, 3, 11, f.World, f.Id, f.Revision, f.X, f.Y, f.Z);
        Assert.Equal(44, c.Length); Assert.Equal(new byte[] { 1, 0, 1, 3 }, c[..4]);
        Assert.Equal(11ul, BinaryPrimitives.ReadUInt64LittleEndian(c.AsSpan(4))); Assert.Equal(f.World, BinaryPrimitives.ReadUInt32LittleEndian(c.AsSpan(12)));
        Assert.Equal(f.Id, BinaryPrimitives.ReadUInt64LittleEndian(c.AsSpan(16))); Assert.Equal(2ul, BinaryPrimitives.ReadUInt64LittleEndian(c.AsSpan(24)));
        Assert.Equal(f.X, BinaryPrimitives.ReadSingleLittleEndian(c.AsSpan(32))); Assert.Equal(f.Y, BinaryPrimitives.ReadSingleLittleEndian(c.AsSpan(36)));
        Assert.Equal(f.Z, BinaryPrimitives.ReadSingleLittleEndian(c.AsSpan(40)));
        Assert.Throws<ArgumentException>(() => DeathMarkerCodec.ControlArgs((DeathMarkerAction)5, f.Kind, 3, 11, 7));
        Assert.Throws<ArgumentException>(() => DeathMarkerCodec.ControlArgs(f.Action, (DeathMarkerKind)2, 3, 11, 7));
        Assert.Throws<ArgumentException>(() => DeathMarkerCodec.ControlArgs(f.Action, f.Kind, 3, 0, 7));
        Assert.Throws<ArgumentException>(() => DeathMarkerCodec.ControlArgs(f.Action, f.Kind, 3, 11, 7, x: float.NaN));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(16)]
    public void Native_pages_preserve_snapshot_receipt_and_all_rows(int count)
    {
        var expected = Enumerable.Range(1, count).Select(i => Fact(id: (ulong)i)).ToArray();
        byte[] p = Page(expected);
        Assert.Equal(21 + 29 * count, p.Length); Assert.True(DeathMarkerCodec.TryPage(p, out var page)); Assert.NotNull(page);
        Assert.Equal(10ul, page.Generation); Assert.Equal(7u, page.World); Assert.Equal(123u, page.Token);
        Assert.Equal(count, page.Next); Assert.True(page.Done); Assert.Equal(expected, page.Rows);
    }

    [Fact]
    public void Native_page_refuses_partial_overfull_bad_receipt_and_bad_rows()
    {
        byte[] p = Page(Fact());
        for (int n = 0; n < p.Length; n++) Assert.False(DeathMarkerCodec.TryPage(p.AsSpan(0, n), out _));
        Assert.False(DeathMarkerCodec.TryPage([.. p, 0], out _));
        Assert.False(DeathMarkerCodec.TryPage(Page(Enumerable.Range(1, 17).Select(i => Fact(id: (ulong)i)).ToArray()), out _));
        foreach (int at in new[] { 1, 9, 13 }) { var bad = p.ToArray(); bad.AsSpan(at, at == 1 ? 8 : 4).Clear(); Assert.False(DeathMarkerCodec.TryPage(bad, out _)); }
        foreach (int at in new[] { 0, 20, 21 }) { var bad = p.ToArray(); bad[at] = 255; Assert.False(DeathMarkerCodec.TryPage(bad, out _)); }
        var next = p.ToArray(); BinaryPrimitives.WriteUInt16LittleEndian(next.AsSpan(17), 129); Assert.False(DeathMarkerCodec.TryPage(next, out _));
        var coordinate = p.ToArray(); BinaryPrimitives.WriteSingleLittleEndian(coordinate.AsSpan(21 + 17), float.NaN); Assert.False(DeathMarkerCodec.TryPage(coordinate, out _));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void Native_ack_keeps_safe_distinct_from_typed_refusal(byte status)
    {
        byte[] p = new byte[16]; p[0] = 1; p[1] = (byte)DeathMarkerAction.PrepareLoad; p[2] = status;
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(4), 12); BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(12), 7);
        Assert.True(DeathMarkerCodec.TryAck(p, out var ack)); Assert.Equal(status == 0, ack.Safe);
        Assert.Equal(DeathMarkerAction.PrepareLoad, ack.Action); Assert.Equal(12ul, ack.Generation); Assert.Equal(7u, ack.World);
        for (int n = 0; n < p.Length; n++) Assert.False(DeathMarkerCodec.TryAck(p.AsSpan(0, n), out _));
        Assert.False(DeathMarkerCodec.TryAck([.. p, 0], out _));
        foreach (int at in new[] { 0, 1, 2, 3 }) { var bad = p.ToArray(); bad[at] = 255; Assert.False(DeathMarkerCodec.TryAck(bad, out _)); }
    }

    [Fact]
    public void Remove_tombstone_rejects_old_add_and_equal_conflict_without_blocking_cancelled_retries()
    {
        var rows = new DeathMarkerRevisions(); var add = Fact(); var remove = Fact(DeathMarkerAction.Remove, 2);
        Assert.True(rows.Observe(1, add)); Assert.True(rows.Observe(1, add)); // not applied: can retry
        rows.Applied(1, add); Assert.False(rows.Observe(1, add));
        Assert.True(rows.Observe(1, remove)); Assert.False(rows.Observe(1, add));
        Assert.True(rows.Current(1, remove)); Assert.False(rows.Current(1, add));
        Assert.False(rows.Observe(1, remove with { X = remove.X + 1 })); Assert.True(rows.Current(1, remove));
        rows.Applied(1, add); Assert.True(rows.Observe(1, remove)); // late old ACK never completes newer mutation
        rows.Applied(1, remove); Assert.False(rows.Observe(1, remove)); Assert.False(rows.Observe(1, add));
    }

    [Fact]
    public void Owner_world_and_entity_are_independent_and_clear_only_the_requested_owner()
    {
        var rows = new DeathMarkerRevisions(); var dead = Fact(DeathMarkerAction.Remove, 2); var add = Fact();
        Assert.True(rows.Observe(1, dead)); rows.Applied(1, dead);
        Assert.True(rows.Observe(2, add)); Assert.True(rows.Observe(1, add with { World = 8 })); Assert.True(rows.Observe(1, add with { Id = 2 }));
        rows.Clear(2); Assert.False(rows.Current(2, add)); Assert.True(rows.Current(1, dead));
        Assert.False(rows.Observe(1, add));
        rows.Clear(); Assert.True(rows.Observe(1, add));
    }

    [Fact]
    public void Bounded_revision_store_never_evicts_a_tombstone_to_accept_an_old_replay()
    {
        var rows = new DeathMarkerRevisions(); var tombstone = Fact(DeathMarkerAction.Remove, 2);
        Assert.True(rows.Observe(1, tombstone)); rows.Applied(1, tombstone);
        for (ulong id = 1; id <= 4095; id++) Assert.True(rows.Observe(1, Fact(id: id)));
        Assert.False(rows.Observe(1, Fact(id: 5000))); Assert.False(rows.Observe(1, Fact()));
        Assert.True(rows.Current(1, tombstone));
    }
}
