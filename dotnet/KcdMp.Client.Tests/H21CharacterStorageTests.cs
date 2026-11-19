// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Security.Cryptography;

namespace KcdMp.Client.Tests;

public sealed class H21CharacterStorageTests : IDisposable
{
    private const string AtSplit = "<Roots><_Barbora><Nodes><_trosecko><Nodes>"
        + "<_socky><Nodes><_questProgress value='Done'/></Nodes></_socky>"
        + "<_svatba><Nodes><_questProgress value='Active'/><_objectiveVisual1><Logs><Active/></Logs></_objectiveVisual1></Nodes></_svatba>"
        + "</Nodes></_trosecko></Nodes></_Barbora></Roots>";
    private const string ToolsBuild = "1.5.5-release_1_5";
    private static readonly string World = WhsSave.SeedTag(421);
    private static readonly string SaveA = new('a', 32), SaveB = new('b', 32);
    private readonly string _root = Directory.CreateTempSubdirectory("kct-h21c-").FullName;
    private string Snapshots => Path.Combine(_root, "henry");
    private string Catalogue => Path.Combine(_root, "catalogues");
    private string Checkpoints => Path.Combine(_root, "checkpoints");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Restart_does_not_restore_a_different_character_or_host_run()
    {
        var first = Checkpoint("first-profile", 1, "fighter", strength: 110);
        var second = Checkpoint("first-profile", 2, "scout", strength: 220);
        string profile = first.Proof.EngineProfileId;
        var catalog = Catalog(profile);
        var a = Register(catalog, first, "Gleicher Name");
        var b = Register(catalog, second, "Gleicher Name");
        Assert.NotEqual(a.CharacterId, b.CharacterId);
        Assert.NotEqual(a.StartValues.StatXp["strength"], b.StartValues.StatXp["strength"]);
        Assert.Equal(a.ProfileId, b.ProfileId);
        Assert.Equal("soldier", a.Style);
        Assert.Equal("scout", b.Style);

        var runOne = new HenryRunBinding(71, Guid.NewGuid(), World);
        var runTwo = new HenryRunBinding(71, Guid.NewGuid(), World);
        var aOne = Scope(a, runOne);
        var bOne = Scope(b, runOne);
        var aTwo = Scope(a, runTwo);
        var store = Store();
        Assert.NotNull(store.StoreScoped(aOne, Parts(first.Proof), HenryStore.SourceFresh, "{\"run-one\":true}"));
        Assert.Null(Store().PickScoped(bOne, [SaveA]));
        Assert.Null(Store().PickScoped(aTwo, [SaveA]));
        Assert.NotNull(store.StoreScoped(bOne, Parts(second.Proof), HenryStore.SourceFresh));
        Assert.NotNull(store.StoreScoped(aTwo, StartParts(310), HenryStore.SourceSnapshot, "{\"run-two\":true}"));

        var restarted = Store();
        Assert.Equal(110u, Strength(restarted.PickScoped(aOne with { WorldGeneration = 50 }, [])!));
        Assert.Equal(220u, Strength(restarted.PickScoped(bOne, [])!));
        Assert.Equal(310u, Strength(restarted.PickScoped(aTwo, [])!));
        Assert.Equal("{\"run-one\":true}", restarted.LoadChestLedger(restarted.PickScoped(aOne, [])!.Snapshot));
        Assert.Equal("{\"run-two\":true}", restarted.LoadChestLedger(restarted.PickScoped(aTwo, [])!.Snapshot));
        Assert.Null(restarted.PickScoped(Scope(b, runTwo), [SaveA]));
        Assert.Null(restarted.PickScoped(aOne with { Run = runOne with { HostKey = 72 } }, [SaveA]));
        Assert.Null(restarted.PickScoped(aOne with { CharacterRevision = 2 }, [SaveA]));
        Assert.Null(restarted.PickScoped(aOne with { ProfileId = Guid.NewGuid() }, [SaveA]));
        Assert.Empty(restarted.Worlds());
        Assert.Empty(restarted.CleanupStale());

        var restartedCatalog = Catalog(profile);
        Assert.True(restartedCatalog.TryList(out var characters));
        Assert.Equal(2, characters.Count);
        Assert.True(restartedCatalog.TryFind(a.CharacterId, out var selected));
        Assert.Equal(a.CreationId, selected.CreationId);
        Assert.Equal(a.ProfileId, selected.ProfileId);
        Assert.Equal(first.Proof.Sha256, Hash(File.ReadAllBytes(first.EngineOriginal)));
        Assert.Equal(second.Proof.Sha256, Hash(File.ReadAllBytes(second.EngineOriginal)));
    }

