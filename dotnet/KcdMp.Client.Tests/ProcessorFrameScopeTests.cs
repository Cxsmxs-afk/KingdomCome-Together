// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Threading.Channels;

namespace KcdMp.Client.Tests;

public class ProcessorFrameScopeTests
{
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
        ProcessorFrameScope.Lease? captured = null;
        bool wrote = false;
        var result = await ProcessorFrameScope.RunAsync(async _ =>
        {
            captured = ProcessorFrameScope.Capture();
            try { await release.Task; ProcessorFrameScope.ThrowIfAbandoned(); wrote = true; }
            finally { finished.TrySetResult(); }
        }, 50);
        Assert.Equal(ProcessorFrameScope.Outcome.TimedOut, result.Status);
        Assert.NotNull(captured); Assert.True(captured.IsAbandoned);
        release.SetResult(); await finished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(wrote);
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
        await channel.Writer.WriteAsync(117);
        var processor = Task.Run(async () =>
        {
            await foreach (int frame in channel.Reader.ReadAllAsync())
                await ProcessorFrameScope.RunAsync(async _ =>
                {
                    if (frame == 117) { entered.TrySetResult(); await never.Task; }
                    else processed.Add(frame);
                }, 100);
        });
        await entered.Task;
        // This is the independent load/replay clock, not a processor frame.
        await channel.Writer.WriteAsync(39); channel.Writer.TryComplete();
        await processor.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { 39 }, processed);
        never.TrySetResult();
    }
}
