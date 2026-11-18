// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Reflection;

namespace KcdMp.Client.Tests;

public class T3LootReportAndRejoinTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(GameBridge b, string key, object value) => typeof(GameBridge).GetField(key, Private)!.SetValue(b, value);
    private static T Get<T>(GameBridge b, string key) => (T)typeof(GameBridge).GetField(key, Private)!.GetValue(b)!;
    private static object? Call(GameBridge b, string key, params object[] args) => typeof(GameBridge).GetMethod(key, Private)!.Invoke(b, args);

    [Fact]
    public void Lua_reports_cannot_overwrite_native_or_other_packages_and_invalid_snapshots_keep_last_good_values()
    {
        var b = new GameBridge(new ClientConfig());
        var counts = Get<ConcurrentDictionary<string, long>>(b, "_t3Counters");
        counts["decision_blocked"] = 123;
        const string good = "pickup_asks=1 pickup_optimistic=2 pickup_rollbacks=3 pickup_unknown_kept=4 ask_timeouts=5 ask_latency_ms_p95=6 take_busy_refusals=7";
        Call(b, "T3LuaStats", "t3_loot_stats", good);
        Assert.Equal(1, counts["pickup_asks"]); Assert.Equal(7, counts["take_busy_refusals"]);
        foreach (string bad in new[] { good + " decision_blocked=0", good + " pickup_asks=7", good.Replace("pickup_asks=1", "pickup_asks=-1"), "pickup_asks=999" })
            Call(b, "T3LuaStats", "t3_loot_stats", bad);
        Assert.Equal(123, counts["decision_blocked"]); Assert.Equal(1, counts["pickup_asks"]);
        Assert.Equal(4, counts["loot_stats_unreadable"]);
        Call(b, "T3LuaStats", "t3_input_stats", "input_held_max_s=4 input_releases=2");
        Assert.Equal(4, counts["input_held_max_s"]); Assert.Equal(2, counts["input_releases"]);
    }

    [Theory]
    [InlineData(7u, 7u, false)]
    [InlineData(7u, 0u, false)]
    [InlineData(0u, 8u, false)]
    [InlineData(7u, 1u, false)]
    [InlineData(7u, 8u, true)]
    public void Host_identity_or_retry_needs_a_changed_known_world_epoch(uint loaded, uint heard, bool expected)
    {
        var b = new GameBridge(new ClientConfig());
        Set(b, "_joinedWorld", true); Set(b, "_t3JoinedHostWorld", loaded);
        var epochs = Get<Wo206Epochs>(b, "_w206"); epochs.LearnWorld(heard, 99); // a changed transfer epoch alone does not authorize a load
        Assert.Equal(expected, (bool)Call(b, "T3ArmInWorldRejoin", "host-identity-after-reload")!);
        Assert.Equal(expected, Get<bool>(b, "_rejoinPending"));
        Assert.Equal(expected, (bool)Call(b, "T3AdmitInWorldRejoin")!);
        var counts = Get<ConcurrentDictionary<string, long>>(b, "_t3Counters");
        Assert.Equal(expected ? 1 : 0, counts.GetValueOrDefault("auto_rejoins_in_world"));
    }

    [Fact]
    public void A_pending_retry_is_rechecked_immediately_before_asking_for_the_world()
    {
        var b = new GameBridge(new ClientConfig()); Set(b, "_joinedWorld", true); Set(b, "_t3JoinedHostWorld", 7u);
        var epochs = Get<Wo206Epochs>(b, "_w206"); epochs.LearnWorld(8, 2);
        Assert.True((bool)Call(b, "T3ArmInWorldRejoin", "reload")!);
        epochs.LearnWorld(7, 3);
        Assert.False((bool)Call(b, "T3AdmitInWorldRejoin")!);
        Assert.False(Get<bool>(b, "_rejoinPending"));
    }

    [Fact]
    public async Task The_actual_joiner_tick_suppresses_a_stale_pending_rejoin_without_starting_a_transfer()
    {
        var b = new GameBridge(new ClientConfig());
        Set(b, "_joinedWorld", true); Set(b, "_t3JoinedHostWorld", 7u); Set(b, "_rejoinPending", true);
        Set(b, "_hostModeKnown", true); Set(b, "_hostSharedWorld", true);
        Get<Wo206Epochs>(b, "_w206").LearnWorld(7, 20);
        await (Task)Call(b, "Wo124JoinerTickAsync")!;
        Assert.False(Get<bool>(b, "_rejoinPending"));
        Assert.Equal(0u, Get<uint>(b, "_joinOutId"));
        Assert.Equal(0, Get<ConcurrentDictionary<string, long>>(b, "_t3Counters").GetValueOrDefault("auto_rejoins_in_world"));
    }

    [Fact]
    public void Link_cut_in_an_unchanged_shared_world_never_arms_a_leave_and_reload_but_initial_transfer_recovery_survives()
    {
        var b = new GameBridge(new ClientConfig()); Set(b, "_joinedWorld", true); Set(b, "_t3JoinedHostWorld", 7u);
        Get<Wo206Epochs>(b, "_w206").LearnWorld(7, 1);
        Call(b, "Wo207ArmLinkRejoin", 4u);
        Assert.False(Get<bool>(b, "_w207LinkRejoin")); Assert.False(Get<bool>(b, "_joinFromWorldOnce"));
        Set(b, "_joinedWorld", false);
        Call(b, "Wo207ArmLinkRejoin", 4u);
        Assert.True(Get<bool>(b, "_w207LinkRejoin")); Assert.True(Get<bool>(b, "_joinFromWorldOnce"));
    }
}
