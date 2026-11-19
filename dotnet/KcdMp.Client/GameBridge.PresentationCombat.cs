// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using KcdMp.Wire;

namespace KcdMp.Client;

public partial class GameBridge
{
    private const int PresentationCombatRowsPerBody = 192;  // native rp_timeline_actions.cpp
    private const string PresentationMeleeUnavailableCounter = "melee_presentation_unavailable";
    private readonly record struct PresentationCombatScope(RemoteRelocationScope Actor, long Revision);

    private sealed class PresentationCombatRows(PresentationCombatScope scope)
    {
        public readonly PresentationCombatScope Scope = scope;
        public readonly ConcurrentDictionary<(byte Kind, string Fragment, string Tags), PresentationCombatRow> Rows = new();
    }

    private sealed class PresentationCombatRow(uint key)
    {
        public readonly uint Key = key;
        public volatile bool Registered;
    }

    private readonly ConcurrentDictionary<byte, PresentationCombatRows> _presentationCombatRows = new();
    private int _presentationCombatRowKey;

    private bool TryPresentationCombatScope(byte peer, uint eid, PresentationFrame stamp, out PresentationCombatScope scope,
        bool recovery = false)
    {
        scope = default;
        long connection;
        long revision = recovery ? 0 : Interlocked.Read(ref _presentationRevision);
        bool ready = recovery ? PresentationClockScopeCurrent(stamp, out connection) :
            PresentationReady(stamp, out connection) && W221On("rp_combat");
        if (!ready || !NpcNativeBound("kcd2mp_" + peer.ToString(CultureInfo.InvariantCulture)) ||
            !TryRelocationScope(peer, requireLive: true, out var actor) || actor.Entity != eid || actor.Pipe != connection ||
            (!recovery && revision != Interlocked.Read(ref _presentationRevision))) return false;
        scope = new(actor, revision);
        return true;
    }

    private readonly record struct PresentationCombatSubmission(PresentationCombatAdmission Admission, EdgePipeReply? CurrentReceipt = null);

    private async Task<PresentationCombatAdmission> TrySubmitPresentationCombatAsync(byte peer, uint eid, PresentationFrame stamp,
        ushort seq, byte kind, byte[] data, CancellationToken ct, uint sourceMs = 0, Func<bool>? beforeWrite = null,
        bool recovery = false) => (await SubmitPresentationCombatWithEvidenceAsync(peer, eid, stamp, seq, kind, data,
            ct, sourceMs, beforeWrite, recovery)).Admission;

    private async Task<PresentationCombatSubmission> SubmitPresentationCombatWithEvidenceAsync(byte peer, uint eid, PresentationFrame stamp,
        ushort seq, byte kind, byte[] data, CancellationToken ct, uint sourceMs = 0, Func<bool>? beforeWrite = null,
        bool recovery = false)
    {
        if ((recovery && (kind != 0x81 || data is not [0])) || !(beforeWrite?.Invoke() ?? true))
            return new(PresentationCombatAdmission.Suppressed);
        if (!recovery && (!W221On("rp_combat") || stamp.Session is null || stamp.HostAtArrivalMs == 0))
            return new(PresentationCombatAdmission.Fallback);
        if (!TryPresentationCombatScope(peer, eid, stamp, out var scope, recovery))
            return new(recovery ? PresentationCombatAdmission.Fallback : PresentationCombatAdmission.Suppressed);

        bool Current() => TryPresentationCombatScope(peer, eid, stamp, out var current, recovery) &&
            current.Revision == scope.Revision && (kind == 0x81
                ? KnockoutActorScope(current.Actor) == KnockoutActorScope(scope.Actor) : current.Actor == scope.Actor) &&
            (beforeWrite?.Invoke() ?? true);
        Task<EdgePipeReply> SubmitAsync() => _combat.SchedulePresentationCombatAsync(peer, eid, stamp.SampleMs, seq, kind, data,
            scope.Actor.Pipe, Current, ct);
        var reply = await RequestPresentationCombatAsync(SubmitAsync, Current, ct);
        bool current = Current();
        var admission = RecordPresentationCombatAdmission(peer, seq, kind, stamp.SampleMs, sourceMs, reply, current, recovery);
        return new(admission, current ? reply : null);
    }

    private static async Task<EdgePipeReply> RequestPresentationCombatAsync(Func<Task<EdgePipeReply>> request,
        Func<bool> current, CancellationToken ct)
    {
        var reply = await request();
        if (reply.Outcome != EdgeOutcome.Busy || !current()) return reply;
        await Task.Delay(12, ct);
        return current() ? await request() : reply;
    }

