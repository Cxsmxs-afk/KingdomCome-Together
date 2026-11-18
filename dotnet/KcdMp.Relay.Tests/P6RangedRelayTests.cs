// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using KcdMp.Wire;

namespace KcdMp.Relay.Tests;

// Real Program.CreateApp/TCP handshake and shipped codecs, not a routing-rule mirror.
public sealed class P6RangedRelayTests(RelayFixture relay) : IClassFixture<RelayFixture>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5), Quiet = TimeSpan.FromMilliseconds(250);
    private static readonly Guid Bow = Guid.Parse("00000009-0000-0000-0000-000000000000");
    private async Task<(Peer Host, Peer Joiner, Peer Third)> Three()
    {
        var host = await Peer.ConnectAsync(relay.TcpPort, "p6-host");
        var joiner = await Peer.ConnectAsync(relay.TcpPort, "p6-joiner");
        var third = await Peer.ConnectAsync(relay.TcpPort, "p6-third");
        await Task.Delay(100); // existing real relay fixture readiness contract
        return (host, joiner, third);
    }
    private static byte[] Packet(byte target, byte kind, string text, uint token = 10) => new LootMsg(kind, token, text).BuildUp(Protocol.W164Up, target);
    private static (byte Source, uint Join, LootMsg Message) Decode(byte[] payload)
    {
        Assert.True(Protocol.TrySplitJoinDown(payload, out byte source, out _, out uint join, out var body));
        Assert.True(LootMsg.TryDecode(body, out var message)); return (source, join, message);
    }
    private static MeasuredMissileRequest Missile() => new(new(7, 10, 20, 30), "npc_bandit_01", 5.5f, 1.25f);

    [Fact]
    public async Task Typed_bow_steps_cross_both_directions_in_FIFO_with_no_sender_echo_or_third_peer_broadcast()
    {
        var peers = await Three(); await using var host = peers.Host; await using var joiner = peers.Joiner; await using var third = peers.Third;
        byte[] phases = [1, 2, 3, 0, 6];
        foreach (var (sender, receiver, target) in new[] { (host, joiner, joiner.Id), (joiner, host, Protocol.JoinTargetHost) })
        {
            var expected = phases.Select((p, i) => new RangedStepV2(7, 11, 22, (uint)(i + 1), p, RangedWeaponKind.Bow, Bow, 0, 1)).ToArray();
            foreach (var step in expected) await sender.SendRawAsync(Packet(target, Protocol.W164RangedStep, step.Encode(), step.Sequence));
            foreach (var step in expected)
            {
                var (source, join, message) = Decode(await receiver.ReadUntilAsync(Protocol.W164Down, Wait));
                Assert.Equal(sender.Id, source); Assert.Equal(0u, join); Assert.Equal(step.Sequence, message.Tok); Assert.Equal(Protocol.W164RangedStep, message.Kind);
                Assert.True(RangedStepV2.TryDecode(message.Text, out var actual)); Assert.Equal(step, actual);
            }
            Assert.True(await sender.NoneOfAsync(Protocol.W164Down, Quiet)); Assert.True(await third.NoneOfAsync(Protocol.W164Down, Quiet));
        }
    }
    [Fact]
    public async Task Measured_request_is_joiner_to_host_and_result_is_host_to_that_joiner_only()
    {
        var peers = await Three(); await using var host = peers.Host; await using var joiner = peers.Joiner; await using var third = peers.Third;
        var request = Missile(); await joiner.SendRawAsync(Packet(Protocol.JoinTargetHost, Protocol.W164MeasuredMissile, request.Encode()));
        var down = Decode(await host.ReadUntilAsync(Protocol.W164Down, Wait));
        Assert.Equal(joiner.Id, down.Source); Assert.Equal(Protocol.W164MeasuredMissile, down.Message.Kind);
        Assert.True(MeasuredMissileRequest.TryDecode(down.Message.Text, out var actual)); Assert.Equal(request, actual);
        Assert.True(await joiner.NoneOfAsync(Protocol.W164Down, Quiet)); Assert.True(await third.NoneOfAsync(Protocol.W164Down, Quiet));
        var verdict = new MeasuredMissileResult(request.Key, MeasuredMissileStatus.Accepted, 1, request.Health, request.Stamina);
        await host.SendRawAsync(Packet(joiner.Id, Protocol.W164RealHit, verdict.Encode()));
        down = Decode(await joiner.ReadUntilAsync(Protocol.W164Down, Wait)); Assert.Equal(host.Id, down.Source); Assert.Equal(Protocol.W164RealHit, down.Message.Kind);
        Assert.True(MeasuredMissileResult.TryDecode(down.Message.Text, out var accepted)); Assert.Equal(verdict.Key, accepted.Key); Assert.Equal(verdict.Status, accepted.Status);
        Assert.Equal(verdict.Health, accepted.Health); Assert.Equal(verdict.Stamina, accepted.Stamina);
        Assert.True(await host.NoneOfAsync(Protocol.W164Down, Quiet)); Assert.True(await third.NoneOfAsync(Protocol.W164Down, Quiet));
    }
    [Fact]
    public async Task Wrong_producer_wrong_target_legacy_or_malformed_measured_and_ranged_frames_are_dropped_without_breaking_framing()
    {
        var peers = await Three(); await using var host = peers.Host; await using var joiner = peers.Joiner; await using var third = peers.Third;
        var request = Missile(); var verdict = new MeasuredMissileResult(request.Key, MeasuredMissileStatus.Accepted, 1, 5.5f, 1.25f);
        await host.SendRawAsync(Packet(joiner.Id, Protocol.W164MeasuredMissile, request.Encode())); // host cannot claim another client's contact
        await joiner.SendRawAsync(Packet(third.Id, Protocol.W164MeasuredMissile, request.Encode())); // source contact goes only to authority
        await third.SendRawAsync(Packet(joiner.Id, Protocol.W164RealHit, verdict.Encode())); // joiner cannot manufacture a host verdict
        await joiner.SendRawAsync(Packet(Protocol.JoinTargetHost, Protocol.W164RealHit, verdict.Encode()));
        await host.SendRawAsync(Packet(Protocol.JoinTargetHost, Protocol.W164RealHit, verdict.Encode())); // never self-echo
        await host.SendRawAsync(Packet(200, Protocol.W164RealHit, verdict.Encode()));
        await host.SendRawAsync(Packet(joiner.Id, Protocol.W164RangedStep, "v1|3"));
        await host.SendRawAsync(Packet(joiner.Id, Protocol.W164RangedStep, new RangedStepV2(7, 11, 22, 1, 3, RangedWeaponKind.Bow, Guid.Empty, 0, 1).Encode()));
        await joiner.SendRawAsync(Packet(Protocol.JoinTargetHost, Protocol.W164MeasuredMissile, "m1|broken"));
        await host.SendRawAsync(Packet(joiner.Id, Protocol.W164RealHit, "m1|broken"));
        Assert.True(await host.NoneOfAsync(Protocol.W164Down, Quiet)); Assert.True(await joiner.NoneOfAsync(Protocol.W164Down, Quiet)); Assert.True(await third.NoneOfAsync(Protocol.W164Down, Quiet));
        // An actual valid new request and its refusal still arrive on these same connections.
        await joiner.SendRawAsync(Packet(Protocol.JoinTargetHost, Protocol.W164MeasuredMissile, request.Encode(), 99));
        var down = Decode(await host.ReadUntilAsync(Protocol.W164Down, Wait)); Assert.Equal(0u, down.Join); Assert.Equal(99u, down.Message.Tok); Assert.Equal(joiner.Id, down.Source);
        await host.SendRawAsync(Packet(joiner.Id, Protocol.W164RealHit, (verdict with { Status = MeasuredMissileStatus.Arranged, Health = 0, Stamina = 0 }).Encode(), 99));
        down = Decode(await joiner.ReadUntilAsync(Protocol.W164Down, Wait)); Assert.Equal(0u, down.Join); Assert.Equal(99u, down.Message.Tok);
        Assert.True(MeasuredMissileResult.TryDecode(down.Message.Text, out var refused)); Assert.Equal(MeasuredMissileStatus.Arranged, refused.Status);
    }
}
