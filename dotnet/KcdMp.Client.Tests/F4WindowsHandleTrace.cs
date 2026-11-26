// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Runtime;

namespace KcdMp.Client.Tests;

internal sealed class F4WindowsHandleTrace : IDisposable
{
    private const int ProcessHandleTracing = 32;
    private const int Open = 1;
    private const int Close = 2;
    private const int StackFrames = 16;
    private readonly IntPtr _process = GetCurrentProcess();
    private readonly bool? _newestFirst;
    private readonly F4PersistentMonitorTrace? _monitors;
    public int EnableStatus { get; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TraceCapacity
    {
        public uint Flags;
        public uint Slots;
    }

    private sealed record OpenStack(long Thread, long[] Frames, string? Problem = null);
    private sealed record HandleOperation(int Kind, long Thread, long[] Frames);
    private sealed record HandleHistory(HandleOperation[] Operations, string? Problem = null);
    private sealed record WorkerObservation(long Id, string[] Frames, bool Waiting);
    private enum WorkerStartupHandle { None, FirstEvent, SecondEvent, Thread }
    internal readonly record struct Allocation(string Kind, long Thread, string Stack, long Worker = 0, long TargetThread = 0,
        string? PersistentMonitor = null);
    internal readonly record struct Bookkeeping(int WinsockPairs, int RegexCacheEvents, int WorkerStarts, int WorkerNameEvents,
        int WinsockThreadReferences = 0, int PersistentMonitorEvents = 0)
    {
        public int Total => 2 * WinsockPairs + WinsockThreadReferences + RegexCacheEvents + 3 * WorkerStarts +
            WorkerNameEvents + PersistentMonitorEvents;
    }
    internal readonly record struct HandleOrigins(string Details, Bookkeeping Runtime, int IdleNewWorkers = 0);

    public F4WindowsHandleTrace(bool observeMonitors)
    {
        // PROCESS_HANDLE_TRACING_ENABLE_EX. Retain history across the TCP and
        // handle-inventory churn without keeping every open operation in the test.
        var capacity = new TraceCapacity { Flags = 0, Slots = 16_384 };
        EnableStatus = NtSetInformationProcess(_process, ProcessHandleTracing, ref capacity, Marshal.SizeOf<TraceCapacity>());
        if (EnableStatus < 0)
        {
            int flags = 0;
            EnableStatus = NtSetInformationProcess(_process, ProcessHandleTracing, ref flags, sizeof(int));
        }
        if (EnableStatus >= 0) _newestFirst = DetectHistoryOrder();
        if (_newestFirst is not null && observeMonitors && F4PersistentMonitorTrace.Supported)
            _monitors = new F4PersistentMonitorTrace();
    }

    public bool TracksMonitors => _monitors is not null;
    public void TrackGates(string role, object owner) => _monitors?.Track(role, owner);
    public IReadOnlyDictionary<nint, string>? SampleMonitorHandles() => _monitors?.ReadHandles();

    public HandleOrigins DescribeNewHandles(IReadOnlyDictionary<IntPtr, string> start, IReadOnlyDictionary<IntPtr, string> end,
        IReadOnlyDictionary<nint, string>? monitors, IReadOnlySet<long> newThreads, bool checkIdleWorkers)
    {
        if (EnableStatus < 0) return new($"enabled=0 status={unchecked((uint)EnableStatus):x8}", default);
        // Query the original handle history before querying modules or creating a
        // process snapshot. Those diagnostics perform more handle operations.
        var captured = end.Where(kv => !start.TryGetValue(kv.Key, out var kind) || kv.Value != kind)
            .Select(kv => (Handle: kv.Key, Kind: kv.Value, Stack: ReadOpenStack(kv.Key),
                Worker: kv.Value == "Thread" ? ThreadPoolWorkerId(kv.Key) : 0,
                Target: kv.Value == "Thread" ? (long)GetThreadId(kv.Key) : 0)).ToArray();
        using var self = Process.GetCurrentProcess();
        (long Start, int ModuleMemorySize, string ModuleName)[] modules;
        try { modules = self.Modules.Cast<ProcessModule>().Select(m => (Start: m.BaseAddress.ToInt64(), m.ModuleMemorySize, m.ModuleName)).ToArray(); }
        catch (System.ComponentModel.Win32Exception) { return new("enabled=1 modules_unavailable=1", default); }
        var liveThreads = self.Threads.Cast<ProcessThread>().Select(thread => (long)thread.Id).ToHashSet();
        // This runs after an over-budget sample. A snapshot lets the DAC read stable JIT metadata.
        using var diagnostics = TrySnapshot();
        using var runtime = TryReadRuntime(diagnostics);
        var allocations = captured.Select(handle => new Allocation(handle.Kind, handle.Stack.Thread,
            DescribeStack(handle.Stack, modules, runtime), handle.Worker, handle.Target,
            handle.Kind == "Event" ? monitors?.GetValueOrDefault(handle.Handle) : null)).ToArray();
        bool knownNativeWorkerStack = RuntimeInformation.ProcessArchitecture == Architecture.X64 && Environment.Version == new Version(8, 0, 34);
        var bookkeeping = CountBookkeeping(allocations, liveThreads, out var startedWorkers, knownNativeWorkerStack);
        var workers = checkIdleWorkers ? ObservePoolWorkers(runtime, startedWorkers, newThreads) : [];
        var histories = allocations.Select(handle =>
            $"{handle.Kind}:{handle.Stack}@thread={handle.Thread}@worker={handle.Worker}@target={handle.TargetThread}" +
            (handle.PersistentMonitor is { } monitor ? $"@monitor={monitor}" : ""));
        string order = _newestFirst is true ? "newest-first" : _newestFirst is false ? "oldest-first" : "unknown";
        var workerStacks = workers.OrderBy(worker => worker.Id)
            .Select(worker => $"{worker.Id}:{(worker.Waiting ? "waiting" : "other")}:{string.Join('>', worker.Frames.Take(6))}");
        string details = $"enabled=1 clr={Environment.Version} managed={(runtime is null ? 0 : 1)} order={order} " +
            (knownNativeWorkerStack ? "" : $"native=[Allowlist gilt nicht für {Environment.Version} ({RuntimeInformation.ProcessArchitecture})] ") +
            $"new_worker_stacks={string.Join(',', workerStacks)} " +
            string.Join(',', histories.GroupBy(x => x).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}={g.Count()}"));
        return new(details, bookkeeping, workers.Count(worker => worker.Waiting));
    }

    internal static Bookkeeping CountBookkeeping(Allocation[] allocations, IReadOnlySet<long> liveThreads, out HashSet<long> startedWorkers,
        bool knownNativeWorkerStack = false)
    {
        var ordinary = allocations.Where(handle => handle.PersistentMonitor is null).ToArray();
        // The provider holds an Event and a reference to its actual calling
        // thread. An Event alone or competing references do not identify the
        // owner. A single Thread handle can identify itself via its OS target.
        var socketHandles = ordinary
            .Where(handle => liveThreads.Contains(handle.Thread))
            .Select(handle => (Handle: handle, Api: WinsockApi(handle)))
            .Where(row => row.Api is not null);
        int pairs = 0, threadReferences = 0;
        foreach (var socket in socketHandles.GroupBy(row => (row.Handle.Thread, row.Api)))
        {
            var threads = socket.Where(row => row.Handle.Kind == "Thread").ToArray();
            if (threads.Length != 1 || threads[0].Handle.TargetThread != socket.Key.Thread) continue;
            int events = socket.Count(row => row.Handle.Kind == "Event");
            if (events == 1) pairs++;
            else if (events == 0) threadReferences++;
        }

        // RegexCache.Add enters a single static BCL monitor. Extra events
        // with this stack, and events from any application monitor, stay counted.
        int regexEvents = ordinary.Count(handle => handle.Kind == "Event" &&
            handle.Stack.Contains("coreclr.dll+", StringComparison.Ordinal) &&
            handle.Stack.Split('>').Contains("System.Text.RegularExpressions.RegexCache.Add", StringComparer.Ordinal));

        // Attribute CLR startup only for identified, still-live pool workers.
        // An application-owned Thread.StartCore or an unmatched Event stays counted.
        var workers = new HashSet<long>();
        var threadStarts = ordinary.Where(handle => handle.Thread != 0 && (ManagedThreadStart(handle) ||
            knownNativeWorkerStack && handle.Stack.EndsWith(">unresolved", StringComparison.Ordinal) &&
            NativeThreadStart(handle) != WorkerStartupHandle.None));
        foreach (var launch in threadStarts.GroupBy(handle => handle.Thread))
        {
            var threads = launch.Where(handle => handle.Kind == "Thread").ToArray();
            var events = launch.Where(handle => handle.Kind == "Event").ToArray();
            var targetIds = threads.Select(handle => handle.Worker).ToHashSet();
            if (threads.Length == 0 || targetIds.Contains(0) || targetIds.Count != threads.Length ||
                !targetIds.IsSubsetOf(liveThreads) || events.Length != 2 * threads.Length) continue;

            // When DAC cannot resolve the managed frame, require both exact
            // native event call sites and the kernel target of each worker handle.
            if (launch.Any(handle => !ManagedThreadStart(handle)) &&
                (threads.Any(thread => thread.Worker != thread.TargetThread || NativeThreadStart(thread) != WorkerStartupHandle.Thread) ||
                events.Count(handle => NativeThreadStart(handle) == WorkerStartupHandle.FirstEvent) != threads.Length ||
                events.Count(handle => NativeThreadStart(handle) == WorkerStartupHandle.SecondEvent) != threads.Length)) continue;
            workers.UnionWith(targetIds);
        }
        // The CLR assigns the name under the new worker's own monitor. Its
        // single contended initialization event belongs to that same worker.
        int nameEvents = ordinary.Where(handle => handle.Kind == "Event" && workers.Contains(handle.Thread) &&
                handle.Stack.Contains("coreclr.dll+", StringComparison.Ordinal) &&
                handle.Stack.Split('>').Contains("System.Threading.Thread.SetThreadPoolWorkerThreadName", StringComparer.Ordinal))
            .GroupBy(handle => handle.Thread).Count(group => group.Count() == 1);
        // A real native handle and a named pre-existing gate must both match.
        // Unknown or per-leave monitors are charged even with this stack.
        int monitorEvents = knownNativeWorkerStack ? allocations.Where(handle => handle.Kind == "Event" && handle.PersistentMonitor is not null)
            .Select(handle => (handle.PersistentMonitor, Stack: handle.Stack.Split('>')))
            .Where(handle => handle.Stack.Contains("coreclr.dll+886b", StringComparer.OrdinalIgnoreCase) &&
                handle.Stack.Any(frame => frame is "System.Threading.SemaphoreSlim.WaitAsync" or
                    "System.Threading.SemaphoreSlim.Wait" or "System.Threading.SemaphoreSlim.Release"))
            .GroupBy(handle => handle.PersistentMonitor).Count(group => group.Count() == 1) : 0;
        startedWorkers = workers;
        return new(pairs, regexEvents == 1 ? 1 : 0, workers.Count, nameEvents, threadReferences, monitorEvents);
    }

    private static bool ManagedThreadStart(Allocation handle) =>
        handle.Stack.Contains("coreclr.dll+", StringComparison.Ordinal) &&
        handle.Stack.Split('>').Contains("System.Threading.Thread.StartCore", StringComparer.Ordinal);

    // CoreCLR 8.0.34 x64. The #74 traces identify these same native stacks
    // with the managed Thread.StartCore frame. No match for a different runtime.
    private static WorkerStartupHandle NativeThreadStart(Allocation handle)
    {
        const string chain = ">coreclr.dll+416c1>coreclr.dll+413b3>coreclr.dll+408f0>";
        if (handle.Kind == "Thread" && handle.Stack.Contains(">coreclr.dll+417d4" + chain, StringComparison.Ordinal))
            return WorkerStartupHandle.Thread;
        if (handle.Kind != "Event") return WorkerStartupHandle.None;
        if (handle.Stack.Contains(">coreclr.dll+418df>coreclr.dll+4188c>coreclr.dll+417ad" + chain, StringComparison.Ordinal))
            return WorkerStartupHandle.FirstEvent;
        if (handle.Stack.Contains(">coreclr.dll+418df>coreclr.dll+4189a>coreclr.dll+417ad" + chain, StringComparison.Ordinal))
            return WorkerStartupHandle.SecondEvent;
        return WorkerStartupHandle.None;
    }

    private static WorkerObservation[] ObservePoolWorkers(ClrRuntime? runtime, IReadOnlySet<long> startedWorkers, IReadOnlySet<long> newThreads)
    {
        if (runtime is null) return [];
        try
        {
            var observations = new List<WorkerObservation>();
            foreach (var thread in runtime.Threads)
            {
                if (!thread.IsAlive || !startedWorkers.Contains(thread.OSThreadId) || !newThreads.Contains(thread.OSThreadId)) continue;
                var frames = thread.EnumerateStackTrace(includeContext: false, maxFrames: 24)
                    .Where(frame => frame.Kind != ClrStackFrameKind.Runtime)
                    .Select(frame => frame.Method is { Type.Name: string type } method ? $"{type}.{method.Name}" : "unresolved")
                    .ToArray();
                observations.Add(new(thread.OSThreadId, frames, IsPoolWaiting(frames)));
            }
            return [.. observations];
        }
        catch (Exception) { return []; } // a missing or unreadable stack stays inside the raw thread budget
    }

    internal static bool IsPoolWaiting(string[] frames)
    {
        int worker = Array.IndexOf(frames, "System.Threading.PortableThreadPool+WorkerThread.WorkerThreadStart");
        return worker > 0 && frames.Take(worker).All(frame => frame is
            "System.Threading.LowLevelLifoSemaphore.Wait" or
            "System.Threading.LowLevelLifoSemaphore.WaitForSignal" or
            "System.Threading.LowLevelLifoSemaphore.WaitCore");
    }

    private static string? WinsockApi(Allocation handle)
    {
        if (handle.Kind is not ("Event" or "Thread")) return null;
        var stack = handle.Stack.Split('>');
        if (!stack.Contains("ws2_32.dll", StringComparer.OrdinalIgnoreCase) ||
            handle.Kind == "Event" && !stack.Contains("mswsock.dll", StringComparer.OrdinalIgnoreCase)) return null;
        return stack.FirstOrDefault(frame => frame is "Interop+Winsock.WSARecv" or "Interop+Winsock.WSASend");
    }

    private static DataTarget? TrySnapshot()
    {
        try { return DataTarget.CreateSnapshotAndAttach(Environment.ProcessId); }
        catch (Exception) { return null; } // keep the native trace if a DAC snapshot is unavailable
    }

    private static ClrRuntime? TryReadRuntime(DataTarget? diagnostics)
    {
        try
        {
            string dacPath = Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "mscordaccore.dll");
            return diagnostics?.ClrVersions.FirstOrDefault()?.CreateRuntime(dacPath);
        }
        catch (Exception) { return null; }
    }

