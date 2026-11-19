// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace KcdMp.Client;

public sealed record HenryCharacterStartValues(SortedDictionary<string, uint> StatXp, string SkillXp, int InventoryItems);

public sealed record HenryCharacterEntry(Guid ProfileId, Guid CharacterId, ulong Revision, string DisplayName, Guid CreationId,
    string EngineProfileId, string EngineBuild, string Style, string CheckpointSha256, HenryCharacterStartValues StartValues,
    DateTimeOffset? DeletedAt = null);

public sealed record HenryCharacterRunReference(Guid CharacterId, HenryRunBinding Run, bool Orphaned);

/// <summary>
/// The local profile's catalogue. A durable tombstone and its orphaned run references
/// are published in the same transaction. Engine saves and the checkpoint store are
/// independent owners and are never mutated by the catalogue.
/// </summary>
public sealed class HenryCharacterCatalog
{
    private const int Version = 1;
    private readonly string _directory;
    private readonly string _engineProfileId;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<string> _log;

    private sealed record CatalogFile(int Format, ulong Transaction, Guid ProfileId, string EngineProfileId,
        List<HenryCharacterEntry> Characters, List<HenryCharacterRunReference> Runs);

    public HenryCharacterCatalog(string modPrivateRoot, string engineProfileId, Func<DateTimeOffset>? now = null,
        Action<string>? log = null)
    {
        if (!IsFingerprint(engineProfileId)) throw new ArgumentException("Invalid engine profile ID.", nameof(engineProfileId));
        _directory = Path.Combine(Path.GetFullPath(modPrivateRoot), engineProfileId);
        _engineProfileId = engineProfileId;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _log = log ?? Console.WriteLine;
    }

    public static string DefaultRoot() => Path.Combine(Path.GetDirectoryName(HenryStore.DefaultRoot())!, "character-catalog");

    public IReadOnlyList<HenryCharacterRunReference> RunReferences() => ReadVisible()?.Runs.ToArray() ?? [];
    public bool IsDeletedCreation(Guid creationId) =>
        ReadVisible()?.Characters.Exists(c => c.CreationId == creationId && !IsActive(c)) == true;

    public bool TryList(out IReadOnlyList<HenryCharacterEntry> active)
    {
        active = [];
        try
        {
            if (Read() is { } catalog) active = catalog.Characters.Where(IsActive).ToArray();
            return true;
        }
        catch (Exception ex) when (StorageFailure(ex)) { return false; }
    }

    public bool TryFind(Guid characterId, [NotNullWhen(true)] out HenryCharacterEntry? entry)
    {
        entry = ReadVisible()?.Characters.Find(c => c.CharacterId == characterId && IsActive(c));
        return entry is not null;
    }

    public bool TryRegister(LocalStartCheckpointStore checkpoints, Guid creationId, string displayName,
        [NotNullWhen(true)] out HenryCharacterEntry? entry, out string reason)
    {
        entry = null;
        reason = "invalid-character-name";
        if (NormalizeName(displayName) is not { } name) return false;
        if (!checkpoints.TryRead(creationId, null, out var checkpoint, out reason)) return false;
        if (checkpoint.EngineProfileId != _engineProfileId)
        {
            reason = "checkpoint-profile-mismatch";
            return false;
        }

        try
        {
            var save = WhsSave.Inflate(checkpoint.SaveBytes);
            if (!WhsSave.PlayerOf(save.Raw).IsHenry || WhsSave.FindSoul(save.Raw, WhsSave.HenrySoul) is not WhsSave.Node record)
            {
                reason = "checkpoint-henry-unverified";
                return false;
            }
            var soul = WhsSave.DecodePlayerSoul(save.Raw, record);
            var startValues = new HenryCharacterStartValues(soul.StatXp, soul.Scalars.GetValueOrDefault("skill_xp") ?? "", soul.Inventory.Count);
            using var update = OpenCatalogLease();
            var catalog = Read() ?? new CatalogFile(Version, 0, Guid.NewGuid(), _engineProfileId, [], []);
            var existing = catalog.Characters.Find(c => c.CreationId == creationId);
            if (existing is not null)
            {
                reason = "creation-already-published";
                if (!IsActive(existing) || !Matches(existing, checkpoint)) return false;
                entry = existing;
                return true;
            }
            var created = new HenryCharacterEntry(catalog.ProfileId, Guid.NewGuid(), 1, name, creationId, _engineProfileId,
                checkpoint.EngineBuild, checkpoint.Style, checkpoint.Sha256, startValues);
            catalog.Characters.Add(created);
            Publish(catalog);
            entry = created;
            reason = "character-created";
            _log($"MP-HENRY character_created id={ShortId(created.CharacterId)}");
            return true;
        }
        catch (Exception ex) when (StorageFailure(ex))
        {
            reason = "character-catalog-unavailable";
            return false;
        }
    }

