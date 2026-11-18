// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
namespace KcdMp.LogPack.Tests;

public class RecoveryVerdictTests
{
    private const string Scope = "section=recovery recovery_world=8 recovery_gen=23 recovery_run=101";
    private const string Clean = "digest_checked_body=2 digest_mismatch_body=0 resync_body=0 resync_failed_body=0";
    private const string Reconnect = "reconnect_ms=30000 reconnect_same_world=1 reconnect_success=1 reconnect_timeout=0 reconnect_reload=0";

    [Theory]
    [InlineData("digest_mismatch_body=0 resync_body=0", VerdictStatus.Grey, VerdictStatus.Grey)]
    [InlineData("digest_checked_body=0 digest_mismatch_body=0 resync_body=0", VerdictStatus.Grey, VerdictStatus.Grey)]
    [InlineData("digest_checked_body=2 digest_mismatch_body=0 resync_weather=0", VerdictStatus.Grey, VerdictStatus.Grey)]
    [InlineData("digest_checked_npc=2 digest_mismatch_body=0 resync_body=0", VerdictStatus.Grey, VerdictStatus.Grey)]
    [InlineData(Clean, VerdictStatus.Green, VerdictStatus.Grey)]
    [InlineData("digest_checked_body=3 digest_mismatch_body=2 resync_body=1", VerdictStatus.Yellow, VerdictStatus.Yellow)]
    [InlineData("digest_checked_body=3 digest_mismatch_body=2 resync_body=2", VerdictStatus.Green, VerdictStatus.Green)]
    [InlineData("digest_checked_body=1 digest_mismatch_body=2 resync_body=2", VerdictStatus.Grey, VerdictStatus.Grey)]
    [InlineData("digest_checked_body=3 digest_mismatch_body=1 resync_body=2", VerdictStatus.Grey, VerdictStatus.Grey)]
    [InlineData("resync_failed_body=1", VerdictStatus.Red, VerdictStatus.Red)]
    public void Digest_compares_coverage_mismatches_and_confirmed_repairs_in_the_same_scope(string fields,
        VerdictStatus mismatch, VerdictStatus repair)
    {
        var report = Read(Agent(fields));
        Assert.Equal(mismatch, Row(report, "digest_mismatch_body").Status);
        Assert.Equal(repair, Row(report, "resync_body").Status);
        Assert.Equal(VerdictStatus.Grey, Row(report, "digest_mismatch_npc").Status);
    }

    [Theory]
    [InlineData("body")]
    [InlineData("npc")]
    [InlineData("corpse")]
    [InlineData("loot")]
    [InlineData("clock")]
    [InlineData("weather")]
    public void Each_repaired_area_uses_its_own_counters_without_adding_heartbeats_or_mirrors(string area)
    {
        string before = $"digest_checked_{area}=1 digest_mismatch_{area}=1 resync_{area}=0 resync_failed_{area}=0";
        string after = $"digest_checked_{area}=3 digest_mismatch_{area}=1 resync_{area}=1 resync_failed_{area}=0";
        var input = new Briefing.Input();
        input.Logs["agent.log"] = [Agent(before), Agent(after)];
        input.Logs["kcdmp-native.log"] = [Mirror(before), Mirror(after)];
        var report = VerdictRules.Evaluate(input);
        Assert.Equal((VerdictStatus.Green, "3"), State(report, "digest_checked_" + area));
        Assert.Equal((VerdictStatus.Green, "1"), State(report, "digest_mismatch_" + area));
        Assert.Equal((VerdictStatus.Green, "1"), State(report, "resync_" + area));
    }

    [Fact]
    public void A_new_run_in_the_same_generation_cannot_borrow_comparisons_from_the_previous_run()
    {
        const string duplicateWorld = "section=recovery recovery_world=8 recovery_gen=23 recovery_run=102";
        var input = new Briefing.Input();
        input.Logs["agent.log"] = [Agent(Clean), Agent("digest_mismatch_body=0 resync_body=0", duplicateWorld)];
        input.Logs["kcdmp-native.log"] = [Mirror(Clean)];
        Assert.Equal(VerdictStatus.Grey, Row(VerdictRules.Evaluate(input), "digest_mismatch_body").Status);
    }

