// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public sealed class RangedAimHoldTests
{
    private static BodyState2 Drawn(byte charge = 255, short yaw = 0, short pitch = 0) =>
        new(0, 0, BodyState2Bits.RangedAim, WireZone.Undefined, WireGuardStance.None, WireZone.Undefined, charge, yaw, pitch);

    [Fact]
    public void Charge_and_aim_changes_are_a_send_and_an_unreadable_phase_is_not_an_end()
    {
        var held = Drawn();
        var turned = Drawn(255, 300, -100);
        Assert.True(Wo201Rules.AimChanged(held, turned));
        Assert.False(Wo201Rules.AimChanged(held, held));
        Assert.False(Wo201Rules.IsHeldDraw(255));
        Assert.False(Wo201Rules.ConfirmedOff(held));
        Assert.True(Wo201Rules.IsHeldDraw(1));
        Assert.True(Wo201Rules.IsHeldDraw(2));
        Assert.False(Wo201Rules.IsHeldDraw(4));
    }

    [Fact]
    public void A_confirmed_end_is_idle_and_a_bow_is_not_a_crossbow()
    {
        var idle = new BodyState2(0, 0, BodyState2Bits.None, WireZone.Undefined, WireGuardStance.None, WireZone.Undefined, 0, 0, 0);
        Assert.True(Wo201Rules.ConfirmedOff(idle));
        Assert.True(Wo201Rules.AimChanged(Drawn(), idle));
        Assert.True(Wo201Rules.IsBow("longbow"));
        Assert.False(Wo201Rules.IsBow("crossbow_light"));
        Assert.Equal("", Wo201Rules.RangedFragment(2));
        Assert.Equal("Fire", Wo201Rules.RangedFragment(3));
        Assert.Equal("l_bow", Wo201Rules.RangedTags(3, bow: true));
        Assert.NotEqual(Wo201Rules.RangedTags(4, bow: false), Wo201Rules.RangedTags(3, bow: true));
    }
}
