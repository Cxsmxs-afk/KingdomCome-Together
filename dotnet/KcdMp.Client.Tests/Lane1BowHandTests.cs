// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net;
using System.Reflection;
using System.Threading.Channels;
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
    private static readonly Guid Armor = Guid.Parse("10000000-0000-0000-0000-000000000002");

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
        await using var native = await HandPipe.StartAsync(bridge);

        await ReceiveRangedBowAsync(bridge, Environment.TickCount64);
        await native.ExpectAsync(Bow);
        await ReceiveRangedBowAsync(bridge, Environment.TickCount64);
        await ReceiveFigureHandAsync(bridge, "v1|1|-");

        var outfit = new HashSet<Guid> { Sword };
        var kept = (HashSet<Guid>)Call(bridge, "Wo214KeepHeld", Peer, outfit)!;
        Assert.Contains(Bow, kept);
        Assert.DoesNotContain(Bow, outfit);
        Assert.False(native.Declarations.TryRead(out _));
        Assert.Empty(game.Requests);

        await ReceiveFigureHandAsync(bridge, "v1|0|-");
        await native.ExpectAsync(null);
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
        await using var native = await HandPipe.StartAsync(bridge);
        await ReceiveRangedBowAsync(bridge, Environment.TickCount64);
        await native.ExpectAsync(Bow);

        // Advance the receipt timestamp past the ordinary 25-second WO-214 deadline.
        AgeFigureHand(bridge);

        long receivedAt = Environment.TickCount64;
        await ReceiveFigureHandAsync(bridge, "v1|1|-");

        var kept = (HashSet<Guid>)Call(bridge, "Wo214KeepHeld", Peer, new HashSet<Guid>())!;
        Assert.Equal(Bow, Assert.Single(kept));
        var hand = FigureHand(bridge);
        Assert.True((long)hand.GetType().GetProperty("AtMs")!.GetValue(hand)! >= receivedAt);
        Assert.False(native.Declarations.TryRead(out _));
        Assert.Empty(game.Requests);

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
        await using var native = await HandPipe.StartAsync(bridge);
        await ReceiveRangedBowAsync(bridge, Environment.TickCount64);
        await native.ExpectAsync(Bow);
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
                await native.ExpectAsync(null);
                Assert.False(native.Declarations.TryRead(out _));
                Assert.Empty(game.Requests);
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
        await using var native = await HandPipe.StartAsync(bridge);
        await ReceiveFigureHandAsync(bridge, $"v1|1|{Sword:D}");
        await native.ExpectAsync(Sword);
        await ReceiveRangedBowAsync(bridge, Environment.TickCount64);
        await native.ExpectAsync(Bow);
        await ReceiveFigureHandAsync(bridge, "v1|0|-");
        await native.ExpectAsync(null);
        game.Requests.Clear();

        await (Task)Call(bridge, "Wo214PreDrawAsync", Peer, CancellationToken.None)!;

        Assert.Contains(game.Requests, request => request.Contains($"kcd2mp_{Peer}/EquipmentManager/EquippedArmorsByClassId", StringComparison.Ordinal));
        Assert.Contains(game.Requests, request => request.Contains($"kcd2mp_{Peer}/EquipmentManager/EquippedWeaponsByClassId", StringComparison.Ordinal));
        string equip = Assert.Single(game.Requests.Where(IsOutfitMutation));
        Assert.Contains($"KCD2MP_W214OutfitApply(\"{Peer}\",\"1234AA11\",", equip);
        Assert.Contains($",\"equip\",\"{Bow:D}\")", equip);
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
        await using var native = await HandPipe.StartAsync(bridge);

        long capturedAt = Environment.TickCount64 - 20;
        await ReceiveFigureHandAsync(bridge, $"v1|1|{Sword:D}");
        await native.ExpectAsync(Sword);
        await ReceiveRangedBowAsync(bridge, capturedAt);

        var kept = (HashSet<Guid>)Call(bridge, "Wo214KeepHeld", Peer, new HashSet<Guid>())!;
        Assert.Equal(Sword, Assert.Single(kept));
        Assert.False(native.Declarations.TryRead(out _));
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
        await using var native = await HandPipe.StartAsync(bridge);

        byte[] older = HandPayload(10, $"v1|1|{Bow:D}");
        byte[] newer = HandPayload(11, "v1|0|-");
        Call(bridge, "P6NoteVisualAtRead", (int)Protocol.W203Down, older);
        Call(bridge, "P6NoteVisualAtRead", (int)Protocol.W203Down, newer);

        await (Task)Call(bridge, "Wo203OnSideFrameAsync", Peer, older[(1 + Protocol.JoinHeaderLen)..])!;

        Assert.Empty(game.Requests);
        Assert.Empty((HashSet<Guid>)Call(bridge, "Wo214KeepHeld", Peer, new HashSet<Guid>())!);

        await (Task)Call(bridge, "Wo203OnSideFrameAsync", Peer, newer[(1 + Protocol.JoinHeaderLen)..])!;

        await native.ExpectAsync(null);
        Assert.False(native.Declarations.TryRead(out _));
        Assert.Empty(game.Requests);
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
        await using var native = await HandPipe.StartAsync(bridge);
        byte[] confirmed = HandPayload(10, $"v1|1|{Bow:D}");
        byte[] empty = HandPayload(11, "v1|1|-");
        Call(bridge, "P6NoteVisualAtRead", (int)Protocol.W203Down, confirmed);
        Call(bridge, "P6NoteVisualAtRead", (int)Protocol.W203Down, empty);

        await (Task)Call(bridge, "Wo203OnSideFrameAsync", Peer, confirmed[(1 + Protocol.JoinHeaderLen)..])!;
        await (Task)Call(bridge, "Wo203OnSideFrameAsync", Peer, empty[(1 + Protocol.JoinHeaderLen)..])!;

        await native.ExpectAsync(Bow);
        Assert.False(native.Declarations.TryRead(out _));
        Assert.Empty(game.Requests);
        Assert.Equal(Bow, Assert.Single((HashSet<Guid>)Call(bridge, "Wo214KeepHeld", Peer, new HashSet<Guid>())!));
    }

    [Fact]
    public async Task Reusing_an_entity_id_for_a_new_body_retires_its_old_hand_revision()
    {
        await using var link = new MemoryStream();
        using var game = new GameCalls();
        await using var transport = new HttpGameTransport("http://localhost:1403", 1000, game);
        var bridge = await CreateHostAsync(link);
        Field("_transport").SetValue(bridge, transport);
        await using var native = await HandPipe.StartAsync(bridge);
        string ghost = Peer.ToString();
        string[] GearCalls() => game.Requests.Where(request => request.Contains("KCD2MP_W214FigureHand(", StringComparison.Ordinal) ||
            request.Contains("KCD2MP_W144Equip(", StringComparison.Ordinal)).ToArray();
        Call(bridge, "OnGameEvent", "ghostid", $"{ghost} DE");
        Assert.Equal(222u, ((ConcurrentDictionary<string, uint>)Field("_ghostEntityIds").GetValue(bridge)!)[ghost]);
        Call(bridge, "OnGameEvent", "ghostid", $"{ghost} DE 012345");
        await ReceiveFigureHandAsync(bridge, $"v1|1|{Bow:D}");
        await native.ExpectAsync(Bow);
        Assert.Equal(Bow, Assert.Single((HashSet<Guid>)Call(bridge, "Wo214KeepHeld", Peer, new HashSet<Guid>())!));
        Assert.Contains("\"000000DE\",\"0000000000000001\",\"012345\"", Assert.Single(GearCalls()));

        Call(bridge, "OnGameEvent", "ghostid", $"{ghost} DE 012346");

        Assert.Empty((HashSet<Guid>)Call(bridge, "Wo214KeepHeld", Peer, new HashSet<Guid>())!);
        await (Task)Call(bridge, "Wo214PreDrawAsync", Peer, CancellationToken.None)!;
        Assert.False(native.Declarations.TryRead(out _));
        Assert.Empty(game.Requests);
    }

    [Fact]
    public async Task A_holster_read_for_the_previous_body_is_never_applied_to_the_new_body()
    {
        await using var link = new MemoryStream();
        using var game = new GameCalls();
        await using var transport = new HttpGameTransport("http://localhost:1403", 1000, game);
        var bridge = await CreateHostAsync(link);
        Field("_transport").SetValue(bridge, transport);
        await using var native = await HandPipe.StartAsync(bridge);
        string ghost = Peer.ToString();
        ((ConcurrentDictionary<string, uint>)Field("_ghostEntityIds").GetValue(bridge)!)[ghost] = 222;
        byte[] holster = HandPayload(10, "v1|0|-");
        Call(bridge, "P6NoteVisualAtRead", (int)Protocol.W203Down, holster);
        Call(bridge, "OnGameEvent", "ghostid", $"{Peer} 000000DE 1234AA12");

        await (Task)Call(bridge, "Wo203OnSideFrameAsync", Peer, holster[(1 + Protocol.JoinHeaderLen)..])!;

        Assert.Empty(game.Requests);
        byte[] replacement = HandPayload(11, $"v1|1|{Bow:D}");
        Call(bridge, "P6NoteVisualAtRead", (int)Protocol.W203Down, replacement);
        await (Task)Call(bridge, "Wo203OnSideFrameAsync", Peer, replacement[(1 + Protocol.JoinHeaderLen)..])!;
        await native.ExpectAsync(Bow, 0x1234AA12);
        Assert.False(native.Declarations.TryRead(out _));
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

    [Fact]
    public async Task An_empty_local_report_keeps_repeating_a_confirmed_ordinary_weapon()
    {
        await using var link = new MemoryStream();
        var bridge = await CreateHostAsync(link);
        string sword = $"v1|1|{Sword:D}";
        SendOwnHand(bridge, $"1 {Sword:D}");
        Assert.Equal(sword, ReadHandPacket(link));

        link.SetLength(0);
        SendOwnHand(bridge, "1 -");
        Assert.Equal("v1|1|-", ReadHandPacket(link));

        link.SetLength(0);
        TickHandHeartbeat(bridge);
        Assert.Equal(sword, ReadHandPacket(link));
    }

    [Fact]
    public async Task Only_a_current_crossbow_observation_releases_a_different_confirmed_class()
    {
        await using var link = new MemoryStream();
        using var game = new GameCalls();
        await using var transport = new HttpGameTransport("http://localhost:1403", 1000, game);
        var bridge = await CreateHostAsync(link);
        Field("_transport").SetValue(bridge, transport);

        long beforeSword = Environment.TickCount64 - 10;
        await ReceiveFigureHandAsync(bridge, $"v1|1|{Sword:D}");
        var body = Call(bridge, "Wo214FigureBody", Peer);
        Call(bridge, "Wo214OnOtherRangedHand", Peer, Crossbow, body, beforeSword);
        Assert.Equal(Sword, Assert.Single((HashSet<Guid>)Call(bridge, "Wo214KeepHeld", Peer, new HashSet<Guid>())!));

        Call(bridge, "Wo214OnOtherRangedHand", Peer, Crossbow, body, Environment.TickCount64);
        Assert.Empty((HashSet<Guid>)Call(bridge, "Wo214KeepHeld", Peer, new HashSet<Guid>())!);
    }

    private sealed class HandPipe : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly NamedPipeServerStream _server;
        private readonly CombatPipe _client;
        private readonly Channel<byte[]> _requests = Channel.CreateUnbounded<byte[]>();
        private Task? _pump;

        private HandPipe()
        {
            string name = "kct-h4-hand-" + Guid.NewGuid().ToString("N");
            _server = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            _client = new(name) { BatchOn = false };
        }

        public ChannelReader<byte[]> Declarations => _requests.Reader;

        public static async Task<HandPipe> StartAsync(GameBridge bridge)
        {
            var native = new HandPipe();
            try
            {
                Field("_ghostIsolate").SetValue(bridge, false);
                Call(bridge, "OnGameEvent", "ghostid", $"{Peer} 000000DE 1234AA11");
                Field("_combat").SetValue(bridge, native._client);
                Task accepted = native._server.WaitForConnectionAsync(native._stop.Token);
                Assert.True(await native._client.EnsureConnectedAsync(native._stop.Token));
                await accepted;
                native._pump = native.PumpAsync();
                Assert.True(await native._client.ProbeEdgeAsync(native._stop.Token));
                return native;
            }
            catch
            {
                await native.DisposeAsync();
                throw;
            }
        }

        public async Task ExpectAsync(Guid? cls, uint incarnation = 0x1234AA11)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            byte[] body = await Declarations.ReadAsync(timeout.Token);
            Assert.Equal(60, body.Length);
            Assert.Equal(new byte[] { 1, 0, Peer }, body[..3]);
            Assert.Equal(222u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(3)));
            Assert.Equal(cls ?? Guid.Empty, new Guid(body.AsSpan(27, 16)));
            Assert.Equal(cls.HasValue, (body[43] & 1) != 0);
            Assert.Equal(incarnation, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(44)));
            Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(48)));
        }

        private async Task PumpAsync()
        {
            try
            {
                var header = new byte[3];
                while (await _server.ReadAsync(header.AsMemory(0, 1), _stop.Token) != 0)
                {
                    await _server.ReadExactlyAsync(header.AsMemory(1), _stop.Token);
                    var body = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1))];
                    await _server.ReadExactlyAsync(body, _stop.Token);
                    if (header[0] == 0x2B)
                    {
                        Assert.Equal(new byte[] { 255 }, body);
                        await _server.WriteAsync(new byte[] { 0xAC, 5, 0, 1, 0, 255, 0, 1 }, _stop.Token);
                        continue;
                    }
                    Assert.Equal(0x38, header[0]);
                    Assert.Equal(0x41, body[5]);
                    byte[] declaration = body[10..];
                    Assert.Equal(60, declaration.Length);
                    var reply = new byte[44];
                    reply[0] = 0xB8;
                    reply[1] = 41;
                    body.AsSpan(0, 4).CopyTo(reply.AsSpan(3));
                    reply[8] = 0xBB;
                    reply[9] = 1;
                    await _server.WriteAsync(reply, _stop.Token);
                    _requests.Writer.TryWrite(declaration);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            finally { _requests.Writer.TryComplete(); }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            try { if (_pump is not null) await _pump; }
            finally
            {
                await _client.DisposeAsync();
                await _server.DisposeAsync();
                _stop.Dispose();
            }
        }
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
        var serial = (long)Call(bridge, "P6PeerAtRead", Peer)!;
        Assert.NotEqual(0, serial);
        return (Task)Call(bridge, "Wo214OnRangedHandAsync", Peer, Bow, Call(bridge, "P6GetContext"),
            0L, serial, Call(bridge, "Wo214FigureBody", Peer), capturedAt, (Func<bool>)(() => true), CancellationToken.None, null)!;
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

    [Theory]
    [InlineData("direct")]
    [InlineData("atomic")]
    [InlineData("preset")]
    public async Task Appearance_converge_keeps_the_bow_drawn_during_its_equipment_read(string path)
    {
        await using var link = new MemoryStream();
        using var game = new AppearanceCalls(path == "preset");
        await using var transport = new HttpGameTransport("http://localhost:1403", 5000, game);
        var bridge = await CreateHostAsync(link);
        Field("_transport").SetValue(bridge, transport);
        ReportOutfitBody(bridge, 1);
        ((ConcurrentDictionary<string, bool>)Field("_w202On").GetValue(bridge)!)["outfit_atomic"] = path == "atomic";
        OutfitItemIndex(bridge);

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        Guid[] target = path == "atomic" ? [Armor] : [];
        var converge = (Task)Call(bridge, "ApplyAppearanceAsync", Peer, target, stop.Token)!;
        await game.ReadStarted.Task.WaitAsync(stop.Token);
        try
        {
            await ReceiveFigureHandAsync(bridge, $"v1|1|{Bow:D}");
            var kept = (HashSet<Guid>)Call(bridge, "Wo214KeepHeld", Peer, new HashSet<Guid>())!;
            Assert.Contains(Bow, kept);
        }
        finally { game.ReleaseRead.TrySetResult(); }
        await converge.WaitAsync(stop.Token);

        Assert.DoesNotContain(game.Requests, IsOutfitMutation);
        await ReceiveFigureHandAsync(bridge, "v1|0|-");
        string awaitedKind = path switch { "atomic" => "atomic", "preset" => "preset", _ => "unequip" };
        string command = await game.ForKind(awaitedKind).Task.WaitAsync(stop.Token);
        Assert.Contains($"KCD2MP_W214OutfitApply(\"{Peer}\",", command);
        Assert.Contains("\"00000001\"", command);
    }

    [Fact]
    public async Task Appearance_converge_cannot_unequip_from_a_replacement_body_with_the_same_entity_id()
    {
        await using var link = new MemoryStream();
        using var game = new AppearanceCalls(false);
        await using var transport = new HttpGameTransport("http://localhost:1403", 5000, game);
        var bridge = await CreateHostAsync(link);
        Field("_transport").SetValue(bridge, transport);
        ReportOutfitBody(bridge, 1);
        int generation = (int)Call(bridge, "GhostGeneration", Peer.ToString())!;

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var converge = (Task)Call(bridge, "ApplyAppearanceAsync", Peer, Array.Empty<Guid>(), stop.Token)!;
        await game.ReadStarted.Task.WaitAsync(stop.Token);
        try { ReportOutfitBody(bridge, 2); }
        finally { game.ReleaseRead.TrySetResult(); }
        await converge.WaitAsync(stop.Token);

        Assert.DoesNotContain(game.Requests, IsOutfitMutation);
        Assert.Equal(generation + 1, (int)Call(bridge, "GhostGeneration", Peer.ToString())!);
    }

    [Fact]
    public async Task Appearance_converge_catches_up_only_the_newest_outfit_after_a_late_equip_dispatch_and_holster()
    {
        await using var link = new MemoryStream();
        using var game = new AppearanceCalls(false, startWithBow: false, delayFirstEquip: true);
        await using var transport = new HttpGameTransport("http://localhost:1403", 5000, game);
        var bridge = await CreateHostAsync(link);
        Field("_transport").SetValue(bridge, transport);
        Field("_w144Dress").SetValue(bridge, false);
        ReportOutfitBody(bridge, 1);
        game.ReleaseRead.TrySetResult();
        OutfitItemIndex(bridge);

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var converge = (Task)Call(bridge, "ApplyAppearanceAsync", Peer, new Guid[] { Sword }, stop.Token)!;
        await game.BlockedEquipStarted.Task.WaitAsync(stop.Token);
        try
        {
            await ReceiveFigureHandAsync(bridge, $"v1|1|{Bow:D}");
            await (Task)Call(bridge, "ApplyAppearanceAsync", Peer, new Guid[] { Crossbow }, stop.Token)!;
        }
        finally { game.ReleaseEquip.TrySetResult(); }
        await converge.WaitAsync(stop.Token);

        string stale = Assert.Single(game.Requests.Where(IsOutfitMutation));
        Assert.Contains(Sword.ToString("D"), stale);
        Assert.Contains("\"-\"", stale);
        await ReceiveFigureHandAsync(bridge, "v1|0|-");
        string command = await game.ForKind("equip").Task.WaitAsync(stop.Token);
        Assert.Contains(Crossbow.ToString("D"), command);
        Assert.DoesNotContain(Sword.ToString("D"), command);
        Assert.DoesNotContain("\"-\"", command);
    }

    [Fact]
    public async Task Appearance_converge_defers_unproven_atomic_slot_replacement_until_holster()
    {
        await using var link = new MemoryStream();
        using var game = new AppearanceCalls(false);
        await using var transport = new HttpGameTransport("http://localhost:1403", 5000, game);
        var bridge = await CreateHostAsync(link);
        Field("_transport").SetValue(bridge, transport);
        ((ConcurrentDictionary<string, bool>)Field("_w202On").GetValue(bridge)!)["outfit_atomic"] = true;
        ReportOutfitBody(bridge, 1);
        OutfitItemIndex(bridge);

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var converge = (Task)Call(bridge, "ApplyAppearanceAsync", Peer, new Guid[] { Armor, Sword }, stop.Token)!;
        await game.ReadStarted.Task.WaitAsync(stop.Token);
        try { await ReceiveFigureHandAsync(bridge, $"v1|1|{Bow:D}"); }
        finally { game.ReleaseRead.TrySetResult(); }
        await converge.WaitAsync(stop.Token);

        Assert.DoesNotContain(game.Requests, IsOutfitMutation);
        await ReceiveFigureHandAsync(bridge, "v1|0|-");
        string command = await game.ForKind("atomic").Task.WaitAsync(stop.Token);
        Assert.Contains(Armor.ToString("D"), command);
        Assert.Contains(Sword.ToString("D"), command);
    }

    private static void OutfitItemIndex(GameBridge bridge) => Field("_soulIndex").SetValue(bridge,
        Task.FromResult<SoulIndex?>(SoulIndex.FromRows([], [(Armor, "test_armour", "Armor"),
            (Sword, "test_sword", "Weapon"), (Crossbow, "test_crossbow", "Weapon")] )));

    private static void ReportOutfitBody(GameBridge bridge, int incarnation) =>
        Call(bridge, "OnGameEvent", "ghostid", $"{Peer} de {incarnation:X8}");

    private static bool IsOutfitMutation(string url) => url.Contains("KCD2MP_W214OutfitApply(", StringComparison.Ordinal);

    private sealed class AppearanceCalls(bool preset, bool startWithBow = true, bool delayFirstEquip = false) : HttpMessageHandler
    {
        public ConcurrentQueue<string> Requests { get; } = new();
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BlockedEquipStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseEquip { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _kinds = new();
        private int _reads;
        private bool _bowRemoved, _swordEquipped, _armorEquipped, _crossbowEquipped, _presetCleared;

        public TaskCompletionSource<string> ForKind(string kind) =>
            _kinds.GetOrAdd(kind, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string url = Uri.UnescapeDataString(request.RequestUri!.ToString());
            Requests.Enqueue(url);
            string response = "ok";
            if (url.Contains("EquippedWeaponsByClassId", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _reads) == 1)
                {
                    ReadStarted.TrySetResult();
                    await ReleaseRead.Task;
                }
                string bow = _bowRemoved || !startWithBow ? "" : $"<Item ItemClass=\"{Bow:D}\"/>";
                string sword = _swordEquipped || (preset && !_presetCleared) ? $"<Item ItemClass=\"{Sword:D}\"/>" : "";
                string crossbow = _crossbowEquipped ? $"<Item ItemClass=\"{Crossbow:D}\"/>" : "";
                response = $"<Items>{bow}{sword}{crossbow}</Items>";
            }
            else if (url.Contains("EquippedArmorsByClassId", StringComparison.Ordinal))
                response = _armorEquipped ? $"<Items><Item ItemClass=\"{Armor:D}\"/></Items>" : "<Items/>";
            else if (IsOutfitMutation(url))
            {
                string kind = new[] { "equip", "unequip", "atomic", "preset" }
                    .Single(k => url.Contains($",\"{k}\",", StringComparison.Ordinal));
                if (delayFirstEquip && kind == "equip" && BlockedEquipStarted.TrySetResult())
                    await ReleaseEquip.Task;
                else
                {
                    bool Has(Guid cls) => url.Contains(cls.ToString("D"), StringComparison.Ordinal);
                    switch (kind)
                    {
                        case "equip":
                        case "atomic":
                            _armorEquipped |= Has(Armor);
                            _swordEquipped |= Has(Sword);
                            _crossbowEquipped |= Has(Crossbow);
                            break;
                        case "unequip":
                            _bowRemoved |= Has(Bow);
                            _swordEquipped &= !Has(Sword);
                            break;
                        case "preset":
                            _presetCleared = true;
                            _bowRemoved = true;
                            _swordEquipped = _crossbowEquipped = _armorEquipped = false;
                            break;
                    }
                    ForKind(kind).TrySetResult(url);
                }
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) };
        }
    }

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
