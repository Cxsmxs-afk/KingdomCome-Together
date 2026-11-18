// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
namespace KcdMp.Client.Tests;

public class T3AuthorityCountersTests
{
    private static string Snapshot => "B " + string.Join(' ', T3AuthorityCounters.NativeKeys.Select((k, i) => k + "=" + i)) + " reason=armed hidden=12";

    [Fact]
    public void Reads_the_complete_native_snapshot_without_importing_legacy_fields()
    {
        Assert.True(T3AuthorityCounters.TryRead(Snapshot, out var values));
        Assert.Equal(8, values.Count);
        Assert.Equal(7, values["unhidden_unbound_10s"]);
        Assert.False(values.ContainsKey("hidden"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("B npc_near=2")]
    [InlineData("")]
    public void Refuses_incomplete_or_unreadable_status(string? text) => Assert.False(T3AuthorityCounters.TryRead(text, out _));

    [Theory]
    [InlineData("-1")]
    [InlineData("5.0")]
    [InlineData("9223372036854775808")]
    [InlineData("1x")]
    public void Refuses_invalid_numeric_values_without_publishing_a_partial_snapshot(string bad) =>
        Assert.False(T3AuthorityCounters.TryRead(Snapshot.Replace("gate_armed_at_s=0", "gate_armed_at_s=" + bad), out _));

    [Fact]
    public void Refuses_duplicate_names_instead_of_overwriting_a_current_gauge_with_a_legacy_value() =>
        Assert.False(T3AuthorityCounters.TryRead(Snapshot + " npc_hidden=999", out _));
}
