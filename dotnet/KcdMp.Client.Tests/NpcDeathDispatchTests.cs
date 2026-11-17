// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Reflection;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public class NpcDeathDispatchTests
{
    private static GameBridge Bridge(bool role, bool host)
    {
        var bridge = new GameBridge(new ClientConfig());
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(GameBridge).GetField("_combatRoleApplied", flags)!.SetValue(bridge, role);
        typeof(GameBridge).GetField("_isDamageAuthority", flags)!.SetValue(bridge, host);
        return bridge;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Unknown_role_and_joiner_cannot_write_either_death_packet(bool role, bool host)
    {
        var bridge = Bridge(role, host);
        using var wire = new MemoryStream();
        Assert.False(await bridge.SendNpcDamageAsync(wire, "npc", 0, 0, true, fatal: true));
        await bridge.SendLocalDeathAsync(wire, Guid.NewGuid());
        Assert.Equal(0, wire.Length);
        // The same joiner can still request its ordinary blow.
        Assert.True(await bridge.SendNpcDamageAsync(wire, "npc", 2, 12, true, attributed: true, judgeThere: true));
        Assert.Equal(Protocol.NpcDamageUp, wire.ToArray()[0]);
        Assert.Equal(0, wire.ToArray()[^1] & Protocol.NpcDamageFlagFatal);
    }

    [Fact]
    public async Task Concurrent_Dll_and_Lua_death_observers_write_one_host_fact()
    {
        var bridge = Bridge(true, true);
        using var wire = new MemoryStream();
        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => bridge.SendNpcDamageAsync(wire, "npc", 0, 0, true, fatal: true)));
        Assert.Single(results.Where(v => v));
        Assert.Equal(3 + 1 + 3 + Protocol.NpcDamageFixedTail, wire.Length);
        Assert.NotEqual(0, wire.ToArray()[^1] & Protocol.NpcDamageFlagFatal);
    }

    [Fact]
    public async Task Failed_wire_write_does_not_consume_the_hosts_death()
    {
        var bridge = Bridge(true, true);
        var failed = new MemoryStream();
        failed.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => bridge.SendNpcDamageAsync(failed, "npc", 0, 0, true, fatal: true));
        using var retry = new MemoryStream();
        Assert.True(await bridge.SendNpcDamageAsync(retry, "npc", 0, 0, true, fatal: true));
    }

    [Fact]
    public async Task Guid_fallback_emits_one_death_without_a_second_damage_packet()
    {
        var bridge = Bridge(true, true);
        using var wire = new MemoryStream();
        var soul = Guid.NewGuid();
        await bridge.SendLocalDeathAsync(wire, soul);
        await bridge.SendLocalDeathAsync(wire, soul);
        Assert.Equal(3 + Protocol.DeathUpPayloadLen, wire.Length);
        Assert.Equal(Protocol.DeathUp, wire.ToArray()[0]);
        // A later successful name lookup must not announce the same life again.
        Assert.False(await bridge.SendNpcDamageAsync(wire, "npc", 0, 0, true, fatal: true, deathSoul: soul));
        Assert.Equal(3 + Protocol.DeathUpPayloadLen, wire.Length);
    }

    [Fact]
    public async Task Late_soul_resolution_closes_guid_fallback_after_the_name_route_won()
    {
        var bridge = Bridge(true, true);
        using var wire = new MemoryStream();
        var soul = Guid.NewGuid();
        // The observer's lookup had no GUID; it announced by name.
        Assert.True(await bridge.SendNpcDamageAsync(wire, "npc", 0, 0, true, fatal: true));
        // The DLL later resolves that same body, then a lookup fails again.
        Assert.False(await bridge.SendNpcDamageAsync(wire, "npc", 0, 0, true, fatal: true, deathSoul: soul));
        await bridge.SendLocalDeathAsync(wire, soul);
        Assert.Equal(3 + 1 + 3 + Protocol.NpcDamageFixedTail, wire.Length);
        Assert.Equal(Protocol.NpcDamageUp, wire.ToArray()[0]);
    }

    [Fact]
    public void Failed_dispatch_can_retry_and_old_world_failure_cannot_clear_new_claim()
    {
        var gate = new NpcDeathDispatchGate();
        Assert.True(gate.TryBegin("npc", true, true, out long old));
        Assert.False(gate.TryBegin("npc", true, true, out _));
        gate.Complete("npc", old, false);
        Assert.True(gate.TryBegin("npc", true, true, out old));
        gate.Reset(); // loading another save starts a new life
        Assert.True(gate.TryBegin("npc", true, true, out long current));
        gate.Complete("npc", old, false);
        Assert.False(gate.TryBegin("npc", true, true, out _));
        gate.Complete("npc", current, false);
        Assert.True(gate.TryBegin("npc", true, true, out _));
    }

    [Fact]
    public void Host_dead_to_alive_sample_starts_a_new_life_but_a_queued_old_alive_does_not()
    {
        var gate = new NpcDeathDispatchGate();
        Assert.True(gate.TryBegin("npc", true, true, out long ticket));
        gate.Complete("npc", ticket, true);
        gate.NoteState("npc", false); // an older living row already in the native queue
        Assert.False(gate.TryBegin("npc", true, true, out _));
        gate.NoteState("npc", true);
        gate.NoteState("npc", false); // the host actually brings this body back
        Assert.True(gate.TryBegin("npc", true, true, out _));
    }
}
