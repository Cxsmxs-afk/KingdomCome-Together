// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
namespace KcdMp.Client;

public enum WorldLoadPhase : byte { Menu, Offer, Load, Reconstruct, Settle, Play, Exit }

public enum WorldLoadStart : byte
{
    Offer, LocalLoad, HostReload, AdoptLoadedWorld, SupersedingLoad
}

public enum WorldLoadOwnIntent : byte { None, ExpectedPrivate, DifferentSaveGrace, SharedSaveGrace }

public readonly record struct WorldLoadSnapshot(ulong Generation, WorldLoadPhase Phase, long ElapsedMs, uint HostWorld)
{
    public bool LinkHeld { get; init; }
    public WorldLoadOwnIntent OwnIntent { get; init; }
    public long OwnIntentDeadlineMs { get; init; }
    public bool OwnGraceActive { get; init; }
    internal ulong OwnIntentRevision { get; init; }
}

/// <summary>
/// The same local generation and phase authority for host and joiner. An accepted offer keeps its generation when loading.
/// A proven replacement allocates a fresh local generation even when the host's epoch is unchanged.
/// </summary>
public sealed class WorldLoadSequence
{
    private readonly object _gate = new();
    private readonly Func<long> _nowMs;
    private readonly long[] _phaseMs = new long[(int)WorldLoadPhase.Exit + 1];
    private ulong _generation, _ownRevision;
    private uint _hostWorld, _offerId;
    private WorldLoadPhase _phase = WorldLoadPhase.Menu;
    private WorldLoadOwnIntent _ownIntent;
    private long _enteredMs, _lastMs, _ownDeadlineMs;
    private bool _linkHeld;

    public WorldLoadSequence(Func<long>? nowMs = null)
    {
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        _enteredMs = _lastMs = Math.Max(0, _nowMs());
    }

    public WorldLoadSnapshot Current { get { lock (_gate) return Snapshot(Clock()); } }

