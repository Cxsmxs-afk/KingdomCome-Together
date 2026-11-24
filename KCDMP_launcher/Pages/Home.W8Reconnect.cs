// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using KCDMP_launcher.Models;
using Serilog;

namespace KCDMP_launcher.Pages;

public partial class Home
{
    private bool agentReconnectBusy;
    private bool AgentReconnectVisible => launchStage == LaunchStage.Connected && agentRun is not null;
    private string AgentReconnectLabel => settings.Language == "de" ? "Neu verbinden" : "Reconnect";

    private bool CurrentPluginConnect(Process game, SessionLaunchPlan plan) =>
        ReferenceEquals(game, pendingGameProcess) && ReferenceEquals(plan, pendingPlan) && !game.HasExited;

    private async Task RetryPluginConnectAsync()
    {
        if (launchStage != LaunchStage.PluginWaiting || pendingGameProcess is not { } game ||
            pendingPlan is not { } plan || pendingDllPath is not { } dllPath || nativeProofSince is not { } injectedAfter) return;
        try { await FinishPluginConnectAsync(game, plan, dllPath, injectedAfter); }
        catch (LaunchBlockedException ex)
        {
            if (!CurrentPluginConnect(game, plan)) return;
            ShowBlocked(ex.Block);
            PluginConnectWaiting("The plugin is loaded. Check the agent file, then try again.");
        }
        catch (Exception ex)
        {
            if (!CurrentPluginConnect(game, plan)) return;
            Log.Warning(ex, "MP-LAUNCH plugin verification or agent start failed");
            PluginConnectWaiting("Couldn't check or start multiplayer. Try again.");
        }
    }

    private void PluginConnectWaiting(string message)
    {
        launchStage = LaunchStage.PluginWaiting;
        launchStatusMessage = message;
        StateHasChanged();
    }

    private async Task FinishPluginConnectAsync(Process game, SessionLaunchPlan plan, string dllPath, DateTime injectedAfter)
    {
        if (!CurrentPluginConnect(game, plan)) return;
        launchStage = LaunchStage.Verifying;
        launchStatusMessage = "Waiting for the game’s multiplayer plugin to become ready…";
        StateHasChanged();

        // plugin_main first waits up to 60 s for CrySystem and then polls the
        // engine tick for up to 300 s. A launcher timeout leaves that DLL running.
        string logPath = Path.Combine(Path.GetDirectoryName(dllPath) ?? "", "kcdmp-native.log");
        var elapsed = Stopwatch.StartNew();
        while (CurrentPluginConnect(game, plan))
        {
            var ready = await ReadPluginReadinessAsync(logPath, game.Id, injectedAfter);
            if (!CurrentPluginConnect(game, plan)) return;
            if (ready == PluginReadiness.Failed)
            {
                Log.Warning("MP-LAUNCH plugin startup failed gamePid={Pid} reason=native-terminal", game.Id);
                launchStage = LaunchStage.Failed;
                launchStatusMessage = "The plugin reported a startup failure. Send the logs with Report a bug. A fresh game is required to load the plugin again.";
                StateHasChanged();
                return;
            }
            if (ready == PluginReadiness.Ready)
            {
                string? blocked = plan.Hosting ? await HostAgentStartBlockAsync(game) : null;
                if (!CurrentPluginConnect(game, plan)) return;
                if (blocked is not null)
                {
                    PluginConnectWaiting(blocked);
                    return;
                }
                StartAgent(plan, game);
                Log.Information("MP-LAUNCH plugin confirmed gamePid={Pid} agentPid={AgentPid}", game.Id, agentProcess!.Id);
                launchStage = LaunchStage.Connected;
                showConnectedPanel = true;
                launchStatusMessage = "The game is ready. The line at the bottom of this window shows the connection; you can close this.";
                StateHasChanged();
                return;
            }
            if (elapsed.Elapsed >= TimeSpan.FromSeconds(365))
            {
                Log.Information("MP-LAUNCH plugin readiness pending gamePid={Pid}", game.Id);
                PluginConnectWaiting("The plugin hasn’t reported that it’s ready yet. When the game has finished loading, check again.");
                return;
            }
            await Task.Delay(1000);
        }
    }

