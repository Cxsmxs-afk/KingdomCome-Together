// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public class T3NativeCountersTests
{
    private static byte[] Payload(int count = 19)
    {
        var bytes = new byte[2 + count * 9]; bytes[0] = 1; bytes[1] = (byte)count;
        for (int i = 0; i < count; i++) { bytes[2 + i * 9] = (byte)i; BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(3 + i * 9), (ulong)i); }
        return bytes;
    }

    [Fact]
    public void Native_measurements_have_fixed_field_identity_and_fit_existing_reply_budget()
    {
        byte[] bytes = Payload(); Assert.Equal(173, bytes.Length);
        Assert.True(T3NativeCounters.TryRead(bytes, out var values));
        Assert.Equal(19, values.Count);
        Assert.Equal(0, values["tp_total"]); Assert.Equal(5, values["wake_dist_m_max"]);
        Assert.Equal(6, values["wake_drop_m_max"]); Assert.Equal(14, values["falldeath_after_tp"]);
        Assert.Equal(18, values["tp_by_source_scene_nudge"]);
    }

    [Fact]
    public void Bounded_unknown_fields_are_forward_compatible_but_duplicates_are_never_valid()
    {
        byte[] bytes = Payload(20); bytes[2 + 19 * 9] = 250;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(3 + 19 * 9), ulong.MaxValue);
        Assert.True(T3NativeCounters.TryRead(bytes, out var values)); Assert.Equal(19, values.Count);
        bytes = Payload(21); bytes[2 + 19 * 9] = 250; bytes[2 + 20 * 9] = 250;
        Assert.False(T3NativeCounters.TryRead(bytes, out _));
        bytes = Payload(); bytes[2 + 18 * 9] = 17;
        Assert.False(T3NativeCounters.TryRead(bytes, out _));
    }

    [Fact]
    public void Truncated_oversized_overflowing_or_incomplete_snapshots_cannot_be_imported()
    {
        byte[] bytes = Payload();
        for (int n = 0; n < bytes.Length; n++) Assert.False(T3NativeCounters.TryRead(bytes.AsSpan(0, n), out _));
        Assert.False(T3NativeCounters.TryRead(Payload(18), out _));
        Assert.False(T3NativeCounters.TryRead(Payload(57), out _));
        bytes[0] = 2; Assert.False(T3NativeCounters.TryRead(bytes, out _));
        bytes = Payload(); BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(3), ulong.MaxValue);
        Assert.False(T3NativeCounters.TryRead(bytes, out _));
        Assert.False(T3NativeCounters.TryRead([.. Payload(), 0], out _));
    }

    [Fact]
    public void Complete_corpse_group_is_atomic_and_does_not_change_the_P4_prefix()
    {
        Assert.True(T3NativeCounters.TryRead(Payload(24), out var values));
        Assert.Equal(24, values.Count);
        Assert.Equal(19, values["corpses_visible"]); Assert.Equal(20, values["corpse_hidden_blocked"]);
        Assert.Equal(21, values["grave_markers_added"]); Assert.Equal(22, values["grave_marker_fallbacks"]);
        Assert.Equal(23, values["markers_off_session"]);
        for (int n = 20; n < 24; n++) Assert.False(T3NativeCounters.TryRead(Payload(n), out _));
        Assert.True(T3NativeCounters.TryRead(Payload(19), out var old));
        Assert.Equal(19, old.Count); Assert.False(old.ContainsKey("corpses_visible"));
        byte[] next = Payload(25); next[2 + 24 * 9] = 250;
        Assert.True(T3NativeCounters.TryRead(next, out values)); Assert.Equal(24, values.Count);
    }

    [Fact]
    public void Ranged_diagnostics_are_an_atomic_extension_of_the_actual_P4_and_P5_prefix()
    {
        byte[] bytes = Payload(30); Assert.Equal(272, bytes.Length);
        Assert.True(T3NativeCounters.TryRead(bytes, out var values)); Assert.Equal(30, values.Count);
        Assert.Equal(24, values["ranged_native_missing_actor"]); Assert.Equal(25, values["ranged_native_unreadable_phase"]);
        Assert.Equal(26, values["ranged_native_unknown_hand"]); Assert.Equal(27, values["ranged_native_queue_overflow"]);
        Assert.Equal(28, values["ranged_native_expired_rows"]); Assert.Equal(29, values["ranged_native_lifecycle_dropped"]);
        for (int n = 25; n < 30; n++) Assert.False(T3NativeCounters.TryRead(Payload(n), out _));
        byte[] missingP5 = [1, 25, .. bytes.AsSpan(2, 19 * 9).ToArray(), .. bytes.AsSpan(2 + 24 * 9).ToArray()];
        Assert.False(T3NativeCounters.TryRead(missingP5, out _));
        byte[] future = Payload(31); future[2 + 30 * 9] = 250;
        Assert.True(T3NativeCounters.TryRead(future, out values)); Assert.Equal(30, values.Count);
    }

    [Fact]
    public void Presentation_snapshot_pins_measurement_and_coverage_ids_and_requires_its_full_prefix()
    {
        const int fields = 47;
        byte[] bytes = Payload(fields);
        Assert.Equal(425, bytes.Length);
        Assert.True(T3NativeCounters.TryRead(bytes, out var values));
        string[] keys = [
            "rp_e2e_ms_p50", "rp_e2e_ms_p95", "rp_e2e_ms_max", "rp_agent_hop_ms_p95",
            "rp_corr_n", "rp_corr_cm_p95", "rp_corr_cm_max", "rp_hard_place", "rp_step_ratio",
            "rp_action_lag_ms_p95", "owner_violations", "rp_e2e_n", "rp_agent_hop_n", "rp_action_n",
            "rp_step_n", "rp_pose_n", "rp_owner_n"
        ];
        for (int i = 0; i < keys.Length; i++)
        {
            int id = 30 + i;
            Assert.Equal(id, values[keys[i]]);
            int offset = 2 + id * 9;
            byte[] missing = [1, fields - 1, .. bytes.AsSpan(2, offset - 2), .. bytes.AsSpan(offset + 9)];
            Assert.False(T3NativeCounters.TryRead(missing, out _));
        }
        byte[] missingRanged = [1, fields - 6, .. bytes.AsSpan(2, 24 * 9), .. bytes.AsSpan(2 + 30 * 9)];
        Assert.False(T3NativeCounters.TryRead(missingRanged, out _));
        Assert.True(T3NativeCounters.TryRead(Payload(30), out var previous));
        Assert.DoesNotContain("rp_e2e_n", previous.Keys);
    }

    [Fact]
    public void Owner_counters_extend_a_complete_presentation_snapshot_atomically()
    {
        byte[] bytes = Payload(49);
        Assert.Equal(443, bytes.Length);
        Assert.True(T3NativeCounters.TryRead(bytes, out var values));
        Assert.Equal(40, values["owner_violations"]);
        Assert.Equal(46, values["rp_owner_n"]);
        Assert.Equal(47, values["owner_handovers"]);
        Assert.Equal(48, values["owner_denied_sets"]);

        Assert.True(T3NativeCounters.TryRead(Payload(47), out var previous));
        Assert.DoesNotContain("owner_handovers", previous.Keys);
        Assert.DoesNotContain("owner_denied_sets", previous.Keys);
        Assert.False(T3NativeCounters.TryRead(Payload(48), out _));
        byte[] onlyDenials = Payload(48);
        onlyDenials[2 + 47 * 9] = 48;
        Assert.False(T3NativeCounters.TryRead(onlyDenials, out _));
        byte[] missingPresentation = [1, 32, .. bytes.AsSpan(2, 30 * 9), .. bytes.AsSpan(2 + 47 * 9)];
        Assert.False(T3NativeCounters.TryRead(missingPresentation, out _));
        byte[] unknown = Payload(50);
        unknown[2 + 49 * 9] = 80;
        Assert.True(T3NativeCounters.TryRead(unknown, out var future));
        Assert.Equal(49, future.Count);
    }

    [Fact]
    public void Edge_lane_counters_extend_the_owner_snapshot_without_exceeding_the_reply_budget()
    {
        byte[] bytes = Payload(55);
        Assert.Equal(497, bytes.Length);
        Assert.True(T3NativeCounters.TryRead(bytes, out var values));
        Assert.Equal(47, values["owner_handovers"]);
        Assert.Equal(49, values["edge_rt_age_ms_p95"]);
        Assert.Equal(50, values["edge_rt_age_ms_max"]);
        Assert.Equal(51, values["edge_bulk_age_ms_p95"]);
        Assert.Equal(52, values["pipe_busy_replies"]);
        Assert.Equal(53, values["edge_rt_n"]);
        Assert.Equal(54, values["edge_bulk_n"]);

        for (int n = 50; n < 55; n++) Assert.False(T3NativeCounters.TryRead(Payload(n), out _));
        byte[] missingOwner = [1, 53, .. bytes.AsSpan(2, 47 * 9), .. bytes.AsSpan(2 + 49 * 9)];
        Assert.False(T3NativeCounters.TryRead(missingOwner, out _));
        Assert.True(T3NativeCounters.TryRead(Payload(49), out var old));
        Assert.DoesNotContain("edge_rt_n", old.Keys);
        Assert.True(T3NativeCounters.TryRead(Payload(56), out var future));
        Assert.Equal(55, future.Count);
    }
}
