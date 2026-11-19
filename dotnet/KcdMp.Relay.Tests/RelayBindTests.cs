// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KcdMp.Relay.Tests;

/// <summary>
/// Exercise concurrent real relay listeners and the handshake reader's failure report.
/// </summary>
public class RelayBindTests
{
    [Fact]
    public async Task Concurrent_relays_complete_handshakes_on_distinct_ports()
    {
        const int count = 8;
        var fixtures = Enumerable.Range(0, count).Select(_ => new RelayFixture()).ToArray();
        try
        {
            await Task.WhenAll(fixtures.Select(fixture => fixture.InitializeAsync()));
            Assert.All(fixtures, fixture =>
                Assert.Equal("0", fixture.Services.GetRequiredService<IConfiguration>()["Tcp:Port"]));
            Assert.Equal(count, fixtures.Select(fixture => fixture.TcpPort).Distinct().Count());
            await Task.WhenAll(fixtures.Select(async (fixture, index) =>
            {
                // ConnectAsync returns only after the relay's Ack. The first client uses wire id 0.
                await using var peer = await Peer.ConnectAsync(fixture.TcpPort, "bind" + index.ToString());
                Assert.Equal(0, peer.Id);
            }));
        }
        finally
        {
            await Task.WhenAll(fixtures.Select(async fixture =>
            {
                try { await fixture.DisposeAsync(); }
                catch (ObjectDisposedException) { }
            }));
        }
    }

    [Fact]
    public async Task A_truncated_handshake_identifies_the_listener_and_never_logs_payload()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        const string payload = "synthetic-body";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var send = SendTruncatedFrameAsync();
        try
        {
            var error = await Assert.ThrowsAsync<EndOfStreamException>(() => Peer.ConnectAsync(endpoint.Port, "truncated"));
            await send;
            Assert.Contains($"listener={endpoint}", error.Message);
            Assert.Contains("fixture=unassigned", error.Message);
            Assert.Contains("reply=0xFF declared=60 received=14 close=peer-eof", error.Message);
            Assert.DoesNotContain(payload, error.Message);
        }
        finally { listener.Stop(); }

        async Task SendTruncatedFrameAsync()
        {
            using var accepted = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = accepted.GetStream();
            var header = new byte[3];
            await stream.ReadExactlyAsync(header, timeout.Token);
            Assert.Equal(Protocol.Handshake, header[0]);
            await stream.ReadExactlyAsync(new byte[BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1))], timeout.Token);
            await stream.WriteAsync(new byte[] { Protocol.Ack, 60, 0 }, timeout.Token);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(payload), timeout.Token);
            accepted.Client.Shutdown(SocketShutdown.Send);
        }
    }
}
