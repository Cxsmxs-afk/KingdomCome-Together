// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Text;
using System.Text.RegularExpressions;

namespace KcdMp.LogPack;

/// <summary>A bounded transcript receipt kept separate from measured gameplay and queue rules.</summary>
public static class GuidedRunVerdictEvidence
{
    public const string EntryName = "testlauf-beleg.txt";
    internal const string RuleId = "guided_run_protocol";
    private const int StationCount = 7;
    private static readonly Regex Receipt = new(
        @"^MP-TESTLAUF run=([0-9a-f]{32}) role=(host|joiner) observed=([0-7]) manual_yes=([0-7]) manual_no=([0-7])$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static SourceFile Create(Guid run, string role, int observed, int manualYes, int manualNo)
    {
        if (run == Guid.Empty || role is not ("host" or "joiner") ||
            observed is < 0 or > StationCount || manualYes is < 0 or > StationCount ||
            manualNo is < 0 or > StationCount || observed + manualYes + manualNo > StationCount)
            throw new ArgumentException("Invalid guided run receipt.");
        string text = FormattableString.Invariant(
            $"MP-TESTLAUF run={run:N} role={role} observed={observed} manual_yes={manualYes} manual_no={manualNo}\n");
        return new(EntryName, Bytes: Encoding.UTF8.GetBytes(text));
    }

    internal static VerdictRow? Evaluate(Briefing.Input input, string role)
    {
        if (!input.Logs.TryGetValue(EntryName, out var lines)) return null;
        var records = lines.Where(line => line.Length != 0).Take(2).ToArray();
        if (records.Length != 1 || records[0].Length > 160 || Receipt.Match(records[0]) is not { Success: true } match ||
            !Guid.TryParseExact(match.Groups[1].Value, "N", out Guid run) || run == Guid.Empty ||
            match.Groups[2].Value != role)
            return new(RuleId, VerdictStatus.Grey, "Kein gültiger Beleg für diese Rolle", EntryName);

        int observed = match.Groups[3].Value[0] - '0';
        int yes = match.Groups[4].Value[0] - '0';
        int no = match.Groups[5].Value[0] - '0';
        int total = observed + yes + no;
        if (total > StationCount)
            return new(RuleId, VerdictStatus.Grey, "Ungültige Anzahl der Stationen", EntryName);
        string measurement = FormattableString.Invariant(
            $"{total}/{StationCount} Stationen protokolliert, technisch {observed}, manuell {yes} Ja und {no} Nein");
        var status = no != 0 ? VerdictStatus.Red : total == StationCount ? VerdictStatus.Green : VerdictStatus.Grey;
        return new(RuleId, status, measurement, EntryName);
    }
}
