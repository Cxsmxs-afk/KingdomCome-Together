// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KcdMp.Client.Tests;

internal static class F4WindowsHandleTypes
{
    private const int ProcessHandleInformation = 51, ObjectTypeInformation = 2, DuplicateSameAccess = 2;
    private const uint InfoLengthMismatch = 0xc0000004;

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Entry
    {
        public readonly IntPtr Handle;
        public readonly UIntPtr HandleCount, PointerCount;
        public readonly uint Access, Type, Attributes, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct UnicodeString
    {
        public readonly ushort Length, MaximumLength;
        public readonly IntPtr Text;
    }

    internal readonly record struct Inventory(string? Details, int? FileHandles = null);

    public static Inventory Capture(Dictionary<IntPtr, string>? handlesByType = null)
    {
        IntPtr process = GetCurrentProcess();
        int capacity = 64 * 1024;
        IntPtr entries = Marshal.AllocHGlobal(capacity);
        IntPtr typeBuffer = Marshal.AllocHGlobal(1024);
        try
        {
            int status = NtQueryInformationProcess(process, ProcessHandleInformation, entries, capacity, out int needed);
            if (unchecked((uint)status) == InfoLengthMismatch && needed > capacity)
            {
                capacity = needed;
                entries = Marshal.ReAllocHGlobal(entries, (IntPtr)capacity);
                status = NtQueryInformationProcess(process, ProcessHandleInformation, entries, capacity, out needed);
            }
            if (status < 0) return new($"status={unchecked((uint)status):x8}");

            int size = Marshal.SizeOf<Entry>();
            int header = 2 * IntPtr.Size;
            long count = Marshal.ReadIntPtr(entries).ToInt64();
            if (count < 0 || count > (capacity - header) / size) return new("bad_count=1");

            var groups = new Dictionary<uint, List<IntPtr>>();
            for (int i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<Entry>(entries + header + size * i);
                if (!groups.TryGetValue(entry.Type, out var handles)) groups.Add(entry.Type, handles = []);
                handles.Add(entry.Handle);
            }

            var types = new List<string>();
            string threads = "";
            int? files = null;
            foreach (var group in groups)
            {
                string name = ReadName(process, group, typeBuffer);
                types.Add($"{name}={group.Value.Count}");
                if (name == "Thread") threads = ThreadCounts(process, group.Value);
                if (name == "File") files = group.Value.Count;
                if (handlesByType is not null && (name is "Event" or "Thread"))
                    foreach (var handle in group.Value) handlesByType.Add(handle, name);
            }
            return new($"count={count} types={string.Join(',', types.Order(StringComparer.Ordinal))}{threads}", files);
        }
        finally
        {
            Marshal.FreeHGlobal(typeBuffer);
            Marshal.FreeHGlobal(entries);
        }
    }

    private static string ThreadCounts(IntPtr process, IEnumerable<IntPtr> handles)
    {
        var targets = new HashSet<uint>();
        int live = 0, dead = 0, foreign = 0, unknown = 0;
        foreach (var value in handles)
        {
            if (!DuplicateHandle(process, value, process, out var duplicate, 0, false, DuplicateSameAccess))
            {
                unknown++;
                continue;
            }
            using (duplicate)
            {
                uint owner = GetProcessIdOfThread(duplicate);
                uint id = GetThreadId(duplicate);
                if (owner == 0 || id == 0 || !GetExitCodeThread(duplicate, out uint exitCode)) unknown++;
                else if (owner != Environment.ProcessId) foreign++;
                else if (exitCode != 259) dead++; // STILL_ACTIVE
                else { live++; targets.Add(id); }
            }
        }
        return $" thread_live={live} thread_live_distinct={targets.Count} thread_dead={dead} thread_foreign={foreign} thread_unknown={unknown}";
    }

    private static string ReadName(IntPtr process, KeyValuePair<uint, List<IntPtr>> group, IntPtr buffer)
    {
        foreach (var value in group.Value)
        {
            if (!DuplicateHandle(process, value, process, out var duplicate, 0, false, DuplicateSameAccess))
                continue;
            using (duplicate)
            {
                if (NtQueryObject(duplicate, ObjectTypeInformation, buffer, 1024, out _) < 0) continue;
                var name = Marshal.PtrToStructure<UnicodeString>(buffer);
                var text = Marshal.PtrToStringUni(name.Text, name.Length / sizeof(char));
                if (!string.IsNullOrEmpty(text)) return text.Replace(' ', '_');
            }
        }
        return $"Unknown_{group.Key}";
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess,
        out SafeFileHandle target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    [DllImport("kernel32.dll")]
    private static extern uint GetProcessIdOfThread(SafeFileHandle thread);

    [DllImport("kernel32.dll")]
    private static extern uint GetThreadId(SafeFileHandle thread);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeThread(SafeFileHandle thread, out uint exitCode);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int kind, IntPtr buffer, int length, out int needed);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryObject(SafeFileHandle handle, int kind, IntPtr buffer, int length, out int needed);
}
