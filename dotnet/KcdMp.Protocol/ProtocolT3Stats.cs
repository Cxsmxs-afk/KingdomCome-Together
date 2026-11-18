// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;

namespace KcdMp.Wire;

/// <summary>Op250 with no arguments: independent native measurements, not the agent log mirror.
/// Version/count then field-id:u8/value:u64LE. Up to 56 fields fit the existing 512-byte reply budget.</summary>
public static class T3NativeCounters
{
    public const byte Version = 1;
    public const int MaxFields = 56;
    public const int CoreFields = 19, CorpseFields = 5, RangedFields = 6, PresentationFields = 17, OwnerFields = 2, EdgeFields = 6;
    public const int PresentationStart = CoreFields + CorpseFields + RangedFields;
    public const int OwnerStart = PresentationStart + PresentationFields;
    public const int EdgeStart = OwnerStart + OwnerFields;
    // IDs are append-only. Distances are rounded UP to whole metres; detailed
    // teleport logs preserve the actual coordinates and fractional distances.
    public static readonly string[] Keys = [
        "tp_total", "tp_by_source_leash", "tp_by_source_respawn", "tp_by_source_unstuck", "tp_by_source_join",
        "wake_dist_m_max", "wake_drop_m_max", "tp_blocked_combat", "tp_refused_stale", "tp_refused_drop",
        "death_cause_hit", "death_cause_fall", "death_cause_bleed", "death_cause_other", "falldeath_after_tp",
        "tp_by_source_leash_settle", "tp_by_source_video_landing", "tp_by_source_video_rollback", "tp_by_source_scene_nudge",
        "corpses_visible", "corpse_hidden_blocked", "grave_markers_added", "grave_marker_fallbacks", "markers_off_session",
        "ranged_native_missing_actor", "ranged_native_unreadable_phase", "ranged_native_unknown_hand",
        "ranged_native_queue_overflow", "ranged_native_expired_rows", "ranged_native_lifecycle_dropped",
        "rp_e2e_ms_p50", "rp_e2e_ms_p95", "rp_e2e_ms_max", "rp_agent_hop_ms_p95",
        "rp_corr_n", "rp_corr_cm_p95", "rp_corr_cm_max", "rp_hard_place", "rp_step_ratio",
        "rp_action_lag_ms_p95", "owner_violations", "rp_e2e_n", "rp_agent_hop_n", "rp_action_n",
        "rp_step_n", "rp_pose_n", "rp_owner_n", "owner_handovers", "owner_denied_sets",
        "edge_rt_age_ms_p95", "edge_rt_age_ms_max", "edge_bulk_age_ms_p95", "pipe_busy_replies", "edge_rt_n", "edge_bulk_n"
    ];

    public static bool TryRead(ReadOnlySpan<byte> payload, out Dictionary<string, long> values)
    {
        values = new(StringComparer.Ordinal);
        if (payload.Length < 2 || payload[0] != Version || payload[1] > MaxFields || payload.Length != 2 + payload[1] * 9) return false;
        Span<bool> seen = stackalloc bool[256];
        seen.Clear();
        for (int at = 2; at < payload.Length; at += 9)
        {
            byte field = payload[at];
            if (seen[field]) return false;
            seen[field] = true;
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(payload[(at + 1)..]);
            if (field >= Keys.Length) continue; // bounded forward-compatible extension
            if (value > long.MaxValue) return false;
            values.Add(Keys[field], (long)value);
        }
        for (int i = 0; i < CoreFields; i++) if (!seen[i]) return false;
        int corpse = 0;
        for (int i = CoreFields; i < CoreFields + CorpseFields; i++) if (seen[i]) corpse++;
        int ranged = 0, presentation = 0, owners = 0, edge = 0;
        for (int i = CoreFields + CorpseFields; i < PresentationStart; i++) if (seen[i]) ranged++;
        for (int i = PresentationStart; i < OwnerStart; i++) if (seen[i]) presentation++;
        for (int i = OwnerStart; i < EdgeStart; i++) if (seen[i]) owners++;
        for (int i = EdgeStart; i < EdgeStart + EdgeFields; i++) if (seen[i]) edge++;
        // Older DLLs remain readable. Every extension is atomic and requires its prefix.
        return (corpse == 0 || corpse == CorpseFields) && (ranged == 0 || ranged == RangedFields) &&
            (ranged == 0 || corpse == CorpseFields) && (presentation == 0 || presentation == PresentationFields) &&
            (presentation == 0 || ranged == RangedFields) && (owners == 0 || owners == OwnerFields) &&
            (owners == 0 || presentation == PresentationFields) && (edge == 0 || edge == EdgeFields) &&
            (edge == 0 || owners == OwnerFields);
    }
}
