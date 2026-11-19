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
        Publish(journal, saves, 1);

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
        Publish(journal, saves, 1);
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
        Publish(journal, saves, 1);
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
        Publish(journal, saves, 1);
        string part = journal.Register(saves.Root, saves.JoinFile(2), "otherworld", 2, Sha);
        File.WriteAllBytes(part, World);
        journal.SealStage(saves.Root, saves.JoinFile(2), 2, Sha);

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
        Publish(journal, saves, 1);
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
        Publish(journal, saves, 1);
        File.Delete(owned);
        Directory.CreateDirectory(owned);
        string personal = Path.Combine(owned, "personal.whs");
        File.WriteAllBytes(personal, World);

        Assert.False(journal.TryDeleteOwned(saves.Root, owned, 1, Sha));
        Assert.Equal(0, journal.Sweep(saves.Root, _ => false));
        Assert.Equal(World, File.ReadAllBytes(personal));
    }

    [Fact]
    public void Restart_reclaims_a_torn_private_stage_without_touching_the_visible_save()
    {
        using var saves = new TestSaves();
        var journal = new JoinWorldJournal();
        string visible = saves.JoinFile(1);
        string part = Register(journal, saves, 1);
        File.WriteAllBytes(part, World[..8]); // Stop between exclusive creation and durable sealing.
        File.WriteAllBytes(visible, World); // Even identical visible bytes were never published by this reservation.

        var restart = new JoinWorldJournal();
        Assert.Equal(1, restart.Sweep(saves.Root, _ => false));
        Assert.False(File.Exists(part));
        Assert.Equal(World, File.ReadAllBytes(visible));
        Assert.False(restart.HasReservation(saves.Root, Tag));
        Register(restart, saves, 2); // The same world can reserve the same playline again.
    }

    [Fact]
    public void A_replacement_between_identity_check_and_retirement_is_restored_without_deleting_it()
    {
        using var saves = new TestSaves();
        string visible = saves.JoinFile(1);
        string original = Path.Combine(saves.Playline, "original.whs");
        byte[] foreign = "the player's replacement"u8.ToArray();
        var journal = new JoinWorldJournal(path =>
        {
            Assert.Equal(visible, path);
            File.Move(path, original);
            File.WriteAllBytes(path, foreign);
        });
        Publish(journal, saves, 1);

        Assert.False(journal.TryDeleteOwned(saves.Root, visible, 1, Sha));
        Assert.Equal(foreign, File.ReadAllBytes(visible));
        Assert.Equal(World, File.ReadAllBytes(original));
        Assert.False(File.Exists(saves.RetiredFile(1)));
    }

    [Fact]
    public void Restart_restores_a_foreign_file_left_in_quarantine()
    {
        using var saves = new TestSaves();
        Publish(new JoinWorldJournal(), saves, 1);
        string visible = saves.JoinFile(1), retired = saves.RetiredFile(1);
        byte[] foreign = "the player's replacement"u8.ToArray();
        byte[] newer = "another save now occupies the name"u8.ToArray();
        File.Delete(visible);
        File.WriteAllBytes(retired, foreign); // Stop just after capture, before verification can restore it.
        File.WriteAllBytes(visible, newer);

        var restart = new JoinWorldJournal();
        Assert.Equal(0, restart.Sweep(saves.Root, _ => false));
        Assert.Equal(newer, File.ReadAllBytes(visible));
        Assert.Equal(foreign, File.ReadAllBytes(retired));
        Assert.True(restart.HasReservation(saves.Root, Tag));
        File.Move(visible, Path.Combine(saves.Playline, "newer.whs"));
        Assert.Equal(0, restart.Sweep(saves.Root, _ => false));
        Assert.Equal(foreign, File.ReadAllBytes(visible));
        Assert.False(File.Exists(retired));
    }

    [Fact]
    public void A_failed_publish_never_claims_an_identical_file_at_the_destination()
    {
        using var saves = new TestSaves();
        var journal = new JoinWorldJournal();
        string visible = saves.JoinFile(1), stage = Register(journal, saves, 1);
        File.WriteAllBytes(stage, World);
        journal.SealStage(saves.Root, visible, 1, Sha);
        File.WriteAllBytes(visible, World);
        Assert.Throws<IOException>(() => journal.Publish(saves.Root, visible, 1, Sha));

        var restart = new JoinWorldJournal();
        Assert.Equal(1, restart.Sweep(saves.Root, _ => false));
        Assert.Equal(World, File.ReadAllBytes(visible));
        Assert.False(File.Exists(stage));
        Assert.False(restart.HasReservation(saves.Root, Tag));
    }

    [Fact]
    public void A_replacement_with_identical_bytes_cannot_be_submitted_or_removed()
    {
        using var saves = new TestSaves();
        var journal = new JoinWorldJournal();
        Publish(journal, saves, 1);
        string visible = saves.JoinFile(1);
        string original = Path.Combine(saves.Playline, "original.whs");
        File.Move(visible, original);
        File.WriteAllBytes(visible, World);

        Assert.False(journal.MarkLoadSubmitted(saves.Root, visible, 1, Sha));
        Assert.False(journal.TryDeleteOwned(saves.Root, visible, 1, Sha));
        Assert.Equal(World, File.ReadAllBytes(visible));
        Assert.Equal(World, File.ReadAllBytes(original));
    }

    [Fact]
    public void Legacy_stages_are_recovered_only_when_their_complete_bytes_match()
    {
        using var saves = new TestSaves();
        Directory.CreateDirectory(saves.JournalDirectory);
        for (uint id = 1; id <= 2; id++)
        {
            var marker = new JsonObject { ["Version"] = 1, ["WorldTag"] = Tag, ["Playline"] = 2, ["JoinId"] = id,
                ["Sha256"] = Convert.ToHexString(Sha), ["LoadSubmitted"] = false };
            File.WriteAllText(saves.MarkerFile(id), marker.ToJsonString());
            File.WriteAllBytes(Path.ChangeExtension(saves.JoinFile(id), ".part"), id == 1 ? World : World[..8]);
        }

        var restart = new JoinWorldJournal();
        Assert.Equal(1, restart.Sweep(saves.Root, _ => false));
        Assert.False(File.Exists(saves.MarkerFile(1)));
        Assert.Equal(World[..8], File.ReadAllBytes(Path.ChangeExtension(saves.JoinFile(2), ".part")));
        Assert.True(File.Exists(saves.MarkerFile(2)));
    }

    [Fact]
    public void Restart_retains_a_legacy_capture_without_durable_file_identity()
    {
        using var saves = new TestSaves();
        Directory.CreateDirectory(saves.JournalDirectory);
        var marker = new JsonObject { ["Version"] = 1, ["WorldTag"] = Tag, ["Playline"] = 2, ["JoinId"] = 1,
            ["Sha256"] = Convert.ToHexString(Sha), ["LoadSubmitted"] = false };
        File.WriteAllText(saves.MarkerFile(1), marker.ToJsonString());
        string capture = saves.MarkerFile(1) + ".retired";
        File.WriteAllBytes(capture, World);

        var restart = new JoinWorldJournal();
        Assert.Equal(0, restart.Sweep(saves.Root, _ => false));
        Assert.Equal(World, File.ReadAllBytes(capture));
        Assert.True(restart.HasReservation(saves.Root, Tag));
    }

    private static string Register(JoinWorldJournal journal, TestSaves saves, uint id) =>
        journal.Register(saves.Root, saves.JoinFile(id), Tag, id, Sha);

    private static void Publish(JoinWorldJournal journal, TestSaves saves, uint id)
    {
        string path = saves.JoinFile(id);
        File.WriteAllBytes(Register(journal, saves, id), World);
        journal.SealStage(saves.Root, path, id, Sha);
        journal.Publish(saves.Root, path, id, Sha);
    }

    private sealed class TestSaves : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("kcdmp-join-ownership-").FullName;
        public string Playline => Path.Combine(Root, "playline2");
        public string JournalDirectory => Path.Combine(Root, JoinWorldJournal.DirectoryName);

        public TestSaves() => Directory.CreateDirectory(Playline);
        public string JoinFile(uint id) => Path.Combine(Playline, $"mpworld{id:x8}.whs");
        public string MarkerFile(uint id) => Path.Combine(JournalDirectory, $"playline2-mpworld{id:x8}.json");
        public string RetiredFile(uint id)
        {
            var marker = JsonNode.Parse(File.ReadAllText(MarkerFile(id)))!.AsObject();
            var stageId = marker["StageId"]!.GetValue<Guid>();
            return Path.Combine(JournalDirectory, $"{stageId:N}.retired");
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