    [Fact]
    public void A_terminal_repair_failure_survives_a_new_world_and_run_in_the_same_launch()
    {
        const string otherWorld = "section=recovery recovery_world=9 recovery_gen=24 recovery_run=102";
        var report = Read(Agent("digest_checked_body=3 digest_mismatch_body=2 resync_body=1 resync_failed_body=1"),
            Agent(Clean, otherWorld));
        Assert.Equal((VerdictStatus.Red, "2"), State(report, "digest_mismatch_body"));
        Assert.Equal((VerdictStatus.Red, "1"), State(report, "resync_body"));
        Assert.Equal((VerdictStatus.Red, "1"), State(report, "resync_failed_body"));
    }

    [Theory]
    [InlineData("kcdmp-native.log", "MP-T3-STATS origin=dll role=joiner ", Scope)]
    [InlineData("kcdmp-native.log", "[00:00:01.500] t000001 L000002 nested log MP-T3-STATS origin=dll source=agent role=joiner ", Scope)]
    [InlineData("kcdmp-native.log", "MP-T3-STATS origin=dll source=agent role=host ", Scope)]
    [InlineData("agent.log", "Injected MP-T3-STATS origin=agent role=joiner ", Scope)]
    [InlineData("agent.log", "MP-T3-STATS origin=agent role=joiner ", "section=events recovery_world=8 recovery_gen=23 recovery_run=101")]
    [InlineData("agent.log", "MP-T3-STATS origin=agent role=joiner ", "section=recovery recovery_world=8 recovery_gen=23")]
    [InlineData("agent.log", "MP-T3-STATS origin=agent role=joiner ", "section=recovery recovery_world=0 recovery_gen=23 recovery_run=101")]
    [InlineData("agent.log", "MP-T3-STATS origin=agent role=joiner ", "section=recovery recovery_world=8 recovery_gen=0 recovery_run=101")]
    public void Recovery_ignores_unqualified_sources_sections_roles_and_scopes(string log, string prefix, string scope)
    {
        var input = new Briefing.Input();
        input.Logs["agent.log"] = ["MP-WO137-STATS role=joiner"];
        input.Logs[log] = [.. input.Logs.GetValueOrDefault(log) ?? [], prefix + scope + " resync_failed_body=1 reconnect_timeout=1"];
        var report = VerdictRules.Evaluate(input);
        Assert.Equal(VerdictStatus.Grey, Row(report, "digest_mismatch_body").Status);
        Assert.Equal(VerdictStatus.Grey, Row(report, "reconnect_ms").Status);
    }

    [Fact]
    public void A_current_dll_mirror_can_supply_evidence_when_the_agent_report_is_unavailable()
    {
        var input = new Briefing.Input();
        input.Logs["kcdmp-native.log"] = [
            "[00:00:00.500] t000001 L000001 MP-T3-STATS origin=dll source=agent role=joiner " + Scope + " resync_failed_body=1 reconnect_timeout=1",
            Mirror(Clean + " " + Reconnect)];
        var report = VerdictRules.Evaluate(input);
        Assert.Equal(VerdictStatus.Green, Row(report, "digest_mismatch_body").Status);
        Assert.Equal(VerdictStatus.Green, Row(report, "reconnect_ms").Status);
    }

