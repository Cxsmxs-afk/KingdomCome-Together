// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
namespace KcdMp.Steam;

/// <summary>Only accepted outbound or received payloads renew the high-frequency pump window.</summary>
internal sealed class SteamPumpCadence(TimeProvider clock)
{
    internal const int ActiveMs = 1;
    internal const int IdleMs = 10;
    private static readonly TimeSpan ActivityWindow = TimeSpan.FromMilliseconds(100);
    private long _lastPayload = long.MinValue;

    internal void ObservePayload() => Volatile.Write(ref _lastPayload, clock.GetTimestamp());

    internal int WaitMs
    {
        get
        {
            long last = Volatile.Read(ref _lastPayload);
            return last != long.MinValue && clock.GetElapsedTime(last) < ActivityWindow ? ActiveMs : IdleMs;
        }
    }
}
