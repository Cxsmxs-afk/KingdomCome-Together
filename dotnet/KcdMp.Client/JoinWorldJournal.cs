// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Security.Cryptography;
using System.Text.Json;

namespace KcdMp.Client;

/// <summary>Durable proof of which save copies the agent created. Startup may remove only copies whose load was never submitted.</summary>
internal sealed class JoinWorldJournal
{
    internal const string DirectoryName = ".kcdmp-join-worlds";
    private static readonly object Gate = new();
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    private sealed record Entry
    {
        public required int Version { get; init; }
        public required string WorldTag { get; init; }
        public required int Playline { get; init; }
        public required uint JoinId { get; init; }
        public required string Sha256 { get; init; }
        public required bool LoadSubmitted { get; init; }
    }

    public void Register(string savesRoot, string path, string worldTag, uint joinId, ReadOnlySpan<byte> sha256)
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

            var entry = new Entry { Version = 1, WorldTag = worldTag, Playline = playline, JoinId = joinId,
                Sha256 = Convert.ToHexString(sha256), LoadSubmitted = false };
            string file = SavePath(root, playline, joinId);
            SafeDirectory(Path.GetDirectoryName(file)!);
            if (HasPath(file, directory: false) || HasPath(Path.ChangeExtension(file, ".part"), directory: false))
                throw new IOException("the join path already exists");
            if (HasReservationIn(journal, worldTag)) throw new IOException("the host world already has a reserved join save");
            Write(MarkerPath(journal, entry), entry, overwrite: false);
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
                using var save = OpenOwned(owned.File, owned.Entry.Sha256);
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

    private static int Delete(string file, string marker, Entry entry)
    {
        string part = Path.ChangeExtension(file, ".part");
        using var finalFile = OpenOwned(file, entry.Sha256);
        using var partFile = OpenOwned(part, entry.Sha256);
        if (finalFile is not null) File.Delete(file);
        if (partFile is not null) File.Delete(part);
        File.Delete(marker); // Retain proof until all matched copies have been removed successfully.
        return (finalFile is null ? 0 : 1) + (partFile is null ? 0 : 1);
    }

    private static FileStream? OpenOwned(string file, string sha256)
    {
        if (!SafeDirectory(Path.GetDirectoryName(file)!) || !HasPath(file, directory: false)) return null;
        var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        try
        {
            if (string.Equals(Convert.ToHexString(SHA256.HashData(fs)), sha256, StringComparison.Ordinal)) return fs;
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
                entry.Version == 1 && entry.Playline is >= 0 and <= 4 && entry.JoinId != 0 && ValidTag(entry.WorldTag) &&
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
    private static string MarkerPath(string journal, Entry entry) => MarkerPath(journal, entry.Playline, entry.JoinId);
    private static string MarkerPath(string journal, int playline, uint joinId) => Path.Combine(journal, $"playline{playline}-mpworld{joinId:x8}.json");

    private void Report(string reason, string detail)
    {
        if (_reported.Add(reason)) Console.WriteLine($"MP-JOIN-SLOTS retained reason={reason}: {detail}");
    }
}
