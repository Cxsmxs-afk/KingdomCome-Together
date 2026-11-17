// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using KcdMp.Wire;

namespace KcdMp.Client;

public static class OutfitRepair
{
    public sealed record Plan(List<byte[]> FullWorn, List<byte[]> Remove, bool Needed);
    public readonly record struct Result(bool Converged, bool Known, int Missing, int Extra, int Applies);

    public static List<byte[]>? ReadCopy(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2 || payload[0] != 1) return null;
        int n = payload[1];
        if (n > Protocol.OutfitMaxItems || payload.Length != 2 + n * 16) return null;
        var worn = new List<byte[]>(n);
        for (int i = 0; i < n; i++) worn.Add(payload.Slice(2 + i * 16, 16).ToArray());
        return worn;
    }

    public static Plan Build(IReadOnlyList<byte[]> host, IReadOnlyList<byte[]>? copy, bool force)
    {
        var full = host.Select(b => b.ToArray()).ToList();
        if (copy is null) return new(full, [], true);
        var diff = Wo216Rules.OutfitDiff(host, copy);
        return new(full, diff.Remove, force || diff.Add.Count != 0 || diff.Remove.Count != 0);
    }

    /// <summary>Reapply the complete layered target after every mismatch, never just the missing classes.
    /// Unknown reads cannot prove a match. A superseded target stops before its next apply.</summary>
    public static async Task<Result> ConvergeAsync(IReadOnlyList<byte[]> host,
        Func<Task<List<byte[]>?>> read, Func<Plan, Task> apply, Func<bool> current,
        Func<Task>? delay = null, int maxApplies = 3)
    {
        delay ??= () => Task.Delay(2000);
        List<byte[]>? copy = null;
        int applied = 0;
        bool force = true; // a full host record also repairs a slot mismatch hidden by an equal class set
        while (current())
        {
            copy = await read();
            if (!current()) return new(false, false, 0, 0, applied);
            var plan = Build(host, copy, force);
            if (!plan.Needed) return new(true, true, 0, 0, applied);
            if (applied >= maxApplies) break;
            await apply(plan);
            applied++;
            force = false;
            await delay();
        }
        var final = copy is null ? (host.ToList(), new List<byte[]>()) : Wo216Rules.OutfitDiff(host, copy);
        return new(false, copy is not null, final.Item1.Count, final.Item2.Count, applied);
    }
}
