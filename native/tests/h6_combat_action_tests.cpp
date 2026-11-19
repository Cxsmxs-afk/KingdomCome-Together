// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#include "combat_actions.h"

#include <cstdio>

int h6_combat_action_tests(int* passed) {
    using kcdmp::combatactions::BodyProof;
    using kcdmp::combatactions::detail::Retirement;
    using kcdmp::combatactions::detail::observe_finished;
    using kcdmp::combatactions::detail::retire;
    using kcdmp::combatactions::detail::same_body;
    using namespace kcdmp::wo167rules;
    int failed = 0;
    const auto check = [&](bool result, const char* name) {
        if (result) ++*passed;
        else { ++failed; std::printf("FAIL H6: %s\n", name); }
    };

    int actor, entity, combat, otherActor, otherEntity, otherCombat;
    const BodyProof body{17, &actor, &entity, &combat, 3, 25};
    check(same_body(body, body), "the recorded and releasing body match");
    check(!same_body(body, {17, &otherActor, &otherEntity, &otherCombat, 3, 25}),
          "a reused entity ID must not identify any previous action");
    check(!same_body(body, {17, &actor, &entity, &otherCombat, 3, 25}),
          "a replaced combat component is a new incarnation even on the same actor");
    check(!same_body(body, {17, &actor, &entity, &combat, 5, 25}),
          "a lifecycle transition fences even reused addresses");
    check(!same_body(body, {17, &actor, &entity, &combat, 3, 26}),
          "a new load scope fences work even before the lifecycle observer catches up");
    check(!same_body(body, {17}), "an entity ID without body proof cannot retire a native action");
    auto samePointersDifferentEid = body;
    samePointersDifferentEid.eid = 18;
    check(same_body(body, samePointersDifferentEid), "the EID is not the identity of the proved actor and combat component");

    Retirement pending = Retirement::Active;
    int quieted = 0, stopped = 0;
    const auto quiet = [&] { ++quieted; return true; };
    const auto stop = [&] { ++stopped; return true; };
    check(retire(pending, true, kStatusNone, true, quiet, stop) && pending == Retirement::StopRequested && quieted == 1 && stopped == 1,
          "an action at the admission-to-install edge receives one Stop request");
    check(retire(pending, true, kStatusInstalled, true, quiet, stop) && quieted == 1 && stopped == 1,
          "a late installed callback cannot authorize a second stop of the same action");
    check(!observe_finished(pending, true, kStatusInstalled) && !observe_finished(pending, false, kStatusFinished),
          "neither a Stop invocation nor an unreadable terminal state confirms safe teardown");
    check(observe_finished(pending, true, kStatusFinished) && pending == Retirement::Finished,
          "the actual terminal state confirms the action is through with teardown");

    Retirement unknown = Retirement::Active;
    check(!retire(unknown, false, kStatusPending, true, quiet, stop) && unknown == Retirement::Quarantined && quieted == 1 && stopped == 1,
          "an unreadable action does not authorize a write through its pointer");
    check(!retire(unknown, true, kStatusInstalled, true, quiet, stop) && quieted == 1 && stopped == 1,
          "an unproven action stays quarantined after a later callback");

    Retirement offMain = Retirement::Active;
    check(!retire(offMain, true, kStatusPending, false, quiet, stop) && offMain == Retirement::Quarantined && quieted == 2 && stopped == 1,
          "an off-main release can silence verified callbacks without counting a Stop");
    Retirement complete = Retirement::Active;
    check(retire(complete, true, kStatusFinished, true, quiet, stop) && complete == Retirement::Finished && quieted == 2 && stopped == 1,
          "a verified finished action does not touch its callbacks or request a new Stop");

    Retirement stopFailed = Retirement::Active;
    check(!retire(stopFailed, true, kStatusPending, true, quiet, [&] { ++stopped; return false; }) &&
          stopFailed == Retirement::Quarantined && quieted == 3 && stopped == 2,
          "even successfully silenced callbacks do not prove a failed Stop");
    return failed;
}
