// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Collections.Concurrent;
using System.Globalization;
using KcdMp.Wire;

namespace KcdMp.Client;

public partial class GameBridge
{
    // ================================================================ WO-171 M: the partner's own colour and facing on the map

    private const string Wo171SlotMismatch = "marker_slot_mismatch";
    private volatile Wo171Rules.Style _w171Style = Wo171Rules.Style.Rotating;   // mp_partner_marker_style
    private volatile int _w171Offset;                                          // mp_marker_dir_offset 0..7
    private volatile bool _w171Flip;                                           // mp_marker_dir_flip
    private readonly object _w171SlotLock = new();
    private Stream? _w171SlotStream;
    private byte _w171SlotHost = 0xFF;
    private Guid _w171SlotHostMember;
    private Guid _w171RosterInstance;
    private uint _w171HostWorld;
    private HostRun? _w171GuestRun;
    private Wo171Rules.SlotTable? _w171Slots;
    private long _w171NextSlotSend, _w171SlotRefusals;

    private sealed class W171Pin
    {
        public readonly Wo171Rules.MoveGate Gate = new();
        public int Slot, RawOctant = -1, SentCode = -1, Octant;
        public long LastTurnMs = long.MinValue / 2, LastSendMs = long.MinValue / 2, Moves;
        public float X, Y, Z;
        public string Name = "player";
    }

    private readonly ConcurrentDictionary<byte, W171Pin> _w171Pins = new();
    private readonly ConcurrentDictionary<int, List<string>> _w171Taken = new();   // slot -> the names its revisions carry in this game run
    private string? _w171RunStamp;
    private long _w171Turns, _w171MoveSends, _w171Heartbeats, _w171Adds, _w171Removes;

