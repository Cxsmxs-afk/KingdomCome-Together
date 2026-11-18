// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#pragma once

#include <cstdint>

namespace kcdmp::rpbodyactions {

constexpr uint8_t kBow = 1, kCrossbow = 2;
constexpr uint8_t kFire = 3, kLoad = 4, kUncharge = 5;

// P6 only proved these visual fragments. Crossbow reload needs a mechanism-
// specific fragment; the old goatsfoot animation did not prove the others.
inline const char* ranged_fragment(uint8_t weapon, uint8_t phase) {
    if (weapon != kBow && weapon != kCrossbow) return nullptr;
    switch (phase) {
    case kFire: return "Fire";
    case kLoad: return weapon == kBow ? "Load" : nullptr;
    case kUncharge: return "Uncharge";
    default: return nullptr;
    }
}

inline bool ranged_completion_confirmed(bool done, uint8_t result, int request, uint32_t completed) {
    return done && result == 2 && request >= 0 && static_cast<uint32_t>(request) == completed;
}

inline bool down_confirmed(bool unconscious, bool physicsPresent, bool living) {
    return unconscious && physicsPresent && !living;
}

inline bool up_confirmed(bool unconscious, bool physicsPresent, bool living, bool grounded, bool ragdoll) {
    return !unconscious && physicsPresent && living && grounded && !ragdoll;
}

} // namespace kcdmp::rpbodyactions
