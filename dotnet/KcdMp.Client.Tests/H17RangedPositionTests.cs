// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Reflection;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public sealed class H17RangedPositionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly MethodInfo Gate = typeof(GameBridge).GetMethod("Wo121State2For", Private)!;
    private static readonly MethodInfo Write = typeof(GameBridge).GetMethod("SendPositionAsync", Private)!;
    private static readonly BodyState2 Held = new(0, 0, BodyState2Bits.RangedAim | BodyState2Bits.RangedChargeKnown,
        0, 0, 0, 96, 300, -200);
    private static readonly BodyState2 Ended = default(BodyState2) with
    {
        AimYaw = BodyState2.AimAngleUnavailable,
        AimPitch = BodyState2.AimAngleUnavailable,
    };

    [Theory]
    [InlineData(97, 300, -200)]
    [InlineData(96, 500, -200)]
    [InlineData(96, 300, -400)]
    public async Task Stationary_ranged_change_wakes_the_position_writer_within_one_sample(int charge, int yaw, int pitch)
    {
        var bridge = Start();
        var next = Held with { Charge = (byte)charge, AimYaw = (short)yaw, AimPitch = (short)pitch };
        Assert.False(Select(bridge, next, 49, false).Due);
        var (block, due) = Select(bridge, next, 50, false);
        Assert.True(due);
        Assert.Equal(next, block);
        Assert.Equal(next, Decode(await Packet(bridge, Select(bridge, next, 50, true).Block, 0)));
    }

    [Fact]
    public void Held_and_confirmed_end_resend_at_one_second_in_the_existing_position_loop()
    {
        var bridge = Start();
        Assert.False(Select(bridge, Held, 999, false).Due);
        for (int second = 1; second <= 10; ++second)
            Assert.Equal((Held, true), Select(bridge, Held, 1000 * second, true));
        Assert.Equal((Ended, true), Select(bridge, Ended, 10001, true));
        // The first Ended packet is lost; the ordinary Position heartbeat is two seconds.
        Assert.False(Select(bridge, Ended, 11000, false).Due);
        Assert.Equal((Ended, true), Select(bridge, Ended, 11001, true));
    }

    [Fact]
    public async Task Confirmed_end_survives_replacement_by_a_newer_position()
    {
        var bridge = Start();
        var scheduler = new LinkScheduler(new LinkSchedOptions { Budget = false, LatestWins = true });
        scheduler.Enqueue(await Packet(bridge, Select(bridge, Ended, 50, true).Block, 0));
        scheduler.Enqueue(await Packet(bridge, Select(bridge, Ended, 60, true).Block, 1));
        Assert.True(scheduler.TryTake(int.MaxValue, -1, out var wire, out bool state));
        Assert.True(state);
        Assert.Equal(Ended, Decode(wire));
    }

    [Fact]
    public void Missing_phase_does_not_assert_end_or_resend_a_cached_hold()
    {
        var bridge = Start();
        Assert.Equal(((BodyState2?)null, false), Select(bridge, null, 50, true));
        var crouched = default(BodyState2) with { Bits = BodyState2Bits.Crouched };
        Assert.Equal((crouched, true), Select(bridge, crouched, 60, true));
        Assert.Equal((Held, true), Select(bridge, Held, 1000, true));
    }

    private static GameBridge Start()
    {
        var bridge = new GameBridge(new ClientConfig());
        Assert.Equal((Held, true), Select(bridge, Held, 0, true));
        return bridge;
    }

    private static (BodyState2? Block, bool Due) Select(GameBridge bridge, BodyState2? state, int ms, bool consume)
    {
        long tick = (10000L + ms) * Stopwatch.Frequency / 1000;
        var local = new LocalState(1, 0, 0, 0, 0, 0, null, state);
        object?[] args = [local, tick, consume, false];
        var block = (BodyState2?)Gate.Invoke(bridge, args);
        return (block, (bool)args[3]!);
    }

    private static async Task<byte[]> Packet(GameBridge bridge, BodyState2? state, float x)
    {
        await using var stream = new MemoryStream();
        await (Task)Write.Invoke(bridge, [stream, x, 0f, 0f, 0f, false, false, state])!;
        return stream.ToArray();
    }

    private static BodyState2 Decode(byte[] wire)
    {
        Assert.NotEqual(0, wire[19] & Protocol.PositionFlagBodyState2);
        return BodyState2.Read(wire.AsSpan(20));
    }
}
