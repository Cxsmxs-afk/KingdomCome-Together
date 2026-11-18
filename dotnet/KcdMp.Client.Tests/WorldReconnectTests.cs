// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Reflection;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public sealed class WorldReconnectTests
{
    private const byte Receiver = 5, Host = 2;
    private const uint HostKey = 17, Seed = 41, World = 7;
    private const ulong HostGeneration = 62;
    private static readonly Guid Instance = Guid.ParseExact("be8f121bb25b46289953a2cc36d8368c", "N");

    private static byte[] Status(byte source, byte state, uint id, string reason, ushort flags = 0, byte target = Receiver)
    {
        byte[] frame = JoinStatusCodec.Build(target, id, state, Protocol.JoinReasonId(reason), flags);
        return [source, .. frame.AsSpan(3)];
    }

    private static byte[] Epoch(byte source, uint world = World, ulong gen = HostGeneration, Guid? instance = null, bool modern = true)
    {
        string text = Wo206Rules.EpochText(world, 3, "tick");
        if (modern) text = WorldReconnectFacts.EpochProof(text, instance ?? Instance, gen);
        byte[] frame = new LootMsg(Protocol.W203Epoch, 0, text).BuildUp(Protocol.W203Up, Receiver);
        return [source, .. frame.AsSpan(3)];
    }

    private static WorldReconnectProof Proof(byte source = Host, uint world = World, ulong generation = HostGeneration)
        => new(source, HostKey, Seed, world, generation, Instance);

    [Fact]
    public void Epoch_key_and_seed_must_be_fresh_targeted_and_from_one_relay_authority()
    {
        var facts = new WorldReconnectFacts();
        Assert.True(facts.Observe(Protocol.W203Down, Epoch(Host), Receiver));  // Network epoch can arrive before status.
        Assert.True(facts.Observe(Protocol.JoinStatusDown, Status(3, Protocol.JoinStateHostKey, HostKey, "host-key"), Receiver));
        Assert.True(facts.Observe(Protocol.JoinStatusDown, Status(3, Protocol.JoinStateSession, Seed, "shared-world", Protocol.SessionSeedKnown), Receiver));
        Assert.Null(facts.Proof);
        Assert.False(facts.Observe(Protocol.JoinStatusDown, Status(Host, Protocol.JoinStateHostKey, HostKey, "host-key", target: 4), Receiver));
        Assert.True(facts.Observe(Protocol.JoinStatusDown, Status(Host, Protocol.JoinStateHostKey, HostKey, "host-key"), Receiver));
        Assert.Null(facts.Proof);
        Assert.True(facts.Observe(Protocol.JoinStatusDown, Status(Host, Protocol.JoinStateSession, Seed, "shared-world", Protocol.SessionSeedKnown), Receiver));
        Assert.Equal(Proof(), facts.Proof);
        Assert.True(facts.Observe(Protocol.W203Down, Epoch(3, world: 8), Receiver));
        Assert.Equal(Proof(), facts.Proof);
        Assert.True(facts.Forget(Host));
        Assert.Null(facts.Proof);
    }

    [Fact]
    public void A_legacy_epoch_or_malformed_process_identity_never_qualifies_for_same_world_reconnect()
    {
        var facts = new WorldReconnectFacts();
        Assert.True(facts.Observe(Protocol.JoinStatusDown, Status(Host, Protocol.JoinStateHostKey, HostKey, "host-key"), Receiver));
        Assert.True(facts.Observe(Protocol.JoinStatusDown, Status(Host, Protocol.JoinStateSession, Seed, "shared-world", Protocol.SessionSeedKnown), Receiver));
        Assert.True(facts.Observe(Protocol.W203Down, Epoch(Host, modern: false), Receiver));
        Assert.Null(facts.Proof);
        Assert.True(facts.Observe(Protocol.W203Down, Epoch(Host, gen: 0), Receiver));
        Assert.Null(facts.Proof);
        Assert.True(facts.Observe(Protocol.W203Down, Epoch(Host, instance: Guid.Empty), Receiver));
        Assert.Null(facts.Proof);
        Assert.True(facts.Observe(Protocol.W203Down, Epoch(Host), Receiver));
        Assert.Equal(Proof(), facts.Proof);
        Assert.True(facts.Observe(Protocol.JoinStatusDown, Status(Host, Protocol.JoinStateSession, Seed, "separate", Protocol.SessionSeedKnown), Receiver));
        Assert.Null(facts.Proof);
    }

    [Fact]
    public void Confirmed_same_world_resumes_play_in_the_existing_generation_without_entering_load()
    {
        long now = 500;
        var load = new WorldLoadSequence(() => now);
        Assert.True(load.TryBegin(0, WorldLoadStart.AdoptLoadedWorld, out var before, World));
        Assert.True(load.TryHoldLink(before.Generation));
        var attempt = new WorldReconnectAttempt(new(World, before.Generation), Proof(), now);
        now += 10_500;

        // The relay may assign the same host a new slot. Its install, world and process proof stay fixed.
        var returned = Proof(source: 6);
        Assert.Equal(WorldReconnectDecision.SameWorld, attempt.Decide(load.Current, returned, now));
        Assert.True(attempt.TryClaim(WorldReconnectDecision.SameWorld));
        Assert.True(load.TryReconnect(attempt.Scope.Generation, returned.World));
        Assert.True(load.CanApply(before.Generation));
        Assert.Equal(WorldLoadPhase.Play, load.Current.Phase);
        Assert.Equal(before.Generation, load.Current.Generation);
        Assert.Equal(0, load.PhaseMilliseconds(WorldLoadPhase.Load));
        Assert.False(attempt.TryClaim(WorldReconnectDecision.Timeout));
        Assert.Equal(10_500, attempt.Elapsed(now));
    }

    [Fact]
    public void Only_a_newer_scope_from_the_same_authority_selects_the_rejoin_path()
    {
        var scope = new WorldSyncScope(World, 14);
        var held = new WorldLoadSnapshot(scope.Generation, WorldLoadPhase.Play, 0, World) { LinkHeld = true };
        var attempt = new WorldReconnectAttempt(scope, Proof(), 0);
        var valid = Proof(world: World + 1, generation: HostGeneration + 1);
        Assert.Equal(WorldReconnectDecision.DifferentWorld, attempt.Decide(held, valid, 14));
        Assert.Equal(WorldReconnectDecision.Waiting, attempt.Decide(held, valid with { HostGeneration = HostGeneration }, 14));
        Assert.Equal(WorldReconnectDecision.Waiting, attempt.Decide(held, valid with { World = World }, 14));
        Assert.Equal(WorldReconnectDecision.Waiting, attempt.Decide(held, valid with { Seed = Seed + 1 }, 14));
        Assert.Equal(WorldReconnectDecision.Waiting, attempt.Decide(held, valid with { HostKey = HostKey + 1 }, 14));
        Assert.Equal(WorldReconnectDecision.Waiting, attempt.Decide(held, Proof() with { Instance = Guid.Empty }, 14));
        Assert.Equal(WorldReconnectDecision.Waiting, attempt.Decide(held with { Generation = scope.Generation + 1 }, Proof(), 14));
        Assert.Equal(WorldReconnectDecision.Waiting, attempt.Decide(held with { LinkHeld = false }, Proof(), 14));
    }

    [Fact]
    public void Deadline_and_one_winner_claims_prevent_duplicate_success_or_timeout()
    {
        var scope = new WorldSyncScope(World, 14);
        var held = new WorldLoadSnapshot(scope.Generation, WorldLoadPhase.Play, 0, World) { LinkHeld = true };
        var attempt = new WorldReconnectAttempt(scope, Proof(), 1_000);
        Assert.Equal(WorldReconnectDecision.SameWorld, attempt.Decide(held, Proof(), 31_000));
        Assert.Equal(WorldReconnectDecision.Timeout, attempt.Decide(held, Proof(), 31_001));
        Assert.Equal(0, attempt.Remaining(31_001));

        int claims = 0;
        Parallel.For(0, 16, i =>
        {
            if (attempt.TryClaim(i % 2 == 0 ? WorldReconnectDecision.SameWorld : WorldReconnectDecision.Timeout))
                Interlocked.Increment(ref claims);
        });
        Assert.Equal(1, claims);
    }

    [Fact]
    public void An_old_stream_or_uninstalled_baseline_does_not_reopen_gameplay_after_reconnect()
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        var bridge = new GameBridge(new ClientConfig());
        void Set(string field, object value) => typeof(GameBridge).GetField(field, Private)!.SetValue(bridge, value);
        var switches = (ConcurrentDictionary<string, bool>)typeof(GameBridge).GetField("_w221On", Private)!.GetValue(bridge)!;
        switches["load_sequence"] = switches["baseline_events"] = true;
        Set("_loadSequenceRuntime", true);
        Set("_combatRoleApplied", true);
        Set("_combatRoleApplyComplete", true);
        Set("_sharedWorld", true);
        Set("_w213Caps", Protocol.LinkCapWorldSync);
        var load = (WorldLoadSequence)typeof(GameBridge).GetField("_worldLoad", Private)!.GetValue(bridge)!;
        var epochs = (Wo206Epochs)typeof(GameBridge).GetField("_w206", Private)!.GetValue(bridge)!;
        Assert.True(load.TryBegin(0, WorldLoadStart.AdoptLoadedWorld, out var before, World));
        epochs.LearnWorld(World, 1);
        var scope = new WorldSyncScope(World, before.Generation);
        var ready = typeof(GameBridge).GetMethod("WorldReconnectGameplayReady", Private)!;
        using var old = new MemoryStream();
        using var fresh = new MemoryStream();
        bool Ready() => (bool)ready.Invoke(bridge, [scope, fresh, true])!;
        Assert.False(Ready());

        var receiver = typeof(GameBridge).GetNestedType("WorldSyncReceiver", BindingFlags.NonPublic)!;
        void Bind(Stream stream, WorldSyncScope which, bool installed)
        {
            object link = Activator.CreateInstance(receiver, Private | BindingFlags.Public, null,
                [stream, which, CancellationToken.None, CancellationToken.None], null)!;
            receiver.GetField("Installed")!.SetValue(link, installed);
            Set("_worldReceiver", link);
        }

        Bind(old, scope, installed: true);
        Assert.False(Ready());
        Bind(fresh, scope with { Generation = scope.Generation + 1 }, installed: true);
        Assert.False(Ready());
        Bind(fresh, scope, installed: false);
        Assert.False(Ready());
        Bind(fresh, scope, installed: true);
        Assert.True(Ready());
    }
}
