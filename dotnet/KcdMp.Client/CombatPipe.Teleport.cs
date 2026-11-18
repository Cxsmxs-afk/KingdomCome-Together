// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using KcdMp.Wire;

namespace KcdMp.Client;

/// <summary>The same local deadline survives every queue. The delegate fences connection/world/life at physical write.</summary>
public sealed record TeleportProof(TeleportSource Source, long DeadlineTick64, ulong Generation, Func<bool> IsCurrent)
{
    public bool Valid => TeleportSnapshotCodec.ValidSource(Source) && Generation != 0 &&
        DeadlineTick64 >= Environment.TickCount64 && DeadlineTick64 - Environment.TickCount64 <= 2000 && IsCurrent();
}

public sealed partial class CombatPipe
{
    public async Task<TeleportSnapshot?> ReadTeleportSnapshotAsync(ulong generation, CancellationToken ct = default)
    {
        if (generation == 0) return null;
        ulong started = checked((ulong)Environment.TickCount64);
        byte[] args = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(args, generation);
        byte[] payload = [Protocol.T3OpPlayerSnapshot, .. args];
        var (body, _) = await SendAndAwaitAsync(Wo163, payload, Wo163Reply, ct, () => true);
        return body is { Length: 36 } && body[0] == 1 && body[2] == Protocol.T3OpPlayerSnapshot &&
            TeleportSnapshotCodec.TryNative(body.AsSpan(4), out var s) && s.Generation == generation &&
            s.CapturedTick64 >= started && s.CapturedTick64 <= (ulong)Environment.TickCount64 ? s : null;
    }

    private async Task<PipeResult> SendPartnerProofAsync(byte[] payload, TeleportProof proof, bool valid, CancellationToken ct)
    {
        if (valid && !proof.Valid) return new PipeResult(false, PipeReason.NotConnected);
        var (body, fail) = await SendAndAwaitAsync(SetPartner, payload, Result, ct, () => !valid || proof.Valid);
        return body is { Length: >= 3 } ? new PipeResult(body[0] == 1, (PipeReason)body[2]) : new PipeResult(false, fail);
    }

    private static void WriteTeleportProof(Span<byte> tail, TeleportProof proof)
    {
        tail[0] = (byte)proof.Source;
        BinaryPrimitives.WriteUInt64LittleEndian(tail[1..], checked((ulong)proof.DeadlineTick64));
        BinaryPrimitives.WriteUInt64LittleEndian(tail[9..], proof.Generation);
    }
}
