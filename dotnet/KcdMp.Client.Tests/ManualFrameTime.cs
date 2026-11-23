// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only

namespace KcdMp.Client.Tests;

/// <summary>A frame deadline that advances only when the test expires it.</summary>
internal sealed class ManualFrameTime : TimeProvider
{
    private DeadlineTimer? _timer;
    private long _ticks;
    private readonly TaskCompletionSource _armed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Armed => _armed.Task;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new DeadlineTimer(this, callback, state, dueTime);
        Volatile.Write(ref _timer, timer);
        _armed.TrySetResult();
        return timer;
    }

    public void Expire() => Volatile.Read(ref _timer)?.Fire();

    private sealed class DeadlineTimer(ManualFrameTime owner, TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        private TimerCallback? _callback = callback;

        public void Fire()
        {
            if (Interlocked.Exchange(ref _callback, null) is not { } fire) return;
            Interlocked.Add(ref owner._ticks, dueTime.Ticks);
            fire(state);
        }
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() => Interlocked.Exchange(ref _callback, null);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
