// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;
using System.Reflection;
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
        var bridge = new GameBridge(new ClientConfig());
        Set(bridge, "_myGhostId", self);
        Set(bridge, "_wo122Stream", relay);
        Set(bridge, "_combatRoleApplied", true);
        Set(bridge, "_isDamageAuthority", hosting);
        Set(bridge, "_sharedWorld", hosting);
        return bridge;
    }

    private static void ConfirmHost(GameBridge bridge)
    {
        Set(bridge, "_hostModeKnown", true);
        Set(bridge, "_hostSharedWorld", true);
        Set(bridge, "_hostModeFrom", Host);
    }

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
        static byte[] Body(Wo171Rules.SlotTable slots) =>
            new LootMsg(Protocol.W164MarkNote, 1, slots.ToWire()).BuildUp(Protocol.W164Up, Ann)[(3 + Protocol.JoinHeaderLen)..];

        await Receive(joiner, Host, Body(hostSlots));
        await Receive(joiner, earliestPeer, Body(otherSlots));
        Assert.Null(Slot(joiner, Bob));
        ConfirmHost(joiner);
        await Receive(joiner, earliestPeer, Body(otherSlots));
        Assert.Null(Slot(joiner, Bob));
        await Receive(joiner, Host, Body(hostSlots));
        Assert.Equal(3, Slot(joiner, Bob));
        await Receive(joiner, earliestPeer, Body(otherSlots));
        Assert.Equal(3, Slot(joiner, Bob));

        Set(joiner, "_wo122Stream", newRelay);
        Assert.Null(Slot(joiner, Bob));
        await Receive(joiner, Host, Body(hostSlots));
        Assert.Equal(3, Slot(joiner, Bob));
    }
}
