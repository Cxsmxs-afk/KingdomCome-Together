// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
// Phase 1: one holder for a walking person. Engine-free.
#include <cstdio>

#include "walk_holder.h"

using kcdmp::walkhold::consult_route;
using kcdmp::walkhold::driver_class;
using kcdmp::walkhold::extrapolate_pose;

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
