// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
namespace KcdMp.Wire;

public static partial class Protocol
{
    // Wo163 family: [op][role 1 host / 2 joiner][ASCII key=uint ...].
    // The DLL mirrors these explicitly as source=agent, at most once per 60 s.
    public const byte T3OpStats = 250;
}
