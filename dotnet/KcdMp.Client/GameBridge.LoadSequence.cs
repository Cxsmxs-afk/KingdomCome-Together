// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using KcdMp.Wire;

namespace KcdMp.Client;

public partial class GameBridge
{
    private readonly WorldLoadSequence _worldLoad = new();
    private readonly object _loadSequenceGate = new();
    private readonly SemaphoreSlim _loadSequencePublish = new(1, 1);
    private readonly string _loadPublisherId = Guid.NewGuid().ToString("N");
    private CancellationTokenSource _loadGenerationStop = new();
    private volatile bool _loadSequenceRuntime;
    private uint _loadHostOffer;
    private ushort _loadHostHoldSeconds;
    private long _staleGenerationDropped, _loadScopeDropped, _loadPublishRetryAt, _loadLuaRevision;
    private (byte Phase, byte Flags, ulong Generation, ushort HoldSeconds, long Pipe)? _loadNativeApplied;
    private (byte Phase, byte Flags, ulong Generation)? _loadLuaApplied;

    private bool LoadSequenceEnabled => _loadSequenceRuntime && W221On("load_sequence");
    private bool LoadSequenceWritable => !LoadSequenceEnabled ||
        (Volatile.Read(ref _loadHostOffer) == 0 && _worldLoad.CanApply(_worldLoad.Current.Generation));
    private string LoadSequenceRole => _isDamageAuthority || (!_combatRoleApplied && config.IsHosting) ? "host" : "joiner";

    private bool LoadSequenceChange(Func<WorldLoadSnapshot, bool> change)
    {
        if (!_loadSequenceRuntime) return false;
        WorldLoadSnapshot before, after;
        lock (_loadSequenceGate)
        {
            before = _worldLoad.Current;
            if (!change(before)) return false;
            after = _worldLoad.Current;
            if (LoadSequenceEnabled && after.Generation != before.Generation)
                T3SideChannelsInvalidate("load-sequence-generation");
            if (after.Generation != before.Generation || after.Phase == WorldLoadPhase.Menu && before.Phase != WorldLoadPhase.Menu ||
                after.Phase == WorldLoadPhase.Exit && before.Phase != WorldLoadPhase.Exit || after.LinkHeld && !before.LinkHeld)
            {
                var retired = _loadGenerationStop;
                _loadGenerationStop = new();
                _ = LoadSequenceRetireAsync(retired);
            }
        }
        bool changed = (before.Generation, before.Phase, before.HostWorld, before.LinkHeld, before.OwnIntent) !=
            (after.Generation, after.Phase, after.HostWorld, after.LinkHeld, after.OwnIntent);
        if (changed && LoadSequenceEnabled)
        {
            Console.WriteLine(FormattableString.Invariant($"MP-LOAD-SEQUENCE role={LoadSequenceRole} gen={after.Generation} world={after.HostWorld} phase={after.Phase} link_hold={(after.LinkHeld ? 1 : 0)}"));
            _ = LoadSequenceSyncNativeAsync();
        }
        return true;
    }

    private static async Task LoadSequenceRetireAsync(CancellationTokenSource old)
    {
        try { await old.CancelAsync(); }
        finally { old.Dispose(); }
    }

    private void LoadSequenceObservePlace(GameWhere where)
    {
        if (where == GameWhere.Menu)
        {
            if (_worldLoad.Current.Phase is WorldLoadPhase.Menu or WorldLoadPhase.Play or WorldLoadPhase.Exit) LoadSequenceOnMenu();
        }
        else if (where == GameWhere.Loading) LoadSequenceBeginLoad();
        else if (where == GameWhere.World)
            LoadSequenceChange(s => s.Phase is WorldLoadPhase.Menu or WorldLoadPhase.Play &&
                _worldLoad.TryBegin(s.Generation, WorldLoadStart.AdoptLoadedWorld, _w206.World));
    }

