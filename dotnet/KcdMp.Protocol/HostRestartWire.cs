// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Globalization;

namespace KcdMp.Wire;

public readonly record struct HostRestartWorld(uint Epoch, Guid Run);

/// <summary>Private host ticket and public, relay-authored restart notice. The ticket never accompanies a notice.</summary>
public static class HostRestartWire
{
    public const int TicketBytes = 32;

    public static bool TryTicket(string text, out byte[] ticket)
    {
        ticket = [];
        if (text.Length != TicketBytes * 2 || !text.All(char.IsAsciiHexDigit)) return false;
        ticket = Convert.FromHexString(text);
        return true;
    }

    public static string Proof(HostRestartWorld next, byte[] ticket) =>
        FormattableString.Invariant($"v1|{next.Epoch}|{next.Run:N}|{Convert.ToHexString(ticket)}");

    public static bool TryProof(string text, out HostRestartWorld next, out byte[] ticket)
    {
        next = default;
        ticket = [];
        var words = text.Split('|');
        return words.Length == 4 && words[0] == "v1" && TryWorld(words[1], words[2], out next) && TryTicket(words[3], out ticket);
    }

    public static string Notice(HostRestartWorld previous, HostRestartWorld next) =>
        FormattableString.Invariant($"v1|{previous.Epoch}|{previous.Run:N}|{next.Epoch}|{next.Run:N}");

    public static bool TryNotice(string text, out HostRestartWorld previous, out HostRestartWorld next)
    {
        previous = next = default;
        var words = text.Split('|');
        return words.Length == 5 && words[0] == "v1" && TryWorld(words[1], words[2], out previous) &&
            TryWorld(words[3], words[4], out next) && previous.Run != next.Run;
    }

    private static bool TryWorld(string epoch, string run, out HostRestartWorld world)
    {
        world = default;
        if (!uint.TryParse(epoch, NumberStyles.None, CultureInfo.InvariantCulture, out uint e) || e == 0 ||
            !Guid.TryParseExact(run, "N", out Guid r) || r == Guid.Empty) return false;
        world = new(e, r);
        return true;
    }
}
