// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#pragma once
// P4: all join/leash/unstuck/video/scene placements go through p4::place.
// Fresh source/deadline/generation are mandatory; unknown legacy calls refuse.
// Ground probing never skips exact targets. Original source and placement drops
// are bounded separately. The shared fall lease waits for 1s known grounded proxy,
// at most20s. Target hostility beyond the peer snapshot is not a verified API.
// Main thread only (pipe0x1C ->0x8C).

#include <cstdint>
#include "p4.h"

namespace kcdmp::joinnative {

struct PlaceReport {
    bool  ok = false;          // the teleport ran and the player stands at the target
    bool  snapped = false;     // a target on the ground was found
    int   tried = 0;           // directions tried
    bool  fallHeld = false;    // fall damage suppressed for the landing
    float target[3]{};         // where it aimed (after the snap)
    float before[3]{};
    float after[3]{};
    float residual = -1.f;     // |after - target| in the horizontal plane
};

PlaceReport place(float hostX, float hostY, float hostZ, float dist, p4::Context context = {});

// WO-114: the ground search place() uses, alone -- eight directions around
// (hx,hy), `dist` metres out, navmesh-snapped within 2 m of hz. False when no
// direction has ground. Main thread only.
bool find_beside(float hx, float hy, float hz, float dist, float out[3], int* tried);

// Per-frame: shared landing/protection/provenance monitor.
void tick();

} // namespace kcdmp::joinnative
