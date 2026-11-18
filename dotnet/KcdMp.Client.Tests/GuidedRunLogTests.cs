// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Formats.Tar;
using System.Text.Json;
using KcdMp.Client;
using ZstdSharp;

namespace KcdMp.Client.Tests;

public sealed class GuidedRunLogTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("kcd-guided-test-").FullName;

    [Fact]
    public void Exports_the_captured_local_sources_and_an_Ampel_without_a_live_relay()
    {
        string game = MakeFile("game/kcd(1).log", "engine launch and gameplay\n");
        string agent = MakeFile("agent/agent.log", "agent launch and gameplay\n");
        string native = MakeFile("agent/kcdmp-native-B.log", "current B mirror\n");
        MakeFile("agent/agent.prev.log", "previous agent\n");
        MakeFile("other-install/kcd.log", "different installation\n");
        MakeFile("agent/relay.log", "unverified relay\n");
        MakeFile("game/private.whs", "never include a save\n");
        var report = Completed();
        var saved = GuidedRunLogSources.Capture(report.RunId, game, Path.GetDirectoryName(agent)!, "B");
        File.AppendAllText(agent, "trennen happened here\n");
        string directory = saved.Export(report, Path.Combine(_root, "exports"), CancellationToken.None);
        Assert.True(File.Exists(native));
        Assert.Contains(report.RunId.ToString("N"), Path.GetFileName(directory));
        var entries = ReadBundle(directory);
        Assert.Contains("engine launch", entries["kcd.log"]);
        Assert.Contains("trennen happened here", entries["agent.log"]);
        Assert.Contains("current B mirror", entries["kcdmp-native.log"]);
        Assert.DoesNotContain("agent.prev.log", entries.Keys);
        Assert.DoesNotContain("relay.log", entries.Keys);
        Assert.DoesNotContain("private.whs", entries.Keys);
        Assert.Contains("testlauf-beleg.txt", entries.Keys);
        Assert.Equal(File.ReadAllText(Path.Combine(directory, "ERGEBNIS.txt")), entries["ERGEBNIS.txt"]);
        string json = File.ReadAllText(Path.Combine(directory, "TESTLAUF.json"));
        Assert.Equal(json, entries["TESTLAUF.json"]);
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(report.RunId, parsed.RootElement.GetProperty("runId").GetGuid());
        Assert.Equal(7, parsed.RootElement.GetProperty("stations").GetArrayLength());
        Assert.False(Directory.Exists(Path.Combine(directory, "sources")));
        Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(directory)!));
    }

    [Fact]
    public void Never_substitutes_a_recreated_live_log_or_another_runs_paths()
    {
        string game = MakeFile("game/kcd.log", "owned game process\n");
        string agent = MakeFile("agent/agent.log", "captured local agent\n");
        var report = Completed();
        var saved = GuidedRunLogSources.Capture(report.RunId, game, Path.GetDirectoryName(agent)!, null);
        string target = Path.Combine(_root, "exports");
        Assert.Throws<InvalidOperationException>(() => saved.Export(report with { RunId = Guid.NewGuid() }, target, CancellationToken.None));
        Assert.False(Directory.Exists(target));
        File.Move(game, game + ".rotated");
        File.WriteAllText(game, "new, unrelated game process\n");
        string directory = saved.Export(report, target, CancellationToken.None);
        var entries = ReadBundle(directory);
        Assert.DoesNotContain("kcd.log", entries.Keys);
        Assert.Contains("captured local agent", entries["agent.log"]);
    }

    [Fact]
    public void Canceled_or_incomplete_runs_leave_no_export()
    {
        string game = MakeFile("game/kcd.log", "engine log\n");
        var report = Completed();
        var saved = GuidedRunLogSources.Capture(report.RunId, game, Path.Combine(_root, "empty-agent"), null);
        string target = Path.Combine(_root, "exports");
        Assert.Throws<OperationCanceledException>(() => saved.Export(report, target, new CancellationToken(true)));
        Assert.Throws<InvalidOperationException>(() => saved.Export(report with { Stations = [] }, target, CancellationToken.None));
        Assert.False(Directory.Exists(target));
    }

    private string MakeFile(string relative, string content)
    {
        string file = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
        return file;
    }

    private static GuidedRunReport Completed()
    {
        var start = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
        var stations = Enum.GetValues<GuidedRunStation>().Select((station, index) =>
            new GuidedRunStationResult(station, GuidedRunEvidence.ManualYes, 4, 9, start.AddSeconds(index + 1), null, null)).ToArray();
        return new(Guid.NewGuid(), "joiner", 4, 9, start, start.AddSeconds(8), stations);
    }

    private static Dictionary<string, string> ReadBundle(string directory)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in Directory.GetFiles(directory, "*.tzst"))
        {
            using var input = File.OpenRead(path);
            using var decompressed = new DecompressionStream(input);
            using var tar = new TarReader(decompressed);
            while (tar.GetNextEntry() is { } entry)
            {
                if (entry.DataStream is null) continue;
                using var read = new StreamReader(entry.DataStream, leaveOpen: true);
                entries.Add(entry.Name, read.ReadToEnd());
            }
        }
        Assert.NotEmpty(entries);
        return entries;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
