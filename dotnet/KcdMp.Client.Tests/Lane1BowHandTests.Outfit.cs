// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;

namespace KcdMp.Client.Tests;

public sealed partial class Lane1BowHandTests
{
    private static readonly Guid Armor = Guid.Parse("10000000-0000-0000-0000-000000000002");

    [Theory]
    [InlineData("direct")]
    [InlineData("atomic")]
    [InlineData("preset")]
    public async Task Appearance_converge_keeps_the_bow_drawn_during_its_equipment_read(string path)
    {
        await using var link = new MemoryStream();
        using var game = new AppearanceCalls(preset: path == "preset");
        await using var transport = new HttpGameTransport("http://localhost:1403", 5000, game);
        var bridge = await CreateHostAsync(link);
        ConfigureAppearance(bridge, transport, atomic: path == "atomic");
        await using var native = await HandPipe.StartAsync(bridge);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        var converge = ApplyAppearance(bridge, path == "atomic" ? [Armor] : [], stop.Token);
        await game.ReadStarted.Task.WaitAsync(stop.Token);
        try
        {
            await ReceiveFigureHandAsync(bridge, $"v1|1|{Bow:D}");
            await native.ExpectAsync(Bow);
        }
        finally { game.ReleaseRead.TrySetResult(); }
        await converge.WaitAsync(stop.Token);
        Assert.Empty(game.Mutations);

        await ReceiveFigureHandAsync(bridge, "v1|0|-");
        await ObserveHolster(bridge, native, await native.ExpectAsync(null), 3, stop.Token);
        string method = path switch { "atomic" => "KCD2MP_W202EquipAll", "preset" => "KCD2MP_W136ClearPreset", _ => "KCD2MP_W144Unequip" };
        string command = await game.ForMethod(method).Task.WaitAsync(stop.Token);
        Assert.Contains("\"000000DE\",\"0000000000000002\",\"012345\"", command);
        await AwaitAppearanceIdle(bridge, stop.Token);
    }

    [Fact]
    public async Task Appearance_converge_cannot_unequip_from_a_replacement_body_with_the_same_entity_id()
    {
        await using var link = new MemoryStream();
        using var game = new AppearanceCalls();
        await using var transport = new HttpGameTransport("http://localhost:1403", 5000, game);
        var bridge = await CreateHostAsync(link);
        ConfigureAppearance(bridge, transport, atomic: false);
        await using var native = await HandPipe.StartAsync(bridge);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        int generation = (int)Call(bridge, "GhostGeneration", Peer.ToString())!;
        var converge = ApplyAppearance(bridge, [], stop.Token);
        await game.ReadStarted.Task.WaitAsync(stop.Token);
        try { Call(bridge, "OnGameEvent", "ghostid", $"{Peer} 000000DE 012346"); }
        finally { game.ReleaseRead.TrySetResult(); }
        await converge.WaitAsync(stop.Token);
        Assert.Empty(game.Mutations);
        Assert.Equal(generation + 1, (int)Call(bridge, "GhostGeneration", Peer.ToString())!);
    }

    [Fact]
    public async Task Appearance_converge_catches_up_only_the_newest_outfit_after_a_late_equip_dispatch_and_holster()
    {
        await using var link = new MemoryStream();
        using var game = new AppearanceCalls(startWithBow: false, holdFirstEquip: true);
        await using var transport = new HttpGameTransport("http://localhost:1403", 5000, game);
        var bridge = await CreateHostAsync(link);
        ConfigureAppearance(bridge, transport, atomic: false);
        await using var native = await HandPipe.StartAsync(bridge);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        game.ReleaseRead.TrySetResult();

        var converge = ApplyAppearance(bridge, [Sword], stop.Token);
        await game.BlockedEquipStarted.Task.WaitAsync(stop.Token);
        try
        {
            await ReceiveFigureHandAsync(bridge, $"v1|1|{Bow:D}");
            await native.ExpectAsync(Bow);
            await ApplyAppearance(bridge, [Crossbow], stop.Token);
        }
        finally { game.ReleaseEquip.TrySetResult(); }
        await converge.WaitAsync(stop.Token);
        string stale = Assert.Single(game.Mutations);
        Assert.Contains(Sword.ToString("D"), stale);
        Assert.Contains("\"0000000000000000\"", stale);

        await ReceiveFigureHandAsync(bridge, "v1|0|-");
        await ObserveHolster(bridge, native, await native.ExpectAsync(null), 3, stop.Token);
        string fresh = await game.ForMethod("KCD2MP_W144Equip").Task.WaitAsync(stop.Token);
        Assert.Contains(Crossbow.ToString("D"), fresh);
        Assert.Contains("\"000000DE\",\"0000000000000002\",\"012345\"", fresh);
        Assert.DoesNotContain(Sword.ToString("D"), fresh);
        await AwaitAppearanceIdle(bridge, stop.Token);
    }

