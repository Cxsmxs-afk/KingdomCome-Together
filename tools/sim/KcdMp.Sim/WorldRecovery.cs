// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
namespace KcdMp.Sim;

using KcdMp.Client;

internal static class WorldRecovery
{
    internal sealed record Check(string Name, bool Pass);

    private static readonly WorldSyncScope Scope = new(7, 41);
    private const uint Snapshot = 13;
    private static readonly WorldBaselineSection[] Areas = Enum.GetValues<WorldBaselineSection>();

    internal static List<Check> Run()
    {
        var checks = new List<Check>();
        foreach (var area in Areas)
        {
            var receiver = InstallBaseline();
            var digest = Digest(area);
            byte[][] expected = Rows(area);
            bool scoped = receiver.TestCorrupt(area) &&
                receiver.Observe(digest, 10_000) == WorldRecoveryReceiver.DigestResult.Mismatch &&
                OtherAreasStillMatch(receiver, area, 10_000);
            byte[] request = WorldRecoveryWire.BuildRequest(new(digest));
            scoped &= request[0] == Protocol.WorldRepairUp && WorldRecoveryWire.TryReadRequest(request.AsSpan(3), out var parsed) &&
                parsed.Expected == digest;
            checks.Add(new($"{area} mismatch requests only its exact world, snapshot and section", scoped));

            // Deliver the last chunk first, drop the first once, then repeat
            // the digest and deliver the missing chunk. This exercises the
            // production hash and receipt projection without engine effects.
            var delayedEarlierWorld = Chunk(digest with { Scope = new(Scope.HostWorld, Scope.Generation - 1) }, 0, expected[0]);
            bool recovered = receiver.Accept(delayedEarlierWorld, out _) == WorldRecoveryReceiver.PartResult.Ignore &&
                receiver.Accept(Chunk(digest, 1, expected[1]), out _) == WorldRecoveryReceiver.PartResult.Waiting &&
                receiver.Observe(digest, 20_000) == WorldRecoveryReceiver.DigestResult.Repeat &&
                receiver.Accept(Chunk(digest, 1, expected[1]), out _) == WorldRecoveryReceiver.PartResult.Waiting &&
                receiver.Accept(Chunk(digest, 0, expected[0]), out var complete) == WorldRecoveryReceiver.PartResult.Ready &&
                complete is { Length: 2 } && complete.Select((part, index) => part.Payload.AsSpan().SequenceEqual(expected[index])).All(equal => equal);
            recovered &= receiver.Observe(digest, 20_001) == WorldRecoveryReceiver.DigestResult.Repeat;
            if (recovered) receiver.Finish(digest, confirmed: true); // modeled engine receipt
            recovered &= receiver.Observe(digest, 30_000) == WorldRecoveryReceiver.DigestResult.Match &&
                OtherAreasStillMatch(receiver, area, 30_000);
            checks.Add(new($"{area} repair survives reordered chunks and becomes a match after confirmation", recovered));
        }

        var damaged = InstallBaseline();
        var corpse = Digest(WorldBaselineSection.Corpse);
        bool damagedRefused = damaged.TestCorrupt(corpse.Section) &&
            damaged.Observe(corpse, 10_000) == WorldRecoveryReceiver.DigestResult.Mismatch &&
            damaged.Accept(Chunk(corpse, 0, [0xfe]), out _) == WorldRecoveryReceiver.PartResult.Waiting &&
            damaged.Accept(Chunk(corpse, 1, Rows(corpse.Section)[1]), out _) == WorldRecoveryReceiver.PartResult.Rejected &&
            OtherAreasStillMatch(damaged, corpse.Section, 20_000);
        checks.Add(new("altered repair payload is refused and leaves every other section in sync", damagedRefused));

        var unconfirmed = InstallBaseline();
        bool confirmationRequired = unconfirmed.TestCorrupt(corpse.Section) &&
            unconfirmed.Observe(corpse, 10_000) == WorldRecoveryReceiver.DigestResult.Mismatch &&
            unconfirmed.Accept(Chunk(corpse, 0, Rows(corpse.Section)[0]), out _) == WorldRecoveryReceiver.PartResult.Waiting &&
            unconfirmed.Accept(Chunk(corpse, 1, Rows(corpse.Section)[1]), out _) == WorldRecoveryReceiver.PartResult.Ready;
        if (confirmationRequired) unconfirmed.Finish(corpse, confirmed: false);
        confirmationRequired &= unconfirmed.Observe(corpse, 20_000) != WorldRecoveryReceiver.DigestResult.Match &&
            OtherAreasStillMatch(unconfirmed, corpse.Section, 20_000);
        checks.Add(new("valid repair bytes cannot replace an unconfirmed section receipt", confirmationRequired));

        var stalled = InstallBaseline();
        bool bounded = stalled.TestCorrupt(corpse.Section) &&
            stalled.Observe(corpse, 10_000) == WorldRecoveryReceiver.DigestResult.Mismatch &&
            stalled.Observe(corpse, 39_999) == WorldRecoveryReceiver.DigestResult.Repeat &&
            stalled.Observe(corpse, 40_000) == WorldRecoveryReceiver.DigestResult.TimedOut &&
            OtherAreasStillMatch(stalled, corpse.Section, 40_000);
        checks.Add(new("one stalled repair expires without disturbing other sections", bounded));

        long now = 0;
        var world = new WorldLoadSequence(() => now);
        bool adopted = world.TryBegin(0, WorldLoadStart.AdoptLoadedWorld, out var playing, Scope.HostWorld);
        bool held = adopted && world.TryHoldLink(playing.Generation);
        now = 10_000;
        Guid hostInstance = Guid.Parse("50729893-eeb4-4a27-a514-8dba71935c19");
        var authority = new WorldReconnectProof(1, 37, 1009, playing.HostWorld, 17, hostInstance);
        var attempt = new WorldReconnectAttempt(new(playing.HostWorld, playing.Generation), authority, 0);
        var fresh = authority with { Source = 4 }; // relay reassigned ID, same trusted host
        bool rejectsWrongScope = held && !world.TryReconnect(playing.Generation + 1, playing.HostWorld) &&
            !world.TryReconnect(playing.Generation, playing.HostWorld + 1) && world.Current.LinkHeld;
        checks.Add(new("a different world or generation cannot clear the held link", rejectsWrongScope));
        bool trusted = attempt.Decide(world.Current, null, now) == WorldReconnectDecision.Waiting &&
            attempt.Decide(world.Current, fresh with { Instance = Guid.Empty }, now) == WorldReconnectDecision.Waiting &&
            attempt.Decide(world.Current, fresh with { HostGeneration = 16 }, now) == WorldReconnectDecision.Waiting &&
            attempt.Decide(world.Current, fresh with { World = playing.HostWorld + 1, HostGeneration = 18 }, now)
            == WorldReconnectDecision.DifferentWorld;
        checks.Add(new("same-world recovery requires fresh matching authority and host generation", trusted));
        bool deadline = attempt.Decide(world.Current, fresh, 30_000) == WorldReconnectDecision.SameWorld &&
            attempt.Decide(world.Current, fresh, 30_001) == WorldReconnectDecision.Timeout;
        checks.Add(new("virtual reconnect deadline accepts 30 seconds and rejects 30 seconds plus 1 ms", deadline));
        bool resumed = rejectsWrongScope && trusted &&
            attempt.Decide(world.Current, fresh, now) == WorldReconnectDecision.SameWorld &&
            attempt.TryClaim(WorldReconnectDecision.SameWorld) && !attempt.TryClaim(WorldReconnectDecision.SameWorld) &&
            world.TryReconnect(playing.Generation, Scope.HostWorld) &&
            world.Current is { Phase: WorldLoadPhase.Play, LinkHeld: false } &&
            world.Current.Generation == playing.Generation && world.Current.HostWorld == playing.HostWorld &&
            world.PhaseMilliseconds(WorldLoadPhase.Load) == 0;
        checks.Add(new("virtual ten-second reconnect keeps the loaded world and generation", resumed));
        return checks;
    }

