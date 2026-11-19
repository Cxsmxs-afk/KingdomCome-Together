// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using KCDMP_launcher.Models;
using KCDMP_launcher.Services;
using Serilog;

namespace KCDMP_launcher.Pages;

public partial class Home
{
    private readonly LauncherSessionRole sessionRoles = new();
    private SessionLaunchPlan? pendingPlan;
    private MenuGameScope? roleGame;
    private Process? agentGameProcess;
    private bool roleSwitchBusy;
    private bool launchPreparationBusy;
    private bool hidePendingPanel;
    private bool showConnectedPanel;
    private bool showJoinRole;
    private string joinRoleAddress = "";
    private string joinRoleError = "";
    private ServerInfo? directRoleTarget;

    private readonly record struct MenuObservation(GameStage Stage, double WorldSinceS, long MenuRevision, bool Reading);

    private sealed class MenuGameScope(Process game, DateTime started, string workingRoot, string installRoot)
    {
        private readonly object logGate = new();
        private readonly KcdLogFollower workingLog = new(Path.Combine(workingRoot, "kcd.log"), started, KcdLogFollower.SessionStartSlack);
        private readonly KcdLogFollower? installLog = string.Equals(workingRoot, installRoot, StringComparison.OrdinalIgnoreCase) ? null
            : new(Path.Combine(installRoot, "kcd.log"), started, KcdLogFollower.SessionStartSlack);
        private KcdLogFollower? activeLog;

        public Process Game { get; } = game;
        public DateTime Started { get; } = started;
        public Stopwatch Clock { get; } = Stopwatch.StartNew();
        public Action<string>? MenuEvent { get; set; }
        public ConcurrentQueue<MenuTakeoverRule.MenuChoice> Choices { get; } = new();
        public bool ActionInProgress { get; set; }
        public bool LoadIssued { get; set; }
        public long RoleAck { get; set; }
        public string RoleStatus { get; set; } = "";

        public async Task<GameStage> ReadStageAsync() => (await ObserveAsync()).Stage;

        public Task<MenuObservation> ObserveAsync() => Task.Run(() =>
        {
            lock (logGate)
            {
                double now = Clock.Elapsed.TotalSeconds;
                if (activeLog is { } log)
                {
                    log.OnLine = MenuEvent;
                    log.Poll(now);
                }
                else
                {
                    workingLog.Poll(now);
                    if (workingLog.LatestPollRead) activeLog = workingLog;
                    else if (installLog is { } fallback)
                    {
                        fallback.Poll(now);
                        if (fallback.LatestPollRead) activeLog = fallback;
                    }
                }
                return activeLog is { LatestPollRead: true } current
                    ? new MenuObservation(current.State.Stage, current.State.WorldSinceS, current.State.MenuRevision, true)
                    : new MenuObservation(GameStage.Starting, double.NaN, 0, false);
            }
        });
    }

    private ServerInfo LocalHostTarget() => new() { Name = "(Hosting)", Ip = "127.0.0.1", Port = settings.HostPort, Ping = 0 };

    private Task LaunchJoinGame(ServerInfo server) => SelectLauncherRoleAsync(SessionRole.Join, server);

    private Task SelectHostForPendingGameAsync() => launchStage == LaunchStage.WaitingForConnect && roleGame is not null
        ? SelectLauncherRoleAsync(SessionRole.Host, LocalHostTarget()) : Task.CompletedTask;

    private async Task SelectLauncherRoleAsync(SessionRole role, ServerInfo server)
    {
        if (O4BlocksSession()) return;
        if (diagnosticMessageRevision == messageRevision || directRoleTarget is not null) DismissMessage();
        var plan = sessionRoles.Select(role, role == SessionRole.Host ? LocalHostTarget() : server)!;
        if (launchStage == LaunchStage.WaitingForConnect && roleGame is { } scope)
        {
            string status = await ChangeSessionRoleAsync(scope, plan, "launcher-menu");
            if (status.Length != 0) UiService.ShowError(status);
            return;
        }
        await LaunchGame(plan);
    }

    private bool RoleSessionCurrent(MenuGameScope scope) => ReferenceEquals(roleGame, scope)
        && ReferenceEquals(pendingGameProcess, scope.Game) && !scope.Game.HasExited;

    private bool SessionAgentActive(MenuGameScope scope) => ReferenceEquals(agentGameProcess, scope.Game)
        && agentProcess is { HasExited: false };

    private bool RoleSwitchAllowed(MenuGameScope scope, GameStage stage) => LauncherSessionRole.MenuSwitchAllowed(stage,
        launchStage == LaunchStage.WaitingForConnect && pendingDllPath is not null, RoleSessionCurrent(scope),
        SessionAgentActive(scope), scope.ActionInProgress || scope.LoadIssued || w159JoinState != "idle");

    private const string RoleBusyText = "Finish the current action or session before changing roles.";
    private const string RoleMenuText = "Change roles from the main menu before connecting or loading a world.";

