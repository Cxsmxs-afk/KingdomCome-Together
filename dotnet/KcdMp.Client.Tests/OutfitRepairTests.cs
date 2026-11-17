// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
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
    public void Native_copy_parser_distinguishes_known_empty_unknown_and_truncated_sets()
    {
        Assert.Empty(OutfitRepair.ReadCopy([1, 0])!);
        Assert.Null(OutfitRepair.ReadCopy([0, 0]));
        Assert.Null(OutfitRepair.ReadCopy([1, 2, .. Under]));
        Assert.Null(OutfitRepair.ReadCopy([1, 0, 0]));
        Assert.Equal(Under, Assert.Single(OutfitRepair.ReadCopy([1, 1, .. Under])!));
    }
}
