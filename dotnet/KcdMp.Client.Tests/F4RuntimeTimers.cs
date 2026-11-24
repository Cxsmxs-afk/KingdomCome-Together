// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Reflection;

namespace KcdMp.Client.Tests;

/// <summary>Testhost diagnostics for the .NET 8 timers behind Timer.ActiveCount.</summary>
internal static class F4RuntimeTimers
{
    private const uint FrameDeadlineMaxMs = 1950; // the default 2000 ms processor budget reserves 50 ms
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly Type? QueueType = typeof(Timer).Assembly.GetType("System.Threading.TimerQueue");
    private static readonly Type? EntryType = typeof(Timer).Assembly.GetType("System.Threading.TimerQueueTimer");
    private static readonly PropertyInfo? Queues = QueueType?.GetProperty("Instances", BindingFlags.Static | BindingFlags.Public);
    private static readonly FieldInfo? Short = QueueType?.GetField("_shortTimers", Fields);
    private static readonly FieldInfo? Long = QueueType?.GetField("_longTimers", Fields);
    private static readonly FieldInfo? Next = EntryType?.GetField("_next", Fields);
    private static readonly FieldInfo? State = EntryType?.GetField("_state", Fields);
    private static readonly FieldInfo? Due = EntryType?.GetField("_dueTime", Fields);
    private static readonly FieldInfo? Period = EntryType?.GetField("_period", Fields);

    internal sealed record Inventory(int Count, string Types, Task[] FrameDeadlines);

    public static Inventory? Capture(F4DelayTrace trace, out long activeCount)
    {
        if (Queues?.GetValue(null) is not Array queues || Short is null || Long is null ||
            Next is not { } next || State is not { } state ||
            Due is not { } dueTime || Period is not { } period)
        {
            activeCount = Timer.ActiveCount;
            return null;
        }

        var counts = new Dictionary<(string Owner, uint Due, bool Repeating), int>();
        var deadlines = new List<Task>();
        int total = 0;
        // Use the same instant for the framework counter and the attribution.
        // TimerQueue operations in .NET 8 use one queue lock at a time.
        var timerQueues = queues.OfType<object>().ToArray();
        int locked = 0;
        try
        {
            for (; locked < timerQueues.Length; locked++) Monitor.Enter(timerQueues[locked]);
            activeCount = Timer.ActiveCount;
            foreach (var queue in timerQueues)
            {
                Observe(Short.GetValue(queue));
                Observe(Long.GetValue(queue));
            }
        }
        finally
        {
            while (locked > 0) Monitor.Exit(timerQueues[--locked]);
        }
        var types = counts.OrderBy(row => row.Key.Owner).ThenBy(row => row.Key.Due)
            .Select(row => $"{row.Key.Owner}:{row.Key.Due}:{(row.Key.Repeating ? "repeat" : "once")}={row.Value}");
        return new(total, string.Join(',', types), deadlines.ToArray());

        void Observe(object? entry)
        {
            for (; entry is not null; entry = next.GetValue(entry))
            {
                uint due = (uint)dueTime.GetValue(entry)!;
                if (due == uint.MaxValue) continue;
                bool repeating = (uint)period.GetValue(entry)! != uint.MaxValue;
                object? timerState = state.GetValue(entry);
                string owner = timerState?.GetType().Name.Split('`')[0] ?? "state-none";
                var task = timerState as Task;
                var site = task is not null ? trace.Site(task) : null;
                var key = (site is null ? owner : $"{owner}@{site.Method}", due, repeating);
                counts[key] = counts.GetValueOrDefault(key) + 1;
                total++;
                if (!repeating && due <= FrameDeadlineMaxMs && site is { IsFrameDeadline: true })
                    deadlines.Add(task!);
            }
        }
    }
}
