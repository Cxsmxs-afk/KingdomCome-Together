// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;

namespace KcdMp.Client.Tests;

[Collection("T3 connection lifecycle")]
public class T3InputOwnerSyncTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static void Set(GameBridge b, string name, object value) => typeof(GameBridge).GetField(name, Private)!.SetValue(b, value);
    private static Task Invoke(GameBridge b, string method, params object[] args) =>
        (Task)typeof(GameBridge).GetMethod(method, Private)!.Invoke(b, args)!;
    private static void Menu(GameBridge b, bool opened) =>
        typeof(GameBridge).GetMethod("Wo192OnMenu", Private)!.Invoke(b, [opened]);
    private readonly record struct Snapshot(bool Menu, bool Downed, long Serial, ulong Generation, long LifeRevision);
    private sealed class Game : HttpMessageHandler
    {
        public readonly ConcurrentQueue<Snapshot> States = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string command = Uri.UnescapeDataString(request.RequestUri!.Query);
            var m = Regex.Match(command, "KCD2MP_T3InputAgentState\\((true|false),(true|false),(\\d+),\"(\\d+)\",(\\d+)\\)");
            if (m.Success) States.Enqueue(new(m.Groups[1].Value == "true", m.Groups[2].Value == "true",
                long.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), ulong.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture),
                long.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture)));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        }
    }
    private static HostAuthorityLifecycle Lifecycle() => new((phase, generation, _) => Task.FromResult<HostAuthorityLifecycle.Ack?>(
        new(true, phase == HostAuthorityLifecycle.Query ? HostAuthorityLifecycle.Load : phase,
            phase != HostAuthorityLifecycle.Disabled, false, generation, 0)));
    private static GameBridge Bridge(HttpGameTransport transport, HostAuthorityLifecycle lifecycle)
    {
        var b = new GameBridge(new ClientConfig()); Set(b, "_transport", transport); Set(b, "_p1Lifecycle", lifecycle); return b;
    }
    private static async Task<Snapshot> Sync(GameBridge b, Game game, bool forceDue = false)
    {
        if (forceDue) Set(b, "_t3InputNextMs", 0L); // advance the test clock without a one-second sleep
        int count = game.States.Count;
        await Invoke(b, "T3SyncInputOwnersAsync", CancellationToken.None);
        Assert.Equal(count + 1, game.States.Count);
        return game.States.Last();
    }

    [Fact]
    public async Task Down_then_wake_before_the_first_snapshot_preserves_a_nonzero_wake_revision_and_duplicate_up_is_idempotent()
    {
        var game = new Game(); await using var transport = new HttpGameTransport("http://localhost:1403", 5000, game);
        var lifecycle = Lifecycle(); await lifecycle.PendingAsync(true, true, default); var b = Bridge(transport, lifecycle);
        await Invoke(b, "OnLocalDownedAsync", true, (byte)0);
        await Invoke(b, "OnLocalDownedAsync", false, (byte)0);
        var wake = await Sync(b, game);
        Assert.False(wake.Downed); Assert.False(wake.Menu);
        Assert.Equal(lifecycle.Current.Generation, wake.Generation); Assert.True(wake.LifeRevision > 0);

        await Invoke(b, "OnLocalDownedAsync", false, (byte)0);
        var repeat = await Sync(b, game, forceDue: true);
        Assert.False(repeat.Downed); Assert.Equal(wake.LifeRevision, repeat.LifeRevision);
        Assert.Equal(wake.Generation, repeat.Generation); Assert.True(repeat.Serial > wake.Serial);
    }

    [Fact]
    public async Task A_life_edge_belongs_to_its_event_generation_and_cannot_create_a_wake_in_the_next_generation()
    {
        var game = new Game(); await using var transport = new HttpGameTransport("http://localhost:1403", 5000, game);
        var lifecycle = Lifecycle(); await lifecycle.PendingAsync(true, true, default); var b = Bridge(transport, lifecycle);
        await Invoke(b, "OnLocalDownedAsync", true, (byte)0); await Invoke(b, "OnLocalDownedAsync", false, (byte)0);
        var oldWake = await Sync(b, game); Assert.True(oldWake.LifeRevision > 0);
        await lifecycle.PendingAsync(true, true, default); Assert.True(lifecycle.Current.Generation > oldWake.Generation);
        var newWorld = await Sync(b, game, forceDue: true);
        Assert.False(newWorld.Downed); Assert.Equal(lifecycle.Current.Generation, newWorld.Generation); Assert.Equal(0, newWorld.LifeRevision);
        await Invoke(b, "OnLocalDownedAsync", false, (byte)0); // same boolean: not a new event in this world
        Assert.Equal(0, (await Sync(b, game, forceDue: true)).LifeRevision);

        await Invoke(b, "OnLocalDownedAsync", true, (byte)1);
        var newDown = await Sync(b, game);
        Assert.True(newDown.Downed); Assert.Equal(newWorld.Generation, newDown.Generation); Assert.True(newDown.LifeRevision > oldWake.LifeRevision);
        await Invoke(b, "OnLocalDownedAsync", false, (byte)1);
        var newWake = await Sync(b, game);
        Assert.False(newWake.Downed); Assert.Equal(newWorld.Generation, newWake.Generation); Assert.True(newWake.LifeRevision > newDown.LifeRevision);
    }

    [Fact]
    public async Task Real_menu_edges_are_synchronized_independently_without_changing_the_life_revision()
    {
        var game = new Game(); await using var transport = new HttpGameTransport("http://localhost:1403", 5000, game);
        var lifecycle = Lifecycle(); await lifecycle.PendingAsync(true, true, default); var b = Bridge(transport, lifecycle);
        await Invoke(b, "OnLocalDownedAsync", true, (byte)0);
        var down = await Sync(b, game); Assert.True(down.Downed); Assert.False(down.Menu);
        Menu(b, true);
        var menu = await Sync(b, game); Assert.True(menu.Menu); Assert.True(menu.Downed);
        Assert.Equal(down.LifeRevision, menu.LifeRevision); Assert.Equal(down.Generation, menu.Generation);
        Menu(b, false);
        var closed = await Sync(b, game); Assert.False(closed.Menu); Assert.True(closed.Downed);
        Assert.Equal(down.LifeRevision, closed.LifeRevision); Assert.True(closed.Serial > menu.Serial);
    }
}
