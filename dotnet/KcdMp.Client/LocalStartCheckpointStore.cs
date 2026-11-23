// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KcdMp.Wire;

namespace KcdMp.Client;

public sealed record LocalStartCheckpoint(Guid CreationId, string EngineProfileId, string EngineBuild, string Style,
    string Sha256, string EngineSave, DateTimeOffset CapturedAt, byte[] SaveBytes)
{
    public string? Role { get; init; }
}

/// <summary>
/// Private, immutable engine checkpoints. An empty-slot reservation and a native
/// receipt belong to one creation. The engine's save file remains read-only.
/// </summary>
public sealed class LocalStartCheckpointStore
{
    private const int Version = 1;
    private const string ManifestName = "start.json";
    private const string CheckpointName = "checkpoint.whs";
    private readonly string _profileDirectory;
    private readonly string _profileId;

    private sealed record Manifest(int Format, Guid CreationId, string EngineProfileId, string EngineBuild,
        string Style, string Sha256, string EngineSave, DateTimeOffset CapturedAt)
    {
        public string? Role { get; init; }
    }

    internal sealed record EngineSaveReceipt(Guid CreationId, int EngineProcessId, string EngineProfileId,
        string EngineBuild, string EngineStyle, string EngineSave, string Sha256, DateTimeOffset CompletedAt);

    internal sealed class Reservation : IDisposable
    {
        private readonly FileStream _lease;
        internal string EngineProfile { get; }
        internal Guid CreationId { get; }
        internal int Playline { get; }
        internal int EngineProcessId { get; }
        internal string EngineBuild { get; }
        internal string Role { get; }
        internal DateTimeOffset? DispatchedAt { get; set; }
        internal bool Active { get; private set; } = true;

        internal Reservation(FileStream lease, string engineProfile, Guid creationId, int playline, int engineProcessId,
            string engineBuild, string role)
        {
            _lease = lease;
            EngineProfile = engineProfile;
            CreationId = creationId;
            Playline = playline;
            EngineProcessId = engineProcessId;
            EngineBuild = engineBuild;
            Role = role;
        }

        public void Dispose()
        {
            lock (this)
            {
                Active = false;
                _lease.Dispose();
            }
        }
    }

    public LocalStartCheckpointStore(string root, string engineProfileId)
    {
        if (!IsFingerprint(engineProfileId)) throw new ArgumentException("Invalid engine profile ID.", nameof(engineProfileId));
        _profileId = engineProfileId;
        _profileDirectory = Path.Combine(Path.GetFullPath(root), engineProfileId);
    }

    public static string DefaultRoot() => Path.Combine(Path.GetDirectoryName(HenryStore.DefaultRoot())!, "start-checkpoints");

