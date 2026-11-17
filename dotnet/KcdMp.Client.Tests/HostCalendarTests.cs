// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using Xunit;

namespace KcdMp.Client.Tests;

public class HostCalendarTests
{
    [Fact]
    public void FrozenJoinSnapshotSurvivesLongLoadWithoutInventingProgress()
    {
        Assert.True(HostCalendar.TryParse(HostCalendar.Format(572161000, 0, 1000, true), 50, 1000, true, 0, out var sample));
        Assert.True(sample.Fresh(208050));
        Assert.Equal(572161000, sample.TargetMs(208050));
    }

    [Fact]
    public void ResumeReplacesBothTimeBasisAndRate()
    {
        Assert.True(HostCalendar.TryParse(HostCalendar.Format(572161000, 0, 1000, true), 50, 1000, true, 0, out var held));
        Assert.True(HostCalendar.TryParse(HostCalendar.Format(572161000, 15000, 209000, true), 208050, 209000, true, 0, out var running));
        Assert.Equal(572161000, held.TargetMs(208050));
        Assert.Equal(572176000, running.TargetMs(209050));
        Assert.False(running.Fresh(219051));
    }

    [Fact]
    public void PreciseSampleIncludesReliableQueueAge()
    {
        const long exact = 572161123;
        Assert.True(HostCalendar.TryParse(HostCalendar.Format(exact, 15000, 5000, true), 100, 5120, true, 0, out var sample));
        Assert.Equal(exact + 1800, sample.TargetMs(100));
        Assert.Equal(exact + 3300, sample.TargetMs(200));
    }

    [Fact]
    public void UnsynchronisedPeerUsesBoundedTransitEstimate()
    {
        Assert.True(HostCalendar.TryParse(HostCalendar.Format(1000000, 15000, 5000, false), 100, 90000000, true, 40, out var sample));
        Assert.Equal(1000600, sample.TargetMs(100));
    }

    [Theory]
    [InlineData("b1 572161000 0 5000 1 0")]
    [InlineData("b1 572161123 15000 5000 1 0")]
    public void ZeroAndNormalCalendarRatesAreAccepted(string text) =>
        Assert.True(HostCalendar.TryParse(text, 100, 5000, true, 0, out _));

    [Theory]
    [InlineData("572161 15000")]
    [InlineData("b1 572161000 17260 5000 1 0")]
    [InlineData("b1 572161000 -1 5000 1 0")]
    [InlineData("b1 -1 15000 5000 1 0")]
    [InlineData("b1 4294967295001 15000 5000 1 0")]
    [InlineData("b1 572161000 15000 5000 2 0")]
    [InlineData("b1 572161e3 15000 5000 1 0")]
    [InlineData("b1 572161000 15000 5000 1")]
    public void MalformedAndAcceleratedCalendarSamplesAreRejected(string text) =>
        Assert.False(HostCalendar.TryParse(text, 100, 5000, true, 0, out _));

    [Fact]
    public void RunningPacketOlderThanTenSecondsIsRejectedButFrozenPacketRemainsValid()
    {
        Assert.False(HostCalendar.TryParse(HostCalendar.Format(572161000, 15000, 1000, true), 100, 12000, true, 0, out _));
        Assert.True(HostCalendar.TryParse(HostCalendar.Format(572161000, 0, 1000, true), 100, 12000, true, 0, out _));
    }

    [Fact]
    public void OldFrozenSampleCannotBootstrapAnotherAttempt()
    {
        Assert.True(HostCalendar.TryParse(HostCalendar.Format(572161000, 0, 1000, true, 123), 100, 1000, true, 0, out var sample));
        Assert.True(sample.ReadyForJoin(200, 123));
        Assert.False(sample.ReadyForJoin(200, 124));
        Assert.True(sample.ReadyForJoin(208100, 123));
        Assert.True(sample.Fresh(208100));   // once armed, the same held load can take its time
    }
}
