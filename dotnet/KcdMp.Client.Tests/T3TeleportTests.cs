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
public class T3TeleportTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static void Set(object b, string field, object value) => b.GetType().GetField(field, Private)!.SetValue(b, value);
    private static T Get<T>(object b, string field) => (T)b.GetType().GetField(field, Private)!.GetValue(b)!;
    private static object Call(object b, string method, params object?[] args) => b.GetType().GetMethod(method, Private)!.Invoke(b, args)!;
    private static async Task<object?> Result(object task)
    { await ((Task)task).WaitAsync(TimeSpan.FromSeconds(4)); return task.GetType().GetProperty("Result")!.GetValue(task); }
    private static async Task Wait(Func<bool> predicate)
    { using var timeout = new CancellationTokenSource(3000); while (!predicate()) await Task.Delay(5, timeout.Token); }
    private static byte[] Wire(byte type, params byte[] payload) => [type, (byte)payload.Length, (byte)(payload.Length >> 8), .. payload];
    private static byte[] Down(byte kind, string text, byte peer = 1)
    { var p = new LootMsg(kind, 0, text).BuildUp(Protocol.W203Up, 0); return Wire(Protocol.W203Down, [peer, .. p[3..]]); }
    private static TeleportChallenge Challenge(byte[] framed)
    { Assert.True(LootMsg.TryDecode(framed.AsSpan(3 + Protocol.JoinHeaderLen), out var m)); Assert.Equal(Protocol.W203TpSnapshotRequest, m.Kind); Assert.True(TeleportSnapshotCodec.TryChallenge(m.Text, out var q)); return q; }
    private sealed class Game(Func<string, Task>? handler = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            if (handler is not null) await handler(Uri.UnescapeDataString(r.RequestUri!.Query));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        }
    }
    private sealed class Link : Stream
    {
        private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
        public readonly Channel<byte[]> Sent = Channel.CreateUnbounded<byte[]>();
        private byte[]? _current; private int _at; private long _readBytes;
        public long ReadBytes => Interlocked.Read(ref _readBytes);
        public void Feed(params byte[][] frames) { foreach (var f in frames) Assert.True(_incoming.Writer.TryWrite(f)); }
        public void End() => _incoming.Writer.TryComplete();
        public async Task<byte[]> Next() => await Sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        public override async ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken ct = default)
        {
            while (_current is null || _at == _current.Length)
            {
                if (!await _incoming.Reader.WaitToReadAsync(ct)) return 0;
                if (!_incoming.Reader.TryRead(out _current)) continue; _at = 0;
            }
            int n = Math.Min(b.Length, _current.Length - _at); _current.AsMemory(_at, n).CopyTo(b); _at += n;
            Interlocked.Add(ref _readBytes, n); return n;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> b, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); Sent.Writer.TryWrite(b.ToArray()); return ValueTask.CompletedTask; }
        public override Task WriteAsync(byte[] b, int o, int n, CancellationToken ct) => WriteAsync(b.AsMemory(o, n), ct).AsTask();
        public override Task<int> ReadAsync(byte[] b, int o, int n, CancellationToken ct) => ReadAsync(b.AsMemory(o, n), ct).AsTask();
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { } public override int Read(byte[] b, int o, int n) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int n) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long n) => throw new NotSupportedException();
    }
    private static async Task<GameBridge> Bridge(CombatPipe pipe, HttpGameTransport transport)
    {
        var b = new GameBridge(new ClientConfig()); Set(b, "_combat", pipe); Set(b, "_transport", transport);
        var where = b.GetType().GetField("_where", Private)!; where.SetValue(b, Enum.Parse(where.FieldType, "World"));
        Set(b, "_combatRoleApplied", true); Set(b, "_isDamageAuthority", true); Set(b, "_sharedWorld", true);
        Set(b, "_t3StatsNextMs", long.MaxValue); Set(b, "_p1GameplayWorld", 7u); Set(b, "_p1GameplayMs", Environment.TickCount64);
        var epoch = Get<Wo206Epochs>(b, "_w206"); epoch.LearnWorld(7, 1); epoch.NoteAck(1, 7);
        byte phase = 0; ulong gen = 10;
        var lifecycle = new HostAuthorityLifecycle((p, g, ct) =>
        {
            if (p != HostAuthorityLifecycle.Query) { phase = p; gen = g; }
            return Task.FromResult<HostAuthorityLifecycle.Ack?>(new(true, phase, false, false, gen, 0));
        });
        await lifecycle.PendingAsync(false, true, default); await lifecycle.GameplayAsync(lifecycle.Current.Generation, default);
        Set(b, "_p1Lifecycle", lifecycle); return b;
    }
    private static Task Receive(GameBridge b, Link wire, CancellationToken ct) => (Task)Call(b, "ReceiveLoopAsync", wire, ct);
    private static object Probe(GameBridge b, byte peer = 1) => Call(b, "T3TpPeerAsync", peer, TeleportSource.Join, 7u, CancellationToken.None);
    private static NamedPipeServerStream Server(string name) => new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    private static async Task<(byte Type, byte[] Body)> Read(Stream s)
    {
        using var timeout = new CancellationTokenSource(3000); var h = new byte[3]; await s.ReadExactlyAsync(h, timeout.Token);
        var b = new byte[BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(1))]; await s.ReadExactlyAsync(b, timeout.Token); return (h[0], b);
    }
    private static async Task Reply(Stream s, byte type, params byte[] b) { await s.WriteAsync(Wire(type, b)); await s.FlushAsync(); }
    private static byte[] NativeSnapshot(ulong generation, float x = 123, byte flags = 0)
    {
        byte[] b = new byte[32]; b[0] = 1; b[1] = 15; b[2] = flags;
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(4), x); BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(8), 45); BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(12), 67);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), generation); BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(24), (ulong)Environment.TickCount64); return b;
    }

    [Fact]
    public async Task Actual_reader_correlates_nonce_peer_world_and_keeps_request_start_as_the_deadline()
    {
        await using var pipe = new CombatPipe("unused-" + Guid.NewGuid()); await using var transport = new HttpGameTransport("http://localhost:1403", 2000, new Game());
        var b = await Bridge(pipe, transport); using var wire = new Link(); using var stop = new CancellationTokenSource(); Task rx = Receive(b, wire, stop.Token);
        try
        {
            long begin = Environment.TickCount64; object probe = Probe(b); var q = Challenge(await wire.Next());
            object shared = Probe(b); Assert.False(wire.Sent.Reader.TryRead(out _)); // one challenge for both callers
            wire.Feed(Down(Protocol.W203TpSnapshotResponse, TeleportSnapshotCodec.Build(new TeleportAnswer(q.Nonce + 1, 7, true, 1, 2, 3, 15, 0))),
                Down(Protocol.W203TpSnapshotResponse, TeleportSnapshotCodec.Build(new TeleportAnswer(q.Nonce, 7, true, 1, 2, 3, 15, 0)), 2),
                Down(Protocol.W203TpSnapshotResponse, TeleportSnapshotCodec.Build(new TeleportAnswer(q.Nonce, 8, true, 1, 2, 3, 15, 0))));
            await Task.Delay(50); Assert.False(((Task)probe).IsCompleted);
            wire.Feed(Down(Protocol.W203TpSnapshotResponse, TeleportSnapshotCodec.Build(new TeleportAnswer(q.Nonce, 7, true, 123, 2, 3, 15, 0))));
            object target = (await Result(probe))!; Assert.Same(target, await Result(shared));
            var proof = (TeleportProof)Call(b, "T3TpProof", target, TeleportSource.Join);
            Assert.InRange(proof.DeadlineTick64, begin + 2000, begin + 2100); Assert.True(proof.Valid);
            await (Task)Call(b, "OnLocalDownedAsync", true, (byte)0); await (Task)Call(b, "OnLocalDownedAsync", false, (byte)0);
            Assert.False(proof.Valid); // recovery never recertifies an old life
        }
        finally { wire.End(); await rx.WaitAsync(TimeSpan.FromSeconds(3)); stop.Cancel(); }
    }

    [Theory]
    [InlineData("life")]
    [InlineData("world")]
    [InlineData("eof")]
    public async Task Pending_old_life_world_or_connection_reply_cannot_certify_a_target(string change)
    {
        await using var pipe = new CombatPipe("unused-" + Guid.NewGuid()); await using var transport = new HttpGameTransport("http://localhost:1403", 2000, new Game());
        var b = await Bridge(pipe, transport); using var wire = new Link(); using var stop = new CancellationTokenSource(); Task rx = Receive(b, wire, stop.Token);
        try
        {
            object probe = Probe(b); var q = Challenge(await wire.Next());
            if (change == "life") { await (Task)Call(b, "OnLocalDownedAsync", true, (byte)0); await (Task)Call(b, "OnLocalDownedAsync", false, (byte)0); }
            else if (change == "world") Get<Wo206Epochs>(b, "_w206").LearnWorld(8, 2);
            else wire.End();
            if (change != "eof") wire.Feed(Down(Protocol.W203TpSnapshotResponse, TeleportSnapshotCodec.Build(new TeleportAnswer(q.Nonce, 7, true, 1, 2, 3, 15, 0))));
            Assert.Null(await Result(probe));
        }
        finally { wire.End(); await rx.WaitAsync(TimeSpan.FromSeconds(3)); stop.Cancel(); }
    }

    [Fact]
    public async Task Old_timed_out_nonce_cannot_refresh_the_next_request()
    {
        await using var pipe = new CombatPipe("unused-" + Guid.NewGuid()); await using var transport = new HttpGameTransport("http://localhost:1403", 2000, new Game());
        var b = await Bridge(pipe, transport); using var wire = new Link(); using var stop = new CancellationTokenSource(); Task rx = Receive(b, wire, stop.Token);
        try
        {
            object a = Probe(b); var qa = Challenge(await wire.Next()); Assert.Null(await Result(a));
            object next = Probe(b); var qb = Challenge(await wire.Next()); Assert.NotEqual(qa.Nonce, qb.Nonce);
            wire.Feed(Down(Protocol.W203TpSnapshotResponse, TeleportSnapshotCodec.Build(new TeleportAnswer(qa.Nonce, 7, true, 9, 9, 9, 15, 0))));
            await Task.Delay(30); Assert.False(((Task)next).IsCompleted);
            wire.Feed(Down(Protocol.W203TpSnapshotResponse, TeleportSnapshotCodec.Build(new TeleportAnswer(qb.Nonce, 7, true, 1, 2, 3, 15, 0))));
            Assert.NotNull(await Result(next));
        }
        finally { wire.End(); await rx.WaitAsync(TimeSpan.FromSeconds(3)); stop.Cancel(); }
    }

    [Fact]
    public async Task Reader_serves_a_new_native_snapshot_and_bounds_a_second_request_while_native_waits()
    {
        string name = "p4-" + Guid.NewGuid().ToString("N"); using var server = Server(name); var accept = server.WaitForConnectionAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pipe = new CombatPipe(name);
        await using var transport = new HttpGameTransport("http://localhost:1403", 10000, new Game(async command =>
        { if (command.Contains("KCD2MP_SetGhostHorse")) { entered.TrySetResult(); await release.Task; } })) { BatchingEnabled = false };
        var b = await Bridge(pipe, transport); using var wire = new Link(); using var stop = new CancellationTokenSource(); Task rx = Receive(b, wire, stop.Token);
        try
        {
            wire.Feed(Wire(Protocol.HorseInfoDown, 1, 0)); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            wire.Feed(Down(Protocol.W203TpSnapshotRequest, TeleportSnapshotCodec.Build(new TeleportChallenge(81, 7, TeleportSource.Join))));
            await accept.WaitAsync(TimeSpan.FromSeconds(3)); var request = await Read(server);
            Assert.Equal(0x2B, request.Type); Assert.Equal(9, request.Body.Length); Assert.Equal(251, request.Body[0]);
            ulong generation = BinaryPrimitives.ReadUInt64LittleEndian(request.Body.AsSpan(1));
            wire.Feed(Down(Protocol.W203TpSnapshotRequest, TeleportSnapshotCodec.Build(new TeleportChallenge(82, 7, TeleportSource.Join))));
            long before = wire.ReadBytes; wire.Feed(Wire(Protocol.Ping));
            await Wait(() => wire.ReadBytes > before); // reader was never held behind Native await
            Assert.Single(Get<HashSet<byte>>(b, "_t3TpResponding"));
            await Reply(server, 0xAC, [1, 0, 251, 0, .. NativeSnapshot(generation)]);
            var response = await wire.Next(); Assert.True(LootMsg.TryDecode(response.AsSpan(3 + Protocol.JoinHeaderLen), out var m));
            Assert.Equal(Protocol.W203TpSnapshotResponse, m.Kind); Assert.True(TeleportSnapshotCodec.TryAnswer(m.Text, out var answer));
            Assert.False(release.Task.IsCompleted); Assert.Equal(81ul, answer.Nonce); Assert.Equal(123, answer.X); Assert.True(answer.Ok);
        }
        finally { release.TrySetResult(); wire.End(); await rx.WaitAsync(TimeSpan.FromSeconds(3)); stop.Cancel(); }
    }

    [Theory]
    [InlineData("_gate", false)]
    [InlineData("_writeLock", false)]
    [InlineData("_writeLock", true)]
    public async Task Gate_or_writer_wait_cannot_send_expired_or_changed_generation_and_pipe_still_works(string lockName, bool supersede)
    {
        string name = "p4-" + Guid.NewGuid().ToString("N"); using var server = Server(name); var accept = server.WaitForConnectionAsync();
        await using var pipe = new CombatPipe(name); Assert.True(await pipe.EnsureConnectedAsync()); await accept;
        var hold = Get<SemaphoreSlim>(pipe, lockName); await hold.WaitAsync();
        bool current = true; var proof = new TeleportProof(TeleportSource.Join, Environment.TickCount64 + (supersede ? 2000 : 100), 42, () => current);
        var tp = pipe.JoinPlaceAsync(1, 2, 3, 3, proof);
        await Task.Delay(160); if (supersede) current = false; hold.Release();
        Assert.Null(await tp);
        var normal = pipe.Wo163Async(164, []); var frame = await Read(server);
        Assert.Equal(0x2B, frame.Type); Assert.Equal(164, frame.Body[0]); // no stale TP bytes were written
        await Reply(server, 0xAC, 1, 0, 164, 0); Assert.True((await normal)!.Value.Ok);
        Assert.True(pipe.IsConnected);
    }

    [Fact]
    public async Task Partner_heartbeat_keeps_original_deadline_and_generation_in_the_actual_pipe_payload()
    {
        string name = "p4-" + Guid.NewGuid().ToString("N"); using var server = Server(name); var accept = server.WaitForConnectionAsync();
        await using var pipe = new CombatPipe(name); Assert.True(await pipe.EnsureConnectedAsync()); await accept;
        var proof = new TeleportProof(TeleportSource.Respawn, Environment.TickCount64 + 2000, 42, () => true);
        for (byte i = 0; i < 2; i++)
        {
            var call = pipe.SetPartnerAsync(true, 1, 2, 3, 30, proof); var frame = await Read(server);
            Assert.Equal(0x20, frame.Type); Assert.Equal(34, frame.Body.Length); Assert.Equal(2, frame.Body[17]);
            Assert.Equal((ulong)proof.DeadlineTick64, BinaryPrimitives.ReadUInt64LittleEndian(frame.Body.AsSpan(18)));
            Assert.Equal(42ul, BinaryPrimitives.ReadUInt64LittleEndian(frame.Body.AsSpan(26)));
            await Reply(server, 0x81, 1, i, 0); Assert.True((await call).Ok);
        }
    }

    [Fact]
    public async Task Actual_downed_wake_edge_revokes_a_TP_already_waiting_at_the_physical_writer()
    {
        string name = "p4-" + Guid.NewGuid().ToString("N"); using var server = Server(name); var accept = server.WaitForConnectionAsync();
        await using var pipe = new CombatPipe(name); Assert.True(await pipe.EnsureConnectedAsync()); await accept;
        await using var transport = new HttpGameTransport("http://localhost:1403", 2000, new Game());
        var b = await Bridge(pipe, transport); using var wire = new Link(); using var stop = new CancellationTokenSource(); Task rx = Receive(b, wire, stop.Token);
        var writer = Get<SemaphoreSlim>(pipe, "_writeLock");
        try
        {
            object probe = Probe(b); var q = Challenge(await wire.Next());
            wire.Feed(Down(Protocol.W203TpSnapshotResponse, TeleportSnapshotCodec.Build(new TeleportAnswer(q.Nonce, 7, true, 1, 2, 3, 15, 0))));
            object target = (await Result(probe))!;
            await writer.WaitAsync();
            object placing = Call(b, "T3TpPlaceAsync", target, 3f, TeleportSource.Join, CancellationToken.None);
            await Task.Delay(30);
            await (Task)Call(b, "OnLocalDownedAsync", true, (byte)0); await (Task)Call(b, "OnLocalDownedAsync", false, (byte)0);
            Assert.Null(await Result(placing)); writer.Release();
            var normal = pipe.Wo163Async(164, []); var first = await Read(server);
            Assert.Equal(0x2B, first.Type); Assert.Equal(164, first.Body[0]);
            await Reply(server, 0xAC, 1, 0, 164, 0); Assert.True((await normal)!.Value.Ok);
        }
        finally { if (writer.CurrentCount == 0) writer.Release(); wire.End(); await rx.WaitAsync(TimeSpan.FromSeconds(3)); stop.Cancel(); }
    }

    [Fact]
    public async Task Replaced_scene_is_checked_again_after_the_physical_writer_wait()
    {
        string name = "p4-" + Guid.NewGuid().ToString("N"); using var server = Server(name); var accept = server.WaitForConnectionAsync();
        await using var pipe = new CombatPipe(name); Assert.True(await pipe.EnsureConnectedAsync()); await accept;
        await using var transport = new HttpGameTransport("http://localhost:1403", 2000, new Game());
        var b = await Bridge(pipe, transport); using var wire = new Link(); using var stop = new CancellationTokenSource(); Task rx = Receive(b, wire, stop.Token);
        var writer = Get<SemaphoreSlim>(pipe, "_writeLock");
        try
        {
            object probe = Probe(b); var q = Challenge(await wire.Next());
            wire.Feed(Down(Protocol.W203TpSnapshotResponse, TeleportSnapshotCodec.Build(new TeleportAnswer(q.Nonce, 7, true, 1, 2, 3, 15, 0))));
            object target = (await Result(probe))!;
            var sceneType = typeof(GameBridge).GetNestedType("W179Remote", BindingFlags.NonPublic)!;
            object scene = Activator.CreateInstance(sceneType, true)!; sceneType.GetField("Src")!.SetValue(scene, (byte)1);
            object scenes = Get<object>(b, "_w179Remote"); var item = scenes.GetType().GetProperty("Item")!;
            item.SetValue(scenes, scene, [(byte)1]);
            Func<bool> current = () => (bool)Call(b, "Wo179LandingCurrent", scene, 0L);
            Assert.True(current()); target.GetType().GetProperty("CurrentOperation")!.SetValue(target, current);
            await writer.WaitAsync(); object placing = Call(b, "T3TpPlaceAsync", target, 3f, TeleportSource.VideoLanding, CancellationToken.None);
            await Task.Delay(30); item.SetValue(scenes, Activator.CreateInstance(sceneType, true), [(byte)1]); Assert.False(current());
            writer.Release(); Assert.Null(await Result(placing));
            var normal = pipe.Wo163Async(164, []); var first = await Read(server); Assert.Equal(164, first.Body[0]);
            await Reply(server, 0xAC, 1, 0, 164, 0); Assert.True((await normal)!.Value.Ok);
        }
        finally { if (writer.CurrentCount == 0) writer.Release(); wire.End(); await rx.WaitAsync(TimeSpan.FromSeconds(3)); stop.Cancel(); }
    }

    [Fact]
    public async Task Scene_nudge_started_before_a_life_edge_cannot_rebind_itself_after_its_delay()
    {
        string name = "p4-" + Guid.NewGuid().ToString("N"); using var server = Server(name); var accept = server.WaitForConnectionAsync();
        await using var pipe = new CombatPipe(name); Assert.True(await pipe.EnsureConnectedAsync()); await accept;
        await using var transport = new HttpGameTransport("http://localhost:1403", 2000, new Game());
        var b = await Bridge(pipe, transport); using var wire = new Link(); using var stop = new CancellationTokenSource(); Task rx = Receive(b, wire, stop.Token);
        try
        {
            object scene = Activator.CreateInstance(typeof(GameBridge).GetNestedType("W194Own", BindingFlags.NonPublic)!, true)!;
            Set(b, "_w194Own", scene);
            var nudge = (Task)Call(b, "Wo194NudgeAsync", scene);
            await (Task)Call(b, "OnLocalDownedAsync", true, (byte)0); await (Task)Call(b, "OnLocalDownedAsync", false, (byte)0);
            await nudge.WaitAsync(TimeSpan.FromSeconds(1));
            var normal = pipe.Wo163Async(164, []); var first = await Read(server);
            Assert.Equal(164, first.Body[0]); // neither a new-world snapshot nor a late TP was issued by the old scene
            await Reply(server, 0xAC, 1, 0, 164, 0); Assert.True((await normal)!.Value.Ok);
        }
        finally { wire.End(); await rx.WaitAsync(TimeSpan.FromSeconds(3)); stop.Cancel(); }
    }

    [Fact]
    public async Task Finished_scene_can_nudge_after_its_normal_release_without_authorizing_a_new_scene()
    {
        string name = "p4-" + Guid.NewGuid().ToString("N"); using var server = Server(name); var accept = server.WaitForConnectionAsync();
        await using var pipe = new CombatPipe(name); Assert.True(await pipe.EnsureConnectedAsync()); await accept;
        await using var transport = new HttpGameTransport("http://localhost:1403", 2000, new Game());
        var b = await Bridge(pipe, transport); using var wire = new Link(); using var stop = new CancellationTokenSource(); Task rx = Receive(b, wire, stop.Token);
        try
        {
            object scene = Activator.CreateInstance(typeof(GameBridge).GetNestedType("W194Own", BindingFlags.NonPublic)!, true)!;
            Set(b, "_w194Own", scene); Set(b, "_w179HavePos", true); Set(b, "_w179Z", 67f);
            var nudge = (Task)Call(b, "Wo194NudgeAsync", scene);
            Set(b, "_w194Own", null!); // normal ReleaseScene clears the object, not its generation
            var snapshot = await Read(server); Assert.Equal(251, snapshot.Body[0]);
            ulong gen = BinaryPrimitives.ReadUInt64LittleEndian(snapshot.Body.AsSpan(1));
            await Reply(server, 0xAC, [1, 0, 251, 0, .. NativeSnapshot(gen)]);
            var tp = await Read(server); Assert.Equal(0x1C, tp.Type); Assert.Equal(33, tp.Body.Length);
            Assert.Equal((byte)TeleportSource.SceneNudge, tp.Body[16]);
            byte[] reply = new byte[44]; reply[0] = 1; reply[1] = 1; reply[2] = 1; reply[3] = 1;
            BinaryPrimitives.WriteSingleLittleEndian(reply.AsSpan(36), 67);
            await Reply(server, 0x8C, reply); await nudge.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { wire.End(); await rx.WaitAsync(TimeSpan.FromSeconds(3)); stop.Cancel(); }
    }

    [Fact]
    public async Task Host_native_restart_revokes_old_readiness_until_an_actual_new_Gameplay_edge()
    {
        await using var pipe = new CombatPipe("unused-" + Guid.NewGuid()); await using var transport = new HttpGameTransport("http://localhost:1403", 2000, new Game());
        var b = await Bridge(pipe, transport); using var wire = new Link(); using var stop = new CancellationTokenSource(); Task rx = Receive(b, wire, stop.Token);
        ulong nativeGeneration = 20; byte phase = 0; bool enabled = false;
        var lifecycle = new HostAuthorityLifecycle((p, g, ct) =>
        {
            if (p != HostAuthorityLifecycle.Query) { phase = p; nativeGeneration = g; if (p != HostAuthorityLifecycle.Gameplay) enabled = p == HostAuthorityLifecycle.Load; }
            return Task.FromResult<HostAuthorityLifecycle.Ack?>(new(true, phase, enabled, false, nativeGeneration, 0));
        });
        await lifecycle.PendingAsync(false, true, default); await lifecycle.GameplayAsync(lifecycle.Current.Generation, default);
        Set(b, "_p1Lifecycle", lifecycle); ulong old = lifecycle.Current.Generation;
        try
        {
            nativeGeneration = 0; phase = HostAuthorityLifecycle.Load; enabled = true; // new DLL defaults, old C# state remains
            Assert.False((bool)(await Result(Call(b, "T3TpPrepareAsync", CancellationToken.None)))!);
            Assert.Equal(-1, Get<long>(b, "_p1GameplayMs"));
            Assert.False((bool)(await Result(Call(b, "T3TpPrepareAsync", CancellationToken.None)))!);
            Call(b, "P1OnGameplayStarted"); // the engine's actual edge, not a timeout or role change
            Assert.True((bool)(await Result(Call(b, "T3TpPrepareAsync", CancellationToken.None)))!);
            Assert.True(lifecycle.Current.Generation > old); Assert.True(lifecycle.Current.GameplaySent);
            Assert.False(lifecycle.Current.Enabled); Assert.False(lifecycle.Current.Active);
        }
        finally { wire.End(); await rx.WaitAsync(TimeSpan.FromSeconds(3)); stop.Cancel(); }
    }

    [Theory]
    [InlineData("1|1|0|4")]
    [InlineData("1|0|7|4")]
    [InlineData("1|1|7|0")]
    [InlineData("1|1|7|9")]
    public void Unknown_source_world_or_nonce_is_not_a_challenge(string text) => Assert.False(TeleportSnapshotCodec.TryChallenge(text, out _));
}
