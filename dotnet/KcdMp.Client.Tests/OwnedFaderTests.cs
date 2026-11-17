// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;

namespace KcdMp.Client.Tests;

public class OwnedFaderTests
{
    [Theory]
    [InlineData(FaderOwner.VideoLanding)]
    [InlineData(FaderOwner.Wake)]
    [InlineData(FaderOwner.Harness)]
    public void Each_fade_caller_carries_its_owner_and_exact_lease(FaderOwner owner)
    {
        uint token = OwnedFader.NextToken();
        Assert.NotEqual(0u, token);
        Assert.NotEqual(token, OwnedFader.NextToken());
        foreach (bool on in new[] { true, false })
        {
            var b = OwnedFader.Command(on, 0.3f, owner, token);
            Assert.Equal(10, b.Length);
            Assert.Equal(on ? 1 : 0, b[0]);
            Assert.Equal(0.3f, BitConverter.ToSingle(b, 1));
            Assert.Equal((byte)owner, b[5]);
            Assert.Equal(token, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(6)));
        }
    }

    [Fact]
    public void Anonymous_and_zero_lease_requests_are_refused_before_the_pipe()
    {
        Assert.Throws<ArgumentException>(() => OwnedFader.Command(true, 0.3f, (FaderOwner)0, 1));
        Assert.Throws<ArgumentException>(() => OwnedFader.Command(true, 0.3f, FaderOwner.Wake, 0));
    }

    [Fact]
    public void A_native_ok_with_refused_payload_never_acquires_a_wake_hold()
    {
        Assert.False(OwnedFader.Acquired(true, [0]));
        Assert.False(OwnedFader.Acquired(true, []));
        Assert.False(OwnedFader.Acquired(false, [1]));
        Assert.True(OwnedFader.Acquired(true, [1]));
    }
}
