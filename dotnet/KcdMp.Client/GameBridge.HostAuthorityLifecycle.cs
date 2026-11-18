// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
namespace KcdMp.Client;

public partial class GameBridge
{
    private volatile bool _hostAuthorityEnabled = true;
    private HostAuthorityLifecycle? _p1Lifecycle;
    private long _p1Serial, _p1GameplayMs = -1;
    private uint _p1GameplayWorld;
    private volatile bool _p1Loading;
    private Task<bool>? _p1Pending;
    private JoinerJoin? _p1PreparedJoin;
    private int _p1TickBusy;
    private string _p1LuaPhase = "";
    private ulong _p1LuaGeneration;
    private ulong _p1LegacyZeroGeneration;
    private long _p1RetryAtMs;
    private bool P1JoinerIntent => !_isDamageAuthority && !_ownLoadExpected && !_loadGraceOwn && !_leaveInProgress &&
        (_joinedWorld || _jj is not null || _hostAuthorityPrepared);
    private bool P1HasGameplay => _p1GameplayMs >= 0 && _p1GameplayWorld == _w206.World;
    // Host time decisions remain host-owned even when optional story/brain suppression is disabled.
    private bool HostWorldDecisionContext => !_isDamageAuthority && !_ownLoadExpected && !_loadGraceOwn &&
        (_hostAuthorityPrepared || _joinedWorld || (_jj is { } join && ReferenceEquals(_p1PreparedJoin, join)));
    private HostAuthorityLifecycle P1Lifecycle => LazyInitializer.EnsureInitialized(ref _p1Lifecycle, () => new HostAuthorityLifecycle(async (phase, generation, ct) =>
    {
        var r = await _combat.Wo216LifecycleAsync(phase, generation, ct);
        ProcessorFrameScope.ThrowIfAbandoned(); ct.ThrowIfCancellationRequested();
        return r is { } ack ? new HostAuthorityLifecycle.Ack(ack.Accepted, ack.Phase, ack.Enabled, ack.Active, ack.Generation, ack.GameplayAgeMs) : null;
    }));

    private async Task P1TellLuaAsync(string phase, CancellationToken ct)
    {
        ProcessorFrameScope.ThrowIfAbandoned(); ct.ThrowIfCancellationRequested();
        ulong generation = P1Lifecycle.Current.Generation;
        if (_p1LuaPhase == phase && _p1LuaGeneration == generation) return;
        if (generation == _p1LuaGeneration && phase == "pending" && _p1LuaPhase is "active" or "legacy" or "disabled") return;
        bool legacy = phase == "legacy" || phase == "disabled";
        await _transport.ExecuteNowAsync($"if KCD2MP_HostAuthority then KCD2MP_HostAuthority({(legacy ? "false" : "true")},{(phase == "active" ? "true" : "false")},\"{phase}\",\"{generation}\") end", ct);
        ProcessorFrameScope.ThrowIfAbandoned(); ct.ThrowIfCancellationRequested();
        if (generation != P1Lifecycle.Current.Generation) return;
        if (generation == _p1LuaGeneration && phase == "pending" && _p1LuaPhase is "active" or "legacy" or "disabled") return;
        _p1LuaPhase = phase; _p1LuaGeneration = generation;
    }

    private Task<bool> P1BeginLoad(string why, CancellationToken ct)
    {
        bool next = !_p1Loading;
        T3TpInvalidate(); P5RetireOperations(cancelLoad: next);
        if (next) { Interlocked.Increment(ref _p1Serial); _p1GameplayMs = -1; _p1GameplayWorld = 0; }
        _p1Loading = true;
        long serial = Interlocked.Read(ref _p1Serial);
        bool enabled = _hostAuthorityEnabled && P1JoinerIntent;
        var task = Pending();
        _p1Pending = task;
        return task;
        async Task<bool> Pending()
        {
            using var link = ProcessorFrameScope.Link(ct);
            // This call bypasses the batch pump. A preceding main-thread job may still be loading.
            bool accepted = await P1Lifecycle.PendingAsync(enabled, next, link.Token);
            ProcessorFrameScope.ThrowIfAbandoned(); link.Token.ThrowIfCancellationRequested();
            if (serial != Interlocked.Read(ref _p1Serial)) return false;
            if (!accepted) { Console.WriteLine($"MP-P1 pending not confirmed why={why}"); return false; }
            ulong generation = P1Lifecycle.Current.Generation;
            Console.WriteLine($"MP-P1 pending gen={generation} enabled={enabled} why={why}");
            await P1TellLuaAsync(P1JoinerIntent ? "pending" : "disabled", link.Token);
            if (serial != Interlocked.Read(ref _p1Serial)) return false;
            if (!enabled && !await P1EnsureLegacyZeroAsync(generation, link.Token)) return false;
            return serial == Interlocked.Read(ref _p1Serial);
        }
    }

