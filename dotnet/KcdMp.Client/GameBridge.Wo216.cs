// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using KcdMp.Wire;

namespace KcdMp.Client;

// WO-216 (docs/WO-216-findings.md): the rest of the body. Every switch ships on (W216Switch; `mp_w216 <name> on|off` in the game's console);
// the bench's twin is KCDMP_TEST_W216_OFF=<names|all> in both agents' and both games' environment.
//   A  route_replay: the host's DLL reads its streamed NPCs' movement requests on its AI update (op 162); this agent sends every change
//      to each joiner as state (NpcRoute 0x84 -> 0x85, kind npc-route); the joiner's agent turns names into its copies' entity ids and
//      hands the lines to its DLL (op 163), where each copy makes its host's request once per change.
// Log tags: MP-W216, MP-W216-NATIVE, MP-W216-STATS; the DLL's WO216-ROUTE, WO216-REPLAY, WO216-CORRECT, WO216-A0, WO216-STATUS.
public partial class GameBridge
{
    private readonly ConcurrentDictionary<string, bool> _w216On = new(StringComparer.Ordinal);
    private bool W216On(string name) => _w216On.TryGetValue(name, out bool v) ? v : !W216Switch.TwinOff(name);
    private bool _w216NativeToldOld;
    private long _w216RoutePolls, _w216RouteBodiesOut, _w216RouteFramesIn, _w216RouteBodiesIn, _w216RouteUnknown, _w216RouteBad, _w216RouteSkipped;

    /// <summary>B: 0 none, 1 the host, 2 a joiner in the host's world and not in a load (the DLL's census runs only then).</summary>
    private byte Wo216Role() => W137Host ? (byte)1 : HostAuthorityOwned ? (byte)2 : (byte)0;
    private byte _w216RolePushed = 0xFF;

    private async Task Wo216PushNativeAsync()
    {
        await _hostAuthorityGate.WaitAsync();
        try
        {
            byte role = Wo216Role();
            if (!await PushHostAuthorityRoleAsync(role) && !_w216NativeToldOld) { _w216NativeToldOld = true; Console.WriteLine("MP-W216 native authority configuration refused -- join requires the current armed DLL"); }
        }
        catch (Exception ex) { Console.WriteLine($"MP-W216 the native word was not sent: {ex.Message}"); }
        finally { _hostAuthorityGate.Release(); }
    }

    private async Task Wo216NativeStatusAsync()
    {
        try
        {
            var r = await _combat.Wo163Async(Protocol.Wo216OpStatus, []);
            if (r is { Ok: true } rr) Console.WriteLine($"MP-W216-NATIVE {Encoding.ASCII.GetString(rr.Payload)}");
        }
        catch { }
    }

    /// <summary>The mod's own switches: told at every connect and every change.</summary>
    private Task Wo216TellModAsync() =>
        ExecLuaAsync($"if KCD2MP_W216Cfg then KCD2MP_W216Cfg(\"{string.Join(',', W216Switch.Names.Select(n => n + "=" + (W216On(n) ? "on" : "off")))}\") end");

