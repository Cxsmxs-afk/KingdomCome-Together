// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace KcdMp.Client;

/// <summary>
/// WO-124 -- the join, on the joiner's side (docs/WO-124-findings.md). Dormant:
/// nothing here runs unless the HOST's mp_shared_world is on.
///
/// The host announces its session mode to every peer (JoinStatus state
/// "session", joinId 0: on connect, on a new peer, on a toggle and every 30 s).
/// A joiner takes the mode from the host. Then:
///   * at the main menu: the agent asks for the world by itself (WO-123 does
///     the pause, the save and the transfer);
///   * already in a private world: the player can ask to join from the mod menu;
///   * the world arrives (verified by WO-123) -> the joiner's own Henry is
///     spliced in (WhsSave.Splice + Check + Verify) from their newest own save
///     (or mp_join_henry) -> the result is placed as a transient
///     mpworld&lt;joinId&gt;.whs in that save's playline, read back, the engine's
///     cached save list is rescanned natively and the file must be listed ->
///     wh_sys_LoadGame from the menu;
///   * after "Gameplay started", keep the verified file through the save lock,
///     read back, the death guard, the Henry check (money + items against the
///     source), the teleport beside the host, and Ready (the host resumes).
///     Only join=in or the player's cancel authorizes its removal.
/// Every failure sends JoinAbort (the host resumes) and leaves the joiner
/// where it can go on: at the menu before the load; after it, back in their
/// own newest save (KCD2 1.5.5 has no safe exit to the main menu: console
/// `disconnect` crashes the game (observed) and the menu's Quit ends the
/// process (code-verified)).
/// Save names in logs are playlineN/file only.
/// </summary>
public partial class GameBridge
{
    // ---------------------------------------------------------------- session mode

    private volatile bool _hostSharedWorld;
    private volatile bool _hostModeKnown;
    private volatile byte _hostModeFrom = 0xFF;
    private sealed record HostRun(uint Epoch, Guid Run);
    private HostRun? _absentHostRun, _lastHostRun;
    private int _hostAbsentRefusalLogged;
    // Keep the save lock while the engine may still be rendering the host's world.
    private volatile bool _returningFromSharedWorld;
    internal bool HostAbsentPause => Volatile.Read(ref _absentHostRun) is not null;
    private readonly ConcurrentDictionary<byte, bool> _modeTold = new();
    private DateTime _modeBroadcastUtc = DateTime.MinValue;
    private bool _modeEverShared;

    /// <summary>The session's mode as this machine should act on it: the host's word once heard, else the local toggle (older hosts, WO-122 tests).</summary>
    private bool JoinerSharedEffective => _hostModeKnown ? _hostSharedWorld : _sharedWorld;

    // ---------------------------------------------------------------- where the game is

    private enum GameWhere { Unknown, Menu, Loading, World }
    private volatile GameWhere _where = GameWhere.Unknown;
    private DateTime _whereAskedUtc = DateTime.MinValue;

    // ---------------------------------------------------------------- the joiner's join

    /// <summary>This game's world is (true) or is no longer (false) the host's; the WO-122 saves watch follows it.</summary>
    private void SetJoinedWorld(bool on)
    {
        if (_joinedWorld == on) return;
        _joinedWorld = on;
        if (!on)
        {
            Wo211StopHoldClock(Interlocked.Read(ref _w211HoldSerial));
            Volatile.Write(ref _joinedWorldRefresh, null);
            ReleaseJoinedCharacter();
            _ = Wo134InvalidateLedgerAsync("left-host-world");
            _ = ResetHostCalendarAsync("left-host-world");
        }
        Wo122EnsureWatcher();
    }

    private sealed class JoinerJoin
    {
        public uint JoinId;
        public byte Host;
        public uint WorldSavedSeq;
        public uint HostWorldEpoch;
        public JoinRetryBlock? RetryScope;
        public JoinCharacterSelection? Character;
        public WorldRunOffer? RunOffer;
        public HenryCharacterScope? CharacterScope;
        public HenrySource? Source;
        public string? SavesRoot;
        public string? PlacedPath;   // the reserved destination, verified separately after publication
        public bool PlacedVerified;
        public int Playline;
        public string Name = "";
        public byte[]? SplicedSha;
        public WhsSave.PlayerSoul? Henry;          // the spliced file's Henry (= the source's, minus quest items)
        public DateTime LoadCmdUtc, LoadStartUtc, LoadGameUtc, GameplayUtc;
        public volatile bool LoadDispatchInFlight;
        public JoinEngineReceipt? EngineReceipt;
        public LauncherGuestWorld? LauncherGuestScope;
        public bool LauncherGuestScopeWasInitial;
        public int RemovalScheduled;
        public readonly TaskCompletionSource DispatchEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ulong LoadGeneration;
        public long CalendarGeneration;
        public long CalendarPipeRevision;
        public bool CalendarVerified;
        public bool ReadyHandoffStarted;
        public volatile string Phase = "preparing";
        public bool InWorld;                        // WO-125: an in-world rejoin (a host reload) or a join from the own world
        public bool Frozen;                         // WO-154 4.2: the load froze (busy, no CPU): the player is told to restart the game
        public string Mode = "bring";               // WO-125: bring | fresh | restore
        public string? WorldTag;
        public WhsSave.HenryParts? SplicedParts;
        public byte[]? OfferMd5;
        public TaskCompletionSource<bool>? LoadStarted, GameplayStarted, LoadFailed;
        public readonly TaskCompletionSource<bool> Abandoned = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private JoinerJoin? _jj;
    private readonly JoinWorldFiles<JoinerJoin> _retainedJoinWorlds = new();
    private volatile bool _joinedWorld;            // this game's loaded world is the host's (after a join, until it leaves)
    private HenrySource? _joinedSource;            // the save to go back to when leaving
    private string? _joinedSourceFingerprint;
    private volatile bool _ownLoadExpected;        // our own "leave" load is running
    private bool OwnLoadExpected => LoadSequenceEnabled
        ? _worldLoad.Current.OwnIntent == WorldLoadOwnIntent.ExpectedPrivate : _ownLoadExpected;
    private int _autoRequests;
    private DateTime _autoNextUtc = DateTime.MinValue;
    private sealed record JoinRetryBlock(Stream? Link, byte Host, uint World, string? Tag, string? Choice);
    private JoinRetryBlock? _blockedJoinRetry;
    private volatile bool _needsMenuTold;
    private volatile bool _gameQuitting;           // "CSystem::Quit invoked" seen; cleared by the next connection
    private string _henryOverride = "auto";        // mp_join_henry
    private Dictionary<string, string>? _questClasses;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _wo124Replies = new();

    public sealed record HenrySource(int Playline, string File, string FullPath, long SaveTime)
    {
        public string Display => $"playline{Playline}/{File}";
        public string Base => Path.GetFileNameWithoutExtension(File);
    }

    // ---------------------------------------------------------------- lifecycle

    /// <summary>KCDMP_JOIN_SAVES_DIR (tests) or the engine's saves folder.</summary>
    private static string? ResolveSavesDirForJoin() =>
        Environment.GetEnvironmentVariable("KCDMP_JOIN_SAVES_DIR") is { Length: > 0 } d ? d : ResolveSavesDir() ?? Wo159Rules.CreateSavesDirIfGameRan();   // WO-159: a new player's first join

    private void Wo124OnConnect(Stream stream, CancellationToken ct)
    {
        _modeTold.Clear();
        _modeBroadcastUtc = DateTime.MinValue;
        _hostModeKnown = false;
        lock (_joinGate) ResetJoinSessionState("connect", suppressAutomatic: false);
        _autoNextUtc = DateTime.UtcNow.AddSeconds(2);
        _gameQuitting = false;
        _ = ExecLuaAsync("if KCD2MP_Wo124CfgEmit then KCD2MP_Wo124CfgEmit() end");
        _ = ExecLuaAsync("if KCD2MP_W171Emit then KCD2MP_W171Emit() end");   // WO-171: the marker's style / offset / flip as the mod has them
        _ = Wo124LoopAsync(ct);
    }

    // Retry policy is separate from ownership of an in-flight engine load.
    // Called with the join gate; an explicit action opens a fresh attempt.
    private void ResetJoinSessionState(string reason, bool suppressAutomatic)
    {
        _autoRequests = suppressAutomatic ? 99 : 0;
        _autoNextUtc = DateTime.MinValue;
        _needsMenuTold = false;
        _rejoinPending = false;
        Wo202RejoinReset(reason);
        if (!suppressAutomatic) Volatile.Write(ref _blockedJoinRetry, null);
    }

    private async Task Wo124OnDisconnectAsync()
    {
        _hostModeKnown = false;
        var join = _jj;
        if ((join is not null || _rejoinPending) && W202("rejoin_recover")) Wo207ArmLinkRejoin(join?.JoinId ?? 0);   // WO-207 C.4
        if (LinkHeld) return;   // WO-211 A.1: let the link hold decide between reconnect and leave
        const string message = "Lost the connection to your host.";
        if (join is { Phase: "preparing" })
            await AbortJoinerJoinAsync(join, Protocol.JoinAbortIo, "relay-lost", message, sendAbort: false);
        else if (join is not null)
            StartOrphanedJoinCleanup(join, "relay-lost", message);
        else if (_joinedWorld)
            await LeaveSharedWorldAsync("relay-lost", message);
    }