    private void P1OnLoadSeen(string why)
    {
        if (!_p1Loading) Interlocked.Increment(ref _hostAuthorityGeneration);
        _ = P1ObserveAsync(P1BeginLoad(why, _wo122Ct), "load");
    }

    private async Task<bool> P1PrepareJoinAsync(JoinerJoin join, CancellationToken ct)
    {
        if (ReferenceEquals(_p1PreparedJoin, join) && P1Lifecycle.Current.Confirmed) return true;
        _p1Loading = false; // a distinct join is a new load, even if the menu's earlier load never emitted Gameplay
        bool ok = await P1BeginLoad("join-before-load", ct);
        ProcessorFrameScope.ThrowIfAbandoned(); ct.ThrowIfCancellationRequested();
        if (!ok || !ReferenceEquals(_jj, join) || join.Abandoned.Task.IsCompleted) return false;
        _p1PreparedJoin = join;
        return true;
    }

    private async Task<bool> P1BeforeModLoadAsync(string why, CancellationToken ct = default)
    {
        using var link = ProcessorFrameScope.Link(ct);
        bool pending = await P1BeginLoad(why, link.Token);
        ProcessorFrameScope.ThrowIfAbandoned(); link.Token.ThrowIfCancellationRequested();
        return pending && P1Lifecycle.Current.Confirmed && !P1Lifecycle.Current.Active && await P5PrepareLoadAsync(link.Token);
    }

    private void P1OnGameplayStarted()
    {
        if (!P1HasGameplay) { _p1GameplayMs = Environment.TickCount64; _p1GameplayWorld = _w206.World; }
        _p1Loading = false;
        long serial = Interlocked.Read(ref _p1Serial);
        _ = P1ObserveAsync(Gameplay(), "gameplay");
        async Task Gameplay()
        {
            if (_p1Pending is { } pending && !await pending) return;
            if (serial != Interlocked.Read(ref _p1Serial) || !P1HasGameplay) return;
            var state = P1Lifecycle.Current;
            if (state.Confirmed && await P1Lifecycle.GameplayAsync(state.Generation, _wo122Ct)) await P5AfterGameplayAsync(_wo122Ct);
        }
    }

    private async Task P1ObserveAsync(Task work, string where)
    {
        try { await work; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.WriteLine($"MP-P1 {where} failed: {ex.GetType().Name}"); }
    }

    private async Task P1ResetLifecycleAsync(string why, CancellationToken ct)
    {
        T3TpInvalidate(); P5RetireOperations();
        bool keepGameplay = why is "role-change" or "disconnect" && P1HasGameplay && (_joinedWorld || _isDamageAuthority) && _jj is null &&
            _hostCalendarResumeEpoch == _w206.World && _where == GameWhere.World && !_ownLoadExpected && !_loadGraceOwn && !_p1Loading;
        long gameplay = keepGameplay ? _p1GameplayMs : -1;
        uint world = keepGameplay ? _p1GameplayWorld : 0;
        Interlocked.Increment(ref _p1Serial);
        _p1PreparedJoin = null; _p1Loading = false; _p1GameplayMs = gameplay; _p1GameplayWorld = world;
        await P1Lifecycle.PendingAsync(false, true, ct);
        ProcessorFrameScope.ThrowIfAbandoned(); ct.ThrowIfCancellationRequested();
        await P1TellLuaAsync("disabled", ct);
    }

    private async Task<bool> P1EnsureOnPendingAsync(CancellationToken ct)
    {
        var state = P1Lifecycle.Current;
        if (state.Enabled && state.Confirmed) return true;
        long gameplay = _p1GameplayMs;
        uint world = _p1GameplayWorld;
        if (!await P1BeginLoad("confirmed-world-resume", ct)) return false;
        if (gameplay >= 0 && world == _w206.World && _joinedWorld && _where == GameWhere.World && !Wo136Holding)
        {
            _p1GameplayMs = gameplay; _p1GameplayWorld = world; _p1Loading = false;
            return await P1Lifecycle.GameplayAsync(P1Lifecycle.Current.Generation, ct);
        }
        return true; // no invented Gameplay: the new role remains pending until the real edge arrives
    }