    /// <summary>The Lua's <c>w171_cfg style=rotating|flat|companion offset=0..7 flip=on|off</c> (any subset).</summary>
    private void Wo171OnCfg(string? arg)
    {
        foreach (var part in (arg ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0 || eq == part.Length - 1) continue;
            string k = part[..eq], v = part[(eq + 1)..];
            if (k == "style" && Wo171Rules.TryParseStyle(v, out var st)) _w171Style = st;
            else if (k == "offset" && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int off) && off is >= 0 and <= 7) _w171Offset = off;
            else if (k == "flip" && v is "on" or "off") _w171Flip = v == "on";
        }
        Console.WriteLine($"WO171-CFG marker style={Wo171Rules.StyleWord(_w171Style)} offset={_w171Offset} flip={(_w171Flip ? "on" : "off")}");
    }

    private byte Wo171HostId() => W137Host ? _myGhostId
        : _combatRoleApplied && !_isDamageAuthority && !HostAbsentPause && _hostModeKnown &&
          _hostSharedWorld && IsLivePeer(_hostModeFrom) ? _hostModeFrom : (byte)0xFF;

    // Called under _w171SlotLock. A temporarily unknown host cannot replace a confirmed table on the same stream.
    private void Wo171BindSlots(Stream? stream, byte host)
    {
        bool sameStream = ReferenceEquals(_w171SlotStream, stream);
        bool ownHost = W137Host && host == _myGhostId;
        uint world = _w206.World;
        Guid rosterInstance = _peers.RelayInstance;
        if (rosterInstance != Guid.Empty && rosterInstance != _w171RosterInstance)
        {
            _w171Slots = null;
            _w171RosterInstance = rosterInstance;
            _w171SlotHostMember = Guid.Empty;
            _w171NextSlotSend = 0;
        }
        // Host ownership comes from the relay. The same local host process can
        // keep its reservations while it reclaims its still-loaded world.
        bool keepLocal = _w171HostWorld != 0 && _w171HostWorld == world && config.IsHosting && _sharedWorld &&
            !_leaveInProgress && !_gameQuitting;
        if (keepLocal && host == 0xFF) return;
        if (sameStream && host == 0xFF)
        {
            if (HostAbsentPause) return;
            bool separate = _isDamageAuthority ? _combatRoleApplied && !_sharedWorld
                : _hostModeKnown && !_hostSharedWorld && IsLivePeer(_hostModeFrom);
            if (!separate) return;
        }
        Guid hostMember = host != 0xFF ? _peers.RosterMemberOf(host) : Guid.Empty;
        var guestRun = ownHost || host == 0xFF ? null : Volatile.Read(ref _lastHostRun);
        if (guestRun?.Epoch != world) guestRun = null;
        if (sameStream && _w171SlotHost == host && _w171SlotHostMember == hostMember)
        {
            if (ownHost) _w171HostWorld = world;
            _w171GuestRun = guestRun;
            return;
        }
        // The returning host can receive another ghost ID. Retain the table for the same member and verified run.
        bool sameReturnedHost = sameStream && hostMember != Guid.Empty && _w171SlotHostMember == hostMember &&
            guestRun is not null && guestRun == _w171GuestRun;
        _w171SlotStream = stream;
        _w171SlotHost = host;
        _w171SlotHostMember = hostMember;
        _w171HostWorld = ownHost ? world : 0;
        _w171GuestRun = guestRun;
        if (!sameReturnedHost && (!ownHost || !keepLocal)) _w171Slots = null;
        _w171NextSlotSend = 0;
    }

    private async Task Wo171SyncSlotsAsync()
    {
        List<byte> peers;
        Wo171Rules.SlotTable slots;
        Stream? stream;
        CancellationToken ct;
        lock (_w171SlotLock)
        {
            stream = _wo122Stream;
            ct = _wo122Ct;
            byte host = Wo171HostId();
            Wo171BindSlots(stream, host);
            if (host != _myGhostId || !W137Host || stream is null || ct.IsCancellationRequested) return;

            peers = LivePartners();
            slots = _w171Slots ?? new();
            // First observation in the host's live relay roster. Relay id breaks ties within the same tick.
            foreach (byte peer in peers) slots = slots.AssignGuest(_peers.RosterMemberOf(peer));
            long now = Environment.TickCount64;
            if (peers.Count == 0 || (ReferenceEquals(_w171Slots, slots) && now < _w171NextSlotSend)) return;
            _w171Slots = slots;
            _w171NextSlotSend = now + 5_000;
        }

        // Repeat the full table for late peers and receivers still waiting for the host's session announcement.
        string wire = slots.ToWire();
        foreach (byte peer in peers)
        {
            if (!W137Host || ct.IsCancellationRequested || !ReferenceEquals(stream, _wo122Stream)) return;
            await W164SendAsync(peer, Protocol.W164MarkNote, wire, ct);
        }
    }

    private void Wo171OnSlotSnapshot(byte src, string text)
    {
        lock (_w171SlotLock)
        {
            ProcessorFrameScope.ThrowIfAbandoned();
            byte host = Wo171HostId();
            Wo171BindSlots(_wo122Stream, host);
            if (_wo122Stream is null || _wo122Ct.IsCancellationRequested || W137Host || host == 0xFF || src != host)
            { Wo171RefuseSlots("host-not-confirmed"); return; }
            if (!Wo171Rules.SlotTable.TryRead(text, out var slots)) { Wo171RefuseSlots("malformed"); return; }
            if (_w171Slots is { } previous && !slots.Preserves(previous)) { Wo171RefuseSlots("changed-reservation"); return; }
            _w171Slots = slots;
        }
    }

    private void Wo171RefuseSlots(string why)
    {
        long n = Interlocked.Increment(ref _w171SlotRefusals);
        if (n <= 4 || n % 64 == 0) Console.WriteLine($"WO171-SLOTS refused reason={why} n={n}");
    }

    private int? Wo171PinSlot(byte ghost)
    {
        lock (_w171SlotLock)
        {
            byte host = Wo171HostId();
            Wo171BindSlots(_wo122Stream, host);
            if (_wo122Stream is null || !ReferenceEquals(_w171SlotStream, _wo122Stream) || _wo122Ct.IsCancellationRequested) return null;
            // The old one-peer marker harness bypasses the join and supplies no session-mode frame.
            if (_w171SlotHost == 0xFF)
                return W164TestJoinerInWorld && !_isDamageAuthority && LivePartners() is [var only] && ghost == only ? 1 : null;
            Guid member = _peers.RosterMemberOf(ghost);
            // The old ghost ID can be recycled while the host is absent.
            bool isHost = ghost == _w171SlotHost && (ghost == host || (member != Guid.Empty && member == _w171SlotHostMember));
            return isHost ? 1 : _w171Slots?.FindSlot(member, isHost: false);
        }
    }

    private bool Wo171PinSession()
    {
        if (W137Host || W137Joiner) return true;
        if (!_joinedWorld || !_combatRoleApplied || _isDamageAuthority || !_hostAuthority) return false;

        // A missing host mode can briefly make W137Joiner fall back to the local shared-world setting.
        lock (_w171SlotLock)
        {
            byte host = Wo171HostId();
            Wo171BindSlots(_wo122Stream, host);
            return host == 0xFF && _w171Slots is not null && _wo122Stream is not null && !_wo122Ct.IsCancellationRequested;
        }
    }

    /// <summary>One partner's pin this second: sets it, moves it, turns it or just keeps it alive.</summary>
    private async Task W171PinOneAsync(byte g, float x, float y, float z, long nowMs)
    {
        var st = _w171Pins.GetOrAdd(g, _ => new W171Pin());
        string? who = _peers.NameOf(g);
        st.Name = Wo171Rules.SanitizeLabel(who);
        int? hostSlot = Wo171PinSlot(g);
        int slot = hostSlot ?? 0;
        int rev = slot is >= 1 and <= Wo171Rules.Colours ? Wo171NoteName(slot, st.Name) : 0;
        float yaw = _ghostLastYaw.TryGetValue(g, out var yv) ? yv : 0f;   // a mounted partner's stream is the horse's too (the saddle turns with it)
        st.RawOctant = Wo171Rules.PickOctant(st.RawOctant, Wo171Rules.HeadingDeg(yaw));
        int oct = Wo171Rules.ApplyOffsetFlip(st.RawOctant, _w171Offset, _w171Flip);
        var style = _w171Style;
        int code = Wo171Rules.CodeFor(style, slot, oct, _w171Offset, rev);

        bool first = st.SentCode < 0;
        bool moveDue = first || st.Gate.Due(x, y, nowMs);
        bool turnDue = !first && (st.Slot != slot || (st.SentCode == 0) != (code == 0)
            || Wo171Rules.TurnDue(st.SentCode, code, st.LastTurnMs, nowMs));
        bool beat = !first && nowMs - st.LastSendMs >= 5000;
        if (!moveDue && !turnDue && !beat) return;

        int sendCode = first || turnDue ? code : st.SentCode;
        float sx = moveDue ? x : st.X, sy = moveDue ? y : st.Y, sz = moveDue ? z : st.Z;
        if ((first || turnDue || beat) && sendCode != 0) await _combat.PinLabelAsync(g, st.Name);   // the hover title (the DLL forgets its pins at a load: said again every 5 s)
        if (!await _combat.MirrorGraveAsync(3, g, (ulong)sendCode, sx, sy, sz)) return;
        if (hostSlot is int expected)
        {
            int mismatch = Wo171Rules.PictureSlot(sendCode) == (style == Wo171Rules.Style.Companion ? 0 : expected) ? 0 : 1;
            T3Increment(Wo171SlotMismatch, mismatch);
        }
        Interlocked.Increment(ref _w164PinSets);
        _w164PinAt[g] = (sx, sy);
        st.LastSendMs = nowMs;
        st.Slot = slot;
        string colour = slot is >= 1 and <= Wo171Rules.Colours ? Wo171Rules.ColourNames[slot - 1] : "none";
        string dir = Wo171Rules.Compass[oct];
        if (first)
        {
            st.SentCode = sendCode; st.LastTurnMs = nowMs; st.X = sx; st.Y = sy; st.Z = sz; st.Octant = oct;
            st.Gate.Sent(sx, sy, nowMs, counted: false);
            Interlocked.Increment(ref _w171Adds);
            Console.WriteLine(FormattableString.Invariant($"WO171-MARK slot={slot} colour={colour} dir={dir} name=\"{st.Name}\" picture={Wo171Rules.PictureName(sendCode)} style={Wo171Rules.StyleWord(style)} added"));
            return;
        }
        if (moveDue)
        {
            st.X = sx; st.Y = sy; st.Z = sz;
            st.Gate.Sent(sx, sy, nowMs, counted: true);
            Interlocked.Increment(ref _w171MoveSends);
            if (++st.Moves % 20 == 1)
                Console.WriteLine(FormattableString.Invariant($"WO171-MARK slot={slot} colour={colour} dir={dir} moved (move {st.Moves}; {st.Gate.InLastMinute(nowMs)} in the last minute, cap {Wo171Rules.MoveCapPerMinute}; one line per 20)"));
        }
        if (turnDue)
        {
            st.SentCode = sendCode; st.LastTurnMs = nowMs; st.Octant = oct;
            Interlocked.Increment(ref _w171Turns);
            Console.WriteLine(FormattableString.Invariant($"WO171-MARK slot={slot} colour={colour} dir={dir} name=\"{st.Name}\" picture={Wo171Rules.PictureName(sendCode)} turned"));
        }
        else if (!moveDue) Interlocked.Increment(ref _w171Heartbeats);
    }

    /// <summary>
    /// A colour's player name for the hover text: returns the revision of the pictures to use. A name this game run has not carried yet is
    /// written to its own small file and loaded by the mod, once; the same name again (a rejoin, an agent restart) reuses its revision.
    /// </summary>
    private int Wo171NoteName(int slot, string name)
    {
        try
        {
            if (KcdLogLocator.Find() is not string log || Path.GetDirectoryName(log) is not string root) return 0;
            string dir = Path.Combine(root, "Mods", "kdcmp", "Localization");
            if (_w171RunStamp is null)
            {
                string[] head;
                using (var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs))
                {
                    var first = new List<string>();
                    for (string? l; first.Count < 12 && (l = sr.ReadLine()) is not null;) first.Add(l);
                    head = first.ToArray();
                }
                _w171RunStamp = Wo171Rules.RunStamp(head) ?? "unknown";
                Directory.CreateDirectory(dir);
                foreach (var old in Directory.GetFiles(dir, "kct_n_*.xml"))
                    if (!Path.GetFileName(old).StartsWith($"kct_n_{_w171RunStamp}_", StringComparison.Ordinal)) { try { File.Delete(old); } catch { } }
            }
            var taken = _w171Taken.GetOrAdd(slot, s => Wo171ReadTaken(dir, s));
            lock (taken)
            {
                int rev = Wo171Rules.RevFor(taken, name, out bool isNew);
                if (isNew)
                {
                    string file = Wo171Rules.NamesFileName(_w171RunStamp, slot, rev);
                    File.WriteAllText(Path.Combine(dir, file), Wo171Rules.NamesXml(slot, rev, name), new System.Text.UTF8Encoding(false));
                    taken.Add(name);
                    _ = ExecLuaAsync($"if KCD2MP_W171LoadNames then KCD2MP_W171LoadNames(\"{Wo171Rules.NamesFolder}/{file}\") end");
                    Console.WriteLine($"WO171-NAMES slot={slot} revision={rev} name=\"{name}\" written ({file}); the mod loads it once");
                }
                else if (rev == Wo171Rules.Revisions - 1 && taken.Count == Wo171Rules.Revisions && taken[rev] != name && !_w171FullTold.Contains(slot))
                {
                    _w171FullTold.Add(slot);
                    Console.WriteLine($"WO171-NAMES slot={slot} has carried {Wo171Rules.Revisions} different names in this game run: the hover text keeps the last one's until the game restarts");
                }
                return rev;
            }
        }
        catch (Exception ex) { Console.WriteLine($"WO171-NAMES could not write the hover names ({ex.GetType().Name}): the hover text stays blank"); return 0; }
    }

    private readonly HashSet<int> _w171FullTold = new();

    /// <summary>The names this game run's earlier files already carry for a colour (an agent restart inside the same game run).</summary>
    private List<string> Wo171ReadTaken(string dir, int slot)
    {
        var taken = new List<string>();
        for (int r = 0; r < Wo171Rules.Revisions; r++)
        {
            string path = Path.Combine(dir, Wo171Rules.NamesFileName(_w171RunStamp!, slot, r));
            if (!File.Exists(path)) break;
            var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(path), @"</Cell><Cell>([^<]*)</Cell>");
            taken.Add(m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value) : "?");
        }
        return taken;
    }

    private void W171Removed(byte g)
    {
        if (!_w171Pins.TryRemove(g, out var st)) return;
        Interlocked.Increment(ref _w171Removes);
        string colour = st.Slot is >= 1 and <= Wo171Rules.Colours ? Wo171Rules.ColourNames[st.Slot - 1] : "none";
        Console.WriteLine($"WO171-MARK slot={st.Slot} colour={colour} dir={Wo171Rules.Compass[st.Octant & 7]} name=\"{st.Name}\" removed");
    }

    private string Wo171StatsText() => FormattableString.Invariant(
        $"style={Wo171Rules.StyleWord(_w171Style)} offset={_w171Offset} flip={(_w171Flip ? "on" : "off")} added={Interlocked.Read(ref _w171Adds)} turns={Interlocked.Read(ref _w171Turns)} move_sends={Interlocked.Read(ref _w171MoveSends)} heartbeats={Interlocked.Read(ref _w171Heartbeats)} removed={Interlocked.Read(ref _w171Removes)} slots={Volatile.Read(ref _w171Slots)?.GuestCount ?? 0} {Wo171SlotMismatch}={_t3Counters.GetValueOrDefault(Wo171SlotMismatch)}");
}
