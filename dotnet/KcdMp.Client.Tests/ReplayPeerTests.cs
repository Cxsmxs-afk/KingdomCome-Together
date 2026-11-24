// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public sealed class ReplayPeerTests
{
    [Fact]
    public async Task ReplayCapturesBothReplaceableSamplesAndTheReliableCommitFromTheActualSender()
    {
        await using var relay = await ReplayRelay.StartAsync(keepRecordedStates: true);
        await using var source = await ReplayPeer.ConnectAsync(relay.Port, "replay-source");
        await using var receiver = await ReplayPeer.ConnectAsync(relay.Port, "replay-receiver");
        Assert.NotEqual(source.Id, receiver.Id);
        Assert.Equal(new byte[] { 1 }, await source.ReadUntilAsync(Protocol.CombatRole));
        Assert.Equal(new byte[] { 0 }, await receiver.ReadUntilAsync(Protocol.CombatRole));

        var outbox = new ActionOutbox();
        byte[] actionTime = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(actionTime, 200);
        byte[] jump = outbox.Build(ActionKind.Jump, ActionPhase.Commit, actionTime);
        var packets = await source.ReplayToAsync(receiver,
        [
            PositionCodec.BuildPosition(8, 9, 10, 0, false, false, null, 101),
            PositionCodec.BuildPosition(8, 9, 11, 0, false, false, null, 102),
            jump
        ]);

        GhostSample[] poses = packets.Where(packet => packet.Type == Protocol.Ghost).Select(packet =>
        {
            Assert.True(PositionCodec.TryDecodeGhost(packet.Payload, out var sample));
            Assert.Equal(source.Id, sample.GhostId);
            return sample;
        }).ToArray();
        Assert.Equal(new uint[] { 101, 102 }, poses.Select(pose => pose.SenderMs));
        Assert.Equal(new float[] { 10, 11 }, poses.Select(pose => pose.Z));
        var forwarded = Assert.Single(packets.Where(packet => packet.Type == Protocol.ActionDown));
        var inbound = new ActionInbox().Accept(forwarded.Payload, out _);
        Assert.NotNull(inbound);
        Assert.Equal(source.Id, inbound.Value.SourceGhostId);
        Assert.Equal(ActionKind.Jump, inbound.Value.Kind);
        Assert.Equal(actionTime, inbound.Value.Payload);
    }
}