    public long PhaseMilliseconds(WorldLoadPhase phase)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((byte)phase, (byte)WorldLoadPhase.Exit);
        lock (_gate) return _phaseMs[(int)phase] + (_phase == phase ? Clock() - _enteredMs : 0);
    }

    /// <summary>Returns true for a duplicate observation of the same offer or load without changing its generation or timing.</summary>
    public bool TryBegin(ulong expectedGeneration, WorldLoadStart reason, uint hostWorld = 0, uint offerId = 0) =>
        TryBegin(expectedGeneration, reason, out _, hostWorld, offerId);

    public bool TryBegin(ulong expectedGeneration, WorldLoadStart reason, out WorldLoadSnapshot started,
        uint hostWorld = 0, uint offerId = 0)
    {
        lock (_gate)
        {
            long now = Clock();
            bool success = expectedGeneration == _generation && Begin(reason, hostWorld, offerId, now);
            started = Snapshot(now);
            return success;
        }
    }

    private bool Begin(WorldLoadStart reason, uint hostWorld, uint offerId, long now)
    {
        switch (reason)
        {
            case WorldLoadStart.Offer:
                if (_hostWorld == hostWorld && _phase == WorldLoadPhase.Offer && (_offerId == 0 || offerId == 0 || _offerId == offerId))
                {
                    if (offerId != 0) _offerId = offerId;
                    return true;
                }
                if (Active && _hostWorld == hostWorld && offerId != 0 && _offerId == offerId) return true;
                return StartNew(WorldLoadPhase.Offer, hostWorld, offerId, false, now);
            case WorldLoadStart.LocalLoad:
                if (_phase == WorldLoadPhase.Load && (hostWorld == 0 || _hostWorld == hostWorld)) return true;
                return StartNew(WorldLoadPhase.Load, hostWorld, 0, true, now);
            case WorldLoadStart.HostReload:
                if (hostWorld == 0 || (Active && _hostWorld != 0 && hostWorld < _hostWorld)) return false;
                if (Active && _hostWorld == hostWorld) return true;
                return StartNew(WorldLoadPhase.Load, hostWorld, 0, false, now);
            case WorldLoadStart.AdoptLoadedWorld:
                if (_phase == WorldLoadPhase.Play) return hostWorld == 0 || SetHostWorld(hostWorld);
                return _phase == WorldLoadPhase.Menu && StartNew(WorldLoadPhase.Play, hostWorld, 0, false, now);
            case WorldLoadStart.SupersedingLoad:
                return StartNew(WorldLoadPhase.Load, hostWorld, 0, true, now);
            default:
                return false;
        }
    }

    private bool StartNew(WorldLoadPhase phase, uint hostWorld, uint offerId, bool preservePrivateIntent, long now)
    {
        if (_generation == ulong.MaxValue) return false;
        _generation++;
        _hostWorld = hostWorld;
        _offerId = offerId;
        _linkHeld = false;
        if (!preservePrivateIntent || _ownIntent != WorldLoadOwnIntent.ExpectedPrivate) ChangeOwnIntent(WorldLoadOwnIntent.None, 0);
        Enter(phase, now);
        return true;
    }

    public bool TryAdvance(ulong generation, WorldLoadPhase phase)
    {
        lock (_gate) return Advance(generation, _phase, phase);
    }

    public bool TryAdvance(ulong generation, WorldLoadPhase expectedPhase, WorldLoadPhase phase)
    {
        lock (_gate) return Advance(generation, expectedPhase, phase);
    }

    public bool TryAdvance(WorldLoadSnapshot expected, WorldLoadPhase phase)
    {
        lock (_gate) return expected.OwnIntentRevision == _ownRevision && expected.LinkHeld == _linkHeld &&
            expected.HostWorld == _hostWorld && Advance(expected.Generation, expected.Phase, phase);
    }

    private bool Advance(ulong generation, WorldLoadPhase expectedPhase, WorldLoadPhase phase)
    {
        if (generation == 0 || generation != _generation || _phase != expectedPhase) return false;
        if (_phase == phase) return true;
        bool allowed = (_phase, phase) is
            (WorldLoadPhase.Offer, WorldLoadPhase.Load) or
            (WorldLoadPhase.Load, WorldLoadPhase.Reconstruct) or
            (WorldLoadPhase.Reconstruct, WorldLoadPhase.Settle) or
            (WorldLoadPhase.Settle, WorldLoadPhase.Play) or
            (WorldLoadPhase.Exit, WorldLoadPhase.Menu) || (Active && phase == WorldLoadPhase.Exit);
        if (!allowed) return false;
        Enter(phase, Clock());
        return true;
    }

    public bool TryAbort(ulong generation)
    {
        lock (_gate) return Abort(generation, _phase);
    }

    public bool TryAbort(ulong generation, WorldLoadPhase expectedPhase)
    {
        lock (_gate) return Abort(generation, expectedPhase);
    }

    private bool Abort(ulong generation, WorldLoadPhase expectedPhase)
    {
        if (generation == 0 || generation != _generation || _phase != expectedPhase ||
            _phase is WorldLoadPhase.Menu or WorldLoadPhase.Play) return false;
        Enter(WorldLoadPhase.Menu, Clock());
        return true;
    }

    public bool TrySetHostWorld(ulong generation, uint hostWorld)
    {
        lock (_gate) return IsActive(generation) && SetHostWorld(hostWorld);
    }

    private bool SetHostWorld(uint hostWorld)
    {
        if (hostWorld == 0 || (_hostWorld != 0 && _hostWorld != hostWorld)) return false;
        _hostWorld = hostWorld;
        return true;
    }

    public bool TryHoldLink(ulong generation, bool allowSettling = false)
    {
        lock (_gate)
        {
            bool mayHold = IsPlaying(generation) ||
                (allowSettling && generation != 0 && generation == _generation && _phase == WorldLoadPhase.Settle);
            if (!mayHold || PrivateIntent || _linkHeld) return false;
            _linkHeld = true;
            return true;
        }
    }

    public bool TryReconnect(ulong generation, uint hostWorld)
    {
        lock (_gate)
        {
            if (!IsPlaying(generation) || !_linkHeld || PrivateIntent || hostWorld == 0 || _hostWorld != hostWorld) return false;
            _linkHeld = false;
            return true;
        }
    }

    public bool TryReleaseLink(ulong generation)
    {
        lock (_gate)
        {
            if (!IsPlaying(generation) || !_linkHeld) return false;
            _linkHeld = false;
            return true;
        }
    }

    public bool SetOwnIntent(ulong generation, WorldLoadOwnIntent intent, long deadlineMs = 0) =>
        SetOwnIntent(generation, intent, out _, deadlineMs);

    public bool SetOwnIntent(ulong generation, WorldLoadOwnIntent intent, out WorldLoadSnapshot applied, long deadlineMs = 0) =>
        SetOwnIntent(generation, intent, out applied, out _, deadlineMs);

    public bool SetOwnIntent(ulong generation, WorldLoadOwnIntent intent, out WorldLoadSnapshot applied,
        out WorldLoadSnapshot previous, long deadlineMs = 0)
    {
        lock (_gate)
        {
            long now = Clock();
            previous = Snapshot(now);
            bool accepted = generation != 0 && generation == _generation && _phase != WorldLoadPhase.Menu &&
                ValidOwnIntent(intent, deadlineMs);
            if (accepted) ChangeOwnIntent(intent, deadlineMs);
            applied = Snapshot(now);
            return accepted;
        }
    }

    /// <summary>Rolls back one local load preparation only while it still owns the same generation and intent revision.</summary>
    public bool TryRestoreOwnIntent(WorldLoadSnapshot applied, WorldLoadSnapshot previous)
    {
        lock (_gate)
        {
            if (!CurrentOwnIntent(applied) || previous.Generation != _generation || previous.Phase != _phase ||
                previous.OwnIntentRevision + 1 != _ownRevision || !ValidOwnIntent(previous.OwnIntent, previous.OwnIntentDeadlineMs))
                return false;
            ChangeOwnIntent(previous.OwnIntent, previous.OwnIntentDeadlineMs);
            return true;
        }
    }

    /// <summary>Reclassifies an observed own-save intent only while its phase and revision still match.</summary>
    public bool TryUpdateOwnIntent(WorldLoadSnapshot expected, WorldLoadOwnIntent intent, long deadlineMs = 0)
    {
        lock (_gate)
        {
            if (!CurrentOwnIntent(expected) || !ValidOwnIntent(intent, deadlineMs)) return false;
            ChangeOwnIntent(intent, deadlineMs);
            return true;
        }
    }

    public bool IsCurrent(ulong generation) { lock (_gate) return IsActive(generation); }
    public bool CanApply(ulong generation) { lock (_gate) return IsPlaying(generation) && !_linkHeld && !PrivateIntent; }

    private bool Active => _phase is not (WorldLoadPhase.Menu or WorldLoadPhase.Exit);
    private bool IsActive(ulong generation) => generation != 0 && _generation == generation && Active;
    private bool IsPlaying(ulong generation) => generation != 0 && _generation == generation && _phase == WorldLoadPhase.Play;
    private bool PrivateIntent => _ownIntent is WorldLoadOwnIntent.ExpectedPrivate or WorldLoadOwnIntent.DifferentSaveGrace;
    private static bool Grace(WorldLoadOwnIntent intent) => intent is WorldLoadOwnIntent.DifferentSaveGrace or WorldLoadOwnIntent.SharedSaveGrace;
    private static bool ValidOwnIntent(WorldLoadOwnIntent intent, long deadlineMs) =>
        Enum.IsDefined(intent) && (Grace(intent) ? deadlineMs > 0 : deadlineMs == 0);
    private bool CurrentOwnIntent(WorldLoadSnapshot snapshot) => snapshot.Generation != 0 && snapshot.Generation == _generation &&
        snapshot.Phase == _phase && _phase != WorldLoadPhase.Menu && snapshot.OwnIntentRevision == _ownRevision;
    private long Clock() => _lastMs = Math.Max(_lastMs, _nowMs());

    private void Enter(WorldLoadPhase phase, long now)
    {
        _phaseMs[(int)_phase] += now - _enteredMs;
        _phase = phase;
        _enteredMs = now;
        if (phase is WorldLoadPhase.Menu or WorldLoadPhase.Exit) _linkHeld = false;
        if (phase == WorldLoadPhase.Menu)
        {
            _hostWorld = 0;
            _offerId = 0;
            ChangeOwnIntent(WorldLoadOwnIntent.None, 0);
        }
    }

    private void ChangeOwnIntent(WorldLoadOwnIntent intent, long deadlineMs)
    {
        _ownIntent = intent;
        _ownDeadlineMs = deadlineMs;
        _ownRevision++;
    }

    private WorldLoadSnapshot Snapshot(long now) => new(_generation, _phase, now - _enteredMs, _hostWorld)
    {
        LinkHeld = _linkHeld,
        OwnIntent = _ownIntent,
        OwnIntentDeadlineMs = _ownDeadlineMs,
        OwnGraceActive = Grace(_ownIntent) && now < _ownDeadlineMs,
        OwnIntentRevision = _ownRevision
    };
}
