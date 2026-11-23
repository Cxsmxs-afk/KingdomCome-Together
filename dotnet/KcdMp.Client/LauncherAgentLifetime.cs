// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using KcdMp.Wire;

namespace KcdMp.Client;

internal static class LauncherAgentLifetime
{
    public static LauncherAgentControl? Attach()
    {
        if (Environment.GetEnvironmentVariable(LauncherAgentControl.EnvironmentKey) is not { } id) return null;
        try
        {
            using var current = Process.GetCurrentProcess();
            if (Console.IsInputRedirected && LauncherAgentControl.FromId(id) is { } control &&
                control.StampProcess(current.Id, LauncherAgentControl.ReadProcessStart(current))) return control;
            Console.WriteLine("MP-LAUNCH agent restart unavailable reason=unconfirmed-launcher-control");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.WriteLine($"MP-LAUNCH agent restart unavailable reason={ex.GetType().Name}");
        }
        return null;
    }

    public static async Task ListenForStopAsync(CancellationTokenSource lifetime)
    {
        try
        {
            using var input = new StreamReader(Console.OpenStandardInput());
            // EOF means the launcher closed. The session agent may continue.
            if (await input.ReadLineAsync(lifetime.Token) == LauncherAgentControl.StopCommand)
            {
                Console.WriteLine("MP-LAUNCH agent stop requested by launcher");
                lifetime.Cancel();
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }
}
