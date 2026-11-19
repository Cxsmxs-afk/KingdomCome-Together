// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KcdMp.Client;

public sealed record HenryRunBinding(uint HostKey, Guid RunId, string WorldTag);

public sealed record HenryCharacterScope(Guid ProfileId, Guid CharacterId, ulong CharacterRevision,
    HenryRunBinding Run, ulong WorldGeneration, string HostSaveMd5);

public sealed partial class HenryStore
{
    private static readonly Regex ScopedSnapshotName = new(@"^pair-(\d{17})-([0-9a-f]{32})-([0-9a-f]{32})\.json$", RegexOptions.CultureInvariant);
    private sealed record ScopedFile(int Version, HenryCharacterScope Scope, string Source, byte[] Block);

    private string ScopedDirectory(HenryCharacterScope scope) =>
        Path.Combine(Root, "characters", scope.ProfileId.ToString("N"), scope.CharacterId.ToString("N"),
            scope.Run.HostKey.ToString("x8"), scope.Run.RunId.ToString("N"), scope.Run.WorldTag);

    internal static bool ValidRun(HenryRunBinding? run) =>
        run is { HostKey: > 0 } && run.RunId != Guid.Empty && run.WorldTag is string tag && TagRx.IsMatch(tag);

    internal static bool ValidScope(HenryCharacterScope? scope) => scope is { CharacterRevision: > 0, WorldGeneration: > 0 } &&
        scope.ProfileId != Guid.Empty && scope.CharacterId != Guid.Empty && ValidRun(scope.Run) && IsSaveHash(scope.HostSaveMd5);

    private static bool IsSaveHash(string? hash) => hash is { Length: 32 } && hash.All(Uri.IsHexDigit);
    private static bool ValidScopedSource(string? source) =>
        source is SourceBring or SourceFresh or SourceSnapshot or SourceJoin or SourceRestore;

    private static string DisplayId(Guid value) => value.ToString("N")[..8];

    /// <summary>
    /// Restore only a verified save pair for this character revision and host run. A host can reload
    /// the same save in a new live generation. Persist the original generation for provenance and
    /// require the caller's live join to revalidate its own generation before using the returned parts.
    /// </summary>
    public Pick? PickScoped(HenryCharacterScope scope, IReadOnlyList<string> verifiedHostLineage)
    {
        if (!ValidScope(scope)) return null;
        string directory = ScopedDirectory(scope);
        string[] filenames;
        try
        {
            if (!LocalStartCheckpointStore.SafeDirectory(directory)) return null;
            filenames = Directory.GetFiles(directory, "pair-*.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log($"MP-HENRY character_snapshot unavailable reason={ex.GetType().Name}");
            return null;
        }
        var candidates = filenames
            .Select(path => (Path: path, Match: ScopedSnapshotName.Match(Path.GetFileName(path))))
            .Where(entry => entry.Match.Success).OrderByDescending(entry => entry.Path, StringComparer.Ordinal).ToArray();
        int position = 0;
        foreach (string hostSave in verifiedHostLineage.Prepend(scope.HostSaveMd5).Where(IsSaveHash)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var (path, match) in candidates)
            {
                if (!string.Equals(match.Groups[2].Value, hostSave, StringComparison.OrdinalIgnoreCase) ||
                    !DateTime.TryParseExact(match.Groups[1].Value, "yyyyMMddHHmmssfff", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime time)) continue;
                if (ReadScoped(path) is not { } read ||
                    read.File.Scope != (scope with { HostSaveMd5 = hostSave.ToLowerInvariant(), WorldGeneration = read.File.Scope.WorldGeneration }))
                    continue;
                var s = new Snapshot(scope.Run.WorldTag, Path.GetFileName(path), hostSave.ToLowerInvariant(), time, read.File.Source, read.Bytes)
                {
                    Scope = read.File.Scope,
                    ScopedFingerprint = read.Fingerprint
                };
                return new(s, read.Parts, position == 0 ? "paired scoped host save" : $"paired scoped host lineage save #{position}");
            }
            position++;
        }
        return null;
    }

