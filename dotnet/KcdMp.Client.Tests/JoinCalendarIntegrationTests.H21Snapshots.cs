// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Reflection;
using System.Threading.Channels;
using System.Xml.Linq;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public sealed partial class JoinCalendarIntegrationTests
{
    [Theory]
    [InlineData("expired")]
    [InlineData("manual")]
    [InlineData("unconfirmed")]
    [InlineData("leave")]
    public async Task Scoped_snapshot_waits_for_the_engine_save_of_its_own_attempt(string firstFile)
    {
        using var fixture = new SaveFixture();
        string original = fixture.RecordLocalCheckpoint();
        byte[] originalBytes = File.ReadAllBytes(original);
        const string build = "1.5.5-release_1_5";
        string profile = LocalStartCheckpointStore.ProfileId(Path.GetDirectoryName(fixture.GuestSaves)!);
        var checkpoints = new LocalStartCheckpointStore(LocalStartCheckpointStore.DefaultRoot(), profile);
        Assert.True(checkpoints.TryFind("soldier", build, out var checkpoint, out string reason), reason);
        var catalog = new HenryCharacterCatalog(HenryCharacterCatalog.DefaultRoot(), profile);
        Assert.True(catalog.TryRegister(checkpoints, checkpoint!.CreationId, "Henry", out var entry, out reason), reason);
        using IDisposable lease = Assert.IsAssignableFrom<IDisposable>(catalog.TryLease(entry!.CharacterId, entry.Revision));
        var home = new GameBridge.HenryChoice("fresh", WhsSave.PartsFromFile(originalBytes, WhsSave.HenryParts.OriginFreshSave), null, "fixture");
        object selected = SnapshotFixtureType("SelectedCharacter", entry, catalog, lease, home);
        const uint seed = 421;
        var run = new HenryRunBinding(15, Guid.NewGuid(), WhsSave.SeedTag(seed));
        string aHash = new('a', 32), bHash = new('b', 32);
        var scope = new HenryCharacterScope(entry.ProfileId, entry.CharacterId, entry.Revision, run, 1, aHash);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = stop.Token;
        await using var pipe = new JoinCalendarTestPipe();
        await using var engine = new JoinCalendarTestEngine(Path.Combine(fixture.Root, "guest.log"), fixture.GuestSaves, null, pipe.ObserveGameplayStarted);
        using var relay = new MemoryStream();
        var bridge = CreateBridge(1, false, relay, pipe.Pipe, engine.Transport, fixture.GuestSaves, ct);
        Assert.True(Get<WorldLoadSequence>(bridge, "_worldLoad").TryBegin(0, WorldLoadStart.AdoptLoadedWorld, hostWorld: 7));
        Set(bridge, "_joinedCharacter", SnapshotFixtureType("JoinedCharacter", selected, scope));
        Set(bridge, "_joinedWorld", true);
        Set(bridge, "_joinedTag", run.WorldTag);
        Set(bridge, "_lastWorldPlayline", 1);
        Set(bridge, "_w184Recover", false);
        string dir = Path.Combine(fixture.GuestSaves, "playline1");
        Directory.CreateDirectory(dir);
        var commands = Channel.CreateUnbounded<string>();
        engine.RequestLegacySnapshot = token => commands.Writer.TryWrite("legacy-" + token);
        pipe.RequestScopedSnapshot = marker => commands.Writer.TryWrite(marker);

        var a = new WorldSaved(1, 0, Protocol.SaveKindAuto, 0, 1, Convert.FromHexString(aHash));
        var b = new WorldSaved(2, 0, Protocol.SaveKindAuto, 0, 2, Convert.FromHexString(bHash));
        var aTask = (Task)Call(bridge, "Wo184TakeAsync", a, null!, "test A", 1L, false)!;
        string aMarker = await commands.Reader.ReadAsync(ct);
        await aTask.WaitAsync(ct);
        Assert.Null(new HenryStore(HenryStore.DefaultRoot()).PickScoped(scope, []));

        Task bTask;
        if (firstFile == "leave")
        {
            engine.Emit("wo124_where", "world");
            Set(bridge, "_leaveInProgress", true);
            Set(bridge, "_w184LastHost", b);
            Call(bridge, "LoadSequenceOnExit");
            Assert.Equal(WorldLoadPhase.Exit, Get<WorldLoadSequence>(bridge, "_worldLoad").Current.Phase);
            bTask = (Task)Call(bridge, "Wo184FinalSnapshotAsync", "test leave", false, false)!;
        }
        else bTask = (Task)Call(bridge, "Wo184TakeAsync", b, null!, "test B", 5000L, false)!;
        var command = commands.Reader.ReadAsync(ct).AsTask();
        Assert.Same(command, await Task.WhenAny(command, bTask).WaitAsync(ct));
        string bMarker = await command;
        Assert.NotEqual(aMarker, bMarker);
        string wrong = Path.Combine(dir, "quicksave001.whs"), right = Path.Combine(dir, "quicksave002.whs");
        string marker = firstFile == "expired" ? aMarker : firstFile == "unconfirmed" ? bMarker : "";
        byte[] wrongBytes = WriteSnapshot(wrong, seed, 111, marker, 1);
        if (firstFile != "unconfirmed") engine.Feed($"SaveGame: '{wrong}' done.");
        _ = Call(bridge, "Wo125OnJoinerSaveObserved", wrong, WhsSave.Verify(wrongBytes).Md5);
        Console.WriteLine($"H21-SNAPSHOT probe: {firstFile} file observed before B");
        byte[] rightBytes = WriteSnapshot(right, seed, 222, bMarker, 2);
        engine.Feed($"SaveGame: '{right}' done.");
        _ = Call(bridge, "Wo125OnJoinerSaveObserved", right, WhsSave.Verify(rightBytes).Md5);
        await bTask.WaitAsync(ct);

        var saved = Assert.IsType<HenryStore.Pick>(new HenryStore(HenryStore.DefaultRoot()).PickScoped(scope with { HostSaveMd5 = bHash }, []));
        Assert.Equal(222u, WhsSave.DecodePlayerSoul(saved.Parts.Record, WhsSave.RecordNode(saved.Parts.Record)).StatXp["strength"]);
        Assert.Equal(wrongBytes, File.ReadAllBytes(wrong));
        Assert.Equal(rightBytes, File.ReadAllBytes(right));
        Assert.Equal(originalBytes, File.ReadAllBytes(original));
    }

    private static object SnapshotFixtureType(string type, params object[] arguments) =>
        Activator.CreateInstance(typeof(GameBridge).GetNestedType(type, BindingFlags.NonPublic)!,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, arguments, null)!;

    private static byte[] WriteSnapshot(string path, uint seed, uint strength, string marker, int index)
    {
        var save = WhsSave.Inflate(WhsSaveTests.File(new WhsSaveTests.Spec { Seed = seed, Strength = strength }));
        var header = new XElement("C_SaveGameDescription", new XAttribute("BuildInfo", "1.5.5-release_1_5"),
            new XAttribute("SaveType", "QuickSave"), new XAttribute("SaveId", index),
            new XAttribute("QuestNameOverride", marker));
        byte[] bytes = WhsSave.Deflate(System.Text.Encoding.UTF8.GetBytes(header.ToString()), save.Raw, save.FooterTail);
        File.WriteAllBytes(path, bytes);
        return bytes;
    }
}
