// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;
using System.Text;
using KcdMp.Wire;
using Xunit;

namespace KcdMp.Client.Tests;

public class T3SideChannelsTests
{
    [Fact]
    public void Load_keeps_only_latest_weather_and_applies_after_gameplay_plus_five_seconds()
    {
        var q = new T3SideChannelQueue<int>(); var lease = q.Capture(7);
        for (int i = 0; i < 1000; i++) Assert.True(q.Put(1, i, lease));
        Assert.Equal(1, q.Count); Assert.Empty(q.Take(7, 1_000_000, true));
        q.GameplayStarted(7, 1_000_000);
        Assert.Empty(q.Take(7, 1_004_999, true)); Assert.Empty(q.Take(7, 1_005_000, false));
        Assert.Equal(999, Assert.Single(q.Take(7, 1_005_000, true)).Value); Assert.Equal(0, q.Count);
    }

    [Theory]
    [InlineData(8u)]
    [InlineData(7u)]
    public void World_or_life_change_cancels_inflight_work_and_drops_old_pending(uint nextWorld)
    {
        var q = new T3SideChannelQueue<int>(); var lease = q.Capture(7); q.GameplayStarted(7, 0); q.Put(1, 42, lease);
        q.Invalidate(nextWorld);
        Assert.True(lease.Token.IsCancellationRequested); Assert.False(q.IsCurrent(lease)); Assert.False(q.Put(1, 99, lease));
        Assert.Empty(q.Take(nextWorld, 100_000, true));
        var current = q.Capture(nextWorld); q.Put(1, 1, current); q.GameplayStarted(nextWorld, 100_000);
        Assert.Empty(q.Take(nextWorld, 104_999, true)); Assert.Single(q.Take(nextWorld, 105_000, true));
    }

    [Fact]
    public void Role_change_keeps_completed_settle_but_discards_previous_owner_work()
    {
        var q = new T3SideChannelQueue<int>(); var old = q.Capture(7); q.GameplayStarted(7, 0); q.Put(1, 42, old);
        q.Invalidate(7, preserveGameplay: true);
        Assert.False(q.IsCurrent(old)); Assert.Empty(q.Take(7, 5000, true));
        var current = q.Capture(7); Assert.True(q.Ready(current, 5000));
        q.Put(1, 43, current); Assert.Equal(43, Assert.Single(q.Take(7, 5000, true)).Value);
    }

    [Fact]
    public void Actual_W164_header_is_split_before_weather_or_calendar_body()
    {
        byte[] Weather(byte kind, string text) => Down(4, new LootMsg(kind, 123, text).BuildUp(Protocol.W164Up, 2));
        Assert.True(T3SideChannelFrame.TryDecode(Protocol.W164Down, Weather(Protocol.W164Weather, W164Text.Weather(0.5f, "foggy_storm")), 7, out var wx));
        Assert.Equal((byte)4, wx.Source); Assert.Equal(0.5f, wx.Rain); Assert.Equal("foggy_storm", wx.Profile); Assert.Equal((ushort)7, wx.Blend);
        string frozen = HostCalendar.Format(123456000, 0, 1000, true, 22);
        Assert.True(T3SideChannelFrame.TryDecode(Protocol.W164Down, Weather(Protocol.W164Clock, frozen), 7, out var clock));
        Assert.Equal(frozen, clock.Text); Assert.Equal(Protocol.W164Clock, clock.Kind);
        Assert.False(T3SideChannelFrame.TryDecode(Protocol.W164Down, Weather(Protocol.W164Torch, "4 1"), 7, out _));
    }

