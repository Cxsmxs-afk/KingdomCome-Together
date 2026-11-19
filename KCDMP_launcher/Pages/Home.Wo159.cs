// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KCDMP_launcher.Components.Shared;
using KCDMP_launcher.Models;
using KcdMp.Setup;
using Serilog;

namespace KCDMP_launcher.Pages
{
    /// <summary>
    /// WO-159: New Game without the prologue -- Start Game / Join Game on the main menu of a game this launcher
    /// started (docs/WO-159-findings.md).
    ///
    /// Before the game starts: the first-run pages are checked (read only, never written); a host's bundled start save
    /// is staged into a free playline with a new playthrough seed (the engine lists saves once, at its start, and the
    /// host has no plugin at the menu to rescan); the player's own worlds are listed by the agent's helper (the WO-125
    /// / WO-157 classifier).
    ///
    /// While the game sits at its main menu: the menu is armed and redrawn through the game's own console (four times a
    /// second; no Lua timer runs at the main menu), the menu's choices are read from kcd.log, and each is carried out
    /// with the calls that already exist -- a load from the menu (WO-124, after the menu's video is stopped: WO-154),
    /// the joiner's CONNECT once its host is ready (the WO-154 gate) and the first-join answer (WO-125's /join-choice).
    /// When the game exits, a start save that was never played is taken away again.
    /// </summary>
    public partial class Home
    {
        private volatile bool w159Takeover;                 // this launch's menu is ours (cleared when the launch is reset)
        private string w159JoinChoice = "";                 // "fresh" / "bring" once the joiner pressed Join
        private string w159JoinState = "idle";              // idle | waiting | joining (what the joiner's menu shows)
        private W159SavesData? w159Saves;
        private string w159NewWhy = "";
        private string? w159StartReservation;
        private string? w159StartRoot;                        // the start saves: one folder per playstyle
        private List<string> w159Styles = new();              // the playstyles whose start save is installed

        private const int W159TickMs = 250;
        private const int W159StopVideoSettleMs = 1500;   // the agent's own settle after wh_ui_StopMovie (GameBridge.Wo154Join)

        public sealed class W159World { public int Pl { get; set; } public string Name { get; set; } = ""; public string Label { get; set; } = ""; }
        public sealed class W159StagedData { public int Pl { get; set; } public string Name { get; set; } = ""; public string Path { get; set; } = ""; public string SeedTag { get; set; } = ""; }
        public sealed class W159SavesData
        {
            public bool Ok { get; set; }
            public List<W159World> Usable { get; set; } = new();
            public string Hidden { get; set; } = "";
            public int HiddenRegular { get; set; }
            public int HiddenCopies { get; set; }
            public List<int> Free { get; set; } = new();
            public W159StagedData? Staged { get; set; }
        }
        public sealed class W159StageData { public bool Ok { get; set; } public int Pl { get; set; } public string Name { get; set; } = ""; public string SeedTag { get; set; } = ""; public string Why { get; set; } = ""; }
        public sealed class W159UnstageData { public bool Ok { get; set; } public int Removed { get; set; } public int Kept { get; set; } public List<string> Lines { get; set; } = new(); }

        /// <summary>
        /// Before the game starts. False = the launch stops here (the first-run pages are not accepted: the game would show
        /// them first, under a menu this launcher redraws).
        /// </summary>
        private async Task<bool> Wo159ConsentReadyAsync()
        {
            var consent = await Task.Run(() => ConsentFlags.Read(ConsentFlags.UserFolder()));
            Log.Information("MP-W159 first-run pages: {State} ({Detail})", consent.State, consent.Detail);
            if (consent.State is ConsentFlags.State.Missing or ConsentFlags.State.Stale)
            {
                UiService.ShowError(ConsentFlags.AcceptMessage);
                return false;
            }
            return true;
        }

