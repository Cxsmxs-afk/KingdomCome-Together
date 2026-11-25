// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Reflection;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public class Wo216LifecycleCodecTests
{
    [Fact]
    public void Request_is_exactly_13_bytes_with_unsigned_little_endian_generation_and_request_id()
    {
        Assert.Equal(new byte[] { 1, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB, 0xCC },
            Wo216LifecycleCodec.BuildArgs(Protocol.Wo216LifecycleLoad, 0x8877665544332211, 0xCCBBAA99));
        Assert.Throws<ArgumentOutOfRangeException>(() => Wo216LifecycleCodec.BuildArgs(4, 1, 1));
    }

    internal static byte[] Ack(uint id, byte sequence = 0, byte phase = Protocol.Wo216LifecycleLoad, ulong generation = 1,
        bool accepted = true, byte reason = 0, uint age = 0, bool enabled = true, bool active = false)
    {
        var body = new byte[Wo216LifecycleCodec.ReplyBodyLength];
        body[0] = accepted ? (byte)1 : (byte)0; body[1] = sequence; body[2] = Protocol.Wo216OpLifecycle; body[3] = reason;
        body[4] = phase; body[5] = enabled ? (byte)1 : (byte)0; body[6] = active ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(7), generation);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(15), age);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(19), id);
        return body;
    }

    [Fact]
    public void Ack_keeps_native_state_and_echo_id_even_when_a_stale_mutation_is_rejected()
    {
        var body = Ack(0xCCBBAA99, 255, Protocol.Wo216LifecycleGameplay, 0x8877665544332211,
            accepted: false, reason: 2, age: 0x44332211, active: true);
        Assert.Equal(23, body.Length); // generic header4 + ACK19
        Assert.True(Wo216LifecycleCodec.TryReadReply(body, out var ack));
        Assert.Equal(new Wo216LifecycleReply(false, 2, 2, true, true, 0x8877665544332211, 0x44332211, 0xCCBBAA99), ack);
    }

    [Fact]
    public void Ack_rejects_truncation_extra_bytes_wrong_op_and_invalid_state_booleans()
    {
        var valid = Ack(17);
        Assert.False(Wo216LifecycleCodec.TryReadReply(valid[..^1], out _));
        Assert.False(Wo216LifecycleCodec.TryReadReply([.. valid, 0], out _));
        foreach ((int index, byte value) in new[] { (0, (byte)2), (2, (byte)167), (4, (byte)3), (5, (byte)2), (6, (byte)2) })
        {
            var bad = valid.ToArray(); bad[index] = value;
            Assert.False(Wo216LifecycleCodec.TryReadReply(bad, out _));
        }
    }
}

