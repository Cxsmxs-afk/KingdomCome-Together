// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Channels;

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
    private readonly Queue<string> _pendingJoinReads = new();
    private readonly Queue<string> _pendingOwnReads = new();
    private readonly Queue<string> _pendingLockReplies = new();
    private readonly Channel<string> _joinLoads = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _ownLoads = Channel.CreateUnbounded<string>();
    private readonly TaskCompletionSource _exitMenu = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string> _exitLoad = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<string> _saveLockQueries = Channel.CreateUnbounded<string>();
    private readonly TaskCompletionSource<string> _loadIssued = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string> _resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<bool> _joinedNotifications = Channel.CreateUnbounded<bool>();
    private long _eventSequence;
    private int _saveIndex;
    private string _where = "menu";
    private string? _lastLoaded, _lastLoadedPath;
    public volatile string? LastLoadedPathOverride;
    private int _joinLoadsIssued, _joinFilesRead, _ownLoadsIssued, _whereQueries;
    public int WhereQueries => Volatile.Read(ref _whereQueries);
    public volatile bool ReturnRetryVisible;
    private readonly Channel<string> _exitLoads = Channel.CreateUnbounded<string>();

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
    public Action? BeforeOwnLoadQuiet { get; set; }
    public Action<string>? RequestLegacySnapshot { get; set; }
    public bool DeferJoinFileRead { get; set; }
    public bool DeferOwnFileRead { get; set; }
    public bool DeferExitFileRead { get; set; }
    public bool FailExitResponse { get; set; }
    public bool DeferSaveLockReply { get; set; }
    public int JoinLoadsIssued => Volatile.Read(ref _joinLoadsIssued);
    public int JoinFilesRead => Volatile.Read(ref _joinFilesRead);
    public int OwnLoadsIssued => Volatile.Read(ref _ownLoadsIssued);
    public Task<string> LoadIssued => _loadIssued.Task;
    public Task<string> Resumed => _resumed.Task;
    public Task ExitMenu => _exitMenu.Task;
    public Task<string> ExitLoad => _exitLoad.Task;
    // The production toast is sent after the post-load owner has published the
    // Henry snapshot and chest ledger and finished cleaning the retained join file.
    public Task WaitForJoinedNotificationAsync(CancellationToken ct) => _joinedNotifications.Reader.ReadAsync(ct).AsTask();
    public Task<string> WaitForJoinLoadAsync(CancellationToken ct = default) => _joinLoads.Reader.ReadAsync(ct).AsTask();
    public Task<string> WaitForOwnLoadAsync(CancellationToken ct = default) => _ownLoads.Reader.ReadAsync(ct).AsTask();
    public Task<string> WaitForExitLoadAsync(CancellationToken ct = default) => _exitLoads.Reader.ReadAsync(ct).AsTask();
    public Task<string> WaitForSaveLockAsync(CancellationToken ct = default) => _saveLockQueries.Reader.ReadAsync(ct).AsTask();

    public void CompleteJoinLoad()
    {
        lock (_logGate) ReadLoadedFile(_pendingJoinReads.Dequeue(), joined: true);
    }

    public void CompleteOwnLoad()
    {
        lock (_logGate) ReadLoadedFile(_pendingOwnReads.Dequeue(), joined: false);
    }

    public void ReadOwnSaveWithoutTerminal()
    {
        lock (_logGate)
        {
            string path = _pendingOwnReads.Dequeue();
            Assert.NotEmpty(File.ReadAllBytes(path));
            Feed($"[CryAction] LoadGame: '{path}'");
            _lastLoaded = Path.GetFileNameWithoutExtension(path);
            _lastLoadedPath = path;
            _where = "world";
            _gameplayStarted();
        }
    }

    public void CompleteExitLoad(string path)
    {
        lock (_logGate)
        {
            Assert.True(File.Exists(path));
            Feed($"Loading saved game '{path}'");
            Feed($"[CryAction] LoadGame: '{path}'");
            Feed("Exiting to main menu because save game loading failed");
            Feed("PlayVideoOnly 'main_menu'");
            _where = "menu";
            _exitMenu.TrySetResult();
        }
    }

    public void ReleaseSaveLock()
    {
        lock (_logGate) Emit("wo124_reply", $"{_pendingLockReplies.Dequeue()} lock=held");
    }

    public void Feed(string line)
    {
        lock (_logGate) _feed(line.AsSpan());
    }

    public void Emit(string name, string args)
    {
        lock (_logGate)
        {
            if (name == "wo124_where") _where = args;
            _feed($"[KCD2-MP-EVT] v1 {++_eventSequence} {name} {args}".AsSpan());
        }
    }

    private void ConsoleCommand(string command)
    {
        var pause = Regex.Match(command, """KCD2MP_JoinTry\("([0-9a-f]{8})",""");
        if (pause.Success) Emit("join_try", $"{pause.Groups[1].Value} paused 0");

        if (_hostSave is { } hostSave &&
            command.Contains("""KCD2MP_HostWorldSave("join",""", StringComparison.Ordinal))
        {
            int index = Interlocked.Increment(ref _saveIndex);
            string path = Path.Combine(_saves, "playline0", FormattableString.Invariant($"save{index:D3}.whs"));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, hostSave(index));
            SaveWritten?.Invoke(path);
        }

        var load = Regex.Match(command, """KCD2MP_Wo124LoadGame\(([0-4]),"(mpworld[0-9a-f]{8})","join-command"\)""");
        if (load.Success) AcceptLoad(load, joined: true);
        var ownLoad = Regex.Match(command, """KCD2MP_Wo124LoadGame\(([0-4]),"([A-Za-z0-9_]+)","leave-command"\)""");
        if (ownLoad.Success) AcceptLoad(ownLoad, joined: false);
        var repair = Regex.Match(command, """KCD2MP_Wo124LoadGame\(([0-4]),"([A-Za-z0-9_]+)","w175-repaired"\)""");
        if (repair.Success) AcceptLoad(repair, joined: false);
        var exit = Regex.Match(command, """KCD2MP_Wo124LoadGame\(([0-4]),"(mpexit[0-9a-f]{8})","exit-to-menu"\)""");
        if (exit.Success)
        {
            string path = Path.Combine(_saves, $"playline{exit.Groups[1].Value}", exit.Groups[2].Value + ".whs");
            Assert.True(File.Exists(path));
            _exitLoad.TrySetResult(path);
            _exitLoads.Writer.TryWrite(path);
            if (!DeferExitFileRead) CompleteExitLoad(path);
        }

        var retry = Regex.Match(command, @"KCD2MP_W154MenuReturnRetry\((true|false)\)");
        if (retry.Success) ReturnRetryVisible = retry.Groups[1].Value == "true";

        var query = Regex.Match(command, """(KCD2MP_Wo125LastLoaded|KCD2MP_Wo124Lock|KCD2MP_Wo124Henry|KCD2MP_W154Where)\("(r?[0-9a-f]{8})"(, true)?\)""");
        if (query.Success) AnswerQuery(query.Groups[1].Value, query.Groups[2].Value, query.Groups[3].Success);
        if (RequestLegacySnapshot is { } snapshot)
        {
            var request = Regex.Match(command, """KCD2MP_Wo125Snapshot\("([0-9a-f]{8})"\)""");
            if (request.Success)
            {
                string token = request.Groups[1].Value;
                Emit("wo124_reply", $"{token} ok=true");
                snapshot(token);
            }
        }

        var quiet = Regex.Match(command, """KCD2MP_W177LoadQuiet\("([a-z-]+)"\)""");
        if (quiet.Success)
        {
            if (quiet.Groups[1].Value == "leave") BeforeOwnLoadQuiet?.Invoke();
            Emit("w177_load_quiet", $"{quiet.Groups[1].Value} ok");
        }

        var resume = Regex.Match(command, """KCD2MP_JoinResume\("[0-9a-f]{8}",\s*"([^"]+)"\)""");
        if (resume.Success) _resumed.TrySetResult(resume.Groups[1].Value);
        if (command.Contains("Co-op: you are in your host's world.", StringComparison.Ordinal))
            _joinedNotifications.Writer.TryWrite(true);
    }

    private void AcceptLoad(Match load, bool joined)
    {
        string name = load.Groups[2].Value;
        string path = Path.Combine(_saves, $"playline{load.Groups[1].Value}", name + ".whs");
        lock (_logGate)
        {
            Assert.True(File.Exists(path), $"The agent asked the engine to load a missing save: {name}.");
            _where = "loading";
            Feed($"Loading saved game '{path}'");
            if (joined)
            {
                bool defer = DeferJoinFileRead;
                Interlocked.Increment(ref _joinLoadsIssued);
                if (defer) _pendingJoinReads.Enqueue(path);
                _loadIssued.TrySetResult(path);
                _joinLoads.Writer.TryWrite(path);
                if (!defer) ReadLoadedFile(path, joined: true);
            }
            else
            {
                Interlocked.Increment(ref _ownLoadsIssued);
                if (DeferOwnFileRead) _pendingOwnReads.Enqueue(path);
                else ReadLoadedFile(path, joined: false);
                _ownLoads.Writer.TryWrite(path);
            }
        }
    }

    private void ReadLoadedFile(string path, bool joined)
    {
        Assert.NotEmpty(File.ReadAllBytes(path));
        _lastLoaded = Path.GetFileNameWithoutExtension(path);
        _lastLoadedPath = path;
        if (joined) Interlocked.Increment(ref _joinFilesRead);
        Feed($"[CryAction] LoadGame: '{path}'");
        Feed("Sending concept notification 'EntityModuleOnPostLoadGame'");
        _gameplayStarted();
        _where = "world";
        Feed("Gameplay started");
    }

    private void AnswerQuery(string query, string token, bool fullPath)
    {
        lock (_logGate)
        {
            if (query == "KCD2MP_Wo124Lock" && DeferSaveLockReply)
            {
                _pendingLockReplies.Enqueue(token);
                _saveLockQueries.Writer.TryWrite(token);
                return;
            }

            if (query == "KCD2MP_W154Where") Interlocked.Increment(ref _whereQueries);
            string reply = query switch
            {
                "KCD2MP_Wo125LastLoaded" when fullPath => "path=" + (LastLoadedPathOverride ?? _lastLoadedPath),
                "KCD2MP_Wo125LastLoaded" => "last=" + _lastLoaded,
                "KCD2MP_Wo124Lock" => "lock=held",
                "KCD2MP_W154Where" => _where,
                // WhsSaveTests.Pair's guest has 151 saved money units and six apples. The quest letter is stripped.
                _ => "money=15.10 items=0a0a0a0a-0000-0000-0000-00000000a001:6"
            };
            Emit("wo124_reply", $"{token} {reply}");
        }
    }

    public ValueTask DisposeAsync() => Transport.DisposeAsync();

    private sealed class ConsoleHandler(JoinCalendarTestEngine engine) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            string command = Uri.UnescapeDataString(request.RequestUri?.Query ?? "");
            engine.ConsoleCommand(command);
            bool failed = engine.FailExitResponse && command.Contains(",\"exit-to-menu\")", StringComparison.Ordinal);
            if (failed) Console.WriteLine("H21-HOME exit audit: HTTP 503 after delivering the engine command");
            return Task.FromResult(new HttpResponseMessage(failed ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
                { Content = new StringContent(failed ? "unavailable" : "ok") });
        }
    }
}
