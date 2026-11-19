// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;
using System.Reflection;
using KcdMp.Server.Features.ClientHandling;
using Microsoft.Extensions.DependencyInjection;
using PositionCodec = KcdMp.Client.PositionCodec;

namespace KcdMp.Relay.Tests;

// =============================================================================
// WO-203 II.5: THE RELAY MESSAGE TABLE GATE. A PRE-SHIP GATE, LIKE THE
// ROUND-TRIP GATE BESIDE IT (tools\Test-RelayTable.ps1, Build-Installer).
//
// Why it exists: in session C (0.62.0) the relay dropped 463 W164 frames
// (0x74) as "unknown-or-wrong-length" -- every WO-201 speech line and some
// votes. The type WAS in the relay's table; its row's length limit (80 bytes
// of text) was older than the senders, which went up to ~500. Nothing compared
// the protocol's types with the relay's rules, nothing put a row's maximum
// through the relay, and the drop reason could not tell "unknown" from "too
// long".
//
// What it does: (a) reflects over every message id the protocol defines and
// requires a relay rule for each up type (Protocol.RelayUp or JoinWire), and
// a defined type for each rule; (b) puts every join row through the REAL relay
// at its minimum and its maximum, and one byte past each -- dropped, counted
// as a wrong length, and the framing survives; (c) does the same for every
// other up type with a minimal valid body; (d) proves Act I's and Act II's
// channels carry their 1400-byte texts both ways. A new type without a rule,
// or a rule the relay does not honour, fails here and does not ship.
// =============================================================================