public class Wo216LifecyclePipeTests
{
    private const byte Wo163 = 0x2B, Wo163Reply = 0xAC;
    private static string Name() => "p1-" + Guid.NewGuid().ToString("N");
    private static NamedPipeServerStream Server(string name, int instances = 1) =>
        new(name, PipeDirection.InOut, instances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    private static SemaphoreSlim Gate(CombatPipe pipe, string name) =>
        (SemaphoreSlim)typeof(CombatPipe).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pipe)!;
    private static async Task<(byte Type, byte[] Payload)> Read(Stream stream)
    {
        using var timeout = new CancellationTokenSource(3000);
        var h = new byte[3]; await stream.ReadExactlyAsync(h, timeout.Token);
        var payload = new byte[BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(1))];
        await stream.ReadExactlyAsync(payload, timeout.Token); return (h[0], payload);
    }
    private static async Task Reply(Stream stream, byte type, params byte[] payload)
    {
        using var timeout = new CancellationTokenSource(3000);
        byte[] frame = [type, (byte)payload.Length, (byte)(payload.Length >> 8), .. payload];
        await stream.WriteAsync(frame, timeout.Token);
        await stream.FlushAsync(timeout.Token);
    }
    private static uint Id((byte Type, byte[] Payload) frame)
    {
        Assert.Equal(Wo163, frame.Type); Assert.Equal(14, frame.Payload.Length); Assert.Equal(168, frame.Payload[0]);
        return BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload.AsSpan(10));
    }
    private static async Task WaitFor(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(3000);
        while (!ready()) await Task.Delay(5, timeout.Token);
    }

    [Fact]
    public async Task Control_connects_and_acks_while_the_normal_global_gate_is_held()
    {
        string name = Name(); using var server = Server(name); var accept = server.WaitForConnectionAsync();
        await using var pipe = new CombatPipe(name);
        var gate = Gate(pipe, "_gate"); await gate.WaitAsync();
        try
        {
            var request = pipe.Wo216LifecycleAsync(Protocol.Wo216LifecycleLoad, 0x1122334455667788);
            await accept.WaitAsync(TimeSpan.FromSeconds(3));
            var frame = await Read(server); uint id = Id(frame);
            Assert.Equal(Protocol.Wo216LifecycleLoad, frame.Payload[1]);
            Assert.Equal(0x1122334455667788ul, BinaryPrimitives.ReadUInt64LittleEndian(frame.Payload.AsSpan(2)));
            await Reply(server, Wo163Reply, Wo216LifecycleCodecTests.Ack(id, generation: 0x1122334455667788));
            var ack = await request.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(ack!.Value.Accepted); Assert.Equal(id, ack.Value.RequestId); Assert.Equal(0, gate.CurrentCount);
        }
        finally { gate.Release(); }
    }

    [Fact]
    public async Task Unanswered_main_thread_batch_does_not_hold_pure_control_or_steal_its_ack()
    {
        string name = Name(); using var server = Server(name); var accept = server.WaitForConnectionAsync();
        await using var pipe = new CombatPipe(name) { ControlTime = new ManualFrameTime() };
        Assert.True(await pipe.EnsureConnectedAsync()); await accept;
        pipe.SetBatchSupported(true);
        var ordinary = pipe.Wo151Async(2, []); var batch = await Read(server); Assert.Equal(0x2D, batch.Type);
        var control = pipe.Wo216LifecycleAsync(Protocol.Wo216LifecycleQuery, 0); var frame = await Read(server); uint id = Id(frame);
        await Reply(server, Wo163Reply, Wo216LifecycleCodecTests.Ack(id, phase: Protocol.Wo216LifecycleGameplay, generation: 81, age: 6000, active: true));
        var ack = await control.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(81ul, ack!.Value.Generation); Assert.True(ack.Value.Active); Assert.False(ordinary.IsCompleted);
        await Reply(server, 0xAF, 1, batch.Payload[0], batch.Payload[1], 1, 0xAB, 4, 0, 1, 0, 2, 0);
        Assert.True((await ordinary.WaitAsync(TimeSpan.FromSeconds(2)))!.Value.Ok);
        Assert.True(pipe.BatchActive);
    }

    [Theory]
    [InlineData((byte)42)]
    [InlineData((byte)253)]
    public async Task Early_control_ack_never_advances_an_older_single_reply_and_the_next_single_handles_the_gap_and_wrap(byte firstSeq)
    {
        string name = Name(); using var server = Server(name); var accept = server.WaitForConnectionAsync();
        await using var pipe = new CombatPipe(name); Assert.True(await pipe.EnsureConnectedAsync()); await accept;
        var warm = pipe.Wo163Async(164, []); await Read(server); await Reply(server, Wo163Reply, 1, firstSeq, 164, 0, 10); await warm;
        var ordinary = pipe.Wo163Async(161, []); await Read(server);
        var control = pipe.Wo216LifecycleAsync(Protocol.Wo216LifecycleQuery, 0); uint id = Id(await Read(server));
        await Reply(server, Wo163Reply, Wo216LifecycleCodecTests.Ack(id, unchecked((byte)(firstSeq + 2))));
        Assert.NotNull(await control.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(unchecked((byte)(firstSeq + 1)), typeof(CombatPipe).GetField("_expectedSeq", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pipe));
        Assert.False(ordinary.IsCompleted);
        await Reply(server, Wo163Reply, 1, unchecked((byte)(firstSeq + 1)), 161, 0, 20);
        Assert.Equal(new byte[] { 20 }, (await ordinary.WaitAsync(TimeSpan.FromSeconds(2)))!.Value.Payload);
        var next = pipe.Wo163Async(161, []); await Read(server);
        await Reply(server, Wo163Reply, 1, unchecked((byte)(firstSeq + 3)), 161, 0, 30);
        Assert.Equal(new byte[] { 30 }, (await next.WaitAsync(TimeSpan.FromSeconds(2)))!.Value.Payload);
    }

    [Fact]
    public async Task Cancel_before_physical_write_removes_the_request_without_reset_or_late_send()
    {
        string name = Name(); using var server = Server(name); var accept = server.WaitForConnectionAsync();
        var controlTime = new ManualFrameTime();
        await using var pipe = new CombatPipe(name) { ControlTime = controlTime };
        Assert.True(await pipe.EnsureConnectedAsync()); await accept;
        var writer = Gate(pipe, "_writeLock"); await writer.WaitAsync();
        Task<Wo216LifecycleReply?>? abandoned = null;
        try
        {
            var clock = new ManualFrameTime();
            var result = await ProcessorFrameScope.RunAsync(async _ =>
            {
                await clock.Armed;
                abandoned = pipe.Wo216LifecycleAsync(1, 44);
                await controlTime.Armed;
                Assert.False(abandoned.IsCompleted);
                clock.Expire();
                await abandoned;
            }, 100, CancellationToken.None, clock);
            Assert.Equal(ProcessorFrameScope.Outcome.TimedOut, result.Status);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned!.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(pipe.IsConnected);
        }
        finally { writer.Release(); }
        var live = pipe.Wo216LifecycleAsync(3, 0); var frame = await Read(server); uint id = Id(frame);
        Assert.Equal(3, frame.Payload[1]);
        await Reply(server, Wo163Reply, Wo216LifecycleCodecTests.Ack(id));
        Assert.NotNull(await live.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Cancellation_after_a_whole_written_frame_keeps_framing_and_ignores_its_late_ack()
    {
        string name = Name(); using var server = Server(name); var accept = server.WaitForConnectionAsync();
        await using var pipe = new CombatPipe(name); Assert.True(await pipe.EnsureConnectedAsync()); await accept;
        using var cancellation = new CancellationTokenSource();
        var old = pipe.Wo216LifecycleAsync(1, 55, cancellation.Token); uint oldId = Id(await Read(server));
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old);
        Assert.True(pipe.IsConnected);
        var live = pipe.Wo216LifecycleAsync(3, 0); uint liveId = Id(await Read(server)); Assert.NotEqual(oldId, liveId);
        await Reply(server, Wo163Reply, Wo216LifecycleCodecTests.Ack(oldId, generation: 55));
        // A normal reply fences the reader past the old ACK without a timing-based assertion.
        var ordinary = pipe.Wo163Async(161, []); await Read(server); await Reply(server, Wo163Reply, 1, 2, 161, 0, 99);
        Assert.NotNull(await ordinary.WaitAsync(TimeSpan.FromSeconds(2))); Assert.False(live.IsCompleted);
        await Reply(server, Wo163Reply, Wo216LifecycleCodecTests.Ack(liveId, generation: 56));
        Assert.Equal(56ul, (await live.WaitAsync(TimeSpan.FromSeconds(2)))!.Value.Generation);
    }

    [Fact]
    public async Task Own_timeout_keeps_the_connection_and_cannot_complete_a_retried_query_with_an_old_echo_id()
    {
        string name = Name(); using var server = Server(name); var accept = server.WaitForConnectionAsync();
        var deadline = new ManualFrameTime();
        await using var pipe = new CombatPipe(name) { ControlTime = deadline };
        Assert.True(await pipe.EnsureConnectedAsync()); await accept;
        var old = pipe.Wo216LifecycleAsync(3, 0); uint oldId = Id(await Read(server));
        var writer = Gate(pipe, "_writeLock");
        Assert.True(await writer.WaitAsync(TimeSpan.FromSeconds(2)));
        writer.Release();
        deadline.Expire();
        Assert.Null(await old.WaitAsync(TimeSpan.FromSeconds(2))); Assert.True(pipe.IsConnected);
        var retry = pipe.Wo216LifecycleAsync(3, 0); uint nextId = Id(await Read(server)); Assert.NotEqual(oldId, nextId);
        await Reply(server, Wo163Reply, Wo216LifecycleCodecTests.Ack(oldId, generation: 1));
        await Reply(server, Wo163Reply, Wo216LifecycleCodecTests.Ack(nextId, generation: 2));
        Assert.Equal(2ul, (await retry.WaitAsync(TimeSpan.FromSeconds(2)))!.Value.Generation);
    }

    [Fact]
    public async Task Native_rejection_is_a_typed_current_state_and_malformed_ack_cannot_be_applied()
    {
        string name = Name(); using var server = Server(name); var accept = server.WaitForConnectionAsync();
        await using var pipe = new CombatPipe(name) { ControlTime = new ManualFrameTime() };
        Assert.True(await pipe.EnsureConnectedAsync()); await accept;
        var stale = pipe.Wo216LifecycleAsync(1, 99); uint id = Id(await Read(server));
        await Reply(server, Wo163Reply, Wo216LifecycleCodecTests.Ack(id, phase: 2, generation: 100, accepted: false, reason: 2, active: true));
        var ack = (await stale.WaitAsync(TimeSpan.FromSeconds(2)))!.Value;
        Assert.False(ack.Accepted); Assert.Equal(100ul, ack.Generation); Assert.Equal(2, ack.Phase);
        var malformed = pipe.Wo216LifecycleAsync(3, 0); id = Id(await Read(server));
        byte[] bad = Wo216LifecycleCodecTests.Ack(id); bad[5] = 2;
        await Reply(server, Wo163Reply, bad);
        // Answers on this connection are read in wire order. A later valid
        // ACK must complete this request if the malformed one was ignored.
        await Reply(server, Wo163Reply, Wo216LifecycleCodecTests.Ack(id, generation: 101));
        Assert.Equal(101ul, (await malformed.WaitAsync(TimeSpan.FromSeconds(2)))!.Value.Generation);
        Assert.True(pipe.IsConnected);
    }

    [Fact]
    public async Task Pipe_drop_completes_old_waiters_and_a_fresh_connection_has_a_new_reader_and_id()
    {
        string name = Name(); using var first = Server(name); var accept = first.WaitForConnectionAsync();
        await using var pipe = new CombatPipe(name); Assert.True(await pipe.EnsureConnectedAsync()); await accept;
        var oldReader = typeof(CombatPipe).GetField("_reader", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pipe);
        var old = pipe.Wo216LifecycleAsync(1, 2); uint oldId = Id(await Read(first)); first.Disconnect();
        Assert.Null(await old.WaitAsync(TimeSpan.FromSeconds(2))); await WaitFor(() => !pipe.IsConnected); first.Dispose();
        using var second = Server(name); var nextAccept = second.WaitForConnectionAsync();
        var next = pipe.Wo216LifecycleAsync(3, 0); await nextAccept.WaitAsync(TimeSpan.FromSeconds(3)); uint id = Id(await Read(second));
        Assert.NotEqual(oldId, id);
        await Reply(second, Wo163Reply, Wo216LifecycleCodecTests.Ack(id, generation: 3));
        Assert.Equal(3ul, (await next.WaitAsync(TimeSpan.FromSeconds(2)))!.Value.Generation);
        Assert.NotSame(oldReader, typeof(CombatPipe).GetField("_reader", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pipe));
    }


    [Fact]
    public async Task Queued_old_connection_control_cannot_cross_onto_the_replacement_pipe()
    {
        string name = Name(); using var first = Server(name); var accept = first.WaitForConnectionAsync();
        await using var pipe = new CombatPipe(name); Assert.True(await pipe.EnsureConnectedAsync()); await accept;
        var writer = Gate(pipe, "_writeLock"); await writer.WaitAsync();
        Task<Wo216LifecycleReply?> old = pipe.Wo216LifecycleAsync(1, 87);
        try
        {
            first.Disconnect(); first.Dispose(); await WaitFor(() => !pipe.IsConnected);
            using var second = Server(name); var nextAccept = second.WaitForConnectionAsync();
            Assert.True(await pipe.EnsureConnectedAsync()); await nextAccept.WaitAsync(TimeSpan.FromSeconds(2));
            writer.Release();
            Assert.Null(await old.WaitAsync(TimeSpan.FromSeconds(2)));
            var live = pipe.Wo216LifecycleAsync(3, 0); var frame = await Read(second); uint id = Id(frame);
            Assert.Equal(3, frame.Payload[1]); // no old Load request was written to this world
            await Reply(second, Wo163Reply, Wo216LifecycleCodecTests.Ack(id, generation: 88));
            Assert.Equal(88ul, (await live.WaitAsync(TimeSpan.FromSeconds(2)))!.Value.Generation);
        }
        finally { if (writer.CurrentCount == 0) writer.Release(); }
    }

    [Fact]
    public async Task Normal_frame_completion_keeps_background_control_live_until_session_cancellation()
    {
        string name = Name(); using var server = Server(name); var accept = server.WaitForConnectionAsync();
        await using var pipe = new CombatPipe(name); Assert.True(await pipe.EnsureConnectedAsync()); await accept;
        using var session = new CancellationTokenSource();
        Task<Wo216LifecycleReply?>? background = null;
        var frame = await ProcessorFrameScope.RunAsync(_ =>
        {
            background = pipe.Wo216LifecycleAsync(3, 0);
            return Task.CompletedTask;
        }, 500, session.Token);
        Assert.Equal(ProcessorFrameScope.Outcome.Completed, frame.Status);
        uint id = Id(await Read(server)); Assert.False(background!.IsCompleted);
        session.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => background);
        Assert.True(pipe.IsConnected);
        var live = pipe.Wo216LifecycleAsync(3, 0); uint liveId = Id(await Read(server));
        await Reply(server, Wo163Reply, Wo216LifecycleCodecTests.Ack(id, generation: 1));
        await Reply(server, Wo163Reply, Wo216LifecycleCodecTests.Ack(liveId, generation: 2));
        Assert.Equal(2ul, (await live.WaitAsync(TimeSpan.FromSeconds(2)))!.Value.Generation);
    }


    [Fact]
    public async Task Dispose_resolves_both_control_and_normal_waiters_and_prevents_reconnect()
    {
        string name = Name(); using var server = Server(name); var accept = server.WaitForConnectionAsync();
        var pipe = new CombatPipe(name); Assert.True(await pipe.EnsureConnectedAsync()); await accept;
        var ordinary = pipe.Wo163Async(164, []); await Read(server);
        var control = pipe.Wo216LifecycleAsync(3, 0); Id(await Read(server));
        await pipe.DisposeAsync();
        Assert.Null(await control.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Null(await ordinary.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(await pipe.EnsureConnectedAsync());
        Assert.Null(await pipe.Wo216LifecycleAsync(3, 0));
        await pipe.DisposeAsync();
    }


    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Old_single_or_ping_waiting_at_gate_or_writer_never_moves_to_a_control_reconnected_pipe(bool ping, bool writerWait)
    {
        string name = Name(); using var first = Server(name); var accept = first.WaitForConnectionAsync();
        await using var pipe = new CombatPipe(name); Assert.True(await pipe.EnsureConnectedAsync()); await accept;
        var held = Gate(pipe, writerWait ? "_writeLock" : "_gate"); await held.WaitAsync();
        Task<bool> old = ping ? pipe.PingAsync() : Single();
        async Task<bool> Single() => await pipe.Wo163Async(161, [0xAA]) is not null;
        Assert.False(old.IsCompleted);
        try
        {
            first.Disconnect(); first.Dispose(); await WaitFor(() => !pipe.IsConnected);
            using var second = Server(name); var nextAccept = second.WaitForConnectionAsync();
            var control = pipe.Wo216LifecycleAsync(3, 0);
            await nextAccept.WaitAsync(TimeSpan.FromSeconds(2));
            if (writerWait) held.Release();
            // The old operation had already passed EnsureConnected on the old
            // stream. Neither its opcode nor its reply lane may cross worlds.
            uint id = Id(await Read(second));
            await Reply(second, Wo163Reply, Wo216LifecycleCodecTests.Ack(id, generation: 71));
            Assert.NotNull(await control.WaitAsync(TimeSpan.FromSeconds(2)));
            if (!writerWait) held.Release();
            Assert.False(await old.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Null(typeof(CombatPipe).GetField("_expectedSeq", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pipe));
            var live = pipe.Wo163Async(164, []); var frame = await Read(second);
            Assert.Equal(new byte[] { 164 }, frame.Payload);
            await Reply(second, Wo163Reply, 1, 1, 164, 0, 99);
            Assert.Equal(new byte[] { 99 }, (await live.WaitAsync(TimeSpan.FromSeconds(2)))!.Value.Payload);
            var livePing = pipe.PingAsync(); frame = await Read(second); Assert.Equal(3, frame.Type);
            await Reply(second, 0x83);
            Assert.True(await livePing.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal((byte)3, typeof(CombatPipe).GetField("_expectedSeq", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pipe));
        }
        finally { if (held.CurrentCount == 0) held.Release(); }
    }

    [Fact]
    public async Task Simultaneous_normal_and_control_reconnect_share_one_connection_and_one_reader()
    {
        string name = Name(); using var server1 = Server(name, 2); using var server2 = Server(name, 2);
        using var stop = new CancellationTokenSource(3000);
        Task a = server1.WaitForConnectionAsync(stop.Token), b = server2.WaitForConnectionAsync(stop.Token);
        await using var pipe = new CombatPipe(name);
        var ordinary = pipe.Wo163Async(164, []); var control = pipe.Wo216LifecycleAsync(3, 0);
        Task accepted = await Task.WhenAny(a, b); await accepted;
        var server = accepted == a ? server1 : server2;
        var frames = new[] { await Read(server), await Read(server) };
        foreach (var frame in frames)
        {
            if (frame.Payload[0] == 168) await Reply(server, Wo163Reply, Wo216LifecycleCodecTests.Ack(Id(frame), sequence: 1));
            else await Reply(server, Wo163Reply, 1, 0, frame.Payload[0], 0, 7);
        }
        Assert.NotNull(await control.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(new byte[] { 7 }, (await ordinary.WaitAsync(TimeSpan.FromSeconds(2)))!.Value.Payload);
        Assert.False((accepted == a ? b : a).IsCompleted); stop.Cancel();
    }
}
