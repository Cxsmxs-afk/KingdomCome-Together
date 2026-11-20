// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KcdMp.Client;

/// <summary>Stable filesystem identity of an open handle, independent of the name used to open it.</summary>
internal readonly record struct FileIdentity(ulong Volume, ulong Low, ulong High, long BirthSeconds, uint BirthNanoseconds)
{
    internal static bool TryRead(SafeFileHandle handle, out FileIdentity identity)
    {
        const int FileIdInfo = 18, AtEmptyPath = 0x1000;
        const uint StatxInode = 0x100, StatxBirthTime = 0x800;
        if (OperatingSystem.IsWindows() && GetFileInformationByHandleEx(handle, FileIdInfo, out var id, 24))
        {
            identity = new(id.Volume, id.Low, id.High, 0, 0);
            return true;
        }
        if (OperatingSystem.IsLinux() &&
            Statx(handle.DangerousGetHandle().ToInt32(), "", AtEmptyPath, StatxInode | StatxBirthTime, out var info) == 0 &&
            (info.Mask & StatxInode) != 0)
        {
            ulong volume = ((ulong)info.DeviceMajor << 32) | info.DeviceMinor;
            identity = (info.Mask & StatxBirthTime) != 0
                ? new(volume, info.Inode, 0, info.BirthSeconds, info.BirthNanoseconds)
                : new(volume, info.Inode, 0, 0, 0);
            return true;
        }
        identity = default;
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileId
    {
        public ulong Volume, Low, High;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out WindowsFileId id, uint bytes);

    // Linux's statx ABI carries its device/inode and optional birth time independently of libc's stat layout.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxFileId
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(80)] public long BirthSeconds;
        [FieldOffset(88)] public uint BirthNanoseconds;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out LinuxFileId info);
}
