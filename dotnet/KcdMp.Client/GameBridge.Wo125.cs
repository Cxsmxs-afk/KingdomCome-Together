// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using KcdMp.Wire;

namespace KcdMp.Client;

/// <summary>
/// WO-125 -- continuity: the joiner's Henry belongs to a world, not to the
/// player (docs/WO-125-findings.md). Dormant: nothing here runs unless the
/// HOST runs a shared world.
///
/// Host:
///   * knows its world: the playthrough seed and whether its player is Henry,
///     read from the save it loaded (from the menu or in a world) and from
///     every world save it writes; unknown at an agent start -> one world save
///     identifies it;
///   * announces both in the session status (JoinStatus state 8: the seed in
///     the joinId slot, flags in arg) and refuses a join in a non-Henry world
///     without pausing;
///   * tracks its BRANCH (each save's parent is the save before it; a load
///     makes the loaded save the head; persisted in &lt;data&gt;/host-branch.json)
///     and replays it right before every WorldOffer (WorldSaved, kind bit 0x80);
///   * tells every peer when it starts a load (JoinStatus state 9).
/// Joiner:
///   * Henry files per world (<see cref="HenryStore"/>, keyed by the seed's tag);
///   * a first join asks "bring my character" / "start fresh" (the launcher,
///     or mp_join_henry auto|fresh|playlineN/file) before any request, so the
///     host is never paused while someone decides; later joins restore the
///     snapshot paired with the newest save of the host's branch;
///   * every host world save while joined -> a QuickSave of this game's copy
///     (the one save type that passes the joiner's lock, WO-124 s9.1) -> the
///     Henry block is stored, paired with the host save's md5 -> the QuickSave
///     is moved out of the playline at once; the engine cache is refreshed only for a load;
///   * a host reload takes the joiner along (an in-world rejoin); a save with
///     the host world's seed is never "own" (Henry source, leave route,
///     Continue check); with no own save to go back to, a listed file with no
///     world in it fails its load and the engine returns to the main menu.
/// Save names in logs: playlineN/file. The seed is never logged: only its tag.
/// </summary>
public partial class GameBridge
{
    private readonly HenryStore _henry = new(HenryStore.DefaultRoot(), HenryStore.ClockFromEnv);

    // ---------------------------------------------------------------- host: its world

    private uint? _hostWorldSeed;
    private bool? _hostWorldHenry;
    private string _hostWorldPlayer = "?";
    private DateTime _identifyAskedUtc = DateTime.MinValue;
    private volatile bool _hostLoadAnnounced;
    private string? _branchHead;
    private readonly object _branchGate = new();

    private sealed class BranchNode
    {
        public string? Parent { get; set; }
        public string Tag { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTime AtUtc { get; set; }
    }

    private Dictionary<string, BranchNode>? _branchMap;
    private string BranchPath => Path.Combine(Path.GetDirectoryName(HenryStore.DefaultRoot())!, "host-branch.json");

    private Dictionary<string, BranchNode> BranchMap()
    {
        if (_branchMap is not null) return _branchMap;
        try
        {
            if (File.Exists(BranchPath) && JsonSerializer.Deserialize<Dictionary<string, BranchNode>>(File.ReadAllText(BranchPath)) is { } m)
                return _branchMap = m;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { Console.WriteLine($"MP-HENRY host: host-branch.json unreadable ({ex.Message}) -- starting a new branch map"); }
        return _branchMap = new Dictionary<string, BranchNode>(StringComparer.Ordinal);
    }

    private void SaveBranchMap()
    {
        var m = BranchMap();
        if (m.Count > 2000)
            foreach (var k in m.OrderBy(kv => kv.Value.AtUtc).Take(m.Count - 2000).Select(kv => kv.Key).ToList()) m.Remove(k);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BranchPath)!);
            File.WriteAllText(BranchPath + ".part", JsonSerializer.Serialize(m));
            File.Move(BranchPath + ".part", BranchPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.WriteLine($"MP-HENRY host: host-branch.json not written: {ex.Message}"); }
    }

    /// <summary>The host's current branch, newest first (the head and its parents), at most <paramref name="max"/>.</summary>
    internal List<string> BranchNewestFirst(int max = 64)
    {
        lock (_branchGate)
        {
            var o = new List<string>();
            var m = BranchMap();
            var cur = _branchHead;
            while (cur is not null && o.Count < max && !o.Contains(cur))
            {
                o.Add(cur);
                cur = m.TryGetValue(cur, out var n) ? n.Parent : null;
            }
            return o;
        }
    }