        private async Task Wo159PrepareMenuAsync(string agent, bool takeover)
        {
            w159JoinChoice = "";
            w159JoinState = "idle";
            w159Saves = null;
            w159NewWhy = "";
            w159StartReservation = null;
            if (!takeover) return;
            w159StartRoot = Environment.GetEnvironmentVariable("KCDMP_START_SAVE") is { Length: > 0 } envRoot ? envRoot : ResolveAgainstLauncher(settings.StartSavePath);
            w159Styles = MenuTakeoverRule.Styles.Where(st => Directory.Exists(Path.Combine(w159StartRoot, st))
                && Directory.GetFiles(Path.Combine(w159StartRoot, st), "*.whs").Length == 1).ToList();
            Log.Information("MP-W159 start saves installed: {Styles}", w159Styles.Count == 0 ? "none" : string.Join(", ", w159Styles));
            // a start save an earlier run staged and never played (the launcher closed with its game still open)
            if (await AgentHelper.RunAsync<W159UnstageData>(agent, "--w159 unstage", "W159", TimeSpan.FromSeconds(20)) is { } u && u.Lines.Count > 0)
                Log.Information("MP-W159 earlier start saves: {Lines}", string.Join("; ", u.Lines));
            // The engine indexes saves when it starts. Prepare the empty-slot start save for either menu role.
            if (w159Styles.Count > 0)
            {
                var st = await AgentHelper.RunAsync<W159StageData>(agent, $"--w159 stage --source \"{w159StartRoot}\"", "W159", TimeSpan.FromSeconds(30));
                if (st is { Ok: true })
                {
                    w159StartReservation = $"playline{st.Pl}/{st.Name}.whs";
                    Log.Information("MP-W159 start save staged: playline{Pl}/{Name} (new world {Tag})", st.Pl, st.Name, st.SeedTag);
                }
                else
                {
                    w159NewWhy = st is null ? "The start save could not be prepared (see the launcher log)." : PlainNewWhy(st.Why);
                    Log.Information("MP-W159 no new adventure this launch: {Why}", st?.Why ?? "the helper did not answer");
                }
            }
            Wo159PlaceLogo();
            w159Saves = await AgentHelper.RunAsync<W159SavesData>(agent, "--w159 saves", "W159", TimeSpan.FromSeconds(60));
            Log.Information("MP-W159 worlds: {N} usable, staged={Staged}, hidden=\"{Hidden}\"", w159Saves?.Usable.Count ?? -1,
                w159Saves?.Staged is { } s ? $"playline{s.Pl}/{s.Name}" : "none", w159Saves?.Hidden ?? "");
        }

        private static string PlainNewWhy(string why) =>
            why.StartsWith("All five", StringComparison.Ordinal) ? why
            : why.StartsWith("no start save", StringComparison.Ordinal) ? "This installation has no start saves (they come with the installer)."
            : "The start save could not be prepared: " + why;

        /// <summary>From LaunchGame, once the game process is up.</summary>
        private void Wo159StartDriver(MenuGameScope scope, bool takeover, string agent)
        {
            w159Takeover = takeover;
            if (!w159Takeover) Log.Information("MP-W159 MenuTakeover off: the game's own menu");
            _ = Wo159DriverAsync(scope, takeover, agent);
        }

        private async Task<bool> Wo159DiscardStartSaveForJoinAsync()
        {
            string? reservation = w159StartReservation;
            if (reservation is null && w159Saves?.Staged is { } st) reservation = $"playline{st.Pl}/{st.Name}.whs";
            if (reservation is null) return true;
            var result = await AgentHelper.RunAsync<W159UnstageData>(ResolveAgainstLauncher(settings.AgentPath),
                "--w159 unstage", "W159", TimeSpan.FromSeconds(20));
            bool released = result is { Ok: true } && result.Lines.Any(line =>
                line.Equals(reservation + ": never played -- removed", StringComparison.OrdinalIgnoreCase)
                || line.Equals(reservation + ": gone already", StringComparison.OrdinalIgnoreCase));
            if (!released && result is { Ok: true } && w159Saves?.Staged is { Path.Length: > 0 } staged
                && reservation.Equals($"playline{staged.Pl}/{staged.Name}.whs", StringComparison.OrdinalIgnoreCase))
            {
                // The previous attempt can remove this file before its helper reply is lost.
                try { File.GetAttributes(staged.Path); }
                catch (FileNotFoundException) { released = true; }
                catch (DirectoryNotFoundException) { released = true; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warning("MP-W159 could not check the staged start save after release: {Kind}", ex.GetType().Name);
                }
            }
            if (!released)
            {
                Log.Warning("MP-W159 staged start save release unconfirmed before Join: {Result}", result is null ? "no helper answer" : string.Join(" | ", result.Lines));
                return false;
            }
            w159StartReservation = null;
            if (w159Saves is { } saves) saves.Staged = null;
            Log.Information("MP-W159 staged start save released before Join");
            return true;
        }

