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

/// One shown step. The body may go up to 1.2 m/s faster than the pace it
/// already shows, and never faster than a sprint. A hitch is charged as
/// 50 ms, so one late frame cannot spend half a metre. Eight metres or more
/// is a real relocation (a fast travel, a spawn) and is left alone.
constexpr float kCatchMps = 1.2f;
constexpr float kMaxShowMps = 6.5f;
constexpr float kHitchDtS = 0.05f;
constexpr float kPlaceM = 8.0f;
inline void limit_step(float fromX, float fromY, float& x, float& y, float& pace, float dt) {
    if (!(dt > 0.f)) return;
    const float dx = x - fromX, dy = y - fromY;
    const float dist = std::sqrt(dx * dx + dy * dy);
    if (!(dist < kPlaceM)) return;
    float stepDt = dt;
    if (stepDt < 0.016f) stepDt = 0.016f;
    if (stepDt > kHitchDtS) stepDt = kHitchDtS;
    float cap = pace + kCatchMps;
    if (cap > kMaxShowMps) cap = kMaxShowMps;
    if (!(cap > kCatchMps)) cap = kCatchMps;
    const float allow = cap * stepDt;
    if (dist > allow) {
        const float u = allow / dist;
        x = fromX + dx * u;
        y = fromY + dy * u;
        pace = cap;
    } else {
        pace = dist / dt;
        if (pace > kMaxShowMps) pace = kMaxShowMps;
    }
}

/// The gait follows the host, but a single late sample must not jump it
/// (the live run: the partner's gait leapt to 5.6 m/s). At most 3 m/s of
/// change per second. A reading over 9 m/s is a placement, not a pace.
inline float smooth_gait(float prev, float want, float dt) {
    if (!(want < 9.f) || !(dt > 0.f)) return prev;
    const float step = 3.0f * dt;
    if (want > prev + step) return prev + step;
    if (want < prev - step) return prev - step;
    return want;
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

/// A sample this old is a load or a fast travel: show it. A late packet
/// (the live run: 1.15 s, which used to jump the body onto the newest
/// sample) keeps the clock. The step limit walks the gap off.
constexpr double kPlaceAgeS = 4.0;
inline bool place_now(double newestAgeS) { return newestAgeS > kPlaceAgeS; }
inline bool snap_catchup(double newestAgeS, double delayS) {
    (void)delayS;
    return place_now(newestAgeS);
}

} // namespace kcdmp::walkhold
