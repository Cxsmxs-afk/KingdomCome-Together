// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Reflection;
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
