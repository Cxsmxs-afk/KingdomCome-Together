// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#pragma once
#include <cstddef>
#include <cstdint>
#include <limits>

namespace kcdmp::t3native {
constexpr size_t kCoreFields = 19, kCorpseFields = 5, kRangedFields = 6;
constexpr size_t kRpMeasureFields = 17, kOwnerFields = 2;
constexpr size_t kEdgeFields = 6;
constexpr size_t kTimelineFields = 6, kTimelineBytes = 2 + 9 * kTimelineFields;
constexpr size_t kControllerFields = 14, kControllerBytes = 2 + 9 * kControllerFields;
constexpr size_t kCombatFields = 10, kCombatBytes = 2 + 9 * kCombatFields;
constexpr size_t kLoadFields = 11, kLoadBytes = 2 + 9 * kLoadFields;
constexpr size_t kRpOwnerViolationsIndex = 10, kRpOwnerChecksIndex = 16;
constexpr size_t kCoreBytes = 2 + 9 * kCoreFields, kP5Bytes = 2 + 9 * (kCoreFields + kCorpseFields);
constexpr size_t kP6Bytes = kP5Bytes + 9 * kRangedFields;
constexpr size_t kRpMeasureBytes = kP6Bytes + 9 * kRpMeasureFields;
constexpr size_t kOwnerBytes = kRpMeasureBytes + 9 * kOwnerFields;
constexpr size_t kEdgeBytes = kOwnerBytes + 9 * kEdgeFields;
static_assert(kEdgeBytes <= 512);
inline size_t append_group(uint8_t* out, size_t used, size_t cap, size_t prefix, const uint64_t* values, size_t count) {
    if (!out || !values || used != 2 + 9 * prefix || cap < used + 9 * count || out[0] != 1 || out[1] != prefix) return 0;
    for (size_t i = 0; i < prefix; ++i) if (out[2 + 9*i] != i) return 0;
    const uint64_t limit = uint64_t((std::numeric_limits<int64_t>::max)());
    for (size_t i = 0; i < count; ++i) {
        out[used++] = static_cast<uint8_t>(prefix + i);
        const uint64_t value = values[i] > limit ? limit : values[i];
        for (unsigned b = 0; b < 8; ++b) out[used++] = static_cast<uint8_t>(value >> (8*b));
    }
    out[1] = static_cast<uint8_t>(prefix + count);
    return used;
}
inline size_t append_corpses(uint8_t* out, size_t used, size_t cap, const uint64_t* values) {
    return append_group(out, used, cap, kCoreFields, values, kCorpseFields);
}
inline size_t append_ranged(uint8_t* out, size_t used, size_t cap, const uint64_t* values) {
    return append_group(out, used, cap, kCoreFields + kCorpseFields, values, kRangedFields);
}
inline size_t append_rp_measure(uint8_t* out, size_t used, size_t cap, const uint64_t* values) {
    return append_group(out, used, cap, kCoreFields + kCorpseFields + kRangedFields, values, kRpMeasureFields);
}
inline size_t append_owners(uint8_t* out, size_t used, size_t cap, const uint64_t* values) {
    return append_group(out, used, cap, kCoreFields + kCorpseFields + kRangedFields + kRpMeasureFields, values, kOwnerFields);
}
inline size_t append_edge(uint8_t* out, size_t used, size_t cap, const uint64_t* values) {
    return append_group(out, used, cap, kCoreFields + kCorpseFields + kRangedFields + kRpMeasureFields + kOwnerFields, values, kEdgeFields);
}
// Each Op250 subcommand page retains its own set of counter IDs.
inline size_t write_page(uint8_t* out, size_t cap, const uint64_t* values, size_t fields) {
    if (!out || !values || cap < 2 + 9 * fields) return 0;
    out[0] = 1;
    out[1] = 0;
    return append_group(out, 2, cap, 0, values, fields);
}
inline size_t write_timeline(uint8_t* out, size_t cap, const uint64_t* values) {
    return write_page(out, cap, values, kTimelineFields);
}
inline size_t write_controller(uint8_t* out, size_t cap, const uint64_t* values) {
    return write_page(out, cap, values, kControllerFields);
}
inline size_t write_combat(uint8_t* out, size_t cap, const uint64_t* values) {
    return write_page(out, cap, values, kCombatFields);
}
inline size_t write_load(uint8_t* out, size_t cap, const uint64_t* values) {
    return write_page(out, cap, values, kLoadFields);
}
} // namespace kcdmp::t3native
