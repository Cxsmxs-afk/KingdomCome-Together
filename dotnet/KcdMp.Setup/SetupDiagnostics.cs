// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;

namespace KcdMp.Setup;

internal static class SetupDiagnostics
{
    private static readonly ConcurrentDictionary<string, long> NextReport = new(StringComparer.Ordinal);

    internal static void ReportThrottled(string operation, Exception error)
    {
        long now = Environment.TickCount64;
        long next = now + 10_000;
        if (NextReport.TryAdd(operation, next) ||
            (NextReport.TryGetValue(operation, out long previous) && previous <= now &&
             NextReport.TryUpdate(operation, next, previous)))
        {
            try { Console.Error.WriteLine($"MP-SETUP diagnostic={operation} type={error.GetType().Name}"); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }
}