    /// <summary>Names are local display metadata, so a rename preserves an in-flight source revision.</summary>
    public bool TryRename(Guid characterId, string displayName)
    {
        if (NormalizeName(displayName) is not { } name) return false;
        try
        {
            using var update = OpenCatalogLease();
            var catalog = Read();
            int i = ActiveIndex(catalog, characterId);
            if (i < 0) return false;
            catalog!.Characters[i] = catalog.Characters[i] with { DisplayName = name };
            Publish(catalog);
            return true;
        }
        catch (Exception ex) when (StorageFailure(ex)) { return false; }
    }

    /// <summary>The caller owns the lease from reservation to terminal release. It also excludes deletion in a second process.</summary>
    public IDisposable? TryLease(Guid characterId, ulong expectedRevision)
    {
        try
        {
            using var update = OpenCatalogLease();
            var catalog = Read();
            int i = ActiveIndex(catalog, characterId);
            return i >= 0 && catalog!.Characters[i].Revision == expectedRevision ? OpenCharacterLease(characterId) : null;
        }
        catch (Exception ex) when (StorageFailure(ex)) { return null; }
    }

    public bool TryNoteRun(HenryCharacterScope scope)
    {
        if (!HenryStore.ValidScope(scope)) return false;
        try
        {
            using var update = OpenCatalogLease();
            var catalog = Read();
            int i = ActiveIndex(catalog, scope.CharacterId);
            if (i < 0 || catalog!.ProfileId != scope.ProfileId || catalog.Characters[i].Revision != scope.CharacterRevision) return false;
            var row = new HenryCharacterRunReference(scope.CharacterId, scope.Run, Orphaned: false);
            if (catalog.Runs.Contains(row)) return true;
            catalog.Runs.Add(row);
            Publish(catalog);
            return true;
        }
        catch (Exception ex) when (StorageFailure(ex)) { return false; }
    }

    public bool TryDelete(LocalStartCheckpointStore checkpoints, Guid characterId, bool confirmed, out string reason)
    {
        reason = "character-delete-unconfirmed";
        if (!confirmed || characterId == Guid.Empty) return false;
        reason = "character-catalog-unavailable";
        try
        {
            using var update = OpenCatalogLease();
            var catalog = Read();
            int i = ActiveIndex(catalog, characterId);
            reason = "character-not-found";
            if (i < 0) return false;
            var entry = catalog!.Characters[i];
            reason = "character-active-or-reserved";
            using var lease = OpenCharacterLease(characterId);
            reason = "checkpoint-owner-proof-missing";
            if (!checkpoints.TryRead(entry.CreationId, entry.EngineBuild, out var checkpoint, out _) || !Matches(entry, checkpoint))
                return false;
            catalog.Characters[i] = entry with { DeletedAt = _now() };
            for (int j = 0; j < catalog.Runs.Count; j++)
                if (catalog.Runs[j].CharacterId == characterId)
                    catalog.Runs[j] = catalog.Runs[j] with { Orphaned = true };
            reason = "character-catalog-unavailable";
            Publish(catalog);
            reason = "character-deleted";
            _log($"MP-HENRY character_deleted id={ShortId(characterId)}");
            return true;
        }
        catch (Exception ex) when (StorageFailure(ex)) { return false; }
    }

    private static bool IsActive(HenryCharacterEntry entry) => entry.DeletedAt is null;
    private static int ActiveIndex(CatalogFile? catalog, Guid id) =>
        catalog?.Characters.FindIndex(c => c.CharacterId == id && IsActive(c)) ?? -1;
    private static string ShortId(Guid value) => value.ToString("N")[..8];

    private static bool Matches(HenryCharacterEntry entry, LocalStartCheckpoint checkpoint) =>
        entry.CreationId == checkpoint.CreationId && entry.EngineProfileId == checkpoint.EngineProfileId &&
        entry.EngineBuild == checkpoint.EngineBuild && entry.Style == checkpoint.Style && entry.CheckpointSha256 == checkpoint.Sha256;

