// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Reflection;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public class TestRecordingBoundaryTests
{
    private static bool EnablesRecording(string text) =>
        text.Contains("KCDMP_TEST_RECORD", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("--test-record", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Shipped_configuration_and_launcher_sources_never_enable_test_recording()
    {
        Assert.True(EnablesRecording("KCDMP_TEST_RECORD=1"));
        Assert.True(EnablesRecording("--test-record"));
        Assert.False(EnablesRecording("--host 127.0.0.1"));

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "VERSION"))) root = root.Parent;
        Assert.NotNull(root);
        string[] apps = ["KCDMP_launcher", "dotnet/KcdMp.Client", "dotnet/KcdMp.Server", "dotnet/KcdMp.MasterServer"];
        var configuration = apps.SelectMany(app => Directory.EnumerateFiles(Path.Combine(root.FullName, app), "*.json"));
        var starts = Directory.EnumerateFiles(Path.Combine(root.FullName, "KCDMP_launcher"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(root.FullName, path).Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"))
            .Concat(Directory.EnumerateFiles(Path.Combine(root.FullName, "installer"), "*.iss", SearchOption.AllDirectories))
            .Concat(new[] { "tools/Build-Installer.ps1", "tools/Publish-Release.ps1" }.Select(path => Path.Combine(root.FullName, path)));
        Assert.Empty(configuration.Concat(starts).Where(path => EnablesRecording(File.ReadAllText(path))));
    }

    [Theory]
    [InlineData(null, false, true, false)]
    [InlineData("1", false, true, false)]
    [InlineData("0", true, false, false)]
    [InlineData(null, true, false, false)]
    [InlineData("1", true, true, true)]
    public void Both_opt_ins_are_required_and_the_agent_keeps_its_positional_arguments(
        string? environment, bool flag, bool allowed, bool enabled)
    {
        string[] positional = ["127.0.0.1", "7778", "Fixture"];
        string[] args = flag ? [.. positional, "--test-record"] : positional;
        Assert.Equal(allowed, TestRecordingOptions.TryRead(args, environment, out var options, out var forwarded));
        Assert.Equal(enabled, options is not null);
        if (allowed) Assert.Equal(positional, forwarded);
        Assert.False(TestRecordingOptions.TryRead(["--test-record=fixture.whs"], "1", out _, out _));
        Assert.False(TestRecordingOptions.TryRead(["--test-record", "--test-record=fixture.kcdrec"], "1", out _, out _));
    }

    [Theory]
    [InlineData(Protocol.Handshake)]
    [InlineData(Protocol.Name)]
    [InlineData(Protocol.VoiceUp)]
    [InlineData(Protocol.WorldOfferDown)]
    [InlineData(Protocol.WorldChunkUp)]
    [InlineData(Protocol.WorldChunkDown)]
    [InlineData(Protocol.WorldSavedUp)]
    public void No_record_of_name_handshake_voice_or_save_transfer(byte type)
        => Assert.False(TestRecordingOptions.AllowsPacket([type, 1, 0, 0]));

    [Fact]
    public void Only_allowlisted_join_payloads_and_confirmed_envelopes_are_recorded()
    {
        var profile = new LootMsg(Protocol.W164Profile, 1, "local profile").BuildUp(Protocol.W164Up, 2);
        var step = new RangedStepV2(1, 1, 1, 1, 3, RangedWeaponKind.Bow,
            Guid.Parse("10000000-0000-0000-0000-000000000001"), 0, 1);
        var shot = new LootMsg(Protocol.W164RangedStep, 1, step.Encode()).BuildUp(Protocol.W164Up, 2);
        Assert.False(TestRecordingOptions.AllowsPacket(profile));
        Assert.True(TestRecordingOptions.AllowsPacket(shot));
        Assert.True(TestRecordingOptions.AllowsPacket(PresentationTimeWire.StampCombatUp(shot, 77)));
        Assert.False(TestRecordingOptions.AllowsPacket(shot[..^1]));
        Assert.False(TestRecordingOptions.AllowsLuaEffect("KCD2MP_Wo124LoadGame(1, \"private.whs\", \"reload\")"));
        Assert.True(TestRecordingOptions.AllowsLuaEffect("if KCD2MP_ApplyWeather then KCD2MP_ApplyWeather(\"clear\",2) end"));
    }

    [Fact]
    public async Task The_agent_records_the_completed_transport_write_and_ignores_unapproved_frames()
    {
        string path = Path.Combine(Path.GetTempPath(), $"kcd-test-{Guid.NewGuid():N}.kcdrec");
        try
        {
            byte[] state = [Protocol.PlayerStateUp, Protocol.PlayerStateUpPayloadLen, 0, .. new byte[Protocol.PlayerStateUpPayloadLen]];
            byte[] excluded = [Protocol.VoiceUp, 1, 0, 0];
            using (var recorder = SessionRecorder.Create(path))
            {
                var bridge = new GameBridge(new ClientConfig());
                bridge.RecordSession(recorder);
                var write = typeof(GameBridge).GetMethod("WritePacketAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                using var open = new MemoryStream();
                await (Task)write.Invoke(bridge, [open, state, CancellationToken.None])!;
                await (Task)write.Invoke(bridge, [open, excluded, CancellationToken.None])!;
                using var closed = new MemoryStream();
                closed.Close();
                await Assert.ThrowsAsync<ObjectDisposedException>(() => (Task)write.Invoke(bridge, [closed, state, CancellationToken.None])!);
                byte[] expected = [.. state, .. excluded];
                Assert.Equal(expected, open.ToArray());
            }
            using var file = File.OpenRead(path);
            var entry = Assert.Single(SessionRecording.Read(file));
            Assert.Equal(SessionRecordKind.RelaySent, entry.Kind);
            Assert.Equal(state, entry.Payload);
        }
        finally { File.Delete(path); }
    }
}
