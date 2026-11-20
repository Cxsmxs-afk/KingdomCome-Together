// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using KcdMp.Wire;
using Microsoft.Win32.SafeHandles;

namespace KcdMp.Client;

/// <summary>Pins the verified personal save and its containing path for the engine's delayed read.</summary>
internal sealed class HomeSaveReadLease : IDisposable
{
    private readonly List<SafeFileHandle> _directories = [];
    private FileStream? _save;

    private HomeSaveReadLease() { }

    internal static HomeSaveReadLease? TryAcquire(string savesRoot, GameBridge.HenrySource source, string fingerprint)
    {
        var lease = new HomeSaveReadLease();
        bool acquired = false;
        try
        {
            string folder = Path.Combine(Path.GetFullPath(savesRoot), $"playline{source.Playline}");
            string path = Path.Combine(folder, source.File);
            if (source.Playline is < 0 or > 4 || source.File != Path.GetFileName(source.File) ||
                !string.Equals(path, source.FullPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                !LocalStartCheckpointStore.SafeDirectory(folder) || !LocalStartCheckpointStore.SafeFile(path)) return null;

            if (OperatingSystem.IsWindows())
            {
                // A Windows file share without Delete pins the file name but not a parent
                // directory's name. Pin top down before opening the actual save.
                var ancestors = new Stack<string>();
                for (DirectoryInfo? directory = new(folder); directory?.Parent is not null; directory = directory.Parent)
                    ancestors.Push(directory.FullName);
                foreach (string directory in ancestors)
                {
                    const uint ListDirectory = 1, OpenExisting = 3, BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
                    var handle = CreateFile(directory, ListDirectory, FileShare.ReadWrite, IntPtr.Zero, OpenExisting,
                        BackupSemantics | OpenReparsePoint, IntPtr.Zero);
                    lease._directories.Add(handle);
                    if (handle.IsInvalid || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return null;
                }
            }

            // Only sharing reads permits the game's loader while Windows denies writes,
            // replacement and deletion. No data is written through this handle.
            lease._save = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!LocalStartCheckpointStore.SafeFile(path) || lease._save.Length is <= 0 or > Protocol.WorldMaxBytes ||
                !string.Equals(Convert.ToHexString(SHA256.HashData(lease._save)), fingerprint, StringComparison.OrdinalIgnoreCase)) return null;
            acquired = true;
            return lease;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
        finally { if (!acquired) lease.Dispose(); }
    }

    public void Dispose()
    {
        _save?.Dispose();
        foreach (var directory in _directories) directory.Dispose();
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security,
        uint creation, uint flags, IntPtr template);
}
