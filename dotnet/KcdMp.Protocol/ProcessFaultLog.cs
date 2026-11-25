// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;

namespace KcdMp.Wire;

/// <summary>Observes background failures without copying exception data into support logs.</summary>
public sealed class ProcessFaultLog(string component, Action<string> write)
{
    private readonly ConcurrentDictionary<string, long> _nextReport = new(StringComparer.Ordinal);

    public void Install()
    {
        TaskScheduler.UnobservedTaskException += Unobserved;
        AppDomain.CurrentDomain.UnhandledException += Unhandled;
    }

    public void FireAndLog(Task task, string operation) => _ = ObserveAsync(task, operation);

    public void Report(string operation, Exception error) => Record(operation, error.GetType().Name, false);

    public void ReportThrottled(string operation, Exception error)
    {
        long now = Environment.TickCount64;
        long next = now + 10_000;
        if (_nextReport.TryAdd(operation, next) ||
            (_nextReport.TryGetValue(operation, out long previous) && previous <= now &&
             _nextReport.TryUpdate(operation, next, previous)))
            Report(operation, error);
    }

    private async Task ObserveAsync(Task task, string operation)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) when (task.IsCanceled) { }
        catch (Exception error)
        {
            if (task.Exception is { } aggregate)
                foreach (var failure in aggregate.Flatten().InnerExceptions) Report(operation, failure);
            else Report(operation, error);
        }
    }

    private void Unobserved(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        args.SetObserved();
        foreach (var error in args.Exception.Flatten().InnerExceptions) Report("unobserved-task", error);
    }

    private void Unhandled(object sender, UnhandledExceptionEventArgs args) =>
        Record("unhandled", args.ExceptionObject.GetType().Name, args.IsTerminating);

    private void Record(string operation, string type, bool terminating)
    {
        try { write($"MP-PROCESS-FAULT component={component} operation={operation} type={type} terminating={(terminating ? 1 : 0)}"); }
        catch (IOException) { } // stderr can already be closed during process exit
        catch (ObjectDisposedException) { }
    }

}
