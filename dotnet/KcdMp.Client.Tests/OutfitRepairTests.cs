// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public class OutfitRepairTests
{
    private static readonly byte[] Under = Guid.Parse("11111111-1111-1111-1111-111111111111").ToByteArray();
    private static readonly byte[] Outer = Guid.Parse("22222222-2222-2222-2222-222222222222").ToByteArray();
    private static readonly byte[] Extra = Guid.Parse("33333333-3333-3333-3333-333333333333").ToByteArray();

    [Fact]
    public async Task Missing_underlayer_reapplies_the_whole_host_set_including_the_outer_piece()
    {
        var worn = new List<byte[]> { Outer, Extra };
        var calls = new List<OutfitRepair.Plan>();
        var result = await OutfitRepair.ConvergeAsync([Under, Outer], () => Task.FromResult<List<byte[]>?>(worn), plan =>
        {
            calls.Add(plan);
            Assert.Equal(new[] { Convert.ToHexString(Under), Convert.ToHexString(Outer) }, plan.FullWorn.Select(Convert.ToHexString));
            Assert.Equal(Extra, Assert.Single(plan.Remove));
            worn = plan.FullWorn.ToList();
            return Task.CompletedTask;
        }, () => true, () => Task.CompletedTask);
        Assert.True(result.Converged);
        Assert.Single(calls);
    }

    [Fact]
    public async Task Equal_class_set_still_repairs_a_wrong_slot_when_a_full_host_record_arrives()
    {
        bool wrongSlot = true;
        var result = await OutfitRepair.ConvergeAsync([Under, Outer], () => Task.FromResult<List<byte[]>?>([Under, Outer]), plan =>
        {
            Assert.Equal(2, plan.FullWorn.Count);
            wrongSlot = false;
            return Task.CompletedTask;
        }, () => true, () => Task.CompletedTask);
        Assert.False(wrongSlot);
        Assert.True(result.Converged);
        Assert.Equal(1, result.Applies);
    }

    [Fact]
    public async Task Unknown_worn_read_is_repaired_then_verified_and_never_counted_as_an_empty_match()
    {
        bool applied = false;
        var result = await OutfitRepair.ConvergeAsync([Under, Outer],
            () => Task.FromResult<List<byte[]>?>(applied ? [Under, Outer] : null), plan =>
            {
                Assert.Equal(2, plan.FullWorn.Count);
                applied = true;
                return Task.CompletedTask;
            }, () => true, () => Task.CompletedTask);
        Assert.True(result.Known);
        Assert.True(result.Converged);
        var unreadable = await OutfitRepair.ConvergeAsync([Under], () => Task.FromResult<List<byte[]>?>(null), _ => Task.CompletedTask,
            () => true, () => Task.CompletedTask);
        Assert.False(unreadable.Known);
        Assert.False(unreadable.Converged);
        Assert.Equal(3, unreadable.Applies);
    }

    [Fact]
    public async Task Superseded_retry_stops_before_another_old_outfit_apply()
    {
        bool current = true;
        int applies = 0;
        var result = await OutfitRepair.ConvergeAsync([Under], () => Task.FromResult<List<byte[]>?>([]), _ =>
        {
            applies++;
            return Task.CompletedTask;
        }, () => current, () => { current = false; return Task.CompletedTask; });
        Assert.False(result.Converged);
        Assert.Equal(1, applies);
    }

    [Fact]
    public async Task Actual_worker_keeps_a_silent_target_without_native_work_then_retires_it_when_host_death_arrives()
    {
        var bridge = Joiner();
        const string name = "outfit_test_npc";
        const BindingFlags fields = BindingFlags.NonPublic | BindingFlags.Instance;
        var wanted = (IDictionary)typeof(GameBridge).GetField("_w216OutfitWanted", fields)!.GetValue(bridge)!;
        var targetType = typeof(GameBridge).GetNestedType("OutfitTarget", BindingFlags.NonPublic)!;
        var target = Activator.CreateInstance(targetType, new object[] { new List<byte[]> { Under }, 0u, 0L })!;
        wanted.Add(name, target);
        var newest = (ConcurrentDictionary<string, (ushort, byte, long)>)typeof(GameBridge).GetField("_w147NpcNewest", fields)!.GetValue(bridge)!;
        long now = Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
        newest[name] = (1, 0, now - 60_000);
        var worker = typeof(GameBridge).GetMethod("Wo216RepairOutfitAsync", fields)!;
        await (Task)worker.Invoke(bridge, [name])!;
        Assert.Same(target, wanted[name]); // no engine request: this bridge has no connected game
        newest[name] = (2, Protocol.NpcStateFlagDead, now);
        await (Task)worker.Invoke(bridge, [name])!;
        Assert.False(wanted.Contains(name)); // the retained living outfit can no longer rebuild corpse loot
    }

    [Fact]
    public async Task Actual_outfit_receiver_refuses_a_predeath_set_after_an_authoritative_death()
    {
        var bridge = Joiner();
        const string name = "outfit_test_npc";
        const BindingFlags fields = BindingFlags.NonPublic | BindingFlags.Instance;
        var dead = (ConcurrentDictionary<string, bool>)typeof(GameBridge).GetField("_npcLastDead", fields)!.GetValue(bridge)!;
        dead[name] = true;
        var receive = typeof(GameBridge).GetMethod("Wo216OnOutfitAsync", fields)!;
        await (Task)receive.Invoke(bridge, [new ExtraRow { Name = name, Worn = [Under] }])!;
        var wanted = (IDictionary)typeof(GameBridge).GetField("_w216OutfitWanted", fields)!.GetValue(bridge)!;
        Assert.Empty(wanted);
    }

    private static GameBridge Joiner()
    {
        var bridge = new GameBridge(new ClientConfig());
        const BindingFlags fields = BindingFlags.NonPublic | BindingFlags.Instance;
        void Set(string field, object value) => typeof(GameBridge).GetField(field, fields)!.SetValue(bridge, value);
        Set("_combatRoleApplied", true); Set("_isDamageAuthority", false); Set("_sharedWorld", true);
        Set("_hostAuthority", true); Set("_joinedWorld", true);
        var where = typeof(GameBridge).GetField("_where", fields)!;
        where.SetValue(bridge, Enum.Parse(where.FieldType, "World"));
        return bridge;
    }

    [Fact]
    public void Native_copy_parser_distinguishes_known_empty_unknown_and_truncated_sets()
    {
        Assert.Empty(OutfitRepair.ReadCopy([1, 0])!);
        Assert.Null(OutfitRepair.ReadCopy([0, 0]));
        Assert.Null(OutfitRepair.ReadCopy([1, 2, .. Under]));
        Assert.Null(OutfitRepair.ReadCopy([1, 0, 0]));
        Assert.Equal(Under, Assert.Single(OutfitRepair.ReadCopy([1, 1, .. Under])!));
    }
}
