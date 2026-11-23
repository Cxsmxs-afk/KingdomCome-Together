// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;

namespace KcdMp.Client.Tests;

public class HttpFrameCancellationTests
{
    private sealed class Game(Func<HttpRequestMessage, CancellationToken, Task> send) : HttpMessageHandler
    {
        public readonly ConcurrentQueue<string> Calls = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls.Enqueue(Uri.UnescapeDataString(request.RequestUri!.ToString()));
            await send(request, token);
            return new(HttpStatusCode.OK) { Content = new StringContent("ok") };
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TimeSpan FrameTestWatchdog = TimeSpan.FromSeconds(3);
    private static HttpGameTransport Transport(Game game) => new("http://localhost:1403", 5000, game);
    private static string Toast(string text) => $"if KCD2MP_ShowNativeToast then KCD2MP_ShowNativeToast(\"{text}\") end";

    [Fact]
    public async Task Cancelled_caller_cannot_enqueue_or_displace_an_earlier_valid_state()
    {
        var game = new Game((_, _) => Task.CompletedTask);
        await using var transport = Transport(game);
        await transport.ExecuteAsync("KCD2MP_UpdateGhost(1,100)");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.ExecuteAsync("KCD2MP_UpdateGhost(1,999)", cancelled.Token));
        await transport.FlushAsync();
        string call = Assert.Single(game.Calls);
        Assert.Contains("KCD2MP_UpdateGhost(1,100)", call);
        Assert.DoesNotContain("999", call);
    }

    [Fact]
    public async Task Caller_cancel_before_flush_removes_only_its_rows()
    {
        var game = new Game((_, _) => Task.CompletedTask);
        await using var transport = Transport(game);
        using var cancelled = new CancellationTokenSource();
        await transport.ExecuteAsync(Toast("cancelled"), cancelled.Token);
        await transport.ExecuteAsync(Toast("independent"));
        cancelled.Cancel();
        await transport.FlushAsync();
        string call = Assert.Single(game.Calls);
        Assert.DoesNotContain("cancelled", call);
        Assert.Contains("independent", call);
        Assert.Equal(1, transport.AbandonedStatements);
        Assert.Equal(0, transport.Queued);
    }

    [Fact]
    public async Task Normal_frame_completion_keeps_queued_rows_deliverable()
    {
        var game = new Game((_, _) => Task.CompletedTask);
        await using var transport = Transport(game);
        var result = await ProcessorFrameScope.RunAsync(ct => transport.ExecuteAsync(Toast("completed"), ct), 2000);
        Assert.Equal(ProcessorFrameScope.Outcome.Completed, result.Status);
        await transport.FlushAsync();
        Assert.Contains("completed", Assert.Single(game.Calls));
        Assert.Equal(0, transport.AbandonedStatements);
    }

    [Fact]
    public async Task Background_HTTP_survives_normal_frame_completion_but_still_obeys_session_cancellation()
    {
        var started = Signal();
        var game = new Game(async (_, token) => { started.SetResult(); await Task.Delay(Timeout.Infinite, token); });
        await using var transport = Transport(game);
        using var session = new CancellationTokenSource();
        Task? background = null;
        var result = await ProcessorFrameScope.RunAsync(async _ =>
        {
            background = transport.ExecuteNowAsync(Toast("background"));
            await started.Task;
        }, 2000, session.Token);
        Assert.Equal(ProcessorFrameScope.Outcome.Completed, result.Status);
        Assert.NotNull(background);
        Assert.False(background.IsCompleted);
        session.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => background.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Single(game.Calls);
    }

    [Fact]
    public async Task Abandoned_frame_cannot_leave_rows_for_a_later_flush()
    {
        var game = new Game((_, _) => Task.CompletedTask);
        await using var transport = Transport(game);
        var queued = Signal();
        var release = Signal();
        var done = Signal();
        var clock = new ManualFrameTime();
        var frame = ProcessorFrameScope.RunAsync(async _ =>
        {
            try
            {
                await transport.ExecuteAsync(Toast("abandoned"));
                queued.SetResult();
                await release.Task;
            }
            finally { done.SetResult(); }
        }, 50, CancellationToken.None, clock);
        try
        {
            await queued.Task.WaitAsync(FrameTestWatchdog);
            Assert.Equal(1, transport.Queued);
            clock.Expire();
            Assert.Equal(ProcessorFrameScope.Outcome.TimedOut, (await frame.WaitAsync(FrameTestWatchdog)).Status);
            release.SetResult();
            await done.Task.WaitAsync(FrameTestWatchdog);
            await transport.ExecuteAsync(Toast("next-frame"));
            await transport.FlushAsync();
            string call = Assert.Single(game.Calls);
            Assert.DoesNotContain("abandoned", call);
            Assert.Contains("next-frame", call);
            Assert.Equal(1, transport.AbandonedStatements);
        }
        finally
        {
            clock.Expire();
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Blocked_HTTP_is_cancelled_by_frame_deadline_without_transport_hold_or_retry()
    {
        var started = Signal();
        var cancelled = Signal();
        var game = new Game(async (_, token) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled.SetResult(); throw; }
        });
        await using var transport = Transport(game);
        var run = ProcessorFrameScope.RunAsync(_ => transport.ExecuteNowAsync(Toast("blocked")), 100);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(ProcessorFrameScope.Outcome.TimedOut, (await run).Status);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Single(game.Calls);
        Assert.False(transport.Held);
        Assert.Equal(0, transport.BatchesRetried);
    }

