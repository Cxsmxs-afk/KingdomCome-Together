// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Globalization;

namespace KcdMp.Client;

/// <summary>A calendar sample, never a world-simulation speed. 15000 means the game's normal 15 calendar seconds per real second.</summary>
public readonly record struct HostCalendarSample(long WorldMs, int Ratio1000, long ArrivedMs, long TransitMs, uint JoinId = 0)
{
    public const long RunningFreshMs = 10000;
    public double Ratio => Ratio1000 / 1000.0;
    public bool Fresh(long nowMs) => Ratio1000 == 0 || (nowMs >= ArrivedMs && nowMs - ArrivedMs + TransitMs <= RunningFreshMs);
    public bool ReadyForJoin(long nowMs, uint joinId) => JoinId == joinId && Fresh(nowMs);
    public long TargetMs(long nowMs) => Math.Min(HostCalendar.MaxWorldMs, WorldMs + (long)(Math.Max(0, nowMs - ArrivedMs + TransitMs) * Ratio));
}

public static class HostCalendar
{
    public const long MaxWorldMs = (long)uint.MaxValue * 1000;

    public static string Format(long worldMs, int ratio1000, long relayMs, bool synced, uint joinId = 0) =>
        string.Create(CultureInfo.InvariantCulture, $"b1 {worldMs} {ratio1000} {relayMs} {(synced ? 1 : 0)} {joinId}");

    /// <summary>Reliable clock channel v1. A relay-clock timestamp includes queue/network age when both peers have a clock estimate.</summary>
    public static bool TryParse(string text, long nowMs, long relayNowMs, bool synced, double fallbackTransitMs, out HostCalendarSample sample)
    {
        sample = default;
        var f = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length != 6 || f[0] != "b1" ||
            !long.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out long worldMs) || worldMs < 0 || worldMs > MaxWorldMs ||
            !int.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out int ratio1000) || ratio1000 < 0 || ratio1000 > 15000 ||
            !long.TryParse(f[3], NumberStyles.None, CultureInfo.InvariantCulture, out long relayMs) || relayMs < 0 ||
            (f[4] is not ("0" or "1")) || !uint.TryParse(f[5], NumberStyles.None, CultureInfo.InvariantCulture, out uint joinId)) return false;
        long transit = synced && f[4] == "1" ? relayNowMs - relayMs : (long)Math.Clamp(double.IsFinite(fallbackTransitMs) ? fallbackTransitMs : 0, 0, 5000);
        // More than a second in the future is a bad clock estimate; a paused snapshot does not age in game time.
        if (transit < -1000 || (ratio1000 != 0 && transit > HostCalendarSample.RunningFreshMs)) return false;
        sample = new HostCalendarSample(worldMs, ratio1000, nowMs, Math.Max(0, transit), joinId);
        return true;
    }
}
