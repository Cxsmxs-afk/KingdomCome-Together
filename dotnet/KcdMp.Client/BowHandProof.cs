// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using KcdMp.Wire;

namespace KcdMp.Client;

internal readonly record struct BowHandProof(Guid ItemClass, long CapturedAt)
{
    internal static bool IsDrawnPhase(byte phase) => phase is 1 or 2 or 3;

    internal bool Fresh(long now) => now >= CapturedAt && now - CapturedAt <= Protocol.RangedCaptureMaxAgeMs;
}
