// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using KcdMp.Wire;
namespace KcdMp.Client;

public sealed partial class CombatPipe
{
    public Func<Guid, float, float, MeasuredMissileEvidence, Task>? OnMeasuredMissile { get; set; }

    public async Task<RangedPollReply?> RangedPollAsync(bool on, ulong generation, Func<bool> current, CancellationToken ct = default)
    {
        var (body, _) = await SendAndAwaitAsync(Wo163, [Protocol.Wo201OpRangedPoll, .. RangedCaptureCodec.Request(on, generation)], Wo163Reply, ct, current);
        ct.ThrowIfCancellationRequested(); ProcessorFrameScope.ThrowIfAbandoned();
        return current() && body is { Length: >= 4 } && body[0] == 1 && body[2] == Protocol.Wo201OpRangedPoll &&
            RangedCaptureCodec.TryDecode(body.AsSpan(4), out var reply) && reply.Generation == generation ? reply : null;
    }
    public async Task<int?> RangedOneShotAsync(string name, string fragment, string tags, Func<bool> current, CancellationToken ct = default)
    {
        var (body, _) = await SendAndAwaitAsync(Wo143, [4, .. Wo143Codec.OneShot(name, fragment, tags, 0, 0)], Wo143Reply, ct, current);
        ct.ThrowIfCancellationRequested(); ProcessorFrameScope.ThrowIfAbandoned();
        return current() && body is { Length: 8 } && body[0] == 1 && body[2] == 4 ? BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4)) : null;
    }
    /// <summary>Measured missile acceptance only. Refusal/timeout never falls back to melee or a second 0x19 write.</summary>
    public async Task<MeasuredMissileResult?> AcceptMeasuredMissileAsync(byte source, MeasuredMissileRequest request, Guid victim, uint avatar,
        ulong hostGeneration, ulong deadline, Func<bool> current, CancellationToken ct = default)
    {
        bool Valid() => current() && unchecked((ulong)Environment.TickCount64) <= deadline;
        var args = MeasuredMissileCodec.BuildArgs(source, request, victim, avatar, hostGeneration, deadline);
        var (body, _) = await SendAndAwaitAsync(Wo163, [18, .. args], Wo163Reply, ct, Valid);
        ct.ThrowIfCancellationRequested(); ProcessorFrameScope.ThrowIfAbandoned();
        if (!current() || body is not { Length: >= 4 } || body[2] != 18) return null;
        if (body.Length == 4 && body[0] == 0) return new(request.Key, MeasuredMissileStatus.Invalid, 0, 0, 0); // matching old DLL: explicit refusal
        return MeasuredMissileCodec.TryReadReply(body.AsSpan(4), source, request.Key, out var result) &&
            (body[0] == 1 || result.Status != MeasuredMissileStatus.Accepted) ? result : null;
    }
}
