// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using KcdMp.Wire;
using Xunit;

namespace KcdMp.Client.Tests;

public sealed class WorldRecoveryReceiverTests
{
    private static readonly WorldSyncScope Scope = new(52, 107);
    private const uint Snapshot = 4;

    private static WorldRecoveryDigest Digest(WorldBaselineSection area, ulong revision, params byte[][] rows)
    {
        Assert.True(WorldRecoveryWire.TryComputeHash(Scope, Snapshot, area, rows, out var hash));
        return new(Scope, Snapshot, area, revision, hash);
    }

    private static WorldRecoveryReceiver Install(params WorldBaselineChunk[] chunks)
    {
        var receiver = new WorldRecoveryReceiver();
        receiver.Install(chunks);
        return receiver;
    }

    private static WorldBaselineChunk Row(WorldBaselineSection area, byte[] payload, ushort index = 0, ushort count = 1) =>
        new(Scope, Snapshot, WorldSyncWire.BaselineVersion, area, index, count, payload);

    [Fact]
    public void A_confirmed_targeted_repair_does_not_mutate_the_other_confirmed_areas()
    {
        byte[] corpse = [31], item1 = [8], item2 = [36];
        var receiver = Install(Row(WorldBaselineSection.Corpse, corpse),
            Row(WorldBaselineSection.Loot, item2, 1, 2), Row(WorldBaselineSection.Loot, item1, 0, 2));
        var body = Digest(WorldBaselineSection.Corpse, 1, corpse);
        var loot = Digest(WorldBaselineSection.Loot, 1, item1, item2);
        Assert.Equal(WorldRecoveryReceiver.DigestResult.Match, receiver.Observe(loot, 0));
        Assert.True(receiver.TestCorrupt(loot.Section));
        Assert.False(receiver.TestCorrupt(body.Section));
        Assert.Equal(WorldRecoveryReceiver.DigestResult.Match, receiver.Observe(body, 1));
        Assert.Equal(WorldRecoveryReceiver.DigestResult.Mismatch, receiver.Observe(loot, 1));
        Assert.Equal(WorldRecoveryReceiver.DigestResult.Repeat, receiver.Observe(loot, 10_001));
        Assert.Equal(WorldRecoveryReceiver.PartResult.Waiting, receiver.Accept(new(loot, 1, 2, item2), out _));
        Assert.Equal(WorldRecoveryReceiver.PartResult.Waiting, receiver.Accept(new(loot, 1, 2, item2), out _));
        Assert.Equal(WorldRecoveryReceiver.PartResult.Ignore, receiver.Accept(new(body, 0, 1, corpse), out _));
        Assert.Equal(WorldRecoveryReceiver.PartResult.Ready, receiver.Accept(new(loot, 0, 2, item1), out var verified));
        Assert.All(verified!, row => Assert.Equal(WorldBaselineSection.Loot, row.Section));
        Assert.Equal(item1, verified![0].Payload);
        Assert.Equal(item2, verified[1].Payload);
        receiver.Finish(loot, confirmed: true);
        Assert.Equal(WorldRecoveryReceiver.DigestResult.Match, receiver.Observe(loot, 10_002));
        Assert.Equal(WorldRecoveryReceiver.DigestResult.Match, receiver.Observe(body, 10_002));
        Assert.Equal(WorldRecoveryReceiver.PartResult.Ignore, receiver.Accept(new(loot, 0, 2, item1), out _));
    }

    [Fact]
    public void A_fresher_absolute_image_can_only_replace_an_area_with_an_outstanding_request()
    {
        byte[] older = [1], newer = [2];
        var receiver = Install(Row(WorldBaselineSection.Weather, older));
        var first = Digest(WorldBaselineSection.Weather, 1, older);
        var second = Digest(WorldBaselineSection.Weather, 2, newer);
        var unrelated = second with { Section = WorldBaselineSection.Clock };
        Assert.Equal(WorldRecoveryReceiver.DigestResult.Match, receiver.Observe(first, 0));
        Assert.Equal(WorldRecoveryReceiver.DigestResult.Ignore, receiver.Observe(second, 1));
        Assert.True(receiver.TestCorrupt(first.Section));
        Assert.Equal(WorldRecoveryReceiver.DigestResult.Mismatch, receiver.Observe(first, 10_000));
        Assert.Equal(WorldRecoveryReceiver.DigestResult.Ignore, receiver.Observe(unrelated, 10_001));
        Assert.Equal(WorldRecoveryReceiver.DigestResult.Reanchored, receiver.Observe(second, 10_002));
        Assert.Equal(WorldRecoveryReceiver.PartResult.Ignore, receiver.Accept(new(first, 0, 1, older), out _));
        Assert.Equal(WorldRecoveryReceiver.PartResult.Ready, receiver.Accept(new(second, 0, 1, newer), out var verified));
        Assert.Equal(newer, Assert.Single(verified!).Payload);
        receiver.Finish(second, confirmed: true);
        Assert.Equal(WorldRecoveryReceiver.DigestResult.Match, receiver.Observe(second, 11_000));
    }

