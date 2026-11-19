// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Security.Cryptography;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public sealed class HostAbsentPauseTests
{
    [Fact]
    public void A_joined_world_pauses_and_a_join_in_progress_aborts()
    {
        Assert.Equal(HostAbsentRules.GoneAction.Pause, HostAbsentRules.OnHostGone(joinedWorld: true, joinPreparing: false, joinLoading: false));
        Assert.Equal(HostAbsentRules.GoneAction.AbortJoin, HostAbsentRules.OnHostGone(joinedWorld: true, joinPreparing: true, joinLoading: false));
        Assert.Equal(HostAbsentRules.GoneAction.AbortJoin, HostAbsentRules.OnHostGone(joinedWorld: false, joinPreparing: false, joinLoading: true));
        Assert.Equal(HostAbsentRules.GoneAction.Ignore, HostAbsentRules.OnHostGone(joinedWorld: false, joinPreparing: false, joinLoading: false));
        Assert.False(HostAbsentRules.MayWritePersonalSave(hostAbsent: true));
    }

    [Fact]
    public void Only_the_same_player_and_world_resume()
    {
        var left = new HostLease("tcp:127.0.0.1", "the-host", 142, Guid.ParseExact("452560e1066a4794a05071e8c539ecf4", "N"));
        Assert.True(HostAbsentRules.SameReturn(left, left.IdentityKey, left.Name, 142, left.RunId));
        Assert.False(HostAbsentRules.SameReturn(left, left.IdentityKey, "joiner-1", 142, left.RunId));
        Assert.False(HostAbsentRules.SameReturn(left, left.IdentityKey, left.Name, 99, left.RunId));
        Assert.False(HostAbsentRules.SameReturn(left, left.IdentityKey, left.Name, 142, Guid.NewGuid()));
        Assert.False(HostAbsentRules.SameReturn(left, left.IdentityKey, left.Name, 0, left.RunId));
    }

    [Fact]
    public void The_pause_does_not_touch_a_personal_save_and_a_different_epoch_does_not_resume()
    {
        string path = Path.Combine(Path.GetTempPath(), "kct-host-absent-" + Guid.NewGuid().ToString("N") + ".whs");
        byte[] original = [1, 2, 3, 4, 5, 6, 7, 8];
        File.WriteAllBytes(path, original);
        try
        {
            var bridge = new GameBridge(new ClientConfig());
            var epochs = (Wo206Epochs)typeof(GameBridge).GetField("_w206", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(bridge)!;
            Guid run = Guid.ParseExact("452560e1066a4794a05071e8c539ecf4", "N");
            Assert.True(epochs.LearnWorld(142, 2));
            bridge.HostAbsentOnEpoch(142, run);
            bridge.EnterHostAbsentPause("host-left");
            Assert.True(bridge.HostAbsentPause);
            Assert.Contains("host-absent", bridge.JoinStatusJson());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(original)), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

            bridge.HostAbsentOnEpoch(99, run);
            Assert.True(bridge.HostAbsentPause);
            bridge.HostAbsentOnEpoch(142, Guid.NewGuid());
            Assert.True(bridge.HostAbsentPause);
            bridge.HostAbsentOnEpoch(142, run);
            Assert.False(bridge.HostAbsentPause);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(original)), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        }
        finally { File.Delete(path); }
    }
}