    [Fact]
    public void Frozen_clock_is_available_for_bootstrap_while_native_follow_remains_held()
    {
        var q = new T3SideChannelQueue<HostCalendarSample>(); var lease = q.Capture(7);
        string frozen = HostCalendar.Format(123456000, 0, 1000, true, 22);
        Assert.True(T3SideChannelFrame.TryDecode(Protocol.W164Down, Down(0, new LootMsg(Protocol.W164Clock, 1, frozen).BuildUp(Protocol.W164Up, 1)), 7, out var clock));
        Assert.True(HostCalendar.TryParse(clock.Text, 10, 1000, true, 0, out var bootstrap));
        Assert.True(bootstrap.ReadyForJoin(10, 22)); // the join can issue its load before GameplayStarted
        q.Put(0, bootstrap, lease); Assert.Empty(q.Take(7, 200_000, true));
        q.GameplayStarted(7, 200_000); Assert.Empty(q.Take(7, 204_999, true)); Assert.Single(q.Take(7, 205_000, true));
    }

    [Fact]
    public void Legacy_weather_is_validated_and_can_replace_newer_slot_without_an_old_encoding_replay()
    {
        var bytes = Encoding.UTF8.GetBytes("foggy_storm"); var p = new byte[bytes.Length + 4]; p[0] = 0; p[1] = (byte)bytes.Length;
        bytes.CopyTo(p, 2); BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(2 + bytes.Length), 12);
        Assert.True(T3SideChannelFrame.TryDecode(Protocol.WeatherDown, p, 7, out var wx)); Assert.Null(wx.Rain); Assert.Equal((ushort)12, wx.Blend);
        var q = new T3SideChannelQueue<T3SideChannelFrame>(); var lease = q.Capture(7); q.GameplayStarted(7, 0);
        q.Put(1, new(0, Protocol.W164Weather, "", 0.8f, "old", 7), lease); q.Put(1, wx, lease);
        Assert.Equal(wx, Assert.Single(q.Take(7, 5000, true)).Value);
        p[2] = (byte)'"'; Assert.False(T3SideChannelFrame.TryDecode(Protocol.WeatherDown, p, 7, out _));
    }

    [Fact]
    public async Task Abandoned_clock_drain_does_not_discard_weather_waiting_for_the_next_independent_tick()
    {
        var q = new T3SideChannelQueue<string>(); var lease = q.Capture(7); q.GameplayStarted(7, 0);
        q.Put(0, "hung clock", lease); q.Put(1, "latest weather", lease);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = await ProcessorFrameScope.RunAsync(async _ =>
        {
            Assert.Equal("hung clock", Assert.Single(q.Take(7, 5000, true)).Value);
            await release.Task; ProcessorFrameScope.ThrowIfAbandoned();
        }, 50);
        Assert.Equal(ProcessorFrameScope.Outcome.TimedOut, result.Status);
        Assert.Equal("latest weather", Assert.Single(q.Take(7, 5100, true)).Value);
        release.TrySetResult();
    }

    [Fact]
    public async Task Late_continuation_cannot_commit_to_a_new_world_or_life()
    {
        var q = new T3SideChannelQueue<int>(); var lease = q.Capture(7);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int writes = 0;
        var work = ProcessorFrameScope.RunAsync(async _ =>
        {
            await released.Task;
            if (!q.IsCurrent(lease)) throw new OperationCanceledException(lease.Token);
            ProcessorFrameScope.ThrowIfAbandoned(); writes++;
        }, 1000);
        q.Invalidate(8); released.SetResult(); await work; Assert.Equal(0, writes);
    }

    [Fact]
    public void Replenished_clock_cannot_starve_pending_weather_after_a_failed_turn()
    {
        var q = new T3SideChannelQueue<string>(); var lease = q.Capture(7); q.GameplayStarted(7, 0);
        q.Put(0, "clock", lease); q.Put(1, "weather", lease);
        Assert.Equal("clock", Assert.Single(q.Take(7, 5000, true)).Value);
        for (int i = 0; i < 100; i++) q.Put(0, "new clock", lease);
        Assert.Equal("weather", Assert.Single(q.Take(7, 5001, true)).Value);
        Assert.Equal("new clock", Assert.Single(q.Take(7, 5002, true)).Value);
    }

    private static byte[] Down(byte source, byte[] up)
    {
        var down = new byte[up.Length - 2]; down[0] = source; up.AsSpan(3).CopyTo(down.AsSpan(1)); return down;
    }
}
