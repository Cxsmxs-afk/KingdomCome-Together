// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
namespace KcdMp.Client;

/// <summary>Use the engine cache between loads; a snapshot must never rotate away an own save.</summary>
public static class SaveListMaintenance
{
    public static string SnapshotBlock(int diskQuicksaves, int? cachedDescriptions)
    {
        if (diskQuicksaves >= GameBridge.SnapshotQuicksaveGuard)
            return $"{diskQuicksaves} quicksaves on disk (the engine rotates at 100; an own save could be removed)";
        if (cachedDescriptions is null or < 0)
            return "the engine's cached save count is unavailable; quicksave rotation cannot be ruled out";
        // This deliberately includes other save kinds and entries whose files the mod moved out.
        // Refuse a snapshot rather than refreshing the whole list during active play or risking rotation.
        if (cachedDescriptions >= GameBridge.SnapshotQuicksaveGuard)
            return $"{cachedDescriptions} cached save entries (possibly stale; quicksave rotation cannot be ruled out)";
        return "";
    }

    /// <summary>The newest verified own save found in the cache. One missing entry may refresh the
    /// whole engine list once for this leave attempt; later candidates only query that refreshed cache.</summary>
    public static async Task<T?> FindOwnAsync<T>(IEnumerable<T> candidates, Func<T, bool> verify,
        Func<T, byte, Task<CombatPipe.SaveListReport?>> query, Action<T, string>? trace = null) where T : class
    {
        bool refreshed = false;
        foreach (T candidate in candidates)
        {
            if (!verify(candidate)) continue;
            var r = await query(candidate, 3);
            if (r is { Listed: false } && !refreshed)
            {
                refreshed = true;
                r = await query(candidate, 1);
            }
            if (r is null)
            {
                trace?.Invoke(candidate, "the plugin did not answer; verified file used without engine-list confirmation");
                return candidate;
            }
            if (r.Listed) return candidate;
            trace?.Invoke(candidate, "not in the cached engine list (one refresh at most for this leave attempt)");
        }
        return null;
    }
}
