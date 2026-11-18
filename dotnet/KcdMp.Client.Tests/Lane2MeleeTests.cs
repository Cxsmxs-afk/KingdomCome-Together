// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

[CollectionDefinition("Lane 2 console output", DisableParallelization = true)]
public sealed class Lane2ConsoleOutputCollection { }

[Collection("Lane 2 console output")]
public sealed class Lane2MeleeTests
{
    private const string UnavailableCounter = "melee_presentation_unavailable";

    [Theory]
    [InlineData(ActionKind.Attack, "combat_action_attack", 0, 1, -1)]
    [InlineData(ActionKind.Attack, "combat_action_attack", AttackEvent.FlagCombo, 1, -1)]
    [InlineData(ActionKind.BlockImpulse, "combat_action_block", 0, 6, 6)]
    [InlineData(ActionKind.BlockImpulse, "combat_action_perfect_block", Wo187Rules.NativeFlagPerfect, 0x83, 14)]
    [InlineData(ActionKind.Dodge, "combat_action_dodge", 0, 7, -1)]
    [InlineData(ActionKind.AvatarHit, "combat_action_hit", 0, 0x84, -1)]
    [InlineData(ActionKind.AvatarRecoil, "combat_action_failed_attack", 0, 0x85, -1)]
    public void Committed_melee_rows_have_their_own_native_kind(ActionKind action, string table, byte flags, byte kind, int actionType) =>
        Assert.Equal(kind, GameBridge.PresentationMeleeKind(action, table, flags, actionType));

    [Theory]
    [InlineData(ActionKind.Attack, "combat_action_block", 0)]
    [InlineData(ActionKind.BlockImpulse, "combat_action_attack", 0)]
    [InlineData(ActionKind.BlockImpulse, "combat_action_block", Wo187Rules.NativeFlagPerfect)]
    [InlineData(ActionKind.BlockImpulse, "combat_action_perfect_block", 0)]
    [InlineData(ActionKind.Dodge, "combat_action_block", 0)]
    [InlineData(ActionKind.AvatarHit, "combat_action_failed_attack", 0)]
    [InlineData(ActionKind.AvatarRecoil, "combat_action_hit", 0)]
    public void A_wrong_table_or_inconsistent_flags_have_no_native_kind(ActionKind action, string table, byte flags) =>
        Assert.Equal((byte)0, GameBridge.PresentationMeleeKind(action, table, flags));

    [Fact]
    public Task A_combo_flagged_attack_row_registers_and_schedules_an_ordinary_swing() =>
        new EdgePipeTests().Presentation_combat_registers_a_row_once_then_sends_only_its_numeric_key(
            ActionKind.Attack, "combat_action_attack", AttackEvent.FlagCombo, (byte)ActionKind.Attack);

    [Theory]
    [InlineData(0)]
    [InlineData(AttackEvent.FlagCombo)]
    public async Task A_paired_attack_has_no_native_kind_and_stays_out_of_legacy_playback(byte flags)
    {
        const string table = "combat_action_sync_attack";
        Assert.Equal((byte)0, GameBridge.PresentationMeleeKind(ActionKind.Attack, table, flags));
        Assert.True(await Schedule(typedPresentation: false, ActionKind.Attack, table, flags));
    }

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
    public void An_unsupported_request_can_use_legacy_playback_but_uncertain_receipts_cannot()
    {
        Assert.Equal(PresentationCombatAdmission.Accepted, GameBridge.PresentationMeleeAdmission(new(EdgeOutcome.Completed, [1, 0, 0])));
        Assert.Equal(PresentationCombatAdmission.Suppressed, GameBridge.PresentationMeleeAdmission(new(EdgeOutcome.Unavailable, [])));
        Assert.Equal(PresentationCombatAdmission.Suppressed, GameBridge.PresentationMeleeAdmission(new(EdgeOutcome.Ambiguous, [])));
        Assert.Equal(PresentationCombatAdmission.Fallback, GameBridge.PresentationMeleeAdmission(new(EdgeOutcome.Unsupported, [])));
    }

    [Fact]
    public void A_native_unavailable_receipt_preserves_the_typed_melee_damage_gate()
    {
        EdgePipeReply receipt = new(EdgeOutcome.Completed, [0, 0, 1]);
        Assert.Equal(PresentationCombatAdmission.Fallback, CombatPipe.GetPresentationCombatAdmission(receipt));
        Assert.Equal(PresentationCombatAdmission.Suppressed, GameBridge.PresentationMeleeAdmission(receipt));
    }