    internal static string Markdown(IEnumerable<Check> checks) =>
        "\n## World recovery rules\n\nPortable replay of the production digest, repair receiver and load sequence rules. " +
        "Connection time and section application are modeled.\n\n" +
        string.Concat(checks.Select(check => $"- {(check.Pass ? "PASS" : "FAIL")} {check.Name}\n"));

    private static WorldRecoveryReceiver InstallBaseline()
    {
        var receiver = new WorldRecoveryReceiver();
        var baseline = Areas.SelectMany(area => Rows(area).Select((payload, index) => new WorldBaselineChunk(
            Scope, Snapshot, WorldSyncWire.BaselineVersion, area, (ushort)index, 2, payload))).ToArray();
        receiver.Install(baseline);
        return receiver;
    }

    private static byte[][] Rows(WorldBaselineSection area) => [[(byte)area, 1], [(byte)area, 2]];

    private static WorldRecoveryDigest Digest(WorldBaselineSection area)
    {
        if (!WorldRecoveryWire.TryComputeHash(Scope, Snapshot, area, Rows(area), out var hash))
            throw new InvalidOperationException($"Could not build digest for {area}.");
        byte[] packet = WorldRecoveryWire.BuildDigest(new(Scope, Snapshot, area, 1, hash));
        if (!WorldRecoveryWire.TryReadDigest(packet.AsSpan(3), out var digest))
            throw new InvalidOperationException($"Could not read digest for {area}.");
        return digest;
    }

    private static WorldRecoveryChunk Chunk(WorldRecoveryDigest digest, ushort index, byte[] payload)
    {
        byte[] packet = WorldRecoveryWire.BuildChunk(new(digest, index, 2, payload));
        if (!WorldRecoveryWire.TryReadChunk(packet.AsSpan(3), out var chunk))
            throw new InvalidOperationException($"Could not read repair chunk {index}.");
        return chunk;
    }

    private static bool OtherAreasStillMatch(WorldRecoveryReceiver receiver, WorldBaselineSection affected, long now) =>
        Areas.All(area => area == affected || receiver.Observe(Digest(area), now) == WorldRecoveryReceiver.DigestResult.Match);
}
