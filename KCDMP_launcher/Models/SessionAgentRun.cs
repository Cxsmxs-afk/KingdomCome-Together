// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Diagnostics;
using System.IO;

namespace KCDMP_launcher.Models;

public sealed record SessionAgentLaunch(string Executable, string Arguments, bool Hosting, bool Steam, int StatusPort)
{
    public ProcessStartInfo StartInfo() => new()
    {
        FileName = Executable,
        Arguments = Arguments,
        UseShellExecute = false,
        WorkingDirectory = Path.GetDirectoryName(Executable)
    };
}

public sealed record SessionAgentRun(Process Game, Process Agent, SessionAgentLaunch Launch)
{
    public string? ReconnectBlock(Process game, SessionAgentRun? current)
    {
        if (!ReferenceEquals(Game, game) || !ReferenceEquals(this, current) || Game.HasExited)
            return "game-or-agent-changed";
        return Agent.HasExited ? null : "agent-still-running";
    }

    public SessionAgentRun Restart(Process game, SessionAgentRun? current, Func<ProcessStartInfo, Process?> start)
    {
        if (ReconnectBlock(game, current) is { } reason) throw new InvalidOperationException(reason);
        return new(Game, start(Launch.StartInfo()) ?? throw new InvalidOperationException("The session agent did not start."), Launch);
    }
}

public enum PluginReadiness { Waiting, Ready, Failed }

public static class PluginReadinessLog
{
    private const string Attach = "KCDMP.dll attached, pid=";

    public static PluginReadiness Inspect(string log, int gamePid)
    {
        int attach = log.LastIndexOf(Attach, StringComparison.Ordinal);
        ReadOnlySpan<char> run = attach < 0 ? default : log.AsSpan(attach);
        if (!run.StartsWith($"{Attach}{gamePid} build=", StringComparison.Ordinal)) return PluginReadiness.Waiting;
        if (run.Contains("MAIN: tick never fired after ", StringComparison.Ordinal) ||
            run.Contains("MAIN: no tick -- refusing", StringComparison.Ordinal) ||
            run.Contains("MAIN: walk timed out waiting for a frame; not starting the pipe", StringComparison.Ordinal) ||
            run.Contains("PIPE: listener stopped", StringComparison.Ordinal)) return PluginReadiness.Failed;
        return run.Contains(" -- tick is live", StringComparison.Ordinal) && run.Contains("PIPE: listening on ", StringComparison.Ordinal)
            ? PluginReadiness.Ready : PluginReadiness.Waiting;
    }
}