    private PresentationCombatAdmission RecordPresentationCombatAdmission(byte peer, ushort seq, byte kind, ulong hostMs,
        uint sourceMs, EdgePipeReply reply, bool current, bool recovery = false, bool melee = false)
    {
        if (!current)
        {
            if (melee)
            {
                string staleStatus = reply is { Outcome: EdgeOutcome.Completed, Payload: [1, 0, 0] } or { Outcome: EdgeOutcome.Ambiguous }
                    ? "ambiguous" : "refused";
                Console.WriteLine(FormattableString.Invariant($"MP-RP-TIMELINE-IN peer={peer} kind={kind} phase=1 seq={seq} source_ms={sourceMs} event_host_ms={hostMs} result={staleStatus} reason=stale-world-or-pipe"));
            }
            return PresentationCombatAdmission.Suppressed;
        }
        var admission = recovery && (reply.Outcome is EdgeOutcome.Busy or EdgeOutcome.Unavailable) ? PresentationCombatAdmission.Fallback :
            melee ? PresentationMeleeAdmission(reply) : CombatPipe.GetPresentationCombatAdmission(reply);
        string? meleeUnavailableReason = melee ? RecordMeleePresentationUnavailable(reply) : null;
        string status = admission switch
        {
            PresentationCombatAdmission.Accepted => "accepted",
            PresentationCombatAdmission.Fallback => "unavailable",
            _ when meleeUnavailableReason is not null => "unavailable",
            _ when reply.Outcome == EdgeOutcome.Ambiguous => "ambiguous",
            _ => "refused"
        };
        string reason = melee && admission != PresentationCombatAdmission.Accepted
            ? $" reason={meleeUnavailableReason ?? PresentationMeleeReceiptReason(reply, registration: false)}" : "";
        Console.WriteLine(FormattableString.Invariant($"MP-RP-TIMELINE-IN peer={peer} kind={kind} phase=1 seq={seq} source_ms={sourceMs} event_host_ms={hostMs} result={status}{reason}"));
        return admission;
    }

    private static bool PresentationMeleeProviderUnavailable(EdgePipeReply reply) =>
        reply is { Outcome: EdgeOutcome.Completed, Payload: [0, 0, 1] };

    private string? RecordMeleePresentationUnavailable(EdgePipeReply reply)
    {
        if (!PresentationMeleeProviderUnavailable(reply)) return null;
        T3Increment(PresentationMeleeUnavailableCounter);
        return "typed-provider-unavailable";
    }

    // An active typed owner cannot hand a row to the legacy queue, which has no equivalent damage guard.
    internal static PresentationCombatAdmission PresentationMeleeAdmission(EdgePipeReply reply) =>
        reply is { Outcome: EdgeOutcome.Completed, Payload: [1, 0, 0] }
            ? PresentationCombatAdmission.Accepted : PresentationCombatAdmission.Suppressed;

    private static string PresentationMeleeReceiptReason(EdgePipeReply reply, bool registration) => reply switch
    {
        { Outcome: EdgeOutcome.Completed, Payload: [0, 0, 2] } => "invalid-native-request-or-scope",
        { Outcome: EdgeOutcome.Completed, Payload: [0, 0, 3] } => "blocked-native",
        { Outcome: EdgeOutcome.Completed, Payload: [0, 0, 4] } => "row-not-registered",
        { Outcome: EdgeOutcome.Unsupported } => "typed-request-unsupported",
        { Outcome: EdgeOutcome.Busy } => "pipe-busy-not-started",
        { Outcome: EdgeOutcome.Unavailable } => "pipe-unavailable-not-started",
        { Outcome: EdgeOutcome.Ambiguous } when registration => "registration-unconfirmed",
        { Outcome: EdgeOutcome.Ambiguous } => "dispatch-unconfirmed",
        _ => "invalid-receipt"
    };

    // Ordinary catalogue rows can omit action_type_id. A perfect block requires the authored type and the wire flag.
    internal static byte PresentationMeleeKind(ActionKind action, string table, byte flags, int actionType = -1) => (action, table) switch
    {
        (ActionKind.Attack, "combat_action_attack") => (byte)ActionKind.Attack,
        (ActionKind.BlockImpulse, "combat_action_block") when (flags & RowEvent.FlagPerfect) == 0 &&
            actionType is -1 or ActionRowCatalog.ActionBlock or ActionRowCatalog.ActionBlockBroken => (byte)ActionKind.BlockImpulse,
        (ActionKind.BlockImpulse, "combat_action_perfect_block")
            when (flags & RowEvent.FlagPerfect) != 0 && actionType == ActionRowCatalog.ActionPerfectBlock => 0x83,
        (ActionKind.Dodge, "combat_action_dodge") => (byte)ActionKind.Dodge,
        (ActionKind.AvatarHit, "combat_action_hit") => 0x84,
        (ActionKind.AvatarRecoil, "combat_action_failed_attack") => 0x85,
        _ => 0
    };