    private string Wo216StatsLine() =>
        $"MP-W216-STATS {string.Join(' ', W216Switch.Names.Select(n => $"{n}={(W216On(n) ? "on" : "off")}"))} route_polls={Interlocked.Read(ref _w216RoutePolls)}"
        + $" route_bodies_out={Interlocked.Read(ref _w216RouteBodiesOut)} route_frames_in={Interlocked.Read(ref _w216RouteFramesIn)}"
        + $" route_bodies_in={Interlocked.Read(ref _w216RouteBodiesIn)} route_not_here={Interlocked.Read(ref _w216RouteUnknown)} route_bad={Interlocked.Read(ref _w216RouteBad)} route_skipped={Interlocked.Read(ref _w216RouteSkipped)}"
        + $" line_bodies={Interlocked.Read(ref _w216LineBodies)} line_activity_applied={Interlocked.Read(ref _w216LineActApplied)} line_hands_applied={Interlocked.Read(ref _w216LineHandsApplied)}"
        + $" rows_retired={Interlocked.Read(ref _w216RowsRetired)} hand_rows_retired={Interlocked.Read(ref _w216HandRowsRetired)} exempts={Interlocked.Read(ref _w216Exempts)}"
        + $" outfits_out={Interlocked.Read(ref _w216OutfitsOut)} outfits_in={Interlocked.Read(ref _w216OutfitsIn)} dressed={Interlocked.Read(ref _w216Dressed)}"
        + $" dress_checked={Interlocked.Read(ref _w216DressChecked)} dress_matched={Interlocked.Read(ref _w216DressMatched)} dress_missing={Interlocked.Read(ref _w216DressMissing)} dress_extra={Interlocked.Read(ref _w216DressExtra)} dress_retries={Interlocked.Read(ref _w216DressRetries)}";

    /// <summary>The 4 Hz session tick: the native word every 2 s, its status and the stats every 30 s.</summary>
    private void Wo216Tick(long n)
    {
        if (n % 8 == 5 || Wo216Role() != _w216RolePushed) _ = Wo216PushNativeAsync();   // a load or the join's end goes at once
        if (n % 4 == 3) Wo216AuditOneOutfit();
        if (n % 20 == 3 && W137Joiner && W216On("no_brains")) _ = ExecLuaAsync("if KCD2MP_W216Tick then KCD2MP_W216Tick() end");
        if (n % 120 == 47 && (W137Host || W137Joiner)) { _ = Wo216NativeStatusAsync(); Console.WriteLine(Wo216StatsLine()); }
        if (n % 240 == 53) _ = Wo216TellModAsync();
    }

    private void Wo216OnConnect(CancellationToken ct)
    {
        _ = Wo216TellModAsync();
        _ = Wo216PushNativeAsync();
        _ = Wo216RouteLoopAsync(ct);
        _ = Wo216OutfitLoopAsync(ct);
    }

    // ---------------------------------------------------------------- D: outfits
    private sealed record OutfitTarget(List<byte[]> Worn, uint World, long Life)
    {
        public long NextAuditMs;
    }
    private sealed record HostOutfitRefresh(IReadOnlyList<byte> Peers, uint World, long Life)
    {
        public long NextTryMs;
    }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, HostOutfitRefresh> _w216HostRefresh = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, OutfitTarget> _w216OutfitWanted = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _w216OutfitGates = new(StringComparer.Ordinal);
    private long _w216OutfitsOut, _w216OutfitsIn, _w216Dressed, _w216DressChecked, _w216DressMatched, _w216DressMissing, _w216DressExtra, _w216DressRetries;

