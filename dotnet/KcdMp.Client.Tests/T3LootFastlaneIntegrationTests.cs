// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

[Collection("T3 connection lifecycle")]
public class T3LootFastlaneIntegrationTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private const string Cls = "4cea28a0-0814-405a-bf24-4fd711f7eb63";
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Set(object b, string field, object value) => b.GetType().GetField(field, Private)!.SetValue(b, value);
    private static T Get<T>(object b, string field) => (T)b.GetType().GetField(field, Private)!.GetValue(b)!;
    private static object? Call(GameBridge b, string method, params object?[] args) => typeof(GameBridge).GetMethod(method, Private)!.Invoke(b, args);
    private static void Where(GameBridge b, string value)
    {
        var f = typeof(GameBridge).GetField("_where", Private)!; f.SetValue(b, Enum.Parse(f.FieldType, value));
    }
    private static async Task Wait(Func<bool> ready)
    {
        using var stop = new CancellationTokenSource(4000);
        while (!ready()) await Task.Delay(5, stop.Token);
    }
    private sealed class Game(Func<string, CancellationToken, Task>? leaf = null) : HttpMessageHandler
    {
        public readonly ConcurrentQueue<string> Calls = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string command = Uri.UnescapeDataString(request.RequestUri!.Query);
            int at = command.IndexOf("command=", StringComparison.Ordinal);
            if (at >= 0) command = command[(at + "command=".Length)..];
            Calls.Enqueue(command);
            if (leaf is not null) await leaf(command, token);
            return new(HttpStatusCode.OK) { Content = new StringContent("ok") };
        }
        public string[] Loot => Calls.Where(s => Regex.IsMatch(s, @"KCD2MP_W(?:134(?:Host|BodyStateChunk|TakeResult|ItemResult|ItemGone)|217(?:Host|BodyListChunk|TakeResult))")).ToArray();
    }
    // Supplies real wire framing while letting the test decide exactly when EOF
    // occurs. The receive loop must keep reading while either processor waits.
    private sealed class WireStream : Stream
    {
        private readonly Channel<byte[]> _bytes = Channel.CreateUnbounded<byte[]>();
        private byte[]? _current;
        private int _at;
        private long _readBytes;
        public long ReadBytes => Interlocked.Read(ref _readBytes);
        public void Feed(params byte[][] frames) { foreach (var frame in frames) Assert.True(_bytes.Writer.TryWrite(frame)); }
        public void End() => _bytes.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_current is null || _at == _current.Length)
            {
                if (!await _bytes.Reader.WaitToReadAsync(cancellationToken)) return 0;
                if (!_bytes.Reader.TryRead(out _current)) continue;
                _at = 0;
            }
            int n = Math.Min(buffer.Length, _current.Length - _at);
            _current.AsMemory(_at, n).CopyTo(buffer); _at += n; Interlocked.Add(ref _readBytes, n); return n;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private static GameBridge Bridge(HttpGameTransport transport, bool host = false, uint world = 7)
    {
        var b = new GameBridge(new ClientConfig()); Set(b, "_transport", transport); Where(b, "World");
        Set(b, "_combatRoleApplied", true); Set(b, "_isDamageAuthority", host); Set(b, "_hostAuthority", true);
        Set(b, "_joinedWorld", !host); Set(b, "_sharedWorld", true);
        Set(b, "_t3StatsNextMs", long.MaxValue); // diagnostics must not probe a real native pipe in this harness
        var epochs = Get<Wo206Epochs>(b, "_w206"); epochs.LearnWorld(world, 1); epochs.NoteAck(1, world);
        return b;
    }
    private static byte[] Wire(byte type, params byte[] payload) => [type, (byte)payload.Length, (byte)(payload.Length >> 8), .. payload];
    private static byte[] Down(byte upType, byte kind, uint token, string text, byte source = 0)
    {
        var up = new LootMsg(kind, token, text).BuildUp(upType, 2);
        return Wire(Protocol.JoinWireFor(upType)!.Value.Down, [source, .. up[3..]]);
    }
    private static byte[] Answer(uint token, string verdict = "unknown") => Down(Protocol.LootHostUp, Protocol.LootHostItemResult, token, $"{verdict} {Cls} 1 2 3");
    private static Task Receive(GameBridge b, WireStream stream, CancellationToken token) => (Task)Call(b, "ReceiveLoopAsync", stream, token)!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_loot_families_reach_the_game_while_a_real_main_processor_handler_is_still_blocked(bool host)
    {
        var horse = Signal(); var release = Signal();
        var game = new Game(async (text, _) => { if (text.Contains("KCD2MP_SetGhostHorse")) { horse.TrySetResult(); await release.Task; } });
        await using var transport = new HttpGameTransport("http://localhost:1403", 30000, game) { BatchingEnabled = false };
        var b = Bridge(transport, host); using var stop = new CancellationTokenSource(); using var wire = new WireStream();
        Task receive = Receive(b, wire, stop.Token);
        try
        {
            wire.Feed(Wire(Protocol.HorseInfoDown, 1, 0)); await horse.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (host)
                wire.Feed(Down(Protocol.LootAskUp, Protocol.LootAskBodyOpen, 1, "bandit_1", 1),
                    Down(Protocol.LootAskUp, Protocol.LootAskTakeReq, 2, $"bandit_1 id_1 {Cls} 1", 1),
                    Down(Protocol.LootAskUp, Protocol.LootAskTakeAck, 2, "bandit_1 applied", 1));
            else
                wire.Feed(Answer(1), Down(Protocol.LootHostUp, Protocol.LootHostBodyList, 2, "bandit_1 3 1 1 -"),
                    Down(Protocol.LootHostUp, Protocol.LootHostTakeVerdict, 3, "grant bandit_1 id_1 - 0"));
            await Wait(() => game.Loot.Length == 3);
            Assert.False(release.Task.IsCompleted);
            string[] calls = game.Loot;
            Assert.Contains(host ? "W134HostOpen" : "W134ItemResult", calls[0]);
            Assert.Contains(host ? "W217HostTakeReq" : "W217BodyListChunk", calls[1]);
            Assert.Contains(host ? "W217HostAck" : "W217TakeResult", calls[2]);
        }
        finally { wire.End(); await receive.WaitAsync(TimeSpan.FromSeconds(2)); release.TrySetResult(); stop.Cancel(); }
    }

    [Fact]
    public async Task Loot_bypasses_a_real_batch_backlog_and_send_lock_while_prop_updates_stay_queued()
    {
        var game = new Game();
        await using var transport = new HttpGameTransport("http://localhost:1403", 5000, game);
        var send = Get<SemaphoreSlim>(transport, "_sendLock"); await send.WaitAsync();
        var b = Bridge(transport); using var stop = new CancellationTokenSource(); using var wire = new WireStream();
        Task receive = Receive(b, wire, stop.Token);
        try
        {
            await transport.ExecuteAsync("KCD2MP_UpdateGhost(9,1)");
            wire.Feed(Answer(1), Down(Protocol.LootHostUp, Protocol.LootHostProp, 0, "prop1|BasicEntity|-|-|1|2|3|0|0|0"), Answer(2));
            await Wait(() => game.Loot.Length == 2);
            Assert.Equal(2, Get<BatchQueue>(transport, "_queue").Count);
            Assert.DoesNotContain(game.Calls, s => s.Contains("KCD2MP_W217Prop("));
            Assert.Equal(0, transport.OversizeDropped);
        }
        finally { wire.End(); await receive.WaitAsync(TimeSpan.FromSeconds(2)); stop.Cancel(); send.Release(); }
    }

    [Fact]
    public async Task Body_parts_and_verdicts_keep_wire_order_without_coalescing()
    {
        var game = new Game();
        await using var transport = new HttpGameTransport("http://localhost:1403", 5000, game);
        var b = Bridge(transport); using var stop = new CancellationTokenSource(); using var wire = new WireStream();
        Task receive = Receive(b, wire, stop.Token);
        try
        {
            wire.Feed(Down(Protocol.LootHostUp, Protocol.LootHostBodyList, 81, "bandit_1 12 1 2 -"),
                Down(Protocol.LootHostUp, Protocol.LootHostBodyList, 81, "bandit_1 12 2 2 -"),
                Down(Protocol.LootHostUp, Protocol.LootHostTakeVerdict, 91, "grant bandit_1 id_1 - 0"), Answer(92));
            await Wait(() => game.Loot.Length == 4);
            string[] calls = game.Loot;
            Assert.Contains("(\"bandit_1\", 12, 1, 2, 1, 1, {}, \"81\", \"0\")", calls[0]);
            Assert.Contains("(\"bandit_1\", 12, 2, 2, 1, 1, {}, \"81\", \"0\")", calls[1]);
            Assert.Contains("W217TakeResult(\"91\"", calls[2]); Assert.Contains("W134ItemResult(\"92\"", calls[3]);
            Assert.Equal(0, Get<BatchQueue>(transport, "_queue").Count);
        }
        finally { wire.End(); await receive.WaitAsync(TimeSpan.FromSeconds(2)); stop.Cancel(); }
    }

    [Fact]
    public async Task An_epoch_read_ahead_of_the_lane_drops_queued_old_loot_and_does_not_change_main_processor_stamp()
    {
        var entered = Signal(); var release = Signal();
        var game = new Game(async (text, _) => { if (text.Contains("W134ItemResult(\"1\"")) { entered.TrySetResult(); await release.Task; } });
        await using var transport = new HttpGameTransport("http://localhost:1403", 30000, game);
        var b = Bridge(transport); Set(b, "_w206FrameStamp", 456u);
        using var stop = new CancellationTokenSource(); using var wire = new WireStream(); Task receive = Receive(b, wire, stop.Token);
        try
        {
            wire.Feed(Answer(1)); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            wire.Feed(Answer(2), Down(Protocol.W203Up, Protocol.W203Epoch, 0, Wo206Rules.EpochText(8, 2, "load")), Answer(3));
            await Wait(() => Get<Wo206Epochs>(b, "_w206").World == 8);
            release.TrySetResult(); await Wait(() => game.Loot.Any(s => s.Contains("W134ItemResult(\"3\"")));
            Assert.DoesNotContain(game.Loot, s => s.Contains("W134ItemResult(\"2\""));
            Assert.Equal(456u, Get<uint>(b, "_w206FrameStamp"));
            Assert.Equal(1, Get<ConcurrentDictionary<string, long>>(b, "_t3Counters")["loot_fastlane_stale"]);
        }
        finally { release.TrySetResult(); wire.End(); await receive.WaitAsync(TimeSpan.FromSeconds(2)); stop.Cancel(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mutations_require_a_known_current_epoch_but_build_metadata_still_works_before_join(bool host)
    {
        var game = new Game();
        await using var transport = new HttpGameTransport("http://localhost:1403", 5000, game);
        var b = Bridge(transport, host, 0); using var stop = new CancellationTokenSource(); using var wire = new WireStream();
        Task receive = Receive(b, wire, stop.Token);
        try
        {
            wire.Feed(host ? Down(Protocol.LootAskUp, Protocol.LootAskItemTake, 1, $"{Cls} 1 2 3 0", 1) : Answer(1));
            await Wait(() => Get<ConcurrentDictionary<string, long>>(b, "_t3Counters").ContainsKey("loot_fastlane_unknown_epoch"));
            Assert.Empty(game.Loot);
            if (!host)
            {
                wire.Feed(Down(Protocol.LootHostUp, Protocol.LootHostBuild, 0, "1.5.5"));
                await Wait(() => Get<string?>(b, "_peerHostBuild") == "1.5.5");
            }
            var epochs = Get<Wo206Epochs>(b, "_w206"); epochs.LearnWorld(7, 1); epochs.NoteAck(1, 7);
            wire.Feed(host ? Down(Protocol.LootAskUp, Protocol.LootAskItemTake, 2, $"{Cls} 1 2 3 0", 1) : Answer(2));
            await Wait(() => game.Loot.Length == 1);
        }
        finally { wire.End(); await receive.WaitAsync(TimeSpan.FromSeconds(2)); stop.Cancel(); }
    }

    [Fact]
    public async Task A_non_session_http_timeout_abandons_one_loot_frame_and_the_next_reply_still_runs()
    {
        var game = new Game((text, _) => text.Contains("W134ItemResult(\"1\"")
            ? Task.FromException(new TaskCanceledException("simulated HTTP timeout")) : Task.CompletedTask);
        await using var transport = new HttpGameTransport("http://localhost:1403", 5000, game);
        var b = Bridge(transport); using var stop = new CancellationTokenSource(); using var wire = new WireStream();
        Task receive = Receive(b, wire, stop.Token);
        try
        {
            wire.Feed(Answer(1), Answer(2)); await Wait(() => game.Loot.Length == 2);
            Assert.False(receive.IsCompleted);
            Assert.Equal(1, Get<ConcurrentDictionary<string, long>>(b, "_t3Counters")["proc_frames_abandoned"]);
        }
        finally { wire.End(); await receive.WaitAsync(TimeSpan.FromSeconds(2)); stop.Cancel(); }
    }

    [Fact]
    public async Task EOF_revokes_inflight_loot_drains_only_its_rows_and_a_fresh_receive_still_handles_loot()
    {
        long initial = Wo207Trace.WaitingTotal; const int sentinel = 0xEF; Wo207Trace.Enqueued(sentinel);
        var entered = Signal(); var release = Signal(); var returned = Signal(); ProcessorFrameScope.Lease? leaf = null;
        var game = new Game(async (text, _) =>
        {
            if (text.Contains("W217BodyListChunk"))
            {
                leaf = ProcessorFrameScope.Capture(); entered.TrySetResult();
                try { await release.Task; } finally { returned.TrySetResult(); }
            }
        });
        await using var transport = new HttpGameTransport("http://localhost:1403", 30000, game);
        var b = Bridge(transport); using var stop = new CancellationTokenSource(); using var wire = new WireStream();
        Task receive = Receive(b, wire, stop.Token);
        try
        {
            wire.Feed(Down(Protocol.LootHostUp, Protocol.LootHostBodyList, 1, "bandit_1 3 1 1 -"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            for (uint i = 2; i < 12; i++) wire.Feed(Answer(i));
            await Wait(() => Wo207Trace.WaitingTotal == initial + 11);
            wire.End(); await receive.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(leaf!.IsAbandoned); Assert.Equal(initial + 1, Wo207Trace.WaitingTotal);
            Assert.False(returned.Task.IsCompleted);
            release.TrySetResult(); await returned.Task.WaitAsync(TimeSpan.FromSeconds(2));
            using var next = new WireStream(); Task nextReceive = Receive(b, next, stop.Token);
            try
            {
                next.Feed(Answer(99)); await Wait(() => game.Loot.Any(s => s.Contains("W134ItemResult(\"99\"")));
                Assert.Equal(2, game.Loot.Length); Assert.True(leaf.IsAbandoned);
            }
            finally { next.End(); await nextReceive.WaitAsync(TimeSpan.FromSeconds(2)); }
        }
        finally { release.TrySetResult(); wire.End(); stop.Cancel(); await receive; Wo207Trace.Dequeued(sentinel); }
        Assert.Equal(initial, Wo207Trace.WaitingTotal);
    }



    private static HostAuthorityLifecycle Lifecycle() => new((phase, generation, _) =>
        Task.FromResult<HostAuthorityLifecycle.Ack?>(new(true, phase == HostAuthorityLifecycle.Query ? HostAuthorityLifecycle.Load : phase,
            phase != HostAuthorityLifecycle.Disabled, false, generation, 0)));

    [Fact]
    public async Task Local_reload_generation_is_captured_at_read_and_cannot_be_replaced_when_the_lane_catches_up()
    {
        var entered = Signal(); var release = Signal();
        var game = new Game(async (text, _) => { if (text.Contains("W134ItemResult(\"1\"")) { entered.TrySetResult(); await release.Task; } });
        await using var transport = new HttpGameTransport("http://localhost:1403", 30000, game);
        var b = Bridge(transport); var lifecycle = Lifecycle(); await lifecycle.PendingAsync(true, true, default); Set(b, "_p1Lifecycle", lifecycle);
        using var stop = new CancellationTokenSource(); using var wire = new WireStream(); Task receive = Receive(b, wire, stop.Token);
        try
        {
            wire.Feed(Answer(1)); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            long waiting = Wo207Trace.WaitingTotal;
            // The reader counts the queued frame after capturing its generation.
            wire.Feed(Answer(2)); await Wait(() => Wo207Trace.WaitingTotal == waiting + 1);
            await lifecycle.PendingAsync(true, true, default); Assert.Equal(2ul, lifecycle.Current.Generation);
            wire.Feed(Answer(3)); release.TrySetResult();
            await Wait(() => game.Loot.Any(s => s.Contains("W134ItemResult(\"3\"")));
            Assert.Contains("W134ItemResult(\"1\", \"unknown\", \"1\")", game.Loot[0]);
            Assert.DoesNotContain(game.Loot, s => s.Contains("W134ItemResult(\"2\""));
            Assert.Contains(game.Loot, s => s.Contains("W134ItemResult(\"3\", \"unknown\", \"2\")"));
        }
        finally { release.TrySetResult(); wire.End(); await receive.WaitAsync(TimeSpan.FromSeconds(2)); stop.Cancel(); }
    }

    [Fact]
    public async Task The_load_sequence_retires_an_admitted_inventory_frame_before_another_world_can_apply_it()
    {
        var entered = Signal();
        var release = Signal();
        var game = new Game(async (text, _) =>
        {
            if (text.Contains("W134ItemResult(\"1\"")) { entered.TrySetResult(); await release.Task; }
        });
        await using var transport = new HttpGameTransport("http://localhost:1403", 30000, game);
        var bridge = Bridge(transport);
        var load = Get<WorldLoadSequence>(bridge, "_worldLoad");
        Assert.True(load.TryBegin(0, WorldLoadStart.AdoptLoadedWorld, 7));
        Set(bridge, "_loadSequenceRuntime", true);
        using var stop = new CancellationTokenSource();
        using var wire = new WireStream();
        Task receive = Receive(bridge, wire, stop.Token);
        try
        {
            wire.Feed(Answer(1));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            long waiting = Wo207Trace.WaitingTotal;
            wire.Feed(Answer(2));
            await Wait(() => Wo207Trace.WaitingTotal == waiting + 1);

            Call(bridge, "LoadSequenceBeginLoad", false);
            var active = load.Current;
            Assert.Equal((2ul, WorldLoadPhase.Load), (active.Generation, active.Phase));
            Assert.True(load.TryAdvance(active.Generation, WorldLoadPhase.Reconstruct));
            Assert.True(load.TryAdvance(active.Generation, WorldLoadPhase.Settle));
            Assert.True(load.TryAdvance(active.Generation, WorldLoadPhase.Play));
            release.TrySetResult();
            wire.Feed(Answer(3));

            await Wait(() => game.Loot.Any(text => text.Contains("W134ItemResult(\"3\"")));
            Assert.DoesNotContain(game.Loot, text => text.Contains("W134ItemResult(\"2\""));
            Assert.Equal(1, Get<long>(bridge, "_staleGenerationDropped"));
            Assert.Equal(0, Get<long>(bridge, "_loadScopeDropped"));
        }
        finally
        {
            release.TrySetResult();
            wire.End();
            await receive.WaitAsync(TimeSpan.FromSeconds(3));
            stop.Cancel();
        }
    }

    [Fact]
    public async Task Immediate_host_requests_carry_the_captured_generation_and_keep_take_put_argument_positions()
    {
        var game = new Game(); await using var transport = new HttpGameTransport("http://localhost:1403", 5000, game);
        var b = Bridge(transport, true); var lifecycle = Lifecycle(); await lifecycle.PendingAsync(true, true, default); Set(b, "_p1Lifecycle", lifecycle);
        using var stop = new CancellationTokenSource(); using var wire = new WireStream(); Task receive = Receive(b, wire, stop.Token);
        try
        {
            wire.Feed(Down(Protocol.LootAskUp, Protocol.LootAskBodyOpen, 1, "bandit_1", 1),
                Down(Protocol.LootAskUp, Protocol.LootAskBodyTake, 2, $"bandit_1 {Cls} 1 0.85", 1),
                Down(Protocol.LootAskUp, Protocol.LootAskBodyPut, 3, $"bandit_1 {Cls} 1 0.85", 1),
                Down(Protocol.LootAskUp, Protocol.LootAskItemTake, 4, $"{Cls} 1 2 3 0", 1),
                Down(Protocol.LootAskUp, Protocol.LootAskTakeReq, 5, $"bandit_1 id_1 {Cls} 1", 1),
                Down(Protocol.LootAskUp, Protocol.LootAskTakeAck, 5, "bandit_1 applied", 1),
                Down(Protocol.LootAskUp, Protocol.LootAskBodyApplied, 0, "bandit_1 9 ok", 1),
                Down(Protocol.LootAskUp, Protocol.LootAskLiveEnd, 0, "bandit_1", 1));
            await Wait(() => game.Loot.Length == 8);
            string[] calls = game.Loot; Assert.All(calls, s => Assert.Contains(", \"1\")", s));
            Assert.Contains($"W134HostTake(1, 2, \"bandit_1\", \"{Cls}\", 1, 0.85, false, \"1\")", calls[1]);
            Assert.Contains($"W134HostPut(1, 3, \"bandit_1\", \"{Cls}\", 1, 0.85, \"1\")", calls[2]);
            Assert.Contains(", 0, nil, \"1\")", calls[3]);
            Assert.Contains("W217HostStateAck(1, \"bandit_1\", 9, true, \"1\")", calls[6]);
            Assert.Contains("W217HostEnd(1, \"bandit_1\", \"1\")", calls[7]);
        }
        finally { wire.End(); await receive.WaitAsync(TimeSpan.FromSeconds(2)); stop.Cancel(); }
    }

    [Theory]
    [InlineData("Loading", false)]
    [InlineData("Menu", false)]
    [InlineData("World", true)]
    public async Task Load_holds_allow_ticket_replies_but_menu_and_separate_world_keep_inventory_writes_out(string where, bool separate)
    {
        var game = new Game(); await using var transport = new HttpGameTransport("http://localhost:1403", 5000, game);
        var b = Bridge(transport); Where(b, where); Set(b, "_w140Separate", separate);
        using var stop = new CancellationTokenSource(); using var wire = new WireStream(); Task receive = Receive(b, wire, stop.Token);
        try
        {
            wire.Feed(Answer(31), Down(Protocol.LootHostUp, Protocol.LootHostBodyList, 32, "bandit_1 3 1 1 -"));
            if (separate) await Wait(() => Get<long>(b, "_w140SepDropped") == 2);
            else await Wait(() => Get<ConcurrentDictionary<string, long>>(b, "_t3Counters").TryGetValue("loot_fastlane_frames", out long n) && n == 2);
            if (where == "Loading") await Wait(() => game.Loot.Length == 2);
            else Assert.Empty(game.Loot);
            Assert.Equal(0, Get<BatchQueue>(transport, "_queue").Count);
        }
        finally { wire.End(); await receive.WaitAsync(TimeSpan.FromSeconds(2)); stop.Cancel(); }
    }

    [Fact]
    public async Task Timed_out_loot_does_not_hold_the_next_reply_or_send_its_remaining_subchunk_late()
    {
        var entered = Signal(); var release = Signal(); var returned = Signal(); ProcessorFrameScope.Lease? leaf = null;
        var game = new Game(async (text, _) =>
        {
            if (text.Contains("W217BodyListChunk"))
            {
                leaf = ProcessorFrameScope.Capture(); entered.TrySetResult();
                try { await release.Task; } finally { returned.TrySetResult(); }
            }
        });
        await using var transport = new HttpGameTransport("http://localhost:1403", 30000, game);
        var b = Bridge(transport); Set(b, "_t3ProcFrameTimeoutMs", 100);
        using var stop = new CancellationTokenSource(); using var wire = new WireStream(); Task receive = Receive(b, wire, stop.Token);
        try
        {
            string rows = string.Join(',', Enumerable.Range(0, 8).Select(i => $"{new string('u', 39)}{i}:{Cls}:1:0.85:0:1:{new string('o', 63)}"));
            wire.Feed(Down(Protocol.LootHostUp, Protocol.LootHostBodyList, 41, $"bandit_1 3 1 1 {rows}"), Answer(42));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Wait(() => game.Loot.Any(s => s.Contains("W134ItemResult(\"42\"")));
            Assert.True(leaf!.IsAbandoned); Assert.False(returned.Task.IsCompleted);
            release.TrySetResult(); await returned.Task.WaitAsync(TimeSpan.FromSeconds(2));
            // A later FIFO reply fences the still-running old continuation.
            wire.Feed(Answer(43)); await Wait(() => game.Loot.Any(s => s.Contains("W134ItemResult(\"43\"")));
            Assert.Single(game.Loot.Where(s => s.Contains("W217BodyListChunk")));
            Assert.Equal(1, Get<ConcurrentDictionary<string, long>>(b, "_t3Counters")["proc_stalls"]);
        }
        finally { release.TrySetResult(); wire.End(); await receive.WaitAsync(TimeSpan.FromSeconds(2)); stop.Cancel(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legal_large_wire_parts_are_delivered_in_safe_http_subchunks_without_losing_parts_or_receipt(bool live)
    {
        string body = new('b', 80);
        string rows = live ? string.Join(',', Enumerable.Range(0, 8).Select(i => $"{new string('u', 39)}{i}:{Cls}:1:0.85:0:1:{new string('o', 63)}"))
            : string.Join(',', Enumerable.Repeat($"{Cls}:1:0.85:w", 25));
        string text = live ? $"{body} 17 1 1 {rows}" : $"{body} open 1 1 1 {rows}";
        Assert.InRange(text.Length, 1, Protocol.LootTextMax);
        var game = new Game(); await using var transport = new HttpGameTransport("http://localhost:1403", 5000, game);
        var b = Bridge(transport); using var stop = new CancellationTokenSource(); using var wire = new WireStream();
        Task receive = Receive(b, wire, stop.Token);
        try
        {
            wire.Feed(Down(Protocol.LootHostUp, live ? Protocol.LootHostBodyList : Protocol.LootHostBodyState, 123, text), Answer(999));
            await Wait(() => game.Loot.Any(s => s.Contains("W134ItemResult(\"999\"")));
            string name = live ? "W217BodyListChunk" : "W134BodyStateChunk";
            var chunks = game.Loot.Where(s => s.Contains(name)).ToArray(); Assert.True(chunks.Length > 1);
            Assert.All(chunks, s => Assert.InRange(Uri.EscapeDataString(s).Length, 1, LuaCommandBudget.MaxEncodedCommandChars));
            Assert.All(chunks, s => Assert.Contains(live ? $"(\"{body}\", 17, 1, 1," : $"(\"{body}\", \"open\", 1, 1, 1,", s));
            Assert.Equal(live ? 8 : 25, chunks.Sum(s => Regex.Matches(s, live ? "id=" : Cls).Count));
            if (live) Assert.All(chunks, s => Assert.Contains(", \"123\", \"0\")", s));
            Assert.Equal(0, transport.OversizeDropped); Assert.Equal(0, Get<BatchQueue>(transport, "_queue").Count);
        }
        finally { wire.End(); await receive.WaitAsync(TimeSpan.FromSeconds(2)); stop.Cancel(); }
    }
}
