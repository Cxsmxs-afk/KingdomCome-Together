// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#pragma once

#include "rp_timeline_action_rules.h"

#include <array>
#include <cstring>
#include <deque>
#include <optional>
#include <utility>

namespace kcdmp::rpcombat {

constexpr uint8_t kSwing = 1, kBlock = 6, kDodge = 7, kKo = 0x81, kRanged = 0x82,
                  kParry = 0x83, kHit = 0x84, kRecoil = 0x85;
constexpr size_t kRegistrationHeader = 10, kActionHeader = timelineactions::kHeader;
constexpr uint8_t kRangedProtocolVersion = 3;
constexpr size_t kRangedPayloadSize = 43;

inline bool uses_row(uint8_t kind) {
    return kind == kSwing || kind == kBlock || kind == kDodge || kind == kParry || kind == kHit || kind == kRecoil;
}

struct Registration {
    uint8_t peer = 0, kind = 0;
    uint32_t eid = 0, rowKey = 0;
    std::string_view spec;
};

inline bool decode_registration(const uint8_t* body, size_t n, Registration& out) {
    using timelineactions::read_le;
    if (!body || n <= kRegistrationHeader || n > kRegistrationHeader + timelineactions::kMaxSpec) return false;
    Registration r{body[0], body[5], static_cast<uint32_t>(read_le(body + 1, 4)), static_cast<uint32_t>(read_le(body + 6, 4)),
                   {reinterpret_cast<const char*>(body + kRegistrationHeader), n - kRegistrationHeader}};
    if (!r.eid || !r.rowKey || !uses_row(r.kind) || r.spec.find('\0') != std::string_view::npos) return false;
    out = r;
    return true;
}

struct Event {
    uint8_t peer = 0, kind = 0, phase = 0, stage = 0; // KO level or ranged weapon kind
    uint32_t eid = 0, rowKey = 0;
    uint64_t hostMs = 0;
    uint16_t sequence = 0;
    uint8_t rangedPhase = 0;
    std::array<uint8_t, 16> itemClass{};
    timelineactions::RangedHandFence rangedHand{};
};

inline bool decode_event(const uint8_t* body, size_t n, Event& out) {
    using timelineactions::read_le;
    if (!body || n <= kActionHeader) return false;
    Event e;
    e.peer = body[0];
    e.eid = static_cast<uint32_t>(read_le(body + 1, 4));
    e.hostMs = read_le(body + 5, 8);
    e.sequence = static_cast<uint16_t>(read_le(body + 13, 2));
    e.kind = body[15]; e.phase = body[16];
    if (!e.eid || !e.hostMs || e.phase != timelineactions::kCommit) return false;
    const auto* payload = body + kActionHeader;
    const size_t size = n - kActionHeader;
    if (uses_row(e.kind)) {
        if (size != 4) return false;
        e.rowKey = static_cast<uint32_t>(read_le(payload, 4));
        if (!e.rowKey) return false;
    } else if (e.kind == kKo) {
        if (size != 1 || payload[0] > 1) return false;
        e.stage = payload[0];
    } else if (e.kind == kRanged) {
        if (size != kRangedPayloadSize || payload[0] != kRangedProtocolVersion) return false;
        e.stage = payload[1]; e.rangedPhase = payload[2];
        if ((e.stage != 1 && e.stage != 2) || e.rangedPhase < 3 || e.rangedPhase > 5 ||
            (e.rangedPhase == 4 && e.stage != 1)) return false;
        std::memcpy(e.itemClass.data(), payload + 3, e.itemClass.size());
        e.rangedHand = {read_le(payload + 19, 8), static_cast<uint32_t>(read_le(payload + 27, 4)),
            static_cast<uint32_t>(read_le(payload + 31, 4)), read_le(payload + 35, 8)};
        bool nonempty = false;
        for (const uint8_t byte : e.itemClass) nonempty |= byte != 0;
        if (!nonempty || !e.rangedHand.valid()) return false;
    } else return false;
    out = e;
    return true;
}

struct EventKey {
    uint64_t hostMs = 0;
    uint16_t sequence = 0;
    uint8_t kind = 0, phase = 0;
    bool operator==(EventKey other) const {
        return hostMs == other.hostMs && sequence == other.sequence && kind == other.kind && phase == other.phase;
    }
};

inline EventKey key(const Event& e) { return {e.hostMs, e.sequence, e.kind, e.phase}; }

// Keep accepted IDs while their stamps could still be rendered. When full,
// reject new entries instead of making an already executed event reusable.
constexpr uint64_t kReplayMs = 3000;
inline bool stale(uint64_t actionHostMs, uint64_t targetHostMs) {
    return targetHostMs > actionHostMs && targetHostMs - actionHostMs > kReplayMs;
}

class Receipts {
    static constexpr size_t kLimit = 256;
    std::deque<std::pair<EventKey, timelineactions::Receipt>> entries_;
    uint64_t latestTarget_ = 0;

public:
    void observe_target(uint64_t targetHostMs) {
        if (targetHostMs <= latestTarget_) return;
        latestTarget_ = targetHostMs;
        for (auto it = entries_.begin(); it != entries_.end();) {
            if (stale(it->first.hostMs, latestTarget_)) it = entries_.erase(it);
            else ++it;
        }
    }

    bool too_old(EventKey event) const { return stale(event.hostMs, latestTarget_); }
    bool full() const { return entries_.size() >= kLimit; }

    std::optional<timelineactions::Receipt> find(EventKey key) const {
        for (const auto& entry : entries_)
            if (entry.first == key) return entry.second;
        return std::nullopt;
    }

    timelineactions::Receipt remember(EventKey key, timelineactions::Receipt receipt) {
        if (full()) return timelineactions::Blocked;
        entries_.emplace_back(key, receipt);
        return receipt;
    }
};

enum class Schedule { Wait, Run, Expire };

inline Schedule schedule(uint64_t actionHostMs, uint64_t poseHostMs, uint64_t targetHostMs,
                         uint64_t readMs, uint64_t nowMs, bool trackedWake) {
    using timelineactions::due;
    using timelineactions::waited;
    if (stale(actionHostMs, targetHostMs) && !trackedWake) return Schedule::Expire;
    if (due(actionHostMs, poseHostMs, targetHostMs) || (trackedWake && targetHostMs >= actionHostMs))
        return Schedule::Run;
    if (!waited(readMs, nowMs)) return Schedule::Wait;
    if (targetHostMs >= actionHostMs || trackedWake) return Schedule::Run;
    return Schedule::Expire;
}

// A confirmed engine request contributes to the ratio; a queue ACK cannot.
inline bool visible_target(uint64_t installed, uint64_t received) {
    if (!received || installed > received) return false;
    const uint64_t required = (received / 20) * 19 + ((received % 20) * 19 + 19) / 20;
    return installed >= required;
}

} // namespace kcdmp::rpcombat
