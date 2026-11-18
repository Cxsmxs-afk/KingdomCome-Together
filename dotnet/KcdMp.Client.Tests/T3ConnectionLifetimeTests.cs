// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Channels;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

[CollectionDefinition("T3 connection lifecycle", DisableParallelization = true)]
public class T3ConnectionLifetimeCollection { }

[Collection("T3 connection lifecycle")]
public class T3ConnectionLifetimeTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static void Set(GameBridge b, string name, object value) => typeof(GameBridge).GetField(name, Private)!.SetValue(b, value);
    private static T Get<T>(GameBridge b, string name) => (T)typeof(GameBridge).GetField(name, Private)!.GetValue(b)!;
    private static object? Call(GameBridge b, string name, params object?[] args) => typeof(GameBridge).GetMethod(name, Private)!.Invoke(b, args);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Game : HttpMessageHandler
    {
        public readonly TaskCompletionSource<ProcessorFrameScope.Lease> HorseEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleaseHorse = Signal();
        public readonly TaskCompletionSource HorseReturned = Signal();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (Uri.UnescapeDataString(request.RequestUri!.Query).Contains("KCD2MP_SetGhostHorse"))
            {
                HorseEntered.TrySetResult(ProcessorFrameScope.Capture()!);
                try { await ReleaseHorse.Task; } // an uncancellable old HTTP/engine leaf
                finally { HorseReturned.TrySetResult(); }
            }
            return new(HttpStatusCode.OK) { Content = new StringContent("ok") };
        }
    }

    private sealed class EofStream(byte[] bytes) : Stream
    {
        private int _at;
        public readonly TaskCompletionSource AllowEof = Signal();
        public readonly TaskCompletionSource ReadWaiting = Signal();
        public readonly TaskCompletionSource ReadCancelled = Signal();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_at < bytes.Length)
            {
                int n = Math.Min(buffer.Length, bytes.Length - _at);
                bytes.AsMemory(_at, n).CopyTo(buffer); _at += n; return n;
            }
            ReadWaiting.TrySetResult();
            try { await AllowEof.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { ReadCancelled.TrySetResult(); throw; }
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => _at; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static GameBridge Bridge(HttpGameTransport transport)
    {
        var b = new GameBridge(new ClientConfig()); Set(b, "_transport", transport);
        var where = typeof(GameBridge).GetField("_where", Private)!;
        where.SetValue(b, Enum.Parse(where.FieldType, "World")); return b;
    }
    private static byte[] Wire(byte kind, byte[] payload) => [kind, (byte)payload.Length, (byte)(payload.Length >> 8), .. payload];
    private static async Task WaitFor(Func<bool> ready)
    {
        using var stop = new CancellationTokenSource(3000);
        while (!ready()) await Task.Delay(5, stop.Token);
    }

    private sealed class Frames
    {
        private readonly GameBridge _bridge;
        private readonly Type _type = typeof(GameBridge).GetNestedType("InFrame", BindingFlags.NonPublic)!;
        private static readonly object NoPresentation =
            Activator.CreateInstance(typeof(GameBridge).GetNestedType("PresentationFrame", BindingFlags.NonPublic)!)!;
        public readonly object Reader, Writer;
        public Frames(GameBridge bridge)
        {
            _bridge = bridge;
            var create = typeof(Channel).GetMethods().Single(m => m.Name == nameof(Channel.CreateUnbounded) && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            var channel = create.MakeGenericMethod(_type).Invoke(null, null)!;
            Reader = channel.GetType().GetProperty("Reader")!.GetValue(channel)!;
            Writer = channel.GetType().GetProperty("Writer")!.GetValue(channel)!;
            Set(bridge, "_frameWriter", Writer);
        }
        public void Send(int kind, byte[] payload)
        {
            object frame = Activator.CreateInstance(_type, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, [kind, payload, Stopwatch.GetTimestamp(), 0u], null)!;
            Assert.True((bool)Call(_bridge, "Wo207Queue", Writer, frame, kind)!);
        }
        public void Coalesce(byte ghost) => Assert.True((bool)Call(_bridge, "Wo202Coalesce", Writer, (int)Protocol.Ghost,
            new byte[] { ghost }, Stopwatch.GetTimestamp(), 0u, NoPresentation)!);
        public Task Run(CancellationToken token) => (Task)Call(_bridge, "ProcessFramesAsync", Reader, token)!;
    }

    [Fact]
    public async Task EOF_revokes_the_blocked_frame_before_outer_session_teardown_and_next_connection_still_runs()
    {
        var game = new Game();
        await using var transport = new HttpGameTransport("http://localhost:1403", 30000, game) { BatchingEnabled = false };
        var b = Bridge(transport);
        using var parent = new CancellationTokenSource();
        using var old = new EofStream(Wire(Protocol.HorseInfoDown, [1, 0]));
        Task receive = (Task)Call(b, "ReceiveLoopAsync", old, parent.Token)!;
        try
        {
            var lease = await game.HorseEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            old.AllowEof.SetResult();
            await receive.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(parent.IsCancellationRequested);
            Assert.True(lease.IsAbandoned);
            Assert.Null(typeof(GameBridge).GetField("_frameWriter", Private)!.GetValue(b));
            Assert.False(game.ReleaseHorse.Task.IsCompleted); // reader did not await the hung old handler

            using var next = new EofStream(Wire(Protocol.ReleaseVersion, [2, .. Encoding.UTF8.GetBytes("new-connection")]));
            Task nextReceive = (Task)Call(b, "ReceiveLoopAsync", next, parent.Token)!;
            await WaitFor(() => Get<IDictionary>(b, "_ghostReleaseVersions")[(byte)2]?.ToString() == "new-connection");
            next.AllowEof.SetResult(); await nextReceive.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(lease.IsAbandoned); // the new connection cannot revive the old producer's authority
        }
        finally { game.ReleaseHorse.TrySetResult(); parent.Cancel(); await receive; }
        await game.HorseReturned.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Cancelled_processor_uncounts_each_remaining_row_and_coalesced_slot_without_resetting_other_queues()
    {
        long initial = Wo207Trace.WaitingTotal;
        const int sentinel = 0xEF, own = 0xEE;
        Wo207Trace.Enqueued(sentinel); // belongs to another queue: cleanup must preserve it
        var game = new Game();
        await using var transport = new HttpGameTransport("http://localhost:1403", 30000, game) { BatchingEnabled = false };
        var b = Bridge(transport); var frames = new Frames(b);
        using var stop = new CancellationTokenSource();
        frames.Send(Protocol.HorseInfoDown, [1, 0]);
        Task processor = frames.Run(stop.Token);
        try
        {
            await game.HorseEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            for (int i = 0; i < 40; i++) frames.Send(own, [(byte)i]);
            frames.Coalesce(91); frames.Coalesce(91); // two samples occupy exactly one counted waiting row
            Assert.Equal(initial + 42, Wo207Trace.WaitingTotal);
            stop.Cancel(); await processor.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(initial + 1, Wo207Trace.WaitingTotal);
            Assert.Empty(Get<IDictionary>(b, "_w202Latest"));
            Assert.False((bool)frames.Writer.GetType().GetMethod("TryWrite")!.Invoke(frames.Writer,
                [Activator.CreateInstance(typeof(GameBridge).GetNestedType("InFrame", BindingFlags.NonPublic)!)] )!);
        }
        finally { game.ReleaseHorse.TrySetResult(); stop.Cancel(); await processor; Wo207Trace.Dequeued(sentinel); }
        Assert.Equal(initial, Wo207Trace.WaitingTotal);
    }

    [Fact]
    public async Task Processor_completion_cancels_a_reader_waiting_for_its_next_header()
    {
        var game = new Game();
        await using var transport = new HttpGameTransport("http://localhost:1403", 5000, game) { BatchingEnabled = false };
        var b = Bridge(transport);
        using var parent = new CancellationTokenSource();
        using var stream = new EofStream([]);
        Task receive = (Task)Call(b, "ReceiveLoopAsync", stream, parent.Token)!;
        await stream.ReadWaiting.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var writer = typeof(GameBridge).GetField("_frameWriter", Private)!.GetValue(b)!;
        writer.GetType().GetMethod("TryComplete")!.Invoke(writer, [null]);
        await receive.WaitAsync(TimeSpan.FromSeconds(2));
        await stream.ReadCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(parent.IsCancellationRequested);
        Assert.Null(typeof(GameBridge).GetField("_frameWriter", Private)!.GetValue(b));
    }
}
