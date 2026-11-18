// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Globalization;
using KcdMp.Client;
using KcdMp.Wire;

static class BotHostCalendar
{
    private const int RunningRatio = 15;

    // Calendar.GetWorldTime() supplies level-relative seconds. Keep the
    // original precision until converting to the wire's milliseconds.
    public static bool TryParseWorldTime(string text, out long milliseconds)
    {
        milliseconds = 0;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
            || !double.IsFinite(seconds) || seconds < 0 || seconds > HostCalendar.MaxWorldMs / 1000.0)
            return false;
        milliseconds = (long)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero);
        return true;
    }

    public static long ParseWorldTime(string text) =>
        TryParseWorldTime(text, out var milliseconds) ? milliseconds
            : throw new FormatException("Der Bot-Host braucht eine gültige --world-time in Weltsekunden.");

    public static byte[] Frozen(byte target, uint joinId, long worldMs)
    {
        if (joinId == 0) throw new ArgumentOutOfRangeException(nameof(joinId));
        return Build(target, worldMs, ratio1000: 0, joinId: joinId);
    }

    public static byte[] Running(byte target, long startWorldMs, TimeSpan elapsed)
    {
        long elapsedMs = Math.Max(0, elapsed.Ticks / TimeSpan.TicksPerMillisecond);
        long worldMs = Math.Min(HostCalendar.MaxWorldMs, startWorldMs + elapsedMs * RunningRatio);
        return Build(target, worldMs, ratio1000: RunningRatio * 1000, joinId: 0);
    }

    private static byte[] Build(byte target, long worldMs, int ratio1000, uint joinId)
    {
        // The bot has no synchronized relay clock. Receivers use their
        // existing fallback transit estimate for these samples.
        string sample = HostCalendar.Format(worldMs, ratio1000, relayMs: 0, synced: false, joinId: joinId);
        return new LootMsg(Protocol.W164Clock, 0, sample).BuildUp(Protocol.W164Up, target);
    }
}
