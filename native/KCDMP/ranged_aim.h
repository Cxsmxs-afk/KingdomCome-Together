// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#pragma once
#include <cmath>
#include <cstdint>
#include <limits>
#include <optional>

namespace kcdmp::rangedaim {

constexpr uint8_t kHeldBit = 0x08;
constexpr uint8_t kChargeKnownBit = 0x80;
constexpr int16_t kAngleUnavailable = (std::numeric_limits<int16_t>::min)();

enum Phase : uint8_t {
    None = 0, Charging = 1, Aiming = 2, Firing = 3, Reloading = 4,
    Uncharging = 5, Leaving = 6, Unreadable = 255
};

enum class Edge : uint8_t { Same, Held, Ended, Ignored };

struct Block { bool aim = false; std::optional<uint8_t> charge; };
struct Sample { uint8_t phase = Unreadable; std::optional<uint8_t> charge; };

inline bool is_hold_phase(uint8_t phase) { return phase == Charging || phase == Aiming; }

inline std::optional<uint8_t> quantize_charge(float charge, float min, float max) {
    if (!std::isfinite(charge) || !std::isfinite(min) || !std::isfinite(max) ||
        min < 0 || max > 1 || max <= min || charge < min || charge > max) return std::nullopt;
    return static_cast<uint8_t>(std::lround((charge - min) / (max - min) * 255.f));
}

inline Edge apply(const Sample& sample, Block& held) {
    if (sample.phase == Unreadable) return Edge::Ignored;
    if (is_hold_phase(sample.phase)) {
        const auto charge = sample.charge ? sample.charge : held.charge;
        const bool changed = !held.aim || held.charge != charge;
        held = {true, charge};
        return changed ? Edge::Held : Edge::Same;
    }
    if (!held.aim) return Edge::Same;
    held = {};
    return Edge::Ended;
}

} // namespace kcdmp::rangedaim
