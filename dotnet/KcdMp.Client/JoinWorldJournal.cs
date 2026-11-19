// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace KcdMp.Client;

/// <summary>Durable proof of which save copies the agent created. Startup may remove only copies whose load was never submitted.</summary>
internal sealed class JoinWorldJournal(Action<string>? beforeRetire = null)
{
    internal const string DirectoryName = ".kcdmp-join-worlds";
    private static readonly object Gate = new();
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly record struct FileIdentity(ulong Volume, ulong Low, ulong High, long BirthSeconds, uint BirthNanoseconds);

    private sealed record Entry
    {
        public required int Version { get; init; }
        public required string WorldTag { get; init; }
        public required int Playline { get; init; }
        public required uint JoinId { get; init; }
        public required string Sha256 { get; init; }
        public required bool LoadSubmitted { get; init; }
        public Guid? StageId { get; init; }
        public bool? StageReady { get; init; }
        public FileIdentity? Identity { get; init; }
    }

    public string Register(string savesRoot, string path, string worldTag, uint joinId, ReadOnlySpan<byte> sha256)
    {
        lock (Gate)
        {
            string root = Path.GetFullPath(savesRoot);
            int playline = FindPlayline(root, path, joinId);
            if (!ValidTag(worldTag) || sha256.Length != 32) throw new IOException("invalid join world identity");
            if (!SafeDirectory(root)) throw new IOException("join saves root does not exist");
            string journal = Path.Combine(root, DirectoryName);
            if (!SafeDirectory(journal)) Directory.CreateDirectory(journal);
            if (!SafeDirectory(journal)) throw new IOException("join journal directory does not exist");

            var entry = new Entry { Version = 2, WorldTag = worldTag, Playline = playline, JoinId = joinId,
                Sha256 = Convert.ToHexString(sha256), LoadSubmitted = false, StageId = Guid.NewGuid(), StageReady = false };
            string file = SavePath(root, playline, joinId);
            SafeDirectory(Path.GetDirectoryName(file)!);
            if (HasPath(file, directory: false) || HasPath(Path.ChangeExtension(file, ".part"), directory: false))
                throw new IOException("the join path already exists");
            if (HasReservationIn(journal, worldTag)) throw new IOException("the host world already has a reserved join save");
            string marker = MarkerPath(journal, entry);
            string stage = StagePath(marker, entry);
            if (HasPath(stage, directory: false)) throw new IOException("the private stage already exists");
            Write(marker, entry, overwrite: false);
            return stage;
        }
    }

    public void SealStage(string savesRoot, string path, uint joinId, ReadOnlySpan<byte> sha256)
    {
        lock (Gate)
        {
            if (FindRegistered(savesRoot, path, joinId, sha256) is not { } owned ||
                owned.Entry.StageReady != false)
                throw new IOException("join save has no unpublished reservation");
            string staged = StagePath(owned.Marker, owned.Entry);
            using var source = OpenOwned(staged, owned.Entry.Sha256);
            if (source is null) throw new IOException("join save stage is missing");
            var identity = ReadIdentity(source.SafeFileHandle);

            // StageReady is durable before the atomic move. If the stage is still
            // present after restart, the visible name was never published by it.
            Write(owned.Marker, owned.Entry with { StageReady = true, Identity = identity }, overwrite: true);
        }
    }

    public void Publish(string savesRoot, string path, uint joinId, ReadOnlySpan<byte> sha256)
    {
        lock (Gate)
        {
            if (FindRegistered(savesRoot, path, joinId, sha256) is not { } owned ||
                owned.Entry.StageReady != true || owned.Entry.LoadSubmitted)
                throw new IOException("join save has no sealed stage");
            MoveNoReplace(StagePath(owned.Marker, owned.Entry), owned.File);
        }
    }

    public bool HasReservation(string savesRoot, string worldTag)
    {
        lock (Gate)
        {
            try
            {
                string journal = Path.Combine(Path.GetFullPath(savesRoot), DirectoryName);
                return SafeDirectory(journal) && HasReservationIn(journal, worldTag);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Report("reservation-unreadable", ex.Message);
                return true;
            }
        }
    }

    private bool HasReservationIn(string journal, string worldTag) =>
        Directory.EnumerateFiles(journal, "*.json").Any(marker =>
            Read(marker) is { } entry && SameWorld(entry.WorldTag, worldTag));