    private static string? NormalizeName(string? name)
    {
        string? value = name?.Trim();
        return value is { Length: >= 1 and <= 48 } && !value.Any(char.IsControl) ? value : null;
    }

    private static bool IsFingerprint(string? value) =>
        value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string CatalogName(ulong transaction) => FormattableString.Invariant($"catalog-{transaction:D20}.json");

    private static ulong CatalogTransaction(string path)
    {
        string name = Path.GetFileName(path);
        return name is { Length: 33 } && ulong.TryParse(name.AsSpan(8, 20), NumberStyles.None, CultureInfo.InvariantCulture, out ulong revision)
            ? revision : 0;
    }

    private FileStream OpenCatalogLease()
    {
        if (!LocalStartCheckpointStore.SafeDirectory(_directory)) Directory.CreateDirectory(_directory);
        if (!LocalStartCheckpointStore.SafeDirectory(_directory)) throw new IOException("Character catalogue unavailable.");
        return OpenExclusive(Path.Combine(_directory, "catalog.lock"));
    }

    private FileStream OpenCharacterLease(Guid id) => OpenExclusive(Path.Combine(_directory, id.ToString("N") + ".lease"));

    private static FileStream OpenExclusive(string path)
    {
        if (!LocalStartCheckpointStore.SafeFile(path, permitMissing: true)) throw new IOException("Unsafe character catalogue lease.");
        return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private CatalogFile? ReadVisible()
    {
        try { return Read(); }
        catch (Exception ex) when (StorageFailure(ex)) { return null; }
    }

    private CatalogFile? Read()
    {
        if (!LocalStartCheckpointStore.SafeDirectory(_directory)) return null;
        ulong latest = Directory.EnumerateFiles(_directory, "catalog-*.json").Select(CatalogTransaction).DefaultIfEmpty().Max();
        if (latest == 0) return null;
        string path = Path.Combine(_directory, CatalogName(latest));
        if (!LocalStartCheckpointStore.SafeFile(path)) throw new IOException("Unsafe character catalogue.");
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var catalog = JsonSerializer.Deserialize<CatalogFile>(reader);
        if (catalog is null || catalog.Format != Version || catalog.Transaction != latest || catalog.ProfileId == Guid.Empty ||
            catalog.EngineProfileId != _engineProfileId ||
            catalog.Characters is null || catalog.Runs is null ||
            catalog.Characters.Any(c => c is null || c.ProfileId != catalog.ProfileId || c.EngineProfileId != _engineProfileId ||
                c.CharacterId == Guid.Empty || c.CreationId == Guid.Empty || c.Revision == 0 || !IsFingerprint(c.CheckpointSha256) ||
                NormalizeName(c.DisplayName) is not { } normalized || normalized != c.DisplayName ||
                string.IsNullOrWhiteSpace(c.EngineBuild) ||
                c.Style is not ("soldier" or "adviser" or "scout") ||
                c.StartValues is not { StatXp: not null, SkillXp: not null, InventoryItems: >= 0 }) ||
            catalog.Characters.Select(c => c.CharacterId).Distinct().Count() != catalog.Characters.Count ||
            catalog.Characters.Select(c => c.CreationId).Distinct().Count() != catalog.Characters.Count ||
            catalog.Runs.Any(r => r is null || !HenryStore.ValidRun(r.Run) || !catalog.Characters.Any(c =>
                c.CharacterId == r.CharacterId && r.Orphaned == (c.DeletedAt is not null))))
            throw new InvalidDataException("The character catalogue is incompatible or invalid.");
        return catalog;
    }

    private void Publish(CatalogFile catalog)
    {
        if (catalog.Transaction == ulong.MaxValue) throw new InvalidDataException("The character catalogue journal is exhausted.");
        catalog = catalog with { Transaction = catalog.Transaction + 1 };
        string pending = Path.Combine(_directory, $".catalog-{Guid.NewGuid():N}.pending");
        using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, catalog);
            stream.Flush(flushToDisk: true);
        }
        // An occupied commit path belongs to another writer or an unknown file.
        // Leave it untouched and keep the previously published state.
        File.Move(pending, Path.Combine(_directory, CatalogName(catalog.Transaction)));
    }

    private static bool StorageFailure(Exception ex) =>
        ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException;
}
