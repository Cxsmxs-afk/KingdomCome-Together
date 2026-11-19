// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Text;
using KcdMp.Wire;
namespace KcdMp.Relay.Tests;
public sealed class P5DeathMarkerRelayTests : IAsyncLifetime
{
    // The relay can hold a departing host's session while another peer remains.
    // Each case owns its session so an earlier socket's teardown cannot hold this one.
    private readonly RelayFixture _relay = new();
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    public Task InitializeAsync() => _relay.InitializeAsync();
    public Task DisposeAsync() => _relay.DisposeAsync();

    private static async Task ConfirmRecipientAsync(Peer sender, Peer receiver, string name)
    {
        // The relay queues Ack first, then admits the peer to broadcast fan-out.
        byte[] ready = await sender.ReadUntilAsync(Protocol.Name, Wait);
        Assert.NotEmpty(ready);
        Assert.Equal(receiver.Id, ready[0]);
        Assert.Equal(name, Encoding.UTF8.GetString(ready.AsSpan(1)));
    }

    [Theory]
    [InlineData(DeathMarkerKind.Grave)]
    [InlineData(DeathMarkerKind.MarkerOnly)]
    public async Task Typed_fact_is_reliable_sender_prefixed_with_no_legacy_or_own_echo(DeathMarkerKind kind)
    {
        await using var a = await _relay.ConnectAsync("marker-owner");
        await using var b = await _relay.ConnectAsync("marker-peer");
        await ConfirmRecipientAsync(a, b, "marker-peer");
        var add = new DeathMarkerFact(DeathMarkerAction.Add, kind, 7, 123, 19, 1, 2, 3);
        await a.SendRawAsync(DeathMarkerCodec.BuildUp(add));
        byte[] received = await b.ReadUntilAsync(Protocol.DeathMarkerDown, Wait);
        Assert.Equal(36, received.Length); Assert.Equal(a.Id, received[0]); Assert.True(DeathMarkerCodec.TryRead(received.AsSpan(1), out var fact)); Assert.Equal(add, fact);
        var remove = add with { Action = DeathMarkerAction.Remove, Revision = 20 };
        await a.SendRawAsync(DeathMarkerCodec.BuildUp(remove));
        received = await b.ReadUntilAsync(Protocol.DeathMarkerDown, Wait); Assert.True(DeathMarkerCodec.TryRead(received.AsSpan(1), out fact)); Assert.Equal(remove, fact);
        Assert.True(await a.NoneOfAsync(Protocol.DeathMarkerDown, TimeSpan.FromMilliseconds(100)));
        Assert.True(await b.NoneOfAsync(Protocol.GraveAddDown, TimeSpan.FromMilliseconds(100)));
    }
    [Theory]
    [InlineData("kind")]
    [InlineData("nan")]
    [InlineData("world")]
    public async Task Malformed_typed_facts_never_cross_the_actual_relay(string error)
    {
        await using var a = await _relay.ConnectAsync("bad-marker");
        await using var b = await _relay.ConnectAsync("peer");
        await ConfirmRecipientAsync(a, b, "peer");
        byte[] bad = DeathMarkerCodec.BuildUp(new(DeathMarkerAction.Add, DeathMarkerKind.MarkerOnly, 7, 123, 1, 1, 2, 3));
        if (error == "kind") bad[5] = 2;
        if (error == "world") Array.Clear(bad, 6, 4);
        if (error == "nan") System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bad.AsSpan(26), float.NaN);
        var valid = new DeathMarkerFact(DeathMarkerAction.Add, DeathMarkerKind.MarkerOnly, 7, 124, 2, 1, 2, 3);
        await a.SendRawAsync(bad);
        await a.SendRawAsync(DeathMarkerCodec.BuildUp(valid));
        byte[] received = await b.ReadUntilAsync(Protocol.DeathMarkerDown, Wait);
        Assert.Equal(a.Id, received[0]);
        Assert.True(DeathMarkerCodec.TryRead(received.AsSpan(1), out var fact));
        Assert.Equal(valid, fact);
        Assert.True(await b.NoneOfAsync(Protocol.DeathMarkerDown, TimeSpan.FromMilliseconds(200)));
    }
}
