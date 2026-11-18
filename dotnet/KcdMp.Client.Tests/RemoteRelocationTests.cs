// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public class RemoteRelocationTests
{
    private const byte Host = 5, Joiner = 7;
    private static readonly RemoteRelocationScope HostFigure = new(18, 33, 21, 53, 4, 0xFF123456, 8, false, Host);
    private static readonly RemoteRelocationScope JoinerFigure = HostFigure with { IsHost = true, Host = null, Entity = 0x1020 };
    private static readonly RemoteRelocationPoint Site = new(2500, 1100.5f, 88);

    [Fact]
    public void Join_needs_a_fresh_relay_peer_and_the_actual_new_local_body_once()
    {
        var proof = new RemoteRelocationProofs();
        proof.BodySpawned(Joiner, JoinerFigure, 1000);
        Assert.Null(proof.Take(Joiner, JoinerFigure, 1020, 1010, 1025, Site));
        proof.PeerNamed(Joiner);
        Assert.Null(proof.Take(Joiner, JoinerFigure, 1050, 1040, 1060, Site));
        proof.BodySpawned(Joiner, JoinerFigure, 1080);
        Assert.Null(proof.Take(Joiner, JoinerFigure, 1100, 1079, 1101, Site)); // a late packet captured for the prior scene
        var approval = Assert.IsType<RemoteRelocationApproval>(proof.Take(Joiner, JoinerFigure, 1110, 1090, 1120, Site));
        Assert.Equal(RemoteRelocationReason.Join, approval.Reason);
        Assert.Equal(Site, approval.Point);
        Assert.True(proof.IsCurrent(approval));
        Assert.Null(proof.Take(Joiner, JoinerFigure, 1140, 1120, 1142, Site));

        proof.PeerNamed(Joiner); // the same relay name is repeated
        Assert.True(proof.IsCurrent(approval));
        proof.BodySpawned(Joiner, JoinerFigure with { Entity = 0x1021 }, 1200);
        Assert.False(proof.IsCurrent(approval));
        Assert.Null(proof.Take(Joiner, JoinerFigure with { Entity = 0x1021 }, 1220, 1210, 1221, Site));
        proof.PeerLeft(Joiner);
        proof.PeerNamed(Joiner); // relay ID can be reused
        Assert.False(proof.IsCurrent(approval));
        proof.BodySpawned(Joiner, JoinerFigure with { Entity = 0x1022 }, 1320);
        Assert.Equal(RemoteRelocationReason.Join,
            proof.Take(Joiner, JoinerFigure with { Entity = 0x1022 }, 1340, 1330, 1342, Site)!.Value.Reason);
    }

    [Theory]
    [InlineData(Protocol.RespawnReasonDeath, true)]
    [InlineData(Protocol.RespawnReasonExecution, true)]
    [InlineData(Protocol.RespawnReasonKnockdown, false)]
    [InlineData(123, false)]
    public void Respawn_uses_the_relay_sender_and_a_fresh_pose_close_to_the_wake_point(byte reason, bool approved)
    {
        var proof = new RemoteRelocationProofs();
        proof.Respawn(Joiner, JoinerFigure, 1000, reason, Site);
        proof.PeerNamed(Joiner);
        proof.PeerNamed(Host);
        Assert.False(proof.HasPending(Joiner));
        proof.Respawn(Joiner, JoinerFigure, 1000, reason, Site with { Y = float.PositiveInfinity });
        Assert.False(proof.HasPending(Joiner));
        proof.Respawn(Joiner, JoinerFigure, 1000, reason, Site);
        Assert.Null(proof.Take(Host, JoinerFigure, 1030, 1010, 1035, Site));
        Assert.Null(proof.Take(Joiner, JoinerFigure, 1030, 1009, 1035, Site with { X = Site.X + 5 }));
        var decision = proof.Take(Joiner, JoinerFigure, 1050, 1030, 1060, Site with { Z = Site.Z + 0.5f });
        Assert.Equal(approved, decision.HasValue);
        if (decision is not { } approval) return;
        Assert.Equal(RemoteRelocationReason.Respawn, approval.Reason);
        Assert.Equal(Site, approval.Point);
        byte[] body = approval.Build();
        Assert.Equal(18, body.Length);
        Assert.Equal(Joiner, body[0]);
        Assert.Equal(JoinerFigure.Entity, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(1)));
        Assert.Equal((byte)2, body[5]);
        Assert.Equal((Site.X, Site.Y, Site.Z), (BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(6)),
            BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(10)), BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(14))));
        Assert.Null(proof.Take(Joiner, JoinerFigure, 1065, 1040, 1070, Site));
    }

    [Fact]
    public void Changed_body_load_world_pipe_ownership_or_event_deadline_retires_a_permission()
    {
        foreach (var changed in new[] { JoinerFigure with { BodyGeneration = 19 }, JoinerFigure with { Entity = 0x5678 },
            JoinerFigure with { LoadSerial = 200 }, JoinerFigure with { LoadGeneration = 24 }, JoinerFigure with { Pipe = 900 },
            JoinerFigure with { World = 55 }, JoinerFigure with { Join = 98 }, JoinerFigure with { IsHost = false, Host = 8 } })
        {
            var proof = new RemoteRelocationProofs();
            proof.PeerNamed(Joiner);
            proof.Respawn(Joiner, JoinerFigure, 1000, Protocol.RespawnReasonDeath, Site);
            Assert.Null(proof.Take(Joiner, changed, 1100, 1080, 1200, Site));
            Assert.False(proof.HasPending(Joiner));
        }
        var late = new RemoteRelocationProofs();
        late.PeerNamed(Joiner);
        late.Respawn(Joiner, JoinerFigure, 1000, Protocol.RespawnReasonDeath, Site);
        Assert.Null(late.Take(Joiner, JoinerFigure, 3020, 3010, 3025, Site));
        Assert.False(late.HasPending(Joiner));
    }

    [Fact]
    public void Only_the_current_hosts_paired_non_replayed_WO167_engine_edges_allow_travel()
    {
        var proof = new RemoteRelocationProofs();
        proof.PeerNamed(Host);
        proof.PeerNamed(Joiner);
        string End(uint seq) => W164Text.TravelEnd(seq, Site.X, Site.Y, Site.Z, interrupted: true);

        proof.HostTravel(Host, HostFigure, 5000, End(11)); // completion alone
        proof.HostTravel(Joiner, HostFigure, 5010, "s 11 @Road");
        proof.HostTravel(Joiner, HostFigure, 5030, End(11)); // wrong relay source
        proof.HostTravel(Host, HostFigure with { IsHost = true }, 5040, "s 11 @Road");
        proof.HostTravel(Host, HostFigure with { IsHost = true }, 5050, End(11));
        Assert.False(proof.HasPending(Host));
        Assert.False(proof.HasPending(Joiner));

        proof.HostTravel(Host, HostFigure, 5100, "s 11 @Road");
        proof.HostTravel(Host, HostFigure, 5120, End(12));
        Assert.False(proof.HasPending(Host));
        proof.HostTravel(Host, HostFigure, 5200, End(11));
        var travel = proof.Take(Host, HostFigure, 5250, 5220, 5260, Site);
        Assert.Equal(RemoteRelocationReason.Travel, travel!.Value.Reason);

        proof.HostTravel(Host, HostFigure, 5300, "s 11 @Road"); // both legs repeated
        proof.HostTravel(Host, HostFigure, 5320, End(11));
        proof.HostTravel(Host, HostFigure, 5400, "s 12 @Road");
        proof.HostTravel(Host, HostFigure with { World = 19 }, 5500, End(12));
        proof.HostTravel(Host, HostFigure, 5550, "s 13 @Road");
        proof.HostTravel(Host, HostFigure, 5550 + Wo167Rules.MirrorMaxMs + 1, End(13));
        Assert.False(proof.HasPending(Host));
    }
}
