// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#pragma once

#include <cstdint>

namespace kcdmp::visualdamage {

struct Identity {
    uint32_t eid = 0;
    const void* actor = nullptr;
};

inline bool same(Identity a, Identity b) {
    return a.eid && a.actor && a.eid == b.eid && a.actor == b.actor;
}

// The existing authority-selected replay may enter the engine once with the
// exact combat actor and victim, on the thread that owns this ticket.
struct ReplayPermit {
    uint32_t attacker = 0, victim = 0;
    const void* combat = nullptr;
    bool used = false;

    bool take(Identity proposing, uint32_t target, const void* engineCombat) {
        if (used || !combat || !target || !proposing.eid || combat != engineCombat || attacker != proposing.eid || victim != target)
            return false;
        used = true;
        return true;
    }
};

enum class Contact { Ordinary, Authorized, Suppressed };

inline Contact decide(Identity registered, Identity proposing, uint32_t victim, const void* combat, ReplayPermit& replay) {
    if (!same(registered, proposing)) return Contact::Ordinary;
    return replay.take(proposing, victim, combat) ? Contact::Authorized : Contact::Suppressed;
}

} // namespace kcdmp::visualdamage
