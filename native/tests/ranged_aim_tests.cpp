// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#include "../KCDMP/ranged_aim.h"

#include <cstdio>

int ranged_aim_tests(int* passed) {
    using namespace kcdmp::rangedaim;
    int failures = 0;
    const auto check = [&](bool result, const char* label) {
        if (result) ++*passed;
        else { ++failures; std::fprintf(stderr, "ranged aim: %s\n", label); }
    };
    Block held{};
    check(apply({}, held) == Edge::Ignored && !held.aim && !held.charge, "an unreadable sample does not invent a zero draw");
    check(apply({Aiming, {}}, held) == Edge::Held && held.aim && !held.charge, "unmeasured aim does not invent full charge");
    check(apply({Charging, 128}, held) == Edge::Held && held.charge == 128, "measured charge is used instead of phase");
    check(apply({}, held) == Edge::Ignored && held.aim && held.charge == 128, "an unreadable gap keeps the held aim");
    check(apply({Aiming, {}}, held) == Edge::Same && held.charge == 128, "the last measurement carries into the aim phase");
    check(apply({Reloading, {}}, held) == Edge::Ended && !held.aim && !held.charge, "crossbow reload is not a held aim");
    check(apply({Aiming, {}}, held) == Edge::Held && held.aim && !held.charge, "a new aim cannot borrow the last draw's charge");
    check(apply({None, {}}, held) == Edge::Ended && !held.aim, "a readable none is the confirmed end");
    check(apply({None, {}}, held) == Edge::Same, "a second none does not count as a new end");
    check(quantize_charge(0, 0, 1) == 0 && quantize_charge(.5f, .25f, .75f) == 128 && quantize_charge(1, 0, 1) == 255,
          "bounded real values round to wire charge, including a genuine zero");
    check(!quantize_charge(-.1f, 0, 1) && !quantize_charge(.5f, .5f, .5f) &&
          !quantize_charge(std::numeric_limits<float>::quiet_NaN(), 0, 1),
          "invalid measurement never becomes zero");
    float camera[12]{};
    camera[1] = .6123724f; camera[5] = .6123724f; camera[9] = -.5f;
    const auto downRight = camera_angles(camera);
    check(downRight.known() && downRight.yaw == -4500 && downRight.pitch == -3000,
          "the camera forward column gives known signs on both axes");
    camera[1] = camera[5] = 0; camera[9] = 1;
    check(!camera_angles(camera).known(), "vertical view has no measurable yaw and does not fabricate a pair");
    camera[1] = std::numeric_limits<float>::quiet_NaN();
    check(!camera_angles(camera).known(), "a malformed camera does not become a zero angle");
    return failures;
}
