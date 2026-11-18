// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#pragma once
#include <cstddef>
#include <cstdint>
#include <limits>

namespace kcdmp::t3native {
constexpr size_t kCoreFields = 19, kCorpseFields = 5;
constexpr size_t kCoreBytes = 2 + 9 * kCoreFields, kP5Bytes = 2 + 9 * (kCoreFields + kCorpseFields);
inline size_t append_corpses(uint8_t* out, size_t used, size_t cap, const uint64_t* values) {
    if (!out || !values || used != kCoreBytes || cap < kP5Bytes || out[0] != 1 || out[1] != kCoreFields) return 0;
    for (size_t i = 0; i < kCoreFields; ++i) if (out[2 + 9*i] != i) return 0;
    const uint64_t limit = uint64_t((std::numeric_limits<int64_t>::max)());
    for (size_t i = 0; i < kCorpseFields; ++i) {
        out[used++] = static_cast<uint8_t>(kCoreFields + i);
        const uint64_t value = values[i] > limit ? limit : values[i];
        for (unsigned b = 0; b < 8; ++b) out[used++] = static_cast<uint8_t>(value >> (8*b));
    }
    out[1] = static_cast<uint8_t>(kCoreFields + kCorpseFields);
    return used;
}
} // namespace kcdmp::t3native
