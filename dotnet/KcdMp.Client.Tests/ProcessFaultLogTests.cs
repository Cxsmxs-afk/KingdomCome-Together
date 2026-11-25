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
        Assert.Equal("MP-PROCESS-FAULT component=client operation=reader-start type=IOException terminating=0",
            await lines.Reader.ReadAsync(deadline.Token));
        Assert.Equal("MP-PROCESS-FAULT component=client operation=reader-start type=InvalidOperationException terminating=0",
            await lines.Reader.ReadAsync(deadline.Token));
        logger.ReportThrottled("join-recovery", new InvalidDataException("private"));
        logger.ReportThrottled("join-recovery", new IOException("private"));
        Assert.Equal("MP-PROCESS-FAULT component=client operation=join-recovery type=InvalidDataException terminating=0",
            await lines.Reader.ReadAsync(deadline.Token));
        Assert.False(lines.Reader.TryRead(out _));
    }
}
