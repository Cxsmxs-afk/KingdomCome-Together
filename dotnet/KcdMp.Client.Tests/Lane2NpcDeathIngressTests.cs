// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Reflection;
using System.Text;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public sealed class Lane2NpcDeathIngressTests
{
    private const string Npc = "A_Test_Npc";
    private const uint Eid = 0x11223344;
    private const byte Peer = 2;
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    [Theory]
    [InlineData("named-reader", Eid)]
    [InlineData("fatal", Eid)]
    [InlineData("guid", 0U)]
    public async Task Every_ingress_uses_one_scoped_death_attempt(string route, uint expectedEid)
    {
        await using var fixture = await Fixture.OpenAsync();
        Task ingress = fixture.Ingress(route);
        var first = await fixture.ReadRequestAsync();
        fixture.AssertBound(first, expectedEid);
        await fixture.ReplyAsync(1, 0x70);
        Assert.False(fixture.Dead);

        var recheck = await fixture.ReadRequestAsync();
        Assert.Equal(first.Body, recheck.Body);
        Assert.False(fixture.Dead);
        await fixture.ReplyAsync(2, 0);
        await ingress.WaitAsync(fixture.Token);
        if (expectedEid != 0)
        {
            await fixture.WaitForDeadAsync();
            Assert.Equal(1L, fixture.Releases);
            Assert.Equal(Eid + 1, fixture.EngagedEid);
        }

        await fixture.Ingress(route).WaitAsync(fixture.Token);
        await fixture.Ingress(route == "guid" ? "fatal" : "guid").WaitAsync(fixture.Token);
        if (expectedEid != 0) await fixture.CompleteFallCheckAsync(3);
        await fixture.AssertNoWriteAsync();
    }

    [Fact]
    public async Task Named_death_without_concrete_soul_binding_writes_nothing()
    {
        await using var fixture = await Fixture.OpenAsync();
        fixture.RemoveBinding();
        await fixture.Ingress("named");
        Assert.False(fixture.Dead);
        await fixture.AssertNoWriteAsync();
    }

    [Theory]
    [InlineData("load")]
    [InlineData("eid-reuse")]
    [InlineData("soul-reuse")]
    [InlineData("peer-reuse")]
    public async Task Retired_binding_cannot_dispatch_waiting_writer(string reason)
    {
        await using var fixture = await Fixture.OpenAsync();
        Task ingress;
        await fixture.Writer.WaitAsync(fixture.Token);
        try
        {
            ingress = fixture.Ingress("named");
            Assert.False(ingress.IsCompleted);
            Assert.False(fixture.Dead);
            fixture.Retire(reason);
        }
        finally { fixture.Writer.Release(); }
        await ingress.WaitAsync(fixture.Token);
        await fixture.AssertNoWriteAsync();
        Assert.False(fixture.Dead);
    }

    [Theory]
    [InlineData("eid-reuse")]
    [InlineData("load")]
    [InlineData("peer-disconnect")]
    public async Task Old_receipt_cannot_touch_new_body_or_send_a_poll(string reason)
    {
        await using var fixture = await Fixture.OpenAsync();
        Task ingress = fixture.Ingress("named");
        fixture.AssertBound(await fixture.ReadRequestAsync(), Eid);
        fixture.Retire(reason);
        await fixture.ReplyAsync(1, 0x70);
        await ingress.WaitAsync(fixture.Token);
        await fixture.AssertNoWriteAsync();
        Assert.False(fixture.Dead);
    }

    [Theory]
    [InlineData("named-reader", Eid)]
    [InlineData("fatal", Eid)]
    [InlineData("guid", 0U)]
    public async Task Finished_after_1200ms_confirms_one_apply_on_the_original_body(string route, uint expectedEid)
    {
        await using var fixture = await Fixture.OpenAsync();
        Task ingress = fixture.Ingress(route);
        var first = await fixture.ReadRequestAsync();
        fixture.AssertBound(first, expectedEid);
        byte seq = 1;
        long started = Stopwatch.GetTimestamp();
        await fixture.ReplyAsync(seq++, 0x70);

        while (true)
        {
            var next = await fixture.ReadWhilePendingAsync(ingress);
            Assert.Equal(first.Op, next.Op);
            Assert.Equal(first.Body, next.Body);
            Assert.False(fixture.Dead);
            Assert.Equal(0L, fixture.Releases);
            if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromMilliseconds(1200))
            {
                // The DLL publishes its sole lethal application once Finished
                // is visible. The same original request authorizes that apply.
                await fixture.ReplyAsync(seq, 0);
                break;
            }
            await fixture.ReplyAsync(seq++, 0x70);
        }

        await ingress.WaitAsync(fixture.Token);
        if (expectedEid != 0)
        {
            Assert.True(fixture.Dead);
            Assert.Equal(1L, fixture.Releases);
        }
        await fixture.Ingress(route == "guid" ? "fatal" : "guid");
        if (expectedEid != 0) await fixture.CompleteFallCheckAsync((byte)(seq + 1));
        await fixture.AssertNoWriteAsync();
    }

    [Fact]
    public async Task Uncertain_apply_is_observed_without_again_authorizing_engine_mutation()
    {
        await using var fixture = await Fixture.OpenAsync();
        Task ingress = fixture.Ingress("fatal");
        var first = await fixture.ReadRequestAsync();
        fixture.AssertBound(first, Eid);
        await fixture.ReplyAsync(1, 0x71);

        var observation = await fixture.ReadWhilePendingAsync(ingress);
        fixture.AssertFollowup(first, observation, 1);
        Assert.False(fixture.Dead);
        await fixture.Ingress("guid");
        await fixture.ReplyAsync(2, 0x70);

        var repeatedObservation = await fixture.ReadWhilePendingAsync(ingress);
        fixture.AssertFollowup(first, repeatedObservation, 1);
        Assert.False(fixture.Dead);
        await fixture.ReplyAsync(3, 0);
        await ingress.WaitAsync(fixture.Token);
        Assert.True(fixture.Dead);
        Assert.Equal(1L, fixture.Releases);
        await fixture.Ingress("fatal").WaitAsync(fixture.Token);
        await fixture.CompleteFallCheckAsync(4);
        await fixture.AssertNoWriteAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expired_poll_budget_is_stale_only_when_the_body_scope_was_retired(bool retireBinding)
    {
        await using var fixture = await Fixture.OpenAsync();
        Task ingress = fixture.Ingress("fatal");
        fixture.AssertBound(await fixture.ReadRequestAsync(), Eid);
        if (retireBinding) fixture.Retire("eid-reuse");

        // Keep the first real pipe request outstanding. A canceled local
        // observer must classify its binding without sending another write.
        var expected = retireBinding ? CombatPipe.NpcDeathReceipt.Stale : CombatPipe.NpcDeathReceipt.Uncertain;
        Assert.Equal(expected, await fixture.ObserveWithExpiredBudgetAsync());
        await fixture.ReplyAsync(1, 0x73);
        await ingress.WaitAsync(fixture.Token);
        Assert.False(fixture.Dead);
        await fixture.AssertNoWriteAsync();
    }

    [Theory]
    [InlineData((byte)0x73)]
    [InlineData((byte)0x71)]
    [InlineData(null)]
    public async Task Deadline_sends_one_pre_apply_abort_and_no_replacement(byte? abortReceipt)
    {
        await using var fixture = await Fixture.OpenAsync();
        Task ingress = fixture.Ingress("fatal");
        var first = await fixture.ReadRequestAsync();
        fixture.AssertBound(first, Eid);
        byte seq = 1;
        await fixture.ReplyAsync(seq++, 0x70);

        while (true)
        {
            var next = await fixture.ReadWhilePendingAsync(ingress);
            Assert.False(fixture.Dead);
            if (next.Body.Length == 37)
            {
                fixture.AssertFollowup(first, next, 2);
                if (abortReceipt is byte answer) await fixture.ReplyAsync(seq, answer);
                break;
            }
            Assert.Equal(first.Body, next.Body);
            await fixture.ReplyAsync(seq++, 0x70);
        }

        await ingress.WaitAsync(fixture.Token);
        Assert.False(fixture.Dead);
        Assert.Equal(0L, fixture.Releases);
        await fixture.Ingress("guid").WaitAsync(fixture.Token);
        await fixture.AssertNoWriteAsync();
    }

    [Fact]
    public async Task Initial_abort_receipt_closes_the_attempt_without_followup_or_replacement()
    {
        await using var fixture = await Fixture.OpenAsync();
        Task ingress = fixture.Ingress("fatal");
        fixture.AssertBound(await fixture.ReadRequestAsync(), Eid);
        await fixture.ReplyAsync(1, 0x73);

        await fixture.AssertCompletedWithoutWriteAsync(ingress);
        Assert.False(fixture.Dead);
        Assert.Equal(0L, fixture.Releases);
        await fixture.Ingress("named");
        await fixture.Ingress("guid");
        await fixture.AssertNoWriteAsync();
    }

    [Fact]
    public async Task Pending_releases_the_frame_and_keeps_observing_the_same_attempt()
    {
        await using var fixture = await Fixture.OpenAsync();
        Task<ProcessorFrameScope.Result> frame = ProcessorFrameScope.RunAsync(
            _ => fixture.Ingress("fatal"), 2_000, fixture.Token);
        var first = await fixture.ReadRequestAsync();
        fixture.AssertBound(first, Eid);
        await fixture.ReplyAsync(1, 0x70);
        Assert.Equal(ProcessorFrameScope.Outcome.Completed, (await frame.WaitAsync(fixture.Token)).Status);
        Assert.False(fixture.Dead);
        var observation = await fixture.ReadRequestAsync();
        Assert.Equal(first.Body, observation.Body);
        await fixture.ReplyAsync(2, 0);
        await fixture.WaitForDeadAsync();
    }

    [Theory]
    [InlineData("named-damage")]
    [InlineData("guid-damage")]
    public async Task Unjudged_numeric_damage_cannot_kill_an_npc_copy(string route)
    {
        await using var fixture = await Fixture.OpenAsync();
        Task ingress = fixture.Ingress(route);
        var request = await fixture.ReadRequestAsync();
        Assert.Equal(0x01, request.Op);
        Assert.Equal(25, request.Body.Length);
        Assert.Equal(7f, BinaryPrimitives.ReadSingleLittleEndian(request.Body.AsSpan(16)));
        Assert.Equal(95f, BinaryPrimitives.ReadSingleLittleEndian(request.Body.AsSpan(20)));
        Assert.Equal(0x02, request.Body[24] & 0x02);
        await fixture.ReplyAsync(1, 0);
        await ingress.WaitAsync(fixture.Token);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private static readonly TimeSpan PipeQuietWindow = TimeSpan.FromMilliseconds(150);
        // The watchdog covers startup and both production polling budgets on a busy runner.
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(15));
        private readonly NamedPipeServerStream _wire;
        private readonly CombatPipe _pipe;
        private readonly GameBridge _bridge = new(new ClientConfig());
        private readonly HttpGameTransport _game = new("http://localhost:1403", 5_000, new GameDouble()) { BatchingEnabled = false };
        private readonly Stream _relay = new MemoryStream();
        private readonly Guid _soul = Guid.NewGuid();
        private ulong _generation;

        private Fixture()
        {
            string name = "lane2-npc-death-" + Guid.NewGuid().ToString("N");
            _wire = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            _pipe = new(name);
        }

        public CancellationToken Token => _deadline.Token;

        public static async Task<Fixture> OpenAsync()
        {
            var f = new Fixture();
            Task connecting = f._wire.WaitForConnectionAsync(f.Token);
            Assert.True(await f._pipe.EnsureConnectedAsync(f.Token));
            await connecting;
            var life = new HostAuthorityLifecycle((phase, generation, _) =>
                Task.FromResult<HostAuthorityLifecycle.Ack?>(new(true, phase, false, false, generation, 0)));
            Assert.True(await life.PendingAsync(enabled: false, newLoad: false, ct: f.Token));
            f._generation = life.Current.Generation;
            Assert.True(await life.GameplayAsync(f._generation, f.Token));
            f.Field("_transport").SetValue(f._bridge, f._game);
            f.Field("_combat").SetValue(f._bridge, f._pipe);
            f.Field("_p1Lifecycle").SetValue(f._bridge, life);
            f.Field("_where").SetValue(f._bridge, Enum.Parse(f.Field("_where").FieldType, "World"));
            f.Field("_resyncStream").SetValue(f._bridge, f._relay);
            f.Field("_dmgGuardIdentityAtUtc").SetValue(f._bridge, DateTime.UtcNow);
            f.Engaged[Npc] = (DateTime.UtcNow, Eid + 1);
            ((PeerSet)f.Field("_peers").GetValue(f._bridge)!).Connected(Peer, "partner");
            ((ConcurrentDictionary<string, uint>)f.Field("_npcEntityIds").GetValue(f._bridge)!)[Npc] = Eid;
            ((ConcurrentDictionary<string, (uint, Guid)>)f.Field("_copySoulGuid").GetValue(f._bridge)!)[Npc] = (Eid, f._soul);
            f.Method("P5PeerAtRead").Invoke(f._bridge, [(int)Protocol.Name, new byte[] { Peer }]);
            return f;
        }

        private ConcurrentDictionary<string, (DateTime At, uint Eid)> Engaged =>
            (ConcurrentDictionary<string, (DateTime, uint)>)Field("_w132Engaged").GetValue(_bridge)!;
        private ConcurrentDictionary<string, uint> Entities =>
            (ConcurrentDictionary<string, uint>)Field("_npcEntityIds").GetValue(_bridge)!;
        public SemaphoreSlim Writer =>
            (SemaphoreSlim)typeof(CombatPipe).GetField("_writeLock", PrivateInstance)!.GetValue(_pipe)!;
        public long Releases => (long)Field("_w195Releases").GetValue(_bridge)!;
        public uint EngagedEid => Engaged[Npc].Eid;
        public bool Dead => (bool)Method("Wo195IsDead").Invoke(_bridge, [Npc])!;

        public void RemoveBinding() => Entities.TryRemove(Npc, out _);

        public void Retire(string reason)
        {
            if (reason == "load") Method("ResetNpcDeathDispatch").Invoke(_bridge, null);
            else if (reason == "eid-reuse") Entities[Npc] = Eid + 1;
            else if (reason == "soul-reuse")
                ((ConcurrentDictionary<string, (uint, Guid)>)Field("_copySoulGuid").GetValue(_bridge)!)[Npc] = (Eid, Guid.NewGuid());
            else
            {
                Method("P5PeerAtRead").Invoke(_bridge, [(int)Protocol.Disconnect, new byte[] { Peer }]);
                if (reason == "peer-reuse")
                    Method("P5PeerAtRead").Invoke(_bridge, [(int)Protocol.Name, new byte[] { Peer }]);
            }
        }

        public Task<CombatPipe.NpcDeathReceipt> ObserveWithExpiredBudgetAsync()
        {
            var attempts = (System.Collections.IDictionary)Field("_npcDeathAttempts").GetValue(_bridge)!;
            return (Task<CombatPipe.NpcDeathReceipt>)Method("ObserveNpcDeathAsync").Invoke(_bridge,
                [attempts[_soul], new CancellationToken(canceled: true), CombatPipe.NpcDeathIntent.Advance])!;
        }

        public Task Ingress(string route)
        {
            if (route == "named")
                return (Task)Method("ApplyRemoteNpcDeathAsync").Invoke(_bridge, [Npc, _soul, Peer, "0x27 dead transition", Token, false, 0L])!;
            int kind;
            byte[] payload;
            if (route == "named-reader")
            {
                kind = Protocol.NpcStateDown;
                byte[] name = Encoding.UTF8.GetBytes(Npc);
                payload = new byte[2 + name.Length + Protocol.NpcStateFixedTail];
                payload[1] = (byte)name.Length;
                name.CopyTo(payload, 2);
                payload[2 + name.Length + Protocol.NpcStateFlagsOffset] = Protocol.NpcStateFlagDead;
                ((ConcurrentDictionary<string, bool>)Field("_npcLastDead").GetValue(_bridge)!)[Npc] = false;
            }
            else if (route is "fatal" or "named-damage")
            {
                kind = Protocol.NpcDamageDown;
                byte[] name = Encoding.UTF8.GetBytes(Npc);
                payload = new byte[2 + name.Length + Protocol.NpcDamageFixedTail];
                payload[1] = (byte)name.Length;
                name.CopyTo(payload, 2);
                BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(2 + name.Length), 7f);
                BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(6 + name.Length), 95f);
                if (route == "fatal") payload[^1] = Protocol.NpcDamageFlagFatal | Protocol.NpcDamageFlagJudgeThere;
            }
            else if (route == "guid-damage")
            {
                kind = Protocol.DamageDown;
                payload = new byte[Protocol.DamageDownPayloadLen];
                _soul.TryWriteBytes(payload.AsSpan(1));
                BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(17), 7f);
                BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(21), 95f);
            }
            else
            {
                kind = Protocol.DeathDown;
                payload = new byte[Protocol.DeathDownPayloadLen];
                _soul.TryWriteBytes(payload.AsSpan(1));
            }
            payload[0] = Peer;
            uint stamp = (uint)Method("Wo206StampAtRead").Invoke(_bridge, [payload])!;
            var frameType = typeof(GameBridge).GetNestedType("InFrame", BindingFlags.NonPublic)!;
            object frame = Activator.CreateInstance(frameType, kind, payload, Stopwatch.GetTimestamp(), stamp)!;
            long peer = (long)Method("NpcDeathPeerAtRead").Invoke(_bridge, [kind, payload])!;
            frameType.GetProperty("NpcDeathPeerSerial")!.SetValue(frame, peer);
            return (Task)Method("ProcessFrameCoreAsync").Invoke(_bridge, [frame, Token, null])!;
        }

        public async Task<(byte Op, byte[] Body)> ReadRequestAsync()
        {
            byte[] header = new byte[3];
            await _wire.ReadExactlyAsync(header, Token);
            byte[] body = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1))];
            await _wire.ReadExactlyAsync(body, Token);
            return (header[0], body);
        }

        public void AssertBound((byte Op, byte[] Body) request, uint expectedEid)
        {
            Assert.Equal(0x02, request.Op);
            Assert.Equal(36, request.Body.Length);
            Assert.Equal(_soul, new Guid(request.Body.AsSpan(0, 16)));
            Assert.Equal(expectedEid, BinaryPrimitives.ReadUInt32LittleEndian(request.Body.AsSpan(16, 4)));
            Assert.Equal(_generation, BinaryPrimitives.ReadUInt64LittleEndian(request.Body.AsSpan(20, 8)));
            Assert.NotEqual(0UL, BinaryPrimitives.ReadUInt64LittleEndian(request.Body.AsSpan(28, 8)));
        }

        public async Task<(byte Op, byte[] Body)> ReadWhilePendingAsync(Task ingress)
        {
            Task<(byte Op, byte[] Body)> read = ReadRequestAsync();
            Assert.Same(read, await Task.WhenAny(read, ingress).WaitAsync(Token));
            return await read;
        }

        public void AssertFollowup((byte Op, byte[] Body) first, (byte Op, byte[] Body) next, byte intent)
        {
            Assert.Equal(first.Op, next.Op);
            Assert.Equal(37, next.Body.Length);
            Assert.Equal(first.Body, next.Body[..36]);
            Assert.Equal(intent, next.Body[^1]);
        }

        public ValueTask ReplyAsync(byte seq, byte reason) =>
            _wire.WriteAsync(new byte[] { 0x81, 3, 0, reason == 0 ? (byte)1 : (byte)0, seq, reason }, Token);

        public async Task CompleteFallCheckAsync(byte seq)
        {
            // A confirmed death schedules a read-only check for the same body.
            // Consume exactly that frame before requiring the pipe to stay idle.
            var check = await ReadRequestAsync();
            Assert.Equal(0x2B, check.Op);
            Assert.Equal(29, check.Body.Length);
            Assert.Equal(Protocol.Wo195OpCheck, check.Body[0]);
            Assert.Equal(Eid, BinaryPrimitives.ReadUInt32LittleEndian(check.Body.AsSpan(1, 4)));
            Assert.Equal(_soul, new Guid(check.Body.AsSpan(5, 16)));
            Assert.Equal(_generation, BinaryPrimitives.ReadUInt64LittleEndian(check.Body.AsSpan(21, 8)));
            await _wire.WriteAsync(new byte[] { 0xAC, 8, 0, 1, seq, Protocol.Wo195OpCheck, 0, 1, 0, 0, 0 }, Token);
            FieldInfo fallDown = Field("_w195FallDown");
            while ((long)fallDown.GetValue(_bridge)! == 0) await Task.Delay(10, Token);
        }

        public async Task WaitForDeadAsync()
        {
            while (!Dead) await Task.Delay(10, Token);
        }

        public async Task AssertCompletedWithoutWriteAsync(Task ingress)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(Token);
            Task read = _wire.ReadExactlyAsync(new byte[1], wait.Token).AsTask();
            Task first = await Task.WhenAny(ingress, read).WaitAsync(Token);
            Assert.Same(ingress, first);
            await ingress;
            await wait.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await read);
        }

        public async Task AssertNoWriteAsync()
        {
            using var quiet = new CancellationTokenSource();
            using var read = CancellationTokenSource.CreateLinkedTokenSource(Token, quiet.Token);
            Task pending = _wire.ReadExactlyAsync(new byte[1], read.Token).AsTask();
            quiet.CancelAfter(PipeQuietWindow);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            if (Token.IsCancellationRequested)
                throw new TimeoutException("NPC death fixture deadline elapsed during the pipe idle check.");
            Assert.True(quiet.IsCancellationRequested, "The pipe read must remain idle for the full observation window.");
        }

        public async ValueTask DisposeAsync()
        {
            await _deadline.CancelAsync();
            await _pipe.DisposeAsync();
            await _wire.DisposeAsync();
            await _relay.DisposeAsync();
            await _game.DisposeAsync();
            _deadline.Dispose();
        }

        private sealed class GameDouble : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        }

        private FieldInfo Field(string name) => typeof(GameBridge).GetField(name, PrivateInstance)!;
        private MethodInfo Method(string name) => typeof(GameBridge).GetMethod(name, PrivateInstance)!;
    }
}
