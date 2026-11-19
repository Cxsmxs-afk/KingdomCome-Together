// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#pragma once
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include "damage_invocation.h"
// WO-155: the engine-free rule of the friendly-fire knockdown window (hits.cpp). native/tests/wo155_rules_tests.cpp
// pins it.
//
// A friendly-fire hit reaches the victim as the game's scripted hit (TakeDamage with two arguments), which knocks
// a player down whenever he is not in a combat stance (WO-151 1.1). It keeps doing so, as in 0.45.0 -- but a
// stun-lock is no fun: while he is on the ground, and for kWindowS after the hit that knocked him down, the next
// hits deal their damage through the four-argument call with SuppressHitReaction (health and stamina exact, no
// second fall).

namespace kcdmp::wo155rules {

// Native local-pipe notice 0x97. Times share the Windows boot clock with the
// Agent. The native life and measurement identifier bind the observed event.
struct Observation {
    uint64_t capturedTick = 0, measuredTick = 0, sourceLifeProof = 0, measurementId = 0;
};

constexpr size_t kNoticeBytes = 46;
inline std::array<uint8_t, kNoticeBytes> encode_notice(uint32_t victim, float stamina, float health,
                                                      uint8_t flags, uint8_t material, const Observation& observation) {
    std::array<uint8_t, kNoticeBytes> body{};
    std::memcpy(body.data(), &victim, 4);
    std::memcpy(body.data() + 4, &stamina, 4);
    std::memcpy(body.data() + 8, &health, 4);
    body[12] = flags;
    body[13] = material;
    std::memcpy(body.data() + 14, &observation.capturedTick, 8);
    std::memcpy(body.data() + 22, &observation.measuredTick, 8);
    std::memcpy(body.data() + 30, &observation.sourceLifeProof, 8);
    std::memcpy(body.data() + 38, &observation.measurementId, 8);
    return body;
}

struct Recipient {
    uint64_t generation;
    uint64_t life;
    uint64_t connection;
    bool gameplay;
    uint64_t proof = 0;
    bool matches(Recipient current, uint64_t readTick) const {
        return generation && connection && gameplay && current.gameplay &&
            generation == current.generation && life == current.life &&
            connection == current.connection && readTick > life &&
            (!proof || proof == current.proof);
    }
};

enum class HitResult : uint8_t {
    Refused, Acknowledged, ProtectedConsumed, DispatchUnconfirmed, RecipientRetired, EffectObserved
};

template<class Invoke>
HitResult dispatch(bool protectedPlayer, bool suppress, Invoke invoke) {
    if (protectedPlayer) return HitResult::ProtectedConsumed;
    using rttr::DamageInvocation;
    auto result = suppress ? invoke(true) : DamageInvocation::NotInvoked;
    if (result == DamageInvocation::NotInvoked) result = invoke(false);
    if (result == DamageInvocation::Acknowledged) return HitResult::Acknowledged;
    if (result == DamageInvocation::Unconfirmed) return HitResult::DispatchUnconfirmed;
    return HitResult::Refused;
}

constexpr double kWindowS = 5.0;       // after a knocking hit, no new knockdown from friendly fire
constexpr double kSettleS = 1.5;       // a knockdown shows as a down edge this soon after the hit or not at all

struct FfWindow {
    double lastKnockAt = -1e9;         // the last friendly-fire hit let through to knock him down
    bool   armed = false;              // a down edge was seen for that hit: the window is real

    // One friendly-fire hit at `now`. `downNow` = his body is down right now; `lastDownEdgeAt` = when the last
    // down edge (up -> down) was seen (-1e9 = never). True = the hit goes in suppressed (no knockdown).
    bool suppress(double now, bool downNow, double lastDownEdgeAt) {
        if (downNow) return true;                                   // already on the ground: never a second fall
        // did the previous knocking hit really knock him down? (a down edge after it, within kSettleS)
        if (!armed && lastDownEdgeAt > -1e8 && lastDownEdgeAt >= lastKnockAt && lastDownEdgeAt - lastKnockAt <= kSettleS) armed = true;
        if (armed && now - lastKnockAt < kWindowS) return true;    // inside the window of a knockdown
        // this hit may knock him down: it starts a new window (armed only once a down edge confirms it)
        lastKnockAt = now;
        armed = false;
        return false;
    }
};

} // namespace kcdmp::wo155rules