        private async Task Wo159CleanupPreparedMenuAsync(string agent)
        {
            try
            {
                await Wo159RemoveLogoAsync();
                var u = await AgentHelper.RunAsync<W159UnstageData>(agent, "--w159 unstage", "W159", TimeSpan.FromSeconds(20));
                if (u is not { Ok: true }) Log.Warning("MP-W159 could not confirm start save cleanup");
                else if (u.Lines.Count > 0)
                    Log.Information("MP-W159 start saves after menu cleanup: {Lines}", string.Join("; ", u.Lines));
            }
            catch (Exception ex) { Log.Warning("MP-W159 menu cleanup failed: {Kind}", ex.GetType().Name); }
        }

        /// <summary>From ResetLaunchState: the menu is the game's again (the exit watch still takes back an unplayed start save).</summary>
        private void Wo159OnLaunchReset() => w159Takeover = false;

        private string Wo159Model(MenuGameScope scope, bool hosting)
        {
            var s = w159Saves;
            var worlds = (s?.Usable ?? new()).Select(w => new MenuTakeoverRule.WorldEntry(w.Pl, w.Name, w.Label)).ToList();
            var adv = s?.Staged is { } st && st.Pl is >= 0 and <= 4 ? new MenuTakeoverRule.WorldEntry(st.Pl, st.Name, "") : null;
            string why = adv is not null ? "" : w159NewWhy.Length > 0 ? w159NewWhy : "No new adventure is available.";
            var (bring, msg) = MenuTakeoverRule.JoinChoice(s?.Usable.Count ?? 0, s?.HiddenRegular ?? 0, s?.HiddenCopies ?? 0);
            return MenuTakeoverRule.ModelCall(hosting, worlds, s?.Hidden ?? "", adv, why, w159JoinState, bring, msg, w159Styles,
                w159JoinState == "joining" ? joinStatusMessage ?? "" : "", scope.RoleStatus, scope.RoleAck);
        }