    [Fact]
    public void Restore_requires_a_confirmed_host_save_or_its_lineage()
    {
        var scope = TestScope();
        var store = Store();
        Assert.NotNull(store.StoreScoped(scope, StartParts(110), HenryStore.SourceFresh));
        store.Store(World, StartParts(999), SaveA, HenryStore.SourceSnapshot, "unassigned legacy pair");
        var restarted = Store();

        Assert.Null(restarted.PickScoped(scope with { HostSaveMd5 = SaveB }, []));
        Assert.Null(restarted.PickVerified(World, [SaveB]));
        Assert.Equal(110u, Strength(restarted.PickScoped(scope with { HostSaveMd5 = SaveB, WorldGeneration = 8 }, [SaveA])!));
        Assert.Equal(999u, Strength(restarted.PickVerified(World, [SaveA])!));
        Assert.Equal(999u, Strength(restarted.PickFor(World, [])!)); // old callers retain their own explicit compatibility API
        Assert.Single(restarted.Snapshots(World));
    }

    [Fact]
    public void Foreign_file_and_retired_receipt_do_not_replace_a_confirmed_pair()
    {
        var a = TestScope();
        var b = a with { CharacterId = Guid.NewGuid() };
        var store = Store();
        var snapA = Assert.IsType<HenryStore.Snapshot>(store.StoreScoped(a, StartParts(110), HenryStore.SourceSnapshot));
        var snapB = Assert.IsType<HenryStore.Snapshot>(store.StoreScoped(b, StartParts(220), HenryStore.SourceSnapshot));
        string pathA = Directory.GetFiles(Snapshots, snapA.File, SearchOption.AllDirectories).Single();
        string pathB = Directory.GetFiles(Snapshots, snapB.File, SearchOption.AllDirectories).Single();
        byte[] originalB = File.ReadAllBytes(pathB);

        int currentChecks = 0;
        Assert.Null(store.StoreScoped(a, StartParts(333), HenryStore.SourceSnapshot, stillCurrent: () => ++currentChecks == 1));
        Assert.Equal(2, Directory.GetFiles(Snapshots, "pair-*.json", SearchOption.AllDirectories).Length);
        Assert.Equal(110u, Strength(Store().PickScoped(a, [])!));
        File.Copy(pathB, pathA, overwrite: true);
        Assert.Null(Store().PickScoped(a, []));
        Assert.Null(Store().Load(snapA));
        Assert.Equal(220u, Strength(Store().PickScoped(b, [])!));
        Assert.Equal(originalB, File.ReadAllBytes(pathB));
    }

