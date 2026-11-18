// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Text.Json;
using System.Text.Json.Serialization;
using KcdMp.LogPack;

namespace KcdMp.Client;

public partial class GameBridge
{
    private GuidedRunLogSources? _guidedRunLogSources;

    private void CaptureGuidedRunSources(Guid runId)
    {
        // Keep the exact adopted tail. Another install's log is not a
        // substitute when the relay drops or this game's log rotates.
        if (_transport is LogTailGameTransport tail)
            Volatile.Write(ref _guidedRunLogSources, GuidedRunLogSources.Capture(
                runId, tail.LogPath, AppContext.BaseDirectory, BenchInstance.Id));
    }

    private Task<string> CollectGuidedRunAsync(GuidedRunReport report, CancellationToken ct)
    {
        if (Volatile.Read(ref _guidedRunLogSources) is not { } captured || captured.RunId != report.RunId)
            throw new InvalidOperationException("The guided run has no captured local log sources.");
        string userData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(userData))
            throw new IOException("The local application data directory is unavailable.");
        string output = Path.Combine(userData, "KCDMP", "Testlauf");
        return Task.Run(() => captured.Export(report, output, ct), ct);
    }
}

internal sealed class GuidedRunLogSources
{
    private sealed record LogSource(string Name, string Path, DateTime CreatedUtc, long InitialLength, byte[] Prefix);
    private readonly IReadOnlyList<LogSource> _logs;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(namingPolicy: JsonNamingPolicy.CamelCase) }
    };

    internal Guid RunId { get; }

    private GuidedRunLogSources(Guid runId, IReadOnlyList<LogSource> logs) => (RunId, _logs) = (runId, logs);

    internal static GuidedRunLogSources Capture(Guid runId, string gameLog, string agentDirectory, string? instance)
    {
        string gameDirectory = Path.GetDirectoryName(Path.GetFullPath(gameLog))!;
        var game = Probe("kcd.log", gameLog);
        var agent = Probe("agent.log", Path.Combine(agentDirectory, "agent.log"));
        DateTime earliestNative = game?.CreatedUtc ?? agent?.CreatedUtc ?? DateTime.UtcNow;
        string nativeName = BenchInstance.Valid(instance) is { } id ? $"kcdmp-native-{id}.log" : "kcdmp-native.log";
        LogSource? native = Probe("kcdmp-native.log", Path.Combine(agentDirectory, nativeName), earliestNative);
        LogSource? mirror = Probe("kcdmp-native.mirror.log", Path.Combine(gameDirectory, "kcdmp-native.mirror.log"), earliestNative);
        return new(runId, new[] { game, agent, native, mirror }.OfType<LogSource>().ToArray());
    }

    private static LogSource? Probe(string name, string path, DateTime? earliestWrite = null)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || earliestWrite is { } earliest && info.LastWriteTimeUtc < earliest) return null;
            using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = stream.Length;
            byte[] prefix = new byte[Math.Min(512, length)];
            stream.ReadExactly(prefix);
            return new(name, info.FullName, info.CreationTimeUtc, length, prefix);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal string Export(GuidedRunReport report, string outputRoot, CancellationToken ct)
    {
        if (report.RunId == Guid.Empty || report.RunId != RunId || report.Role is not ("host" or "joiner") ||
            report.StartWorld == 0 || report.StartGeneration == 0 || report.CompletedAtUtc < report.StartedAtUtc ||
            report.Stations.Count != 7 || report.Stations.Select(s => s.Station).Distinct().Count() != 7 ||
            report.Stations.Any(s => !Enum.IsDefined(s.Station) || !Enum.IsDefined(s.Evidence)))
            throw new InvalidOperationException("The guided run report is incomplete or belongs to another local run.");

        ct.ThrowIfCancellationRequested();
        string name = FormattableString.Invariant($"testlauf-{report.StartedAtUtc.UtcDateTime:yyyyMMdd-HHmmss}-{report.Role}-{RunId:N}");
        string final = Path.Combine(outputRoot, name);
        string pending = Path.Combine(outputRoot, $".{name}-{Guid.NewGuid():N}.pending");
        Directory.CreateDirectory(pending);
        try
        {
            string scratch = Directory.CreateDirectory(Path.Combine(pending, "sources")).FullName;
            var inputs = new List<SourceFile>();
            foreach (var log in _logs)
            {
                ct.ThrowIfCancellationRequested();
                if (TrySnapshot(log, scratch, ct)) inputs.Add(new(log.Name, Path.Combine(scratch, log.Name)));
            }
            if (inputs.Count == 0) throw new IOException("The captured logs are no longer readable.");

            byte[] json = JsonSerializer.SerializeToUtf8Bytes(new
            {
                report.RunId, report.Role, report.StartWorld, report.StartGeneration,
                report.StartedAtUtc, report.CompletedAtUtc, report.Stations,
                logs = inputs.Select(input => input.EntryName).ToArray(),
            }, Json);
            inputs.Add(new("TESTLAUF.json", Bytes: json));
            inputs.Add(GuidedRunVerdictEvidence.Create(report.RunId, report.Role,
                report.Stations.Count(s => s.Evidence == GuidedRunEvidence.Observed),
                report.Stations.Count(s => s.Evidence == GuidedRunEvidence.ManualYes),
                report.Stations.Count(s => s.Evidence == GuidedRunEvidence.ManualNo)));
            var packed = Packer.Pack(inputs, pending, "bundle", progress: _ => ct.ThrowIfCancellationRequested());
            if (packed.Parts.Count == 0 || string.IsNullOrWhiteSpace(packed.Verdict))
                throw new IOException("LogPack did not produce a bundle and an Ampel result.");
            File.WriteAllBytes(Path.Combine(pending, "TESTLAUF.json"), json);
            File.WriteAllText(Path.Combine(pending, "ERGEBNIS.txt"), packed.Verdict);
            Directory.Delete(scratch, recursive: true);
            ct.ThrowIfCancellationRequested();
            Directory.Move(pending, final);
            return final;
        }
        finally
        {
            if (Directory.Exists(pending))
                try { Directory.Delete(pending, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
        }
    }

    private static bool TrySnapshot(LogSource log, string directory, CancellationToken ct)
    {
        try
        {
            using var source = new FileStream(log.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = source.Length;
            if (length == 0 || length < log.InitialLength) return false;
            // On Windows this is the actual file birth time. Some Unix file
            // systems expose modification time through this API instead.
            if (OperatingSystem.IsWindows() && File.GetCreationTimeUtc(log.Path) != log.CreatedUtc) return false;
            byte[] currentPrefix = new byte[log.Prefix.Length];
            source.ReadExactly(currentPrefix);
            if (!currentPrefix.AsSpan().SequenceEqual(log.Prefix)) return false;
            source.Position = 0;
            using var snapshot = new FileStream(Path.Combine(directory, log.Name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            byte[] buffer = new byte[64 * 1024];
            long remaining = length;
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                int size = (int)Math.Min(remaining, buffer.Length);
                source.ReadExactly(buffer.AsSpan(0, size));
                snapshot.Write(buffer, 0, size);
                remaining -= size;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
