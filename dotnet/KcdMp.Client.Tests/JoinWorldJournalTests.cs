// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace KcdMp.Client.Tests;

public sealed class JoinWorldJournalTests
{
    private static readonly byte[] World = "verified join save"u8.ToArray();
    private static readonly byte[] Sha = SHA256.HashData(World);
    private const string Tag = "abc0123456";

    [Fact]
    public void Restart_sweeps_only_the_marked_preload_copy_and_reopens_its_reservation()
    {
        using var saves = new TestSaves();
        var journal = new JoinWorldJournal();
        string owned = saves.JoinFile(1);
        string unmarked = saves.JoinFile(2);
        string personal = Path.Combine(saves.Playline, "save001.whs");
        File.WriteAllBytes(unmarked, World); // Filename and bytes are insufficient without the marker.
        File.WriteAllBytes(personal, World);
        Register(journal, saves, 1);
        File.WriteAllBytes(owned, World);

        var restarted = new JoinWorldJournal();
        Assert.True(restarted.HasReservation(saves.Root, Tag));
        Assert.Equal(1, restarted.Sweep(saves.Root, _ => false));
        Assert.False(File.Exists(owned));
        Assert.Equal(World, File.ReadAllBytes(unmarked));
        Assert.Equal(World, File.ReadAllBytes(personal));
        Assert.False(restarted.HasReservation(saves.Root, Tag));
    }

    [Fact]
    public void Submitted_load_survives_restart_until_the_caller_confirms_native_safety()
    {
        using var saves = new TestSaves();
        var journal = new JoinWorldJournal();
        string owned = saves.JoinFile(1);
        Register(journal, saves, 1);
        File.WriteAllBytes(owned, World);
        Assert.True(journal.MarkLoadSubmitted(saves.Root, owned, 1, Sha));

        var restarted = new JoinWorldJournal();
        Assert.Equal(0, restarted.Sweep(saves.Root, _ => false));
        Assert.Equal(World, File.ReadAllBytes(owned));
        Assert.True(restarted.HasReservation(saves.Root, Tag));
        Assert.True(restarted.TryDeleteOwned(saves.Root, owned, 1, Sha));
        Assert.False(File.Exists(owned));
        Assert.False(restarted.HasReservation(saves.Root, Tag));
    }

    [Fact]
    public void Marker_and_exact_bytes_are_required_to_mark_load_or_delete()
    {
        using var saves = new TestSaves();
        var journal = new JoinWorldJournal();
        string owned = saves.JoinFile(1);
        byte[] changed = "a changed world"u8.ToArray();
        Register(journal, saves, 1);
        File.WriteAllBytes(owned, changed);

        Assert.False(journal.MarkLoadSubmitted(saves.Root, owned, 1, Sha));
        Assert.False(journal.TryDeleteOwned(saves.Root, owned, 1, Sha));
        Assert.Equal(0, new JoinWorldJournal().Sweep(saves.Root, _ => false));
        Assert.Equal(changed, File.ReadAllBytes(owned));
        Assert.True(journal.HasReservation(saves.Root, Tag));

        File.WriteAllBytes(owned, World);
        Assert.False(journal.TryDeleteOwned(saves.Root, owned, 1, SHA256.HashData(changed)));
        Assert.False(journal.TryDeleteOwned(saves.Root, owned, 2, Sha));
        Assert.Equal(World, File.ReadAllBytes(owned));
    }

    [Fact]
    public void Registration_refuses_preexisting_paths_and_a_second_reservation_for_one_world()
    {
        using var saves = new TestSaves();
        var journal = new JoinWorldJournal();
        string existing = saves.JoinFile(1);
        File.WriteAllBytes(existing, World);
        Assert.Throws<IOException>(() => Register(journal, saves, 1));
        Assert.False(journal.HasReservation(saves.Root, Tag));

        Register(journal, saves, 2);
        Assert.Throws<IOException>(() => Register(new JoinWorldJournal(), saves, 3));
        Assert.Equal(World, File.ReadAllBytes(existing));
        Assert.Equal(0, journal.Sweep(saves.Root, _ => false)); // No save was published for attempt 2.
        Assert.False(journal.HasReservation(saves.Root, Tag));
        Register(journal, saves, 3);
    }

