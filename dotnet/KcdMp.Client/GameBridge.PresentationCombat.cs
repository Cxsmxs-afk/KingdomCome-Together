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
    private long _presentationCombatUnsupportedConnection = -1;

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

    private async Task<PresentationCombatAdmission> TrySubmitPresentationCombatAsync(byte peer, uint eid, PresentationFrame stamp,
        ushort seq, byte kind, byte[] data, CancellationToken ct, uint sourceMs = 0, Func<bool>? beforeWrite = null,
        bool recovery = false)
    {
        if ((recovery && (kind != 0x81 || data is not [0])) || !(beforeWrite?.Invoke() ?? true))
            return PresentationCombatAdmission.Suppressed;
        if (!recovery && (!W221On("rp_combat") || stamp.Session is null || stamp.HostAtArrivalMs == 0))
            return PresentationCombatAdmission.Fallback;
        if (!TryPresentationCombatScope(peer, eid, stamp, out var scope, recovery))
            return recovery ? PresentationCombatAdmission.Fallback : PresentationCombatAdmission.Suppressed;

        bool Current() => TryPresentationCombatScope(peer, eid, stamp, out var current, recovery) && current == scope &&
            (beforeWrite?.Invoke() ?? true);
        Task<EdgePipeReply> SubmitAsync() => _combat.SchedulePresentationCombatAsync(peer, eid, stamp.SampleMs, seq, kind, data,
            scope.Actor.Pipe, Current, ct);
        var reply = await RequestPresentationCombatAsync(SubmitAsync, Current, ct);
        return RecordPresentationCombatAdmission(peer, seq, kind, stamp.SampleMs, sourceMs, reply, Current(), recovery);
    }

    private static async Task<EdgePipeReply> RequestPresentationCombatAsync(Func<Task<EdgePipeReply>> request,
        Func<bool> current, CancellationToken ct)
    {
        var reply = await request();
        if (reply.Outcome != EdgeOutcome.Busy || !current()) return reply;
        await Task.Delay(12, ct);
        return current() ? await request() : reply;
    }

    private static PresentationCombatAdmission RecordPresentationCombatAdmission(byte peer, ushort seq, byte kind, ulong hostMs,
        uint sourceMs, EdgePipeReply reply, bool current, bool recovery = false)
    {
        if (!current) return PresentationCombatAdmission.Suppressed;
        var admission = recovery && (reply.Outcome is EdgeOutcome.Busy or EdgeOutcome.Unavailable) ? PresentationCombatAdmission.Fallback :
            CombatPipe.GetPresentationCombatAdmission(reply);
        string status = admission switch
        {
            PresentationCombatAdmission.Accepted => "accepted",
            PresentationCombatAdmission.Fallback => "unavailable",
            _ when reply.Outcome == EdgeOutcome.Ambiguous => "ambiguous",
            _ => "refused"
        };
        Console.WriteLine(FormattableString.Invariant($"MP-RP-TIMELINE-IN peer={peer} kind={kind} phase=1 seq={seq} source_ms={sourceMs} event_host_ms={hostMs} result={status}"));
        return admission;
    }

    private async Task<bool> TrySchedulePresentationRowAsync(InboundAction action, uint eid, ActionRowCatalog.Row row,
        byte flags, PresentationFrame stamp, CancellationToken ct)
    {
        async Task<bool> LegacyAsync(Func<bool>? beforeWrite = null)
        {
            if (beforeWrite is not null && !beforeWrite()) return true;
            return row.Table != "combat_action_sync_attack" &&
                await TryScheduleLegacyPresentationRowAsync(action, eid, row.Spec, stamp, ct, beforeWrite);
        }
        if (!W221On("rp_combat")) return await LegacyAsync();
        if (stamp.Session is null || stamp.HostAtArrivalMs == 0) return false;
        if (action.Phase != ActionPhase.Commit || !TryPresentationCombatScope(action.SourceGhostId, eid, stamp, out var scope))
            return true;

        byte kind = (action.Kind, row.Table) switch
        {
            (ActionKind.Attack, "combat_action_attack" or "combat_action_sync_attack") => (byte)ActionKind.Attack,
            (ActionKind.BlockImpulse, "combat_action_perfect_block") when (flags & Wo187Rules.NativeFlagPerfect) != 0 => 0x83,
            (ActionKind.BlockImpulse, "combat_action_block") => (byte)ActionKind.BlockImpulse,
            (ActionKind.Dodge, "combat_action_dodge") => (byte)ActionKind.Dodge,
            (ActionKind.AvatarHit, "combat_action_hit") => 0x84,
            (ActionKind.AvatarRecoil, "combat_action_failed_attack") => 0x85,
            _ => 0
        };
        if (kind == 0) return true;

        byte peer = action.SourceGhostId;
        bool Current() => TryPresentationCombatScope(peer, eid, stamp, out var current) && current == scope;
        if (Interlocked.Read(ref _presentationCombatUnsupportedConnection) == scope.Actor.Pipe) return await LegacyAsync(Current);
        var rows = _presentationCombatRows.AddOrUpdate(peer, _ => new(scope),
            (_, old) => old.Scope == scope ? old : new(scope));
        var rowKey = (kind, row.Fragment, row.Tags);
        if (!rows.Rows.TryGetValue(rowKey, out var entry))
        {
            if (rows.Rows.Count >= PresentationCombatRowsPerBody) return true;
            entry = rows.Rows.GetOrAdd(rowKey, _ => new(unchecked((uint)Interlocked.Increment(ref _presentationCombatRowKey))));
        }
        async Task<PresentationCombatAdmission> RegisterAsync()
        {
            string spec = row.Spec;
            Task<EdgePipeReply> RegisterRowAsync() => _combat.RegisterPresentationCombatRowAsync(peer, eid, kind, entry.Key, spec,
                scope.Actor.Pipe, Current, ct);
            var receipt = await RequestPresentationCombatAsync(RegisterRowAsync, Current, ct);
            if (!Current()) return PresentationCombatAdmission.Suppressed;
            if (receipt.Outcome == EdgeOutcome.Unsupported)
                Interlocked.Exchange(ref _presentationCombatUnsupportedConnection, scope.Actor.Pipe);
            var status = CombatPipe.GetPresentationCombatAdmission(receipt);
            entry.Registered = status == PresentationCombatAdmission.Accepted;
            return status;
        }

        if (!entry.Registered)
        {
            var registration = await RegisterAsync();
            if (registration != PresentationCombatAdmission.Accepted)
            {
                Console.WriteLine($"MP-RP-COMBAT-REGISTER peer={peer} kind={kind} result={registration.ToString().ToLowerInvariant()}");
                return registration == PresentationCombatAdmission.Suppressed || await LegacyAsync(Current);
            }
        }

        var data = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, entry.Key);
        Task<EdgePipeReply> SubmitAsync() => _combat.SchedulePresentationCombatAsync(peer, eid, stamp.SampleMs, action.Seq,
            kind, data, scope.Actor.Pipe, Current, ct);
        var reply = await RequestPresentationCombatAsync(SubmitAsync, Current, ct);
        if (CombatPipe.PresentationCombatRowMissing(reply) && Current())
        {
            entry.Registered = false;
            if (await RegisterAsync() == PresentationCombatAdmission.Accepted)
                reply = await RequestPresentationCombatAsync(SubmitAsync, Current, ct);
        }
        uint sourceMs = action.Payload.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(action.Payload) : 0;
        var admission = RecordPresentationCombatAdmission(peer, action.Seq, kind, stamp.SampleMs, sourceMs, reply, Current());
        if (admission != PresentationCombatAdmission.Fallback) return true;
        if (reply.Outcome == EdgeOutcome.Unsupported)
            Interlocked.Exchange(ref _presentationCombatUnsupportedConnection, scope.Actor.Pipe);
        return await LegacyAsync(Current);
    }
}
