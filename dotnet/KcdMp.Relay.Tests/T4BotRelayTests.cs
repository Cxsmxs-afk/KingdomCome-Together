// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using KcdMp.Client;
using KcdMp.Server.Features.ClientHandling;
using Microsoft.Extensions.DependencyInjection;

namespace KcdMp.Relay.Tests;

public sealed class T4BotRelayTests(RelayFixture relay) : IClassFixture<RelayFixture>
{
    private const uint World = 43;
    private static readonly Guid Item = new("00000001-0000-0000-0000-000000000001");
    private static readonly Guid Bow = new("00000002-0000-0000-0000-000000000002");
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(12);
    private string Port => relay.TcpPort.ToString(CultureInfo.InvariantCulture);
    private ClientHandler Clients => relay.Services.GetRequiredService<ClientHandler>();

    [Fact]
    public async Task Scripted_joiner_refuses_to_play_when_it_would_be_the_relay_authority()
    {
        await WaitForAsync(() => Clients.ReadyClientCount == 0, "Previous relay peers did not disconnect");
        using var fixture = new BotFixture();
        string scenario = fixture.Write("wrong-role.txt", "at 0 stand 0 0 0 0\nend 1\n");
        await using var bot = T4BotTools.Start("AvatarPeer", true, "--test-bot", "--scenario", scenario,
            "--host", "127.0.0.1", "--port", Port, "--duration", "10");
        var result = await bot.WaitAsync();
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("reason=relay_role", result.Output);
        Assert.DoesNotContain("BOT-T4 role=joiner ready=1", result.Output);
    }

    [Fact]
    public async Task Scripted_host_refuses_when_another_peer_already_has_relay_authority()
    {
        await WaitForAsync(() => Clients.ReadyClientCount == 0, "Previous relay peers did not disconnect");
        await using var host = await Peer.ConnectAsync(relay.TcpPort, "t4-already-hosting");
        Assert.Equal(new byte[] { 1 }, await host.ReadUntilAsync(Protocol.CombatRole, Wait));
        using var fixture = new BotFixture();
        string save = fixture.Write("world.whs", BotFixture.Save());
        string plan = fixture.Write("host-plan.txt", "at 0 pos 1 2 3\n");
        await using var bot = T4BotTools.Start("SynthPeer", true, "--test-bot", "--join-host125", save,
            "--bot-plan", plan, "--host", "127.0.0.1", "--port", Port, "--duration", "10");
        var result = await bot.WaitAsync();
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("refused=relay_role", result.Output);
        Assert.Equal(host.Id, Clients.DamageAuthority?.Id);
    }

