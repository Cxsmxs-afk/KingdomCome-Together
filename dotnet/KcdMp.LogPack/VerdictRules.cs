// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace KcdMp.LogPack;

public enum VerdictStatus { Green, Grey, Yellow, Red }

public sealed record VerdictRow(string Id, VerdictStatus Status, string Measurement, string Evidence);

public sealed record VerdictReport(VerdictStatus Status, string Role, IReadOnlyList<VerdictRow> Rows)
{
    public const string EntryName = "ERGEBNIS.txt";

    private static string Label(VerdictStatus status) => status switch
    {
        VerdictStatus.Green => "GRÜN",
        VerdictStatus.Yellow => "GELB",
        VerdictStatus.Red => "ROT",
        _ => "GRAU"
    };

    public string ToText()
    {
        var text = new StringBuilder("KCDMP-ERGEBNIS\t1\n");
        text.Append("Gesamt\t").Append(Label(Status)).Append('\n');
        text.Append("Rolle\t").Append(Role).Append('\n');
        text.Append("Regel\tStatus\tMesswert\tBeleg\n");
        foreach (var row in Rows)
            text.Append(row.Id).Append('\t').Append(Label(row.Status)).Append('\t')
                .Append(row.Measurement).Append('\t').Append(row.Evidence).Append('\n');
        return text.ToString();
    }

    public string ToMarkdown()
    {
        var text = new StringBuilder("## Ergebnis\n\n");
        text.Append("**Gesamt: ").Append(Label(Status)).Append("**\n\n");
        text.Append("**Rolle im Agent-Log:** ").Append(Role).Append("\n\n");
        text.Append("Grau bedeutet, dass für die Regel eine Messung oder ein ausgeübter Vorgang fehlt. ")
            .Append("Für Grün im Gesamtergebnis ist eine gemessene Queue erforderlich. ")
            .Append("Sichtbares Spielverhalten erfordert einen Spieltest.\n\n");
        text.Append("| Regel | Status | Messwert | Beleg |\n| --- | --- | --- | --- |\n");
        foreach (var row in Rows)
            text.Append("| `").Append(row.Id).Append("` | ").Append(Label(row.Status)).Append(" | ")
                .Append(row.Measurement).Append(" | ").Append(row.Evidence).Append(" |\n");
        return text.Append('\n').ToString();
    }
}

public static class VerdictRules
{
    public const long QueueGreenP99Ms = 250;
    public const long QueueGreenMaxMs = 1000;
    public const long QueueYellowP99Ms = 1000;
    public const long QueueYellowMaxMs = 2000;

    private const string T3 = "MP-T3-STATS";
    private const string W136 = "MP-WO136-STATS";
    // The agent's TeeTextWriter supplies the wall clock and optional shared-clock metadata.
    private const string AgentPrefix = @"^(?:\d\d:\d\d:\d\d(?:\.\d+)? (?:m=\d+ off=(?:[+-]?\d+|\?) )?)?";
    private static readonly Regex AgentPrefixOnly = new(AgentPrefix + @"\z", RegexOptions.Compiled);
    private static readonly Regex AgentSessionRole = new(AgentPrefix
        + @"MP-WO137-STATS role=(host|joiner)(?: |$)", RegexOptions.Compiled);

    private sealed record CounterRule(string Id, VerdictStatus AboveLimit, long Limit = 0, string Unit = "",
        string? Role = null, string? RequiresActivity = null, string? Unreadable = null, string Marker = T3,
        bool ZeroMeansMissing = false, string? RequiresHostActivity = null)
    {
        public string? ActivityFor(string role) => role == "host" ? RequiresHostActivity ?? RequiresActivity : RequiresActivity;
    }

