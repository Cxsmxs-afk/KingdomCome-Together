// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#pragma once

#include <cstddef>
#include <cstdint>
#include <string_view>

namespace kcdmp::timelineactions {

constexpr size_t kHeader = 17, kMaxSpec = 191;
constexpr uint8_t kAttack = 1, kBlockImpulse = 6, kDodge = 7, kCommit = 1;
// the existing SwingInbox spawn grace. If an accepted action still cannot
// find a timed pose by then, it follows its ordinary guarded presentation.
constexpr uint64_t kPoseWaitMs = 750;
enum Receipt : uint8_t { Accepted = 0, Unavailable = 1, Invalid = 2 };

struct Action {
    uint8_t peer = 0, kind = 0, phase = 0;
    uint32_t eid = 0;
    uint64_t hostMs = 0;
    uint16_t sequence = 0;
    std::string_view spec;
};

inline uint64_t read_le(const uint8_t* p, unsigned n) {
    uint64_t value = 0;
    for (unsigned i = 0; i < n; ++i) value |= static_cast<uint64_t>(p[i]) << (8 * i);
    return value;
}

inline bool decode(const uint8_t* body, size_t size, Action& action) {
    if (!body || size <= kHeader || size > kHeader + kMaxSpec) return false;
    const Action parsed{body[0], body[15], body[16], static_cast<uint32_t>(read_le(body + 1, 4)), read_le(body + 5, 8),
                        static_cast<uint16_t>(read_le(body + 13, 2)),
                        {reinterpret_cast<const char*>(body + kHeader), size - kHeader}};
    if (!parsed.eid || !parsed.hostMs || parsed.phase != kCommit ||
        (parsed.kind != kAttack && parsed.kind != kBlockImpulse && parsed.kind != kDodge) ||
        parsed.spec.find('\0') != std::string_view::npos) return false;
    action = parsed;
    return true;
}

inline bool due(uint64_t actionHostMs, uint64_t poseHostMs, uint64_t targetHostMs) {
    return poseHostMs >= actionHostMs && targetHostMs >= actionHostMs;
}

inline bool waited(uint64_t readMs, uint64_t nowMs) {
    return nowMs >= readMs && nowMs - readMs >= kPoseWaitMs;
}

inline uint64_t skew(uint64_t actionHostMs, uint64_t poseHostMs) {
    return actionHostMs > poseHostMs ? actionHostMs - poseHostMs : poseHostMs - actionHostMs;
}

} // namespace kcdmp::timelineactions
