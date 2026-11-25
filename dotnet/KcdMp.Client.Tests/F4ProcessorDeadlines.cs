// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;

namespace KcdMp.Client.Tests;

internal sealed class F4ProcessorDeadlines
{
    private static readonly TimeSpan DefaultDeadline = TimeSpan.FromMilliseconds(1950);
    private ImmutableStack<Task<bool>> _deadlines = ImmutableStack<Task<bool>>.Empty;

    public TimeProvider Wrap(TimeProvider source) => new DeadlineClock(source, this);

    public Task<bool>[] Pending() => Volatile.Read(ref _deadlines).Where(timer => !timer.IsCompleted).ToArray();

    public (long RuntimeTimers, Task<bool>[] Deadlines) Sample()
    {
        var starting = Volatile.Read(ref _deadlines);
        var active = starting.Where(timer => !timer.IsCompleted).ToList();
        long count = Timer.ActiveCount;
        // A frame may create or dispose its timer while ActiveCount reads the
        // process queues. Keep the exact owners active on either side of that read.
        for (var added = Volatile.Read(ref _deadlines); !ReferenceEquals(added, starting); added = added.Pop())
            active.Add(added.Peek());
        return (count, active.ToArray());
    }

    private sealed class DeadlineClock(TimeProvider source, F4ProcessorDeadlines probe) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => source.GetUtcNow();
        public override long TimestampFrequency => source.TimestampFrequency;
        public override long GetTimestamp() => source.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            // This provider is used only by these processors, with the same budget as before.
            if (dueTime > DefaultDeadline) return source.CreateTimer(callback, state, dueTime, period);
            var timer = new DeadlineTimer(callback, state);
            ImmutableInterlocked.Push(ref probe._deadlines, timer.Released);
            timer.Start(source, dueTime, period);
            return timer;
        }
    }

    private sealed class DeadlineTimer(TimerCallback callback, object? state) : ITimer
    {
        private readonly TaskCompletionSource<bool> _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ITimer _timer = null!;
        private int _fired;
        public Task<bool> Released => _released.Task;

        public void Start(TimeProvider source, TimeSpan dueTime, TimeSpan period) =>
            _timer = source.CreateTimer(static timer => ((DeadlineTimer)timer!).Fire(), this, dueTime, period);

        private void Fire()
        {
            Volatile.Write(ref _fired, 1);
            callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => _timer.Change(dueTime, period);

        public void Dispose()
        {
            _timer.Dispose();
            _released.TrySetResult(Volatile.Read(ref _fired) == 0);
        }

        public async ValueTask DisposeAsync()
        {
            await _timer.DisposeAsync();
            _released.TrySetResult(Volatile.Read(ref _fired) == 0);
        }
    }
}
