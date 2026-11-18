// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

internal static class ReplayScenarioCombatCases
{
    private const byte Joiner = 1;
    private static readonly Guid Bow = Guid.Parse("1810e207-83fa-40bf-894c-a882d782b60d");

    internal static Task<string?> VerifyAsync(ReplayScenarioContext context) => context.Name switch
    {
        "knockdown" => KnockdownAsync(context),
        "bow-shot" => BowAsync(context),
        "remote-walk-stairs" => Task.FromResult(Stairs(context)),
        "remote-fight-down-up" => FightAsync(context),
        _ => throw new ArgumentOutOfRangeException(nameof(context.Name))
    };

    private static IEnumerable<Wo197Rules.KnockEdge> KnockEdges(ReplayScenarioContext c)
    {
        byte flags = 0;
        foreach (var packet in c.Packets.Where(p => p.Type == Protocol.PlayerStateDown))
        {
            if (packet.Payload is not [Joiner, _, _, _, _, _, _, _, _, var next])
                throw new InvalidDataException("The relay vitals were not from the expected partner.");
            var edge = Wo197Rules.KnockoutEdge(flags, next);
            flags = next;
            if (edge != Wo197Rules.KnockEdge.None) yield return edge;
        }
    }

    private static async Task<string?> KnockdownAsync(ReplayScenarioContext c)
    {
        var edges = KnockEdges(c).ToArray();
        await VerifyKnockCommandsAsync(c, edges);
        if (c.Packets.Any(packet => packet.Type == Protocol.PlayerStateDown &&
            packet.Payload is [Joiner, _, _, _, _, _, _, _, _, var flags] && Wo155Rules.BodyOf(
                (flags & Protocol.PlayerStateFlagUnconscious) != 0, (flags & Protocol.PlayerStateFlagKnockedDown) != 0) == Wo155Rules.Body.Death))
            return "The knockdown was classified as a death instead of a recoverable fall.";
        if (!edges.SequenceEqual([Wo197Rules.KnockEdge.Down, Wo197Rules.KnockEdge.Up]))
            return "Knockdown vitals did not yield one fall and one wake.";
        return null;
    }

    private static async Task VerifyKnockCommandsAsync(ReplayScenarioContext c, IReadOnlyList<Wo197Rules.KnockEdge> edges)
    {
        if (edges.Count == 0) return;
        // Run the real Agent fallback with a loaded world, no native entity, and
        // the recorded game transport. The transcript checks Lua dispatch, not game animation.
        var bridge = new GameBridge(new ClientConfig());
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        static void SetMode(GameBridge bridge, string field, string value)
        {
            var target = typeof(GameBridge).GetField(field, fields)!;
            target.SetValue(bridge, Enum.Parse(target.FieldType, value));
        }
        typeof(GameBridge).GetField("_transport", fields)!.SetValue(bridge, c.Game);
        SetMode(bridge, "_where", "World");
        SetMode(bridge, "_edgeMode", "Http");
        var present = typeof(GameBridge).GetMethod("Wo201FigureKnockoutAsync", fields)!;
        foreach (var edge in edges)
            await ((Task)present.Invoke(bridge, [Joiner, edge == Wo197Rules.KnockEdge.Down])!).WaitAsync(c.Cancellation);
    }

    private static async Task<string?> BowAsync(ReplayScenarioContext c)
    {
        var packets = c.Packets.Where(p => p.Type == Protocol.W164Down).ToArray();
        if (packets.Length != 1 || !TryReadBowStep(packets[0].Payload, out var step))
            return "The relay did not deliver the scoped P6 Fire step.";

        Guid[]? equipped = await c.Game.ReadGhostEquippedItemClassesAsync(Wo141Rules.AvatarName(Joiner), c.Cancellation);
        var catalog = ReadItemFixture();
        if (step.Kind != RangedWeaponKind.Bow || catalog.Kind(step.ItemClass) != step.Kind)
            return "The Fire step's weapon kind disagrees with the measured bow item class.";
        if (Wo201Rules.RangedFragment(step.Phase) != "Fire") return "The measured bow step cannot select the checked Fire fragment.";
        // An equipped class alone does not prove the engine held the bow in its hand.
        return equipped?.Contains(step.ItemClass) == true ? null : "The captured bow class is absent from the partner figure's replayed equipment.";
    }

    private static bool TryReadBowStep(byte[] payload, out RangedStepV2 step)
    {
        step = default;
        return Protocol.TrySplitJoinDown(payload, out byte source, out _, out uint join, out var body) &&
               source == Joiner && join == 0 && LootMsg.TryDecode(body, out var message) &&
               message.Kind == Protocol.W164RangedStep && RangedStepV2.TryDecode(message.Text, out step) &&
               step.World == 7 && step.Sequence == message.Tok && step.Phase == 3 && step.Source == 1;
    }

    private static RangedItemCatalog ReadItemFixture()
    {
        string file = Path.Combine(Path.GetTempPath(), "kcdmp-replay-bow-" + Guid.NewGuid().ToString("N") + ".pak");
        try
        {
            using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
            using (var xml = new StreamWriter(zip.CreateEntry("Libs/Tables/item/item.xml").Open()))
                xml.Write($"<Database><MissileWeapon Id=\"{Bow:D}\" Class=\"9\"/></Database>");
            return RangedItemCatalog.Read(file);
        }
        finally { File.Delete(file); }
    }

