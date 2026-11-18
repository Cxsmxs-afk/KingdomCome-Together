// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
namespace KcdMp.Wire;

public static partial class Protocol
{
    // Wo163 family: [op] reads the native snapshot; [op][role 1 host / 2 joiner]
    // followed by ASCII counters mirrors the agent line at most once per 60 s.
    // The test-only clock command is [op][3][on:0|1].
    public const byte T3OpStats = 250;
    public const byte T3PresentationClockCommand = 3;
    public const byte T3TimelineStatsCommand = 4;
}
