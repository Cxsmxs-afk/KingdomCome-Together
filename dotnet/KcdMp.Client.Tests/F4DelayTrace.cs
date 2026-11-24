// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace KcdMp.Client.Tests;

internal sealed class F4DelayTrace : EventListener
{
    internal sealed record Source(string Method, bool IsFrameDeadline);
    private readonly ConcurrentDictionary<int, Source> _sites = new();

    internal Source? Site(Task task) => _sites.GetValueOrDefault(task.Id);

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "System.Threading.Tasks.TplEventSource")
            EnableEvents(source, EventLevel.Informational, (EventKeywords)8);
    }

    protected override void OnEventWritten(EventWrittenEventArgs data)
    {
        if (data.EventName != "TraceOperationBegin" || data.Payload is not [int id, "Task.Delay", _]) return;
        var method = new StackTrace().GetFrames().Select(frame => frame.GetMethod()).FirstOrDefault(method =>
            method?.DeclaringType?.FullName?.StartsWith("KcdMp.", StringComparison.Ordinal) == true
                && method.DeclaringType != typeof(F4DelayTrace));
        if (method?.DeclaringType is not { } type) return;
        bool frameDeadline = type.DeclaringType == typeof(ProcessorFrameScope) &&
            type.Name.StartsWith($"<{nameof(ProcessorFrameScope.RunAsync)}>", StringComparison.Ordinal);
        _sites[id] = new($"{type.FullName}.{method.Name}", frameDeadline);
    }
}