    private static bool SameWorld(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return true;
        // A host without a known install key reserves the plain 10-digit seed tag.
        // Distinct known hosts use seed-tag + '-' + host-tag and can coexist.
        return (left.Length, right.Length) switch
        {
            (10, 21) when right[10] == '-' => left.AsSpan().Equals(right.AsSpan(0, 10), StringComparison.OrdinalIgnoreCase),
            (21, 10) when left[10] == '-' => right.AsSpan().Equals(left.AsSpan(0, 10), StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    public bool MarkLoadSubmitted(string savesRoot, string path, uint joinId, ReadOnlySpan<byte> sha256)
    {
        lock (Gate)
        {
            try
            {
                if (FindRegistered(savesRoot, path, joinId, sha256) is not { } owned) return false;
                if (owned.Entry.Version == 2 && (owned.Entry.StageReady != true ||
                    HasPath(StagePath(owned.Marker, owned.Entry), directory: false))) return false;
                if (HasPath(RetiredPath(owned.Marker, owned.Entry), directory: false)) return false;
                using var save = OpenOwned(owned.File, owned.Entry.Sha256, owned.Entry.Identity);
                if (save is null) { Report("load-file-missing", SaveName(owned.Entry)); return false; }
                if (!owned.Entry.LoadSubmitted) Write(owned.Marker, owned.Entry with { LoadSubmitted = true }, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Report("load-mark-refused", ex.Message);
                return false;
            }
        }
    }

    /// <summary>The caller already proved no engine request can still be reading this copy.</summary>
    public bool TryDeleteOwned(string savesRoot, string path, uint joinId, ReadOnlySpan<byte> sha256)
    {
        lock (Gate)
        {
            try
            {
                if (FindRegistered(savesRoot, path, joinId, sha256) is not { } owned) return false;
                Delete(owned.File, owned.Marker, owned.Entry);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Report("delete-refused", ex.Message);
                return false;
            }
        }
    }

    public int Sweep(string savesRoot, Func<string, bool> protects)
    {
        lock (Gate)
        {
            int removed = 0;
            try
            {
                string root = Path.GetFullPath(savesRoot);
                string journal = Path.Combine(root, DirectoryName);
                if (!SafeDirectory(journal)) return 0;
                foreach (string marker in Directory.EnumerateFiles(journal, "*.json"))
                {
                    if (Read(marker) is not { } entry) continue;
                    string file = SavePath(root, entry.Playline, entry.JoinId);
                    if (entry.LoadSubmitted || protects(file)) continue;
                    try { removed += Delete(file, marker, entry); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { Report("sweep-refused", ex.Message); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Report("journal-unreadable", ex.Message); }
            return removed;
        }
    }

    internal bool CanSweepPath(string path, bool directory)
    {
        try
        {
            return directory ? SafeDirectory(path)
                : SafeDirectory(Path.GetDirectoryName(path)!) && HasPath(path, directory: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (Gate) Report("unsafe-sweep-path", Path.GetFileName(path));
            return false;
        }
    }

    private (string File, string Marker, Entry Entry)? FindRegistered(string savesRoot, string path, uint joinId, ReadOnlySpan<byte> sha256)
    {
        string root = Path.GetFullPath(savesRoot);
        int playline = FindPlayline(root, path, joinId);
        string file = SavePath(root, playline, joinId);
        string journal = Path.Combine(root, DirectoryName);
        string marker = MarkerPath(journal, playline, joinId);
        if (SafeDirectory(journal) && Read(marker) is { } entry &&
            string.Equals(entry.Sha256, Convert.ToHexString(sha256), StringComparison.Ordinal)) return (file, marker, entry);
        Report("ownership-unconfirmed", $"playline{playline}/mpworld{joinId:x8}.whs");
        return null;
    }

    private int Delete(string file, string marker, Entry entry)
    {
        int removed = 0;
        if (entry.Version == 2)
        {
            string stage = StagePath(marker, entry);
            if (HasPath(stage, directory: false))
            {
                if (entry.LoadSubmitted) throw new IOException("submitted join save unexpectedly has a stage");
                // Retire publication intent before removing the stage, so a
                // crash here cannot make a foreign visible file look published.
                if (entry.StageReady == true) Write(marker, entry with { StageReady = false, Identity = null }, overwrite: true);
                File.Delete(stage);
                removed++;
            }
            else if (entry.StageReady == true)
                removed += RetireChecked(file, RetiredPath(marker, entry), entry.Sha256, entry.Identity);
        }
        else
        {
            // Old deployments wrote into a publicly named .part. A torn or
            // changed legacy file cannot prove ownership and is kept in place.
            removed += RetireChecked(file, marker + ".retired", entry.Sha256);
            removed += RetireChecked(Path.ChangeExtension(file, ".part"), marker + ".part.retired", entry.Sha256);
        }
        File.Delete(marker); // Retain proof until all matched copies have been removed successfully.
        return removed;
    }

    private int RetireChecked(string file, string quarantine, string sha256, FileIdentity? identity = null)
    {
        if (!SafeDirectory(Path.GetDirectoryName(file)!))
            throw new IOException($"the playline for {Path.GetFileName(file)} is missing");
        bool alreadyCaptured = HasPath(quarantine, directory: false);
        if (alreadyCaptured && !identity.HasValue)
            throw new IOException($"legacy file kept at {Path.GetFileName(quarantine)}; no captured identity was recorded");

        using var visible = alreadyCaptured ? null : OpenOwned(file, sha256, identity);
        if (!alreadyCaptured)
        {
            if (visible is null) return 0;
            identity ??= ReadIdentity(visible.SafeFileHandle);
            beforeRetire?.Invoke(file);
            MoveNoReplace(file, quarantine);
        }

        // Verify what the atomic move actually captured. The public path is
        // never unlinked after being inspected by name.
        try
        {
            using var captured = OpenOwned(quarantine, sha256, identity);
            if (captured is null) throw new IOException("captured join save is missing");
        }
        catch (IOException)
        {
            try { MoveNoReplace(quarantine, file); }
            catch (IOException ex)
            {
                throw new IOException($"foreign file kept in the join journal at {Path.GetFileName(quarantine)}; could not restore {Path.GetFileName(file)}", ex);
            }
            throw new IOException($"{Path.GetFileName(file)} changed during retirement; replacement restored");
        }

        File.Delete(quarantine);
        return 1;
    }

    private static FileStream? OpenOwned(string file, string sha256, FileIdentity? identity = null)
    {
        if (!SafeDirectory(Path.GetDirectoryName(file)!) || !HasPath(file, directory: false)) return null;
        var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        try
        {
            if ((!identity.HasValue || ReadIdentity(fs.SafeFileHandle) == identity.Value) &&
                string.Equals(Convert.ToHexString(SHA256.HashData(fs)), sha256, StringComparison.Ordinal)) return fs;
            throw new IOException($"{Path.GetFileName(file)} changed since publication");
        }
        catch { fs.Dispose(); throw; }
    }

    private Entry? Read(string marker)
    {
        try
        {
            if (!HasPath(marker, directory: false)) return null;
            using var stream = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length <= 4096 && JsonSerializer.Deserialize<Entry>(stream) is { } entry &&
                (entry.Version == 1 && entry.StageId is null && entry.StageReady is null && entry.Identity is null ||
                 entry.Version == 2 && entry.StageId is Guid stageId && stageId != Guid.Empty && entry.StageReady.HasValue &&
                    entry.StageReady == entry.Identity.HasValue && (!entry.LoadSubmitted || entry.StageReady == true)) &&
                entry.Playline is >= 0 and <= 4 && entry.JoinId != 0 && ValidTag(entry.WorldTag) &&
                entry.Sha256 is { Length: 64 } && entry.Sha256.All(char.IsAsciiHexDigitUpper) &&
                string.Equals(marker, MarkerPath(Path.GetDirectoryName(marker)!, entry), PathComparison)) return entry;
            Report("invalid-ownership-marker", Path.GetFileName(marker));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { Report("unreadable-ownership-marker", $"{Path.GetFileName(marker)} ({ex.GetType().Name})"); }
        return null;
    }

    private static void Write(string marker, Entry entry, bool overwrite)
    {
        string pending = marker + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, entry);
                stream.Flush(flushToDisk: true);
            }
            File.Move(pending, marker, overwrite);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }

    private static int FindPlayline(string root, string path, uint joinId)
    {
        string full = Path.GetFullPath(path);
        for (int pl = 0; joinId != 0 && pl <= 4; pl++)
            if (string.Equals(full, SavePath(root, pl, joinId), PathComparison)) return pl;
        throw new IOException("join path is outside the five playlines or does not match its join ID");
    }

    private static bool SafeDirectory(string path)
    {
        bool present = false;
        for (DirectoryInfo? dir = new(path); dir is not null; dir = dir.Parent)
        {
            bool exists = HasPath(dir.FullName, directory: true);
            if (string.Equals(dir.FullName, path, PathComparison)) present = exists;
        }
        return present;
    }

    private static bool HasPath(string path, bool directory)
    {
        FileAttributes attrs;
        try { attrs = File.GetAttributes(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
        if ((attrs & FileAttributes.ReparsePoint) != 0 || ((attrs & FileAttributes.Directory) != 0) != directory)
            throw new IOException($"unsafe path {Path.GetFileName(path)}");
        return true;
    }

    private static bool ValidTag(string? tag) => tag is { Length: > 0 and <= 96 } &&
        tag.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static string SaveName(Entry entry) => $"playline{entry.Playline}/mpworld{entry.JoinId:x8}.whs";
    private static string SavePath(string root, int playline, uint joinId) => Path.Combine(root, $"playline{playline}", $"mpworld{joinId:x8}.whs");
    private static string StagePath(string marker, Entry entry) => Path.Combine(Path.GetDirectoryName(marker)!, $"{entry.StageId:N}.partial");
    private static string RetiredPath(string marker, Entry entry) => entry.StageId.HasValue
        ? Path.Combine(Path.GetDirectoryName(marker)!, $"{entry.StageId:N}.retired") : marker + ".retired";
    private static string MarkerPath(string journal, Entry entry) => MarkerPath(journal, entry.Playline, entry.JoinId);
    private static string MarkerPath(string journal, int playline, uint joinId) => Path.Combine(journal, $"playline{playline}-mpworld{joinId:x8}.json");

    private void Report(string reason, string detail)
    {
        if (_reported.Add(reason)) Console.WriteLine($"MP-JOIN-SLOTS retained reason={reason}: {detail}");
    }

    private static FileIdentity ReadIdentity(SafeFileHandle handle)
    {
        const int FileIdInfo = 18, AtEmptyPath = 0x1000;
        const uint StatxInode = 0x100, StatxBirthTime = 0x800;
        try
        {
            if (OperatingSystem.IsWindows() && GetFileInformationByHandleEx(handle, FileIdInfo, out var id, 24))
                return new(id.Volume, id.Low, id.High, 0, 0);
            if (OperatingSystem.IsLinux() &&
                Statx(handle.DangerousGetHandle().ToInt32(), "", AtEmptyPath, StatxInode | StatxBirthTime, out var info) == 0 &&
                (info.Mask & StatxInode) != 0)
            {
                ulong volume = ((ulong)info.DeviceMajor << 32) | info.DeviceMinor;
                return (info.Mask & StatxBirthTime) != 0
                    ? new(volume, info.Inode, 0, info.BirthSeconds, info.BirthNanoseconds)
                    : new(volume, info.Inode, 0, 0, 0);
            }
        }
        catch (EntryPointNotFoundException ex)
        { throw new IOException("the filesystem does not expose stable join file identity", ex); }
        throw new IOException("could not verify the join file's identity");
    }

    // .NET File.Move may fall back to copying or linking before deleting the
    // source. A transaction needs one atomic, non-replacing rename or a refusal.
    private static void MoveNoReplace(string source, string target)
    {
        const int AtCurrentDirectory = -100;
        const uint RenameNoReplace = 1;
        bool moved;
        try
        {
            if (OperatingSystem.IsWindows())
                moved = MoveFileEx(WindowsPath(source), WindowsPath(target), 0);
            else if (OperatingSystem.IsLinux())
                moved = RenameAt2(AtCurrentDirectory, source, AtCurrentDirectory, target, RenameNoReplace) == 0;
            else throw new IOException("atomic join file moves are unavailable on this platform");
        }
        catch (EntryPointNotFoundException ex)
        { throw new IOException("the filesystem does not expose an atomic non-replacing join file move", ex); }
        if (!moved) throw new IOException($"atomic join file move refused for {Path.GetFileName(source)}",
            new Win32Exception(Marshal.GetLastPInvokeError()));
    }

    private static string WindowsPath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
        : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string source, string target, uint flags);

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameAt2(int sourceDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        int targetDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string target, uint flags);

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