    private void LoadSequenceBeginOffer(uint offerId, uint hostWorld) =>
        LoadSequenceChange(s => _worldLoad.TryBegin(s.Generation, WorldLoadStart.Offer, hostWorld, offerId));

    private void LoadSequenceBeginLoad(bool superseding = false) =>
        LoadSequenceChange(s => !superseding && s.Phase == WorldLoadPhase.Offer
            ? _worldLoad.TryAdvance(s.Generation, s.Phase, WorldLoadPhase.Load)
            : _worldLoad.TryBegin(s.Generation, superseding ? WorldLoadStart.SupersedingLoad : WorldLoadStart.LocalLoad,
                s.Phase == WorldLoadPhase.Load ? s.HostWorld :
                s.OwnIntent == WorldLoadOwnIntent.ExpectedPrivate || superseding ? 0 : _w206.World));

    private void LoadSequenceObserveHostWorld(uint world)
    {
        if (world == 0) return;
        LoadSequenceChange(s =>
        {
            if (s.Generation == 0 || s.Phase is WorldLoadPhase.Menu or WorldLoadPhase.Exit ||
                s.OwnIntent is WorldLoadOwnIntent.ExpectedPrivate or WorldLoadOwnIntent.DifferentSaveGrace) return false;
            if (!_isDamageAuthority && _joinedWorld && s.HostWorld != 0 && s.HostWorld != world && T3HostWorldChanged)
            {
                if (s.LinkHeld) return false; // recovery closes the held play scope before its new offer
                return _worldLoad.TryBegin(s.Generation, WorldLoadStart.Offer, world);
            }
            return _worldLoad.TrySetHostWorld(s.Generation, world);
        });
    }

    private void LoadSequenceOnReconstruction() =>
        LoadSequenceChange(s => s.Phase == WorldLoadPhase.Load &&
            _worldLoad.TryAdvance(s.Generation, s.Phase, WorldLoadPhase.Reconstruct));

    private void LoadSequenceOnPhysicalGameplay()
    {
        if (!_loadSequenceRuntime) return;
        if (_worldLoad.Current.Phase == WorldLoadPhase.Menu) LoadSequenceBeginLoad();
        LoadSequenceOnReconstruction();
        // A shared-world joiner still needs the join's real placement/Ready
        // proof. The engine marker suffices for a local reload or the host.
        if (_jj is null || _isDamageAuthority || _jj.LoadGeneration != _worldLoad.Current.Generation) LoadSequenceOnJoinReady();
    }

    private void LoadSequenceOnJoinReady() =>
        LoadSequenceChange(s => s.Phase == WorldLoadPhase.Reconstruct &&
            _worldLoad.TryAdvance(s.Generation, s.Phase, WorldLoadPhase.Settle));

    private void LoadSequenceOnExit() =>
        LoadSequenceChange(s => s.Generation != 0 && s.Phase != WorldLoadPhase.Menu &&
            _worldLoad.TryAdvance(s.Generation, s.Phase, WorldLoadPhase.Exit));

    private void LoadSequenceOnMenu()
    {
        if (_worldLoad.Current.Phase == WorldLoadPhase.Play) LoadSequenceOnExit();
        LoadSequenceChange(s => _worldLoad.TryAbort(s.Generation, s.Phase));
    }

    private bool LoadSequenceTryHoldLink() => LoadSequenceChange(s => _worldLoad.TryHoldLink(s.Generation));

    private bool LoadSequenceTryResumeLink(uint world, bool resumed) => LoadSequenceChange(s =>
    {
        if (resumed) return _worldLoad.TryReconnect(s.Generation, world);
        if (!s.LinkHeld) return false;
        if (world != 0 && world != s.HostWorld && T3HostWorldChanged)
            return _worldLoad.TryBegin(s.Generation, WorldLoadStart.Offer, world);
        return _worldLoad.TryAdvance(s.Generation, s.Phase, WorldLoadPhase.Exit);
    });

