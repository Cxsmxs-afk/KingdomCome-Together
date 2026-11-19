// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;

namespace KcdMp.Client.Tests;

/// <summary>Engine boundary for the networked join test. The real agent sends every console request and parses
/// every resulting event/engine line via the shipped transport.</summary>
internal sealed class JoinCalendarTestEngine : IAsyncDisposable
{
    private delegate void EngineLine(ReadOnlySpan<char> line);

    private readonly string _saves;
    private readonly Func<int, byte[]>? _hostSave;
    private readonly Action _gameplayStarted;
    private readonly EngineLine _feed;
    private readonly object _logGate = new();
    private readonly TaskCompletionSource<string> _loadIssued = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string> _resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _eventSequence;
    private int _saveIndex;
    private string? _lastLoaded;

    public JoinCalendarTestEngine(string logPath, string saves, Func<int, byte[]>? hostSave, Action gameplayStarted)
    {
        _saves = saves;
        _hostSave = hostSave;
        _gameplayStarted = gameplayStarted;
        var http = new HttpGameTransport("http://localhost:1403", 2000, new ConsoleHandler(this)) { BatchingEnabled = false };
        Transport = new LogTailGameTransport(http, logPath);
        var process = typeof(LogTailGameTransport).GetMethod("ProcessLine", BindingFlags.NonPublic | BindingFlags.Instance)!;
        _feed = (EngineLine)process.CreateDelegate(typeof(EngineLine), Transport);
    }

    public LogTailGameTransport Transport { get; }
    public Action<string>? SaveWritten { get; set; }
    public Task<string> LoadIssued => _loadIssued.Task;
    public Task<string> Resumed => _resumed.Task;

    public void Feed(string line)
    {
        lock (_logGate) _feed(line.AsSpan());
    }

    public void Emit(string name, string args)
    {
        lock (_logGate) _feed($"[KCD2-MP-EVT] v1 {++_eventSequence} {name} {args}".AsSpan());
    }

    private void ConsoleCommand(string command)
    {
        var pause = Regex.Match(command, """KCD2MP_JoinTry\("([0-9a-f]{8})",""");
        if (pause.Success) Emit("join_try", $"{pause.Groups[1].Value} paused 0");

        if (_hostSave is not null && command.Contains("""KCD2MP_HostWorldSave("join",""", StringComparison.Ordinal))
        {
            int index = Interlocked.Increment(ref _saveIndex);
            string path = Path.Combine(_saves, "playline0", FormattableString.Invariant($"save{index:D3}.whs"));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, _hostSave(index));
            SaveWritten?.Invoke(path);
        }

        var load = Regex.Match(command, """KCD2MP_Wo124LoadGame\(([0-4]),"(mpworld[0-9a-f]{8})","join-command"\)""");
        if (load.Success)
        {
            string name = load.Groups[2].Value;
            string path = Path.Combine(_saves, $"playline{load.Groups[1].Value}", name + ".whs");
            Assert.True(File.Exists(path), $"The agent asked the engine to load a missing save: {name}.");
            _lastLoaded = name;
            _loadIssued.TrySetResult(path);
            Feed($"Loading saved game '{path}'");
            Feed($"[CryAction] LoadGame: '{path}'");
            Feed("Sending concept notification 'EntityModuleOnPostLoadGame'");
            _gameplayStarted();
            Feed("Gameplay started");
        }

        var query = Regex.Match(command, """(KCD2MP_Wo125LastLoaded|KCD2MP_Wo124Lock|KCD2MP_Wo124Henry)\("([0-9a-f]{8})"\)""");
        if (query.Success)
        {
            string reply = query.Groups[1].Value switch
            {
                "KCD2MP_Wo125LastLoaded" => "last=" + _lastLoaded,
                "KCD2MP_Wo124Lock" => "lock=held",
                // WhsSaveTests.Pair's guest has 151 saved money units and six apples. The quest letter is stripped.
                _ => "money=15.10 items=0a0a0a0a-0000-0000-0000-00000000a001:6"
            };
            Emit("wo124_reply", $"{query.Groups[2].Value} {reply}");
        }

        var resume = Regex.Match(command, """KCD2MP_JoinResume\("[0-9a-f]{8}",\s*"([^"]+)"\)""");
        if (resume.Success) _resumed.TrySetResult(resume.Groups[1].Value);
    }

    public ValueTask DisposeAsync() => Transport.DisposeAsync();

    private sealed class ConsoleHandler(JoinCalendarTestEngine engine) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (request.RequestUri is { } uri)
                engine.ConsoleCommand(Uri.UnescapeDataString(uri.Query));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        }
    }
}
