// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System;
using System.Threading.Tasks;

namespace KCDMP_launcher.Models;

public enum SessionRole { Host, Join }

public sealed record SessionLaunchPlan(SessionRole Role, ServerInfo Server)
{
    public bool Hosting => Role == SessionRole.Host;
    public string HostClaimArgument => Hosting ? " --hosting" : "";
}

public sealed class LauncherSessionRole
{
    private ServerInfo? _joinTarget;

    public ServerInfo? JoinTarget => _joinTarget is { } server ? CopyTarget(server) : null;

    // A direct diagnostic can remember a destination without selecting a session role.
    public void RememberJoinTarget(ServerInfo server) => _joinTarget = CopyTarget(server);

    public SessionLaunchPlan? Select(SessionRole role, ServerInfo? server = null)
    {
        ServerInfo? target = server ?? (role == SessionRole.Join ? _joinTarget : null);
        return target is null ? null : new(role, CopyTarget(target));
    }

    public static bool MenuSwitchAllowed(GameStage stage, bool launchWaiting, bool currentGame, bool agentActive, bool menuActionStarted) =>
        stage == GameStage.Menu && launchWaiting && currentGame && !agentActive && !menuActionStarted;

    public static bool MenuActionAllowed(SessionRole role, string kind) => role switch
    {
        SessionRole.Host => kind is "load" or "newadv",
        SessionRole.Join => kind == "join",
        _ => false
    };

    public static async Task<bool> DispatchMenuActionAsync(SessionRole role, GameStage stage, MenuTakeoverRule.MenuChoice choice,
        Func<MenuTakeoverRule.MenuChoice, Task> hostAction, Func<MenuTakeoverRule.MenuChoice, Task> joinAction)
    {
        if (stage != GameStage.Menu || !MenuActionAllowed(role, choice.Kind)) return false;
        if (role == SessionRole.Host) await hostAction(choice);
        else await joinAction(choice);
        return true;
    }

    private static ServerInfo CopyTarget(ServerInfo server) => new()
    {
        Name = server.Name, Ip = server.Ip, Port = server.Port, InfoPort = server.InfoPort, Ping = server.Ping, SteamCode = server.SteamCode
    };
}
