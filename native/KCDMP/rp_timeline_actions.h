// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#pragma once

#include "rp_timeline_action_rules.h"

namespace kcdmp::timelineactions {

// Main thread only. The edge reply proves admission before the native action
// is due. Body identity and scope are checked again before any engine request.
struct Scope {
    uint64_t lifecycle = 0, connection = 0, life = 0;
    bool operator==(Scope other) const {
        return lifecycle == other.lifecycle && connection == other.connection && life == other.life;
    }
};

void configure(bool enabled);
void reset();
void forget(uint32_t eid);
Receipt submit(const uint8_t* bytes, size_t size, Scope received, uint64_t readMs);
Receipt register_combat(const uint8_t* bytes, size_t size, Scope received);
Receipt submit_combat(const uint8_t* bytes, size_t size, Scope received, uint64_t readMs);
void tick();
void observed(uint64_t token, bool installed);

} // namespace kcdmp::timelineactions