    /// <summary>The host, once a second: the bodies whose worn set changed (or new to the stream), one body a frame to every joiner.</summary>
    private async Task Wo216OutfitLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
                if (!W137Host || !W216On("outfits")) continue;
                var peers = Wo134Peers();
                if (peers.Count == 0) continue;
                try
                {
                    var r = await _combat.Wo163Async(Protocol.Wo216OpOutfitRead, [12]);
                    if (r is not { Ok: true } rr || rr.Payload.Length < 1 || rr.Payload[0] == 0) continue;
                    foreach (var (name, worn) in Wo216Rules.ReadOutfits(rr.Payload))
                    {
                        var row = new ExtraRow { Name = name, Worn = worn };
                        foreach (byte j in peers) await Wo143SendAsync(j, Protocol.ExtraKindOutfit, [row]);
                        Interlocked.Increment(ref _w216OutfitsOut);
                    }
                }
                catch (Exception ex) { Console.WriteLine($"MP-W216 outfit tick failed: {ex.GetType().Name}: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>The joiner: the host NPC's worn set -- the copy takes what it lacks and takes off what the host's does not wear.</summary>
    private Task Wo216OnOutfitAsync(ExtraRow r)
    {
        Interlocked.Increment(ref _w216OutfitsIn);
        if (!W137Joiner || !W216On("outfits") || Wo216OutfitDead(r.Name)) return Task.CompletedTask;
        _w216OutfitWanted[r.Name] = new(r.Worn.Select(b => b.ToArray()).ToList(), _w206.World, Interlocked.Read(ref _npcDeathLife));
        _ = Task.Run(() => Wo216RepairOutfitAsync(r.Name));
        return Task.CompletedTask;
    }

    private void Wo216AuditOneOutfit()
    {
        long now = Environment.TickCount64;
        var pending = _w216OutfitWanted.Where(p => p.Value.NextAuditMs <= now).OrderBy(p => p.Value.NextAuditMs).FirstOrDefault();
        if (pending.Key is not null)
        {
            pending.Value.NextAuditMs = now + 5000;
            _ = Task.Run(() => Wo216RepairOutfitAsync(pending.Key));
        }
        var host = _w216HostRefresh.Where(p => p.Value.NextTryMs <= now).OrderBy(p => p.Value.NextTryMs).FirstOrDefault();
        if (host.Key is not null)
        {
            host.Value.NextTryMs = now + 5000;
            _ = Task.Run(() => Wo216TryHostRefreshAsync(host.Key, host.Value));
        }
    }

    // Outfit records are changes, not leases. Keep a quiet target for the next
    // active stream, but never dress a corpse from a record received before loot.
    private bool Wo216OutfitDead(string name) =>
        (_npcLastDead.TryGetValue(name, out bool dead) && dead)
        || _npcDeathAppliedUtc.ContainsKey(name) || _w195Dead.ContainsKey(name) || _w217CorpseWant.ContainsKey(name)
        || (_w147NpcNewest.TryGetValue(name, out var newest) && (newest.Flags & Protocol.NpcStateFlagDead) != 0);

    private bool Wo216OutfitActive(string name) => _where == GameWhere.World && !Wo136Holding
        && _w147NpcNewest.TryGetValue(name, out var newest)
        && W147StampMs(Stopwatch.GetTimestamp()) - newest.AtMs <= Wo214SilenceReleaseMs(name)
        && !_w147SilentSent.ContainsKey(name);

    private async Task Wo216RepairOutfitAsync(string name)
    {
        var gate = _w216OutfitGates.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0)) return;
        try
        {
            while (_w216OutfitWanted.TryGetValue(name, out var target))
            {
                bool Valid() => W137Joiner && W216On("outfits") && _w206.World == target.World
                    && Interlocked.Read(ref _npcDeathLife) == target.Life
                    && _w216OutfitWanted.TryGetValue(name, out var wanted) && ReferenceEquals(wanted, target);
                bool Current() => Valid() && !Wo216OutfitDead(name) && Wo216OutfitActive(name);
                if (!Valid() || Wo216OutfitDead(name))
                {
                    ((ICollection<KeyValuePair<string, OutfitTarget>>)_w216OutfitWanted).Remove(new(name, target));
                    continue;
                }
                if (!Wo216OutfitActive(name)) { target.NextAuditMs = Environment.TickCount64 + 5000; return; }
                var ordered = (await Wo144InLayerOrderAsync(target.Worn.Select(b => new Guid(b)))).Select(g => g.ToByteArray()).ToList();
                var result = await OutfitRepair.ConvergeAsync(ordered, () => Wo216OutfitOfAsync(name), async plan =>
                {
                    if (!Current()) return;
                    Interlocked.Increment(ref _w216Dressed);
                    await ExecLuaAsync($"if KCD2MP_W216Dress then KCD2MP_W216Dress(\"{EscapeLua(name)}\", \"{string.Join(',', plan.FullWorn.Select(ExtraRow.ClassText))}\", \"{string.Join(',', plan.Remove.Select(ExtraRow.ClassText))}\") end");
                }, Current);
                Interlocked.Increment(ref _w216DressChecked);
                if (result.Converged) Interlocked.Increment(ref _w216DressMatched);
                Interlocked.Add(ref _w216DressRetries, Math.Max(0, result.Applies - 1));
                Interlocked.Add(ref _w216DressMissing, result.Missing);
                Interlocked.Add(ref _w216DressExtra, result.Extra);
                Console.WriteLine($"MP-W216 outfit npc={name} full_set={ordered.Count} worn_known={B(result.Known)} classes_match={B(result.Converged)} missing={result.Missing} extra={result.Extra} applies={result.Applies}");
                if (!Valid() || Wo216OutfitDead(name))
                {
                    ((ICollection<KeyValuePair<string, OutfitTarget>>)_w216OutfitWanted).Remove(new(name, target));
                    continue;
                }
                if (!Wo216OutfitActive(name)) { target.NextAuditMs = Environment.TickCount64 + 5000; return; }
                target.NextAuditMs = Environment.TickCount64 + (result.Converged ? 30000 : 5000);
                return; // keep even matched targets: class equality cannot prove that the equipment slots stayed correct
            }
        }
        catch (Exception ex) { Console.WriteLine($"MP-W216 outfit repair paused npc={name}: {ex.GetType().Name}: {ex.Message}"); }
        finally { gate.Release(); }
    }

