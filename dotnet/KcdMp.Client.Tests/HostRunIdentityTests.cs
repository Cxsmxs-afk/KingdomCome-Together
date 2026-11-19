// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public sealed class HostRunIdentityTests
{
    private static HostRunSave Save(int playline, char fingerprint, string file = "save001.whs") =>
        new(new string('1', 64), playline, file, 32, "Modding Tools fixture", new string(fingerprint, 64),
            new string(fingerprint, 32));

    [Fact]
    public void Two_same_seed_runs_keep_distinct_ids_across_restart_and_reload_of_known_children()
    {
        using var files = new LedgerDirectory();
        HostRunSave a = Save(1, 'a'), b = Save(2, 'b');
        HostRunSave aContinuation = Save(1, 'c', "quicksave040.whs"), bContinuation = Save(2, 'd', "quicksave040.whs");
        Guid aCreation = Guid.NewGuid(), bCreation = Guid.NewGuid();
        var firstAgent = new HostRunLedger(files.Path);

        Assert.Null(firstAgent.Resolve(a));
        Guid aRun = Assert.IsType<Guid>(firstAgent.RegisterPrologue(aCreation, a));
        Guid bRun = Assert.IsType<Guid>(firstAgent.RegisterPrologue(bCreation, b));
        Assert.NotEqual(aRun, bRun);
        Assert.Equal(aRun, firstAgent.RecordDescendant(aRun, a, aContinuation));
        Assert.Equal(bRun, firstAgent.RecordDescendant(bRun, b, bContinuation));

        var afterRestart = new HostRunLedger(files.Path);
        Assert.Equal(aRun, afterRestart.Resolve(aContinuation));
        Assert.Equal(bRun, afterRestart.Resolve(bContinuation));
        Assert.Equal(aRun, afterRestart.Resolve(a));
        Assert.Equal(aRun, afterRestart.RegisterPrologue(aCreation, a));
        Assert.Equal(bRun, afterRestart.RegisterPrologue(bCreation, b));
        Assert.Null(afterRestart.Resolve(aContinuation with { Playline = 2 }));
        Assert.Null(afterRestart.Resolve(aContinuation with { ProfileId = new string('f', 64) }));
        Assert.Null(afterRestart.Resolve(aContinuation with { Sha256 = new string('e', 64) }));
        Assert.Null(afterRestart.RecordDescendant(aRun, a, bContinuation));
        Assert.Null(afterRestart.RecordDescendant(aRun, b, aContinuation));
        Assert.Null(afterRestart.RegisterPrologue(aCreation, b));
        Assert.Null(afterRestart.RegisterPrologue(Guid.NewGuid(), a));
    }

    [Fact]
    public void Unreadable_campaign_catalog_cannot_silently_mint_or_overwrite_a_run()
    {
        using var files = new LedgerDirectory();
        const string unreadable = "{\"Version\": 12, \"Runs\": []}";
        File.WriteAllText(files.Path, unreadable);
        var store = new HostRunLedger(files.Path);
        Assert.Null(store.RegisterPrologue(Guid.NewGuid(), Save(1, 'a')));
        Assert.Null(store.Resolve(Save(1, 'a')));
        Assert.Equal(unreadable, File.ReadAllText(files.Path));
    }

    [Fact]
    public void Host_run_offer_preserves_offer_digests_and_rejects_empty_or_unknown_scope()
    {
        var run = new WorldRunOffer(Guid.NewGuid(), 3, 17, 99);
        byte[] file = [1, 45, 99];
        byte[] md5 = Enumerable.Repeat((byte)128, 16).ToArray();
        var sender = new WorldSender(file, 7, 2, 4, md5, run: run);
        byte[] extended = sender.Offer.Encode();
        Assert.Equal(WorldOffer.RunBodyLen, extended.Length);
        WorldOffer decoded = Assert.IsType<WorldOffer>(WorldOffer.TryDecode(extended, out _));
        Assert.Equal(run, decoded.Run);
        Assert.Equal(md5, decoded.Md5);
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(file), decoded.Sha256);
        Assert.Equal(sender.Offer.WorldSavedSeq, decoded.WorldSavedSeq);

        byte[] legacy = (sender.Offer with { Run = null }).Encode();
        Assert.Equal(WorldOffer.BodyLen, legacy.Length);
        Assert.Null(Assert.IsType<WorldOffer>(WorldOffer.TryDecode(legacy, out _)).Run);
        Assert.Null(WorldOffer.TryDecode(extended.AsSpan(0, extended.Length - 1), out _));
        extended[WorldOffer.BodyLen] = 2;
        Assert.Null(WorldOffer.TryDecode(extended, out _));
        Assert.Throws<ArgumentException>(() => (sender.Offer with { Run = run with { HostGeneration = 0 } }).Encode());
    }

    private sealed class LedgerDirectory : IDisposable
    {
        private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("kcdmp-host-run-");
        public string Path => System.IO.Path.Combine(_temp.FullName, "runs.json");
        public void Dispose() => _temp.Delete(recursive: true);
    }
}
