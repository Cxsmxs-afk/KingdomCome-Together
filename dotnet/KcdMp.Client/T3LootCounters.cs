// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;

namespace KcdMp.Client;

internal static class T3LootCounters
{
    internal static readonly string[] LootKeys = ["pickup_asks", "pickup_optimistic", "pickup_rollbacks", "pickup_unknown_kept", "ask_timeouts", "ask_latency_ms_p95", "take_busy_refusals"];
    internal static readonly string[] InputKeys = ["input_held_max_s", "input_releases"];
    internal static bool TryRead(string kind, string? text, out Dictionary<string, long> values)
    {
        values = new(StringComparer.Ordinal);
        string[] keys = kind == "t3_loot_stats" ? LootKeys : kind == "t3_input_stats" ? InputKeys : [];
        if (keys.Length == 0 || text is null || text.Length > 1024) return false;
        foreach (string field in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = field.IndexOf('=');
            if (eq <= 0 || !keys.Contains(field[..eq], StringComparer.Ordinal) ||
                !long.TryParse(field.AsSpan(eq + 1), NumberStyles.None, CultureInfo.InvariantCulture, out long value) || value < 0 ||
                !values.TryAdd(field[..eq], value)) return false;
        }
        return values.Count == keys.Length;
    }
}