    // The gate and destination change together on the Launcher dispatcher after the final current-game check.
    private async Task<string> ChangeSessionRoleAsync(MenuGameScope scope, SessionLaunchPlan plan, string source, long menuSeq = 0)
    {
        if (roleSwitchBusy)
        {
            Log.Information("MP-LAUNCH session role refused source={Source} reason=role-change-busy", source);
            return RoleBusyText;
        }
        roleSwitchBusy = true;
        StateHasChanged();
        try
        {
            if (!RoleSwitchAllowed(scope, await scope.ReadStageAsync()))
            {
                Log.Information("MP-LAUNCH session role refused source={Source} reason=outside-idle-menu", source);
                return RoleMenuText;
            }
            if (plan.Hosting && (!await EnsureHostedRelayAsync() || !await IsServerReachableAsync(plan.Server)))
            {
                Log.Warning("MP-LAUNCH session role refused source={Source} reason=host-relay-not-ready", source);
                return hostErrorMessage.Length == 0 ? "The local relay is unavailable. Open Host Game in the launcher for details." : hostErrorMessage;
            }
            if (!RoleSwitchAllowed(scope, await scope.ReadStageAsync()))
            {
                Log.Information("MP-LAUNCH session role refused source={Source} reason=menu-left-during-preparation", source);
                return RoleMenuText;
            }
            SetSessionPlan(scope, plan, source, menuSeq);
            launchStatusMessage = RoleLaunchStatus(plan.Hosting);
            return "";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "MP-LAUNCH session role refused source={Source} reason=preparation-failed", source);
            return "The role could not be prepared. Check the launcher and try again.";
        }
        finally
        {
            roleSwitchBusy = false;
            StateHasChanged();
        }
    }

    private async Task HandleMenuRoleAsync(MenuGameScope scope, MenuTakeoverRule.MenuChoice choice)
    {
        SessionRole requested = choice.Name == "host" ? SessionRole.Host : SessionRole.Join;
        var plan = sessionRoles.Select(requested, requested == SessionRole.Host ? LocalHostTarget() : null);
        string status;
        if (plan is not null)
        {
            status = await ChangeSessionRoleAsync(scope, plan, "menu", choice.Seq);
        }
        else if (!roleSwitchBusy && RoleSwitchAllowed(scope, await scope.ReadStageAsync()))
        {
            OpenJoinRole();
            status = "Choose a host address or Steam code in the launcher.";
            Log.Information("MP-LAUNCH session role awaits a join destination menuSeq={Seq}", choice.Seq);
        }
        else
        {
            status = RoleMenuText;
            Log.Information("MP-LAUNCH session role refused source=menu reason=outside-idle-menu menuSeq={Seq}", choice.Seq);
        }
        scope.RoleAck = choice.Seq;
        scope.RoleStatus = status;
    }

    private void SetSessionPlan(MenuGameScope scope, SessionLaunchPlan plan, string source, long menuSeq = 0)
    {
        if (diagnosticMessageRevision == messageRevision || directRoleTarget is not null) DismissMessage();
        pendingPlan = plan;
        if (!plan.Hosting) sessionRoles.RememberJoinTarget(plan.Server);
        w159JoinChoice = "";
        w159JoinState = "idle";
        scope.RoleStatus = "";
        StartConnectGate(plan, scope);
        Log.Information("MP-LAUNCH session role selected ({Role}) source={Source} gamePid={Pid} menuSeq={Seq}",
            plan.Hosting ? "host" : "join", source, scope.Game.Id, menuSeq);
    }

    private string RoleLaunchStatus(bool hosting) => hosting
        ? (w159Takeover ? "Press Start Game on the game's main menu. The launcher connects once your world has loaded."
                        : "Load into your save, then click CONNECT once you can see and move your character.")
        : (w159Takeover ? "Press Join Game on the game's main menu. You join as soon as your host is ready." : JoinerReadyText);

    private void OpenJoinRole()
    {
        if (roleSwitchBusy || launchStage is LaunchStage.Injecting or LaunchStage.Verifying or LaunchStage.Connected)
        {
            UiService.ShowError(RoleBusyText);
            return;
        }
        var remembered = sessionRoles.JoinTarget;
        joinRoleAddress = remembered is { SteamCode: null }
            ? (remembered.Ip.Contains(':') ? $"[{remembered.Ip}]:{remembered.Port}" : $"{remembered.Ip}:{remembered.Port}") : "";
        joinRoleError = "";
        showJoinRole = true;
        StateHasChanged();
    }

    private async Task JoinRoleThroughSteamAsync()
    {
        showJoinRole = false;
        await OpenJoinSteam();
    }

    private async Task ConfirmJoinRoleAsync()
    {
        if (!TryJoinAddress(joinRoleAddress, out var server))
        {
            joinRoleError = "Enter the host's address in the form host.example.net:7778.";
            return;
        }
        showJoinRole = false;
        await LaunchJoinGame(server);
    }

    private bool TryJoinAddress(string address, out ServerInfo server)
    {
        string host = address.Trim();
        int port = settings.HostPort;
        if (host.StartsWith('[') && host.IndexOf(']') is int end && end > 1)
        {
            string suffix = host[(end + 1)..];
            if (suffix.Length > 0 && (!suffix.StartsWith(':') || !TryPort(suffix[1..], out port))) port = 0;
            host = host[1..end];
        }
        else if (host.IndexOf(':') is int colon && colon > 0 && colon == host.LastIndexOf(':'))
        {
            if (!TryPort(host[(colon + 1)..], out port)) port = 0;
            host = host[..colon];
        }
        server = new() { Name = "(Address)", Ip = host, Port = port };
        return NetService.ValidateServerAddress(host, showError: false) && port is > 0 and <= 65535;

        static bool TryPort(string value, out int port) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port);
    }

    private async Task HostFromDirectTestAsync()
    {
        if (!showMessage || directRoleTarget is null) return;
        DismissMessage();
        if (launchStage == LaunchStage.WaitingForConnect) await SelectHostForPendingGameAsync();
        else await OpenHostModal();
    }

    private async Task JoinFromDirectTestAsync()
    {
        if (!showMessage || directRoleTarget is not { } target) return;
        DismissMessage();
        await LaunchJoinGame(target);
    }

    private void CloseLaunchPanel()
    {
        if (launchStage == LaunchStage.Connected) showConnectedPanel = false;
        else if (launchStage == LaunchStage.Failed) ResetLaunchState();
    }
}