        private async Task Wo159DriverAsync(MenuGameScope scope, bool takeoverPrepared, string agent)
        {
            using var console = new GameConsole();
            var game = scope.Game;
            var clock = scope.Clock;
            if (takeoverPrepared) scope.MenuEvent = line =>
            {
                if (!w159Takeover) return;
                if (MenuTakeoverRule.Parse(line) is { } c) scope.Choices.Enqueue(c);
                else if (MenuTakeoverRule.ParseCoopSwitch(line) is { } sw) w175Switches.Enqueue(sw);   // WO-175 S1
            };
            long lastSeq = -1, w175LastSeq = -1;
            string pushed = "";
            bool armedLogged = false, choicePosted = false, menuLoad = false, hostConnectTried = false;
            bool recapPending = false, recapSent = false;
            long menuRevision = 0;
            DateTime connectedAt = DateTime.MinValue;
            var driverPlan = pendingPlan;
            if (takeoverPrepared) Log.Information("MP-W159 menu driver on ({Role})", driverPlan?.Hosting == true ? "host" : "joiner");
            try
            {
                while (!game.HasExited)
                {
                    double now = clock.Elapsed.TotalSeconds;
                    var seen = await scope.ObserveAsync();
                    var stage = seen.Stage;
                    if (!w159Takeover || !RoleSessionCurrent(scope))
                    {
                        await Task.Delay(W159TickMs);
                        continue;
                    }
                    if (driverPlan != pendingPlan)
                    {
                        driverPlan = pendingPlan;
                        menuLoad = hostConnectTried = choicePosted = recapPending = recapSent = false;
                        Log.Information("MP-W159 menu driver on ({Role})", driverPlan?.Hosting == true ? "host" : "joiner");
                    }
                    bool hosting = driverPlan?.Hosting == true;
                    if (stage == GameStage.Menu && seen.MenuRevision != menuRevision)
                    {
                        menuRevision = seen.MenuRevision;
                        scope.LoadIssued = false;
                        pushed = "";
                    }

                    while (scope.Choices.TryDequeue(out var c))
                    {
                        if (c.Seq <= lastSeq) continue;
                        lastSeq = c.Seq;
                        if (c.Kind == "role")
                        {
                            await HandleMenuRoleAsync(scope, c);
                            seen = await scope.ObserveAsync();
                            stage = seen.Stage;
                            if (driverPlan != pendingPlan)
                            {
                                driverPlan = pendingPlan;
                                hosting = driverPlan?.Hosting == true;
                                menuLoad = hostConnectTried = choicePosted = recapPending = recapSent = false;
                                Log.Information("MP-W159 menu driver on ({Role})", hosting ? "host" : "joiner");
                            }
                            continue;
                        }
                        if (c.Kind == "recap")
                        {
                            Log.Information("MP-W159 the prologue's cutscenes {How}", c.Name);
                            continue;
                        }
                        if (c.Kind == "cancel" && !hosting && w159JoinState == "waiting")
                        {
                            w159JoinChoice = "";
                            w159JoinState = "idle";
                            continue;
                        }
                        if (driverPlan is null || !RoleSessionCurrent(scope) || roleSwitchBusy || scope.LoadIssued
                            || launchStage != LaunchStage.WaitingForConnect || w159JoinState != "idle")
                        {
                            Log.Information("MP-W159 menu action refused kind={Kind} reason=session-action-active", c.Kind);
                            continue;
                        }
                        scope.ActionInProgress = true;
                        try
                        {
                            seen = await scope.ObserveAsync();
                            stage = seen.Stage;
                            if (!RoleSessionCurrent(scope) || !ReferenceEquals(pendingPlan, driverPlan) || roleSwitchBusy
                                || launchStage != LaunchStage.WaitingForConnect)
                            {
                                Log.Information("MP-W159 menu action refused kind={Kind} reason=plan-retired", c.Kind);
                                continue;
                            }
                            bool acted = await LauncherSessionRole.DispatchMenuActionAsync(driverPlan.Role, stage, c,
                                async picked =>
                                {
                                    Log.Information("MP-W159 the menu chose {Kind} {Pl} {Name}", picked.Kind, picked.Playline, picked.Name);
                                    scope.LoadIssued = await Wo159RunHostChoiceAsync(scope, console, picked);
                                    menuLoad |= scope.LoadIssued;
                                    recapPending |= scope.LoadIssued && picked.Watch;
                                    if (!scope.LoadIssued) pushed = "";
                                }, picked =>
                                {
                                    Log.Information("MP-W159 the menu chose join {Join}", picked.Join);
                                    w159JoinChoice = picked.Join == "fresh" ? "fresh:" + picked.Style : "bring";
                                    w159JoinState = "waiting";
                                    return Task.CompletedTask;
                                });
                            if (!acted) Log.Information("MP-W159 menu action refused kind={Kind} stage={Stage} reason=role-or-stage", c.Kind, stage);
                        }
                        finally { scope.ActionInProgress = false; }
                    }

                    // WO-175 S1: mp_coop_playline typed at the main menu (in a world the agent remembers it)
                    while (w175Switches.TryDequeue(out var cs))
                    {
                        if (cs.Seq <= w175LastSeq) continue;
                        w175LastSeq = cs.Seq;
                        if (stage != GameStage.World) await Wo175CoopSwitchAsync(console, cs.On);
                    }

                    // "Watch the prologue": its rendered cutscenes start once the New adventure's world is there
                    if (recapPending && !recapSent && stage == GameStage.World
                        && now - seen.WorldSinceS >= 2)
                    {
                        recapSent = await console.LuaAsync(MenuTakeoverRule.RecapCall());
                        Log.Information("MP-W159 the prologue's cutscenes {State} ({Min} min, skipping one skips them all)", recapSent ? "started" : "NOT started", MenuTakeoverRule.RecapMinutes);
                    }

                    // the host: CONNECT pressed by the launcher once the world Start Game loaded has settled (no Alt+Tab)
                    if (w159Takeover && !hostConnectTried && pendingDllPath is not null && MenuTakeoverRule.HostAutoConnect(hosting, menuLoad, stage,
                            seen.WorldSinceS, now, launchStage == LaunchStage.WaitingForConnect, ConnectEnabled))
                    {
                        hostConnectTried = true;
                        Log.Information("MP-W159 the world Start Game loaded has settled: CONNECT for the host");
                        await InvokeAsync(ConnectToGame);
                        if (launchStage != LaunchStage.Connected) await console.LuaAsync(MenuTakeoverRule.HudLine(MenuTakeoverRule.ConnectFailedText));
                    }

                    // the joiner: CONNECT as soon as the host is ready (the WO-154 gate), then the first-join answer
                    if (!hosting && w159Takeover && w159JoinState == "waiting" && launchStage == LaunchStage.WaitingForConnect && pendingDllPath is not null && ConnectEnabled)
                    {
                        w159JoinState = "joining";
                        Log.Information("MP-W159 the host is ready: CONNECT for the menu's Join ({Choice})", w159JoinChoice);
                        await InvokeAsync(ConnectToGame);
                        if (launchStage == LaunchStage.Connected) connectedAt = DateTime.UtcNow;
                        else
                        {
                            w159JoinChoice = "";
                            w159JoinState = "idle";
                            await console.LuaAsync(MenuTakeoverRule.HudLine(MenuTakeoverRule.ConnectFailedText));
                        }
                    }
                    if (!hosting && w159JoinState == "joining" && !choicePosted && launchStage == LaunchStage.Connected && w159JoinChoice.Length > 0)
                    {
                        if (await NetService.PostJoinChoiceAsync(settings.VersionIpcPort, w159JoinChoice))
                        {
                            choicePosted = true;
                            Log.Information("MP-W159 first-join answer sent to the agent: {Choice}", w159JoinChoice);
                        }
                        else if ((DateTime.UtcNow - connectedAt).TotalSeconds > 90)
                        {
                            choicePosted = true;
                            Log.Warning("MP-W159 the agent did not take the first-join answer in 90 s; the launcher's own buttons ask again");
                        }
                    }
                    if (!hosting && w159JoinState == "joining" && launchStage is LaunchStage.Idle or LaunchStage.Failed)
                        w159JoinState = "idle";   // the connect did not happen (refused by Windows, reset): the choice page again

                    if (w159Takeover && stage == GameStage.Menu && !scope.LoadIssued)
                    {
                        string model = Wo159Model(scope, hosting);
                        if (model != pushed && await console.LuaAsync(model)) pushed = model;
                        if (await console.LuaAsync(MenuTakeoverRule.TickCall) && !armedLogged)
                        {
                            armedLogged = true;
                            Log.Information("MP-W159 menu armed: Start Game / Join Game");
                        }
                    }
                    await Task.Delay(W159TickMs);
                }
            }
            catch (Exception ex) { Log.Warning("MP-W159 menu driver stopped: {Kind}: {Msg}", ex.GetType().Name, ex.Message); }
            try { await game.WaitForExitAsync(); }
            catch (Exception ex) { Log.Warning("MP-W159 game wait ended with {Kind}", ex.GetType().Name); }
            Log.Information("MP-W159 the game exited");
            if (takeoverPrepared) await Wo159CleanupPreparedMenuAsync(agent);
            if (ReferenceEquals(roleGame, scope))
            {
                roleGame = null;
                if (ReferenceEquals(pendingGameProcess, game)) ResetLaunchState();
            }
        }

