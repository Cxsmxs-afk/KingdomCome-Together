// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;

namespace KcdMp.Client;

/// <summary>Only complete, numeric native snapshots enter the 60-second report.</summary>
public static class T3AuthorityCounters
{
    public static readonly IReadOnlyList<string> NativeKeys = Array.AsReadOnly(new[] {
        "gate_armed_at_s", "decision_blocked", "decision_blocked_during_load",
        "npc_near", "npc_bound", "npc_hidden", "hide_failsafe_on", "unhidden_unbound_10s"
    });

    public static bool TryRead(string? text, out IReadOnlyDictionary<string, long> counters)
    {
        var found = new Dictionary<string, long>(StringComparer.Ordinal);
        counters = found;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (string token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            int split = token.IndexOf('=');
            if (split < 1) continue;
            string key = token[..split];
            if (!NativeKeys.Contains(key, StringComparer.Ordinal)) continue;
            if (!long.TryParse(token.AsSpan(split + 1), NumberStyles.None, CultureInfo.InvariantCulture, out long n) ||
                !found.TryAdd(key, n)) return false;
        }
        return found.Count == NativeKeys.Count;
    }
}
