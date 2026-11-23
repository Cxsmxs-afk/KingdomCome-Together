// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Security.Cryptography;
using System.Text;
using KcdMp.Wire;

namespace KcdMp.Client;

public partial class GameBridge
{
    private sealed record HostRestartOffer(HostRun Paused, byte Source, HostRestartWorld Next, bool Available);
    private HostRestartOffer? _hostRestartOffer;
    private byte[]? _hostRestartTicket;
    private Lazy<Task<byte[]?>>? _hostRestartTicketFromDisk;
    private readonly object _hostRestartTicketStoreGate = new();
    private Stream? _hostRestartTicketAskedOn;
    private long _hostRestartRequestedAt;

    private bool HostRestartAwaitingLeave => Volatile.Read(ref _absentHostRun) is { } paused &&
        ReferenceEquals(Volatile.Read(ref _hostRestartOffer)?.Paused, paused);

    private bool HostRestartCanRequest() => config.IsHosting && _sharedWorld && _combatRoleApplied && !_isDamageAuthority &&
        !_joinedWorld && !_leaveInProgress && !_gameQuitting && _where == GameWhere.World &&
        _worldLoad.Current is { Phase: WorldLoadPhase.Play, Generation: > 0, LinkHeld: false };

    private async Task HostRestartRequestTicketAsync()
    {
        if (_wo122Stream is not { } link || ReferenceEquals(Interlocked.Exchange(ref _hostRestartTicketAskedOn, link), link)) return;
        byte[] request = new LootMsg(Protocol.W203HostRestartTicket, 0, "v1").BuildUp(Protocol.W203Up, Protocol.JoinTargetHost);
        try { await WritePacketAsync(link, request, _wo122Ct); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        { Console.WriteLine($"MP-HOST-ABSENT restart-ticket=not-requested reason={ex.GetType().Name}"); }
    }

    private async Task HostRestartRequestAsync()
    {
        if (!HostRestartCanRequest() || _wo122Stream is not { } link) return;
        long now = Environment.TickCount64;
        long previous = Interlocked.Read(ref _hostRestartRequestedAt);
        if (now - previous < Wo206Rules.AnnounceEveryMs || Interlocked.CompareExchange(ref _hostRestartRequestedAt, now, previous) != previous)
            return;

        var file = Volatile.Read(ref _hostRestartTicketFromDisk);
        if (file is null)
        {
            file = new(() => ReadHostRestartTicketAsync(HostRestartTicketPath()));
            file = Interlocked.CompareExchange(ref _hostRestartTicketFromDisk, file, null) ?? file;
        }
        byte[]? ticket = Volatile.Read(ref _hostRestartTicket) ?? await file.Value;
        if (ticket is null || !HostRestartCanRequest() || !ReferenceEquals(link, _wo122Stream)) return;
        _w206.EnsureHost();
        uint world = _w206.World;
        LoadSequenceObserveHostWorld(world);
        var loaded = _worldLoad.Current;
        if (loaded.HostWorld != world || !_worldLoad.CanApply(loaded.Generation)) return;
        byte[] proof = new LootMsg(Protocol.W203HostRestartProof, 0,
            HostRestartWire.Proof(new(world, _worldReconnectInstance), ticket)).BuildUp(Protocol.W203Up, Protocol.JoinTargetHost);
        try { await WritePacketAsync(link, proof, _wo122Ct); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        { Console.WriteLine($"MP-HOST-ABSENT restart-proof=not-sent reason={ex.GetType().Name}"); }
    }

    private bool HostRestartTakeAtRead(int type, byte[] payload)
    {
        if (type != Protocol.W203Down || payload.Length <= 1 + Protocol.JoinHeaderLen) return false;
        byte kind = payload[1 + Protocol.JoinHeaderLen];
        if (kind is not (Protocol.W203HostRestarted or Protocol.W203HostRestartTicket or Protocol.W203HostRestartProof)) return false;
        if (!Protocol.TrySplitJoinDown(payload, out byte source, out byte receiver, out _, out var body) ||
            receiver != _myGhostId || !LootMsg.TryDecode(body, out var message)) return true;
        if (kind == Protocol.W203HostRestartTicket && source == Protocol.JoinTargetHost && config.IsHosting &&
            HostRestartWire.TryTicket(message.Text, out var ticket))
        {
            if (Volatile.Read(ref _hostRestartTicket) is not { } prior || !prior.AsSpan().SequenceEqual(ticket))
            {
                Volatile.Write(ref _hostRestartTicket, ticket);
                _ = Task.Run(() => PersistHostRestartTicket(HostRestartTicketPath(), ticket));
            }
        }
        if (kind == Protocol.W203HostRestarted && source != _myGhostId &&
            HostRestartWire.TryNotice(message.Text, out var previous, out var next)) ShowHostRestartOffer(source, previous, next);
        return true;
    }

    private void ShowHostRestartOffer(byte source, HostRestartWorld previous, HostRestartWorld next)
    {
        const string message = "Your host is back. Choose Leave in the co-op menu, then Join / Rejoin your host.";
        HostRestartOffer offer;
        lock (_joinGate)
        {
            if (!_joinedWorld || _leaveInProgress || Volatile.Read(ref _absentHostRun) is not { } paused) return;
            var prior = Volatile.Read(ref _hostRestartOffer);
            bool followsPause = previous.Epoch == paused.Epoch && (previous.Run == paused.Run || paused.Run == Guid.Empty);
            bool followsOffer = ReferenceEquals(prior?.Paused, paused) && prior!.Next == previous;
            if (!followsPause && !followsOffer) return;
            offer = new(paused, source, next, true);
            Volatile.Write(ref _hostRestartOffer, offer);
            _rewinding = false;
            _rejoinPending = false;
            _newWorldLeavePending = false;
            SetJoinUi("host-restarted", message, "host-restarted");
        }
        Console.WriteLine($"MP-HOST-ABSENT restart-offer source={source} old_epoch={previous.Epoch} old_run={previous.Run:N} epoch={next.Epoch} run={next.Run:N}");
        _ = Task.Run(async () =>
        {
            if (ReferenceEquals(Volatile.Read(ref _hostRestartOffer), offer) && HostRestartAwaitingLeave)
                await ExecLuaAsync($"if KCD2MP_Wo124Msg then KCD2MP_Wo124Msg(\"{EscapeLua(message)}\") end");
        });
    }

    private void HostRestartObserveDisconnect(byte source)
    {
        lock (_joinGate)
        {
            if (!HostRestartAwaitingLeave || _leaveInProgress || Volatile.Read(ref _hostRestartOffer) is not { Available: true } offer ||
                offer.Source != source) return;
            Volatile.Write(ref _hostRestartOffer, offer with { Available = false });
            SetJoinUi("host-absent", "Your host left. This world is paused until your host returns, or you leave.", "host-left");
        }
    }

    private string HostRestartTicketPath()
    {
        string root = Environment.GetEnvironmentVariable("KCDMP_DATA_DIR") is { Length: > 0 } configured ? configured :
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KCDMP");
        string scope = config.SteamCode is { Length: > 0 } steam ? "steam:" + steam : $"tcp:{config.ServerHost}:{config.ServerPort}";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope + "\0" + config.PlayerName)));
        return Path.Combine(root, "host-restart", key + ".ticket");
    }

    private static async Task<byte[]?> ReadHostRestartTicketAsync(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, useAsync: true,
                bufferSize: HostRestartWire.TicketBytes);
            if (file.Length != HostRestartWire.TicketBytes) return null;
            var ticket = new byte[HostRestartWire.TicketBytes];
            await file.ReadExactlyAsync(ticket);
            return ticket;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private void PersistHostRestartTicket(string path, byte[] ticket)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temp, options)) { file.Write(ticket); file.Flush(flushToDisk: true); }
            lock (_hostRestartTicketStoreGate)
                if (ReferenceEquals(Volatile.Read(ref _hostRestartTicket), ticket)) File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Console.WriteLine($"MP-HOST-ABSENT restart-ticket=persist-failed reason={ex.GetType().Name}"); }
        finally { try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }
}
