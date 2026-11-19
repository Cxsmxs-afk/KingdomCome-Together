// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;
using KcdMp.Wire;
using Xunit;

namespace KcdMp.Relay.Tests;

// =============================================================================
// WO-102.5 Phase 4: departure handoff must be caught by a timeout, not only a
// clean disconnect (FIN) or an immediate reset (RST). A peer whose machine or
// network vanishes silently -- cable pulled, hard crash -- leaves the relay's
// read parked with neither signal. ClientSession sets NetworkStream.ReadTimeout
// (Tcp:IdleTimeoutMs, TcpSocketService); this is the one test that proves an
// idle connection is actually torn down and the selected host remains the
// only rightful owner even when its connection times out.
//
// Runs its own relay (not RelayFixture's) so its idle timeout can be short
// (300 ms) without shortening the 30 s production default or affecting any
// other test's shared relay.
// =============================================================================
public class IdleTimeoutTests
{
    [Fact]
    public async Task Silent_host_is_dropped_and_the_survivor_stays_without_damage_authority()
    {
        var relay = RelayListeners.CreateApp("--Tcp:IdleTimeoutMs", "300");
        int tcpPort = (await RelayListeners.StartAsync(relay)).Port;
        await using var app = relay;

        var a = await Peer.ConnectAsync(tcpPort, "alpha");
        var b = await Peer.ConnectAsync(tcpPort, "bravo");

        // Rule 2: the lowest ready id is the damage authority -- alpha connected first.
        var roleA = await a.ReadUntilAsync(Protocol.CombatRole, TimeSpan.FromSeconds(2));
        var roleB = await b.ReadUntilAsync(Protocol.CombatRole, TimeSpan.FromSeconds(2));
        Assert.Equal(1, roleA[0]);
        Assert.Equal(0, roleB[0]);

        // Alpha goes silent -- no FIN, no RST, no Ping, nothing. Its socket
        // is simply never touched again. Bravo pings every 100 ms (well
        // under the 300 ms idle timeout) so ONLY alpha's silence is under
        // test -- a real client's own 2 s position heartbeat plays the same
        // role against the 30 s production default.
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(900);
        while (DateTime.UtcNow < deadline)
        {
            await b.SendRawAsync(BuildPing());
            await Task.Delay(100);
        }

        // Read the actual disconnect before the resulting role notice, so a
        // previously queued "not the host" frame cannot satisfy the test.
        var disconnected = await b.ReadUntilAsync(Protocol.Disconnect, TimeSpan.FromSeconds(2));
        Assert.Equal(a.Id, disconnected[0]);
        var newRole = await b.ReadUntilAsync(Protocol.CombatRole, TimeSpan.FromSeconds(2));
        Assert.Equal(0, newRole[0]);

        await a.DisposeAsync();
        await b.DisposeAsync();
        await app.StopAsync();
    }

    private static byte[] BuildPing()
    {
        var packet = new byte[3 + 8];
        packet[0] = Protocol.Ping;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(1), 8);
        BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(3), DateTime.UtcNow.Ticks);
        return packet;
    }
}
