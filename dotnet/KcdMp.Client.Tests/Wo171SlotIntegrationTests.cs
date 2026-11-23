// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public class Wo171SlotIntegrationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const byte Host = 7, Ann = 8, Bob = 9;

    private static void Set(GameBridge bridge, string name, object value) =>
        typeof(GameBridge).GetField(name, Private)!.SetValue(bridge, value);

    private static object? Call(GameBridge bridge, string name, params object?[] arguments) =>
        typeof(GameBridge).GetMethod(name, Private)!.Invoke(bridge, arguments);

    private static PeerSet Peers(GameBridge bridge) =>
        (PeerSet)typeof(GameBridge).GetField("_peers", Private)!.GetValue(bridge)!;

    private static int? Slot(GameBridge bridge, byte peer) =>
        (int?)Call(bridge, "Wo171PinSlot", peer, Peers(bridge).NameOf(peer));

    private static Task Receive(GameBridge bridge, byte source, byte[] body) =>
        (Task)Call(bridge, "Wo164OnFrameAsync", source, body)!;

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
        foreach (byte id in ids.Reverse()) Peers(host).Connected(id, $"Guest{id}");

        await (Task)Call(host, "Wo164TickAsync")!;

        Console.WriteLine($"WO171-TEST players={players} palette_bytes={wire.Length}");
        Assert.True(wire.Length > 0, "The host tick did not publish any authoritative marker table.");
        string expected = "marker-slots-v1|" + string.Join('|', ids.Select(id => Convert.ToBase64String(Encoding.UTF8.GetBytes($"Guest{id}")))
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
        var host = Bridge(hostPeer.Id, hostPeer.Wire, hosting: true);
        Set(host, "_wo122Ct", ct);
        Set(host, "_w164On", false);
        var guests = peers.Select(p => Bridge(p.Peer!.Id, p.Peer.Wire)).ToArray();
        var readers = new List<Task>();
        for (int i = 0; i != guests.Length; i++)
        {
            byte self = peers[i].Peer!.Id;
            Peers(host).Connected(self, peers[i].Name);
            var guest = guests[i];
            Set(guest, "_wo122Ct", ct);
            Peers(guest).Connected(hostPeer.Id, "Host");
            foreach (var p in peers.Reverse()) Peers(guest).Connected(p.Peer!.Id, p.Name);
            readers.Add((Task)Call(guest, "ReceiveLoopAsync", peers[i].Peer!.Wire, ct)!);
        }

        try
        {
            byte[] position = new byte[Protocol.PositionPayloadLen];
            position[16] = Protocol.PositionFlagHostClaim;
            await hostPeer.SendAsync(ReplayPeer.Frame(Protocol.Position, position), ct);
            foreach (var p in peers)
                await hostPeer.SendAsync(JoinStatusCodec.Build(p.Peer!.Id, 0, Protocol.JoinStateSession,
                    Protocol.JoinReasonId("shared-world"), 0), ct);
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
        Peers(guest).Connected(Host, "Host");
        Peers(guest).Connected(Bob, "Bob");
        ConfirmHost(guest);
        var world = (Wo206Epochs)typeof(GameBridge).GetField("_w206", Private)!.GetValue(guest)!;
        world.LearnWorld(15, 1);
        var run = Guid.NewGuid();
        guest.HostAbsentOnEpoch(15, run);

        var original = new Wo171Rules.SlotTable().AssignGuest("Ann").AssignGuest("Bob");
        await Receive(guest, Host, SnapshotBody(original, Ann));
        Assert.Equal(3, Slot(guest, Bob));
        guest.EnterHostAbsentPause("fixture");
        var messageFromReturningHost = SnapshotBody(original.AssignGuest("Cy"), Ann);
        Peers(guest).Connected(10, "Cy");
        byte returningHost = Host;
        if (changedHostId)
        {
            returningHost = 17;
            Peers(guest).Disconnected(Host);
            Peers(guest).Connected(Host, "Different player");
            Peers(guest).Connected(returningHost, "Host");
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
        Peers(host).Connected(Ann, "Ann");
        if (players >= 3) Peers(host).Connected(Bob, "Bob");
        if (players >= 4) Peers(host).Connected(10, "Cy");
        await (Task)Call(host, "Wo171SyncSlotsAsync")!;
        Assert.Equal(2, Slot(host, Ann));
        if (players >= 3) Assert.Equal(3, Slot(host, Bob));
        if (players >= 4) Assert.Equal(4, Slot(host, 10));

        Set(host, "_combatRoleApplied", false);
        Set(host, "_isDamageAuthority", false);
        Set(host, "_wo122Stream", newRelay);
        Peers(host).Disconnected(Ann);
        Peers(host).Connected(20, "Ann");
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
            Peers(host).Connected(next, "Ann");
            await (Task)Call(host, "Wo171SyncSlotsAsync")!;
            Assert.Equal(2, Slot(host, next));
        }
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
        Peers(host).Connected(Ann, "Ann");
        Peers(host).Connected(Bob, "Bob");
        Peers(ann).Connected(Bob, "Bob");
        Peers(ann).Connected(Host, "Host");
        Peers(bob).Connected(Host, "Host");
        Peers(bob).Connected(Ann, "Ann");
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
        Peers(host).Connected(bobAfterRejoin, "Bob");
        Peers(ann).Disconnected(Bob);
        Peers(ann).Connected(bobAfterRejoin, "Bob");
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
        Peers(joiner).Connected(earliestPeer, "Cy");
        Peers(joiner).Connected(Host, "Host");
        Peers(joiner).Connected(Bob, "Bob");
        var hostSlots = new Wo171Rules.SlotTable().AssignGuest("Ann").AssignGuest("Bob");
        var otherSlots = new Wo171Rules.SlotTable().AssignGuest("Bob").AssignGuest("Ann");
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
        Peers(joiner).Connected(Host, "Host");
        Peers(joiner).Connected(Ann, "Ann");
        Peers(joiner).Connected(Bob, "Bob");
        Peers(joiner).Connected(cy, "Cy");
        ConfirmHost(joiner);
        var original = new Wo171Rules.SlotTable().AssignGuest("Ann").AssignGuest("Bob").AssignGuest("Cy");
        var conflicting = new Wo171Rules.SlotTable().AssignGuest("Cy").AssignGuest("Bob").AssignGuest("Ann");
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
            Peers(joiner).Connected(Host, "Different player");
            Assert.Equal(0, Slot(joiner, Host));
        }
        Assert.Equal(new[] { 2, 3, 4 }, Slots().Skip(1).Select(slot =>
            Wo171Rules.PictureSlot(Wo171Rules.CodeFor(Wo171Rules.Style.Rotating, slot ?? 0, 0, 0))));

        Peers(joiner).Connected(Host, "Host");
        ConfirmHost(joiner);
        Assert.Equal(expected, Slots());
    }

    [Fact]
    public async Task Pins_stay_visible_during_unknown_host_mode_but_reset_when_separate_worlds_are_confirmed()
    {
        using var relay = new MemoryStream();
        var joiner = Bridge(Ann, relay);
        Peers(joiner).Connected(Host, "Host");
        Peers(joiner).Connected(Bob, "Bob");
        Set(joiner, "_joinedWorld", true);
        Set(joiner, "_hostAuthority", true);
        Set(joiner, "_sharedWorld", false);
        ConfirmHost(joiner);
        var slots = new Wo171Rules.SlotTable().AssignGuest("Ann").AssignGuest("Bob");
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
        Peers(joiner).Connected(Host, "Host");
        Peers(joiner).Connected(Bob, "Bob");
        Peers(joiner).Connected(replacementHost, "Replacement");
        ConfirmHost(joiner);
        var original = new Wo171Rules.SlotTable().AssignGuest("Ann").AssignGuest("Bob");
        var replacement = new Wo171Rules.SlotTable().AssignGuest("Bob").AssignGuest("Ann");
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
