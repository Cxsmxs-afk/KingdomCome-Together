// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#pragma once
// The bow/crossbow aim that rides the existing 12-byte body block.
// Phase 255 (the step reader failed) must not become a drawn-or-idle zero.
// Charge here is the already-read shooting phase, not a measured draw percent:
// aiming is full, charging is half. Yaw and pitch stay at the last verified
// sample; this build has no verified angle source, so they stay 0 and the log says so.
#include <atomic>
#include <cstdint>

namespace kcdmp::rangedaim {

enum Phase : uint8_t {
    None = 0, Charging = 1, Aiming = 2, Firing = 3, Reloading = 4,
    Uncharging = 5, Leaving = 6, Unreadable = 255
};

enum class Edge : uint8_t { Same, Held, Ended, Ignored };

struct Block {
    bool aim = false;
    uint8_t charge = 0;
    int16_t yaw = 0;
    int16_t pitch = 0;
    bool confirmedEnd = false;
};

inline bool is_hold_phase(uint8_t phase) { return phase == Charging || phase == Aiming; }

inline Edge apply(uint8_t phase, Block* held) {
    if (!held || phase == Unreadable) return Edge::Ignored;
    if (is_hold_phase(phase)) {
        const uint8_t charge = phase == Aiming ? 255 : 128;
        const bool changed = !held->aim || held->charge != charge;
        held->aim = true;
        held->charge = charge;
        held->confirmedEnd = false;
        return changed ? Edge::Held : Edge::Same;
    }
    if (!held->aim && held->charge == 0) return Edge::Same;
    held->aim = false;
    held->charge = 0;
    held->yaw = 0;
    held->pitch = 0;
    held->confirmedEnd = true;
    return Edge::Ended;
}

inline bool restore_after_end(uint64_t endStamp, uint64_t packetStamp) { return packetStamp > endStamp; }

inline std::atomic<uint8_t>& slot() {
    static std::atomic<uint8_t> phase{None};
    return phase;
}

inline void publish(uint8_t phase) { slot().store(phase, std::memory_order_release); }
inline uint8_t published() { return slot().load(std::memory_order_acquire); }

} // namespace kcdmp::rangedaim
