// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#include "../KCDMP/holder_rules.h"
#include "../KCDMP/rp_controller_rules.h"

#include <cstdio>

int rp_controller_tests(int* passed) {
    using namespace kcdmp::rpctrl;
    int failed = 0;
    const auto check = [&](bool value, const char* rule) {
        if (value) ++*passed;
        else { ++failed; std::fprintf(stderr, "partner controller: %s\n", rule); }
    };
    check(fallback(false, false, false, 0.3f) == Fallback::None, "ground figures can take the engine controller");
    check(fallback(false, false, false, -2.f) == Fallback::Air, "airborne uses the guarded writer");
    check(fallback(true, false, false, 3.f) == Fallback::Jump, "known jump precedes generic air");
    check(fallback(true, true, false, 3.f) == Fallback::Mount &&
          fallback(true, true, true, 3.f) == Fallback::Ragdoll, "parent and ragdoll dominate movement");

    const float origin[3]{0.f, 0.f, 0.f}, endpoint[3]{50.f, -50.f, 4.f};
    float unknown[4]{50.f, -50.f, 4.f, 2.f};
    soften(origin, 0.f, unknown, 6.5f, 1.0);
    check(std::hypot(unknown[0], unknown[1]) <= 0.326f && unknown[2] <= 0.125f && unknown[3] <= 0.16001f &&
          distance(origin, unknown) < kHardStepM, "ordinary hitch cannot hard-place by distance in XY, Z or yaw");
    float pending[4]{50.f, -50.f, 4.f, 2.f};
    soften(origin, 0.f, pending, 2.f, 0.0);
    check(pending[0] == 0.f && pending[1] == 0.f && pending[2] == 0.f && pending[3] == 0.f,
          "a zero-time correction keeps the physical point");
    float turn[4]{0.f, 0.f, 0.f, -3.12f};
    soften(origin, 3.12f, turn, 1.f, 0.016);
    check(turn[3] > 3.12f && turn[3] < 3.17f, "facing takes shortest path across ±pi");

    Proof proof;
    check(!proof.allows(1100, 5, 3, endpoint, origin), "large unexplained jump has no permit");
    for (const auto kind : {Relocation::Join, Relocation::Respawn, Relocation::Travel}) {
        proof = {kind, 1000, 5, 3, {50.f, -50.f, 4.f}};
        check(proof.allows(1100, 5, 3, endpoint, origin), "each authorized cause still matches a fresh confirmed endpoint");
    }
    const float other[3]{50.f, -48.f, 4.f};
    check(!proof.allows(1100, 5, 3, other, origin) && !proof.allows(1100, 5, 3, endpoint, endpoint) &&
          !proof.allows(1100, 6, 3, endpoint, origin) && !proof.allows(1100, 5, 4, endpoint, origin) &&
          !proof.allows(1000 + kPermitMs + 1, 5, 3, endpoint, origin), "mismatched/old scope and trivial moves cannot spend a permit");
    proof.consume();
    check(!proof.allows(1101, 5, 3, endpoint, origin), "successful hard placement consumes its permission");
    const float invalid[] = {(std::numeric_limits<float>::quiet_NaN)(), 0.f, 0.f};
    check(!finite_position(invalid) && finite_position(endpoint), "nonfinite transform rejected before a request");

    const float stand[4]{0.f, 0.f, 0.f, 0.f}, step[4]{1.f, 0.f, 0.1f, 0.f};
    const auto idle = movement(origin, stand, 0.f, 0.f, 0.f, false, 0.f, 0.016);
    const auto walk = movement(origin, step, 1.f, 0.f, 1.5f, false, 0.f, 0.016);
    const auto run = movement(origin, step, 2.f, 0.f, 3.2f, false, 0.f, 0.016);
    const auto sprint = movement(origin, step, 2.f, 0.f, 5.2f, false, 0.f, 0.016);
    check(!idle.walking && idle.gait == 0.f && idle.face[0] == 0.f && idle.face[1] == 1.f,
          "stationary request still owns facing with no move target");
    check(walk.walking && walk.move[0] > 1.f && walk.face[1] == 1.f &&
          walk.gait == 1.f && run.gait == 2.f && sprint.gait == 3.f,
          "walk, run and sprint preserve sampled trajectory and independent facing");
    const auto crouch = movement(origin, step, 2.f, 0.f, 3.2f, true, 4.f, 0.016);
    check(crouch.walking && crouch.gait == 1.f && crouch.speed <= 1.55f, "fresh crouch request does not sprint");
    const auto catchup = movement(origin, step, 1.f, 0.f, 1.5f, false, 1.f, 0.016);
    check(catchup.speed <= 1.049f, "ordinary drift correction accelerates softly");

    namespace owners = kcdmp::holders;
    owners::Table table;
    table.synchronize({3, 4}, 10);
    constexpr const char* driver = "bodydrive::remote-controller";
    check(table.select(123, owners::Holder::Driver, 1.0).selected, "driver first owns the figure");
    const auto request = table.acquire(123, owners::Holder::Driver, driver, owners::Claim::Held);
    // A refused request can partially merge in the engine. Mark its attempt
    // as an engine mutation before trying to establish the writer fallback.
    check(request && table.wrote(123, owners::Holder::Driver, driver, request), "attempted engine update pins the owner");
    table.finish(123, request);
    check(!table.select(123, owners::Holder::Writer, 1.1).selected &&
          !table.acquire(123, owners::Holder::Writer, "npc-drive-stream", owners::Claim::Available),
          "a refused movement request cannot share the figure with the writer");
    const auto stop = table.acquire(123, owners::Holder::Driver, driver, owners::Claim::Release);
    check(stop && table.wrote(123, owners::Holder::Driver, driver, stop), "the old owner may issue its release");
    table.finish(123, stop);
    table.engine_stopped(123, owners::Holder::Driver);
    check(table.select(123, owners::Holder::Writer, 1.2).selected, "confirmed stop permits the writer handover");
    const auto writer = table.acquire(123, owners::Holder::Writer, "npc-drive-stream", owners::Claim::Held);
    check(writer && table.wrote(123, owners::Holder::Writer, "npc-drive-stream", writer) &&
          table.stats().violations == 0 && table.stats().denied_sets == 1,
          "both enabled paths stay at zero simultaneous-owner violations");
    table.finish(123, writer);

    check(pull(0.85f, 1.f, 0.016) && !pull(0.85f, 6.f, 0.16) && !pull(1.2f, 0.f, 0.8),
          "pull is measured actual excess displacement during a bounded observation");
    check(pulls_per_min(0, 0) == 0 && pulls_per_min(1, 300001) == 199 && pulls_per_min(1, 300000) == 200 &&
          pulls_per_min(2, 300000) == 400,
          "derived thousandths per observed body minute enforce the 0.2 threshold");
    return failed;
}
