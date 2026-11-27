// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#include "combat_actions.h"

#include <cstdio>
#include <initializer_list>

int h6_combat_action_tests(int* passed) {
    using kcdmp::combatactions::BodyProof;
    using kcdmp::combatactions::QueueResult;
    using kcdmp::combatactions::QueueTicket;
    using kcdmp::combatactions::detail::Retirement;
    using kcdmp::combatactions::detail::observe_finished;
    using kcdmp::combatactions::detail::retire;
    using kcdmp::combatactions::detail::same_body;
    using kcdmp::combatactions::detail::submit;
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
    auto released = body;
    ++released.combatRevision;
    check(!same_body(body, released), "an LOD release fences the same addresses in the same world");
    auto disconnected = body;
    ++disconnected.queueEpoch;
    check(!same_body(body, disconnected), "disconnect fences the same addresses and lifecycle");

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

    Retirement releasedDuringQuiet = Retirement::Active;
    bool bodyCurrent = true;
    bool lateStop = false;
    check(!retire(releasedDuringQuiet, true, kStatusInstalled, true,
          [&] { bodyCurrent = false; return true; }, [&] { lateStop = true; return true; }, [&] { return bodyCurrent; }) &&
          releasedDuringQuiet == Retirement::Quarantined && !lateStop,
          "a release which invalidates the body after callback silencing prevents Stop");

    int action = 0;
    const QueueTicket ticket{body, &action, 1};
    unsigned phase = 0, calls = 0;
    const auto invoke = [&] { ++calls; return true; };
    const auto identity = [](const QueueTicket&, QueueResult value) { return value; };
    check(submit([] { return QueueTicket{}; }, [](const QueueTicket&) { return true; }, invoke, identity) == QueueResult::Refused && !calls,
          "failure to publish an owned reference prevents the engine call");
    check(submit([&] { return ticket; }, [](const QueueTicket&) { return false; }, invoke,
          [&](const QueueTicket&, QueueResult value) { ++phase; return value; }) == QueueResult::Refused && !calls && phase == 1,
          "failed pre-dispatch proof settles the unsubmitted reservation without issuing QueueAction");

    bool releaseSawReservation = false;
    const auto result = submit([&] { phase = 2; return ticket; },
        [&](const QueueTicket& queued) { return phase == 2 && queued.serial == ticket.serial; },
        [&] { releaseSawReservation = phase == 2; ++calls; phase = 3; return true; },
        [&](const QueueTicket&, QueueResult attempted) {
            return attempted == QueueResult::Submitted && phase == 3 ? QueueResult::Uncertain : attempted;
        });
    check(result == QueueResult::Uncertain && releaseSawReservation && calls == 1,
          "a synchronous release sees the published action and cannot synthesize an acknowledgement or repeat the call");
    check(submit([&] { return ticket; }, [](const QueueTicket&) { return true; },
          [&] { ++calls; return false; }, identity) == QueueResult::Uncertain && calls == 2,
          "an engine fault retains uncertain ownership and never retries");

    // Main owns the caller and offered references, and the ledger owns its reservation.
    // The engine can take its own references and independently consumes the offer.
    enum class Recipient { Immediate, Watcher, Wo208 };
    bool balanced = true;
    for (const auto result : {QueueResult::Refused, QueueResult::Submitted, QueueResult::Uncertain})
        for (const bool protect : {false, true}) for (const bool proved : {false, true})
            for (const auto recipient : {Recipient::Immediate, Recipient::Watcher, Recipient::Wo208}) {
                const int opening = 0;
                int refs = opening + 3;
                const auto drop = [&](int count) { balanced = balanced && refs >= count; refs -= count; };
                const int engine = result == QueueResult::Refused ? 0 : 2;
                refs += engine;
                if (result == QueueResult::Refused) drop(1); // settle undoes its ledger reservation
                else drop(1); // manager consumes the offered smart pointer
                const int early = static_cast<int>(kcdmp::combatactions::detail::caller_releases(result, protect, proved));
                drop(early);
                if (result == QueueResult::Submitted && recipient == Recipient::Immediate) drop(1);
                drop(engine);
                if (result == QueueResult::Submitted && recipient != Recipient::Immediate) drop(1); // inherited caller ref
                if (result == QueueResult::Submitted) drop(1); // ledger at Finished
                if (result == QueueResult::Uncertain) {
                    const int retained = protect || !proved ? 2 : 1;
                    balanced = balanced && refs == retained; // an unknown state must still own its safety references
                    drop(retained); // exact receipt now proves Finished
                }
                balanced = balanced && refs == opening;
            }
    check(balanced, "ledger, Watcher and WO208 return exactly their own references after proven completion");
    return failed;
}
