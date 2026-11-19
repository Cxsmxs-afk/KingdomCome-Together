// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using KCDMP_launcher.Models;
using KcdMp.Setup;
using Serilog;

namespace KCDMP_launcher.Pages;

public partial class Home
{
    private string o4Status = "";
    private bool O4Requested => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(O4LocalProfile.EnvironmentName));

    private bool O4BlocksSession()
    {
        if (!O4Requested && (string.IsNullOrEmpty(settings.GamePath) ||
            !O4LocalProfile.ConfiguresDedicatedFolder(GameRootOf(settings.GamePath)))) return false;
        UiService.ShowError("The dedicated prologue profile is for a local test. Host and Join are unavailable in this profile.");
        return true;
    }

    private async Task StartO4LocalProfileAsync()
    {
        if (launchPreparationBusy || roleSwitchBusy || roleGame is not null || launchStage != LaunchStage.Idle) return;
        launchPreparationBusy = true;
        try
        {
            if (!await EnsureSetupReadyAsync()) return;
            string? expected = Environment.GetEnvironmentVariable(O4LocalProfile.EnvironmentName);
            string? regular = ConsentFlags.UserFolder();
            string exe = settings.GamePath;
            if (expected is null || regular is null || !File.Exists(exe) || !IsModdingToolsBuild(exe))
            {
                o4Status = "The dedicated Modding Tools test profile is unavailable.";
                return;
            }
            string gameRoot = GameRootOf(exe);
            string leases = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KCDMP", "o4-leases");
            var games = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe));
            foreach (var existing in games) existing.Dispose();
            if (games.Length > 0)
            {
                o4Status = "Close the current game before starting the isolated prologue.";
                return;
            }
            if (!O4LocalProfile.TryReserve(gameRoot, expected, regular, leases, out var profile, out string refused))
            {
                Log.Information("MP-H21 isolated prologue refused reason={Reason}", refused);
                o4Status = "The isolated profile could not be reserved. Check that its configured user folder is separate and empty.";
                return;
            }

            using var reservation = profile!;
            var launchAtUtc = DateTime.UtcNow;
            using var game = StartChecked(new ProcessStartInfo { FileName = exe, WorkingDirectory = gameRoot, UseShellExecute = false });
            if (game is null)
            {
                o4Status = "The Modding Tools game could not be started.";
                return;
            }

            // The native menu stays in control. The Launcher sends no key,
            // console, DLL injection or save action during this preparation.
            o4Status = "Checking the game’s isolated profile. Wait for confirmation before choosing New Game.";
            StateHasChanged();
            string logPath = Path.Combine(gameRoot, "kcd.log");
            bool verified = await WaitForO4ProfileAsync(game, logPath, launchAtUtc, reservation);
            if (!verified)
            {
                Log.Warning("MP-H21 isolated prologue refused reason=engine-profile-unconfirmed");
                o4Status = "The game did not confirm the isolated profile. The Launcher is closing this test run.";
                StateHasChanged();
                if (!game.HasExited) game.Kill();
                await game.WaitForExitAsync();
                return;
            }
            Log.Information("MP-H21 isolated prologue profile=verified empty=verified network=off");
            o4Status = "Isolated profile verified. Choose New Game in the game and play the normal prologue.";
            StateHasChanged();
            await game.WaitForExitAsync();
            o4Status = "The local prologue session ended. An engine checkpoint has yet to be verified.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Warning("MP-H21 isolated prologue stopped reason={Kind}", ex.GetType().Name);
            o4Status = "The isolated prologue session could not be verified.";
        }
        finally { launchPreparationBusy = false; }
    }

    private static async Task<bool> WaitForO4ProfileAsync(Process game, string log, DateTime launchedUtc, O4LocalProfile profile)
    {
        var elapsed = Stopwatch.StartNew();
        bool wrongProfile = false;
        bool sawProfile = false;
        var follower = new KcdLogFollower(log, launchedUtc.ToLocalTime(), KcdLogFollower.SessionStartSlack)
        {
            OnLine = line =>
            {
                if (O4LocalProfile.ReadEngineFolder(line) is not string folder) return;
                if (profile.MatchesEngineIdentity(folder)) sawProfile = true;
                else wrongProfile = true;
            }
        };
        while (!game.HasExited && elapsed.Elapsed < TimeSpan.FromMinutes(7))
        {
            if (File.GetLastWriteTimeUtc(log) >= launchedUtc)
            {
                follower.Poll(elapsed.Elapsed.TotalSeconds);
                if (wrongProfile) return false;
                if (sawProfile && follower.LatestPollRead && follower.State.Stage == GameStage.Menu)
                    return !game.HasExited && profile.HasEmptySaves();
            }
            await Task.Delay(250);
        }
        return false;
    }
}
