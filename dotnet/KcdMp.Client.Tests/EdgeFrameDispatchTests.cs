// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Reflection;
using System.Threading.Channels;

namespace KcdMp.Client.Tests;

public class EdgeFrameDispatchTests
{
    [Fact]
    public void Dispatch_admits_ordered_frames_while_the_game_worker_is_stalled()
    {
        var incoming = Channel.CreateUnbounded<int>();
        var game = Channel.CreateUnbounded<int>();
        var arrivals = Enumerable.Range(1, 1024).ToArray();
        var dispatch = typeof(GameBridge).GetMethod("EdgeDispatchAvailable", BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(int)).CreateDelegate<Func<ChannelReader<int>, ChannelWriter<int>, int>>();

        foreach (int frame in arrivals)
            Assert.True(incoming.Writer.TryWrite(frame));

        Assert.Equal(arrivals.Length, dispatch(incoming.Reader, game.Writer));
        Assert.Equal(0, dispatch(incoming.Reader, game.Writer));

        var dispatched = new List<int>();
        while (game.Reader.TryRead(out int frame)) dispatched.Add(frame);
        Assert.Equal(arrivals, dispatched);
    }
}