    private bool? DetectHistoryOrder()
    {
        // The debugger displays recent operations first. Prove the ordering
        // against this running kernel before using it to select a live owner.
        using var marker = new EventWaitHandle(false, EventResetMode.AutoReset);
        IntPtr handle = marker.SafeWaitHandle.DangerousGetHandle();
        long thread = GetCurrentThreadId();
        marker.Dispose();
        var operations = ReadHistory(handle).Operations;
        if (operations.Length < 2) return null;
        bool newest = MarkerCloses(operations[0], operations[1]);
        bool oldest = MarkerCloses(operations[^1], operations[^2]);
        return newest == oldest ? null : newest;

        bool MarkerCloses(HandleOperation closed, HandleOperation opened) =>
            closed.Kind == Close && opened.Kind == Open && closed.Thread == thread && opened.Thread == thread;
    }

    private OpenStack ReadOpenStack(IntPtr handle)
    {
        if (_newestFirst is not bool newestFirst) return new(0, [], "unknown_history_order");
        var history = ReadHistory(handle);
        if (history.Problem is string problem) return new(0, [], problem);
        if (history.Operations.Length == 0) return new(0, [], "untraced");
        var opening = newestFirst ? history.Operations[0] : history.Operations[^1];
        return opening.Kind == Open ? new(opening.Thread, opening.Frames) : new(0, [], "latest_operation_is_not_open");
    }

