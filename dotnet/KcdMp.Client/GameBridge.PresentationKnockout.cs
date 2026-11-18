// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Collections.Concurrent;
using System.Globalization;

namespace KcdMp.Client;

public partial class GameBridge
{
    private sealed record PresentationKnockout(PresentationSession Session, ulong Epoch, uint Entity, int BodyGeneration,
        ushort WakeSequence = 0, PresentationFrame WakeStamp = default);

    private readonly ConcurrentDictionary<byte, SemaphoreSlim> _presentationKnockoutGates = new();
    private readonly ConcurrentDictionary<byte, PresentationKnockout> _presentationKnockouts = new();
    private int _presentationKnockoutSequence;

    private bool PresentationWakePending(byte peer) =>
        _presentationKnockouts.TryGetValue(peer, out var knockout) && knockout.WakeSequence != 0;

    private ushort NextPresentationKnockoutSequence()
    {
        ushort sequence;
        do { sequence = unchecked((ushort)Interlocked.Increment(ref _presentationKnockoutSequence)); }
        while (sequence == 0);
        return sequence;
    }

    private async Task PresentKnockoutAsync(byte peer, bool down, PresentationFrame stamp, bool retryWake = false)
    {
        var ct = _wo121Ct;
        var name = peer.ToString(CultureInfo.InvariantCulture);
        int bodyGeneration = GhostGeneration(name);
        _ghostEntityIds.TryGetValue(name, out uint entity);
        var gate = _presentationKnockoutGates.GetOrAdd(peer, static _ => new SemaphoreSlim(1, 1));
        try
        {
            await gate.WaitAsync(ct);
            try
            {
                if (GhostGeneration(name) != bodyGeneration) return;
                bool Current() => !ct.IsCancellationRequested && GhostGeneration(name) == bodyGeneration &&
                    _ghostEntityIds.TryGetValue(name, out uint current) && current == entity;
                _presentationKnockouts.TryGetValue(peer, out var previous);
                if (previous is not null && (previous.Entity != entity || previous.BodyGeneration != bodyGeneration ||
                    !ReferenceEquals(previous.Session, Volatile.Read(ref _presentationSession)) || !previous.Session.IsEpoch(previous.Epoch)))
                {
                    _presentationKnockouts.TryRemove(peer, out _);
                    previous = null;
                }

                if (!down && previous is not null)
                {
                    ushort sequence = previous.WakeSequence == 0 ? NextPresentationKnockoutSequence() : previous.WakeSequence;
                    var remembered = previous.WakeStamp.SampleMs != 0 ? previous.WakeStamp : stamp;
                    _presentationKnockouts[peer] = previous with { WakeSequence = sequence, WakeStamp = remembered };
                    if (stamp.HostAtArrivalMs == 0 || stamp.Epoch != previous.Epoch || !ReferenceEquals(stamp.Session, previous.Session)) return;
                    var wake = remembered with { HostAtArrivalMs = stamp.HostAtArrivalMs };
                    var result = await TrySubmitPresentationCombatAsync(peer, entity, wake, sequence, 0x81, [0], ct,
                        beforeWrite: Current, recovery: true);
                    if (result != PresentationCombatAdmission.Fallback) _presentationKnockouts.TryRemove(peer, out _);
                    Console.WriteLine(FormattableString.Invariant($"MP-RP-KO-IN peer={peer} eid=0x{entity:X} down=0 seq={sequence} event_host_ms={wake.SampleMs} result={result.ToString().ToLowerInvariant()}"));
                    return;
                }
                if (retryWake) return;

                if (down && entity != 0 && stamp.Session is { } session && stamp.HostAtArrivalMs != 0)
                {
                    ushort sequence = NextPresentationKnockoutSequence();
                    var result = await TrySubmitPresentationCombatAsync(peer, entity, stamp, sequence, 0x81, [1], ct, beforeWrite: Current);
                    if (result != PresentationCombatAdmission.Fallback)
                    {
                        if (Current()) _presentationKnockouts[peer] = new PresentationKnockout(session, stamp.Epoch, entity, bodyGeneration);
                        Console.WriteLine(FormattableString.Invariant($"MP-RP-KO-IN peer={peer} eid=0x{entity:X} down=1 seq={sequence} event_host_ms={stamp.SampleMs} result={result.ToString().ToLowerInvariant()}"));
                        return;
                    }
                }
                await Wo201FigureKnockoutAsync(peer, down);
            }
            finally { gate.Release(); }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
    }
}
