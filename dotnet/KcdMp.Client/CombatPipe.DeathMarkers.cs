// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using KcdMp.Wire;

namespace KcdMp.Client;
public sealed partial class CombatPipe
{
    public Func<NativeDeathMarker, Task>? OnDeathMarker { get; set; }
    private async Task<(bool Ok, byte Reason, byte[] Payload)?> P5NativeAsync(byte op, byte[] args, CancellationToken ct)
    {
        byte[] payload = [op, .. args];
        var (body, _) = await SendAndAwaitAsync(Wo163, payload, Wo163Reply, ct);
        return body is { Length: >= 4 } && body[2] == op ? (body[0] == 1, body[3], body.AsSpan(4).ToArray()) : null;
    }
    public async Task<DeathMarkerAck?> DeathMarkerControlAsync(DeathMarkerAction action, DeathMarkerKind kind, byte owner,
        ulong generation, uint world, ulong id = 0, ulong revision = 0, float x = 0, float y = 0, float z = 0, CancellationToken ct = default)
    {
        var args = DeathMarkerCodec.ControlArgs(action, kind, owner, generation, world, id, revision, x, y, z);
        var result = await P5NativeAsync(Protocol.DeathMarkerControlOp, args, ct);
        if (result is not { } r || !DeathMarkerCodec.TryAck(r.Payload, out var ack) || ack.Action != action || ack.Generation != generation ||
            (ack.Safe && (!r.Ok || ack.World != world))) return null;
        return ack;
    }
    public async Task<IReadOnlyList<DeathMarkerFact>?> ListDeathMarkersAsync(ulong generation, uint world, CancellationToken ct = default)
    {
        uint token = 0; ushort cursor = 0; long start = Environment.TickCount64;
        var rows = new List<DeathMarkerFact>(); var ids = new HashSet<ulong>();
        while (Environment.TickCount64 - start < 5000)
        {
            var reply = await P5NativeAsync(Protocol.DeathMarkerListOp, DeathMarkerCodec.PageArgs(generation, world, token, cursor), ct);
            if (Environment.TickCount64 - start >= 5000) return null;
            if (reply is not { Ok: true } r || !DeathMarkerCodec.TryPage(r.Payload, out var page) || page is null || page.Generation != generation || page.World != world ||
                (token != 0 && page.Token != token) || page.Next != cursor + page.Rows.Count || (!page.Done && page.Rows.Count == 0)) return null;
            foreach (var row in page.Rows) if (!ids.Add(row.Id)) return null;
            rows.AddRange(page.Rows); if (rows.Count > DeathMarkerCodec.MaxRows) return null;
            if (page.Done) return rows;
            token = page.Token; cursor = page.Next;
        }
        return null;
    }
}
