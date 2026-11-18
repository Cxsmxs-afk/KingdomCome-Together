// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

[Collection("T3 connection lifecycle")]
public sealed class Lane1BowHandTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private const byte Peer = 1;
    private static readonly Guid Bow = Guid.Parse("10000000-0000-0000-0000-000000000009");
    private static readonly Guid Crossbow = Guid.Parse("10000000-0000-0000-0000-000000000010");
    private static readonly Guid Sword = Guid.Parse("10000000-0000-0000-0000-000000000001");

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task A_drawn_bow_with_a_fresh_native_class_sends_that_guid_on_the_wo214_channel(byte phase)
    {
        await using var link = new MemoryStream();
        var bridge = await CreateHostAsync(link);
        Observe(bridge, phase, RangedWeaponKind.Bow, Bow);

        SendOwnHand(bridge, "1 -");

        Assert.Equal($"v1|1|{Bow:D}", ReadHandPacket(link));
    }

    [Fact]
    public async Task A_classified_drawn_bow_with_a_zero_guid_is_counted_and_never_sent_as_empty()
    {
        await using var link = new MemoryStream();
        var bridge = await CreateHostAsync(link);
        Observe(bridge, 2, RangedWeaponKind.Bow, Guid.Empty);

        SendOwnHand(bridge, "1 -");

        Assert.Equal(0L, link.Length);
        var counters = (ConcurrentDictionary<string, long>)Field("_t3Counters").GetValue(bridge)!;
        Assert.Equal(1L, counters["bow_hand_empty_while_drawn"]);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("crossbow")]
    [InlineData("stale")]
    [InlineData("mode-changed")]
    [InlineData("sheathed")]
    [InlineData("sword")]
    public async Task An_empty_one_slot_report_stays_empty_without_current_bow_proof(string scenario)
    {
        await using var link = new MemoryStream();
        var bridge = await CreateHostAsync(link);
        switch (scenario)
        {
            case "unknown":
                Observe(bridge, 2, RangedWeaponKind.Unknown, Guid.Empty);
                break;
            case "crossbow":
                Observe(bridge, 2, RangedWeaponKind.Crossbow, Crossbow);
                break;
            case "stale":
                Observe(bridge, 2, RangedWeaponKind.Bow, Bow, Protocol.RangedCaptureMaxAgeMs + 1);
                break;
            case "mode-changed":
                Observe(bridge, 2, RangedWeaponKind.Bow, Bow);
                Call(bridge, "P6VisualModeChanged");
                break;
            case "sheathed":
                Observe(bridge, 2, RangedWeaponKind.Bow, Bow);
                SendOwnHand(bridge, "0 -");
                Assert.Equal("v1|0|-", ReadHandPacket(link));
                link.SetLength(0);
                break;
            case "sword":
                Observe(bridge, 2, RangedWeaponKind.Bow, Bow);
                SendOwnHand(bridge, $"1 {Sword:D}");
                Assert.Equal($"v1|1|{Sword:D}", ReadHandPacket(link));
                link.SetLength(0);
                break;
        }

        SendOwnHand(bridge, "1 -");

        Assert.Equal("v1|1|-", ReadHandPacket(link));
    }

    [Fact]
    public async Task The_figure_keeps_the_measured_bow_when_the_next_drawn_row_has_no_class()
    {
        await using var link = new MemoryStream();
        using var game = new GameCalls();
        await using var transport = new HttpGameTransport("http://localhost:1403", 1000, game);
        var bridge = await CreateHostAsync(link);
        Field("_transport").SetValue(bridge, transport);

        await ReceiveRangedBowAsync(bridge, Environment.TickCount64);
        await ReceiveRangedBowAsync(bridge, Environment.TickCount64);
        await ReceiveFigureHandAsync(bridge, "v1|1|-");

        var outfit = new HashSet<Guid> { Sword };
        var kept = (HashSet<Guid>)Call(bridge, "Wo214KeepHeld", Peer, outfit)!;
        Assert.Contains(Bow, kept);
        Assert.DoesNotContain(Bow, outfit);
        string command = Assert.Single(game.Requests);
        Assert.Contains($"KCD2MP_W214FigureHand(\"{Peer}\", true, \"{Bow:D}\"", command);

        await ReceiveFigureHandAsync(bridge, "v1|0|-");
        Assert.DoesNotContain(Bow, (HashSet<Guid>)Call(bridge, "Wo214KeepHeld", Peer, outfit)!);
    }

    [Fact]
    public async Task A_late_ranged_row_cannot_restore_a_bow_after_a_newer_sword_report()
    {
        await using var link = new MemoryStream();
        using var game = new GameCalls();
        await using var transport = new HttpGameTransport("http://localhost:1403", 1000, game);
        var bridge = await CreateHostAsync(link);
        Field("_transport").SetValue(bridge, transport);

        long capturedAt = Environment.TickCount64 - 20;
        await ReceiveFigureHandAsync(bridge, $"v1|1|{Sword:D}");
        await ReceiveRangedBowAsync(bridge, capturedAt);

        var kept = (HashSet<Guid>)Call(bridge, "Wo214KeepHeld", Peer, new HashSet<Guid>())!;
        Assert.Equal(Sword, Assert.Single(kept));
        Assert.DoesNotContain(game.Requests, command => command.Contains(Bow.ToString("D"), StringComparison.Ordinal));
    }

    private static async Task<GameBridge> CreateHostAsync(Stream link)
    {
        byte phase = 0;
        ulong generation = 1;
        bool enabled = false;
        var lifecycle = new HostAuthorityLifecycle((operation, requestedGeneration, _) =>
        {
            if (operation != HostAuthorityLifecycle.Query)
            {
                phase = operation;
                generation = requestedGeneration;
                if (operation != HostAuthorityLifecycle.Gameplay) enabled = operation == HostAuthorityLifecycle.Load;
            }
            return Task.FromResult<HostAuthorityLifecycle.Ack?>(new(true, phase, enabled, false, generation, 0));
        });
        await lifecycle.PendingAsync(false, true, default);
        await lifecycle.GameplayAsync(lifecycle.Current.Generation, default);

        var bridge = new GameBridge(new ClientConfig());
        var where = Field("_where");
        where.SetValue(bridge, Enum.Parse(where.FieldType, "World"));
        Field("_wo122Stream").SetValue(bridge, link);
        Field("_myGhostId").SetValue(bridge, (byte)0);
        Field("_combatRoleApplied").SetValue(bridge, true);
        Field("_isDamageAuthority").SetValue(bridge, true);
        Field("_sharedWorld").SetValue(bridge, true);
        Field("_p1Lifecycle").SetValue(bridge, lifecycle);
        ((Wo206Epochs)Field("_w206").GetValue(bridge)!).LearnWorld(7, 1);
        ((PeerSet)Field("_peers").GetValue(bridge)!).Connected(Peer, "peer");
        Call(bridge, "P5PeerAtRead", (int)Protocol.Name, new byte[] { Peer, 120 });
        Call(bridge, "P6BeginReceive", link, CancellationToken.None);
        Assert.NotNull(Call(bridge, "P6GetContext"));
        return bridge;
    }

    private static void Observe(GameBridge bridge, byte phase, RangedWeaponKind kind, Guid itemClass, int ageMs = 0)
    {
        ulong capturedAt = unchecked((ulong)(Environment.TickCount64 - ageMs));
        var capture = new RangedCapture(phase, 1, 3, 1, capturedAt, itemClass, Guid.Empty);
        Call(bridge, "Wo214ObserveOwnRangedHand", Call(bridge, "P6GetContext"), 0L, capture, kind, itemClass);
    }

    private static void SendOwnHand(GameBridge bridge, string report) => Call(bridge, "Wo214OnEvent", "w214_hand", report);

    private static Task ReceiveRangedBowAsync(GameBridge bridge, long capturedAt)
    {
        var serial = (long)Call(bridge, "P6PeerAtRead", (int)Protocol.W164Down, new byte[] { Peer })!;
        Assert.NotEqual(0, serial);
        return (Task)Call(bridge, "Wo214OnRangedBowHandAsync", Peer, Bow, Call(bridge, "P6GetContext"),
            0L, serial, capturedAt, (Func<bool>)(() => true), CancellationToken.None)!;
    }

    private static Task ReceiveFigureHandAsync(GameBridge bridge, string report)
    {
        var frame = new LootMsg(Protocol.W203FigureHand, 1, report).BuildUp(Protocol.W203Up, 0);
        return (Task)Call(bridge, "Wo203OnSideFrameAsync", Peer, frame[(3 + Protocol.JoinHeaderLen)..])!;
    }

    private static string ReadHandPacket(MemoryStream link)
    {
        byte[] packet = link.ToArray();
        Assert.True(packet.Length >= 3 + Protocol.JoinHeaderLen);
        Assert.Equal(Protocol.W203Up, packet[0]);
        Assert.Equal(packet.Length - 3, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(1)));
        Assert.Equal(Peer, packet[3]);
        Assert.True(LootMsg.TryDecode(packet.AsSpan(3 + Protocol.JoinHeaderLen), out var message));
        Assert.Equal(Protocol.W203FigureHand, message.Kind);
        return message.Text;
    }

    private static FieldInfo Field(string name) => typeof(GameBridge).GetField(name, Private)!;

    private static object? Call(GameBridge bridge, string method, params object?[] arguments) =>
        typeof(GameBridge).GetMethod(method, Private)!.Invoke(bridge, arguments);

    private sealed class GameCalls : HttpMessageHandler
    {
        public ConcurrentQueue<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Enqueue(Uri.UnescapeDataString(request.RequestUri!.ToString()));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        }
    }
}
