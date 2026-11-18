// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public class T3NativeCountersTests
{
    private static byte[] Payload(int count = 19)
    {
        var bytes = new byte[2 + count * 9]; bytes[0] = 1; bytes[1] = (byte)count;
        for (int i = 0; i < count; i++) { bytes[2 + i * 9] = (byte)i; BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(3 + i * 9), (ulong)i); }
        return bytes;
    }

    [Fact]
    public void Native_measurements_have_fixed_field_identity_and_fit_existing_reply_budget()
    {
        byte[] bytes = Payload(); Assert.Equal(173, bytes.Length);
        Assert.True(T3NativeCounters.TryRead(bytes, out var values));
        Assert.Equal(19, values.Count);
        Assert.Equal(0, values["tp_total"]); Assert.Equal(5, values["wake_dist_m_max"]);
        Assert.Equal(6, values["wake_drop_m_max"]); Assert.Equal(14, values["falldeath_after_tp"]);
        Assert.Equal(18, values["tp_by_source_scene_nudge"]);
    }

    [Fact]
    public void Bounded_unknown_fields_are_forward_compatible_but_duplicates_are_never_valid()
    {
        byte[] bytes = Payload(20); bytes[2 + 19 * 9] = 250;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(3 + 19 * 9), ulong.MaxValue);
        Assert.True(T3NativeCounters.TryRead(bytes, out var values)); Assert.Equal(19, values.Count);
        bytes = Payload(21); bytes[2 + 19 * 9] = 250; bytes[2 + 20 * 9] = 250;
        Assert.False(T3NativeCounters.TryRead(bytes, out _));
        bytes = Payload(); bytes[2 + 18 * 9] = 17;
        Assert.False(T3NativeCounters.TryRead(bytes, out _));
    }

    [Fact]
    public void Truncated_oversized_overflowing_or_incomplete_snapshots_cannot_be_imported()
    {
        byte[] bytes = Payload();
        for (int n = 0; n < bytes.Length; n++) Assert.False(T3NativeCounters.TryRead(bytes.AsSpan(0, n), out _));
        Assert.False(T3NativeCounters.TryRead(Payload(18), out _));
        Assert.False(T3NativeCounters.TryRead(Payload(57), out _));
        bytes[0] = 2; Assert.False(T3NativeCounters.TryRead(bytes, out _));
        bytes = Payload(); BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(3), ulong.MaxValue);
        Assert.False(T3NativeCounters.TryRead(bytes, out _));
        Assert.False(T3NativeCounters.TryRead([.. Payload(), 0], out _));
    }
}