    // A threshold violation is evidence even when subsequent reads failed. For
    // a non-violating value, a failed source read or an unexercised operation
    // prevents the initial zero of a heartbeat from becoming a pass.
    private static readonly CounterRule[] Counters =
    [
        new("proc_stall_max_ms", VerdictStatus.Red, 2000, " ms"),
        new("proc_stalls", VerdictStatus.Yellow),
        new("proc_frames_abandoned", VerdictStatus.Yellow),
        new("decision_blocked_during_load", VerdictStatus.Red, Role: "joiner", RequiresActivity: "gate_armed_at_s",
            Unreadable: "authority_stats_unreadable"),
        new("postload_timeouts", VerdictStatus.Red, RequiresActivity: "gate_armed_at_s"),
        new("ordered_dropped", VerdictStatus.Red, RequiresActivity: "held", Marker: W136),
        new("ask_timeouts", VerdictStatus.Yellow, RequiresActivity: "pickup_asks", Unreadable: "loot_stats_unreadable"),
        new("ask_latency_ms_p95", VerdictStatus.Yellow, 5000, " ms", RequiresActivity: "pickup_asks", Unreadable: "loot_stats_unreadable",
            ZeroMeansMissing: true),
        new("input_releases", VerdictStatus.Yellow, RequiresActivity: "input_held_max_s"),
        new("wake_dist_m_max", VerdictStatus.Red, 30, " m", RequiresActivity: "tp_by_source_respawn",
            Unreadable: "world_stats_unreadable"),
        new("wake_drop_m_max", VerdictStatus.Red, 2, " m", RequiresActivity: "tp_by_source_respawn",
            Unreadable: "world_stats_unreadable"),
        new("falldeath_after_tp", VerdictStatus.Red, RequiresActivity: "tp_total", Unreadable: "world_stats_unreadable"),
        new("markers_off_session", VerdictStatus.Red, RequiresActivity: "grave_markers_added",
            Unreadable: "corpse_stats_unreadable"),
        new("ranged_kind_unknown", VerdictStatus.Yellow, RequiresActivity: "ranged_in"),
        new("ranged_native_queue_overflow", VerdictStatus.Red, RequiresActivity: "ranged_out",
            Unreadable: "ranged_stats_unreadable"),
        new("ranged_missile_uncertain", VerdictStatus.Red, RequiresActivity: "ranged_missile_requests",
            RequiresHostActivity: "ranged_missile_measured_accept"),
        new("owner_violations", VerdictStatus.Red)
    ];

