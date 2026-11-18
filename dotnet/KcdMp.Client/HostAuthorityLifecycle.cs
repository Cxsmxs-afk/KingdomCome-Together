// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
namespace KcdMp.Client;

/// <summary>Generation-bound control of the native load latch. A late ACK never certifies a newer load.</summary>
public sealed class HostAuthorityLifecycle
{
    public const byte Disabled = 0, Load = 1, Gameplay = 2, Query = 3;
    public readonly record struct Ack(bool Accepted, byte Phase, bool Enabled, bool Active, ulong Generation, uint GameplayAgeMs);
    public readonly record struct State(ulong Generation, bool Enabled, bool Confirmed, bool GameplaySent, bool Active, uint GameplayAgeMs);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _initialize = new(1, 1);
    private readonly Func<byte, ulong, CancellationToken, Task<Ack?>> _send;
    private bool _initialized;
    private bool _hasPendingRequest, _desiredEnabled;
    private long _requestVersion;
    private ulong _maximum;
    private State _state;

    public HostAuthorityLifecycle(Func<byte, ulong, CancellationToken, Task<Ack?>> send) => _send = send;
    public State Current { get { lock (_gate) return _state; } }
    public async Task<ulong> KnownGenerationAsync(CancellationToken ct)
    {
        if (!await InitializeAsync(ct)) throw new IOException("native lifecycle could not be queried");
        lock (_gate) return _state.Generation != 0 ? _state.Generation : _maximum;
    }

    private async Task<bool> InitializeAsync(CancellationToken ct)
    {
        await _initialize.WaitAsync(ct);
        try
        {
            lock (_gate) if (_initialized) return true;
            var read = await _send(Query, 0, ct);
            ct.ThrowIfCancellationRequested();
            if (read is not { Accepted: true } ack) return false;
            lock (_gate) { _maximum = Math.Max(_maximum, ack.Generation); _initialized = true; }
            return true;
        }
        finally { _initialize.Release(); }
    }

    public async Task<bool> PendingAsync(bool enabled, bool newLoad, CancellationToken ct)
        => await PendingCoreAsync(enabled, newLoad, ct) is not null;

    private async Task<ulong?> PendingCoreAsync(bool enabled, bool newLoad, CancellationToken ct, long? expectedRequest = null)
    {
        long request;
        lock (_gate)
        {
            if (expectedRequest is long expected && expected != _requestVersion) return null;
            if (expectedRequest is null)
            {
                if (newLoad || !_hasPendingRequest || _desiredEnabled != enabled) _requestVersion++;
                _desiredEnabled = enabled; _hasPendingRequest = true;
            }
            request = _requestVersion;
        }
        if (!await InitializeAsync(ct)) return null;
        ct.ThrowIfCancellationRequested();
        State pending;
        lock (_gate)
        {
            if (request != _requestVersion) return null;
            if (newLoad || _state.Generation == 0 || _state.Enabled != enabled || _state.GameplaySent)
                _state = new(checked(++_maximum), enabled, false, false, false, 0);
            pending = _state;
        }
        byte phase = enabled ? Load : Disabled;
        var response = await _send(phase, pending.Generation, ct);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (response is { } observed) _maximum = Math.Max(_maximum, observed.Generation);
            if (request != _requestVersion || _state.Generation != pending.Generation || _state.Enabled != enabled || _state.GameplaySent ||
                response is not { Accepted: true, Active: false } ack || ack.Generation != pending.Generation || ack.Phase != phase || ack.Enabled != enabled) return null;
            _state = pending with { Confirmed = true }; return pending.Generation;
        }
    }

    public async Task<bool> GameplayAsync(ulong generation, CancellationToken ct)
    {
        lock (_gate) if (_state.Generation != generation || !_state.Enabled || !_state.Confirmed) return false;
        var response = await _send(Gameplay, generation, ct);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_state.Generation != generation || response is not { Accepted: true, Enabled: true, Phase: Gameplay } ack || ack.Generation != generation) return false;
            _state = _state with { GameplaySent = true, Active = ack.Active, GameplayAgeMs = ack.GameplayAgeMs }; return true;
        }
    }

    public async Task<bool> RefreshAsync(CancellationToken ct)
    {
        State before = Current;
        if (!before.Confirmed) return false;
        var response = await _send(Query, 0, ct);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (response is { } observed) _maximum = Math.Max(_maximum, observed.Generation);
            if (_state.Generation != before.Generation || response is not { Accepted: true } ack) return false;
            if (ack.Generation != before.Generation || ack.Enabled != before.Enabled ||
                (before.Enabled && ack.Phase != (before.GameplaySent ? Gameplay : Load)) || (!before.Enabled && ack.Phase != Disabled))
            {
                _state = _state with { Confirmed = false, Active = false }; return false;
            }
            _state = _state with { Active = ack.Active, GameplayAgeMs = ack.GameplayAgeMs }; return true;
        }
    }

    /// <summary>Retry one already-known load; only a separately observed engine Gameplay edge may send phase 2.</summary>
    public async Task<bool> RetryAsync(bool gameplayObserved, CancellationToken ct)
    {
        State state = Current;
        bool enabled; long request;
        lock (_gate)
        {
            if (!_hasPendingRequest) return false;
            enabled = _desiredEnabled; request = _requestVersion;
        }
        ulong? generation = state.Confirmed ? state.Generation : await PendingCoreAsync(enabled, false, ct, request);
        if (generation is not ulong expected || Current.Generation != expected) return false;
        state = Current;
        return !gameplayObserved || !state.Enabled || state.GameplaySent || await GameplayAsync(expected, ct);
    }
}
