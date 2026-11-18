// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace KcdMp.Relay.Tests;

public sealed class T4BotGateTests
{
    // Build-Installer runs Relay.Tests before publishing any of these projects.
    private static readonly string[] ShippedProjects =
    [
        "KCDMP_launcher/KCDMP_launcher.csproj", "dotnet/KcdMp.Client/KcdMp.Client.csproj",
        "dotnet/KcdMp.Server/KcdMp.Server.csproj", "dotnet/KcdMp.MasterServer/KcdMp.MasterServer.csproj",
        "dotnet/KcdMp.SetupHost/KcdMp.SetupHost.csproj"
    ];

    private static readonly string[] PackagingInputs =
    [
        "Directory.Build.props", "tools/Build-Installer.ps1", "tools/Publish-Release.ps1",
        "tools/Package-DropIn.ps1", "tools/Build-DirectInstall.ps1"
    ];

    private static readonly Regex BotSurface = new(
        @"KCDMP_TEST_BOT|--test-bot|bot-joiner|bot-host|[/\\]bothost\b|(?:AvatarPeer|SynthPeer)\.(?:csproj|dll|exe)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".json", ".config", ".xml", ".ini", ".txt", ".ps1", ".cmd", ".bat", ".iss",
        ".lua", ".cpp", ".h", ".py", ".props", ".targets", ".razor", ".xaml", ".manifest", ".yml", ".yaml"
    };

    [Fact]
    public void Build_and_shipping_inputs_have_no_bot_entry_or_activation_flags()
    {
        string root = T4BotTools.Root;
        var projects = new Queue<string>(ShippedProjects.Select(file => Path.Combine(root, file)));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inputs = new HashSet<string>(PackagingInputs.Select(file => Path.Combine(root, file)), StringComparer.OrdinalIgnoreCase);
        foreach (string folder in new[] { "installer", "native/KCDMP", "kdcmp" })
            inputs.UnionWith(TextUnder(Path.Combine(root, folder)));

        while (projects.TryDequeue(out string? project) && project is not null)
        {
            if (!seen.Add(project)) continue;
            string dir = Path.GetDirectoryName(project)!;
            Assert.False(Path.GetRelativePath(root, project).StartsWith($"tools{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase),
                $"A shipped project links a bench tool: {Path.GetRelativePath(root, project)}");
            inputs.UnionWith(TextUnder(dir));
            foreach (var item in XDocument.Load(project).Descendants("ProjectReference"))
            {
                string include = ((string?)item.Attribute("Include") ?? "").Replace('\\', Path.DirectorySeparatorChar);
                if (include.Length > 0) projects.Enqueue(Path.GetFullPath(Path.Combine(dir, include)));
            }
        }

        var findings = inputs.Order().SelectMany(file => BotSurface.Matches(File.ReadAllText(file))
            .Select(match => $"{Path.GetRelativePath(root, file)}: {match.Value}")).ToArray();
        Assert.Empty(findings);
    }

    [Theory]
    [InlineData("{\"activation\":\"KCDMP_TEST_BOT\"}")]
    [InlineData("Start-Process KcdMpClient.exe '--test-bot'")]
    [InlineData("<ProjectReference Include='../../tools/wo121/avatarpeer/AvatarPeer.csproj' />")]
    [InlineData("Copy-Item tools\\bothost\\bot-joiner $payload")]
    public void Source_gate_detects_a_planted_bot_setting_launch_or_project(string text) => Assert.Matches(BotSurface, text);

    [Theory]
    [InlineData("AvatarPeer", "--self-test")]
    [InlineData("SynthPeer", "--bot-plan")]
    public async Task Bot_tool_requires_both_explicit_switches(string tool, string botOnlyOption)
    {
        foreach (var (enableEnvironment, enableFlag) in new[] { (false, true), (true, false) })
        {
            var args = new List<string>();
            if (enableFlag) args.Add("--test-bot");
            args.Add(botOnlyOption);
            if (tool == "SynthPeer") args.Add("unused-plan.txt");
            await using var bot = T4BotTools.Start(tool, enableEnvironment, args.ToArray());
            var result = await bot.WaitAsync();
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(enableFlag ? T4BotTools.EnvironmentFlag : "--test-bot", result.Output, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Joiner_packet_and_pacing_self_test_runs_when_explicitly_enabled()
    {
        await using var bot = T4BotTools.Start("AvatarPeer", true, "--test-bot", "--self-test");
        var result = await bot.WaitAsync();
        Assert.True(result.ExitCode == 0, result.Output);
    }

    private static IEnumerable<string> TextUnder(string folder)
    {
        foreach (string file in Directory.EnumerateFiles(folder))
            if (TextExtensions.Contains(Path.GetExtension(file))) yield return file;
        foreach (string child in Directory.EnumerateDirectories(folder))
        {
            string name = Path.GetFileName(child);
            if (name is "bin" or "obj" or "build" || File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint)) continue;
            foreach (string file in TextUnder(child)) yield return file;
        }
    }
}

internal sealed class T4BotTools : IAsyncDisposable
{
    public const string EnvironmentFlag = "KCDMP_TEST_BOT";
    public static string Root { get; } = FindRoot();
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(25);
    private readonly Process _process;
    private readonly Task<string> _stdout;
    private readonly Task<string> _stderr;
    public bool HasExited => _process.HasExited;

    private T4BotTools(Process process)
    {
        _process = process;
        _stdout = process.StandardOutput.ReadToEndAsync();
        _stderr = process.StandardError.ReadToEndAsync();
    }

    public static T4BotTools Start(string tool, bool enabled, params string[] args)
    {
        string folder = tool == "AvatarPeer" ? "tools/wo121/avatarpeer" : "tools/wo118/synthpeer";
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string dll = Path.Combine(Root, folder, "bin", configuration, "net8.0", tool + ".dll");
        Assert.True(File.Exists(dll), $"T4 test executable was not built: {folder}/bin/{configuration}/net8.0/{tool}.dll");
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.Environment.Remove(EnvironmentFlag);
        if (enabled) info.Environment[EnvironmentFlag] = "1";
        info.ArgumentList.Add(dll);
        foreach (string arg in args) info.ArgumentList.Add(arg);
        return new(Process.Start(info) ?? throw new InvalidOperationException($"Cannot start {tool}"));
    }

    public async Task<(int ExitCode, string Output)> WaitAsync()
    {
        await _process.WaitForExitAsync().WaitAsync(Limit);
        return (_process.ExitCode, (await _stdout) + (await _stderr));
    }

    public async ValueTask DisposeAsync()
    {
        using (_process)
        {
            if (!_process.HasExited)
            {
                try { _process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (_process.HasExited) { }
                await _process.WaitForExitAsync().WaitAsync(Limit);
            }
            await Task.WhenAll(_stdout, _stderr).WaitAsync(Limit);
        }
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "tools", "Publish-Release.ps1"))) return dir.FullName;
        throw new DirectoryNotFoundException("The Kingdom Come: Together checkout was not found.");
    }
}