    private static string? Stairs(ReplayScenarioContext c)
    {
        var poses = new List<GhostSample>();
        foreach (var packet in c.Packets.Where(p => p.Type == Protocol.Ghost))
        {
            if (!PositionCodec.TryDecodeGhost(packet.Payload, out var pose) || pose.GhostId != Joiner || pose.IsRiding || pose.IsStale ||
                pose.SenderMs == 0 || pose.Body is not { Pace: BodyPace.Walk })
                return "The stair route lost the partner's timed walking state.";
            poses.Add(pose);
        }
        if (poses.Count != 5) return "The relay did not deliver all five sampled stair treads.";
        var seen = c.Records.Where(r => r.Kind == SessionRecordKind.GameEvent)
            .Select(r => JsonSerializer.Deserialize<string[]>(r.Payload))
            .Where(e => e is ["replay_model_remote_pose", _])
            .Select(e => JsonSerializer.Deserialize<ModeledRemotePose>(e![1])).ToArray();
        if (seen.Length != poses.Count) return "The stair trace is missing modeled receiving-controller observations.";

        const uint entity = 68;
        var scope = new RemoteRelocationScope(7, 0, 4, 8, 9, entity, 1, true, 0);
        var relocation = new RemoteRelocationProofs();
        relocation.PeerNamed(Joiner);
        relocation.BodySpawned(Joiner, scope, poses[0].SenderMs - 1);
        for (int i = 0; i < poses.Count; i++)
        {
            var sample = poses[i];
            var observation = seen[i];
            var point = new RemoteRelocationPoint(observation.X, observation.Y, observation.Z);
            if (!point.Valid || observation.Entity != entity || observation.SenderMs != sample.SenderMs)
                return "The modeled stair readback belongs to the wrong body or pose sample.";
            // Only modeled receiver evidence comes from the transcript. The relay
            // inputs are identical in both twins; the production ledger judges the hard placement.
            long at = observation.SenderMs;
            if (observation.HardPlacement && relocation.Take(Joiner, scope, at, at, at, point) is not { Reason: RemoteRelocationReason.Join })
                return "The receiving controller made a hard stair correction without a relocation proof.";
            if ((i == 0) != observation.HardPlacement || MathF.Abs(point.X - sample.X) > 0.05f ||
                MathF.Abs(point.Y - sample.Y) > 0.05f || MathF.Abs(point.Z - sample.Z) > 0.05f)
                return "The modeled receiving controller did not follow the sampled stair route.";
            if (i == 0) continue;
            var before = seen[i - 1];
            var after = observation;
            float rise = after.Z - before.Z;
            float run = MathF.Abs(after.X - before.X);
            float expectedRun = (after.SenderMs - before.SenderMs) / 1000f * sample.State2!.Value.SpeedMps;
            if (after.SenderMs <= before.SenderMs || rise is <= 0f or > 0.3f || MathF.Abs(run - expectedRun) > 0.05f)
                return "The stair trace contains a hard position jump instead of a continuous step.";
        }
        return null;
    }

    private readonly record struct ModeledRemotePose(uint Entity, uint SenderMs, bool HardPlacement, float X, float Y, float Z);

    private static async Task<string?> FightAsync(ReplayScenarioContext c)
    {
        var inbox = new ActionInbox();
        var sequence = new List<string>();
        var edges = new List<Wo197Rules.KnockEdge>();
        byte flags = 0;
        int contacts = 0;
        bool duplicateDuringKnockout = false;
        foreach (var packet in c.Packets)
        {
            if (packet.Type == Protocol.PlayerHitV8Down)
            {
                if (!PlayerHitV8.TryDecodeDown(packet.Payload, out byte attacker, out var contact) ||
                    attacker != Joiner || contact.Victim != 0 || contact.Health != 12f || contact.Stamina != 3f)
                    return "The separate friendly-fire contact lost its victim or its original measured damage.";
                contacts++;
            }
            if (packet.Type == Protocol.PlayerStateDown && packet.Payload is [Joiner, _, _, _, _, _, _, _, _, var next])
            {
                var edge = Wo197Rules.KnockoutEdge(flags, next);
                flags = next;
                if (edge != Wo197Rules.KnockEdge.None) { sequence.Add(edge.ToString()); edges.Add(edge); }
            }
            if (packet.Type != Protocol.ActionDown) continue;
            var action = inbox.Accept(packet.Payload, out var rejection);
            if (rejection == ActionReject.StaleOrDuplicate && (flags & Protocol.PlayerStateFlagEngineKnockout) != 0)
            {
                duplicateDuringKnockout = true;
                continue;
            }
            if (action is not { SourceGhostId: Joiner, Phase: ActionPhase.Commit } committed ||
                (committed.Kind == ActionKind.Attack ? !AttackEvent.TryFromBytes(committed.Payload, out _) :
                 committed.Kind != ActionKind.BlockImpulse || !RowEvent.TryFromBytes(committed.Payload, out _)))
                return "The relay delivered an invalid committed fight action.";
            sequence.Add(committed.Kind.ToString());
        }
        if (!sequence.SequenceEqual(["Attack", "BlockImpulse", "Down", "Up", "Attack"]))
            return "The committed fight did not retain attack, block, down, wake and the next attack in order.";
        if (contacts != 1) return "The two visual attacks must carry exactly one separate measured contact.";
        await VerifyKnockCommandsAsync(c, edges);
        return duplicateDuringKnockout ? "A duplicate attack reached the receiver while the partner was knocked out." : null;
    }
}
