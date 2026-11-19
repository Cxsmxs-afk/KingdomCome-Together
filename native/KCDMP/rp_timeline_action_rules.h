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
enum Receipt : uint8_t { Accepted = 0, Unavailable = 1, Invalid = 2, Blocked = 3, RowNotRegistered = 4 };

struct RangedHandFence {
    uint64_t peerSerial = 0;
    uint32_t bodyGeneration = 0, birth = 0;
    uint64_t revision = 0;

    bool valid() const { return peerSerial && birth && birth <= 0xFFFFFF && revision; }
    bool operator==(const RangedHandFence& other) const {
        return peerSerial == other.peerSerial && bodyGeneration == other.bodyGeneration && birth == other.birth && revision == other.revision;
    }
};

enum class HandSlot : uint8_t { Unknown, Left, Right };
constexpr uint8_t kRangedHandVersion = 2;
constexpr uint8_t kHandDrawn = 1, kHandOversized = 2;
constexpr size_t kRangedHandObserveSize = 47, kRangedHandDeclareSize = 60, kRangedHandReplySize = 37;
enum class HandOperation : uint8_t { Declare, Observe };

template <typename ItemClass>
bool class_in_confirmed_slot(HandSlot slot, uint8_t known, const ItemClass& left,
                             const ItemClass& right, const ItemClass& expected) {
    if (slot == HandSlot::Left) return (known & 1) && left == expected;
    if (slot == HandSlot::Right) return (known & 2) && right == expected;
    return false;
}

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

struct RangedHandRequest {
    HandOperation operation{};
    uint8_t peer = 0, flags = 0;
    uint32_t eid = 0, world = 0;
    uint64_t session = 0;
    RangedHandFence fence;
};

inline bool decode_ranged_hand_request(const uint8_t* body, size_t size, RangedHandRequest& request) {
    if (!body || size < kRangedHandObserveSize || body[0] != kRangedHandVersion ||
        body[1] > static_cast<uint8_t>(HandOperation::Observe)) return false;
    RangedHandRequest parsed;
    parsed.operation = static_cast<HandOperation>(body[1]);
    parsed.peer = body[2];
    parsed.eid = static_cast<uint32_t>(read_le(body + 3, 4));
    parsed.fence = {read_le(body + 7, 8), static_cast<uint32_t>(read_le(body + 15, 4)),
        static_cast<uint32_t>(read_le(body + 19, 4)), read_le(body + 23, 8)};
    if (!parsed.eid || !parsed.fence.valid()) return false;
    if (parsed.operation == HandOperation::Declare) {
        if (size != kRangedHandDeclareSize) return false;
        parsed.flags = body[47];
        parsed.world = static_cast<uint32_t>(read_le(body + 48, 4));
        parsed.session = read_le(body + 52, 8);
        if ((parsed.flags & ~(kHandDrawn | kHandOversized)) || !parsed.world) return false;
    } else if (size != kRangedHandObserveSize) return false;
    request = parsed;
    return true;
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
