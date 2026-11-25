// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace KcdMp.Wire;

/// <summary>Observes background failures and records a redacted, single-line diagnostic.</summary>
public sealed class ProcessFaultLog(string component, Action<string> write, TimeProvider? timeProvider = null)
{
    public static ProcessFaultLog Support { get; } = new("support", message => Console.Error.WriteLine(message));

    private sealed class Throttle
    {
        public long? LastReport;
        public long Suppressed;
    }

    private readonly ConcurrentDictionary<string, Throttle> _throttles = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public void Install()
    {
        TaskScheduler.UnobservedTaskException += Unobserved;
        AppDomain.CurrentDomain.UnhandledException += Unhandled;
    }

    public void FireAndLog(Task task, string operation, ulong? sessionId = null) =>
        _ = ObserveAsync(task, operation, sessionId);

    public void Report(string operation, Exception error) => Record(operation, error, false);

    public void ReportThrottled(string operation, Exception error)
    {
        var throttle = _throttles.GetOrAdd(operation, static _ => new());
        long suppressed;
        lock (throttle)
        {
            long now = _clock.GetTimestamp();
            if (throttle.LastReport is long previous && _clock.GetElapsedTime(previous, now) < TimeSpan.FromSeconds(10))
            {
                throttle.Suppressed++;
                return;
            }
            throttle.LastReport = now;
            suppressed = throttle.Suppressed;
            throttle.Suppressed = 0;
        }
        Record(operation, error, false, suppressed: suppressed);
    }

    private async Task ObserveAsync(Task task, string operation, ulong? sessionId)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) when (task.IsCanceled) { }
        catch (Exception error)
        {
            if (task.Exception is { } aggregate)
                foreach (var failure in aggregate.Flatten().InnerExceptions) Record(operation, failure, false, sessionId);
            else Record(operation, error, false, sessionId);
        }
    }

    private void Unobserved(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        args.SetObserved();
        foreach (var error in args.Exception.Flatten().InnerExceptions) Report("unobserved-task", error);
    }

    private void Unhandled(object sender, UnhandledExceptionEventArgs args) =>
        Record("unhandled", args.ExceptionObject, args.IsTerminating);

    private void Record(string operation, object error, bool terminating, ulong? sessionId = null, long? suppressed = null)
    {
        string details = error is Exception ex
            ? $" at={OwnFrame(ex)} message=\"{SingleLine(DiagnosticRedaction.Text(ex.Message))}\"" : "";
        string session = sessionId is ulong id ? $" session={id:x}" : "";
        string count = suppressed is long n ? FormattableString.Invariant($" suppressed={n}") : "";
        try { write($"MP-PROCESS-FAULT component={component} operation={operation} type={error.GetType().Name} terminating={(terminating ? 1 : 0)}{session}{count}{details}"); }
        catch (IOException) { } // stderr can already be closed during process exit
        catch (ObjectDisposedException) { }
    }

    private static string OwnFrame(Exception error)
    {
        foreach (var frame in new StackTrace(error, true).GetFrames())
        {
            var method = frame.GetMethod();
            var owner = method?.DeclaringType;
            if (method is null || owner is null) continue;
            string? assembly = owner.Assembly.GetName().Name;
            if (assembly is not ("KcdMpClient" or "KcdMpServer" or "KCDMP_launcher") &&
                !(assembly?.StartsWith("KcdMp.", StringComparison.Ordinal) == true &&
                  !assembly.EndsWith(".Tests", StringComparison.Ordinal))) continue;

            string name = method.Name;
            if (name == "MoveNext" && owner.DeclaringType is { } parent && owner.Name.StartsWith('<') &&
                owner.Name.IndexOf('>') is int end && end > 1)
            {
                name = owner.Name[1..end];
                owner = parent;
            }
            int line = frame.GetFileLineNumber();
            return $"{owner.FullName}.{name}{(line > 0 ? $":{line}" : "")}";
        }
        return "-";
    }

    private static string SingleLine(string text)
    {
        const int maxLength = 512;
        var line = new StringBuilder(Math.Min(text.Length, maxLength));
        foreach (char chr in text.AsSpan(0, Math.Min(maxLength, text.Length)))
            line.Append(chr switch
            {
                '"' => '\'',
                '\u2028' or '\u2029' => ' ',
                _ => char.IsControl(chr) ? ' ' : chr,
            });
        if (text.Length > maxLength) line.Append('…');
        return line.ToString();
    }
}
