// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using KCDMP_launcher.Models;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public sealed class SessionAgentReconnectTests
{
    private const int GamePid = 49;
    private const string Attach = "KCDMP.dll attached, pid=49 build=test\n";
    private const string Tick = "MAIN: 25 frames after ~1000 ms -- tick is live\n";
    private const string Pipe = "PIPE: listening on \\\\.\\pipe\\kcdmp\n";

    [Fact]
    public void Evidence_from_a_different_game_cannot_confirm_this_plugin()
    {
        Assert.Equal(PluginReadiness.Waiting, PluginReadinessLog.Inspect(Attach.Replace("49", "490") + Tick + Pipe, GamePid));
        Assert.Equal(PluginReadiness.Waiting, PluginReadinessLog.Inspect(Attach + Tick + Pipe + Attach.Replace("49", "90"), GamePid));
    }

    [Fact]
    public void Existing_plugin_can_complete_after_a_slow_engine_start()
    {
        string waiting = Attach + "MAIN: still waiting for the tick to start (30000 ms elapsed, 0 frames so far)\n";
        Assert.Equal(PluginReadiness.Waiting, PluginReadinessLog.Inspect(waiting, GamePid));
        Assert.Equal(PluginReadiness.Waiting, PluginReadinessLog.Inspect(waiting + Tick, GamePid));
        Assert.Equal(PluginReadiness.Ready, PluginReadinessLog.Inspect(waiting + Tick + Pipe, GamePid));
        Assert.Equal(PluginReadiness.Failed, PluginReadinessLog.Inspect(waiting + "MAIN: tick never fired after 300000 ms -- aborting before any game-state access\n", GamePid));
        Assert.Equal(PluginReadiness.Failed, PluginReadinessLog.Inspect(waiting + Tick + Pipe + "PIPE: listener stopped\n", GamePid));
    }

    [Fact]
    public async Task Reconnect_refuses_a_live_agent_or_an_untracked_process_even_with_the_same_pid()
    {
        using Process game = StartFixture(live: true);
        using Process agent = StartFixture(live: true);
        using Process unrelatedHandle = Process.GetProcessById(game.Id);
        using Process exited = StartFixture(live: false);
        try
        {
            await WaitAsync(exited);
            var rules = new SessionAgentLaunch("agent.exe", "--hosting", true, false, 5274);
            var current = new SessionAgentRun(game, agent, rules);
            var stale = new SessionAgentRun(game, exited, rules);
            Process? UnexpectedStart(ProcessStartInfo _) => throw new Xunit.Sdk.XunitException("A refused restart launched an agent.");

            Assert.Equal("agent-still-running", Assert.Throws<InvalidOperationException>(() => current.Restart(game, current, UnexpectedStart)).Message);
            Assert.Equal("game-or-agent-changed", Assert.Throws<InvalidOperationException>(() => stale.Restart(unrelatedHandle, stale, UnexpectedStart)).Message);
            Assert.Equal("game-or-agent-changed", Assert.Throws<InvalidOperationException>(() => stale.Restart(game, current, UnexpectedStart)).Message);
            var noGame = new SessionAgentRun(exited, exited, rules);
            Assert.Equal("game-or-agent-changed", Assert.Throws<InvalidOperationException>(() => noGame.Restart(exited, noGame, UnexpectedStart)).Message);
            Assert.False(game.HasExited);
            Assert.False(agent.HasExited);
        }
        finally
        {
            StopFixture(agent);
            StopFixture(game);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reconnect_stops_the_tracked_live_agent_before_launching_its_original_command(bool hangs)
    {
        using Process game = StartFixture(live: true);
        using Process agent = StartControlledFixture(hangs);
        using Process unrelated = StartFixture(live: true);
        var control = LauncherAgentControl.Create();
        try
        {
            var launch = new SessionAgentLaunch("agent.exe", "--host 127.0.0.1 --port 7778 --hosting", true, false, 5274);
            var run = new SessionAgentRun(game, agent, launch, control);
            Assert.True(control.StampProcess(run.AgentPid, run.AgentStartTime));
            var stopped = await run.StopForRestartAsync(game, () => run, hangs ? TimeSpan.FromMilliseconds(100) : null);
            Assert.Equal(hangs ? AgentStop.Forced : AgentStop.Graceful, stopped);
            if (!hangs) Assert.Equal(0, agent.ExitCode);
            var replacement = run.Restart(game, run, info =>
            {
                Assert.True(agent.HasExited);
                Assert.Equal(launch.Arguments, info.Arguments);
                Assert.True(info.RedirectStandardInput);
                Assert.NotNull(LauncherAgentControl.FromId(info.Environment[LauncherAgentControl.EnvironmentKey]!));
                return StartFixture(live: false);
            });
            await WaitAsync(replacement.Agent);
            replacement.DisposeExited();
            Assert.Same(game, replacement.Game);
            Assert.False(game.HasExited);
            Assert.False(unrelated.HasExited);
        }
        finally
        {
            control.Remove();
            StopFixture(unrelated);
            StopFixture(agent);
            StopFixture(game);
        }
    }

    [Fact]
    public async Task Same_pid_with_a_different_start_time_and_a_live_engine_pin_each_block_the_stop()
    {
        using Process game = StartFixture(live: true);
        using Process otherIncarnation = StartControlledFixture(hangs: true);
        using Process pinned = StartControlledFixture(hangs: true);
        var staleControl = LauncherAgentControl.Create();
        var pinnedControl = LauncherAgentControl.Create();
        try
        {
            var launch = new SessionAgentLaunch("agent.exe", "--host 127.0.0.1", false, false, 5274);
            var stale = new SessionAgentRun(game, otherIncarnation, launch, staleControl);
            Assert.True(staleControl.StampProcess(otherIncarnation.Id, stale.AgentStartTime + 1));
            Assert.Equal("agent-identity-unconfirmed", (await Assert.ThrowsAsync<AgentRecoveryRefused>(
                () => stale.StopForRestartAsync(game, () => stale))).Message);
            Assert.False(otherIncarnation.HasExited);

            var held = new SessionAgentRun(game, pinned, launch, pinnedControl);
            Assert.True(pinnedControl.StampProcess(pinned.Id, held.AgentStartTime));
            using (var lease = Assert.IsType<FileStream>(pinnedControl.TryProtectEngineRead()))
            {
                Assert.Equal("engine-read-or-control-unavailable", (await Assert.ThrowsAsync<AgentRecoveryRefused>(
                    () => held.StopForRestartAsync(game, () => held))).Message);
                Assert.False(pinned.HasExited);
            }
            Assert.Equal(AgentStop.Forced, await held.StopForRestartAsync(game, () => held, TimeSpan.FromMilliseconds(100)));
            Assert.True(pinned.HasExited);
            Assert.False(game.HasExited);
        }
        finally
        {
            staleControl.Remove();
            pinnedControl.Remove();
            StopFixture(pinned);
            StopFixture(otherIncarnation);
            StopFixture(game);
        }
    }

    [Theory]
    [InlineData("--host 127.0.0.1 --port 7778 --hosting", true, false)]
    [InlineData("--steam sample --steam-app 123", false, true)]
    public async Task Five_restarts_reuse_the_exact_game_and_original_agent_launch(string arguments, bool host, bool steam)
    {
        using Process game = StartFixture(live: true);
        int gamePid = game.Id;
        Process agent = StartFixture(live: false);
        var settings = new SessionAgentLaunch(Path.Combine(Path.GetTempPath(), "agent.exe"), arguments, host, steam, 5274);
        var run = new SessionAgentRun(game, agent, settings);
        try
        {
            for (int cycle = 0; cycle < 5; ++cycle)
            {
                await WaitAsync(agent);
                var previous = run;
                run = previous.Restart(game, previous, startInfo =>
                {
                    Assert.Equal(settings.Executable, startInfo.FileName);
                    Assert.Equal(arguments, startInfo.Arguments);
                    Assert.Equal(Path.GetDirectoryName(settings.Executable), startInfo.WorkingDirectory);
                    Assert.False(startInfo.UseShellExecute);
                    return StartFixture(live: false);
                });
                previous.DisposeExited();
                agent = run.Agent;
                Assert.Same(game, run.Game);
                Assert.Same(settings, run.Launch);
                Assert.Equal(gamePid, run.Game.Id);
                Assert.False(run.Game.HasExited);
            }
            await WaitAsync(agent);
        }
        finally
        {
            if (agent.HasExited) run.DisposeExited();
            else { StopFixture(agent); agent.Dispose(); }
            StopFixture(game);
        }
    }

    private static Process StartFixture(bool live)
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", live ? "/d /c ping -n 30 127.0.0.1 > nul" : "/d /c exit 0")
            : new ProcessStartInfo("/bin/sh", live ? "-c \"exec sleep 30\"" : "-c \"exit 0\"");
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        return Process.Start(start)!;
    }

    private static Process StartControlledFixture(bool hangs)
    {
        bool windows = OperatingSystem.IsWindows();
        var start = new ProcessStartInfo(windows ? "powershell.exe" : "/bin/sh")
        {
            RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true
        };
        if (windows)
        {
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
        }
        start.ArgumentList.Add(windows ? "-Command" : "-c");
        start.ArgumentList.Add((windows, hangs) switch
        {
            (true, true) => "Start-Sleep -Seconds 30",
            (false, true) => "exec sleep 30",
            (true, false) => $"if ([Console]::ReadLine() -ceq '{LauncherAgentControl.StopCommand}') {{ exit 0 }} else {{ exit 1 }}",
            _ => $"IFS= read -r command; test \"$command\" = '{LauncherAgentControl.StopCommand}'"
        });
        return Process.Start(start)!;
    }

    private static Task WaitAsync(Process process) => process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

    private static void StopFixture(Process process)
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
    }
}
