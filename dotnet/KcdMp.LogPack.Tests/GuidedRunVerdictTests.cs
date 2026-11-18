// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Text;

namespace KcdMp.LogPack.Tests;

public sealed class GuidedRunVerdictTests
{
    private static readonly Guid Run = Guid.Parse("c1f14246-55bd-4264-9faa-60b238c9af4a");

    private static VerdictReport Read(SourceFile? receipt, params string[] agent)
    {
        var lines = new SourceFile("agent.log", Bytes: Encoding.UTF8.GetBytes(string.Join('\n', agent)));
        return VerdictRules.Evaluate(Briefing.Read(receipt is null ? [lines] : [lines, receipt]));
    }

    [Theory]
    [InlineData(2, 5, 0, VerdictStatus.Green, VerdictStatus.Green)]
    [InlineData(2, 4, 1, VerdictStatus.Red, VerdictStatus.Red)]
    [InlineData(1, 4, 0, VerdictStatus.Grey, VerdictStatus.Grey)]
    public void Guide_protocol_is_separate_from_queue_and_respects_explicit_negative_observations(
        int observed, int yes, int no, VerdictStatus expected, VerdictStatus total)
    {
        var report = Read(GuidedRunVerdictEvidence.Create(Run, "joiner", observed, yes, no),
            "MP-WO137-STATS role=joiner",
            "MP-W207-Q lag_ms=15");
        var row = Assert.Single(report.Rows, r => r.Id == GuidedRunVerdictEvidence.RuleId);
        Assert.Equal(expected, row.Status);
        Assert.Equal(total, report.Status);
        Assert.Contains($"technisch {observed}, manuell {yes} Ja und {no} Nein", row.Measurement, StringComparison.Ordinal);
        Assert.Equal(VerdictStatus.Green, Assert.Single(report.Rows, r => r.Id == "queue").Status);
    }

    [Fact]
    public void A_completed_manual_protocol_does_not_establish_measured_world_activity()
    {
        var report = Read(GuidedRunVerdictEvidence.Create(Run, "host", 0, 7, 0), "MP-WO137-STATS role=host");
        Assert.Equal(VerdictStatus.Grey, report.Status);
        Assert.Equal(VerdictStatus.Grey, Assert.Single(report.Rows, r => r.Id == "owner_violations").Status);
        Assert.Equal(VerdictStatus.Grey, Assert.Single(report.Rows, r => r.Id == "reconnect_ms").Status);
    }

    [Fact]
    public void Missing_or_wrong_role_receipt_does_not_certify_the_local_guide()
    {
        var unrelated = Read(GuidedRunVerdictEvidence.Create(Run, "host", 2, 5, 0), "MP-WO137-STATS role=joiner");
        Assert.Equal(VerdictStatus.Grey, Assert.Single(unrelated.Rows, r => r.Id == GuidedRunVerdictEvidence.RuleId).Status);
        Assert.DoesNotContain(Read(null, "MP-WO137-STATS role=joiner").Rows, r => r.Id == GuidedRunVerdictEvidence.RuleId);
    }

    [Theory]
    [InlineData("observed=1 manual_yes=7 manual_no=1")]
    [InlineData("observed=1 manual_yes=4 manual_no=2\nsecond record")]
    [InlineData("observed=1 manual_yes=4 manual_no=2 unrecognized")]
    public void Invalid_receipt_stays_grey(string fields)
    {
        var receipt = new SourceFile(GuidedRunVerdictEvidence.EntryName,
            Bytes: Encoding.UTF8.GetBytes($"MP-TESTLAUF run={Run:N} role=host {fields}\n"));
        Assert.Equal(VerdictStatus.Grey, Assert.Single(Read(receipt, "MP-WO137-STATS role=host").Rows,
            r => r.Id == GuidedRunVerdictEvidence.RuleId).Status);
    }

    [Fact]
    public void Guided_run_trace_is_retained_in_the_bundle()
    {
        Assert.True(NoiseRules.IsProtected("00:00:01.030 MP-TESTLAUF role=joiner action=station"));
        Assert.Throws<ArgumentException>(() => GuidedRunVerdictEvidence.Create(Run, "off", 5, 2, 0));
        Assert.Throws<ArgumentException>(() => GuidedRunVerdictEvidence.Create(Run, "host", 3, 5, 0));
    }
}
