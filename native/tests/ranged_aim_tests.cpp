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
    check(apply(Unreadable, &held) == Edge::Ignored && !held.aim && held.charge == 0, "an unreadable sample does not invent a zero draw");
    check(apply(Aiming, &held) == Edge::Held && held.aim && held.charge == 255, "aiming publishes a full phase charge");
    Block stuck = held;
    check(apply(Unreadable, &held) == Edge::Ignored && held.aim && held.charge == stuck.charge, "an unreadable gap keeps the held aim");
    check(apply(Charging, &held) == Edge::Held && held.charge == 128, "charging is a separate half charge");
    check(apply(Reloading, &held) == Edge::Ended && !held.aim && held.confirmedEnd, "crossbow reload is not a held aim");
    check(apply(Aiming, &held) == Edge::Held && held.aim, "a new aim after reload is a new hold");
    check(apply(None, &held) == Edge::Ended && !held.aim && held.charge == 0 && held.yaw == 0, "a readable none is the confirmed end");
    check(apply(None, &held) == Edge::Same && held.confirmedEnd, "a second none does not count as a new end");
    check(!restore_after_end(1000, 900), "an older hold packet does not restore the pose");
    check(restore_after_end(1000, 1001), "a newer sample after the end is eligible");
    publish(Aiming);
    check(published() == Aiming, "the published phase is the one the body block reads");
    publish(None);
    return failures;
}
