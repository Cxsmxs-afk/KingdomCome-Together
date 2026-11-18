// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#include "rp_body_action_rules.h"

#include <cstdio>
#include <cstring>

int rp_body_action_tests(int* passed) {
    using namespace kcdmp::rpbodyactions;
    int failed = 0;
    const auto check = [&](bool valid, const char* name) {
        if (valid) ++*passed;
        else { ++failed; std::printf("FAIL: %s\n", name); }
    };
    const auto fragment_is = [](uint8_t weapon, uint8_t phase, const char* expected) {
        const char* fragment = ranged_fragment(weapon, phase);
        return fragment && std::strcmp(fragment, expected) == 0;
    };
    check(fragment_is(kBow, kFire, "Fire"), "bow uses the whitelisted Fire fragment");
    check(fragment_is(kCrossbow, kFire, "Fire"), "crossbow uses the whitelisted Fire fragment");
    check(fragment_is(kBow, kLoad, "Load"), "bow has a checked reload");
    check(!ranged_fragment(kCrossbow, kLoad), "unknown crossbow mechanism cannot borrow bow reload");
    check(fragment_is(kCrossbow, kUncharge, "Uncharge"), "crossbow has a checked let-down");
    check(!ranged_fragment(kBow, 2), "unsupported aim has no typed visual request");
    check(!ranged_fragment(0, kFire), "unknown P6 kind cannot issue a visual request");
    check(ranged_completion_confirmed(true, 2, 10, 10), "matching engine success proves the visual request");
    check(!ranged_completion_confirmed(false, 2, 10, 10), "uncompleted request cannot cover a shot");
    check(!ranged_completion_confirmed(true, 1, 10, 10), "engine refusal cannot cover a shot");
    check(!ranged_completion_confirmed(true, 2, 11, 10), "a different callback cannot cover a shot");
    check(down_confirmed(true, true, false), "unconsciousness with nonliving physics proves down");
    check(!down_confirmed(false, true, false), "ragdoll alone cannot cover a knockout");
    check(!down_confirmed(true, false, false), "unreadable physics cannot cover a knockout");
    check(!down_confirmed(true, true, true), "a buff alone cannot cover a knockout");
    check(up_confirmed(false, true, true, true, false), "conscious and grounded living physics proves upright");
    check(!up_confirmed(true, true, true, true, false), "an unconscious body cannot cover get-up");
    check(!up_confirmed(false, true, true, true, true), "remaining ragdoll cannot cover get-up");
    check(!up_confirmed(false, true, true, false, false), "airborne living body cannot cover get-up");
    return failed;
}
