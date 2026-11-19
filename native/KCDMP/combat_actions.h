// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#pragma once
// The lifetime of the combat animation actions this mod queues (WO-221 A, mp_combat_action_end; docs/WO-221-findings.md).
//
// The fault (0.65.0, the joiner): a C_CombatAnimAction reads its body's combat part (C_Actor +0x300 -> +0x490) in two of its
// callbacks without a check -- "fragment started" (vftable slot 14) and "the fragment is about to end" (slot 17) -- on the animation
// job. The engine drops a body's combat part whenever the body changes its level of detail (C_Actor::SetMonsterLOD), dies or is
// reset, and when its actor is destroyed; all of them go through one virtual, C_Actor::ReleaseCombatActor. An action of ours still
// waiting or running on that body's action controller then faults in the game's own code the next time the controller runs it
// (minutes later, when the player comes back to the body).
//
// Track the physical actor, entity, combat part and lifecycle for every owned action. While that proof is current, a game-thread
// retirement can request Mannequin's IAction::Stop. The engine's release hook can also silence verified callbacks before giving the
// combat part back. When either operation is uncertain, retain the reference in quarantine instead of guessing that teardown is safe.
#include <cstdint>
#include "wo167_rules.h"

namespace kcdmp::combatactions {

struct BodyProof {
    uint32_t eid = 0;
    void* actor = nullptr;
    void* entity = nullptr;
    void* combat = nullptr;
    uint64_t lifecycle = 0;
    uint64_t load = 0;
    uint64_t combatRevision = 0;
    uint64_t queueEpoch = 0;
};

enum class ReleaseReceipt : uint8_t { Ready = 1, Pending = 2, Uncertain = 3, Stale = 4 };
enum class QueueResult : uint8_t { Refused, Submitted, Uncertain };

struct QueueTicket {
    BodyProof body;
    void* action = nullptr;
    uint64_t serial = 0;
};

namespace detail {

inline bool known_body(const BodyProof& body) {
    return body.eid && body.actor && body.entity && body.lifecycle;
}

inline bool same_body(const BodyProof& recorded, const BodyProof& releasing) {
    return known_body(recorded) && known_body(releasing) && recorded.actor == releasing.actor &&
        recorded.entity == releasing.entity && recorded.combat == releasing.combat &&
        recorded.lifecycle == releasing.lifecycle && recorded.load == releasing.load &&
        recorded.combatRevision == releasing.combatRevision && recorded.queueEpoch == releasing.queueEpoch;
}

// The engine gets the only queue call after the owned reference is published. The call can synchronously release the body.
template <class Reserve, class Begin, class Call, class Settle>
QueueResult submit(Reserve reserve, Begin begin, Call call, Settle settle) {
    const QueueTicket ticket = reserve();
    if (!ticket.serial) return QueueResult::Refused;
    if (!begin(ticket)) {
        settle(ticket, QueueResult::Refused);
        return QueueResult::Refused;
    }
    return settle(ticket, call() ? QueueResult::Submitted : QueueResult::Uncertain);
}

enum class Retirement : uint8_t { Active, StopRequested, Finished, Quarantined };

inline bool observe_finished(Retirement& state, bool actionKnown, uint32_t status) {
    if (actionKnown && status == wo167rules::kStatusFinished) state = Retirement::Finished;
    return state == Retirement::Finished;
}

template <class Quiet, class Stop>
bool retire(Retirement& state, bool actionKnown, uint32_t status, bool canStop, Quiet quiet, Stop stop) {
    if (state != Retirement::Active) return state != Retirement::Quarantined;
    if (observe_finished(state, actionKnown, status)) return true;
    if (actionKnown && status < wo167rules::kStatusFinished && quiet() && canStop && stop()) state = Retirement::StopRequested;
    else state = Retirement::Quarantined;
    return state != Retirement::Quarantined;
}

} // namespace detail

// Main only. Publish one owned reference before the engine may call its release hook, then recheck the exact ticket before calling.
QueueTicket reserve_queue(const BodyProof& body, void* action, bool movesBody);
bool begin_queue(const QueueTicket& ticket);
QueueResult settle_queue(const QueueTicket& ticket, QueueResult result);
// True while a tracked combat one-shot still occupies the engine's pose controller.
bool body_pose_holds(uint32_t eid);

// Main thread. Resolve both engine indexes, then verify the proof again before a write. A known actor may have no combat part.
BodyProof observe_body(uint32_t eid);
// A nonzero attempt fences this physical body against new queues. Pending can only be re-observed with the same body/attempt.
ReleaseReceipt retire_for_body(const BodyProof& body, const char* why, uint64_t attempt = 0);
bool release_ready(const BodyProof& body, uint64_t attempt);
// Exact actions confirmed finished, or -1 for a pending stop, unknown identity or incomplete tracking.
int end_for_body(const BodyProof& body, const char* why);
// Legacy ABI. An EID-only request has no body proof and cannot end a tracked action.
int end_for_entity(uint32_t eid, const char* why);
// Any thread. Request bounded main-thread retirement of entries already tracked. No work is ended inline.
int end_all(const char* why);

bool armed();

} // namespace kcdmp::combatactions
