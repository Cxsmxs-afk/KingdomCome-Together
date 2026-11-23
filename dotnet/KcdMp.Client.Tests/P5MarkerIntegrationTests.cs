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
public class P5MarkerIntegrationTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static void Set(object b, string f, object? v) => b.GetType().GetField(f, Private)!.SetValue(b, v);
    private static T Get<T>(object b, string f) => (T)b.GetType().GetField(f, Private)!.GetValue(b)!;
    private static object Call(object b, string m, params object?[] a) => b.GetType().GetMethod(m, Private)!.Invoke(b, a)!;
    private static async Task<T?> Result<T>(object task) { await ((Task)task).WaitAsync(TimeSpan.FromSeconds(4)); return (T?)task.GetType().GetProperty("Result")!.GetValue(task); }
    private static byte[] Wire(byte type, params byte[] b) => [type, (byte)b.Length, (byte)(b.Length >> 8), .. b];
    private static async Task<(byte Type, byte[] Body)> Read(Stream s)
    { using var stop = new CancellationTokenSource(4000); byte[] h = new byte[3]; await s.ReadExactlyAsync(h, stop.Token); byte[] b = new byte[BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(1))]; await s.ReadExactlyAsync(b, stop.Token); return (h[0], b); }
    private static async Task Reply(Stream s, byte op, byte seq, byte[] payload, bool ok = true) { await s.WriteAsync(Wire(0xAC, [(byte)(ok ? 1 : 0), seq, op, 0, .. payload])); await s.FlushAsync(); }
    private static NamedPipeServerStream Server(string name) => new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    private sealed class Game : HttpMessageHandler
    {
        public readonly ConcurrentQueue<string> Commands = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        { Commands.Enqueue(Uri.UnescapeDataString(r.RequestUri!.Query)); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") }); }
    }
    private sealed class Link : Stream
    {
        private readonly Channel<byte[]> _in = Channel.CreateUnbounded<byte[]>();
        public readonly Channel<byte[]> Sent = Channel.CreateUnbounded<byte[]>();
        private byte[]? _buffer; private int _at; private long _read;
        public long ReadBytes => Interlocked.Read(ref _read);
        public void Feed(byte[] f) => _in.Writer.TryWrite(f); public void End() => _in.Writer.TryComplete();
        public Task<byte[]> Next() => Sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        public override async ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken ct = default)
        {
            while (_buffer is null || _at == _buffer.Length) { if (!await _in.Reader.WaitToReadAsync(ct)) return 0; if (!_in.Reader.TryRead(out _buffer)) continue; _at = 0; }
            int n = Math.Min(b.Length, _buffer.Length - _at); _buffer.AsMemory(_at, n).CopyTo(b); _at += n; Interlocked.Add(ref _read, n); return n;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> b, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); Sent.Writer.TryWrite(b.ToArray()); return ValueTask.CompletedTask; }
        public override Task WriteAsync(byte[] b, int o, int n, CancellationToken ct) => WriteAsync(b.AsMemory(o, n), ct).AsTask();
        public override Task<int> ReadAsync(byte[] b, int o, int n, CancellationToken ct) => ReadAsync(b.AsMemory(o, n), ct).AsTask();
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask; public override void Flush() { }
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => 0; public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] b, int o, int n) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int n) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long n) => throw new NotSupportedException();
    }
    private static async Task<GameBridge> Bridge(CombatPipe pipe, HttpGameTransport http, Link stream, CancellationToken ct, bool joined = true)
    {
        var b = new GameBridge(new ClientConfig()); Set(b, "_combat", pipe); Set(b, "_transport", http); Set(b, "_wo122Stream", stream); Set(b, "_wo122Ct", ct);
        var where = b.GetType().GetField("_where", Private)!; where.SetValue(b, Enum.Parse(where.FieldType, "World"));
        Set(b, "_myGhostId", (byte)1); Set(b, "_combatRoleApplied", true); Set(b, "_isDamageAuthority", false); Set(b, "_sharedWorld", true); Set(b, "_joinedWorld", joined);
        Set(b, "_t3JoinedHostWorld", joined ? 7u : 0u); Set(b, "_t3StatsNextMs", long.MaxValue); Set(b, "_p1GameplayWorld", 7u); Set(b, "_p1GameplayMs", Environment.TickCount64);
        Get<Wo206Epochs>(b, "_w206").LearnWorld(7, 1); Get<Wo206Epochs>(b, "_w206").NoteAck(1, 7);
        byte phase = 0; ulong gen = 10; bool enabled = false;
        var lifecycle = new HostAuthorityLifecycle((p, g, _) => { if (p != HostAuthorityLifecycle.Query) { phase = p; gen = g; if (p != HostAuthorityLifecycle.Gameplay) enabled = p == HostAuthorityLifecycle.Load; } return Task.FromResult<HostAuthorityLifecycle.Ack?>(new(true, phase, enabled, false, gen, 0)); });
        await lifecycle.PendingAsync(false, true, ct); await lifecycle.GameplayAsync(lifecycle.Current.Generation, ct); Set(b, "_p1Lifecycle", lifecycle);
        Set(b, "_p5ScopeGeneration", lifecycle.Current.Generation); Set(b, "_p5ScopeWorld", joined ? 7u : 0u); return b;
    }
    private static byte[] Ack(byte action, byte status, ulong gen, uint world)
    { byte[] b = new byte[16]; b[0] = 1; b[1] = action; b[2] = status; BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(4), gen); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), world); return b; }
    private static byte[] Page(ulong gen, uint world, uint token, ushort cursor, IReadOnlyList<DeathMarkerFact> rows, bool done)
    {
        byte[] p = new byte[21 + rows.Count * 29]; p[0] = 1; BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(1), gen); BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(9), world);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(13), token); BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(17), (ushort)(cursor + rows.Count)); p[19] = (byte)rows.Count; p[20] = done ? (byte)1 : (byte)0;
        for (int i = 0; i < rows.Count; i++) { var f = rows[i]; int o = 21 + i * 29; p[o] = (byte)f.Kind; BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(o + 1), f.Id); BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(o + 9), f.Revision); BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(o + 17), f.X); BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(o + 21), f.Y); BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(o + 25), f.Z); }
        return p;
    }
    private static DeathMarkerFact Fact(ulong id, DeathMarkerKind kind = DeathMarkerKind.MarkerOnly, ulong revision = 1) => new(DeathMarkerAction.Add, kind, 7, id, revision, 1, 2, 3);

    [Fact]
    public async Task Actual_pipe_paging_returns_all_128_rows_and_preserves_kinds_and_revisions()
    {
        string name = "p5-" + Guid.NewGuid().ToString("N"); using var server = Server(name); var accept = server.WaitForConnectionAsync(); await using var pipe = new CombatPipe(name);
        var list = pipe.ListDeathMarkersAsync(11, 7); await accept;
        for (ushort cursor = 0; cursor < 128; cursor += 16)
        {
            var req = await Read(server); Assert.Equal(252, req.Body[0]); Assert.Equal(20, req.Body.Length);
            Assert.Equal(cursor, BinaryPrimitives.ReadUInt16LittleEndian(req.Body.AsSpan(18)));
            Assert.Equal(cursor == 0 ? 0u : 19u, BinaryPrimitives.ReadUInt32LittleEndian(req.Body.AsSpan(14)));
            var rows = Enumerable.Range(cursor + 1, 16).Select(i => Fact((ulong)i, i <= 64 ? DeathMarkerKind.Grave : DeathMarkerKind.MarkerOnly, (ulong)i * 2)).ToArray();
            byte[] page = Page(11, 7, 19, cursor, rows, cursor == 112); Assert.Equal(485, page.Length);
            await Reply(server, 252, (byte)(cursor / 16), page);
        }
        var result = await list; Assert.Equal(128, result!.Count); Assert.Equal(DeathMarkerKind.MarkerOnly, result[127].Kind); Assert.Equal(256ul, result[127].Revision);
    }

    [Theory]
    [InlineData("token")]
    [InlineData("cursor")]
    [InlineData("generation")]
    [InlineData("world")]
    public async Task A_changed_page_receipt_never_returns_a_partial_snapshot(string error)
    {
        string name = "p5-" + Guid.NewGuid().ToString("N"); using var server = Server(name); var accept = server.WaitForConnectionAsync(); await using var pipe = new CombatPipe(name);
        var list = pipe.ListDeathMarkersAsync(11, 7); await accept; await Read(server); await Reply(server, 252, 0, Page(11, 7, 19, 0, [Fact(1)], false));
        await Read(server); await Reply(server, 252, 1, Page(error == "generation" ? 12ul : 11ul, error == "world" ? 8u : 7u, error == "token" ? 20u : 19u,
            error == "cursor" ? (ushort)2 : (ushort)1, [Fact(2)], true));
        Assert.Null(await list);
    }

    [Theory]
    [InlineData((byte)3, "join-command")]
    [InlineData((byte)1, "join-command")]
    [InlineData((byte)3, "leave-command")]
    [InlineData((byte)3, "exit-to-menu")]
    public async Task Unsafe_or_unready_main_detach_prevents_the_real_load_HTTP_call_and_explains_DE_EN(byte status, string purpose)
    {
        string name = "p5-" + Guid.NewGuid().ToString("N"); using var server = Server(name); var accept = server.WaitForConnectionAsync(); await using var pipe = new CombatPipe(name);
        var game = new Game(); await using var http = new HttpGameTransport("http://localhost:1403", 2000, game); using var link = new Link(); using var stop = new CancellationTokenSource();
        var bridge = await Bridge(pipe, http, link, stop.Token);
        var loading = Result<bool>(Call(bridge, "P5LoadGameAsync", 1, "safe", purpose, stop.Token)); await accept; var req = await Read(server);
        byte sequence = 0;
        if (req.Body[0] == 160) { await Reply(server, 160, sequence++, []); req = await Read(server); }
        Assert.Equal(253, req.Body[0]); Assert.Equal(45, req.Body.Length); Assert.Equal(4, req.Body[2]); ulong gen = BinaryPrimitives.ReadUInt64LittleEndian(req.Body.AsSpan(5));
        await Reply(server, 253, sequence, Ack(4, status, gen, 7), false); Assert.False(await loading);
        Assert.DoesNotContain(game.Commands, x => x.Contains("KCD2MP_Wo124LoadGame"));
        Assert.False(Get<bool>(bridge, "_ownLoadExpected")); Assert.False(Get<bool>(bridge, "_loadGraceOwn"));
        Assert.False(Get<HostAuthorityLifecycle>(bridge, "_p1Lifecycle").Current.GameplaySent); // no invented recovery edge
        Assert.Contains("Laden angehalten", Get<string>(bridge, "_joinUiMessage")); Assert.Contains("Loading paused", Get<string>(bridge, "_joinUiMessage"));
        if (status == 3)
        {
            string message = Get<string>(bridge, "_joinUiMessage");
            Assert.Contains("Versuche die Aktion im Koop-Menü erneut", message);
            Assert.Contains("Retry the action in the co-op menu", message);
            Assert.Contains("If safe removal still fails, fully restart", message);
        }
    }

    [Fact]
    public async Task Main_verified_cleanup_hint_only_changes_detach_parameter_not_loaded_or_joined_receipt()
    {
        string name = "p5-" + Guid.NewGuid().ToString("N"); using var server = Server(name); var accept = server.WaitForConnectionAsync(); await using var pipe = new CombatPipe(name);
        var game = new Game(); await using var http = new HttpGameTransport("http://localhost:1403", 2000, game); using var link = new Link(); using var stop = new CancellationTokenSource();
        var bridge = await Bridge(pipe, http, link, stop.Token, joined: false);
        var loading = Result<bool>(Call(bridge, "P5LoadGameAsync", 1, "safe", "join-command", stop.Token)); await accept; var first = await Read(server);
        byte sequence = 0;
        if (first.Body[0] == 160) { await Reply(server, 160, sequence++, []); first = await Read(server); }
        Assert.Equal(253, first.Body[0]);
        ulong gen = BinaryPrimitives.ReadUInt64LittleEndian(first.Body.AsSpan(5)); Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(first.Body.AsSpan(13)));
        await Reply(server, 253, sequence++, Ack(4, 2, gen, 9), false);
        var retry = await Read(server); Assert.Equal(9u, BinaryPrimitives.ReadUInt32LittleEndian(retry.Body.AsSpan(13))); Assert.Equal(gen, BinaryPrimitives.ReadUInt64LittleEndian(retry.Body.AsSpan(5)));
        await Reply(server, 253, sequence, Ack(4, 0, gen, 9)); Assert.True(await loading);
        Assert.Equal(0u, Get<uint>(bridge, "_p5ScopeWorld")); Assert.Equal(0u, Get<uint>(bridge, "_t3JoinedHostWorld")); Assert.False(Get<bool>(bridge, "_joinedWorld"));
        Assert.False(await Result<bool>(Call(bridge, "P5EnsureScopeAsync", stop.Token)));
        Assert.Single(game.Commands.Where(c => c.Contains("KCD2MP_Wo124LoadGame")));
    }

    [Fact]
    public async Task Remove_tombstone_defeats_an_older_paged_heartbeat_but_same_world_reconnect_reannounces_live_marker()
    {
        string name = "p5-" + Guid.NewGuid().ToString("N"); using var server = Server(name); var accept = server.WaitForConnectionAsync(); await using var pipe = new CombatPipe(name);
        await using var http = new HttpGameTransport("http://localhost:1403", 2000, new Game()); using var link = new Link(); using var stop = new CancellationTokenSource();
        var b = await Bridge(pipe, http, link, stop.Token); ulong gen = Get<HostAuthorityLifecycle>(b, "_p1Lifecycle").Current.Generation;
        var announce = (Task)Call(b, "P5AnnounceMarkersAsync", link, stop.Token); await accept; await Read(server);
        var removed = Fact(1, revision: 2) with { Action = DeathMarkerAction.Remove };
        Get<DeathMarkerRevisions>(b, "_p5OwnRevisions").Observe(1, removed); // live Remove arrives while immutable native list is in flight
        await Reply(server, 252, 0, Page(gen, 7, 19, 0, [Fact(1), Fact(2)], true)); await announce;
        var packet = await link.Next(); Assert.True(DeathMarkerCodec.TryRead(packet.AsSpan(3), out var first)); Assert.Equal(2ul, first.Id); Assert.False(link.Sent.Reader.TryRead(out _));
        using var reconnected = new Link(); Set(b, "_wo122Stream", reconnected);
        var reconnect = (Task)Call(b, "P5AnnounceMarkersAsync", reconnected, stop.Token); await Read(server);
        await Reply(server, 252, 1, Page(gen, 7, 20, 0, [Fact(2)], true)); await reconnect;
        Assert.True(DeathMarkerCodec.TryRead((await reconnected.Next()).AsSpan(3), out var replay)); Assert.Equal(first, replay);
    }
    private static byte[] Down(DeathMarkerFact f, byte owner = 0) => Wire(Protocol.DeathMarkerDown, [owner, .. DeathMarkerCodec.BuildUp(f)[3..]]);
    private static async Task Wait(Func<bool> f) { using var stop = new CancellationTokenSource(3000); while (!f()) await Task.Delay(5, stop.Token); }

    [Fact]
    public async Task Actual_receive_uses_loaded_receipt_then_typed_mirror_and_ignores_old_add_after_remove()
    {
        string name = "p5-" + Guid.NewGuid().ToString("N"); using var server = Server(name); var accept = server.WaitForConnectionAsync(); await using var pipe = new CombatPipe(name);
        await using var http = new HttpGameTransport("http://localhost:1403", 2000, new Game()); using var link = new Link(); using var stop = new CancellationTokenSource();
        var b = await Bridge(pipe, http, link, stop.Token); Get<Wo206Epochs>(b, "_w206").LearnWorld(8, 2);
        Task rx = (Task)Call(b, "ReceiveLoopAsync", link, stop.Token);
        try
        {
            var fact = Fact(4) with { World = 8 }; byte[] badWorld = Down(fact); link.Feed(badWorld);
            await Wait(() => link.ReadBytes >= badWorld.Length); await Task.Delay(30); Assert.False(pipe.IsConnected); // current8 is not loaded7
            Set(b, "_t3JoinedHostWorld", 8u); Set(b, "_p5ScopeWorld", 8u); Set(b, "_p1GameplayWorld", 8u);
            link.Feed(Down(fact)); await accept; var add = await Read(server);
            Assert.Equal(0x2B, add.Type); Assert.Equal(253, add.Body[0]); Assert.Equal(1, add.Body[2]); Assert.Equal(1, add.Body[3]);
            ulong gen = BinaryPrimitives.ReadUInt64LittleEndian(add.Body.AsSpan(5)); await Reply(server, 253, 0, Ack(1, 0, gen, 8));
            link.Feed(Down(fact with { Action = DeathMarkerAction.Remove, Revision = 2 })); var remove = await Read(server);
            Assert.Equal(0, remove.Body[2]); await Reply(server, 253, 1, Ack(0, 0, gen, 8));
            link.Feed(Down(fact)); link.Feed(Down(fact with { Revision = 3 })); var latest = await Read(server);
            Assert.Equal(253, latest.Body[0]); Assert.Equal(3ul, BinaryPrimitives.ReadUInt64LittleEndian(latest.Body.AsSpan(25)));
            await Reply(server, 253, 2, Ack(1, 0, gen, 8));
        }
        finally { link.End(); await rx.WaitAsync(TimeSpan.FromSeconds(3)); stop.Cancel(); }
    }

    [Fact]
    public async Task EOF_revokes_a_marker_waiting_at_the_physical_pipe_writer()
    {
        string name = "p5-" + Guid.NewGuid().ToString("N"); using var server = Server(name); var accept = server.WaitForConnectionAsync(); await using var pipe = new CombatPipe(name);
        await using var http = new HttpGameTransport("http://localhost:1403", 2000, new Game()); using var link = new Link(); using var stop = new CancellationTokenSource();
        var b = await Bridge(pipe, http, link, stop.Token); var writer = Get<SemaphoreSlim>(pipe, "_writeLock"); await writer.WaitAsync();
        Task rx = (Task)Call(b, "ReceiveLoopAsync", link, stop.Token);
        try
        {
            link.Feed(Down(Fact(1))); await accept.WaitAsync(TimeSpan.FromSeconds(3)); link.End(); await rx.WaitAsync(TimeSpan.FromSeconds(3)); writer.Release();
            var next = pipe.Wo163Async(164, []); var first = await Read(server); Assert.Equal(164, first.Body[0]);
            await Reply(server, 164, 0, []); Assert.True((await next)!.Value.Ok);
        }
        finally { if (writer.CurrentCount == 0) writer.Release(); link.End(); stop.Cancel(); }
    }

    [Fact]
    public async Task Typed_native_event_has_one_callback_and_never_uses_the_legacy_grave_route()
    {
        string name = "p5-" + Guid.NewGuid().ToString("N"); using var server = Server(name); var accept = server.WaitForConnectionAsync(); await using var pipe = new CombatPipe(name);
        var received = new TaskCompletionSource<NativeDeathMarker>(TaskCreationOptions.RunContinuationsAsynchronously); int legacy = 0;
        pipe.OnDeathMarker = m => { received.TrySetResult(m); return Task.CompletedTask; }; pipe.OnLocalGrave = (_, _, _, _, _) => { legacy++; return Task.CompletedTask; };
        Assert.True(await pipe.EnsureConnectedAsync()); await accept;
        byte[] body = new byte[43]; body[0] = 1; body[1] = 1; body[2] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(3), 11); BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(11), 7);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(15), 123); BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(23), 1);
        await server.WriteAsync(Wire(0xB0, body)); var marker = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(DeathMarkerKind.MarkerOnly, marker.Fact.Kind); Assert.Equal(0, legacy);
    }

    [Fact]
    public async Task Typed_native_notice_reaches_only_the_new_network_route_and_discards_old_generation_or_world()
    {
        string name = "p5-" + Guid.NewGuid().ToString("N"); using var server = Server(name); var accept = server.WaitForConnectionAsync(); await using var pipe = new CombatPipe(name);
        await using var http = new HttpGameTransport("http://localhost:1403", 2000, new Game()); using var link = new Link(); using var stop = new CancellationTokenSource();
        var b = await Bridge(pipe, http, link, stop.Token);
        pipe.OnDeathMarker = m => { Call(b, "P5QueueLocalMarker", m, link, stop.Token); return Task.CompletedTask; };
        Assert.True(await pipe.EnsureConnectedAsync()); await accept;
        byte[] Event(ulong gen, uint world, ulong id)
        {
            byte[] bytes = new byte[43]; bytes[0] = 1; bytes[1] = 1; bytes[2] = 1;
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(3), gen); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(11), world);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(15), id); BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(23), 1); return bytes;
        }
        await server.WriteAsync(Wire(0xB0, Event(10, 7, 1))); await server.WriteAsync(Wire(0xB0, Event(11, 8, 2)));
        await server.WriteAsync(Wire(0xB0, Event(11, 7, 3)));
        byte[] sent = await link.Next(); Assert.Equal(Protocol.DeathMarkerUp, sent[0]);
        Assert.True(DeathMarkerCodec.TryRead(sent.AsSpan(3), out var fact)); Assert.Equal(3ul, fact.Id); Assert.Equal(DeathMarkerKind.MarkerOnly, fact.Kind);
        Assert.False(link.Sent.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Legacy_receive_is_fail_closed_without_loaded_receipt_and_never_uses_unfenced_old_mirror_op()
    {
        string name = "p5-" + Guid.NewGuid().ToString("N"); using var server = Server(name); var accept = server.WaitForConnectionAsync(); await using var pipe = new CombatPipe(name);
        await using var http = new HttpGameTransport("http://localhost:1403", 2000, new Game()); using var link = new Link(); using var stop = new CancellationTokenSource();
        var b = await Bridge(pipe, http, link, stop.Token, joined: false); Task rx = (Task)Call(b, "ReceiveLoopAsync", link, stop.Token);
        byte[] add = new byte[21]; BinaryPrimitives.WriteUInt64LittleEndian(add.AsSpan(1), 9);
        try
        {
            byte[] legacy = Wire(Protocol.GraveAddDown, add); link.Feed(legacy); await Wait(() => link.ReadBytes >= legacy.Length); await Task.Delay(30); Assert.False(pipe.IsConnected);
            Set(b, "_joinedWorld", true); Set(b, "_t3JoinedHostWorld", 7u); Set(b, "_p5ScopeWorld", 7u);
            link.Feed(legacy); await accept; var typed = await Read(server);
            Assert.Equal(0x2B, typed.Type); Assert.Equal(253, typed.Body[0]); Assert.Equal(0, typed.Body[3]);
            ulong gen = BinaryPrimitives.ReadUInt64LittleEndian(typed.Body.AsSpan(5)); await Reply(server, 253, 0, Ack(1, 0, gen, 7));
            byte[] remove = new byte[9]; BinaryPrimitives.WriteUInt64LittleEndian(remove.AsSpan(1), 9); link.Feed(Wire(Protocol.GraveRemoveDown, remove));
            var rm = await Read(server); Assert.Equal(253, rm.Body[0]); Assert.Equal(0, rm.Body[2]); await Reply(server, 253, 1, Ack(0, 0, gen, 7));
            link.Feed(legacy); link.Feed(Down(Fact(10)));
            var next = await Read(server); Assert.Equal(253, next.Body[0]); Assert.Equal(1, next.Body[3]); Assert.Equal(10ul, BinaryPrimitives.ReadUInt64LittleEndian(next.Body.AsSpan(17)));
            await Reply(server, 253, 2, Ack(1, 0, gen, 7));
        }
        finally { link.End(); await rx.WaitAsync(TimeSpan.FromSeconds(3)); stop.Cancel(); }
    }

}
