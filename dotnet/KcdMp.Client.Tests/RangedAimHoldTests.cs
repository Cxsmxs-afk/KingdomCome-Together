// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Diagnostics;
using System.Reflection;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public sealed class RangedAimHoldTests
{
    private static BodyState2 Drawn(byte charge = 255, short yaw = 0, short pitch = 0) =>
        new(0, 0, BodyState2Bits.RangedAim, WireZone.Undefined, WireGuardStance.None, WireZone.Undefined, charge, yaw, pitch);

    [Fact]
    public void Aim_updates_piggyback_and_the_confirmed_end_is_available_after_multiple_lost_positions()
    {
        var bridge = new GameBridge(new ClientConfig());
        var sender = typeof(GameBridge).GetMethod("Wo121State2For", BindingFlags.Instance | BindingFlags.NonPublic)!;
        long time = Stopwatch.Frequency;
        var held = Drawn(127, 140, -60);
        var turn = Drawn(140, 400, -90);
        var off = default(BodyState2) with
        {
            AimYaw = BodyState2.AimAngleUnavailable,
            AimPitch = BodyState2.AimAngleUnavailable,
        };

        Assert.Equal((held, true), Position(held, time, consume: true));
        var turned = Position(turn, time + Stopwatch.Frequency / 50, consume: false);
        Assert.False(turned.Due);
        Assert.Equal(turn, Position(turn, time + Stopwatch.Frequency / 50, consume: true).State);
        Assert.Equal(((BodyState2?)off, true), Position(off, time + Stopwatch.Frequency, consume: true));
        for (int lost = 2; lost <= 5; ++lost)
        {
            var repaired = Position(off, time + lost * Stopwatch.Frequency, consume: true);
            Assert.True(repaired.Due);
            Assert.Equal(off, repaired.State);
        }

        (BodyState2? State, bool Due) Position(BodyState2 state, long stamp, bool consume)
        {
            object?[] args = [new LocalState(1, 0, 0, 0, 0, 0, null, state), stamp, consume, false];
            var block = (BodyState2?)sender.Invoke(bridge, args);
            return (block, (bool)args[3]!);
        }
    }

    [Fact]
    public void Charge_and_aim_changes_are_a_send_and_an_unreadable_phase_is_not_an_end()
    {
        var held = Drawn();
        var turned = Drawn(255, 300, -100);
        Assert.True(Wo201Rules.AimChanged(held, turned));
        Assert.False(Wo201Rules.AimChanged(held, held));
        var unknown = held with { Charge = 0, AimYaw = BodyState2.AimAngleUnavailable, AimPitch = BodyState2.AimAngleUnavailable };
        var measuredZero = unknown with { Bits = unknown.Bits | BodyState2Bits.RangedChargeKnown };
        Assert.True(Wo201Rules.AimChanged(unknown, measuredZero));
    }

    [Fact]
    public void A_confirmed_end_is_idle_and_a_bow_is_not_a_crossbow()
    {
        var idle = new BodyState2(0, 0, BodyState2Bits.None, WireZone.Undefined, WireGuardStance.None, WireZone.Undefined, 0, 0, 0);
        Assert.True(Wo201Rules.AimChanged(Drawn(), idle));
        Assert.True(Wo201Rules.IsBow("longbow"));
        Assert.False(Wo201Rules.IsBow("crossbow_light"));
        Assert.Equal("", Wo201Rules.RangedFragment(2));
        Assert.Equal("Fire", Wo201Rules.RangedFragment(3));
        Assert.Equal("l_bow", Wo201Rules.RangedTags(3, bow: true));
        Assert.NotEqual(Wo201Rules.RangedTags(4, bow: false), Wo201Rules.RangedTags(3, bow: true));
    }
}