    [Theory]
    [InlineData("", VerdictStatus.Grey, VerdictStatus.Grey, VerdictStatus.Grey, "–")]
    [InlineData("reconnect_ms=0 reconnect_same_world=0 reconnect_success=0 reconnect_timeout=0 reconnect_reload=0",
        VerdictStatus.Grey, VerdictStatus.Grey, VerdictStatus.Grey, "–")]
    [InlineData("reconnect_ms=2 reconnect_same_world=1 reconnect_success=1 reconnect_timeout=0",
        VerdictStatus.Grey, VerdictStatus.Grey, VerdictStatus.Grey, "2 ms")]
    [InlineData("reconnect_ms=2 reconnect_same_world=1 reconnect_success=0 reconnect_timeout=0 reconnect_reload=0",
        VerdictStatus.Grey, VerdictStatus.Grey, VerdictStatus.Grey, "2 ms")]
    [InlineData("reconnect_ms=2 reconnect_same_world=0 reconnect_success=1 reconnect_timeout=0 reconnect_reload=1",
        VerdictStatus.Grey, VerdictStatus.Grey, VerdictStatus.Grey, "2 ms")]
    [InlineData(Reconnect, VerdictStatus.Green, VerdictStatus.Green, VerdictStatus.Green, "30000 ms")]
    [InlineData("reconnect_ms=30001 reconnect_same_world=1 reconnect_success=1 reconnect_timeout=0 reconnect_reload=0",
        VerdictStatus.Red, VerdictStatus.Grey, VerdictStatus.Grey, "30001 ms")]
    [InlineData("reconnect_ms=5000 reconnect_same_world=1 reconnect_success=1 reconnect_timeout=0 reconnect_reload=1",
        VerdictStatus.Red, VerdictStatus.Red, VerdictStatus.Grey, "5000 ms")]
    [InlineData("reconnect_ms=30000 reconnect_same_world=0 reconnect_success=0 reconnect_timeout=1 reconnect_reload=0",
        VerdictStatus.Red, VerdictStatus.Grey, VerdictStatus.Red, "30000 ms")]
    public void Recovery_grades_only_confirmed_same_world_attempts_and_retains_known_failures(string fields,
        VerdictStatus timing, VerdictStatus reload, VerdictStatus timeout, string ms)
    {
        var report = Read(Agent(fields));
        Assert.Equal((timing, ms), State(report, "reconnect_ms"));
        Assert.Equal(reload, Row(report, "reconnect_reload").Status);
        Assert.Equal(timeout, Row(report, "reconnect_timeout").Status);
    }

    [Fact]
    public void Later_success_keeps_an_earlier_timeout_or_same_world_reload_red()
    {
        var report = Read(Agent("reconnect_timeout=1 reconnect_ms=30000"),
            Agent("reconnect_ms=1000 reconnect_same_world=1 reconnect_reload=1"),
            Agent(Reconnect, "section=recovery recovery_world=9 recovery_gen=24 recovery_run=102"));
        Assert.Equal((VerdictStatus.Red, "1000 ms"), State(report, "reconnect_ms"));
        Assert.Equal((VerdictStatus.Red, "1"), State(report, "reconnect_reload"));
        Assert.Equal((VerdictStatus.Red, "1"), State(report, "reconnect_timeout"));
    }

    [Theory]
    [InlineData("MP-WORLD-DIGEST role=joiner world=8 gen=23 area=corpse result=mismatch")]
    [InlineData("MP-WORLD-RECONNECT role=joiner world=8 gen=23 same_world=1 result=complete")]
    public void Collection_keeps_recovery_scenario_evidence(string line) => Assert.True(NoiseRules.IsProtected(line));

    private static string Agent(string fields, string scope = Scope) => $"MP-T3-STATS origin=agent role=joiner {scope} {fields}";

    private static string Mirror(string fields) =>
        $"[00:00:01.500] t000001 L000002 MP-T3-STATS origin=dll source=agent role=joiner {Scope} {fields}";

    private static VerdictReport Read(params string[] lines)
    {
        var input = new Briefing.Input();
        input.Logs["agent.log"] = lines;
        return VerdictRules.Evaluate(input);
    }

    private static VerdictRow Row(VerdictReport report, string id) => Assert.Single(report.Rows, row => row.Id == id);

    private static (VerdictStatus, string) State(VerdictReport report, string id)
    {
        var row = Row(report, id);
        return (row.Status, row.Measurement);
    }
}