    private async Task<Guid[]?> Wo216RefreshFullOutfitAsync(string name, IReadOnlyList<byte>? targets = null)
    {
        if (!W137Host) return null;
        var request = new HostOutfitRefresh((targets ?? Wo134Peers()).ToArray(), _w206.World, Interlocked.Read(ref _npcDeathLife));
        _w216HostRefresh[name] = request;
        return await Wo216TryHostRefreshAsync(name, request);
    }

    private async Task<Guid[]?> Wo216TryHostRefreshAsync(string name, HostOutfitRefresh request)
    {
        bool Current() => W137Host && request.World == _w206.World && request.Life == Interlocked.Read(ref _npcDeathLife)
            && _w216HostRefresh.TryGetValue(name, out var latest) && ReferenceEquals(request, latest);
        if (!Current())
        {
            ((ICollection<KeyValuePair<string, HostOutfitRefresh>>)_w216HostRefresh).Remove(new(name, request));
            return null;
        }
        request.NextTryMs = Environment.TickCount64 + 5000;
        var worn = await Wo216OutfitOfAsync(name);
        if (!Current() || worn is null) return null;
        var row = new ExtraRow { Name = name, Worn = worn };
        foreach (byte peer in request.Peers)
        {
            if (!Current()) return null;
            await Wo143SendAsync(peer, Protocol.ExtraKindOutfit, [row]);
        }
        Interlocked.Increment(ref _w216OutfitsOut);
        ((ICollection<KeyValuePair<string, HostOutfitRefresh>>)_w216HostRefresh).Remove(new(name, request));
        return worn.Select(b => new Guid(b)).ToArray();
    }

    private async Task<List<byte[]>?> Wo216OutfitOfAsync(string name)
    {
        var nb = Encoding.ASCII.GetBytes(name);
        var body = new byte[1 + nb.Length];
        body[0] = (byte)nb.Length;
        nb.CopyTo(body, 1);
        var r = await _combat.Wo163Async(Protocol.Wo216OpOutfitOf, body);
        return r is { Ok: true } rr ? OutfitRepair.ReadCopy(rr.Payload) : null;
    }