    /// <summary>
    /// Publish an immutable pair in the selected character's run. File provenance belongs to the
    /// offered host save, while an unrelated or retired engine receipt must be fenced by the caller.
    /// </summary>
    public Snapshot? StoreScoped(HenryCharacterScope scope, WhsSave.HenryParts parts, string source,
        string? lootLedgerJson = null, Func<bool>? stillCurrent = null)
    {
        if (!ValidScope(scope)) throw new ArgumentException("The character snapshot scope is incomplete.", nameof(scope));
        if (!ValidScopedSource(source))
            throw new ArgumentException("Invalid character snapshot source.", nameof(source));
        if (stillCurrent?.Invoke() == false) return null;
        scope = scope with { HostSaveMd5 = scope.HostSaveMd5.ToLowerInvariant() };
        var storageParts = new WhsSave.HenryParts
        {
            Record = parts.Record, Side = parts.Side, KeyEntries = parts.KeyEntries, Build = parts.Build, Seed = parts.Seed,
            Origin = parts.Origin, PrivateLootLedgerJson = lootLedgerJson ?? parts.PrivateLootLedgerJson
        };
        byte[] block = WhsSave.SerializeBlock(storageParts);
        WhsSave.ParseBlock(block);
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(new ScopedFile(1, scope, source, block));
        string fingerprint = Convert.ToHexString(SHA256.HashData(data));
        DateTime utc = _now().ToUniversalTime();
        string directory = ScopedDirectory(scope);
        string name = FormattableString.Invariant($"pair-{utc:yyyyMMddHHmmssfff}-{scope.HostSaveMd5}-{Guid.NewGuid():N}.json");
        string path = Path.Combine(directory, name);
        lock (_gate)
        {
            if (!LocalStartCheckpointStore.SafeDirectory(directory)) Directory.CreateDirectory(directory);
            if (!LocalStartCheckpointStore.SafeDirectory(directory)) throw new IOException("The character scope directory is unavailable.");
            string pending = path + ".part";
            using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            if (stillCurrent?.Invoke() == false) return null; // a torn/retired record cannot be selected
            File.Move(pending, path);
            if (ReadScoped(path) is not { } read || read.Fingerprint != fingerprint)
                throw new IOException("The character snapshot did not read back as published.");
        }
        _log($"MP-HENRY character_snapshot id={DisplayId(scope.CharacterId)} run={DisplayId(scope.Run.RunId)} generation={scope.WorldGeneration} source={source}");
        return new Snapshot(scope.Run.WorldTag, name, scope.HostSaveMd5, utc, source, data.Length)
        {
            Scope = scope,
            ScopedFingerprint = fingerprint
        };
    }

    private WhsSave.HenryParts? LoadScoped(Snapshot snapshot)
    {
        var scope = snapshot.Scope!;
        if (!ValidScope(scope) || !ScopedSnapshotName.IsMatch(snapshot.File)) return null;
        var read = ReadScoped(Path.Combine(ScopedDirectory(scope), snapshot.File));
        return read is { } pair && pair.File.Scope == scope && pair.Fingerprint == snapshot.ScopedFingerprint ? pair.Parts : null;
    }

    private (ScopedFile File, WhsSave.HenryParts Parts, string Fingerprint, int Bytes)? ReadScoped(string path)
    {
        try
        {
            if (!LocalStartCheckpointStore.SafeDirectory(Path.GetDirectoryName(path)!) || !LocalStartCheckpointStore.SafeFile(path)) return null;
            byte[] bytes = File.ReadAllBytes(path);
            var file = JsonSerializer.Deserialize<ScopedFile>(bytes);
            if (file is not { Version: 1, Block: not null } || !ValidScope(file.Scope) || !ValidScopedSource(file.Source)) return null;
            return (file, WhsSave.ParseBlock(file.Block), Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            _log($"MP-HENRY character_snapshot unreadable reason={ex.GetType().Name}");
            return null;
        }
    }
}