    [Fact]
    public async Task Joiner_plays_typed_bow_and_stamped_positions_after_transfer_and_resume()
    {
        await WaitForAsync(() => Clients.ReadyClientCount == 0, "Previous relay peers did not disconnect");
        await using var host = await Peer.ConnectAsync(relay.TcpPort, "t4-fixture-host");
        Assert.Equal(new byte[] { 1 }, await host.ReadUntilAsync(Protocol.CombatRole, Wait));
        using var fixture = new BotFixture();
        string scenario = fixture.Write("route.txt", "at 0 stand 10 20 30 0\nat 0.02 ranged charge\nat 0.2 ranged fire\nend 0.6\n");
        await using var bot = T4BotTools.Start("AvatarPeer", true, "--test-bot", "--scenario", scenario,
            "--bow-class", Bow.ToString("D"), "--host", "127.0.0.1", "--port", Port, "--duration", "20");

        var (botId, join, _) = ReadJoin(await host.ReadUntilAsync(Protocol.JoinRequestDown, Wait));
        byte[] save = BotFixture.Save();
        var sender = new WorldSender(save, join, botId, 1, Convert.FromHexString(WhsSave.Verify(save).Md5));
        await host.SendRawAsync(new LootMsg(Protocol.W203Epoch, 0, Wo206Rules.EpochText(World, join, "tick"))
            .BuildUp(Protocol.W203Up, botId));
        await host.SendRawAsync(PositionCodec.BuildPosition(10, 20, 30, 0, false, false, null, 300_000));
        await host.SendRawAsync(sender.BuildOfferPacket());
        foreach (byte[] chunk in sender.TakeSendable()) await host.SendRawAsync(chunk);
        var (readyPeer, readyJoin, readyBody) = ReadJoin(await host.ReadUntilAsync(Protocol.JoinerReadyDown, Wait));
        Assert.Equal((botId, join), (readyPeer, readyJoin));
        Assert.Equal(sender.Offer.WorldSavedSeq, BinaryPrimitives.ReadUInt32LittleEndian(readyBody));
        await host.SendRawAsync(JoinStatusCodec.Build(botId, join, Protocol.JoinStateResumed, Protocol.JoinReasonId("ready"), 0));

        var steps = new List<RangedStepV2>();
        GhostSample? position = null;
        long since = Stopwatch.GetTimestamp();
        while (steps.Count < 2 || position is null)
        {
            TimeSpan left = Wait - Stopwatch.GetElapsedTime(since);
            Assert.True(left > TimeSpan.Zero, "The bot did not send its typed actions and live position");
            var (type, payload) = await host.ReadPacketAsync(left);
            if (type == Protocol.Ghost && PositionCodec.TryDecodeGhost(payload, out var sample)
                && sample.GhostId == botId && !sample.IsStale)
                position = sample;
            if (type != Protocol.W164Down) continue;
            var (source, _, body) = ReadJoin(payload);
            if (source != botId || !LootMsg.TryDecode(body, out var message) || message.Kind != Protocol.W164RangedStep) continue;
            Assert.True(RangedStepV2.TryDecode(message.Text, out var ranged));
            steps.Add(ranged);
        }

        Assert.Equal((10f, 20f, 30f), (position.Value.X, position.Value.Y, position.Value.Z));
        Assert.NotEqual(0u, position.Value.SenderMs);
        Assert.Equal(new byte[] { 1, 3 }, steps.Select(step => step.Phase));
        Assert.True(steps[0].Sequence < steps[1].Sequence);
        Assert.All(steps, step =>
        {
            Assert.Equal(World, step.World);
            Assert.Equal(RangedWeaponKind.Bow, step.Kind);
            Assert.Equal(Bow, step.ItemClass);
        });
        var result = await bot.WaitAsync();
        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("BOT-T4 role=joiner complete=1", result.Output);
        var planned = Regex.Matches(result.Output, @"BOT-T4-STATION role=joiner station=\w+ planned_host_ms=(\d+)")
            .Select(match => uint.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(3, planned.Length);
        Assert.Equal(new uint[] { 0, 20, 200 }, planned.Select(stamp => unchecked(stamp - planned[0])));
    }

    [Fact]
    public async Task Host_sends_the_supplied_world_and_returns_scripted_pickup_results()
    {
        await WaitForAsync(() => Clients.ReadyClientCount == 0, "Previous relay peers did not disconnect");
        using var fixture = new BotFixture();
        byte[] save = BotFixture.Save();
        string savePath = fixture.Write("world.whs", save);
        string plan = fixture.Write("host-plan.txt", $"setup witem {Item:D} 1 2 3\nat 0 pos 1 2 3\nat 0.6 pos 4 5 6\n");
        await using var bot = T4BotTools.Start("SynthPeer", true, "--test-bot", "--join-host125", savePath,
            "--bot-plan", plan, "--host", "127.0.0.1", "--port", Port, "--name", "t4-scripted-host", "--duration", "20");
        await WaitForAsync(() => Clients.DamageAuthority?.Name == "t4-scripted-host", "Bot-Host did not acquire relay authority");
        await using var joiner = await Peer.ConnectAsync(relay.TcpPort, "t4-fixture-joiner");
        Assert.Equal(new byte[] { 0 }, await joiner.ReadUntilAsync(Protocol.CombatRole, Wait));

        const uint joinId = 125;
        await joiner.SendRawAsync(WorldReceiver.BuildRequest(joinId));
        var (hostId, offerId, offerBody) = ReadJoin(await joiner.ReadUntilAsync(Protocol.WorldOfferDown, Wait));
        Assert.Equal(joinId, offerId);
        var offer = WorldOffer.TryDecode(offerBody, out string why);
        Assert.True(offer.HasValue, why);
        using var receiver = new WorldReceiver(fixture.Directory, joinId, hostId, offer.Value);
        for (int i = 0; i < offer.Value.ChunkCount; i++)
        {
            var (sender, chunkJoin, body) = ReadJoin(await joiner.ReadUntilAsync(Protocol.WorldChunkDown, Wait));
            Assert.Equal((hostId, joinId), (sender, chunkJoin));
            var chunk = receiver.Accept(BinaryPrimitives.ReadUInt32LittleEndian(body), body.AsSpan(4), out why);
            Assert.NotEqual(WorldReceiver.ChunkResult.Error, chunk);
            if (chunk is WorldReceiver.ChunkResult.AckDue or WorldReceiver.ChunkResult.Complete)
                await joiner.SendRawAsync(receiver.BuildAck());
        }
        var verified = receiver.Finish();
        Assert.True(verified.Ok, verified.Why);
        Assert.Equal(save, await File.ReadAllBytesAsync(receiver.FinalPath));
        await joiner.SendRawAsync(receiver.BuildDone());
        await joiner.SendRawAsync(WorldReceiver.BuildReady(joinId, offer.Value.WorldSavedSeq));
        await WaitForReadyAsync(joiner, hostId, joinId);

        var ghosts = new List<GhostSample>();
        long since = Stopwatch.GetTimestamp();
        while (ghosts.Count < 2)
        {
            TimeSpan left = Wait - Stopwatch.GetElapsedTime(since);
            Assert.True(left > TimeSpan.Zero, "Timed host positions did not arrive");
            if (!PositionCodec.TryDecodeGhost(await joiner.ReadUntilAsync(Protocol.Ghost, left), out var ghost)
                || ghost.GhostId != hostId || ghost.IsStale || ghost.X is not (1f or 4f)
                || (ghosts.Count != 0 && ghosts[^1].X == ghost.X)) continue;
            ghosts.Add(ghost);
        }
        Assert.Equal(new[] { (1f, 2f, 3f), (4f, 5f, 6f) }, ghosts.Select(g => (g.X, g.Y, g.Z)));
        Assert.All(ghosts, ghost => Assert.NotEqual(0u, ghost.SenderMs));


        foreach (var (item, expected, token) in new[] { (Item, "ok", 1u), (Item, "ok", 1u), (Item, "gone", 2u), (Bow, "unknown", 3u) })
        {
            await joiner.SendRawAsync(new LootMsg(Protocol.LootAskItemTake, token, $"{item:D} 1 2 3 0")
                .BuildUp(Protocol.LootAskUp, Protocol.JoinTargetHost));
            while (true)
            {
                var (source, _, body) = ReadJoin(await joiner.ReadUntilAsync(Protocol.LootHostDown, Wait));
                Assert.Equal(hostId, source);
                Assert.True(LootMsg.TryDecode(body, out var result));
                if (result.Kind != Protocol.LootHostItemResult) continue;
                Assert.Equal(token, result.Tok);
                Assert.StartsWith(expected + " ", result.Text);
                break;
            }
        }
    }

    private static async Task WaitForReadyAsync(Peer peer, byte host, uint join)
    {
        long since = Stopwatch.GetTimestamp();
        while (true)
        {
            TimeSpan left = Wait - Stopwatch.GetElapsedTime(since);
            Assert.True(left > TimeSpan.Zero, "The bot did not resume its scripted host world");
            var (source, id, body) = ReadJoin(await peer.ReadUntilAsync(Protocol.JoinStatusDown, left));
            if (source != host || id != join || !JoinStatusCodec.TryDecode(body, out byte state, out byte reason, out _)) continue;
            Assert.NotEqual(Protocol.JoinStateRefused, state);
            if (state != Protocol.JoinStateResumed) continue;
            Assert.Equal(Protocol.JoinReasonId("ready"), reason);
            return;
        }
    }

    private static (byte Source, uint Join, byte[] Body) ReadJoin(byte[] payload)
    {
        Assert.True(Protocol.TrySplitJoinDown(payload, out byte source, out _, out uint join, out var body));
        return (source, join, body.ToArray());
    }

    private static async Task WaitForAsync(Func<bool> condition, string reason)
    {
        long since = Stopwatch.GetTimestamp();
        while (!condition())
        {
            Assert.True(Stopwatch.GetElapsedTime(since) < Wait, reason);
            await Task.Delay(15);
        }
    }

    private sealed class BotFixture : IDisposable
    {
        public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("kcdmp-t4-").FullName;

        public string Write(string name, string text) => Write(name, System.Text.Encoding.UTF8.GetBytes(text));

        public string Write(string name, byte[] bytes)
        {
            string path = Path.Combine(Directory, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public static byte[] Save()
        {
            // Only a synthetic container, with the identity fields the shipped verifier reads.
            byte[] name = [.. "player_henry\0"u8, 0x77, 0x77, 0, 0, 0, 0, 0, 0];
            var henry = Tlv(0x115E, [.. Guid.Parse(WhsSave.HenrySoul).ToByteArray(), .. new byte[16], .. Tlv(0x12F9, name)]);
            var souls = Tlv(0x01F8, Tlv(0x7308, Tlv(0x3529, Tlv(0x1161, henry))));
            var body = Tlv(0x01F4, [.. Tlv(0x01FB, [0xD1, 0xB0, 0, 0]), .. souls]);
            var file = WhsSave.Deflate("<SaveGame LevelName=\"synthetic\"/>"u8.ToArray(), [23, 0, 0, 0, .. body], new byte[44]);
            Assert.True(WhsSave.Verify(file).Ok);
            Assert.True(WhsSave.PlayerOf(WhsSave.Inflate(file).Raw).IsHenry);
            return file;
        }

        private static byte[] Tlv(ushort tag, byte[] payload)
        {
            var bytes = new byte[6 + payload.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, tag);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2), (uint)payload.Length);
            payload.CopyTo(bytes, 6);
            return bytes;
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
