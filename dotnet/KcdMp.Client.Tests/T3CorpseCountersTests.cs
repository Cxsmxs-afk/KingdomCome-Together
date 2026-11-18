// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Reflection;

namespace KcdMp.Client.Tests;

public class T3CorpseCountersTests
{
    private const string Good = "lua_corpse_visible=3 lua_corpse_hidden_blocked=4 lua_corpse_unknown_kept=5";
    private static bool Parse(string kind, string fields, out Dictionary<string, long> values)
    {
        var parser = typeof(GameBridge).Assembly.GetType("KcdMp.Client.T3LootCounters")!.GetMethod("TryRead", BindingFlags.Static | BindingFlags.NonPublic)!;
        object?[] args = [kind, fields, null];
        bool ok = (bool)parser.Invoke(null, args)!; values = (Dictionary<string, long>)args[2]!; return ok;
    }

    [Fact]
    public void Lua_group_is_complete_and_cannot_overwrite_native_measurements()
    {
        Assert.True(Parse("t3_corpse_stats", Good, out var values));
        Assert.Equal(3, values.Count); Assert.Equal(3, values["lua_corpse_visible"]);
        foreach (string text in new[] { "", "lua_corpse_visible=3", Good + " lua_corpse_visible=6", Good + " corpses_visible=1000", Good.Replace("=3", "=-1"), Good.Replace("=3", "=1.5"), Good.Replace("=3", "=9223372036854775808") })
            Assert.False(Parse("t3_corpse_stats", text, out _));
        Assert.False(Parse("t3_loot_stats", Good, out _));
    }

    [Fact]
    public async Task Bad_lua_group_preserves_last_readback_and_the_native_gauge()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var bridge = new GameBridge(new ClientConfig());
        var counters = (ConcurrentDictionary<string, long>)typeof(GameBridge).GetField("_t3Counters", flags)!.GetValue(bridge)!;
        counters["corpses_visible"] = 12;
        MethodInfo receive = typeof(GameBridge).GetMethod("T3LuaStats", flags)!;
        receive.Invoke(bridge, ["t3_corpse_stats", Good]);
        receive.Invoke(bridge, ["t3_corpse_stats", "lua_corpse_visible=999"]);
        receive.Invoke(bridge, ["t3_corpse_stats", Good + " corpses_visible=1000"]);
        Assert.Equal(3, counters["lua_corpse_visible"]); Assert.Equal(12, counters["corpses_visible"]);
        Assert.Equal(2, counters["corpse_lua_stats_unreadable"]);
        await ((CombatPipe)typeof(GameBridge).GetField("_combat", flags)!.GetValue(bridge)!).DisposeAsync();
    }
}