    [Fact]
    public async Task HTTP_timeout_faults_only_its_frame_and_the_next_frame_can_send()
    {
        int calls = 0;
        var game = new Game((_, _) => Interlocked.Increment(ref calls) == 1
            ? Task.FromException(new TaskCanceledException("HttpClient.Timeout of 0.8 seconds elapsed")) : Task.CompletedTask);
        await using var transport = Transport(game);
        var failed = await ProcessorFrameScope.RunAsync(_ => transport.ExecuteNowAsync(Toast("timeout")), 2000);
        Assert.Equal(ProcessorFrameScope.Outcome.Faulted, failed.Status);
        var next = await ProcessorFrameScope.RunAsync(_ => transport.ExecuteNowAsync(Toast("next-frame")), 2000);
        Assert.Equal(ProcessorFrameScope.Outcome.Completed, next.Status);
        Assert.Equal(2, game.Calls.Count);
        Assert.False(transport.Held);
    }

    [Fact]
    public async Task Late_continuation_cannot_enqueue_send_or_mutate_reflected_inventory()
    {
        var game = new Game((_, _) => Task.CompletedTask);
        await using var transport = Transport(game);
        var entered = Signal();
        var release = Signal();
        var done = Signal();
        var clock = new ManualFrameTime();
        var failures = new ConcurrentQueue<Exception>();
        var frame = ProcessorFrameScope.RunAsync(async _ =>
        {
            entered.SetResult();
            await release.Task; // deliberately ignores cancellation like an old semaphore or callback
            foreach (Func<Task> write in new Func<Task>[]
            {
                () => transport.ExecuteAsync(Toast("late-queued")),
                () => transport.ExecuteNowAsync(Toast("late-now")),
                () => transport.EquipItemOnGhostAsync("ghost", Guid.NewGuid(), true),
                () => transport.ReadPlayerStateAsync(), // cannot start an ownerless yaw/CVar writer either
                () => { transport.RequestFlush(); return Task.CompletedTask; }
            })
            {
                try { await write(); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { failures.Enqueue(ex); }
            }
            done.SetResult();
        }, 50, CancellationToken.None, clock);
        try
        {
            await entered.Task.WaitAsync(FrameTestWatchdog);
            clock.Expire();
            Assert.Equal(ProcessorFrameScope.Outcome.TimedOut, (await frame.WaitAsync(FrameTestWatchdog)).Status);
            release.SetResult();
            await done.Task.WaitAsync(FrameTestWatchdog);
            await transport.FlushAsync();
            Assert.Empty(failures);
            Assert.Empty(game.Calls);
            Assert.Equal(0, transport.Queued);
        }
        finally
        {
            clock.Expire();
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Cancel_after_create_response_prevents_the_following_equip_write()
    {
        var started = Signal();
        var release = Signal();
        var game = new Game(async (_, _) => { started.TrySetResult(); await release.Task; });
        await using var transport = Transport(game);
        using var cancellation = new CancellationTokenSource();
        Task equip = transport.EquipItemOnGhostAsync("ghost", Guid.NewGuid(), true, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => equip);
        Assert.Contains("Inventory/CreateItems", Assert.Single(game.Calls));
    }

    [Fact]
    public async Task Mixed_inflight_cancellation_retains_independent_edge_with_the_same_once_id()
    {
        var started = Signal();
        int number = 0;
        var game = new Game(async (_, token) =>
        {
            if (Interlocked.Increment(ref number) == 1) { started.SetResult(); await Task.Delay(Timeout.Infinite, token); }
        });
        await using var transport = Transport(game);
        using var cancellation = new CancellationTokenSource();
        await transport.ExecuteAsync(Toast("cancelled"), cancellation.Token);
        await transport.ExecuteAsync(Toast("independent"));
        Task flush = transport.FlushAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await flush;
        await transport.FlushAsync();
        string[] calls = game.Calls.ToArray();
        Assert.Equal(2, calls.Length);
        Assert.DoesNotContain("cancelled", calls[1]);
        string id(string call) => Regex.Match(call, "KCD2MP_Once\\(\"([^\"]+)\",f\\) else f\\(\\) end end\\)\\s*$").Groups[1].Value;
        Assert.NotEmpty(id(calls[0]));
        Assert.Equal(id(calls[0]), id(calls[1]));
        Assert.False(transport.Held);
        Assert.Equal(0, transport.BatchesRetried);
        Assert.Equal(1, transport.AbandonedStatements);
    }

    [Fact]
    public async Task Recovery_started_in_an_abandoned_frame_probes_health_without_resending_its_rows()
    {
        int calls = 0;
        var probeEntered = Signal();
        var game = new Game((_, _) =>
        {
            int call = Interlocked.Increment(ref calls);
            if (call <= 2) return Task.FromException(new TaskCanceledException("game did not answer"));
            if (call == 3) probeEntered.SetResult();
            return Task.CompletedTask;
        });
        await using var transport = Transport(game);
        transport.RecoveryIntervalMs = 200;
        var holdEntered = Signal();
        using var releaseHold = new ManualResetEventSlim();
        var release = Signal();
        var frameEnded = Signal();
        var clock = new ManualFrameTime();
        transport.NowMs = () =>
        {
            // EnterHold sets Held before reading the clock and launching recovery.
            if (transport.Held && holdEntered.TrySetResult() && !releaseHold.Wait(FrameTestWatchdog))
                throw new TimeoutException("The test did not release the transport hold");
            return Environment.TickCount64;
        };
        var frame = ProcessorFrameScope.RunAsync(async _ =>
        {
            try
            {
                await transport.ExecuteAsync(Toast("abandoned"));
                await transport.FlushAsync();
                await transport.FlushAsync();
                await release.Task;
            }
            finally { frameEnded.SetResult(); }
        }, 100, CancellationToken.None, clock);
        bool verified = false;
        try
        {
            await holdEntered.Task.WaitAsync(FrameTestWatchdog);
            Assert.True(transport.Held);
            clock.Expire();
            Assert.Equal(ProcessorFrameScope.Outcome.TimedOut, (await frame.WaitAsync(FrameTestWatchdog)).Status);
            releaseHold.Set();
            release.SetResult();
            await probeEntered.Task.WaitAsync(FrameTestWatchdog);
            using var deadline = new CancellationTokenSource(FrameTestWatchdog);
            while (transport.Held) await Task.Delay(5, deadline.Token);
            string[] requests = game.Calls.ToArray();
            Assert.True(requests.Length >= 3);
            Assert.Contains("local _ = 1", requests[2]);
            Assert.All(requests.Skip(2), request => Assert.DoesNotContain("abandoned", request));
            await transport.ExecuteAsync(Toast("independent"));
            await transport.FlushAsync();
            Assert.Contains(game.Calls, c => c.Contains("independent"));
            Assert.Equal(1, transport.AbandonedStatements);
            verified = true;
        }
        finally
        {
            clock.Expire();
            releaseHold.Set();
            release.TrySetResult();
            // A frame already in EnterHold must finish launching recovery before transport disposal.
            if (holdEntered.Task.IsCompleted)
            {
                var finished = await Task.WhenAny(frameEnded.Task, Task.Delay(FrameTestWatchdog));
                if (verified && finished != frameEnded.Task)
                    throw new TimeoutException("The abandoned recovery frame did not finish after its hold was released");
            }
        }
    }

    [Fact]
    public async Task Flusher_started_in_a_timed_out_frame_remains_available_to_independent_frames()
    {
        var started = Signal();
        var cancelled = Signal();
        var delivered = Signal();
        int number = 0;
        var game = new Game(async (_, token) =>
        {
            if (Interlocked.Increment(ref number) == 1)
            {
                started.SetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { cancelled.SetResult(); throw; }
            }
            else delivered.TrySetResult();
        });
        await using var transport = Transport(game);
        var release = Signal();
        var result = await ProcessorFrameScope.RunAsync(async _ =>
        {
            await transport.ExecuteAsync(Toast("abandoned"));
            transport.RequestFlush();
            await started.Task;
            await release.Task;
        }, 100);
        Assert.Equal(ProcessorFrameScope.Outcome.TimedOut, result.Status);
        release.SetResult();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await transport.ExecuteAsync(Toast("independent"));
        transport.RequestFlush();
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains(game.Calls, c => c.Contains("independent"));
        Assert.False(transport.Held);
    }
}
