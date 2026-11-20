// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
namespace KcdMp.Client;

public partial class GameBridge
{
    // Each submission observes the game log independently of its relay session.
    // The receipt proves that this engine stopped reading this file. The placed
    // world's lease and journal still authorize the actual deletion.
    private sealed class JoinEngineReceipt : IDisposable
    {
        private readonly LogTailGameTransport _tail;
        private readonly CombatPipe _pipe;
        private readonly long _logIncarnation, _pipeRevision;
        private readonly string _path;
        private readonly string? _userAlias;
        private static StringComparison PathComparison =>
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        public readonly TaskCompletionSource<bool> Terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public JoinEngineReceipt(LogTailGameTransport tail, CombatPipe pipe, string path, string saveRoot)
        {
            _tail = tail;
            _pipe = pipe;
            _logIncarnation = tail.EngineLogIncarnation;
            _pipeRevision = pipe.EdgeConnectionRevision;
            _path = Path.GetFullPath(path).Replace('\\', '/');
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

        private void Observe(LogTailGameTransport.EngineSaveTerminal receipt)
        {
            if (receipt.Incarnation == _logIncarnation && Matches(receipt.Path) && Terminal.TrySetResult(receipt.Loaded))
                Dispose();
        }

        private void EngineQuit()
        {
            if (Terminal.TrySetCanceled()) Dispose();
        }

        public void Dispose()
        {
            _tail.EngineSaveFinished -= Observe;
            _tail.GameQuit -= EngineQuit;
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
        Volatile.Read(ref _pinnedHomeLoad) is not null ||
        (!OwnLoadExpected && _jj is not null && engine.DispatchingAmbiguousSaveTerminal) ||
        EngineNoticeBelongsToAnotherJoin(engine.DispatchingTerminalSave);
}
