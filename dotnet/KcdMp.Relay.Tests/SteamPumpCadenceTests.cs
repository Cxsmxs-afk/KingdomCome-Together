// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using KcdMp.Steam;

namespace KcdMp.Relay.Tests;

public sealed class SteamPumpCadenceTests
{
    [Fact]
    public void Only_payload_traffic_opens_or_renews_the_one_millisecond_window()
    {
        var clock = new Clock();
        var cadence = new SteamPumpCadence(clock);
        Assert.Equal(10, cadence.WaitMs);
        clock.Milliseconds = 1_000;
        Assert.Equal(10, cadence.WaitMs);
        cadence.ObservePayload();
        Assert.Equal(1, cadence.WaitMs);
        clock.Milliseconds += 99;
        Assert.Equal(1, cadence.WaitMs);
        cadence.ObservePayload();
        clock.Milliseconds += 99;
        Assert.Equal(1, cadence.WaitMs);
        clock.Milliseconds++;
        Assert.Equal(10, cadence.WaitMs);
    }

    private sealed class Clock : TimeProvider
    {
        public long Milliseconds { get; set; }
        public override long TimestampFrequency => 1_000;
        public override long GetTimestamp() => Milliseconds;
    }
}