    [Theory]
    [InlineData(false, 0x3F, "MP-RP-MELEE-ROW ")]
    [InlineData(true, 0x40, "MP-RP-TIMELINE-IN ")]
    public async Task A_native_unavailable_row_is_counted_logged_and_consumed(bool registered, byte rejectedOp, string logPrefix)
    {
        // Reuse EdgePipeTests' active-session fixture so both typed operations traverse the real scheduler.
        const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        var fixtures = typeof(EdgePipeTests);
        var connectionType = fixtures.GetNestedType("Connection", BindingFlags.NonPublic)!;
        var harnessType = fixtures.GetNestedType("PresentationCombatHarness", BindingFlags.NonPublic)!;
        await using var connection = (IAsyncDisposable)Activator.CreateInstance(connectionType)!;
        await (Task)connectionType.GetMethod("Connect")!.Invoke(connection, [true])!;
        var create = (Task)harnessType.GetMethod("CreateAsync")!.Invoke(null, [connection])!;
        await create;
        var harness = create.GetType().GetProperty("Result")!.GetValue(create)!;
        var bridge = (GameBridge)harnessType.GetFields(privateInstance).Single(field => field.FieldType == typeof(GameBridge)).GetValue(harness)!;
        var token = (CancellationToken)connectionType.GetProperty("Token")!.GetValue(connection)!;

        Task<byte[]> Read() => (Task<byte[]>)connectionType.GetMethod("Read")!.Invoke(connection, [(byte)0x38])!;
        Task Reply(byte[] request, byte[] payload) => (Task)connectionType.GetMethod("Respond")!.Invoke(connection,
            [BinaryPrimitives.ReadUInt32LittleEndian(request), EdgeOutcome.Completed, (byte)0x81, payload])!;
        void EmitStats() => typeof(GameBridge).GetMethod("T3EmitStats", privateInstance)!.Invoke(bridge,
            [(byte)0, token, null, null, null, null, null]);

        using var output = new StringWriter();
        var original = Console.Out;
        Console.SetOut(output);
        try
        {
            var action = (Task<bool>)harnessType.GetMethod("Apply")!.Invoke(harness,
                [ActionKind.Attack, "combat_action_attack", AttackEvent.FlagCombo, (ushort)12, token])!;
            byte[] request = await Read();
            Assert.Equal(0x3F, request[5]);
            EmitStats();
            if (registered)
            {
                await Reply(request, [1, 0, 0]);
                request = await Read();
            }
            Assert.Equal(rejectedOp, request[5]);
            await Reply(request, [0, 0, 1]);
            // A legacy call would need another pipe reply. The caller's older playback also needs a false return.
            Assert.True(await action.WaitAsync(TimeSpan.FromSeconds(3)));
            EmitStats();
        }
        finally
        {
            Console.SetOut(original);
        }

        string[] lines = output.ToString().Split('\n');
        var diagnostic = Assert.Single(lines, line => line.Contains("reason=typed-provider-unavailable", StringComparison.Ordinal));
        Assert.StartsWith(logPrefix, diagnostic);
        Assert.Contains(" peer=2 ", diagnostic);
        Assert.Contains(" seq=12 ", diagnostic);
        Assert.Contains(" result=unavailable ", diagnostic);
        Assert.Collection(lines.Where(line => line.StartsWith("MP-T3-STATS ", StringComparison.Ordinal) && line.Contains(UnavailableCounter)),
            before => Assert.Contains(UnavailableCounter + "=0", before),
            after => Assert.Contains(UnavailableCounter + "=1", after));
    }

    [Fact]
    public void Other_receipts_do_not_count_as_native_melee_unavailable()
    {
        const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        var bridge = new GameBridge(new ClientConfig());
        var record = typeof(GameBridge).GetMethod("RecordMeleePresentationUnavailable", privateInstance)!;
        EdgePipeReply[] otherReceipts = [
            new(EdgeOutcome.Completed, [1, 0, 0]), new(EdgeOutcome.Completed, [0, 0, 3]), new(EdgeOutcome.Completed, [0, 0, 4]),
            new(EdgeOutcome.Completed, [0, 1, 1]), new(EdgeOutcome.Completed, [0, 0, 1, 0]),
            new(EdgeOutcome.Busy, []), new(EdgeOutcome.Unsupported, []), new(EdgeOutcome.Ambiguous, []),
            new(EdgeOutcome.Unavailable, [0, 0, 1])
        ];
        foreach (var reply in otherReceipts) Assert.Null(record.Invoke(bridge, [reply]));

        var counters = (ConcurrentDictionary<string, long>)typeof(GameBridge).GetField("_t3Counters", privateInstance)!.GetValue(bridge)!;
        Assert.Equal(0, counters.GetValueOrDefault(UnavailableCounter));
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
