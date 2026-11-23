// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#pragma once

#include "load_sequence_rules.h"

#include <array>
#include <cstddef>
#include <cstdint>

namespace kcdmp::dialogvote {

constexpr uint8_t kOp = 92, kTyped = 255, kClear = 255, kRows = 8;
constexpr size_t kTokenBytes = 32, kBytes = 7 + kTokenBytes;
enum Result : uint8_t { Submitted = 0, ScopeChanged = 4, NotEntered = 5, Uncertain = 6 };

struct SessionScope {
    uint64_t connection = 0;
    uint64_t load = 0;
    bool live() const { return connection != 0; }
    bool operator==(const SessionScope& other) const { return connection == other.connection && load == other.load; }
};

inline SessionScope observed_scope(uint64_t connection, uint64_t load) {
    return connection && loadsequence::allowed(load, load, loadsequence::Access::Live)
        ? SessionScope{connection, load} : SessionScope{};
}

struct Request {
    uint8_t index = kClear;
    uint32_t snapshot = 0;
    std::array<char, kTokenBytes + 1> token{};
};

inline bool token_character(uint8_t c) {
    return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
}

inline bool typed(const uint8_t* body, size_t size) {
    return body && size == kBytes && body[0] == kOp && body[2] == kTyped;
}

inline bool decode(const uint8_t* body, size_t size, Request& out) {
    if (!typed(body, size) || (body[1] >= kRows && body[1] != kClear)) return false;
    Request req;
    req.index = body[1];
    for (size_t i = 0; i < 4; ++i) req.snapshot |= uint32_t(body[3 + i]) << (i * 8);
    if (!req.snapshot) return false;
    for (size_t i = 0; i < kTokenBytes; ++i) {
        if (!token_character(body[7 + i])) return false;
        req.token[i] = static_cast<char>(body[7 + i]);
    }
    out = req;
    return true;
}

inline bool matches(const Request& request, uint32_t snapshot, int rows, bool hudCurrent) {
    return hudCurrent && request.snapshot == snapshot && rows > 0 &&
           (request.index == kClear || request.index < rows);
}

} // namespace kcdmp::dialogvote
