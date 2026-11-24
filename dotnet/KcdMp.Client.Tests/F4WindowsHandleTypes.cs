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

    public static string Capture()
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
            if (status < 0) return $"status={unchecked((uint)status):x8}";

            int size = Marshal.SizeOf<Entry>();
            int header = 2 * IntPtr.Size;
            long count = Marshal.ReadIntPtr(entries).ToInt64();
            if (count < 0 || count > (capacity - header) / size) return "bad_count=1";

            var groups = new Dictionary<uint, List<IntPtr>>();
            for (int i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<Entry>(entries + header + size * i);
                if (!groups.TryGetValue(entry.Type, out var handles)) groups.Add(entry.Type, handles = []);
                handles.Add(entry.Handle);
            }

            var types = groups.Select(group => $"{ReadName(process, group, typeBuffer)}={group.Value.Count}");
            return $"count={count} types={string.Join(',', types.Order(StringComparer.Ordinal))}";
        }
        finally
        {
            Marshal.FreeHGlobal(typeBuffer);
            Marshal.FreeHGlobal(entries);
        }
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

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int kind, IntPtr buffer, int length, out int needed);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryObject(SafeFileHandle handle, int kind, IntPtr buffer, int length, out int needed);
}
