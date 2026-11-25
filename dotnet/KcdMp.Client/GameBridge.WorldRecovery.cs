// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
namespace KcdMp.Client;

public partial class GameBridge
{
    private bool WorldRecoveryEnabled => LoadSequenceEnabled && W221On("world_recovery");
    private bool WorldRecoveryWireEffective => WorldRecoveryEnabled && WorldSyncEffective &&
        (_w213Caps & Protocol.LinkCapWorldRecovery) != 0;
    internal bool WorldRecoveryTestEnabled { get; init; }

    private static string WorldRecoveryArea(WorldBaselineSection section) => section.ToString().ToLowerInvariant();

    private void WorldRecoveryLine(WorldSyncReceiver receiver, WorldBaselineSection section, string result, ulong revision = 0)
    {
        Console.WriteLine($"MP-WORLD-DIGEST role={LoadSequenceRole} world={receiver.Scope.HostWorld} gen={receiver.Scope.Generation} snapshot={receiver.Snapshot} section={WorldRecoveryArea(section)} rev={revision} result={result}");
    }

    private void WorldRecoveryTestEvent(string? arg)
    {
        var receiver = Volatile.Read(ref _worldReceiver);
        if (!WorldRecoveryTestEnabled || !WorldRecoveryWireEffective || receiver is not { Installed: true } ||
            !WorldSyncCurrent(receiver) || !Enum.TryParse<WorldBaselineSection>(arg, true, out var section) ||
            arg != WorldRecoveryArea(section) || section is < WorldBaselineSection.Body or > WorldBaselineSection.Weather ||
            !receiver.Recovery.TestCorrupt(section))
        {
            Console.WriteLine("MP-WORLD-RECOVERY-TEST refused");
            return;
        }
        WorldRecoveryLine(receiver, section, "corrupted");
    }

