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
    private static byte[] TimedFire(byte peer) => TimedRanged(peer, Step());
    private static byte[] TimedRanged(byte peer, RangedStepV2 step) => PresentationTimeWire.Wrap(Protocol.W164Down,
        Down(Protocol.W164RangedStep, step.Encode(), peer).AsSpan(3), new PresentationStamp(1, (ulong)PresentationClock.NowMs(), 0));
    private static LootMsg Message(byte[] wire) { Assert.Equal(Protocol.W164Up, wire[0]); Assert.True(LootMsg.TryDecode(wire.AsSpan(3 + Protocol.JoinHeaderLen), out var msg)); return msg; }
    private static async Task<(byte Type, byte[] Body)> Read(Stream s)
    { using var stop = new CancellationTokenSource(3500); byte[] h = new byte[3]; await s.ReadExactlyAsync(h, stop.Token); byte[] b = new byte[BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(1))]; await s.ReadExactlyAsync(b, stop.Token); return (h[0], b); }
    private static Task Reply(Stream s, byte type, byte seq, byte op, byte[] payload, bool ok = true) => s.WriteAsync(Wire(type, [(byte)(ok ? 1 : 0), seq, op, 0, .. payload])).AsTask();
    private static Task ReplyEdge(Stream stream, byte[] request, EdgeOutcome outcome, params byte[] payload) =>
        stream.WriteAsync(Wire(0xB8, [.. request[..4], (byte)outcome, outcome == EdgeOutcome.Completed ? (byte)0x81 : (byte)0, .. payload])).AsTask();
    private static Task ReplyHandEdge(Stream stream, byte[] request, byte known = 0, Guid left = default, Guid right = default) =>
        stream.WriteAsync(Wire(0xB8, [.. request[..4], (byte)EdgeOutcome.Completed, 0xBB, 1, 0, known,
            .. left.ToByteArray(), .. right.ToByteArray()])).AsTask();
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
        private long _read, _readRequestedAfterBytes;
        // The next read request proves the previous frame was dispatched by the reader.
        public long ReadRequestedAfterBytes => Interlocked.Read(ref _readRequestedAfterBytes);
        public readonly Channel<byte[]> Sent = Channel.CreateUnbounded<byte[]>();
        public void Feed(params byte[][] frames) { foreach (var f in frames) _incoming.Writer.TryWrite(f); }
        public void End() => _incoming.Writer.TryComplete();
        public Task<byte[]> Next() => Sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4));
        public override async ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken ct = default)
        {
            Interlocked.Exchange(ref _readRequestedAfterBytes, _read);
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
        public async Task StartTimedReceiveAsync(bool friendlyFire = false)
        {
            Call(Bridge, "OnGameEvent", "ghostid", $"{Peer} 000000DE 00000001");
            Assert.True(await Pipe.EnsureConnectedAsync());
            await Accept;
            Task<bool> probe = Pipe.ProbeEdgeAsync(Stop.Token);
            var capability = await Read(Server);
            Assert.Equal((byte)0x2B, capability.Type);
            Assert.Equal(new byte[] { 255 }, capability.Body);
            await Reply(Server, 0xAC, 0, 255, [1]);
            Assert.True(await probe);

            ushort capabilities = Protocol.LinkCapPresentationTime | Protocol.LinkCapPresentationCombat;
            if (friendlyFire)
            {
                capabilities |= Protocol.LinkCapPvpContact | Protocol.LinkCapPvpLife;
                var contactSession = typeof(GameBridge).GetNestedType("PvpContactSession", BindingFlags.NonPublic)!;
                Set(Bridge, "_wo121ContactSession", Activator.CreateInstance(contactSession, Private | BindingFlags.Public, null, [Link], null));
            }
            Set(Bridge, "_w213Caps", capabilities);
            Get<ConcurrentDictionary<string, byte>>(Bridge, "_nativeBound")[$"kcd2mp_{Peer}"] = 1;
            Get<ConcurrentDictionary<string, bool>>(Bridge, "_w221On")["rp_combat"] = true;
            Receive();
            var config = await Read(Server);
            Assert.Equal((byte)0x38, config.Type);
            Assert.Equal((byte)0x3D, config.Body[5]);
            await ReplyEdge(Server, config.Body, EdgeOutcome.Completed, 1, 0, 0);

            byte[] query = await Link.Next();
            Assert.Equal(Protocol.PresentationClockUp, query[0]);
            Assert.True(PresentationClock.TryReadQuery(query.AsSpan(3), out long t0));
            byte[] clockReply = PresentationClock.BuildReply(t0, t0, t0);
            Link.Feed(clockReply);
            byte[] confirmation = await Link.Next();
            Assert.Equal(Protocol.PresentationClockUp, confirmation[0]);
            Assert.True(PresentationClock.TryReadConfirm(confirmation.AsSpan(3), out long confirmedT0, out _));
            Assert.Equal(t0, confirmedT0);
            await Until(() => Link.ReadRequestedAfterBytes >= clockReply.Length);
            await Until(() => Bridge.GetType().GetField("_presentationNativeConfig", Private)!.GetValue(Bridge) is not null);
        }

        public async Task<byte[]> ConfirmBowBeforeFireAsync(byte known = 3, Guid? observedClass = null, RangedWeaponKind weapon = RangedWeaponKind.Bow)
        {
            Guid item = weapon == RangedWeaponKind.Bow ? Bow : Crossbow;
            byte[] up = new LootMsg(Protocol.W203FigureHand, 120, $"v1|1|{item:D}").BuildUp(Protocol.W203Up, 0);
            Link.Feed(Wire(Protocol.W203Down, [Peer, .. up[3..]]));
            var declared = await Read(Server);
            Assert.Equal(0x38, declared.Type);
            Assert.Equal(0x41, declared.Body[5]);
            byte[] hand = declared.Body[10..];
            Assert.Equal(60, hand.Length);
            Assert.Equal(new byte[] { 1, 0, Peer }, hand[..3]);
            Assert.Equal(222u, BinaryPrimitives.ReadUInt32LittleEndian(hand.AsSpan(3)));
            Assert.Equal(item, new Guid(hand.AsSpan(27, 16)));
            Assert.Equal(1, hand[43] & 1);
            uint incarnation = BinaryPrimitives.ReadUInt32LittleEndian(hand.AsSpan(44));
            uint world = BinaryPrimitives.ReadUInt32LittleEndian(hand.AsSpan(48));
            ulong serial = BinaryPrimitives.ReadUInt64LittleEndian(hand.AsSpan(52));
            ulong peerSerial = BinaryPrimitives.ReadUInt64LittleEndian(hand.AsSpan(7));
            uint body = BinaryPrimitives.ReadUInt32LittleEndian(hand.AsSpan(15));
            ulong revision = BinaryPrimitives.ReadUInt64LittleEndian(hand.AsSpan(19));
            Assert.Equal(1u, incarnation);
            Assert.Equal(7u, world);
            Assert.NotEqual(0ul, peerSerial);
            Assert.NotEqual(0ul, revision);
            await ReplyHandEdge(Server, declared.Body);

            string scope = $"{world:X8}{serial:X16}{peerSerial:X16}{body:X8}{revision:X16}";
            Call(Bridge, "OnGameEvent", "w214_figure_hand", $"{Peer} ok held {incarnation:x8} {scope}");
            var observed = await Read(Server);
            Assert.Equal(0x38, observed.Type);
            Assert.Equal(0x41, observed.Body[5]);
            byte[] expectedObservation = [1, 1, .. hand[2..43]];
            Assert.Equal(expectedObservation, observed.Body[10..]);
            await ReplyHandEdge(Server, observed.Body, known, observedClass ?? item);
            await Until(() => Get<System.Collections.IDictionary>(Bridge, "_w214EngineHands").Contains(Peer));
            return [0x82, 1, 2, (byte)weapon, 3, .. item.ToByteArray(), .. hand[7..27]];
        }
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
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Old_inflight_poll_after_down_then_wake_cannot_publish_or_poison_the_next_poll(bool host, bool sequenceKnown)
    {
        await using var r = await Rig.Create(host);
        var gate = Get<SemaphoreSlim>(r.Bridge, "_p6PollGate");
        if (sequenceKnown)
        {
            Call(r.Bridge, "Wo201RangedTick");
            await r.Accept;
            Assert.Equal(86, (await Read(r.Server)).Body[0]);
            await Reply(r.Server, 0xAC, 250, 86, Poll(r.Generation, 18));
            await Until(() => gate.CurrentCount == 1);
        }

        Call(r.Bridge, "Wo201RangedTick");
        await r.Accept;
        Assert.Equal(86, (await Read(r.Server)).Body[0]);
        await (Task)Call(r.Bridge, "OnLocalDownedAsync", true, (byte)0)!;
        await (Task)Call(r.Bridge, "OnLocalDownedAsync", false, (byte)0)!;
        await Until(() => gate.CurrentCount == 1);
        Assert.False(r.Link.Sent.Reader.TryRead(out _));

        Call(r.Bridge, "Wo201RangedTick");
        if (sequenceKnown)
        {
            Assert.Equal(86, (await Read(r.Server)).Body[0]);
            // The old reply arrives only after the replacement command reached the native pipe.
            await Reply(r.Server, 0xAC, 251, 86, Poll(r.Generation, 19, 3));
        }
        else
        {
            // Cancellation before the first reply leaves no sequence offset to distinguish the old rows.
            // The ambiguous stream must close before the replacement is written.
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            Assert.Equal(0, await r.Server.ReadAsync(new byte[1], stop.Token));
            await Until(() => gate.CurrentCount == 1);
            Assert.False(r.Link.Sent.Reader.TryRead(out _));
            r.Server.Disconnect();
            var accepted = r.Server.WaitForConnectionAsync(stop.Token);
            await Until(() => Environment.TickCount64 >= Get<long>(r.Bridge, "_w201RangedRetryAtMs"));
            Call(r.Bridge, "Wo201RangedTick");
            await accepted;
            Assert.Equal(86, (await Read(r.Server)).Body[0]);
        }

        await Until(() => Environment.TickCount64 > Get<long>(r.Bridge, "_p6VisualModeAtTick"));
        await Reply(r.Server, 0xAC, sequenceKnown ? (byte)252 : (byte)1, 86, Poll(r.Generation, 20, 1, 2, 3));
        foreach (byte phase in new byte[] { 1, 2, 3 })
        {
            var message = Message(await r.Link.Next());
            Assert.Equal(Protocol.W164RangedStep, message.Kind);
            Assert.True(RangedStepV2.TryDecode(message.Text, out var step));
            Assert.Equal(20ul, step.Epoch);
            Assert.Equal(phase, step.Phase);
        }
        await Until(() => gate.CurrentCount == 1);
        Assert.Equal(3, Count(r.Bridge, "ranged_out"));
        Assert.Equal(sequenceKnown ? 1 : 0, r.Pipe.StaleRepliesDropped);
        Assert.False(r.Link.Sent.Reader.TryRead(out _));
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
        await Until(() => r.Link.ReadRequestedAfterBytes >= horse.Length + incoming.Length);
        await lifecycle.GameplayAsync(lifecycle.Current.Generation, default); Set(r.Bridge, "_p1Loading", false); r.Game.ReleaseHorse.TrySetResult();
        await Until(() => Count(r.Bridge, "ranged_stale") > 0); Assert.False(r.Pipe.IsConnected);
        Assert.Equal(0, Count(r.Bridge, "ranged_missile_measured_accept")); Assert.Equal(0, Count(r.Bridge, "ranged_bow_played"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_receive_without_a_current_presentation_clock_never_requests_a_generic_fire(bool stamped)
    {
        await using var r = await Rig.Create();
        r.Receive();
        r.Link.Feed(stamped ? TimedFire(r.Peer) : Down(Protocol.W164RangedStep, Step().Encode(), r.Peer),
            Down(Protocol.W164RangedStep, "invalid-after-fire", r.Peer));

        Task processed = Until(() => Count(r.Bridge, "ranged_malformed") == 1);
        if (await Task.WhenAny(r.Accept, processed) == r.Accept)
        {
            var unexpected = await Read(r.Server);
            Assert.Fail($"Unchecked Fire opened native request 0x{unexpected.Type:X2}, op {unexpected.Body[0]}.");
        }
        await processed;
        Assert.Equal(1, Count(r.Bridge, "ranged_in"));
        Assert.Equal(0, Count(r.Bridge, "ranged_bow_played"));
        Assert.DoesNotContain(r.Game.Calls, x => x.Contains("W201Shot") || x.Contains("ApplyDamage") || x.Contains("CreateItem"));
        Assert.False(r.Link.Sent.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("unsupported")]
    [InlineData("unavailable")]
    [InlineData("refused")]
    public async Task Actual_receive_carries_the_bow_guid_to_typed_fire_and_never_falls_back_to_a_generic_shot(string outcome)
    {
        await using var r = await Rig.Create();
        await r.StartTimedReceiveAsync();
        byte[] expected = await r.ConfirmBowBeforeFireAsync();
        r.Link.Feed(TimedFire(r.Peer), Down(Protocol.W164RangedStep, "invalid-after-fire", r.Peer));

        var request = await Read(r.Server);
        Assert.Equal((byte)0x38, request.Type);
        Assert.Equal((byte)0x40, request.Body[5]);
        Assert.Equal(expected, request.Body[25..]);
        switch (outcome)
        {
            case "accepted": await ReplyEdge(r.Server, request.Body, EdgeOutcome.Completed, 1, 0, 0); break;
            case "unsupported": await ReplyEdge(r.Server, request.Body, EdgeOutcome.Unsupported); break;
            case "unavailable": await ReplyEdge(r.Server, request.Body, EdgeOutcome.Completed, 0, 0, 1); break;
            default: await ReplyEdge(r.Server, request.Body, EdgeOutcome.Completed, 0, 0, 2); break;
        }

        using var quiet = new CancellationTokenSource();
        byte[] header = new byte[3];
        Task<bool> followup = ReadFrameHeaderAsync(r.Server, header, quiet.Token);
        Task processed = Until(() => Count(r.Bridge, "ranged_malformed") == 1);
        if (await Task.WhenAny(followup, processed) == followup && await followup)
            Assert.Fail($"Typed Fire {outcome} opened a second native request 0x{header[0]:X2}.");
        await processed;
        quiet.Cancel();
        Assert.False(await followup);
        Assert.DoesNotContain(r.Game.Calls, x => x.Contains("W201Shot") || x.Contains("ApplyDamage") || x.Contains("CreateItem"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_fire_read_for_the_old_body_cannot_bind_to_a_replacement_before_processing(bool host)
    {
        await using var r = await Rig.Create(host);
        await r.StartTimedReceiveAsync();
        r.Game.BlockHorse = true;
        r.Http.BatchingEnabled = false;
        long received = r.Link.ReadRequestedAfterBytes;
        byte[] horse = Wire(Protocol.HorseInfoDown, r.Peer, 0);
        r.Link.Feed(horse);
        await r.Game.HorseEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        byte[] fire = TimedFire(r.Peer);
        r.Link.Feed(fire);
        await Until(() => r.Link.ReadRequestedAfterBytes >= received + horse.Length + fire.Length);
        Call(r.Bridge, "OnGameEvent", "ghostid", $"{r.Peer} df");
        Assert.Equal(223u, Get<ConcurrentDictionary<string, uint>>(r.Bridge, "_ghostEntityIds")[r.Peer.ToString()]);
        Assert.Equal(1, Get<ConcurrentDictionary<string, int>>(r.Bridge, "_ghostBodyGen")[r.Peer.ToString()]);

        r.Link.Feed(Down(Protocol.W164RangedStep, "after-old-body", r.Peer));
        r.Game.ReleaseHorse.TrySetResult();
        using var quiet = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        byte[] header = new byte[3];
        Task<bool> nextRequest = ReadFrameHeaderAsync(r.Server, header, quiet.Token);
        Task processed = Until(() => Count(r.Bridge, "ranged_malformed") == 1);
        if (await Task.WhenAny(nextRequest, processed) == nextRequest && await nextRequest)
        {
            byte[] body = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1))];
            await r.Server.ReadExactlyAsync(body, quiet.Token);
            await ReplyEdge(r.Server, body, EdgeOutcome.Completed, 1, 0, 0);
            Assert.Fail($"A Fire read for body 222 reached the native writer after replacement: op=0x{body[5]:X2}, entity={BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(11))}.");
        }
        await processed;
        quiet.Cancel();
        Assert.False(await nextRequest);
        Assert.Equal(1, Count(r.Bridge, "ranged_stale"));
        Assert.Equal(0, Count(r.Bridge, "ranged_in"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_mixed_reader_queue_preserves_the_fire_body_and_friendly_fire_contact_without_affecting_other_frames(bool host)
    {
        await using var r = await Rig.Create(host);
        await r.StartTimedReceiveAsync(friendlyFire: true);
        r.Game.BlockHorse = true;
        r.Http.BatchingEnabled = false;

        const ulong sourceIncarnation = 43, formerLife = 1001, currentLife = 1002;
        byte local = Get<byte>(r.Bridge, "_myGhostId");
        byte[] Contact(byte recipient, ulong sequence, float health, ulong life) => Wire(Protocol.PlayerHitV8Down,
            [r.Peer, .. new PlayerHitV8(recipient, 10, health, 0, 0).BuildUp(sequence, sourceIncarnation, life)[3..]]);
        async Task PublishNativeLifeAsync(ulong life)
        {
            byte[] proof = new byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64LittleEndian(proof, life);
            await r.Server.WriteAsync(Wire(0xBC, proof));
            await Until(() => r.Pipe.PvpNativeLife == life);
        }

        await PublishNativeLifeAsync(formerLife);

        long bytesRead = r.Link.ReadRequestedAfterBytes;
        byte[] horse = Wire(Protocol.HorseInfoDown, r.Peer, 0);
        r.Link.Feed(horse);
        await r.Game.HorseEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        byte[] retiredContact = Contact(local, 1, 5, formerLife);
        r.Link.Feed(retiredContact);
        bytesRead += horse.Length + retiredContact.Length;
        await Until(() => r.Link.ReadRequestedAfterBytes >= bytesRead);
        Call(r.Bridge, "T3NoteInputLifeEdge", true);
        Call(r.Bridge, "T3NoteInputLifeEdge", false);
        await PublishNativeLifeAsync(currentLife);

        byte[] retiredFire = TimedFire(r.Peer);
        r.Link.Feed(retiredFire);
        bytesRead += retiredFire.Length;
        await Until(() => r.Link.ReadRequestedAfterBytes >= bytesRead);
        Call(r.Bridge, "OnGameEvent", "ghostid", $"{r.Peer} df");

        byte[] currentContact = Contact(local, 2, 7, currentLife);
        byte[] wrongRecipient = Contact(r.Peer, 3, 9, currentLife);
        byte[] version = Wire(Protocol.ReleaseVersion, [r.Peer, .. "integration-test"u8]);
        long currentContactReadStart = Environment.TickCount64;
        r.Link.Feed(retiredContact, wrongRecipient, currentContact, currentContact, version);
        bytesRead += retiredContact.Length + wrongRecipient.Length + 2 * currentContact.Length + version.Length;
        await Until(() => r.Link.ReadRequestedAfterBytes >= bytesRead);
        long currentContactReadEnd = Environment.TickCount64;

        r.Game.ReleaseHorse.TrySetResult();
        var versions = Get<ConcurrentDictionary<byte, string>>(r.Bridge, "_ghostReleaseVersions");
        Task processed = Until(() => versions.ContainsKey(r.Peer));
        var request = Read(r.Server);
        Assert.Same(request, await Task.WhenAny(request, processed));
        var (type, body) = await request;
        Assert.Equal(0x1A, type);
        Assert.Equal(42, body.Length);
        Assert.Equal(7f, BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(4)));
        Assert.Equal(r.Peer, body[9]);
        Assert.Equal(2UL, BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(10)));
        Assert.Equal(sourceIncarnation, BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(18)));
        Assert.InRange((long)BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(26)), currentContactReadStart, currentContactReadEnd);
        Assert.Equal(currentLife, BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(34)));
        await r.Server.WriteAsync(Wire(0x81, 1, 1, (byte)PipeReason.PvpEffectObserved));

        await processed;
        Assert.Equal("integration-test", versions[r.Peer]);
        Assert.Equal(1, Count(r.Bridge, "ranged_stale"));
        Assert.Equal(0, Count(r.Bridge, "ranged_in"));
        Assert.Equal(1, Get<long>(r.Bridge, "_w121FfIn"));
        Assert.Equal(4, Get<long>(r.Bridge, "_w121FfInDropped"));
    }

    [Theory]
    [InlineData(true, RangedWeaponKind.Bow)]
    [InlineData(false, RangedWeaponKind.Crossbow)]
    public async Task Actual_receive_requests_one_typed_fire_and_keeps_uncharging_separate_from_fire(bool host, RangedWeaponKind weapon)
    {
        await using var r = await Rig.Create(host);
        await r.StartTimedReceiveAsync();
        Guid itemClass = weapon == RangedWeaponKind.Bow ? Bow : Crossbow;
        byte[] nativeHand = await r.ConfirmBowBeforeFireAsync(weapon: weapon);
        var fire = Step(seq: 10) with { Kind = weapon, ItemClass = itemClass };
        byte[] first = TimedRanged(r.Peer, fire);
        r.Link.Feed(first, first, TimedRanged(r.Peer, fire with { Epoch = fire.Epoch - 1, Sequence = 11 }),
            TimedRanged(r.Peer, fire with { Sequence = 11, Phase = 5 }), first,
            Down(Protocol.W164RangedStep, "after-uncharging", r.Peer));

        foreach (byte phase in new byte[] { 3, 5 })
        {
            var request = await Read(r.Server);
            Assert.Equal((byte)0x38, request.Type);
            Assert.Equal((byte)0x40, request.Body[5]);
            await ReplyEdge(r.Server, request.Body, EdgeOutcome.Completed, 1, 0, 0);
            byte[] expected = [.. nativeHand];
            expected[4] = phase;
            Assert.Equal(expected, request.Body[25..]);
        }

        await Until(() => Count(r.Bridge, "ranged_malformed") == 1);
        Assert.Equal(2, Count(r.Bridge, "ranged_duplicate"));
        Assert.Equal(1, Count(r.Bridge, "ranged_stale"));
        Assert.Equal(2, Get<long>(r.Bridge, "_w201RangedDispatched"));
        Assert.DoesNotContain(r.Game.Calls, call => call.Contains("W201Shot") || call.Contains("ApplyDamage"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_successful_Lua_hand_result_is_not_proof_when_the_native_slot_is_unknown_or_wrong(bool wrongClass)
    {
        await using var r = await Rig.Create();
        await r.StartTimedReceiveAsync();
        await r.ConfirmBowBeforeFireAsync(wrongClass ? (byte)3 : (byte)0, wrongClass ? Crossbow : Bow);
        using var quiet = new CancellationTokenSource();
        byte[] header = new byte[3];
        Task<bool> pipe = ReadFrameHeaderAsync(r.Server, header, quiet.Token);
        Task drained = Until(() => Count(r.Bridge, "ranged_malformed") == 1);
        r.Link.Feed(TimedFire(r.Peer), Down(Protocol.W164RangedStep, "after-unconfirmed-fire", r.Peer));
        if (await Task.WhenAny(pipe, drained) == pipe)
            Assert.False(await pipe, "Unknown or wrong physical hand queued a native request.");
        await drained;
        quiet.Cancel();
        Assert.False(await pipe);
        Assert.Equal(0, Get<long>(r.Bridge, "_w201RangedDispatched"));
    }

    [Theory]
    [InlineData(true, "new-eid")]
    [InlineData(true, "reused-eid")]
    [InlineData(true, "unchanged")]
    [InlineData(false, "new-eid")]
    [InlineData(false, "reused-eid")]
    [InlineData(false, "unchanged")]
    public async Task Ranged_fire_queued_by_the_real_reader_targets_only_its_original_body(bool receiverIsHost, string replacement)
    {
        await using var r = await Rig.Create(receiverIsHost);
        await r.StartTimedReceiveAsync();
        byte[] expected = await r.ConfirmBowBeforeFireAsync();
        r.Game.BlockHorse = true;
        r.Http.BatchingEnabled = false;
        long before = r.Link.ReadRequestedAfterBytes;
        byte[] horse = Wire(Protocol.HorseInfoDown, r.Peer, 0);
        r.Link.Feed(horse);
        await r.Game.HorseEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        byte[] fire = TimedFire(r.Peer);
        byte[] marker = Down(Protocol.W164RangedStep, "after-queued-fire", r.Peer);
        r.Link.Feed(fire, marker);
        await Until(() => r.Link.ReadRequestedAfterBytes >= before + horse.Length + fire.Length + marker.Length);
        string ghost = r.Peer.ToString();
        if (replacement == "new-eid") Get<ConcurrentDictionary<string, uint>>(r.Bridge, "_ghostEntityIds")[ghost] = 333;
        if (replacement != "unchanged") Call(r.Bridge, "BumpGhostGeneration", ghost, "fixture replaced body after read");

        using var quiet = new CancellationTokenSource();
        byte[] header = new byte[3];
        Task<bool> nativeRequest = ReadFrameHeaderAsync(r.Server, header, quiet.Token);
        Task drained = Until(() => Count(r.Bridge, "ranged_malformed") == 1);
        r.Game.ReleaseHorse.TrySetResult();
        bool dispatched = await Task.WhenAny(nativeRequest, drained) == nativeRequest && await nativeRequest;
        if (dispatched)
        {
            using var timeout = new CancellationTokenSource(3500);
            byte[] request = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1))];
            await r.Server.ReadExactlyAsync(request, timeout.Token);
            if (request[5] == 0x41) await ReplyHandEdge(r.Server, request);
            else await ReplyEdge(r.Server, request, EdgeOutcome.Completed, 1, 0, 0);
            Assert.Equal("unchanged", replacement);
            Assert.Equal(0x38, header[0]);
            Assert.Equal(0x40, request[5]);
            Assert.Equal(expected, request[25..]);
        }
        await drained;
        quiet.Cancel();
        if (replacement == "unchanged")
        {
            Assert.True(dispatched);
        }
        else
        {
            Assert.False(dispatched, $"An old native hand or Fire request crossed the body boundary ({replacement}).");
            Assert.Equal(0, Count(r.Bridge, "ranged_in"));
        }
    }

    private static async Task<bool> ReadFrameHeaderAsync(Stream stream, byte[] header, CancellationToken ct)
    {
        try { await stream.ReadExactlyAsync(header, ct); return true; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
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
    [InlineData("life")] [InlineData("world")] [InlineData("eof")] [InlineData("scene")] [InlineData("body")] [InlineData("peer")]
    [InlineData("hand")] [InlineData("other-class")] [InlineData("same-class")] [InlineData("redraw")]
    public async Task A_ranged_animation_waiting_for_the_physical_writer_loses_authority_after_a_newer_conflicting_state(string change)
    {
        await using var r = await Rig.Create(); await r.StartTimedReceiveAsync();
        byte[] expected = await r.ConfirmBowBeforeFireAsync();
        uint lastRequest = Get<uint>(r.Pipe, "_edgeNextId");
        long initialRead = r.Link.ReadRequestedAfterBytes;
        var writer = Get<SemaphoreSlim>(r.Pipe, "_writeLock"); await writer.WaitAsync(); byte[] frame = TimedFire(r.Peer); r.Link.Feed(frame);
        await Until(() => Get<uint>(r.Pipe, "_edgeNextId") != lastRequest);
        Assert.Equal(1, Count(r.Bridge, "ranged_in"));
        if (change == "life") { await (Task)Call(r.Bridge, "OnLocalDownedAsync", true, (byte)0)!; await (Task)Call(r.Bridge, "OnLocalDownedAsync", false, (byte)0)!; }
        if (change == "world") Get<Wo206Epochs>(r.Bridge, "_w206").LearnWorld(8, 2);
        if (change == "eof") { r.Link.End(); await Until(() => Get<Stream?>(r.Bridge, "_p6ReceiveStream") is null); }
        if (change == "scene") { Set(r.Bridge, "_w179Bus", false); byte[] scene = Down(Protocol.W164SceneBus, Wo179Rules.Start(1, 'V', "test_video")); r.Link.Feed(scene); await Until(() => r.Link.ReadRequestedAfterBytes >= initialRead + frame.Length + scene.Length); }
        if (change == "body") Call(r.Bridge, "BumpGhostGeneration", r.Peer.ToString(), "fixture retired the confirmed body");
        if (change == "peer") Call(r.Bridge, "P5PeerAtRead", (int)Protocol.Name, new byte[] { r.Peer, 121 });
        if (change is "hand" or "other-class" or "same-class" or "redraw")
        {
            string value = change is "hand" or "redraw" ? "v1|0|-" : $"v1|1|{(change == "other-class" ? Crossbow : Bow):D}";
            byte[] up = new LootMsg(Protocol.W203FigureHand, 124, value).BuildUp(Protocol.W203Up, 0);
            byte[] hand = Wire(Protocol.W203Down, [r.Peer, .. up[3..]]);
            r.Link.Feed(hand);
            int bytes = hand.Length;
            if (change == "redraw")
            {
                var redraw = new LootMsg(Protocol.W203FigureHand, 125, $"v1|1|{Bow:D}").BuildUp(Protocol.W203Up, 0);
                byte[] next = Wire(Protocol.W203Down, [r.Peer, .. redraw[3..]]);
                r.Link.Feed(next);
                bytes += next.Length;
            }
            await Until(() => r.Link.ReadRequestedAfterBytes >= initialRead + frame.Length + bytes);
        }
        writer.Release();
        if (change == "same-class")
        {
            var accepted = await Read(r.Server);
            Assert.Equal((byte)0x38, accepted.Type);
            Assert.Equal((byte)0x40, accepted.Body[5]);
            Assert.Equal(expected, accepted.Body[25..]);
            await ReplyEdge(r.Server, accepted.Body, EdgeOutcome.Completed, 1, 0, 0);
            await Until(() => Get<long>(r.Bridge, "_w201RangedDispatched") == 1);
            return;
        }
        await Task.Delay(80); Assert.Equal(0, Count(r.Bridge, "ranged_bow_played"));
        using var stop = new CancellationTokenSource(100);
        byte[] header = new byte[3];
        while (await ReadFrameHeaderAsync(r.Server, header, stop.Token))
        {
            Assert.Equal((byte)0x38, header[0]);
            byte[] body = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1))];
            await r.Server.ReadExactlyAsync(body, stop.Token);
            if (change is "hand" or "other-class" or "redraw")
            {
                Assert.Equal(0x41, body[5]);
                await ReplyHandEdge(r.Server, body);
            }
            else
            {
                // EOF also resets the controller independently of Fire.
                Assert.Equal("eof", change);
                Assert.Equal(0x3D, body[5]);
                await ReplyEdge(r.Server, body, EdgeOutcome.Completed, 1, 0, 0);
            }
        }
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
        await using var r = await Rig.Create(); Set(r.Bridge, "_t3ProcFrameTimeoutMs", 3000); await r.StartTimedReceiveAsync();
        await r.ConfirmBowBeforeFireAsync();
        uint lastRequest = Get<uint>(r.Pipe, "_edgeNextId");
        long initialRead = r.Link.ReadRequestedAfterBytes;
        var writer = Get<SemaphoreSlim>(r.Pipe, "_writeLock"); await writer.WaitAsync(); var hit = Hit();
        byte[] ranged = TimedFire(r.Peer), measured = Down(Protocol.W164MeasuredMissile, hit.Encode());
        r.Link.Feed(ranged, measured); await Until(() => r.Link.ReadRequestedAfterBytes >= initialRead + ranged.Length + measured.Length);
        await Until(() => Get<uint>(r.Pipe, "_edgeNextId") != lastRequest);
        byte[] wire; do { wire = await r.Link.Next(); } while (wire[0] == Protocol.PresentationClockUp);
        var ack = Message(wire); Assert.True(MeasuredMissileResult.TryDecode(ack.Text, out var result)); Assert.Equal(MeasuredMissileStatus.Expired, result.Status);
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
