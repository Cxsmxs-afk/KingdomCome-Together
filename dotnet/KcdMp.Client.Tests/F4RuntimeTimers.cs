// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Reflection;

namespace KcdMp.Client.Tests;

/// <summary>Testhost diagnostics for the .NET 8 timers behind Timer.ActiveCount.</summary>
internal static class F4RuntimeTimers
{
    private const uint MaxOperationDeadlineMs = 5000;
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly Type? QueueType = typeof(Timer).Assembly.GetType("System.Threading.TimerQueue");
    private static readonly Type? EntryType = typeof(Timer).Assembly.GetType("System.Threading.TimerQueueTimer");
    private static readonly PropertyInfo? Queues = QueueType?.GetProperty("Instances", BindingFlags.Static | BindingFlags.Public);
    private static readonly FieldInfo? Short = QueueType?.GetField("_shortTimers", Fields);
    private static readonly FieldInfo? Long = QueueType?.GetField("_longTimers", Fields);
    private static readonly FieldInfo? Next = EntryType?.GetField("_next", Fields);
    private static readonly FieldInfo? State = EntryType?.GetField("_state", Fields);
    private static readonly FieldInfo? Start = EntryType?.GetField("_startTicks", Fields);
    private static readonly FieldInfo? Due = EntryType?.GetField("_dueTime", Fields);
    private static readonly FieldInfo? Period = EntryType?.GetField("_period", Fields);

    internal sealed record Inventory(int Count, int ShortDeadlines, long WaitMs, string Types);

    public static Inventory? Capture()
    {
        if (Queues?.GetValue(null) is not Array queues || Short is null || Long is null ||
            Next is not { } next || State is not { } state || Start is not { } start ||
            Due is not { } dueTime || Period is not { } period) return null;

        var counts = new Dictionary<(string Owner, uint Due, bool Repeating), int>();
        int total = 0, deadlines = 0;
        long wait = 0;
        foreach (var queue in queues)
        {
            if (queue is null) continue;
            lock (queue)
            {
                Observe(Short.GetValue(queue));
                Observe(Long.GetValue(queue));
            }
        }
        var types = counts.OrderBy(row => row.Key.Owner).ThenBy(row => row.Key.Due)
            .Select(row => $"{row.Key.Owner}:{row.Key.Due}:{(row.Key.Repeating ? "repeat" : "once")}={row.Value}");
        return new(total, deadlines, wait, string.Join(',', types));

        void Observe(object? entry)
        {
            for (; entry is not null; entry = next.GetValue(entry))
            {
                uint due = (uint)dueTime.GetValue(entry)!;
                if (due == uint.MaxValue) continue;
                bool repeating = (uint)period.GetValue(entry)! != uint.MaxValue;
                string owner = state.GetValue(entry)?.GetType().Name.Split('`')[0] ?? "state-none";
                var key = (owner, due, repeating);
                counts[key] = counts.GetValueOrDefault(key) + 1;
                total++;
                if (repeating || due > MaxOperationDeadlineMs ||
                    owner is not ("CancellationTokenSource" or "DelayPromise" or "DelayPromiseWithCancellation" or "CancellationPromise")) continue;
                deadlines++;
                long age = Environment.TickCount64 - (long)start.GetValue(entry)!;
                wait = Math.Max(wait, Math.Clamp(due - age, 0, MaxOperationDeadlineMs));
            }
        }
    }
}
