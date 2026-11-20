// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using KcdMp.Server.Features.ClientHandling;
using KcdMp.Wire;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace KcdMp.Client.Tests;

/// <summary>Fresh relay services and real TCP sessions for each fixture.</summary>
internal sealed class ReplayRelay : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<TcpClient> _clients = [];
    private readonly Task _accept;

    private ReplayRelay(WebApplication app, TcpListener listener, ClientSessionRunner runner)
    {
        _app = app;
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _accept = AcceptAsync(runner);
    }

    public int Port { get; }

    public static async Task<ReplayRelay> StartAsync(CancellationToken ct = default, string logLevel = "Fatal")
    {
        ct.ThrowIfCancellationRequested();
        // Reuse the shipped relay's services. The test owns its TCP listener so
        // the OS assigns a loopback-only port without a free-port/bind gap.
        var app = Server.Program.CreateApp(["--Echo", "false", "--Serilog:MinimumLevel:Default", logLevel]);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            return new ReplayRelay(app, listener, app.Services.GetRequiredService<ClientSessionRunner>());
        }
        catch
        {
            listener.Stop();
            await app.DisposeAsync();
            throw;
        }
    }

    private async Task AcceptAsync(ClientSessionRunner runner)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _clients.Add(client);
                client.NoDelay = true;
                runner.Start(runner.Create(RelayConnection.FromTcp(client)));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public async Task StopAsync(bool gracefulEof = false)
    {
        _stop.Cancel();
        try { await _accept; }
        finally
        {
            _listener.Stop();
            for (int i = _clients.Count - 1; i >= 0; i--)
            {
                var client = _clients[i];
                if (gracefulEof)
                {
                    try { client.Client?.Shutdown(SocketShutdown.Send); }
                    catch (SocketException) { }
                    catch (ObjectDisposedException) { }
                }
                client.Dispose();
            }
            _clients.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(); }
        finally
        {
            _stop.Dispose();
            await _app.DisposeAsync();
        }
    }
}

internal readonly record struct ReplayPacket(byte Type, byte[] Payload);

/// <summary>A test-only TCP replay endpoint. Serial operations on each peer preserve wire order.</summary>
internal sealed class ReplayPeer : IAsyncDisposable
{
    private static long _pingStamp = long.MinValue;
    private readonly TcpClient _client;
    private readonly NetworkStream _wire;
    private readonly List<ReplayPacket> _received = [];

    private ReplayPeer(TcpClient client)
    {
        _client = client;
        _wire = client.GetStream();
    }

    public byte Id { get; private set; }

    // After the handshake, an integration test may hand this endpoint to the production reader.
    public Stream Wire => _wire;

    // Let the relay announce a real departure while the reader drains its stream.
    public void FinishSending() => _client.Client.Shutdown(SocketShutdown.Send);

    public static async Task<ReplayPeer> ConnectAsync(int port, string name, CancellationToken ct = default)
    {
        using var deadline = Deadline(ct);
        var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
            var peer = new ReplayPeer(tcp);
            await peer.SendAsync(RelayConnector.BuildHandshake(name, ReleaseVersionInfo.Wire), deadline.Token);
            var reply = await peer.ReadAsync(deadline.Token);
            if (reply.Type != Protocol.Ack || reply.Payload.Length != 1)
                throw new IOException($"Relay rejected replay peer {name}: expected a one-byte Ack, received 0x{reply.Type:X2}.");
            peer.Id = reply.Payload[0];
            // Ack is queued before the server marks the peer ready for broadcasts.
            await peer.BarrierAsync(deadline.Token);
            return peer;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    public static byte[] Frame(byte type, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(payload));
        byte[] packet = new byte[3 + payload.Length];
        packet[0] = type;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(1), (ushort)payload.Length);
        payload.CopyTo(packet.AsSpan(3));
        return packet;
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
    {
        if (frame.Length < 3 || BinaryPrimitives.ReadUInt16LittleEndian(frame.Span[1..]) != frame.Length - 3)
            throw new ArgumentException("A replay write needs one complete relay frame.", nameof(frame));
        using var deadline = Deadline(ct);
        await _wire.WriteAsync(frame, deadline.Token);
    }

