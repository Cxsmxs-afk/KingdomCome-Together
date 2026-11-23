// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public class RelayFrameCancellationTests
{
    private sealed class Path(bool blockFirst = false) : ILinkPath
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ConcurrentQueue<byte[]> Sent = new();
        private int _calls;
        public string Transport => "test";
        public bool CanSendUnreliable => false;
        public LinkPathStatus Status() => default;
        public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, bool state, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1 && blockFirst)
            {
                Started.SetResult();
                await Release.Task.WaitAsync(ct);
            }
            Sent.Enqueue(frame.ToArray());
        }
    }

    private static byte[] Frame(byte type, byte[] payload)
    {
        var frame = new byte[payload.Length + 3];
        frame[0] = type;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1), (ushort)payload.Length);
        payload.CopyTo(frame, 3);
        return frame;
    }
    private static byte[] Event(byte tag) => Frame(Protocol.StoryBeatUp, [1, 1, tag]);
    private static byte[] Npc(string name)
    {
        var n = Encoding.UTF8.GetBytes(name);
        var p = new byte[2 + n.Length + Protocol.NpcStateFixedTail];
        p[1] = (byte)n.Length; n.CopyTo(p, 2);
        return Frame(Protocol.NpcStateDown, p);
    }

    private static (LinkWriter Writer, Action<byte[], CancellationToken> Queue) ClientWriter(Path path)
    {
        // This fixture tests frame cancellation independently of the scheduler's state age limit.
        var writer = new LinkWriter(new LinkScheduler(new LinkSchedOptions { Budget = false, AgeLimitMs = 0 }), path);
        var bridge = new GameBridge(new ClientConfig());
        var stream = new MemoryStream();
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(GameBridge).GetField("_w213Writer", flags)!.SetValue(bridge, writer);
        typeof(GameBridge).GetField("_w213Stream", flags)!.SetValue(bridge, stream);
        var enqueue = typeof(GameBridge).GetMethod("Wo213TryQueue", flags)!;
        return (writer, (packet, token) => Assert.True((bool)enqueue.Invoke(bridge, [stream, packet, token])!));
    }

    [Fact]
    public async Task Blocked_writer_drops_abandoned_events_and_state_then_sends_the_next_valid_packet()
    {
        var path = new Path(blockFirst: true);
        var (writer, queue) = ClientWriter(path);
        queue(Event(1), default);
        Task pump = writer.PumpOnceAsync(CancellationToken.None);
        var watchdog = TimeSpan.FromSeconds(3);
        var clock = new ManualFrameTime();
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await path.Started.Task.WaitAsync(watchdog);
            var frame = ProcessorFrameScope.RunAsync(async _ =>
            {
                queue(Event(2), default);
                queue(Npc("abandoned_npc"), default);
                queued.SetResult();
                await releaseHandler.Task;
            }, 50, CancellationToken.None, clock);
            await queued.Task.WaitAsync(watchdog);
            clock.Expire();
            Assert.Equal(ProcessorFrameScope.Outcome.TimedOut, (await frame.WaitAsync(watchdog)).Status);
            releaseHandler.SetResult();
            queue(Event(3), default);
            queue(Npc("valid_npc"), default);
            path.Release.SetResult();
            await pump.WaitAsync(watchdog);
            byte[][] sent = path.Sent.ToArray();
            Assert.Equal(new byte[] { 1, 3 }, sent.Where(f => f[0] == Protocol.StoryBeatUp).Select(f => f[^1]));
            Assert.DoesNotContain(sent, f => Encoding.UTF8.GetString(f).Contains("abandoned_npc"));
            Assert.Contains(sent, f => Encoding.UTF8.GetString(f).Contains("valid_npc"));
            Assert.Equal(2, writer.Scheduler.CancelledDropped);
            Assert.Equal(0, writer.Scheduler.StaleDropped);
            Assert.Null(writer.Failed);
        }
        finally
        {
            clock.Expire();
            releaseHandler.TrySetResult();
            path.Release.TrySetResult();
        }
    }

    [Fact]
    public async Task Normally_completed_frame_packet_is_still_sent_with_explicit_handler_token()
    {
        var path = new Path();
        var (writer, queue) = ClientWriter(path);
        var result = await ProcessorFrameScope.RunAsync(token => { queue(Event(4), token); return Task.CompletedTask; }, 2000);
        Assert.Equal(ProcessorFrameScope.Outcome.Completed, result.Status);
        await writer.PumpOnceAsync(CancellationToken.None);
        Assert.Equal(4, Assert.Single(path.Sent)[^1]);
        Assert.Equal(0, writer.Scheduler.CancelledDropped);
    }

    [Fact]
    public async Task Caller_cancellation_revokes_only_its_packet_after_normal_frame_completion()
    {
        var path = new Path();
        var (writer, queue) = ClientWriter(path);
        using var caller = new CancellationTokenSource();
        var result = await ProcessorFrameScope.RunAsync(_ => { queue(Event(5), caller.Token); return Task.CompletedTask; }, 2000);
        Assert.Equal(ProcessorFrameScope.Outcome.Completed, result.Status);
        caller.Cancel();
        queue(Event(6), default);
        await writer.PumpOnceAsync(CancellationToken.None);
        Assert.Equal(6, Assert.Single(path.Sent)[^1]);
        Assert.Equal(1, writer.Scheduler.CancelledDropped);
    }

    [Fact]
    public async Task Writer_rechecks_after_selection_immediately_before_the_physical_write()
    {
        var path = new Path();
        var writer = new LinkWriter(new LinkScheduler(new LinkSchedOptions { Budget = false }), path);
        bool valid = true;
        writer.Enqueue(Event(7), () => valid);
        writer.Enqueue(Event(8));
        writer.Transform = frame => { if (frame[^1] == 7) valid = false; return frame; };
        await writer.PumpOnceAsync(CancellationToken.None);
        Assert.Equal(8, Assert.Single(path.Sent)[^1]);
        Assert.Equal(1, writer.Scheduler.CancelledDropped);
    }
}
