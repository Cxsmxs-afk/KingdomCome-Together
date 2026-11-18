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
            Assert.StartsWith($"KCDMP-ERGEBNIS\t1\nGesamt\t{expected}\nRegel\tStatus\tMesswert\tBeleg\n", verdict);
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
    [InlineData("owner_violations=0", VerdictStatus.Green)]
    [InlineData("owner_violations=1", VerdictStatus.Red)]
    [InlineData("owner_violations=0 owner_violations=1", VerdictStatus.Grey)]
    public void Owner_observations_are_explicit_and_ambiguous_samples_never_pass(string fields, VerdictStatus expected)
    {
        var input = new Briefing.Input();
        input.Logs["agent.log"] = [$"12:00:00.000 MP-T3-STATS origin=agent role=joiner {fields}"];
        var row = Assert.Single(VerdictRules.Evaluate(input).Rows, row => row.Id == "owner_violations");
        Assert.Equal(expected, row.Status);
    }
}
