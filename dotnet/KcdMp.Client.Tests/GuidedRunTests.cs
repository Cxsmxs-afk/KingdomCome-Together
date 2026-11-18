// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public sealed class GuidedRunTests
{
    private static readonly DateTimeOffset Started = new(2026, 11, 18, 18, 0, 0, TimeSpan.Zero);
    private static readonly WorldSyncScope Initial = new(5, 16), Reloaded = new(6, 17);

    private static GuidedRunContext Context(Stream link, WorldSyncScope scope, uint? baseline = 24) =>
        new("joiner", scope, 2, link, baseline);

    [Theory]
    [InlineData("1", "--test-guided-run start", true)]
    [InlineData("1", "--test-guided-run status", true)]
    [InlineData("1", "--test-guided-run weiter", true)]
    [InlineData("1", "--test-guided-run weiter ja", true)]
    [InlineData("1", "--test-guided-run weiter nein", true)]
    [InlineData("1", "--test-guided-run wiederholen", true)]
    [InlineData("1", "--test-guided-run abbruch", true)]
    [InlineData(null, "--test-guided-run start", false)]
    [InlineData("true", "--test-guided-run start", false)]
    [InlineData("1", "start", false)]
    [InlineData("1", "--test-guided-run start ja", false)]
    [InlineData("1", "--test-guided-run weiter yes", false)]
    [InlineData("1", "--test-guided-run weiter ja extra", false)]
    [InlineData("1", "--test-guided-run  start", false)]
    public void Every_command_requires_both_explicit_test_switches_and_a_supported_operand(string? environment, string input, bool valid)
        => Assert.Equal(valid, GuidedRunCommand.TryParse(input, environment, out _));

    [Fact]
    public void Manual_stations_need_an_active_matching_partner_and_an_explicit_observation()
    {
        using var link = new MemoryStream();
        using var other = new MemoryStream();
        var state = new GuidedRun();
        var initial = Context(link, Initial);
        Assert.Null(state.Command(new(GuidedRunAction.Start), null, Started).Started);
        Assert.Null(state.Command(new(GuidedRunAction.Start), initial with { Role = "unknown" }, Started).Started);
        Assert.NotNull(state.Command(new(GuidedRunAction.Start), initial, Started).Started);
        Assert.Null(state.Command(new(GuidedRunAction.Start), initial, Started).Started);
        Assert.Null(state.Command(new(GuidedRunAction.Next), initial, Started).Station);
        Assert.Null(state.Command(new(GuidedRunAction.Next, true), initial with { Link = other }, Started).Station);
        Assert.Null(state.Command(new(GuidedRunAction.Next, true), initial with { Partner = 3 }, Started).Station);
        Assert.Null(state.Command(new(GuidedRunAction.Next, true), initial with { Scope = Reloaded }, Started).Station);
        Assert.Null(state.Command(new(GuidedRunAction.Repeat), initial, Started).Station);

        var at = Started.AddSeconds(15);
        var first = state.Command(new(GuidedRunAction.Next, false), initial, at);
        Assert.Equal(GuidedRunStation.See, first.Station!.Value.Station);
        Assert.Equal(GuidedRunEvidence.ManualNo, first.Station.Value.Evidence);
        Assert.Equal(at, first.Station.Value.RecordedAtUtc);
        Assert.Contains("Laufen", first.Text);
        var aborted = state.Command(new(GuidedRunAction.Abort), initial, at);
        Assert.Null(aborted.Completed);
        Assert.Equal(first.RunId, aborted.RunId);
        Assert.Equal(initial.Role, aborted.AbortedRole);
        Assert.Null(state.Command(new(GuidedRunAction.Next, true), initial, at).Station);
    }

    [Fact]
    public void Reload_needs_an_actual_new_load_and_its_confirmed_baseline_for_automatic_evidence()
    {
        using var link = new MemoryStream();
        using var other = new MemoryStream();
        var state = new GuidedRun();
        var old = Context(link, Initial);
        var loaded = Context(link, Reloaded);
        GoToReload(state, old);
        Assert.Null(state.Command(new(GuidedRunAction.Next), loaded, Started).Station);
        state.ObserveLoad(new(Reloaded.Generation, WorldLoadPhase.Offer, 0, Reloaded.HostWorld), link);
        Assert.Null(state.Command(new(GuidedRunAction.Next), loaded, Started).Station);
        state.ObserveLoad(new(Reloaded.Generation, WorldLoadPhase.Load, 0, Reloaded.HostWorld), other);
        Assert.Null(state.Command(new(GuidedRunAction.Next), loaded, Started).Station);
        state.ObserveLoad(new(Reloaded.Generation, WorldLoadPhase.Load, 0, Reloaded.HostWorld), link);
        Assert.Null(state.Command(new(GuidedRunAction.Next), loaded with { BaselineSnapshot = null }, Started).Station);

        var result = state.Command(new(GuidedRunAction.Next), loaded, Started).Station!.Value;
        Assert.Equal(GuidedRunStation.Reload, result.Station);
        Assert.Equal(GuidedRunEvidence.Observed, result.Evidence);
        Assert.Equal(Reloaded.HostWorld, result.World);
        Assert.Equal(Reloaded.Generation, result.Generation);
        Assert.Equal(24u, result.BaselineSnapshot);
    }

    [Fact]
    public void Disconnect_uses_the_retained_scope_and_exact_matching_recovery_before_a_single_collection()
    {
        using var link = new MemoryStream();
        using var newLink = new MemoryStream();
        var state = new GuidedRun();
        var id = GoToReload(state, Context(link, Initial));
        var current = Context(link, Reloaded);
        state.ObserveLoad(new(Reloaded.Generation, WorldLoadPhase.Load, 0, Reloaded.HostWorld), link);
        Assert.NotNull(state.Command(new(GuidedRunAction.Next), current, Started).Station);

        state.ObserveReconnect("joiner", Initial, 10, "started", 0, reload: false);
        state.ObserveReconnect("joiner", Initial, 10, "same-world", 12_000, reload: false);
        Assert.Null(state.Command(new(GuidedRunAction.Next), current, Started).Station);
        state.ObserveReconnect("joiner", Reloaded, 10, "started", 0, reload: false);
        state.ObserveReconnect("host", Reloaded, 10, "same-world", 12_000, reload: false);
        state.ObserveReconnect("joiner", Reloaded, 11, "same-world", 12_000, reload: false);
        state.ObserveReconnect("joiner", Reloaded, 10, "same-world", 30_001, reload: false);
        state.ObserveReconnect("joiner", Reloaded, 10, "same-world", 12_000, reload: true);
        Assert.Null(state.Command(new(GuidedRunAction.Next), current, Started).Station);
        state.ObserveReconnect("joiner", Reloaded, 10, "same-world", 12_000, reload: false);
        Assert.Null(state.Command(new(GuidedRunAction.Next), current with { Scope = Initial }, Started).Station);
        Assert.Null(state.Command(new(GuidedRunAction.Next), null, Started).Station);

        var exact = current with { Link = newLink, Partner = 7 };
        var doneAt = Started.AddMinutes(12);
        var completions = new ConcurrentBag<GuidedRunReport>();
        Parallel.For(0, 12, _ =>
        {
            var report = state.Command(new(GuidedRunAction.Next), exact, doneAt).Completed;
            if (report is not null) completions.Add(report);
        });
        var completed = Assert.Single(completions);
        Assert.Equal(id, completed.RunId);
        Assert.Equal("joiner", completed.Role);
        Assert.Equal(Initial.HostWorld, completed.StartWorld);
        Assert.Equal(Initial.Generation, completed.StartGeneration);
        Assert.Equal(Started, completed.StartedAtUtc);
        Assert.Equal(doneAt, completed.CompletedAtUtc);
        Assert.Equal(Enum.GetValues<GuidedRunStation>(), completed.Stations.Select(row => row.Station));
        Assert.Equal(12_000, completed.Stations[^1].ReconnectMs);
        Assert.Null(state.Command(new(GuidedRunAction.Start), exact, doneAt).Started);
        Assert.False(state.Collected(Guid.NewGuid(), "falscher Lauf"));
        Assert.True(state.Collected(id, "Logs und Ampel geschrieben."));
        Assert.Contains("geschrieben", state.Command(new(GuidedRunAction.Status), null, doneAt).Text);
    }

    [Fact]
    public void After_a_real_scoped_disconnect_a_manual_result_can_finish_offline_without_claiming_recovery()
    {
        using var link = new MemoryStream();
        using var staleLink = new MemoryStream();
        var state = new GuidedRun();
        var context = Context(link, Initial);
        GoToReload(state, context);
        var reload = state.Command(new(GuidedRunAction.Next, false), context, Started);
        Assert.Equal(GuidedRunEvidence.ManualNo, reload.Station!.Value.Evidence);
        Assert.Null(state.Command(new(GuidedRunAction.Next, true), null, Started).Station);
        state.ObserveLoss(Initial, staleLink, peer: 2);
        state.ObserveLoss(Initial, link, peer: 3);
        Assert.Null(state.Command(new(GuidedRunAction.Next, true), null, Started).Station);
        state.ObserveLoss(Initial, link, peer: 2);
        Assert.Null(state.Command(new(GuidedRunAction.Next), null, Started).Station);
        var completed = state.Command(new(GuidedRunAction.Next, true), null, Started).Completed;
        var disconnect = Assert.IsType<GuidedRunReport>(completed).Stations[^1];
        Assert.Equal(GuidedRunEvidence.ManualYes, disconnect.Evidence);
        Assert.Null(disconnect.ReconnectMs);
    }

    private static Guid GoToReload(GuidedRun state, GuidedRunContext context)
    {
        var start = state.Command(new(GuidedRunAction.Start), context, Started);
        Assert.NotNull(start.Started);
        for (int i = 0; i < 5; i++)
        {
            var station = state.Command(new(GuidedRunAction.Next, i != 2), context, Started.AddSeconds(i + 1)).Station;
            Assert.Equal((GuidedRunStation)i, station!.Value.Station);
        }
        return start.Started.GetValueOrDefault();
    }
}
