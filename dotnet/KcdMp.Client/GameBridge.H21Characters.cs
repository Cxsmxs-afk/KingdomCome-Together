// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using KcdMp.Wire;

namespace KcdMp.Client;

public partial class GameBridge
{
    private sealed class SelectedCharacter(HenryCharacterEntry entry, HenryCharacterCatalog catalog, IDisposable lease, HenryChoice home)
    {
        public HenryCharacterEntry Entry { get; } = entry;
        public HenryCharacterCatalog Catalog { get; } = catalog;
        public HenryChoice Home { get; } = home;
        private readonly object _leaseGate = new();
        private int _engineReads;
        private bool _releaseRequested;

        public bool RetainForEngine(JoinerJoin join, JoinEngineReceipt receipt)
        {
            lock (_leaseGate)
            {
                if (_releaseRequested) return false;
                _engineReads++;
            }
            _ = ReleaseAfterEngineAsync(join, receipt);
            return true;
        }

        public void Release()
        {
            lock (_leaseGate)
            {
                if (_releaseRequested) return;
                _releaseRequested = true;
                if (_engineReads != 0) return;
            }
            lease.Dispose();
        }

        private async Task ReleaseAfterEngineAsync(JoinerJoin join, JoinEngineReceipt receipt)
        {
            // Use the same handoff and terminal proof as the retained world file.
            // An abort, a closed relay, or a timed-out HTTP request is not that proof.
            await join.DispatchEnded.Task;
            try { await receipt.Terminal.Task; }
            catch (OperationCanceledException) when (receipt.Terminal.Task.IsCanceled) { }   // the observed game quit ends the read
            lock (_leaseGate)
            {
                _engineReads--;
                if (!_releaseRequested || _engineReads != 0) return;
            }
            lease.Dispose();
        }
    }
    private sealed record JoinedCharacter(SelectedCharacter Selection, HenryCharacterScope Scope);
    private JoinedCharacter? _joinedCharacter;

    private static bool TryCharacterChoice(string choice, out Guid id, out bool restore)
    {
        id = Guid.Empty;
        restore = choice.StartsWith("restore:", StringComparison.Ordinal);
        int prefix = restore ? 8 : 10;
        return (restore || choice.StartsWith("character:", StringComparison.Ordinal)) &&
            Guid.TryParseExact(choice.AsSpan(prefix), "N", out id) && id != Guid.Empty;
    }

    private (SelectedCharacter? Selection, HenryChoice? Source, string Why) Wo125SelectCatalogCharacter(Guid id, string? build)
    {
        const string unavailable = "The chosen local character and its original checkpoint could not be verified.";
        if (ResolveSavesDirForJoin() is not string saves || !TryCheckpointProfile(saves, out _, out string profile))
            return (null, null, unavailable);
        var catalog = new HenryCharacterCatalog(HenryCharacterCatalog.DefaultRoot(), profile);
        if (!catalog.TryFind(id, out var entry)) return (null, null, unavailable);
        var store = new LocalStartCheckpointStore(LocalStartCheckpointStore.DefaultRoot(), profile);
        if (!store.TryRead(entry.CreationId, build, out var checkpoint, out _) ||
            !string.Equals(checkpoint.Sha256, entry.CheckpointSha256, StringComparison.OrdinalIgnoreCase))
            return (null, null, unavailable);
        var source = Wo125CheckpointChoice(saves, checkpoint);
        var lease = catalog.TryLease(id, entry.Revision);
        return lease is null ? (null, null, "The chosen character is already active or unavailable.") : (new(entry, catalog, lease, source), source, "");
    }

    private static bool TryCheckpointProfile(string saves, out string userFolder, out string profile)
    {
        userFolder = Path.GetDirectoryName(saves) ?? "";
        profile = "";
        if (!Path.IsPathFullyQualified(userFolder) || !LocalStartCheckpointStore.SafeDirectory(userFolder) ||
            !string.Equals(Path.GetFileName(saves), "saves", StringComparison.OrdinalIgnoreCase))
            return false;
        profile = LocalStartCheckpointStore.ProfileId(userFolder);
        return true;
    }

    private void ReleasePendingCharacter(JoinCharacterSelection? pending)
    {
        if (pending?.Scoped is { } selection && !ReferenceEquals(Volatile.Read(ref _joinedCharacter)?.Selection, selection))
            selection.Release();
    }

    private bool TryBindJoinCharacter(JoinerJoin join, string worldTag, string saveMd5)
    {
        if (join.Character?.Scoped is not { } selected) return true;
        if (join.RunOffer is not { } offer || !offer.IsValid || offer.HostWorldEpoch != join.HostWorldEpoch ||
            !_w157PeerHostKeyKnown || _w157PeerHostKey != offer.HostKey || join.LoadGeneration == 0 ||
            !selected.Catalog.TryFind(selected.Entry.CharacterId, out var live) || live.Revision != selected.Entry.Revision)
            return false;
        var run = new HenryRunBinding(offer.HostKey, offer.RunId, worldTag);
        if (join.InWorld && Volatile.Read(ref _joinedCharacter) is { } active && active.Scope.Run != run) return false;
        join.CharacterScope = new(live.ProfileId, live.CharacterId, live.Revision, run, join.LoadGeneration, saveMd5);
        if (!HenryStore.ValidScope(join.CharacterScope)) return false;
        Console.WriteLine($"MP-HENRY character_selected id={live.CharacterId.ToString("N")[..8]} run={run.RunId.ToString("N")[..8]} join=0x{join.JoinId:x8}");
        return true;
    }

    private JoinedCharacter? BindLoadedCharacter(JoinerJoin join)
    {
        if (join.Character?.Scoped is not { } selected || join.CharacterScope is not { } scope) return null;
        return new(selected, scope);
    }

    private void ReleaseJoinedCharacter()
    {
        var selected = Interlocked.Exchange(ref _joinedCharacter, null);
        selected?.Selection.Release();
    }
}
