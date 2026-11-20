// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Collections;
using System.IO.Pipes;
using System.Reflection;
using System.Threading.Channels;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public sealed class QuestMutationBoundaryTests
{
    private const string State = "Barbora.trosecko.auditQuest.State";
    private const string Dialogue = "Barbora.trosecko.auditQuest.Dialogue";

    [Fact]
    public async Task Competing_host_action_after_query_refuses_the_old_quest_request()
    {
        await using var f = await Fixture.OpenAsync();
        var ask = new QuestChange(1, QuestChange.FNotify, 1, 2, "SetNext", "int", State, 26);
        await f.SendAsync(Protocol.QuestAskRequest, 1, Wo137Rules.RequestText(ask));
        var reply = await f.ReadQuestReplyAsync(Protocol.QuestHostResult);
        Assert.True(Wo137Rules.TryParseResultText(reply.Text, out string verdict, out int value, out _, out _));
        Console.WriteLine($"QUEST-APPLY-RACE queried=1 competitor=3 final={f.Vital} pulses={f.Pulses} verdict={verdict} reported={value}");
        Assert.Equal(("refused", 3, 0, 3), (verdict, value, f.Pulses, f.Vital));

        f.Compete = false;
        await f.SendAsync(Protocol.QuestAskRequest, 2, Wo137Rules.RequestText(ask with { Old = 3 }));
        var fresh = await f.ReadQuestReplyAsync(Protocol.QuestHostResult);
        Assert.True(Wo137Rules.TryParseResultText(fresh.Text, out verdict, out value, out _, out _));
        Assert.Equal(("applied", 2, 1, 2), (verdict, value, f.Pulses, f.Vital));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Held_outport_mutates_only_the_world_it_waited_for(bool reload)
    {
        await using var f = await Fixture.OpenAsync();
        f.SetWhere("Loading");
        await f.SendAsync(Protocol.QuestAskOutPortReplay, 9, Wo200Rules.ReplayText(7, 1, "Out", Dialogue));
        await f.WaitForQueuedReplayAsync();
        Assert.Equal(0, f.Replays);
        if (reload) f.Reload();
        f.SetWhere("World");

        await f.FinishReplayQueueAsync();
        // A Pong from the guest's drain does not wait for the host's write.
        // In the live case, wait for the actual ACK before checking for extras.
        if (!reload)
        {
            var reply = await f.ReadQuestReplyAsync(Protocol.QuestHostReplayAck);
            Assert.Equal((9u, "7 ran 0 " + Dialogue), (reply.Tok, reply.Text));
        }
        var extraReplies = await f.DrainQuestRepliesAsync();
        Console.WriteLine($"QUEST-REPLAY-RELOAD reload={(reload ? 1 : 0)} replays={f.Replays} acks={extraReplies.Length + (reload ? 0 : 1)}");
        Assert.Equal(reload ? 0 : 1, f.Replays);
        Assert.Empty(extraReplies);
        Assert.Equal(0, f.QueuedReplays);

        await f.SendAsync(Protocol.QuestAskOutPortReplay, 10, Wo200Rules.ReplayText(8, 2, "Out", Dialogue));
        var fresh = await f.ReadQuestReplyAsync(Protocol.QuestHostReplayAck);
        Assert.Equal("8 ran 0 " + Dialogue, fresh.Text);
        Assert.Equal(reload ? 1 : 2, f.Replays);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(15));
        private readonly GameBridge _bridge = new(new ClientConfig());
        private readonly ReplayRelay _relay;
        private readonly ReplayPeer _host, _guest;
        private readonly NamedPipeServerStream _native;
        private readonly CombatPipe _combat;
        private readonly Channel<Func<Task>> _work = Channel.CreateUnbounded<Func<Task>>();
        private readonly Task _receiver, _worker, _pipe;
        private int _vital = 1, _pulses, _replays;

        private Fixture(ReplayRelay relay, ReplayPeer host, ReplayPeer guest)
        {
            _relay = relay;
            _host = host;
            _guest = guest;
            string name = $"kct-quest-{Guid.NewGuid():N}";
            _native = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            _combat = new(name);
            SetWhere("World");
            Set("_combatRoleApplied", true);
            Set("_isDamageAuthority", true);
            Set("_hostAuthority", true);
            Set("_sharedWorld", true);
            Set("_myGhostId", _host.Id);
            Set("_combat", _combat);
            Set("_w137Work", _work);
            Set("_wo122Stream", _host.Wire);
            Set("_wo122Ct", _stop.Token);
            Set("_t3StatsNextMs", long.MaxValue);
            // The destructive-policy branch has separate tests. This case
            // reaches the regular query, authorization and native mutation.
            Set("_w147QuestSafety", false);
            Epochs.EnsureHost();
            Epochs.NoteAck(_guest.Id, Epochs.World);
            _pipe = ServeNativeAsync();
            _worker = (Task)Call("Wo137WorkLoopAsync", _work.Reader, _stop.Token);
            _receiver = (Task)Call("ReceiveLoopAsync", _host.Wire, _stop.Token);
        }

        public static async Task<Fixture> OpenAsync()
        {
            var relay = await ReplayRelay.StartAsync();
            var host = await ReplayPeer.ConnectAsync(relay.Port, "QuestHost");
            var guest = await ReplayPeer.ConnectAsync(relay.Port, "QuestGuest");
            await host.DrainAsync();
            return new Fixture(relay, host, guest);
        }

        public int Vital => Volatile.Read(ref _vital);
        public int Pulses => Volatile.Read(ref _pulses);
        public int Replays => Volatile.Read(ref _replays);
        public bool Compete { get; set; } = true;
        public int QueuedReplays => ((ICollection)Get("_w200HostQueue")).Count;
        private Wo206Epochs Epochs => (Wo206Epochs)Get("_w206");

        public void SetWhere(string place) => Set("_where", Enum.Parse(Field("_where").FieldType, place));
        public void Reload()
        {
            Call("Wo206HostWorldLoad", "fixture-reload");
            Epochs.NoteAck(_guest.Id, Epochs.World);
        }

        private bool ReplayQueueBusy => (int)Get("_w200HostBusy") != 0;

        public async Task FinishReplayQueueAsync()
        {
            // The Reader can already own this tick. Wait for that worker
            // as well, including the old-world case that sends no reply.
            while (true)
            {
                await (Task)Call("Wo200HostQueueTickAsync");
                if (QueuedReplays == 0 && !ReplayQueueBusy) return;
                await Task.Delay(1, _stop.Token);
            }
        }

        public async Task WaitForQueuedReplayAsync()
        {
            while (QueuedReplays == 0 || ReplayQueueBusy)
                await Task.Delay(1, _stop.Token);
        }

        public ValueTask SendAsync(byte kind, uint token, string text) =>
            _guest.SendAsync(new LootMsg(kind, token, text).BuildUp(Protocol.QuestAskUp, _host.Id), _stop.Token);

        public async Task<LootMsg> ReadQuestReplyAsync(byte kind)
        {
            var payload = await _guest.ReadUntilAsync(Protocol.QuestHostDown, _stop.Token);
            var msg = DecodeReply(payload);
            Assert.Equal(kind, msg.Kind);
            return msg;
        }

        private LootMsg DecodeReply(byte[] payload)
        {
            Assert.True(Protocol.TrySplitJoinDown(payload, out var source, out _, out _, out var body));
            Assert.Equal(_host.Id, source);
            Assert.True(LootMsg.TryDecode(body.ToArray(), out var msg));
            return msg;
        }

        public async Task<LootMsg[]> DrainQuestRepliesAsync()
        {
            var replies = new List<LootMsg>();
            foreach (var packet in await _guest.DrainAsync(_stop.Token))
            {
                if (packet.Type != Protocol.QuestHostDown) continue;
                replies.Add(DecodeReply(packet.Payload));
            }
            return [.. replies];
        }

        private async Task ServeNativeAsync()
        {
            var ct = _stop.Token;
            try
            {
                await _native.WaitForConnectionAsync(ct);
                byte seq = 0;
                var header = new byte[3];
                while (await _native.ReadAtLeastAsync(header, header.Length, false, ct) == header.Length)
                {
                    var body = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1))];
                    await _native.ReadExactlyAsync(body, ct);
                    var reply = NativeReply(header[0], ++seq, body);
                    await _native.WriteAsync(reply, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }

        private byte[] NativeReply(byte type, byte sequence, byte[] body)
        {
            byte op = body[0];
            if (type == 0x23 && op == 3)
            {
                byte[] read = [1, 1, 0, 1, 0, 0, 0, 0];
                BinaryPrimitives.WriteInt32LittleEndian(read.AsSpan(4), Vital);
                return ReplayPeer.Frame(0x9D, [1, sequence, op, 0, .. read]);
            }
            if (type == 0x23 && op is 2 or 10)
            {
                if (Compete) Volatile.Write(ref _vital, 3);
                int before = Vital;
                // Model the old unconditional engine pulse and the new
                // wire request's compare at dispatch. Native guard behavior
                // is exercised separately through wo137.cpp.
                bool stale = op == 10 && before != BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(body.Length - 8));
                if (!stale)
                {
                    Interlocked.Increment(ref _pulses);
                    Volatile.Write(ref _vital, 2);
                }
                byte[] applied = [(byte)(stale ? 12 : 0), 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 3, (byte)'i', (byte)'n', (byte)'t'];
                BinaryPrimitives.WriteInt32LittleEndian(applied.AsSpan(2), before);
                BinaryPrimitives.WriteInt32LittleEndian(applied.AsSpan(7), Vital);
                return ReplayPeer.Frame(0x9D, [1, sequence, op, 0, .. applied]);
            }
            Assert.Equal((0x2B, (int)Protocol.Wo200OpReplay), ((int)type, (int)op));
            Interlocked.Increment(ref _replays);
            return ReplayPeer.Frame(0xAC, [1, sequence, op, 0, 1, 0]);
        }

        private static FieldInfo Field(string name) => typeof(GameBridge).GetField(name, Private)!;
        private void Set(string name, object value) => Field(name).SetValue(_bridge, value);
        private object Get(string name) => Field(name).GetValue(_bridge)!;
        private object Call(string name, params object[] args) =>
            typeof(GameBridge).GetMethod(name, Private)!.Invoke(_bridge, args)!;

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _work.Writer.TryComplete();
            await _combat.DisposeAsync();
            try { await Task.WhenAll(_receiver, _worker, _pipe); }
            finally
            {
                await _host.DisposeAsync();
                await _guest.DisposeAsync();
                await _relay.DisposeAsync();
                await _native.DisposeAsync();
                _stop.Dispose();
            }
        }
    }
}
