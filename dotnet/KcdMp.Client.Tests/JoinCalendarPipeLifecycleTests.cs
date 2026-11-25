// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
namespace KcdMp.Client.Tests;

public sealed class JoinCalendarPipeLifecycleTests
{
    [Fact]
    public async Task The_native_double_accepts_a_fresh_stream_after_the_bridge_retires_an_unconfirmed_sequence()
    {
        await using var engine = new JoinCalendarTestPipe();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var allowReply = new ManualResetEventSlim();
        engine.BeforeCalendarArm = () =>
        {
            read.TrySetResult();
            allowReply.Wait(deadline.Token);
        };
        var pending = engine.Pipe.CalendarFollowAsync(2, JoinCalendarTestPipe.HostWorldMs,
            JoinCalendarTestPipe.HostRatio, deadline.Token);
        try
        {
            await read.Task.WaitAsync(deadline.Token);
            Assert.Null(await pending);
            Assert.Equal(1, engine.Pipe.TimedOut);
            Assert.Equal(CombatPipe.NpcDeathReceipt.Uncertain,
                await engine.Pipe.ApplyOwnedNpcDeathAsync(Guid.NewGuid(), 5, 7, 1, CombatPipe.NpcDeathIntent.Advance,
                    () => true, deadline.Token));
            Assert.Equal("sequence-unconfirmed", engine.Pipe.LastConnectionDropReason);
        }
        finally
        {
            engine.BeforeCalendarArm = null;
            allowReply.Set();
        }
        var fresh = await engine.Pipe.CalendarSnapshotAsync(deadline.Token);
        Assert.Equal((JoinCalendarTestPipe.HostWorldMs, JoinCalendarTestPipe.HostRatio), fresh);
    }
}
