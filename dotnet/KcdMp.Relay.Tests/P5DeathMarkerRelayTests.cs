// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using KcdMp.Wire;
namespace KcdMp.Relay.Tests;
public class P5DeathMarkerRelayTests(RelayFixture relay) : IClassFixture<RelayFixture>
{
    [Theory]
    [InlineData(DeathMarkerKind.Grave)]
    [InlineData(DeathMarkerKind.MarkerOnly)]
    public async Task Typed_fact_is_reliable_sender_prefixed_with_no_legacy_or_own_echo(DeathMarkerKind kind)
    {
        await using var a = await Peer.ConnectAsync(relay.TcpPort, "marker-owner"); await using var b = await Peer.ConnectAsync(relay.TcpPort, "marker-peer"); await Task.Delay(100);
        var add = new DeathMarkerFact(DeathMarkerAction.Add, kind, 7, 123, 19, 1, 2, 3);
        await a.SendRawAsync(DeathMarkerCodec.BuildUp(add));
        byte[] received = await b.ReadUntilAsync(Protocol.DeathMarkerDown, TimeSpan.FromSeconds(3));
        Assert.Equal(36, received.Length); Assert.Equal(a.Id, received[0]); Assert.True(DeathMarkerCodec.TryRead(received.AsSpan(1), out var fact)); Assert.Equal(add, fact);
        var remove = add with { Action = DeathMarkerAction.Remove, Revision = 20 };
        await a.SendRawAsync(DeathMarkerCodec.BuildUp(remove));
        received = await b.ReadUntilAsync(Protocol.DeathMarkerDown, TimeSpan.FromSeconds(3)); Assert.True(DeathMarkerCodec.TryRead(received.AsSpan(1), out fact)); Assert.Equal(remove, fact);
        Assert.True(await a.NoneOfAsync(Protocol.DeathMarkerDown, TimeSpan.FromMilliseconds(100)));
        Assert.True(await b.NoneOfAsync(Protocol.GraveAddDown, TimeSpan.FromMilliseconds(100)));
    }
    [Theory]
    [InlineData("kind")]
    [InlineData("nan")]
    [InlineData("world")]
    public async Task Malformed_typed_facts_never_cross_the_actual_relay(string error)
    {
        await using var a = await Peer.ConnectAsync(relay.TcpPort, "bad-marker"); await using var b = await Peer.ConnectAsync(relay.TcpPort, "peer"); await Task.Delay(100);
        byte[] bad = DeathMarkerCodec.BuildUp(new(DeathMarkerAction.Add, DeathMarkerKind.MarkerOnly, 7, 123, 1, 1, 2, 3));
        if (error == "kind") bad[5] = 2;
        if (error == "world") Array.Clear(bad, 6, 4);
        if (error == "nan") System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bad.AsSpan(26), float.NaN);
        await a.SendRawAsync(bad); Assert.True(await b.NoneOfAsync(Protocol.DeathMarkerDown, TimeSpan.FromMilliseconds(200)));
    }
}
