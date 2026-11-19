// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
namespace KcdMp.Relay.Tests;

/// <summary>
/// The death-marker class shares one relay and runs beside every other relay
/// fixture. A reused loopback port accepts the TCP connection and then closes
/// it during the handshake. These starts overlap on purpose.
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
            Assert.Equal(count, fixtures.Select(fixture => fixture.TcpPort).Distinct().Count());
            await Task.WhenAll(fixtures.Select(async (fixture, index) =>
            {
                // ConnectAsync returns only after the relay's Ack. The first client uses wire id 0.
                await using var _ = await Peer.ConnectAsync(fixture.TcpPort, "bind" + index.ToString());
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
}
