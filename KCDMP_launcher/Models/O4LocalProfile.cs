// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace KCDMP_launcher.Models;

/// <summary>The optional dedicated test profile. No engine configuration or save is written here.</summary>
public sealed class O4LocalProfile : IDisposable
{
    public const string EnvironmentName = "KCDMP_O4_PROFILE";
    private const string FolderPrefix = "KingdomCome2_KCT_O4_";
    private static readonly StringComparison ComparePath = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly Regex Setting = new(@"^\s*sys_user_folder\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Value = new(@"^(?:""(?<name>[^""]+)""|(?<name>[A-Za-z0-9_]+))$", RegexOptions.CultureInvariant);
    private readonly FileStream _lease;

    private O4LocalProfile(string folder, FileStream lease)
    {
        Folder = folder;
        _lease = lease;
    }

    public string Folder { get; }

    public static bool ConfiguresDedicatedFolder(string gameRoot)
    {
        try { return ConfiguredFolder(gameRoot) is { } folder && IsDedicatedName(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    public static bool TryReserve(string gameRoot, string requestedFolder, string regularFolder, string privateRoot,
        out O4LocalProfile? profile, out string reason)
    {
        profile = null;
        reason = "profile-not-explicit";
        FileStream? lease = null;
        try
        {
            if (!Path.IsPathFullyQualified(requestedFolder) || !Path.IsPathFullyQualified(regularFolder)) return false;
            string expected = Normalize(requestedFolder), regular = Normalize(regularFolder);
            string? parent = Path.GetDirectoryName(regular);
            string name = Path.GetFileName(expected);
            if (parent is null || !string.Equals(Path.GetDirectoryName(expected), parent, ComparePath) ||
                string.Equals(expected, regular, ComparePath) || !IsDedicatedName(name)) return false;
            if (!string.Equals(ConfiguredFolder(gameRoot), name, StringComparison.OrdinalIgnoreCase)) return false;
            RequireOrdinaryPath(Path.Combine(gameRoot, "kcd.log"), requireDirectory: false);
            RequireOrdinaryPath(expected);
            RequireOrdinaryPath(regular);
            string root = Normalize(privateRoot);
            if (IsWithin(root, expected) || IsWithin(root, regular)) return false;
            RequireOrdinaryPath(root);
            Directory.CreateDirectory(root);
            RequireOrdinaryPath(root);

            reason = "profile-lease-unavailable";
            string key = OperatingSystem.IsWindows() ? expected.ToUpperInvariant() : expected;
            string marker = Path.Combine(root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".lock");
            RequireOrdinaryPath(marker, requireDirectory: false);
            lease = new FileStream(marker, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            reason = "profile-has-existing-save-data";
            if (!EmptySaves(expected)) return false;
            profile = new(expected, lease);
            lease = null;
            reason = "reserved";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
        finally { lease?.Dispose(); }
    }

    public static string? ReadEngineFolder(string line)
    {
        const string prefix = "User folder is '";
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) return null;
        int end = line.IndexOf('\'', prefix.Length);
        return end > prefix.Length ? line[prefix.Length..end] : null;
    }

    public bool MatchesEngineIdentity(string? engineFolder)
    {
        if (engineFolder is null || !Path.IsPathFullyQualified(engineFolder)) return false;
        try
        {
            string actual = Normalize(engineFolder);
            RequireOrdinaryPath(actual);
            return string.Equals(actual, Folder, ComparePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    public bool HasEmptySaves()
    {
        try { return Directory.Exists(Folder) && EmptySaves(Folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    public void Dispose() => _lease.Dispose();

    private static string? ConfiguredFolder(string gameRoot)
    {
        string config = Path.Combine(gameRoot, "system.cfg");
        RequireOrdinaryPath(config, requireDirectory: false);
        string? name = null;
        if (!File.Exists(config)) return null;
        foreach (string line in File.ReadLines(config))
        {
            var prefix = Setting.Match(line);
            if (!prefix.Success) continue;
            var value = Value.Match(line[prefix.Length..].Trim());
            name = value.Success ? value.Groups["name"].Value : null;
        }
        return name;
    }

    private static bool IsDedicatedName(string folder) =>
        folder.StartsWith(FolderPrefix, StringComparison.OrdinalIgnoreCase) &&
        folder.Length - FolderPrefix.Length is >= 8 and <= 32 &&
        folder.AsSpan(FolderPrefix.Length).ToString().All(char.IsAsciiHexDigit);

    private static bool EmptySaves(string profile)
    {
        string saves = Path.Combine(profile, "saves");
        RequireOrdinaryPath(saves);
        if (!Directory.Exists(saves)) return true;
        foreach (string item in Directory.EnumerateFileSystemEntries(saves))
        {
            if (!Directory.Exists(item)) return false;
            RequireOrdinaryPath(item);
            if (Directory.EnumerateFileSystemEntries(item).Any()) return false;
        }
        return true;
    }

    private static bool IsWithin(string child, string root)
    {
        string relative = Path.GetRelativePath(root, child);
        return !Path.IsPathFullyQualified(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static void RequireOrdinaryPath(string path, bool requireDirectory = true)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                FileAttributes attrs = File.GetAttributes(current);
                bool expectedDirectory = current != path || requireDirectory;
                if ((attrs & FileAttributes.ReparsePoint) != 0 || ((attrs & FileAttributes.Directory) != 0) != expectedDirectory)
                    throw new IOException("Unsafe path in isolated profile preflight.");
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
        }
    }
}
