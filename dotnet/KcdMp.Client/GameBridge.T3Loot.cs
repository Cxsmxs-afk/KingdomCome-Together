// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Threading.Channels;
using KcdMp.Wire;

namespace KcdMp.Client;

public partial class GameBridge
{
    private sealed record LootFrame(GameBridge Owner, InFrame Frame, ulong Generation);
    private static readonly AsyncLocal<LootFrame?> CurrentLoot = new();
    private static bool T3LootType(int type) => type is Protocol.LootAskDown or Protocol.LootHostDown;

    // Both reliable families share one FIFO. Parts, grants and acknowledgements
    // must keep their wire order while NPC/ghost processing waits elsewhere.
    private async Task T3LootLaneAsync(Channel<InFrame> lane, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in lane.Reader.ReadAllAsync(ct))
            {
                var accounting = new T3FrameAccounting(frame);
                try
                {
                    if (Wo140DropSeparate(frame.Type)) continue;
                    if (Wo206Rules.WorldCategory(frame.Type, frame.Payload) is Wo206Cat category)
                    {
                        // Unknown peer epochs cannot authorize inventory writes.
                        // Build metadata is deliberately outside this category.
                        if (frame.Stamp == 0)
                        {
                            T3Increment("loot_fastlane_unknown_epoch");
                            continue;
                        }
                        if (frame.LootGeneration != P1Lifecycle.Current.Generation || !Wo206Admit(category, frame.Stamp, "loot fastlane"))
                        {
                            T3Increment("loot_fastlane_stale");
                            continue;
                        }
                    }
                    T3Increment("loot_fastlane_frames");
                    T3Max("loot_fastlane_max_wait_ms", Math.Max(0, (Stopwatch.GetTimestamp() - frame.Arrival) * 1000 / Stopwatch.Frequency));
                    var result = await ProcessorFrameScope.RunAsync(async token =>
                    {
                        var before = CurrentLoot.Value;
                        CurrentLoot.Value = new(this, frame, frame.LootGeneration);
                        Wo207Trace.Kind.Value = frame.Type;
                        try { await Wo123OnFrameAsync(frame.Type, frame.Payload, token); }
                        finally { CurrentLoot.Value = before; }
                    }, Volatile.Read(ref _t3ProcFrameTimeoutMs), ct);
                    T3FrameResult(frame.Type, result);
                    if (result.Status == ProcessorFrameScope.Outcome.SessionCancelled) return;
                }
                finally { accounting.End(frame.Type); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { Console.WriteLine($"MP-T3-LOOT lane stopped: {ex.GetType().Name}: {ex.Message}"); }
        finally
        {
            lane.Writer.TryComplete();
            // Only this lane's accepted rows: never reset another queue's trace.
            while (lane.Reader.TryRead(out var remaining)) Wo207Trace.Dequeued(remaining.Type);
        }
    }

    private void T3LootGuard(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ProcessorFrameScope.ThrowIfAbandoned();
        if (CurrentLoot.Value is { } owner && ReferenceEquals(owner.Owner, this)
            && (owner.Frame.Stamp == 0 || (_w206.Check && _w206.WorldStale(owner.Frame.Stamp)) || owner.Generation != P1Lifecycle.Current.Generation))
            throw new OperationCanceledException("loot world was superseded", ct);
    }

    private ulong T3LootGeneration => CurrentLoot.Value is { } owner && ReferenceEquals(owner.Owner, this)
        ? owner.Generation : P1Lifecycle.Current.Generation;

    // Explicitly called by true loot handlers only. Neither census/props/carts
    // nor ledger work gets an implicit bypass of the normal Lua queue.
    private async Task T3LootExecAsync(string lua, CancellationToken ct)
    {
        using var link = ProcessorFrameScope.Link(ct);
        T3LootGuard(link.Token);
        if (_where == GameWhere.Menu) return;
        if (LuaCommandBudget.EncodedLength(lua) + LuaCommandBudget.WrapperEncodedChars > LuaCommandBudget.MaxEncodedCommandChars)
            throw new InvalidDataException("loot Lua command exceeds the encoded console budget");
        await GameCalls.ExecuteNowAsync(lua, link.Token);
        T3LootGuard(link.Token);
    }

    // Split a single wire part locally. Original part count, revision and
    // receipt remain intact; Lua assembles these before its existing apply.
    private async Task T3LootItemsAsync<T>(IReadOnlyList<T> items, Func<IReadOnlyList<T>, string> table,
        Func<int, int, string, string> call, CancellationToken ct)
    {
        var groups = new List<List<T>>();
        var current = new List<T>();
        foreach (var item in items)
        {
            var trial = new List<T>(current) { item };
            // 40 is the strict maximum local chunk count (wire lists are bounded).
            string candidate = call(40, 40, table(trial));
            if (LuaCommandBudget.EncodedLength(candidate) + LuaCommandBudget.WrapperEncodedChars > LuaCommandBudget.MaxEncodedCommandChars)
            {
                if (current.Count == 0) throw new InvalidDataException("one loot item exceeds the encoded console budget");
                groups.Add(current); current = [item];
            }
            else current = trial;
        }
        if (current.Count > 0 || groups.Count == 0) groups.Add(current);
        if (groups.Count > 40) throw new InvalidDataException("too many local loot chunks");
        for (int i = 0; i < groups.Count; i++)
            await T3LootExecAsync(call(i + 1, groups.Count, table(groups[i])), ct);
    }
}