    [Fact]
    public void Corrupt_repair_bytes_do_not_become_a_receipt_or_end_the_other_area()
    {
        byte[] body = [98], weather = [42];
        var receiver = Install(Row(WorldBaselineSection.Body, body), Row(WorldBaselineSection.Weather, weather));
        var expected = Digest(WorldBaselineSection.Weather, 1, weather);
        Assert.True(receiver.TestCorrupt(expected.Section));
        Assert.Equal(WorldRecoveryReceiver.DigestResult.Mismatch, receiver.Observe(expected, 100));
        var wrongWorld = expected with { Scope = new(53, Scope.Generation) };
        Assert.Equal(WorldRecoveryReceiver.PartResult.Ignore, receiver.Accept(new(wrongWorld, 0, 1, weather), out _));
        Assert.Equal(WorldRecoveryReceiver.PartResult.Rejected, receiver.Accept(new(expected, 0, 1, [9]), out var verified));
        Assert.Null(verified);
        Assert.Equal(WorldRecoveryReceiver.DigestResult.Match, receiver.Observe(Digest(WorldBaselineSection.Body, 1, body), 101));
    }

    [Fact]
    public void Reanchoring_does_not_extend_the_repair_deadline()
    {
        var area = WorldBaselineSection.Clock;
        var receiver = Install(Row(area, [2]));
        var expected = Digest(area, 1, [2]);
        var replacement = Digest(area, 2, [3]);
        Assert.True(receiver.TestCorrupt(area));
        Assert.Equal(WorldRecoveryReceiver.DigestResult.Mismatch, receiver.Observe(expected, 10));
        Assert.Equal(WorldRecoveryReceiver.DigestResult.Reanchored, receiver.Observe(replacement, 30_009));
        Assert.Equal(WorldRecoveryReceiver.DigestResult.TimedOut, receiver.Observe(replacement, 30_010));
        Assert.Equal(WorldRecoveryReceiver.PartResult.Ignore, receiver.Accept(new(replacement, 0, 1, [3]), out _));
    }

    [Fact]
    public void Persistent_independent_drift_requests_repair_even_when_the_committed_image_hash_matches()
    {
        var area = WorldBaselineSection.Clock;
        var receiver = Install(Row(area, [8]));
        var expected = Digest(area, 1, [8]);
        ulong read = receiver.ReadbackRevision(area);
        receiver.NoteReadback(area, read, known: true, drift: true);
        Assert.Equal(WorldRecoveryReceiver.DigestResult.Match, receiver.Observe(expected, 10));
        receiver.NoteReadback(area, read, known: false, drift: false);
        receiver.NoteReadback(area, read, known: true, drift: true);
        Assert.Equal(WorldRecoveryReceiver.DigestResult.Match, receiver.Observe(expected, 20));
        receiver.NoteReadback(area, read, known: true, drift: true);
        Assert.Equal(WorldRecoveryReceiver.DigestResult.Mismatch, receiver.Observe(expected, 30));
        Assert.Equal(WorldRecoveryReceiver.PartResult.Ready, receiver.Accept(new(expected, 0, 1, [8]), out _));
        receiver.Finish(expected, confirmed: true);
        receiver.NoteReadback(area, read, known: true, drift: true);
        Assert.Equal(WorldRecoveryReceiver.DigestResult.Match, receiver.Observe(expected, 40));
    }

    [Theory]
    [InlineData(null, "", true, false)]
    [InlineData("1", "", true, false)]
    [InlineData(null, "--test-world-recovery", false, false)]
    [InlineData("0", "--test-world-recovery", false, false)]
    [InlineData("1", "--test-world-recovery", true, true)]
    [InlineData("1", "--test-world-recovery=loot", false, false)]
    [InlineData("1", "--test-world-recovery --test-world-recovery", false, false)]
    public void Test_corruption_needs_both_explicit_startup_switches(string? env, string switches, bool accepted, bool active)
    {
        string[] args = ["--no-voice", .. switches.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
        Assert.Equal(accepted, WorldRecoveryTestOptions.TryRead(args, env, out bool enabled, out string[] forwarded));
        Assert.Equal(active, enabled);
        if (accepted) Assert.Equal(["--no-voice"], forwarded);
    }

    [Fact]
    public void No_normal_launcher_or_release_script_enables_the_corruption_switch()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "VERSION"))) root = root.Parent;
        Assert.NotNull(root);
        string[] apps = ["KCDMP_launcher", "dotnet/KcdMp.Client", "dotnet/KcdMp.Server", "dotnet/KcdMp.MasterServer"];
        var config = apps.SelectMany(app => Directory.EnumerateFiles(Path.Combine(root.FullName, app), "*.json"));
        var launcher = Directory.EnumerateFiles(Path.Combine(root.FullName, "KCDMP_launcher"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(root.FullName, path).Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"));
        var installer = Directory.EnumerateFiles(Path.Combine(root.FullName, "installer"), "*.iss", SearchOption.AllDirectories);
        foreach (string file in config.Concat(launcher).Concat(installer)
            .Concat(new[] { "tools/Build-Installer.ps1", "tools/Publish-Release.ps1" }.Select(path => Path.Combine(root.FullName, path))))
        {
            string content = File.ReadAllText(file);
            Assert.DoesNotContain("KCDMP_TEST_WORLD_RECOVERY", content, StringComparison.Ordinal);
            Assert.DoesNotContain("--test-world-recovery", content, StringComparison.Ordinal);
        }
    }
}
