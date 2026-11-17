// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Security.Cryptography;

namespace KcdMp.Setup.Tests;

public class FacialWorkspaceTests
{
    private static string Facial(FakeSteam steam, string rel, string contents)
    {
        string path = Path.Combine(steam.GameRoot, "Data", "Facials", rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc));
        return path;
    }

    private static string Rel(FakeSteam steam, string path) => Path.GetRelativePath(steam.GameRoot, path);

    [Fact]
    public void Actual_facial_files_are_included_recursively_without_guessing_an_extension()
    {
        using var f = new FakeSteam("facial-list").WithGame();
        var files = new[]
        {
            Facial(f, "voices.pak", "facial pack"),
            Facial(f, Path.Combine("dialogue", "voice.fsq"), "facial sequence"),
            Facial(f, Path.Combine("idle", "face.caf"), "facial clip"),
        };
        string extra = Path.Combine(f.GameRoot, "Data", "Unrelated", "not-a-workspace-file.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(extra)!);
        File.WriteAllText(extra, "unrelated");
        var expected = Workspace.ExpectedFiles(f.GameRoot);
        Assert.Equal(FakeSteam.GamePaks.Length + files.Length, expected.Count);
        foreach (var file in files) Assert.Contains(Rel(f, file), expected);
        Assert.DoesNotContain(Path.GetRelativePath(f.GameRoot, extra), expected);
        Assert.Equal(expected, Workspace.ExpectedFiles(f.GameRoot));
    }

    [Fact]
    public void Missing_facial_data_blocks_workspace_completion_until_real_links_verify()
    {
        using var f = new FakeSteam("facial-link").WithGame().WithModdingTools().Linked();
        string source = Facial(f, Path.Combine("dialogue", "voice.fsq"), "real installed facial data");
        string rel = Rel(f, source);
        byte[] original = SHA256.HashData(File.ReadAllBytes(source));
        var missing = Workspace.Verify(f.GameRoot, f.MtRoot);
        Assert.False(missing.Complete);
        Assert.Equal(EntryState.Missing, missing.Entries.Single(e => e.Rel == rel).State);
        var run = WorkspaceLinker.Link(missing);
        Assert.Equal(1, run.Linked);
        Assert.Equal(1, run.HardLinks);
        var linked = Workspace.Verify(f.GameRoot, f.MtRoot);
        Assert.True(linked.Complete);
        var entry = linked.Entries.Single(e => e.Rel == rel);
        Assert.Equal(EntryState.Linked, entry.State);
        Assert.Equal(LinkKind.HardLink, entry.Kind);
        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(source)));
        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(entry.ModdingToolsPath)));
    }

    [Fact]
    public void A_retail_facial_update_is_detected_and_relinked_without_removing_local_extras()
    {
        using var f = new FakeSteam("facial-update").WithGame().WithModdingTools();
        string source = Facial(f, Path.Combine("idle", "face.caf"), "old face");
        string rel = Rel(f, source);
        Assert.Equal(0, WorkspaceLinker.Link(Workspace.Verify(f.GameRoot, f.MtRoot)).Failed);
        string local = Path.Combine(f.MtRoot, "Data", "Facials", "my-own-face.fsq");
        File.WriteAllText(local, "local extra");
        File.Delete(source); // Steam replaces a file, leaving its old hard link behind.
        File.WriteAllText(source, "updated installed facial data, a different length");
        var stale = Workspace.Verify(f.GameRoot, f.MtRoot);
        Assert.False(stale.Complete);
        Assert.Equal(EntryState.Stale, stale.Entries.Single(e => e.Rel == rel).State);
        Assert.Equal(1, WorkspaceLinker.Link(stale).Linked);
        Assert.True(Workspace.Verify(f.GameRoot, f.MtRoot).Complete);
        Assert.Equal(File.ReadAllText(source), File.ReadAllText(Path.Combine(f.MtRoot, rel)));
        Assert.Equal("local extra", File.ReadAllText(local));
        Assert.Empty(Directory.GetFiles(f.MtRoot, "*" + WorkspaceLinker.TempSuffix, SearchOption.AllDirectories));
    }

    [Fact]
    public void A_separate_facial_copy_is_verified_and_becomes_stale_after_a_retail_update()
    {
        using var f = new FakeSteam("facial-copy").WithGame().WithModdingTools();
        string source = Facial(f, "voices.pak", "facial v1");
        f.Linked(FakeSteam.Linking.Copies);
        string rel = Rel(f, source);
        Assert.Equal(EntryState.Copy, Workspace.Verify(f.GameRoot, f.MtRoot).Entries.Single(e => e.Rel == rel).State);
        File.WriteAllText(source, "facial v2, updated and larger");
        Assert.Equal(EntryState.Stale, Workspace.Verify(f.GameRoot, f.MtRoot).Entries.Single(e => e.Rel == rel).State);
    }

    [Fact]
    public void No_source_facial_folder_preserves_the_existing_workspace_contract()
    {
        using var f = new FakeSteam("facial-absent").WithGame().WithModdingTools().Linked();
        Assert.False(Directory.Exists(Path.Combine(f.GameRoot, "Data", "Facials")));
        Assert.Equal(FakeSteam.GamePaks.Length, Workspace.ExpectedFiles(f.GameRoot).Count);
        Assert.True(Workspace.Verify(f.GameRoot, f.MtRoot).Complete);
    }

    [Fact]
    public async Task Existing_installs_run_the_normal_link_flow_when_only_facial_files_are_missing()
    {
        using var f = new FakeSteam("facial-existing").WithGame().WithModdingTools().Linked();
        string source = Facial(f, Path.Combine("dialogue", "voice.fsq"), "installed facial data");
        var outcome = await WorkspaceSetupFlow.RunAsync(f.GameRoot, f.MtRoot,
            new WorkspaceFlowOptions { TryWarhorseTool = false });
        Assert.Equal(WorkspaceFlowResult.Linked, outcome.Result);
        Assert.True(outcome.After.Complete);
        Assert.Equal(EntryState.Linked, outcome.After.Entries.Single(e => e.Rel == Rel(f, source)).State);
        Assert.Equal("hard links", outcome.Route);
    }
}
