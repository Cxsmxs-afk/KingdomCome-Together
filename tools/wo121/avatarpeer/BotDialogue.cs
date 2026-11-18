// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Net.Sockets;
using KcdMp.Client;
using KcdMp.Wire;

static partial class P
{
    private readonly record struct DialogVotes(int Cid, int Decision, int First, int Second);

    private sealed class BotDialogue(byte host, byte target)
    {
        private int _cid = -1, _closedCid = -1;
        private int _latestDecision, _closedDecision;
        private uint _sequence;
        private string _anchor = "";
        private bool _listening, _offeredVotes;
        private DialogVotes? _votes;

        public (byte[]? Join, DialogVotes? Votes) Observe(byte type, byte[] payload)
        {
            if (_offeredVotes || type != Protocol.ConvDown ||
                !Protocol.TrySplitJoinDown(payload, out byte source, out byte recipient, out uint join, out var body) ||
                source != host || recipient != target || join != 0 || !LootMsg.TryDecode(body, out var message)) return default;
            switch (message.Kind)
            {
                case Protocol.ConvAnnounce when Wo203CamRules.TryParseAnnounce(message.Text, out int offer, out _, out bool joinable, out var anchor):
                    if (!joinable || offer == _cid || offer == _closedCid) return default;
                    _cid = offer;
                    _anchor = anchor;
                    _listening = false;
                    _votes = null;
                    _latestDecision = _closedDecision = 0;
                    return (Frame(Protocol.ConvJoin, Wo203CamRules.JoinText(offer, false)), null);
                case Protocol.ConvSnapshot when Wo203CamRules.TryParseSnapshot(message.Text, out int snapshot, out var bound):
                    if (snapshot == _cid && string.Equals(bound, _anchor, StringComparison.Ordinal)) _listening = true;
                    break;
                case Protocol.ConvOptions when Wo204Rules.TryParseOptions(message.Text, out int options, out int decision, out var rows):
                    if (options != _cid || decision <= _closedDecision || decision < _latestDecision) return default;
                    _latestDecision = decision;
                    int[] choices = rows.Where(row => row.Index >= 0 && row.Index < rows.Count)
                        .OrderBy(row => row.Used).Select(row => row.Index).Distinct().Take(2).ToArray();
                    _votes = choices.Length == 2 ? new DialogVotes(options, decision, choices[0], choices[1]) : null;
                    break;
                case Protocol.ConvChosen when Wo204Rules.TryParseVote(message.Text, out int chosen, out int closed, out int index):
                    if (chosen == _cid && index >= 0)
                    {
                        _closedDecision = Math.Max(_closedDecision, closed);
                        if (_votes is { } pair && pair.Decision <= _closedDecision) _votes = null;
                    }
                    break;
                case Protocol.ConvEnd when Wo203CamRules.TryParseReason(message.Text, out int ended, out _):
                    if (ended == _cid)
                    {
                        _closedCid = ended;
                        _cid = -1;
                        _listening = false;
                        _votes = null;
                    }
                    break;
            }
            if (!_listening || _votes is null) return default;
            _offeredVotes = true;
            return (null, _votes);
        }

        private byte[] Frame(byte kind, string text) =>
            new LootMsg(kind, ++_sequence, text).BuildUp(Protocol.ConvUp, Protocol.JoinTargetHost);

        public (byte[] Packet, uint Token) Vote(DialogVotes pair, int index)
        {
            byte[] packet = Frame(Protocol.ConvVote, Wo204Rules.VoteText(pair.Cid, pair.Decision, index));
            return (packet, _sequence);
        }
    }

    private static byte[] BotDialogPing()
    {
        byte[] ping = new byte[3 + Protocol.PingPayloadLen];
        ping[0] = Protocol.Ping;
        BinaryPrimitives.WriteUInt16LittleEndian(ping.AsSpan(1), Protocol.PingPayloadLen);
        BinaryPrimitives.WriteInt64LittleEndian(ping.AsSpan(3), Environment.TickCount64);
        return ping;
    }

