// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;
using KcdMp.Server.Features.ClientHandling;
using KcdMp.Wire;
using Microsoft.Extensions.DependencyInjection;

namespace KcdMp.Relay.Tests;

/// <summary>
/// The host's machine is the world. When that client leaves, no remaining joiner becomes damage or world authority,
/// and a joiner's world save, NPC hit, or dialog vote is not forwarded.
/// </summary>
public sealed class HostAbsentRelayTests : IClassFixture<RelayFixture>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private readonly RelayFixture _relay;
    private ClientHandler Clients => _relay.Services.GetRequiredService<ClientHandler>();

    public HostAbsentRelayTests(RelayFixture relay) => _relay = relay;

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Host_leave_does_not_elect_a_joiner(int players)
    {
        await WaitNoClientsAsync();
        var peers = new Peer[players];
        for (int i = 0; i < players; i++)
            peers[i] = await Peer.ConnectAsync(_relay.TcpPort, i == 0 ? "the-host" : $"joiner-{i}");
        await using var _ = new PeerGroup(peers);
        await peers[0].SendRawAsync(Claim());
        await WaitUntilAsync(() => Clients.DamageAuthority?.Name == "the-host", "the host did not become authority");

        await peers[0].DisposeAsync();
        await WaitUntilAsync(() => Clients.ReadyClientCount == players - 1, "the host's connection did not leave");

        Assert.Null(Clients.DamageAuthority);
        for (int i = 1; i < players; i++)
            Assert.False(Clients.IsDamageAuthority(Clients.GetClients().Single(c => c.Id == peers[i].Id)));
    }

    [Fact]
    public async Task Joiners_cannot_save_hit_or_vote_while_the_host_is_gone()
    {
        await WaitNoClientsAsync();
        await using var host = await Peer.ConnectAsync(_relay.TcpPort, "the-host");
        await using var a = await Peer.ConnectAsync(_relay.TcpPort, "joiner-a");
        await using var b = await Peer.ConnectAsync(_relay.TcpPort, "joiner-b");
        await using var c = await Peer.ConnectAsync(_relay.TcpPort, "joiner-c");
        await host.SendRawAsync(Claim());
        await WaitUntilAsync(() => Clients.DamageAuthority?.Id == host.Id, "the host did not become authority");
        long saves = Clients.DropCount($"0x{Protocol.WorldSavedUp:X2}:host-absent");
        long hits = Clients.DropCount($"0x{Protocol.PlayerHitUp:X2}:host-absent");
        long votes = Clients.DropCount($"0x{Protocol.ConvUp:X2}/k{Protocol.ConvVote}:host-absent");

        await host.DisposeAsync();
        await WaitUntilAsync(() => Clients.DamageAuthority is null && Clients.ReadyClientCount == 3, "the session still has an authority");

        await a.SendRawAsync(WorldSaved());
        await b.SendRawAsync(Hit(c.Id));
        await c.SendRawAsync(new LootMsg(Protocol.ConvVote, 0, "v1|7|1|0").BuildUp(Protocol.ConvUp, Protocol.JoinTargetHost));
        await WaitUntilAsync(() => Clients.DropCount($"0x{Protocol.WorldSavedUp:X2}:host-absent") == saves + 1
            && Clients.DropCount($"0x{Protocol.PlayerHitUp:X2}:host-absent") == hits + 1
            && Clients.DropCount($"0x{Protocol.ConvUp:X2}/k{Protocol.ConvVote}:host-absent") == votes + 1,
            "a joiner save, hit, or vote was accepted");
        Assert.False(await SeesAsync(b, Protocol.WorldSavedDown));
        Assert.False(await SeesAsync(c, Protocol.PlayerHitDown));
        Assert.False(await SeesAsync(a, Protocol.ConvDown));
    }

    [Theory]
    [InlineData(2, "join")]
    [InlineData(2, "combat")]
    [InlineData(2, "dialog")]
    [InlineData(3, "join")]
    [InlineData(3, "combat")]
    [InlineData(3, "dialog")]
    [InlineData(4, "join")]
    [InlineData(4, "combat")]
    [InlineData(4, "dialog")]
    public async Task The_relay_pauses_a_running_session_when_the_host_leaves(int players, string activity)
    {
        await WaitNoClientsAsync();
        var peers = new Peer[players];
        for (int index = 0; index < peers.Length; index++)
            peers[index] = await Peer.ConnectAsync(_relay.TcpPort, index == 0 ? "the-host" : $"joiner-{index}");
        await using var group = new PeerGroup(peers);
        Peer host = peers[0], joiner = peers[1], witness = peers[^1];
        await host.SendRawAsync(Claim());
        await WaitUntilAsync(() => Clients.DamageAuthority?.Id == host.Id, "the host did not acquire authority");

        byte[] request = new byte[Protocol.JoinHeaderLen + 1];
        request[0] = Protocol.JoinTargetHost;
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), 27);
        byte[] join = Frame(Protocol.JoinRequestUp, request);
        var vote = new LootMsg(Protocol.ConvVote, 27, "v1|7|1|0");
        byte[] dialog = vote.BuildUp(Protocol.ConvUp, Protocol.JoinTargetHost);

        byte upstream, downstream;
        byte[] update;
        switch (activity)
        {
            case "join":
                await joiner.SendRawAsync(join);
                await host.ReadUntilAsync(Protocol.JoinRequestDown, Wait);
                (upstream, downstream, update) = (Protocol.JoinerReadyUp, Protocol.JoinerReadyDown, Frame(Protocol.JoinerReadyUp, new byte[Protocol.JoinHeaderLen + 4]));
                break;
            case "combat":
                await host.SendRawAsync(Hit(joiner.Id));
                await joiner.ReadUntilAsync(Protocol.PlayerHitDown, Wait);
                var hit = new byte[PlayerHitV8.UpLen];
                hit[0] = witness.Id;
                (upstream, downstream, update) = (Protocol.PlayerHitV8Up, Protocol.PlayerHitV8Down, Frame(Protocol.PlayerHitV8Up, hit));
                break;
            default:
                await joiner.SendRawAsync(dialog);
                await host.ReadUntilAsync(Protocol.ConvDown, Wait);
                (upstream, downstream, update) = (Protocol.ConvUp, Protocol.ConvDown, vote.BuildUp(Protocol.ConvUp, witness.Id));
                break;
        }

        string kind = activity == "dialog" ? $"/k{Protocol.ConvVote}" : "";
        string drop = $"0x{upstream:X2}{kind}:host-absent";
        long before = Clients.DropCount(drop);
        await host.DisposeAsync();
        Assert.Equal(host.Id, (await joiner.ReadUntilAsync(Protocol.Disconnect, Wait))[0]);
        Assert.Null(Clients.DamageAuthority);

        await joiner.SendRawAsync(update);
        await joiner.SendRawAsync(Frame(Protocol.Ping, new byte[Protocol.PingPayloadLen]));
        await joiner.ReadUntilAsync(Protocol.Pong, Wait);
        Assert.Equal(before + 1, Clients.DropCount(drop));
        Assert.True(await witness.NoneOfAsync(downstream, TimeSpan.FromMilliseconds(100)));
        Assert.False(Clients.HasHostConnected());
    }

    [Fact]
    public async Task The_same_host_resumes_only_the_same_world_epoch_and_run()
    {
        await WaitNoClientsAsync();
        await using var host = await Peer.ConnectAsync(_relay.TcpPort, "the-host");
        await using var joiner = await Peer.ConnectAsync(_relay.TcpPort, "joiner-a");
        await host.SendRawAsync(Claim());
        await WaitUntilAsync(() => Clients.DamageAuthority?.Id == host.Id, "the host did not become authority");
        Guid run = Guid.ParseExact("452560e1066a4794a05071e8c539ecf4", "N");
        await host.SendRawAsync(Epoch(142, run, 81, joiner.Id));
        await WaitUntilAsync(() => Clients.GetClients().Any(c => c.Name == "the-host" && c.AnnouncedWorldEpoch == 142 && c.AnnouncedRunId == run),
            "the host epoch was not recorded");
        await joiner.ReadUntilAsync(Protocol.W203Down, Wait);

        await host.DisposeAsync();
        await WaitUntilAsync(() => Clients.AbsentHost is { WorldEpoch: 142, RunId: var id } && id == run && Clients.DamageAuthority is null,
            "a joiner was elected");

        await using var other = await Peer.ConnectAsync(_relay.TcpPort, "someone-else");
        await other.SendRawAsync(Claim());
        await other.SendRawAsync(Epoch(142, run, 81, joiner.Id));
        await Task.Delay(200);
        Assert.Null(Clients.DamageAuthority);

        await using var back = await Peer.ConnectAsync(_relay.TcpPort, "the-host");
        await back.SendRawAsync(Claim());
        await back.SendRawAsync(Epoch(99, run, 81, joiner.Id));
        Assert.True(await joiner.NoneOfAsync(Protocol.W203Down, TimeSpan.FromMilliseconds(400)),
            "a returning host with the wrong world epoch changed a paused joiner's scope");
        Assert.Null(Clients.DamageAuthority);

        await back.SendRawAsync(Epoch(142, Guid.NewGuid(), 81, joiner.Id));
        Assert.True(await joiner.NoneOfAsync(Protocol.W203Down, TimeSpan.FromMilliseconds(400)),
            "a returning host with the wrong run was forwarded to the paused joiner");
        Assert.Null(Clients.DamageAuthority);

        await back.SendRawAsync(new LootMsg(Protocol.W203Epoch, 0, "v1|142|2|resume").BuildUp(Protocol.W203Up, joiner.Id));
        Assert.True(await joiner.NoneOfAsync(Protocol.W203Down, TimeSpan.FromMilliseconds(400)),
            "a returning host without run proof was forwarded to the paused joiner");
        Assert.Null(Clients.DamageAuthority);

        await back.SendRawAsync(Epoch(142, run, 81, joiner.Id));
        await WaitUntilAsync(() => Clients.DamageAuthority?.Id == back.Id, "the same host did not resume");
        Assert.Null(Clients.AbsentHost);
        byte[] accepted = await joiner.ReadUntilAsync(Protocol.W203Down, Wait);
        Assert.Equal(back.Id, accepted[0]);
        Assert.True(HostAbsentRules.TryReadRunFromDown(accepted, out Guid resumedRun));
        Assert.Equal(run, resumedRun);
    }

    [Fact]
    public async Task A_return_proof_before_the_role_claim_is_held_until_both_agree()
    {
        await WaitNoClientsAsync();
        await using var host = await Peer.ConnectAsync(_relay.TcpPort, "the-host");
        await using var guest = await Peer.ConnectAsync(_relay.TcpPort, "joiner");
        Guid run = Guid.ParseExact("852560e1066a4794a05071e8c539ecf5", "N");
        await host.SendRawAsync(Claim());
        await host.SendRawAsync(Epoch(142, run, 81, guest.Id));
        await guest.ReadUntilAsync(Protocol.W203Down, Wait);
        await host.DisposeAsync();
        Assert.Equal(host.Id, (await guest.ReadUntilAsync(Protocol.Disconnect, Wait))[0]);

        await using var back = await Peer.ConnectAsync(_relay.TcpPort, "the-host");
        await back.SendRawAsync(Epoch(142, run, 81, guest.Id));
        await back.SendRawAsync(Frame(Protocol.Ping, new byte[Protocol.PingPayloadLen]));
        await back.ReadUntilAsync(Protocol.Pong, Wait);
        Assert.Null(Clients.DamageAuthority);
        Assert.True(await guest.NoneOfAsync(Protocol.W203Down, TimeSpan.FromMilliseconds(100)));

        // A newer, incompatible declaration must invalidate a proof waiting for a claim.
        await back.SendRawAsync(Epoch(143, run, 81, guest.Id));
        await back.SendRawAsync(Claim());
        await back.SendRawAsync(Frame(Protocol.Ping, new byte[Protocol.PingPayloadLen]));
        await back.ReadUntilAsync(Protocol.Pong, Wait);
        Assert.Null(Clients.DamageAuthority);
        await back.SendRawAsync(Epoch(142, run, 81, guest.Id));

        byte[] accepted = await guest.ReadUntilAsync(Protocol.W203Down, Wait);
        Assert.Equal(back.Id, accepted[0]);
        Assert.Null(Clients.AbsentHost);
        Assert.Equal(back.Id, Clients.DamageAuthority?.Id);
    }

    [Fact]
    public async Task A_lower_id_claim_cannot_replace_the_host_of_a_confirmed_run()
    {
        await WaitNoClientsAsync();
        await using var guest = await Peer.ConnectAsync(_relay.TcpPort, "joiner");
        await using var host = await Peer.ConnectAsync(_relay.TcpPort, "the-host");
        Guid run = Guid.ParseExact("952560e1066a4794a05071e8c539ecf6", "N");
        await host.SendRawAsync(Claim());
        await host.SendRawAsync(Epoch(142, run, 81, guest.Id));
        await guest.ReadUntilAsync(Protocol.W203Down, Wait);
        Assert.True(guest.Id < host.Id);

        await guest.SendRawAsync(Claim());
        await guest.SendRawAsync(Frame(Protocol.Ping, new byte[Protocol.PingPayloadLen]));
        await guest.ReadUntilAsync(Protocol.Pong, Wait);
        Assert.Equal(host.Id, Clients.DamageAuthority?.Id);

        await host.DisposeAsync();
        Assert.Equal(host.Id, (await guest.ReadUntilAsync(Protocol.Disconnect, Wait))[0]);
        Assert.Null(Clients.DamageAuthority);
        Assert.Equal(run, Clients.AbsentHost?.RunId);
    }

    private async Task WaitNoClientsAsync()
    {
        var until = DateTime.UtcNow + Wait;
        while (Clients.ReadyClientCount > 0 && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.Equal(0, Clients.ReadyClientCount);
    }

    private static async Task WaitUntilAsync(Func<bool> ready, string why)
    {
        var until = DateTime.UtcNow + Wait;
        while (!ready() && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.True(ready(), why);
    }

    private static async Task<bool> SeesAsync(Peer peer, byte type)
    {
        var until = DateTime.UtcNow + TimeSpan.FromMilliseconds(400);
        try
        {
            while (DateTime.UtcNow < until)
            {
                var (got, _) = await peer.ReadPacketAsync(until - DateTime.UtcNow);
                if (got == type) return true;
            }
        }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { }
        catch (EndOfStreamException) { }
        return false;
    }

    private static byte[] Claim()
    {
        var body = new byte[Protocol.PositionPayloadLen];
        body[16] = Protocol.PositionFlagHostClaim;
        return Frame(Protocol.Position, body);
    }

    private static byte[] Hit(byte target)
    {
        var body = new byte[Protocol.PlayerHitUpPayloadLen];
        body[0] = target;
        return Frame(Protocol.PlayerHitUp, body);
    }

    private static byte[] WorldSaved()
    {
        byte[] body = new WorldSaved(7, 1_790_000_000_123L, Protocol.SaveKindAuto, 1, 42,
            Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray()).Encode();
        return Frame(Protocol.WorldSavedUp, body);
    }

    private static byte[] Epoch(uint world, Guid run, ulong generation, byte target) =>
        new LootMsg(Protocol.W203Epoch, 0, FormattableString.Invariant($"v1|{world}|2|resume|r1|{run:N}|{generation}"))
            .BuildUp(Protocol.W203Up, target);

    private static byte[] Frame(byte type, byte[] body)
    {
        var packet = new byte[3 + body.Length];
        packet[0] = type;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(1), (ushort)body.Length);
        body.CopyTo(packet, 3);
        return packet;
    }

    private sealed class PeerGroup : IAsyncDisposable
    {
        private readonly Peer[] _peers;
        public PeerGroup(Peer[] peers) => _peers = peers;
        public async ValueTask DisposeAsync()
        {
            foreach (var peer in _peers)
                try { await peer.DisposeAsync(); } catch { }
        }
    }
}
