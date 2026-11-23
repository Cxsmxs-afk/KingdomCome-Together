// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#pragma once
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <limits>
#include <optional>

namespace kcdmp::rangedaim {

constexpr uint8_t kHeldBit = 0x08;
constexpr uint8_t kChargeKnownBit = 0x80;
constexpr int16_t kAngleUnavailable = (std::numeric_limits<int16_t>::min)();
constexpr int kYawLimit = 18000, kPitchLimit = 9000; // centidegrees
constexpr uint64_t kAngleBlendGapMs = 150;

struct Angles {
    int16_t yaw = kAngleUnavailable, pitch = kAngleUnavailable;
    bool known() const { return std::abs(int(yaw)) <= kYawLimit && std::abs(int(pitch)) <= kPitchLimit; }
    bool operator!=(const Angles& other) const { return yaw != other.yaw || pitch != other.pitch; }
};

inline Angles camera_angles(const float matrix[12]) {
    // CryEngine Matrix34 is row-major and the view faces its +Y column.
    const double x = matrix[1], y = matrix[5], z = matrix[9];
    const double ground = x * x + y * y, norm = ground + z * z;
    if (!std::isfinite(norm) || norm < 0.9 || norm > 1.1 || ground < 0.000001) return {};
    constexpr double degrees100PerRadian = 18000.0 / 3.14159265358979323846;
    return {static_cast<int16_t>(std::lround(std::atan2(-x, y) * degrees100PerRadian)),
            static_cast<int16_t>(std::lround(std::atan2(z, std::sqrt(ground)) * degrees100PerRadian))};
}

enum Phase : uint8_t {
    None = 0, Charging = 1, Aiming = 2, Firing = 3, Reloading = 4,
    Uncharging = 5, Leaving = 6, Unreadable = 255
};

enum class Edge : uint8_t { Same, Held, Ended, Ignored };

struct Block { bool aim = false; std::optional<uint8_t> charge; bool observed = false; Angles angles{}; };
struct Sample { uint8_t phase = Unreadable; std::optional<uint8_t> charge; Angles angles{}; };

// Carries one confirmed observation independently of the normal body block.
// hostMs is zero for an omitted or unreadable ranged sample.
struct TimedState {
    uint64_t hostMs = 0;
    uint8_t bits = 0, charge = 0;
    int16_t yaw = 0, pitch = 0;
    bool held() const { return hostMs && (bits & kHeldBit) != 0; }
    bool angles_known() const { return held() && Angles{yaw, pitch}.known(); }
};

inline TimedState decode_state(const uint8_t* body, uint64_t stamp) {
    const uint8_t bits = body[3] & (kHeldBit | kChargeKnownBit);
    const uint8_t charge = body[7];
    const auto yaw = static_cast<int16_t>(body[8] | (body[9] << 8));
    const auto pitch = static_cast<int16_t>(body[10] | (body[11] << 8));
    if (bits & kHeldBit) {
        const Angles measured{yaw, pitch};
        const auto angles = measured.known() ? measured : Angles{};
        return {stamp, bits, charge, angles.yaw, angles.pitch};
    }
    if (!bits && !charge && yaw == kAngleUnavailable && pitch == kAngleUnavailable)
        return {stamp, 0, 0, yaw, pitch};
    return {};
}

inline void smooth_angles(TimedState& first, const TimedState& second, uint64_t targetHostMs) {
    if (!first.angles_known() || !second.angles_known() || second.hostMs <= first.hostMs ||
        targetHostMs <= first.hostMs || second.hostMs - first.hostMs > kAngleBlendGapMs ||
        (first.yaw == second.yaw && first.pitch == second.pitch)) return;
    constexpr int fullTurn = 2 * kYawLimit;
    int delta = int(second.yaw) - int(first.yaw);
    if (delta > kYawLimit) delta -= fullTurn;
    if (delta < -kYawLimit) delta += fullTurn;
    const double progress = double((std::min)(targetHostMs, second.hostMs) - first.hostMs) / double(second.hostMs - first.hostMs);
    int yaw = int(first.yaw) + static_cast<int>(std::lround(delta * progress));
    if (yaw > kYawLimit) yaw -= fullTurn;
    if (yaw < -kYawLimit) yaw += fullTurn;
    first.yaw = static_cast<int16_t>(yaw);
    first.pitch = static_cast<int16_t>(int(first.pitch) + std::lround((int(second.pitch) - int(first.pitch)) * progress));
}

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
        const auto angles = sample.angles.known() ? sample.angles : held.angles;
        const bool changed = !held.aim || held.charge != charge || held.angles != angles;
        held = {true, charge, true, angles};
        return changed ? Edge::Held : Edge::Same;
    }
    const bool wasHeld = held.aim;
    held = {false, {}, true};
    return wasHeld ? Edge::Ended : Edge::Same;
}

} // namespace kcdmp::rangedaim
