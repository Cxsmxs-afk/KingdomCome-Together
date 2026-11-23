// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
namespace KcdMp.Wire;

/// <summary>Stable join-status reasons and player-facing text for unavailable host authority.</summary>
public static class HostAuthorityFailure
{
    public const string DecisionHook = "authority-decision-hook-unavailable";
    public const string StoryHook = "authority-story-hook-unavailable";
    public const string BothHooks = "authority-hooks-unavailable";
    public const string NotConfirmed = "authority-unavailable";

    public static bool IsReason(string reason) => reason is DecisionHook or StoryHook or BothHooks or NotConfirmed;

    public static string FromStatus(string status)
    {
        var fields = status.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        bool decision = fields.Contains("decision_gate=unavailable"), story = fields.Contains("story_gate=unavailable");
        if (decision && story) return BothHooks;
        if (decision) return DecisionHook;
        if (story) return StoryHook;
        return fields.Contains("decision_gate=armed") && fields.Contains("story_gate=armed") ? "" : NotConfirmed;
    }

    public static string JoinReason(string reason) => IsReason(reason) ? reason : reason == "load-frozen" ? "frozen" : "";

    private static bool German(string language) => string.Equals(language, "de", StringComparison.OrdinalIgnoreCase)
        || (language?.StartsWith("de-", StringComparison.OrdinalIgnoreCase) ?? false);

    public static string Title(string reason, string language) => reason == NotConfirmed
        ? German(language) ? "MULTIPLAYER NICHT BEREIT" : "MULTIPLAYER NOT READY"
        : German(language) ? "MOD-UPDATE ERFORDERLICH" : "MOD UPDATE REQUIRED";

    public static string? Message(string reason, string language = "en")
    {
        if (!IsReason(reason)) return null;
        bool de = German(language);
        if (reason == NotConfirmed)
            return de
                ? "Beitritt gestoppt: Der Multiplayer-Schutz für NSC-Verhalten und Story wurde nicht bestätigt. Wähle im Koop-Menü erneut Beitreten. Bleibt die Meldung, prüfe die Mod-Version auf beiden PCs und sende die Protokolle über Report a bug."
                : "Joining stopped: Multiplayer protection for NPC AI and story was not confirmed. Choose Join / Rejoin in the co-op menu to try again. If this persists, check the mod version on both PCs and send the logs with Report a bug.";
        string system = reason == DecisionHook ? (de ? "NPC-KI" : "NPC AI")
            : reason == StoryHook ? "Story" : (de ? "NPC-KI und Story" : "NPC AI and story");
        return de
            ? $"Beitritt gestoppt: Die benötigte Spielanbindung für {system} ist in dieser Spielversion nicht verfügbar. Ein Spielupdate kann die Ursache sein. Installiere die aktuelle Mod-Version auf beiden PCs. Bleibt die Meldung, sende die Protokolle über Report a bug; dann wird ein kompatibles Mod-Update benötigt."
            : $"Joining stopped: The required game integration for {system} is unavailable in this game build. A game update may be the cause. Install the current mod on both PCs. If this persists, send the logs with Report a bug; a compatible mod update is needed.";
    }
}