    private sealed record Sample(string Marker, string Role, string Origin, Dictionary<string, string> Fields)
    {
        public long? Number(string key) => Fields.TryGetValue(key, out var value)
            && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long number) ? number : null;
    }

    public static VerdictReport Evaluate(Briefing.Input input)
    {
        var agent = Briefing.AgentLines(input).ToList();
        var native = Briefing.NativeLines(input);
        var samples = ReadSamples(agent, fromNativeLog: false).Concat(ReadSamples(native, fromNativeLog: true)).ToList();
        var queue = Queue(agent);
        var rows = new List<VerdictRow> { queue };
        rows.AddRange(Counters.Select(rule => Evaluate(rule, samples)));

        // Traffic observable on this machine is required for green. Optional
        // rows are still shown as grey and any measured warning or fault wins.
        var status = rows.Where(row => row.Status is VerdictStatus.Yellow or VerdictStatus.Red)
            .Select(row => row.Status).DefaultIfEmpty(queue.Status).Max();
        return new(status, Role(agent, samples), rows.AsReadOnly());
    }

    private static string Role(IReadOnlyList<string> agent, IReadOnlyList<Sample> samples)
    {
        // A role can change during one game launch. Conflicting local observations
        // cannot establish the side of a paired run. DLL mirrors do not vote.
        var roles = samples.Where(sample => sample.Marker == T3 && sample.Origin == "Agent")
            .Select(sample => sample.Role)
            .Concat(agent.Select(line => AgentSessionRole.Match(line)).Where(match => match.Success)
                .Select(match => match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal).Take(2).ToList();
        return roles.Count == 1 ? roles[0] : "unbekannt";
    }

    private static VerdictRow Queue(IReadOnlyList<string> agent)
    {
        var (samples, _, p99, maximum, _) = W218Section.QueueOf(agent);
        long legacyMax = agent.Select(line => Briefing.QueueLag.Match(line))
            .Select(match => match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out long lag) ? lag : -1)
            .DefaultIfEmpty(-1).Max();
        if (samples == 0)
        {
            if (legacyMax < 0) return Missing("queue", "Keine Queue-Messung im Agent-Log");
            var legacyStatus = legacyMax > QueueYellowMaxMs ? VerdictStatus.Red :
                legacyMax > QueueGreenMaxMs ? VerdictStatus.Yellow : VerdictStatus.Grey;
            return new("queue", legacyStatus, Format(legacyMax, " ms"), "Explizite Ping-Queue, p99 fehlt");
        }

        long worst = Math.Max(maximum, legacyMax);
        var status = p99 <= QueueGreenP99Ms && worst <= QueueGreenMaxMs ? VerdictStatus.Green :
            p99 <= QueueYellowP99Ms && worst <= QueueYellowMaxMs ? VerdictStatus.Yellow : VerdictStatus.Red;
        var measurement = string.Create(CultureInfo.InvariantCulture, $"p99 {p99} ms, max. {worst} ms, {samples} Messungen");
        return new("queue", status, measurement, legacyMax > maximum ? "MP-W207-Q und explizite Ping-Queue" : "MP-W207-Q");
    }

    private static VerdictRow Evaluate(CounterRule rule, IReadOnlyList<Sample> samples)
    {
        var observations = samples.Where(sample => sample.Marker == rule.Marker && (rule.Role is null || sample.Role == rule.Role))
            .Select(sample => (Sample: sample, Value: sample.Number(rule.Id)))
            .Where(observation => observation.Value.HasValue).ToList();
        if (observations.Count == 0) return Missing(rule.Id, $"Keine Messung in {rule.Marker}");

        // Counts and maxima are snapshots. Taking the maximum keeps an older
        // recorded fault without adding a copied row from the DLL mirror.
        var (source, value) = observations.MaxBy(observation => observation.Value);
        string evidence = $"{rule.Marker}, {source.Origin}";
        string measurement = Format(value!.Value, rule.Unit);
        if (value > rule.Limit) return new(rule.Id, rule.AboveLimit, measurement, evidence);

        if (rule.Unreadable is { } unreadable && observations.Any(observation => observation.Sample.Number(unreadable) != 0))
            return new(rule.Id, VerdictStatus.Grey, measurement, $"Lesbarkeit offen, {unreadable}");
        if (observations.All(observation => rule.ActivityFor(observation.Sample.Role) is { } activity
            && !(observation.Sample.Number(activity) > 0)))
            return new(rule.Id, VerdictStatus.Grey, measurement, $"Kein beobachteter Vorgang, {rule.ActivityFor(source.Role)}");
        if (rule.ZeroMeansMissing && value == 0) return Missing(rule.Id, "Keine aufgezeichnete Stichprobe");

        return new(rule.Id, VerdictStatus.Green, measurement, evidence);
    }

    private static string Format(long value, string unit) => value.ToString(CultureInfo.InvariantCulture) + unit;

    private static VerdictRow Missing(string id, string reason) => new(id, VerdictStatus.Grey, "–", reason);

    private static IEnumerable<Sample> ReadSamples(IEnumerable<string> lines, bool fromNativeLog)
    {
        foreach (string line in lines)
        {
            int start = line.IndexOf(T3 + " ", StringComparison.Ordinal);
            string marker = T3;
            if (start < 0 && !fromNativeLog)
            {
                start = line.IndexOf(W136 + " ", StringComparison.Ordinal);
                marker = W136;
            }
            if (start < 0 || (start > 0 && line[start - 1] != ' ')) continue;
            if (!fromNativeLog && start > 0 && !AgentPrefixOnly.IsMatch(line.AsSpan(0, start))) continue;

            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var field in line[(start + marker.Length + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = field.IndexOf('=');
                if (equals <= 0) continue;
                if (!fields.TryAdd(field[..equals], field[(equals + 1)..]))
                {
                    fields.Clear();
                    break;
                }
            }
            if (fields.Count == 0) continue;

            if (marker == W136)
            {
                yield return new(marker, "", "Agent", fields);
                continue;
            }

            string? role = fields.GetValueOrDefault("role");
            if (role is not ("host" or "joiner")) continue;
            string expected = fromNativeLog ? "dll" : "agent";
            if (fields.GetValueOrDefault("origin") != expected) continue;

            bool mirror = fromNativeLog && fields.GetValueOrDefault("source") == "agent";
            string origin = !fromNativeLog ? "Agent" : mirror ? "DLL-Spiegel" : "DLL";
            yield return new(marker, role, origin, fields);
        }
    }
}
