// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Runtime;

namespace KcdMp.Client.Tests;

internal sealed class F4WindowsHandleTrace : IDisposable
{
    private const int ProcessHandleTracing = 32;
    private const int Open = 1;
    private const int StackFrames = 16;
    private readonly IntPtr _process = GetCurrentProcess();
    public int EnableStatus { get; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TraceCapacity
    {
        public uint Flags;
        public uint Slots;
    }

    private sealed record OpenStack(long Thread, long[] Frames, string? Problem = null);
    internal readonly record struct Allocation(string Kind, long Thread, string Stack);
    internal readonly record struct Bookkeeping(int WinsockPairs, int RegexCacheEvents)
    {
        public int Total => 2 * WinsockPairs + RegexCacheEvents;
    }
    internal readonly record struct HandleOrigins(string Details, Bookkeeping Runtime);

    public F4WindowsHandleTrace()
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
    }

    public HandleOrigins DescribeNewHandles(IReadOnlyDictionary<IntPtr, string> start, IReadOnlyDictionary<IntPtr, string> end)
    {
        if (EnableStatus < 0) return new($"enabled=0 status={unchecked((uint)EnableStatus):x8}", default);
        // Query the original handle history before querying modules or creating a
        // process snapshot. Those diagnostics perform more handle operations.
        var captured = end.Where(kv => !start.TryGetValue(kv.Key, out var kind) || kv.Value != kind)
            .Select(kv => (Kind: kv.Value, Stack: ReadOpenStack(kv.Key))).ToArray();
        using var self = Process.GetCurrentProcess();
        (long Start, int ModuleMemorySize, string ModuleName)[] modules;
        try { modules = self.Modules.Cast<ProcessModule>().Select(m => (Start: m.BaseAddress.ToInt64(), m.ModuleMemorySize, m.ModuleName)).ToArray(); }
        catch (System.ComponentModel.Win32Exception) { return new("enabled=1 modules_unavailable=1", default); }
        var liveThreads = self.Threads.Cast<ProcessThread>().Select(thread => (long)thread.Id).ToHashSet();
        // This runs after an over-budget sample. A snapshot lets the DAC read stable JIT metadata.
        using var diagnostics = TrySnapshot();
        using var runtime = TryReadRuntime(diagnostics);
        var allocations = captured.Select(handle => new Allocation(handle.Kind, handle.Stack.Thread,
            DescribeStack(handle.Stack, modules, runtime))).ToArray();
        var histories = allocations.Select(handle => $"{handle.Kind}:{handle.Stack}@thread={handle.Thread}");
        string details = $"enabled=1 clr={Environment.Version} managed={(runtime is null ? 0 : 1)} " +
            string.Join(',', histories.GroupBy(x => x).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}={g.Count()}"));
        return new(details, CountBookkeeping(allocations, liveThreads));
    }

    internal static Bookkeeping CountBookkeeping(Allocation[] allocations, IReadOnlySet<long> liveThreads)
    {
        // A first-use socket-provider context has one event and one thread
        // handle on the opening worker. Incomplete or repeated pairs still
        // count against the application budget.
        var socketHandles = allocations
            .Where(handle => liveThreads.Contains(handle.Thread))
            .Select(handle => (Handle: handle, Api: WinsockApi(handle)))
            .Where(row => row.Api is not null);
        int pairs = socketHandles.GroupBy(row => (row.Handle.Thread, row.Api)).Count(group =>
            group.Count(row => row.Handle.Kind == "Event") == 1 &&
            group.Count(row => row.Handle.Kind == "Thread") == 1);

        // RegexCache.Add enters a single static BCL monitor. Extra events
        // with this stack, and events from any application monitor, stay counted.
        int regexEvents = allocations.Count(handle => handle.Kind == "Event" &&
            handle.Stack.Contains("coreclr.dll+", StringComparison.Ordinal) &&
            handle.Stack.Split('>').Contains("System.Text.RegularExpressions.RegexCache.Add", StringComparer.Ordinal));
        return new(pairs, regexEvents == 1 ? 1 : 0);
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

    private OpenStack ReadOpenStack(IntPtr handle)
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
            if (status < 0) return new(0, [], $"status_{unchecked((uint)status):x8}");
            int traces = Marshal.ReadInt32(buffer, IntPtr.Size);
            if (traces < 0 || traces > slots) return new(0, [], "bad_count");
            OpenStack? owner = null;
            for (int i = 0; i < traces; i++)
            {
                IntPtr trace = buffer + header + i * traceSize;
                if (Marshal.ReadInt32(trace, 3 * IntPtr.Size) != Open) continue;
                var frames = new List<long>(StackFrames);
                for (int frame = 0; frame < StackFrames; frame++)
                {
                    long pc = Marshal.ReadIntPtr(trace, frameStart + frame * IntPtr.Size).ToInt64();
                    if (pc == 0) break;
                    frames.Add(pc);
                }
                // HANDLE, CLIENT_ID { process, thread }, operation, stack.
                long thread = Marshal.ReadIntPtr(trace, 2 * IntPtr.Size).ToInt64();
                if (owner is not null && (owner.Thread != thread || !frames.SequenceEqual(owner.Frames)))
                    return new(0, [], "reused_handle");
                owner = new(thread, [.. frames]);
            }
            return owner ?? new(0, [], "untraced");
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

    public void Dispose()
    {
        if (EnableStatus >= 0)
            _ = NtSetInformationProcess(_process, ProcessHandleTracing, IntPtr.Zero, 0);
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationProcess(IntPtr process, int kind, ref int buffer, int size);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationProcess(IntPtr process, int kind, ref TraceCapacity buffer, int size);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationProcess(IntPtr process, int kind, IntPtr buffer, int size);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int kind, IntPtr buffer, int length, out int needed);
}