    /// <summary>Fence input on this connection and the reliable output ahead of its Pong.</summary>
    public async Task BarrierAsync(CancellationToken ct = default)
    {
        using var deadline = Deadline(ct);
        long stamp = Interlocked.Increment(ref _pingStamp);
        byte[] tick = new byte[Protocol.PingPayloadLen];
        BinaryPrimitives.WriteInt64LittleEndian(tick, stamp);
        await SendAsync(Frame(Protocol.Ping, tick), deadline.Token);
        while (true)
        {
            var packet = await ReadAsync(deadline.Token);
            if (packet.Type == Protocol.Pong && packet.Payload.Length == tick.Length &&
                BinaryPrimitives.ReadInt64LittleEndian(packet.Payload) == stamp)
                return;
            _received.Add(packet);
        }
    }

    public async Task<IReadOnlyList<ReplayPacket>> DrainAsync(CancellationToken ct = default)
    {
        await BarrierAsync(ct);
        ReplayPacket[] packets = [.. _received];
        _received.Clear();
        return packets;
    }

    public async Task<byte[]> ReadUntilAsync(byte type, CancellationToken ct = default)
    {
        int index = await WaitForAsync(packet => packet.Type == type, ct);
        var packet = _received[index];
        _received.RemoveAt(index);
        return packet.Payload;
    }

    /// <summary>Replay intact frames and capture real relay output for the other endpoint.</summary>
    public async Task<IReadOnlyList<ReplayPacket>> ReplayToAsync(ReplayPeer receiver, IEnumerable<byte[]> frames,
        CancellationToken ct = default)
    {
        if (ReferenceEquals(this, receiver)) throw new ArgumentException("Replay needs a separate receiving peer.", nameof(receiver));
        var captured = new List<ReplayPacket>(await receiver.DrainAsync(ct));
        foreach (byte[] frame in frames)
        {
            await SendAsync(frame, ct);
            await BarrierAsync(ct);
            // The production scheduler may put Pong ahead of a replaceable
            // state sample. Wait for this exact sender's pose before feeding
            // the next recorded sample, so latest-wins cannot hide the case.
            if (frame[0] is Protocol.Position or Protocol.NpcStateUp)
                await receiver.WaitForAsync(packet => MatchesState(frame, packet), ct);
            captured.AddRange(await receiver.DrainAsync(ct));
        }
        return captured;
    }

    private async Task<int> WaitForAsync(Predicate<ReplayPacket> match, CancellationToken ct)
    {
        int index = _received.FindIndex(match);
        if (index >= 0) return index;
        using var deadline = Deadline(ct);
        while (true)
        {
            var packet = await ReadAsync(deadline.Token);
            _received.Add(packet);
            if (match(packet)) return _received.Count - 1;
        }
    }

    private bool MatchesState(byte[] upstream, ReplayPacket packet)
    {
        byte type = packet.Type;
        ReadOnlySpan<byte> down = packet.Payload;
        if (type == Protocol.PresentationPoseDown &&
            PresentationTimeWire.TryRead(type, down, out _, out byte inner, out int offset))
        {
            type = inner;
            down = down[offset..];
        }
        byte expected = upstream[0] == Protocol.Position ? Protocol.Ghost : Protocol.NpcStateDown;
        ReadOnlySpan<byte> up = upstream.AsSpan(3);
        if (type != expected || down.Length != up.Length + 1 || down[0] != Id) return false;
        down = down[1..];
        if (expected == Protocol.NpcStateDown) return down.SequenceEqual(up);
        const int flagsIndex = Protocol.PositionPayloadLen - 1;
        return up.Length > flagsIndex && down[..flagsIndex].SequenceEqual(up[..flagsIndex]) &&
               down[flagsIndex] == (byte)(up[flagsIndex] & ~Protocol.PositionFlagHostClaim) &&
               down[(flagsIndex + 1)..].SequenceEqual(up[(flagsIndex + 1)..]);
    }

    private async Task<ReplayPacket> ReadAsync(CancellationToken ct)
    {
        byte[] header = new byte[3];
        await _wire.ReadExactlyAsync(header, ct);
        byte[] payload = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1))];
        await _wire.ReadExactlyAsync(payload, ct);
        return new ReplayPacket(header[0], payload);
    }

    private static CancellationTokenSource Deadline(CancellationToken ct)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        source.CancelAfter(TimeSpan.FromSeconds(10));
        return source;
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