    [Fact]
    public void Rename_and_confirmed_delete_are_profile_local_and_lease_safe_after_restart()
    {
        var aStart = Checkpoint("first-profile", 1, "fighter", strength: 110);
        var bStart = Checkpoint("first-profile", 2, "scout", strength: 220);
        var foreignStart = Checkpoint("second-profile", 1, "fighter", strength: 330);
        var catalog = Catalog(aStart.Proof.EngineProfileId);
        var foreignCatalog = Catalog(foreignStart.Proof.EngineProfileId);
        var a = Register(catalog, aStart, "Gleicher Name");
        var b = Register(catalog, bStart, "Gleicher Name");
        var foreign = Register(foreignCatalog, foreignStart, "Gleicher Name");
        Assert.False(foreignCatalog.TryFind(a.CharacterId, out _));
        Assert.NotEqual(a.ProfileId, foreign.ProfileId);
        Assert.False(catalog.TryRegister(foreignStart.Store, foreignStart.Proof.CreationId, "fremd", out _, out string wrongProfile));
        Assert.Equal("checkpoint-profile-mismatch", wrongProfile);
        Assert.Equal(a.CharacterId, Register(catalog, aStart, "Erneuter Commit").CharacterId);
        Assert.True(catalog.TryRename(a.CharacterId, "Jäger Ä"));
        Assert.True(catalog.TryFind(a.CharacterId, out var renamed));
        Assert.Equal(a.Revision, renamed.Revision);
        Assert.Equal("Jäger Ä", renamed.DisplayName);
        Assert.Equal(a.CheckpointSha256, renamed.CheckpointSha256);
        Assert.True(catalog.TryNoteRun(Scope(a, new(71, Guid.NewGuid(), World))));
        Assert.True(catalog.TryNoteRun(Scope(b, new(71, Guid.NewGuid(), World))));

        var restarted = Catalog(aStart.Proof.EngineProfileId);
        Assert.False(restarted.TryDelete(aStart.Store, a.CharacterId, false, out _));
        using (var lease = Assert.IsAssignableFrom<IDisposable>(catalog.TryLease(a.CharacterId, a.Revision)))
        {
            Assert.False(restarted.TryDelete(aStart.Store, a.CharacterId, true, out string active));
            Assert.Equal("character-active-or-reserved", active);
        }
        string checkpointFile = Path.Combine(Checkpoints, a.EngineProfileId, a.CreationId.ToString("N"), "checkpoint.whs");
        byte[] original = File.ReadAllBytes(checkpointFile);
        byte[] replacement = File.ReadAllBytes(Path.Combine(Checkpoints, b.EngineProfileId, b.CreationId.ToString("N"), "checkpoint.whs"));
        File.WriteAllBytes(checkpointFile, replacement);
        Assert.False(restarted.TryDelete(aStart.Store, a.CharacterId, true, out string wrongFile));
        Assert.Equal("checkpoint-owner-proof-missing", wrongFile);
        Assert.Equal(replacement, File.ReadAllBytes(checkpointFile));
        File.WriteAllBytes(checkpointFile, original);
        Assert.True(restarted.TryDelete(aStart.Store, a.CharacterId, true, out _));

        var nextLaunch = Catalog(aStart.Proof.EngineProfileId);
        Assert.True(nextLaunch.IsDeletedCreation(a.CreationId));
        Assert.False(nextLaunch.TryFind(a.CharacterId, out _));
        Assert.True(nextLaunch.TryList(out var activeCharacters));
        Assert.Equal(b.CharacterId, Assert.Single(activeCharacters).CharacterId);
        Assert.True(nextLaunch.RunReferences().Single(r => r.CharacterId == a.CharacterId).Orphaned);
        Assert.False(nextLaunch.RunReferences().Single(r => r.CharacterId == b.CharacterId).Orphaned);
        Assert.False(nextLaunch.TryRegister(aStart.Store, a.CreationId, "Erneut", out _, out _));
        Assert.Equal(original, File.ReadAllBytes(checkpointFile));
        Assert.Equal(aStart.Proof.Sha256, Hash(File.ReadAllBytes(aStart.EngineOriginal)));
        Assert.Equal(bStart.Proof.Sha256, Hash(File.ReadAllBytes(bStart.EngineOriginal)));
        Assert.Equal(foreignStart.Proof.Sha256, Hash(File.ReadAllBytes(foreignStart.EngineOriginal)));
    }

    [Fact]
    public void Torn_catalogue_commit_is_ignored_and_unreadable_catalogue_is_not_replaced()
    {
        var one = Checkpoint("first-profile", 1, "fighter", strength: 110);
        var two = Checkpoint("first-profile", 2, "scout", strength: 220);
        var catalog = Catalog(one.Proof.EngineProfileId);
        var identity = Register(catalog, one, "Henry");
        string profile = one.Proof.EngineProfileId;
        string folder = Path.Combine(Catalogue, profile);
        string pending = Path.Combine(folder, $".catalog-{Guid.NewGuid():N}.pending");
        File.WriteAllText(pending, "interrupted write");
        Assert.True(Catalog(profile).TryFind(identity.CharacterId, out _));
        Assert.Equal("interrupted write", File.ReadAllText(pending));
        string target = Assert.Single(Directory.GetFiles(folder, "catalog-*.json"));
        File.WriteAllText(target, "file replacement");
        Assert.False(Catalog(profile).TryRegister(two.Store, two.Proof.CreationId, "Neuer Henry", out _, out _));
        Assert.False(Catalog(profile).TryList(out _));
        Assert.Equal("file replacement", File.ReadAllText(target));
    }

