// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using KcdMp.Wire;

namespace KcdMp.Client;

internal readonly record struct WorldReconnectProof(byte Source, uint HostKey, uint Seed, uint World, ulong HostGeneration, Guid Instance)
{
    public bool SameAuthority(WorldReconnectProof other) => HostKey == other.HostKey && Seed == other.Seed && Instance == other.Instance;
}

/// <summary>The three host announcements must be from the same authority on the current relay stream.</summary>
internal sealed class WorldReconnectFacts
{
    private sealed class Host
    {
        public uint Key, Seed, World;
        public ulong Generation;
        public Guid Instance;
        public bool SeedKnown;
    }

    private readonly Dictionary<byte, Host> _candidates = new();
    private byte _authority = 0xFF;

    public static string EpochProof(string epoch, Guid instance, ulong generation) =>
        FormattableString.Invariant($"{epoch}|r1|{instance:N}|{generation}");

    public WorldReconnectProof? Proof
    {
        get
        {
            if (!_candidates.TryGetValue(_authority, out var host) || host.Key == 0 || !host.SeedKnown || host.World == 0 ||
                host.Generation == 0 || host.Instance == Guid.Empty) return null;
            return new(_authority, host.Key, host.Seed, host.World, host.Generation, host.Instance);
        }
    }

    public bool Forget(byte source)
    {
        if (!_candidates.Remove(source)) return false;
        if (_authority == source) _authority = 0xFF;
        return true;
    }

    public bool Observe(int type, byte[] payload, byte receiver)
    {
        if (type is not (Protocol.JoinStatusDown or Protocol.W203Down) ||
            !Protocol.TrySplitJoinDown(payload, out byte source, out byte target, out uint id, out var body) ||
            target != receiver || source == receiver || source == 0xFF) return false;

        if (type == Protocol.JoinStatusDown)
        {
            if (!JoinStatusCodec.TryDecode(body, out byte state, out byte reason, out ushort flags)) return false;
            if (state == Protocol.JoinStateHostKey && id != 0 && reason == Protocol.JoinReasonId("host-key"))
            {
                var host = Candidate(source);
                if (host.Key != 0 && host.Key != id) { Forget(source); host = Candidate(source); }
                host.Key = id;
            }
            else if (state == Protocol.JoinStateSession)
            {
                var host = Candidate(source);
                host.SeedKnown = reason == Protocol.JoinReasonId("shared-world") && (flags & Protocol.SessionSeedKnown) != 0;
                host.Seed = id;
            }
            else return false;
            _authority = source; // Relay routes both status types from the damage authority only.
            return true;
        }

        if (!LootMsg.TryDecode(body, out var message) || message.Kind != Protocol.W203Epoch ||
            !Wo206Rules.TryParseEpoch(message.Text, out uint world, out _, out string why) || why == Wo206Rules.Ack || world == 0)
            return false;
        var fields = message.Text.Split('|');
        var candidate = Candidate(source);
        candidate.World = world;
        candidate.Instance = Guid.Empty;
        candidate.Generation = 0;
        if (fields.Length == 7 && fields[4] == "r1" && Guid.TryParseExact(fields[5], "N", out Guid instance) &&
            ulong.TryParse(fields[6], NumberStyles.None, CultureInfo.InvariantCulture, out ulong generation))
        {
            candidate.Instance = instance;
            candidate.Generation = generation;
        }
        return true;
    }

    private Host Candidate(byte source)
    {
        if (!_candidates.TryGetValue(source, out var host)) _candidates.Add(source, host = new());
        return host;
    }
}

internal enum WorldReconnectDecision { Waiting, SameWorld, DifferentWorld, Timeout }

internal sealed class WorldReconnectAttempt(WorldSyncScope scope, WorldReconnectProof? original, long startedMs)
{
    public const int WindowMs = 30_000;
    public WorldSyncScope Scope { get; } = scope;
    public long Elapsed(long nowMs) => Math.Max(0, nowMs - startedMs);
    public long Remaining(long nowMs) => Math.Max(0, WindowMs - Elapsed(nowMs));
    private int _claimed;

    public WorldReconnectDecision Decide(WorldLoadSnapshot held, WorldReconnectProof? fresh, long nowMs)
    {
        if (Elapsed(nowMs) > WindowMs) return WorldReconnectDecision.Timeout;
        if (held.Generation != Scope.Generation || held.HostWorld != Scope.HostWorld || held.Phase != WorldLoadPhase.Play || !held.LinkHeld ||
            original is not { } before || fresh is not { } after || before.World != Scope.HostWorld || !before.SameAuthority(after))
            return WorldReconnectDecision.Waiting;
        if (after.World == before.World && after.HostGeneration == before.HostGeneration) return WorldReconnectDecision.SameWorld;
        if (after.World > before.World && after.HostGeneration > before.HostGeneration) return WorldReconnectDecision.DifferentWorld;
        return WorldReconnectDecision.Waiting;
    }

    public bool TryClaim(WorldReconnectDecision decision) => decision != WorldReconnectDecision.Waiting &&
        Interlocked.CompareExchange(ref _claimed, (int)decision, 0) == 0;
    public bool TryRelease(WorldReconnectDecision decision) => decision != WorldReconnectDecision.Waiting &&
        Interlocked.CompareExchange(ref _claimed, 0, (int)decision) == (int)decision;
}