        private async Task<bool> Wo159RunHostChoiceAsync(MenuGameScope scope, GameConsole console, MenuTakeoverRule.MenuChoice choice)
        {
            if (choice.Kind == "load") return await Wo159LoadAsync(scope, console, choice.Playline, choice.Name, false);
            if (w159Saves?.Staged is not { } staged)
            {
                Log.Information("MP-W159 new adventure refused reason=no-staged-save");
                return false;
            }
            var swapped = await AgentHelper.RunAsync<W159StageData>(ResolveAgainstLauncher(settings.AgentPath),
                $"--w159 swap --path \"{staged.Path}\" --source \"{w159StartRoot}\" --style {choice.Style}", "W159", TimeSpan.FromSeconds(30));
            if (!RoleSessionCurrent(scope) || await scope.ReadStageAsync() != GameStage.Menu)
            {
                Log.Information("MP-W159 new adventure refused reason=menu-left-during-preparation");
                return false;
            }
            if (swapped is not { Ok: true })
            {
                Log.Warning("MP-W159 the {Style} start save could not be put in place: {Why}", choice.Style, swapped?.Why ?? "the helper did not answer");
                await console.LuaAsync(MenuTakeoverRule.HudLine("The new adventure could not be prepared. Switch to the launcher window for details."));
                return false;
            }
            Log.Information("MP-W159 new adventure: {Style}, a new world {Tag}; prologue {How}", choice.Style, swapped.SeedTag, choice.Watch ? "watched" : "skipped");
            return await Wo159LoadAsync(scope, console, staged.Pl, staged.Name, true);
        }

