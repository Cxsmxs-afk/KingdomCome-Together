// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KcdMp.Wire;

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

public enum AgentStop { AlreadyExited, Graceful, Forced }

public sealed class AgentRecoveryRefused(string reason) : InvalidOperationException(reason);

public sealed class SessionAgentRun(Process game, Process agent, SessionAgentLaunch launch, LauncherAgentControl? control = null)
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);
    public Process Game { get; } = game;
    public Process Agent { get; } = agent;
    public SessionAgentLaunch Launch { get; } = launch;
    public int AgentPid { get; } = agent.Id;
    public long AgentStartTime { get; } = ReadStart(agent);
    public bool StopControlPrepared => control is not null;

    public static SessionAgentRun Start(Process game, SessionAgentLaunch launch, Func<ProcessStartInfo, Process?> start)
    {
        var info = launch.StartInfo();
        LauncherAgentControl? gate;
        try { gate = LauncherAgentControl.Create(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { gate = null; }
        if (gate is not null)
        {
            info.RedirectStandardInput = true;
            info.Environment[LauncherAgentControl.EnvironmentKey] = gate.Id;
        }
        else info.Environment.Remove(LauncherAgentControl.EnvironmentKey);
        try
        {
            return new(game, start(info) ?? throw new InvalidOperationException("The session agent did not start."), launch, gate);
        }
        catch
        {
            gate?.Remove();
            throw;
        }
    }

    private string? ScopeBlock(Process game, SessionAgentRun? current) =>
        !ReferenceEquals(Game, game) || !ReferenceEquals(this, current) || Game.HasExited
            ? "game-or-agent-changed" : null;

    public string? ReconnectBlock(Process game, SessionAgentRun? current)
    {
        if (ScopeBlock(game, current) is { } reason) return reason;
        return Agent.HasExited ? null : "agent-still-running";
    }

    public async Task<AgentStop> StopForRestartAsync(Process game, Func<SessionAgentRun?> current, TimeSpan? gracefulWait = null)
    {
        CheckScope();
        if (Agent.HasExited) return AgentStop.AlreadyExited;
        if (control is null || !TrackedIdentityCurrent()) throw new AgentRecoveryRefused("agent-identity-unconfirmed");
        using var stopping = control.TryBeginStop(AgentPid, AgentStartTime, out string reason);
        if (Agent.HasExited) return AgentStop.AlreadyExited;
        if (stopping is null) throw new AgentRecoveryRefused(reason);

        CheckScope();
        using (var deadline = new CancellationTokenSource(gracefulWait ?? StopTimeout))
        {
            try
            {
                await Agent.StandardInput.WriteLineAsync(LauncherAgentControl.StopCommand.AsMemory(), deadline.Token);
                await Agent.StandardInput.FlushAsync(deadline.Token);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException) { }
            try
            {
                await Agent.WaitForExitAsync(deadline.Token);
                return AgentStop.Graceful;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        }

        CheckScope();
        if (Agent.HasExited) return AgentStop.Graceful;
        if (!TrackedIdentityCurrent()) throw new AgentRecoveryRefused("agent-identity-unconfirmed");
        // Process.Start gave us the original process handle. No lookup by name or
        // PID, and a process tree would include children the launcher doesn't own.
        try { Agent.Kill(entireProcessTree: false); }
        catch (InvalidOperationException) when (Agent.HasExited) { return AgentStop.Graceful; }
        using var forcedDeadline = new CancellationTokenSource(StopTimeout);
        await Agent.WaitForExitAsync(forcedDeadline.Token);
        return AgentStop.Forced;

        void CheckScope()
        {
            if (ScopeBlock(game, current()) is { } blocked) throw new AgentRecoveryRefused(blocked);
        }
    }

    private bool TrackedIdentityCurrent() =>
        AgentStartTime > 0 && Agent.Id == AgentPid && ReadStart(Agent) == AgentStartTime;

    private static long ReadStart(Process process)
    {
        try
        {
            // Keep the native handle attached to the launcher's Process object on
            // Windows. It keeps identifying the original process across PID reuse.
            if (OperatingSystem.IsWindows()) _ = process.SafeHandle;
            return LauncherAgentControl.ReadProcessStart(process);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception or NotSupportedException) { return 0; }
    }

    public SessionAgentRun Restart(Process game, SessionAgentRun? current, Func<ProcessStartInfo, Process?> start)
    {
        if (ReconnectBlock(game, current) is { } reason) throw new InvalidOperationException(reason);
        return Start(Game, Launch, start);
    }

    public void DisposeExited()
    {
        if (!Agent.HasExited) return;
        Agent.Dispose();
        control?.Remove();
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
