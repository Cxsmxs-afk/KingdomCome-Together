// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using KcdMp.Client;

namespace KcdMp.Relay.Tests;

public class QuestItemMirrorRelayTests : IClassFixture<RelayFixture>
{
    private readonly RelayFixture _relay;
    public QuestItemMirrorRelayTests(RelayFixture relay) => _relay = relay;

    private static (byte Source, LootMsg Message) Decode(byte[] down)
    {
        Assert.True(Protocol.TrySplitJoinDown(down, out byte source, out _, out _, out var payload));
        Assert.True(LootMsg.TryDecode(payload, out var message));
        return (source, message);
    }

    [Fact]
    public async Task Host_pickup_and_hand_in_counts_cross_the_real_relay_in_order()
    {
        await using var host = await Peer.ConnectAsync(_relay.TcpPort, "quest-item-host");
        await using var joiner = await Peer.ConnectAsync(_relay.TcpPort, "quest-item-joiner");
        await Task.Delay(50);
        await host.SendRawAsync(PositionCodec.BuildPosition(1, 2, 3, 0, false, false, null, hostClaim: true));
        await joiner.ReadUntilAsync(Protocol.Ghost, TimeSpan.FromSeconds(5));
        var cls = Guid.Parse("11111111-1111-1111-1111-111111111111");
        foreach (int expected in new[] { 1, 0 })
        {
            await host.SendRawAsync(new LootMsg(Protocol.QuestHostItemCount, 0, Wo200Rules.ItemCountText(expected, cls))
                .BuildUp(Protocol.QuestHostUp, joiner.Id));
            var down = await joiner.ReadUntilAsync(Protocol.QuestHostDown, TimeSpan.FromSeconds(5));
            var (source, message) = Decode(down);
            Assert.Equal(host.Id, source);
            Assert.Equal(Protocol.QuestHostItemCount, message.Kind);
            Assert.True(Wo200Rules.TryParseItemCountText(message.Text, out int count, out Guid item));
            Assert.Equal(expected, count);
            Assert.Equal(cls, item);
        }
        // A copy's fabricated count cannot become the host's quest-item state.
        await joiner.SendRawAsync(new LootMsg(Protocol.QuestHostItemCount, 0, Wo200Rules.ItemCountText(9, cls))
            .BuildUp(Protocol.QuestHostUp, host.Id));
        Assert.True(await host.NoneOfAsync(Protocol.QuestHostDown, TimeSpan.FromMilliseconds(200)));
    }
}
