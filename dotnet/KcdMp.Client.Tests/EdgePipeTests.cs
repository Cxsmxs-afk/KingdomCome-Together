// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public sealed class EdgePipeTests
{
    private const byte Request = 0x38, Reply = 0xB8, Generic = 0x2B, GenericReply = 0xAC;
    private const EdgeOutcome BusyAccepted = (EdgeOutcome)6;

    private sealed class Connection : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(10));
        public readonly NamedPipeServerStream Server;
        public readonly CombatPipe Pipe;
        public CancellationToken Token => _stop.Token;
        public SemaphoreSlim Writer => (SemaphoreSlim)typeof(CombatPipe)
            .GetField("_writeLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Pipe)!;

        public Connection()
        {
            string name = "edge-" + Guid.NewGuid().ToString("N")[..16];
            Server = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            Pipe = new(name);
        }

        public async Task Connect(bool supported = true)
        {
            var accept = Server.WaitForConnectionAsync(Token);
            Assert.True(await Pipe.EnsureConnectedAsync(Token));
            await accept;
            var probe = Pipe.ProbeEdgeAsync(Token);
            Assert.Equal(new byte[] { 255 }, await Read(Generic));
            if (supported) await Send(GenericReply, [1, 40, 255, 0, 1]);
            else await Send(GenericReply, [0, 40, 255, 18]);
            Assert.Equal(supported, await probe);
        }

        public async Task<byte[]> Read(byte type)
        {
            var header = new byte[3];
            await Server.ReadExactlyAsync(header, Token);
            Assert.Equal(type, header[0]);
            var body = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1))];
            await Server.ReadExactlyAsync(body, Token);
            return body;
        }

        public Task<EdgePipeReply> Ask(byte tag, EdgeLane lane = EdgeLane.Bulk, CancellationToken cancellation = default) =>
            Pipe.EdgeRequestAsync(Generic, [254, tag], GenericReply, lane, cancellation.CanBeCanceled ? cancellation : Token);

        public Task Complete(byte[] request, params byte[] result) =>
            Respond(Id(request), EdgeOutcome.Completed, GenericReply, result);

        public Task Respond(uint id, EdgeOutcome status, byte nested = 0, byte[]? result = null)
        {
            var body = new byte[6 + (result?.Length ?? 0)];
            BinaryPrimitives.WriteUInt32LittleEndian(body, id);
            body[4] = (byte)status;
            body[5] = nested;
            result?.CopyTo(body, 6);
            return Send(Reply, body);
        }

        public Task Send(byte type, byte[] body)
        {
            var frame = new byte[3 + body.Length];
            frame[0] = type;
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1), (ushort)body.Length);
            body.CopyTo(frame, 3);
            return Server.WriteAsync(frame, Token).AsTask();
        }

        public async ValueTask DisposeAsync()
        {
            await Pipe.DisposeAsync();
            Server.Dispose();
            _stop.Dispose();
        }
    }

    private static uint Id(byte[] request) => BinaryPrimitives.ReadUInt32LittleEndian(request);

    private sealed class PresentationCombatHarness(GameBridge bridge, object stamp)
    {
        public const byte Peer = 2;
        public const uint Entity = 0x14151617;
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly MethodInfo Schedule = typeof(GameBridge).GetMethod("TrySchedulePresentationRowAsync", Instance)!;
        private static readonly MethodInfo Submit = typeof(GameBridge).GetMethod("TrySubmitPresentationCombatAsync", Instance)!;
        private static readonly MethodInfo Dispatch = typeof(GameBridge).GetMethod("DispatchWo121ActionAsync", Instance)!;

        private static FieldInfo Field(string name) => typeof(GameBridge).GetField(name, Instance)!;
        private static Type Nested(string name) => typeof(GameBridge).GetNestedType(name, BindingFlags.NonPublic)!;
        public static async Task<PresentationCombatHarness> CreateAsync(Connection conn)
        {
            var bridge = new GameBridge(new ClientConfig());
            Field("_combat").SetValue(bridge, conn.Pipe);
            Field("_combatRoleApplied").SetValue(bridge, true);
            Field("_isDamageAuthority").SetValue(bridge, true);
            Field("_sharedWorld").SetValue(bridge, true);
            Field("_where").SetValue(bridge, Enum.Parse(Field("_where").FieldType, "World"));
            ((Wo206Epochs)Field("_w206").GetValue(bridge)!).EnsureHost();
            ((PeerSet)Field("_peers").GetValue(bridge)!).Connected(Peer, "partner");
            ((ConcurrentDictionary<string, uint>)Field("_ghostEntityIds").GetValue(bridge)!)["2"] = Entity;
            ((ConcurrentDictionary<string, byte>)Field("_nativeBound").GetValue(bridge)!)["kcd2mp_2"] = 1;
            ((ConcurrentDictionary<string, bool>)Field("_w221On").GetValue(bridge)!)["rp_combat"] = true;
            var lifecycle = new HostAuthorityLifecycle((phase, gen, _) =>
                Task.FromResult<HostAuthorityLifecycle.Ack?>(new(true, phase, false, false, gen, 0)));
            Assert.True(await lifecycle.PendingAsync(false, true, conn.Token));
            Assert.True(await lifecycle.GameplayAsync(lifecycle.Current.Generation, conn.Token));
            Field("_p1Lifecycle").SetValue(bridge, lifecycle);

            var session = Activator.CreateInstance(Nested("PresentationSession"), Instance, null, [Stream.Null, conn.Token], null)!;
            Assert.True((bool)session.GetType().GetMethod("AcceptEpoch", Instance)!.Invoke(session, [3UL, false])!);
            Field("_presentationSession").SetValue(bridge, session);
            var config = Activator.CreateInstance(Nested("PresentationConfig"), Instance, null, [0L, conn.Pipe.EdgeConnectionRevision, true], null);
            Field("_presentationNativeConfig").SetValue(bridge, config);
            var stamp = Activator.CreateInstance(Nested("PresentationFrame"), Instance, null, [session, 3UL, 1150UL, 1190UL], null)!;
            return new(bridge, stamp);
        }

        public Task<bool> Apply(ActionKind kind, string table, byte flags, ushort seq, CancellationToken ct)
        {
            int actionType = table switch
            {
                "combat_action_block" => ActionRowCatalog.ActionBlock,
                "combat_action_perfect_block" => ActionRowCatalog.ActionPerfectBlock,
                _ => -1
            };
            return ApplyRow(kind, new(table, "Combat", "tags", 0, 0, 0, ActionType: actionType), flags, seq, ct);
        }

        public Task<bool> ApplyRow(ActionKind kind, ActionRowCatalog.Row row, byte flags, ushort seq, CancellationToken ct)
        {
            var action = new InboundAction(Peer, kind, seq, ActionPhase.Commit, default, [70, 0, 0, 0]);
            return (Task<bool>)Schedule.Invoke(bridge, [action, Entity, row, flags, stamp, ct])!;
        }

        public Task<bool> DispatchBlock(ActionRowCatalog catalog, Guid row, ushort seq, CancellationToken ct, string name = "")
        {
            Field("_rowCatalog").SetValue(bridge, Task.FromResult<ActionRowCatalog?>(catalog));
            var action = new InboundAction(Peer, ActionKind.BlockImpulse, seq, ActionPhase.Commit, default,
                new RowEvent(70, 0, row, name).ToBytes());
            return (Task<bool>)Dispatch.Invoke(bridge, [action, ct, stamp])!;
        }

        public void Rebind() => ((ConcurrentDictionary<string, int>)Field("_ghostBodyGen").GetValue(bridge)!)["2"] = 1;

        public void SetTypedCombat(bool enabled) =>
            ((ConcurrentDictionary<string, bool>)Field("_w221On").GetValue(bridge)!)["rp_combat"] = enabled;

        public void DisableModes()
        {
            ((ConcurrentDictionary<string, bool>)Field("_w221On").GetValue(bridge)!)["rp_combat"] = false;
            Field("_presentationOn").SetValue(bridge, false);
        }

        public Task<PresentationCombatAdmission> Knockout(bool down, bool recovery, CancellationToken ct, Func<bool>? ready = null) =>
            (Task<PresentationCombatAdmission>)Submit.Invoke(bridge,
                [Peer, Entity, stamp, (ushort)62, (byte)0x81, new byte[] { down ? (byte)1 : (byte)0 }, ct, 0u, ready, recovery])!;
    }

    private static readonly ActionRowCatalog.Row CapturedPerfectBlock = new("combat_action_perfect_block", "CombatBlockPerfectGen",
        "l_noShield+r_shortSwords+player+rightGuard+eZ1+dZ1+oppMale", 1, -1, -1, ActionRowCatalog.ActionPerfectBlock,
        ActorClass: 1578932418, WeaponR: 2, WeaponL: 0);

    private static readonly ActionRowCatalog.Row AuthoredOrdinaryBlock = CapturedPerfectBlock with
    {
        Table = "combat_action_block", Fragment = "CombatBlockGen", Tags = "r_shortSwords+l_noShield+player+sZ1+eZ1+dZ1+oppMale",
        ActionType = ActionRowCatalog.ActionBlock
    };

    [Fact]
    public async Task Unflagged_perfect_block_with_exact_player_context_uses_an_authored_ordinary_block()
    {
        await using var conn = new Connection();
        await conn.Connect();
        var harness = await PresentationCombatHarness.CreateAsync(conn);
        var perfectId = Guid.NewGuid();
        var catalog = ActionRowCatalog.FromRows([(perfectId, CapturedPerfectBlock), (Guid.NewGuid(), AuthoredOrdinaryBlock)]);
        var play = harness.DispatchBlock(catalog, perfectId, 12, conn.Token);
        var firstRequest = conn.Read(Request);
        Assert.Same(firstRequest, await Task.WhenAny(play, firstRequest));
        var registration = await firstRequest;
        Assert.Equal(0x3F, registration[5]);
        Assert.Equal((byte)ActionKind.BlockImpulse, registration[15]);
        Assert.Equal(AuthoredOrdinaryBlock.Spec, Encoding.UTF8.GetString(registration.AsSpan(20)));
        await conn.Respond(Id(registration), EdgeOutcome.Completed, 0x81, [1, 0, 0]);

        var request = await conn.Read(Request);
        Assert.Equal(0x40, request[5]);
        Assert.Equal(registration[16..20], request[27..]);
        Assert.Equal(1150ul, BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(15)));
        Assert.Equal(12, BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(23)));
        Assert.Equal((byte)ActionKind.BlockImpulse, request[25]);
        await conn.Respond(Id(request), EdgeOutcome.Completed, 0x81, [1, 0, 0]);
        Assert.True(await play);
    }

    [Theory]
    [InlineData("missing-actor")]
    [InlineData("wrong-actor")]
    [InlineData("missing-left-weapon")]
    [InlineData("reversed-weapon-ids")]
    [InlineData("wrong-hand-tags")]
    [InlineData("conflicting-hand-tags")]
    [InlineData("missing-zone")]
    [InlineData("conflicting-zone")]
    [InlineData("npc-only")]
    [InlineData("npc-pair")]
    [InlineData("npc-wire")]
    [InlineData("other-opponent")]
    [InlineData("broken-block")]
    [InlineData("sync-source")]
    [InlineData("sync-candidate")]
    [InlineData("counter-55")]
    [InlineData("counter-62")]
    [InlineData("ambiguous")]
    public async Task Unflagged_perfect_block_without_exact_authored_context_does_not_request_a_pose(string mismatch)
    {
        await using var conn = new Connection();
        await conn.Connect();
        var harness = await PresentationCombatHarness.CreateAsync(conn);
        var perfect = mismatch switch
        {
            "missing-actor" => CapturedPerfectBlock with { ActorClass = 0 },
            "missing-zone" => CapturedPerfectBlock with { Zone = -1 },
            "npc-pair" => CapturedPerfectBlock with { Tags = CapturedPerfectBlock.Tags.Replace("+player", "", StringComparison.Ordinal) },
            "sync-source" => CapturedPerfectBlock with { Fragment = "CombatBlockPerfectSyncGen" },
            "counter-55" => CapturedPerfectBlock with { ActionType = 55 },
            "counter-62" => CapturedPerfectBlock with { ActionType = 62 },
            _ => CapturedPerfectBlock
        };
        var ordinary = mismatch switch
        {
            "wrong-actor" => AuthoredOrdinaryBlock with { ActorClass = 20967892 },
            "missing-left-weapon" => AuthoredOrdinaryBlock with { WeaponL = -1 },
            "reversed-weapon-ids" => AuthoredOrdinaryBlock with { WeaponL = 2, WeaponR = 0 },
            "wrong-hand-tags" => AuthoredOrdinaryBlock with { Tags = AuthoredOrdinaryBlock.Tags.Replace("l_noShield", "l_shield", StringComparison.Ordinal) },
            "conflicting-hand-tags" => AuthoredOrdinaryBlock with { Tags = AuthoredOrdinaryBlock.Tags + "+l_shield" },
            "conflicting-zone" => AuthoredOrdinaryBlock with { Zone = 2 },
            "npc-only" or "npc-pair" => AuthoredOrdinaryBlock with { Tags = AuthoredOrdinaryBlock.Tags.Replace("+player", "", StringComparison.Ordinal) },
            "other-opponent" => AuthoredOrdinaryBlock with { Tags = AuthoredOrdinaryBlock.Tags.Replace("oppMale", "oppFemale", StringComparison.Ordinal) },
            "broken-block" => AuthoredOrdinaryBlock with { ActionType = ActionRowCatalog.ActionBlockBroken },
            "sync-candidate" => AuthoredOrdinaryBlock with { ComboId = "combo", ComboStep = 1 },
            _ => AuthoredOrdinaryBlock
        };
        var perfectId = Guid.NewGuid();
        var rows = new List<(Guid, ActionRowCatalog.Row)> { (perfectId, perfect), (Guid.NewGuid(), ordinary) };
        if (mismatch == "ambiguous")
            rows.Add((Guid.NewGuid(), ordinary with { Tags = ordinary.Tags.Replace("sZ1", "sZ2", StringComparison.Ordinal) }));
        var rejected = harness.DispatchBlock(ActionRowCatalog.FromRows(rows), perfectId, 12, conn.Token,
            mismatch == "npc-wire" ? "guard" : "");
        Assert.True(await rejected.WaitAsync(conn.Token));
        // Fence the rejected action on the wire. Any unexpected pose would precede this Ping.
        var ping = conn.Pipe.PingAsync(conn.Token);
        Assert.Empty(await conn.Read(0x03));
        await conn.Send(0x83, []);
        Assert.True(await ping);
    }

    [Theory]
    [InlineData("combat_action_block", ActionRowCatalog.ActionPerfectBlock, 0)]
    [InlineData("combat_action_perfect_block", -1, RowEvent.FlagPerfect)]
    [InlineData("combat_action_perfect_block", 23, RowEvent.FlagPerfect)]
    [InlineData("combat_action_perfect_block", ActionRowCatalog.ActionPerfectBlock, 0)]
    [InlineData("combat_action_perfect_block", 55, 0)]
    [InlineData("combat_action_perfect_block", 62, 0)]
    public async Task Presentation_combat_drops_unproven_defence_through_the_real_row_scheduler(string table, int actionType, byte flags)
    {
        await using var conn = new Connection();
        await conn.Connect();
        var harness = await PresentationCombatHarness.CreateAsync(conn);
        var unproven = new ActionRowCatalog.Row(table, "UnprovenDefence", "tags", 0, 0, 0, ActionType: actionType);
        var rejected = harness.ApplyRow(ActionKind.BlockImpulse, unproven, flags, 12, conn.Token);
        var firstRequest = conn.Read(Request);
        if (await Task.WhenAny(rejected, firstRequest) == firstRequest)
        {
            byte[] unexpected = await firstRequest;
            await conn.Respond(Id(unexpected), EdgeOutcome.Completed, 0x81, [0, 0, 3]);
            Assert.Fail($"Unproven defence {table}:{actionType} flags={flags} wrote pipe operation 0x{unexpected[5]:X2}");
        }
        Assert.True(await rejected);

        // The next unflagged ordinary block still takes the original typed path.
        var ordinary = new ActionRowCatalog.Row("combat_action_block", "CombatBlockGen", "tags", 0, 0, 0, ActionType: ActionRowCatalog.ActionBlockBroken);
        var accepted = harness.ApplyRow(ActionKind.BlockImpulse, ordinary, 0, 13, conn.Token);
        byte[] registration = await firstRequest;
        Assert.Equal(0x3F, registration[5]);
        Assert.Equal((byte)ActionKind.BlockImpulse, registration[15]);
        Assert.Equal("CombatBlockGen, tags", Encoding.UTF8.GetString(registration.AsSpan(20)));
        await conn.Respond(Id(registration), EdgeOutcome.Completed, 0x81, [1, 0, 0]);
        byte[] request = await conn.Read(Request);
        Assert.Equal(0x40, request[5]);
        Assert.Equal((byte)ActionKind.BlockImpulse, request[25]);
        Assert.Equal(13, BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(23)));
        await conn.Respond(Id(request), EdgeOutcome.Completed, 0x81, [1, 0, 0]);
        Assert.True(await accepted);
    }

    [Theory]
    [InlineData(ActionKind.Attack, "combat_action_attack", 0, 1)]
    [InlineData(ActionKind.BlockImpulse, "combat_action_block", 0, 6)]
    [InlineData(ActionKind.BlockImpulse, "combat_action_perfect_block", Wo187Rules.NativeFlagPerfect, 0x83)]
    [InlineData(ActionKind.AvatarHit, "combat_action_hit", 0, 0x84)]
    [InlineData(ActionKind.AvatarRecoil, "combat_action_failed_attack", 0, 0x85)]
    public async Task Presentation_combat_registers_a_row_once_then_sends_only_its_numeric_key(
        ActionKind sourceKind, string table, byte flags, byte nativeKind)
    {
        await using var conn = new Connection();
        await conn.Connect();
        var harness = await PresentationCombatHarness.CreateAsync(conn);
        var first = harness.Apply(sourceKind, table, flags, 12, conn.Token);
        byte[] register = await conn.Read(Request);
        Assert.Equal(new byte[] { 2, 0x3F, 0, 0, 0, 0, 2, 0x17, 0x16, 0x15, 0x14, nativeKind }, register[4..16]);
        Assert.Equal("Combat, tags", Encoding.UTF8.GetString(register.AsSpan(20)));
        byte[] key = register[16..20];
        Assert.NotEqual(0u, BinaryPrimitives.ReadUInt32LittleEndian(key));
        await conn.Respond(Id(register), EdgeOutcome.Completed, 0x81, [1, 0, 0]);

        byte[] request = await conn.Read(Request);
        byte[] expected = [1, 0x40, 0, 0, 0, 0, 2, 0x17, 0x16, 0x15, 0x14, 0x7E, 0x04, 0, 0, 0, 0, 0, 0, 12, 0, nativeKind, 1, .. key];
        Assert.Equal(expected, request[4..]);
        await conn.Respond(Id(request), EdgeOutcome.Completed, 0x81, [1, 0, 0]);
        Assert.True(await first);

        var second = harness.Apply(sourceKind, table, flags, 13, conn.Token);
        var repeated = await conn.Read(Request);
        Assert.Equal(0x40, repeated[5]);
        Assert.Equal(13, BinaryPrimitives.ReadUInt16LittleEndian(repeated.AsSpan(23)));
        Assert.Equal(key, repeated[27..]);
        await conn.Respond(Id(repeated), EdgeOutcome.Completed, 0x81, [1, 0, 0]);
        Assert.True(await second);
    }

    [Fact]
    public async Task Presentation_combat_retries_unaccepted_busy_and_row_miss_but_never_repeats_an_ambiguous_effect()
    {
        await using var conn = new Connection();
        await conn.Connect();
        var harness = await PresentationCombatHarness.CreateAsync(conn);
        var action = harness.Apply(ActionKind.BlockImpulse, "combat_action_block", 0, 42, conn.Token);
        var setup = await conn.Read(Request);
        await conn.Respond(Id(setup), EdgeOutcome.Completed, 0x81, [1, 0, 0]);
        var first = await conn.Read(Request);
        await conn.Respond(Id(first), EdgeOutcome.Busy);
        var retry = await conn.Read(Request);
        Assert.NotEqual(Id(first), Id(retry));
        Assert.Equal(first[4..], retry[4..]);
        await conn.Respond(Id(retry), EdgeOutcome.Completed, 0x81, [0, 0, 4]);
        var reRegister = await conn.Read(Request);
        Assert.Equal(setup[4..], reRegister[4..]);
        await conn.Respond(Id(reRegister), EdgeOutcome.Completed, 0x81, [1, 0, 0]);
        var recovered = await conn.Read(Request);
        Assert.Equal(retry[4..], recovered[4..]);
        await conn.Respond(Id(recovered), (EdgeOutcome)5);   // Accepted local work faulted without a definitive result.
        Assert.True(await action);

        var next = harness.Apply(ActionKind.BlockImpulse, "combat_action_block", 0, 43, conn.Token);
        var nextRequest = await conn.Read(Request);
        Assert.Equal(43, BinaryPrimitives.ReadUInt16LittleEndian(nextRequest.AsSpan(23)));
        await conn.Respond(Id(nextRequest), EdgeOutcome.Unsupported);
        Assert.True(await next);

        var following = harness.Apply(ActionKind.BlockImpulse, "combat_action_block", 0, 44, conn.Token);
        var followingRequest = await conn.Read(Request);
        Assert.Equal(0x40, followingRequest[5]);
        Assert.Equal(44, BinaryPrimitives.ReadUInt16LittleEndian(followingRequest.AsSpan(23)));
        await conn.Respond(Id(followingRequest), EdgeOutcome.Completed, 0x81, [1, 0, 0]);
        Assert.True(await following);
    }

    [Fact]
    public async Task Presentation_combat_does_not_release_a_legacy_retry_after_the_typed_owner_takes_over()
    {
        await using var conn = new Connection();
        await conn.Connect();
        var harness = await PresentationCombatHarness.CreateAsync(conn);
        harness.SetTypedCombat(false);
        var action = harness.Apply(ActionKind.Attack, "combat_action_attack", 0, 45, conn.Token);
        var legacy = await conn.Read(Request);
        Assert.Equal(0x3B, legacy[5]);
        harness.SetTypedCombat(true);
        await conn.Respond(Id(legacy), EdgeOutcome.Unsupported);
        Assert.True(await action);
    }

    [Fact]
    public async Task Presentation_combat_does_not_fall_back_after_its_actor_generation_retires()
    {
        await using var conn = new Connection();
        await conn.Connect();
        var harness = await PresentationCombatHarness.CreateAsync(conn);
        var action = harness.Apply(ActionKind.BlockImpulse, "combat_action_block", 0, 42, conn.Token);
        var setup = await conn.Read(Request);
        await conn.Respond(Id(setup), EdgeOutcome.Completed, 0x81, [1, 0, 0]);
        var request = await conn.Read(Request);
        harness.Rebind();
        await conn.Respond(Id(request), EdgeOutcome.Unsupported);
        Assert.True(await action);
    }

    [Fact]
    public async Task Presentation_wake_recovery_can_use_the_same_actor_with_both_modes_off()
    {
        await using var conn = new Connection();
        await conn.Connect();
        var harness = await PresentationCombatHarness.CreateAsync(conn);
        harness.DisableModes();
        Assert.Equal(PresentationCombatAdmission.Fallback, await harness.Knockout(down: true, recovery: false, conn.Token));
        Assert.Equal(PresentationCombatAdmission.Suppressed, await harness.Knockout(down: true, recovery: true, conn.Token));
        Assert.Equal(PresentationCombatAdmission.Suppressed, await harness.Knockout(down: false, recovery: true, conn.Token, () => false));

        var wake = harness.Knockout(down: false, recovery: true, conn.Token);
        var request = await conn.Read(Request);
        Assert.Equal(0x40, request[5]);
        Assert.Equal(new byte[] { 0x81, 1, 0 }, request[25..]);
        await conn.Respond(Id(request), EdgeOutcome.Busy);
        var retry = await conn.Read(Request);
        Assert.Equal(request[4..], retry[4..]);
        await conn.Respond(Id(retry), EdgeOutcome.Busy);
        Assert.Equal(PresentationCombatAdmission.Fallback, await wake);  // Keep the original typed wake pending in the caller.
    }

    [Fact]
    public async Task A_realtime_reply_can_pass_unanswered_bulk_and_only_its_own_id_and_typed_reply_complete_it()
    {
        await using var conn = new Connection();
        await conn.Connect();
        var bulk = conn.Ask(9);
        var first = await conn.Read(Request);
        var realtime = conn.Ask(7, EdgeLane.Realtime);
        var second = await conn.Read(Request);
        Assert.NotEqual(Id(first), Id(second));
        Assert.Equal(new byte[] { 2, Generic, 0, 0, 0, 0, 254, 9 }, first[4..]);
        Assert.Equal(new byte[] { 1, Generic, 0, 0, 0, 0, 254, 7 }, second[4..]);

        await conn.Respond(Id(second) + 100, EdgeOutcome.Completed, GenericReply, [1, 0, 254, 0, 66]);
        await conn.Respond(Id(second), EdgeOutcome.Completed, 0x81, [1, 0, 0]);
        await conn.Complete(second, 1, 0, 17, 0, 66); // same ID, wrong nested operation
        await conn.Complete(second, 1, 0, 254, 0, 7);
        Assert.Equal(new byte[] { 1, 0, 254, 0, 7 }, (await realtime).Payload);
        Assert.False(bulk.IsCompleted);

        await conn.Respond(Id(first), EdgeOutcome.Busy);
        Assert.Equal(EdgeOutcome.Busy, (await bulk).Outcome);
        Assert.Equal(1, conn.Pipe.EdgeBusyReplies);
    }

    [Fact]
    public async Task Busy_progress_keeps_its_id_pending_while_other_requests_complete_out_of_order()
    {
        await using var conn = new Connection();
        await conn.Connect();
        var bulk = conn.Ask(9);
        var first = await conn.Read(Request);
        var realtime = conn.Ask(7, EdgeLane.Realtime);
        var second = await conn.Read(Request);

        await conn.Respond(Id(first) + 100, BusyAccepted);
        await conn.Respond(Id(first), BusyAccepted, GenericReply);
        await conn.Respond(Id(first), BusyAccepted, result: [1]);
        await conn.Respond(Id(first), BusyAccepted);
        await conn.Complete(second, 1, 0, 254, 0, 7);
        Assert.Equal(new byte[] { 1, 0, 254, 0, 7 }, (await realtime).Payload);
        Assert.False(bulk.IsCompleted);
        Assert.Equal(1, conn.Pipe.EdgeBusyReplies);

        var next = conn.Ask(12);
        var third = await conn.Read(Request);
        Assert.NotEqual(Id(first), Id(third));
        Assert.Equal(12, third[11]);
        await conn.Complete(first, 1, 0, 254, 0, 9);
        await conn.Respond(Id(first), BusyAccepted); // progress on a completed ID is stale
        await conn.Complete(third, 1, 0, 254, 0, 12);
        Assert.Equal(new byte[] { 1, 0, 254, 0, 9 }, (await bulk).Payload);
        Assert.Equal(new byte[] { 1, 0, 254, 0, 12 }, (await next).Payload);
        Assert.Equal(1, conn.Pipe.EdgeBusyReplies);
    }

    [Fact]
    public async Task Busy_progress_without_a_final_reply_still_times_out_as_ambiguous_without_replaying()
    {
        await using var conn = new Connection();
        await conn.Connect();
        var pending = conn.Ask(1);
        var first = await conn.Read(Request);
        await conn.Respond(Id(first), BusyAccepted);
        Assert.Equal(EdgeOutcome.Ambiguous, (await pending).Outcome);
        Assert.Equal(1, conn.Pipe.EdgeBusyReplies);

        var live = conn.Ask(2);
        var second = await conn.Read(Request);
        Assert.NotEqual(Id(first), Id(second));
        Assert.Equal(2, second[11]);
        await conn.Respond(Id(first), BusyAccepted);
        await conn.Complete(first, 1, 0, 254, 0, 1);
        await conn.Complete(second, 1, 0, 254, 0, 2);
        Assert.Equal(new byte[] { 1, 0, 254, 0, 2 }, (await live).Payload);
        Assert.Equal(1, conn.Pipe.EdgeBusyReplies);
    }

    [Fact]
    public async Task Edge_and_legacy_requests_can_overlap_without_consuming_the_legacy_sequence()
    {
        await using var conn = new Connection();
        await conn.Connect();
        var edge = conn.Ask(17);
        var outer = await conn.Read(Request);
        var bind = typeof(CombatPipe).GetMethod("NpcBindAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var legacy = (Task<(bool Ok, byte Reason)>)bind.Invoke(conn.Pipe,
            [false, 0u, 0ul, 0f, 0f, 0f, (ushort)0, "kcd2mp_1", true, conn.Token])!;
        await conn.Read(0x11);
        await conn.Send(0x81, [1, 41, 0]); // capability probe used 40; edge used no legacy sequence
        Assert.True((await legacy).Ok);
        Assert.False(edge.IsCompleted);
        await conn.Complete(outer, 1, 0, 254, 0, 1);
        Assert.Equal(EdgeOutcome.Completed, (await edge).Outcome);
    }

    [Fact]
    public async Task A_relocation_fence_rechecks_scope_at_the_physical_writer()
    {
        await using var conn = new Connection();
        await conn.Connect();
        var body = new RemoteRelocationApproval(7, 100, RemoteRelocationReason.Join, new(1, 2, 3), 20, 1).Build();
        int current = 1;
        await conn.Writer.WaitAsync(conn.Token);
        Task<EdgePipeReply> stale;
        try
        {
            stale = conn.Pipe.EdgeRequestAsync(0x3E, body, 0x81, EdgeLane.Realtime, conn.Token,
                beforeWrite: () => Volatile.Read(ref current) == 1);
            Volatile.Write(ref current, 0);
        }
        finally { conn.Writer.Release(); }
        Assert.Equal(EdgeOutcome.Unavailable, (await stale).Outcome);
        var valid = conn.Pipe.EdgeRequestAsync(0x3E, body, 0x81, EdgeLane.Realtime, conn.Token,
            beforeWrite: () => Volatile.Read(ref current) == 0);
        var frame = await conn.Read(Request);
        Assert.Equal(new byte[] { 1, 0x3E, 0, 0, 0, 0 }, frame[4..10]);
        Assert.Equal(body, frame[10..]);
        Assert.Equal(2u, Id(frame)); // the first request was skipped before any bytes were written
        await conn.Respond(Id(frame), EdgeOutcome.Completed, 0x81, [1, 0, 0]);
        Assert.Equal(EdgeOutcome.Completed, (await valid).Outcome);
    }

    [Fact]
    public async Task The_writer_keeps_fifo_within_each_lane_and_gives_waiting_bulk_every_fourth_turn()
    {
        await using var conn = new Connection();
        await conn.Connect();
        await conn.Writer.WaitAsync(conn.Token);
        Task<EdgePipeReply>[] work;
        try
        {
            var realtime = Enumerable.Range(1, 6).Select(n => conn.Ask((byte)n, EdgeLane.Realtime)).ToArray();
            work = [.. realtime, conn.Ask(9), conn.Ask(10)];
        }
        finally { conn.Writer.Release(); }
        foreach (byte tag in new byte[] { 1, 2, 3, 9, 4, 5, 6, 10 })
        {
            var req = await conn.Read(Request);
            Assert.Equal(tag, req[11]);
            await conn.Complete(req, 1, 0, 254, 0, tag);
        }
        Assert.All(await Task.WhenAll(work), item => Assert.Equal(EdgeOutcome.Completed, item.Outcome));
    }

    [Fact]
    public async Task Latest_state_replaces_only_its_key_and_raw_pose_preserves_the_arrival_and_original_bytes()
    {
        await using var conn = new Connection();
        await conn.Connect();
        byte[] pose = NpcCompact.BuildDown(8, "Npc_17", new(2, 3, 4, 5, 90, 0, 8, 13));
        await conn.Writer.WaitAsync(conn.Token);
        try
        {
            Assert.True(conn.Pipe.PostEdgeLatest(0x09, [1, 0, 0, 0], 0x85, 41));
            Assert.True(conn.Pipe.PostEdgeLatest(0x09, [2, 0, 0, 0], 0x85, 41));
            Assert.True(conn.Pipe.PostEdgeLatest(0x09, [3, 0, 0, 0], 0x85, 42));
            Assert.True(conn.Pipe.PostEdgePose(Protocol.NpcStateDown, pose, 1234567));
        }
        finally { conn.Writer.Release(); }
        for (int value = 2; value <= 3; value++)
        {
            var latest = await conn.Read(Request);
            Assert.Equal(3, latest[4]);
            Assert.Equal((uint)(39 + value), BinaryPrimitives.ReadUInt32LittleEndian(latest.AsSpan(6)));
            Assert.Equal(new byte[] { (byte)value, 0, 0, 0 }, latest[10..]);
            await conn.Respond(Id(latest), EdgeOutcome.Completed, 0x85, [1, 0, 0, 0, 0, 0]);
        }
        var raw = await conn.Read(Request);
        Assert.Equal(new byte[] { 1, 0x39, 0, 0, 0, 0, 0x27 }, raw[4..11]);
        Assert.Equal(1234567, BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(11)));
        Assert.Equal(pose, raw[19..]);
        await conn.Respond(Id(raw), EdgeOutcome.Completed, 0x81, [1, 0, 0]);
        while (conn.Pipe.EdgeStateSkipped == 0) await Task.Delay(1, conn.Token);
        Assert.Equal(1, conn.Pipe.EdgeStateSkipped);
    }

    [Fact]
    public async Task Timed_realtime_pose_carries_both_host_times_and_original_body_only_on_its_current_connection()
    {
        await using var conn = new Connection();
        await conn.Connect();
        byte[] pose = NpcCompact.BuildDown(8, "Npc_17", new(2, 3, 4, 5, 90, 0, 8, 13));
        long revision = conn.Pipe.EdgeConnectionRevision;
        const ulong captured = 1UL << 40, arrived = captured + 37;
        Assert.False(conn.Pipe.PostPresentationPose(Protocol.NpcStateDown, pose, 54321, captured, arrived, revision + 1));
        Assert.False(conn.Pipe.PostPresentationPose(Protocol.NpcStateDown, pose, 54321, 0, arrived, revision));
        Assert.True(conn.Pipe.PostPresentationPose(Protocol.NpcStateDown, pose, 54321, captured, arrived, revision));
        var wire = await conn.Read(Request);
        Assert.Equal(new byte[] { 1, 0x3C, 0, 0, 0, 0, Protocol.NpcStateDown }, wire[4..11]);
        Assert.Equal(54321, BinaryPrimitives.ReadInt64LittleEndian(wire.AsSpan(11)));
        Assert.Equal(captured, BinaryPrimitives.ReadUInt64LittleEndian(wire.AsSpan(19)));
        Assert.Equal(arrived, BinaryPrimitives.ReadUInt64LittleEndian(wire.AsSpan(27)));
        Assert.Equal(pose, wire[35..]);
        await conn.Respond(Id(wire), EdgeOutcome.Completed, 0x81, [1, 0, 0]);
    }

    [Fact]
    public async Task A_full_send_queue_retains_latest_raw_pose_and_its_stance_without_replacing_reliable_work()
    {
        await using var conn = new Connection();
        await conn.Connect();
        var stance = new byte[Protocol.GhostPayloadLenV8];
        var older = new byte[Protocol.GhostPayloadLen];
        var newest = new byte[Protocol.GhostPayloadLen];
        stance[0] = older[0] = newest[0] = 7;
        stance[17] = Protocol.PositionFlagBodyState2;
        stance[^1] = 23;
        older[1] = 1;
        newest[1] = 2;
        Task<EdgePipeReply>[] reliable;

        await conn.Writer.WaitAsync(conn.Token);
        try
        {
            Assert.True(conn.Pipe.PostEdgePose(Protocol.Ghost, stance, 20));
            Assert.True(conn.Pipe.PostEdgePose(Protocol.Ghost, older, 21));
            reliable = Enumerable.Range(0, 510).Select(n => conn.Ask((byte)n)).ToArray();
            Assert.Equal(EdgeOutcome.Unavailable, (await conn.Ask(255)).Outcome);
            Assert.True(conn.Pipe.PostEdgePose(Protocol.Ghost, newest, 23));
            Assert.True(conn.Pipe.PostEdgePose(Protocol.Ghost, older, 22)); // late dispatch cannot replace fresher state
        }
        finally { conn.Writer.Release(); }

        foreach (var (pose, arrival) in new[] { (stance, 20L), (newest, 23L) })
        {
            var preview = await conn.Read(Request);
            Assert.Equal(new byte[] { 1, 0x39, 0, 0, 0, 0, Protocol.Ghost }, preview[4..11]);
            Assert.Equal(arrival, BinaryPrimitives.ReadInt64LittleEndian(preview.AsSpan(11)));
            Assert.Equal(pose, preview[19..]);
            await conn.Respond(Id(preview), EdgeOutcome.Completed, 0x81, [1, 0, 0]);
        }
        for (int i = 0; i < reliable.Length; i++)
        {
            var other = await conn.Read(Request);
            Assert.Equal(2, other[4]);
            Assert.Equal((byte)i, other[11]);
            await conn.Complete(other, 1, 0, 254, 0);
        }
        Assert.All(await Task.WhenAll(reliable), result => Assert.Equal(EdgeOutcome.Completed, result.Outcome));
        Assert.Equal(2, conn.Pipe.EdgeStateSkipped);
    }

    [Fact]
    public async Task Notices_keep_their_order_while_a_callback_waits_and_do_not_hold_up_request_replies()
    {
        await using var conn = new Connection();
        await conn.Connect();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.Pipe.OnLocalDowned = async (_, value) =>
        {
            if (value == 1) { entered.TrySetResult(); await released.Task; }
            else seen.TrySetResult(value);
        };
        await conn.Send(0x91, [1, 1]);
        await entered.Task.WaitAsync(conn.Token);
        try
        {
            await conn.Send(0x91, [1, 2]);
            var request = conn.Ask(4);
            var req = await conn.Read(Request);
            await conn.Complete(req, 1, 0, 254, 0, 1);
            Assert.Equal(EdgeOutcome.Completed, (await request).Outcome);
            Assert.False(seen.Task.IsCompleted);
        }
        finally { released.TrySetResult(); }
        Assert.Equal(2, await seen.Task.WaitAsync(conn.Token));
    }

    [Fact]
    public async Task A_disconnect_fails_unsent_and_sent_operations_differently_and_retires_the_capability()
    {
        await using var conn = new Connection();
        await conn.Connect();
        long before = conn.Pipe.EdgeConnectionRevision;
        var sent = conn.Ask(1);
        await conn.Read(Request);
        await conn.Writer.WaitAsync(conn.Token);
        try
        {
            var unsent = conn.Ask(2);
            conn.Server.Dispose();
            Assert.Equal(EdgeOutcome.Ambiguous, (await sent).Outcome);
            Assert.Equal(EdgeOutcome.Unavailable, (await unsent).Outcome);
        }
        finally { conn.Writer.Release(); }
        Assert.False(conn.Pipe.EdgeActive);
        Assert.True(conn.Pipe.EdgeConnectionRevision > before);
    }

    [Fact]
    public async Task Native_failure_after_admission_is_ambiguous()
    {
        await using var conn = new Connection();
        await conn.Connect();
        var request = conn.Ask(1);
        var sent = await conn.Read(Request);
        await conn.Respond(Id(sent), (EdgeOutcome)5);
        Assert.Equal(EdgeOutcome.Ambiguous, (await request).Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Older_DLL_and_HTTP_policy_do_not_send_new_envelopes(bool supported)
    {
        await using var conn = new Connection();
        await conn.Connect(supported);
        if (supported) conn.Pipe.EdgeTransportOn = false;
        Assert.False(conn.Pipe.EdgeActive);
        Assert.Equal(EdgeOutcome.Unavailable, (await conn.Ask(1)).Outcome);
        Assert.False(await conn.Pipe.ProbeEdgeAsync(conn.Token));
        var old = conn.Pipe.NpcHoldAsync("kcd2mp_1", 100, conn.Token);
        await conn.Read(0x12);
        await conn.Send(0x81, [1, 41, 0]);
        Assert.True((await old).Ok);
    }

    [Theory]
    [InlineData(4090, true)]
    [InlineData(4091, false)]
    [InlineData(6146, false)]
    [InlineData(6402, false)]
    public async Task Only_a_stats_mirror_that_exceeds_the_batch_cap_uses_the_large_standalone_request(int length, bool batch)
    {
        await using var conn = new Connection();
        await conn.Connect();
        conn.Pipe.SetBatchSupported(true);
        byte[] args = new byte[length - 1];
        args[0] = 1;
        var op = conn.Pipe.Wo163Async(250, args, conn.Token);
        var frame = await conn.Read(batch ? (byte)0x2D : Generic);
        if (batch)
        {
            Assert.Equal(4096, frame.Length);
            Assert.Equal(Generic, frame[3]);
            Assert.Equal(length, BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(4)));
            Assert.Equal(250, frame[6]);
            await conn.Send(0xAF, [1, frame[0], frame[1], 1, GenericReply, 4, 0, 1, 0, 250, 0]);
        }
        else
        {
            Assert.Equal(length, frame.Length);
            Assert.Equal(250, frame[0]);
            await conn.Send(GenericReply, [1, 41, 250, 0]);
        }
        Assert.True((await op)!.Value.Ok);
    }
}
