// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.IO.Pipes;

namespace KcdMp.Client.Tests;

public sealed class JoinCalendarPipeLifecycleTests
{
    [Fact]
    public async Task The_native_double_accepts_the_next_connection_after_a_peer_closes_before_its_reply()
    {
        await using var engine = new JoinCalendarTestPipe();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.BeforeReply = async () =>
        {
            read.TrySetResult();
            await reply.Task.WaitAsync(deadline.Token);
        };
        using (var peer = Open(engine))
        {
            await peer.ConnectAsync(deadline.Token);
            await peer.WriteAsync(new byte[] { 0x03, 0, 0 }, deadline.Token);
            await read.Task.WaitAsync(deadline.Token);
        }
        engine.BeforeReply = null;
        reply.SetResult();

        using var next = Open(engine);
        Task exchanged = PingAsync(next, deadline.Token);
        Assert.Same(exchanged, await Task.WhenAny(engine.Completion, exchanged));
        await exchanged;
    }

    private static NamedPipeClientStream Open(JoinCalendarTestPipe engine) =>
        new(".", engine.Name, PipeDirection.InOut, PipeOptions.Asynchronous);

    private static async Task PingAsync(NamedPipeClientStream next, CancellationToken ct)
    {
        await next.ConnectAsync(ct);
        await next.WriteAsync(new byte[] { 0x03, 0, 0 }, ct);
        var header = new byte[3];
        await next.ReadExactlyAsync(header, ct);
        Assert.Equal(new byte[] { 0x83, 0, 0 }, header);
    }
}
