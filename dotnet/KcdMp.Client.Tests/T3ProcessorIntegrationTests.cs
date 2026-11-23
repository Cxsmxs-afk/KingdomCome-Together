// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Channels;
using KcdMp.Wire;
using Xunit.Abstractions;

namespace KcdMp.Client.Tests;

public class T3ProcessorIntegrationTests(ITestOutputHelper output)
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private sealed class Game(Func<CancellationToken, Task> send) : HttpMessageHandler
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Interlocked.Increment(ref Calls); Entered.TrySetResult(); await send(token); return new(HttpStatusCode.OK) { Content = new StringContent("ok") }; }
    }
    private static void Set(GameBridge b, string field, object value) => typeof(GameBridge).GetField(field, Private)!.SetValue(b, value);
    private static T Get<T>(GameBridge b, string field) => (T)typeof(GameBridge).GetField(field, Private)!.GetValue(b)!;
    private static object? Call(GameBridge b, string method, params object?[] args) => typeof(GameBridge).GetMethod(method, Private)!.Invoke(b, args);
    private static void Where(GameBridge b, string state)
    { var f = typeof(GameBridge).GetField("_where", Private)!; f.SetValue(b, Enum.Parse(f.FieldType, state)); }
    private static GameBridge Bridge(HttpGameTransport transport, string state, TimeProvider? frameTime = null)
    { var b = new GameBridge(new ClientConfig()) { T3FrameTime = frameTime ?? TimeProvider.System }; Set(b, "_transport", transport); Where(b, state); return b; }

    private sealed class Frames
    {
        private readonly GameBridge _bridge;
        private readonly Type _frameType = typeof(GameBridge).GetNestedType("InFrame", BindingFlags.NonPublic)!;
        public readonly object Reader, Writer;
        public Frames(GameBridge bridge)
        {
            _bridge = bridge;
            var create = typeof(Channel).GetMethods().Single(m => m.Name == nameof(Channel.CreateUnbounded) && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            object channel = create.MakeGenericMethod(_frameType).Invoke(null, null)!;
            Reader = channel.GetType().GetProperty("Reader")!.GetValue(channel)!;
            Writer = channel.GetType().GetProperty("Writer")!.GetValue(channel)!;
            Set(bridge, "_frameWriter", Writer);
        }
        public object Frame(int type, byte[] bytes, uint world = 0) => Activator.CreateInstance(_frameType,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null,
            [type, bytes, Stopwatch.GetTimestamp(), world], null)!;
        public void Send(int type, byte[] bytes, uint world = 0)
        {
            object frame = Frame(type, bytes, world);
            Assert.True((bool)Call(_bridge, "Wo207Queue", Writer, frame, type)!);
        }
        public Task Run(CancellationToken ct) => (Task)Call(_bridge, "ProcessFramesAsync", Reader, ct)!;
        public void Complete() => Writer.GetType().GetMethod("TryComplete")!.Invoke(Writer, [null]);
    }

    [Fact]
    public async Task An_identical_role_frame_retries_after_the_first_apply_was_abandoned()
    {
        var game = new Game(_ => Task.CompletedTask);
        await using var transport = new HttpGameTransport("http://localhost:1403", 5000, game) { BatchingEnabled = false };
        var b = Bridge(transport, "World");
        var gate = Get<SemaphoreSlim>(b, "_hostAuthorityGate");
        await gate.WaitAsync();
        Task? first = null;
        try
        {
            var result = await ProcessorFrameScope.RunAsync(token => first = (Task)Call(b, "ApplyCombatRoleAsync", false, token)!, 100);
            Assert.Equal(ProcessorFrameScope.Outcome.TimedOut, result.Status);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first!.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(Get<bool>(b, "_combatRoleApplyComplete"));
        }
        finally { gate.Release(); }
        var retry = await ProcessorFrameScope.RunAsync(token => (Task)Call(b, "ApplyCombatRoleAsync", false, token)!, 5000);
        Assert.Equal(ProcessorFrameScope.Outcome.Completed, retry.Status);
        Assert.True(Get<bool>(b, "_combatRoleApplyComplete"));
        Assert.True(game.Calls > 0);
    }

    [Fact]
    public async Task Real_processor_survives_a_non_session_http_timeout_and_clears_its_stall_watch()
    {
        var game = new Game(_ => Task.FromException(new TaskCanceledException("simulated HTTP timeout")));
        await using var transport = new HttpGameTransport("http://localhost:1403", 5000, game) { BatchingEnabled = false };
        var b = Bridge(transport, "World"); var frames = new Frames(b);
        frames.Send(Protocol.HorseInfoDown, [1, 0]); // real handler awaits an HTTP/Lua write without an inner catch
        frames.Send(Protocol.ReleaseVersion, [2, .. Encoding.UTF8.GetBytes("after-timeout")]); frames.Complete();
        await frames.Run(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("after-timeout", Get<IDictionary>(b, "_ghostReleaseVersions")[(byte)2]);
        Assert.Equal(1, Get<System.Collections.Concurrent.ConcurrentDictionary<string, long>>(b, "_t3Counters")["proc_frames_abandoned"]);
        Assert.Equal(0, Get<long>(b, "_w206dProcSinceMs"));
    }

    [Fact]
    public async Task Real_load_replay_clock_runs_while_an_uncancellable_handler_blocks_and_queue_catches_up()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var game = new Game(_ => release.Task); // deliberately ignores cancellation, like a genuinely stuck leaf
        await using var transport = new HttpGameTransport("http://localhost:1403", 30000, game) { BatchingEnabled = false };
        var clock = new ManualFrameTime();
        var b = Bridge(transport, "Loading", clock); var frames = new Frames(b);
        using var session = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task? processor = null;
        try
        {
            Assert.True((bool)Call(b, "Wo136Defer", frames.Frame(Protocol.ActionDown, []))!);
            frames.Send(Protocol.HorseInfoDown, [1, 0]);
            processor = frames.Run(session.Token);
            await game.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await clock.Armed.WaitAsync(session.Token);
            Call(b, "Wo124OnGameplayStartedCore", true);
            Call(b, "T3StartIndependentTick", frames.Writer, session.Token);
            long started = Stopwatch.GetTimestamp();
            for (int i = 0; i < 100; i++)
            {
                long arrival = Stopwatch.GetTimestamp();
                long ms = (long)typeof(GameBridge).GetMethod("W147StampMs", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [arrival])!;
                Set(b, "_w202LastReadMs", ms);
                frames.Send(Protocol.ReleaseVersion, [2, .. Encoding.UTF8.GetBytes("next-" + i)]);
                await Task.Delay(12, session.Token);
            }
            while (Get<long>(b, "_w136Replayed") == 0)
                await Task.Delay(10, session.Token);
            Assert.False(release.Task.IsCompleted);
            Assert.Null(Get<IDictionary>(b, "_ghostReleaseVersions")[(byte)2]);
            clock.Expire();
            while (Get<IDictionary>(b, "_ghostReleaseVersions")[(byte)2]?.ToString() != "next-99")
                await Task.Delay(10, session.Token);
            Assert.True(Stopwatch.GetElapsedTime(started).TotalSeconds < 5);
            Assert.Equal(1, Get<long>(b, "_w136Replayed"));
            Assert.True((long)Call(b, "Wo202QueueLagMs")! < 1000);
            var counters = Get<System.Collections.Concurrent.ConcurrentDictionary<string, long>>(b, "_t3Counters");
            Assert.Equal(1, counters["proc_stalls"]); Assert.Equal(1950, counters["proc_stall_max_ms"]);
            output.WriteLine($"T3-P0-SIM proc_stall_max_ms={counters["proc_stall_max_ms"]} catchup_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} backlog_after_ms={Call(b, "Wo202QueueLagMs")} replayed={Get<long>(b, "_w136Replayed")}");
            frames.Complete(); await processor.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { release.TrySetResult(); session.Cancel(); if (processor is not null) await processor; }
    }

    [Fact]
    public async Task Legacy_world_observation_does_not_release_replay_before_the_gameplay_receipt()
    {
        var game = new Game(_ => Task.CompletedTask);
        await using var transport = new HttpGameTransport("http://localhost:1403", 5000, game) { BatchingEnabled = false };
        var b = Bridge(transport, "Loading");
        var frames = new Frames(b);
        Call(b, "Wo136OnLoadSeen", "fixture");
        Assert.True((bool)Call(b, "Wo136Defer", frames.Frame(Protocol.ActionDown, []))!);

        Call(b, "Wo124OnWhere", "world");
        Call(b, "Wo136ReplayIfReleased", frames.Writer, CancellationToken.None);
        Assert.Equal(0, Get<long>(b, "_w136Replayed"));

        Call(b, "Wo124OnGameplayStartedCore", true);
        Assert.True(Get<DateTime>(b, "_w136SettleUntilUtc") > DateTime.UtcNow);
        Set(b, "_w136SettleUntilUtc", DateTime.UtcNow.AddTicks(-1));
        Call(b, "Wo136ReplayIfReleased", frames.Writer, CancellationToken.None);
        Assert.Equal(1, Get<long>(b, "_w136Replayed"));
        frames.Complete();
    }

    [Fact]
    public async Task Actual_W164_weather_is_held_during_loading_without_contacting_the_game()
    {
        var game = new Game(_ => Task.CompletedTask);
        await using var transport = new HttpGameTransport("http://localhost:1403", 5000, game) { BatchingEnabled = false };
        var b = Bridge(transport, "Loading");
        Set(b, "_hostModeKnown", true); Set(b, "_hostModeFrom", (byte)1);
        Get<Wo206Epochs>(b, "_w206").LearnWorld(7, 1);
        var frames = new Frames(b);
        byte[] up = new LootMsg(Protocol.W164Weather, 0, W164Text.Weather(0.5f, "foggy_storm")).BuildUp(Protocol.W164Up, 2);
        var body = new byte[up.Length - 2]; body[0] = 1; up.AsSpan(3).CopyTo(body.AsSpan(1));
        frames.Send(Protocol.W164Down, body, 7);
        frames.Send(Protocol.ReleaseVersion, [2, .. Encoding.UTF8.GetBytes("during-load")], 7); frames.Complete();
        await frames.Run(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, game.Calls);
        Assert.Equal("during-load", Get<IDictionary>(b, "_ghostReleaseVersions")[(byte)2]);
        var side = typeof(GameBridge).GetField("_t3SideChannels", Private)!.GetValue(b)!;
        Assert.Equal(1, side.GetType().GetProperty("Count")!.GetValue(side));
    }
}
