// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Threading.Channels;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public sealed class ProcessFaultLogTests
{
    [Fact]
    public async Task Startup_tasks_log_each_fault_by_type_and_ignore_expected_cancellation()
    {
        var lines = Channel.CreateUnbounded<string>();
        var logger = new ProcessFaultLog("client", line => lines.Writer.TryWrite(line));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        logger.FireAndLog(Task.FromCanceled(new CancellationToken(true)), "normal-stop");
        var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        logger.FireAndLog(operation.Task, "reader-start");
        operation.SetException(new Exception[] { new IOException("private"), new InvalidOperationException("private") });
        Assert.StartsWith("MP-PROCESS-FAULT component=client operation=reader-start type=IOException terminating=0",
            await lines.Reader.ReadAsync(deadline.Token));
        Assert.StartsWith("MP-PROCESS-FAULT component=client operation=reader-start type=InvalidOperationException terminating=0",
            await lines.Reader.ReadAsync(deadline.Token));
        logger.ReportThrottled("join-recovery", new InvalidDataException("private"));
        logger.ReportThrottled("join-recovery", new IOException("private"));
        Assert.StartsWith("MP-PROCESS-FAULT component=client operation=join-recovery type=InvalidDataException terminating=0",
            await lines.Reader.ReadAsync(deadline.Token));
        Assert.False(lines.Reader.TryRead(out _));
    }

    [Fact]
    public void A_throttled_report_counts_suppressed_errors_in_the_next_entry()
    {
        var lines = new List<string>();
        var clock = new ReportClock();
        var logger = new ProcessFaultLog("client", lines.Add, clock);
        var failure = new IOException("disk unavailable");
        logger.ReportThrottled("stage", failure);
        logger.ReportThrottled("stage", failure);
        logger.ReportThrottled("stage", failure);
        Assert.Contains("suppressed=0", Assert.Single(lines));
        clock.AddSeconds(10);
        logger.ReportThrottled("stage", failure);
        Assert.Equal(2, lines.Count);
        Assert.Contains("suppressed=2", lines[1]);
        Assert.Contains("message=\"disk unavailable\"", lines[1]);
    }

    private sealed class ReportClock : TimeProvider
    {
        private long _now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _now;
        public void AddSeconds(int seconds) => _now += TimeSpan.FromSeconds(seconds).Ticks;
    }
}