    private static async Task<PluginReadiness> ReadPluginReadinessAsync(string path, int pid, DateTime injectedAfter)
    {
        try
        {
            if (File.GetLastWriteTimeUtc(path) < injectedAfter) return PluginReadiness.Waiting;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
            using var reader = new StreamReader(file);
            return PluginReadinessLog.Inspect(await reader.ReadToEndAsync(), pid);
        }
        catch (IOException) { return PluginReadiness.Waiting; }
    }

    private async Task<string?> HostAgentStartBlockAsync(Process game)
    {
        if (hostedRelayProcess is not { HasExited: false } relay) return "The local relay is unavailable. Open Host Game for details.";
        if (roleGame is not { } scope || !ReferenceEquals(scope.Game, game)) return "This game could not be verified.";
        var observed = await scope.ObserveAsync();
        if (!RoleSessionCurrent(scope) || !ReferenceEquals(relay, hostedRelayProcess) || relay.HasExited)
            return "The game or its local relay changed. Try again.";
        double now = scope.Clock.Elapsed.TotalSeconds;
        var gate = ConnectGateRule.ForHost(observed.Stage, observed.WorldSinceS, observed.Reading, now, now);
        return gate.Open ? null : gate.Reason;
    }

    private void AgentReconnectRefused(string reason, string? message = null)
    {
        Log.Information("MP-LAUNCH agent reconnect refused reason={Reason}", reason);
        UiService.ShowError(message ?? (reason switch
        {
            "engine-read-or-control-unavailable" => "Wait for any load to finish. If you are still in the host’s world, choose Leave in the co-op menu. Reconnect once you are back in your own game or at the main menu.",
            "agent-identity-unconfirmed" => "The launcher could not verify the running multiplayer agent.",
            _ => "The current game or multiplayer agent changed. Try again."
        }));
    }

    private async Task ReconnectAgentAsync()
    {
        if (agentReconnectBusy || !AgentReconnectVisible || agentRun is not { } prior || pendingGameProcess is not { } game) return;
        agentReconnectBusy = true;
        StateHasChanged();
        try
        {
            string? hostBlock = prior.Launch.Hosting ? await HostAgentStartBlockAsync(game) : null;
            if (hostBlock is not null)
            {
                AgentReconnectRefused("host-gate", hostBlock);
                return;
            }
            var stop = await prior.StopForRestartAsync(game, () => ReferenceEquals(game, pendingGameProcess) ? agentRun : null);
            if (stop != AgentStop.AlreadyExited && prior.Launch.Hosting && await HostAgentStartBlockAsync(game) is { } afterStop)
            {
                AgentReconnectRefused("host-gate", afterStop);
                return;
            }
            if (!ReferenceEquals(game, pendingGameProcess) || prior.ReconnectBlock(game, agentRun) is not null)
            {
                AgentReconnectRefused("game-or-agent-changed");
                return;
            }
            TrackAgent(prior.Restart(game, agentRun, StartSessionAgent));
            Log.Information("MP-LAUNCH agent reconnected gamePid={GamePid} previousAgentPid={PreviousPid} agentPid={Pid} stop={Stop}",
                game.Id, prior.AgentPid, agentProcess!.Id, stop);
        }
        catch (AgentRecoveryRefused ex) { AgentReconnectRefused(ex.Message); }
        catch (LaunchBlockedException ex) { ShowBlocked(ex.Block); }
        catch (Exception ex)
        {
            Log.Warning(ex, "MP-LAUNCH agent reconnect failed");
            UiService.ShowError("The multiplayer agent couldn’t start. Try again.");
        }
        finally
        {
            agentReconnectBusy = false;
            StateHasChanged();
        }
    }
}