    private void P1OnAuthorityConfig(string? text)
    {
        string value = (text ?? "").Trim();
        if (value is not ("on" or "off")) return;
        bool enabled = value == "on";
        if (_hostAuthorityEnabled == enabled) return;
        Interlocked.Increment(ref _hostAuthorityGeneration);
        _hostAuthorityEnabled = enabled;
        _ = P1ObserveAsync(Apply(), "switch");
        async Task Apply()
        {
            long gameplay = _p1GameplayMs;
            uint world = _p1GameplayWorld;
            if (enabled && _where == GameWhere.World && !Wo136Holding && P1HasGameplay)
            {
                ulong oldGeneration = await P1Lifecycle.KnownGenerationAsync(_wo122Ct);
                await _transport.ExecuteNowAsync($"if KCD2MP_P1ReleaseLegacy then KCD2MP_P1ReleaseLegacy(\"{oldGeneration}\") end", _wo122Ct);
            }
            _p1Loading = false;
            var pending = P1BeginLoad("switch-" + value, _wo122Ct);
            long serial = Interlocked.Read(ref _p1Serial);
            if (!await pending || serial != Interlocked.Read(ref _p1Serial) || _hostAuthorityEnabled != enabled) return;
            if (_where == GameWhere.World && !Wo136Holding && gameplay >= 0 && world == _w206.World)
            {
                _p1GameplayMs = gameplay; _p1GameplayWorld = world;
                _p1Loading = false;
                if (enabled && P1JoinerIntent)
                {
                    if (!await ResumeHostAuthorityAsync(Interlocked.Read(ref _hostCalendarGeneration), _wo122Ct)) return;
                    await P1Lifecycle.GameplayAsync(P1Lifecycle.Current.Generation, _wo122Ct);
                }
            }
        }
    }

    private async Task P1TickAsync()
    {
        if (Interlocked.CompareExchange(ref _p1TickBusy, 1, 0) != 0) return;
        try
        {
            if (_p1Lifecycle is null) return;
            using var pulse = CancellationTokenSource.CreateLinkedTokenSource(_wo122Ct);
            pulse.CancelAfter(1500);
            if (_p1Pending is { IsCompleted: false }) return;
            var state = P1Lifecycle.Current;
            if (!state.Confirmed)
            {
                long now = Environment.TickCount64;
                if (now < _p1RetryAtMs) return;
                _p1RetryAtMs = now + 500;
                long serial = Interlocked.Read(ref _p1Serial);
                if (!await P1Lifecycle.RetryAsync(!_p1Loading && P1HasGameplay, pulse.Token) || serial != Interlocked.Read(ref _p1Serial)) return;
                await P1TellLuaAsync(P1JoinerIntent ? "pending" : "disabled", pulse.Token);
                state = P1Lifecycle.Current;
            }
            if (!P1JoinerIntent || _where is GameWhere.Menu or GameWhere.Loading || _p1Loading || !P1HasGameplay) return;
            if (!_hostAuthorityEnabled)
            {
                if (Wo136Holding || !W137Joiner) return;
                if (!await P1EnsureLegacyZeroAsync(state.Generation, pulse.Token)) return;
                if (_w216RolePushed != 2 && !await PushHostAuthorityRoleAsync(2, pulse.Token, state.Generation)) return;
                await P1TellLuaAsync("legacy", pulse.Token);
                return;
            }
            if (!state.Enabled)
            {
                if (!await P1EnsureOnPendingAsync(pulse.Token)) return;
                state = P1Lifecycle.Current;
            }
            if (!state.GameplaySent && !await P1Lifecycle.GameplayAsync(state.Generation, pulse.Token)) return;
            if (!await P1Lifecycle.RefreshAsync(pulse.Token)) return;
            if (P1Lifecycle.Current.Active) await P1TellLuaAsync("active", pulse.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.WriteLine($"MP-P1 lifecycle tick: {ex.GetType().Name}"); }
        finally { Interlocked.Exchange(ref _p1TickBusy, 0); }
    }

    private async Task<bool> P1EnsureLegacyZeroAsync(ulong generation, CancellationToken ct)
    {
        if (_p1LegacyZeroGeneration == generation) return true;
        if (!await PushHostAuthorityRoleAsync(0, ct, generation)) return false;
        if (generation != P1Lifecycle.Current.Generation) return false;
        _p1LegacyZeroGeneration = generation; return true;
    }

    private void P1OnPostLoadTimeout() => T3Increment("postload_timeouts");
}
