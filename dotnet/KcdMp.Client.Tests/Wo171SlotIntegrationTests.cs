// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;
using System.Net;
using System.Reflection;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public class Wo171SlotIntegrationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const byte Host = 7, Ann = 8, Bob = 9;
    private static readonly Guid FixtureRelay = Wo171Tests.Member("fixture-relay");

    private static void Connect(PeerSet peers, byte id, string name)
    {
        Assert.True(peers.AnnounceRosterPeer(id, FixtureRelay, Wo171Tests.Member(name)));
        peers.Connected(id, name);
    }

    private static void Set(GameBridge bridge, string name, object value) =>
        typeof(GameBridge).GetField(name, Private)!.SetValue(bridge, value);

    private static object? Call(GameBridge bridge, string name, params object?[] arguments) =>
        typeof(GameBridge).GetMethod(name, Private)!.Invoke(bridge, arguments);

    private static PeerSet Peers(GameBridge bridge) =>
        (PeerSet)typeof(GameBridge).GetField("_peers", Private)!.GetValue(bridge)!;

    private static int? Slot(GameBridge bridge, byte peer) =>
        (int?)Call(bridge, "Wo171PinSlot", peer);

    private static Task Receive(GameBridge bridge, byte source, byte[] body) =>
        (Task)Call(bridge, "Wo164OnFrameAsync", source, body)!;

    private static async Task<Stream> ReaderStreamAsync(ReplayPeer peer, CancellationToken ct) =>
        new JoinCalendarIntegrationTests.JoinWire(peer.Wire, await peer.DrainAsync(ct), dropFirstSnapshot: false);

    private static GameBridge Bridge(byte self, Stream relay, bool hosting = false)
    {
        var bridge = new GameBridge(new ClientConfig { IsHosting = hosting });
        Set(bridge, "_myGhostId", self);
        Set(bridge, "_wo122Stream", relay);
        Set(bridge, "_combatRoleApplied", true);
        Set(bridge, "_isDamageAuthority", hosting);
        Set(bridge, "_sharedWorld", hosting);
        return bridge;
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Production_tick_publishes_the_same_palette_to_every_relay_partner_even_when_local_markers_are_off(int players)
    {
        using var wire = new MemoryStream();
        var host = Bridge(Host, wire, hosting: true);
        Set(host, "_w164On", false);
        byte[] ids = Enumerable.Range(8, players - 1).Select(x => (byte)x).ToArray();
        foreach (byte id in ids.Reverse()) Connect(Peers(host), id, $"Guest{id}");

        await (Task)Call(host, "Wo164TickAsync")!;

        Console.WriteLine($"WO171-TEST players={players} palette_bytes={wire.Length}");
        Assert.True(wire.Length > 0, "The host tick did not publish any authoritative marker table.");
        string expected = "marker-slots-v2|" + string.Join('|', ids.Select(id => Wo171Tests.Member($"Guest{id}").ToString("N"))
            .Concat(Enumerable.Repeat("", 4 - players)));
        wire.Position = 0;
        byte[] header = new byte[3];
        foreach (byte id in ids)
        {
            wire.ReadExactly(header);
            Assert.Equal(Protocol.W164Up, header[0]);
            byte[] packet = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1))];
            wire.ReadExactly(packet);
            Assert.Equal(id, packet[0]);
            Assert.True(LootMsg.TryDecode(packet.AsSpan(Protocol.JoinHeaderLen), out var message));
            Assert.Equal(Protocol.W164MarkNote, message.Kind);
            Assert.Equal(expected, message.Text);
        }
        Assert.Equal(wire.Length, wire.Position);
    }

    private static void ConfirmHost(GameBridge bridge, byte host = Host)
    {
        Set(bridge, "_hostModeKnown", true);
        Set(bridge, "_hostSharedWorld", true);
        Set(bridge, "_hostModeFrom", host);
    }

    [Fact]
    public void Parked_crime_survives_a_reconnect_but_never_crosses_a_relay_restart()
    {
        using var relay = new MemoryStream();
        var bridge = Bridge(Host, relay, hosting: true);
        Guid member = Wo171Tests.Member("Henry");

        ConnectMember(Ann, FixtureRelay);
        var record = (JoinerCrimeRecord)Call(bridge, "Wo139Record", Ann)!;
        Assert.True(record.Add(new JoinerCrimeRecord.Crime { Kind = "theft", Witnesses = 1 }));
        Call(bridge, "Wo144OnPeerDisconnected", Ann);
        Call(bridge, "Wo144OnRelayLost");
        ConnectMember(Bob, FixtureRelay);
        Assert.Same(record, Call(bridge, "Wo139Record", Bob));

        Call(bridge, "Wo144OnPeerDisconnected", Bob);
        Call(bridge, "Wo144OnRelayLost");
        ConnectMember(Ann, Wo171Tests.Member("restarted-relay"));
        var fresh = (JoinerCrimeRecord)Call(bridge, "Wo139Record", Ann)!;
        Assert.NotSame(record, fresh);
        Assert.Equal(0, fresh.Count);

        void ConnectMember(byte id, Guid roster)
        {
            Call(bridge, "Wo144OnRosterPeer", id, roster, member);
            Call(bridge, "Wo144OnPeerConnected", id, "Henry");
        }
    }

    [Fact]
    public async Task Two_guests_with_the_same_display_name_keep_distinct_colours_across_rejoin()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = stop.Token;
        await using var relay = await ReplayRelay.StartAsync(ct);
        await using var hostPeer = await ReplayPeer.ConnectAsync(relay.Port, "Host", ct);
        await using var first = await ReplayPeer.ConnectAsync(relay.Port, "Henry", ct);
        var otherAddress = IPAddress.Parse("127.0.0.2");
        await using var second = await ReplayPeer.ConnectAsync(relay.Port, "Henry", ct, otherAddress);
        var hostLink = await ReaderStreamAsync(hostPeer, ct);
        var host = Bridge(hostPeer.Id, hostLink, hosting: true);
        Set(host, "_wo122Ct", ct);
        Set(host, "_w164On", false);
        var reader = (Task)Call(host, "ReceiveLoopAsync", hostLink, ct)!;
        try
        {
            await WaitForRosterAsync(first.Id, second.Id);
            await (Task)Call(host, "Wo171SyncSlotsAsync")!;
            Assert.Equal(2, Slot(host, first.Id));
            Assert.Equal(3, Slot(host, second.Id));

            await using var returning = await ReplayPeer.ConnectAsync(relay.Port, "Henry", ct, otherAddress);
            Assert.NotEqual(second.Id, returning.Id);
            await WaitForRosterAsync(first.Id, returning.Id);
            Assert.False(Peers(host).IsLive(second.Id));
            await (Task)Call(host, "Wo171SyncSlotsAsync")!;
            Assert.Equal(2, Slot(host, first.Id));
            Assert.Equal(3, Slot(host, returning.Id));
        }
        finally
        {
            stop.Cancel();
            await reader.WaitAsync(TimeSpan.FromSeconds(5));
        }

        async Task WaitForRosterAsync(byte a, byte b)
        {
            while (Peers(host).NameOf(a) != "Henry" || Peers(host).NameOf(b) != "Henry")
                await Task.Delay(5, ct);
        }
    }

    private static byte[] SnapshotBody(Wo171Rules.SlotTable slots, byte target) =>
        new LootMsg(Protocol.W164MarkNote, 1, slots.ToWire()).BuildUp(Protocol.W164Up, target)[(3 + Protocol.JoinHeaderLen)..];

    private static (byte Target, byte[] Body) ReadSlotPacket(Stream stream)
    {
        byte[] header = new byte[3];
        stream.ReadExactly(header);
        Assert.Equal(Protocol.W164Up, header[0]);
        int length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1));
        byte[] packet = new byte[length];
        stream.ReadExactly(packet);
        byte[] body = packet[Protocol.JoinHeaderLen..];
        Assert.True(LootMsg.TryDecode(body, out var message));
        Assert.Equal(Protocol.W164MarkNote, message.Kind);
        Assert.StartsWith(Wo171Rules.SlotTable.WirePrefix, message.Text);
        return (packet[0], body);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task The_authoritative_palette_crosses_the_real_relay_and_each_guest_reader(int players)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = stop.Token;
        await using var relay = await ReplayRelay.StartAsync(ct);
        await using var hostPeer = await ReplayPeer.ConnectAsync(relay.Port, "Host", ct);
        await using var ann = await ReplayPeer.ConnectAsync(relay.Port, "Ann", ct);
        await using var bob = players >= 3 ? await ReplayPeer.ConnectAsync(relay.Port, "Bob", ct) : null;
        await using var cy = players >= 4 ? await ReplayPeer.ConnectAsync(relay.Port, "Cy", ct) : null;
        var peers = new[] { (Peer: ann, Name: "Ann"), (Peer: bob, Name: "Bob"), (Peer: cy, Name: "Cy") }
            .Where(x => x.Peer is not null).OrderBy(x => x.Peer!.Id).ToArray();
        var hostLink = await ReaderStreamAsync(hostPeer, ct);
        var host = Bridge(hostPeer.Id, hostLink, hosting: true);
        Set(host, "_wo122Ct", ct);
        Set(host, "_w164On", false);
        var guests = new GameBridge[peers.Length];
        var readers = new List<Task> { (Task)Call(host, "ReceiveLoopAsync", hostLink, ct)! };
        for (int i = 0; i != guests.Length; i++)
        {
            var peer = peers[i].Peer!;
            var link = await ReaderStreamAsync(peer, ct);
            var guest = guests[i] = Bridge(peer.Id, link);
            Set(guest, "_wo122Ct", ct);
            readers.Add((Task)Call(guest, "ReceiveLoopAsync", link, ct)!);
        }

        try
        {
            byte[] position = new byte[Protocol.PositionPayloadLen];
            position[16] = Protocol.PositionFlagHostClaim;
            await hostPeer.SendAsync(ReplayPeer.Frame(Protocol.Position, position), ct);
            foreach (var p in peers)
                await hostPeer.SendAsync(JoinStatusCodec.Build(p.Peer!.Id, 0, Protocol.JoinStateSession,
                    Protocol.JoinReasonId("shared-world"), 0), ct);
            while (peers.Any(p => Peers(host).NameOf(p.Peer!.Id) != p.Name) ||
                guests.Any(guest => Slot(guest, hostPeer.Id) != 1)) await Task.Delay(5, ct);
            await (Task)Call(host, "Wo164TickAsync")!;

            for (int i = 0; i != guests.Length; i++)
            {
                var guest = guests[i];
                while (Slot(guest, hostPeer.Id) != 1 || Slot(guest, peers[i].Peer!.Id) != i + 2)
                    await Task.Delay(5, ct);
                for (int j = 0; j != peers.Length; j++)
                    Assert.Equal(j + 2, Slot(guest, peers[j].Peer!.Id));
                Assert.Equal(1, Slot(guest, hostPeer.Id));
            }
            Console.WriteLine($"WO171-TEST relay-readers={players - 1} marker_slot_mismatch=0");
        }
        finally
        {
            stop.Cancel();
            await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_confirmed_host_pause_refuses_changes_until_the_same_world_resumes(bool changedHostId)
    {
        using var relay = new MemoryStream();
        var guest = Bridge(Ann, relay);
        Connect(Peers(guest), Host, "Host");
        Connect(Peers(guest), Bob, "Bob");
        ConfirmHost(guest);
        var world = (Wo206Epochs)typeof(GameBridge).GetField("_w206", Private)!.GetValue(guest)!;
        world.LearnWorld(15, 1);
        var run = Guid.NewGuid();
        guest.HostAbsentOnEpoch(15, run);

        var original = new Wo171Rules.SlotTable().AssignGuest(Wo171Tests.Member("Ann")).AssignGuest(Wo171Tests.Member("Bob"));
        await Receive(guest, Host, SnapshotBody(original, Ann));
        Assert.Equal(3, Slot(guest, Bob));
        guest.EnterHostAbsentPause("fixture");
        var messageFromReturningHost = SnapshotBody(original.AssignGuest(Wo171Tests.Member("Cy")), Ann);
        Connect(Peers(guest), 10, "Cy");
        byte returningHost = Host;
        if (changedHostId)
        {
            returningHost = 17;
            Peers(guest).Disconnected(Host);
            Connect(Peers(guest), Host, "Different player");
            Connect(Peers(guest), returningHost, "Host");
            ConfirmHost(guest, returningHost);
            Assert.NotEqual(1, Slot(guest, Host));
        }
        await Receive(guest, returningHost, messageFromReturningHost);
        Assert.Equal(3, Slot(guest, Bob));
        Assert.Null(Slot(guest, 10));
        Assert.False(guest.CompleteHostAbsentResume(16, run));
        Assert.True(guest.CompleteHostAbsentResume(15, run));
        Console.WriteLine($"WO171-TEST returning_host_id={returningHost} prior_slot={Slot(guest, Bob)?.ToString() ?? "none"}");
        Assert.Equal(3, Slot(guest, Bob));
        Assert.Equal(1, Slot(guest, returningHost));
        await Receive(guest, returningHost, messageFromReturningHost);
        Assert.Equal(4, Slot(guest, 10));
        await Receive(guest, returningHost, SnapshotBody(original, Ann));
        Assert.Equal(4, Slot(guest, 10));
        Assert.Equal(3, Slot(guest, Bob));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task The_same_local_host_world_keeps_guest_reservations_when_its_relay_stream_returns(int players)
    {
        using var oldRelay = new MemoryStream();
        using var newRelay = new MemoryStream();
        var host = Bridge(Host, oldRelay, hosting: true);
        var world = (Wo206Epochs)typeof(GameBridge).GetField("_w206", Private)!.GetValue(host)!;
        world.EnsureHost();
        Connect(Peers(host), Ann, "Ann");
        if (players >= 3) Connect(Peers(host), Bob, "Bob");
        if (players >= 4) Connect(Peers(host), 10, "Cy");
        await (Task)Call(host, "Wo171SyncSlotsAsync")!;
        Assert.Equal(2, Slot(host, Ann));
        if (players >= 3) Assert.Equal(3, Slot(host, Bob));
        if (players >= 4) Assert.Equal(4, Slot(host, 10));

        Set(host, "_combatRoleApplied", false);
        Set(host, "_isDamageAuthority", false);
        Set(host, "_wo122Stream", newRelay);
        Peers(host).Disconnected(Ann);
        Connect(Peers(host), 20, "Ann");
        await (Task)Call(host, "Wo171SyncSlotsAsync")!;
        Assert.Equal(0, newRelay.Length);
        Assert.Null(Slot(host, 20));

        Set(host, "_combatRoleApplied", true);
        Set(host, "_isDamageAuthority", true);
        await (Task)Call(host, "Wo171SyncSlotsAsync")!;
        Assert.True(newRelay.Length > 0);
        Assert.Equal(2, Slot(host, 20));
        if (players >= 3) Assert.Equal(3, Slot(host, Bob));
        if (players >= 4) Assert.Equal(4, Slot(host, 10));
        for (byte next = 21; next <= 25; next++)
        {
            Peers(host).Disconnected((byte)(next - 1));
            Connect(Peers(host), next, "Ann");
            await (Task)Call(host, "Wo171SyncSlotsAsync")!;
            Assert.Equal(2, Slot(host, next));
        }
    }

    [Fact]
    public async Task A_new_relay_instance_assigns_fresh_slots_even_when_the_host_keeps_its_world()
    {
        using var previous = new MemoryStream();
        using var current = new MemoryStream();
        var host = Bridge(Host, previous, hosting: true);
        var world = (Wo206Epochs)typeof(GameBridge).GetField("_w206", Private)!.GetValue(host)!;
        world.EnsureHost();
        var roster = Peers(host);
        Connect(roster, Ann, "Ann");
        Connect(roster, Bob, "Bob");
        await (Task)Call(host, "Wo171SyncSlotsAsync")!;
        Assert.Equal(3, Slot(host, Bob));

        roster.Clear();
        Set(host, "_wo122Stream", current);
        Assert.True(roster.AnnounceRosterPeer(Bob, Wo171Tests.Member("restarted-relay"), Wo171Tests.Member("Bob-new-relay")));
        roster.Connected(Bob, "Bob");
        await (Task)Call(host, "Wo171SyncSlotsAsync")!;
        Assert.Equal(2, Slot(host, Bob));
        Assert.Equal(1, Slot(host, Host));
    }

    [Fact]
    public async Task Host_sends_one_snapshot_to_each_peer_before_either_joins_the_world()
    {
        using var hostRelay = new MemoryStream();
        using var annRelay = new MemoryStream();
        using var bobRelay = new MemoryStream();
        var host = Bridge(Host, hostRelay, hosting: true);
        var ann = Bridge(Ann, annRelay);
        var bob = Bridge(Bob, bobRelay);
        Connect(Peers(host), Ann, "Ann");
        Connect(Peers(host), Bob, "Bob");
        Connect(Peers(ann), Bob, "Bob");
        Connect(Peers(ann), Host, "Host");
        Connect(Peers(bob), Host, "Host");
        Connect(Peers(bob), Ann, "Ann");
        ConfirmHost(ann);
        ConfirmHost(bob);

        Assert.Null(Slot(ann, Bob));
        Assert.Null(Slot(bob, Ann));
        await (Task)Call(host, "Wo171SyncSlotsAsync")!;
        hostRelay.Position = 0;
        var toAnn = ReadSlotPacket(hostRelay);
        var toBob = ReadSlotPacket(hostRelay);
        Assert.Equal(Ann, toAnn.Target);
        Assert.Equal(Bob, toBob.Target);
        Assert.Equal(hostRelay.Length, hostRelay.Position);
        await Receive(ann, Host, toAnn.Body);
        await Receive(bob, Host, toBob.Body);

        Assert.Equal(1, Slot(ann, Host));
        Assert.Equal(1, Slot(bob, Host));
        Assert.Equal(2, Slot(host, Ann));
        Assert.Equal(2, Slot(bob, Ann));
        Assert.Equal(3, Slot(host, Bob));
        Assert.Equal(3, Slot(ann, Bob));

        const byte bobAfterRejoin = 20;
        Peers(host).Disconnected(Bob);
        Connect(Peers(host), bobAfterRejoin, "Bob");
        Peers(ann).Disconnected(Bob);
        Connect(Peers(ann), bobAfterRejoin, "Bob");
        await (Task)Call(host, "Wo171SyncSlotsAsync")!;
        Assert.Equal(3, Slot(host, bobAfterRejoin));
        Assert.Equal(3, Slot(ann, bobAfterRejoin));
    }

    [Fact]
    public async Task Slot_snapshots_require_the_confirmed_session_host_and_current_relay_stream()
    {
        using var relay = new MemoryStream();
        using var newRelay = new MemoryStream();
        var joiner = Bridge(Ann, relay);
        const byte earliestPeer = 1;
        Connect(Peers(joiner), earliestPeer, "Cy");
        Connect(Peers(joiner), Host, "Host");
        Connect(Peers(joiner), Bob, "Bob");
        var hostSlots = new Wo171Rules.SlotTable().AssignGuest(Wo171Tests.Member("Ann")).AssignGuest(Wo171Tests.Member("Bob"));
        var otherSlots = new Wo171Rules.SlotTable().AssignGuest(Wo171Tests.Member("Bob")).AssignGuest(Wo171Tests.Member("Ann"));
        byte[] accepted = SnapshotBody(hostSlots, Ann);
        byte[] untrusted = SnapshotBody(otherSlots, Ann);

        await Receive(joiner, Host, accepted);
        await Receive(joiner, earliestPeer, untrusted);
        Assert.Null(Slot(joiner, Bob));
        ConfirmHost(joiner);
        await Receive(joiner, earliestPeer, untrusted);
        Assert.Null(Slot(joiner, Bob));
        await Receive(joiner, Host, accepted);
        Assert.Equal(3, Slot(joiner, Bob));
        await Receive(joiner, earliestPeer, untrusted);
        Assert.Equal(3, Slot(joiner, Bob));

        Set(joiner, "_wo122Stream", newRelay);
        Assert.Null(Slot(joiner, Bob));
        await Receive(joiner, Host, accepted);
        Assert.Equal(3, Slot(joiner, Bob));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Losing_host_confirmation_on_the_same_stream_preserves_rendered_slots_without_accepting_snapshots(bool hostStillLive)
    {
        const byte cy = 10, observer = 11;
        using var relay = new MemoryStream();
        var joiner = Bridge(observer, relay);
        Connect(Peers(joiner), Host, "Host");
        Connect(Peers(joiner), Ann, "Ann");
        Connect(Peers(joiner), Bob, "Bob");
        Connect(Peers(joiner), cy, "Cy");
        ConfirmHost(joiner);
        var original = new Wo171Rules.SlotTable().AssignGuest(Wo171Tests.Member("Ann")).AssignGuest(Wo171Tests.Member("Bob")).AssignGuest(Wo171Tests.Member("Cy"));
        var conflicting = new Wo171Rules.SlotTable().AssignGuest(Wo171Tests.Member("Cy")).AssignGuest(Wo171Tests.Member("Bob")).AssignGuest(Wo171Tests.Member("Ann"));
        await Receive(joiner, Host, SnapshotBody(original, observer));

        int?[] Slots() => [Slot(joiner, Host), Slot(joiner, Ann), Slot(joiner, Bob), Slot(joiner, cy)];
        int?[] expected = [1, 2, 3, 4];
        Assert.Equal(expected, Slots());

        if (hostStillLive)
        {
            Set(joiner, "_hostModeKnown", false);
            // The renderer sees unknown confirmation before the next packet does.
            Assert.Equal(expected, Slots());
        }
        else
        {
            // The next packet sees the host leave the roster before the renderer polls.
            Peers(joiner).Disconnected(Host);
        }
        await Receive(joiner, Host, SnapshotBody(conflicting, observer));
        Assert.Equal(hostStillLive ? expected : new int?[] { null, 2, 3, 4 }, Slots());
        if (!hostStillLive)
        {
            Set(joiner, "_hostModeKnown", false);
            Connect(Peers(joiner), Host, "Different player");
            Assert.Equal(0, Slot(joiner, Host));
        }
        Assert.Equal(new[] { 2, 3, 4 }, Slots().Skip(1).Select(slot =>
            Wo171Rules.PictureSlot(Wo171Rules.CodeFor(Wo171Rules.Style.Rotating, slot ?? 0, 0, 0))));

        Connect(Peers(joiner), Host, "Host");
        ConfirmHost(joiner);
        Assert.Equal(expected, Slots());
    }

    [Fact]
    public async Task Pins_stay_visible_during_unknown_host_mode_but_reset_when_separate_worlds_are_confirmed()
    {
        using var relay = new MemoryStream();
        var joiner = Bridge(Ann, relay);
        Connect(Peers(joiner), Host, "Host");
        Connect(Peers(joiner), Bob, "Bob");
        Set(joiner, "_joinedWorld", true);
        Set(joiner, "_hostAuthority", true);
        Set(joiner, "_sharedWorld", false);
        ConfirmHost(joiner);
        var slots = new Wo171Rules.SlotTable().AssignGuest(Wo171Tests.Member("Ann")).AssignGuest(Wo171Tests.Member("Bob"));
        byte[] snapshot = SnapshotBody(slots, Ann);
        await Receive(joiner, Host, snapshot);

        bool PinsVisible() => (bool)Call(joiner, "Wo171PinSession")!;
        Assert.True(PinsVisible());
        Assert.Equal(3, Slot(joiner, Bob));

        Set(joiner, "_hostModeKnown", false);
        Assert.True(PinsVisible());
        Assert.Equal(3, Slot(joiner, Bob));
        Set(joiner, "_joinedWorld", false);
        Assert.False(PinsVisible());
        Set(joiner, "_joinedWorld", true);

        Set(joiner, "_hostSharedWorld", false);
        Set(joiner, "_hostModeKnown", true);
        Assert.False(PinsVisible());
        await Receive(joiner, Host, snapshot);
        ConfirmHost(joiner);
        Assert.True(PinsVisible());
        Assert.Null(Slot(joiner, Bob));
        await Receive(joiner, Host, snapshot);
        Assert.Equal(3, Slot(joiner, Bob));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_new_stream_or_confirmed_host_discards_the_previous_slots_even_after_an_unknown_host(bool newStream)
    {
        const byte replacementHost = 3;
        using var relay = new MemoryStream();
        using var replacementRelay = new MemoryStream();
        var joiner = Bridge(Ann, relay);
        Connect(Peers(joiner), Host, "Host");
        Connect(Peers(joiner), Bob, "Bob");
        Connect(Peers(joiner), replacementHost, "Replacement");
        ConfirmHost(joiner);
        var original = new Wo171Rules.SlotTable().AssignGuest(Wo171Tests.Member("Ann")).AssignGuest(Wo171Tests.Member("Bob"));
        var replacement = new Wo171Rules.SlotTable().AssignGuest(Wo171Tests.Member("Bob")).AssignGuest(Wo171Tests.Member("Ann"));
        byte[] originalBody = SnapshotBody(original, Ann);
        byte[] replacementBody = SnapshotBody(replacement, Ann);
        await Receive(joiner, Host, originalBody);
        Assert.Equal(3, Slot(joiner, Bob));
        Set(joiner, "_hostModeKnown", false);
        Assert.Equal(3, Slot(joiner, Bob));

        byte nextHost;
        if (newStream)
        {
            Set(joiner, "_wo122Stream", replacementRelay);
            Assert.Null(Slot(joiner, Host));
            nextHost = Host;
        }
        else
        {
            nextHost = replacementHost;
            ConfirmHost(joiner, nextHost);
            Assert.Null(Slot(joiner, Host));
            await Receive(joiner, Host, originalBody);
        }
        Assert.Null(Slot(joiner, Bob));
        ConfirmHost(joiner, nextHost);
        Assert.Equal(1, Slot(joiner, nextHost));
        Assert.Null(Slot(joiner, Bob));
        await Receive(joiner, nextHost, replacementBody);
        Assert.Equal(2, Slot(joiner, Bob));
    }
}
