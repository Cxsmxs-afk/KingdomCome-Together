// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using KcdMp.Server.Features.ClientHandling;
using KcdMp.Server.Features.Interactions;
using KcdMp.Server.Features.Tcp;
using KcdMp.Wire;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace KcdMp.Relay.Tests;

public sealed class RelayWriterDeadlineTests
{
    [Fact]
    public async Task An_idle_relay_writer_does_not_probe_the_path_and_wakes_to_deliver_a_refusal()
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        var cfg = new ConfigurationBuilder().Build();
        var clients = new ClientHandler(logger, cfg);
        var runner = new ClientSessionRunner(logger, cfg, clients, new TcpBroadcastService(cfg, clients), new SessionManager(logger));
        var path = new CountingPath();
        using var stream = new HandshakeStream();
        var session = runner.Create(new RelayConnection(stream, "fixture", false, "tcp", stream.Dispose) { Path = path });
        Task run = session.RunAsync();
        try
        {
            await stream.Reading.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Task quiet = Task.Delay(TimeSpan.FromMilliseconds(200));
            Assert.Same(quiet, await Task.WhenAny(quiet, path.RepeatedStatus.Task));
            Assert.Equal(0, path.Probes);
        }
        finally
        {
            // A supported handshake frame with an obsolete version sends a
            // refusal just before both sides close. The writer must drain it.
            stream.Release.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
        }
        Assert.Equal(Protocol.VersionMismatch, Assert.Single(path.Sent)[0]);
    }

    [Fact]
    public async Task Shutdown_closes_the_transport_when_its_reliable_lane_cannot_drain()
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        var cfg = new ConfigurationBuilder().Build();
        var clients = new ClientHandler(logger, cfg);
        var runner = new ClientSessionRunner(logger, cfg, clients, new TcpBroadcastService(cfg, clients), new SessionManager(logger));
        var path = new CountingPath(blockEvents: true);
        using var stream = new HandshakeStream();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = runner.Create(new RelayConnection(stream, "fixture", false, "steam", () => closed.TrySetResult()) { Path = path });
        stream.Release.SetResult();

        await session.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(closed.Task.IsCompletedSuccessfully);
        Assert.Empty(path.Sent);
        Assert.True(path.Probes > 0);
    }

    private sealed class CountingPath(bool blockEvents = false) : ILinkPath
    {
        private int _probes;
        public int Probes => Volatile.Read(ref _probes);
        public TaskCompletionSource RepeatedStatus { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<byte[]> Sent { get; } = [];
        public string Transport => "fixture";
        public bool CanSendUnreliable => false;
        public bool StateOwnLane => blockEvents;
        public LinkPathStatus Status()
        {
            if (Interlocked.Increment(ref _probes) == 5) RepeatedStatus.TrySetResult();
            return blockEvents ? new LinkPathStatus(true, 0, 0, 0) { EventPendingBytes = 256 * 1024 } : default;
        }
        public ValueTask SendAsync(ReadOnlyMemory<byte> frame, bool state, CancellationToken ct)
        {
            Sent.Add(frame.ToArray());
            return ValueTask.CompletedTask;
        }
    }

    private sealed class HandshakeStream : MemoryStream
    {
        private readonly MemoryStream _input = new([Protocol.Handshake, 2, 0, 0, 0]);
        public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Reading.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return _input.Read(buffer, offset, count);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _input.Dispose();
            base.Dispose(disposing);
        }
    }
}
