// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only

namespace KcdMp.Client.Tests;

/// <summary>A frame deadline that advances only when the test expires it.</summary>
internal sealed class ManualFrameTime : TimeProvider
{
    private DeadlineTimer? _timer;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        _timer = new DeadlineTimer(callback, state);
        return _timer;
    }

    public void Expire() => _timer?.Fire();

    private sealed class DeadlineTimer(TimerCallback callback, object? state) : ITimer
    {
        private TimerCallback? _callback = callback;

        public void Fire() => Interlocked.Exchange(ref _callback, null)?.Invoke(state);
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() => Interlocked.Exchange(ref _callback, null);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
