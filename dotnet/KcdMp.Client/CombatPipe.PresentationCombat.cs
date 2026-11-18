// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Text;

namespace KcdMp.Client;

public enum PresentationCombatAdmission
{
    Accepted,
    Suppressed,
    Fallback
}

public sealed partial class CombatPipe
{
    private const byte RemoteCombatRow = 0x3F, RemoteCombatAction = 0x40;

    public Task<EdgePipeReply> RegisterPresentationCombatRowAsync(byte peer, uint eid, byte kind, uint rowKey,
        string spec, long connection, Func<bool> beforeWrite, CancellationToken ct)
    {
        int length = Encoding.UTF8.GetByteCount(spec);
        if (rowKey == 0 || length is < 1 or > 191) return Task.FromResult(EmptyEdge(EdgeOutcome.Invalid));
        var body = new byte[10 + length];
        body[0] = peer;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(1), eid);
        body[5] = kind;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(6), rowKey);
        Encoding.UTF8.GetBytes(spec, body.AsSpan(10));
        return EdgeRequestAsync(RemoteCombatRow, body, Result, EdgeLane.Bulk, ct, connection, beforeWrite);
    }

    public Task<EdgePipeReply> SchedulePresentationCombatAsync(byte peer, uint eid, ulong hostMs, ushort seq, byte kind,
        byte[] data, long connection, Func<bool> beforeWrite, CancellationToken ct)
    {
        var body = new byte[17 + data.Length];
        body[0] = peer;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(1), eid);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(5), hostMs);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(13), seq);
        body[15] = kind;
        body[16] = 1;   // committed action
        data.CopyTo(body, 17);
        return EdgeRequestAsync(RemoteCombatAction, body, Result, EdgeLane.Realtime, ct, connection, beforeWrite);
    }

    public static PresentationCombatAdmission GetPresentationCombatAdmission(EdgePipeReply reply) => reply switch
    {
        { Outcome: EdgeOutcome.Completed, Payload: [1, 0, 0] } => PresentationCombatAdmission.Accepted,
        { Outcome: EdgeOutcome.Unsupported } or
            { Outcome: EdgeOutcome.Completed, Payload: [0, 0, 1] } => PresentationCombatAdmission.Fallback,
        _ => PresentationCombatAdmission.Suppressed
    };

    public static bool PresentationCombatRowMissing(EdgePipeReply reply) =>
        reply is { Outcome: EdgeOutcome.Completed, Payload: [0, 0, 4] };
}