    [Fact]
    public async Task Appearance_converge_defers_unproven_atomic_slot_replacement_until_holster()
    {
        await using var link = new MemoryStream();
        using var game = new AppearanceCalls();
        await using var transport = new HttpGameTransport("http://localhost:1403", 5000, game);
        var bridge = await CreateHostAsync(link);
        ConfigureAppearance(bridge, transport, atomic: true);
        await using var native = await HandPipe.StartAsync(bridge);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await ReceiveFigureHandAsync(bridge, $"v1|1|{Bow:D}");
        await native.ExpectAsync(Bow);
        game.ReleaseRead.TrySetResult();

        await ApplyAppearance(bridge, [Armor, Sword], stop.Token);
        Assert.Empty(game.Mutations);

        await ReceiveFigureHandAsync(bridge, "v1|0|-");
        await ObserveHolster(bridge, native, await native.ExpectAsync(null), 3, stop.Token);
        string command = await game.ForMethod("KCD2MP_W202EquipAll").Task.WaitAsync(stop.Token);
        Assert.Contains(Armor.ToString("D"), command);
        Assert.Contains(Sword.ToString("D"), command);
        await AwaitAppearanceIdle(bridge, stop.Token);
    }

    [Fact]
    public async Task Actual_empty_hands_do_not_override_a_pending_native_hand_confirmation()
    {
        await using var link = new MemoryStream();
        using var game = new AppearanceCalls(preset: true);
        await using var transport = new HttpGameTransport("http://localhost:1403", 5000, game);
        var bridge = await CreateHostAsync(link);
        ConfigureAppearance(bridge, transport, atomic: false);
        await using var native = await HandPipe.StartAsync(bridge);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await ReceiveFigureHandAsync(bridge, "v1|0|-");
        var declared = await native.ExpectAsync(null);
        game.ReleaseRead.TrySetResult();

        await ObserveHolster(bridge, native, declared, 1, stop.Token);
        await ApplyAppearance(bridge, [], stop.Token);
        Assert.Empty(game.Mutations);

        await ObserveHolster(bridge, native, declared, 3, stop.Token);
        string done = await game.ForMethod("KCD2MP_W136ClearPreset").Task.WaitAsync(stop.Token);
        Assert.Contains("\"000000DE\",\"0000000000000001\",\"012345\"", done);
        await AwaitAppearanceIdle(bridge, stop.Token);
    }

    private static Task ApplyAppearance(GameBridge bridge, Guid[] target, CancellationToken ct) =>
        (Task)Call(bridge, "ApplyAppearanceAsync", Peer, target, ct)!;

    private static async Task AwaitAppearanceIdle(GameBridge bridge, CancellationToken ct)
    {
        var gates = (ConcurrentDictionary<byte, SemaphoreSlim>)Field("_ghostApplyGate").GetValue(bridge)!;
        await gates[Peer].WaitAsync(ct);
        gates[Peer].Release();
    }

    private static void ConfigureAppearance(GameBridge bridge, HttpGameTransport transport, bool atomic)
    {
        transport.BatchingEnabled = false;
        Field("_transport").SetValue(bridge, transport);
        ((ConcurrentDictionary<string, bool>)Field("_w202On").GetValue(bridge)!)["outfit_atomic"] = atomic;
        Field("_soulIndex").SetValue(bridge, Task.FromResult<SoulIndex?>(SoulIndex.FromRows([], [
            (Armor, "test_armour", "Armor"), (Sword, "test_sword", "Weapon"), (Crossbow, "test_crossbow", "Weapon")
        ])));
    }

