// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using KcdMp.Wire;

namespace KcdMp.Client;

// WO-195: three field fixes from a 0.60.5 joiner's evening (Wo195Rules has the rules; docs/WO-195-findings.md the evidence).
//   1  mp_dead_release (joiner): at every death apply of a copy ([npcdeath]: owner-death, 0x31 FATAL, 0x27 transition / resync) every fight hold goes at once -- WO-132's engagement (logged now: 0.60.5 removed it quietly), WO-147's
//      own engagement, WO-187's guard hold and opponent wish (and its pin), the DLL's combat mode on the body (op 56). A copy marked dead
//      is never engaged, held or wished again. One second later the body is checked (WO195-FALL: combat mode off, physics a ragdoll),
//      two seconds later this player's own combat state (WO195-PLAYER, the mod's Lua).
//   2  mp_stealth_witness: the joiner's mod sends its stealth snapshot (w195_stealth, once a second on a change); every crime report
//      to the host carries the latest (the 8th field). The host feeds its NPCs' sleep from WO-141's own activity read
//      (KCD2MP_W195Asleep) and its witness rule uses both (the mod's Lua, WO195-WITNESS lines).
//   3  mp_body_loot_host: a body take marked "screen" (the loot screen's own key) goes to the host's take as such (the host's Lua grants it
//      once per body and class, and removes the copy its own loot screen would make).
// Log tags: WO195-RELEASE, WO195-FALL, WO195-DEAD, WO195-CFG, WO195-STATS (the Lua: WO195-WITNESS, WO195-LOOT, WO195-PLAYER).
public partial class GameBridge
{
    private volatile bool _w195DeadRelease = Wo195Rules.DefaultDeadRelease, _w195StealthWitness = Wo195Rules.DefaultStealthWitness,
                          _w195BodyLootHost = Wo195Rules.DefaultBodyLootHost;
    private readonly record struct Wo195BodyScope(uint Eid, Guid Soul, long Life, ulong Lifecycle, ulong Load, uint World, long Pipe);
    private sealed record Wo195DeathMark(long At, Wo195BodyScope Body);
    private readonly ConcurrentDictionary<string, Wo195DeathMark> _w195Dead = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _w195Refused = new(StringComparer.Ordinal);    // copy -> what was refused (said once)
    private readonly ConcurrentDictionary<string, bool> _w195HostAsleep = new(StringComparer.Ordinal); // host: NPC -> asleep (its activity)
    private volatile string _w195Stealth = "";   // joiner: the mod's latest stealth snapshot
    private long _w195TickN, _w195Releases, _w195ReleasedEngaged, _w195Refusals, _w195FallDown, _w195FallStanding, _w195AsleepSent,
                 _w195StealthIn, _w195ScreenTakes;

    // ---------------------------------------------------------------- switches

