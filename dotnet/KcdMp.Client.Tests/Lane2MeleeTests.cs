// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Collections.Concurrent;
using System.Reflection;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public sealed class Lane2MeleeTests
{
    [Theory]
    [InlineData(ActionKind.Attack, "combat_action_attack", 0, 1, -1)]
    [InlineData(ActionKind.BlockImpulse, "combat_action_block", 0, 6, 6)]
    [InlineData(ActionKind.BlockImpulse, "combat_action_perfect_block", Wo187Rules.NativeFlagPerfect, 0x83, 14)]
    [InlineData(ActionKind.Dodge, "combat_action_dodge", 0, 7, -1)]
    [InlineData(ActionKind.AvatarHit, "combat_action_hit", 0, 0x84, -1)]
    [InlineData(ActionKind.AvatarRecoil, "combat_action_failed_attack", 0, 0x85, -1)]
    public void Committed_melee_rows_have_their_own_native_kind(ActionKind action, string table, byte flags, byte kind, int actionType) =>
        Assert.Equal(kind, GameBridge.PresentationMeleeKind(action, table, flags, actionType));

    [Theory]
    [InlineData(ActionKind.Attack, "combat_action_block", 0)]
    [InlineData(ActionKind.Attack, "combat_action_attack", AttackEvent.FlagCombo)]
    [InlineData(ActionKind.BlockImpulse, "combat_action_attack", 0)]
    [InlineData(ActionKind.BlockImpulse, "combat_action_block", Wo187Rules.NativeFlagPerfect)]
    [InlineData(ActionKind.BlockImpulse, "combat_action_perfect_block", 0)]
    [InlineData(ActionKind.Dodge, "combat_action_block", 0)]
    [InlineData(ActionKind.AvatarHit, "combat_action_failed_attack", 0)]
    [InlineData(ActionKind.AvatarRecoil, "combat_action_hit", 0)]
    public void A_wrong_table_or_inconsistent_flags_have_no_native_kind(ActionKind action, string table, byte flags) =>
        Assert.Equal((byte)0, GameBridge.PresentationMeleeKind(action, table, flags));

    [Fact]
    public void A_paired_attack_is_never_mapped_as_an_ordinary_swing() =>
        Assert.NotEqual((byte)ActionKind.Attack,
            GameBridge.PresentationMeleeKind(ActionKind.Attack, "combat_action_sync_attack", AttackEvent.FlagCombo));

    [Fact]
    public async Task A_wrong_table_is_consumed_when_the_legacy_path_is_enabled()
    {
        Assert.True(await Schedule(typedPresentation: false, ActionKind.Attack, "combat_action_block", 0));
        Assert.False(await Schedule(typedPresentation: false, ActionKind.Attack, "combat_action_attack", 0));
    }

    [Theory]
    [InlineData(55)]
    [InlineData(62)]
    public async Task A_master_strike_is_consumed_without_playing_the_perfect_block(int actionType)
    {
        const string table = "combat_action_perfect_block";
        Assert.Equal((byte)0, GameBridge.PresentationMeleeKind(ActionKind.BlockImpulse, table, Wo187Rules.NativeFlagPerfect, actionType));
        Assert.True(await Schedule(typedPresentation: false, ActionKind.BlockImpulse, table, Wo187Rules.NativeFlagPerfect, actionType));
        Assert.False(await Schedule(typedPresentation: false, ActionKind.BlockImpulse, table, Wo187Rules.NativeFlagPerfect, 14));
    }

    [Theory]
    [InlineData(ActionKind.Attack, "combat_action_attack", 0)]
    [InlineData(ActionKind.BlockImpulse, "combat_action_perfect_block", Wo187Rules.NativeFlagPerfect)]
    [InlineData(ActionKind.AvatarHit, "combat_action_hit", 0)]
    [InlineData(ActionKind.AvatarRecoil, "combat_action_failed_attack", 0)]
    public async Task Typed_melee_consumes_rows_when_the_clock_is_missing_or_from_an_inactive_session(ActionKind kind, string table, byte flags)
    {
        Assert.True(await Schedule(typedPresentation: true, kind, table, flags));
        Assert.True(await Schedule(typedPresentation: true, kind, table, flags, retiredClock: true));
        Assert.False(await Schedule(typedPresentation: false, kind, table, flags));
    }

    [Fact]
    public void Only_an_unsupported_typed_request_allows_legacy_melee_playback()
    {
        Assert.Equal(PresentationCombatAdmission.Accepted, GameBridge.PresentationMeleeAdmission(new(EdgeOutcome.Completed, [1, 0, 0])));
        Assert.Equal(PresentationCombatAdmission.Suppressed, GameBridge.PresentationMeleeAdmission(new(EdgeOutcome.Completed, [0, 0, 1])));
        Assert.Equal(PresentationCombatAdmission.Suppressed, GameBridge.PresentationMeleeAdmission(new(EdgeOutcome.Unavailable, [])));
        Assert.Equal(PresentationCombatAdmission.Suppressed, GameBridge.PresentationMeleeAdmission(new(EdgeOutcome.Ambiguous, [])));
        Assert.Equal(PresentationCombatAdmission.Fallback, GameBridge.PresentationMeleeAdmission(new(EdgeOutcome.Unsupported, [])));
    }

    private static Task<bool> Schedule(bool typedPresentation, ActionKind kind, string table, byte flags, int actionType = -1,
        bool retiredClock = false)
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var bridge = new GameBridge(new ClientConfig());
        var bridgeType = typeof(GameBridge);
        var switches = (ConcurrentDictionary<string, bool>)bridgeType.GetField("_w221On", instance)!.GetValue(bridge)!;
        switches["rp_combat"] = typedPresentation;
        var frameType = bridgeType.GetNestedType("PresentationFrame", BindingFlags.NonPublic)!;
        object? stamp = Activator.CreateInstance(frameType);
        if (retiredClock)
        {
            var sessionType = bridgeType.GetNestedType("PresentationSession", BindingFlags.NonPublic)!;
            var session = Activator.CreateInstance(sessionType, instance | BindingFlags.Public, null, [Stream.Null, CancellationToken.None], null);
            stamp = Activator.CreateInstance(frameType, instance | BindingFlags.Public, null, [session, 3UL, 1150UL, 1190UL], null);
        }
        var schedule = bridgeType.GetMethod("TrySchedulePresentationRowAsync", instance)!;
        var action = new InboundAction(2, kind, 12, ActionPhase.Commit, default, [70, 0, 0, 0]);
        var row = new ActionRowCatalog.Row(table, "Combat", "tags", 0, 0, 0, ActionType: actionType);
        return (Task<bool>)schedule.Invoke(bridge, [action, 0x14151617u, row, flags, stamp, CancellationToken.None])!;
    }
}
