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

    [Fact]
    public async Task A_drawn_bow_heartbeat_keeps_its_class_after_the_native_capture_window()
    {
        await using var link = new MemoryStream();
        var bridge = await CreateHostAsync(link);
        Observe(bridge, 2, RangedWeaponKind.Bow, Bow);
        SendOwnHand(bridge, "1 -");
        Assert.Equal($"v1|1|{Bow:D}", ReadHandPacket(link));

        long observedBy = Environment.TickCount64;
        await Task.Delay(Protocol.RangedCaptureMaxAgeMs + 1);
        while (Environment.TickCount64 - observedBy <= Protocol.RangedCaptureMaxAgeMs)
            await Task.Delay(1);

        link.SetLength(0);
        SendOwnHand(bridge, "1 -");
        Assert.Equal($"v1|1|{Bow:D}", ReadHandPacket(link));
    }

    [Fact]
    public async Task The_agent_repeats_drawn_and_sheathed_states_without_another_lua_event()
    {
        await using var link = new MemoryStream();
        var bridge = await CreateHostAsync(link);
        TickHandHeartbeat(bridge);
        Assert.Equal(0L, link.Length);

        Observe(bridge, 2, RangedWeaponKind.Bow, Bow);
        SendOwnHand(bridge, "1 -");
        Assert.Equal($"v1|1|{Bow:D}", ReadHandPacket(link));
        link.SetLength(0);

        TickHandHeartbeat(bridge, elapsedMs: 0);
        Assert.Equal(0L, link.Length);

        TickHandHeartbeat(bridge);
        Assert.Equal($"v1|1|{Bow:D}", ReadHandPacket(link));
        link.SetLength(0);

        SendOwnHand(bridge, "0 -");
        Assert.Equal("v1|0|-", ReadHandPacket(link));
        link.SetLength(0);

        TickHandHeartbeat(bridge);
        Assert.Equal("v1|0|-", ReadHandPacket(link));
        link.SetLength(0);

        TickHandHeartbeat(bridge);
        Assert.Equal("v1|0|-", ReadHandPacket(link));
        link.SetLength(0);

        DisconnectPeer(bridge);
        TickHandHeartbeat(bridge);
        Assert.Equal(0L, link.Length);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_measured_draw_is_never_repeated_after_its_mode_or_session_ends(bool modeChanged)
    {
        await using var link = new MemoryStream();
        var bridge = await CreateHostAsync(link);
        Observe(bridge, 2, RangedWeaponKind.Bow, Bow);
        SendOwnHand(bridge, "1 -");
        Assert.Equal($"v1|1|{Bow:D}", ReadHandPacket(link));
        link.SetLength(0);

        var context = Call(bridge, "P6GetContext");
        if (modeChanged) Call(bridge, "P6VisualModeChanged");
        else Call(bridge, "P6EndReceive", link, CancellationToken.None);
        RunHandHeartbeat(bridge, context, Protocol.BodyState2HeartbeatMs);

        Assert.Equal(0L, link.Length);
    }

    [Fact]
    public async Task The_same_bow_report_and_an_unknown_capture_keep_the_class_until_a_typed_crossbow_arrives()
    {
        await using var link = new MemoryStream();
        var bridge = await CreateHostAsync(link);
        Observe(bridge, 2, RangedWeaponKind.Bow, Bow);

        SendOwnHand(bridge, $"1 {Bow:D}");
        Assert.Equal($"v1|1|{Bow:D}", ReadHandPacket(link));
        link.SetLength(0);

        long reportedBy = Environment.TickCount64;
        while (Environment.TickCount64 <= reportedBy)
            await Task.Delay(1);
        Observe(bridge, 2, RangedWeaponKind.Unknown, Guid.Empty);
        SendOwnHand(bridge, "1 -");
        Assert.Equal($"v1|1|{Bow:D}", ReadHandPacket(link));
        link.SetLength(0);

        SendOwnHand(bridge, $"1 {Bow:D}");
        Assert.Equal($"v1|1|{Bow:D}", ReadHandPacket(link));
        link.SetLength(0);

        Observe(bridge, 2, RangedWeaponKind.Crossbow, Crossbow);
        TickHandHeartbeat(bridge);
        if (link.Length != 0)
        {
            Assert.Equal($"v1|1|{Crossbow:D}", ReadHandPacket(link));
            link.SetLength(0);
        }
        SendOwnHand(bridge, "1 -");
        Assert.Equal("v1|1|-", ReadHandPacket(link));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("crossbow")]
    [InlineData("initially-stale")]
    [InlineData("initially-stale-empty")]
    [InlineData("mode-changed")]
    [InlineData("session-reset")]
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
            case "initially-stale":
                Observe(bridge, 2, RangedWeaponKind.Bow, Bow, Protocol.RangedCaptureMaxAgeMs + 1);
                break;
            case "initially-stale-empty":
                Observe(bridge, 2, RangedWeaponKind.Bow, Guid.Empty, Protocol.RangedCaptureMaxAgeMs + 1);
                break;
            case "mode-changed":
                Observe(bridge, 2, RangedWeaponKind.Bow, Bow);
                Call(bridge, "P6VisualModeChanged");
                break;
            case "session-reset":
                Observe(bridge, 2, RangedWeaponKind.Bow, Bow);
                Call(bridge, "Wo214HandsReset");
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
    public async Task A_drawn_empty_report_keeps_a_confirmed_bow_beyond_the_generic_hand_timeout()
    {
        await using var link = new MemoryStream();
        using var game = new GameCalls();
        await using var transport = new HttpGameTransport("http://localhost:1403", 1000, game);
        var bridge = await CreateHostAsync(link);
        Field("_transport").SetValue(bridge, transport);
        await ReceiveRangedBowAsync(bridge, Environment.TickCount64);

        // Advance the receipt timestamp past the ordinary 25-second WO-214 deadline.
        AgeFigureHand(bridge);

        long receivedAt = Environment.TickCount64;
        await ReceiveFigureHandAsync(bridge, "v1|1|-");

        var kept = (HashSet<Guid>)Call(bridge, "Wo214KeepHeld", Peer, new HashSet<Guid>())!;
        Assert.Equal(Bow, Assert.Single(kept));
        var hand = FigureHand(bridge);
        Assert.True((long)hand.GetType().GetProperty("AtMs")!.GetValue(hand)! >= receivedAt);
        Assert.Single(game.Requests);

        Call(bridge, "P6VisualModeChanged");
        Assert.Empty((HashSet<Guid>)Call(bridge, "Wo214KeepHeld", Peer, new HashSet<Guid>())!);
    }

    [Theory]
    [InlineData("peer-lost")]
    [InlineData("session-ended")]
    [InlineData("sheathed")]
    public async Task A_silent_but_connected_figure_keeps_the_bow_until_a_new_state_or_scope_ends(string next)
    {
        await using var link = new MemoryStream();
        using var game = new GameCalls();
        await using var transport = new HttpGameTransport("http://localhost:1403", 1000, game);
        var bridge = await CreateHostAsync(link);
        Field("_transport").SetValue(bridge, transport);
        await ReceiveRangedBowAsync(bridge, Environment.TickCount64);
        AgeFigureHand(bridge);

        var kept = (HashSet<Guid>)Call(bridge, "Wo214KeepHeld", Peer, new HashSet<Guid>())!;
        Assert.Equal(Bow, Assert.Single(kept));

        switch (next)
        {
            case "peer-lost":
                DisconnectPeer(bridge);
                break;
            case "session-ended":
                Call(bridge, "P6EndReceive", link, CancellationToken.None);
                break;
            case "sheathed":
                await ReceiveFigureHandAsync(bridge, "v1|0|-");
                await ReceiveFigureHandAsync(bridge, "v1|0|-");
                string command = Assert.Single(game.Requests.Where(request => request.Contains($"KCD2MP_W214FigureHand(\"{Peer}\", false, \"-\"", StringComparison.Ordinal)));
                Assert.Contains($"KCD2MP_GhostCombat(\"{Peer}\",{Protocol.CombatEventWeaponSheathed})", command.Replace(" ", ""));
                break;
        }

        Assert.Empty((HashSet<Guid>)Call(bridge, "Wo214KeepHeld", Peer, new HashSet<Guid>())!);
    }

    [Fact]
    public async Task A_class_report_on_the_wo214_channel_has_no_age_timeout_while_the_peer_is_connected()
    {
        await using var link = new MemoryStream();
        using var game = new GameCalls();
        await using var transport = new HttpGameTransport("http://localhost:1403", 1000, game);
        var bridge = await CreateHostAsync(link);
        Field("_transport").SetValue(bridge, transport);
        await ReceiveFigureHandAsync(bridge, $"v1|1|{Bow:D}");
        AgeFigureHand(bridge);

        var kept = (HashSet<Guid>)Call(bridge, "Wo214KeepHeld", Peer, new HashSet<Guid>())!;
        Assert.Equal(Bow, Assert.Single(kept));

        DisconnectPeer(bridge);
        Assert.Empty((HashSet<Guid>)Call(bridge, "Wo214KeepHeld", Peer, new HashSet<Guid>())!);
    }

    [Fact]
    public async Task Pre_draw_re_equips_the_latest_ranged_bow_instead_of_the_previous_sword()
    {
        await using var link = new MemoryStream();
        using var game = new GameCalls();
        await using var transport = new HttpGameTransport("http://localhost:1403", 1000, game);
        var bridge = await CreateHostAsync(link);
        Field("_transport").SetValue(bridge, transport);
        await ReceiveFigureHandAsync(bridge, $"v1|1|{Sword:D}");
        await ReceiveRangedBowAsync(bridge, Environment.TickCount64);
        await ReceiveFigureHandAsync(bridge, "v1|0|-");
        game.Requests.Clear();

        await (Task)Call(bridge, "Wo214PreDrawAsync", Peer, CancellationToken.None)!;

        Assert.Contains(game.Requests, request => request.Contains($"kcd2mp_{Peer}/EquipmentManager/EquippedArmorsByClassId", StringComparison.Ordinal));
        Assert.Contains(game.Requests, request => request.Contains($"kcd2mp_{Peer}/EquipmentManager/EquippedWeaponsByClassId", StringComparison.Ordinal));
        string equip = Assert.Single(game.Requests.Where(request => request.Contains("KCD2MP_W144Equip(", StringComparison.Ordinal)));
        Assert.Contains($"KCD2MP_W144Equip(\"kcd2mp_{Peer}\",\"{Bow:D}\")", equip);
        Assert.DoesNotContain(Sword.ToString("D"), equip);
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

    [Fact]
    public async Task An_older_queued_draw_cannot_apply_after_the_reader_receives_sheathe()
    {
        await using var link = new MemoryStream();
        using var game = new GameCalls();
        await using var transport = new HttpGameTransport("http://localhost:1403", 1000, game);
        var bridge = await CreateHostAsync(link);
        Field("_transport").SetValue(bridge, transport);

        byte[] older = HandPayload(10, $"v1|1|{Bow:D}");
        byte[] newer = HandPayload(11, "v1|0|-");
        Call(bridge, "P6NoteVisualAtRead", (int)Protocol.W203Down, older);
        Call(bridge, "P6NoteVisualAtRead", (int)Protocol.W203Down, newer);

        await (Task)Call(bridge, "Wo203OnSideFrameAsync", Peer, older[(1 + Protocol.JoinHeaderLen)..])!;

        Assert.Empty(game.Requests);
        Assert.Empty((HashSet<Guid>)Call(bridge, "Wo214KeepHeld", Peer, new HashSet<Guid>())!);

        await (Task)Call(bridge, "Wo203OnSideFrameAsync", Peer, newer[(1 + Protocol.JoinHeaderLen)..])!;

        Assert.Contains($"KCD2MP_W214FigureHand(\"{Peer}\", false, \"-\"", Assert.Single(game.Requests));
        Assert.Empty((HashSet<Guid>)Call(bridge, "Wo214KeepHeld", Peer, new HashSet<Guid>())!);

    }

    [Fact]
    public async Task An_empty_receipt_cannot_supersede_a_queued_confirmed_class()
    {
        await using var link = new MemoryStream();
        using var game = new GameCalls();
        await using var transport = new HttpGameTransport("http://localhost:1403", 1000, game);
        var bridge = await CreateHostAsync(link);
        Field("_transport").SetValue(bridge, transport);
        byte[] confirmed = HandPayload(10, $"v1|1|{Bow:D}");
        byte[] empty = HandPayload(11, "v1|1|-");
        Call(bridge, "P6NoteVisualAtRead", (int)Protocol.W203Down, confirmed);
        Call(bridge, "P6NoteVisualAtRead", (int)Protocol.W203Down, empty);

        await (Task)Call(bridge, "Wo203OnSideFrameAsync", Peer, confirmed[(1 + Protocol.JoinHeaderLen)..])!;
        await (Task)Call(bridge, "Wo203OnSideFrameAsync", Peer, empty[(1 + Protocol.JoinHeaderLen)..])!;

        Assert.Contains(Bow.ToString("D"), Assert.Single(game.Requests));
        Assert.Equal(Bow, Assert.Single((HashSet<Guid>)Call(bridge, "Wo214KeepHeld", Peer, new HashSet<Guid>())!));
    }

    [Fact]
    public async Task A_stalled_writer_keeps_only_one_repeat_of_the_sheathed_state_per_peer()
    {
        await using var link = new MemoryStream();
        var bridge = await CreateHostAsync(link);
        var writer = new LinkWriter(new LinkScheduler(new LinkSchedOptions { Budget = false }), new StreamLinkPath(link));
        Field("_w213Writer").SetValue(bridge, writer);
        Field("_w213Stream").SetValue(bridge, link);

        SendOwnHand(bridge, "0 -");
        for (int n = 0; n < 4; n++) TickHandHeartbeat(bridge);
        Assert.Equal(2, writer.Scheduler.EventCount);

        await writer.PumpOnceAsync(CancellationToken.None);
        Assert.Equal(2, writer.Written);
        TickHandHeartbeat(bridge);
        Assert.Equal(1, writer.Scheduler.EventCount);
    }

    private static byte[] HandPayload(uint token, string value)
    {
        byte[] up = new LootMsg(Protocol.W203FigureHand, token, value).BuildUp(Protocol.W203Up, 0);
        return [Peer, .. up[3..]];
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

    private static void TickHandHeartbeat(GameBridge bridge, int elapsedMs = Protocol.BodyState2HeartbeatMs) =>
        RunHandHeartbeat(bridge, Call(bridge, "P6GetContext"), elapsedMs);

    private static void RunHandHeartbeat(GameBridge bridge, object? context, int elapsedMs)
    {
        Field("_w214OwnHandHeartbeatAtMs").SetValue(bridge, Environment.TickCount64 - elapsedMs);
        Call(bridge, "Wo214HandHeartbeat", context);
    }

    private static void DisconnectPeer(GameBridge bridge)
    {
        Call(bridge, "P5PeerAtRead", (int)Protocol.Disconnect, new byte[] { Peer });
        ((PeerSet)Field("_peers").GetValue(bridge)!).Disconnected(Peer);
    }

    private static object FigureHand(GameBridge bridge)
    {
        var hands = Field("_w214FigureHand").GetValue(bridge)!;
        return hands.GetType().GetProperty("Item")!.GetValue(hands, [(object)Peer])!;
    }

    private static void AgeFigureHand(GameBridge bridge)
    {
        var hand = FigureHand(bridge);
        hand.GetType().GetProperty("AtMs")!.SetValue(hand, Environment.TickCount64 - Wo214Rules.HandFreshMs - 1);
    }

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
            string url = Uri.UnescapeDataString(request.RequestUri!.ToString());
            Requests.Enqueue(url);
            string response = url.Contains("/EquipmentManager/Equipped", StringComparison.Ordinal) ? "<Items />" : "ok";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) });
        }
    }
}
