// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
namespace KcdMp.Wire;

/// <summary>Bounded admission for a single authenticated source incarnation. The caller serializes access.</summary>
public struct PvpContactWindow
{
    private ulong _newest;
    private ulong _seen;

    public bool TryClaim(ulong sequence)
    {
        if (sequence == 0) return false;
        if (sequence > _newest)
        {
            ulong advance = sequence - _newest;
            _seen = advance >= 64 ? 1 : (_seen << (int)advance) | 1;
            _newest = sequence;
            return true;
        }
        ulong age = _newest - sequence;
        if (age >= 64) return false;
        ulong bit = 1UL << (int)age;
        if ((_seen & bit) != 0) return false;
        _seen |= bit;
        return true;
    }
}
