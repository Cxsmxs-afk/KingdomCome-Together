// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;

namespace KcdMp.Client;

public readonly record struct CalendarReadback(byte Result, long BeforeMs, long AfterMs);

public sealed partial class CombatPipe
{
    public async Task<(long WorldMs, double Ratio)?> CalendarSnapshotAsync(CancellationToken ct = default)
    {
        var r = await Wo140Async(9, [], ct);
        if (r is not { Ok: true, Payload.Length: >= 17 } x || x.Payload[0] != 1) return null;
        long ms = BinaryPrimitives.ReadInt64LittleEndian(x.Payload.AsSpan(1));
        double ratio = BinaryPrimitives.ReadDoubleLittleEndian(x.Payload.AsSpan(9));
        return ms >= 0 && ms <= HostCalendar.MaxWorldMs && double.IsFinite(ratio) && ratio >= 0 && ratio <= 15 ? (ms, ratio) : null;
    }

    /// <summary>Native pre-update calendar: 0 reset, 1 sample, 2 arm before load, 3 verify the new load's pre-update latch.</summary>
    public async Task<CalendarReadback?> CalendarFollowAsync(byte mode, long worldMs = 0, double ratio = 0, CancellationToken ct = default)
    {
        var args = new byte[17];
        args[0] = mode;
        BinaryPrimitives.WriteInt64LittleEndian(args.AsSpan(1), worldMs);
        BinaryPrimitives.WriteDoubleLittleEndian(args.AsSpan(9), ratio);
        var r = await Wo140Async(10, args, ct);
        return r is { Ok: true, Payload.Length: >= 17 } x
            ? new CalendarReadback(x.Payload[0], BinaryPrimitives.ReadInt64LittleEndian(x.Payload.AsSpan(1)), BinaryPrimitives.ReadInt64LittleEndian(x.Payload.AsSpan(9)))
            : null;
    }
}
