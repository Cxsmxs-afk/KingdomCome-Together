// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Threading.Channels;
using System.Text;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using KcdMp.Client;
using KcdMp.Server.Features.ClientHandling;
using KcdMp.Server.Features.Interactions;
using KcdMp.Server.Features.Tcp;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit.Abstractions;

namespace KcdMp.Relay.Tests;

public sealed class RelaySessionFaultTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Relay_handshake_target_and_clock_logs_keep_session_context_without_peer_identity()
    {
        const string hostName = "privacy-fixture-host", guestName = "privacy-fixture-guest", otherName = "privacy-fixture-other";
        const string refusedName = "privacy-fixture-refused", fullName = "privacy-fixture-full";
        const string remote = "192.0.2.153:41315", badRelease = "privacy-fixture-raw-release";
        var sink = new CaptureSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ServerInfo:MaxPlayers"] = "3",
        }).Build();
        var clients = new ClientHandler(logger, configuration);
        var runner = new ClientSessionRunner(logger, configuration, clients,
            new TcpBroadcastService(configuration, clients), new SessionManager(logger));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var deadline = TimeSpan.FromSeconds(5);
        async Task<(Peer Peer, byte Type, byte[] Payload)> Connect(string name, string release, bool loopback = false)
        {
            var pending = Peer.ConnectRawAsync(port, name, release, Protocol.Version, "redacted-log");
            using var accepting = new CancellationTokenSource(deadline);
            var socket = await listener.AcceptTcpClientAsync(accepting.Token);
            runner.Start(runner.Create(new RelayConnection(socket.GetStream(), remote, loopback, "tcp", socket.Dispose, "fixture-endpoint")));
            return await pending;
        }
        static byte[] Frame(byte type, byte[] payload)
        {
            byte[] frame = new byte[3 + payload.Length];
            frame[0] = type;
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1), (ushort)payload.Length);
            payload.CopyTo(frame, 3);
            return frame;
        }
        async Task Fence(Peer peer)
        {
            var ping = new byte[Protocol.PingPayloadLen];
            await peer.SendRawAsync(Frame(Protocol.Ping, ping));
            Assert.Equal(ping, await peer.ReadUntilAsync(Protocol.Pong, deadline));
        }
        static byte[] Npc(string name, float x, float yaw)
        {
            var tag = Encoding.ASCII.GetBytes(name);
            var body = new byte[1 + tag.Length + Protocol.NpcStateFixedTail];
            body[0] = (byte)tag.Length;
            tag.CopyTo(body, 1);
            BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(1 + tag.Length), x);
            BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(13 + tag.Length), yaw);
            return Frame(Protocol.NpcStateUp, body);
        }

        var bad = await Connect(refusedName, badRelease);
        await using var badPeer = bad.Peer;
        Assert.Equal(Protocol.ReleaseVersionMismatch, bad.Type);
        var h = await Connect(hostName, ReleaseVersionInfo.Wire, true);
        await using var host = h.Peer;
        var g = await Connect(guestName, ReleaseVersionInfo.Wire);
        await using var guest = g.Peer;
        var a = await Connect(otherName, ReleaseVersionInfo.Wire);
        await using var other = a.Peer;
        Assert.All(new[] { h.Type, g.Type, a.Type }, type => Assert.Equal(Protocol.Ack, type));
        var full = await Connect(fullName, ReleaseVersionInfo.Wire);
        await using var fullPeer = full.Peer;
        Assert.Equal(Protocol.ServerFull, full.Type);

        await guest.SendRawAsync(Frame(Protocol.PlayerHitUp, new byte[Protocol.PlayerHitUpPayloadLen]));
        await guest.SendRawAsync(Npc("guard", float.NaN, 0));
        await guest.SendRawAsync(Npc("guard", 0, float.NaN));
        await guest.SendRawAsync(Npc("guard", 0, 0));
        await guest.SendRawAsync(Npc("guard", 1_000_000, 0));
        await guest.SendRawAsync(Npc("kcd2mp_body", 0, 0));
        foreach (byte phase in new[] { Protocol.TimeSkipPhaseStart, Protocol.TimeSkipPhaseStart, Protocol.TimeSkipPhaseDone, Protocol.TimeSkipPhaseSync })
        {
            var payload = new byte[Protocol.TimeSkipUpPayloadLen];
            payload[0] = phase;
            await guest.SendRawAsync(Frame(Protocol.TimeSkipUp, payload));
        }
        await Fence(guest);
        await other.SendRawAsync(Npc("guard", 0, 0));
        await Fence(other);
        var replacement = await Connect(guestName, ReleaseVersionInfo.Wire);
        await using var replacementPeer = replacement.Peer;
        Assert.Equal(Protocol.Ack, replacement.Type);
        await Fence(replacementPeer);

        string[] lines = sink.Lines.ToArray();
        string[] privateValues = [hostName, guestName, otherName, refusedName, fullName, badRelease, remote, "192.0.2.153"];
        Assert.All(lines, line => Assert.All(privateValues, value => Assert.DoesNotContain(value, line)));
        foreach (string marker in new[] { "reject-release", "server is full", "replaces its older connection", "PlayerHitUp without",
                     "non-finite position", "non-finite rotZ", "implausible movement", "reserved-name", "stale-owner",
                     "began the session's active skip", "skip done", "clock sync", "absorbed (joined/duplicate)" })
        {
            string line = Assert.Single(lines, item => item.Contains(marker, StringComparison.Ordinal));
            Assert.Matches(@"session=[1-9a-f][0-9a-f]* name=<redacted> id=(?:[0-9]+|-) loopback=[01]", line);
        }
        var connected = lines.Where(line => line.StartsWith("MP-FIX RELAY connected ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(4, connected.Length);
        Assert.All(connected, line => Assert.Contains($"protocol={Protocol.Version} release={ReleaseVersionInfo.Wire}", line));
        foreach (var line in connected) output.WriteLine(line);
    }

    [Fact]
    public async Task A_crashing_relay_session_logs_one_scrubbed_fault_with_location_and_session_id()
    {
        const string address = "192.0.2.153";
        const string player = "fixture-unlisted-player";
        string steam = "76561" + new string('0', 12);
        var sink = new FaultSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var configuration = new ConfigurationBuilder().Build();
        var clients = new ClientHandler(logger, configuration);
        var runner = new ClientSessionRunner(logger, configuration, clients,
            new TcpBroadcastService(configuration, clients), new SessionManager(logger));
        using var stream = new CrashingStream(player, $"fixture read at {address} steamid:{steam} password=fixture-private");
        var session = runner.Create(new RelayConnection(stream, "fixture", false, "tcp", stream.Dispose));

        runner.Start(session);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string fault = await sink.Lines.Reader.ReadAsync(deadline.Token);
        Assert.Equal(0, clients.ClientCount);
        Assert.Contains("type=InvalidOperationException", fault);
        Assert.Contains("ClientSession.ReadExactWithIdleTimeoutAsync", fault);
        Assert.Matches(@"session=[0-9a-f]+", fault);
        Assert.Contains("fixture read", fault);
        Assert.Contains("password=<redacted>", fault);
        Assert.DoesNotContain(address, fault);
        Assert.DoesNotContain(steam, fault);
        Assert.DoesNotContain("fixture-private", fault);
        Assert.False(sink.Lines.Reader.TryRead(out _));
        Assert.True(sink.Connected.Task.IsCompletedSuccessfully, "missing redacted connection row");
        string connect = await sink.Connected.Task;
        Assert.Contains($"session={fault.Split(" session=")[1].Split(' ')[0]}", connect);
        Assert.Contains("name=<redacted>", connect);
        Assert.DoesNotContain(player, connect);
        output.WriteLine(fault);
    }

    private sealed class FaultSink : ILogEventSink
    {
        public Channel<string> Lines { get; } = Channel.CreateUnbounded<string>();
        public TaskCompletionSource<string> Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Emit(LogEvent logEvent)
        {
            if (logEvent.Properties.TryGetValue("Fault", out var property) &&
                property is ScalarValue { Value: string fault })
                Lines.Writer.TryWrite(fault);
            else if (logEvent.MessageTemplate.Text.StartsWith("MP-FIX RELAY ", StringComparison.Ordinal))
                Connected.TrySetResult(logEvent.RenderMessage());
        }
    }

    private sealed class CaptureSink : ILogEventSink
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public void Emit(LogEvent logEvent) => Lines.Enqueue(logEvent.RenderMessage());
    }

    private sealed class CrashingStream(string player, string fault) : MemoryStream
    {
        private readonly MemoryStream _input = new(
            [Protocol.Handshake, (byte)(player.Length + 2), 0, Protocol.Version, (byte)player.Length, .. Encoding.ASCII.GetBytes(player)]);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _input.Position < _input.Length ? _input.ReadAsync(buffer, offset, count, cancellationToken) :
            Task.FromException<int>(new InvalidOperationException(fault));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new InvalidOperationException(fault));

        protected override void Dispose(bool disposing)
        {
            if (disposing) _input.Dispose();
            base.Dispose(disposing);
        }
    }
}
