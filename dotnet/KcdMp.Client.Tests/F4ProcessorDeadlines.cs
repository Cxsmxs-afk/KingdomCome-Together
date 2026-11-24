// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;

namespace KcdMp.Client.Tests;

internal sealed class F4ProcessorDeadlines : IDisposable
{
    private const int DefaultDeadlineMs = 1950; // the 2000 ms frame budget reserves 50 ms
    private readonly ConcurrentQueue<Task> _deadlines = new();

    public F4ProcessorDeadlines() => ProcessorFrameScope.DeadlineStarted += Observe;

    private void Observe(Task delay, int deadlineMs)
    {
        if (deadlineMs <= DefaultDeadlineMs && !delay.IsCompleted) _deadlines.Enqueue(delay);
    }

    public Task[] Pending() => _deadlines.Where(delay => !delay.IsCompleted).ToArray();

    public void Dispose() => ProcessorFrameScope.DeadlineStarted -= Observe;
}
