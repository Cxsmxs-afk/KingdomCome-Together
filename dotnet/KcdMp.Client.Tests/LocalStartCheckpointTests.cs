// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Security.Cryptography;

namespace KcdMp.Client.Tests;

public sealed class LocalStartCheckpointTests : IDisposable
{
    private const string Build = "1.5.5-release_1_5";
    private const string CutPoint = "<Roots><_Barbora><Nodes><_trosecko><Nodes>"
        + "<_socky><Nodes><_questProgress value='Done'/></Nodes></_socky>"
        + "<_svatba><Nodes><_questProgress value='Active'/><_objectiveVisual1><Logs><Active/></Logs></_objectiveVisual1></Nodes></_svatba>"
        + "</Nodes></_trosecko></Nodes></_Barbora></Roots>";
    private const string EngineSave = "playline2/autosave002";
    private const int EngineProcess = 4321;
    private static readonly DateTimeOffset DispatchTime = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "kct-start-checkpoint-" + Guid.NewGuid().ToString("N"));
    private readonly string _engineFolder;
    private readonly string _checkpointRoot;
    private readonly string _profileId;
    private readonly LocalStartCheckpointStore _store;

    public LocalStartCheckpointTests()
    {
        _engineFolder = Path.Combine(_sandbox, "engine-profile");
        _checkpointRoot = Path.Combine(_sandbox, "private-store");
        Directory.CreateDirectory(_engineFolder);
        _profileId = LocalStartCheckpointStore.ProfileId(_engineFolder);
        _store = new(_checkpointRoot, _profileId);
    }

    [Theory]
    [InlineData("host", "fighter")]
    [InlineData("join", "fighter")]
    public void Receipt_matched_to_the_dispatched_normal_prologue_publishes_an_immutable_local_copy(string role, string engineStyle)
    {
        using var reservation = Reserve(role);
        string ownSave = Path.Combine(_engineFolder, "saves", "playline0", "save001.whs");
        Directory.CreateDirectory(Path.GetDirectoryName(ownSave)!);
        File.WriteAllBytes(ownSave, [5, 4, 2]);
        Assert.True(_store.TryAuthorizeNativeStart(reservation, EngineProcess, _profileId, Build, role, DispatchTime, out var why), why);
        var bytes = MakeSave();
        WriteEngineSave(bytes);
        var receipt = Receipt(reservation) with { EngineStyle = engineStyle, Sha256 = Fingerprint(bytes) };
        Assert.True(_store.TryCapture(reservation, receipt, out var published, out why), why);
        Assert.False(_store.TryCapture(reservation, receipt, out _, out _));

        var restarted = new LocalStartCheckpointStore(_checkpointRoot, _profileId);
        Assert.True(restarted.TryRead(reservation.CreationId, Build, out var afterRestart, out why), why);
        Assert.Equal(EngineSave, afterRestart!.EngineSave);
        Assert.Equal("soldier", afterRestart.Style);
        Assert.Equal(published!.Sha256, afterRestart.Sha256);
        Assert.Equal(bytes, afterRestart.SaveBytes);
        Assert.Equal(role, afterRestart.Role);
        Assert.Equal(role == "host", restarted.TryFindExact(EngineSave, published.Sha256, Build, out _));
        Assert.True(restarted.TryFind("soldier", Build, out var found, out _));
        Assert.Equal(afterRestart.CreationId, found!.CreationId);
        Assert.True(LocalStartCheckpointStore.EngineHomeMatches(_engineFolder, found));
        Assert.Equal(new byte[] { 5, 4, 2 }, File.ReadAllBytes(ownSave));
        Assert.Equal(bytes, File.ReadAllBytes(EnginePath));
    }

    [Fact]
    public void A_reserved_slot_is_exclusive_and_the_dispatch_rechecks_role_profile_and_slot()
    {
        using var reservation = Reserve("host");
        Assert.False(_store.TryReserve(_engineFolder, Guid.NewGuid(), EngineProcess + 1, Build, 2, "join", out _, out _));
        Assert.False(_store.TryAuthorizeNativeStart(reservation, EngineProcess, _profileId, Build, "join", DispatchTime, out _));
        Assert.False(_store.TryAuthorizeNativeStart(reservation, EngineProcess + 1, _profileId, Build, "host", DispatchTime, out _));
        Assert.False(_store.TryAuthorizeNativeStart(reservation, EngineProcess, new string('f', 64), Build, "host", DispatchTime, out _));

        WriteEngineSave([7, 1, 2]);
        Assert.False(_store.TryAuthorizeNativeStart(reservation, EngineProcess, _profileId, Build, "host", DispatchTime, out var why));
        Assert.Equal("playline-no-longer-empty", why);
        Assert.Equal(new byte[] { 7, 1, 2 }, File.ReadAllBytes(EnginePath));
    }

    [Fact]
    public void Full_slots_and_exit_before_checkpoint_preserve_all_files()
    {
        for (int slot = 0; slot < 5; slot++)
        {
            string folder = Path.Combine(_engineFolder, "saves", $"playline{slot}");
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "save001.whs"), [(byte)slot]);
            Assert.False(_store.TryReserve(_engineFolder, Guid.NewGuid(), EngineProcess, Build, slot, "host", out _, out var reason));
            Assert.Equal("playline-not-empty", reason);
        }
        for (int slot = 0; slot < 5; slot++)
            Assert.Equal(new byte[] { (byte)slot }, File.ReadAllBytes(Path.Combine(_engineFolder, "saves", $"playline{slot}", "save001.whs")));

        string ownedFixture = Path.Combine(_engineFolder, "saves", "playline2", "save001.whs");
        File.Delete(ownedFixture);
        var reservation = Reserve("join");
        Assert.True(_store.TryAuthorizeNativeStart(reservation, EngineProcess, _profileId, Build, "join", DispatchTime, out _));
        reservation.Dispose();
        var next = new LocalStartCheckpointStore(_checkpointRoot, _profileId);
        Assert.False(next.TryFind(null, Build, out _, out _));
        Assert.False(_store.TryCapture(reservation, Receipt(reservation), out _, out _));
        using var recovered = Reserve("join");
    }

    [Theory]
    [InlineData("no-dispatch")]
    [InlineData("foreign-process")]
    [InlineData("foreign-profile")]
    [InlineData("old-receipt")]
    [InlineData("wrong-slot")]
    [InlineData("changed-content")]
    [InlineData("wrong-build")]
    [InlineData("unconfirmed-style")]
    [InlineData("wrong-style")]
    [InlineData("early")]
    [InlineData("late")]
    public void Unconfirmed_or_changed_engine_receipts_cannot_publish(string failure)
    {
        using var reservation = Reserve("join");
        if (failure != "no-dispatch")
            Assert.True(_store.TryAuthorizeNativeStart(reservation, EngineProcess, _profileId, Build, "join", DispatchTime, out _));
        string cutPoint = failure switch
        {
            "early" => CutPoint.Replace("value='Done'", "value='Active'"),
            "late" => CutPoint.Replace("<Active/></Logs>", "<Active/><Done/></Logs>"),
            _ => CutPoint
        };
        byte[] bytes = MakeSave(cutPoint, failure == "wrong-build" ? "1.6.0-release_1_6" : Build);
        WriteEngineSave(bytes);
        var receipt = Receipt(reservation) with { Sha256 = Fingerprint(bytes) };
        receipt = failure switch
        {
            "foreign-process" => receipt with { EngineProcessId = EngineProcess + 1 },
            "foreign-profile" => receipt with { EngineProfileId = new string('b', 64) },
            "old-receipt" => receipt with { CompletedAt = DispatchTime.AddMilliseconds(-1) },
            "wrong-slot" => receipt with { EngineSave = "playline0/autosave002" },
            "changed-content" => receipt with { Sha256 = new string('a', 64) },
            "unconfirmed-style" => receipt with { EngineStyle = "soldier" },
            "wrong-style" => receipt with { EngineStyle = "scout" },
            _ => receipt
        };
        Assert.False(_store.TryCapture(reservation, receipt, out _, out _));
        Assert.False(_store.TryRead(reservation.CreationId, Build, out _, out _));
        Assert.Equal(bytes, File.ReadAllBytes(EnginePath));
    }

    [Fact]
    public void A_frozen_checkpoint_and_its_engine_home_have_separate_identity_checks()
    {
        using var reservation = Reserve("join");
        Assert.True(_store.TryAuthorizeNativeStart(reservation, EngineProcess, _profileId, Build, "join", DispatchTime, out _));
        byte[] bytes = MakeSave();
        WriteEngineSave(bytes);
        Assert.True(_store.TryCapture(reservation, Receipt(reservation) with { Sha256 = Fingerprint(bytes) }, out var checkpoint, out _));
        Assert.False(_store.TryRead(reservation.CreationId, "1.6.0-release_1_6", out _, out _));
        var other = new LocalStartCheckpointStore(_checkpointRoot, LocalStartCheckpointStore.ProfileId(Path.Combine(_sandbox, "other")));
        Assert.False(other.TryRead(reservation.CreationId, Build, out _, out _));

        File.WriteAllBytes(EnginePath, [7]);
        Assert.False(LocalStartCheckpointStore.EngineHomeMatches(_engineFolder, checkpoint!));
        Assert.True(_store.TryRead(reservation.CreationId, Build, out var privateCopy, out _));
        Assert.Equal(bytes, privateCopy!.SaveBytes);
        File.WriteAllBytes(Path.Combine(_checkpointRoot, _profileId, reservation.CreationId.ToString("N"), "checkpoint.whs"), [8]);
        Assert.False(_store.TryRead(reservation.CreationId, Build, out _, out _));
    }

    private string EnginePath => Path.Combine(_engineFolder, "saves", "playline2", "autosave002.whs");

    private void WriteEngineSave(byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(EnginePath)!);
        File.WriteAllBytes(EnginePath, bytes);
    }

    private LocalStartCheckpointStore.Reservation Reserve(string role)
    {
        Assert.True(_store.TryReserve(_engineFolder, Guid.NewGuid(), EngineProcess, Build, 2, role, out var reservation, out var why), why);
        return reservation!;
    }

    private LocalStartCheckpointStore.EngineSaveReceipt Receipt(LocalStartCheckpointStore.Reservation reservation) =>
        new(reservation.CreationId, EngineProcess, _profileId, Build, "fighter", EngineSave, new string('a', 64), DispatchTime.AddSeconds(5));

    private static byte[] MakeSave(string cutPoint = CutPoint, string build = Build) =>
        WhsSaveTests.File(new WhsSaveTests.Spec { World = cutPoint, Build = build, Seed = 122,
            Skills = [(17, 1500), (23, 900), (24, 600), (19, 10)] });

    private static string Fingerprint(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public void Dispose() => Directory.Delete(_sandbox, recursive: true);
}
