// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using KcdMp.Wire;

namespace KcdMp.Client;

public partial class GameBridge
{
    private readonly GuidedRun _guidedRun = new();
    private readonly object _guidedCommands = new();
    private readonly SemaphoreSlim _guidedUi = new(1, 1);
    private CancellationToken _guidedRunLifetime;
    private long _guidedUiRevision;

    private static bool GuidedRunEnabled => Environment.GetEnvironmentVariable("KCDMP_TEST_GUIDED_RUN") == "1";

    private GuidedRunContext? GuidedRunCurrent()
    {
        if (!LoadSequenceEnabled || !_combatRoleApplyComplete || _leaveInProgress || _where != GameWhere.World ||
            !LoadSequenceWritable || !WorldSyncReady) return null;
        var world = _worldLoad.Current;
        var scope = new WorldSyncScope(world.HostWorld, world.Generation);
        if (!scope.IsValid || world.HostWorld != _w206.World || !_worldLoad.CanApply(scope.Generation)) return null;
        var link = Volatile.Read(ref _presentationSession);
        if (link is null || link.Lifetime.IsCancellationRequested || !ReferenceEquals(_wo121Stream, link.Relay)) return null;
        var partners = LivePartners();
        if (partners.Count != 1) return null;
        byte partner = partners[0];
        string? role = W137Host ? "host" : W137Joiner && _hostModeKnown && _hostSharedWorld && _hostModeFrom == partner ? "joiner" : null;
        if (role is null) return null;
        uint? baseline = WorldSyncEffective && Volatile.Read(ref _worldReceiver) is { Installed: true, ServerInstalled: true } receiver &&
            receiver.Scope == scope && ReferenceEquals(receiver.Stream, link.Relay) && WorldSyncCurrent(receiver) ? receiver.Snapshot : null;
        return new(role, scope, partner, link.Relay, baseline is > 0 ? baseline : null);
    }

    private void GuidedRunGameEvent(string arg)
    {
        if (_guidedRunLifetime.IsCancellationRequested ||
            !GuidedRunCommand.TryParse(arg, Environment.GetEnvironmentVariable("KCDMP_TEST_GUIDED_RUN"), out var command)) return;
        GuidedRunReply reply;
        lock (_guidedCommands)
        {
            var current = GuidedRunCurrent();
            reply = _guidedRun.Command(command, current, DateTimeOffset.UtcNow);
            if (reply.Started is { } started && current is not null)
            {
                try { CaptureGuidedRunSources(started); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                { Console.WriteLine($"MP-TESTLAUF run={started:N} result=source-capture-failed error={ex.GetType().Name}"); }
                Console.WriteLine(FormattableString.Invariant($"MP-TESTLAUF role={current.Role} run={started:N} result=started world={current.Scope.HostWorld} gen={current.Scope.Generation}"));
            }
            if (reply.AbortedRole is { } abortedRole && reply.RunId is Guid aborted)
                Console.WriteLine($"MP-TESTLAUF role={abortedRole} run={aborted:N} result=aborted");
            if (reply.Station is { } row && reply.RunId is Guid runId)
            {
                string role = reply.Completed?.Role ?? current?.Role ?? LoadSequenceRole;
                Console.WriteLine(FormattableString.Invariant($"MP-TESTLAUF role={role} run={runId:N} result=station station={row.Station} evidence={row.Evidence} world={row.World} gen={row.Generation}"));
                if (row.Station == GuidedRunStation.Reload && current is not null)
                    _ = T3PublishStatsAsync(_guidedRunLifetime);
            }
            if (reply.Completed is { } completed)
                Console.WriteLine($"MP-TESTLAUF role={completed.Role} run={completed.RunId:N} result=complete stations={completed.Stations.Count}");
            GuidedRunSay(reply.Text);
        }
        if (reply.Completed is { } report)
            _ = Task.Run(() => GuidedRunCollectAsync(report, _guidedRunLifetime));
    }

    private async Task GuidedRunCollectAsync(GuidedRunReport report, CancellationToken ct)
    {
        string text;
        try
        {
            text = await CollectGuidedRunAsync(report, ct);
            Console.WriteLine($"MP-TESTLAUF role={report.Role} run={report.RunId:N} result=collected");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"MP-TESTLAUF role={report.Role} run={report.RunId:N} result=collection-failed error={ex.GetType().Name}");
            text = "Logs und Ampel konnten nicht vollständig erstellt werden.";
        }
        lock (_guidedCommands)
            if (_guidedRun.Collected(report.RunId, text)) GuidedRunSay(text);
    }

    private void GuidedRunSay(string text)
    {
        Console.WriteLine("[Testlauf] " + text);
        long revision = Interlocked.Increment(ref _guidedUiRevision);
        _ = GuidedRunSayAsync(revision, text);
    }

    private async Task GuidedRunSayAsync(long revision, string text)
    {
        await _guidedUi.WaitAsync();
        try
        {
            if (revision != Interlocked.Read(ref _guidedUiRevision) || _guidedRunLifetime.IsCancellationRequested) return;
            await ExecLuaAsync($"if KCD2MP_ShowInteractionMsg then KCD2MP_ShowInteractionMsg(\"{EscapeLua(text)}\") end");
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException) { }
        finally { _guidedUi.Release(); }
    }

    private void GuidedRunObserveLoad(WorldLoadSnapshot after)
    {
        if (GuidedRunEnabled && Volatile.Read(ref _presentationSession) is { } link)
            _guidedRun.ObserveLoad(after, link.Relay);
    }

    private void GuidedRunObserveLoss(Stream link, byte? peer = null)
    {
        if (!GuidedRunEnabled) return;
        var world = _worldLoad.Current;
        _guidedRun.ObserveLoss(new(world.HostWorld, world.Generation), link, peer);
    }

    private void GuidedRunObserveReconnect(string role, long serial, WorldSyncScope scope, string result, long elapsed, bool reload)
    {
        if (GuidedRunEnabled) _guidedRun.ObserveReconnect(role, scope, serial, result, elapsed, reload);
    }
}