    [Fact]
    public void A_file_placed_at_the_delete_commit_target_is_never_overwritten()
    {
        var one = Checkpoint("first-profile", 1, "fighter", strength: 110);
        string profile = one.Proof.EngineProfileId;
        string folder = Path.Combine(Catalogue, profile);
        string next = Path.Combine(folder, "catalog-00000000000000000002.json");
        var catalog = new HenryCharacterCatalog(Catalogue, profile, now: () =>
        {
            File.WriteAllText(next, "external replacement");
            return DateTimeOffset.UtcNow;
        }, log: _ => { });
        var identity = Register(catalog, one, "Henry");
        string first = Assert.Single(Directory.GetFiles(folder, "catalog-*.json"));
        byte[] previous = File.ReadAllBytes(first);

        Assert.False(catalog.TryDelete(one.Store, identity.CharacterId, true, out string reason));
        Assert.Equal("character-catalog-unavailable", reason);
        Assert.Equal("external replacement", File.ReadAllText(next));
        Assert.Equal(previous, File.ReadAllBytes(first));
        Assert.Equal(one.Proof.Sha256, Hash(File.ReadAllBytes(one.EngineOriginal)));
        File.Delete(next);
        Assert.True(Catalog(profile).TryFind(identity.CharacterId, out _));
    }

    private HenryStore Store() => new(Snapshots, log: _ => { });
    private HenryCharacterCatalog Catalog(string engineProfileId) => new(Catalogue, engineProfileId, log: _ => { });
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static HenryCharacterScope TestScope() =>
        new(Guid.NewGuid(), Guid.NewGuid(), 1, new HenryRunBinding(71, Guid.NewGuid(), World), 1, SaveA);

    private static HenryCharacterScope Scope(HenryCharacterEntry entry, HenryRunBinding run) =>
        new(entry.ProfileId, entry.CharacterId, entry.Revision, run, 1, SaveA);

    private static WhsSave.HenryParts Parts(LocalStartCheckpoint checkpoint) =>
        WhsSave.PartsFromFile(checkpoint.SaveBytes, WhsSave.HenryParts.OriginFreshSave);

    private static WhsSave.HenryParts StartParts(uint strength) =>
        WhsSave.PartsFromFile(WhsSaveTests.File(new WhsSaveTests.Spec { Strength = strength }), WhsSave.HenryParts.OriginSnapshot);

    private static uint Strength(HenryStore.Pick pick) =>
        WhsSave.DecodePlayerSoul(pick.Parts.Record, WhsSave.RecordNode(pick.Parts.Record)).StatXp["strength"];

    private static HenryCharacterEntry Register(HenryCharacterCatalog catalog,
        (LocalStartCheckpointStore Store, LocalStartCheckpoint Proof, string EngineOriginal) checkpoint, string name)
    {
        Assert.True(catalog.TryRegister(checkpoint.Store, checkpoint.Proof.CreationId, name, out var entry, out string reason), reason);
        return Assert.IsType<HenryCharacterEntry>(entry);
    }

    private (LocalStartCheckpointStore Store, LocalStartCheckpoint Proof, string EngineOriginal) Checkpoint(
        string profile, int playline, string style, uint strength)
    {
        string engine = Path.Combine(_root, profile);
        Directory.CreateDirectory(engine);
        string engineId = LocalStartCheckpointStore.ProfileId(engine);
        var store = new LocalStartCheckpointStore(Checkpoints, engineId);
        Guid creationId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.True(store.TryReserve(engine, creationId, 101, ToolsBuild, playline, "join", out var lease, out string reason), reason);
        using (lease)
        {
            Assert.True(store.TryAuthorizeNativeStart(lease!, 101, engineId, ToolsBuild, "join", now, out reason), reason);
            string gameSave = $"playline{playline}/save001";
            string path = Path.Combine(engine, "saves", gameSave + ".whs");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            byte[] bytes = WhsSaveTests.File(new WhsSaveTests.Spec
            {
                World = AtSplit, Strength = strength, Build = ToolsBuild,
                Skills = style == "fighter" ? [(17, 1_500), (23, 900)] : [(0, 900), (4, 700), (19, 1_200)]
            });
            File.WriteAllBytes(path, bytes);
            var receipt = new LocalStartCheckpointStore.EngineSaveReceipt(creationId, 101, engineId, ToolsBuild, style, gameSave,
                Hash(bytes), now.AddSeconds(1));
            Assert.True(store.TryCapture(lease!, receipt, out var checkpoint, out reason), reason);
            return (store, Assert.IsType<LocalStartCheckpoint>(checkpoint), path);
        }
    }
}