    private async Task<bool> TrySchedulePresentationRowAsync(InboundAction action, uint eid, ActionRowCatalog.Row row,
        byte flags, PresentationFrame stamp, CancellationToken ct)
    {
        if (action.Phase != ActionPhase.Commit) return true;
        byte peer = action.SourceGhostId;
        uint sourceMs = action.Payload.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(action.Payload) : 0;
        byte kind = PresentationMeleeKind(action.Kind, row.Table, flags, row.ActionType);
        if (kind == 0)
        {
            // The verified native owner installs a single body's row. A paired action needs an engine entry for both bodies.
            string disposition = (action.Kind, row.Table) switch
            {
                (ActionKind.Attack or ActionKind.BlockImpulse, "combat_action_sync_attack") => "result=grey reason=paired-engine-entry-unverified",
                (ActionKind.BlockImpulse, "combat_action_perfect_block")
                    when ActionRowCatalog.IsMasterStrike(row.ActionType)
                    => "result=grey reason=counter-engine-entry-unverified",
                (ActionKind.BlockImpulse, "combat_action_perfect_block") when row.ActionType == ActionRowCatalog.ActionPerfectBlock
                    => "result=refused reason=perfect-block-flag-missing",
                (ActionKind.BlockImpulse, "combat_action_block" or "combat_action_perfect_block")
                    => "result=refused reason=defence-row-context-unverified",
                _ => "result=refused reason=kind-table-mismatch"
            };
            ReportRow(disposition);
            return true;
        }

        void ReportRow(string disposition) => Console.WriteLine(FormattableString.Invariant(
            $"MP-RP-MELEE-ROW peer={peer} kind={action.Kind} seq={action.Seq} source_ms={sourceMs} table={row.Table} {disposition}"));
        bool LegacyOwner() => !W221On("rp_combat");
        if (LegacyOwner())
            return await TryScheduleLegacyPresentationRowAsync(action, eid, row.Spec, stamp, ct, LegacyOwner) || !LegacyOwner();
        if (stamp.Session is null || stamp.HostAtArrivalMs == 0 || stamp.SampleMs == 0)
        {
            ReportRow("result=refused reason=missing-presentation-clock");
            return true;
        }
        if (!TryPresentationCombatScope(peer, eid, stamp, out var scope))
        {
            ReportRow("result=refused reason=stale-world-or-pipe-or-body");
            return true;
        }
        _t3Counters.TryAdd(PresentationMeleeUnavailableCounter, 0);
        bool Current() => TryPresentationCombatScope(peer, eid, stamp, out var current) && current == scope;
        var rows = _presentationCombatRows.AddOrUpdate(peer, _ => new(scope),
            (_, old) => old.Scope == scope ? old : new(scope));
        var rowKey = (kind, row.Fragment, row.Tags);
        if (!rows.Rows.TryGetValue(rowKey, out var entry))
        {
            if (rows.Rows.Count >= PresentationCombatRowsPerBody)
            {
                ReportRow("result=refused reason=row-cache-full");
                return true;
            }
            entry = rows.Rows.GetOrAdd(rowKey, _ => new(unchecked((uint)Interlocked.Increment(ref _presentationCombatRowKey))));
        }
        async Task<PresentationCombatAdmission> RegisterAsync()
        {
            string spec = row.Spec;
            Task<EdgePipeReply> RegisterRowAsync() => _combat.RegisterPresentationCombatRowAsync(peer, eid, kind, entry.Key, spec,
                scope.Actor.Pipe, Current, ct);
            var receipt = await RequestPresentationCombatAsync(RegisterRowAsync, Current, ct);
            if (!Current())
            {
                ReportRow("result=refused reason=stale-world-or-pipe stage=registration");
                return PresentationCombatAdmission.Suppressed;
            }
            var status = PresentationMeleeAdmission(receipt);
            entry.Registered = status == PresentationCombatAdmission.Accepted;
            if (status != PresentationCombatAdmission.Accepted)
            {
                if (RecordMeleePresentationUnavailable(receipt) is { } unavailableReason)
                    ReportRow($"result=unavailable reason={unavailableReason} stage=registration");
                else ReportRow($"result=refused reason={PresentationMeleeReceiptReason(receipt, registration: true)} stage=registration");
            }
            return status;
        }

        if (!entry.Registered && await RegisterAsync() != PresentationCombatAdmission.Accepted) return true;

        var data = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, entry.Key);
        Task<EdgePipeReply> SubmitAsync() => _combat.SchedulePresentationCombatAsync(peer, eid, stamp.SampleMs, action.Seq,
            kind, data, scope.Actor.Pipe, Current, ct);
        var reply = await RequestPresentationCombatAsync(SubmitAsync, Current, ct);
        if (CombatPipe.PresentationCombatRowMissing(reply) && Current())
        {
            entry.Registered = false;
            if (await RegisterAsync() != PresentationCombatAdmission.Accepted) return true;
            reply = await RequestPresentationCombatAsync(SubmitAsync, Current, ct);
        }
        RecordPresentationCombatAdmission(peer, action.Seq, kind, stamp.SampleMs, sourceMs, reply, Current(), melee: true);
        return true;
    }
}
