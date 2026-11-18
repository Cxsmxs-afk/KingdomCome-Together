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
        text.Append("Grau bedeutet, dass für die Regel eine Messung, eine Prüfvoraussetzung oder ein Grenzwert fehlt. ")
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
    public const long PresentationGreenBelowMs = 150;
    public const long PresentationYellowBelowMs = 300;
    public const long PresentationAgentHopReviewAboveMs = 20;
    public const long EdgeRealtimeGreenBelowMs = 34;
    public const long TimelineBufferMinMs = 60, TimelineBufferMaxMs = 120, TimelineExtrapolationMaxMs = 200;
    public const long TimelineSkewGreenBelowMs = 20, TimelineSkewYellowBelowMs = 50;
    public const long ControllerStepMinPermille = 900, ControllerStepMaxPermille = 1100;
    public const long ControllerPullsGreenBelowPermille = 200;
    public const int CombatPlaybackGreenPercent = 95;
    public const long RecoveryReconnectMaxMs = 30_000;

    private const string T3 = "MP-T3-STATS";
    private const string W136 = "MP-WO136-STATS";
    private const string WorldEventFault = "MP-WORLD-EVENT-FAULT";
    private const string WorldEventScope = "MP-WORLD-EVENT-SCOPE";
    private const string StepPair = "MP-RP-STEP";
    private const string OwnerViolation = "WO221-OWNER-VIOLATION";
    private static readonly string[] LoadPhases = ["Menu", "Offer", "Load", "Reconstruct", "Settle", "Play", "Exit"];
    private static readonly string[] RecoveryAreas = ["body", "npc", "corpse", "loot", "clock", "weather"];
    // The agent's TeeTextWriter supplies the wall clock and optional shared-clock metadata.
    private const string AgentPrefix = @"^(?:\d\d:\d\d:\d\d(?:\.\d+)? (?:m=\d+ off=(?:[+-]?\d+|\?) )?)?";
    private static readonly Regex AgentPrefixOnly = new(AgentPrefix + @"\z", RegexOptions.Compiled);
    private static readonly Regex NativePrefixOnly = new(@"^(?:\[\d\d:\d\d:\d\d\.\d{3}\] t\d+ L[0-9a-f]{6} )?\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AgentSessionRole = new(AgentPrefix
        + @"MP-WO137-STATS role=(host|joiner)(?: |$)", RegexOptions.Compiled);
    private static readonly Regex BotProgress = new(@"^(?:SYNTH125 t=[\d.]+s )?BOT-T4 role=(host|joiner) (started=1|ready=1|complete=[01]|refused=\S+|stopped=\S+)(?: |$)", RegexOptions.Compiled);
    private static readonly Regex StepPairSample = new(@"^MP-RP-STEP rp_step_n=([1-9][0-9]*) rp_step_ratio=([1-9][0-9]*)\r?$", RegexOptions.Compiled);
    private static readonly Regex NativeOwnerViolation = new(@"^\[\d\d:\d\d:\d\d\.\d{3}\] t\d+ L[0-9a-f]{6} " + OwnerViolation
        + @" eid=0x[0-9A-Fa-f]+ holder=\S+ path=\S+ frame=\d+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
        new("proc_awaits_per_frame_max", VerdictStatus.Red, RequiresActivity: "edge_proc_n"),
        new("http_calls_rt", VerdictStatus.Red, RequiresActivity: "edge_rt_n"),
        new("rp_hard_tp_unsafe", VerdictStatus.Red, RequiresActivity: "rp_ctrl_frames"),
        new("dmg_double", VerdictStatus.Red, RequiresActivity: "dmg_double_blocked", Unreadable: "dmg_double_unconfirmed")
    ];

    private sealed record PresentationRule(string Id, string Coverage, string Unit = "", string? AlternateCoverage = null,
        bool ReportNonZeroWithoutCoverage = false)
    {
        public long? Count(Sample sample) => AlternateCoverage is { } alternate
            ? Math.Max(sample.Number(Coverage) ?? 0, sample.Number(alternate) ?? 0)
            : sample.Number(Coverage);
    }

    private static readonly PresentationRule[] PresentationCounters =
    [
        new("rp_e2e_ms_p50", "rp_e2e_n", " ms"),
        new("rp_e2e_ms_p95", "rp_e2e_n", " ms"),
        new("rp_e2e_ms_max", "rp_e2e_n", " ms"),
        new("rp_agent_hop_ms_p95", "rp_agent_hop_n", " ms"),
        new("rp_corr_n", "rp_pose_n"),
        new("rp_corr_cm_p95", "rp_corr_n", " cm"),
        new("rp_corr_cm_max", "rp_corr_n", " cm"),
        new("rp_hard_place", "rp_pose_n"),
        new("rp_step_ratio", "rp_step_n"),
        new("rp_action_lag_ms_p95", "rp_action_n", " ms"),
        new("owner_violations", "rp_owner_n", ReportNonZeroWithoutCoverage: true),
        new("owner_handovers", "rp_owner_n"),
        new("owner_denied_sets", "rp_owner_n"),
        new("edge_rt_age_ms_p95", "edge_rt_n", " ms"),
        new("edge_rt_age_ms_max", "edge_rt_n", " ms"),
        new("edge_bulk_age_ms_p95", "edge_bulk_n", " ms"),
        new("pipe_busy_replies", "edge_rt_n", AlternateCoverage: "edge_bulk_n", ReportNonZeroWithoutCoverage: true),
        new("inproc_fallbacks", "edge_proc_n", ReportNonZeroWithoutCoverage: true),
        new("rp_buffer_ms", "rp_timeline_pose_n", " ms"),
        new("rp_extrap_ms_max", "rp_timeline_pose_n", " ms"),
        new("rp_late_samples", "rp_timeline_pose_n", ReportNonZeroWithoutCoverage: true),
        new("rp_action_pos_skew_ms_p95", "rp_timeline_skew_n", " ms"),
        new("rp_ctrl_frames", "rp_ctrl_frames"),
        new("rp_req_ok", "rp_ctrl_frames", ReportNonZeroWithoutCoverage: true),
        new("rp_req_fail", "rp_ctrl_frames", ReportNonZeroWithoutCoverage: true),
        new("rp_fallback_by_jump", "rp_ctrl_frames", ReportNonZeroWithoutCoverage: true),
        new("rp_fallback_by_mount", "rp_ctrl_frames", ReportNonZeroWithoutCoverage: true),
        new("rp_fallback_by_ragdoll", "rp_ctrl_frames", ReportNonZeroWithoutCoverage: true),
        new("rp_fallback_by_air", "rp_ctrl_frames", ReportNonZeroWithoutCoverage: true),
        new("rp_hard_tp_by_join", "rp_ctrl_frames", ReportNonZeroWithoutCoverage: true),
        new("rp_hard_tp_by_respawn", "rp_ctrl_frames", ReportNonZeroWithoutCoverage: true),
        new("rp_hard_tp_by_travel", "rp_ctrl_frames", ReportNonZeroWithoutCoverage: true),
        new("rp_pulls_per_min", "rp_pull_observed_ms", " / min"),
        new("rp_actions_in", "rp_actions_in"),
        new("dmg_double_blocked", "dmg_double_blocked"),
        new("dmg_double_unconfirmed", "dmg_double_blocked", ReportNonZeroWithoutCoverage: true),
        new("rp_ko_up_lag_ms_p95", "rp_ko_up_n", " ms")
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
        if (ReadStepPair(input) is { } stepPair) samples.Add(stepPair);
        var role = Role(agent, samples);
        var queue = Queue(agent);
        var rows = new List<VerdictRow> { queue };
        rows.AddRange(Counters.Select(rule => Evaluate(rule, samples)));
        rows.AddRange(PresentationCounters.Select(rule => EvaluatePresentation(rule, samples)));
        rows.Add(ActionPlayback(samples));
        rows.AddRange(LoadSequence(samples));
        rows.AddRange(WorldEvents(samples));
        rows.AddRange(WorldRecovery(samples, role));
        rows.Add(BotPlan(input));
        var guidedRun = GuidedRunVerdictEvidence.Evaluate(input, role);
        if (guidedRun is not null) rows.Add(guidedRun);

        // Traffic observable on this machine is required for green. Optional
        // rows are still shown as grey and any measured warning or fault wins.
        var status = rows.Where(row => row.Status is VerdictStatus.Yellow or VerdictStatus.Red)
            .Select(row => row.Status).DefaultIfEmpty(queue.Status).Max();
        if (status == VerdictStatus.Green && guidedRun?.Status == VerdictStatus.Grey)
            status = VerdictStatus.Grey;
        return new(status, role, rows.AsReadOnly());
    }

    private static VerdictRow BotPlan(Briefing.Input input)
    {
        const string id = "bot_plan_complete";
        const string entry = "bot-t4.log";
        if (!input.Logs.TryGetValue(entry, out var lines)) return Missing(id, "Kein Bot-Protokoll im Bundle");
        var runs = new Dictionary<string, VerdictStatus>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var match = BotProgress.Match(line);
            if (!match.Success) continue;
            var role = match.Groups[1].Value;
            switch (match.Groups[2].Value)
            {
                case "started=1":
                case "ready=1":
                    runs[role] = VerdictStatus.Grey;
                    break;
                case "complete=1":
                    if (runs.TryGetValue(role, out var progress) && progress == VerdictStatus.Grey)
                        runs[role] = VerdictStatus.Green;
                    break;
                default:
                    runs[role] = VerdictStatus.Red;
                    break;
            }
        }
        if (runs.Count == 0) return Missing(id, "Kein belegter Bot-Start");
        return runs.Values.All(progress => progress == VerdictStatus.Green)
            ? new(id, VerdictStatus.Green, "Abgeschlossen", entry)
            : new(id, VerdictStatus.Red, "Abgebrochen oder Abschluss fehlt", entry);
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
            return new(rule.Id, VerdictStatus.Grey, measurement, rule.Id == "dmg_double"
                ? $"Schadenswirkung ungeklärt, {unreadable}" : $"Lesbarkeit offen, {unreadable}");
        if (observations.All(observation => rule.ActivityFor(observation.Sample.Role) is { } activity
            && !(observation.Sample.Number(activity) > 0)))
            return new(rule.Id, VerdictStatus.Grey, measurement, $"Kein beobachteter Vorgang, {rule.ActivityFor(source.Role)}");
        if (rule.ZeroMeansMissing && value == 0) return Missing(rule.Id, "Keine aufgezeichnete Stichprobe");

        if (rule.Id == "dmg_double") evidence += ", beobachtete Treffer vor der Schadensursache blockiert";
        return new(rule.Id, VerdictStatus.Green, measurement, evidence);
    }

    private static VerdictRow EvaluatePresentation(PresentationRule rule, IReadOnlyList<Sample> samples)
    {
        const string confirmed = "rp_action_confirmed_n";
        var useCombatLatency = rule.Id == "rp_action_lag_ms_p95" &&
            samples.Any(sample => sample.Marker == T3 && sample.Fields.ContainsKey(confirmed));
        var observations = samples.Where(sample => sample.Marker == T3 ||
                (rule.Id == "rp_step_ratio" && sample.Marker == StepPair) ||
                (rule.Id == "owner_violations" && sample.Marker == OwnerViolation))
            .Select(sample => (Sample: sample, Value: sample.Number(rule.Id), Count: useCombatLatency ? sample.Number(confirmed) : rule.Count(sample)))
            .Where(observation => observation.Value.HasValue && ((observation.Count > 0 &&
                (rule.Coverage != "rp_owner_n" || HasOwnerCounters(observation.Sample)) &&
                (rule.Id != "rp_pulls_per_min" || HasPullEvidence(observation.Sample, observation.Value.Value, observation.Count.GetValueOrDefault()))) ||
                (rule.ReportNonZeroWithoutCoverage && observation.Value > 0))).ToList();
        if (observations.Count == 0)
            return Missing(rule.Id, $"Keine belegte Stichprobe in {T3}, {(useCombatLatency ? confirmed : rule.Coverage)}");

        // A scope-matched skeleton comparison takes precedence over a raw
        // number from the status line, which carries no paired capture.
        var pairedStep = rule.Id == "rp_step_ratio" ? observations.FirstOrDefault(x => x.Sample.Marker == StepPair) : default;
        var (source, observed, count) = pairedStep.Value.HasValue ? pairedStep : observations.MaxBy(observation => observation.Value);
        long value = observed!.Value;
        string measurement = rule.Id is "rp_step_ratio" or "rp_pulls_per_min"
            ? (value / 1000m).ToString("0.000", CultureInfo.InvariantCulture) + rule.Unit
            : Format(value, rule.Unit);
        string evidence = $"{source.Marker}, {source.Origin}";
        if (count is long samplesCount && samplesCount > 0)
            evidence += rule.Id == "rp_pulls_per_min" ? $", {Format(samplesCount, " ms")} beobachtet" : $", n={Format(samplesCount, "")}";

        switch (rule.Id)
        {
            case "rp_e2e_ms_p95":
                var latency = value < PresentationGreenBelowMs ? VerdictStatus.Green :
                    value < PresentationYellowBelowMs ? VerdictStatus.Yellow : VerdictStatus.Red;
                return new(rule.Id, latency, measurement, evidence + ", gemeinsame Rechneruhr");
            case "rp_agent_hop_ms_p95":
                var hop = value > PresentationAgentHopReviewAboveMs ? VerdictStatus.Yellow : VerdictStatus.Green;
                return new(rule.Id, hop, measurement, evidence + ", Agent-Empfang bis DLL-Empfang");
            case "edge_rt_age_ms_p95":
                var realtime = value < EdgeRealtimeGreenBelowMs ? VerdictStatus.Green : VerdictStatus.Red;
                return new(rule.Id, realtime, measurement, evidence + ", DLL-Empfang bis Verarbeitung im Hauptthread");
            case "rp_buffer_ms":
                // Either endpoint is a violation, including in an older measured window.
                var (outside, invalidBuffer, outsideCount) = observations.FirstOrDefault(x =>
                    x.Value < TimelineBufferMinMs || x.Value > TimelineBufferMaxMs);
                if (invalidBuffer is long buffer)
                {
                    return new(rule.Id, VerdictStatus.Red, Format(buffer, rule.Unit),
                        $"{outside!.Marker}, {outside.Origin}, n={Format(outsideCount.GetValueOrDefault(), "")}, Puffer außerhalb 60–120 ms");
                }
                return new(rule.Id, VerdictStatus.Green, measurement, evidence + ", Puffer 60–120 ms");
            case "rp_extrap_ms_max":
                var extrapolation = value <= TimelineExtrapolationMaxMs ? VerdictStatus.Green : VerdictStatus.Red;
                return new(rule.Id, extrapolation, measurement, evidence + ", Extrapolation bis 200 ms");
            case "rp_action_pos_skew_ms_p95":
                var skew = value < TimelineSkewGreenBelowMs ? VerdictStatus.Green :
                    value < TimelineSkewYellowBelowMs ? VerdictStatus.Yellow : VerdictStatus.Red;
                return new(rule.Id, skew, measurement, evidence + ", Host-Zeit der ausgelösten Aktion und der dargestellten Pose");
            case "rp_step_ratio" when source.Marker == StepPair:
                var step = value is >= ControllerStepMinPermille and <= ControllerStepMaxPermille ? VerdictStatus.Green : VerdictStatus.Yellow;
                return new(rule.Id, step, measurement, evidence + ", gepaarte Skelettmessung");
            case "rp_pulls_per_min":
                var pulls = value < ControllerPullsGreenBelowPermille ? VerdictStatus.Green : VerdictStatus.Yellow;
                return new(rule.Id, pulls, measurement, evidence + ", Position der tatsächlich geführten Figur");
            case "owner_violations":
                // The native guard can assert before the next periodic count.
                if (source.Marker == OwnerViolation) return new(rule.Id, VerdictStatus.Red, "≥1", evidence);
                return new(rule.Id, value == 0 ? VerdictStatus.Green : VerdictStatus.Red, measurement, evidence);
            case "rp_action_lag_ms_p95":
                return new(rule.Id, VerdictStatus.Grey, measurement, evidence + (useCombatLatency
                    ? ", DLL-Empfang bis bestätigter Engine-Wiedergabe, ohne Grenzwert"
                    : ", DLL-Empfang bis zur angenommenen Aktion, ohne Grenzwert"));
            case "rp_ko_up_lag_ms_p95":
                return new(rule.Id, VerdictStatus.Grey, measurement, evidence + ", DLL-Empfang bis bestätigtem KO-/Aufstehzustand, ohne Grenzwert");
            default:
                return new(rule.Id, VerdictStatus.Grey, measurement, evidence + ", ohne Grenzwert");
        }
    }

    private static VerdictRow ActionPlayback(IReadOnlyList<Sample> samples)
    {
        const string id = "rp_actions_played";
        var observations = samples.Where(sample => sample.Marker == T3 && sample.Number("rp_actions_pending") == 0)
            .Select(sample => (Sample: sample, Received: sample.Number("rp_actions_in"), Played: sample.Number(id)))
            .Where(x => x.Received > 0 && x.Played.HasValue && x.Played <= x.Received).ToList();
        if (observations.Count == 0) return Missing(id, "Keine abgeschlossene Stichprobe bestätigter Engine-Aktionen");

        var (source, received, played) = observations.MinBy(x => (decimal)x.Played.GetValueOrDefault() / x.Received.GetValueOrDefault());
        decimal percent = played.GetValueOrDefault() * 100m / received.GetValueOrDefault();
        string measurement = string.Create(CultureInfo.InvariantCulture, $"{played}/{received} ({percent:0.00} %)");
        var status = percent >= CombatPlaybackGreenPercent ? VerdictStatus.Green : VerdictStatus.Yellow;
        return new(id, status, measurement, $"{source.Marker}, {source.Origin}, bestätigte Engine-Wiedergabe");
    }

    private static IEnumerable<VerdictRow> LoadSequence(IReadOnlyList<Sample> samples)
    {
        var reports = samples.Where(sample => sample.Marker == T3 && sample.Fields.GetValueOrDefault("section") == "load").ToList();
        var agent = reports.LastOrDefault(sample => sample.Origin == "Agent");
        var latest = agent ?? reports.LastOrDefault();
        var phase = latest?.Number("load_phase");
        var generation = latest?.Number("load_gen");
        bool validAgentPhase = phase < LoadPhases.Length && generation.HasValue;
        bool validNativePhase = latest?.Number("load_phase_native") < LoadPhases.Length
            && latest?.Number("load_native_gen") > 0 && latest?.Number("load_native_observations") > 0;
        if (validAgentPhase)
        {
            bool sameNativePlay = validNativePhase && phase == 5 && generation > 0 &&
                latest!.Number("load_native_gen") == generation && latest.Number("load_phase_native") == phase;
            yield return new("load_phase", sameNativePlay ? VerdictStatus.Green : VerdictStatus.Grey, LoadPhases[(int)phase.GetValueOrDefault()],
                sameNativePlay ? $"Agent und DLL melden Play für Generation {generation}" :
                $"Generation {generation}, gemeinsames Play von Agent und DLL unbelegt");
        }
        else
            yield return Missing("load_phase", "Kein gültiger Agent-Ladephasenbericht");

        foreach (string name in LoadPhases)
        {
            string suffix = name.ToLowerInvariant();
            string key = "load_phase_ms_" + suffix;
            var managedMs = latest?.Number(key);
            var nativeMs = latest?.Number("load_native_phase_ms_" + suffix);
            var durations = new List<string>(capacity: 2);
            if (managedMs > 0 && validAgentPhase) durations.Add("Agent " + Format(managedMs.Value, " ms"));
            if (nativeMs > 0 && validNativePhase)
                durations.Add("DLL " + Format(nativeMs.Value, " ms"));
            yield return durations.Count == 0
                ? Missing(key, "Keine verstrichene Zeit gemessen")
                : new(key, VerdictStatus.Grey, string.Join(", ", durations),
                    "Agent-Laufzeit kumuliert, DLL für gemeldete Generation, ohne Grenzwert");
        }

        yield return LoadRejected(reports, "stale_gen_dropped", "load_gen", "Agent");
        yield return LoadRejected(reports, "stale_gen_native_dropped", "load_native_gen", "DLL", "load_native_observations");
    }

    private static VerdictRow LoadRejected(IEnumerable<Sample> reports, string id, string generation, string source,
        string? coverage = null)
    {
        var counts = reports.Where(sample => sample.Number(generation) > 0 &&
                (coverage is null || sample.Number(coverage) > 0))
            .Select(sample => sample.Number(id)).Where(value => value.HasValue).ToList();
        if (counts.Count == 0) return Missing(id, $"Kein gültiger Zähler vom {source}");

        long maximum = counts.Max().GetValueOrDefault();
        return new(id, maximum > 0 ? VerdictStatus.Green : VerdictStatus.Grey, Format(maximum, ""),
            maximum > 0 ? $"{source} hat veraltete Frames verworfen" : $"Kein veralteter Frame beim {source} beobachtet");
    }

    private static IEnumerable<VerdictRow> WorldEvents(IReadOnlyList<Sample> samples)
    {
        var reports = samples.Where(sample => sample.Marker is T3 or WorldEventFault or WorldEventScope
            && sample.Origin is "Agent" or "DLL-Spiegel" && sample.Fields.GetValueOrDefault("section") == "events"
            && sample.Number("evt_world") is > 0 and <= uint.MaxValue
            && sample.Number("evt_gen") > 0 && sample.Number("evt_snapshot") is > 0 and <= uint.MaxValue
            && sample.Number("evt_recipient") is >= 0 and <= byte.MaxValue
            && (sample.Marker != WorldEventScope || sample.Number("evt_scope_active") == 0 && sample.Number("evt_receiver") > 0)).ToList();
        var latest = reports.LastOrDefault(sample => sample.Origin == "Agent") ?? reports.LastOrDefault();
        var receiver = latest?.Number("evt_receiver");
        if (latest?.Marker == WorldEventScope || (receiver > 0 && reports.Any(sample => sample.Marker == WorldEventScope
                && sample.Role == latest!.Role && sample.Number("evt_receiver") == receiver)))
            latest = null; // A heartbeat captured just before retirement may be written afterward.
        var version = latest?.Number("baseline_version");
        bool baselineApplied = version is > 0 and <= ushort.MaxValue;
        yield return baselineApplied
            ? new("baseline_version", VerdictStatus.Green, Format(version.GetValueOrDefault(), ""), "Empfänger meldet angewendeten Startzustand")
            : Missing("baseline_version", "Kein angewendeter Startzustand gemeldet");
        var baselineMs = latest?.Number("baseline_ms");
        yield return baselineApplied && baselineMs.HasValue
            ? new("baseline_ms", VerdictStatus.Grey, Format(baselineMs.Value, " ms"), "Empfang bis Anwendung des Startzustands, ohne Grenzwert")
            : Missing("baseline_ms", "Keine Dauer für angewendeten Startzustand gemessen");

        foreach (var (id, evidence) in new[] {
            ("evt_sent", "Relay hat Ereignisse zur ersten Übertragung angenommen"), ("evt_applied", "Empfänger meldet angewendete Ereignisse"),
            ("evt_acked", "Relay hat Anwendungsbestätigungen erhalten"), ("evt_retx", "Relay hat Wiederholungen zur Übertragung angenommen")
        })
        {
            var count = latest?.Number(id);
            yield return count.HasValue ? new(id, VerdictStatus.Grey, Format(count.Value, ""), evidence + ", ohne Grenzwert")
                : Missing(id, "Kein Zähler für den aktuellen Startzustand");
        }

        // A zero is a pass only after the same recipient installed its
        // baseline and the relay observed ACKs for the complete measured send.
        var sent = latest?.Number("evt_sent");
        bool covered = baselineApplied && sent > 0 && latest!.Number("evt_acked") == sent && latest.Number("evt_applied") == sent;
        var gap = reports.Max(sample => sample.Number("evt_gap"));
        var localDrop = reports.Max(sample => sample.Number("evt_dropped"));
        var writerDrop = reports.Max(sample => sample.Number("evt_sender_dropped"));
        yield return gap > 0 ? new("evt_gap", VerdictStatus.Red, Format(gap.Value, ""), "Empfänger meldet eine nicht aufgelöste Reihenfolgelücke")
            : WorldEventZero("evt_gap", latest?.Number("evt_gap") == 0, covered);
        if (localDrop > 0 || writerDrop > 0)
        {
            var reasons = new List<string>();
            if (localDrop > 0) reasons.Add("Empfänger " + Format(localDrop.Value, ""));
            if (writerDrop > 0) reasons.Add("Relay " + Format(writerDrop.Value, ""));
            yield return new("evt_dropped", VerdictStatus.Red, string.Join(", ", reasons), "Endgültiger Verlust gemeldet");
        }
        else
            yield return WorldEventZero("evt_dropped", latest?.Number("evt_dropped") == 0 && latest.Number("evt_sender_dropped") == 0, covered);
    }

    private static VerdictRow WorldEventZero(string id, bool measured, bool covered) =>
        measured ? new(id, covered ? VerdictStatus.Green : VerdictStatus.Grey, "0",
            covered ? "Gesendete Ereignisse im selben Startzustand angewendet und bestätigt" :
                "Vollständige Anwendung und Bestätigung fehlen im Messfenster")
            : Missing(id, "Kein gültiger Zähler für den aktuellen Startzustand");

    private static IEnumerable<VerdictRow> WorldRecovery(IReadOnlyList<Sample> samples, string role)
    {
        var reports = samples.Where(sample => sample.Marker == T3 && sample.Origin is "Agent" or "DLL-Spiegel"
            && (role == "unbekannt" || sample.Role == role) && sample.Fields.GetValueOrDefault("section") == "recovery"
            && sample.Number("recovery_world") is > 0 and <= uint.MaxValue
            && sample.Number("recovery_gen") > 0 && sample.Number("recovery_run") > 0).ToList();
        var current = reports.LastOrDefault(sample => sample.Origin == "Agent") ?? reports.LastOrDefault();

        // Each line contains a complete scope's snapshot. Never add periodic
        // counters or pair a fresh reset with a previous run's confirmations.
        foreach (string area in RecoveryAreas)
        {
            string checkKey = "digest_checked_" + area;
            string mismatchKey = "digest_mismatch_" + area;
            string repairKey = "resync_" + area;
            string failureKey = "resync_failed_" + area;
            var checkedCount = current?.Number(checkKey);
            var mismatches = current?.Number(mismatchKey);
            var repaired = current?.Number(repairKey);
            var failureSample = reports.MaxBy(sample => sample.Number(failureKey));
            var failures = failureSample?.Number(failureKey);
            bool covered = checkedCount > 0 && mismatches <= checkedCount && repaired <= mismatches;
            var comparison = covered ? repaired < mismatches ? VerdictStatus.Yellow : VerdictStatus.Green : VerdictStatus.Grey;
            string evidence = !covered ? "Keine vollständige Prüfsummenmessung im aktuellen Lauf"
                : repaired < mismatches ? "Gezielte Wiederherstellung steht aus" : "Prüfsummen verglichen, alle Abweichungen behoben";
            if (covered && mismatches == 0) evidence = "Prüfsummen stimmen überein";

            yield return RecoveryValue(checkKey, checkedCount, checkedCount > 0 ? VerdictStatus.Green : VerdictStatus.Grey,
                checkedCount > 0 ? "Vergleiche im aktuellen Lauf" : "Keine Prüfsumme verglichen");
            if (failures > 0)
            {
                string failureEvidence = "Endgültig abgelehnte Wiederherstellungen: " + Format(failures.Value, "");
                yield return RecoveryValue(mismatchKey, failureSample?.Number(mismatchKey), VerdictStatus.Red, failureEvidence);
                yield return RecoveryValue(repairKey, failureSample?.Number(repairKey), VerdictStatus.Red, failureEvidence);
                yield return RecoveryValue(failureKey, failures, VerdictStatus.Red, failureEvidence);
                continue;
            }

            yield return RecoveryValue(mismatchKey, mismatches, comparison, evidence);
            yield return RecoveryValue(repairKey, repaired, mismatches > 0 ? comparison : VerdictStatus.Grey,
                mismatches > 0 ? evidence : "Keine gezielte Wiederherstellung erforderlich oder belegt");
            yield return RecoveryValue(failureKey, current?.Number(failureKey), covered && mismatches > 0 && repaired == mismatches
                ? VerdictStatus.Green : VerdictStatus.Grey, evidence);
        }

        var reconnects = reports.Select(sample => new RecoveryReconnect(sample)).ToList();
        var latest = new RecoveryReconnect(current);
        var slowest = reconnects.MaxBy(attempt => attempt.Milliseconds);
        var timeout = reconnects.LastOrDefault(attempt => attempt.Timeout == 1);
        var reload = reconnects.Where(attempt => attempt.SameWorld == 1 && attempt.Reloads > 0).MaxBy(attempt => attempt.Reloads);
        var fault = reload ?? timeout ?? (slowest?.Milliseconds > RecoveryReconnectMaxMs ? slowest : null);
        string? failure = reload is not null ? "Neuladen bei gleicher Welt und Generation"
            : timeout is not null ? "Reconnect abgebrochen, Zeitlimit erreicht"
            : fault is not null ? "Reconnect dauerte länger als 30 s" : null;
        bool passed = latest.SameWorld == 1 && latest.Success == 1 && latest.Timeout == 0 && latest.Reloads == 0
            && latest.Milliseconds <= RecoveryReconnectMaxMs;
        var reconnectStatus = failure is not null ? VerdictStatus.Red : passed ? VerdictStatus.Green : VerdictStatus.Grey;
        string reconnectEvidence = failure ?? (passed ? "Gleiche Welt und Generation, ohne Neuladen" : "Kein bestätigter Reconnect derselben Welt und Generation");
        yield return RecoveryValue("reconnect_ms", fault is not null ? fault.Milliseconds : latest.Observed ? latest.Milliseconds : null,
            reconnectStatus, reconnectEvidence, " ms");
        yield return RecoveryValue("reconnect_reload", reload?.Reloads ?? (latest.Observed ? latest.Reloads : null),
            reload is not null ? VerdictStatus.Red : passed ? VerdictStatus.Green : VerdictStatus.Grey,
            reload is not null ? "Neuladen bei gleicher Welt und Generation" : reconnectEvidence);
        yield return RecoveryValue("reconnect_timeout", timeout?.Timeout ?? (latest.Observed ? latest.Timeout : null),
            timeout is not null ? VerdictStatus.Red : passed ? VerdictStatus.Green : VerdictStatus.Grey,
            timeout is not null ? "Reconnect abgebrochen, Zeitlimit erreicht" : reconnectEvidence);
        yield return RecoveryValue("reconnect_same_world", latest.Observed ? latest.SameWorld : null,
            passed ? VerdictStatus.Green : VerdictStatus.Grey, reconnectEvidence);
        yield return RecoveryValue("reconnect_success", fault is not null ? fault.Success : latest.Observed ? latest.Success : null,
            reconnectStatus, reconnectEvidence);
    }

    private sealed record RecoveryReconnect(Sample? Source)
    {
        public long? Milliseconds => Source?.Number("reconnect_ms");
        public long? SameWorld => Bit("reconnect_same_world");
        public long? Success => Bit("reconnect_success");
        public long? Timeout => Bit("reconnect_timeout");
        public long? Reloads => Source?.Number("reconnect_reload");
        public bool Observed => Milliseconds > 0 || Success == 1 || Timeout == 1 || SameWorld == 1 && Reloads > 0;

        private long? Bit(string key) => Source?.Number(key) is long value && value <= 1 ? value : null;
    }

    private static VerdictRow RecoveryValue(string id, long? value, VerdictStatus status, string evidence, string unit = "") =>
        value.HasValue || status == VerdictStatus.Red ? new(id, status, value.HasValue ? Format(value.Value, unit) : "–", evidence)
            : Missing(id, evidence);

    private static bool HasOwnerCounters(Sample sample) => sample.Number("owner_handovers").HasValue
        && sample.Number("owner_denied_sets").HasValue;

    private static bool HasPullEvidence(Sample sample, long rate, long observedMs) =>
        sample.Number("rp_pull_n") is long pulls &&
        Math.Min(long.MaxValue, Math.Floor(pulls * 60_000_000m / observedMs)) == rate;

    private static Sample? ReadStepPair(Briefing.Input input)
    {
        const string entry = "rp-step.log";
        if (!input.Logs.TryGetValue(entry, out var lines)) return null;
        var records = lines.Where(line => !string.IsNullOrWhiteSpace(line)).Take(2).ToArray();
        if (records is not [string record]) return null;
        var sample = StepPairSample.Match(record);
        if (!sample.Success) return null;
        return new(StepPair, "", entry, new(StringComparer.Ordinal)
        {
            ["rp_step_n"] = sample.Groups[1].Value,
            ["rp_step_ratio"] = sample.Groups[2].Value
        });
    }

    private static string Format(long value, string unit) => value.ToString(CultureInfo.InvariantCulture) + unit;

    private static VerdictRow Missing(string id, string reason) => new(id, VerdictStatus.Grey, "–", reason);

    private static IEnumerable<Sample> ReadSamples(IEnumerable<string> lines, bool fromNativeLog)
    {
        foreach (string line in lines)
        {
            if (fromNativeLog && line.Contains(OwnerViolation, StringComparison.Ordinal) && NativeOwnerViolation.IsMatch(line))
            {
                yield return new(OwnerViolation, "", "DLL", new(StringComparer.Ordinal) { ["owner_violations"] = "1" });
                continue;
            }
            int start = line.IndexOf(T3 + " ", StringComparison.Ordinal);
            string marker = T3;
            if (start < 0 && !fromNativeLog)
            {
                start = line.IndexOf(W136 + " ", StringComparison.Ordinal);
                marker = W136;
                if (start < 0)
                {
                    start = line.IndexOf(WorldEventFault + " ", StringComparison.Ordinal);
                    marker = WorldEventFault;
                    if (start < 0)
                    {
                        start = line.IndexOf(WorldEventScope + " ", StringComparison.Ordinal);
                        marker = WorldEventScope;
                    }
                }
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
            if (fromNativeLog && fields.GetValueOrDefault("section") is "events" or "recovery"
                && !NativePrefixOnly.IsMatch(line.AsSpan(0, start))) continue;

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
