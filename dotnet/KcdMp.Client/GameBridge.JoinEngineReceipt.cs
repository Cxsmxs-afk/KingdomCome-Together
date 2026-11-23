// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using KcdMp.Wire;

namespace KcdMp.Client;

public partial class GameBridge
{
    internal LauncherAgentControl? LauncherRestartControl { get; init; }
    private FileStream? _launcherUnobservableRead;

    private bool TryProtectLauncherHandoff(out FileStream? lease)
    {
        lease = LauncherRestartControl?.TryProtectEngineRead();
        if (LauncherRestartControl is null || lease is not null) return true;
        Console.WriteLine("MP-LAUNCH engine handoff refused reason=agent-restart");
        return false;
    }

    private JoinEngineReceipt? PrepareLauncherProtectedReceipt(LogTailGameTransport tail, string path, string savesRoot)
    {
        if (!TryProtectLauncherHandoff(out var lease)) return null;
        try { return new(tail, _combat, path, savesRoot, lease); }
        catch { lease?.Dispose(); throw; }
    }

    // Without a current engine receipt source, recoverability cannot be
    // confirmed after dispatch. Keep one exclusion for this agent's lifetime.
    private bool TryProtectUnobservableEngineRead()
    {
        if (LauncherRestartControl is null || Volatile.Read(ref _launcherUnobservableRead) is not null) return true;
        if (!TryProtectLauncherHandoff(out var lease)) return false;
        if (lease is null) return true;
        if (Interlocked.CompareExchange(ref _launcherUnobservableRead, lease, null) is not null) lease.Dispose();
        else Console.WriteLine("MP-LAUNCH agent restart inhibited reason=no-exact-engine-receipt-source");
        return true;
    }

    // Each submission observes the game log independently of its relay session.
    // The receipt proves that this engine stopped reading this file. The placed
    // world's lease and journal still authorize the actual deletion.
    private sealed class JoinEngineReceipt : IDisposable
    {
        private readonly LogTailGameTransport _tail;
        private readonly CombatPipe _pipe;
        private readonly long _logIncarnation, _pipeRevision, _initialReadSequence;
        private readonly string _path;
        private readonly string? _userAlias;
        private FileStream? _launcherReadLease;
        private bool _launcherSubmitted;
        private static StringComparison PathComparison =>
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        public readonly TaskCompletionSource<bool> Terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public JoinEngineReceipt(LogTailGameTransport tail, CombatPipe pipe, string path, string saveRoot)
            : this(tail, pipe, path, saveRoot, null) { }

        public JoinEngineReceipt(LogTailGameTransport tail, CombatPipe pipe, string path, string saveRoot, FileStream? launcherReadLease)
        {
            _tail = tail;
            _pipe = pipe;
            _logIncarnation = tail.EngineLogIncarnation;
            _initialReadSequence = tail.EngineReadSequence;
            _pipeRevision = pipe.EdgeConnectionRevision;
            _path = Path.GetFullPath(path).Replace('\\', '/');
            _launcherReadLease = launcherReadLease;
            // The live resolver selects the game's profile (and the named test
            // copy, where configured). An explicit join-root override is not
            // proof that the game resolves its %USER% token to that override.
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KCDMP_JOIN_SAVES_DIR")))
                _userAlias = "%USER%/saves/" + Path.GetRelativePath(saveRoot, path).Replace('\\', '/');
            tail.EngineSaveFinished += Observe;
            tail.GameQuit += EngineQuit;
        }

        private bool Current => _logIncarnation == _tail.EngineLogIncarnation && _pipeRevision == _pipe.EdgeConnectionRevision;
        public bool Matches(string? path) => Current && path is not null &&
            (string.Equals(path, _path, PathComparison) || string.Equals(path, _userAlias, PathComparison));

        public bool MatchesTerminal(bool loaded) => Current && Terminal.Task.IsCompletedSuccessfully && Terminal.Task.Result == loaded;
        public bool CanRemoveFile => Terminal.Task.IsCompletedSuccessfully;
        public bool PublishesPrivateReturn { get; set; }

        public long FindProbedRead() => Current
            ? _tail.FindProbedEngineRead(_logIncarnation, _initialReadSequence, _path, _userAlias, PathComparison) : 0;

        public bool TryConfirmObservedPrivateRead(long sequence) => sequence > 0 && Current &&
            _tail.TryConsumeProbedEngineRead(_logIncarnation, sequence, _path, _userAlias, PathComparison) && TryComplete(loaded: true);

        public void ProtectSubmittedLoad() => Volatile.Write(ref _launcherSubmitted, true);

        public bool TryComplete(bool loaded)
        {
            if (!Current) return false;
            if (Volatile.Read(ref _launcherReadLease) is not null && !Volatile.Read(ref _launcherSubmitted)) return false;
            if (!Terminal.TrySetResult(loaded)) return false;
            Dispose();
            return true;
        }

        private void Observe(LogTailGameTransport.EngineSaveTerminal receipt)
        {
            if (receipt.Incarnation != _logIncarnation || !Matches(receipt.Path)) return;
            if (PublishesPrivateReturn) _tail.MarkSaveTerminalOwned();
            TryComplete(receipt.Loaded);
        }

        private void EngineQuit()
        {
            if (Terminal.TrySetCanceled()) Dispose();
        }

        public void Dispose()
        {
            // A relay/HTTP cancellation or a timed-out waiter cannot prove
            // that the separate game process stopped reading this file.
            if (Volatile.Read(ref _launcherSubmitted) && Volatile.Read(ref _launcherReadLease) is not null &&
                !Terminal.Task.IsCompleted) return;
            _tail.EngineSaveFinished -= Observe;
            _tail.GameQuit -= EngineQuit;
            Interlocked.Exchange(ref _launcherReadLease, null)?.Dispose();
        }
    }

    private static bool JoinSaveReadEnded(JoinerJoin join) => Wo154Rules.PlacedFileMayGo(
        join.LoadCmdUtc != default, join.LoadDispatchInFlight, join.EngineReceipt?.CanRemoveFile == true);

    private bool EngineNoticeBelongsToAnotherJoin(string? path)
    {
        if (path is not null && Volatile.Read(ref _pinnedHomeLoad) is { } home) return !home.Matches(path);
        if (path is null || _jj is not { } active || OwnLoadExpected) return false;
        var file = path.AsSpan((path.LastIndexOf('/') + 1)..);
        if (file.Length != 19 || !file.StartsWith("mpworld", StringComparison.OrdinalIgnoreCase) ||
            !file.EndsWith(".whs", StringComparison.OrdinalIgnoreCase)) return false;
        return active.EngineReceipt?.Matches(path) != true;
    }

    private bool IgnoreEngineTerminal(LogTailGameTransport engine) =>
        // The private load worker publishes only the matching terminal, after
        // giving the file back. Generic or stale gameplay cannot unpin it.
        engine.DispatchingOwnedSaveTerminal || Volatile.Read(ref _privateReturnCheck) is not null || Volatile.Read(ref _pinnedHomeLoad) is not null ||
        (!OwnLoadExpected && _jj is not null && engine.DispatchingAmbiguousSaveTerminal) ||
        EngineNoticeBelongsToAnotherJoin(engine.DispatchingTerminalSave);
}