        // ------------------------------------------------------------ our logo on the main menu (launcher-started only)
        //
        // The game draws its menu logo from Libs/UI/Textures/KCDLogo.dds. kdcmp_brand.pak (beside the launcher; built by
        // tools\Build-MenuLogo.py + tools\Publish-Release.ps1) carries ours under that name. It is put into the mod's
        // Data folder only for a game this launcher starts and taken out when that game exits, so a game started any other
        // way shows the game's own logo. A pak left behind by a launcher that was killed is removed by the next launch's
        // exit, or by Setup (it prunes what is not the mod's).
        private const string BrandPak = "kdcmp_brand.pak";

        private string? Wo159BrandTarget() =>
            string.IsNullOrEmpty(settings.GamePath) ? null : Path.Combine(ModDirFor(settings.GamePath), "Data", BrandPak);

        private void Wo159PlaceLogo()
        {
            string src = ResolveAgainstLauncher(BrandPak);
            if (!File.Exists(src) || Wo159BrandTarget() is not string dst) { Log.Information("MP-W159 no menu logo pak beside the launcher: the game's own logo"); return; }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, overwrite: true);
                Log.Information("MP-W159 our logo placed for this game ({Pak})", BrandPak);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warning("MP-W159 our logo could not be placed: {Kind}", ex.GetType().Name); }
        }

        private async Task Wo159RemoveLogoAsync()
        {
            if (Wo159BrandTarget() is not string dst || !File.Exists(dst)) return;
            // the game can hold the pak for a moment after its process ended (0.45.6: IOException at the exit)
            for (int attempt = 1; ; attempt++)
            {
                try { File.Delete(dst); Log.Information("MP-W159 our logo taken out again (the game's own for a normal start)"); return; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt >= 15) { Log.Warning("MP-W159 our logo could not be taken out: {Kind} (the next launch's exit or Setup removes it)", ex.GetType().Name); return; }
                    await Task.Delay(1000);
                }
            }
        }

        /// <summary>The menu's load: the menu's video stopped first (WO-154's freeze), then the WO-124 load.</summary>
        private async Task<bool> Wo159LoadAsync(MenuGameScope scope, GameConsole console, int pl, string name, bool newAdventure)
        {
            if (!MenuTakeoverRule.IsSaveName(name) || pl is < 0 or > 4)
            {
                Log.Information("MP-W159 load refused reason=invalid-slot-or-save");
                return false;
            }
            await console.LuaAsync("if KCD2MP_W159Disarm then KCD2MP_W159Disarm(\"load\") end");
            await console.CommandAsync("wh_ui_StopMovie");
            await Task.Delay(W159StopVideoSettleMs);
            GameStage stage = await scope.ReadStageAsync();
            if (!RoleSessionCurrent(scope) || stage != GameStage.Menu || pendingPlan is not { Hosting: true }
                || launchStage != LaunchStage.WaitingForConnect)
            {
                Log.Information("MP-W159 load refused stage={Stage} reason=menu-left-during-preparation", stage);
                return false;
            }
            bool ok = await console.LuaAsync(MenuTakeoverRule.LoadCall(pl, name));
            Log.Information("MP-W159 {What}: wh_sys_LoadGame {Pl} {Name} -> {Ok}", newAdventure ? "new adventure" : "load", pl, name, ok ? "sent" : "NOT sent");
            if (ok && newAdventure && w159Saves?.Staged is { } st)
            {
                // a normal save from now on: never taken back, even before its first save
                string agent = ResolveAgainstLauncher(settings.AgentPath);
                if (await AgentHelper.RunAsync<W159StageData>(agent, $"--w159 used --path \"{st.Path}\"", "W159", TimeSpan.FromSeconds(20)) is { Ok: true })
                {
                    w159StartReservation = null;
                    w159Saves.Staged = null;
                }
                else Log.Warning("MP-W159 load was sent but staged-save adoption was not confirmed");
            }
            return ok;
        }
    }
}