    /// <summary>
    /// Read the host world's identity from a save it loaded (<paramref name="loaded"/>) or wrote.
    /// A write becomes the new head with the old head as its parent; a load makes the loaded save the head.
    /// </summary>
    private void Wo125HostIdentify(string path, bool loaded, string why)
    {
        try
        {
            var bytes = WhsSave.ReadShared(path);
            var v = WhsSave.Verify(bytes);
            if (!v.Ok) { Console.WriteLine($"MP-HENRY host: {SaveDisplay(path)} does not verify ({v.Reason}) -- world identity unchanged"); return; }
            var inflated = WhsSave.Inflate(bytes);
            var raw = inflated.Raw;
            uint? seed = WhsSave.ReadSeed(raw);
            // WO-135: the world's game build, announced to every joiner (Wo135HostTick).
            string? build = WhsSave.DescriptionSummary(inflated.Desc).GetValueOrDefault("BuildInfo");
            if (build != _hostWorldBuild) { _hostWorldBuild = build; _w135BuildTold.Clear(); }
            var who = WhsSave.PlayerOf(raw);
            string md5 = v.Md5.ToLowerInvariant();
            bool changed = seed != _hostWorldSeed || who.IsHenry != _hostWorldHenry;
            _hostWorldSeed = seed;
            _hostWorldHenry = who.IsHenry;
            _hostWorldPlayer = who.Player;
            string tag = seed is uint s ? WhsSave.SeedTag(s) : "-";
            lock (_branchGate)
            {
                var m = BranchMap();
                if (loaded)
                {
                    if (!m.ContainsKey(md5)) m[md5] = new BranchNode { Parent = null, Tag = tag, Name = SaveDisplay(path), AtUtc = DateTime.UtcNow };
                }
                else if (!m.ContainsKey(md5))
                    m[md5] = new BranchNode { Parent = _branchHead, Tag = tag, Name = SaveDisplay(path), AtUtc = DateTime.UtcNow };
                _branchHead = md5;
                SaveBranchMap();
            }
            if (seed is uint) Wo134OnHostWorld(tag, md5, loaded);   // WO-134: the host's chest ledger pairs with this save
            if (seed is uint) Wo221HorseOnHostWorld(tag, md5, loaded);   // WO-221 D.1: so does the record of whose horse is whose
            if (seed is uint && _sharedWorld && config.IsHosting) _henry.MarkHosted(tag);   // WO-157: this install's own playthrough (the launcher's host flow only)
            Console.WriteLine($"MP-HENRY host: world {tag} ({why}: {SaveDisplay(path)}, md5 {md5[..8]} = the save's footer MD5) player={who.Player} henry={On(who.IsHenry)} branch_depth={BranchNewestFirst().Count}");
            if (changed)
            {
                _modeTold.Clear();   // the session status carries the identity: resend at the next tick
                if (_hostWorldHenry == false && _sharedWorld)
                {
                    const string msg = "Co-op: you are in a part of the story your partner can't join yet.";
                    Console.WriteLine($"MP-HENRY host: this world's player is not Henry ({who.Player}) -- no join now; joiners are told");
                    _ = ExecLuaAsync($"if KCD2MP_Wo124Msg then KCD2MP_Wo124Msg(\"{EscapeLua(msg)}\") end");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Console.WriteLine($"MP-HENRY host: could not read {SaveDisplay(path)}: {ex.Message}");
        }
    }

    /// <summary>The session status's joinId + flags: the seed and whether the host's player is Henry.</summary>
    private (uint Seed, ushort Flags) Wo125SessionIdentity()
    {
        if (_hostWorldSeed is not uint s) return (0, 0);
        return (s, (ushort)(Protocol.SessionSeedKnown | (_hostWorldHenry == true ? Protocol.SessionHenryWorld : 0)));
    }

    /// <summary>Host tick: an unknown world (agent started mid-world) is identified by one world save, once a minute at most, only with a peer present.</summary>
    private void Wo125HostTick()
    {
        if (!_sharedWorld || _hostWorldSeed is not null || _where == GameWhere.Menu) return;
        if (LivePartners().Count == 0) return;   // WO-144
        if ((DateTime.UtcNow - _identifyAskedUtc).TotalSeconds < 60 || Volatile.Read(ref _worldSaveBusy) != 0 || Wo123HostJoinActive) return;
        _identifyAskedUtc = DateTime.UtcNow;
        Console.WriteLine("MP-HENRY host: this world's seed is not known yet (no load or save seen since the agent started) -- one world save identifies it");
        _ = RequestWorldSaveAsync("identify");
    }

    /// <summary>"Loading saved game '...'" on the host: its world is about to change.</summary>
    private void Wo125HostOnLoadAccepted(string display)
    {
        if (!(_combatRoleApplied && _isDamageAuthority && _sharedWorld && _modeEverShared)) return;
        if (ResolveSavesDirForJoin() is string saves)
        {
            var m = Regex.Match(display, @"^playline([0-4])/([A-Za-z0-9_]+\.whs)$", RegexOptions.IgnoreCase);
            if (m.Success) Wo125HostIdentify(Path.Combine(saves, $"playline{m.Groups[1].Value}", m.Groups[2].Value), loaded: true, "loaded");
        }
        _hostLoadAnnounced = true;
        Wo201HostLoadStart();   // WO-201 B15
        var peers = LivePartners();   // WO-144: the relay's connections, never a stale name
        foreach (byte g in peers)
            _ = WriteJoinAsync(JoinStatusCodec.Build(g, 0, Protocol.JoinStateReloading, Protocol.JoinReasonId("reloading"), 0)).ContinueWith(_ => { });
        Console.WriteLine($"MP-HENRY host: a save load started ({display}) -- {peers.Count} peer(s) told 'your host is reloading'");
    }

    private void Wo125HostOnGameplayStarted()
    {
        H21HostGameplayStarted();
        if (!_hostLoadAnnounced) return;
        _hostLoadAnnounced = false;
        Wo201HostLoadDone();   // WO-201 B15
        _modeTold.Clear();   // announce the (possibly new) world at the next tick: a joiner in it rejoins
    }

    /// <summary>Right before a WorldOffer: the branch, oldest first, as WorldSaved entries with the branch bit.</summary>
    private async Task Wo125SendBranchReplayAsync(uint joinId)
    {
        var br = BranchNewestFirst();
        br.Reverse();
        if (br.Count == 0 || _wo122Stream is not Stream stream) { Console.WriteLine($"MP-HENRY host: join 0x{joinId:x8}: no branch to replay"); return; }
        var m = BranchMap();
        for (int i = 0; i < br.Count; i++)
        {
            byte kind = Protocol.SaveKindBranchFlag;
            byte pl = 0; ushort idx = 0;
            if (m.TryGetValue(br[i], out var n) && WorldSaved.ParsePath(n.Name.Replace('/', Path.DirectorySeparatorChar)) is { } id) { kind |= id.Kind; pl = id.Playline; idx = id.Idx; }
            var ws = new WorldSaved((uint)br.Count, i, kind, pl, idx, Convert.FromHexString(br[i]));
            var body = ws.Encode();
            var pkt = new byte[3 + body.Length];
            pkt[0] = Protocol.WorldSavedUp;
            BinaryPrimitives.WriteUInt16LittleEndian(pkt.AsSpan(1), (ushort)body.Length);
            body.CopyTo(pkt, 3);
            await WritePacketAsync(stream, pkt, _wo122Ct);
        }
        Console.WriteLine($"MP-HENRY host: join 0x{joinId:x8}: branch replayed ({br.Count} save(s), newest md5 {br[^1][..8]})");
    }

    // ---------------------------------------------------------------- joiner: what the host said

    private volatile bool _peerSeedKnown;
    private uint _peerSeed;
    private volatile bool _peerHenryWorld;
    private string? _peerTag;
    private readonly List<string> _hostBranch = [];            // newest first, from the last complete replay
    private string?[] _branchRx = [];
    private volatile JoinCharacterIntent? _firstCharacter;
    private bool _chooseAsked, _notHenryTold;
    private volatile bool _rewinding;                          // the host is reloading: nothing gained now is kept
    private volatile bool _rejoinPending;                      // the host is back: rejoin from inside the world
    private DateTime _rejoinSinceUtc;
    private volatile bool _joinFromWorldOnce;                  // after leaving for a host's new world: join from the own world once
    private volatile bool _leaveInProgress;                    // a leave's own load (or the way to the menu) is running: no request
    private volatile bool _newWorldLeavePending;               // the host changed worlds while a join ran: leave when it ends
    private DateTime _leaveSinceUtc;
    private string? _joinedTag;                                // the world this game is in (while joined)
    private string? _resetTag;                                 // mp_henry_reset while in it: no snapshot for it until the next join
    private byte[]? _joinReceivedMd5;
    private byte[]? _lastWorldDesc;                             // the last received world's header, for the no-own-save way out
    private int _lastWorldPlayline = -1;

    /// <summary>The session status's identity (WO-124 hosts send joinId 0 and no flags).</summary>
    private void Wo125OnSessionIdentity(uint seed, ushort flags)
    {
        bool known = (flags & Protocol.SessionSeedKnown) != 0;
        bool henry = (flags & Protocol.SessionHenryWorld) != 0;
        string? tag = known ? Wo157TagFor(seed) : null;   // WO-157: the host's world on this machine (its install key)
        bool changed = known != _peerSeedKnown || (known && seed != _peerSeed) || henry != _peerHenryWorld;
        if (known && (!_peerSeedKnown || seed != _peerSeed)) _peerSeedKnownSinceUtc = DateTime.UtcNow;   // WO-135: the build wait starts here
        _peerSeedKnown = known;
        _peerSeed = seed;
        _peerHenryWorld = henry;
        _peerTag = tag;
        if (changed)
        {
            Console.WriteLine(known
                ? $"MP-HENRY joiner: the host's world is {tag} (player {(henry ? "Henry" : "NOT Henry")}); Henry files for it here: {(_henry.HasWorld(tag!) ? _henry.Snapshots(tag!).Count + " snapshot(s)" : "none (a first join)")}"
                : "MP-HENRY joiner: the host has not identified its world yet");
            _chooseAsked = _notHenryTold = false;
            if (_joinedWorld && known && !henry)
            {
                const string msg = "Your host is in a part of the story you can't share yet.";
                Console.WriteLine("MP-HENRY joiner: the host entered a stretch where its player is not Henry -- told; no further behaviour (quest sync, later)");
                _ = ExecLuaAsync($"if KCD2MP_Wo124Msg then KCD2MP_Wo124Msg(\"{EscapeLua(msg)}\") end");
            }
        }
        if (_rewinding && known)
        {
            // The host is back from its load.
            if (_joinedWorld && tag == _joinedTag)
            {
                if (!_rejoinPending) T3ArmInWorldRejoin("host-identity-after-reload");
            }
            else if (_joinedWorld)
            {
                _rewinding = false;
                _joinFromWorldOnce = true;
                Console.WriteLine($"T3-AUTO-REJOIN trigger=host-playthrough-changed decision=leave-then-join loaded_tag={_joinedTag} host_tag={tag}");
                if (_jj is not null)
                {
                    // Never two loads at once (observed: a leave fired mid-join raced the join's own load).
                    _newWorldLeavePending = true;
                    Console.WriteLine($"MP-HENRY joiner: the host loaded ANOTHER playthrough ({tag}, was {_joinedTag}) -- a join is still running; leaving once it ends");
                }
                else
                {
                    Console.WriteLine($"MP-HENRY joiner: the host loaded ANOTHER playthrough ({tag}, was {_joinedTag}) -- leaving this world first, then joining the new one");
                    _ = LeaveSharedWorldAsync("host-new-world", "Your host loaded a different game.");
                }
            }
        }
    }

    /// <summary>JoinStatus state 9: the host started a load.</summary>
    private void Wo125OnHostReloading()
    {
        if (!_joinedWorld) { Console.WriteLine("MP-HENRY joiner: the host is reloading (this game is not in its world)"); return; }
        if (_rewinding) return;
        // WO-184 B4: a snapshot still owed for a host save before this reload is taken now (its last chance: the rejoin restores the pair of
        // the save the host loads, and nothing done from here on is kept). Off the relay's reader; the world is unchanged meanwhile.
        _ = Task.Run(async () =>
        {
            string r = await Wo184FinalSnapshotAsync("host reload", ignoreRewind: true, onlyIfOwed: true);
            if (r is not ("" or "-")) Console.WriteLine($"WO184-FINAL host reload: the owed snapshot was not taken ({r})");
        });
        _rewinding = true;
        if (_t3JoinedHostWorld == 0) _t3JoinedHostWorld = _w206.World;
        Console.WriteLine($"T3-AUTO-REJOIN trigger=host-load-status decision=await-epoch loaded_epoch={_t3JoinedHostWorld} host_epoch={_w206.World}");
        _w206RewindJoinEpoch = _w206.Join;   // WO-206 E: this "reloading" belongs to the join epoch it began under
        _rejoinPending = false;
        Wo202RejoinReset("the host reloads");   // WO-202 III.3: a new reload, a new count
        _ = Wo201OnHostBusyAsync("load 1");   // WO-201 B15: its pings stall while it loads -- no "connection" toast
        const string msg = "Your host is reloading... what you do now won't be kept.";
        _ = W164NoticeAsync(Wo164Rules.ReloadStartText);   // WO-164 RL: the game's own HUD line, queued if a menu holds the timers
        Console.WriteLine($"MP-HENRY joiner: the host started a load -- this game's progress from here on is NOT kept; it rewinds with the host");
        SetJoinUi("rewinding", "Your host is reloading...");
        _ = ExecLuaAsync($"if KCD2MP_Wo124Msg then KCD2MP_Wo124Msg(\"{EscapeLua(msg)}\") end");
    }

    /// <summary>A branch-replay entry (WorldSaved with the branch bit): position SenderUnixMs of Seq.</summary>
    private void Wo125OnBranchEntry(WorldSaved w)
    {
        int pos = (int)w.SenderUnixMs, n = (int)w.Seq;
        if (n <= 0 || n > 512 || pos < 0 || pos >= n) return;
        if (pos == 0 || _branchRx.Length != n) _branchRx = new string?[n];
        _branchRx[pos] = Convert.ToHexString(w.Md5).ToLowerInvariant();
        if (pos == n - 1 && _branchRx.All(x => x is not null))
        {
            lock (_hostBranch)
            {
                _hostBranch.Clear();
                _hostBranch.AddRange(_branchRx.Reverse()!);
            }
            Console.WriteLine($"MP-HENRY joiner: the host's branch: {n} save(s), newest md5 {_hostBranch[0][..8]}");
        }
    }

    // ---------------------------------------------------------------- joiner: the first-join choice

    private void Wo125OnLauncherChoice(string c)
    {
        c = (c ?? "").Trim().ToLowerInvariant();
        if (!Wo159Rules.TryParseJoinChoice(c, out string parsed, out string? style)) { Console.WriteLine($"MP-HENRY joiner: the launcher sent an unknown choice '{c}' -- ignored"); return; }
        c = parsed;
        _firstCharacter = new(c, style, _peerTag);
        _chooseAsked = false;
        Console.WriteLine($"MP-HENRY joiner: first join to world {_peerTag ?? "?"}: the player chose '{(c == "bring" ? "Bring my character" : "Start fresh")}' (launcher)");
        _autoNextUtc = DateTime.UtcNow;
    }

    /// <summary>mp_join_henry typed at the console: the same answer as the launcher's (auto = bring the newest own save).</summary>
    private void Wo125OnConsoleChoice(string v)
    {
        v = (v ?? "").Trim();
        string choice = v == "auto" ? "bring" : v;
        _firstCharacter = new(choice, null, _peerTag);
        _chooseAsked = false;
        Console.WriteLine($"MP-HENRY joiner: a local character choice was received for world {_peerTag ?? "the next host world"}");
        _autoNextUtc = DateTime.UtcNow;
    }

    private JoinCharacterIntent? CurrentCharacter() =>
        _firstCharacter is { } intent && (intent.WorldTag is null || intent.WorldTag == _peerTag) ? intent : null;

    private string? CurrentChoice() => CurrentCharacter()?.Key;

    /// <summary>
    /// Whether a request may go out now (the host is never paused while
    /// someone decides). Tells the player why not, once.
    /// </summary>
    private bool Wo125ReadyToRequest(out string why)
    {
        why = "";
        if (!_peerSeedKnown) { why = "the host has not identified its world yet"; SetJoinUi("waiting", "Waiting for your host..."); return false; }
        if (!_peerHenryWorld)
        {
            why = "the host's player is not Henry";
            if (!_notHenryTold)
            {
                _notHenryTold = true;
                const string msg = "Your host is in a part of the story where you can't join yet.";
                Console.WriteLine($"MP-HENRY joiner: no join asked -- {why} (world {_peerTag})");
                SetJoinUi("not-henry", msg);
            }
            return false;
        }
        if (!Wo180SlotAvailableOrTell()) { why = "no save slot for the host's world"; return false; }   // WO-180: before the first-join question and before any request
        var choice = CurrentChoice();
        if (choice is null && _henry.HasWorld(_peerTag!))
        {
            // WO-200 Act IV: a rejoin -- the launcher says which character comes back, by the per-world Henry file's time
            var newest = _henry.Snapshots(_peerTag!).FirstOrDefault();
            if (newest is not null) SetJoinUi("rejoin", Wo200Rules.RejoinText(DateTime.UtcNow - newest.TakenUtc));   // the request follows at once
            return true;
        }
        // WO-135: a first join's Henry comes from a save of the host world's game build only
        // (the splice's rule, WO-115). The host announces it; until it has (15 s at most),
        // nothing is asked. What this machine can offer is decided before any request, so
        // the host is never paused for a join that cannot splice.
        if (_peerHostBuild is null && (DateTime.UtcNow - _peerSeedKnownSinceUtc).TotalSeconds < 15)
        {
            why = "waiting for the host's game version";
            SetJoinUi("waiting", "Waiting for your host...");
            return false;
        }
        if (choice is null)
        {
            why = "a first join: waiting for the player's choice";
            if (!_chooseAsked)
            {
                _chooseAsked = true;
                var (state, msg, reason, canFresh) = Wo135FirstJoinUi();
                Console.WriteLine($"MP-HENRY joiner: first join to world {_peerTag} -- {(state == "no-save" ? $"NO save of this player's to bring ({reason}){(canFresh ? "; a new character can join" : ": nothing asked of the host")}" : $"asking the player ({state}): Bring my character / Start fresh")} (launcher; console: mp_join_henry auto|fresh|playlineN/file)");
                SetJoinUi(state, msg, reason, canFresh);
                if (state == "no-save") Wo135TellInGame(msg);
            }
            return false;
        }
        return true;
    }

    /// <summary>WO-135: the host's build, when known (null = no filter: an announcement never came).</summary>
    private string? Wo135TargetBuild() => _peerHostBuild;

    /// <summary>WO-135: which of Bring / Start fresh exist from the host's build, as the launcher shows it.</summary>
    private (string State, string Message, string Reason, bool CanFresh) Wo135FirstJoinUi()
    {
        string? host = Wo135TargetBuild();
        if (host is null) { var (s0, m0) = Wo135Rules.ChooseUi(Wo135Rules.Offer.Both, null, ""); return (s0, m0, "", false); }
        bool bring = Wo125SourceFor("bring", out _) is not null;
        bool fresh = Wo125SourceFor("fresh", out _) is not null;
        if (bring) { var (s1, m1) = Wo135Rules.ChooseUi(Wo135Rules.OfferFor(bring, fresh), Wo135NewestHenryBuild(), host); return (s1, m1, "", fresh); }
        // WO-154 4.4: nothing of this player's to bring -- the plain reason, and a new character when one can join
        string reason = Wo154NoSaveReason(host);
        return ("no-save", Wo154Rules.NoSaveMessage(reason, fresh, Wo135NewestHenryBuild(), host), reason, fresh);
    }

    /// <summary>WO-154 4.4: the census behind <see cref="Wo154Rules.NoSaveReason"/> (headers only; nothing inflated).</summary>
    private string Wo154NoSaveReason(string? host)
    {
        if (ResolveSavesDirForJoin() is not string saves) return "no-saves";
        int all = ListOwnSaves(saves).Count;
        var own = Wo125OwnSaves(saves, HostSeedForOwn());
        var builds = own.Select(s => WhsSave.ReadBuildFromFile(s.Save.FullPath)).ToList();
        int same = host is null ? own.Count : builds.Count(b => Wo135Rules.SameBuild(b, host));
        int regular = builds.Count(Wo154Rules.IsRegularGameBuild);
        return Wo154Rules.NoSaveReason(all, own.Count, same, regular, host);
    }

    /// <summary>The build of this player's newest own save (any build) -- the "X" of the plain message.</summary>
    private string? Wo135NewestHenryBuild()
    {
        if (ResolveSavesDirForJoin() is not string saves) return null;
        foreach (var s in Wo125OwnSaves(saves, HostSeedForOwn()))
            if (WhsSave.ReadBuildFromFile(s.Save.FullPath) is string b) return b;
        return null;
    }

    private string? _w135ToldInGame;
    private void Wo135TellInGame(string msg)
    {
        if (_w135ToldInGame == msg) return;
        _w135ToldInGame = msg;
        _ = ExecLuaAsync($"if KCD2MP_Wo124Msg then KCD2MP_Wo124Msg(\"{EscapeLua(msg)}\") end");
    }

    // ---------------------------------------------------------------- joiner: which saves are "own"

    public sealed record OwnSave(HenrySource Save, uint? Seed);

    /// <summary>
    /// Every engine-named save in playline0..4, newest SaveTime first, EXCEPT a
    /// save whose seed is <paramref name="hostSeed"/>: that is a copy of the
    /// host's world (e.g. copied in by hand under 0.28.x), never the player's
    /// own. Each skip is logged (a warning: the file is left where it is).
    /// WO-157: unless <paramref name="hostedHere"/> says this install hosted that world itself -- two hosts of
    /// the same start save share its seed, and the one who hosted it first then had "only copies of the host's
    /// world" and could not join the other. Scoped guest saves with no cleanup receipt are retained and
    /// excluded by their exact observed file and content identity.
    /// </summary>
    public static List<OwnSave> OwnSaves(string saves, uint? hostSeed, Action<string>? log = null, Func<uint, bool>? hostedHere = null)
        => OwnSaves(saves, hostSeed, log, hostedHere, null);

    private List<OwnSave> Wo125OwnSaves(string saves, uint? hostSeed, Action<string>? log = null)
        => OwnSaves(saves, hostSeed, log, Wo157HostedSeed, Wo125ScopedGuestSaveFilter(saves));

    private Func<HenrySource, bool>? Wo125ScopedGuestSaveFilter(string saves)
        => TryCheckpointProfile(saves, out _, out string profile) ? _henry.ScopedGuestSaveFilter(profile) : null;

    private static List<OwnSave> OwnSaves(string saves, uint? hostSeed, Action<string>? log, Func<uint, bool>? hostedHere,
        Func<HenrySource, bool>? retainedGuestSave)
    {
        var o = new List<OwnSave>();
        var skipped = new List<string>();
        bool ownWorld = hostSeed is uint h0 && hostedHere?.Invoke(h0) == true;
        foreach (var s in ListOwnSaves(saves))
        {
            if (retainedGuestSave?.Invoke(s) == true) continue;
            uint? seed = WhsSave.ReadSeedFromFile(s.FullPath);
            if (hostSeed is uint hs && seed == hs && !ownWorld)
            {
                // Logged once per file per agent run: a tester with a whole host playline would otherwise see it on every lookup.
                if (Warned.TryAdd(s.FullPath, 0)) skipped.Add(s.Display);
                continue;
            }
            o.Add(new OwnSave(s, seed));
        }
        if (ownWorld && Warned.TryAdd("hosted:" + saves + "|" + hostSeed, 0))
            log?.Invoke("MP-HENRY joiner: the host's world has the playthrough seed of a world this install hosted itself (the same start save): this player's saves of it are his own");
        if (skipped.Count > 0)
            log?.Invoke($"MP-HENRY WARNING {skipped.Count} save(s) are copies of the host's world (same playthrough seed): never used as this player's own save, left where they are: {string.Join(", ", skipped.Take(6))}{(skipped.Count > 6 ? ", ..." : "")}");
        return o;
    }

    private static readonly ConcurrentDictionary<string, byte> Warned = new(StringComparer.OrdinalIgnoreCase);

    private uint? HostSeedForOwn() => _peerSeedKnown ? _peerSeed : null;

    private static bool TestNoOwnSave => Environment.GetEnvironmentVariable("KCDMP_TEST_NO_OWN_SAVE") == "1";

    /// <summary>The newest own save that verifies (the leave route's target), or null.</summary>
    private HenrySource? Wo125NewestOwn(out string why)
    {
        why = "";
        if (TestNoOwnSave) { why = "KCDMP_TEST_NO_OWN_SAVE=1 (test: no own save)"; Console.WriteLine("MP-HENRY TEST KCDMP_TEST_NO_OWN_SAVE=1 -- acting as if this player had no save of their own"); return null; }
        if (ResolveSavesDirForJoin() is not string saves) { why = "no saves folder"; return null; }
        var own = Wo125OwnSaves(saves, HostSeedForOwn(), Console.WriteLine);
        // WO-147: only a save this game can load -- of the build of the world it runs. The live run's leave picked a
        // newer build's save (the retail game shares the saves folder): "needs newer game", and the joiner stayed.
        if (Wo135TargetBuild() is string build) own = SameBuildSaves(own, build, l => Console.WriteLine(l));
        foreach (var s in own)
        {
            var v = WhsSave.VerifyFile(s.Save.FullPath);
            if (v.Ok) return s.Save;
            Console.WriteLine($"MP-HENRY skipping {s.Save.Display}: {v.Reason}");
        }
        why = "no save of this player's own (with a different playthrough than the host's)";
        return null;
    }

    /// <summary>
    /// The leave route's target: the newest own save in the engine cache; one refresh at most
    /// per leave attempt (a save that appeared after the game started is not in it, and wh_sys_LoadGame then
    /// silently loads nothing, WO-112 s3.5). No plugin answer: the newest own save, unchecked (logged).
    /// </summary>
    private async Task<(HenrySource? Save, string Why)> Wo125NewestOwnLoadableAsync()
    {
        if (TestNoOwnSave || ResolveSavesDirForJoin() is not string saves)
            return (Wo125NewestOwn(out string w0), w0);
        var selected = await SaveListMaintenance.FindOwnAsync(
            Wo125OwnSaves(saves, HostSeedForOwn(), Console.WriteLine),
            s =>
            {
                var v = WhsSave.VerifyFile(s.Save.FullPath);
                if (!v.Ok) Console.WriteLine($"MP-HENRY skipping {s.Save.Display}: {v.Reason}");
                return v.Ok;
            },
            (s, op) => _combat.SaveListAsync(op, s.Save.Playline, s.Save.Base),
            (s, why) => Console.WriteLine($"MP-HENRY leave target {s.Save.Display}: {why}"));
        if (selected is not null) return (selected.Save, "");
        return (null, "no loadable save of this player's own (with a different playthrough than the host's)");
    }

    private async Task<(HenrySource? Save, string Why)> Wo125ListedHomeAsync(HenrySource source)
    {
        // File identity is proved by the read lease after these engine calls.
        var listed = await _combat.SaveListAsync(3, source.Playline, source.Base);
        if (listed is { Listed: false }) listed = await _combat.SaveListAsync(1, source.Playline, source.Base);
        if (listed is { Listed: false }) return (null, "the selected character's original save is absent from the engine list");
        return (source, "");
    }

    public sealed record HenryChoice(string Mode, WhsSave.HenryParts Parts, HenrySource? Save, string Detail)
    {
        public string? Fingerprint { get; init; }
    }

    private sealed record JoinCharacterIntent(string Choice, string? Style, string? WorldTag)
    {
        public string Key => Style is null ? Choice : $"{Choice}:{Style}";
    }

    private sealed record JoinCharacterSelection(JoinCharacterIntent? Intent, uint? HostSeed, string? HostBuild, HenryChoice? First,
        SelectedCharacter? Scoped = null);

    private (JoinCharacterSelection? Character, string Why) Wo125PrepareCharacter(JoinCharacterIntent? intent, string? build, uint? seed)
    {
        if (intent is null && _joinedWorld && Volatile.Read(ref _joinedCharacter) is { } joined)
            return (new(null, seed, build, null, joined.Selection), "");
        if (intent is not null && TryCharacterChoice(intent.Choice, out Guid id, out bool restore))
        {
            var selected = Wo125SelectCatalogCharacter(id, build);
            return selected.Selection is null
                ? (null, selected.Why)
                : (new(intent, seed, build, restore ? null : selected.Source, selected.Selection), "");
        }
        if (intent is null or { Choice: "restore" })
        {
            if (seed is not null && (_peerTag is not string tag || !_henry.HasWorld(tag)))
                return (null, "Choose a verified local character before requesting this world.");
            return (new(intent, seed, build, null), "");
        }
        var source = Wo125SourceFor(intent.Choice, out string why, (intent.Style, build, seed));
        if (source is null) return (null, why);
        if (build is not null && !Wo135Rules.SameBuild(source.Parts.Build, build))
            return (null, Wo135Rules.WrongBuildMessage(source.Parts.Build, build));
        if (seed is uint hostSeed && source.Parts.Seed == hostSeed && (source.Mode == "fresh" || !Wo157HostedSeed(hostSeed)))
            return (null, "The chosen save belongs to the host's world.");
        return (new(intent, seed, build, source), "");
    }

    /// <summary>
    /// The source for a first join. bring = the newest own save whose player is
    /// Henry (Phase 7: a Godwin/prologue save is skipped); fresh = a new game's
    /// first Henry save on this machine (Phase 3); playlineN/file = that save,
    /// refused when it is a copy of the host's world or not a Henry save.
    /// </summary>
    private HenryChoice? Wo125SourceFor(string choice, out string why, (string? Style, string? Build, uint? Seed)? requested = null)
    {
        why = "";
        if (ResolveSavesDirForJoin() is not string saves) { why = "no saves folder"; return null; }
        uint? hostSeed = requested is { } scope ? scope.Seed : HostSeedForOwn();
        string? build = requested is { } expected ? expected.Build : Wo135TargetBuild();
        if (choice == "fresh") return Wo125LocalCheckpoint(saves, requested is { } offer ? offer.Style : CurrentCharacter()?.Style, build, out why);
        if (choice.StartsWith("playline", StringComparison.Ordinal))
        {
            var m = Regex.Match(choice, @"^playline([0-4])/([A-Za-z0-9_]+?)(\.whs)?$");
            if (!m.Success) { why = $"mp_join_henry '{choice}' is not playlineN/file"; return null; }
            string f = m.Groups[2].Value + ".whs", full = Path.Combine(saves, $"playline{m.Groups[1].Value}", f);
            if (!File.Exists(full)) { why = $"That save doesn't exist (playline{m.Groups[1].Value}/{f})."; return null; }
            if (hostSeed is uint hs && WhsSave.ReadSeedFromFile(full) == hs && !Wo157HostedSeed(hs))
            {
                why = $"That save is a copy of your host's world, not your own character (playline{m.Groups[1].Value}/{f}).";
                Console.WriteLine($"MP-HENRY joiner: mp_join_henry playline{m.Groups[1].Value}/{f} REFUSED: it has the host world's playthrough seed");
                return null;
            }
            var src = new HenrySource(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), f, full, ReadSaveTime(full) ?? 0);
            if (Wo125ScopedGuestSaveFilter(saves)?.Invoke(src) == true)
            {
                why = "That save was written while playing in a host's run. Choose a personal save.";
                return null;
            }
            if (build is not null && WhsSave.ReadBuildFromFile(full) is var fb && !Wo135Rules.SameBuild(fb, build))
            {
                why = Wo135Rules.WrongBuildMessage(fb, build);
                Console.WriteLine($"MP-HENRY joiner: mp_join_henry playline{m.Groups[1].Value}/{f} REFUSED: its game build is {fb ?? "unknown"}, the host world's is {build}");
                return null;
            }
            return TryHenrySave(src, WhsSave.HenryParts.OriginSave, "bring", out why, requirePristine: false);
        }
        var own = Wo125OwnSaves(saves, hostSeed, Console.WriteLine);
        // WO-135: a save from another game build is skipped (logged once per file), before it is even read.
        var same = build is null ? own : SameBuildSaves(own, build, l => Console.WriteLine(l));
        if (choice == "bring")
        {
            foreach (var s in same)
                if (TryHenrySave(s.Save, WhsSave.HenryParts.OriginSave, "bring", out string w, requirePristine: false, quiet: true) is { } c) return c;
                else Console.WriteLine($"MP-HENRY joiner: skipping {s.Save.Display} as the Henry source: {w}");
            why = own.Count == 0 ? NoOwnSaveMessage
                : same.Count == 0 && build is not null ? Wo135Rules.WrongBuildMessage(Wo135NewestHenryBuild(), build)
                : "No save of your own with Henry in it yet.";
            return null;
        }
        why = $"unknown choice '{choice}'";
        return null;
    }

    private static HenryChoice? Wo125LocalCheckpoint(string saves, string? style, string? build, out string why)
    {
        why = "Start fresh needs a verified local checkpoint from the normal prologue in this game profile.";
        if (!TryCheckpointProfile(saves, out string folder, out string profile)) return null;
        var catalog = new HenryCharacterCatalog(HenryCharacterCatalog.DefaultRoot(), profile);
        if (!catalog.TryListAll(out var entries)) return null;
        var claimedCreations = entries.Select(entry => entry.CreationId).ToHashSet();
        var store = new LocalStartCheckpointStore(LocalStartCheckpointStore.DefaultRoot(), profile);
        if (!store.TryFindFresh(folder, style, build, claimedCreations, out var checkpoint, out _)) return null;
        why = "";
        return Wo125CheckpointChoice(saves, checkpoint);
    }

    private static HenryChoice Wo125CheckpointChoice(string saves, LocalStartCheckpoint checkpoint)
    {
        var home = checkpoint.EngineSave.Split('/');
        int playline = home[0][^1] - '0';
        string file = home[1] + ".whs";
        string path = Path.Combine(saves, home[0], file);
        var parts = WhsSave.PartsFromFile(checkpoint.SaveBytes, WhsSave.HenryParts.OriginFreshSave);
        return new HenryChoice("fresh", parts, new(playline, file, path, 0), "verified local checkpoint")
        { Fingerprint = checkpoint.Sha256 };
    }

    private static readonly ConcurrentDictionary<string, byte> BuildWarned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// WO-135: the own saves of <paramref name="build"/> only, in the same (newest-first) order;
    /// every skip is logged once per file and build, from the header alone (nothing is inflated).
    /// </summary>
    public static List<OwnSave> SameBuildSaves(List<OwnSave> own, string build, Action<string>? log = null)
    {
        var o = new List<OwnSave>();
        var skipped = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);   // build -> newly skipped files
        foreach (var s in own)
        {
            string? b = WhsSave.ReadBuildFromFile(s.Save.FullPath);
            if (Wo135Rules.SameBuild(b, build)) { o.Add(s); continue; }
            if (BuildWarned.TryAdd($"{s.Save.FullPath}|{build}", 0))
                (skipped.TryGetValue(b ?? "unknown", out var l) ? l : skipped[b ?? "unknown"] = []).Add(s.Save.Display);
        }
        // One line per build (each file is reported once per host build, ever).
        foreach (var (b, files) in skipped)
            log?.Invoke($"MP-HENRY joiner: skipping {files.Count} save(s) of game build {b} (the host world's is {build}; a character and a world are combined from the same build only): {string.Join(", ", files.Take(6))}{(files.Count > 6 ? ", ..." : "")}");
        return o;
    }

    private HenryChoice? TryHenrySave(HenrySource s, string origin, string mode, out string why, bool requirePristine, bool quiet = false)
    {
        why = "";
        byte[] bytes;
        try { bytes = WhsSave.ReadShared(s.FullPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { why = "The selected save cannot be read."; return null; }
        var v = WhsSave.Verify(bytes);
        if (!v.Ok) { why = v.Reason; return null; }
        var c = WhsSave.Inflate(bytes);
        var who = WhsSave.PlayerOf(c.Raw);
        if (!who.IsHenry) { why = $"its player is not Henry ({who.Player})"; if (!quiet) Console.WriteLine($"MP-HENRY joiner: {s.Display}: {why}"); return null; }
        if (requirePristine && !who.Pristine) { why = "not a new game's first Henry save"; return null; }
        var parts = WhsSave.PartsFromStream(c.Raw, WhsSave.DescriptionSummary(c.Desc).GetValueOrDefault("BuildInfo") ?? "", origin);
        return new HenryChoice(mode, parts, s, s.Display)
        { Fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) };
    }

    // ---------------------------------------------------------------- joiner: the Henry for a received world

    /// <summary>
    /// After the world arrived: check it is the world announced and a Henry
    /// world, then restore (a Henry file exists for it) or use the first-join
    /// choice. Null = abort with <paramref name="abortReason"/>.
    /// </summary>
    private HenryChoice? Wo125HenryForWorld(JoinerJoin join, byte[] hostBytes,
        out string why, out byte abortReason, out string tag)
    {
        why = ""; abortReason = 0; tag = "";
        uint joinId = join.JoinId;
        var selection = join.Character;
        var c = WhsSave.Inflate(hostBytes);
        uint? seed = WhsSave.ReadSeed(c.Raw);
        var who = WhsSave.PlayerOf(c.Raw);
        _lastWorldDesc = c.DescBytes;
        if (seed is not uint s) { why = "the world has no playthrough seed"; abortReason = Protocol.JoinAbortSpliceFailed; return null; }
        tag = Wo157TagFor(s);   // WO-157
        if ((_peerSeedKnown && s != _peerSeed) || (selection?.HostSeed is uint selectedSeed && s != selectedSeed))
        {
            why = $"the world received ({tag}) is not the one the host announced ({_peerTag}) -- it changed; the next announcement decides";
            abortReason = Protocol.JoinAbortWorldChanged;
            return null;
        }
        if (!who.IsHenry) { why = $"the host's player in this world is not Henry ({who.Player})"; abortReason = Protocol.JoinAbortNotHenry; return null; }
        if (selection is null)
        {
            why = "The join has no matching character selection.";
            abortReason = Protocol.JoinAbortNoHenrySource;
            return null;
        }
        string? receivedBuild = WhsSave.DescriptionSummary(c.Desc).GetValueOrDefault("BuildInfo");
        if (selection.HostBuild is string expectedBuild && !Wo135Rules.SameBuild(receivedBuild, expectedBuild))
        {
            why = "The host's game build changed during the join.";
            abortReason = Protocol.JoinAbortWorldChanged;
            return null;
        }
        var footer = WhsSave.VerifyFooter(hostBytes);
        if (!footer.Ok || !TryBindJoinCharacter(join, tag, footer.Stored))
        {
            why = "The offered world does not carry a verified run binding for the selected character.";
            abortReason = Protocol.JoinAbortWorldChanged;
            return null;
        }
        if (selection.First is { } src)
        {
            if (!Wo135Rules.SameBuild(src.Parts.Build, receivedBuild))
            {
                why = "The selected character and the received world are from different game builds.";
                abortReason = Protocol.JoinAbortNoHenrySource;
                return null;
            }
            if (src.Parts.Seed == s && (src.Mode == "fresh" || !Wo157HostedSeed(s)))
            {
                why = "The selected character source is a copy of the received world.";
                abortReason = Protocol.JoinAbortNoHenrySource;
                return null;
            }
            Wo134OnRestorePick(null);
            Console.WriteLine($"MP-HENRY joiner: join 0x{joinId:x8}: world {tag} henry_source={(src.Mode == "fresh" ? "fresh-local" : src.Mode)} ({src.Detail})");
            return src;
        }
        if (join.CharacterScope is not null || _henry.HasWorld(tag))
        {
            List<string> branch;
            lock (_hostBranch) branch = [.. _hostBranch];
            var index = branch.FindIndex(md5 => string.Equals(md5, footer.Stored, StringComparison.OrdinalIgnoreCase));
            var verified = index >= 0 ? branch.Skip(index).ToArray() : [footer.Stored];
            var pick = join.CharacterScope is { } scoped ? _henry.PickScoped(scoped, verified) : _henry.PickVerified(tag, verified);
            if (pick is null) { why = $"world {tag}: no snapshot could be read (all broken)"; abortReason = Protocol.JoinAbortNoHenrySource; return null; }
            Console.WriteLine($"MP-HENRY joiner: join 0x{joinId:x8}: world {tag} -- RESTORE snapshot {pick.Snapshot.Short} ({pick.Snapshot.Source}): {pick.How}");
            if (!footer.Ok || !verified.Contains(pick.Snapshot.Md5, StringComparer.OrdinalIgnoreCase) ||
                !Wo135Rules.SameBuild(pick.Parts.Build, receivedBuild))
            {
                why = "No compatible character snapshot is paired with the received host save.";
                abortReason = Protocol.JoinAbortNoHenrySource;
                return null;
            }
            Wo134OnRestorePick(pick.Snapshot);   // WO-134: its chest ledger comes back with it
            if (join.CharacterScope is { } completed)
                Console.WriteLine($"MP-HENRY character_restored id={completed.CharacterId.ToString("N")[..8]} run={completed.Run.RunId.ToString("N")[..8]} join=0x{joinId:x8}");
            return new HenryChoice("restore", pick.Parts, selection.Scoped?.Home.Save, $"snapshot {pick.Snapshot.Short}")
            { Fingerprint = selection.Scoped?.Home.Fingerprint };
        }
        Wo134OnRestorePick(null);   // WO-134: a first join: an empty chest ledger
        why = "Choose a local character before requesting this world.";
        abortReason = Protocol.JoinAbortNoHenrySource;
        return null;
    }

    /// <summary>After Ready: the join save pairs with the Henry just loaded (a matched pair: the joiner has not played in it yet).</summary>
    private void Wo125AfterReady(string tag, string mode, WhsSave.HenryParts parts, byte[]? offerMd5, uint seq, uint hostWorldEpoch)
    {
        if (_rejoinPending || _rewinding) _ = W164NoticeAsync(Wo164Rules.ReloadBackText);   // WO-164 RL: the rejoin after the host's reload
        _joinedTag = tag;
        _t3JoinedHostWorld = hostWorldEpoch;
        _t3RejoinReason = "-";
        _rewinding = false;
        _rejoinPending = false;
        Wo202RejoinReset("joined");   // WO-202 III.3
        _w207LinkRejoin = false;      // WO-207 C.4: back in
        if (_resetTag == tag) _resetTag = null;
        if (_newWorldLeavePending || (_peerSeedKnown && _peerTag != tag))
        {
            // The host moved to another playthrough while this join ran: the join save still pairs (the joiner
            // was in that world at it), then this game leaves for its own save and joins the new world.
            _newWorldLeavePending = false;
            _joinFromWorldOnce = true;
            Console.WriteLine($"T3-AUTO-REJOIN trigger=host-playthrough-changed-during-join decision=leave-then-join loaded_tag={tag} host_tag={_peerTag}");
            Console.WriteLine($"MP-HENRY joiner: the host is in another playthrough now ({_peerTag}) -- leaving world {tag} after this join");
            _ = Task.Run(() => LeaveSharedWorldAsync("host-new-world", "Your host loaded a different game."));
        }
        Wo184OnJoined(offerMd5, seq);   // WO-184: the join save is this world's first pair; a final snapshot pairs with it until the host saves
        Wo189OnJoined();                // WO-189 B3: the host's active quest profiles asked for (mp_profile_mirror)
        if (offerMd5 is null) { Console.WriteLine("MP-HENRY joiner: the offer's md5 is unknown -- the join save is not paired"); return; }
        try
        {
            parts.Origin = WhsSave.HenryParts.OriginSnapshot;
            if (Volatile.Read(ref _joinedCharacter) is { } character)
            {
                var scope = character.Scope;
                string md5 = Convert.ToHexString(offerMd5);
                if (scope.Run.WorldTag != tag || !string.Equals(scope.HostSaveMd5, md5, StringComparison.OrdinalIgnoreCase) ||
                    !character.Selection.Catalog.TryNoteRun(scope)) return;
                var scopedSnap = _henry.StoreScoped(scope, parts, mode == "restore" ? HenryStore.SourceJoin : mode,
                    Wo134SnapshotLedgerJson(), () => ReferenceEquals(Volatile.Read(ref _joinedCharacter), character));
                if (scopedSnap is not null) Wo134OnSnapshotStored(scopedSnap);
                return;
            }
            var joinSnap = _henry.Store(tag, parts, Convert.ToHexString(offerMd5), mode == "restore" ? HenryStore.SourceJoin : mode, $"the join save (seq {seq})", lootLedgerJson: Wo134SnapshotLedgerJson());
            Wo134OnSnapshotStored(joinSnap);   // WO-134: the chest ledger pairs with it
            _henry.MarkJoined(tag);
            Wo157ClaimJoinedWorld(tag);   // WO-157: this folder is this host's world from now on
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            Console.WriteLine($"MP-HENRY joiner: the join save's pair was NOT stored: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- joiner: snapshots paired with host saves

    private int _snapBusy;
    private readonly object _snapGate = new();
    private (int Playline, DateTime Since, TaskCompletionSource<SnapshotFile> Tcs)? _snapWait;

    /// <summary>Guard: a playline near the engine's 100-quicksave rotation is never snapshotted into (the rotation could remove the player's oldest).</summary>
    public const int SnapshotQuicksaveGuard = 95;

    // WO-184: the snapshot itself is GameBridge.Wo184.cs (Wo184OnHostSaveIn -> Wo184TakeAsync): asked at every host save, two hard blocks only,
    // the engine's own "not now" asked again every 2 s, stuck after 60 s, a final snapshot at every leave (WO-183's gate and its owed list are gone).

    /// <summary>The folder watch may serve the legacy snapshot reader. It never authorizes a file move.</summary>
    private bool Wo125OnJoinerSaveObserved(string path)
    {
        var id = WorldSaved.ParsePath(path);
        // The scoped owner consumes only an engine write receipt plus a request marker in the file.
        if (Volatile.Read(ref _joinedCharacter) is not null) return _joinedWorld;
        lock (_snapGate)
        {
            if (_snapWait is { } w &&
                id is { } i && i.Playline == w.Playline && i.Kind == Protocol.SaveKindQuick && File.GetLastWriteTimeUtc(path) >= w.Since)
            {
                w.Tcs.TrySetResult(new(path));
                return true;
            }
        }
        if (Wo184TakeLateFile(path)) return true;
        if (!_joinedWorld) return false;
        if (Warned.TryAdd("unowned-guest-save:" + path, 0))
            Console.WriteLine($"MP-SAVELOCK retained {SaveDisplay(path)} reason=unconfirmed-engine-save-owner");
        return true;
    }

    private void Wo125RecordScopedGuestSave(string path, string md5)
    {
        if (ResolveSavesDirForJoin() is not string saves || H21EngineSavePath(saves, path) is not string reported ||
            WorldSaved.ParsePath(reported) is not { } save || !TryCheckpointProfile(saves, out _, out string profile)) return;
        _henry.RecordScopedGuestSave(profile, save.Playline, Path.GetFileName(reported), md5);
    }

    // ---------------------------------------------------------------- sweeps (agent start, before every join)

    private readonly JoinWorldJournal _joinWorldJournal = new();
    private string? _wo125ActiveExitPath;

    /// <summary>At startup retire only agent-owned copies with proven file identity and no submitted load.</summary>
    internal int Wo125Sweep(string why)
    {
        string? saves = ResolveSavesDirForJoin();
        if (saves is null) return 0;
        saves = Path.GetFullPath(saves);
        if (!_joinWorldJournal.CanSweepPath(saves, directory: true)) return 0;
        int n = 0;
        if (why == "agent start")
        {
            lock (_joinGate)
            {
                var protectedPaths = _retainedJoinWorlds.ProtectedPaths();
                if (_jj?.PlacedPath is string pending) protectedPaths.Add(pending);
                if (_wo125ActiveExitPath is string exit) protectedPaths.Add(exit);
                n = _joinWorldJournal.Sweep(saves, protectedPaths.Contains);
            }
        }
        if (why == "agent start" && _henry.Ledger().Any(e => e.State != "done" && e.Kind != "scoped-guest") &&
            Warned.TryAdd("legacy-save-ledger-retained", 0))
            Console.WriteLine("MP-HENRY sweep retained reason=legacy-ledger-has-no-bound-engine-file");
        // A copy of a host world placed by hand: warn (once per file per agent run), never touch.
        var known = _henry.Worlds().Select(w => _henry.BaseTagOf(w.Tag)).ToHashSet();
        known.ExceptWith(_henry.Hosted());   // WO-157: this install's own playthrough of a world it also joined is no copy
        var copies = new List<string>();
        if (known.Count > 0)
            foreach (var s in ListOwnSaves(saves))
                if (WhsSave.ReadSeedFromFile(s.FullPath) is uint sd && known.Contains(WhsSave.SeedTag(sd)) && Warned.TryAdd("sweep:" + s.FullPath, 0))
                    copies.Add(s.Display);
        if (copies.Count > 0)
            Console.WriteLine($"MP-HENRY sweep WARNING {copies.Count} save(s) have the playthrough seed of a host world this player joined -- not saves the mod made (left alone), never used as this player's own: {string.Join(", ", copies.Take(6))}{(copies.Count > 6 ? ", ..." : "")}");
        Console.WriteLine($"MP-HENRY sweep ({why}): {n} file(s) removed from the playlines");
        return n;
    }

    private void Wo125AtStart()
    {
        try
        {
            var gone = _henry.CleanupStale();
            var worlds = _henry.Worlds();
            Console.WriteLine($"MP-HENRY store <data>/henry: {worlds.Count} world(s), {worlds.Sum(w => w.Count)} snapshot(s); 90-day rule removed {gone.Count}; keep the pairs of the newest {HenryStore.KeepPerWorld} host saves per world, {HenryStore.KeepPerHostSave} each");
            Wo125Sweep("agent start");
        }
        catch (Exception ex) { Console.WriteLine($"MP-HENRY start failed: {ex.Message}"); }
    }

    // ---------------------------------------------------------------- the way out with no own save

    /// <summary>
    /// No own save to go back to: a listed file with no world in it (the last
    /// world's header over an empty stream, 837 B in the probe) fails its load
    /// deterministically and the engine returns to the main menu with a
    /// "Game load failed / OK" box (observed from inside a world). Nothing in it
    /// could ever load as a world.
    /// </summary>
    private async Task<bool> Wo125ExitToMenuAsync(string why, CancellationToken ct, bool relayClosed)
    {
        string? saves = ResolveSavesDirForJoin();
        int pl = _lastWorldPlayline >= 0 ? _lastWorldPlayline : 0;
        if (saves is null || _lastWorldDesc is null) { Console.WriteLine($"MP-HENRY no own save and no way to build the exit file ({(saves is null ? "no saves folder" : "no world header kept")}) -- staying"); return false; }
        var raw = new byte[4 + 6];
        BinaryPrimitives.WriteUInt32LittleEndian(raw, 23);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(4), 0x01FA);
        var file = WhsSave.Deflate(_lastWorldDesc, raw, new byte[44]);
        uint exitId = (uint)Random.Shared.Next(1, int.MaxValue);
        string name = $"mpexit{exitId:x8}";
        string path = Path.Combine(saves, $"playline{pl}", name + ".whs");
        byte[] sha = System.Security.Cryptography.SHA256.HashData(file);
        bool registered = false, admitted = false;
        JoinEngineReceipt? exitReceipt = null;
        HomeSaveReadLease? exitLease = null;
        try
        {
            lock (_joinGate)
            {
                _joinWorldJournal.Register(saves, path, "menu-exit", exitId, sha, isExit: true);
                registered = true;
                _wo125ActiveExitPath = path;
            }
            using (var stage = _joinWorldJournal.CreateStage(saves, path, exitId, sha, isExit: true)) stage.Write(file);
            _joinWorldJournal.SealStage(saves, path, exitId, sha, isExit: true);
            _joinWorldJournal.Publish(saves, path, exitId, sha, isExit: true);

            var listed = await _combat.SaveListAsync(1, pl, name);
            if (listed is null || !listed.Listed) { Console.WriteLine($"MP-HENRY exit file not listed after the rescan -- staying ({why})"); return false; }
            Console.WriteLine($"MP-HENRY no own save to go back to ({why}) -- loading an empty file ({SaveDisplay(path)}, {file.Length} B, no world in it): the engine fails the load and returns to the MAIN MENU");
            SetJoinUi("exit-menu", "You have no save of your own to go back to, so the game returns to the main menu. It shows \"Game load failed\" -- press OK.");
            if (!await Wo177WaitQuietAsync("exit-to-menu"))
            {
                Console.WriteLine("MP-HENRY exit cancelled: the engine's pre-load gate refused");
                return false;
            }
            // Pin the name and its parents on Windows before proving the journal's
            // identity. Keep that exact path for the engine's asynchronous read.
            exitLease = HomeSaveReadLease.TryAcquire(saves, new(pl, name + ".whs", path, 0), Convert.ToHexString(sha));
            using (var submittedLease = exitLease is null ? null :
                _joinWorldJournal.PrepareLoadSubmission(saves, path, exitId, sha, isExit: true))
            {
                if (submittedLease is null)
                {
                    Console.WriteLine("MP-HENRY exit refused reason=unverified-owned-file");
                    return false;
                }
            }
            bool sent = false;
            try
            {
                sent = await P5LoadGameCoreAsync(pl, name, "exit-to-menu", () =>
                {
                    if (_transport is LogTailGameTransport tail)
                        exitReceipt = new JoinEngineReceipt(tail, _combat, path, saves);
                    admitted = true;
                    return true;
                }, ct);
            }
            catch (Exception ex) when (admitted && (ex is IOException or OperationCanceledException or HttpRequestException))
            {
                // The engine can receive the load before HTTP reports a failed response.
                Console.WriteLine($"MP-HENRY exit handoff uncertain transport={ex.GetType().Name}");
            }
            if (exitReceipt is null) return sent;

            bool loaded = await WaitForJoinReceiptAsync(exitReceipt.Terminal.Task,
                "MP-HENRY waiting for the exact exit load to settle; its file stays protected");
            if (!loaded && _leaveInProgress && OwnLoadExpected) Wo124OnLoadFailedToMenu();
            return !loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            Console.WriteLine($"MP-HENRY exit file failed: {ex.GetType().Name}");
            return false;
        }
        catch (OperationCanceledException) when (relayClosed || exitReceipt?.Terminal.Task.IsCanceled == true || _joinEngineLifetime.IsCancellationRequested)
        {
            Console.WriteLine("MP-HENRY exit load interrupted; unconfirmed files stay protected");
            return false;
        }
        finally
        {
            exitReceipt?.Dispose();
            exitLease?.Dispose();
            if (registered)
            {
                if (!admitted || exitReceipt?.CanRemoveFile == true)
                {
                    bool released = _joinWorldJournal.TryDeleteOwned(saves, path, exitId, sha, isExit: true);
                    Console.WriteLine($"MP-HENRY exit cleanup reservation_released={On(released)} reason={(admitted ? "engine-terminal" : "pre-dispatch")}");
                }
                else Console.WriteLine("MP-HENRY exit file retained reason=no-terminal-engine-receipt");
                lock (_joinGate)
                    if (_wo125ActiveExitPath == path) _wo125ActiveExitPath = null;
            }
        }
    }

    // ---------------------------------------------------------------- commands

    private void Wo125OnEvent(string name, string? arg)
    {
        switch (name)
        {
            case "wo125_choice":        // mp_join_henry <auto|fresh|playlineN/file>, typed
                Wo125OnConsoleChoice(arg ?? "");
                return;
            case "wo125_reset":         // mp_henry_reset
            {
                if (_peerTag is not string tag) { Console.WriteLine("MP-HENRY mp_henry_reset: the host's world is not known (no host, or it has not identified its world)"); _ = Wo125ToastAsync("mp_henry_reset: no host world known yet."); return; }
                bool had = _henry.DeleteWorld(tag, "mp_henry_reset (the player starts over in this host's world)");
                _firstCharacter = null; _chooseAsked = false;
                if (_joinedWorld && _joinedTag == tag) _resetTag = tag;
                Console.WriteLine($"MP-HENRY mp_henry_reset world {tag}: {(had ? "Henry files deleted" : "there were none")}; the next join asks Bring / Fresh again{(_resetTag == tag ? " (no snapshots for it this session)" : "")}");
                _ = Wo125ToastAsync(had ? "Your character for this world was deleted. Next join: bring or start fresh." : "No character stored for this world.");
                return;
            }
            case "wo125_files":         // mp_henry_files [delete <tag>]
            {
                var p = (arg ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 2 && p[0] == "delete")
                {
                    bool ok = Regex.IsMatch(p[1], "^[0-9a-f]{10}$") && _henry.DeleteWorld(p[1], "mp_henry_files delete");
                    Console.WriteLine($"MP-HENRY mp_henry_files delete {p[1]}: {(ok ? "deleted" : "no such world")}");
                    _ = Wo125ToastAsync(ok ? $"World {p[1]} deleted." : $"No world {p[1]}.");
                    return;
                }
                var ws = _henry.Worlds();
                Console.WriteLine($"MP-HENRY mp_henry_files: {ws.Count} world(s) (the {HenryStore.StaleDays}-day rule deletes a world not joined for {HenryStore.StaleDays} days)");
                var lines = new List<string>();
                foreach (var w in ws)
                {
                    string l = FormattableString.Invariant($"{w.Tag}  last joined {w.LastJoinedUtc:yyyy-MM-dd}  {w.Count} snapshot(s)  {w.Bytes / 1024.0:F0} KB  first={w.FirstSource}{(w.Tag == _peerTag ? "  <- your host's world" : "")}");
                    lines.Add(l);
                    Console.WriteLine("MP-HENRY   " + l);
                }
                _ = ExecLuaAsync($"if KCD2MP_Wo125FilesShow then KCD2MP_Wo125FilesShow(\"{EscapeLua(string.Join("|", lines))}\") end");
                return;
            }
        }
    }

    private Task Wo125ToastAsync(string msg) => ExecLuaAsync($"if KCD2MP_Wo124Msg then KCD2MP_Wo124Msg(\"{EscapeLua(msg)}\") end");
}

