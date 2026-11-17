// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#pragma once
#include <cmath>
#include <cstdlib>
#include <cstdint>

namespace kcdmp::calendarfollow {
constexpr int64_t kMaxWorldMs = int64_t(0xFFFFFFFFu) * 1000;
constexpr uint64_t kRunningFreshMs = 10000;
constexpr int64_t kReadbackToleranceMs = 1000;
inline bool valid(int64_t ms, double ratio) {
    return ms >= 0 && ms <= kMaxWorldMs && std::isfinite(ratio) && ratio >= 0.0 && ratio <= 15.0;
}
// Calendar game seconds / real seconds; never the world's simulation timescale.
inline bool target(int64_t ms, double ratio, uint64_t at, uint64_t now, int64_t* out) {
    if (!valid(ms, ratio) || now < at || (ratio != 0 && now - at > kRunningFreshMs)) return false;
    const double t = double(ms) + double(now - at) * ratio;
    if (!std::isfinite(t) || t > double(kMaxWorldMs)) return false;
    *out = static_cast<int64_t>(t);
    return true;
}
inline bool may_write(int skipState) { return skipState == 0; }
inline bool agrees(int64_t actual, int64_t expected) {
    return actual >= 0 && actual <= kMaxWorldMs && std::llabs(actual - expected) <= kReadbackToleranceMs;
}
struct Follower {
    bool on = false, verified = false;
    int64_t ms = 0, lastWritten = 0;
    double ratio = 0;
    uint64_t at = 0, frames = 0;
    void* lastCalendar = nullptr;
};
// The production memory path, with injectable accessors for engine-free tests.
// The caller supplies the current Calendar each time and serializes reset/update/apply.
template<class Read, class Write>
bool apply(Follower& s, void* cal, uint64_t now, int skipState, Read read, Write write, int64_t* before) {
    int64_t to = 0, after = 0;
    if (!s.on || !cal || !may_write(skipState) || !target(s.ms, s.ratio, s.at, now, &to) ||
        !read(cal, before) || !write(cal, to) || !read(cal, &after) || after != to) {
        s.verified = false; return false;
    }
    s.verified = true; s.lastCalendar = cal; s.lastWritten = after; ++s.frames;
    return true;
}
inline bool verify(const Follower& s, void* cal, int64_t current, uint64_t now, int skipState) {
    int64_t to = 0;
    return s.on && s.verified && cal && cal == s.lastCalendar && may_write(skipState) &&
        target(s.ms, s.ratio, s.at, now, &to) && agrees(current, to);
}
} // namespace kcdmp::calendarfollow
