// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;

namespace KcdMp.Wire;

/// <summary>Op250 with no arguments: independent native measurements, not the agent log mirror.
/// Version/count then field-id:u8/value:u64LE. Up to 56 fields fit the existing 512-byte reply budget.</summary>
public static class T3NativeCounters
{
    public const byte Version = 1;
    public const int MaxFields = 56;
    // IDs are append-only. Distances are rounded UP to whole metres; detailed
    // teleport logs preserve the actual coordinates and fractional distances.
    public static readonly string[] Keys = [
        "tp_total", "tp_by_source_leash", "tp_by_source_respawn", "tp_by_source_unstuck", "tp_by_source_join",
        "wake_dist_m_max", "wake_drop_m_max", "tp_blocked_combat", "tp_refused_stale", "tp_refused_drop",
        "death_cause_hit", "death_cause_fall", "death_cause_bleed", "death_cause_other", "falldeath_after_tp",
        "tp_by_source_leash_settle", "tp_by_source_video_landing", "tp_by_source_video_rollback", "tp_by_source_scene_nudge"
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
        return values.Count == Keys.Length;
    }
}