    private void Wo216OnEvent(string name, string? arg)
    {
        switch (name)
        {
            case "w216_cfg":
            {
                int eq = (arg ?? "").IndexOf('=');
                if (eq <= 0 || arg![(eq + 1)..] is not ("on" or "off") || !W216Switch.Names.Contains(arg[..eq])) { Console.WriteLine($"MP-W216 cfg refused: '{arg}'"); return; }
                _w216On[arg[..eq]] = arg[(eq + 1)..] == "on";
                Console.WriteLine($"MP-W216 cfg {arg}");
                _ = Wo216PushNativeAsync();
                _ = Wo216TellModAsync();
                return;
            }
            case "w216_status":
                _ = Wo216NativeStatusAsync();
                Console.WriteLine(Wo216StatsLine());
                return;
            case "w216_exempt":   // B: "<name> on|off <secs>" -- a feature's own resume here (op 165)
                _ = Wo216ExemptAsync(arg);
                return;
            case "w216_outfitcheck":   // D (the bench): "<name>,<name>..." -- each body's worn set as this machine's soul holds it
                _ = Task.Run(async () =>
                {
                    foreach (var n in (arg ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Take(40))
                    {
                        var w = await Wo216OutfitOfAsync(n);
                        Console.WriteLine($"MP-W216 outfit-check npc={n} found={(w is null ? 0 : 1)} n={w?.Count ?? 0} classes={(w is null ? "" : string.Join(',', w.Select(ExtraRow.ClassText).OrderBy(s => s)))}");
                    }
                });
                return;
        }
    }

    private long _w216Exempts;

    private async Task Wo216ExemptAsync(string? arg)
    {
        if (!Wo216Rules.TryParseExempt(arg, out string name, out bool on, out ushort secs)) { Console.WriteLine($"MP-W216 exempt refused: '{arg}'"); return; }
        var nb = Encoding.ASCII.GetBytes(name);
        var body = new byte[4 + nb.Length];
        body[0] = on ? (byte)1 : (byte)0;
        BitConverter.GetBytes(secs).CopyTo(body, 1);
        body[3] = (byte)nb.Length;
        nb.CopyTo(body, 4);
        Interlocked.Increment(ref _w216Exempts);
        try { await _combat.Wo163Async(Protocol.Wo216OpExempt, body); }
        catch (Exception ex) { Console.WriteLine($"MP-W216 exempt not sent: {ex.Message}"); }
    }

    // ---------------------------------------------------------------- F.1: look targets, both ends (the instrument)
    private long _w216LookHostLines, _w216LookCopyLines;
    private void Wo216NoteHostLooks(IReadOnlyList<ExtraRow> rows)
    {
        foreach (var r in rows)
        {
            long k = Interlocked.Increment(ref _w216LookHostLines);
            if (k <= 2000 || k % 100 == 0)
                Console.WriteLine($"MP-W216 look host npc={r.Name} kind={r.TargetKind} target={(r.Target.Length > 0 ? r.Target : "-")} [n={k}]");
        }
    }
    private void Wo216NoteCopyLook(string copy, string? target)
    {
        long k = Interlocked.Increment(ref _w216LookCopyLines);
        if (k <= 2000 || k % 100 == 0) Console.WriteLine($"MP-W216 look copy npc={copy} target={target ?? "-"} [n={k}]");
    }

    // ---------------------------------------------------------------- C: the one activity line (the joiner)
    // The host's NpcAnim body carries, after its scopes, the body's activity (its NPC-state stance or unstance and their objects) and its
    // hands. With activity_line on, this is the activity's only path here: the activity and the hands are applied from the line, on a
    // change, in the same message as the scopes the owner gets; WO-141's and WO-143's own rows for a body the line covers are kept (the
    // twin's path) but not applied.
    private readonly ConcurrentDictionary<string, long> _w216LinedAtMs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _w216LineAct = new(StringComparer.Ordinal), _w216LineHands = new(StringComparer.Ordinal);
    private long _w216LineBodies, _w216LineActApplied, _w216LineHandsApplied, _w216RowsRetired, _w216HandRowsRetired;

    /// <summary>A body whose host sends the line (within the last 10 s): its WO-141 / WO-143 rows are not applied here.</summary>
    private bool Wo216Lined(string name) =>
        W216On("activity_line") && _w216LinedAtMs.TryGetValue(name, out long at) && Environment.TickCount64 - at < 10_000;

    private async Task Wo216OnLinesAsync(byte[] body)
    {
        if (!W216On("activity_line") || !Wo216Rules.TryReadLines(body, out var lines)) return;
        long now = Environment.TickCount64;
        foreach (var ln in lines)
        {
            _w216LinedAtMs[ln.Name] = now;
            Interlocked.Increment(ref _w216LineBodies);
            if (ln.Activity is { } a)
            {
                string key = Convert.ToHexString(ln.ActivityBytes!);
                if (!_w216LineAct.TryGetValue(ln.Name, out var was) || was != key)
                {
                    _w216LineAct[ln.Name] = key;
                    _w141HostRows[ln.Name] = a;
                    if (!_w141Blocked.ContainsKey(ln.Name) && !W151InFight(ln.Name))
                    {
                        await Wo141ApplyAsync(ln.Name, a, "line");
                        Interlocked.Increment(ref _w216LineActApplied);
                    }
                }
            }
            if (ln.Hands is { } h)
            {
                string key = Convert.ToHexString(h.Left) + Convert.ToHexString(h.Right);
                if (!_w216LineHands.TryGetValue(ln.Name, out var was) || was != key)
                {
                    _w216LineHands[ln.Name] = key;
                    _w143HostHands[ln.Name] = h;
                    if (W141Active && _w143Hands) { await Wo143ApplyHandsAsync(h); Interlocked.Increment(ref _w216LineHandsApplied); }
                    await Wo167OnHostHandsAsync(h);   // WO-167 C2: the weapon in the host NPC's hand
                    await Wo201OnHostHandsAsync(h);   // WO-201 B23: the tool in the host NPC's hand, as a value
                }
            }
        }
    }

    // ---------------------------------------------------------------- A: the host's requests out, the joiner's lines in

    /// <summary>The host, 20 times a second: the changed request lines to every joiner in the world (state: latest wins per body).</summary>
    private async Task Wo216RouteLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(50, ct);
                try { await Wo216RouteTickAsync(); }
                catch (Exception ex) { Console.WriteLine($"MP-W216 route tick failed: {ex.GetType().Name}: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task Wo216RouteTickAsync()
    {
        if (!W137Host || !W216On("route_replay")) return;
        var peers = Wo221RouteReaders(Wo134Peers());   // WO-221 F: only the joiners that read the lines; none -> no read, nothing sent
        if (peers.Count == 0) return;
        var r = await _combat.Wo163Async(Protocol.Wo216OpRouteRead, [(byte)Protocol.NpcRouteMaxBodies]);
        Interlocked.Increment(ref _w216RoutePolls);
        if (r is not { Ok: true } rr || rr.Payload.Length < 2 || rr.Payload[0] == 0) return;
        Interlocked.Add(ref _w216RouteBodiesOut, rr.Payload[0]);
        foreach (byte j in peers)
        {
            try { await WriteJoinAsync(Protocol.BuildJoinUp(Protocol.NpcRouteUp, j, 0, rr.Payload)); }
            catch (Exception ex) { Console.WriteLine($"MP-W216 npc-route not sent to {j}: {ex.Message}"); }
        }
    }

    /// <summary>The joiner: the host's lines (0x85) to this DLL, by this world's copies' entity ids.</summary>
    private async Task Wo216OnNpcRouteAsync(byte[] body)
    {
        if (!W137Joiner) return;
        Interlocked.Increment(ref _w216RouteFramesIn);
        // WO-217 route_skip: with this machine's mp_route off nothing in the DLL reads a line (the replay and the horse replay need it on),
        // and each line handed in is a pipe round trip -- the busy village's 25 a second held the joiner's queue 30-100 s behind
        if (Wo217Rules.SkipRoute(W217On("route_skip"), W215On("route"))) { Interlocked.Increment(ref _w216RouteSkipped); return; }
        if (!Wo216Rules.TryRekeyRoute(body, name => _npcEntityIds.TryGetValue(name, out uint e) ? e : 0u, out byte[] op, out int known, out int unknown))
        {
            Interlocked.Increment(ref _w216RouteBad);
            return;
        }
        Interlocked.Add(ref _w216RouteBodiesIn, known);
        Interlocked.Add(ref _w216RouteUnknown, unknown);
        if (known == 0) return;
        Wo207Trace.Kind.Value = Protocol.NpcRouteDown;
        foreach (var piece in Wo216Rules.RouteInPieces(op)) await _combat.Wo163Async(Protocol.Wo216OpRouteIn, piece);
    }
}

/// <summary>WO-216 C: one body's activity line (the records after its scopes in the host's NpcAnim body).</summary>
public sealed record Wo216Line(string Name, ActivityState? Activity, byte[]? ActivityBytes, ExtraRow? Hands);

/// <summary>WO-216 (docs/WO-216-findings.md): the agent's pure rules. KcdMp.Client.Tests/Wo216Tests.cs checks them.</summary>
public static class Wo216Rules
{
    /// <summary>D: how many times a copy is dressed again when its check still finds the host's set off it (an item made in the same call).</summary>
    public const int DressRetries = 2;

    /// <summary>D: op 166's answer [count]{[nameLen][name][n] n x [class:16]} as (name, worn classes).</summary>
    public static List<(string Name, List<byte[]> Worn)> ReadOutfits(ReadOnlySpan<byte> p)
    {
        var outp = new List<(string, List<byte[]>)>();
        if (p.Length < 1) return outp;
        int count = p[0], o = 1;
        for (int i = 0; i < count; i++)
        {
            if (o >= p.Length) break;
            int nl = p[o];
            if (nl == 0 || o + 1 + nl + 1 > p.Length) break;
            string name = Encoding.ASCII.GetString(p.Slice(o + 1, nl));
            o += 1 + nl;
            int n = p[o++];
            if (n > Protocol.OutfitMaxItems || o + n * 16 > p.Length) break;
            var worn = new List<byte[]>(n);
            for (int k = 0; k < n; k++) { worn.Add(p.Slice(o, 16).ToArray()); o += 16; }
            outp.Add((name, worn));
        }
        return outp;
    }

    /// <summary>D: what the copy must put on (the host wears it, the copy does not) and take off (the copy wears it, the host does not).</summary>
    public static (List<byte[]> Add, List<byte[]> Remove) OutfitDiff(IReadOnlyList<byte[]> host, IReadOnlyList<byte[]> copy)
    {
        static string K(byte[] b) => Convert.ToHexString(b);
        var h = host.Select(K).ToHashSet();
        var c = copy.Select(K).ToHashSet();
        return (host.Where(b => !c.Contains(K(b))).ToList(), copy.Where(b => !h.Contains(K(b))).ToList());
    }

    /// <summary>C: the line records of an NpcAnim body ([count]{[nameLen][name][cm][nrec] nrec x 34}); bodies without one are left out.</summary>
    public static bool TryReadLines(ReadOnlySpan<byte> body, out List<Wo216Line> lines)
    {
        lines = new();
        if (body.Length < 1) return false;
        int count = body[0], o = 1;
        for (int i = 0; i < count; i++)
        {
            if (o + 1 > body.Length) return false;
            int nl = body[o];
            if (nl == 0 || nl > 64 || o + 1 + nl + 2 > body.Length) return false;
            string name = Encoding.ASCII.GetString(body.Slice(o + 1, nl));
            o += 1 + nl;
            int nrec = body[o + 1];
            o += 2;
            if (nrec > Protocol.NpcAnimMaxRecords || o + nrec * Protocol.NpcAnimScopeLen > body.Length) return false;
            ActivityState? act = null; byte[]? actBytes = null; ExtraRow? hands = null;
            for (int k = 0; k < nrec; k++)
            {
                var rec = body.Slice(o + k * Protocol.NpcAnimScopeLen, Protocol.NpcAnimScopeLen);
                if (rec[0] == Protocol.NpcAnimLineActivity && ActivityState.TryRead(rec.Slice(1, Protocol.ActivityBytes), out var a))
                {
                    act = a; actBytes = rec.Slice(1, Protocol.ActivityBytes).ToArray();
                }
                else if (rec[0] == Protocol.NpcAnimLineHands)
                    hands = new ExtraRow { Name = name, Left = rec.Slice(1, 16).ToArray(), Right = rec.Slice(17, 16).ToArray() };
            }
            if (act is not null || hands is not null) lines.Add(new Wo216Line(name, act, actBytes, hands));
            o += nrec * Protocol.NpcAnimScopeLen;
        }
        return o == body.Length;
    }

    /// <summary>B: the mod's "w216_exempt" event, "&lt;name&gt; on|off &lt;secs&gt;" (a name the mod's own feature runs here on purpose).</summary>
    public static bool TryParseExempt(string? arg, out string name, out bool on, out ushort secs)
    {
        name = ""; on = false; secs = 0;
        var f = (arg ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length != 3 || f[0].Length is 0 or > 63 || f[1] is not ("on" or "off")) return false;
        foreach (char c in f[0]) if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')) return false;
        if (!ushort.TryParse(f[2], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out secs) || secs is 0 or > 3600) return false;
        name = f[0]; on = f[1] == "on";
        return true;
    }

    /// <summary>A: op 163's body [count]{[eid:4][rec:52]} in pieces of at most <see cref="Protocol.NpcRouteInMaxBodies"/> bodies -- each one
    /// pipe frame under the DLL's limit (with its op byte).</summary>
    public static IEnumerable<byte[]> RouteInPieces(byte[] op)
    {
        const int per = 4 + Protocol.NpcRouteRecLen;
        int n = op.Length > 0 ? op[0] : 0;
        if (op.Length != 1 + n * per) yield break;
        for (int i = 0; i < n; i += Protocol.NpcRouteInMaxBodies)
        {
            int k = Math.Min(Protocol.NpcRouteInMaxBodies, n - i);
            var b = new byte[1 + k * per];
            b[0] = (byte)k;
            Buffer.BlockCopy(op, 1 + i * per, b, 1, k * per);
            yield return b;
        }
    }

    /// <summary>A: the host's NpcRoute body [count]{[nameLen][name][rec:52]} -> op 163's [count]{[eid:4][rec:52]}, for the bodies this world
    /// knows (an unknown name is left out and counted). False for a malformed body.</summary>
    public static bool TryRekeyRoute(byte[] body, Func<string, uint> eidOf, out byte[] op, out int known, out int unknown)
    {
        op = []; known = 0; unknown = 0;
        if (body.Length < 1) return false;
        int n = body[0], o = 1;
        if (n == 0 || n > Protocol.NpcRouteMaxBodies) return false;
        var outB = new List<byte>(1 + n * (4 + Protocol.NpcRouteRecLen)) { 0 };
        for (int i = 0; i < n; i++)
        {
            if (o >= body.Length) return false;
            int nl = body[o];
            if (nl is 0 or > Protocol.MaxNpcNameLen || o + 1 + nl + Protocol.NpcRouteRecLen > body.Length) return false;
            string name = Encoding.UTF8.GetString(body, o + 1, nl);
            uint eid = eidOf(name);
            if (eid == 0) unknown++;
            else
            {
                Span<byte> e = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(e, eid);
                outB.AddRange(e.ToArray());
                outB.AddRange(body.AsSpan(o + 1 + nl, Protocol.NpcRouteRecLen).ToArray());
                known++;
            }
            o += 1 + nl + Protocol.NpcRouteRecLen;
        }
        if (o != body.Length) return false;
        outB[0] = (byte)known;
        op = [.. outB];
        return true;
    }
}