    private void Wo195OnEvent(string name, string? arg)
    {
        switch (name)
        {
            case "w195_cfg":
                foreach (var kv in (arg ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = kv.IndexOf('=');
                    if (eq <= 0) continue;
                    bool on = kv[(eq + 1)..] == "on";
                    switch (kv[..eq])
                    {
                        case "dead": _w195DeadRelease = on; break;
                        case "stealth": _w195StealthWitness = on; break;
                        case "bodyloot": _w195BodyLootHost = on; break;
                    }
                }
                Console.WriteLine($"WO195-CFG dead_release={On(_w195DeadRelease)} stealth_witness={On(_w195StealthWitness)} body_loot_host={On(_w195BodyLootHost)}");
                _ = Wo195PushConfigAsync();
                return;
            case "w195_stealth":   // the joiner's mod: its stealth snapshot (Wo195Rules.Stealth's text)
                if (Wo195Rules.TryParseStealth(arg, out _)) { _w195Stealth = arg!; Interlocked.Increment(ref _w195StealthIn); }
                return;
            case "w195_status":
                _ = Wo195StatusAsync();
                return;
            case "w195_check":     // KCD2MP_W195Check("<npc>"): the DLL's read of a copy now (WO195-CHECK)
                if (arg is { Length: > 0 } cn && NpcNamePattern.IsMatch(cn)) _ = Wo195CheckNowAsync(cn);
                return;
        }
    }

    private void Wo195OnConnect() => _ = ExecLuaAsync("if KCD2MP_W195Cfg then KCD2MP_W195Cfg() end");

    private async Task Wo195PushConfigAsync()
    {
        try
        {
            var r = await _combat.Wo163Async(Protocol.Wo195OpConfig, [(byte)(_w195DeadRelease ? 1 : 0)]);
            if (r is { Ok: true, Payload.Length: >= 1 } x && x.Payload[0] != (_w195DeadRelease ? 1 : 0))
                Console.WriteLine($"WO195-CFG the DLL holds dead_release={x.Payload[0]}");
        }
        catch (Exception ex) { Console.WriteLine($"WO195-CFG config not pushed ({ex.GetType().Name})"); }
    }

    private async Task Wo195StatusAsync()
    {
        string? n = null;
        try
        {
            var r = await _combat.Wo163Async(Protocol.Wo195OpStatus, []);
            if (r is { Ok: true } x) n = Encoding.ASCII.GetString(x.Payload);
        }
        catch (Exception caughtFailure) { Faults.ReportThrottled("inventory-probe", caughtFailure); }
        Console.WriteLine(FormattableString.Invariant(
            $"WO195-STATS dead_release={On(_w195DeadRelease)} stealth_witness={On(_w195StealthWitness)} body_loot_host={On(_w195BodyLootHost)} releases={Interlocked.Read(ref _w195Releases)} released_engaged={Interlocked.Read(ref _w195ReleasedEngaged)} refusals={Interlocked.Read(ref _w195Refusals)} fall_down={Interlocked.Read(ref _w195FallDown)} fall_standing={Interlocked.Read(ref _w195FallStanding)} marked_dead={_w195Dead.Count} asleep_sent={Interlocked.Read(ref _w195AsleepSent)} host_asleep={_w195HostAsleep.Count(kv => kv.Value)} stealth_in={Interlocked.Read(ref _w195StealthIn)} screen_takes={Interlocked.Read(ref _w195ScreenTakes)} | native: {n ?? "no answer"}"));
    }

    /// <summary>Once a second (the WO-151 loop): the switch every 10 s, the stats every minute.</summary>
    private async Task Wo195TickAsync(CancellationToken ct)
    {
        long n = ++_w195TickN;
        if (n % 10 == 3) await Wo195PushConfigAsync();
        if (n % 60 == 40) await Wo195StatusAsync();
        _ = ct;
    }

    // ---------------------------------------------------------------- 1: dead copies

    /// <summary>A copy marked dead here (its release ran) is never engaged, guard-held or wished again.</summary>
    private bool Wo195IsDead(string npc) => _w195DeadRelease && _w195Dead.TryGetValue(npc, out var mark) &&
        Wo195BodyCurrent(npc, mark.Body);

    private bool Wo195MappedBodyCurrent(string npc, Wo195BodyScope body) =>
        _npcEntityIds.TryGetValue(npc, out uint eid) && eid == body.Eid &&
        _copySoulGuid.TryGetValue(npc, out var copy) && copy == (body.Eid, body.Soul);

    private Wo195BodyScope? Wo195CaptureBody(string npc, long life)
    {
        var lifecycle = P1Lifecycle.Current;
        if (!lifecycle.Confirmed || !lifecycle.GameplaySent || lifecycle.Generation == 0 ||
            !_npcEntityIds.TryGetValue(npc, out uint eid) || eid == 0 ||
            !_copySoulGuid.TryGetValue(npc, out var copy) || copy.Eid != eid || copy.Guid == Guid.Empty)
            return null;
        var body = new Wo195BodyScope(eid, copy.Guid, life, lifecycle.Generation, _worldLoad.Current.Generation,
            _w206.World, _combat.EdgeConnectionRevision);
        return Wo195BodyCurrent(npc, body) ? body : null;
    }

    private bool Wo195BodyCurrent(string npc, Wo195BodyScope body)
    {
        var lifecycle = P1Lifecycle.Current;
        return !_p1Loading && !_leaveInProgress && _where == GameWhere.World && LoadSequenceWritable &&
            body.Life == Interlocked.Read(ref _npcDeathLife) && body.World == _w206.World &&
            body.Load == _worldLoad.Current.Generation && lifecycle is { Confirmed: true, GameplaySent: true } &&
            body.Lifecycle == lifecycle.Generation && body.Pipe == _combat.EdgeConnectionRevision && Wo195MappedBodyCurrent(npc, body);
    }

    private static byte[] Wo195BodyRequest(Wo195BodyScope body)
    {
        var payload = new byte[sizeof(uint) + 16 + sizeof(ulong)];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, body.Eid);
        body.Soul.TryWriteBytes(payload.AsSpan(4, 16));
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(20), body.Lifecycle);
        return payload;
    }

    /// <summary>The host's stream says it lives again (a reload on their side): the mark goes.</summary>
    private void Wo195NoteAlive(string npc)
    {
        if (_w195Dead.TryRemove(npc, out _)) { _w195Refused.TryRemove(npc, out _); Console.WriteLine($"WO195-DEAD npc={npc} alive again on the host's stream (a reload) -- it may fight again"); }
    }

    /// <summary>A refusal of a dead copy's engagement / hold (said once per copy).</summary>
    private void Wo195NoteRefused(string npc, string what)
    {
        Interlocked.Increment(ref _w195Refusals);
        if (_w195Refused.TryAdd(npc, 1)) Console.WriteLine($"WO195-DEAD npc={npc} refused={what} -- a dead copy is never engaged, held or wished again (said once per copy)");
    }

    /// <summary>Mirror the native terminal receipt and observe the same body's fall.</summary>
    private void Wo195OnDeathConfirmed(string npc, Wo195BodyScope body, ulong attempt, string via)
    {
        if (!Wo195BodyCurrent(npc, body)) return;
        var mark = new Wo195DeathMark(Environment.TickCount64, body);
        _w195Dead[npc] = mark;

        // Native release and death have already committed on this binding.
        // Clear only managed state belonging to the same concrete body.
        if (_w132Engaged.TryGetValue(npc, out var engagement) && engagement.Eid == body.Eid &&
            _w132Engaged.TryRemove(new KeyValuePair<string, (DateTime At, uint Eid)>(npc, engagement)))
        {
            Interlocked.Increment(ref _w132EngageOff);
            Interlocked.Increment(ref _w195ReleasedEngaged);
        }
        if (_w147Local.TryGetValue(npc, out var local) && local.Eid == body.Eid &&
            _w147Local.TryRemove(new(npc, local)))
            Interlocked.Increment(ref _w147EngageOff);
        _w131Guarded.TryRemove(new KeyValuePair<string, uint>(npc, body.Eid));
        _w183Left.Forget(npc);
        Wo190ForgetTurn(npc);
        _w187OppLast.TryRemove(npc, out _);
        _w187GuardLast.TryRemove(npc, out _);
        _w187Fights.TryRemove(npc, out _);

        Interlocked.Increment(ref _w195Releases);
        Console.WriteLine($"WO195-RELEASE npc={npc} path={Wo195Rules.PathTag(via)} eid=0x{body.Eid:X} attempt={attempt} result=confirmed");
        bool Current() => _w195Dead.TryGetValue(npc, out var current) && ReferenceEquals(mark, current) && Wo195BodyCurrent(npc, body);
        if (_w195DeadRelease) _ = Wo195CheckLaterAsync(npc, body, attempt, mark.At, Current);
    }

    /// <summary>Read the engine's own corpse state on the same confirmed body.</summary>
    private async Task Wo195CheckLaterAsync(string npc, Wo195BodyScope body, ulong attempt, long releasedMs, Func<bool> current)
    {
        string key = $"npc={npc} eid=0x{body.Eid:X} attempt={attempt}";
        try
        {
            await Task.Delay(Wo195Rules.FallCheckMs);
            // A visible death action may still be settling.
            if (W214On("death_ground") && _w214DeathRowPlayedMs.TryGetValue(npc, out long playedMs) &&
                Environment.TickCount64 - playedMs < 2000)
                await Task.Delay(Wo214Rules.DeathReactionGraceMs);
            if (!current()) return;
            var read = await _combat.Wo195ScopedAsync(Protocol.Wo195OpCheck, Wo195BodyRequest(body), current);
            if (!current()) return;
            if (read is not { Ok: true, Payload.Length: >= 4 } observed)
            {
                Console.WriteLine($"WO195-FALL {key} result=unconfirmed");
                return;
            }
            var state = observed.Payload;
            bool down = state[0] == 1 && state[1] == 0 && state[2] == 0 && state[3] == 0;
            Interlocked.Increment(ref down ? ref _w195FallDown : ref _w195FallStanding);
            if (W214On("death_ground"))
            {
                if (state[2] == 1) Interlocked.Increment(ref _w214StillUpright);
                else if (state[2] == 0) Interlocked.Increment(ref _w214DeathDown);
            }
            Console.WriteLine($"WO195-FALL {key} after_ms={Environment.TickCount64 - releasedMs} dead={Wo195Rules.Tri(state[0], "yes", "no")} combat_mode={Wo195Rules.Tri(state[1], "on", "off")} living={Wo195Rules.Tri(state[2], "yes", "no(ragdoll)")} fight_held={Wo195Rules.Tri(state[3], "YES", "no")}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WO195-FALL {key} result=unconfirmed error={ex.GetType().Name}");
            return;
        }

        try
        {
            await Task.Delay(Wo195Rules.PlayerCheckMs - Wo195Rules.FallCheckMs);
            if (!current()) return;
            int others = _w132Engaged.Count + _w147Local.Count;
            await ExecLuaAsync($"if KCD2MP_W195PlayerCheck then KCD2MP_W195PlayerCheck(\"{EscapeLua(npc)}\", {others}) end");
        }
        catch (Exception ex) { Console.WriteLine($"WO195-PLAYER {key} result=unconfirmed error={ex.GetType().Name}"); }
    }

    /// <summary>KCD2MP_W195Check: one read of a copy by the DLL (op 57) -- dead, combat mode, a ragdoll, a fight hold.</summary>
    private async Task Wo195CheckNowAsync(string npc)
    {
        uint eid = _w131Guarded.TryGetValue(npc, out uint ge) ? ge : _npcEntityIds.TryGetValue(npc, out uint pe) ? pe : 0;
        byte[] a;
        if (eid != 0) { a = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(a, eid); }
        else { var nb = Encoding.ASCII.GetBytes(npc); a = new byte[5 + nb.Length]; a[4] = (byte)nb.Length; nb.CopyTo(a, 5); }   // by name: any body
        var r = await _combat.Wo163Async(Protocol.Wo195OpCheck, a);
        Console.WriteLine(r is { Ok: true, Payload.Length: >= 4 } x
            ? $"WO195-CHECK npc={npc} eid=0x{eid:X} dead={(x.Payload[0] == 1 ? "yes" : "no")} combat_mode={Wo195Rules.Tri(x.Payload[1], "on", "off")} living={Wo195Rules.Tri(x.Payload[2], "yes", "no(ragdoll)")} fight_held={(x.Payload[3] == 1 ? "YES" : "no")} marked_dead={(_w195Dead.ContainsKey(npc) ? "yes" : "no")} engaged={(_w132Engaged.ContainsKey(npc) ? "YES" : "no")}"
            : $"WO195-CHECK npc={npc} eid=0x{eid:X} no answer");
    }

    // ---------------------------------------------------------------- 2: stealth

    /// <summary>Joiner: the stealth snapshot every crime report carries (empty with the switch off or none read yet).</summary>
    private string Wo195StealthForReport() => _w195StealthWitness ? _w195Stealth : "";

    /// <summary>Host: WO-141's capture of its NPCs -- an NPC that lies on its bed is asleep for the witness rule (sent on a change only).</summary>
    private void Wo195NoteHostActivity(string npc, ActivityState a)
    {
        if (!_isDamageAuthority || string.IsNullOrEmpty(npc) || !NpcNamePattern.IsMatch(npc)) return;
        bool asleep = Wo195Rules.AsleepByActivity(a);
        bool was = _w195HostAsleep.TryGetValue(npc, out bool w) && w;
        if (asleep == was) return;
        _w195HostAsleep[npc] = asleep;
        Interlocked.Increment(ref _w195AsleepSent);
        _ = ExecLuaAsync($"if KCD2MP_W195Asleep then KCD2MP_W195Asleep(\"{EscapeLua(npc)}\", {(asleep ? 1 : 0)}) end");
    }

    // ---------------------------------------------------------------- 3: the loot screen's key

    private void Wo195NoteScreenTake() => Interlocked.Increment(ref _w195ScreenTakes);
}

public sealed partial class CombatPipe
{
    internal async Task<(bool Ok, byte Reason, byte[] Payload)?> Wo195ScopedAsync(byte op, byte[] args, Func<bool> current)
    {
        var payload = new byte[1 + args.Length];
        payload[0] = op;
        args.CopyTo(payload, 1);
        var (reply, _) = await SendAndAwaitAsync(Wo163, payload, Wo163Reply, default, beforeWrite: current, exactSequence: true);
        return reply is { Length: >= 4 } && reply[2] == op
            ? (reply[0] == 1, reply[3], reply.AsSpan(4).ToArray())
            : null;
    }
}
