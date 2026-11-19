// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
namespace KcdMp.Client;

using System.Text.Json;

internal sealed record HostRunSave(string ProfileId, int Playline, string FileName, uint Seed, string Build,
    string Sha256, string Md5)
{
    public bool IsValid => Hex(ProfileId, 64) && Playline is >= 0 and <= 4 &&
        FileName is { Length: >= 5 and <= 40 } && FileName.EndsWith(".whs", StringComparison.Ordinal) &&
        FileName.AsSpan(0, FileName.Length - 4).ToString().All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c)) &&
        Build is { Length: > 0 and <= 96 } && Hex(Sha256, 64) && Hex(Md5, 32);

    public bool SameCampaignPlace(HostRunSave other) => ProfileId == other.ProfileId &&
        Playline == other.Playline && Seed == other.Seed;

    internal static bool Hex(string? value, int length) => value is not null && value.Length == length &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>Durable campaign assignments built from a proven prologue and explicit engine save lineage.</summary>
internal sealed class HostRunLedger(string path)
{
    private const int Version = 1;
    private const long MaximumBytes = 4 * 1024 * 1024;
    private sealed record Run(Guid CreationId, Guid RunId, List<HostRunSave> Saves);
    private sealed record Catalog(int Version, List<Run> Runs);

    /// <summary>Only a successfully observed creation in a positively reserved playline calls this.</summary>
    public Guid? RegisterPrologue(Guid creationId, HostRunSave start)
    {
        if (creationId == Guid.Empty || !start.IsValid) return null;
        return Update(runs =>
        {
            if (runs.Find(r => r.CreationId == creationId) is { } prior)
                return (prior.Saves[0] == start ? prior.RunId : null, false);
            if (runs.Exists(r => r.Saves.Contains(start))) return (null, false);
            Guid runId = Guid.NewGuid();
            runs.Add(new(creationId, runId, [start]));
            return (runId, true);
        });
    }

    public Guid? Resolve(HostRunSave save) => !save.IsValid ? null :
        Update(runs => (runs.Find(r => r.Saves.Contains(save))?.RunId, false));

    /// <summary>Only an exact engine write in the world loaded from this known parent can extend its run.</summary>
    public Guid? RecordDescendant(Guid expectedRun, HostRunSave parent, HostRunSave save)
    {
        if (expectedRun == Guid.Empty || !parent.IsValid || !save.IsValid || !parent.SameCampaignPlace(save)) return null;
        return Update(runs =>
        {
            if (runs.Find(r => r.RunId == expectedRun) is not { } run || !run.Saves.Contains(parent)) return (null, false);
            if (runs.Find(r => r.Saves.Contains(save)) is { } prior)
                return (prior.RunId == expectedRun ? expectedRun : null, false);
            run.Saves.Add(save);
            return (expectedRun, true);
        });
    }

    private Guid? Update(Func<List<Run>, (Guid? RunId, bool Changed)> update)
    {
        string? stage = null;
        try
        {
            string full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using var lease = new FileStream(full + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            List<Run> runs = Read(full);
            var (runId, changed) = update(runs);
            if (!changed) return runId;
            stage = full + $".{Guid.NewGuid():N}.part";
            using (var stream = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new Catalog(Version, runs));
                if (stream.Length > MaximumBytes) return null;
                stream.Flush(flushToDisk: true);
            }
            File.Move(stage, full, overwrite: true);
            stage = null;
            return runId;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException) { return null; }
        finally
        {
            if (stage is not null)
            {
                try { File.Delete(stage); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static List<Run> Read(string full)
    {
        FileStream stream;
        try { stream = new(full, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { return []; }
        using (stream)
        {
            if (stream.Length > MaximumBytes || JsonSerializer.Deserialize<Catalog>(stream) is not { Version: Version, Runs: { } runs })
                throw new InvalidDataException("invalid host run ledger");
            var ids = new HashSet<Guid>();
            var creations = new HashSet<Guid>();
            var saves = new HashSet<HostRunSave>();
            foreach (var run in runs)
            {
                if (run is null || run.RunId == Guid.Empty || !ids.Add(run.RunId) || run.CreationId == Guid.Empty ||
                    !creations.Add(run.CreationId) || run.Saves is not { Count: > 0 } || run.Saves[0] is not { } root ||
                    run.Saves.Any(save => save is null || !save.IsValid || !root.SameCampaignPlace(save) || !saves.Add(save)))
                    throw new InvalidDataException("ambiguous host run ledger");
            }
            return runs;
        }
    }
}