    private static async Task<int> RunBotDialogueAsync(NetworkStream stream, byte myId, BotContext context, CancellationToken token)
    {
        var dialogue = new BotDialogue(context.Host, myId);
        bool sent = false;
        using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(2));
        var tick = heartbeat.WaitForNextTickAsync(token).AsTask();
        var read = ReadPacket(stream, token);
        try
        {
            while (true)
            {
                if (await Task.WhenAny(read, tick) == tick)
                {
                    if (!await tick) return sent ? 0 : 1;
                    await Send(stream, BotDialogPing(), token);
                    tick = heartbeat.WaitForNextTickAsync(token).AsTask();
                    continue;
                }
                var (type, payload) = await read;
                if ((type == Protocol.CombatRole && payload is [var role] && role != 0) ||
                    (type == Protocol.Disconnect && payload is [var left] && left == context.Host))
                    throw new IOException("The T4 dialogue host left.");
                if (Wo206Rules.TryReadEpochFrame(type, payload, out byte source, out uint world, out uint join, out string why) &&
                    source == context.Host && why != Wo206Rules.Ack)
                {
                    if (world != context.World) throw new IOException("The T4 dialogue host changed world.");
                    await Send(stream, BotEpochAck(world, join), token);
                }
                var (request, votes) = dialogue.Observe(type, payload);
                if (request is not null) await Send(stream, request, token);
                if (votes is { } pair)
                {
                    var first = dialogue.Vote(pair, pair.First);
                    await Send(stream, first.Packet, token);
                    var second = dialogue.Vote(pair, pair.Second);
                    await Send(stream, second.Packet, token);
                    sent = true;
                    Console.WriteLine(FormattableString.Invariant(
                        $"BOT-T4 role=joiner dialog_votes=sent host={context.Host} cid={pair.Cid} decision={pair.Decision} first={pair.First} second={pair.Second} peer={myId} first_token={first.Token} second_token={second.Token}"));
                    Console.WriteLine("BOT-T4 role=joiner complete=1 mode=dialog-votes");
                }
                read = ReadPacket(stream, token);
            }
        }
        catch (OperationCanceledException) when (sent && token.IsCancellationRequested)
        {
            return 0;
        }
    }

    private static void TestBotDialogue()
    {
        const byte host = 0, target = 1;
        const int cid = 23;
        const string anchor = "test_npc";
        var listener = new BotDialogue(host, target);
        byte[] From(byte source, byte to, byte kind, string text) =>
            [source, .. new LootMsg(kind, 5, text).BuildUp(Protocol.ConvUp, to).AsSpan(3)];
        string announce = Wo203CamRules.AnnounceText(cid, "normal", true, anchor, [anchor]);
        string snapshot = Wo203CamRules.SnapshotText(cid, "normal", anchor, [anchor]);
        Wo204Rules.OptRow[] rows = [new(0, false, "", 0, 0, "First"), new(1, false, "", 0, 0, "Second")];
        var options = Wo204Rules.OptionsText(cid, 2, 0, rows);
        if (listener.Observe(Protocol.ConvDown, From(host, target, Protocol.ConvOptions, options)) != default ||
            listener.Observe(Protocol.ConvDown, From(2, target, Protocol.ConvAnnounce, announce)) != default ||
            listener.Observe(Protocol.ConvDown, From(host, 2, Protocol.ConvAnnounce, announce)) != default)
            throw new InvalidOperationException("Dialogue ignored its authenticated source or live offer scope.");
        var join = listener.Observe(Protocol.ConvDown, From(host, target, Protocol.ConvAnnounce, announce));
        if (join.Join is null || join.Votes is not null || join.Join[0] != Protocol.ConvUp || join.Join[3] != Protocol.JoinTargetHost ||
            !LootMsg.TryDecode(join.Join.AsSpan(3 + Protocol.JoinHeaderLen), out var request) || request.Kind != Protocol.ConvJoin ||
            request.Text != Wo203CamRules.JoinText(cid, false)) throw new InvalidOperationException("Dialogue did not ask to join its live CID.");

        if (listener.Observe(Protocol.ConvDown, From(host, target, Protocol.ConvOptions, options)) != default ||
            listener.Observe(Protocol.ConvDown, From(host, target, Protocol.ConvChosen, Wo204Rules.ChosenText(cid, 2, 1))) != default ||
            listener.Observe(Protocol.ConvDown, From(host, target, Protocol.ConvOptions, options)) != default ||
            listener.Observe(Protocol.ConvDown, From(host, target, Protocol.ConvSnapshot, snapshot)) != default)
            throw new InvalidOperationException("Dialogue voted before binding the offer or after closing the decision.");

        var current = Wo204Rules.OptionsText(cid, 3, 0, rows);
        var accepted = listener.Observe(Protocol.ConvDown, From(host, target, Protocol.ConvOptions, current));
        if (accepted.Votes is not { Cid: cid, Decision: 3, First: 0, Second: 1 } pair ||
            listener.Observe(Protocol.ConvDown, From(host, target, Protocol.ConvOptions, current)) != default)
            throw new InvalidOperationException("Dialogue failed to select one pair of distinct offered rows.");
        uint previous = request.Tok;
        foreach (int row in new[] { pair.First, pair.Second })
        {
            var (wire, token) = listener.Vote(pair, row);
            if (wire[0] != Protocol.ConvUp || wire[3] != Protocol.JoinTargetHost ||
                !LootMsg.TryDecode(wire.AsSpan(3 + Protocol.JoinHeaderLen), out var vote) || vote.Kind != Protocol.ConvVote ||
                vote.Tok != token || token != previous + 1 || !Wo204Rules.TryParseVote(vote.Text, out int sentCid, out int decision, out int index) ||
                (sentCid, decision, index) != (cid, pair.Decision, row))
                throw new InvalidOperationException("Dialogue did not send the ordered legacy vote packet.");
            previous = vote.Tok;
        }
        try
        {
            _ = ParseBotOptions(["--test-bot", DialogFlag]);
            throw new InvalidOperationException("Dialogue mode did not require the explicit baseline flag.");
        }
        catch (FormatException) { }
    }
}