    private async Task Wo124LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct); } catch { return; }
            try
            {
                if (!_combatRoleApplied) continue;
                if (_isDamageAuthority) { Wo154HostJoinPanel(_hostJoin); await Wo124HostModeTickAsync(); continue; }   // WO-154 4.1: the hold's panel
                // Joiner: learn where the game is (the mod answers wo124_where).
                if (_where == GameWhere.Unknown && (DateTime.UtcNow - _whereAskedUtc).TotalSeconds >= 5)
                {
                    _whereAskedUtc = DateTime.UtcNow;
                    await ExecLuaAsync("if KCD2MP_Wo124Where then KCD2MP_Wo124Where() end");
                }
                await Wo124JoinerTickAsync();
            }
            catch (Exception ex) { Console.WriteLine($"MP-JOIN wo124 tick failed: {ex.Message}"); }
        }
    }

    // ---------------------------------------------------------------- host: the mode announcement

    private async Task Wo124HostModeTickAsync()
    {
        // Dormant: a host that never ran a shared world says nothing (peers then
        // keep their own toggle, as before WO-124); once it has, it keeps saying
        // which mode it runs, so a flip back to separate reaches every joiner.
        if (_sharedWorld) _modeEverShared = true;
        if (!_modeEverShared) return;
        Wo125HostTick();   // WO-125: an unknown world is identified by one world save
        // WO-125: silent while a load runs. The status carries the world's identity; sent mid-load it names
        // the world being left, and a joiner rewinding with the host would rejoin THAT one (observed with the
        // synthetic host). The next tick after "Gameplay started" announces the loaded world.
        if (_hostLoadAnnounced) return;
        bool resendAll = (DateTime.UtcNow - _modeBroadcastUtc).TotalSeconds >= 30;
        var peers = LivePartners();   // WO-144: the relay's connections, never a stale name
        foreach (byte g in peers)
        {
            if (!resendAll && _modeTold.TryGetValue(g, out bool told) && told == _sharedWorld) continue;
            try
            {
                var (seed, flags) = _sharedWorld ? Wo125SessionIdentity() : (0u, (ushort)0);   // WO-125: the world's identity rides along
                // WO-157: the host's install key first, so a joiner has it when the world's identity arrives.
                if (_sharedWorld) await WriteJoinAsync(JoinStatusCodec.Build(g, Wo157InstallKey(), Protocol.JoinStateHostKey, Protocol.JoinReasonId("host-key"), 0));
                await WriteJoinAsync(JoinStatusCodec.Build(g, seed, Protocol.JoinStateSession, Protocol.JoinReasonId(_sharedWorld ? "shared-world" : "separate"), flags));
                if (!_modeTold.TryGetValue(g, out bool was) || was != _sharedWorld)
                    Console.WriteLine($"MP-JOIN host: session mode {(_sharedWorld ? "shared-world" : "separate")} -> ghost {g}");
                _modeTold[g] = _sharedWorld;
            }
            catch (Exception ex) { Console.WriteLine($"MP-JOIN host: session mode not sent to ghost {g}: {ex.Message}"); }
        }
        if (resendAll) _modeBroadcastUtc = DateTime.UtcNow;
    }

    /// <summary>A toggle flip on the host: tell every peer at the next tick.</summary>
    private void Wo124OnSharedWorldChanged() => _modeTold.Clear();

    /// <summary>JoinStatus state "session" from the host (WO-125: the joinId slot carries the world's seed, arg its flags).</summary>
    private void Wo124OnSessionMode(byte src, byte reason, uint joinId = 0, ushort arg = 0)
    {
        Wo127NoteHostMode(src);
        bool on = Protocol.JoinReasonName(reason) == "shared-world";
        bool changed = !_hostModeKnown || _hostSharedWorld != on || _hostModeFrom != src;
        _hostSharedWorld = on;
        _hostModeFrom = src;
        if (on) Wo125OnSessionIdentity(joinId, arg);
        _hostModeKnown = true;
        if (!changed) return;
        Console.WriteLine($"MP-JOIN joiner: the host (ghost {src}) runs {(on ? "a SHARED WORLD" : "separate worlds")} -- this machine follows (local mp_shared_world={On(_sharedWorld)} ignored while connected)");
        _ = ExecLuaAsync($"if KCD2MP_Wo124SessionMode then KCD2MP_Wo124SessionMode({(on ? "true" : "false")}) end");
        if (!on)
        {
            _needsMenuTold = false;
            if (_joinUiState is "waiting" or "own-world" or "no-save") SetJoinUi("idle", "");
            // This game is in the host's world and the host stopped sharing it: without
            // the lock it could now be saved into this player's own playline. Leave it.
            if (_joinedWorld && !HostRestartAwaitingLeave) _ = LeaveSharedWorldAsync("host-separate", "Your host turned the shared world off.");
        }
    }

    // ---------------------------------------------------------------- joiner: where the game is

    /// <summary>wo124_where menu|world|loading (the mod, asked by this agent).</summary>
    private void Wo124OnWhere(string? arg)
    {
        var w = (arg ?? "").Trim() switch { "menu" => GameWhere.Menu, "world" => GameWhere.World, "loading" => GameWhere.Loading, _ => GameWhere.Unknown };
        if (w == GameWhere.Unknown) return;
        if (_where != w) Console.WriteLine($"MP-JOIN joiner: the game is at {(w == GameWhere.Menu ? "the MAIN MENU" : w == GameWhere.World ? "a loaded world" : "a load")} (asked the mod)");
        _where = w;
        LoadSequenceObservePlace(w);
        if (w == GameWhere.World) W160WorldConfirmed(); else W160WorldGone();   // WO-160 2: the mod's own word is the proof of a world
    }

    /// <summary>"[CryAction] LoadGame: '...'" -- the save's data is being read (from the menu: after the level load).</summary>
    private void Wo124OnLoadStarted()
    {
        if (_transport is LogTailGameTransport engine && EngineNoticeBelongsToAnotherJoin(engine.EngineReadSave)) return;
        _where = GameWhere.Loading;
        W160WorldGone();
        if (!LoadSequenceEnabled) Wo136OnLoadSeen("load");
        if (_combatRoleApplied && _isDamageAuthority) Wo206HostWorldLoad("LoadGame");   // WO-206 E: (once per load) the host's world changes
        if (LoadSequenceEnabled)
        {
            LoadSequenceBeginLoad();
            Wo136OnLoadSeen("load");
        }
        if (_jj is { LoadStarted: not null } j && (!LoadSequenceEnabled || _worldLoad.IsCurrent(j.LoadGeneration)) &&
            (j.EngineReceipt is null || _transport is LogTailGameTransport tail && j.EngineReceipt.Matches(tail.EngineReadSave)))
        {
            if (j.LoadGameUtc == default) j.LoadGameUtc = DateTime.UtcNow;
        }
    }

    /// <summary>"Loading saved game '...playlineN/x.whs'" -- the engine accepted a load (printed at once).</summary>
    private void Wo124OnSaveLoadAccepted(string display)
    {
        if (_transport is LogTailGameTransport engine && EngineNoticeBelongsToAnotherJoin(engine.EngineAcceptedSave)) return;
        _where = GameWhere.Loading;
        W160WorldGone();
        if (!LoadSequenceEnabled) Wo136OnLoadSeen("accepted");
        if (_combatRoleApplied && _isDamageAuthority) Wo206HostWorldLoad("Loading saved game");   // WO-206 E: a new world epoch, before anything is told
        JoinerJoin? superseded = null;
        if (LoadSequenceEnabled)
        {
            // A known own save can replace an in-flight join. Other engine loading markers
            // also occur for ordinary travel and must be allowed to repeat on this generation.
            if (!_isDamageAuthority && !OwnLoadExpected && _jj is { Source: { } source } join &&
                _worldLoad.IsCurrent(join.LoadGeneration) && display.Equals(source.Display, StringComparison.OrdinalIgnoreCase))
                superseded = join;
            LoadSequenceBeginLoad(superseding: superseded is not null);
        }
        if (_combatRoleApplied && _isDamageAuthority)
        {
            if (LoadSequenceEnabled) Wo136OnLoadSeen("accepted");
            Wo125HostOnLoadAccepted(display);
            return;
        }
        if (_jj is { LoadStarted: { } t } j && (!LoadSequenceEnabled || _worldLoad.IsCurrent(j.LoadGeneration)) &&
            (j.EngineReceipt is null || _transport is LogTailGameTransport tail && j.EngineReceipt.Matches(tail.EngineAcceptedSave)) &&
            display.Equals($"playline{j.Playline}/{j.Name}.whs", StringComparison.OrdinalIgnoreCase))
        {
            if (LoadSequenceEnabled) Wo136OnLoadSeen("accepted");
            WorldReconnectNoteEngineLoad(j.LoadGeneration, j.HostWorldEpoch);
            if (j.LoadStartUtc == default) j.LoadStartUtc = DateTime.UtcNow;
            t.TrySetResult(true);
            return;
        }
        if (OwnLoadExpected) _ = ResetHostCalendarAsync("own-load");
        else if (_joinedWorld)
        {
            // A loading screen, a fast travel and a quest catch-up all print this line.
            // Leaving at once was the "you are in your own world" toast a few seconds
            // into a session. Hold. Only a save that is not the host's, still up
            // after the grace, ends the session -- and the line says why.
            bool own = SessionHold.LoadIsOwnSave(display, _joinedSource?.File);
            if (own) _ = ResetHostCalendarAsync("different-save");   // grace preserves the session, never writes the host's clock into an own save
            if (LoadSequenceEnabled)
            {
                var load = _worldLoad.Current;
                if (load.OwnIntent != WorldLoadOwnIntent.ExpectedPrivate)
                    Wo124UpdateOwnIntent(load, own ? WorldLoadOwnIntent.DifferentSaveGrace : WorldLoadOwnIntent.SharedSaveGrace,
                        Environment.TickCount64 + SessionHold.LoadGraceMs);
            }
            else
            {
                _loadGraceUntilUtc = DateTime.UtcNow.AddMilliseconds(SessionHold.LoadGraceMs);
                _loadGraceOwn = own;
            }
            Console.WriteLine(own
                ? $"MP-JOIN joiner: a different save started ({display}) -- holding the host's world for {SessionHold.LoadGraceMs / 1000} s before calling it your own"
                : $"MP-JOIN joiner: a load started ({display}) -- staying in the host's world (a loading screen is not your own save)");
        }
        if (LoadSequenceEnabled) Wo136OnLoadSeen("accepted");
        if (superseded is { } oldJoin)
        {
            oldJoin.LoadFailed?.TrySetResult(true);
            oldJoin.Abandoned.TrySetResult(true);
            _ = AbortJoinerJoinAsync(oldJoin, Protocol.JoinAbortLoadFailed, "own-save", "The join was replaced by your own save.");
        }
    }

    private DateTime _loadGraceUntilUtc;
    private bool _loadGraceOwn;
    private bool LoadGraceOwn => LoadSequenceEnabled
        ? _worldLoad.Current.OwnIntent == WorldLoadOwnIntent.DifferentSaveGrace : _loadGraceOwn;
    private bool LoadGraceActive => LoadSequenceEnabled ? _worldLoad.Current.OwnGraceActive
        : _loadGraceUntilUtc != default && DateTime.UtcNow < _loadGraceUntilUtc;

    private bool Wo124UpdateOwnIntent(WorldLoadSnapshot current, WorldLoadOwnIntent intent, long deadlineMs = 0)
    {
        if (!_worldLoad.TryUpdateOwnIntent(current, intent, deadlineMs)) return false;
        _ = LoadSequenceSyncNativeAsync();
        return true;
    }

    /// <summary>The load grace ended. A different save that is actually up leaves, and says so. Anything else stays.</summary>
    private void LoadGraceTick()
    {
        if (LoadSequenceEnabled)
        {
            var load = _worldLoad.Current;
            if (load.Phase == WorldLoadPhase.Exit ||
                load.OwnIntent is not (WorldLoadOwnIntent.DifferentSaveGrace or WorldLoadOwnIntent.SharedSaveGrace) ||
                load.OwnGraceActive) return;
            bool differentSave = load.OwnIntent == WorldLoadOwnIntent.DifferentSaveGrace;
            if (differentSave && _where == GameWhere.Loading)
            {
                Wo124UpdateOwnIntent(load, load.OwnIntent, Environment.TickCount64 + SessionHold.LoadGraceMs);
                return;
            }
            if (differentSave && _where == GameWhere.World && _joinedWorld)
            {
                if (!LoadSequenceChange(_ => _worldLoad.TryAdvance(load, WorldLoadPhase.Exit))) return;
                Console.WriteLine("MP-JOIN joiner: the other save is up and the grace ended -- leaving the host's world");
                _ = LeaveSharedWorldAsync("own-save", SessionHold.FailText("a different save finished loading"));
                return;
            }
            if (Wo124UpdateOwnIntent(load, WorldLoadOwnIntent.None))
                Console.WriteLine("MP-JOIN joiner: load grace ended -- still in the host's world");
            return;
        }
        if (_loadGraceUntilUtc == default || DateTime.UtcNow < _loadGraceUntilUtc) return;
        if (_loadGraceOwn && _where == GameWhere.Loading)
        {
            _loadGraceUntilUtc = DateTime.UtcNow.AddMilliseconds(SessionHold.LoadGraceMs);
            return;
        }
        bool own = _loadGraceOwn && _where == GameWhere.World && _joinedWorld;
        _loadGraceUntilUtc = default;
        _loadGraceOwn = false;
        if (!own)
        {
            Console.WriteLine("MP-JOIN joiner: load grace ended -- still in the host's world");
            return;
        }
        Console.WriteLine("MP-JOIN joiner: the other save is up and the grace ended -- leaving the host's world");
        _ = LeaveSharedWorldAsync("own-save", SessionHold.FailText("a different save finished loading"));
    }

    /// <summary>"Exiting to main menu because save game loading failed." -- the engine went back to the menu.</summary>
    private void Wo124OnLoadFailedToMenu() => Wo124OnLoadFailedToMenuCore(confirmedByPrivateReceipt: false);

    private void Wo124OnLoadFailedToMenuCore(bool confirmedByPrivateReceipt)
    {
        if (!confirmedByPrivateReceipt && _transport is LogTailGameTransport engine && IgnoreEngineTerminal(engine)) return;
        if (!OwnLoadExpected && _jj is { EngineReceipt: { } receipt } && !receipt.MatchesTerminal(loaded: false)) return;
        _where = GameWhere.Menu;
        LoadSequenceOnMenu();
        W160WorldGone();
        CompleteConfirmedReturn();
        _leaveInProgress = false;
        Wo206LoadSettled();   // WO-206 E: that load is over (failed); the next one bumps again
        Console.WriteLine("MP-JOIN joiner: the engine reports a failed load and is back at the MAIN MENU");
        if (_jj is { LoadFailed: { } f }) f.TrySetResult(true);
        if (_joinedWorld) { SetJoinedWorld(false); _ = Wo122SetLockAsync(false, "load-failed"); }
    }

    private void Wo124OnMainMenuShown()
    {
        if (Volatile.Read(ref _privateReturnCheck) is not null || Volatile.Read(ref _pinnedHomeLoad) is not null) return;
        if (_where != GameWhere.Menu) Console.WriteLine("MP-JOIN joiner: the main menu is up");
        _where = GameWhere.Menu;
        LoadSequenceOnMenu();
        if (HostAbsentPause || _returningFromSharedWorld)
        {
            CompleteConfirmedReturn();
            SetJoinedWorld(false);
        }
        Wo206LoadSettled();   // WO-206 E
        W160WorldGone();
        _autoNextUtc = DateTime.UtcNow.AddSeconds(3);
        if (_gameQuitting)
        {
            // A new game process (the last one quit): a fresh start for the automatic join.
            _gameQuitting = false;
            lock (_joinGate) ResetJoinSessionState("new-game", suppressAutomatic: false);
            _autoNextUtc = DateTime.UtcNow.AddSeconds(3);
        }
        // WO-125: a new process's mod starts with no session mode and the agent pushes it only on a change,
        // so an agent kept across a restart left the mod refusing the joiner's lock (observed: lock=failed).
        // Every main-menu line re-tells it (idempotent in the mod; a crash prints no quit line).
        if (_hostModeKnown)
        {
            Console.WriteLine($"MP-JOIN joiner: at the main menu -- telling the mod the host's session mode ({(_hostSharedWorld ? "shared world" : "separate")})");
            _ = ExecLuaAsync($"if KCD2MP_Wo124SessionMode then KCD2MP_Wo124SessionMode({(_hostSharedWorld ? "true" : "false")}) end");
        }
    }

    private void Wo124OnGameplayStarted() => Wo124OnGameplayStartedCore(confirmedByGameQuery: false);

    private void Wo124OnGameplayStartedCore(bool confirmedByGameQuery)
    {
        if (!confirmedByGameQuery && _transport is LogTailGameTransport engine && IgnoreEngineTerminal(engine)) return;
        if (!confirmedByGameQuery && !OwnLoadExpected && _jj is { EngineReceipt: { } receipt } &&
            !receipt.MatchesTerminal(loaded: true)) return;
        Wo136ArmLegacySettle();
        _where = GameWhere.World;
        var startedLoad = _worldLoad.Current;
        bool ownLoad = LoadSequenceEnabled ? startedLoad.OwnIntent == WorldLoadOwnIntent.ExpectedPrivate : _ownLoadExpected;
        LoadSequenceOnPhysicalGameplay();
        Wo202NoteEvent("a load (Gameplay started)");   // WO-202 II.4
        Wo207OnGameplayStarted();                      // WO-207 D.2: the joiner's NPC triggers named again for this load
        W160WorldConfirmed();   // WO-160 2: a world just loaded in this game process
        Wo136OnGameplayStarted();   // WO-136: the settle, then the held frames
        Wo206LoadSettled();         // WO-206 E: the next load bumps the world epoch again
        Wo151OnGameplayStarted();   // WO-151 3.7: the weather applied memory goes; a joiner re-applies the host's
        Wo125HostOnGameplayStarted();
        if (ownLoad)
        {
            if (LoadSequenceEnabled)
            {
                var load = _worldLoad.Current;
                ownLoad = load.Generation == startedLoad.Generation && load.OwnIntentRevision == startedLoad.OwnIntentRevision &&
                    Wo124UpdateOwnIntent(load, WorldLoadOwnIntent.None);
            }
            else _ownLoadExpected = false;
        }
        if (ownLoad)
        {
            CompleteConfirmedReturn();
            _leaveInProgress = false;
            Console.WriteLine("MP-JOIN joiner: back in this player's own world (Gameplay started)");
            Wo211AfterHomeLoad();
        }
        if (_jj is { GameplayStarted: { } t } j && (!LoadSequenceEnabled || _worldLoad.IsCurrent(j.LoadGeneration)))
        {
            j.GameplayUtc = DateTime.UtcNow;
            t.TrySetResult(true);
        }
    }

    // ---------------------------------------------------------------- joiner: the tick

    private async Task Wo124JoinerTickAsync()
    {
        if (Volatile.Read(ref _privateReturnCheck) is { } pendingReturn)
        {
            if (DateTime.UtcNow - _leaveSinceUtc >= ReturnCheckDelay)
                await CheckPrivateReturnAsync(pendingReturn);
            return;
        }
        if (_w207LinkRejoin) Wo207LinkRejoinTrace();   // WO-207 C.4: which gate holds a link-cut rejoin (every 20 s)
        if (!_hostModeKnown || !_hostSharedWorld) return;
        if (_jj is not null || _joinRx is not null || _joinOutId != 0 || _gameQuitting) return;
        if (Volatile.Read(ref _blockedJoinRetry) is { } blocked && ReferenceEquals(blocked.Link, _wo122Stream)
            && blocked.Host == _hostModeFrom && blocked.World == _w206.World && blocked.Tag == _peerTag
            && blocked.Choice == CurrentChoice()) return;
        // WO-125: nothing is asked while this game is leaving a world (its own load not yet in). Observed
        // before this gate: a join asked during the leave's 4 s notice raced the leave's own load, and the
        // own load landed last -- the agent believed the joiner was in the host's world.
        if (_leaveInProgress)
        {
            if (Volatile.Read(ref _pinnedHomeLoad) is not null) return;
            // WO-207 C.4: a leave the link cut short may have loaded while the agent was reconnecting (its "Gameplay started" never seen);
            // the mod's last-loaded save says whether it is in
            if (_w207LinkRejoin && await Wo207LeaveLandedAsync()) CompleteConfirmedReturn();
            else
            {
                if ((DateTime.UtcNow - _leaveSinceUtc).TotalMinutes < 5) return;
                if (_returningFromSharedWorld) return;
                Console.WriteLine("MP-HENRY joiner: the leave's own load never reported in (5 min) -- the join gate opens again");
            }
            _leaveInProgress = false;
        }
        if (_joinedWorld)
        {
            // WO-125 Phase 6: the host reloaded this world -- rejoin from inside it (the host defers while it loads).
            if (!_rejoinPending || DateTime.UtcNow < _autoNextUtc) return;
            if (W202("rejoin_recover"))
            {
                // WO-202 III.3: a rejoin that failed is tried again by itself (3 times, the gaps growing); after that the player is
                // asked, never sent to the main menu without a word
                if (_w202RejoinAsked) return;
                if (Wo202Rules.RejoinShouldAsk(_w202RejoinFails)) { await Wo202AskRejoinAsync(); return; }
            }
            else if ((DateTime.UtcNow - _rejoinSinceUtc).TotalMinutes > 5)
            {
                _rejoinPending = false;
                await LeaveSharedWorldAsync("rejoin-timeout", "Could not rejoin your host after its reload.");
                return;
            }
            if (!T3AdmitInWorldRejoin()) return;
            _autoNextUtc = DateTime.UtcNow.AddSeconds(W202("rejoin_recover") ? Wo202Rules.RejoinGapS(_w202RejoinFails) : 20);
            Console.WriteLine("MP-HENRY joiner: asking the host for its reloaded world (an in-world rejoin)");
            await SendJoinRequestAsync();
            return;
        }
        bool fromWorld = _where == GameWhere.World && _joinFromWorldOnce;   // WO-125: after leaving for the host's new world
        // WO-160 2: "your host is in a shared world, quit and start again" needs the mod's own word that a world is loaded (the agent's
        // _where outlives a game that crashed without a quit line: the field's new process was still at its main menu)
        if (_where == GameWhere.World && !fromWorld && !_needsMenuTold && !Wo160Rules.NeedsMenuToldAllowed(W160ConfirmedAgeMs()))
        {
            await W160AskWhereIfDueAsync();
            return;
        }
        if (_where == GameWhere.World && !fromWorld && !_needsMenuTold)
        {
            _needsMenuTold = true;
            const string msg = "Your host is in a shared world. Choose Join / Rejoin your host in the co-op menu.";
            Console.WriteLine("MP-JOIN joiner: the host is in a shared world but this game is already in a world -- no join asked; telling the player");
            SetJoinUi("own-world", msg);
            await ExecLuaAsync($"if KCD2MP_Wo124Msg then KCD2MP_Wo124Msg(\"{EscapeLua(msg)}\") end");
            return;
        }
        // WO-207 C.4: a link-cut rejoin whose own world's "Gameplay started" went by unseen: the mod says where the game is
        if (_w207LinkRejoin && _joinFromWorldOnce && _where != GameWhere.World && _where != GameWhere.Menu) await W160AskWhereIfDueAsync();
        if ((_where != GameWhere.Menu && !fromWorld) || DateTime.UtcNow < _autoNextUtc) return;
        if (_autoRequests >= 5) return;   // five automatic tries per connection; mp_join_request always works
        if (fromWorld && _w207LinkRejoin) { _w207LinkRejoin = false; Console.WriteLine("MP-W202 rejoin: the relay is back -- asking for the host's world again from this game's own world (WO-207 C.4)"); }
        // WO-125: the host's world must be known and a Henry world; a first join needs the player's choice
        // and a usable source BEFORE the request, so the host is never paused while someone decides.
        if (!Wo125ReadyToRequest(out string why))
        {
            _autoNextUtc = DateTime.UtcNow.AddSeconds(2);
            return;
        }
        _autoRequests++;
        _autoNextUtc = DateTime.UtcNow.AddSeconds(30);
        if (fromWorld) _joinFromWorldOnce = false;
        string what = CurrentChoice() is null ? "restoring the paired character" : "using the selected character";
        Console.WriteLine($"MP-JOIN joiner: {(fromWorld ? "in its own world after the host changed worlds" : "at the main menu")} with a shared-world host -- asking for the world (auto, try {_autoRequests}; {what})");
        await SendJoinRequestAsync();
        if (_joinOutId != 0) SetJoinUi("waiting", "Waiting for your host...");
    }

    private const string NoOwnSaveMessage = "Start a game of your own first, so your character can come with you.";

    // ---------------------------------------------------------------- Phase 1: which Henry

    /// <summary>Every engine-named save in playline0..4, newest SaveTime first.</summary>
    public static List<HenrySource> ListOwnSaves(string saves)
    {
        var o = new List<HenrySource>();
        for (int pl = 0; pl <= 4; pl++)
        {
            string dir = Path.Combine(saves, $"playline{pl}");
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.whs"))
            {
                string name = Path.GetFileName(f);
                if (name.StartsWith("mpworld", StringComparison.OrdinalIgnoreCase)) continue;
                if (WorldSaved.ParsePath(f) is null) continue;   // engine names only
                if (ReadSaveTime(f) is long t) o.Add(new HenrySource(pl, name, f, t));
            }
        }
        return o.OrderByDescending(s => s.SaveTime).ThenBy(s => s.Display, StringComparer.Ordinal).ToList();
    }

    /// <summary>SaveTime="unix seconds" from a save's description header (the first bytes only).</summary>
    public static long? ReadSaveTime(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var head = new byte[8];
            if (fs.Read(head, 0, 8) != 8 || BitConverter.ToUInt32(head, 0) != 0xFFFFFFFFu) return null;
            int n = BitConverter.ToInt32(head, 4);
            if (n <= 0 || n > 64 * 1024) return null;
            var desc = new byte[n];
            int got = 0;
            while (got < n) { int r = fs.Read(desc, got, n - got); if (r <= 0) return null; got += r; }
            var m = Regex.Match(Encoding.UTF8.GetString(desc), @"\bSaveTime=""(\d+)""");
            return m.Success ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // ---------------------------------------------------------------- Phase 2+3: splice, place, load

    /// <summary>WO-123 received and verified the world (Done sent). Called from the chunk handler.</summary>
    private Task Wo124OnWorldReceivedAsync(uint joinId, byte host, string stagedPath, uint seq, uint hostWorldEpoch, WorldRunOffer? runOffer = null)
    {
        if (!JoinerSharedEffective) return Task.CompletedTask;
        var request = Volatile.Read(ref _joinCalendarRequest);
        var character = request is not null && request.JoinId == joinId && request.Host == host &&
            ReferenceEquals(request.Link, _wo122Stream) ? request.Character : null;
        var j = new JoinerJoin
        {
            JoinId = joinId, Host = host, WorldSavedSeq = seq, HostWorldEpoch = hostWorldEpoch,
            InWorld = _where == GameWhere.World, OfferMd5 = _joinReceivedMd5,
            Character = character,
            RunOffer = runOffer,
            RetryScope = new(_wo122Stream, host, hostWorldEpoch, _peerTag, character?.Intent?.Key)
        };
        lock (_joinGate)
        {
            LoadSequenceBeginOffer(j.JoinId, j.HostWorldEpoch);
            j.LoadGeneration = _worldLoad.Current.Generation;
            _jj = j;
        }
        // Never awaited by the frame loop: observed, awaiting it held every relay
        // frame (the host's positions, its aborts) for the whole ~50 s load.
        _ = Task.Run(() => RunJoinerJoinAsync(j, stagedPath));
        return Task.CompletedTask;
    }

    private bool Wo124JoinGone(JoinerJoin j, string at)
    {
        if (j.Abandoned.Task.IsCompleted || LoadSequenceEnabled && j.LoadGeneration != _worldLoad.Current.Generation)
            return true;
        return Wo206JoinGone(j, at);
    }

    private async Task RunJoinerJoinAsync(JoinerJoin j, string stagedPath)
    {
        var t0 = DateTime.UtcNow;
        try
        {
            SetJoinUi("preparing", "Preparing your character...");
            string? saves = ResolveSavesDirForJoin();
            string? tables = TablesPakPath();
            if (saves is null || tables is null)
            {
                Console.WriteLine($"MP-JOIN joiner: join 0x{j.JoinId:x8}: {(saves is null ? "no saves folder" : "Tables.pak not found beside kcd.log")} -- abort");
                await AbortJoinerJoinAsync(j, Protocol.JoinAbortSpliceFailed, "splice-failed", "Your character could not be prepared.");
                return;
            }
            byte[] hostBytes = WhsSave.ReadShared(stagedPath);

            // ---- Phase 1 (WO-125): which Henry. The world's own (restore) or the first-join choice
            // (bring / fresh); the world must be the one announced and a Henry world.
            var choice = Wo125HenryForWorld(j, hostBytes, out string why, out byte abortWhy, out string worldTag);
            if (choice is null)
            {
                Console.WriteLine($"MP-JOIN joiner: join 0x{j.JoinId:x8}: {why}");
                string msg = abortWhy switch
                {
                    Protocol.JoinAbortNotHenry => "Your host is in a part of the story where you can't join yet.",
                    Protocol.JoinAbortWorldChanged => "Your host changed worlds -- trying again in a moment.",
                    Protocol.JoinAbortNoOwnSave => NoOwnSaveMessage,
                    _ => why.EndsWith('.') ? why : "Your character could not be prepared.",
                };
                if (j.InWorld) await LeaveAfterFailedJoinAsync(j, abortWhy, Protocol.JoinAbortName(abortWhy), msg);
                else await AbortJoinerJoinAsync(j, abortWhy, abortWhy == Protocol.JoinAbortNoOwnSave ? "no-own-save" : Protocol.JoinAbortName(abortWhy), msg);
                return;
            }
            j.Source = choice.Save;
            j.Mode = choice.Mode;
            j.WorldTag = worldTag;

            // ---- Phase 2: splice + check + verify, in memory
            _questClasses ??= WhsSave.QuestClasses(tables);
            var ts = DateTime.UtcNow;
            WhsSave.SpliceResult res;
            List<string> fails;
            try
            {
                res = WhsSave.SpliceParts(hostBytes, choice.Parts, _questClasses, WhsSave.QuestItemMode.Strip);
                fails = WhsSave.CheckParts(hostBytes, choice.Parts, res.File, _questClasses, WhsSave.QuestItemMode.Strip);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException)
            {
                Console.WriteLine($"MP-JOIN joiner: join 0x{j.JoinId:x8}: splice REFUSED: {ex.Message}");
                await AbortJoinerJoinAsync(j, Protocol.JoinAbortSpliceFailed, "splice-failed", "Your character could not be brought into your host's world.");
                return;
            }
            var ver = WhsSave.Verify(res.File);
            double spliceMs = (DateTime.UtcNow - ts).TotalMilliseconds;
            if (fails.Count > 0 || !ver.Ok)
            {
                foreach (var f in fails.Take(8)) Console.WriteLine($"MP-JOIN joiner:   check FAIL {f}");
                Console.WriteLine($"MP-JOIN joiner: join 0x{j.JoinId:x8}: splice check {fails.Count} failure(s), verify {(ver.Ok ? "ok" : ver.Reason)} -- abort, nothing placed");
                await AbortJoinerJoinAsync(j, Protocol.JoinAbortSpliceFailed, "splice-failed", "Your character could not be brought into your host's world.");
                return;
            }
            var rp = res.Report;
            var splicedRaw = WhsSave.Inflate(res.File).Raw;
            j.Henry = WhsSave.DecodePlayerSoul(splicedRaw, WhsSave.FindSoul(splicedRaw, WhsSave.HenrySoul)!.Value);
            // WO-125: the Henry exactly as loaded -- the join save pairs with it after Ready.
            j.SplicedParts = WhsSave.PartsFromStream(splicedRaw, choice.Parts.Build, WhsSave.HenryParts.OriginSnapshot);
            Console.WriteLine(FormattableString.Invariant(
                $"MP-JOIN joiner: join 0x{j.JoinId:x8} spliced world={hostBytes.Length} B + henry={choice.Detail} ({choice.Mode}) -> {res.File.Length} B in {spliceMs:F0} ms; check PASS, verify ok; quest items stripped={rp.QuestItemsRemoved.Count}, keys host_kept={rp.KeysHostKept} joiner_added={rp.KeysJoinerAdded}"));
            try { File.Delete(stagedPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Faults.ReportThrottled("join-staging-cleanup", ex);
            }
            if (Wo124JoinGone(j, "before placing")) return;   // WO-206 B2: an abort while the splice ran (the relay lost mid-join)

            // ---- Phase 3: place, read back, rescan, listed. WO-175 S1 / WO-180 (mp_coop_playline, on by default): in the playline
            // that already holds the host's world, else the lowest free one, never one holding this player's own saves. Off: in the playline of this
            // player's newest own save (= the menu's current playline, WO-124 s4.2); in a world: the playline it is in.
            uint? hostSeed = WhsSave.ReadSeed(splicedRaw);
            string hostTag = hostSeed is uint seed ? WhsSave.SeedTag(seed) : "";
            string reservationTag = hostSeed is uint knownSeed && _w157PeerHostKeyKnown
                ? $"{hostTag}-{WhsSave.HostWorldTag(knownSeed, _w157PeerHostKey)}" : worldTag;
            Wo125Sweep("before a join");
            if (_joinWorldJournal.HasReservation(saves, reservationTag))
            {
                await AbortJoinerJoinAsync(j, Protocol.JoinAbortIo, "join-slot-still-reserved",
                    "The last join's temporary save is still reserved. Close the game before trying again.");
                return;
            }
            int? coopPl = Wo175JoinPlayline(saves, hostTag, j.JoinId);
            if (coopPl is null)
            {
                await AbortJoinerJoinAsync(j, Protocol.JoinAbortIo, "no-free-playline", Wo175Coop.JoinerNoFreeText);
                return;
            }
            j.Playline = coopPl >= 0 ? coopPl.Value
                : j.InWorld && _lastWorldPlayline >= 0 ? _lastWorldPlayline : Wo125NewestOwn(out _)?.Playline ?? choice.Save?.Playline ?? 0;
            _lastWorldPlayline = j.Playline;
            j.Name = $"mpworld{j.JoinId:x8}";
            string dir = Path.Combine(saves, $"playline{j.Playline}");
            string final = Path.Combine(dir, j.Name + ".whs");
            if (!TryPublishJoinWorld(j, saves, final, res.File, reservationTag)) return;
            byte[] placedSha = j.SplicedSha ?? throw new InvalidOperationException("published join save has no checksum");
            var back = WhsSave.ReadShared(final);
            if (!SHA256.HashData(back).AsSpan().SequenceEqual(placedSha) || !WhsSave.Verify(back).Ok)
            {
                Console.WriteLine($"MP-JOIN joiner: join 0x{j.JoinId:x8}: the placed file does not read back as written -- abort");
                await AbortJoinerJoinAsync(j, Protocol.JoinAbortIo, "load-failed", "Your host's world could not be placed.");
                return;
            }
            Console.WriteLine($"MP-JOIN joiner: join 0x{j.JoinId:x8} placed {SaveDisplay(final)} ({back.Length} B, read back: sha256 = the splice, verify ok)");
            lock (_joinGate)
            {
                if (!ReferenceEquals(_jj, j) || j.Abandoned.Task.IsCompleted) return;
                j.PlacedVerified = true;
            }
            if (Wo124JoinGone(j, "after placing")) { await RemovePlacedWorldAsync(j, "abandoned-after-place"); return; }
            if (coopPl >= 0) Wo175JoinPlaced(j.JoinId, hostTag, final, placedSha);
            int listAttempt = 0;
            var listed = await JoinSaveList.WaitAsync(async (op, ct) =>
            {
                var report = await _combat.SaveListAsync(op, j.Playline, j.Name, ct);
                Console.WriteLine($"MP-JOIN joiner: join 0x{j.JoinId:x8} list attempt={++listAttempt} op={op} file={SaveDisplay(final)} listed={(report is null ? "unreadable" : On(report.Listed))}; the verified world stays in place");
                return report;
            }, () => ReferenceEquals(_jj, j) && j.Phase == "preparing");
            if (Wo124JoinGone(j, "after listing") || j.Phase != "preparing") return;
            if (listed is null || !listed.Listed)
            {
                Console.WriteLine($"MP-JOIN joiner: join 0x{j.JoinId:x8}: the native list did not discover the placed world after {listAttempt} attempts -- abort before load");
                await AbortJoinerJoinAsync(j, Protocol.JoinAbortLoadFailed, "load-failed", "Your host's world could not be loaded.");
                return;
            }
            Console.WriteLine($"MP-JOIN joiner: join 0x{j.JoinId:x8} rescan: {j.Name} is listed (idx {listed.Idx} of {listed.Count} in playline{j.Playline}; current playline {listed.Current}, Continue would load playline{listed.ContinuePlayline}/{listed.ContinueName})");

            // ---- the load, from the menu
            // WO-154 4.2 (live L7, the field's hung join): a load started while the main menu's video plays can freeze for
            // good -- the render thread waits inside the video player for a frame its stopped decode threads never finish,
            // the main thread waits for the render thread at the loading screen. The video is stopped first (the game's own
            // wh_ui_StopMovie: the menu stays, its background goes black) and the load starts once the renderer is past it.
            if (_w154JoinStopVideo && !j.InWorld)
            {
                await ExecLuaAsync("System.ExecuteCommand(\"wh_ui_StopMovie\")");
                Interlocked.Increment(ref _w154JoinVideoStops);
                Console.WriteLine($"MP-JOIN joiner: join 0x{j.JoinId:x8}: the main menu's video stopped (wh_ui_StopMovie) -- the load starts in {W154StopVideoSettleMs / 1000.0:F1} s");
                await Task.Delay(W154StopVideoSettleMs);
            }
            // WO-177 A5: never a load while a fight is running (a join from inside a world waits for it to end)
            if (!await Wo177WaitQuietAsync("join"))
            {
                await AbortJoinerJoinAsync(j, Protocol.JoinAbortLoadFailed, "load-fight", Wo177Rules.GaveUpText);
                return;
            }
            if (Wo124JoinGone(j, "before the load"))   // WO-206 B2: never load a join that was aborted while it was placed
            {
                await RemovePlacedWorldAsync(j, "abandoned-before-load");
                return;
            }
            if (!await PrepareHostAuthorityAsync(j))
            {
                string authorityReason = HostAuthorityFailure.IsReason(_hostAuthorityFailureReason) ? _hostAuthorityFailureReason : HostAuthorityFailure.NotConfirmed;
                await AbortJoinerJoinAsync(j, Protocol.JoinAbortLoadFailed, authorityReason, HostAuthorityFailure.Message(authorityReason)!);
                return;
            }
            bool calendarReady = await PrepareHostCalendarAsync(j);
            if (Wo124JoinGone(j, "after calendar prepare")) return;
            if (!calendarReady)
            {
                await AbortJoinerJoinAsync(j, Protocol.JoinAbortLoadFailed, "clock-unavailable", "Your host's world clock could not be prepared.");
                return;
            }
            bool loadIssued;
            bool ownershipRefused = false;
            try
            {
                if (Wo124JoinGone(j, "at load dispatch")) return;
                loadIssued = await P5LoadGameCoreAsync(j.Playline, j.Name, "join-command",
                    () => AuthorizeJoinCalendarLoad(j, saves, final), prepareDispatch: () =>
                    {
                        var preparedSave = PrepareJoinLoadSubmission(j, saves, final, placedSha);
                        ownershipRefused = preparedSave is null;
                        return preparedSave;
                    });
            }
            finally
            {
                j.LoadDispatchInFlight = false;
                j.DispatchEnded.TrySetResult();
            }
            if (!loadIssued)
            {
                if (Wo124JoinGone(j, "after load dispatch refusal"))
                {
                    // An abort may already have tried cleanup while the verified
                    // file was pinned for dispatch. That handle has now closed.
                    await RemovePlacedWorldAsync(j, "abandoned-before-load");
                    return;
                }
                if (ownershipRefused)
                    await AbortJoinerJoinAsync(j, Protocol.JoinAbortLoadFailed, "join-slot-ownership-unconfirmed",
                        "Your host's temporary save could not be reserved for loading.");
                else await AbortJoinerJoinAsync(j, Protocol.JoinAbortLoadFailed, "authority-pending-unconfirmed",
                    _p5LoadRefusal.Length > 0 ? _p5LoadRefusal : "The load safety gate could not be confirmed.");
                return;
            }
            // The engine prints "Loading saved game '...'" at once; from the menu it
            // then loads the level and reads the FILE only ~40 s later (the second
            // "Loading saved game" + "[CryAction] LoadGame"), so the file must stay
            // until "Gameplay started" (observed: deleting it earlier failed the
            // load and the engine went back to the menu).
            Task accepted = j.LoadStarted?.Task ?? throw new InvalidOperationException("join load was not armed");
            Task gameplay = j.GameplayStarted?.Task ?? throw new InvalidOperationException("join gameplay report was not armed");
            Task failed = j.LoadFailed?.Task ?? throw new InvalidOperationException("join load failure report was not armed");
            // WO-154 4.2: past 20 s the game is asked where it is; never given up while it is busy (mp_join_patient)
            if (await WaitForJoinLoadSignalAsync(20000, accepted, j.Abandoned.Task) != accepted
                && !(_w154JoinPatient && await Wo154WaitLoadAsync(j, "accept")))
            {
                if (Wo124JoinGone(j, "while accepting load")) return;
                Console.WriteLine($"MP-JOIN joiner: join 0x{j.JoinId:x8}: the engine did not accept wh_sys_LoadGame {j.Playline} {j.Name} {(j.Frozen ? "(the game froze)" : _w154JoinPatient ? "(the game answers, not loading)" : "within 20 s")} -- abort");
                if (j.Frozen) await AbortJoinerJoinAsync(j, Protocol.JoinAbortLoadFailed, "load-frozen", Wo154Rules.FrozenLoadText);
                else await AbortJoinerJoinAsync(j, Protocol.JoinAbortLoadFailed, "load-failed", "Your host's world could not be loaded.");
                return;
            }
            var endT = await WaitForJoinLoadSignalAsync(_w154JoinPatient ? 90000 : 300000, gameplay, failed, j.Abandoned.Task);
            if (Wo124JoinGone(j, "while loading")) return;
            if (endT != gameplay && !(endT != failed && _w154JoinPatient && await Wo154WaitLoadAsync(j, "gameplay")))
            {
                if (Wo124JoinGone(j, "after load wait")) return;
                Console.WriteLine($"MP-JOIN joiner: join 0x{j.JoinId:x8}: the load {(endT == failed ? "FAILED (the engine went back to the menu)" : j.Frozen ? "FROZE (busy with no CPU: the game's own freeze at the loading screen)" : "never reached \"Gameplay started\" in 300 s")} -- abort");
                if (j.Frozen) await AbortJoinerJoinAsync(j, Protocol.JoinAbortLoadFailed, "load-frozen", Wo154Rules.FrozenLoadText);
                else await AbortJoinerJoinAsync(j, Protocol.JoinAbortLoadFailed, "load-failed", "Your host's world could not be loaded.");
                return;
            }
            Console.WriteLine(FormattableString.Invariant(
                $"MP-JOIN joiner: join 0x{j.JoinId:x8} loaded: command -> accepted {(j.LoadStartUtc - j.LoadCmdUtc).TotalSeconds:F1} s, -> file read (LoadGame) {(j.LoadGameUtc == default ? double.NaN : (j.LoadGameUtc - j.LoadCmdUtc).TotalSeconds):F1} s, -> Gameplay started {(j.GameplayUtc - j.LoadCmdUtc).TotalSeconds:F1} s (received -> in world {(j.GameplayUtc - t0).TotalSeconds:F1} s)"));
            lock (_joinGate)
            {
                if (!ReferenceEquals(_jj, j) || j.Abandoned.Task.IsCompleted || j.Phase != "loading") return;
                j.Phase = "post-load";
                if (choice.Save is not null || !j.InWorld)
                {
                    _joinedSource = choice.Save;
                    _joinedSourceFingerprint = choice.Fingerprint;
                }
                Volatile.Write(ref _joinedCharacter, BindLoadedCharacter(j));
                SetJoinedWorld(true);   // the saves watch runs: a save that still lands here is a leak (QuickSave passes the lock)
            }

            // GameplayStarted alone is not a completed join: keep the retry source through PostLoad and Ready.
            await PostLoadAsync(j);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"MP-JOIN joiner: join 0x{j.JoinId:x8} failed: {ex.GetType().Name}: {ex.Message}");
            await AbortJoinerJoinAsync(j, Protocol.JoinAbortIo, "failed", "The join failed.");
        }
        finally
        {
            try { File.Delete(stagedPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string? TablesPakPath()
    {
        string? dir = Environment.GetEnvironmentVariable("KCD2MP_INSTALL");
        if (string.IsNullOrEmpty(dir)) dir = KcdLogLocator.Find() is string log ? Path.GetDirectoryName(log) : null;
        if (dir is null) return null;
        string p = Path.Combine(dir, "Data", "Tables.pak");
        return File.Exists(p) ? p : null;
    }

    private bool TryPublishJoinWorld(JoinerJoin j, string savesRoot, string final, byte[] file, string worldTag)
    {
        string directory = Path.GetDirectoryName(final)!;
        if ((File.GetAttributes(savesRoot) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("the saves root is a reparse point");
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("the join playline is a reparse point");
        byte[] sha = SHA256.HashData(file);
        bool reserved = false, moved = false;
        try
        {
            lock (_joinGate)
            {
                if (!ReferenceEquals(_jj, j) || j.Abandoned.Task.IsCompleted)
                {
                    Console.WriteLine($"MP-JOIN joiner: join 0x{j.JoinId:x8}: placement refused after attempt ended");
                    return false;
                }
                _joinWorldJournal.Register(savesRoot, final, worldTag, j.JoinId, sha);
                j.PlacedPath = final;
                reserved = true;
            }
            using (var staged = _joinWorldJournal.CreateStage(savesRoot, final, j.JoinId, sha))
            {
                staged.Write(file);
                staged.Flush(flushToDisk: true);
            }
            _joinWorldJournal.SealStage(savesRoot, final, j.JoinId, sha);
            lock (_joinGate)
            {
                if (!ReferenceEquals(_jj, j) || j.Abandoned.Task.IsCompleted)
                {
                    Console.WriteLine($"MP-JOIN joiner: join 0x{j.JoinId:x8}: placement refused after attempt ended");
                    return false;
                }
                _joinWorldJournal.Publish(savesRoot, final, j.JoinId, sha);
                moved = true;
                j.SavesRoot = savesRoot;
                j.SplicedSha = sha;
                _retainedJoinWorlds.Keep(final, j);
                return true;
            }
        }
        finally
        {
            if (reserved && !moved)
            {
                _joinWorldJournal.TryDeleteOwned(savesRoot, final, j.JoinId, sha);
                lock (_joinGate) j.PlacedPath = null;
            }
        }
    }

    private FileStream? PrepareJoinLoadSubmission(JoinerJoin join, string savesRoot, string path, byte[] sha)
    {
        if (!ReferenceEquals(_jj, join) || join.Abandoned.Task.IsCompleted || join.Phase != "preparing")
            return null;
        return _joinWorldJournal.PrepareLoadSubmission(savesRoot, path, join.JoinId, sha);
    }

    private bool TryBeginJoinLoad(JoinerJoin join, string savesRoot, string path)
    {
        if (!ReferenceEquals(_jj, join) || join.Abandoned.Task.IsCompleted || join.Phase != "preparing" ||
            !join.PlacedVerified || join.PlacedPath != path) return false;
        if (_transport is LogTailGameTransport tail)
        {
            if (!TryProtectLauncherHandoff(out var guestLease)) return false;
            try
            {
                if (PrepareLauncherProtectedReceipt(tail, path, savesRoot) is not { } receipt) return false;
                if (join.Character?.Scoped is { } character && !character.RetainForEngine(join, receipt))
                {
                    receipt.Dispose();
                    return false;
                }
                if (guestLease is not null)
                {
                    var guest = join.LauncherGuestScope = new LauncherGuestWorld(this, tail, guestLease);
                    var previousGuest = Interlocked.Exchange(ref _launcherGuestWorld, guest);
                    join.LauncherGuestScopeWasInitial = previousGuest is null && !_joinedWorld;
                    previousGuest?.Dispose();
                    guestLease = null;
                }
                receipt.ProtectSubmittedLoad();
                join.EngineReceipt = receipt;
            }
            finally { guestLease?.Dispose(); }
        }
        else if (join.Character?.Scoped is not null || !TryProtectUnobservableEngineRead()) return false;
        join.LoadDispatchInFlight = true;
        join.LoadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        join.GameplayStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        join.LoadFailed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        join.LoadCmdUtc = DateTime.UtcNow;
        join.Phase = "loading";
        return true;
    }

    private bool TryDeletePlacedJoinWorld(JoinerJoin join, string path)
    {
        if (!JoinSaveReadEnded(join) || join.SavesRoot is not { } root || join.SplicedSha is not { } sha ||
            !_retainedJoinWorlds.TryDelete(path, join, full => _joinWorldJournal.TryDeleteOwned(root, full, join.JoinId, sha))) return false;
        if (join.LauncherGuestScopeWasInitial && join.LauncherGuestScope is { } guest && join.EngineReceipt is { CanRemoveFile: true } receipt &&
            !receipt.Terminal.Task.Result) ReleaseLauncherGuestWorld(guest);
        return true;
    }

    private bool AuthorizeJoinCalendarLoad(JoinerJoin join, string savesRoot, string path)
    {
        // P5 owns the join gate for this callback and the immediate HTTP handoff.
        if (!JoinCalendarActuatorCurrent(join) || !TryBeginJoinLoad(join, savesRoot, path))
        {
            DropJoinCalendar(join.Host, join.JoinId, "load-dispatch-scope-retired");
            return false;
        }
        Wo137OnJoinLoading(join.JoinId);   // changes from before the transferred save are in it already
        SetJoinUi("loading", "Loading your host's world...");
        return true;
    }

    /// <summary>Delete the transient file and report the engine cache without refreshing it during active play.</summary>
    private async Task RemovePlacedWorldAsync(JoinerJoin j, string why)
    {
        if (j.PlacedPath is not string p) return;
        if (!_retainedJoinWorlds.MayRemove(p, j))
        {
            Console.WriteLine($"MP-JOIN joiner: join 0x{j.JoinId:x8} {why}: kept {SaveDisplay(p)} -- attempt still owns the file");
            return;
        }
        if (Wo154RemovePlacedLater(j, p)) return;
        bool gone = false;
        try
        {
            gone = TryDeletePlacedJoinWorld(j, p);
        }
        catch (Exception ex) { Console.WriteLine($"MP-JOIN joiner: could not delete {SaveDisplay(p)}: {ex.Message}"); }
        j.PlacedPath = gone ? null : p;
        var after = await _combat.SaveListAsync(3, j.Playline, j.Name);
        Console.WriteLine($"MP-JOIN joiner: join 0x{j.JoinId:x8} {why}: {SaveDisplay(p)} deleted={On(gone)}; cached listed={(after is null ? "?" : On(after.Listed))}; " +
                          $"cached Continue entry=playline{after?.ContinuePlayline}/{after?.ContinueName ?? "?"} (not refreshed)");
    }

    private async Task CleanupRetainedJoinWorldsAsync(IEnumerable<JoinerJoin> worlds, string why)
    {
        foreach (var kept in worlds)
            try { await RemovePlacedWorldAsync(kept, why); }
            catch (Exception ex) { Console.WriteLine($"MP-JOIN joiner: retained world cleanup failed: {ex.Message}"); }
    }

    // ---------------------------------------------------------------- Phase 4: in the world, before Ready

    private async Task PostLoadAsync(JoinerJoin j)
    {
        if (Wo124JoinGone(j, "before post-load") || j.Phase != "post-load") return;
        // 0. WO-125: the world in memory is the file this join placed (the engine's own record of the last
        // load). The Henry check cannot tell two worlds apart when the Henry is the same (a "bring" restore
        // equals the player's own save's Henry), so it is asked here.
        string last = await AskModAsync("KCD2MP_Wo125LastLoaded", 6000);
        string lastBase = last.StartsWith("last=", StringComparison.Ordinal) ? last[5..] : "?";
        if (lastBase is "?" or "" or "nil" or "timeout" || last == "timeout")
            Console.WriteLine($"MP-JOIN joiner: join 0x{j.JoinId:x8} step 0 loaded file: the engine's last-loaded save is not readable ({last}) -- not checked (inconclusive)");
        else if (!string.Equals(lastBase, j.Name, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"MP-JOIN joiner: join 0x{j.JoinId:x8} step 0 loaded file: the engine last loaded '{lastBase}', not {j.Name} -- this is not the host's world; leaving");
            await LeaveAfterFailedJoinAsync(j, Protocol.JoinAbortLoadFailed, "load-failed", "Your host's world did not load.");
            return;
        }
        else Console.WriteLine($"MP-JOIN joiner: join 0x{j.JoinId:x8} step 0 loaded file: {lastBase} (the engine's last load = the placed file)");

        if (!await VerifyHostCalendarAsync(j))
        {
            string why = _hostAuthorityFailureReason;
            if (HostAuthorityFailure.IsReason(why))
                await LeaveAfterFailedJoinAsync(j, Protocol.JoinAbortLoadFailed, why, HostAuthorityFailure.Message(why)!);
            else await LeaveAfterFailedJoinAsync(j, Protocol.JoinAbortLoadFailed, "clock-unavailable", "Your host's world clock could not be verified.");
            return;
        }

        // 1. the save lock, read back
        await Wo122SetLockAsync(true, "join");
        string lockR = await AskModAsync("KCD2MP_Wo124Lock", 6000);
        // WO-207: a game in its first seconds after a load can sit on a console batch past the ask (the bench: the lock held, the answer's
        // batch timed out and was retried, the join aborted) -- one more ask before giving up
        if (lockR == "timeout") { Console.WriteLine($"MP-JOIN joiner: join 0x{j.JoinId:x8} step 1 save lock: no answer in 6 s (the load's console backlog) -- asked once more"); lockR = await AskModAsync("KCD2MP_Wo124Lock", 20000); }
        Console.WriteLine($"MP-JOIN joiner: join 0x{j.JoinId:x8} step 1 save lock: {lockR}");
        if (!lockR.StartsWith("lock=held", StringComparison.Ordinal))
        {
            await LeaveAfterFailedJoinAsync(j, Protocol.JoinAbortLockFailed, "lock-failed", "Your saves could not be locked for the shared world.");
            return;
        }

        // 2. the death guard (WO-113; re-applied by the DLL every 250 ms after a load)
        (bool Session, bool Enabled, bool Applied)? g = null;
        for (int i = 0; i < 40; i++)   // up to 10 s: the DLL re-finds the player soul every 2 s after a load
        {
            g = await _combat.JoinGuardAsync();
            if (g is { Applied: true } || g is { Enabled: false }) break;
            await Task.Delay(250);
        }
        string gs = g is null ? "no answer" : $"session={On(g.Value.Session)} mp_respawn={On(g.Value.Enabled)} applied={On(g.Value.Applied)}";
        Console.WriteLine($"MP-JOIN joiner: join 0x{j.JoinId:x8} step 2 death guard: {gs}");
        if (g is not { } gg || (gg.Enabled && !gg.Applied))
        {
            await LeaveAfterFailedJoinAsync(j, Protocol.JoinAbortLockFailed, "lock-failed", "The death guard could not be set up.");
            return;
        }

        // 3. the Henry is the spliced one. WO-144 1.4: money decides; items that differ (or a
        // tutorial-era file that keeps no item list) are a warning, not an abort -- a tutorial join
        // was sent home ~1 s after its load over an item count the file could not state.
        string henry = await AskModAsync("KCD2MP_Wo124Henry", 8000);
        var (hv, hWhy) = JudgeHenry(j.Henry!, henry);
        Console.WriteLine($"MP-JOIN joiner: join 0x{j.JoinId:x8} step 3 Henry check: {hv switch { HenryVerdict.Match => "MATCH", HenryVerdict.Warning => "WARNING (joined anyway)", _ => "MISMATCH" }} -- {hWhy}");
        if (hv == HenryVerdict.Mismatch)
        {
            await LeaveAfterFailedJoinAsync(j, Protocol.JoinAbortHenryMismatch, "henry-mismatch", HenryAbortText(hWhy));
            return;
        }

        // 4. beside the host, on the ground
        var hostTarget = await T3TpPeerAsync(j.Host, TeleportSource.Join, j.HostWorldEpoch);
        var placed = hostTarget is null ? null : await T3TpPlaceAsync(hostTarget, 3.0f, TeleportSource.Join);
        Console.WriteLine($"MP-JOIN joiner: join 0x{j.JoinId:x8} step 4 beside host: {(placed is { Ok: true } ? "placed" : "refused or no fresh safe snapshot; keeps spliced spot")}");

        // 5. Ready
        var request = Volatile.Read(ref _joinCalendarRequest);
        if (request is null || !JoinReadyCurrent(j, request))
        {
            await FailReadyIfCurrentAsync();
            return;
        }
        // Prepare ownership reception BEFORE Ready: the host may answer immediately.
        if (j.WorldTag is string wt0) Wo134AfterReady(wt0);
        if (!await SendJoinerReadyAsync(j, request))
        {
            await FailReadyIfCurrentAsync();
            return;
        }
        JoinerJoin[]? completed = null;
        lock (_joinGate)
        {
            if (JoinReadyCurrent(j, request))
            {
                j.Phase = "in";
                _joinReceivedId = 0;
                SetJoinUi("in", "In your host's world.", showForMs: 10_000);
                completed = _retainedJoinWorlds.End(joined: true, cancelled: false);
                _jj = null;
                LoadSequenceOnJoinReady();
                ScheduleJoinedWorldRefresh("join", profiles: true);
            }
        }
        if (completed is null)
        {
            await FailReadyIfCurrentAsync();
            return;
        }
        if (j.WorldTag is string wt && j.SplicedParts is { } sp)
        {
            Wo125AfterReady(wt, j.Mode, sp, j.OfferMd5, j.WorldSavedSeq, j.HostWorldEpoch);
            if (j.Character?.Intent is { } intent) Interlocked.CompareExchange(ref _firstCharacter, null, intent);
        }
        _ = Wo210AskAnimAsync([], "join");
        await CleanupRetainedJoinWorldsAsync(completed, "join=in");
        T3AfterJoinedWorldCleanup();
        if (_wo122Stream is Stream markerStream) _ = P5AnnounceMarkersAsync(markerStream, _wo122Ct);
        try { await ExecLuaAsync("if KCD2MP_Wo124Msg then KCD2MP_Wo124Msg(\"Co-op: you are in your host's world.\") end"); }
        catch (Exception ex) { Console.WriteLine($"MP-JOIN joiner: joined notification failed: {ex.Message}"); }

        Task FailReadyIfCurrentAsync() =>
            ReferenceEquals(_jj, j) && j.Phase == "post-load" && !j.Abandoned.Task.IsCompleted
                ? LeaveAfterFailedJoinAsync(j, Protocol.JoinAbortIo, "ready-failed", "Your host could not confirm the join.")
                : Task.CompletedTask;
    }

    private static double Dist(float[] a, float[] b) => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));

    public enum HenryVerdict { Match, Warning, Mismatch }

    /// <summary>
    /// WO-144 1.4: the join's verdict on the live Henry. The live read incomplete, or money that
    /// differs, is a mismatch (the join is left, with a plain line saying why). Items that differ
    /// with the money matching, or a file with no item list to compare (a tutorial-era Henry,
    /// WO-132), is a warning: the join goes on.
    /// </summary>
    public static (HenryVerdict Verdict, string Why) JudgeHenry(WhsSave.PlayerSoul file, string live)
    {
        var (ok, why) = CompareHenry(file, live);
        if (ok) return (HenryVerdict.Match, why);
        if (why.StartsWith("the live read is incomplete", StringComparison.Ordinal)) return (HenryVerdict.Mismatch, why);
        // no item list at all: neither money nor items are in the file to compare
        if (!file.HasItemList) return (HenryVerdict.Warning, why + "; the file keeps no item list: money and items not compared");
        var m = Regex.Match(why, @"^money file=(-?\d+) live=(-?\d+)");
        if (!m.Success || m.Groups[1].Value != m.Groups[2].Value) return (HenryVerdict.Mismatch, why);
        // a tutorial-era Henry (the field's "0 vs 11"): the file lists his money and nothing else
        bool onlyMoney = file.Inventory.All(i => i.Class == MoneyClass || i.Class == KeyringClass);
        return (HenryVerdict.Warning, why + (onlyMoney
            ? "; the file lists only his money (a tutorial-era Henry): items not compared"
            : "; the money matches: items that differ are only a warning"));
    }

    /// <summary>WO-144 1.4: what the player reads when the Henry check sends him home.</summary>
    public static string HenryAbortText(string why) =>
        why.StartsWith("the live read is incomplete", StringComparison.Ordinal)
            ? "Your character could not be read after loading your host's world."
            : "Your character arrived in your host's world with different money than you have.";

    public const string MoneyClass = "5ef63059-322e-4e1b-abe8-926e100c770e";
    public const string KeyringClass = "b54eaa25-f0e9-425b-8b29-1fb14a71de56";

    /// <summary>
    /// The live Henry against the spliced file's: money (the file's money item
    /// amount = live GetMoney x 10) and every item class with its total amount
    /// (the live-only keyring aside). Skills are logged only: their saved XP
    /// encoding is not decoded (WO-115).
    /// </summary>
    public static (bool Ok, string Why) CompareHenry(WhsSave.PlayerSoul file, string live)
    {
        var kv = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in live.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0) kv[part[..eq]] = part[(eq + 1)..];
        }
        if (!kv.TryGetValue("money", out var ms) || !double.TryParse(ms, NumberStyles.Float, CultureInfo.InvariantCulture, out double money))
            return (false, $"the live read is incomplete ('{(live.Length > 80 ? live[..80] : live)}')");
        static int Amount(string p)
        {
            var m = Regex.Match(p, @"\bamount=(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 1;
        }
        long fileMoney = file.Inventory.Where(i => i.Class == MoneyClass).Sum(i => (long)Amount(i.Params));
        long liveMoney = (long)Math.Round(money * 10);
        var fileItems = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var i in file.Inventory.Where(i => i.Class != MoneyClass && i.Class != KeyringClass))
            fileItems[i.Class] = fileItems.GetValueOrDefault(i.Class) + Amount(i.Params);
        var liveItems = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var e in (kv.GetValueOrDefault("items") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int c = e.LastIndexOf(':');
            if (c <= 0) continue;
            string cls = e[..c].ToLowerInvariant();
            if (cls == MoneyClass || cls == KeyringClass) continue;
            liveItems[cls] = liveItems.GetValueOrDefault(cls) + (long.TryParse(e[(c + 1)..], out long a) ? a : 1);
        }
        var diff = fileItems.Keys.Union(liveItems.Keys).Where(k => fileItems.GetValueOrDefault(k) != liveItems.GetValueOrDefault(k)).ToList();
        string skills = kv.GetValueOrDefault("skills") ?? "-";
        string why = $"money file={fileMoney} live={liveMoney}; item classes file={fileItems.Count} ({fileItems.Values.Sum()}) live={liveItems.Count} ({liveItems.Values.Sum()}), differing={diff.Count}; skills (logged, not compared)={skills}";
        if (diff.Count > 0) why += "; e.g. " + string.Join(",", diff.Take(3).Select(k => $"{k[..8]} file={fileItems.GetValueOrDefault(k)} live={liveItems.GetValueOrDefault(k)}"));
        return (fileMoney == liveMoney && diff.Count == 0, why);
    }

    // ---------------------------------------------------------------- asking the mod

    /// <summary>Call a mod function with a token; its reply comes back as wo124_reply &lt;tok&gt; &lt;payload&gt;.</summary>
    private Task<string> AskModAsync(string fn, int timeoutMs) => AskModAsync(fn, timeoutMs, null);

    /// <summary>WO-183: with one more argument after the token, already a Lua literal ("..." for a string, a number).</summary>
    private async Task<string> AskModAsync(string fn, int timeoutMs, string? luaArg, CancellationToken ct = default,
        bool requireActiveLog = false)
    {
        string tok = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        if (requireActiveLog) tok = "r" + tok;
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _wo124Replies[tok] = tcs;
        try
        {
            long t0 = Environment.TickCount64;
            string call = luaArg is null ? $"{fn}(\"{tok}\")" : $"{fn}(\"{tok}\", {luaArg})";
            string lua = $"if {fn} then {call} else KCD2MP_EmitEvent(\"wo124_reply\", \"{tok} missing\") end";
            if (ct.CanBeCanceled) await GameCalls.ExecuteNowAsync(lua, ct);
            else await ExecLuaAsync(lua);
            long sent = Environment.TickCount64 - t0;
            string answer;
            try { answer = await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct); }
            catch (TimeoutException)
            {
                Console.WriteLine($"MP-ASK timeout fn={fn} tok={tok} send_ms={sent} waited_ms={Environment.TickCount64 - t0}");
                return "timeout";
            }
            ct.ThrowIfCancellationRequested();
            return answer;
        }
        finally { _wo124Replies.TryRemove(tok, out _); }
    }

    private void Wo124OnEvent(string name, string? arg)
    {
        switch (name)
        {
            case "wo124_reply":
            {
                var s = arg ?? "";
                int sp = s.IndexOf(' ');
                string tok = sp > 0 ? s[..sp] : s, payload = sp > 0 ? s[(sp + 1)..] : "";
                if (_wo124Replies.TryGetValue(tok, out var t)) t.TrySetResult(payload);
                else if (!tok.StartsWith('r')) Console.WriteLine($"MP-ASK reply tok={tok} with no ask waiting (late) -- {payload}");
                return;
            }
            case "wo124_where":
                Wo124OnWhere(arg);
                return;
            case "wo124_henry_cfg":      // mp_join_henry <auto|playlineN/file>
            {
                string v = (arg ?? "").Trim();
                _henryOverride = v == "" ? "auto" : v;
                Console.WriteLine($"MP-JOIN mp_join_henry {_henryOverride}");
                return;
            }
        }
    }

    // ---------------------------------------------------------------- failures and leaving

    private static readonly TimeSpan PrivateReturnNoticeDuration = TimeSpan.FromSeconds(4);
    internal TimeProvider PrivateReturnNoticeTime { private get; init; } = TimeProvider.System;

    /// <summary>Before the load: abort to the host (it resumes), clean up, stay at the menu.</summary>
    private async Task AbortJoinerJoinAsync(JoinerJoin j, byte reason, string why, string message, bool sendAbort = true)
    {
        bool ownsAttempt;
        lock (_joinGate)
        {
            ownsAttempt = ReferenceEquals(_jj, j);
            if (ownsAttempt)
            {
                if (j.Phase == "aborting") return;
                j.Phase = "aborting";
                j.Abandoned.TrySetResult(true);
                _retainedJoinWorlds.Cancel(j);
                Volatile.Write(ref _blockedJoinRetry, j.RetryScope ?? new(_wo122Stream, j.Host, j.HostWorldEpoch, _peerTag, CurrentChoice()));
            }
        }
        if (!ownsAttempt) { await RemovePlacedWorldAsync(j, why); return; }
        Console.WriteLine($"MP-JOIN joiner: join 0x{j.JoinId:x8}: automatic retry blocked for world {j.HostWorldEpoch} ({why})");
        if (sendAbort)
        {
            try { await WriteJoinAsync(WorldReceiver.BuildAbort(j.Host, j.JoinId, reason)); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                sendAbort = false;
                Console.WriteLine($"MP-JOIN joiner: join 0x{j.JoinId:x8}: abort not sent ({ex.GetType().Name})");
            }
        }
        await ResetJoinCalendarAfterAbortAsync(j, "join-abort");
        await RemovePlacedWorldAsync(j, why);
        lock (_joinGate)
        {
            if (!ReferenceEquals(_jj, j)) return;
            _joinReceivedId = 0;
            _jj = null;
            if (_joinCalendarRequest?.JoinId == j.JoinId) ClearJoinCharacterRequest();
            if (LoadSequenceEnabled && _worldLoad.IsCurrent(j.LoadGeneration))
            {
                LoadSequenceOnExit();
                if (_where == GameWhere.Menu) LoadSequenceOnMenu();
            }
            SetJoinUi(why == "no-own-save" ? "no-save" : "failed", message, HostAuthorityFailure.JoinReason(why));
        }
        Console.WriteLine($"MP-JOIN joiner: join 0x{j.JoinId:x8} ABORTED ({why}) -- JoinAbort {Protocol.JoinAbortName(reason)} {(sendAbort ? "sent (the host resumes)" : "not sent")}; {(j.InWorld && _joinedWorld ? "leaving the host's world" : "staying where it is")}");
        // WO-125: a failed in-world rejoin leaves the host's world (what the joiner did since the reload is not kept).
        if (j.InWorld && _joinedWorld) await LeaveSharedWorldAsync(why, message);
    }

    private async Task ResetJoinCalendarAfterAbortAsync(JoinerJoin join, string why)
    {
        try { await ResetHostCalendarAsync(why); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            Console.WriteLine($"MP-JOIN joiner: join 0x{join.JoinId:x8}: calendar reset interrupted ({ex.GetType().Name})");
        }
    }

    /// <summary>After the load: abort to the host, then leave its world (back to this player's own newest save).</summary>
    private async Task LeaveAfterFailedJoinAsync(JoinerJoin j, byte reason, string why, string message)
    {
        if (!ReferenceEquals(_jj, j)) return;
        bool abortSent = false;
        try
        {
            await WriteJoinAsync(WorldReceiver.BuildAbort(j.Host, j.JoinId, reason));
            abortSent = true;
        }
        catch (Exception ex) when (JoinControlRetired(ex)) { }
        catch (Exception ex) { Faults.ReportThrottled("join-abort-after-load", ex); }
        await ResetJoinCalendarAfterAbortAsync(j, "failed-post-load");
        Console.WriteLine($"MP-JOIN joiner: join 0x{j.JoinId:x8} ABORTED after the load ({why}) -- JoinAbort {Protocol.JoinAbortName(reason)} sent={(abortSent ? 1 : 0)}");
        lock (_joinGate)
        {
            if (!ReferenceEquals(_jj, j)) return;
            _joinReceivedId = 0;
            _retainedJoinWorlds.Cancel(j);
            _jj = null;
        }
        await RemovePlacedWorldAsync(j, why);
        if (!_w207LinkRejoin) _autoRequests = 99;   // no automatic retry into the same failure (a join the link cut is not one: WO-207 C.4)
        await LeaveSharedWorldAsync(why, message);
    }

    /// <summary>
    /// Out of the host's world: the player is told, then this player's own
    /// newest save (the join's Henry source) is loaded. KCD2 1.5.5 has no safe
    /// exit to the main menu.
    /// </summary>
    private Task LeaveSharedWorldAsync(string why, string message) => LeaveSharedWorldCoreAsync(why, message, null);
    private CancellationToken _joinEngineLifetime;
    private JoinEngineReceipt? _pinnedHomeLoad;

    private async Task LeaveSharedWorldCoreAsync(string why, string message, JoinerJoin? expected)
    {
        bool hostAbsent;
        HenrySource? selectedHome;
        string? homeFingerprint;
        lock (_joinGate)
        {
            if (_leaveInProgress || (expected is not null && !ReferenceEquals(_jj, expected))) return;
            if (!_joinedWorld && _jj is null && !_returningFromSharedWorld) return;
            ResetJoinSessionState("leave", suppressAutomatic: true);
            _leaveInProgress = true;  // The private load must settle before another join.
            _returningFromSharedWorld = true;
            hostAbsent = HostAbsentPause;
            selectedHome = expected?.Character?.First?.Save ?? _joinedSource;
            homeFingerprint = expected?.Character?.First?.Fingerprint ?? _joinedSourceFingerprint;
        }
        LoadSequenceOnExit();
        await ResetHostCalendarAsync("leave");
        _newWorldLeavePending = false;
        _leaveSinceUtc = DateTime.UtcNow;
        // WO-184 B4: one final snapshot while this game is still in the host's world (paired with the newest host save it follows); a
        // leave that cannot take it says so plainly
        string finalWhy = _joinedWorld && expected is null && !hostAbsent ? await Wo184FinalSnapshotAsync("leave (" + why + ")") : "-";
        if (finalWhy is not ("" or "-")) message += " " + Wo211Rules.SnapshotLostText(finalWhy);   // WO-211 A.2.4: never silent
        // WO-125 Phase 2: the target is recomputed now -- the newest own save whose playthrough is not the
        // host's (a hand-placed copy of the host's world is never "own"). None: the way back to the menu.
        var (src, ownWhy) = selectedHome is not null && homeFingerprint is not null
            ? await Wo125ListedHomeAsync(selectedHome)
            : await Wo125NewestOwnLoadableAsync();
        // WO-211 A.2.3: a leave after a dropped link goes home with the drop moment's Henry -- the paired final snapshot spliced into this
        // player's own newest save (his own quest items kept, the host world's left there), placed where he stood
        if (selectedHome is null && why == "relay-lost" && finalWhy == "" && src is not null && _joinedTag is string homeTag && W211Fix("drop_henry_home"))
            src = await Wo211SpliceHomeAsync(homeTag, src) ?? src;
        lock (_joinGate)
        {
            if (expected is not null && !ReferenceEquals(_jj, expected)) return;
            SetJoinedWorld(false);
            _jj = null;
            _rewinding = false;
        }
        if (!_lockHeld) await Wo122SetLockAsync(true, "returning-to-private-world");
        Wo144ReleaseClock("left-shared-world");   // WO-144 3.3: a clock standing with the host's never stays standing
        string full = src is null ? message : $"{message} Going back to your own game.";
        SetJoinUi("left", full, HostAuthorityFailure.JoinReason(why));
        Console.WriteLine($"MP-JOIN joiner: leaving the host's world ({why}) -> {(src is null ? "no own save (" + ownWhy + "): back to the main menu" : "loading " + src.Display)}");
        await ExecLuaAsync($"if KCD2MP_Wo124Msg then KCD2MP_Wo124Msg(\"{EscapeLua(full)}\") end");
        bool relayClosed = (expected is not null && why == "relay-lost") ||
            (!_wo122Connected && (hostAbsent || why == "relay-lost"));
        using var returnLife = expected is not null || relayClosed ? CancellationTokenSource.CreateLinkedTokenSource(_joinEngineLifetime) : null;
        var returnToken = returnLife?.Token ?? default;
        if (src is null)
        {
            await Task.Delay(PrivateReturnNoticeDuration, PrivateReturnNoticeTime);
            await Wo124ReturnToMenuAsync(why, message, returnToken, relayClosed);
            return;
        }
        if (!await Wo177WaitQuietAsync("leave")) { _leaveInProgress = false; SetJoinUi("left", full + " " + Wo177Rules.GaveUpText); return; }   // WO-177 A5
        await Task.Delay(PrivateReturnNoticeDuration, PrivateReturnNoticeTime);
        if (selectedHome is not null && homeFingerprint is not null)
        {
            if (_transport is LogTailGameTransport engine && ResolveSavesDirForJoin() is string savesRoot &&
                await Task.Run(() => HomeSaveReadLease.TryAcquire(savesRoot, src, homeFingerprint)) is { } fileLease)
                await Wo124LoadPinnedHomeAsync(src, savesRoot, engine, fileLease, returnToken);
            else
            {
                Console.WriteLine("MP-H21-HOME result=menu reason=unverified-file-or-engine");
                await Wo124ReturnToMenuAsync("private-save-changed", message, returnToken, relayClosed);
            }
            return;
        }
        JoinEngineReceipt? homeReceipt = null;
        bool sent = false;
        try
        {
            if (_transport is LogTailGameTransport tail)
            {
                var root = Path.GetDirectoryName(Path.GetDirectoryName(src.FullPath))!;
                sent = await P5LoadGameCoreAsync(src.Playline, src.Base, "leave-command", () =>
                {
                    homeReceipt = PrepareLauncherProtectedReceipt(tail, src.FullPath, root, LauncherPrivateReturn.OwnSave);
                    if (homeReceipt is null) return false;
                    homeReceipt.ProtectSubmittedLoad();
                    AttachPrivateReturn(homeReceipt);
                    return true;
                }, returnToken);
            }
            else sent = await P5LoadGameCoreAsync(src.Playline, src.Base, "leave-command", TryProtectUnobservableEngineRead, returnToken);
        }
        catch (Exception ex) when (homeReceipt is not null && ex is IOException or OperationCanceledException or HttpRequestException)
        {
            Console.WriteLine($"MP-H21-HOME transport={ex.GetType().Name} admitted=on");
        }
        if (!sent && homeReceipt is null)
        {
            _leaveInProgress = false;
            SetJoinUi("failed", _p5LoadRefusal.Length > 0 ? _p5LoadRefusal : "The load safety gate could not be confirmed.");
            return;
        }
        if (homeReceipt is not null)
        {
            // Relay teardown detaches the ordinary gameplay listeners. The
            // private load still has its own engine, log and completion.
            bool loaded;
            try
            {
                loaded = await WaitForPrivateReturnAsync(homeReceipt);
            }
            catch (OperationCanceledException) when (homeReceipt.Terminal.Task.IsCanceled)
            {
                _leaveInProgress = false;
                return;
            }
            finally
            {
                DetachPrivateReturn(homeReceipt);
                homeReceipt.Dispose();
            }
            if (_leaveInProgress && OwnLoadExpected)
            {
                if (loaded) Wo124OnGameplayStartedCore(confirmedByGameQuery: true);
                else Wo124OnLoadFailedToMenuCore(confirmedByPrivateReceipt: true);
            }
        }
    }

    private async Task Wo124ReturnToMenuAsync(string why, string message, CancellationToken ct, bool relayClosed)
    {
        if (await Wo125ExitToMenuAsync(why, ct, relayClosed)) return;
        _leaveInProgress = false;
        SetJoinUi("left", message + " The game could not return to the menu. Choose Leave in the mod menu to retry.",
            HostAuthorityFailure.JoinReason(why));
    }

    private async Task Wo124LoadPinnedHomeAsync(HenrySource save, string root, LogTailGameTransport engine,
        HomeSaveReadLease fileLease, CancellationToken ct)
    {
        JoinEngineReceipt? receipt = null;
        bool? loaded = null;
        try
        {
            bool sent = false;
            try
            {
                sent = await P5LoadGameCoreAsync(save.Playline, save.Base, "leave-command", () =>
                {
                    receipt = PrepareLauncherProtectedReceipt(engine, save.FullPath, root, LauncherPrivateReturn.OwnSave);
                    if (receipt is null) return false;
                    receipt.ProtectSubmittedLoad();
                    Volatile.Write(ref _pinnedHomeLoad, receipt);
                    AttachPrivateReturn(receipt);
                    return true;
                }, ct);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or HttpRequestException)
            {
                Console.WriteLine($"MP-H21-HOME transport={ex.GetType().Name} admitted={receipt is not null}");
            }
            if (receipt is null)
            {
                _leaveInProgress = false;
                SetJoinUi("failed", _p5LoadRefusal.Length > 0 ? _p5LoadRefusal : "The load safety gate could not be confirmed.");
                return;
            }
            Console.WriteLine($"MP-H21-HOME result=held handoff={sent}");
            loaded = await WaitForPrivateReturnAsync(receipt);
        }
        catch (OperationCanceledException) when (receipt?.Terminal.Task.IsCanceled == true || _joinEngineLifetime.IsCancellationRequested)
        {
            _leaveInProgress = false;
        }
        finally
        {
            receipt?.Dispose();
            fileLease.Dispose();
            Volatile.Write(ref _pinnedHomeLoad, null);
            DetachPrivateReturn(receipt);
        }
        if (loaded is bool completed)
        {
            Console.WriteLine($"MP-H21-HOME result=confirmed loaded={completed}");
            if (completed) Wo124OnGameplayStartedCore(confirmedByGameQuery: true);
            else Wo124OnLoadFailedToMenuCore(confirmedByPrivateReceipt: true);
        }
    }

    /// <summary>The host left the relay (0x06) while this game was in its world or joining.</summary>
    private async Task Wo124OnPeerGoneAsync(byte ghostId)
    {
        _modeTold.TryRemove(ghostId, out _);
        if (_hostModeKnown && ghostId == _hostModeFrom)
        {
            switch (HostAbsentRules.OnHostGone(_joinedWorld, _jj is { Phase: "preparing" }, _jj?.Phase is "loading" or "post-load"))
            {
                case HostAbsentRules.GoneAction.AbortJoin when _jj is { Phase: "preparing" } j:
                    await AbortJoinerJoinAsync(j, 0, "host-left", "Your host left the game.", sendAbort: false);
                    break;
                case HostAbsentRules.GoneAction.AbortJoin when _jj is { } join:
                    StartOrphanedJoinCleanup(join, "host-left", "Your host left the game.");
                    break;
                case HostAbsentRules.GoneAction.Pause:
                    EnterHostAbsentPause("host-left");
                    await WorldReconnectHoldPeerAsync();
                    break;
            }
            _hostModeKnown = false;
        }
    }

    /// <summary>
    /// The host's client is gone and this game is already in that world. Stay there. Do not load or rewrite the personal save.
    /// The same world epoch and run resume it; an explicit leave is the only way out.
    /// </summary>
    internal void EnterHostAbsentPause(string why)
    {
        uint world = _w206.World;
        var last = Volatile.Read(ref _lastHostRun);
        var absent = last is not null && last.Epoch == world ? last : new HostRun(world, Guid.Empty);
        if (Interlocked.CompareExchange(ref _absentHostRun, absent, null) is not null) return;
        Interlocked.Exchange(ref _hostAbsentRefusalLogged, 0);
        const string message = "Your host left. This world is paused until the same host returns, or you leave.";
        SetJoinUi("host-absent", message, "host-left");
        Console.WriteLine($"MP-HOST-ABSENT pause why={why} epoch={absent.Epoch} run={absent.Run:N}");
    }

    internal void HostAbsentOnEpoch(uint world, Guid run)
    {
        if (world != 0 && run != Guid.Empty) Volatile.Write(ref _lastHostRun, new HostRun(world, run));
    }

    internal bool HostAbsentCanHearEpoch(uint world, Guid run)
    {
        var absent = Volatile.Read(ref _absentHostRun);
        if (absent is null || absent.Epoch != 0 && absent.Run != Guid.Empty && world == absent.Epoch && run == absent.Run)
            return true;
        if (Interlocked.Exchange(ref _hostAbsentRefusalLogged, 1) == 0)
            Console.WriteLine($"MP-HOST-ABSENT resume=refused epoch={absent.Epoch}->{world} run={absent.Run:N}->{run:N}");
        return false;
    }

    internal bool CompleteHostAbsentResume(uint world, Guid run)
    {
        var absent = Volatile.Read(ref _absentHostRun);
        if (absent is null || _leaveInProgress || world == 0 || run == Guid.Empty ||
            world != absent.Epoch || run != absent.Run ||
            !ReferenceEquals(Interlocked.CompareExchange(ref _absentHostRun, null, absent), absent))
            return false;
        SetJoinUi("in", "Your host is back. The world continues.");
        Console.WriteLine($"MP-HOST-ABSENT resume epoch={world} run={run:N}");
        return true;
    }

    private void CompleteConfirmedReturn()
    {
        bool returning;
        lock (_joinGate)
        {
            returning = _returningFromSharedWorld;
            if (returning)
            {
                ResetJoinSessionState("private-return", suppressAutomatic: true);
                ClearJoinCharacterRequest();
                ClearJoinCalendarBootstrap();
                _leaveInProgress = false;
            }
            _returningFromSharedWorld = false;
        }
        var absent = Interlocked.Exchange(ref _absentHostRun, null);
        if (absent is not null)
            Console.WriteLine($"MP-HOST-ABSENT leave epoch={absent.Epoch} run={absent.Run:N} reason=private-world");
        if (returning || absent is not null) _ = Wo122SetLockAsync(false, "private-world-confirmed");
    }

    /// <summary>The host aborted a join this machine is running (its safety timeout, a reload, a cancel).</summary>
    private async Task<bool> Wo124OnHostAbortAsync(uint joinId, string reason)
    {
        if (_jj is not { } j || j.JoinId != joinId) return false;
        Console.WriteLine($"MP-JOIN joiner: the host aborted join 0x{joinId:x8} ({reason}) while it was {j.Phase}");
        switch (j.Phase)
        {
            case "preparing":
                await AbortJoinerJoinAsync(j, 0, reason, $"Your host stopped the join ({reason}).", sendAbort: false);
                break;
            case "loading":
            case "post-load":
                StartOrphanedJoinCleanup(j, reason, $"Your host stopped the join ({reason}).");
                break;
        }
        return true;
    }

    private void StartOrphanedJoinCleanup(JoinerJoin join, string why, string message)
    {
        lock (_joinGate)
        {
            if (!ReferenceEquals(_jj, join) || join.Phase is not ("loading" or "post-load")) return;
            join.Phase = "orphaned";
            join.Abandoned.TrySetResult(true);
            _retainedJoinWorlds.Cancel(join);
        }

        // The load can outlive the processor frame that received the abort.
        using (ExecutionContext.SuppressFlow()) _ = Task.Run(() => FinishOrphanedJoinAsync(join, why, message));
    }

    private async Task FinishOrphanedJoinAsync(JoinerJoin join, string why, string message)
    {
        if (join.EngineReceipt is not { } receipt)
        {
            Console.WriteLine($"MP-JOIN joiner: join 0x{join.JoinId:x8}: no engine receipt for the aborted load; its file stays protected");
            return;
        }

        try
        {
            await join.DispatchEnded.Task.WaitAsync(_joinEngineLifetime);
            bool loaded = await WaitForJoinReceiptAsync(receipt.Terminal.Task,
                $"MP-JOIN joiner: join 0x{join.JoinId:x8}: no terminal engine receipt yet; its file stays protected");
            await RemovePlacedWorldAsync(join, why);
            lock (_joinGate)
            {
                if (!ReferenceEquals(_jj, join) || join.Phase != "orphaned") return;
                if (!loaded)
                {
                    SetJoinedWorld(false);
                    _joinReceivedId = 0;
                    _jj = null;
                    SetJoinUi("aborted", message);
                    return;
                }
            }
            await LeaveSharedWorldCoreAsync(why, message, join);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            Console.WriteLine($"MP-JOIN joiner: join 0x{join.JoinId:x8}: aborted load cleanup interrupted ({ex.GetType().Name})");
            if (receipt.Terminal.Task.IsCanceled)
            {
                lock (_joinGate)
                {
                    if (!ReferenceEquals(_jj, join) || join.Phase != "orphaned") return;
                    SetJoinedWorld(false);
                    _joinReceivedId = 0;
                    _jj = null;
                    SetJoinUi("aborted", message);
                }
            }
        }
        finally { receipt.Dispose(); }
    }

    private async Task<bool> WaitForJoinReceiptAsync(Task<bool> receipt, string pending)
    {
        try { return await receipt.WaitAsync(TimeSpan.FromSeconds(Wo154Rules.JoinLoadWatch.MaxWaitS), _joinEngineLifetime); }
        catch (TimeoutException)
        {
            Console.WriteLine(pending);
            return await receipt.WaitAsync(_joinEngineLifetime);
        }
    }

    private static async Task<Task?> WaitForJoinLoadSignalAsync(int timeoutMs, params Task[] signals)
    {
        try { return await Task.WhenAny(signals).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs)); }
        catch (TimeoutException) { return null; }
    }

    /// <summary>The game is quitting ("CSystem::Quit invoked").</summary>
    private void Wo124OnGameQuit()
    {
        bool wasShared = _joinedWorld;
        // WO-125: the next process starts at the main menu, so the menu gate holds world pushes from now
        // until a world loads. With Unknown here (WO-124) an agent kept running across a restart pushed a
        // ghost spawn into the new game at its menu and crashed it (observed). The join tick stays quiet
        // meanwhile: _gameQuitting blocks it until the next "main menu" line.
        _where = GameWhere.Menu;
        LoadSequenceOnExit();
        W160WorldGone();
        _gameQuitting = true;
        CompleteConfirmedReturn();
        _ = Wo207OnQuitAsync();       // WO-207 C.2: the player's own background setting back before the process ends
        Wo202CrashWatch.NoteQuit();   // WO-202 I.1: the process that ends next ends on purpose
        var owedAtQuit = wasShared ? Wo184TakeOwedAtQuit() : null;   // WO-184 B4: never dropped silently
        SetJoinedWorld(false);
        if (!wasShared) { Wo125Sweep("game quit"); return; }
        // WO-184: the sweep waits for a final snapshot still on its way (the exit dialog's), then the launcher is told the truth -- WO-125
        // said "saved up to your host's last save" even when that save had never paired (0.50.9: seq 4-8 owed and dropped)
        _ = Wo184AfterQuitAsync(_joinedTag, owedAtQuit);
    }
}