    private async Task ReceiveWorldRecoveryAsync(WorldSyncReceiver receiver, int type, byte[] body, CancellationToken ct)
    {
        if (type == Protocol.WorldDigestDown)
        {
            if (WorldRecoveryWire.TryReadDigest(body, out var digest) && digest.Scope == receiver.Scope &&
                digest.SnapshotId == receiver.Snapshot)
            {
                // The Relay only sends these after accepting this baseline's
                // acknowledgement, even during a quiet interval with no events.
                receiver.ServerInstalled = true;
                ReceiveWorldDigest(receiver, digest);
            }
            return;
        }
        if (!WorldRecoveryWire.TryReadChunk(body, out var chunk) || chunk.Digest.Scope != receiver.Scope ||
            chunk.Digest.SnapshotId != receiver.Snapshot)
            return;

        var area = chunk.Digest.Section;
        switch (receiver.Recovery.Accept(chunk, out var complete))
        {
            case WorldRecoveryReceiver.PartResult.Rejected:
                WorldRecoveryCount(receiver, area, RecoveryMeasure.Failed);
                WorldRecoveryLine(receiver, area, "rejected", chunk.Digest.Revision);
                T3WorldRecoveryReport(LoadSequenceRole, ct, mirror: false);
                return;
            case WorldRecoveryReceiver.PartResult.Ready:
                break;
            default:
                return;
        }

        // These are absolute scoped rows. The regular ordered receive window and
        // its outstanding application receipts stay in place during a repair.
        bool confirmed = false;
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            limit.CancelAfter(5000);
            try { confirmed = await InstallWorldBaselineAsync(receiver, complete!, limit.Token, recovery: true); }
            catch (OperationCanceledException) when (limit.IsCancellationRequested) { }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
            {
                // Keep a disputed repair scoped to this area.
            }
        }
        if (!WorldSyncCurrent(receiver) || !WorldRecoveryWireEffective) return;
        receiver.Recovery.Finish(chunk.Digest, confirmed);
        WorldRecoveryCount(receiver, area, confirmed ? RecoveryMeasure.Repaired : RecoveryMeasure.Failed);
        WorldRecoveryLine(receiver, area, confirmed ? "repaired" : "rejected", chunk.Digest.Revision);
        if (!confirmed) T3WorldRecoveryReport(LoadSequenceRole, ct, mirror: false);
    }

    private Task<bool> ApplyWorldRecoveryRowAsync(WorldBaselineChunk chunk, InFrame frame, CancellationToken ct)
    {
        if (!WorldRecoveryWireEffective || !WorldBaselineCurrent(frame, ct)) return Task.FromResult(false);
        if (chunk.Section == WorldBaselineSection.Clock)
            return ReassertCurrentRecoveryClockAsync(frame, ct);
        if (frame.Type == Protocol.Ghost && PositionCodec.TryDecodeGhost(frame.Payload, out var ghost) &&
            _ghostLastSenderMs.TryGetValue(ghost.GhostId, out uint activeGhost) &&
            (ghost.SenderMs == 0 || unchecked((int)(activeGhost - ghost.SenderMs)) > 0))
            return Task.FromResult(false);
        if (frame.Type == Protocol.NpcStateDown &&
            NpcCompact.TryReadDown(frame.Payload, out _, out var name, out var npc) &&
            _w147NpcNewest.TryGetValue(name, out var activeNpc) && unchecked((short)(activeNpc.Seq - npc.Seq)) > 0)
            return Task.FromResult(false);
        if (chunk.Section == WorldBaselineSection.Weather && !RecoveryWeatherStillCurrent(frame))
            return Task.FromResult(false);
        return ApplyWorldBaselineAsync(chunk, frame, ct);
    }

    private async Task<bool> ReassertCurrentRecoveryClockAsync(InFrame frame, CancellationToken ct)
    {
        if (!T3SideChannelFrame.TryDecode(frame.Type, frame.Payload, WeatherBlendSeconds, out var clock) ||
            clock.Kind != Protocol.W164Clock || !T3SideSource(clock.Source)) return false;
        long generation = Interlocked.Read(ref _hostCalendarGeneration);
        HostCalendarSample? current;
        lock (_hostCalendarLock) current = _hostCalendar;
        long now = Environment.TickCount64;
        if (current is not { } sample || sample.Ratio <= 0 || now - sample.ArrivedMs is < 0 or > 9000)
            return false;
        bool applied = false;
        var result = await RunProcessorFrameAsync(async _ =>
        {
            applied = await UpdateHostCalendarAsync(sample, generation);
        }, 1800, ct);
        if (!applied || result.Status != ProcessorFrameScope.Outcome.Completed || !WorldBaselineCurrent(frame, ct) ||
            await _combat.CalendarSnapshotAsync(ct) is not { } observed) return false;
        return WorldBaselineCurrent(frame, ct) &&
            Math.Abs(observed.WorldMs - sample.TargetMs(Environment.TickCount64)) <= 2000 &&
            Math.Abs(observed.Ratio - sample.Ratio) <= 0.025;
    }

    private bool RecoveryWeatherStillCurrent(InFrame frame)
    {
        var latest = Volatile.Read(ref _worldRecoveryWeather);
        if (latest is null || latest.Scope != new WorldSyncScope(frame.Stamp, frame.LoadGeneration)) return true;
        if (!T3SideChannelFrame.TryDecode(frame.Type, frame.Payload, WeatherBlendSeconds, out var row)) return false;
        return (latest.Profile is null || row.Profile is null || latest.Profile == row.Profile) &&
            (latest.Rain is not float currentRain || row.Rain is not float proposedRain ||
                Math.Abs(currentRain - proposedRain) <= 0.08);
    }

    private void ReceiveWorldDigest(WorldSyncReceiver receiver, WorldRecoveryDigest digest)
    {
        var outcome = receiver.Recovery.Observe(digest, Environment.TickCount64);
        if (outcome == WorldRecoveryReceiver.DigestResult.Match &&
            digest.Section is WorldBaselineSection.Clock or WorldBaselineSection.Weather &&
            receiver.Recovery.BeginReadback(digest.Section))
            _ = RefreshRecoveryReadbackAsync(receiver, digest.Section, receiver.Stop.Token);
        string result;
        switch (outcome)
        {
            case WorldRecoveryReceiver.DigestResult.Match:
                WorldRecoveryCount(receiver, digest.Section, RecoveryMeasure.Checked);
                result = "checked";
                break;
            case WorldRecoveryReceiver.DigestResult.Mismatch:
                WorldRecoveryCount(receiver, digest.Section, RecoveryMeasure.Checked);
                WorldRecoveryCount(receiver, digest.Section, RecoveryMeasure.Mismatch);
                result = "mismatch";
                SendWorldRepair(receiver, digest);
                break;
            case WorldRecoveryReceiver.DigestResult.Repeat:
                SendWorldRepair(receiver, digest);
                return;
            case WorldRecoveryReceiver.DigestResult.TimedOut:
                WorldRecoveryCount(receiver, digest.Section, RecoveryMeasure.Failed);
                result = "rejected";
                break;
            default:
                return;
        }
        WorldRecoveryLine(receiver, digest.Section, result, digest.Revision);
        if (outcome == WorldRecoveryReceiver.DigestResult.TimedOut)
            T3WorldRecoveryReport(LoadSequenceRole, receiver.Stop.Token, mirror: false);
    }

    private void SendWorldRepair(WorldSyncReceiver receiver, WorldRecoveryDigest expected)
    {
        if (_w213Writer is not { } writer || !ReferenceEquals(_w213Stream, receiver.Stream)) return;
        try
        {
            writer.Enqueue(WorldRecoveryWire.BuildRequest(new(expected)), () =>
                WorldRecoveryWireEffective && WorldSyncCurrent(receiver));
        }
        catch (IOException) { } // Link loss is handled by its owning reader.
    }
}

internal static class WorldRecoveryTestOptions
{
    public const string EnvironmentName = "KCDMP_TEST_WORLD_RECOVERY";
    private const string Flag = "--test-world-recovery";

    public static bool TryRead(string[] args, string? enabled, out bool test, out string[] remaining)
    {
        int flags = args.Count(arg => arg == Flag);
        test = flags == 1 && enabled == "1";
        remaining = args.Where(arg => arg != Flag).ToArray();
        return (flags == 0 || test) && !args.Any(arg => arg.StartsWith(Flag + "=", StringComparison.Ordinal));
    }
}
