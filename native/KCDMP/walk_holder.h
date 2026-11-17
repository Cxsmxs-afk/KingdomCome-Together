// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#pragma once
#include <cmath>
// One holder for a walking person (phase 1). The engine's per-frame movement
// request (body_drive) is that holder. The route (WO-215) and pose extrapolation
// were a second guess of the same body: with the route on, the joiner crashed,
// and extrapolation pulled the place the request was steering toward.
//
// Figures, animals, riders, bodies in the air and fights stay with the writer.
// The request cannot do them, and the route may still be asked about a horse.

namespace kcdmp::walkhold {

constexpr float kAirVzMps = 1.5f;

/// A person on the ground the driver is allowed to walk. Matches body_drive's
/// early refusals (figure, beast, carried, not human, no physics, a fight, a fall).
inline bool driver_class(bool figure, bool beast, bool parented, bool human, bool physics, bool fight, float vz) {
    if (figure || beast || parented || !human || !physics || fight) return false;
    return vz <= kAirVzMps && vz >= -kAirVzMps;
}

/// The route is not a second holder for a body the driver class owns.
inline bool consult_route(bool isDriverClass) { return !isDriverClass; }

/// Extrapolation is the writer's catch-up. The driver leads by its own short age.
inline bool extrapolate_pose(bool isDriverClass) { return !isDriverClass; }

/// Phase 2. A player (a figure) and a walking person share one fixed timeline.
/// Horses, fights and falls keep the adaptive delay.
inline bool fixed_timeline(bool figure, bool isDriverClass) { return figure || isDriverClass; }

constexpr double kFixedDelayS = 0.12;
constexpr double kFixedDelayCapS = 0.20;
constexpr double kPoseDelayCapS = 0.80;
constexpr double kDelayFloorS = 0.040;
constexpr double kDelayMarginS = 0.020;
constexpr double kSnapBehindS = 0.40;
constexpr float kStepZ = 0.08f;

/// The render delay for a fixed timeline. The bind's delay when it is already
/// a short fixed span, otherwise 120 ms. It does not take the queue age.
inline double fixed_delay_s(double bindDelayS) {
    if (bindDelayS >= 0.05 && bindDelayS <= kFixedDelayCapS) return bindDelayS;
    return kFixedDelayS;
}

/// Playback delay follows the measured gap. A tight link plays under 120 ms.
/// Floor 40 ms, 20 ms of margin. A bad link may use up to 800 ms so a late
/// sample is interpolated instead of held and then jumped. The bind is not a floor.
inline double adaptive_delay_s(double /*bindDelayS*/, double gapS) {
    double want = gapS + kDelayMarginS;
    if (!(want > kDelayFloorS)) want = kDelayFloorS;
    if (want > kPoseDelayCapS) want = kPoseDelayCapS;
    return want;
}

/// One shown step. Speed may rise by at most 2.5 m/s. A gap of 5 m or more
/// is a teleport and is left alone.
inline void limit_step(float fromX, float fromY, float& x, float& y, float& pace, float dt) {
    if (!(dt > 0.f)) return;
    if (dt < 0.016f) dt = 0.016f;
    const float dx = x - fromX, dy = y - fromY;
    const float dist = std::sqrt(dx * dx + dy * dy);
    const float allow = (pace + 2.5f) * dt;
    if (dist > allow && dist < 5.f) {
        const float u = allow / dist;
        x = fromX + dx * u;
        y = fromY + dy * u;
        pace += 2.5f;
    } else if (dist < 5.f) {
        pace = dist / dt;
    }
}

/// Height along a segment. A step (at least 8 cm, and steeper than the
/// horizontal move) takes the higher tread: going up, the new height as soon
/// as the segment starts, so the body does not walk into the stair; going
/// down, the old height until the segment ends. A slope lerps.
inline float segment_z(float az, float bz, float horiz, float t) {
    float dz = bz - az;
    float adz = dz < 0.f ? -dz : dz;
    if (adz >= kStepZ && adz > horiz) {
        if (bz >= az) return t > 0.f ? bz : az;
        return t >= 1.f ? bz : az;
    }
    if (t < 0.f) t = 0.f; else if (t > 1.f) t = 1.f;
    return az + dz * t;
}

/// The newest sample is further behind the fixed clock than one catch-up.
/// Show that sample now. Do not keep playing the backlog and do not extrapolate.
inline bool snap_catchup(double newestAgeS, double delayS) {
    return newestAgeS > delayS + kSnapBehindS;
}

} // namespace kcdmp::walkhold
