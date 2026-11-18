// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using KcdMp.Client;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public sealed class WorldBaselineReadbackTests
{
    [Fact]
    public void A_wrapped_angle_and_observed_body_state_confirm_the_baseline()
    {
        Assert.True(WorldBaselinePose.Confirms("10.1 11.2 12.1 -3.12 0 1 84.5", 10, 11, 12, 3.13f, false, true, 84));
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("NaN 11 12 0 1 0 0")]
    [InlineData("11.3 12.3 12 0 1 0 0")]
    [InlineData("10 11 14 0 1 0 0")]
    [InlineData("10 11 12 1.2 1 0 0")]
    [InlineData("10 11 12 0 - 0 0")]
    [InlineData("10 11 12 0 0 0 0")]
    [InlineData("10 11 12 0 1 1 0")]
    [InlineData("10 11 12 0 1 0 2")]
    public void Missing_or_different_engine_state_cannot_confirm_the_baseline(string reading)
    {
        Assert.False(WorldBaselinePose.Confirms(reading, 10, 11, 12, 0, true, false, 0));
    }

    [Fact]
    public void A_marker_receive_is_distinct_from_its_exact_applied_receipt()
    {
        var revisions = new DeathMarkerRevisions();
        var add = new DeathMarkerFact(DeathMarkerAction.Add, DeathMarkerKind.Grave, 2, 3, 4, 10, 11, 12);
        Assert.True(revisions.Observe(1, add));
        Assert.False(revisions.IsApplied(1, add));
        revisions.Applied(1, add);
        Assert.True(revisions.IsApplied(1, add));
        Assert.False(revisions.IsApplied(7, add));
        var remove = add with { Action = DeathMarkerAction.Remove, Revision = 5 };
        Assert.True(revisions.Observe(1, remove));
        Assert.False(revisions.IsApplied(1, add));
        Assert.False(revisions.IsApplied(1, remove));
        revisions.Applied(1, remove);
        Assert.True(revisions.IsApplied(1, remove));
    }
}