public class RelayTableGateTests : IClassFixture<RelayFixture>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private readonly RelayFixture _relay;

    public RelayTableGateTests(RelayFixture relay) => _relay = relay;

    private ClientHandler Clients => _relay.Services.GetRequiredService<ClientHandler>();

    private long Drops(string keyPrefix) => Clients.DropCount(keyPrefix);

    private static string Hex(byte t) => $"0x{t:X2}";

    // ---------------------------------------------------------------- (a) the protocol's ids against the relay's table

    // Up types whose names do not end in "Up" (Protocol.cs, the C->S block).
    private static readonly string[] UnsuffixedUps = { "Handshake", "Position", "Ping", "Invite", "InviteResponse", "SessionLeave", "DiceIntent" };
    // Down types whose names do not end in "Down" (Protocol.cs, the S->C block).
    private static readonly string[] UnsuffixedDowns =
    {
        "Ghost", "Name", "Pong", "Disconnect", "VersionMismatch", "InviteReceived", "SessionStart", "SessionEnd", "DiceState", "DiceError",
        "DiceEnd", "ReleaseVersion", "CombatRole", "ServerFull", "ReleaseVersionMismatch", "Ack",
    };
    // Constants whose names end in "Down" but are not message types: a player-state flag and a W164 kind.
    private static readonly string[] DownNamedNotTypes = { "PlayerStateFlagKnockedDown", "W164NpcDown" };

    private static Dictionary<string, byte> ByteConsts() =>
        typeof(Protocol).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(byte))
            .ToDictionary(f => f.Name, f => (byte)f.GetRawConstantValue()!);

    private static Dictionary<string, byte> UpTypes()
    {
        var all = ByteConsts();
        return all.Where(kv => kv.Key.EndsWith("Up", StringComparison.Ordinal) || UnsuffixedUps.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private static Dictionary<string, byte> DownTypes()
    {
        var all = ByteConsts();
        return all.Where(kv => (kv.Key.EndsWith("Down", StringComparison.Ordinal) && !DownNamedNotTypes.Contains(kv.Key)) || UnsuffixedDowns.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    [Fact]
    public void Every_protocol_up_type_has_a_relay_rule_and_every_rule_a_defined_type()
    {
        var all = ByteConsts();
        foreach (var n in UnsuffixedUps.Concat(UnsuffixedDowns).Concat(DownNamedNotTypes))
            Assert.True(all.ContainsKey(n), $"Protocol.{n} is gone -- update this gate's name lists");

        var ups = UpTypes();
        var downs = DownTypes();

        // every up type has exactly one rule, and the relay's gate knows it
        foreach (var (name, v) in ups)
        {
            int rules = (v == Protocol.Handshake ? 1 : 0) + Protocol.RelayUp.Count(r => r.Up == v) + Protocol.JoinWire.Count(r => r.Up == v);
            Assert.True(rules == 1, $"Protocol.{name} ({Hex(v)}) has {rules} relay rules (RelayUp / JoinWire); it needs exactly one");
            Assert.True(Protocol.IsKnownUp(v), $"Protocol.{name} ({Hex(v)}) is not known to the relay's gate");
            Assert.False(Protocol.IsKnownDown(v), $"Protocol.{name} ({Hex(v)}) is an up type the agent counts as a down type");
        }
        // every rule names a defined up type (and a join row a defined down type)
        var upValues = ups.Values.ToHashSet();
        var downValues = downs.Values.ToHashSet();
        foreach (var r in Protocol.RelayUp) Assert.True(upValues.Contains(r.Up), $"RelayUp row '{r.Name}' ({Hex(r.Up)}) names no up type the protocol defines");
        foreach (var r in Protocol.JoinWire)
        {
            Assert.True(upValues.Contains(r.Up), $"JoinWire row '{r.Name}' ({Hex(r.Up)}) names no up type the protocol defines");
            Assert.True(downValues.Contains(r.Down), $"JoinWire row '{r.Name}' down {Hex(r.Down)} is no down type the protocol defines");
        }
        // the table is exactly the protocol: {Handshake} + RelayUp + JoinWire == every up type
        var table = new HashSet<byte> { Protocol.Handshake };
        foreach (var r in Protocol.RelayUp) table.Add(r.Up);
        foreach (var r in Protocol.JoinWire) table.Add(r.Up);
        Assert.Equal(upValues.OrderBy(x => x), table.OrderBy(x => x));

        // ids unique within each direction, and no up id is also a down id
        foreach (var g in ups.GroupBy(kv => kv.Value)) Assert.True(g.Count() == 1, $"up id {Hex(g.Key)} is used by {string.Join(", ", g.Select(kv => kv.Key))}");
        foreach (var g in downs.GroupBy(kv => kv.Value)) Assert.True(g.Count() == 1, $"down id {Hex(g.Key)} is used by {string.Join(", ", g.Select(kv => kv.Key))}");
        foreach (var (name, v) in ups)
            Assert.False(downValues.Contains(v), $"up id Protocol.{name} ({Hex(v)}) is also a down id ({string.Join(", ", downs.Where(d => d.Value == v).Select(d => d.Key))})");
        Assert.Equal(Protocol.RelayUp.Length, Protocol.RelayUp.Select(r => r.Name).Distinct().Count());
        Assert.Equal(Protocol.JoinWire.Length, Protocol.JoinWire.Select(r => r.Name).Distinct().Count());

        // the agent's side: every down type is known, and RelayDown + the join rows' downs are exactly the down types
        foreach (var (name, v) in downs)
        {
            Assert.True(Protocol.IsKnownDown(v), $"Protocol.{name} ({Hex(v)}) is not known to the agent's catch-all");
            Assert.False(Protocol.IsKnownUp(v), $"Protocol.{name} ({Hex(v)}) is a down type the relay's gate takes as an up type");
        }
        var downTable = Protocol.RelayDown.ToHashSet();
        foreach (var r in Protocol.JoinWire) downTable.Add(r.Down);
        Assert.Equal(downValues.OrderBy(x => x), downTable.OrderBy(x => x));

        // a byte nobody defined is unknown both ways
        for (int t = 0; t <= 0xFF; t++)
            if (!upValues.Contains((byte)t) && !downValues.Contains((byte)t))
            {
                Assert.False(Protocol.IsKnownUp(t), $"{Hex((byte)t)} is defined by nobody but the gate knows it");
                Assert.False(Protocol.IsKnownDown(t), $"{Hex((byte)t)} is defined by nobody but the agent knows it");
            }
    }

    [Fact]
    public void Every_rule_is_a_sane_range_and_the_gate_reads_it()
    {
        foreach (var r in Protocol.RelayUp)
        {
            Assert.True(r.Min >= 0 && r.Min <= r.Max && r.Max <= Protocol.FramePayloadMax, $"RelayUp '{r.Name}' {r.Min}..{r.Max}");
            Assert.True(Protocol.IsUpLenOk(r.Up, r.Min), $"RelayUp '{r.Name}' refuses its own minimum {r.Min}");
            Assert.True(Protocol.IsUpLenOk(r.Up, r.Max), $"RelayUp '{r.Name}' refuses its own maximum {r.Max}");
            if (r.Min > 0) Assert.False(Protocol.IsUpLenOk(r.Up, r.Min - 1), $"RelayUp '{r.Name}' takes {r.Min - 1}");
            if (r.Max < Protocol.FramePayloadMax) Assert.False(Protocol.IsUpLenOk(r.Up, r.Max + 1), $"RelayUp '{r.Name}' takes {r.Max + 1}");
        }
        foreach (var r in Protocol.JoinWire)
        {
            Assert.True(Protocol.IsUpLenOk(r.Up, r.Min) && Protocol.IsUpLenOk(r.Up, r.Max), r.Name);
            Assert.False(Protocol.IsUpLenOk(r.Up, r.Min - 1), r.Name);
            Assert.False(Protocol.IsUpLenOk(r.Up, r.Max + 1), r.Name);
        }
        // the two types that take only some lengths of their range
        foreach (int len in new[] { 17, 21, 29, 33 }) Assert.True(Protocol.IsUpLenOk(Protocol.Position, len), $"position {len}");
        foreach (int len in new[] { 16, 18, 20, 22, 26, 34 }) Assert.False(Protocol.IsUpLenOk(Protocol.Position, len), $"position {len}");
        Assert.True(Protocol.IsUpLenOk(Protocol.CombatEventUp, 1) && Protocol.IsUpLenOk(Protocol.CombatEventUp, 3));
        Assert.False(Protocol.IsUpLenOk(Protocol.CombatEventUp, 2));
        // the Handshake: [version][nameLen] at least
        Assert.True(Protocol.IsKnownUp(Protocol.Handshake));
        Assert.False(Protocol.IsUpLenOk(Protocol.Handshake, 1));
        Assert.True(Protocol.IsUpLenOk(Protocol.Handshake, 2));
        Assert.False(Protocol.IsKnownUp(-1));
        Assert.False(Protocol.IsKnownUp(256));
        // the side channels name their kind in a drop key
        Assert.True(Protocol.DropKeyHasKind(Protocol.W164Up) && Protocol.DropKeyHasKind(Protocol.ConvUp) && Protocol.DropKeyHasKind(Protocol.W203Up));
        Assert.False(Protocol.DropKeyHasKind(Protocol.LootAskUp));
        Assert.Equal(-1, Protocol.JoinBodyKind(new byte[Protocol.JoinHeaderLen]));
        Assert.Equal(21, Protocol.JoinBodyKind(new byte[] { 0xFF, 0, 0, 0, 0, 21, 1 }));
    }

    // ---------------------------------------------------------------- the peers

    private static byte[] Frame(byte type, byte[] body)
    {
        var pkt = new byte[3 + body.Length];
        pkt[0] = type;
        BinaryPrimitives.WriteUInt16LittleEndian(pkt.AsSpan(1), (ushort)body.Length);
        body.CopyTo(pkt, 3);
        return pkt;
    }

    private async Task WaitNoClientsAsync()
    {
        var until = DateTime.UtcNow + Wait;
        while (Clients.ReadyClientCount > 0 && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.Equal(0, Clients.ReadyClientCount);
    }

    /// <summary>
    /// The host (it claims the session: WO-127's flag, so it is the damage authority whatever the ids) and one joiner. Returns once
    /// the joiner has seen the host's claim -- the relay has processed it, so every later host frame meets the host's authority.
    /// </summary>
    private async Task<(Peer Host, Peer Joiner)> HostAndJoinerAsync()
    {
        await WaitNoClientsAsync();
        var host = await Peer.ConnectAsync(_relay.TcpPort, "the-host");
        var joiner = await Peer.ConnectAsync(_relay.TcpPort, "the-joiner");
        await Task.Delay(50);
        await host.SendRawAsync(ClaimPosition());
        var g = await joiner.ReadUntilAsync(Protocol.Ghost, Wait);
        Assert.Equal(host.Id, g[0]);
        Assert.True(Clients.IsDamageAuthority(Clients.GetClients().Single(c => c.IsReady && c.Id == host.Id)));
        return (host, joiner);
    }

    private static byte[] ClaimPosition()
    {
        var b = new byte[Protocol.PositionPayloadLen];
        b[16] = Protocol.PositionFlagHostClaim;
        return Frame(Protocol.Position, b);
    }

    private static async Task<byte[]> ArrivesAsync(Peer at, byte down, int len, int src = -1)
    {
        var d = await at.ReadUntilAsync(down, Wait);
        Assert.Equal(len, d.Length);
        if (src >= 0) Assert.Equal((byte)src, d[0]);
        return d;
    }

    // ---------------------------------------------------------------- (b) every join row, at its minimum and its maximum

    public static IEnumerable<object[]> JoinRows() => Protocol.JoinWire.Select(r => new object[] { r.Name });

    private static byte[] JoinBody(int len, byte target)
    {
        var b = new byte[len];
        if (len > 0) b[0] = target;   // [target][joinId:4 = 0][the rest zero: the relay never reads past the target]
        return b;
    }

    [Theory]
    [MemberData(nameof(JoinRows))]
    public async Task Every_join_row_crosses_the_relay_at_min_and_max_and_one_byte_past_is_counted(string name)
    {
        var row = Protocol.JoinWire.Single(r => r.Name == name);
        var (host, joiner) = await HostAndJoinerAsync();
        await using var _h = host; await using var _j = joiner;

        // the side that may send it: a joiner's (and an "either" row's) goes to the host by 0xFF, the host's to the joiner by id
        bool fromHost = row.From == Protocol.JoinFrom.Host;
        var (tx, rx) = fromHost ? (host, joiner) : (joiner, host);
        byte target = fromHost ? joiner.Id : Protocol.JoinTargetHost;
        string type = Hex(row.Up);
        long before = Drops(type);

        foreach (int len in new[] { row.Min, row.Max })
        {
            await tx.SendRawAsync(Frame(row.Up, JoinBody(len, target)));
            var d = await ArrivesAsync(rx, row.Down, len + 1, tx.Id);
            Assert.Equal(target, d[1]);
        }
        Assert.Equal(before, Drops(type));   // nothing of this type dropped on the way

        // one byte past the maximum and one short of the minimum: dropped AND counted as wrong lengths that name the limit; the
        // framing survives both -- the valid frame behind them is the next of its type to arrive
        string key = Protocol.DropKeyHasKind(row.Up) ? $"{type}/k0:" : $"{type}:";
        string over = $"{key}wrong-length:{row.Max + 1}>max{row.Max}", under = $"{key}wrong-length:{row.Min - 1}<min{row.Min}";
        long overBefore = Drops(over), underBefore = Drops(under);
        await tx.SendRawAsync(Frame(row.Up, JoinBody(row.Max + 1, target)));
        await tx.SendRawAsync(Frame(row.Up, JoinBody(row.Min - 1, target)));
        await tx.SendRawAsync(Frame(row.Up, JoinBody(row.Min, target)));
        await ArrivesAsync(rx, row.Down, row.Min + 1, tx.Id);
        Assert.Equal(before + 2, Drops(type));
        Assert.Equal(overBefore + 1, Drops(over));
        Assert.Equal(underBefore + 1, Drops(under));
    }

    // ---------------------------------------------------------------- (c) every other up type, with a minimal valid body

    public static IEnumerable<object[]> RelayUpRows() => Protocol.RelayUp.Select(r => new object[] { r.Name });

    [Theory]
    [MemberData(nameof(RelayUpRows))]
    public async Task Every_relay_up_row_round_trips_with_a_minimal_valid_body(string name)
    {
        var row = Protocol.RelayUp.Single(r => r.Name == name);
        var (host, joiner) = await HostAndJoinerAsync();
        await using var _h = host; await using var _j = joiner;
        string type = Hex(row.Up);
        long before = Drops(type);

        await ProbeAsync(row.Up, row.Min, host, joiner);

        Assert.Equal(before, Drops(type));
    }

    /// <summary>Sends <paramref name="up"/> with a valid body of <paramref name="len"/> bytes the way the agent does, and waits for what
    /// the relay makes of it where it must arrive.</summary>
    private static async Task ProbeAsync(byte up, int len, Peer host, Peer joiner)
    {
        var b = new byte[len];
        switch (up)
        {
            case Protocol.Position:
                b[16] = Protocol.PositionFlagHostClaim;   // the host keeps its claim
                await host.SendRawAsync(Frame(up, b));
                await ArrivesAsync(joiner, Protocol.Ghost, Protocol.GhostPayloadLen + len - Protocol.PositionPayloadLen, host.Id);
                return;
            case Protocol.Ping:
                b[0] = 0x5A;
                await host.SendRawAsync(Frame(up, b));
                Assert.Equal(b, await ArrivesAsync(host, Protocol.Pong, len));
                return;
            case Protocol.ClockSyncUp:
                await host.SendRawAsync(Frame(up, b));
                await ArrivesAsync(host, Protocol.ClockSyncDown, Protocol.ClockSyncDownPayloadLen);
                return;
            case Protocol.PresentationClockUp:
                await OpenPresentationAsync(host);
                await CalibratePresentationAsync(host, 0);
                return;
            case Protocol.PresentationCombatUp:
                await OpenPresentationAsync(host, Protocol.LinkCapPresentationTime | Protocol.LinkCapPresentationCombat);
                var vitals = new byte[Protocol.PlayerStateUpPayloadLen];
                var stamped = PresentationTimeWire.StampCombatUp(Frame(Protocol.PlayerStateUp, vitals), 1);
                Assert.Equal(3 + len, stamped.Length);
                await host.SendRawAsync(stamped);
                await ArrivesAsync(joiner, Protocol.PlayerStateDown, Protocol.PlayerStateDownPayloadLen, host.Id);
                return;
            case Protocol.WorldSyncUp:
                await OpenPresentationAsync(joiner, Protocol.LinkCapWorldSync);
                await AnnounceWorldSyncAsync(host, joiner, 73);
                await joiner.SendRawAsync(WorldSyncWire.BuildSync(new(WorldSyncOperation.Open, new(73, 9), 0, 0, 0)));
                await ArrivesAsync(joiner, Protocol.WorldSyncStatusDown, WorldSyncStatusWire.BodyBytes);
                return;
            case Protocol.WorldRepairUp:
                await OpenPresentationAsync(joiner, Protocol.LinkCapWorldSync | Protocol.LinkCapWorldRecovery);
                var recoveryScope = new WorldSyncScope(73, 9);
                await AnnounceWorldSyncAsync(host, joiner, recoveryScope.HostWorld);
                await AcceptRecoveryBaselineAsync(joiner, recoveryScope);
                var digest = await ReadWorldDigestAsync(joiner, WorldBaselineSection.Body, Wait);
                var request = WorldRecoveryWire.BuildRequest(new(digest));
                Assert.Equal(len + 3, request.Length);
                await joiner.SendRawAsync(request);
                Assert.True(WorldRecoveryWire.TryReadChunk(await joiner.ReadUntilAsync(Protocol.WorldRepairDown, Wait), out var repair));
                Assert.Equal(digest, repair.Digest);
                return;
            case Protocol.LinkHelloUp:   // WO-213: relay-local, the answer comes back to the sender
                b[0] = Protocol.LinkVersion;
                await host.SendRawAsync(Frame(up, b));
                await ArrivesAsync(host, Protocol.LinkHelloDown, Protocol.LinkHelloDownLen);
                return;
            case Protocol.PvpLifeUp:
                const ushort pvp = Protocol.LinkCapPvpContact | Protocol.LinkCapPvpLife;
                await OpenPresentationAsync(host, pvp);
                await OpenPresentationAsync(joiner, pvp);
                await ArrivesAsync(joiner, Protocol.PvpPeerDown, Protocol.PvpPeerLifeLen, host.Id);
                var life = new PvpLifeState(1, 1234);
                var lifeUp = life.BuildUp();
                Assert.Equal(len + 3, lifeUp.Length);
                await host.SendRawAsync(lifeUp);
                var roster = await ArrivesAsync(joiner, Protocol.PvpPeerDown, Protocol.PvpPeerLifeLen, host.Id);
                Assert.True(Protocol.TryReadPvpPeer(roster, out _, out _, out var advertisedLife));
                Assert.Equal(life, advertisedLife);
                return;
            case Protocol.VoiceUp:
                await host.SendRawAsync(Frame(up, b));
                await ArrivesAsync(joiner, Protocol.VoiceDown, 1 + len, host.Id);
                return;

            // ---- WO-2 interactions and WO-5 dice: a short session flow, each message at its type's minimum
            case Protocol.Invite:
                b[0] = joiner.Id; b[1] = (byte)InteractionKind.Duel;
                await host.SendRawAsync(Frame(up, b));
                var inv = await ArrivesAsync(joiner, Protocol.InviteReceived, 5);
                Assert.Equal(host.Id, inv[2]);
                return;
            case Protocol.InviteResponse:
            {
                ushort sid = await InviteAsync(host, joiner, InteractionKind.Duel);
                BinaryPrimitives.WriteUInt16LittleEndian(b, sid); b[2] = 1;
                await joiner.SendRawAsync(Frame(up, b));
                var st = await ArrivesAsync(host, Protocol.SessionStart, 5);
                Assert.Equal(sid, BinaryPrimitives.ReadUInt16LittleEndian(st));
                return;
            }
            case Protocol.SessionEventUp:
            {
                ushort sid = await SessionAsync(host, joiner, InteractionKind.Duel);
                BinaryPrimitives.WriteUInt16LittleEndian(b, sid);
                await host.SendRawAsync(Frame(up, b));
                var ev = await ArrivesAsync(joiner, Protocol.SessionEventDown, 3);
                Assert.Equal(host.Id, ev[2]);
                return;
            }
            case Protocol.SessionLeave:
            {
                ushort sid = await InviteAsync(host, joiner, InteractionKind.Duel);
                BinaryPrimitives.WriteUInt16LittleEndian(b, sid); b[2] = (byte)SessionEndReason.Completed;
                await host.SendRawAsync(Frame(up, b));
                var end = await ArrivesAsync(joiner, Protocol.SessionEnd, 3);
                Assert.Equal(sid, BinaryPrimitives.ReadUInt16LittleEndian(end));
                return;
            }
            case Protocol.DiceIntent:
            {
                ushort sid = await SessionAsync(host, joiner, InteractionKind.Dice);
                BinaryPrimitives.WriteUInt16LittleEndian(b, sid); b[2] = (byte)DiceIntentType.Forfeit;
                await host.SendRawAsync(Frame(up, b));
                var end = await ArrivesAsync(joiner, Protocol.DiceEnd, 15);
                Assert.Equal(sid, BinaryPrimitives.ReadUInt16LittleEndian(end));
                return;
            }

            // ---- routed to the one player they name
            case Protocol.PlayerHitUp:   // the damage authority's NPC hit a player: the target id is dropped on the way down
                b[0] = joiner.Id;
                await host.SendRawAsync(Frame(up, b));
                await ArrivesAsync(joiner, Protocol.PlayerHitDown, Protocol.PlayerHitDownPayloadLen);
                return;
            case Protocol.PlayerHitV8Up:
                b[0] = joiner.Id;
                await host.SendRawAsync(Frame(up, b));
                await ArrivesAsync(joiner, Protocol.PlayerHitV8Down, PlayerHitV8.DownLen, host.Id);
                return;
        }

        // ---- the sender's facts, to every other player with the sender's id in front
        byte down;
        switch (up)
        {
            case Protocol.DamageUp: down = Protocol.DamageDown; break;
            case Protocol.DeathUp: down = Protocol.DeathDown; break;
            case Protocol.AppearanceUp: down = Protocol.AppearanceDown; break;              // [count 0]
            case Protocol.PauseUp: down = Protocol.PauseDown; break;
            case Protocol.PlayerStateUp: down = Protocol.PlayerStateDown; break;
            case Protocol.PlayerDeathUp: down = Protocol.PlayerDeathDown; break;            // no body at all
            case Protocol.NpcStateUp: b[0] = 1; b[1] = (byte)'n'; down = Protocol.NpcStateDown; break;
            case Protocol.TimeSkipUp: b[0] = Protocol.TimeSkipPhaseSync; down = Protocol.TimeSkipDown; break;
            case Protocol.HorseInfoUp: down = Protocol.HorseInfoDown; break;                // [nameLen 0] = on foot
            case Protocol.CombatEventUp: down = Protocol.CombatEventDown; break;
            case Protocol.WeatherUp: down = Protocol.WeatherDown; break;                    // [nameLen 0][blend:2]
            case Protocol.NpcDamageUp: b[0] = 1; b[1] = (byte)'n'; down = Protocol.NpcDamageDown; break;
            case Protocol.ItemDropUp: down = Protocol.ItemDropDown; break;
            case Protocol.ItemClaimUp: down = Protocol.ItemClaimDown; break;
            case Protocol.StoryBeatUp: down = Protocol.StoryBeatDown; break;                // [kind][len 0]
            case Protocol.ActionUp: b[0] = (byte)ActionKind.Jump; down = Protocol.ActionDown; break;   // the header only, len 0
            case Protocol.PlayerRespawnedUp: down = Protocol.PlayerRespawnedDown; break;
            case Protocol.GraveAddUp: down = Protocol.GraveAddDown; break;
            case Protocol.GraveRemoveUp: down = Protocol.GraveRemoveDown; break;
            case Protocol.DeathMarkerUp:
                b = DeathMarkerCodec.BuildUp(new(DeathMarkerAction.Add, DeathMarkerKind.MarkerOnly, 1, 1, 1, 0, 0, 0))[3..];
                Assert.Equal(len, b.Length);
                down = Protocol.DeathMarkerDown;
                break;
            case Protocol.WorldSavedUp: down = Protocol.WorldSavedDown; break;               // the damage authority's
            default:
                Assert.Fail($"RelayUp type {Hex(up)} has no probe in RelayTableGateTests.ProbeAsync -- add one with the row");
                return;
        }
        await host.SendRawAsync(Frame(up, b));
        await ArrivesAsync(joiner, down, 1 + len, host.Id);
    }

    private static async Task<ushort> InviteAsync(Peer host, Peer joiner, InteractionKind kind)
    {
        await host.SendRawAsync(Frame(Protocol.Invite, new[] { joiner.Id, (byte)kind }));
        var inv = await joiner.ReadUntilAsync(Protocol.InviteReceived, Wait);
        return BinaryPrimitives.ReadUInt16LittleEndian(inv);
    }

    private static async Task<ushort> SessionAsync(Peer host, Peer joiner, InteractionKind kind)
    {
        ushort sid = await InviteAsync(host, joiner, kind);
        var accept = new byte[3];
        BinaryPrimitives.WriteUInt16LittleEndian(accept, sid); accept[2] = 1;
        await joiner.SendRawAsync(Frame(Protocol.InviteResponse, accept));
        await host.ReadUntilAsync(Protocol.SessionStart, Wait);
        await joiner.ReadUntilAsync(Protocol.SessionStart, Wait);
        return sid;
    }

    // ---------------------------------------------------------------- wrong lengths of every other up type

    [Theory]
    [MemberData(nameof(RelayUpRows))]
    public async Task A_wrong_length_of_every_relay_up_row_is_counted_and_the_framing_survives(string name)
    {
        var row = Protocol.RelayUp.Single(r => r.Name == name);
        var bad = new List<int>();
        if (row.Min > 0) bad.Add(row.Min - 1);
        if (row.Max < Protocol.FramePayloadMax) bad.Add(row.Max + 1);
        if (row.Up == Protocol.Position) bad.AddRange(new[] { 20, 22, 26 });   // inside the range, not a length it takes (the 0.23.1 lesson)
        if (row.Up == Protocol.CombatEventUp) bad.Add(2);
        Assert.All(bad, len => Assert.False(Protocol.IsUpLenOk(row.Up, len)));
        if (bad.Count == 0) return;   // only a min-0 row with a u16 maximum has no wrong length

        var (host, joiner) = await HostAndJoinerAsync();
        await using var _h = host; await using var _j = joiner;
        string key = $"{Hex(row.Up)}:wrong-length";
        long before = Drops(key);
        foreach (int len in bad) await host.SendRawAsync(Frame(row.Up, new byte[len]));
        // the framing survives every one of them: the Ping behind them is answered
        var ping = new byte[Protocol.PingPayloadLen]; ping[0] = 0x77;
        await host.SendRawAsync(Frame(Protocol.Ping, ping));
        Assert.Equal(ping, await host.ReadUntilAsync(Protocol.Pong, Wait));
        Assert.Equal(before + bad.Count, Drops(key));
        foreach (int len in bad) Assert.True(Drops($"{key}:{len}") >= 1, $"{key}:{len}");
    }

    [Fact]
    public async Task An_unknown_type_a_down_type_and_a_second_handshake_are_counted_apart()
    {
        var (host, joiner) = await HostAndJoinerAsync();
        await using var _h = host; await using var _j = joiner;
        long unknown = Drops("0xEE:unknown-type"), ghost = Drops("0x02:unknown-type"), again = Drops("0x00:handshake-again");
        await host.SendRawAsync(Frame(0xEE, new byte[] { 1, 2, 3 }));                 // a type nobody defined (a newer peer; WO-208 took 0x7A/0x7B, WO-210 0x7E/0x7F)
        await host.SendRawAsync(Frame(Protocol.Ghost, new byte[Protocol.GhostPayloadLen]));   // a down type sent up
        await host.SendRawAsync(Frame(Protocol.Handshake, new byte[] { Protocol.Version, 0 }));
        var ping = new byte[Protocol.PingPayloadLen];
        await host.SendRawAsync(Frame(Protocol.Ping, ping));
        await host.ReadUntilAsync(Protocol.Pong, Wait);
        Assert.Equal(unknown + 1, Drops("0xEE:unknown-type"));
        Assert.Equal(ghost + 1, Drops("0x02:unknown-type"));
        Assert.Equal(again + 1, Drops("0x00:handshake-again"));
        Assert.Equal(0, Drops("0xEE:wrong-length") + Drops("0x02:wrong-length"));
    }

    [Fact]
    public async Task A_too_long_side_channel_frame_names_its_kind_in_the_drop_key()
    {
        var (host, joiner) = await HostAndJoinerAsync();
        await using var _h = host; await using var _j = joiner;
        var row = Protocol.JoinWireFor(Protocol.W164Up)!.Value;
        // a WO-201 speech line (kind 21) one byte past the row: the shape session C dropped 411 times, now with its kind and limit
        var body = new byte[row.Max + 1 - Protocol.JoinHeaderLen];
        body[0] = Protocol.W164SpeechLine;
        for (int i = Protocol.LootFixedLen; i < body.Length; i++) body[i] = (byte)'a';
        string key = $"0x74/k{Protocol.W164SpeechLine}:wrong-length:{row.Max + 1}>max{row.Max}";
        long before = Drops(key);
        await joiner.SendRawAsync(Protocol.BuildJoinUp(Protocol.W164Up, Protocol.JoinTargetHost, 0, body));
        var ok = new LootMsg(Protocol.W164SpeechLine, 1, "v1|@me|1000|0|Localization/dialog/x/y|hello");
        await joiner.SendRawAsync(ok.BuildUp(Protocol.W164Up, Protocol.JoinTargetHost));
        var d = await host.ReadUntilAsync(Protocol.W164Down, Wait);
        Assert.True(LootMsg.TryDecode(d.AsSpan(1 + Protocol.JoinHeaderLen), out var got));
        Assert.Equal(ok, got);
        Assert.Equal(before + 1, Drops(key));
        // a route refusal names the kind too (the host aiming one at itself: no target)
        long self = Drops("0x74/k21:no-target");
        await host.SendRawAsync(ok.BuildUp(Protocol.W164Up, host.Id));
        await host.SendRawAsync(Frame(Protocol.Ping, new byte[Protocol.PingPayloadLen]));
        await host.ReadUntilAsync(Protocol.Pong, Wait);
        Assert.Equal(self + 1, Drops("0x74/k21:no-target"));
    }

    [Fact]
    public async Task The_drops_that_were_silent_are_counted_with_their_reasons()
    {
        var (host, joiner) = await HostAndJoinerAsync();
        await using var _h = host; await using var _j = joiner;
        var keys = new[]
        {
            "0x1A:count-mismatch", "0x2E:namelen-mismatch", "0x30:namelen-mismatch", "0x37:len-mismatch", "0x3B:len-mismatch",
            "0x21:not-host", "0x46:not-host", "0x16:bad-intent:9",
        };
        var before = keys.ToDictionary(k => k, Drops);

        await host.SendRawAsync(Frame(Protocol.AppearanceUp, new byte[] { 2, 0 }));                       // count 2, no items
        await host.SendRawAsync(Frame(Protocol.WeatherUp, new byte[] { 5, 0, 0 }));                       // nameLen 5, no name
        var dmg = new byte[1 + 1 + Protocol.NpcDamageFixedTail]; dmg[0] = 3;                               // nameLen 3, one byte of name
        await host.SendRawAsync(Frame(Protocol.NpcDamageUp, dmg));
        await host.SendRawAsync(Frame(Protocol.StoryBeatUp, new byte[] { 1, 4, (byte)'a' }));            // len 4, one byte
        var act = new byte[Protocol.ActionUpHeaderLen + 4]; act[0] = (byte)ActionKind.Jump; act[Protocol.ActionUpHeaderLen - 1] = 9;   // claims 9, carries 4
        await host.SendRawAsync(Frame(Protocol.ActionUp, act));
        var hit = new byte[Protocol.PlayerHitUpPayloadLen]; hit[0] = host.Id;                             // the joiner is not the authority
        await joiner.SendRawAsync(Frame(Protocol.PlayerHitUp, hit));
        await joiner.SendRawAsync(Frame(Protocol.WorldSavedUp, new byte[Protocol.WorldSavedUpPayloadLen]));
        await joiner.SendRawAsync(Frame(Protocol.DiceIntent, new byte[] { 1, 0, 9 }));                    // intent 9 is no intent
        // both senders' frames are processed in order: a Ping behind each proves its drops were counted
        await host.SendRawAsync(Frame(Protocol.Ping, new byte[Protocol.PingPayloadLen]));
        await joiner.SendRawAsync(Frame(Protocol.Ping, new byte[Protocol.PingPayloadLen]));
        await host.ReadUntilAsync(Protocol.Pong, Wait);
        await joiner.ReadUntilAsync(Protocol.Pong, Wait);

        foreach (var k in keys) Assert.True(before[k] + 1 == Drops(k), $"{k}: {before[k]} -> {Drops(k)}");
    }

    // ---------------------------------------------------------------- (d) Act I's and Act II's channels carry their full texts

    [Theory]
    [InlineData(Protocol.ConvUp, Protocol.ConvDown, Protocol.ConvCam)]
    [InlineData(Protocol.W203Up, Protocol.W203Down, Protocol.W203NpcPose)]
    public async Task Conv_and_w203_frames_of_1400_bytes_cross_both_ways(byte up, byte down, byte kind)
    {
        var (host, joiner) = await HostAndJoinerAsync();
        await using var _h = host; await using var _j = joiner;
        var row = Protocol.JoinWireFor(up)!.Value;
        Assert.Equal(down, row.Down);
        Assert.Equal(Protocol.JoinFrom.Either, row.From);
        Assert.Equal(1400, row.Max - Protocol.JoinHeaderLen - Protocol.LootFixedLen);
        string type = Hex(up);
        long before = Drops(type);

        string text = "c1 " + new string('x', 1400 - 3);
        // the joiner's to the host (0xFF), the host's to the joiner (by id): either player can be the starter
        var fromJoiner = new LootMsg(kind, 7, text);
        await joiner.SendRawAsync(fromJoiner.BuildUp(up, Protocol.JoinTargetHost));
        var d1 = await ArrivesAsync(host, down, 1 + row.Max, joiner.Id);
        Assert.True(LootMsg.TryDecode(d1.AsSpan(1 + Protocol.JoinHeaderLen), out var got1));
        Assert.Equal(fromJoiner, got1);

        var fromHost = new LootMsg(kind, 8, text);
        await host.SendRawAsync(fromHost.BuildUp(up, joiner.Id));
        var d2 = await ArrivesAsync(joiner, down, 1 + row.Max, host.Id);
        Assert.True(LootMsg.TryDecode(d2.AsSpan(1 + Protocol.JoinHeaderLen), out var got2));
        Assert.Equal(fromHost, got2);

        Assert.Equal(before, Drops(type));
        // one byte more is refused before it is sent
        Assert.Throws<ArgumentOutOfRangeException>(() => new LootMsg(kind, 9, text + "x").BuildUp(up, joiner.Id));
    }

    [Fact]
    public async Task Copy_deaths_are_not_relayed_but_the_joiners_blow_still_reaches_the_host()
    {
        var (host, joiner) = await HostAndJoinerAsync();
        await using var _h = host; await using var _j = joiner;
        long stripped = Drops("0x30:fatal-not-host"), refused = Drops("0x14:not-host");
        var body = new byte[2 + Protocol.NpcDamageFixedTail];
        body[0] = 1; body[1] = (byte)'n';
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(2 + 4), 12f);
        body[^1] = Protocol.NpcDamageFlagFatal | Protocol.NpcDamageFlagAttributed;
        await joiner.SendRawAsync(Frame(Protocol.NpcDamageUp, body));
        var delivered = await ArrivesAsync(host, Protocol.NpcDamageDown, 1 + body.Length, joiner.Id);
        Assert.Equal(0, delivered[^1] & Protocol.NpcDamageFlagFatal);
        Assert.NotEqual(0, delivered[^1] & Protocol.NpcDamageFlagAttributed);
        Assert.Equal(12f, BitConverter.ToSingle(delivered, 1 + 2 + 4));
        await joiner.SendRawAsync(Frame(Protocol.DeathUp, new byte[Protocol.DeathUpPayloadLen]));
        await joiner.SendRawAsync(Frame(Protocol.Ping, new byte[Protocol.PingPayloadLen]));
        await joiner.ReadUntilAsync(Protocol.Pong, Wait);
        Assert.Equal(stripped + 1, Drops("0x30:fatal-not-host"));
        Assert.Equal(refused + 1, Drops("0x14:not-host"));
        // The host's identical death remains authoritative.
        await host.SendRawAsync(Frame(Protocol.NpcDamageUp, body));
        var fatal = await ArrivesAsync(joiner, Protocol.NpcDamageDown, 1 + body.Length, host.Id);
        Assert.NotEqual(0, fatal[^1] & Protocol.NpcDamageFlagFatal);
    }
    private static async Task OpenPresentationAsync(Peer peer, ushort capabilities = Protocol.LinkCapPresentationTime)
    {
        await peer.SendRawAsync(Protocol.BuildLinkHelloUp(capabilities));
        var reply = await ArrivesAsync(peer, Protocol.LinkHelloDown, Protocol.LinkHelloDownLen);
        Assert.True(Protocol.TryParseLinkHello(reply, out _, out ushort caps, out _, out _));
        Assert.Equal(capabilities, caps);
    }

    private static async Task CalibratePresentationAsync(Peer peer, long simulatedOffset)
    {
        var clock = new PresentationClockClient();
        long t0 = PresentationClock.NowMs() + simulatedOffset;
        await peer.SendRawAsync(clock.Begin(t0));
        byte[] reply = await ArrivesAsync(peer, Protocol.PresentationClockDown, PresentationClock.ReplyLength);
        var t1 = BinaryPrimitives.ReadInt64LittleEndian(reply.AsSpan(8));
        var t2 = BinaryPrimitives.ReadInt64LittleEndian(reply.AsSpan(16));
        // Model a symmetric local exchange with a known independent monotonic epoch.
        long t3 = t2 + simulatedOffset + (t1 + simulatedOffset - t0);
        Assert.True(clock.TryComplete(reply, t3, out var proof));
        await peer.SendRawAsync(proof);
        await peer.SendRawAsync(Frame(Protocol.Ping, new byte[Protocol.PingPayloadLen]));
        await ArrivesAsync(peer, Protocol.Pong, Protocol.PingPayloadLen);
    }

    [Fact]
    public async Task The_relay_authenticates_WO167_host_travel_edges_and_preserves_each_valid_direction()
    {
        var (host, joiner) = await HostAndJoinerAsync();
        await using var _h = host; await using var _j = joiner;
        await using var other = await Peer.ConnectAsync(_relay.TcpPort, "travel-listener");
        var start = new LootMsg(Protocol.W164Travel, 0, W164Text.TravelStart(7, "@Road"));
        var end = new LootMsg(Protocol.W164Travel, 0, W164Text.TravelEnd(7, 1200, 1300, 40, false));
        var refusal = new LootMsg(Protocol.W164Travel, 0, W164Text.TravelRefused(7, "off"));
        var request = new LootMsg(Protocol.W164Travel, 0, W164Text.TravelRequest(7, "@Road"));
        string notHost = $"{Hex(Protocol.W164Up)}/k{Protocol.W164Travel}:not-the-host";
        string fromHost = $"{Hex(Protocol.W164Up)}/k{Protocol.W164Travel}:from-the-host";
        long priorNonHost = Drops(notHost), priorHostRequest = Drops(fromHost);

        async Task Cross(Peer sender, Peer receiver, LootMsg message, byte target)
        {
            await sender.SendRawAsync(message.BuildUp(Protocol.W164Up, target));
            byte[] down = await receiver.ReadUntilAsync(Protocol.W164Down, Wait);
            Assert.Equal(sender.Id, down[0]);
            Assert.True(LootMsg.TryDecode(down.AsSpan(1 + Protocol.JoinHeaderLen), out var original));
            Assert.Equal(message, original);
        }

        await joiner.SendRawAsync(start.BuildUp(Protocol.W164Up, other.Id));
        await joiner.SendRawAsync(end.BuildUp(Protocol.W164Up, other.Id));
        await joiner.SendRawAsync(Frame(Protocol.Ping, new byte[Protocol.PingPayloadLen]));
        await ArrivesAsync(joiner, Protocol.Pong, Protocol.PingPayloadLen);
        Assert.Equal(priorNonHost + 2, Drops(notHost));
        await host.SendRawAsync(request.BuildUp(Protocol.W164Up, other.Id));
        await host.SendRawAsync(Frame(Protocol.Ping, new byte[Protocol.PingPayloadLen]));
        await ArrivesAsync(host, Protocol.Pong, Protocol.PingPayloadLen);
        Assert.Equal(priorHostRequest + 1, Drops(fromHost));

        await Cross(joiner, host, request, Protocol.JoinTargetHost);
        await Cross(host, other, start, other.Id);
        await Cross(host, other, end, other.Id);
        await Cross(host, other, refusal, other.Id);
    }

    private static byte[] TimedPosition(long sourceMs, bool hostClaim)
    {
        var body = new byte[Protocol.PositionPayloadLen + Protocol.SenderMsLen];
        body[16] = (byte)(Protocol.PositionFlagSenderMs | (hostClaim ? Protocol.PositionFlagHostClaim : 0));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(Protocol.PositionPayloadLen), unchecked((uint)sourceMs));
        return Frame(Protocol.Position, body);
    }

    private static byte[] TimedJump(long sourceMs)
    {
        var body = new byte[Protocol.ActionUpHeaderLen + Protocol.SenderMsLen];
        body[0] = (byte)ActionKind.Jump;
        body[3] = (byte)ActionPhase.Commit;
        body[Protocol.ActionUpHeaderLen - 1] = Protocol.SenderMsLen;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(Protocol.ActionUpHeaderLen), unchecked((uint)sourceMs));
        return Frame(Protocol.ActionUp, body);
    }

    private static async Task<(PresentationStamp Stamp, byte[] Original)> TimedDown(Peer to, byte wrapper, byte inner)
    {
        byte[] body = await to.ReadUntilAsync(wrapper, Wait);
        Assert.True(PresentationTimeWire.TryRead(wrapper, body, out var stamp, out byte type, out int offset));
        Assert.Equal(inner, type);
        return (stamp, body[offset..]);
    }

    private static async Task AnnounceWorldSyncAsync(Peer host, Peer joiner, uint world)
    {
        string text = FormattableString.Invariant($"v1|{world}|1|fixture");
        byte[] frame = new LootMsg(Protocol.W203Epoch, 0, text).BuildUp(Protocol.W203Up, joiner.Id);
        await host.SendRawAsync(frame);
        Assert.Equal(host.Id, (await joiner.ReadUntilAsync(Protocol.W203Down, Wait))[0]);
    }

    private static async Task<(uint Snapshot, Dictionary<WorldBaselineSection, byte[][]> Sections)> AcceptRecoveryBaselineAsync(
        Peer joiner, WorldSyncScope scope, bool acknowledge = true)
    {
        await joiner.SendRawAsync(WorldSyncWire.BuildSync(new(WorldSyncOperation.Open, scope, 0)));
        Assert.True(WorldSyncStatusWire.TryRead(await joiner.ReadUntilAsync(Protocol.WorldSyncStatusDown, Wait), out var status));
        Assert.Equal(scope, status.Scope);
        await joiner.SendRawAsync(WorldSyncWire.BuildSync(new(WorldSyncOperation.Ready, scope, 0)));
        var sections = new Dictionary<WorldBaselineSection, byte[][]>();
        int rowsReceived = 0;
        while (sections.Count < 6 || rowsReceived != sections.Values.Sum(rows => rows.Length))
        {
            Assert.True(WorldSyncWire.TryReadBaseline(await joiner.ReadUntilAsync(Protocol.WorldBaselineDown, Wait), out var part));
            Assert.Equal((scope, status.SnapshotId), (part.Scope, part.SnapshotId));
            if (!sections.TryGetValue(part.Section, out var rows)) sections.Add(part.Section, rows = new byte[part.Count][]);
            Assert.Null(rows[part.Index]);
            rows[part.Index] = part.Payload;
            rowsReceived++;
        }
        if (acknowledge) await joiner.SendRawAsync(WorldSyncWire.BuildSync(new(WorldSyncOperation.BaselineApplied, scope, status.SnapshotId)));
        return (status.SnapshotId, sections);
    }

    private static async Task<WorldRecoveryDigest> ReadWorldDigestAsync(Peer peer, WorldBaselineSection section, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            Assert.True(WorldRecoveryWire.TryReadDigest(await peer.ReadUntilAsync(Protocol.WorldDigestDown, deadline - DateTime.UtcNow), out var digest));
            if (digest.Section == section) return digest;
        }
        throw new TimeoutException($"No {section} digest");
    }

    [Fact]
    public async Task World_recovery_repairs_only_the_authenticated_area_without_touching_event_ids()
    {
        var (host, joiner) = await HostAndJoinerAsync();
        await using var _h = host;
        await using var _j = joiner;
        await OpenPresentationAsync(joiner, Protocol.LinkCapWorldSync | Protocol.LinkCapWorldRecovery);
        var scope = new WorldSyncScope(139, 15);
        await AnnounceWorldSyncAsync(host, joiner, scope.HostWorld);
        await host.SendRawAsync(ClaimPosition());
        await joiner.ReadUntilAsync(Protocol.Ghost, Wait);
        await host.SendRawAsync(Frame(Protocol.WeatherUp, [1, (byte)'a', 1, 0]));
        await joiner.ReadUntilAsync(Protocol.WeatherDown, Wait);

        var (snapshot, sections) = await AcceptRecoveryBaselineAsync(joiner, scope, acknowledge: false);
        Assert.NotEmpty(sections[WorldBaselineSection.Body][0]);
        Assert.NotEmpty(sections[WorldBaselineSection.Weather][0]);
        Assert.True(await joiner.NoneOfAsync(Protocol.WorldDigestDown, TimeSpan.FromMilliseconds(150)));
        await joiner.SendRawAsync(WorldSyncWire.BuildSync(new(WorldSyncOperation.BaselineApplied, scope, snapshot)));
        var target = await ReadWorldDigestAsync(joiner, WorldBaselineSection.Body, Wait);
        Assert.Equal((scope, snapshot), (target.Scope, target.SnapshotId));
        Assert.True(WorldRecoveryWire.TryComputeHash(scope, snapshot, target.Section, sections[target.Section], out var hash));
        Assert.Equal(hash, target.Hash);

        async Task<WorldEventFrame> SendResultAsync(uint token)
        {
            await host.SendRawAsync(new LootMsg(Protocol.LootHostItemResult, token, "unknown").BuildUp(Protocol.LootHostUp, joiner.Id));
            Assert.True(WorldSyncWire.TryReadEvent(await joiner.ReadUntilAsync(Protocol.WorldEventDown, Wait), out var item));
            return item;
        }

        var first = await SendResultAsync(99);
        Assert.Equal(1ul, first.Id);
        await joiner.SendRawAsync(WorldSyncWire.BuildSync(new(WorldSyncOperation.EventApplied, scope, snapshot, first.Channel, first.Id)));
        string invalid = $"{Hex(Protocol.WorldRepairUp)}:invalid-world-repair";
        long dropped = Drops(invalid);
        await host.SendRawAsync(WorldRecoveryWire.BuildRequest(new(target))); // an unrelated link cannot request this repair
        await host.SendRawAsync(Frame(Protocol.Ping, new byte[Protocol.PingPayloadLen]));
        await host.ReadUntilAsync(Protocol.Pong, Wait);
        await joiner.SendRawAsync(WorldRecoveryWire.BuildRequest(new(target with { SnapshotId = snapshot + 1 })));
        await joiner.SendRawAsync(WorldRecoveryWire.BuildRequest(new(target with { Hash = hash with { A = hash.A ^ 1 } })));
        await joiner.SendRawAsync(Frame(Protocol.Ping, new byte[Protocol.PingPayloadLen]));
        await joiner.ReadUntilAsync(Protocol.Pong, Wait);
        Assert.Equal(dropped + 3, Drops(invalid));
        Assert.True(await joiner.NoneOfAsync(Protocol.WorldRepairDown, TimeSpan.FromMilliseconds(80)));

        await joiner.SendRawAsync(WorldRecoveryWire.BuildRequest(new(target)));
        await joiner.SendRawAsync(WorldRecoveryWire.BuildRequest(new(target))); // same request within the throttle window
        foreach (var row in sections[target.Section])
        {
            Assert.True(WorldRecoveryWire.TryReadChunk(await joiner.ReadUntilAsync(Protocol.WorldRepairDown, Wait), out var chunk));
            Assert.Equal(target, chunk.Digest);
            Assert.Equal(row, chunk.Payload);
        }
        Assert.True(await joiner.NoneOfAsync(Protocol.WorldRepairDown, TimeSpan.FromMilliseconds(150)));
        var second = await SendResultAsync(100);
        Assert.Equal((scope, snapshot, first.Channel, 2ul), (second.Scope, second.SnapshotId, second.Channel, second.Id));
        await joiner.SendRawAsync(WorldSyncWire.BuildSync(new(WorldSyncOperation.EventApplied, scope, snapshot, second.Channel, second.Id)));

        // A newer ordinary movement observation stays live without rewriting
        // the immutable, already-acknowledged comparison anchor.
        await host.SendRawAsync(PositionCodec.BuildPosition(95f, 96f, 97f, 0f, false, false, null));
        Assert.Equal(95f, BitConverter.ToSingle(await joiner.ReadUntilAsync(Protocol.Ghost, Wait), 1));
        Assert.Equal(target, await ReadWorldDigestAsync(joiner, target.Section, TimeSpan.FromSeconds(12)));

        // A deliberate mismatch may ask for a fresh absolute body sample.
        // Its new digest precedes the new rows and only this section moves.
        await host.SendRawAsync(PositionCodec.BuildPosition(105f, 106f, 107f, 0f, false, false, null));
        var currentBody = await joiner.ReadUntilAsync(Protocol.Ghost, Wait);
        await joiner.SendRawAsync(WorldRecoveryWire.BuildRequest(new(target)));
        var current = await ReadWorldDigestAsync(joiner, target.Section, Wait);
        Assert.Equal((scope, snapshot, target.Section, target.Revision + 1), (current.Scope, current.SnapshotId, current.Section, current.Revision));
        Assert.NotEqual(target.Hash, current.Hash);
        Assert.True(WorldRecoveryWire.TryReadChunk(await joiner.ReadUntilAsync(Protocol.WorldRepairDown, Wait), out var latest));
        Assert.Equal(current, latest.Digest);
        Assert.Equal(new byte[] { Protocol.Ghost }.Concat(currentBody), latest.Payload);
        Assert.True(WorldRecoveryWire.TryComputeHash(scope, snapshot, target.Section, [latest.Payload], out var latestHash));
        Assert.Equal(current.Hash, latestHash);

        await Task.Delay(370);
        await host.SendRawAsync(PositionCodec.BuildPosition(1f, 2f, 3f, 0f, false, true, null));
        await joiner.ReadUntilAsync(Protocol.Ghost, Wait);
        await joiner.SendRawAsync(WorldRecoveryWire.BuildRequest(new(current)));
        Assert.True(await joiner.NoneOfAsync(Protocol.WorldRepairDown, TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task World_recovery_capability_depends_on_world_sync()
    {
        await using var peer = await Peer.ConnectAsync(_relay.TcpPort, "recovery-only");
        await peer.SendRawAsync(Protocol.BuildLinkHelloUp(Protocol.LinkCapWorldRecovery));
        var reply = await peer.ReadUntilAsync(Protocol.LinkHelloDown, Wait);
        Assert.True(Protocol.TryParseLinkHello(reply, out _, out ushort caps, out _, out _));
        Assert.Equal(0, caps);
    }

    [Fact]
    public async Task World_recovery_clock_uses_running_samples_and_weather_applies_the_latest_profile_last()
    {
        var (host, joiner) = await HostAndJoinerAsync();
        await using var _h = host;
        await using var _j = joiner;
        await OpenPresentationAsync(joiner, Protocol.LinkCapWorldSync | Protocol.LinkCapWorldRecovery);
        var scope = new WorldSyncScope(141, 19);
        await AnnounceWorldSyncAsync(host, joiner, scope.HostWorld);
        byte[] Weather(char profile) => Frame(Protocol.WeatherUp, [1, (byte)profile, 1, 0]);
        byte[] Clock(string sample) => new LootMsg(Protocol.W164Clock, 0, sample).BuildUp(Protocol.W164Up, joiner.Id);
        await host.SendRawAsync(Clock("b1 1000 0 0 0 47")); // frozen join bootstrap
        await joiner.ReadUntilAsync(Protocol.W164Down, Wait);
        await host.SendRawAsync(Weather('a'));
        await joiner.ReadUntilAsync(Protocol.WeatherDown, Wait);
        await AcceptRecoveryBaselineAsync(joiner, scope);
        var clock = await ReadWorldDigestAsync(joiner, WorldBaselineSection.Clock, Wait);
        var weather = await ReadWorldDigestAsync(joiner, WorldBaselineSection.Weather, Wait);

        await joiner.SendRawAsync(WorldRecoveryWire.BuildRequest(new(clock)));
        Assert.True(await joiner.NoneOfAsync(Protocol.WorldRepairDown, TimeSpan.FromMilliseconds(360)));
        await host.SendRawAsync(Clock("b1 4000 15000 0 0 0"));
        var liveClock = await joiner.ReadUntilAsync(Protocol.W164Down, Wait);
        await joiner.SendRawAsync(WorldRecoveryWire.BuildRequest(new(clock)));
        var clockRevision = await ReadWorldDigestAsync(joiner, clock.Section, Wait);
        Assert.Equal(clock.Revision + 1, clockRevision.Revision);
        Assert.True(WorldRecoveryWire.TryReadChunk(await joiner.ReadUntilAsync(Protocol.WorldRepairDown, Wait), out var clockRepair));
        Assert.Equal(clockRevision, clockRepair.Digest);
        Assert.Equal(new byte[] { Protocol.W164Down }.Concat(liveClock), clockRepair.Payload);

        await host.SendRawAsync(new LootMsg(Protocol.W164Weather, 0, "0.80 b").BuildUp(Protocol.W164Up, joiner.Id));
        await joiner.ReadUntilAsync(Protocol.W164Down, Wait);
        await host.SendRawAsync(Weather('c'));
        var profile = await joiner.ReadUntilAsync(Protocol.WeatherDown, Wait);
        await joiner.SendRawAsync(WorldRecoveryWire.BuildRequest(new(weather)));
        var weatherRevision = await ReadWorldDigestAsync(joiner, weather.Section, Wait);
        Assert.Equal(weather.Revision + 1, weatherRevision.Revision);
        Assert.True(WorldRecoveryWire.TryReadChunk(await joiner.ReadUntilAsync(Protocol.WorldRepairDown, Wait), out var rainRepair));
        Assert.True(WorldRecoveryWire.TryReadChunk(await joiner.ReadUntilAsync(Protocol.WorldRepairDown, Wait), out var profileRepair));
        Assert.Equal((weatherRevision, (ushort)0, (ushort)2), (rainRepair.Digest, rainRepair.Index, rainRepair.Count));
        Assert.Equal((weatherRevision, (ushort)1, (ushort)2), (profileRepair.Digest, profileRepair.Index, profileRepair.Count));
        Assert.Equal(Protocol.W164Down, rainRepair.Payload[0]);
        Assert.True(LootMsg.TryDecode(rainRepair.Payload.AsSpan(2 + Protocol.JoinHeaderLen), out var rain));
        Assert.Equal("0.8 -", rain.Text);
        Assert.Equal(new byte[] { Protocol.WeatherDown }.Concat(profile), profileRepair.Payload);
    }

    [Fact]
    public async Task World_epoch_announcements_and_acknowledgements_have_distinct_authority()
    {
        var (host, joiner) = await HostAndJoinerAsync();
        await using var _h = host;
        await using var _j = joiner;
        string key = Hex(Protocol.W203Up);
        long dropped = Drops(key);
        byte[] Epoch(string text, byte target) => new LootMsg(Protocol.W203Epoch, 0, text).BuildUp(Protocol.W203Up, target);
        const string latest = "v1|142|2|resume|r1|452560e1066a4794a05071e8c539ecf4|81";
        await joiner.SendRawAsync(Epoch(latest, host.Id));
        await joiner.SendRawAsync(Epoch("v1|142|-1|ack", host.Id));
        await joiner.SendRawAsync(Epoch("v1|142|2|ack|r1", host.Id));
        await joiner.SendRawAsync(Frame(Protocol.Ping, new byte[Protocol.PingPayloadLen]));
        await joiner.ReadUntilAsync(Protocol.Pong, Wait);
        await host.SendRawAsync(Epoch("v1|142|2|ack", joiner.Id));
        await host.SendRawAsync(Epoch("v1|142|2|resume|r1|malformed|81", joiner.Id));
        await host.SendRawAsync(Frame(Protocol.Ping, new byte[Protocol.PingPayloadLen]));
        await host.ReadUntilAsync(Protocol.Pong, Wait);
        Assert.Equal(dropped + 5, Drops(key));
        Assert.True(await host.NoneOfAsync(Protocol.W203Down, TimeSpan.FromMilliseconds(80)));
        Assert.True(await joiner.NoneOfAsync(Protocol.W203Down, TimeSpan.FromMilliseconds(80)));

        await host.SendRawAsync(Epoch(latest, joiner.Id));
        Assert.Equal(host.Id, (await joiner.ReadUntilAsync(Protocol.W203Down, Wait))[0]);
        await joiner.SendRawAsync(Epoch("v1|142|2|ack", Protocol.JoinTargetHost));
        Assert.Equal(joiner.Id, (await host.ReadUntilAsync(Protocol.W203Down, Wait))[0]);
        Assert.Equal(dropped + 5, Drops(key));
    }

    [Fact]
    public async Task World_sync_keeps_more_than_512_ordered_results_and_waits_for_applied_acknowledgements()
    {
        var (host, joiner) = await HostAndJoinerAsync();
        await using var _h = host;
        await using var _j = joiner;
        await OpenPresentationAsync(joiner, Protocol.LinkCapWorldSync);
        var scope = new WorldSyncScope(77, 85);
        await AnnounceWorldSyncAsync(host, joiner, scope.HostWorld);
        await host.SendRawAsync(new LootMsg(Protocol.LootHostItemResult, 1, "unknown").BuildUp(Protocol.LootHostUp, joiner.Id));
        await joiner.ReadUntilAsync(Protocol.LootHostDown, Wait);
        await joiner.SendRawAsync(WorldSyncWire.BuildSync(new(WorldSyncOperation.Open, scope, 0, 0, 0)));
        var rawStatus = await joiner.ReadUntilAsync(Protocol.WorldSyncStatusDown, Wait);
        Assert.True(WorldSyncStatusWire.TryRead(rawStatus, out var status));
        Assert.Equal(scope, status.Scope);
        Assert.Equal(joiner.Id, status.Recipient);
        uint snapshot = status.SnapshotId;
        string invalid = $"{Hex(Protocol.WorldSyncUp)}:invalid-world-sync";
        long before = Drops(invalid);

        const int count = 650;
        for (uint token = 2; token <= count; token++)
            await host.SendRawAsync(new LootMsg(Protocol.LootHostItemResult, token, "unknown").BuildUp(Protocol.LootHostUp, joiner.Id));
        await host.SendRawAsync(Frame(Protocol.Ping, new byte[Protocol.PingPayloadLen]));
        await ArrivesAsync(host, Protocol.Pong, Protocol.PingPayloadLen);
        await joiner.SendRawAsync(WorldSyncWire.BuildSync(new(WorldSyncOperation.EventApplied, scope, snapshot, WorldEventChannel.Loot, 1)));
        await joiner.SendRawAsync(Frame(Protocol.Ping, new byte[Protocol.PingPayloadLen]));
        await ArrivesAsync(joiner, Protocol.Pong, Protocol.PingPayloadLen);
        Assert.Equal(before + 1, Drops(invalid));

        await joiner.SendRawAsync(WorldSyncWire.BuildSync(new(WorldSyncOperation.Ready, scope, 0, 0, 0)));
        var sections = new HashSet<WorldBaselineSection>();
        while (sections.Count < 6)
        {
            byte[] bytes = await joiner.ReadUntilAsync(Protocol.WorldBaselineDown, Wait);
            Assert.True(WorldSyncWire.TryReadBaseline(bytes, out var part));
            Assert.Equal((scope, snapshot, (ushort)1), (part.Scope, part.SnapshotId, part.Version));
            sections.Add(part.Section);
        }
        await joiner.SendRawAsync(WorldSyncWire.BuildSync(new(WorldSyncOperation.BaselineApplied, scope, snapshot, 0, 0)));

        var arrived = new HashSet<ulong>();
        bool retriedFirst = false;
        ulong contiguous = 0;
        while (contiguous < count)
        {
            byte[] bytes = await joiner.ReadUntilAsync(Protocol.WorldEventDown, Wait);
            Assert.True(WorldSyncWire.TryReadEvent(bytes, out var item));
            Assert.Equal((scope, snapshot, Protocol.LootHostDown, WorldEventChannel.Loot), (item.Scope, item.SnapshotId, item.Type, item.Channel));
            Assert.Equal(host.Id, item.Payload[0]);
            Assert.True(LootMsg.TryDecode(item.Payload.AsSpan(1 + Protocol.JoinHeaderLen), out var msg));
            Assert.Equal(item.Id, (ulong)msg.Tok);
            if (!arrived.Add(item.Id) && item.Id == 1) retriedFirst = true;
            // Make the relay retry the first result. Thereafter a cumulative
            // receipt may retire only the contiguous applied prefix.
            if (!retriedFirst) continue;
            ulong prior = contiguous;
            while (arrived.Contains(contiguous + 1)) contiguous++;
            if (contiguous != prior)
                await joiner.SendRawAsync(WorldSyncWire.BuildSync(new(WorldSyncOperation.EventApplied, scope, snapshot, WorldEventChannel.Loot, contiguous)));
        }
        Assert.True(retriedFirst);
        Assert.Equal(count, arrived.Count);
        using var deadline = new CancellationTokenSource(Wait);
        do
        {
            Assert.True(WorldSyncStatusWire.TryRead(await joiner.ReadUntilAsync(Protocol.WorldSyncStatusDown, Wait), out status));
            deadline.Token.ThrowIfCancellationRequested();
        }
        while (status.Counts.Acked != count);
        Assert.Equal(count, status.Counts.Sent);
        Assert.True(status.Counts.Retx > 0);
        Assert.Equal(0, status.Counts.Dropped);
    }

    [Fact]
    public async Task Combat_stamps_preserve_original_vitals_and_ranged_step_with_capability_and_authority_checks()
    {
        var (host, joiner) = await HostAndJoinerAsync();
        await using var _h = host; await using var _j = joiner;
        await using var older = await Peer.ConnectAsync(_relay.TcpPort, "timeline-only-listener");
        const ushort both = Protocol.LinkCapPresentationTime | Protocol.LinkCapPresentationCombat;
        await OpenPresentationAsync(host, both);
        await OpenPresentationAsync(joiner, both);
        await OpenPresentationAsync(older);
        await CalibratePresentationAsync(host, 80_000);
        await CalibratePresentationAsync(joiner, -12_000);
        long captured = PresentationClock.NowMs();
        var vitals = new byte[Protocol.PlayerStateUpPayloadLen];
        BitConverter.TryWriteBytes(vitals, 65f);
        vitals[^1] = Protocol.PlayerStateFlagKnockedDown;
        await host.SendRawAsync(PresentationTimeWire.StampCombatUp(Frame(Protocol.PlayerStateUp, vitals),
            unchecked((uint)(captured + 80_000))));
        var timed = await TimedDown(joiner, Protocol.PresentationActionDown, Protocol.PlayerStateDown);
        Assert.Equal((ulong)(captured + 80_000), timed.Stamp.HostMs);
        byte[] expectedVitals = [host.Id, .. vitals];
        Assert.Equal(expectedVitals, timed.Original);
        Assert.Equal(expectedVitals, await ArrivesAsync(older, Protocol.PlayerStateDown, expectedVitals.Length, host.Id));

        foreach (var (target, kind) in new[] { (host, RangedWeaponKind.Bow), (older, RangedWeaponKind.Crossbow) })
        {
            var step = new RangedStepV2(1, 2, 3, 4, 3, kind, Guid.Parse("8e9283fb-0306-4879-a64d-cb7d2aab8000"), 11, 1);
            byte[] original = new LootMsg(Protocol.W164RangedStep, 0, step.Encode()).BuildUp(Protocol.W164Up, target.Id);
            byte[] expected = [joiner.Id, .. original.AsSpan(3)];
            await joiner.SendRawAsync(PresentationTimeWire.StampCombatUp(original, unchecked((uint)(captured - 12_000))));
            if (target == host)
            {
                var ranged = await TimedDown(host, Protocol.PresentationActionDown, Protocol.W164Down);
                Assert.Equal(timed.Stamp, ranged.Stamp);
                Assert.Equal(expected, ranged.Original);
            }
            else Assert.Equal(expected, await ArrivesAsync(older, Protocol.W164Down, expected.Length, joiner.Id));
        }

        string malformed = $"{Hex(Protocol.PresentationCombatUp)}:invalid-combat-stamp";
        long prior = Drops(malformed);
        byte[] hit = new byte[PlayerHitV8.UpLen];
        hit[0] = host.Id;
        var wrappedHit = new byte[PresentationTimeWire.SubjectStampLength + hit.Length];
        wrappedHit[0] = Protocol.PlayerHitV8Up;
        BinaryPrimitives.WriteUInt32LittleEndian(wrappedHit.AsSpan(1), 1);
        hit.CopyTo(wrappedHit.AsSpan(PresentationTimeWire.SubjectStampLength));
        await joiner.SendRawAsync(Frame(Protocol.PresentationCombatUp, wrappedHit));
        await joiner.SendRawAsync(Frame(Protocol.Ping, new byte[Protocol.PingPayloadLen]));
        await ArrivesAsync(joiner, Protocol.Pong, Protocol.PingPayloadLen);
        await older.SendRawAsync(PresentationTimeWire.StampCombatUp(Frame(Protocol.PlayerStateUp, vitals), 1));
        await older.SendRawAsync(Frame(Protocol.Ping, new byte[Protocol.PingPayloadLen]));
        await ArrivesAsync(older, Protocol.Pong, Protocol.PingPayloadLen);
        Assert.Equal(prior + 2, Drops(malformed));
    }

    [Fact]
    public async Task Both_roles_share_the_host_clock_and_preserve_legacy_receivers_and_action_phase()
    {
        var (host, joiner) = await HostAndJoinerAsync();
        await using var _h = host; await using var _j = joiner;
        await using var legacy = await Peer.ConnectAsync(_relay.TcpPort, "legacy-listener");
        const long hostOffset = (1L << 34) + 90_000, joinerOffset = (1L << 33) + 20_000;
        await OpenPresentationAsync(host);
        await OpenPresentationAsync(joiner);
        long now = PresentationClock.NowMs();
        await host.SendRawAsync(TimedPosition(now + hostOffset, hostClaim: true));
        await ArrivesAsync(joiner, Protocol.Ghost, Protocol.GhostPayloadLen + Protocol.SenderMsLen, host.Id);
        await ArrivesAsync(legacy, Protocol.Ghost, Protocol.GhostPayloadLen + Protocol.SenderMsLen, host.Id);

        await CalibratePresentationAsync(host, hostOffset);
        await joiner.SendRawAsync(TimedPosition(now + joinerOffset, hostClaim: false));
        await ArrivesAsync(host, Protocol.Ghost, Protocol.GhostPayloadLen + Protocol.SenderMsLen, joiner.Id);
        await ArrivesAsync(legacy, Protocol.Ghost, Protocol.GhostPayloadLen + Protocol.SenderMsLen, joiner.Id);
        await CalibratePresentationAsync(joiner, joinerOffset);

        now = PresentationClock.NowMs();
        long hostTime = now + hostOffset, joinerTime = now + joinerOffset;
        byte[] hostPosition = TimedPosition(hostTime, hostClaim: true);
        await host.SendRawAsync(hostPosition);
        var first = await TimedDown(joiner, Protocol.PresentationPoseDown, Protocol.Ghost);
        Assert.Equal((ulong)hostTime, first.Stamp.HostMs);
        Assert.Equal(hostOffset, first.Stamp.RelayToHostMs);
        byte[] hostGhost = [host.Id, .. hostPosition.AsSpan(3)];
        hostGhost[17] &= unchecked((byte)~Protocol.PositionFlagHostClaim);
        Assert.Equal(hostGhost, first.Original);
        Assert.Equal(hostGhost, await ArrivesAsync(legacy, Protocol.Ghost, hostGhost.Length, host.Id));

        byte[] joinerPosition = TimedPosition(joinerTime, hostClaim: false);
        await joiner.SendRawAsync(joinerPosition);
        var fromJoiner = await TimedDown(host, Protocol.PresentationPoseDown, Protocol.Ghost);
        Assert.Equal(first.Stamp, fromJoiner.Stamp);
        byte[] joinerGhost = [joiner.Id, .. joinerPosition.AsSpan(3)];
        Assert.Equal(joinerGhost, fromJoiner.Original);

        foreach (var (source, target, captured) in new[] { (host, joiner, hostTime), (joiner, host, joinerTime) })
        {
            byte[] action = TimedJump(captured);
            await source.SendRawAsync(action);
            var timedAction = await TimedDown(target, Protocol.PresentationActionDown, Protocol.ActionDown);
            Assert.Equal(first.Stamp, timedAction.Stamp);
            byte[] original = [source.Id, .. action.AsSpan(3)];
            Assert.Equal(original, timedAction.Original);
            Assert.Equal((byte)ActionPhase.Commit, timedAction.Original[4]);
        }

        byte[] npc = NpcCompact.BuildDown(host.Id, "timeline_npc", new(1, 2, 3, 0, 100, 0, 1, unchecked((uint)hostTime)));
        await host.SendRawAsync(Frame(Protocol.NpcStateUp, npc[1..]));
        var timedNpc = await TimedDown(joiner, Protocol.PresentationPoseDown, Protocol.NpcStateDown);
        Assert.Equal(first.Stamp, timedNpc.Stamp);
        Assert.Equal(npc, timedNpc.Original);

        await host.SendRawAsync(TimedPosition(hostTime, hostClaim: false));
        var relinquished = await TimedDown(joiner, Protocol.PresentationPoseDown, Protocol.Ghost);
        Assert.Equal(first.Stamp, relinquished.Stamp);
        await joiner.SendRawAsync(TimedPosition(joinerTime, hostClaim: true));
        var changed = await TimedDown(host, Protocol.PresentationPoseDown, Protocol.Ghost);
        Assert.True(changed.Stamp.Epoch > first.Stamp.Epoch);
        Assert.Equal((ulong)joinerTime, changed.Stamp.HostMs);
        Assert.Equal(joinerOffset, changed.Stamp.RelayToHostMs);
    }
}
