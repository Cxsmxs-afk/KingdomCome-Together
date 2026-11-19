// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Globalization;

namespace KcdMp.Wire;

/// <summary>
/// The host who left, remembered so only that same player and the same world can resume it.
/// The identity key stays in memory. Logs name the player, the epoch and the run, never the key.
/// </summary>
public sealed record HostLease(string IdentityKey, string Name, uint WorldEpoch, Guid RunId);

/// <summary>
/// NightCoop keeps the host's world as the only world. KCD2 has no headless simulation, so a departed
/// host is not replaced by a joiner: the session pauses, and it resumes only for the same player and world.
/// </summary>
public static class HostAbsentRules
{
    public static bool SameReturn(HostLease left, string identity, string name, uint epoch, Guid run) =>
        !string.IsNullOrEmpty(left.IdentityKey) && string.Equals(left.IdentityKey, identity, StringComparison.Ordinal)
        && string.Equals(left.Name, name, StringComparison.Ordinal)
        && epoch != 0 && epoch == left.WorldEpoch && run != Guid.Empty && run == left.RunId;

    public static bool MayWritePersonalSave(bool hostAbsent) => !hostAbsent;

    public enum GoneAction { Ignore, AbortJoin, Pause }

    public static GoneAction OnHostGone(bool joinedWorld, bool joinPreparing, bool joinLoading) =>
        joinPreparing || joinLoading ? GoneAction.AbortJoin : joinedWorld ? GoneAction.Pause : GoneAction.Ignore;

    public static bool TryReadRunFromDown(byte[] payload, out Guid run)
    {
        run = Guid.Empty;
        if (!Protocol.TrySplitJoinDown(payload, out _, out _, out _, out var body)) return false;
        return LootMsg.TryDecode(body, out var message) && TryReadRun(message.Text, out run);
    }

    public static bool TryReadRun(string? text, out Guid run)
    {
        run = Guid.Empty;
        if (string.IsNullOrEmpty(text)) return false;
        var words = text.Split('|');
        if (words.Length < 7 || words[4] != "r1") return false;
        return Guid.TryParseExact(words[5], "N", out run) && run != Guid.Empty
            && ulong.TryParse(words[6], NumberStyles.None, CultureInfo.InvariantCulture, out ulong generation) && generation != 0;
    }
}
