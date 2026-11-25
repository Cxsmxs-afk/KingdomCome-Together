// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Threading.Channels;

namespace KcdMp.Client.Tests;

public class ProcessorFrameScopeTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(2);

    [Fact]
    public void The_client_assembly_has_no_process_wide_deadline_test_hook()
    {
        Assert.Null(typeof(ProcessorFrameScope).GetField("InstrumentDeadlineTime",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic));
    }

    [Fact]
    public async Task Http_timeout_is_a_frame_fault_and_the_following_frame_runs()
    {
        var first = await ProcessorFrameScope.RunAsync(_ => Task.FromException(new TaskCanceledException("HTTP timeout")), 2000);
        Assert.Equal(ProcessorFrameScope.Outcome.Faulted, first.Status);
        bool next = false;
        var second = await ProcessorFrameScope.RunAsync(_ => { next = true; return Task.CompletedTask; }, 2000);
        Assert.True(next); Assert.Equal(ProcessorFrameScope.Outcome.Completed, second.Status);
    }

    [Fact]
    public async Task Never_finishing_handler_is_abandoned_and_a_late_continuation_cannot_write()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<ProcessorFrameScope.Lease>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new ManualFrameTime();
        bool wrote = false;
        var frame = ProcessorFrameScope.RunAsync(async _ =>
        {
            entered.SetResult(ProcessorFrameScope.Capture()!);
            try { await release.Task; ProcessorFrameScope.ThrowIfAbandoned(); wrote = true; }
            finally { finished.TrySetResult(); }
        }, 50, CancellationToken.None, clock);
        try
        {
            var captured = await entered.Task.WaitAsync(Watchdog);
            Assert.NotNull(captured);
            await clock.Armed.WaitAsync(Watchdog);
            clock.Expire();
            Assert.Equal(ProcessorFrameScope.Outcome.TimedOut, (await frame.WaitAsync(Watchdog)).Status);
            Assert.True(captured.IsAbandoned);
            release.SetResult();
            await finished.Task.WaitAsync(Watchdog);
            Assert.False(wrote);
        }
        finally
        {
            clock.Expire();
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Normal_completion_keeps_queued_work_valid_until_the_session_ends()
    {
        using var session = new CancellationTokenSource();
        var clock = new ManualFrameTime();
        ProcessorFrameScope.Lease? captured = null;
        var frame = ProcessorFrameScope.RunAsync(_ =>
        {
            captured = ProcessorFrameScope.Capture();
            return Task.CompletedTask;
        }, 50, session.Token, clock);
        try
        {
            var result = await frame.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ProcessorFrameScope.Outcome.Completed, result.Status);
            Assert.NotNull(captured);
            Assert.False(captured.IsAbandoned);
            captured.ThrowIfAbandoned();
            session.Cancel();
            Assert.True(captured.IsAbandoned);
        }
        finally
        {
            clock.Expire();
        }
    }

    [Fact]
    public async Task Independent_tick_releases_load_replay_while_a_handler_is_stuck()
    {
        var processed = new List<int>();
        var channel = Channel.CreateUnbounded<int>();
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new ManualFrameTime();
        await channel.Writer.WriteAsync(117);
        var processor = Task.Run(async () =>
        {
            await foreach (int frame in channel.Reader.ReadAllAsync())
                await ProcessorFrameScope.RunAsync(async _ =>
                {
                    if (frame == 117) { entered.TrySetResult(); await never.Task; }
                    else processed.Add(frame);
                }, 100, CancellationToken.None, frame == 117 ? clock : new ManualFrameTime());
        });
        try
        {
            await entered.Task.WaitAsync(Watchdog);
            await clock.Armed.WaitAsync(Watchdog);
            // The independent load/replay clock queues the frame while the processor is still blocked.
            await channel.Writer.WriteAsync(39);
            channel.Writer.TryComplete();
            clock.Expire();
            await processor.WaitAsync(Watchdog);
            Assert.False(never.Task.IsCompleted);
            Assert.Equal(new[] { 39 }, processed);
        }
        finally
        {
            clock.Expire();
            channel.Writer.TryComplete();
            never.TrySetResult();
        }
    }
}
