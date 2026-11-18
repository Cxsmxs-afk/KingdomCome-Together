// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#pragma once

#include "holder_rules.h"

namespace kcdmp::motion::remote {

// Same short window as the existing jump's takeoff readback. One waiting
// receipt can give way to the Writer without becoming a second jump.
constexpr double kJumpHandoverS = 0.25;

inline bool stream_may_jump(holders::Holder owner) {
    return owner == holders::Holder::Writer || owner == holders::Holder::Figure;
}

struct JumpScope {
    const void* entity = nullptr;
    const void* actor = nullptr;
    uint64_t lifecycle = 0, connection = 0, life = 0;

    bool matches(const JumpScope& other) const {
        return entity && actor && connection && entity == other.entity && actor == other.actor &&
               lifecycle == other.lifecycle && connection == other.connection && life == other.life;
    }
};

enum class JumpDisposition { Expired, Wait, Submit };

inline JumpDisposition jump_after_handover(const JumpScope& scheduled, const JumpScope& current,
                                           double deadline, double now, holders::Holder owner) {
    if (now >= deadline || !scheduled.matches(current)) return JumpDisposition::Expired;
    if (owner == holders::Holder::Driver) return JumpDisposition::Wait;
    return stream_may_jump(owner) ? JumpDisposition::Submit : JumpDisposition::Expired;
}

} // namespace kcdmp::motion::remote
