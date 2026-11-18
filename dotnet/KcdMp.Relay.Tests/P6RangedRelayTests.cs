// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using KcdMp.Server.Features.ClientHandling;
using KcdMp.Wire;
using Microsoft.Extensions.DependencyInjection;

namespace KcdMp.Relay.Tests;

// Real Program.CreateApp/TCP handshake and shipped codecs, not a routing-rule mirror.
public sealed class P6RangedRelayTests(RelayFixture relay) : IClassFixture<RelayFixture>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5), Quiet = TimeSpan.FromMilliseconds(250);
    private static readonly Guid Bow = Guid.Parse("00000009-0000-0000-0000-000000000000");

    // A quiet assertion may time out halfway through unrelated relay chatter.
    // Keep one physical packet reader alive; deadlines apply only to its inbox.
    private sealed class InboxPeer : IAsyncDisposable
    {
        private readonly Peer _peer;
        private readonly Channel<(byte Type, byte[] Payload)> _packets = Channel.CreateUnbounded<(byte, byte[])>(new() { SingleReader = true, SingleWriter = true });
        private readonly Task _reader;
        private int _disposed;
        public byte Id => _peer.Id;
        public InboxPeer(Peer peer) { _peer = peer; _reader = ReadPacketsAsync(); }
        private async Task ReadPacketsAsync()
        {
            Exception? error = null;
            try
            {
                while (Volatile.Read(ref _disposed) == 0)
                    _packets.Writer.TryWrite(await _peer.ReadPacketAsync(Timeout.InfiniteTimeSpan));
            }
            catch (Exception ex) { if (Volatile.Read(ref _disposed) == 0) error = ex; }
            finally { _packets.Writer.TryComplete(error); }
        }
        public Task SendRawAsync(byte[] packet) => _peer.SendRawAsync(packet);
        public async Task<byte[]> ReadUntilAsync(byte wanted, TimeSpan timeout)
        {
            using var stop = new CancellationTokenSource(timeout);
            try
            {
                while (true)
                {
                    var packet = await _packets.Reader.ReadAsync(stop.Token);
                    if (packet.Type == wanted) return packet.Payload;
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            { throw new TimeoutException($"peer {Id}: no 0x{wanted:X2} within {timeout}"); }
        }
        public async Task<bool> NoneOfAsync(byte type, TimeSpan window)
        {
            try { await ReadUntilAsync(type, window); return false; }
            catch (TimeoutException) { return true; }
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await _peer.DisposeAsync(); await _reader.WaitAsync(Wait);
        }
    }
    private static async Task UntilAsync(Func<bool> ready, string reason)
    {
        long start = Stopwatch.GetTimestamp();
        while (!ready())
        {
            Assert.True(Stopwatch.GetElapsedTime(start) < Wait, reason);
            await Task.Delay(5);
        }
    }
    private async Task<(InboxPeer Host, InboxPeer Joiner, InboxPeer Third)> Three()
    {
        var clients = relay.Services.GetRequiredService<ClientHandler>();
        // The class fixture survives all three tests, but closing a client
        // socket does not synchronously release its server-side ID/authority.
        await UntilAsync(() => clients.ReadyClientCount == 0, "previous relay peers did not finish disconnecting");
        InboxPeer? host = null, joiner = null, third = null;
        try
        {
            host = new(await Peer.ConnectAsync(relay.TcpPort, "p6-host"));
            joiner = new(await Peer.ConnectAsync(relay.TcpPort, "p6-joiner"));
            third = new(await Peer.ConnectAsync(relay.TcpPort, "p6-third"));
            // Ack is deliberately queued before Name/IsReady. Observe the real
            // completed welcome and authority rather than sleeping after Ack.
            await UntilAsync(() => clients.ReadyClientCount == 3 && clients.GetClients().Count(c => c.IsReady &&
                ((c.Id == host.Id && c.Name == "p6-host") || (c.Id == joiner.Id && c.Name == "p6-joiner") || (c.Id == third.Id && c.Name == "p6-third"))) == 3 &&
                clients.DamageAuthority?.Id == host.Id, "relay peers were not ready with the expected host authority");
            Assert.Equal(new byte[] { 1 }, await host.ReadUntilAsync(Protocol.CombatRole, Wait));
            Assert.Equal(new byte[] { 0 }, await joiner.ReadUntilAsync(Protocol.CombatRole, Wait));
            Assert.Equal(new byte[] { 0 }, await third.ReadUntilAsync(Protocol.CombatRole, Wait));
            return (host, joiner, third);
        }
        catch
        {
            if (third is not null) await third.DisposeAsync();
            if (joiner is not null) await joiner.DisposeAsync();
            if (host is not null) await host.DisposeAsync();
            throw;
        }
    }
    private static byte[] Packet(byte target, byte kind, string text, uint token = 10) => new LootMsg(kind, token, text).BuildUp(Protocol.W164Up, target);
    private static (byte Source, uint Join, LootMsg Message) Decode(byte[] payload)
    {
        Assert.True(Protocol.TrySplitJoinDown(payload, out byte source, out _, out uint join, out var body));
        Assert.True(LootMsg.TryDecode(body, out var message)); return (source, join, message);
    }
    private static MeasuredMissileRequest Missile() => new(new(7, 10, 20, 30), "npc_bandit_01", 5.5f, 1.25f);

    [Theory]
    [InlineData(1)] // incomplete header
    [InlineData(4)] // complete header, incomplete body
    public async Task Quiet_expiry_inside_partial_TCP_header_or_body_preserves_the_next_ranged_packet(int split)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var connect = Peer.ConnectAsync(((IPEndPoint)listener.LocalEndpoint).Port, "split-reader");
        using var server = await listener.AcceptTcpClientAsync().WaitAsync(Wait); var wire = server.GetStream();
        byte[] header = new byte[3]; await wire.ReadExactlyAsync(header); Assert.Equal(Protocol.Handshake, header[0]);
        byte[] handshake = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1))]; await wire.ReadExactlyAsync(handshake);
        await wire.WriteAsync(new byte[] { Protocol.Ack, 1, 0, 1 });
        await using var peer = new InboxPeer(await connect);
        byte[] chatter = [Protocol.Name, 5, 0, 2, 116, 101, 115, 116];
        await wire.WriteAsync(chatter.AsMemory(0, split));
        // This deadline expires with a real socket frame incomplete. Its later
        // bytes must still belong to that frame, even after a negative assertion.
        Assert.True(await peer.NoneOfAsync(Protocol.W164Down, Quiet));
        var step = new RangedStepV2(7, 11, 22, 1, 3, RangedWeaponKind.Bow, Bow, 0, 1);
        byte[] up = Packet(0, Protocol.W164RangedStep, step.Encode());
        byte[] payload = [0, .. up[3..]];
        byte[] down = [Protocol.W164Down, (byte)payload.Length, (byte)(payload.Length >> 8), .. payload];
        await wire.WriteAsync(chatter.AsMemory(split)); await wire.WriteAsync(down);
        Assert.Equal(payload, await peer.ReadUntilAsync(Protocol.W164Down, Wait));
    }

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
