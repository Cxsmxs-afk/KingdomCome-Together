// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#pragma once
#include <cstddef>
#include <cstdint>
#include <limits>

namespace kcdmp::t3native {
constexpr size_t kCoreFields = 19, kCorpseFields = 5, kRangedFields = 6;
constexpr size_t kRpMeasureFields = 17;
constexpr size_t kCoreBytes = 2 + 9 * kCoreFields, kP5Bytes = 2 + 9 * (kCoreFields + kCorpseFields);
constexpr size_t kP6Bytes = kP5Bytes + 9 * kRangedFields;
constexpr size_t kRpMeasureBytes = kP6Bytes + 9 * kRpMeasureFields;
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
} // namespace kcdmp::t3native
