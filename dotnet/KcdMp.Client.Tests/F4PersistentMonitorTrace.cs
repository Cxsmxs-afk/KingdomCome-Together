// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using KcdMp.Server.Features.ClientHandling;

namespace KcdMp.Client.Tests;

internal sealed class F4PersistentMonitorTrace : EventListener
{
    // The Win-x64 .NET 8.0.34 AwareLock keeps the native event at +0x20 and
    // its allocation flags at +0x28. A different layout stays fully counted.
    internal static bool Supported => OperatingSystem.IsWindows() &&
        RuntimeInformation.ProcessArchitecture == Architecture.X64 && Environment.Version == new Version(8, 0, 34);

    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const int EventOffset = 0x20;
    private const uint AllocatedMonitorEvent = 8;
    private static readonly FieldInfo? LockObject = typeof(SemaphoreSlim).GetField("m_lockObjAndDisposed", Instance);
    private readonly ConcurrentDictionary<ulong, PersistentGate> _owners = new();
    private readonly ConcurrentDictionary<ulong, ulong> _lockAddresses = new();
    private readonly TaskCompletionSource _firstObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record PersistentGate(string Name, WeakReference<object> Owner, FieldInfo Field,
        WeakReference<SemaphoreSlim> Gate, GCHandle MonitorPin);

    internal Task FirstObserved => _firstObserved.Task;

    internal static StrongBox<bool>? GetMonitor(SemaphoreSlim gate) => LockObject?.GetValue(gate) as StrongBox<bool>;

    // Only register named gates physically present before the measured leaves.
    // The pin roots just StrongBox<bool>, which holds no reference to the gate.
    public void Track(string role, object owner)
    {
        foreach (var field in owner.GetType().GetFields(Instance))
        {
            if (!field.IsInitOnly || field.FieldType != typeof(SemaphoreSlim) ||
                field.GetValue(owner) is not SemaphoreSlim gate || GetMonitor(gate) is not { } held)
                continue;
            object monitor = held;
            var pin = GCHandle.Alloc(monitor, GCHandleType.Pinned);
            ulong address = Unsafe.As<object, nuint>(ref monitor);
            if (!_owners.TryAdd(address, new($"{role}.{field.Name}", new(owner), field, new(gate), pin)))
                pin.Free();
        }
    }

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, EventLevel.Informational, (EventKeywords)0x4000);
    }

    protected override void OnEventWritten(EventWrittenEventArgs data)
    {
        // The runtime emits both IDs before allocating the monitor event.
        if (data.EventId != 90 || data.Payload is not [nint address, nint monitor, _]) return;
        ulong managed = unchecked((ulong)monitor);
        if (_owners.ContainsKey(managed) && _lockAddresses.TryAdd(unchecked((ulong)address), managed))
            _firstObserved.TrySetResult();
    }

    public Dictionary<nint, string> ReadHandles()
    {
        Dictionary<nint, string> events = [];
        if (!Supported) return events;
        nint process = GetCurrentProcess();
        nuint size = (nuint)Marshal.SizeOf<NativeMonitorEvent>();
        foreach (var (address, managed) in _lockAddresses)
        {
            if (!_owners.TryGetValue(managed, out var owner) ||
                !owner.Owner.TryGetTarget(out object? target) || !owner.Gate.TryGetTarget(out var gate) ||
                target is ClientSession { IsReady: false } ||
                !ReferenceEquals(owner.Field.GetValue(target), gate)) continue;
            if (ReadProcessMemory(process, unchecked((nint)address) + EventOffset, out var native,
                    size, out nuint read) && read == size &&
                (native.Flags & AllocatedMonitorEvent) != 0 && native.Handle != 0 && native.Handle != -1)
                events.TryAdd(native.Handle, owner.Name);
        }
        return events;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeMonitorEvent
    {
        public readonly nint Handle;
        public readonly uint Flags;
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool ReadProcessMemory(nint process, nint address, out NativeMonitorEvent value,
        nuint size, out nuint bytesRead);

    public override void Dispose()
    {
        base.Dispose();
        foreach (var owner in _owners.Values) owner.MonitorPin.Free();
        _owners.Clear();
        _lockAddresses.Clear();
    }
}
