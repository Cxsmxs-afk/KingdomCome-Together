// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Globalization;

namespace KcdMp.Client;

public partial class GameBridge
{
    private sealed class OutfitScope(CancellationToken token, CancellationToken peer)
    {
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(token, peer);
        private Task? _cancel;
        private bool _finished;
        public CancellationToken Token => _stop.Token;

        public void Retire()
        {
            lock (_stop)
                if (!_finished) _cancel ??= _stop.CancelAsync();
        }

        public async Task FinishAsync()
        {
            Task? cancelled;
            lock (_stop)
            {
                _finished = true;
                cancelled = _cancel;
            }
            try { if (cancelled is not null) await cancelled; }
            finally { _stop.Dispose(); }
        }
    }

    private readonly ConcurrentDictionary<byte, OutfitScope> _outfitScopes = new();
    private readonly ConcurrentDictionary<byte, byte> _outfitDeferred = new();
    private readonly ConcurrentDictionary<byte, long> _outfitHandWrites = new();
    private readonly ConcurrentDictionary<byte, long> _outfitAttempts = new();
    private long _outfitOrder;

    private readonly record struct OutfitEngineScope(byte Peer, string Incarnation, string Hand, long Order);

    private string? Wo214OutfitIncarnation(byte peer) =>
        Wo214FigureBody(peer) is { Entity: > 0, LuaBirth: > 0 } body
            ? body.LuaBirth.ToString("X8", CultureInfo.InvariantCulture) : null;

    private static string Wo214OutfitHandScope(FigureHand hand) => string.Create(CultureInfo.InvariantCulture,
        $"{hand.Context.World:X8}{hand.Context.Serial:X16}{hand.PeerSerial:X16}{hand.Body.Generation:X8}{hand.Revision:X16}");

    private FigureHand? OutfitHand(byte ghostId) => W214On("figure_hands") &&
        _w214FigureHand.TryGetValue(ghostId, out var hand) && Wo214HandCurrent(ghostId, hand) ? hand : null;

    private void RetireOutfitScope(byte ghostId)
    {
        if (_outfitScopes.TryGetValue(ghostId, out var pass)) pass.Retire();
    }

    private async Task ApplyOutfitHandEdgeAsync(byte ghostId, FigureHand hand, Func<Task> dispatch, CancellationToken ct)
    {
        bool running = _outfitScopes.ContainsKey(ghostId);
        _outfitHandWrites[ghostId] = hand.Revision;
        RetireOutfitScope(ghostId);
        try
        {
            ct.ThrowIfCancellationRequested();
            await dispatch();
        }
        finally { _outfitHandWrites.TryRemove(KeyValuePair.Create(ghostId, hand.Revision)); }
        if (running || _outfitDeferred.ContainsKey(ghostId))
            _ = ResumeOutfitAfterHandAsync(ghostId, hand);
    }

    private async Task ResumeOutfitAfterHandAsync(byte ghostId, FigureHand hand)
    {
        // A holster goes first. An older hand edge never refreshes a newer target.
        if (!Wo214SameHandState(OutfitHand(ghostId), hand)) return;
        if (_ghostWantedAppearance.TryGetValue(ghostId, out var target) &&
            _ghostWantedAppearance.TryUpdate(ghostId, (Guid[])target.Clone(), target))
        {
            try { await RunWantedAppearanceAsync(ghostId, hand.Context.Cancellation); }
            catch (OperationCanceledException) when (hand.Context.Stop.IsCancellationRequested) { }
            catch (Exception ex) { Console.WriteLine($"[appearance] ghost {ghostId}: hand change could not refresh its outfit: {ex.Message}"); }
        }
    }

    private void Wo214OutfitHandConfirmed(byte ghostId, FigureHand hand)
    {
        if (!hand.Drawn && _outfitDeferred.ContainsKey(ghostId))
            _ = ResumeOutfitAfterHandAsync(ghostId, hand);
    }

    private void Wo214OutfitAttempted(string arg)
    {
        var fields = arg.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 3 || !byte.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out byte peer) ||
            Wo214OutfitIncarnation(peer) != fields[1] ||
            !long.TryParse(fields[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long order)) return;
        _outfitAttempts.AddOrUpdate(peer, order, (_, current) => Math.Max(current, order));
    }

    private bool OutfitAttemptedInEngine(OutfitEngineScope scope) => _outfitAttempts.GetValueOrDefault(scope.Peer) == scope.Order;

    private Task OutfitMutationAsync(OutfitEngineScope scope, string kind, string classes, CancellationToken ct) =>
        _transport.ExecuteNowAsync(string.Create(CultureInfo.InvariantCulture,
            $"if KCD2MP_W214OutfitApply then KCD2MP_W214OutfitApply(\"{scope.Peer}\",\"{scope.Incarnation}\",\"{scope.Hand}\",\"{scope.Order:X16}\",\"{kind}\",\"{classes}\") end"), ct);
}
