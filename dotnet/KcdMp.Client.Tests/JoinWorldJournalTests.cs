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
        using (var staged = journal.CreateStage(saves.Root, saves.JoinFile(2), 2, Sha)) staged.Write(World);
        journal.SealStage(saves.Root, saves.JoinFile(2), 2, Sha);

        Assert.Equal(1, journal.Sweep(saves.Root, file => file == live));
        AssertNoSaveBytes(part);
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
        using (var staged = journal.CreateStage(saves.Root, visible, 1, Sha)) staged.Write(World[..8]);
        // Stop after the handle identity was bound but before the stage was sealed.
        File.WriteAllBytes(visible, World); // Even identical visible bytes were never published by this reservation.

        var restart = new JoinWorldJournal();
        Assert.Equal(1, restart.Sweep(saves.Root, _ => false));
        AssertNoSaveBytes(part);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_replacement_after_quarantine_verification_is_never_deleted_or_truncated(bool identical)
    {
        using var saves = new TestSaves();
        byte[] foreign = identical ? World : "a foreign journal occupant"u8.ToArray();
        string checkedObject = Path.Combine(saves.JournalDirectory, "checked-object");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var verified = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task exchange = Task.Run(async () =>
        {
            string quarantine = await verified.Task.WaitAsync(stop.Token);
            File.Move(quarantine, checkedObject);
            File.WriteAllBytes(quarantine, foreign);
        });
        var journal = new JoinWorldJournal(beforeDiscard: quarantine =>
        {
            verified.SetResult(quarantine);
            exchange.WaitAsync(stop.Token).GetAwaiter().GetResult();
        });
        Publish(journal, saves, 1);
        string visible = saves.JoinFile(1), retired = saves.RetiredFile(1);

        Assert.True(await Task.Run(() => journal.TryDeleteOwned(saves.Root, visible, 1, Sha), stop.Token));
        await exchange;
        Assert.False(File.Exists(visible));
        Assert.Equal(foreign, File.ReadAllBytes(retired));
        AssertNoSaveBytes(checkedObject);
        Assert.False(journal.HasReservation(saves.Root, Tag));
    }

    [Fact]
    public void Restart_releases_an_unbound_stage_reservation_without_touching_a_foreign_file()
    {
        using var saves = new TestSaves();
        var journal = new JoinWorldJournal();
        string visible = saves.JoinFile(1), stage = Register(journal, saves, 1);
        File.WriteAllBytes(stage, World);
        Assert.Throws<IOException>(() => journal.CreateStage(saves.Root, visible, 1, Sha));

        var restart = new JoinWorldJournal();
        Assert.Equal(0, restart.Sweep(saves.Root, _ => false));
        Assert.Equal(World, File.ReadAllBytes(stage));
        Assert.False(restart.HasReservation(saves.Root, Tag));
        Assert.NotEqual(stage, Register(restart, saves, 1));
    }

    [Fact]
    public void Restart_never_discards_a_replacement_at_a_durably_bound_torn_stage()
    {
        using var saves = new TestSaves();
        var journal = new JoinWorldJournal();
        string visible = saves.JoinFile(1), stage = Register(journal, saves, 1);
        byte[] torn = World[..8];
        using (var staged = journal.CreateStage(saves.Root, visible, 1, Sha)) staged.Write(torn);
        string original = Path.Combine(saves.JournalDirectory, "original");
        File.Move(stage, original);
        File.WriteAllBytes(stage, torn);

        var restart = new JoinWorldJournal();
        Assert.Equal(0, restart.Sweep(saves.Root, _ => false));
        Assert.Equal(torn, File.ReadAllBytes(stage));
        Assert.Equal(torn, File.ReadAllBytes(original));
        Assert.True(restart.HasReservation(saves.Root, Tag));
    }

    [Fact]
    public void Restart_finishes_retiring_the_same_file_when_interrupted_after_handle_discard()
    {
        using var saves = new TestSaves();
        var journal = new JoinWorldJournal();
        Publish(journal, saves, 1);
        string visible = saves.JoinFile(1), retired = saves.RetiredFile(1);
        File.Move(visible, retired);
        if (OperatingSystem.IsWindows()) File.Delete(retired); // Simulate completion of a delete disposition.
        else using (var original = File.OpenWrite(retired)) original.SetLength(0);
        // The original journal still exists, as it would after a crash here.
        var restart = new JoinWorldJournal();
        restart.Sweep(saves.Root, _ => false);
        Assert.False(restart.HasReservation(saves.Root, Tag));
        Assert.False(File.Exists(visible));
        AssertNoSaveBytes(retired);
        Assert.Equal(0, restart.Sweep(saves.Root, _ => false));
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
        using (var staged = journal.CreateStage(saves.Root, visible, 1, Sha)) staged.Write(World);
        journal.SealStage(saves.Root, visible, 1, Sha);
        File.WriteAllBytes(visible, World);
        Assert.Throws<IOException>(() => journal.Publish(saves.Root, visible, 1, Sha));

        var restart = new JoinWorldJournal();
        Assert.Equal(1, restart.Sweep(saves.Root, _ => false));
        Assert.Equal(World, File.ReadAllBytes(visible));
        AssertNoSaveBytes(stage);
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
        Register(journal, saves, id);
        using (var stage = journal.CreateStage(saves.Root, path, id, Sha)) stage.Write(World);
        journal.SealStage(saves.Root, path, id, Sha);
        journal.Publish(saves.Root, path, id, Sha);
    }

    private static void AssertNoSaveBytes(string path)
    {
        if (OperatingSystem.IsWindows()) Assert.False(File.Exists(path));
        else Assert.Equal(0, new FileInfo(path).Length);
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
