// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#pragma once
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

} // namespace kcdmp::walkhold
