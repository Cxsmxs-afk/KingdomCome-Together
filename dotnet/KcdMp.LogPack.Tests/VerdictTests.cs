// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Text;

namespace KcdMp.LogPack.Tests;

public class VerdictTests
{
    [Theory]
    [InlineData("12:00:00.000 MP-W207-Q lag_ms=230000 waiting=230 by=- act_lag_ms=18\n", "ROT")]
    [InlineData("12:00:00.000 MP-W207-Q lag_ms=25 waiting=0 by=- act_lag_ms=18\n", "GRÜN")]
    [InlineData("", "GRAU")]
    public void The_same_verdict_is_in_the_briefing_and_the_packed_result(string log, string expected)
    {
        var folder = Path.Combine(Path.GetTempPath(), "kcdmp-verdict-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bundle = Packer.Pack([new("agent.log", Bytes: Encoding.UTF8.GetBytes(log))], folder, "verdict");
            var files = PackerTests.Open(Assert.Single(bundle.Parts));
            var verdict = Encoding.UTF8.GetString(files["ERGEBNIS.txt"]).ReplaceLineEndings("\n");
            Assert.StartsWith($"KCDMP-ERGEBNIS\t1\nGesamt\t{expected}\nRolle\tunbekannt\nRegel\tStatus\tMesswert\tBeleg\n", verdict);
            Assert.Contains($"\nqueue\t{expected}\t", verdict);
            var briefing = Encoding.UTF8.GetString(files["BRIEFING.md"]);
            var heading = briefing.IndexOf("## Ergebnis", StringComparison.Ordinal);
            Assert.True(heading >= 0 && heading < briefing.IndexOf("## The queue", StringComparison.Ordinal));
            Assert.Contains(expected, briefing[heading..briefing.IndexOf("## The queue", StringComparison.Ordinal)]);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void A_stall_in_a_previous_game_launch_never_decides_this_launch()
    {
        var input = new Briefing.Input();
        input.Logs["agent.prev.log"] = ["12:00:00.000 MP-LAUNCH id=older", "12:01:00.000 MP-W207-Q lag_ms=230000 waiting=230"];
        input.Logs["agent.log"] = ["13:00:00.000 MP-LAUNCH id=current", "13:01:00.000 MP-W207-Q lag_ms=42 waiting=0"];
        Assert.Contains("Gesamt\tGRÜN", VerdictRules.Evaluate(input).ToText());
        input.Logs["agent.prev.log"][0] = "12:00:00.000 MP-LAUNCH id=current";
        Assert.Contains("Gesamt\tROT", VerdictRules.Evaluate(input).ToText());
    }

    [Theory]
    [InlineData("12:00:00.000 [ping] 24 ms (network)", VerdictStatus.Grey)]
    [InlineData("12:00:00.000 [ping] 24 ms (network), the game's queue is 230000 ms behind", VerdictStatus.Red)]
    public void A_legacy_ping_requires_an_explicit_queue_delay(string line, VerdictStatus expected)
    {
        var input = new Briefing.Input();
        input.Logs["agent.log"] = [line];
        Assert.Equal(expected, VerdictRules.Evaluate(input).Status);
    }

    [Theory]
    [InlineData("owner_violations=0", VerdictStatus.Grey)]
    [InlineData("owner_violations=0 rp_owner_n=0", VerdictStatus.Grey)]
    [InlineData("owner_violations=0 rp_owner_n=50", VerdictStatus.Grey)]
    [InlineData("owner_violations=0 rp_owner_n=50 owner_handovers=2", VerdictStatus.Grey)]
    [InlineData("owner_violations=0 rp_owner_n=50 owner_handovers=2 owner_denied_sets=x", VerdictStatus.Grey)]
    [InlineData("owner_violations=0 rp_owner_n=0 owner_handovers=0 owner_denied_sets=0", VerdictStatus.Grey)]
    [InlineData("owner_violations=0 rp_owner_n=50 owner_handovers=2 owner_denied_sets=10", VerdictStatus.Green)]
    [InlineData("owner_violations=1", VerdictStatus.Red)]
    [InlineData("owner_violations=1 rp_owner_n=0 owner_handovers=0 owner_denied_sets=0", VerdictStatus.Red)]
    [InlineData("owner_violations=0 owner_violations=1", VerdictStatus.Grey)]
    public void Owner_observations_are_explicit_and_ambiguous_samples_never_pass(string fields, VerdictStatus expected)
    {
        var input = new Briefing.Input();
        input.Logs["agent.log"] = [$"12:00:00.000 MP-T3-STATS origin=agent role=joiner {fields}"];
        var row = Assert.Single(VerdictRules.Evaluate(input).Rows, row => row.Id == "owner_violations");
        Assert.Equal(expected, row.Status);
    }

    [Theory]
    [InlineData(999, 0, VerdictStatus.Grey)]
    [InlineData(0, 30, VerdictStatus.Green)]
    [InlineData(33, 30, VerdictStatus.Green)]
    [InlineData(34, 30, VerdictStatus.Red)]
    public void Realtime_age_requires_lane_observations_and_a_p95_below_34_ms(int age, int count, VerdictStatus expected)
    {
        var input = new Briefing.Input();
        input.Logs["kcdmp-native.log"] = [$"MP-T3-STATS origin=dll source=agent role=host edge_rt_age_ms_p95={age} edge_rt_n={count}"];
        var row = Assert.Single(VerdictRules.Evaluate(input).Rows, row => row.Id == "edge_rt_age_ms_p95");
        Assert.Equal(expected, row.Status);
    }

    [Theory]
    [InlineData("proc_awaits_per_frame_max", "edge_proc_n", 0, 0, VerdictStatus.Grey)]
    [InlineData("proc_awaits_per_frame_max", "edge_proc_n", 0, 80, VerdictStatus.Green)]
    [InlineData("proc_awaits_per_frame_max", "edge_proc_n", 1, 0, VerdictStatus.Red)]
    [InlineData("http_calls_rt", "edge_rt_n", 0, 0, VerdictStatus.Grey)]
    [InlineData("http_calls_rt", "edge_rt_n", 0, 30, VerdictStatus.Green)]
    [InlineData("http_calls_rt", "edge_rt_n", 1, 30, VerdictStatus.Red)]
    public void Realtime_invariants_need_exercise_for_a_pass_and_keep_observed_violations(string metric, string coverage, int value, int count, VerdictStatus expected)
    {
        var input = new Briefing.Input();
        input.Logs["agent.log"] = [$"12:00:00.000 MP-T3-STATS origin=agent role=joiner {metric}={value} {coverage}={count}"];
        var row = Assert.Single(VerdictRules.Evaluate(input).Rows, row => row.Id == metric);
        Assert.Equal(expected, row.Status);
    }

    [Fact]
    public void Bulk_age_busy_replies_and_IPC_fallbacks_are_diagnostic_only()
    {
        var input = new Briefing.Input();
        input.Logs["kcdmp-native.log"] = [
            "MP-T3-STATS origin=dll source=agent role=joiner edge_bulk_age_ms_p95=47 edge_bulk_n=50 edge_rt_n=0 pipe_busy_replies=2 inproc_fallbacks=3 edge_proc_n=0"];
        var report = VerdictRules.Evaluate(input).ToText();
        Assert.Contains("edge_bulk_age_ms_p95\tGRAU\t47 ms\tMP-T3-STATS, DLL-Spiegel, n=50", report);
        Assert.Contains("pipe_busy_replies\tGRAU\t2\tMP-T3-STATS, DLL-Spiegel, n=50", report);
        Assert.Contains("inproc_fallbacks\tGRAU\t3\tMP-T3-STATS, DLL-Spiegel, ohne Grenzwert", report);
    }

    [Theory]
    [InlineData(0, 0, VerdictStatus.Grey)]
    [InlineData(140, 0, VerdictStatus.Grey)]
    [InlineData(0, 10, VerdictStatus.Green)]
    [InlineData(19, 10, VerdictStatus.Green)]
    [InlineData(20, 10, VerdictStatus.Yellow)]
    [InlineData(49, 10, VerdictStatus.Yellow)]
    [InlineData(50, 10, VerdictStatus.Red)]
    public void Timeline_skew_needs_a_paired_observation_and_checks_the_exact_boundaries(int skew, int paired, VerdictStatus expected)
    {
        var input = new Briefing.Input();
        input.Logs["kcdmp-native.log"] = [
            $"MP-T3-STATS origin=dll source=agent role=host rp_action_pos_skew_ms_p95={skew} rp_timeline_skew_n={paired} rp_action_n=30 rp_timeline_pose_n=50"];
        var row = Assert.Single(VerdictRules.Evaluate(input).Rows, row => row.Id == "rp_action_pos_skew_ms_p95");
        Assert.Equal(expected, row.Status);
    }

    [Theory]
    [InlineData("rp_buffer_ms", 59, VerdictStatus.Red)]
    [InlineData("rp_buffer_ms", 60, VerdictStatus.Green)]
    [InlineData("rp_buffer_ms", 120, VerdictStatus.Green)]
    [InlineData("rp_buffer_ms", 121, VerdictStatus.Red)]
    [InlineData("rp_extrap_ms_max", 200, VerdictStatus.Green)]
    [InlineData("rp_extrap_ms_max", 201, VerdictStatus.Red)]
    public void Timeline_buffer_and_extrapolation_report_observed_bounds(string metric, int value, VerdictStatus expected)
    {
        var input = new Briefing.Input();
        input.Logs["kcdmp-native.log"] = [
            $"MP-T3-STATS origin=dll source=agent role=joiner {metric}={value} rp_timeline_pose_n=450"];
        var row = Assert.Single(VerdictRules.Evaluate(input).Rows, row => row.Id == metric);
        Assert.Equal(expected, row.Status);
    }

    [Fact]
    public void Buffer_outside_lower_bound_remains_visible_after_later_measured_valid_buffer()
    {
        var input = new Briefing.Input();
        input.Logs["agent.log"] = [
            "12:00:00.000 MP-T3-STATS origin=agent role=joiner rp_buffer_ms=59 rp_timeline_pose_n=10 rp_late_samples=2",
            "12:01:00.000 MP-T3-STATS origin=agent role=joiner rp_buffer_ms=90 rp_timeline_pose_n=100 rp_late_samples=8"];
        var report = VerdictRules.Evaluate(input).ToText();
        Assert.Contains("rp_buffer_ms\tROT\t59 ms\tMP-T3-STATS, Agent, n=10", report);
        Assert.Contains("rp_late_samples\tGRAU\t8\tMP-T3-STATS, Agent, n=100", report);
    }

    [Fact]
    public void Owner_handovers_and_denials_from_a_qualified_mirror_have_no_failure_threshold()
    {
        var input = new Briefing.Input();
        input.Logs["agent.log"] = [
            "12:00:00.000 MP-W207-Q lag_ms=25 waiting=0",
            "12:00:00.000 MP-T3-STATS origin=agent role=host owner_violations=0 rp_owner_n=0 owner_handovers=0 owner_denied_sets=0"
        ];
        input.Logs["kcdmp-native.log"] = [
            "MP-T3-STATS origin=dll source=agent role=host owner_violations=0 rp_owner_n=123 owner_handovers=7 owner_denied_sets=42"
        ];
        var verdict = VerdictRules.Evaluate(input);
        Assert.Equal(VerdictStatus.Green, verdict.Status);
        Assert.Contains("owner_violations\tGRÜN\t0\tMP-T3-STATS, DLL-Spiegel, n=123", verdict.ToText());
        Assert.Contains("owner_handovers\tGRAU\t7\tMP-T3-STATS, DLL-Spiegel, n=123, ohne Grenzwert", verdict.ToText());
        Assert.Contains("owner_denied_sets\tGRAU\t42\tMP-T3-STATS, DLL-Spiegel, n=123, ohne Grenzwert", verdict.ToText());
        var violation = new Briefing.Input();
        violation.Logs["agent.log"] = input.Logs["agent.log"];
        violation.Logs["kcdmp-native.log"] = [input.Logs["kcdmp-native.log"][0].Replace("owner_violations=0", "owner_violations=1")];
        Assert.Equal(VerdictStatus.Red, VerdictRules.Evaluate(violation).Status);
    }

    [Fact]
    public void Owner_violation_before_the_next_counters_is_red_only_for_the_current_native_launch()
    {
        const string marker = "WO221-OWNER-VIOLATION eid=0x4A holder=fall path=writer frame=123";
        const string tally = "MP-T3-STATS origin=dll source=agent role=joiner owner_violations=0 rp_owner_n=20 owner_handovers=1 owner_denied_sets=0";
        var input = new Briefing.Input();
        input.Logs["kcdmp-native.log"] = [
            "[12:00:00.000] t000001 Laaaaaa " + marker,
            "[12:01:00.000] t000001 Lbbbbbb " + tally
        ];
        input.Logs["agent.log"] = ["12:01:00.001 " + marker];
        Assert.Contains("owner_violations\tGRÜN\t0\t", VerdictRules.Evaluate(input).ToText());

        var current = new Briefing.Input();
        current.Logs["kcdmp-native.log"] = [.. input.Logs["kcdmp-native.log"],
            "[12:02:00.000] t000001 Lbbbbbb " + marker];
        var verdict = VerdictRules.Evaluate(current);
        Assert.Equal(VerdictStatus.Red, verdict.Status);
        Assert.Contains("owner_violations\tROT\t≥1\tWO221-OWNER-VIOLATION, DLL", verdict.ToText());
        Assert.True(NoiseRules.IsProtected(current.Logs["kcdmp-native.log"][^1]));
    }

    [Theory]
    [InlineData("rp_e2e_ms_p95", "rp_e2e_n", 0, 0, VerdictStatus.Grey, "–")]
    [InlineData("rp_e2e_ms_p95", "rp_e2e_n", 1, 149, VerdictStatus.Green, "149 ms")]
    [InlineData("rp_e2e_ms_p95", "rp_e2e_n", 1, 150, VerdictStatus.Yellow, "150 ms")]
    [InlineData("rp_e2e_ms_p95", "rp_e2e_n", 1, 299, VerdictStatus.Yellow, "299 ms")]
    [InlineData("rp_e2e_ms_p95", "rp_e2e_n", 1, 300, VerdictStatus.Red, "300 ms")]
    [InlineData("rp_agent_hop_ms_p95", "rp_agent_hop_n", 1, 20, VerdictStatus.Green, "20 ms")]
    [InlineData("rp_agent_hop_ms_p95", "rp_agent_hop_n", 1, 21, VerdictStatus.Yellow, "21 ms")]
    [InlineData("rp_step_ratio", "rp_step_n", 1, 1124, VerdictStatus.Grey, "1.124")]
    [InlineData("rp_step_ratio", "rp_step_n", 0, 0, VerdictStatus.Grey, "–")]
    [InlineData("rp_corr_n", "rp_pose_n", 1, 0, VerdictStatus.Grey, "0")]
    [InlineData("rp_corr_cm_p95", "rp_corr_n", 0, 0, VerdictStatus.Grey, "–")]
    [InlineData("rp_action_lag_ms_p95", "rp_action_n", 1, 40, VerdictStatus.Grey, "40 ms")]
    public void Remote_presentation_thresholds_only_grade_qualified_timing_observations(string metric, string count, int samples, int value,
        VerdictStatus status, string measurement)
    {
        var input = new Briefing.Input();
        input.Logs["agent.log"] = [
            $"12:00:00.000 MP-T3-STATS origin=agent role=joiner {metric}=999 {count}=0",
            $"12:01:00.000 MP-T3-STATS origin=agent role=joiner {metric}={value} {count}={samples}"
        ];
        var row = Assert.Single(VerdictRules.Evaluate(input).Rows, row => row.Id == metric);
        Assert.Equal(status, row.Status);
        Assert.Equal(measurement, row.Measurement);
    }

    [Theory]
    [InlineData("MP-RP-STEP rp_step_n=2 rp_step_ratio=1245", "1.245")]
    [InlineData("MP-RP-STEP rp_step_n=0 rp_step_ratio=1245", "–")]
    [InlineData("MP-RP-STEP rp_step_n=2 rp_step_ratio=1.245", "–")]
    [InlineData("MP-RP-STEP rp_step_n=2 rp_step_ratio=1245\nMP-RP-STEP rp_step_n=3 rp_step_ratio=1300", "–")]
    public void Step_ratio_uses_a_scoped_record_from_the_two_game_probe(string record, string expected)
    {
        var input = new Briefing.Input();
        input.Logs["agent.log"] = ["MP-T3-STATS origin=agent role=joiner rp_step_n=0 rp_step_ratio=0"];
        input.Logs["rp-step.log"] = record.Split('\n');
        var row = Assert.Single(VerdictRules.Evaluate(input).Rows, row => row.Id == "rp_step_ratio");
        Assert.Equal(expected, row.Measurement);
        Assert.Equal(VerdictStatus.Grey, row.Status);
        if (expected != "–") Assert.Contains("rp-step.log, n=2", row.Evidence);
    }

    [Fact]
    public void The_pairing_role_comes_from_the_current_agent_and_conflicting_roles_are_unknown()
    {
        var input = new Briefing.Input();
        input.Logs["agent.log"] = ["12:00:00.000 MP-T3-STATS origin=agent role=host proc_stalls=0"];
        input.Logs["kcdmp-native.log"] = ["MP-T3-STATS origin=dll source=agent role=joiner proc_stalls=0"];
        Assert.Contains("\nRolle\thost\n", VerdictRules.Evaluate(input).ToText());
        input.Logs["agent.log"] = [..input.Logs["agent.log"], "12:01:00.000 MP-T3-STATS origin=agent role=joiner proc_stalls=0"];
        Assert.Contains("\nRolle\tunbekannt\n", VerdictRules.Evaluate(input).ToText());
    }

    [Fact]
    public void Repacking_keeps_a_queue_spike_inside_a_run_of_normal_pings()
    {
        var folder = Path.Combine(Path.GetTempPath(), "kcdmp-verdict-" + Guid.NewGuid().ToString("N"));
        try
        {
            const string spike = "12:01:00.000 [ping] 24 ms (network), the game's queue is 230000 ms behind";
            var pingLines = Enumerable.Range(0, 16).Select(i => i == 8 ? spike : "12:01:00.000 [ping] 24 ms (network)");
            var log = "12:00:00.000 MP-W207-Q lag_ms=25 waiting=0\n" + string.Join('\n', pingLines) + "\n";
            var first = Packer.Pack([new("agent.log", Bytes: Encoding.UTF8.GetBytes(log))], folder, "first");
            var files = PackerTests.Open(Assert.Single(first.Parts));
            Assert.Contains(spike, Encoding.UTF8.GetString(files["agent.log"]));
            var repacked = Packer.Pack([new("agent.log", Bytes: files["agent.log"]), new("ERGEBNIS.txt", Bytes: files["ERGEBNIS.txt"])],
                folder, "second");
            Assert.Equal(first.Verdict, repacked.Verdict);
            Assert.Contains("Gesamt\tROT", repacked.Verdict);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("", VerdictStatus.Grey)]
    [InlineData("BOT-T4 role=joiner complete=1 poses=12", VerdictStatus.Grey)]
    [InlineData("BOT-T4 role=joiner complete=0 reason=relay_role", VerdictStatus.Red)]
    [InlineData("SYNTH125 t=0.16s BOT-T4 role=host refused=relay_role", VerdictStatus.Red)]
    [InlineData("BOT-T4 role=joiner ready=1\nBOT-T4 role=host complete=1", VerdictStatus.Red)]
    [InlineData("BOT-T4 role=joiner ready=1\nBOT-T4 role=joiner complete=1 poses=12", VerdictStatus.Green)]
    [InlineData("SYNTH125 t=0.00s BOT-T4 role=host started=1\nSYNTH125 t=4.24s BOT-T4 role=host complete=1 played=2", VerdictStatus.Green)]
    public void A_scripted_peer_verdict_requires_a_start_and_its_own_completion(string log, VerdictStatus expected)
    {
        var input = new Briefing.Input();
        input.Logs["bot-t4.log"] = log.Split('\n');
        var route = Assert.Single(VerdictRules.Evaluate(input).Rows, row => row.Id == "bot_plan_complete");
        Assert.Equal(expected, route.Status);
    }
}