    private static async Task ObserveHolster(GameBridge bridge, HandPipe native, byte[] declared, byte known, CancellationToken ct)
    {
        native.ObservedKnown = known;
        uint world = BinaryPrimitives.ReadUInt32LittleEndian(declared.AsSpan(48));
        long session = BinaryPrimitives.ReadInt64LittleEndian(declared.AsSpan(52));
        long peerSerial = BinaryPrimitives.ReadInt64LittleEndian(declared.AsSpan(7));
        int generation = BinaryPrimitives.ReadInt32LittleEndian(declared.AsSpan(15));
        uint birth = BinaryPrimitives.ReadUInt32LittleEndian(declared.AsSpan(19));
        long revision = BinaryPrimitives.ReadInt64LittleEndian(declared.AsSpan(23));
        string scope = FormattableString.Invariant($"{world:X8}{session:X16}{peerSerial:X16}{generation:X8}{revision:X16}");
        Call(bridge, "OnGameEvent", "w214_figure_hand", $"{Peer} ok empty {birth:X6} {scope}");
        var hands = Field("_w214EngineHands").GetValue(bridge)!;
        var lookup = hands.GetType().GetMethod("TryGetValue")!;
        while (true)
        {
            object?[] args = [Peer, null];
            if ((bool)lookup.Invoke(hands, args)! &&
                args[1]!.GetType().GetProperty("Actual")!.GetValue(args[1]) is Wo214NativeHands actual && actual.Known == known)
                return;
            await Task.Delay(5, ct);
        }
    }

    private sealed class AppearanceCalls(bool preset = false, bool startWithBow = true, bool holdFirstEquip = false) : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<Guid, byte> _equipped = new(startWithBow ? [KeyValuePair.Create(Bow, (byte)1)] : []);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _methods = new();
        private int _reads;
        public ConcurrentQueue<string> Mutations { get; } = new();
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BlockedEquipStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseEquip { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> ForMethod(string method) => _methods.GetOrAdd(method,
            static _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string url = Uri.UnescapeDataString(request.RequestUri!.ToString());
            string response = "ok";
            if (url.Contains("/EquipmentManager/Equipped", StringComparison.Ordinal))
            {
                bool weaponRead = url.Contains("EquippedWeaponsByClassId", StringComparison.Ordinal);
                if (weaponRead && Interlocked.Increment(ref _reads) == 1)
                {
                    ReadStarted.TrySetResult();
                    await ReleaseRead.Task.WaitAsync(ct);
                }
                var items = _equipped.Keys.Where(item => weaponRead != (item == Armor))
                    .Select(item => $"<Item ItemClass=\"{item:D}\"/>");
                response = "<Items>" + string.Concat(items) + "</Items>";
            }
            else if (new[] { "KCD2MP_W144Equip", "KCD2MP_W144Unequip", "KCD2MP_W136ClearPreset", "KCD2MP_W202EquipAll" }
                .FirstOrDefault(name => url.Contains(name + "(", StringComparison.Ordinal)) is { } method)
            {
                Mutations.Enqueue(url);
                if (holdFirstEquip && method == "KCD2MP_W144Equip" && BlockedEquipStarted.TrySetResult())
                    await ReleaseEquip.Task.WaitAsync(ct);   // its stale Lua stamp is checked separately by H4OutfitEngineTests
                else
                {
                    if (method == "KCD2MP_W136ClearPreset") _equipped.Clear();
                    else
                    {
                        foreach (var item in new[] { Bow, Armor, Sword, Crossbow }.Where(item => url.Contains(item.ToString("D"), StringComparison.Ordinal)))
                        {
                            if (method != "KCD2MP_W144Unequip") _equipped.TryAdd(item, 1);
                            else if (!preset) _equipped.TryRemove(item, out _);
                        }
                    }
                    ForMethod(method).TrySetResult(url);
                }
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) };
        }
    }
}
