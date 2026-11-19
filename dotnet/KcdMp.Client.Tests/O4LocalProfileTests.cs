// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using KCDMP_launcher.Models;

namespace KcdMp.Client.Tests;

public sealed class O4LocalProfileTests : IDisposable
{
    private const string Folder = "KingdomCome2_KCT_O4_12345678";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kct-o4-fixture-" + Guid.NewGuid().ToString("N"));
    private string Game => Path.Combine(_root, "game");
    private string Regular => Path.Combine(_root, "Saved Games", "KingdomCome2");
    private string Isolated => Path.Combine(_root, "Saved Games", Folder);
    private string Private => Path.Combine(_root, "mod");
    private string Config => Path.Combine(Game, "system.cfg");

    public O4LocalProfileTests()
    {
        Directory.CreateDirectory(Game);
        Directory.CreateDirectory(Regular);
        Directory.CreateDirectory(Isolated);
    }

    [Fact]
    public void A_fresh_engine_report_matches_only_the_reserved_configured_profile()
    {
        string config = $"sys_user_folder = KingdomCome2\nsys_user_folder = \"{Folder}\"\n";
        File.WriteAllText(Config, config);
        Assert.True(O4LocalProfile.ConfiguresDedicatedFolder(Game));
        Assert.True(O4LocalProfile.TryReserve(Game, Isolated, Regular, Private, out var reservation, out string why), why);
        using var profile = reservation!;
        Assert.False(O4LocalProfile.TryReserve(Game, Isolated, Regular, Private, out _, out _));
        Assert.Null(O4LocalProfile.ReadEngineFolder($"User folder is '{Isolated}"));
        Assert.False(profile.MatchesEngineIdentity(O4LocalProfile.ReadEngineFolder($"User folder is '{Regular}'")));
        Assert.False(profile.MatchesEngineIdentity(O4LocalProfile.ReadEngineFolder($"User folder is '{Isolated}(1)'")));
        Assert.True(profile.MatchesEngineIdentity(O4LocalProfile.ReadEngineFolder($"User folder is '{Isolated}'")));
        Assert.True(profile.HasEmptySaves());
        Assert.Equal(config, File.ReadAllText(Config));

        string appeared = Path.Combine(Isolated, "saves", "playline0", "autosave001.whs");
        Directory.CreateDirectory(Path.GetDirectoryName(appeared)!);
        File.WriteAllBytes(appeared, [2, 1]);
        Assert.False(profile.HasEmptySaves());
        Assert.Equal(new byte[] { 2, 1 }, File.ReadAllBytes(appeared));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Regular));
    }

    [Fact]
    public void The_previous_games_verified_folder_and_menu_are_not_a_current_launch_receipt()
    {
        string path = Path.Combine(Game, "kcd.log");
        var now = new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Local);
        string Lines(DateTime at) => "Log Started at " + at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            + $"\nUser folder is '{Isolated}'\nPlayVideoOnly 'main_menu'\n";
        File.WriteAllText(path, Lines(now.AddMinutes(-1)));
        string? observed = null;
        var follower = new KcdLogFollower(path, now, KcdLogFollower.SessionStartSlack)
        {
            OnLine = line => observed = O4LocalProfile.ReadEngineFolder(line) ?? observed
        };
        follower.Poll(0);
        Assert.Null(observed);
        Assert.False(follower.LatestPollRead);
        Assert.Equal(GameStage.Starting, follower.State.Stage);

        File.WriteAllText(path, Lines(now));
        follower.Poll(1);
        Assert.Equal(Isolated, observed);
        Assert.True(follower.LatestPollRead);
        Assert.Equal(GameStage.Menu, follower.State.Stage);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("regular-last")]
    [InlineData("malformed-last")]
    [InlineData("relative")]
    [InlineData("existing-saves")]
    public void The_launcher_never_falls_back_to_an_unproven_profile(string variant)
    {
        string requested = variant == "relative" ? Folder : Isolated;
        if (variant != "missing") File.WriteAllText(Config, $"sys_user_folder = {Folder}\n");
        if (variant == "regular-last") File.AppendAllText(Config, "sys_user_folder = KingdomCome2\n");
        if (variant == "malformed-last") File.AppendAllText(Config, "sys_user_folder = ../../KingdomCome2\n");
        if (variant == "existing-saves")
        {
            string occupied = Path.Combine(Isolated, "saves", "playline4");
            Directory.CreateDirectory(occupied);
            File.WriteAllBytes(Path.Combine(occupied, "permanent001.whs"), [7, 5]);
        }
        Assert.False(O4LocalProfile.TryReserve(Game, requested, Regular, Private, out _, out _));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Regular));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