    public static string ProfileId(string engineUserFolder)
    {
        if (!Path.IsPathFullyQualified(engineUserFolder)) throw new ArgumentException("An exact engine user folder is required.", nameof(engineUserFolder));
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(engineUserFolder));
        if (OperatingSystem.IsWindows()) path = path.ToUpperInvariant();
        return Fingerprint(Encoding.UTF8.GetBytes(path));
    }

    internal bool TryReserve(string engineUserFolder, Guid creationId, int engineProcessId, string engineBuild, int playline,
        string role, [NotNullWhen(true)] out Reservation? reservation, out string reason)
    {
        reservation = null;
        reason = "unverified-start-scope";
        if (creationId == Guid.Empty || engineProcessId <= 0 || playline is < 0 or > 4 ||
            role is not ("host" or "join") || !IsToolsBuild(engineBuild)) return false;
        FileStream? lease = null;
        try
        {
            string profile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(engineUserFolder));
            if (ProfileId(engineUserFolder) != _profileId || !SafeDirectory(profile)) return false;
            string relative = Path.GetRelativePath(profile, _profileDirectory);
            if (!Path.IsPathFullyQualified(relative) && relative != ".." &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return false;
            if (!SafeDirectory(_profileDirectory)) Directory.CreateDirectory(_profileDirectory);
            if (!SafeDirectory(_profileDirectory)) return false;
            string saveDirectory = Path.Combine(profile, "saves", $"playline{playline}");
            string marker = Path.Combine(_profileDirectory, $"playline{playline}.lock");
            if (!SafeFile(marker, permitMissing: true)) return false;
            reason = "slot-reservation-unavailable";
            lease = new FileStream(marker, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            reason = "playline-not-empty";
            if (SafeDirectory(saveDirectory) && Directory.EnumerateFileSystemEntries(saveDirectory).Any()) return false;
            reason = "creation-already-published";
            if (Directory.Exists(CheckpointDirectory(creationId))) return false;
            reservation = new(lease, profile, creationId, playline, engineProcessId, engineBuild, role);
            lease = null;
            reason = "reserved";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
        finally { lease?.Dispose(); }
    }

    internal bool TryAuthorizeNativeStart(Reservation reservation, int engineProcessId, string engineProfileId,
        string engineBuild, string role, DateTimeOffset now, out string reason)
    {
        lock (reservation)
        {
            reason = "stale-or-mismatched-start-reservation";
            if (!reservation.Active || reservation.DispatchedAt is not null || reservation.EngineProcessId != engineProcessId ||
                ProfileId(reservation.EngineProfile) != _profileId || engineProfileId != _profileId ||
                reservation.EngineBuild != engineBuild || reservation.Role != role) return false;
            reason = "playline-no-longer-empty";
            try
            {
                string path = Path.Combine(reservation.EngineProfile, "saves", $"playline{reservation.Playline}");
                if (SafeDirectory(path) && Directory.EnumerateFileSystemEntries(path).Any()) return false;
                reservation.DispatchedAt = now;
                reason = "authorized";
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    internal bool TryCapture(Reservation reservation, EngineSaveReceipt receipt,
        [NotNullWhen(true)] out LocalStartCheckpoint? checkpoint, out string reason)
    {
        lock (reservation)
            return TryCaptureAuthorized(reservation, receipt, out checkpoint, out reason);
    }

    private bool TryCaptureAuthorized(Reservation reservation, EngineSaveReceipt receipt,
        [NotNullWhen(true)] out LocalStartCheckpoint? checkpoint, out string reason)
    {
        checkpoint = null;
        reason = "stale-or-mismatched-engine-receipt";
        if (!reservation.Active || reservation.DispatchedAt is not { } dispatched || receipt.CreationId != reservation.CreationId ||
            receipt.EngineProcessId != reservation.EngineProcessId || receipt.EngineProfileId != _profileId ||
            ProfileId(reservation.EngineProfile) != _profileId || receipt.EngineBuild != reservation.EngineBuild ||
            receipt.CompletedAt < dispatched || !IsFingerprint(receipt.Sha256) ||
            !TryParseEngineSave(receipt.EngineSave, out int playline, out string file) || playline != reservation.Playline) return false;
        string? style = receipt.EngineStyle switch { "fighter" => "soldier", "diplomat" => "adviser", "scout" => "scout", _ => null };
        if (style is null) { reason = "engine-style-unconfirmed"; return false; }
        string? stage = null;
        try
        {
            string path = Path.Combine(reservation.EngineProfile, "saves", $"playline{playline}", file);
            reason = "engine-save-unavailable";
            if (!SafeDirectory(Path.GetDirectoryName(path)!) || !SafeFile(path)) return false;
            byte[] bytes = ReadSave(path);
            reason = "engine-save-changed";
            if (Fingerprint(bytes) != receipt.Sha256) return false;
            reason = "engine-save-invalid";
            if (!WhsSave.Verify(bytes).Ok) return false;
            var data = WhsSave.Inflate(bytes);
            reason = "engine-build-mismatch";
            if (WhsSave.DescriptionSummary(data.Desc).GetValueOrDefault("BuildInfo") != receipt.EngineBuild) return false;
            reason = "cutpoint-unverified";
            if (!WhsSave.PlayerOf(data.Raw).IsHenry || WhsSave.ReadSeed(data.Raw) is null || !Wo159.CutPoint(data.Raw).Ok) return false;
            reason = "engine-style-save-mismatch";
            if (Wo159.PlaystyleOf(data.Raw).Style != style) return false;

            reason = "checkpoint-publication-failed";
            if (!SafeDirectory(_profileDirectory)) return false;
            stage = Path.Combine(_profileDirectory, $".{Guid.NewGuid():N}.pending");
            Directory.CreateDirectory(stage);
            var manifest = new Manifest(Version, receipt.CreationId, _profileId, receipt.EngineBuild, style, receipt.Sha256,
                receipt.EngineSave, receipt.CompletedAt) { Role = reservation.Role };
            WriteNew(Path.Combine(stage, CheckpointName), stream => stream.Write(bytes));
            WriteNew(Path.Combine(stage, ManifestName), stream => JsonSerializer.Serialize(stream, manifest));
            Directory.Move(stage, CheckpointDirectory(receipt.CreationId));
            stage = null;
            checkpoint = BuildCheckpoint(manifest, bytes);
            reason = "verified";
            Console.WriteLine($"MP-H21 start_checkpoint source=local creation={receipt.CreationId.ToString("N")[..8]} role={reservation.Role} style={style} cutpoint=verified");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException) { return false; }
        finally
        {
            if (stage is not null)
            {
                try
                {
                    if (SafeDirectory(stage))
                    {
                        File.Delete(Path.Combine(stage, CheckpointName));
                        File.Delete(Path.Combine(stage, ManifestName));
                        Directory.Delete(stage);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    public bool TryRead(Guid creationId, string? expectedBuild, [NotNullWhen(true)] out LocalStartCheckpoint? checkpoint, out string reason)
    {
        checkpoint = null;
        reason = "checkpoint-unavailable";
        if (creationId == Guid.Empty) return false;
        try
        {
            string folder = CheckpointDirectory(creationId);
            string savePath = Path.Combine(folder, CheckpointName);
            if (!SafeDirectory(folder) || !SafeFile(savePath)) return false;
            Manifest? m = ReadManifest(folder);
            reason = "checkpoint-scope-mismatch";
            if (m is null || m.Format != Version || m.CreationId != creationId || m.EngineProfileId != _profileId ||
                !IsToolsBuild(m.EngineBuild) || (expectedBuild is not null && m.EngineBuild != expectedBuild) ||
                m.Style is not ("soldier" or "adviser" or "scout") || m.Role is not (null or "host" or "join") || !IsFingerprint(m.Sha256) ||
                !TryParseEngineSave(m.EngineSave, out _, out _)) return false;
            byte[] bytes = ReadSave(savePath);
            reason = "checkpoint-changed";
            if (Fingerprint(bytes) != m.Sha256) return false;
            checkpoint = BuildCheckpoint(m, bytes);
            reason = "verified";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException) { return false; }
    }

    public bool TryFind(string? style, string? expectedBuild, [NotNullWhen(true)] out LocalStartCheckpoint? checkpoint, out string reason) =>
        TryFind(style, expectedBuild, null, out checkpoint, out reason);

    /// <summary>Finds an unclaimed start with unchanged original home bytes.</summary>
    internal bool TryFindFresh(string engineUserFolder, string? style, string? expectedBuild, IReadOnlySet<Guid> claimedCreations,
        [NotNullWhen(true)] out LocalStartCheckpoint? checkpoint, out string reason) =>
        TryFind(style, expectedBuild, candidate => !claimedCreations.Contains(candidate.CreationId) &&
            EngineHomeMatches(engineUserFolder, candidate), out checkpoint, out reason);

    private bool TryFind(string? style, string? expectedBuild, Predicate<LocalStartCheckpoint>? available,
        [NotNullWhen(true)] out LocalStartCheckpoint? checkpoint, out string reason)
    {
        checkpoint = null;
        reason = "no-verified-local-start-checkpoint";
        try
        {
            if (!SafeDirectory(_profileDirectory)) return false;
            foreach (string folder in Directory.EnumerateDirectories(_profileDirectory))
                if (Guid.TryParseExact(Path.GetFileName(folder), "N", out Guid id) &&
                    TryRead(id, expectedBuild, out var candidate, out _) && (style is null || candidate.Style == style) &&
                    (checkpoint is null || candidate.CapturedAt > checkpoint.CapturedAt) &&
                    (available is null || available(candidate))) checkpoint = candidate;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { checkpoint = null; }
        if (checkpoint is null) return false;
        reason = "verified";
        return true;
    }

    // Host campaign enrollment requires the exact engine file and one unambiguous creation.
    internal bool TryFindExact(string engineSave, string sha256, string build,
        [NotNullWhen(true)] out LocalStartCheckpoint? checkpoint)
    {
        checkpoint = null;
        if (!IsFingerprint(sha256) || !TryParseEngineSave(engineSave, out _, out _)) return false;
        try
        {
            if (!SafeDirectory(_profileDirectory)) return false;
            Guid? selected = null;
            foreach (string folder in Directory.EnumerateDirectories(_profileDirectory))
            {
                if (!Guid.TryParseExact(Path.GetFileName(folder), "N", out Guid id) ||
                    ReadManifest(folder) is not { } manifest || manifest.CreationId != id ||
                    manifest.EngineProfileId != _profileId || manifest.Role != "host" || manifest.EngineSave != engineSave ||
                    manifest.Sha256 != sha256 || manifest.EngineBuild != build) continue;
                if (selected is not null) return false;
                selected = id;
            }
            if (selected is not Guid creation || !TryRead(creation, build, out var exact, out _) ||
                exact.Role != "host" || exact.EngineSave != engineSave || exact.Sha256 != sha256) return false;
            checkpoint = exact;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException) { return false; }
    }

    private static Manifest? ReadManifest(string folder)
    {
        string path = Path.Combine(folder, ManifestName);
        if (!SafeDirectory(folder) || !SafeFile(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return stream.Length <= 16_384 ? JsonSerializer.Deserialize<Manifest>(stream) : null;
    }

    public static bool EngineHomeMatches(string engineUserFolder, LocalStartCheckpoint checkpoint)
    {
        try
        {
            if (ProfileId(engineUserFolder) != checkpoint.EngineProfileId ||
                !TryParseEngineSave(checkpoint.EngineSave, out int playline, out string file)) return false;
            string path = Path.Combine(engineUserFolder, "saves", $"playline{playline}", file);
            return SafeDirectory(Path.GetDirectoryName(path)!) && SafeFile(path) && Fingerprint(ReadSave(path)) == checkpoint.Sha256;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private string CheckpointDirectory(Guid creationId) => Path.Combine(_profileDirectory, creationId.ToString("N"));

    private static LocalStartCheckpoint BuildCheckpoint(Manifest manifest, byte[] bytes) =>
        new(manifest.CreationId, manifest.EngineProfileId, manifest.EngineBuild, manifest.Style, manifest.Sha256,
            manifest.EngineSave, manifest.CapturedAt, bytes) { Role = manifest.Role };

    private static bool TryParseEngineSave(string? relative, out int playline, out string file)
    {
        playline = -1;
        file = "";
        if (relative is null || relative.Length is < 10 or > 32 || !relative.StartsWith("playline", StringComparison.Ordinal) ||
            relative[8] is < '0' or > '4' || relative[9] != '/' || !relative.AsSpan(10).ToString().All(char.IsAsciiLetterOrDigit)) return false;
        playline = relative[8] - '0';
        file = relative[10..] + ".whs";
        var parsed = WorldSaved.ParsePath(Path.Combine($"playline{playline}", file));
        return parsed is not null && parsed.Value.Kind != Protocol.SaveKindExit;
    }

    internal static bool SafeDirectory(string path)
    {
        bool present = Directory.Exists(path);
        for (DirectoryInfo? directory = new(path); directory is not null; directory = directory.Parent)
        {
            try
            {
                var attrs = File.GetAttributes(directory.FullName);
                if ((attrs & FileAttributes.ReparsePoint) != 0 || (attrs & FileAttributes.Directory) == 0)
                    throw new IOException("Unsafe directory in engine profile or checkpoint store.");
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
        }
        return present;
    }

    internal static bool SafeFile(string path, bool permitMissing = false)
    {
        try { return (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return permitMissing; }
    }

    private static void WriteNew(string path, Action<FileStream> write)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        write(stream);
        stream.Flush(flushToDisk: true);
    }

    private static byte[] ReadSave(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > Protocol.WorldMaxBytes) throw new IOException("Invalid checkpoint size.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static string Fingerprint(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool IsFingerprint(string? text) => text is { Length: 64 } &&
        text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool IsToolsBuild(string? build) => build is { Length: > 0 and <= 96 } && !Wo154Rules.IsRegularGameBuild(build);
}
