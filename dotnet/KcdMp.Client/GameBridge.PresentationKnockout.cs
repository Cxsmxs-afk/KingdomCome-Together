// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Collections.Concurrent;
using System.Globalization;
using KcdMp.Wire;

namespace KcdMp.Client;

public partial class GameBridge
{
    private sealed record PresentationKnockout(PresentationSession Session, ulong Epoch, uint Entity, int BodyGeneration,
        RemoteRelocationScope? Actor, bool MayHaveDispatched, ushort WakeSequence = 0, PresentationFrame WakeStamp = default);

    private readonly ConcurrentDictionary<byte, SemaphoreSlim> _presentationKnockoutGates = new();
    private readonly ConcurrentDictionary<byte, PresentationKnockout> _presentationKnockouts = new();
    private int _presentationKnockoutSequence;

    private bool PresentationWakePending(byte peer) =>
        _presentationKnockouts.TryGetValue(peer, out var knockout) && knockout.WakeSequence != 0;

    // A different peer can start a join without changing this body's lifetime.
    private static RemoteRelocationScope KnockoutActorScope(RemoteRelocationScope scope) => scope with { Join = 0 };

    private bool PresentationDeathOwnsBody(byte peer) => _w155Collapsed.ContainsKey(peer) ||
        (_w132PeerFlags.TryGetValue(peer, out byte flags) &&
        Wo155Rules.BodyOf((flags & Protocol.PlayerStateFlagUnconscious) != 0,
            (flags & Protocol.PlayerStateFlagKnockedDown) != 0) == Wo155Rules.Body.Death);

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
                void ReportSuppressed(string reason) => Console.WriteLine(FormattableString.Invariant(
                    $"MP-RP-KO-IN peer={peer} eid=0x{entity:X} down={(down ? 1 : 0)} result=suppressed reason={reason}"));
                if (GhostGeneration(name) != bodyGeneration)
                {
                    ReportSuppressed("actor-retired");
                    return;
                }
                if (PresentationDeathOwnsBody(peer))
                {
                    _presentationKnockouts.TryRemove(peer, out _);
                    ReportSuppressed("death-owner");
                    return;
                }

                RemoteRelocationScope? ActorScope() => TryRelocationScope(peer, requireLive: true, out var scope)
                    ? KnockoutActorScope(scope) : null;
                var actor = ActorScope();
                bool Current() => !ct.IsCancellationRequested && GhostGeneration(name) == bodyGeneration &&
                    _ghostEntityIds.TryGetValue(name, out uint current) && current == entity && ActorScope() == actor &&
                    !PresentationDeathOwnsBody(peer);
                _presentationKnockouts.TryGetValue(peer, out var previous);
                if (previous is not null && (previous.Entity != entity || previous.BodyGeneration != bodyGeneration ||
                    previous.Actor != actor || !ReferenceEquals(previous.Session, Volatile.Read(ref _presentationSession)) ||
                    !previous.Session.IsEpoch(previous.Epoch)))
                {
                    _presentationKnockouts.TryRemove(peer, out _);
                    if (!down)
                    {
                        ReportSuppressed("actor-retired");
                        return;
                    }
                    previous = null;
                }

                if (!down && previous is not null)
                {
                    if (!previous.MayHaveDispatched)
                    {
                        _presentationKnockouts.TryRemove(peer, out _);
                        ReportSuppressed("down-not-admitted");
                        return;
                    }
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
                    var submission = await SubmitPresentationCombatWithEvidenceAsync(peer, entity, stamp, sequence, 0x81, [1], ct,
                        beforeWrite: Current);
                    bool nativeUnavailable = submission.CurrentReceipt is { Outcome: EdgeOutcome.Completed, Payload: [0, 0, 1] };
                    if (submission.Admission != PresentationCombatAdmission.Fallback || nativeUnavailable)
                    {
                        // Native Unavailable can be a failed life/body gate. Only an explicit old-DLL or disabled-mode fallback
                        // may reach legacy guard changes. Ambiguous writes keep the typed wake path for this exact actor.
                        var admission = nativeUnavailable ? PresentationCombatAdmission.Suppressed : submission.Admission;
                        bool mayHaveDispatched = admission == PresentationCombatAdmission.Accepted ||
                            submission.CurrentReceipt is { Outcome: EdgeOutcome.Ambiguous };
                        if (Current()) _presentationKnockouts[peer] = new PresentationKnockout(session, stamp.Epoch, entity,
                            bodyGeneration, actor, mayHaveDispatched);
                        string reason = submission.CurrentReceipt switch
                        {
                            { Outcome: EdgeOutcome.Completed, Payload: [0, 0, 1] } => " reason=native-unavailable",
                            { Outcome: EdgeOutcome.Completed, Payload: [0, 0, 2] } => " reason=native-invalid",
                            { Outcome: EdgeOutcome.Completed, Payload: [0, 0, 3] } => " reason=native-blocked",
                            { Outcome: EdgeOutcome.Ambiguous } => " reason=dispatch-unconfirmed",
                            null => " reason=scope-not-current",
                            _ => ""
                        };
                        Console.WriteLine(FormattableString.Invariant($"MP-RP-KO-IN peer={peer} eid=0x{entity:X} down=1 seq={sequence} event_host_ms={stamp.SampleMs} result={admission.ToString().ToLowerInvariant()}{reason}"));
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
