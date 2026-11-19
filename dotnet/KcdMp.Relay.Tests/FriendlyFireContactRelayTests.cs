// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using KcdMp.Wire;

namespace KcdMp.Relay.Tests;

public sealed class FriendlyFireContactRelayTests : IAsyncLifetime
{
    private readonly RelayFixture _relay = new();
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(400);

    public Task InitializeAsync() => _relay.InitializeAsync();
    public Task DisposeAsync() => _relay.DisposeAsync();

    [Fact]
    public async Task Identified_contact_passes_once_and_a_reused_ghost_has_a_new_source_incarnation()
    {
        await using var target = await _relay.ConnectAsync("ff-target");
        Assert.True(await EnableAsync(target));
        byte oldId;
        ulong oldIncarnation;
        var contact = new PlayerHitV8(target.Id, 10, 7, 0, 0);
        await using (var source = await _relay.ConnectAsync("ff-source"))
        {
            oldId = source.Id;
            Assert.True(await EnableAsync(source));
            ulong recipient = await ReadPeerAsync(source, target.Id);
            byte[] packet = contact.BuildUp(37, recipient);
            await source.SendRawAsync(packet);
            await source.SendRawAsync(packet);
            oldIncarnation = await ReadAsync(target, oldId, 37, contact);
            Assert.True(await target.NoneOfAsync(Protocol.PlayerHitV8Down, Quiet));
            await source.SendRawAsync(contact.BuildUp(38, recipient));
            Assert.Equal(oldIncarnation, await ReadAsync(target, oldId, 38, contact));
        }
        Assert.Equal(new byte[] { oldId }, await target.ReadUntilAsync(Protocol.Disconnect, Wait));
        await using var replacement = await _relay.ConnectAsync("ff-replacement");
        Assert.Equal(oldId, replacement.Id);
        Assert.True(await EnableAsync(replacement));
        await replacement.SendRawAsync(contact.BuildUp(1, await ReadPeerAsync(replacement, target.Id)));
        Assert.True(await ReadAsync(target, oldId, 1, contact) > oldIncarnation);
    }

    [Fact]
    public async Task Identified_hit_fails_closed_between_legacy_and_modern_peers()
    {
        await using var modern = await _relay.ConnectAsync("ff-modern");
        await using var legacy = await _relay.ConnectAsync("ff-legacy");
        Assert.True(await EnableAsync(modern));
        await modern.SendRawAsync(new PlayerHitV8(legacy.Id, 10, 7, 0, 0).BuildUp(1, 1));
        await legacy.SendRawAsync(new PlayerHitV8(modern.Id, 10, 7, 0, 0).BuildUp());
        Assert.True(await legacy.NoneOfAsync(Protocol.PlayerHitV8Down, Quiet));
        Assert.True(await modern.NoneOfAsync(Protocol.PlayerHitV8Down, Quiet));
        Assert.True(await EnableAsync(legacy));
        var contact = new PlayerHitV8(legacy.Id, 10, 7, 0, 0);
        await modern.SendRawAsync(contact.BuildUp());
        Assert.True(await legacy.NoneOfAsync(Protocol.PlayerHitV8Down, Quiet));
        await modern.SendRawAsync(contact.BuildUp(2, await ReadPeerAsync(modern, legacy.Id)));
        Assert.True(await ReadAsync(legacy, modern.Id, 2, contact) > 0);
    }

    [Fact]
    public async Task Delayed_first_contact_cannot_reach_a_replacement_of_its_named_victim()
    {
        await using var source = await _relay.ConnectAsync("ff-source");
        Assert.True(await EnableAsync(source));
        byte oldId;
        ulong oldIncarnation;
        byte[] delayed;
        PlayerHitV8 contact;
        await using (var original = await _relay.ConnectAsync("ff-original"))
        {
            oldId = original.Id;
            Assert.True(await EnableAsync(original));
            oldIncarnation = await ReadPeerAsync(source, oldId);
            contact = new PlayerHitV8(oldId, 10, 7, 0, 0);
            delayed = contact.BuildUp(15, oldIncarnation);
        }
        Assert.Equal(new byte[] { oldId }, await source.ReadUntilAsync(Protocol.Disconnect, Wait));
        await using var replacement = await _relay.ConnectAsync("ff-replacement");
        Assert.Equal(oldId, replacement.Id);
        Assert.True(await EnableAsync(replacement));
        ulong recipient = await ReadPeerAsync(source, replacement.Id);
        Assert.True(recipient > oldIncarnation);

        await source.SendRawAsync(delayed);
        Assert.True(await replacement.NoneOfAsync(Protocol.PlayerHitV8Down, Quiet));
        await source.SendRawAsync(contact.BuildUp(16, recipient));
        Assert.True(await ReadAsync(replacement, source.Id, 16, contact) > 0);
    }

    [Fact]
    public async Task Interior_hit_lengths_are_rejected_and_the_next_valid_frame_survives()
    {
        for (int size = PlayerHitV8.UpLen + 1; size < PlayerHitV8.IdentifiedUpLen; ++size)
            Assert.False(Protocol.IsUpLenOk(Protocol.PlayerHitV8Up, size), $"Unexpected hit length {size}");
        await using var target = await _relay.ConnectAsync("ff-target");
        await using var source = await _relay.ConnectAsync("ff-source");
        Assert.True(await EnableAsync(target));
        Assert.True(await EnableAsync(source));
        ulong recipient = await ReadPeerAsync(source, target.Id);
        var contact = new PlayerHitV8(target.Id, 10, 7, 0, 0);
        byte[] valid = contact.BuildUp(17, recipient);
        byte[] bad = valid[..(3 + PlayerHitV8.UpLen + 1)];
        bad[1] = PlayerHitV8.UpLen + 1;
        await source.SendRawAsync(bad);
        await source.SendRawAsync(valid);
        Assert.True(await ReadAsync(target, source.Id, 17, contact) > 0);
        Assert.True(await target.NoneOfAsync(Protocol.PlayerHitV8Down, Quiet));
    }

    private static async Task<bool> EnableAsync(Peer peer)
    {
        await peer.SendRawAsync(Protocol.BuildLinkHelloUp(Protocol.LinkCapPvpContact));
        var hello = await peer.ReadUntilAsync(Protocol.LinkHelloDown, Wait);
        return Protocol.TryParseLinkHello(hello, out _, out ushort caps, out _, out _) &&
            (caps & Protocol.LinkCapPvpContact) != 0;
    }

    private static async Task<ulong> ReadAsync(Peer target, byte source, ulong sequence, PlayerHitV8 wanted)
    {
        byte[] payload = await target.ReadUntilAsync(Protocol.PlayerHitV8Down, Wait);
        Assert.Equal(PlayerHitV8.IdentifiedDownLen, payload.Length);
        Assert.True(PlayerHitV8.TryDecodeDown(payload, out byte observedSource, out var hit, out ulong observedSequence, out ulong incarnation));
        Assert.Equal(source, observedSource);
        Assert.Equal(sequence, observedSequence);
        Assert.Equal(wanted, hit);
        return incarnation;
    }

    private static async Task<ulong> ReadPeerAsync(Peer peer, byte expected)
    {
        var frame = await peer.ReadUntilAsync(Protocol.PvpPeerDown, Wait);
        Assert.True(Protocol.TryReadPvpPeer(frame, out byte ghost, out ulong incarnation));
        Assert.Equal(expected, ghost);
        return incarnation;
    }
}
