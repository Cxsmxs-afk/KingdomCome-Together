// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
// Phase 1: one holder for a walking person. Engine-free.
#include <cstdio>

#include "walk_holder.h"

using kcdmp::walkhold::consult_route;
using kcdmp::walkhold::driver_class;
using kcdmp::walkhold::extrapolate_pose;
using kcdmp::walkhold::adaptive_delay_s;
using kcdmp::walkhold::fixed_delay_s;
using kcdmp::walkhold::fixed_timeline;
using kcdmp::walkhold::snap_catchup;

namespace {
int g_fail = 0, g_pass = 0;
#define WCHECK(cond, ...) do { if (cond) ++g_pass; else { ++g_fail; std::printf("FAIL  %s:%d  ", __FILE__, __LINE__); std::printf(__VA_ARGS__); std::printf("\n"); } } while (0)
} // namespace

int walk_holder_tests(int* passed) {
    WCHECK(driver_class(false, false, false, true, true, false, 0.0f), "a person on the ground is the driver's");
    WCHECK(!consult_route(true) && !extrapolate_pose(true), "that person is not also routed or extrapolated");
    WCHECK(!driver_class(true, false, false, true, true, false, 0.0f), "the other player is a figure: the writer");
    WCHECK(consult_route(false) && extrapolate_pose(false), "a body the driver refuses may still use the route and extrapolation");
    WCHECK(!driver_class(false, true, false, false, true, false, 0.0f), "a horse is not the person's holder");
    WCHECK(!driver_class(false, false, true, true, true, false, 0.0f), "a rider or a carried body stays the writer's");
    WCHECK(!driver_class(false, false, false, true, true, true, 0.0f), "a fight stays the writer's");
    WCHECK(!driver_class(false, false, false, true, false, false, 0.0f), "no physics: the writer");
    WCHECK(!driver_class(false, false, false, false, true, false, 0.0f), "not a person: the writer");
    WCHECK(!driver_class(false, false, false, true, true, false, 1.6f), "a fall (vz 1.6) is the writer's");
    WCHECK(!driver_class(false, false, false, true, true, false, -1.6f), "a fall the other way is the writer's");
    WCHECK(driver_class(false, false, false, true, true, false, 1.5f), "vz at the air limit is still the driver's");
    WCHECK(fixed_timeline(true, false), "the other player is on the fixed timeline");
    WCHECK(fixed_timeline(false, true), "a walking person is on the fixed timeline");
    WCHECK(!fixed_timeline(false, false), "a horse or a fight keeps the adaptive delay");
    WCHECK(fixed_delay_s(0.12) == 0.12, "a 120 ms bind stays 120 ms");
    WCHECK(fixed_delay_s(3.0) == 0.12, "a multi-second bind does not become the render delay");
    WCHECK(!snap_catchup(0.20, 0.12), "200 ms of age is still inside the fixed clock");
    WCHECK(snap_catchup(0.60, 0.12), "600 ms behind snaps to the newest sample");
    WCHECK(adaptive_delay_s(0.12, 0.05) == 0.12, "a 50 ms gap keeps the 120 ms floor");
    WCHECK(adaptive_delay_s(0.12, 0.16) == 0.16, "a wider gap raises the delay");
    WCHECK(adaptive_delay_s(0.12, 0.50) == 0.20, "the delay stops at 200 ms");
    if (passed) *passed = g_pass;
    return g_fail;
}

#ifdef WALK_HOLDER_STANDALONE
int main() {
    int passed = 0;
    const int failed = walk_holder_tests(&passed);
    std::printf("%d passed, %d failed\n", passed, failed);
    return failed == 0 ? 0 : 1;
}
#endif
