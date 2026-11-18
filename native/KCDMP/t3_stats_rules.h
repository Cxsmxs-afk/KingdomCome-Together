// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#pragma once
#include <cstddef>
#include <cstdint>
#include <limits>

namespace kcdmp::t3stats {
constexpr uint8_t kOp = 250;
constexpr size_t kMaxText = 4096;

// Agent-origin numeric counters only: [role 1 host / 2 joiner][key=uint ...].
// Never accept formatting/control characters into the DLL's log line.
inline bool valid(const uint8_t* body, size_t len) {
    if (!body || len < 4 || len > kMaxText + 1 || (body[0] != 1 && body[0] != 2)) return false;
    size_t i = 1, fields = 0;
    while (i < len) {
        size_t key = 0;
        if (!(body[i] >= 'a' && body[i] <= 'z')) return false;
        while (i < len && ((body[i] >= 'a' && body[i] <= 'z') || (body[i] >= '0' && body[i] <= '9') || body[i] == '_')) {
            ++i; if (++key > 48) return false;
        }
        if (i >= len || body[i++] != '=') return false;
        size_t digits = 0;
        uint64_t value = 0;
        while (i < len && body[i] >= '0' && body[i] <= '9') {
            const uint64_t digit = body[i++] - '0';
            if (value > ((std::numeric_limits<uint64_t>::max)() - digit) / 10 || ++digits > 20) return false;
            value = value * 10 + digit;
        }
        if (!digits || ++fields > 128) return false;
        if (i == len) return true;
        if (body[i++] != ' ' || i == len) return false;
    }
    return false;
}
} // namespace kcdmp::t3stats
