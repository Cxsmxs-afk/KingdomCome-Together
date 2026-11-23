// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
namespace KcdMp.Client;

/// <summary>
/// A joined game stays in the host's world across a loading screen, a short
/// outage and a relay restart. "You loaded your own save" is only the line
/// after a reconnect has actually failed.
/// </summary>
public static class SessionHold
{
    /// <summary>The relay used to drop a silent client at 30 s. A load or a stall of that length is not a dead session.</summary>
    public const int IdleTimeoutMs = 90_000;

    /// <summary>An unexpected load line does not end the session for this long.</summary>
    public const int LoadGraceMs = 45_000;

    /// <summary>How long a dropped link is retried before the player is told the session is gone (WO-211).</summary>
    public const int LinkHoldMs = 60_000;

    /// <summary>The engine printed a save load. It is this player's own save when the line names the file they would go home to.</summary>
    public static bool LoadIsOwnSave(string? display, string? ownFile) =>
        !string.IsNullOrEmpty(display) && !string.IsNullOrEmpty(ownFile)
        && display.Contains(ownFile, System.StringComparison.OrdinalIgnoreCase);

    /// <summary>Leave the host's world only once the grace has run out, a world is up, and that world is the player's own save.</summary>
    public static bool LeaveAfterLoad(bool graceOver, bool inWorld, bool ownSave) =>
        graceOver && inWorld && ownSave;

    /// <summary>The own-world line. Quiet during a load, a link hold, or a reconnect that is still trying.</summary>
    public static bool ShowOwnWorld(bool separate, bool linkHolding, bool loadGrace, bool reconnecting) =>
        separate && !linkHolding && !loadGrace && !reconnecting;

    /// <summary>No packet at all for this long is a dead link. A ping every 2 s keeps a console stall from looking like one.</summary>
    public static bool LinkDead(int silentMs, int idleMs) => silentMs >= idleMs;

    public static string FailText(string reason) =>
        $"Could not stay in your host's world ({(string.IsNullOrWhiteSpace(reason) ? "the link did not come back" : reason)}). Once back in your own game, choose Join / Rejoin your host in the co-op menu.";
}
