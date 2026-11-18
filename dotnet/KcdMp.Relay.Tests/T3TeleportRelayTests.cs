// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using KcdMp.Wire;

namespace KcdMp.Relay.Tests;

public class T3TeleportRelayTests(RelayFixture relay) : IClassFixture<RelayFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Snapshot_nonce_world_source_and_refusal_survive_real_relay_routing(bool refused)
    {
        await using var host = await Peer.ConnectAsync(relay.TcpPort, "tp-host");
        await using var joiner = await Peer.ConnectAsync(relay.TcpPort, "tp-joiner");
        await Task.Delay(100);
        var challenge = new TeleportChallenge(0xF123456789ABCDEF, 7123, TeleportSource.Unstuck);
        var request = new LootMsg(Protocol.W203TpSnapshotRequest, 0, TeleportSnapshotCodec.Build(challenge));
        await joiner.SendRawAsync(request.BuildUp(Protocol.W203Up, host.Id));
        var received = await host.ReadUntilAsync(Protocol.W203Down, TimeSpan.FromSeconds(5));
        Assert.Equal(joiner.Id, received[0]);
        Assert.True(LootMsg.TryDecode(received.AsSpan(1 + Protocol.JoinHeaderLen), out var decoded)); Assert.Equal(request, decoded);
        Assert.True(TeleportSnapshotCodec.TryChallenge(decoded.Text, out var actual)); Assert.Equal(challenge, actual);
        var answer = new TeleportAnswer(challenge.Nonce, challenge.World, !refused, 123.25f, -45, 67, refused ? (byte)0 : (byte)15, 0);
        var response = new LootMsg(Protocol.W203TpSnapshotResponse, 0, TeleportSnapshotCodec.Build(answer));
        await host.SendRawAsync(response.BuildUp(Protocol.W203Up, joiner.Id));
        var back = await joiner.ReadUntilAsync(Protocol.W203Down, TimeSpan.FromSeconds(5));
        Assert.Equal(host.Id, back[0]); Assert.True(LootMsg.TryDecode(back.AsSpan(1 + Protocol.JoinHeaderLen), out var returned));
        Assert.Equal(response, returned); Assert.True(TeleportSnapshotCodec.TryAnswer(returned.Text, out var result)); Assert.Equal(answer, result);
    }
}
