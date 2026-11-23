// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
namespace KcdMp.Client.Tests;

public sealed class JoinCalendarTestPipeTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Peer_close_during_a_pending_reply_is_only_graceful_after_shutdown_starts(bool shuttingDown)
    {
        var pipe = new JoinCalendarTestPipe();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var beforeReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var allowReply = new ManualResetEventSlim();
        pipe.BeforeCalendarArm = () =>
        {
            beforeReply.TrySetResult();
            allowReply.Wait(timeout.Token);
        };

        IOException? liveFailure = null;
        try
        {
            var reply = pipe.Pipe.CalendarFollowAsync(2, JoinCalendarTestPipe.HostWorldMs, JoinCalendarTestPipe.HostRatio, timeout.Token);
            try
            {
                await beforeReply.Task.WaitAsync(timeout.Token);
                if (shuttingDown) pipe.BeginShutdown();
                await pipe.Pipe.DisposeAsync();
            }
            finally { allowReply.Set(); }
            Assert.Null(await reply);

            // Observe the actual write result before fixture disposal can cancel the server.
            if (shuttingDown)
                await pipe.Completion.WaitAsync(timeout.Token);
            else
                liveFailure = await Assert.ThrowsAsync<IOException>(() => pipe.Completion.WaitAsync(timeout.Token));
        }
        finally
        {
            if (liveFailure is null)
                await pipe.DisposeAsync();
            else
                Assert.Same(liveFailure, await Assert.ThrowsAsync<IOException>(() => pipe.DisposeAsync().AsTask()));
        }
    }
}
