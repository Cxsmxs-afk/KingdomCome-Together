// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.CompilerServices;
using System.Text.Json;
using KCDMP_launcher.Models;

namespace KcdMp.Client.Tests;

public sealed partial class JoinCalendarIntegrationTests
{
    [Theory]
    [InlineData("A", "B", "registered")]
    [InlineData("B", "A", "registered")]
    [InlineData("A", "B", "deleted")]
    [InlineData("B", "A", "deleted")]
    [InlineData("A", "B", "changed-home")]
    [InlineData("B", "A", "changed-home")]
    public Task Fresh_menu_joins_and_returns_to_its_verified_original_when_the_newest_checkpoint_is_unavailable(
        string host, string guest, string newerStart) =>
        RunAsync(host, guest, dropFirstSnapshot: false, JoinProbe.LocalFresh, homeProbe: false, newerStart: newerStart);

    private static async Task<string> FreshChoiceWithNewerUnavailableCheckpointAsync(SaveFixture fixture, string newerStart)
    {
        string newer = fixture.RecordLocalCheckpoint(playline: 3);
        string profile = LocalStartCheckpointStore.ProfileId(Path.GetDirectoryName(fixture.GuestSaves)!);
        var checkpoints = new LocalStartCheckpointStore(LocalStartCheckpointStore.DefaultRoot(), profile);
        var catalog = new HenryCharacterCatalog(HenryCharacterCatalog.DefaultRoot(), profile);
        Assert.True(checkpoints.TryFind("soldier", "1.5.5-release_1_5", out var latest, out string why), why);
        Assert.Equal("playline3/autosave002", latest!.EngineSave);
        if (newerStart == "changed-home") File.WriteAllBytes(newer, [7, 1, 2]);
        else
        {
            Assert.True(catalog.TryRegister(checkpoints, latest.CreationId, "Other Henry", out var entry, out why), why);
            if (newerStart == "deleted") Assert.True(catalog.TryDelete(checkpoints, entry!.CharacterId, confirmed: true, out why), why);
        }

        using var output = new StringWriter();
        Assert.Equal(0, Wo159.RunCli(["--w159", "saves", "--saves", fixture.GuestSaves], output));
        using var reported = JsonDocument.Parse(output.ToString()["W159 ".Length..]);
        string[] styles = reported.RootElement.GetProperty("localStyles").EnumerateArray().Select(item => item.GetString()!).ToArray();
        Console.WriteLine($"W6 menu: newest={newerStart} styles={string.Join(',', styles)}");
        Assert.Contains("soldier", styles);
        string log = Path.Combine(fixture.Root, "fresh-menu.log");
        var lua = new H22LauncherSessionRoleTests.MenuLua(log);
        lua.Render(MenuTakeoverRule.ModelCall(false, [], "", null, "", "idle", true, "", styles));
        lua.Click("MP_JoinGame");
        lua.Click("MP_JoinFresh");
        lua.Click("MP_Style_soldier");
        var choice = Assert.Single(File.ReadLines(log).Select(MenuTakeoverRule.Parse).OfType<MenuTakeoverRule.MenuChoice>().Where(c => c.Kind == "join"));
        string? result = null;
        Assert.True(await LauncherSessionRole.DispatchMenuActionAsync(SessionRole.Join, GameStage.Menu, choice,
            _ => throw new InvalidOperationException("Fresh ran the host's load action."), action =>
            {
                result = $"{action.Join}:{action.Style}";
                return Task.CompletedTask;
            }));
        return Assert.IsType<string>(result);
    }

    private static void AssertFreshPreflightSelectsAnAvailableCheckpoint(GameBridge guest)
    {
        object intent = Call(guest, "CurrentCharacter")!;
        var result = Assert.IsAssignableFrom<ITuple>(Call(guest, "Wo125PrepareCharacter", intent, "1.5.5-release_1_5", null!));
        Assert.True(result[0] is not null, $"Fresh preflight rejected the older verified character: {result[1]}");
    }
}