    private HandleHistory ReadHistory(IntPtr handle)
    {
        int header = 2 * IntPtr.Size;
        int frameStart = (3 * IntPtr.Size + sizeof(int) + IntPtr.Size - 1) & ~(IntPtr.Size - 1);
        int traceSize = frameStart + StackFrames * IntPtr.Size;
        const int slots = 128;
        int bytes = header + traceSize * slots;
        IntPtr buffer = Marshal.AllocHGlobal(bytes);
        try
        {
            Marshal.WriteIntPtr(buffer, handle);
            Marshal.WriteInt32(buffer, IntPtr.Size, 0);
            int status = NtQueryInformationProcess(_process, ProcessHandleTracing, buffer, bytes, out _);
            if (status < 0) return new([], $"status_{unchecked((uint)status):x8}");
            int traces = Marshal.ReadInt32(buffer, IntPtr.Size);
            if (traces < 0 || traces > slots) return new([], "bad_count");
            var history = new HandleOperation[traces];
            for (int i = 0; i < traces; i++)
            {
                IntPtr trace = buffer + header + i * traceSize;
                int kind = Marshal.ReadInt32(trace, 3 * IntPtr.Size);
                var frames = new List<long>(StackFrames);
                for (int frame = 0; frame < StackFrames; frame++)
                {
                    long pc = Marshal.ReadIntPtr(trace, frameStart + frame * IntPtr.Size).ToInt64();
                    if (pc == 0) break;
                    frames.Add(pc);
                }
                // HANDLE, CLIENT_ID { process, thread }, operation, stack.
                long thread = Marshal.ReadIntPtr(trace, 2 * IntPtr.Size).ToInt64();
                history[i] = new(kind, thread, [.. frames]);
            }
            return new(history);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string DescribeStack(OpenStack stack, (long Start, int ModuleMemorySize, string ModuleName)[] modules,
        ClrRuntime? runtime)
    {
        if (stack.Problem is { } problem) return problem;
        var names = new List<string>();
        foreach (long pc in stack.Frames)
        {
            var module = modules.FirstOrDefault(m => pc >= m.Start && pc < m.Start + m.ModuleMemorySize);
            string name = module.ModuleName ?? ReadManagedMethod(runtime, pc);
            if (name.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase)) name += $"+{pc - module.Start:x}";
            if (names.Count == 0 || names[^1] != name) names.Add(name);
        }
        return names.Count == 0 ? "empty" : string.Join('>', names);
    }

    private static string ReadManagedMethod(ClrRuntime? runtime, long pc)
    {
        try
        {
            var method = runtime?.GetMethodByInstructionPointer(unchecked((ulong)pc));
            if (method is not null) return $"{method.Type?.Name}.{method.Name}";
        }
        catch (Exception) { } // the allocation frame can belong to code that has already unloaded
        return "unresolved";
    }

    private static long ThreadPoolWorkerId(IntPtr handle)
    {
        if (GetThreadDescription(handle, out IntPtr description) < 0) return 0;
        try { return Marshal.PtrToStringUni(description) == ".NET TP Worker" ? GetThreadId(handle) : 0; }
        finally { _ = LocalFree(description); }
    }

    public void Dispose()
    {
        _monitors?.Dispose();
        if (EnableStatus >= 0)
            _ = NtSetInformationProcess(_process, ProcessHandleTracing, IntPtr.Zero, 0);
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    private static extern uint GetThreadId(IntPtr thread);

    [DllImport("kernel32.dll")]
    private static extern int GetThreadDescription(IntPtr thread, out IntPtr description);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationProcess(IntPtr process, int kind, ref int buffer, int size);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationProcess(IntPtr process, int kind, ref TraceCapacity buffer, int size);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationProcess(IntPtr process, int kind, IntPtr buffer, int size);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int kind, IntPtr buffer, int length, out int needed);
}