    [Fact]
    public void Distinct_known_hosts_can_reserve_the_same_seed_but_a_plain_seed_is_ambiguous()
    {
        using var saves = new TestSaves();
        var journal = new JoinWorldJournal();
        const string first = Tag + "-a000000000", second = Tag + "-b000000000";
        journal.Register(saves.Root, saves.JoinFile(1), first, 1, Sha);
        Assert.True(journal.HasReservation(saves.Root, Tag));
        Assert.False(journal.HasReservation(saves.Root, second));
        journal.Register(saves.Root, saves.JoinFile(2), second, 2, Sha);
        Assert.Throws<IOException>(() => Register(journal, saves, 3));

        Assert.Equal(0, journal.Sweep(saves.Root, _ => false));
        Register(journal, saves, 3);
        Assert.True(journal.HasReservation(saves.Root, first));
        Assert.True(journal.HasReservation(saves.Root, second));
    }

    [Fact]
    public void Sweep_retains_a_live_file_and_removes_a_verified_interrupted_publication()
    {
        using var saves = new TestSaves();
        var journal = new JoinWorldJournal();
        string live = saves.JoinFile(1);
        string part = Path.ChangeExtension(saves.JoinFile(2), ".part");
        Register(journal, saves, 1);
        journal.Register(saves.Root, saves.JoinFile(2), "otherworld", 2, Sha);
        File.WriteAllBytes(live, World);
        File.WriteAllBytes(part, World);

        Assert.Equal(1, journal.Sweep(saves.Root, file => file == live));
        Assert.False(File.Exists(part));
        Assert.Equal(World, File.ReadAllBytes(live));
        Assert.False(journal.HasReservation(saves.Root, "otherworld"));
        Assert.True(journal.HasReservation(saves.Root, Tag));
    }

    [Fact]
    public void A_marker_missing_the_load_state_cannot_authorize_cleanup()
    {
        using var saves = new TestSaves();
        var journal = new JoinWorldJournal();
        string owned = saves.JoinFile(1);
        Register(journal, saves, 1);
        File.WriteAllBytes(owned, World);
        string path = Assert.Single(Directory.EnumerateFiles(Path.Combine(saves.Root, JoinWorldJournal.DirectoryName)));
        var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.True(document.Remove("LoadSubmitted"));
        File.WriteAllText(path, document.ToJsonString());

        Assert.Equal(0, journal.Sweep(saves.Root, _ => false));
        Assert.Equal(World, File.ReadAllBytes(owned));
    }

    [Fact]
    public void Non_file_join_path_is_never_claimed_or_traversed()
    {
        using var saves = new TestSaves();
        var journal = new JoinWorldJournal();
        string owned = saves.JoinFile(1);
        Register(journal, saves, 1);
        Directory.CreateDirectory(owned);
        string personal = Path.Combine(owned, "personal.whs");
        File.WriteAllBytes(personal, World);

        Assert.False(journal.TryDeleteOwned(saves.Root, owned, 1, Sha));
        Assert.Equal(0, journal.Sweep(saves.Root, _ => false));
        Assert.Equal(World, File.ReadAllBytes(personal));
    }

    private static void Register(JoinWorldJournal journal, TestSaves saves, uint id) =>
        journal.Register(saves.Root, saves.JoinFile(id), Tag, id, Sha);

    private sealed class TestSaves : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("kcdmp-join-ownership-").FullName;
        public string Playline => Path.Combine(Root, "playline2");

        public TestSaves() => Directory.CreateDirectory(Playline);
        public string JoinFile(uint id) => Path.Combine(Playline, $"mpworld{id:x8}.whs");
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
