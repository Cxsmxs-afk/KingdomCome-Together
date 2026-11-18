// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

internal sealed record ReplayScenarioContext(string Name, bool OldPath, byte Sender, byte Receiver,
    IReadOnlyList<SessionRecord> Records, IReadOnlyList<ReplayPacket> Packets, ReplayGameTransport Game, CancellationToken Cancellation);

public sealed class ReplayScenarioTests
{
    [Theory]
    [InlineData("weather-during-load", "weather wrote while the game was loading")]
    [InlineData("pickup-under-load", "pickup result waited for the blocked gameplay frame")]
    [InlineData("wolf-pack-fight", "NPC damage targeted the local player's soul")]
    [InlineData("death-respawn", "wake relied on a stale or unsafe teleport snapshot")]
    [InlineData("knockdown", "The knockdown was classified as a death instead of a recoverable fall.")]
    [InlineData("bow-shot", "The Fire step's weapon kind disagrees with the measured bow item class.")]
    [InlineData("remote-walk-stairs", "The receiving controller made a hard stair correction without a relocation proof.")]
    [InlineData("remote-fight-down-up", "A duplicate attack reached the receiver while the partner was knocked out.")]
    public async Task The_current_record_passes_and_its_old_path_twin_fails_the_same_contract(string name, string expectedViolation)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        Assert.Null(await VerifyAsync(name, false, stop.Token));
        Assert.Equal(expectedViolation, await VerifyAsync(name, true, stop.Token));
    }

    private static async Task<string?> VerifyAsync(string name, bool oldPath, CancellationToken ct)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Replay", name + (oldPath ? ".old.kcdrec" : ".kcdrec"));
        using var file = File.OpenRead(path);
        var records = SessionRecording.Read(file);
        byte[][] sends = records.Where(record => record.Kind == SessionRecordKind.RelaySent).Select(record => record.Payload).ToArray();
        Assert.NotEmpty(sends);
        bool joinerSends = name is "knockdown" or "bow-shot" or "remote-walk-stairs" or "remote-fight-down-up";
        await using var relay = await ReplayRelay.StartAsync(ct);
        await using var host = await ReplayPeer.ConnectAsync(relay.Port, "fixture-host", ct);
        await using var joiner = await ReplayPeer.ConnectAsync(relay.Port, "fixture-joiner", ct);
        Assert.Equal((byte)0, host.Id); Assert.Equal((byte)1, joiner.Id);
        var source = joinerSends ? joiner : host;
        var receiver = joinerSends ? host : joiner;
        IReadOnlyList<ReplayPacket> packets = await source.ReplayToAsync(receiver, sends, ct);
        await using var game = new ReplayGameTransport(records);
        var context = new ReplayScenarioContext(name, oldPath, source.Id, receiver.Id, records, packets, game, ct);
        string? result = joinerSends ? await ReplayScenarioCombatCases.VerifyAsync(context) : await ReplayScenarioWorldCases.VerifyAsync(context);
        game.VerifyComplete();
        return result;
    }
}
