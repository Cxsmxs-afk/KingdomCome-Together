// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
namespace KcdMp.Client.Tests;

public class SaveListMaintenanceTests
{
    private static CombatPipe.SaveListReport Report(bool listed, int count = 1) =>
        new(listed, listed ? 0 : -1, count, 2, 2, 0, "own-save");

    [Theory]
    [InlineData(94, 94, false)]
    [InlineData(95, 20, true)]
    [InlineData(20, 95, true)]
    [InlineData(0, 100, true)]
    [InlineData(0, null, true)]
    [InlineData(0, -1, true)]
    public void Snapshot_refuses_rotation_risk_from_either_disk_or_stale_cache(int disk, int? cached, bool blocked) =>
        Assert.Equal(blocked, SaveListMaintenance.SnapshotBlock(disk, cached).Length != 0);

    [Fact]
    public void Moved_out_snapshots_still_count_towards_the_conservative_guard()
    {
        // The mod moves each snapshot out of the playline; the engine still remembers it.
        int diskOwnSaves = 20;
        int requested = 0;
        for (int cached = 20; cached < 105; cached++)
        {
            if (SaveListMaintenance.SnapshotBlock(diskOwnSaves, cached).Length != 0) break;
            requested++;
        }
        Assert.Equal(75, requested); // stop at cache 95 without forcing a rescan or rotating own saves
        Assert.Contains("cached", SaveListMaintenance.SnapshotBlock(diskOwnSaves, 95));
    }

    [Fact]
    public async Task Leaving_refreshes_the_list_once_for_many_missing_files_then_uses_cache()
    {
        var calls = new List<(string Save, byte Op)>();
        string? selected = await SaveListMaintenance.FindOwnAsync(new[] { "newest", "missing", "loadable" }, _ => true,
            (save, op) =>
            {
                calls.Add((save, op));
                return Task.FromResult<CombatPipe.SaveListReport?>(Report(save == "loadable"));
            });
        Assert.Equal("loadable", selected);
        Assert.Equal(new[] { ("newest", (byte)3), ("newest", (byte)1), ("missing", (byte)3), ("loadable", (byte)3) }, calls);
    }

    [Fact]
    public async Task One_refresh_can_discover_the_newest_own_save_before_falling_back_to_older_saves()
    {
        var calls = new List<byte>();
        string? selected = await SaveListMaintenance.FindOwnAsync(new[] { "newest", "older" }, _ => true,
            (save, op) =>
            {
                calls.Add(op);
                return Task.FromResult<CombatPipe.SaveListReport?>(Report(op == 1));
            });
        Assert.Equal("newest", selected);
        Assert.Equal(new byte[] { 3, 1 }, calls);
    }

    [Fact]
    public async Task Corrupt_files_are_never_queried_and_an_unreadable_plugin_is_reported_honestly()
    {
        var calls = new List<(string Save, byte Op)>();
        var logs = new List<string>();
        string? selected = await SaveListMaintenance.FindOwnAsync(new[] { "corrupt", "verified", "older" }, s => s != "corrupt",
            (save, op) =>
            {
                calls.Add((save, op));
                return Task.FromResult<CombatPipe.SaveListReport?>(null);
            }, (_, why) => logs.Add(why));
        Assert.Equal("verified", selected);
        Assert.Equal(new[] { ("verified", (byte)3) }, calls);
        Assert.Contains("without engine-list confirmation", Assert.Single(logs));
    }
}
