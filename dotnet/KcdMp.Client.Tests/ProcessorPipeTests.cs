// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.IO.Pipes;
using System.Reflection;

namespace KcdMp.Client.Tests;

public class ProcessorPipeTests
{
    private static SemaphoreSlim Gate(CombatPipe pipe, string name) =>
        (SemaphoreSlim)typeof(CombatPipe).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pipe)!;

    private static async Task<(byte Type, byte[] Payload)> ReadFrame(Stream server)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var header = new byte[3];
        await server.ReadExactlyAsync(header, timeout.Token);
        var payload = new byte[header[1] | header[2] << 8];
        await server.ReadExactlyAsync(payload, timeout.Token);
        return (header[0], payload);
    }

    private static async Task Reply(Stream server, byte type, params byte[] body)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        byte[] frame = [type, (byte)body.Length, (byte)(body.Length >> 8), .. body];
        await server.WriteAsync(frame, timeout.Token);
        await server.FlushAsync(timeout.Token);
    }

    private static NamedPipeServerStream Server(string name) =>
        new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    [Fact]
    public async Task Ambient_abandon_cancels_connection_gate_wait_even_with_a_distinct_caller_token()
    {
        await using var pipe = new CombatPipe("kcdmp-p0-" + Guid.NewGuid().ToString("N"));
        using var caller = new CancellationTokenSource();
        var gate = Gate(pipe, "_connectLock");
        await gate.WaitAsync();
        Task<bool>? request = null;
        try
        {
            var outcome = await ProcessorFrameScope.RunAsync(_ => request = pipe.Wo151WeatherGateAsync("foggy_storm", caller.Token), 150);
            Assert.Equal(ProcessorFrameScope.Outcome.TimedOut, outcome.Status);
            Assert.NotNull(request);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request!.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(caller.IsCancellationRequested);
            Assert.False(pipe.IsConnected);
            Assert.Equal(0, gate.CurrentCount); // the cancelled waiter never releases somebody else's gate
        }
        finally { gate.Release(); }
    }

    [Fact]
    public async Task A_caller_cancellation_is_still_honoured_inside_an_active_frame()
    {
        await using var pipe = new CombatPipe("kcdmp-p0-" + Guid.NewGuid().ToString("N"));
        using var caller = new CancellationTokenSource();
        var gate = Gate(pipe, "_gate"); await gate.WaitAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? request = null;
        try
        {
            var frame = ProcessorFrameScope.RunAsync(_ =>
            {
                request = pipe.EnsureConnectedAsync(caller.Token);
                started.SetResult();
                return request;
            }, 2000);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            caller.Cancel();
            var outcome = await frame.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(ProcessorFrameScope.Outcome.Faulted, outcome.Status);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request!);
            Assert.Equal(0, gate.CurrentCount);
        }
        finally { gate.Release(); }
    }

    [Fact]
    public async Task An_abandoned_single_request_never_writes_after_its_gate_opens_and_the_next_request_succeeds()
    {
        string name = "kcdmp-p0-" + Guid.NewGuid().ToString("N");
        using var server = Server(name); var accept = server.WaitForConnectionAsync();
        await using var pipe = new CombatPipe(name);
        Assert.True(await pipe.EnsureConnectedAsync()); await accept;
        var line = Gate(pipe, "_line"); await line.WaitAsync();
        Task<bool>? abandoned = null;
        try
        {
            var outcome = await ProcessorFrameScope.RunAsync(_ => abandoned = pipe.Wo151WeatherGateAsync("foggy_storm"), 150);
            Assert.Equal(ProcessorFrameScope.Outcome.TimedOut, outcome.Status);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned!.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(pipe.IsConnected); // queued cancellation does not reset another operation's connection
        }
        finally { line.Release(); }
        var live = pipe.Wo151WeatherGateAsync("sunny");
        var frame = await ReadFrame(server);
        Assert.Equal(0x2A, frame.Type);
        Assert.Equal(new byte[] { 7, 5, (byte)'s', (byte)'u', (byte)'n', (byte)'n', (byte)'y' }, frame.Payload);
        await Reply(server, 0xAB, 1, 0, 7, 0);
        Assert.True(await live.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task A_cancelled_sent_request_cannot_misattribute_its_late_reply_to_the_next_request()
    {
        string name = "kcdmp-p0-" + Guid.NewGuid().ToString("N");
        using var server = Server(name); var accept = server.WaitForConnectionAsync();
        await using var pipe = new CombatPipe(name);
        Assert.True(await pipe.EnsureConnectedAsync()); await accept;
        var warm = pipe.Wo151Async(2, []); await ReadFrame(server); await Reply(server, 0xAB, 1, 0, 2, 0);
        Assert.NotNull(await warm);
        Task? abandoned = null;
        var clock = new ManualFrameTime();
        var scope = ProcessorFrameScope.RunAsync(_ => abandoned = pipe.Wo151Async(7, []), 300, CancellationToken.None, clock);
        var old = await ReadFrame(server); Assert.Equal(7, old.Payload[0]);
        clock.Expire();
        Assert.Equal(ProcessorFrameScope.Outcome.TimedOut, (await scope).Status);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned!.WaitAsync(TimeSpan.FromSeconds(2)));
        var live = pipe.Wo151Async(9, []);
        var next = await ReadFrame(server); Assert.Equal(9, next.Payload[0]);
        await Reply(server, 0xAB, 1, 1, 7, 0, 11); // the abandoned command's late answer
        await Reply(server, 0xAB, 1, 2, 9, 0, 42);
        var result = await live.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new byte[] { 42 }, result!.Value.Payload);
    }

    [Fact]
    public async Task A_cancelled_batch_item_waiting_at_the_writer_is_omitted_and_the_detached_pump_keeps_working()
    {
        string name = "kcdmp-p0-" + Guid.NewGuid().ToString("N");
        using var server = Server(name); var accept = server.WaitForConnectionAsync();
        await using var pipe = new CombatPipe(name);
        Assert.True(await pipe.EnsureConnectedAsync()); await accept; pipe.SetBatchSupported(true);
        var writer = Gate(pipe, "_writeLock"); await writer.WaitAsync();
        Task? abandoned = null;
        Task<(bool Ok, byte Reason, byte[] Payload)?> live;
        try
        {
            var outcome = await ProcessorFrameScope.RunAsync(_ => abandoned = pipe.Wo151Async(7, []), 150);
            Assert.Equal(ProcessorFrameScope.Outcome.TimedOut, outcome.Status);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned!.WaitAsync(TimeSpan.FromSeconds(2)));
            live = pipe.Wo151Async(2, []);
            Assert.True(pipe.IsConnected);
        }
        finally { writer.Release(); }
        var batch = await ReadFrame(server);
        Assert.Equal(0x2D, batch.Type); Assert.Equal(1, batch.Payload[2]);
        Assert.Equal(new byte[] { 0x2A, 1, 0, 2 }, batch.Payload[3..]);
        await Reply(server, 0xAF, 1, batch.Payload[0], batch.Payload[1], 1, 0xAB, 4, 0, 1, 0, 2, 0);
        Assert.True((await live.WaitAsync(TimeSpan.FromSeconds(2)))!.Value.Ok);
    }

    [Fact]
    public async Task Abandoned_posts_are_omitted_but_queued_work_of_a_completed_frame_remains_authorized()
    {
        string name = "kcdmp-p0-" + Guid.NewGuid().ToString("N");
        using var server = Server(name); var accept = server.WaitForConnectionAsync();
        await using var pipe = new CombatPipe(name);
        Assert.True(await pipe.EnsureConnectedAsync()); await accept; pipe.SetBatchSupported(true);
        var writer = Gate(pipe, "_writeLock"); await writer.WaitAsync();
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var dead = await ProcessorFrameScope.RunAsync(_ => { Assert.True(pipe.PostLatest(0x2B, [17, 0], "dead")); return never.Task; }, 150);
            Assert.Equal(ProcessorFrameScope.Outcome.TimedOut, dead.Status);
            var live = await ProcessorFrameScope.RunAsync(_ => { Assert.True(pipe.PostLatest(0x2B, [17, 1], "live")); return Task.CompletedTask; }, 2000);
            Assert.Equal(ProcessorFrameScope.Outcome.Completed, live.Status);
        }
        finally { writer.Release(); never.SetResult(); }
        var batch = await ReadFrame(server);
        Assert.Equal(0x2D, batch.Type); Assert.Equal(1, batch.Payload[2]);
        Assert.Equal(new byte[] { 0x2B, 2, 0, 17, 1 }, batch.Payload[3..]);
        await Reply(server, 0xAF, 1, batch.Payload[0], batch.Payload[1], 1, 0xAC, 4, 0, 1, 0, 17, 0);
    }

    [Fact]
    public async Task All_cancelled_sent_batch_items_release_the_pump_and_late_replies_keep_their_old_batch_id()
    {
        string name = "kcdmp-p0-" + Guid.NewGuid().ToString("N");
        using var server = Server(name); var accept = server.WaitForConnectionAsync();
        await using var pipe = new CombatPipe(name);
        Assert.True(await pipe.EnsureConnectedAsync()); await accept; pipe.SetBatchSupported(true);
        Task? abandoned = null;
        var clock = new ManualFrameTime();
        var scope = ProcessorFrameScope.RunAsync(_ => abandoned = pipe.Wo151Async(7, []), 300, CancellationToken.None, clock);
        var old = await ReadFrame(server);
        clock.Expire();
        Assert.Equal(ProcessorFrameScope.Outcome.TimedOut, (await scope).Status);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned!.WaitAsync(TimeSpan.FromSeconds(2)));
        var live = pipe.Wo151Async(2, []);
        var next = await ReadFrame(server); // within 3 s, never behind the old batch's 5 s deadline
        Assert.Equal(new byte[] { 0x2A, 1, 0, 2 }, next.Payload[3..]);
        Assert.False(old.Payload[..2].SequenceEqual(next.Payload[..2]));
        await Reply(server, 0xAF, 1, old.Payload[0], old.Payload[1], 1, 0xAB, 5, 0, 1, 0, 7, 0, 11);
        await Reply(server, 0xAF, 1, next.Payload[0], next.Payload[1], 1, 0xAB, 5, 0, 1, 0, 2, 0, 42);
        Assert.Equal(new byte[] { 42 }, (await live.WaitAsync(TimeSpan.FromSeconds(2)))!.Value.Payload);
    }
}