    private async Task<bool> LoadSequenceHostOfferAsync(uint joinId, bool on, ushort holdSeconds = 240)
    {
        lock (_loadSequenceGate)
        {
            if (joinId == 0 || (on ? _loadHostOffer != 0 && _loadHostOffer != joinId : _loadHostOffer != joinId)) return false;
            _loadHostOffer = on ? joinId : 0;
            _loadHostHoldSeconds = on ? (ushort)Math.Clamp((int)holdSeconds, 60, 900) : (ushort)0;
        }
        bool accepted = await LoadSequenceSyncNativeAsync();
        if (accepted && on) accepted = await LoadSequenceWaitForActuatorAsync(2, joinId);
        if (on && !accepted)
        {
            lock (_loadSequenceGate) if (_loadHostOffer == joinId) _loadHostOffer = 0;
            _ = LoadSequenceSyncNativeAsync();
        }
        return accepted;
    }

    private async Task<bool> LoadSequenceWaitForActuatorAsync(byte flag, uint joinId = 0)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            while (flag == 1 ? _worldLoad.Current.LinkHeld : Volatile.Read(ref _loadHostOffer) == joinId)
            {
                ulong generation = _worldLoad.Current.Generation;
                var result = await _combat.LoadSequenceAsync(CombatPipe.LoadSequenceQuery, 0, 0, ct: deadline.Token);
                if (result is { Accepted: true, MainApplied: true } reply && reply.Generation == generation && (reply.Flags & flag) != 0) return true;
                await Task.Delay(30, deadline.Token);
            }
        }
        catch (OperationCanceledException) { }
        return false;
    }

    private async Task<bool> LoadSequenceSyncNativeAsync(bool waitForLink = false)
    {
        if (!_loadSequenceRuntime) return false;
        await _loadSequencePublish.WaitAsync();
        try
        {
            byte phase, flags;
            ushort holdSeconds;
            ulong generation;
            lock (_loadSequenceGate)
            {
                var s = _worldLoad.Current;
                bool enabled = LoadSequenceEnabled;
                phase = enabled ? (byte)s.Phase : CombatPipe.LoadSequenceDisabled;
                flags = enabled ? (byte)((s.LinkHeld ? 1 : 0) | (_loadHostOffer != 0 ? 2 : 0) |
                    (s.OwnIntent is WorldLoadOwnIntent.ExpectedPrivate or WorldLoadOwnIntent.DifferentSaveGrace ? 4 : 0)) : (byte)0;
                generation = s.Generation;
                holdSeconds = (flags & 2) != 0 ? _loadHostHoldSeconds : (flags & 1) != 0 ? (ushort)(Wo211Rules.HoldWindowS + 20) : (ushort)0;
            }
            var live = (phase, flags, generation, holdSeconds, _combat.EdgeConnectionRevision);
            bool native = _loadNativeApplied == live;
            if (!native)
            {
                var ack = await _combat.LoadSequenceAsync(phase, flags, generation, holdSeconds);
                native = ack is { Accepted: true } r && (r.Phase, r.Flags, r.Generation) == (phase, flags, generation);
                if (native) _loadNativeApplied = (phase, flags, generation, holdSeconds, _combat.EdgeConnectionRevision);
            }
            if (_loadLuaApplied != (phase, flags, generation))
            {
                long revision = Interlocked.Increment(ref _loadLuaRevision);
                await ExecLuaAsync(FormattableString.Invariant(
                    $"if KCD2MP_LoadSequence then KCD2MP_LoadSequence(\"{_loadPublisherId}\",{revision},\"{generation}\",{phase},{flags}) end"));
                _loadLuaApplied = (phase, flags, generation);
            }
            return native && (!waitForLink || await LoadSequenceWaitForActuatorAsync(1));
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException)
        {
            return false;
        }
        finally { _loadSequencePublish.Release(); }
    }

    private void LoadSequenceTick()
    {
        if (!LoadSequenceEnabled) return;
        var s = _worldLoad.Current;
        if (s.Phase == WorldLoadPhase.Settle && s.ElapsedMs >= W136Settle.TotalMilliseconds &&
            s.OwnIntent is not (WorldLoadOwnIntent.ExpectedPrivate or WorldLoadOwnIntent.DifferentSaveGrace))
            LoadSequenceChange(current => current.Generation == s.Generation && current.Phase == s.Phase &&
                _worldLoad.TryAdvance(s.Generation, s.Phase, WorldLoadPhase.Play));
        long now = Environment.TickCount64;
        long retry = Interlocked.Read(ref _loadPublishRetryAt);
        if (now >= retry && Interlocked.CompareExchange(ref _loadPublishRetryAt, now + 2000, retry) == retry)
            _ = LoadSequenceSyncNativeAsync();
    }

    private static bool LoadSequenceWorldFrame(int type, byte[] body) =>
        Wo206Rules.WorldCategory(type, body) is not null || type == Protocol.W164Down && body.Length > 6 && body[6] == Protocol.W164Clock ||
            type is Protocol.Ghost or Protocol.NpcStateQDown or
            Protocol.ActionDown or Protocol.PlayerStateDown or Protocol.PlayerHitDown or Protocol.PlayerHitV8Down or
            Protocol.PlayerDeathDown or Protocol.PlayerRespawnedDown or Protocol.CombatEventDown or Protocol.AppearanceDown or
            Protocol.HorseInfoDown or Protocol.WeatherDown or Protocol.TimeSkipDown or Protocol.ConvDown or Protocol.W203Down or
            Protocol.ActivityHostDown or Protocol.ActivityExtraDown || Wo147IsLeashFrame(type);

    private (ulong Generation, CancellationToken Token) LoadSequenceCapture()
    {
        if (!LoadSequenceEnabled) return default;
        lock (_loadSequenceGate) return (_worldLoad.Current.Generation, _loadGenerationStop.Token);
    }

    private CancellationTokenSource? LoadSequenceLink(in InFrame frame, CancellationToken connection) =>
        LoadSequenceEnabled && frame.LoadScoped ? CancellationTokenSource.CreateLinkedTokenSource(connection, frame.LoadCancellation) : null;

    private bool LoadSequenceFrameCurrent(in InFrame frame)
    {
        if (!LoadSequenceEnabled || !frame.LoadScoped) return true;
        var s = _worldLoad.Current;
        uint knownWorld = _w206.World;
        bool differentGeneration = frame.LoadGeneration != s.Generation || frame.Stamp != 0 &&
            (knownWorld != 0 && frame.Stamp != knownWorld || s.HostWorld != 0 && frame.Stamp != s.HostWorld);
        if (!differentGeneration)
        {
            if (!frame.LoadCancellation.IsCancellationRequested && _worldLoad.IsCurrent(s.Generation)) return true;
            Interlocked.Increment(ref _loadScopeDropped);
            return false;
        }
        long count = Interlocked.Increment(ref _staleGenerationDropped);
        if (count <= 20 || count % 100 == 0)
            Console.WriteLine($"MP-LOAD-STALE role={LoadSequenceRole} gen={s.Generation} stale_gen={frame.LoadGeneration} world={knownWorld} stale_world={frame.Stamp} type=0x{frame.Type:X2} n={count}");
        return false;
    }

    private void LoadSequenceWeather(string profile, string result)
    {
        if (!LoadSequenceEnabled) return;
        var s = _worldLoad.Current;
        Console.WriteLine(FormattableString.Invariant(
            $"MP-LOAD-WEATHER role={LoadSequenceRole} gen={s.Generation} world={s.HostWorld} phase={s.Phase} profile={profile} result={result}"));
    }
}
