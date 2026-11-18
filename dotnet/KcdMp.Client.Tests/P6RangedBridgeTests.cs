// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipes;
using System.Net;
using System.Reflection;
using System.Threading.Channels;
using KcdMp.Wire;
namespace KcdMp.Client.Tests;

[Collection("T3 connection lifecycle")]
public sealed class P6RangedBridgeTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Guid Bow = Guid.Parse("10000000-0000-0000-0000-000000000009"), Crossbow = Guid.Parse("10000000-0000-0000-0000-000000000010"), Soul = Guid.NewGuid();
    private static void Set(object b, string n, object? value) => b.GetType().GetField(n, Private)!.SetValue(b, value);
    private static T Get<T>(object b, string n) => (T)b.GetType().GetField(n, Private)!.GetValue(b)!;
    private static object? Call(object b, string n, params object?[] args) => b.GetType().GetMethod(n, Private)!.Invoke(b, args);
    private static long Count(GameBridge b, string n) => Get<ConcurrentDictionary<string, long>>(b, "_t3Counters").TryGetValue(n, out long count) ? count : 0;
    private static async Task Until(Func<bool> predicate, int ms = 3500) { using var stop = new CancellationTokenSource(ms); while (!predicate()) await Task.Delay(5, stop.Token); }
    private static byte[] Wire(byte type, params byte[] p) => [type, (byte)p.Length, (byte)(p.Length >> 8), .. p];
    private static byte[] Down(byte kind, string text, byte peer = 1)
    { var up = new LootMsg(kind, 123, text).BuildUp(Protocol.W164Up, 0); return Wire(Protocol.W164Down, [peer, .. up[3..]]); }
    private static LootMsg Message(byte[] wire) { Assert.Equal(Protocol.W164Up, wire[0]); Assert.True(LootMsg.TryDecode(wire.AsSpan(3 + Protocol.JoinHeaderLen), out var msg)); return msg; }
    private static async Task<(byte Type, byte[] Body)> Read(Stream s)
    { using var stop = new CancellationTokenSource(3500); byte[] h = new byte[3]; await s.ReadExactlyAsync(h, stop.Token); byte[] b = new byte[BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(1))]; await s.ReadExactlyAsync(b, stop.Token); return (h[0], b); }
    private static Task Reply(Stream s, byte type, byte seq, byte op, byte[] payload, bool ok = true) => s.WriteAsync(Wire(type, [(byte)(ok ? 1 : 0), seq, op, 0, .. payload])).AsTask();
    private sealed class Game : HttpMessageHandler
    {
        public readonly ConcurrentQueue<string> Calls = new();
        public bool BlockHorse;
        public readonly TaskCompletionSource HorseEntered = new(TaskCreationOptions.RunContinuationsAsynchronously), ReleaseHorse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            string query = Uri.UnescapeDataString(r.RequestUri!.ToString()); Calls.Enqueue(query);
            if (BlockHorse && query.Contains("KCD2MP_SetGhostHorse")) { HorseEntered.TrySetResult(); await ReleaseHorse.Task; }
            return new(HttpStatusCode.OK) { Content = new StringContent("ok") };
        }
    }
    private sealed class Link : Stream
    {
        private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>(); private byte[]? _buffer; private int _at;
        private long _read; public long ReadBytes => Interlocked.Read(ref _read);
        public readonly Channel<byte[]> Sent = Channel.CreateUnbounded<byte[]>();
        public void Feed(params byte[][] frames) { foreach (var f in frames) _incoming.Writer.TryWrite(f); }
        public void End() => _incoming.Writer.TryComplete();
        public Task<byte[]> Next() => Sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4));
        public override async ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken ct = default)
        {
            while (_buffer is null || _at == _buffer.Length) { if (!await _incoming.Reader.WaitToReadAsync(ct)) return 0; if (!_incoming.Reader.TryRead(out _buffer)) continue; _at = 0; }
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
    private sealed class BlockedPath(bool failSecond = false) : ILinkPath
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<byte[]> Writes = new(); private int _calls;
        public readonly List<long> WriteTicks = new();
        public string Transport => "test"; public bool CanSendUnreliable => false;
        public LinkPathStatus Status() => default;
        public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, bool state, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1) { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); }
            else if (failSecond) throw new IOException("physical writer failed");
            WriteTicks.Add(Environment.TickCount64); Writes.Add(frame.ToArray());
        }
    }
    private sealed class Rig : IAsyncDisposable
    {
        public readonly Game Game = new(); public readonly Link Link = new(); public readonly CancellationTokenSource Stop = new();
        public readonly CombatPipe Pipe; public readonly HttpGameTransport Http; public readonly NamedPipeServerStream Server; public readonly GameBridge Bridge;
        public readonly Task Accept; public readonly ulong Generation; public readonly byte Peer; private Task? _receive;
        private Rig(bool host, RangedItemCatalog catalog, HostAuthorityLifecycle lifecycle)
        {
            string name = "p6-bridge-" + Guid.NewGuid().ToString("N"); Server = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous); Accept = Server.WaitForConnectionAsync();
            Pipe = new(name) { BatchOn = false }; Http = new("http://localhost:1403", 1000, Game); Bridge = new(new ClientConfig()); Peer = host ? (byte)1 : (byte)0; Generation = lifecycle.Current.Generation;
            Set(Bridge, "_combat", Pipe); Set(Bridge, "_transport", Http); Set(Bridge, "_wo122Stream", Link); Set(Bridge, "_wo122Ct", Stop.Token); Set(Bridge, "_resyncStream", Link);
            var field = typeof(GameBridge).GetField("_where", Private)!; field.SetValue(Bridge, Enum.Parse(field.FieldType, "World"));
            Set(Bridge, "_myGhostId", host ? (byte)0 : (byte)1); Set(Bridge, "_combatRoleApplied", true); Set(Bridge, "_isDamageAuthority", host); Set(Bridge, "_sharedWorld", true); Set(Bridge, "_joinedWorld", !host);
            Set(Bridge, "_hostModeKnown", true); Set(Bridge, "_hostModeFrom", (byte)0); Set(Bridge, "_hostSharedWorld", true); Set(Bridge, "_t3JoinedHostWorld", host ? 0u : 7u);
            Set(Bridge, "_t3StatsNextMs", long.MaxValue); Set(Bridge, "_p1GameplayMs", Environment.TickCount64); Set(Bridge, "_p1GameplayWorld", 7u); Set(Bridge, "_p1Lifecycle", lifecycle);
            Set(Bridge, "_t3TpHostRefreshAt", long.MaxValue); Set(Bridge, "_p5ScopeGeneration", Generation); Set(Bridge, "_p5ScopeWorld", 7u); Set(Bridge, "_w201Connected", true); Set(Bridge, "_p6Catalog", catalog);
            Get<Wo206Epochs>(Bridge, "_w206").LearnWorld(7, 1); Get<Wo206Epochs>(Bridge, "_w206").NoteAck(Peer, 7);
            Get<PeerSet>(Bridge, "_peers").Connected(Peer, "peer"); Call(Bridge, "P5PeerAtRead", (int)Protocol.Name, new byte[] { Peer, 120 });
            Get<ConcurrentDictionary<string, uint>>(Bridge, "_ghostEntityIds")[Peer.ToString()] = 222;
            Get<ConcurrentDictionary<string, (Guid? Guid, DateTime At)>>(Bridge, "_soulGuidByName")["npc_bandit"] = (Soul, DateTime.UtcNow);
            Get<ConcurrentDictionary<Guid, (string? Name, DateTime At)>>(Bridge, "_soulNameByGuid")[Soul] = ("npc_bandit", DateTime.UtcNow);
            Call(Bridge, "P6BeginReceive", Link, Stop.Token);
        }
        public static async Task<Rig> Create(bool host = true)
        {
            byte phase = 0; ulong gen = 10; bool enabled = false;
            var lifecycle = new HostAuthorityLifecycle((p, g, _) =>
            { if (p != HostAuthorityLifecycle.Query) { phase = p; gen = g; if (p != HostAuthorityLifecycle.Gameplay) enabled = p == HostAuthorityLifecycle.Load; } return Task.FromResult<HostAuthorityLifecycle.Ack?>(new(true, phase, enabled, false, gen, 0)); });
            await lifecycle.PendingAsync(!host, true, default); await lifecycle.GameplayAsync(lifecycle.Current.Generation, default);
            string path = Path.Combine(Path.GetTempPath(), "p6-catalog-" + Guid.NewGuid() + ".pak");
            try
            {
                using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) using (var writer = new StreamWriter(zip.CreateEntry("Libs/Tables/item/item.xml").Open()))
                    writer.Write($"<Database><MissileWeapon Id=\"{Bow:D}\" Class=\"9\"/><MissileWeapon Id=\"{Crossbow:D}\" Class=\"10\"/></Database>");
                return new(host, RangedItemCatalog.Read(path), lifecycle);
            }
            finally { File.Delete(path); }
        }
        public void Receive() => _receive = (Task)Call(Bridge, "ReceiveLoopAsync", Link, Stop.Token)!;
        public async ValueTask DisposeAsync()
        {
            Game.ReleaseHorse.TrySetResult();
            Link.End(); if (_receive is not null) await _receive.WaitAsync(TimeSpan.FromSeconds(4));
            Call(Bridge, "P6EndReceive", Link, Stop.Token); Stop.Cancel(); await Pipe.DisposeAsync(); await Http.DisposeAsync(); Server.Dispose(); Link.Dispose(); Stop.Dispose();
        }
    }
    private static byte[] Poll(ulong gen, ulong epoch, params byte[] phases)
    {
        byte[] b = new byte[18 + phases.Length * 48]; b[0] = 2; b[1] = (byte)phases.Length; BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(2), gen); BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(10), epoch);
        for (int i = 0; i < phases.Length; i++) { int o = 18 + i * 48; b[o] = phases[i]; b[o + 1] = 1; b[o + 2] = 3; BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 4), (uint)(i + 1)); BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o + 8), (ulong)Environment.TickCount64); Bow.TryWriteBytes(b.AsSpan(o + 32)); }
        return b;
    }
    private static RangedStepV2 Step(uint seq = 1, byte phase = 3) => new(7, 55, 66, seq, phase, RangedWeaponKind.Bow, Bow, 0, 1);
    private static MeasuredMissileRequest Hit(ulong id = 1) => new(new(7, 77, 88, id), "npc_bandit", 12, 3);
    private static byte[] Ack(MeasuredMissileRequest r, byte source = 1, MeasuredMissileStatus status = MeasuredMissileStatus.Accepted)
    {
        byte[] b = new byte[40]; b[0] = 1; b[1] = (byte)status; b[2] = status == MeasuredMissileStatus.Accepted ? (byte)1 : (byte)0; b[3] = source;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), r.Key.World); BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), r.Key.SourceGeneration);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), r.Key.Epoch); BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(24), r.Key.ContactId);
        if (status == MeasuredMissileStatus.Accepted) { BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(32), r.Health); BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(36), r.Stamina); } return b;
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Both_roles_forward_real_native_draw_aim_fire_stop_FIFO_with_capture_class_and_one_poll_in_flight(bool host)
    {
        await using var r = await Rig.Create(host); Call(r.Bridge, "Wo201RangedTick"); await r.Accept; var request = await Read(r.Server);
        Assert.Equal(0x2B, request.Type); Assert.Equal(new byte[] { 86, 2, 1 }, request.Body[..3]); Assert.Equal(r.Generation, BinaryPrimitives.ReadUInt64LittleEndian(request.Body.AsSpan(3)));
        for (int i = 0; i < 5; i++) Call(r.Bridge, "Wo201RangedTick"); await Until(() => Count(r.Bridge, "ranged_poll_busy") >= 5);
        await Reply(r.Server, 0xAC, 0, 86, Poll(r.Generation, 19, 1, 2, 3, 0, 6));
        foreach (byte phase in new byte[] { 1, 2, 3, 0, 6 })
        {
            var message = Message(await r.Link.Next()); Assert.Equal(Protocol.W164RangedStep, message.Kind); Assert.True(RangedStepV2.TryDecode(message.Text, out var step));
            Assert.Equal(phase, step.Phase); Assert.Equal(RangedWeaponKind.Bow, step.Kind); Assert.Equal(Bow, step.ItemClass); Assert.Equal(19ul, step.Epoch);
        }
        await Until(() => Count(r.Bridge, "ranged_out") == 5); Assert.Equal(1, Count(r.Bridge, "ranged_poll_started")); Assert.False(r.Link.Sent.Reader.TryRead(out _));
        Assert.DoesNotContain(r.Game.Calls, x => x.Contains("W201Shot") || x.Contains("GetItemInHand"));
    }
    [Fact]
    public async Task Old_inflight_poll_after_down_then_wake_cannot_publish_or_poison_the_next_poll()
    {
        await using var r = await Rig.Create(false); Call(r.Bridge, "Wo201RangedTick"); await r.Accept; await Read(r.Server);
        await (Task)Call(r.Bridge, "OnLocalDownedAsync", true, (byte)0)!; await (Task)Call(r.Bridge, "OnLocalDownedAsync", false, (byte)0)!;
        await Reply(r.Server, 0xAC, 0, 86, Poll(r.Generation, 19, 3));
        await Until(() => Get<SemaphoreSlim>(r.Bridge, "_p6PollGate").CurrentCount == 1); Assert.False(r.Link.Sent.Reader.TryRead(out _));
        Call(r.Bridge, "Wo201RangedTick"); Assert.Equal(86, (await Read(r.Server)).Body[0]);
        // The post-wake capture must occur on a later tick than the wake itself.
        await Until(() => Environment.TickCount64 > Get<long>(r.Bridge, "_p6VisualModeAtTick"));
        await Reply(r.Server, 0xAC, 1, 86, Poll(r.Generation, 20, 1, 2, 3));
        for (int i = 0; i < 3; i++) Assert.Equal(Protocol.W164RangedStep, Message(await r.Link.Next()).Kind);
        await Until(() => Count(r.Bridge, "ranged_out") == 3);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Ranged_out_counts_only_a_complete_physical_link_write_and_revoked_queued_rows_never_count(bool revoke)
    {
        await using var r = await Rig.Create(); var path = new BlockedPath(); var writer = new LinkWriter(new LinkScheduler(new LinkSchedOptions { Budget = false }), path);
        Set(r.Bridge, "_w213Writer", writer); Set(r.Bridge, "_w213Stream", r.Link);
        writer.Enqueue(Wire(Protocol.Ping, new byte[8])); Task pump = writer.PumpOnceAsync(r.Stop.Token); await path.Entered.Task;
        Call(r.Bridge, "Wo201RangedTick"); await r.Accept; await Read(r.Server); await Reply(r.Server, 0xAC, 0, 86, Poll(r.Generation, 19, 3));
        await Until(() => writer.Scheduler.EventCount == 1);
        Assert.Equal(0, Count(r.Bridge, "ranged_out")); Assert.Equal(0, Count(r.Bridge, "ranged_peer_tx"));
        if (revoke) { await (Task)Call(r.Bridge, "OnLocalDownedAsync", true, (byte)0)!; await (Task)Call(r.Bridge, "OnLocalDownedAsync", false, (byte)0)!; }
        path.Release.TrySetResult(); await pump;
        Assert.Equal(revoke ? 0 : 1, Count(r.Bridge, "ranged_out")); Assert.Equal(revoke ? 0 : 1, Count(r.Bridge, "ranged_peer_tx"));
        if (revoke)
        {
            Assert.Equal(1, writer.Scheduler.CancelledDropped);
            Call(r.Bridge, "Wo201RangedTick"); await Read(r.Server);
            await Until(() => Environment.TickCount64 > Get<long>(r.Bridge, "_p6VisualModeAtTick"));
            await Reply(r.Server, 0xAC, 1, 86, Poll(r.Generation, 20, 3));
            await Until(() => writer.Scheduler.EventCount == 1); await writer.PumpOnceAsync(r.Stop.Token);
            Assert.Equal(1, Count(r.Bridge, "ranged_out")); Assert.Equal(1, Count(r.Bridge, "ranged_peer_tx"));
        }
    }
    [Fact]
    public async Task A_failed_physical_link_write_never_counts_an_out_step()
    {
        await using var r = await Rig.Create(); var path = new BlockedPath(true); var writer = new LinkWriter(new LinkScheduler(new LinkSchedOptions { Budget = false }), path);
        Set(r.Bridge, "_w213Writer", writer); Set(r.Bridge, "_w213Stream", r.Link);
        writer.Enqueue(Wire(Protocol.Ping, new byte[8])); Task pump = writer.PumpOnceAsync(r.Stop.Token); await path.Entered.Task;
        Call(r.Bridge, "Wo201RangedTick"); await r.Accept; await Read(r.Server); await Reply(r.Server, 0xAC, 0, 86, Poll(r.Generation, 19, 3)); await Until(() => writer.Scheduler.EventCount == 1);
        path.Release.TrySetResult(); await Assert.ThrowsAsync<IOException>(() => pump);
        Assert.Equal(0, Count(r.Bridge, "ranged_out")); Assert.Equal(0, Count(r.Bridge, "ranged_peer_tx"));
    }
    [Fact]
    public async Task Actual_scheduled_write_refreshes_capture_age_without_changing_the_selected_frame_budget()
    {
        Assert.Equal((Step() with { AgeMs = 100 }).Encode().Length, (Step() with { AgeMs = 1900 }).Encode().Length);
        await using var r = await Rig.Create(); var path = new BlockedPath(); var writer = new LinkWriter(new LinkScheduler(new LinkSchedOptions { Budget = false }), path);
        Set(r.Bridge, "_w213Writer", writer); Set(r.Bridge, "_w213Stream", r.Link);
        writer.Enqueue(Wire(Protocol.Ping, new byte[8])); Task pump = writer.PumpOnceAsync(r.Stop.Token); await path.Entered.Task;
        Call(r.Bridge, "Wo201RangedTick"); await r.Accept; await Read(r.Server); long capture = Environment.TickCount64 - 100;
        var p = Poll(r.Generation, 19, 3); BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(26), (ulong)capture);
        await Reply(r.Server, 0xAC, 0, 86, p); await Until(() => writer.Scheduler.EventCount == 1);
        long queuedAt = Environment.TickCount64; await Task.Delay(100);
        long releaseAt = Environment.TickCount64;
        path.Release.TrySetResult(); await pump;
        Assert.True(releaseAt > queuedAt); // measure the actual clock advance; Task.Delay is not an age lower bound on Windows
        Assert.True(RangedStepV2.TryDecode(Message(path.Writes.Last()).Text, out var step));
        Assert.InRange((long)step.AgeMs, releaseAt - capture, path.WriteTicks.Last() - capture);
        Assert.True(step.AgeMs > queuedAt - capture); // the queued age cannot survive the physical writer wait
        Assert.Equal(1, Count(r.Bridge, "ranged_out"));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Queued_visual_step_is_revoked_by_off_and_off_then_on_without_cancelling_contact_context(bool onAgain)
    {
        await using var r = await Rig.Create(); var path = new BlockedPath(); var writer = new LinkWriter(new LinkScheduler(new LinkSchedOptions { Budget = false }), path);
        Set(r.Bridge, "_w213Writer", writer); Set(r.Bridge, "_w213Stream", r.Link); object context = Call(r.Bridge, "P6GetContext")!;
        writer.Enqueue(Wire(Protocol.Ping, new byte[8])); Task pump = writer.PumpOnceAsync(r.Stop.Token); await path.Entered.Task;
        Call(r.Bridge, "Wo201RangedTick"); await r.Accept; await Read(r.Server); await Reply(r.Server, 0xAC, 0, 86, Poll(r.Generation, 19, 3)); await Until(() => writer.Scheduler.EventCount == 1);
        Call(r.Bridge, "Wo201OnEvent", "w201_cfg", "figure_ranged=off"); if (onAgain) Call(r.Bridge, "Wo201OnEvent", "w201_cfg", "figure_ranged=on");
        Assert.Same(context, Call(r.Bridge, "P6GetContext")); path.Release.TrySetResult(); await pump;
        Assert.Single(path.Writes); Assert.Equal(0, Count(r.Bridge, "ranged_out")); Assert.Equal(0, Count(r.Bridge, "ranged_peer_tx"));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Actual_reader_pending_receipt_cannot_be_promoted_by_later_gameplay_in_the_same_generation(bool measured)
    {
        await using var r = await Rig.Create(); r.Game.BlockHorse = true; r.Http.BatchingEnabled = false; r.Receive(); byte[] horse = Wire(Protocol.HorseInfoDown, 1, 0); r.Link.Feed(horse);
        await r.Game.HorseEntered.Task.WaitAsync(TimeSpan.FromSeconds(3)); var lifecycle = Get<HostAuthorityLifecycle>(r.Bridge, "_p1Lifecycle");
        await lifecycle.PendingAsync(false, true, default); Set(r.Bridge, "_p1Loading", true); Set(r.Bridge, "_p1Serial", 1L);
        byte[] incoming = Down(measured ? Protocol.W164MeasuredMissile : Protocol.W164RangedStep, measured ? Hit().Encode() : Step().Encode()); r.Link.Feed(incoming);
        await Until(() => r.Link.ReadBytes >= horse.Length + incoming.Length);
        await lifecycle.GameplayAsync(lifecycle.Current.Generation, default); Set(r.Bridge, "_p1Loading", false); r.Game.ReleaseHorse.TrySetResult();
        await Until(() => Count(r.Bridge, "ranged_stale") > 0); Assert.False(r.Pipe.IsConnected);
        Assert.Equal(0, Count(r.Bridge, "ranged_missile_measured_accept")); Assert.Equal(0, Count(r.Bridge, "ranged_bow_played"));
    }
    [Fact]
    public async Task Actual_receive_bow_without_figureprop_uses_bow_tags_and_never_HTTP_shot_or_damage()
    {
        await using var r = await Rig.Create(); r.Receive(); r.Link.Feed(Down(Protocol.W164RangedStep, Step().Encode())); await r.Accept;
        var shot = await Read(r.Server); Assert.Equal(0x28, shot.Type); Assert.Equal(4, shot.Body[0]); string strings = System.Text.Encoding.ASCII.GetString(shot.Body);
        Assert.Contains("Fire", strings); Assert.Contains("l_bow", strings); Assert.DoesNotContain("crossbow", strings);
        await Reply(r.Server, 0xA8, 0, 4, BitConverter.GetBytes(42)); await Until(() => Count(r.Bridge, "ranged_bow_played") == 1);
        Assert.DoesNotContain(r.Game.Calls, x => x.Contains("W201Shot") || x.Contains("ApplyDamage") || x.Contains("CreateItem")); Assert.False(r.Link.Sent.Reader.TryRead(out _));
    }
    [Theory]
    [InlineData("unknown")] [InlineData("legacy")] [InlineData("mismatch")] [InlineData("world")] [InlineData("aim")]
    public async Task Unknown_legacy_mismatch_old_world_and_unsupported_aim_never_start_an_animation(string mode)
    {
        await using var r = await Rig.Create(); r.Receive(); var step = Step();
        if (mode == "unknown") step = step with { Kind = RangedWeaponKind.Unknown, ItemClass = Guid.Empty, Source = 0 };
        if (mode == "mismatch") step = step with { ItemClass = Crossbow };
        if (mode == "world") step = step with { World = 6 };
        if (mode == "aim") step = step with { Phase = 2 };
        r.Link.Feed(Down(Protocol.W164RangedStep, mode == "legacy" ? "v1|3" : step.Encode()));
        await Until(() => Count(r.Bridge, "ranged_malformed") + Count(r.Bridge, "ranged_stale") + Count(r.Bridge, "ranged_kind_unknown") + Count(r.Bridge, "ranged_phase_unrenderable") > 0);
        Assert.False(r.Pipe.IsConnected); Assert.DoesNotContain(r.Game.Calls, x => x.Contains("W201Shot"));
    }
    [Theory]
    [InlineData("life")] [InlineData("world")] [InlineData("eof")] [InlineData("scene")]
    public async Task A_ranged_animation_waiting_for_the_physical_writer_loses_old_life_world_connection_or_scene_authority(string change)
    {
        await using var r = await Rig.Create(); var connect = r.Pipe.EnsureConnectedAsync(); await r.Accept; Assert.True(await connect);
        var writer = Get<SemaphoreSlim>(r.Pipe, "_writeLock"); await writer.WaitAsync(); r.Receive(); byte[] frame = Down(Protocol.W164RangedStep, Step().Encode()); r.Link.Feed(frame);
        await Until(() => Count(r.Bridge, "ranged_in") == 1);
        if (change == "life") { await (Task)Call(r.Bridge, "OnLocalDownedAsync", true, (byte)0)!; await (Task)Call(r.Bridge, "OnLocalDownedAsync", false, (byte)0)!; }
        if (change == "world") Get<Wo206Epochs>(r.Bridge, "_w206").LearnWorld(8, 2);
        if (change == "eof") { r.Link.End(); await Until(() => Get<Stream?>(r.Bridge, "_p6ReceiveStream") is null); }
        if (change == "scene") { Set(r.Bridge, "_w179Bus", false); byte[] scene = Down(Protocol.W164SceneBus, Wo179Rules.Start(1, 'V', "test_video")); r.Link.Feed(scene); await Until(() => r.Link.ReadBytes >= frame.Length + scene.Length); }
        writer.Release(); await Task.Delay(80); Assert.Equal(0, Count(r.Bridge, "ranged_bow_played"));
        using var stop = new CancellationTokenSource(100);
        try { Assert.Equal(0, await r.Server.ReadAsync(new byte[3], stop.Token)); } catch (OperationCanceledException) { }
    }
    [Fact]
    public async Task Measured_host_request_is_deduplicated_before_native_write_conflicts_are_refused_and_generation_retry_does_not_reapply()
    {
        await using var r = await Rig.Create(); Get<ConcurrentDictionary<string, bool>>(r.Bridge, "_w201On")["figure_ranged"] = false; r.Receive(); var hit = Hit();
        r.Link.Feed(Down(Protocol.W164MeasuredMissile, hit.Encode())); await r.Accept; var native = await Read(r.Server);
        Assert.Equal(0x2B, native.Type); Assert.Equal(18, native.Body[0]); Assert.Equal(128, native.Body[8]); int tail = native.Body.Length - 50;
        Assert.Equal("P6M1", System.Text.Encoding.ASCII.GetString(native.Body, tail, 4)); Assert.Equal(1, native.Body[tail + 4]); Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(native.Body.AsSpan(tail + 6)));
        ulong deadline = BinaryPrimitives.ReadUInt64LittleEndian(native.Body.AsSpan(tail + 42)); Assert.InRange(deadline, (ulong)Environment.TickCount64, (ulong)Environment.TickCount64 + 2000);
        r.Link.Feed(Down(Protocol.W164MeasuredMissile, hit.Encode())); await Reply(r.Server, 0xAC, 0, 18, Ack(hit));
        var first = Message(await r.Link.Next()); var dupe = Message(await r.Link.Next()); Assert.Equal(first.Text, dupe.Text); Assert.True(MeasuredMissileResult.TryDecode(first.Text, out var accepted)); Assert.Equal(MeasuredMissileStatus.Accepted, accepted.Status);
        r.Link.Feed(Down(Protocol.W164MeasuredMissile, (hit with { Health = 13 }).Encode())); Assert.True(MeasuredMissileResult.TryDecode(Message(await r.Link.Next()).Text, out var conflict)); Assert.Equal(MeasuredMissileStatus.Conflict, conflict.Status);
        var lifecycle = Get<HostAuthorityLifecycle>(r.Bridge, "_p1Lifecycle"); await lifecycle.PendingAsync(false, true, default); await lifecycle.GameplayAsync(lifecycle.Current.Generation, default);
        r.Link.Feed(Down(Protocol.W164MeasuredMissile, hit.Encode())); Assert.Equal(first.Text, Message(await r.Link.Next()).Text);
        Assert.Equal(1, Count(r.Bridge, "ranged_missile_measured_accept")); Assert.Equal(1, Count(r.Bridge, "ranged_hits_verdict")); Assert.True(Count(r.Bridge, "ranged_missile_duplicate") >= 2);
        using var stop = new CancellationTokenSource(100); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => r.Server.ReadAsync(new byte[3], stop.Token).AsTask());
    }
    [Fact]
    public async Task Reader_deadline_is_not_refreshed_after_a_real_processor_backlog()
    {
        await using var r = await Rig.Create(); Set(r.Bridge, "_t3ProcFrameTimeoutMs", 3000); var connect = r.Pipe.EnsureConnectedAsync(); await r.Accept; Assert.True(await connect);
        var writer = Get<SemaphoreSlim>(r.Pipe, "_writeLock"); await writer.WaitAsync(); r.Receive(); var hit = Hit();
        byte[] ranged = Down(Protocol.W164RangedStep, Step().Encode()), measured = Down(Protocol.W164MeasuredMissile, hit.Encode());
        r.Link.Feed(ranged, measured); await Until(() => r.Link.ReadBytes >= ranged.Length + measured.Length);
        var ack = Message(await r.Link.Next()); Assert.True(MeasuredMissileResult.TryDecode(ack.Text, out var result)); Assert.Equal(MeasuredMissileStatus.Expired, result.Status);
        writer.Release(); Assert.Equal(0, Count(r.Bridge, "ranged_missile_measured_accept")); Assert.True(Count(r.Bridge, "proc_stalls") > 0);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Figure_ranged_off_keeps_actual_joiner_measured_hits_with_no_dependency_on_a_poll_epoch_and_never_adds_floor_damage(bool atFloor)
    {
        await using var r = await Rig.Create(false); Get<ConcurrentDictionary<string, bool>>(r.Bridge, "_w201On")["figure_ranged"] = false; Set(r.Bridge, "_w131Connected", true);
        r.Receive();
        int legacy = 0; r.Pipe.OnLocalHit = (_, _, _, _, _, _) => { legacy++; return Task.CompletedTask; };
        r.Pipe.OnMeasuredMissile = (soul, st, hp, evidence) => { Call(r.Bridge, "P6QueueMeasuredHit", soul, st, hp, evidence, (Func<Task>)(() => throw new Exception("joiner must not replicate a host local hit"))); return Task.CompletedTask; };
        var connect = r.Pipe.EnsureConnectedAsync(); await r.Accept; Assert.True(await connect);
        float measuredHealth = atFloor ? 4 : 12;
        byte[] evidence = new byte[61]; Soul.TryWriteBytes(evidence); BinaryPrimitives.WriteSingleLittleEndian(evidence.AsSpan(16), 3); BinaryPrimitives.WriteSingleLittleEndian(evidence.AsSpan(20), measuredHealth);
        evidence[25] = 1; evidence[27] = 1; evidence[28] = 7; BinaryPrimitives.WriteUInt64LittleEndian(evidence.AsSpan(29), 123); BinaryPrimitives.WriteUInt64LittleEndian(evidence.AsSpan(37), r.Generation);
        BinaryPrimitives.WriteUInt64LittleEndian(evidence.AsSpan(45), 9999); BinaryPrimitives.WriteUInt64LittleEndian(evidence.AsSpan(53), (ulong)Environment.TickCount64);
        await r.Server.WriteAsync(Wire(0x90, evidence)); // native LocalHit
        var gate = await Read(r.Server); Assert.Equal(0x21, gate.Type); Assert.Equal(1, gate.Body[0]);
        byte[] check = new byte[13]; check[0] = 1; BinaryPrimitives.WriteSingleLittleEndian(check.AsSpan(8), atFloor ? 1 : 50); check[12] = atFloor ? (byte)1 : (byte)0;
        await Reply(r.Server, 0x98, 0, 1, check);
        var request = Message(await r.Link.Next()); Assert.Equal(Protocol.W164MeasuredMissile, request.Kind); Assert.True(MeasuredMissileRequest.TryDecode(request.Text, out var hit));
        Assert.Equal(123ul, hit.Key.ContactId); Assert.Equal(9999ul, hit.Key.Epoch); Assert.Equal(measuredHealth, hit.Health); Assert.Equal(3, hit.Stamina);
        if (atFloor) Assert.NotEqual(5, hit.Health); // the legacy gate's +1 is not a measured missile value and is never sent
        Assert.Equal(0, legacy); Assert.Equal(0, Count(r.Bridge, "ranged_poll_started"));
        r.Link.Feed(Down(Protocol.W164RealHit, new MeasuredMissileResult(hit.Key, MeasuredMissileStatus.Accepted, 1, hit.Health, hit.Stamina).Encode(), 0));
        await Until(() => Get<ConcurrentDictionary<MeasuredMissileKey, TaskCompletionSource<MeasuredMissileResult>>>(r.Bridge, "_p6AwaitingMissile").IsEmpty);
        await r.Server.WriteAsync(Wire(0x90, evidence)); await Until(() => Count(r.Bridge, "ranged_missile_duplicate") == 1);
        BinaryPrimitives.WriteSingleLittleEndian(evidence.AsSpan(20), 13); await r.Server.WriteAsync(Wire(0x90, evidence)); await Until(() => Count(r.Bridge, "ranged_missile_conflict") == 1);
        Assert.False(r.Link.Sent.Reader.TryRead(out _)); Assert.Equal(1, Count(r.Bridge, "ranged_missile_requests"));
    }
    [Fact]
    public async Task Host_local_measured_contact_replicates_once_and_never_requests_a_second_host_apply_even_with_figure_ranged_off()
    {
        await using var r = await Rig.Create(); Get<ConcurrentDictionary<string, bool>>(r.Bridge, "_w201On")["figure_ranged"] = false;
        int replications = 0; Func<Task> replicate = () => { Interlocked.Increment(ref replications); return Task.CompletedTask; };
        r.Pipe.OnMeasuredMissile = (soul, st, hp, evidence) => { Call(r.Bridge, "P6QueueMeasuredHit", soul, st, hp, evidence, replicate); return Task.CompletedTask; };
        var connect = r.Pipe.EnsureConnectedAsync(); await r.Accept; Assert.True(await connect);
        byte[] body = new byte[61]; Soul.TryWriteBytes(body); BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(16), 3); BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(20), 12);
        body[24] = 1; body[25] = 1; body[27] = 1; body[28] = 7; BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(29), 123); BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(37), r.Generation);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(45), 9999); BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(53), (ulong)Environment.TickCount64);
        await r.Server.WriteAsync(Wire(0x90, body)); await r.Server.WriteAsync(Wire(0x90, body));
        await Until(() => Count(r.Bridge, "ranged_missile_local_host") == 1 && Count(r.Bridge, "ranged_missile_duplicate") == 1);
        Assert.Equal(1, replications); Assert.Equal(1, Count(r.Bridge, "ranged_hits_verdict")); Assert.Equal(1, Count(r.Bridge, "ranged_missile_duplicate"));
        Assert.False(r.Link.Sent.Reader.TryRead(out _));
    }
    [Theory]
    [InlineData((byte)1)] [InlineData((byte)2)]
    public async Task Accepted_measured_hit_arms_the_existing_canonical_host_watcher_and_notifies_a_third_peer(byte state)
    {
        await using var r = await Rig.Create(); var touched = Get<ConcurrentDictionary<string, long>>(r.Bridge, "_w173Touched"); Assert.Empty(touched);
        Get<PeerSet>(r.Bridge, "_peers").Connected(2, "third"); r.Receive(); var hit = Hit(); r.Link.Feed(Down(Protocol.W164MeasuredMissile, hit.Encode())); await r.Accept; Assert.Equal(18, (await Read(r.Server)).Body[0]);
        await Reply(r.Server, 0xAC, 0, 18, Ack(hit)); Assert.Equal(Protocol.W164RealHit, Message(await r.Link.Next()).Kind); Assert.True(touched.ContainsKey("npc_bandit"));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(r.Stop.Token); Task poll = (Task)Call(r.Bridge, "Wo173DownLoopAsync", stop.Token)!;
        try
        {
            var request = await Read(r.Server); Assert.Equal(21, request.Body[0]); await Reply(r.Server, 0xAC, 1, 21, [1, state]);
            var one = await r.Link.Next(); var two = await r.Link.Next(); Assert.Equal(new byte[] { 1, 2 }, new[] { one[3], two[3] }.Order().ToArray());
            foreach (var f in new[] { one, two }) { var m = Message(f); Assert.Equal(Protocol.W164NpcDown, m.Kind); Assert.True(W164Text.TryParseNpcDown(m.Text, out var npc, out var down)); Assert.Equal("npc_bandit", npc); Assert.Equal(state, down); }
            Assert.Equal(1, Count(r.Bridge, "ranged_missile_measured_accept"));
        }
        finally { stop.Cancel(); await poll; }
    }
}
