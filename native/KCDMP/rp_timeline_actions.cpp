// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#include "rp_timeline_actions.h"

#include "combat_swing.h"
#include "engine.h"
#include "hits.h"
#include "log.h"
#include "motion.h"
#include "npc_drive.h"
#include "p4.h"
#include "pipe_server.h"
#include "rp_combat.h"
#include "rp_combat_rules.h"
#include "rp_measure.h"
#include "rp_timeline.h"
#include "wo131.h"
#include "wo143.h"
#include "wo187.h"
#include "wo208.h"
#include "wo216.h"
#include "wo221.h"

#include <windows.h>

#include <algorithm>
#include <deque>
#include <optional>
#include <string>
#include <unordered_map>
#include <utility>

namespace kcdmp::timelineactions {
namespace {

constexpr size_t kMaxPending = 128, kMaxPerBody = 32, kLegacyRecent = 64, kMaxRowsPerBody = 192, kMaxBodies = 16;
constexpr uint64_t kWatchMs = 3500, kKoWatchMs = 27000;

struct Typed {
    rpcombat::Event wire;
    rttr::CombatRow row;
};
struct Pending {
    uint64_t hostMs, readMs;
    uint16_t sequence;
    uint8_t kind, phase;
    std::string spec;
    std::optional<Typed> typed;
};
struct Row {
    uint8_t kind;
    std::string spec;
    rttr::CombatRow descriptor;
};
struct Body {
    void* entity;
    void* actor;
    uint8_t peer;
    std::deque<Pending> pending{};
    std::deque<std::pair<uint16_t, uint64_t>> legacyRecent{};
    rpcombat::Receipts typedRecent{};
    std::unordered_map<uint32_t, Row> rows{};
};
struct Observation {
    uint32_t eid;
    uint8_t peer;
    void* entity;
    void* actor;
    uint64_t deadline;
    Pending action;
};

bool g_enabled = false;
bool g_remoteCombat = false;
Scope g_scope;
size_t g_pending = 0;
uint64_t g_nextToken = 0;
std::unordered_map<uint32_t, Body> g_bodies;
std::unordered_map<uint64_t, Observation> g_observing;
rpcombat::Counts g_combat;
rpmeasure::detail::Histogram g_actionLag, g_koLag;

Scope scope_now() {
    return {wo216::lifecycle_observation_stamp(), pipe::observation_connection(), p4::life_edge_tick()};
}

uint64_t elapsed(uint64_t start, uint64_t now) { return start <= now ? now - start : 0; }

void report(uint32_t eid, uint8_t peer, const Pending& action, const char* result, uint64_t poseHostMs = 0, const char* reason = "-") {
    if (action.typed) {
        const auto& typed = action.typed->wire;
        logf("MP-RP-COMBAT-ACTION peer=%u eid=0x%X kind=%u phase=%u seq=%u event_host_ms=%llu pose_host_ms=%llu lag_ms=%llu "
             "result=%s reason=%s scope=%llu connection=%llu down=%u weapon=%u ranged_phase=%u",
             peer, eid, action.kind, action.phase, action.sequence, static_cast<unsigned long long>(action.hostMs),
             static_cast<unsigned long long>(poseHostMs), static_cast<unsigned long long>(elapsed(action.readMs, GetTickCount64())),
             result, reason, static_cast<unsigned long long>(g_scope.lifecycle), static_cast<unsigned long long>(g_scope.connection),
             typed.kind == rpcombat::kKo ? typed.stage : 0u, typed.kind == rpcombat::kRanged ? typed.stage : 0u, typed.rangedPhase);
        return;
    }
    const uint64_t distance = poseHostMs ? skew(action.hostMs, poseHostMs) : 0;
    logf("MP-RP-TIMELINE-ACTION peer=%u eid=0x%X kind=%u phase=%u seq=%u event_host_ms=%llu pose_host_ms=%llu skew_ms=%llu result=%s "
         "reason=%s scope=%llu connection=%llu", peer, eid, action.kind, action.phase, action.sequence,
         static_cast<unsigned long long>(action.hostMs), static_cast<unsigned long long>(poseHostMs),
         static_cast<unsigned long long>(distance), result, reason, static_cast<unsigned long long>(g_scope.lifecycle),
         static_cast<unsigned long long>(g_scope.connection));
}

void settle(uint32_t eid, uint8_t peer, const Pending& action, const char* result, const char* reason, uint64_t poseHostMs = 0) {
    if (action.typed) --g_combat.actions_pending;
    report(eid, peer, action, result, poseHostMs, reason);
}

bool wake(const Pending& action) {
    return action.typed && action.kind == rpcombat::kKo && action.typed->wire.stage == 0;
}

void forget_pending(uint32_t eid, Body& body, const char* reason, bool keepWake = false) {
    for (auto it = body.pending.begin(); it != body.pending.end();) {
        if (keepWake && wake(*it)) {
            ++it;
            continue;
        }
        settle(eid, body.peer, *it, "expired", reason);
        it = body.pending.erase(it);
        --g_pending;
    }
}

Scope synchronize() {
    const auto scope = scope_now();
    if (!(g_scope == scope)) {
        reset();
        g_scope = scope;
    }
    const bool mode = wo221::on(wo221::kBitRemoteCombat);
    if (g_remoteCombat != mode) {
        for (auto& body : g_bodies) {
            forget_pending(body.first, body.second, "combat-mode-changed", true);
            body.second.rows.clear();
        }
        g_remoteCombat = mode;
    }
    return scope;
}

bool entity_matches(uint32_t eid, uint8_t peer, void* entity) {
    if (!entity || entity != engine::entity_by_id(eid)) return false;
    const char* name = engine::entity_name(entity);
    uint8_t actual = 0;
    return name && rpmeasure::partner_id(name, actual) && actual == peer;
}

bool body_matches(uint32_t eid, uint8_t peer, void* entity, void* actor) {
    return npcdrive::is_bound_eid(eid) && entity_matches(eid, peer, entity) &&
           (!actor || actor == motion::actor_of(eid));
}

rptimeline::Frame matched_frame(uint32_t eid, uint8_t peer) {
    const auto frame = rptimeline::frame(eid, peer);
    return frame.lifecycle == g_scope.lifecycle && frame.connection == g_scope.connection ? frame : rptimeline::Frame{};
}

Body* lookup_body(uint32_t eid, uint8_t peer, void* entity, void* actor) {
    auto it = g_bodies.find(eid);
    if (it != g_bodies.end() && (it->second.entity != entity || it->second.peer != peer || it->second.actor != actor)) {
        forget_pending(eid, it->second, "body-replaced");
        g_bodies.erase(it);
        it = g_bodies.end();
    }
    if (it == g_bodies.end()) {
        if (g_bodies.size() >= kMaxBodies) return nullptr;
        it = g_bodies.emplace(eid, Body{entity, actor, peer}).first;
    }
    return &it->second;
}

bool tracked_wake(const rpcombat::Event& event, const Body& body) {
    if (event.kind != rpcombat::kKo || event.stage != 0) return false;
    if (wo131::rp_knockout_wake_pending(event.eid)) return true;
    return std::any_of(body.pending.begin(), body.pending.end(), [](const Pending& action) {
        return action.typed && action.kind == rpcombat::kKo && action.typed->wire.stage != 0;
    });
}

bool ready_to_dispatch(const rpcombat::Event& event) {
    if (rpcombat::uses_row(event.kind)) return hits::visual_damage_ready(event.eid);
    if (event.kind == rpcombat::kKo) return wo131::rp_knockout_available(event.eid, event.stage != 0);
    return wo143::rp_ranged_available(event.eid, event.itemClass.data());
}

void dispatch_typed(uint32_t eid, Body& body, Pending action, bool timed, uint64_t now) {
    const auto& typed = *action.typed;
    const auto& event = typed.wire;
    if (!(scope_now() == g_scope) || g_observing.size() >= kMaxPending ||
        wo208::probe_owned(eid) || !ready_to_dispatch(event)) {
        settle(eid, body.peer, action, "refused", "runtime-gate");
        return;
    }
    const uint64_t token = ++g_nextToken;
    const uint64_t watchMs = event.kind == rpcombat::kKo ? kKoWatchMs : kWatchMs;
    // Providers may confirm synchronously. Publish the observation before any native call.
    g_observing.emplace(token, Observation{eid, body.peer, body.entity, body.actor, now + watchMs, action});
    bool watching = false;
    const char* refused = "engine-refused";
    if (event.kind == rpcombat::kKo) {
        watching = wo131::rp_knockout(eid, event.stage != 0, token);
    } else if (event.kind == rpcombat::kRanged) {
        watching = wo143::rp_ranged(eid, event.stage, event.rangedPhase, event.itemClass.data(), token);
    } else {
        const auto result = rttr::queue_ghost_row(eid, typed.row, token, &watching);
        if (result == rttr::SwingResult::Ok) {
            wo187::note_move(eid);
            refused = nullptr;
        } else {
            refused = rttr::swing_result_name(result);
        }
    }
    if (watching) {
        if (g_observing.find(token) != g_observing.end())
            report(eid, body.peer, action, "queued", 0, timed ? "timed" : "missing-current-pose");
    } else if (g_observing.erase(token) != 0) {
        settle(eid, body.peer, action, refused ? "refused" : "unconfirmed", refused ? refused : "watch-unavailable");
    }
}

} // namespace

void configure(bool enabled) {
    synchronize();
    // A new relay clock invalidates queued host times and cached setup. A
    // tracked wake can still recover the same body when presentation is off.
    for (auto& body : g_bodies) {
        forget_pending(body.first, body.second, "clock-changed", !enabled);
        body.second.rows.clear();
        body.second.legacyRecent.clear();
    }
    g_enabled = enabled;
}

void forget(uint32_t eid) {
    const auto body = g_bodies.find(eid);
    if (body != g_bodies.end()) {
        forget_pending(eid, body->second, "body-released");
        g_bodies.erase(body);
    }
    for (auto it = g_observing.begin(); it != g_observing.end();) {
        const auto& watched = it->second;
        if (watched.eid != eid) {
            ++it;
            continue;
        }
        settle(eid, watched.peer, watched.action, "unconfirmed", "body-released");
        it = g_observing.erase(it);
    }
}

void reset() {
    for (auto& body : g_bodies) forget_pending(body.first, body.second, "scope-changed");
    for (auto& observation : g_observing) {
        const auto& watched = observation.second;
        settle(watched.eid, watched.peer, watched.action, "unconfirmed", "scope-changed");
    }
    g_bodies.clear();
    g_observing.clear();
    g_pending = 0;
    g_combat = {};
    g_actionLag.reset();
    g_koLag.reset();
}

Receipt submit(const uint8_t* bytes, size_t size, Scope received, uint64_t readMs) {
    Action action;
    if (!decode(bytes, size, action)) return Invalid;
    const Scope current = synchronize();
    if (!current.connection || !(received == current)) return Invalid;
    if (g_remoteCombat) return Blocked;
    if (!g_enabled || !wo216::lifecycle_gameplay()) return Unavailable;
    void* entity = engine::entity_by_id(action.eid);
    if (!entity_matches(action.eid, action.peer, entity)) return Invalid;
    if (!npcdrive::is_bound_eid(action.eid) || wo208::probe_owned(action.eid)) return Unavailable;
    Body* body = lookup_body(action.eid, action.peer, entity, motion::actor_of(action.eid));
    if (!body) return Unavailable;
    const auto key = std::make_pair(action.sequence, action.hostMs);
    if (std::find(body->legacyRecent.begin(), body->legacyRecent.end(), key) != body->legacyRecent.end()) return Accepted;
    if (g_pending >= kMaxPending || body->pending.size() >= kMaxPerBody) return Unavailable;
    body->pending.push_back({action.hostMs, readMs, action.sequence, action.kind, action.phase, std::string(action.spec), {}});
    body->legacyRecent.push_back(key);
    if (body->legacyRecent.size() > kLegacyRecent) body->legacyRecent.pop_front();
    ++g_pending;
    return Accepted;
}

Receipt register_combat(const uint8_t* bytes, size_t size, Scope received) {
    rpcombat::Registration registration;
    if (!rpcombat::decode_registration(bytes, size, registration)) return Invalid;
    const Scope current = synchronize();
    if (!current.connection || !(received == current)) return Invalid;
    if (!g_remoteCombat || !g_enabled) return Unavailable;
    const auto eid = registration.eid;
    void* entity = engine::entity_by_id(eid);
    if (!entity_matches(eid, registration.peer, entity) || !npcdrive::is_bound_eid(eid)) return Invalid;
    if (!wo216::lifecycle_gameplay() && !matched_frame(eid, registration.peer).appliedHostMs) return Blocked;
    void* actor = motion::actor_of(eid);
    if (!actor || wo208::probe_owned(eid) || !hits::visual_damage_ready(eid)) return Blocked;
    Body* body = lookup_body(eid, registration.peer, entity, actor);
    if (!body) return Blocked;
    auto old = body->rows.find(registration.rowKey);
    if (old != body->rows.end()) {
        if (old->second.kind != registration.kind || old->second.spec != registration.spec) return Blocked;
        if (rttr::combat_row_current(eid, old->second.descriptor)) return Accepted;
        body->rows.erase(old);
    }
    if (body->rows.size() >= kMaxRowsPerBody) return Blocked;
    Row row{registration.kind, std::string(registration.spec), {}};
    if (rttr::prepare_ghost_row(eid, row.spec.c_str(), &row.descriptor) != rttr::SwingResult::Ok) return Blocked;
    body->rows.emplace(registration.rowKey, std::move(row));
    return Accepted;
}

Receipt submit_combat(const uint8_t* bytes, size_t size, Scope received, uint64_t readMs) {
    rpcombat::Event event;
    if (!rpcombat::decode_event(bytes, size, event)) return Invalid;
    const Scope current = synchronize();
    if (!current.connection || !(received == current)) return Invalid;
    const auto now = GetTickCount64();
    if (!readMs || readMs > now) return Invalid;
    const auto eid = event.eid;
    void* entity = engine::entity_by_id(eid);
    if (!entity_matches(eid, event.peer, entity) || !npcdrive::is_bound_eid(eid)) return Invalid;
    void* actor = motion::actor_of(eid);
    if (!actor) return Blocked;
    Body* body = lookup_body(eid, event.peer, entity, actor);
    if (!body) return Blocked;
    const auto key = rpcombat::key(event);
    if (const auto old = body->typedRecent.find(key)) return *old;

    const bool recovering = tracked_wake(event, *body);
    if (!recovering && ((!g_enabled || !g_remoteCombat) || (event.kind == rpcombat::kKo && event.stage == 0)))
        return Unavailable;
    const auto frame = matched_frame(eid, event.peer);
    if (!wo216::lifecycle_gameplay() && !frame.appliedHostMs && !recovering) return Blocked;
    body->typedRecent.observe_target(frame.targetHostMs);
    if (body->typedRecent.full()) return recovering ? Unavailable : Blocked;
    Typed typed{event, {}};
    if (rpcombat::uses_row(event.kind)) {
        const auto row = body->rows.find(event.rowKey);
        if (row == body->rows.end()) return RowNotRegistered;
        if (row->second.kind != event.kind) return Blocked;
        if (!rttr::combat_row_current(eid, row->second.descriptor)) {
            body->rows.erase(row);
            return RowNotRegistered;
        }
        typed.row = row->second.descriptor;
    }

    if (!recovering && body->typedRecent.too_old(key)) return Blocked;
    const bool refuse = wo208::probe_owned(eid) || g_pending >= kMaxPending || body->pending.size() >= kMaxPerBody;
    const bool ready = !refuse && ready_to_dispatch(event);
    // A recovery rejected before scheduling stays retryable on this exact
    // body. The provider proves its down state again when eventually invoked.
    if (recovering && !ready) return Unavailable;
    Pending action{event.hostMs, readMs, event.sequence, event.kind, event.phase, {}, typed};
    ++g_combat.actions_in;
    report(eid, event.peer, action, "in");
    if (!ready) {
        report(eid, event.peer, action, "refused", 0, refuse ? "admission-gate" : "provider-unavailable");
        body->typedRecent.remember(key, Blocked);
        return !refuse && !rpcombat::uses_row(event.kind) ? Unavailable : Blocked;
    }
    body->pending.push_back(std::move(action));
    ++g_pending;
    ++g_combat.actions_pending;
    return body->typedRecent.remember(key, Accepted);
}

void tick() {
    const Scope scope = synchronize();
    wo131::rp_knockout_tick();
    if (!scope.connection) return;
    const uint64_t now = GetTickCount64();
    for (auto it = g_observing.begin(); it != g_observing.end();) {
        const auto& watched = it->second;
        const bool matches = body_matches(watched.eid, watched.peer, watched.entity, watched.actor);
        if (matches && now < watched.deadline) {
            ++it;
            continue;
        }
        settle(watched.eid, watched.peer, watched.action, "unconfirmed", matches ? "watch-timeout" : "body-replaced");
        it = g_observing.erase(it);
    }
    for (auto it = g_bodies.begin(); it != g_bodies.end();) {
        const uint32_t eid = it->first;
        Body& body = it->second;
        if (body.pending.empty()) {
            ++it;
            continue;
        }
        if (!body_matches(eid, body.peer, body.entity, body.actor)) {
            forget_pending(eid, body, "body-replaced");
            it = g_bodies.erase(it);
            continue;
        }
        ++it;
        const auto frame = matched_frame(eid, body.peer);
        const auto& next = body.pending.front();
        const bool timed = due(next.hostMs, frame.appliedHostMs, frame.targetHostMs);
        auto schedule = rpcombat::Schedule::Run;
        if (next.typed) {
            const bool recovery = wake(next) && wo131::rp_knockout_wake_pending(eid);
            schedule = rpcombat::schedule(next.hostMs, frame.appliedHostMs, frame.targetHostMs, next.readMs, now, recovery);
            if ((!g_enabled || !g_remoteCombat) && !recovery) schedule = rpcombat::Schedule::Expire;
            if (schedule == rpcombat::Schedule::Wait) continue;
        } else if (g_enabled && !timed && !waited(next.readMs, now)) continue;
        Pending action = std::move(body.pending.front());
        body.pending.pop_front();
        --g_pending;
        if (action.typed) {
            if (schedule == rpcombat::Schedule::Expire) settle(eid, body.peer, action, "expired", "missing-current-clock");
            else dispatch_typed(eid, body, std::move(action), timed, now);
            continue;
        }
        const uint64_t token = ++g_nextToken;
        bool watching = false;
        const auto result = rttr::ghost_swing(eid, action.spec.c_str(), token, &watching);
        if (result != rttr::SwingResult::Ok) {
            report(eid, body.peer, action, "refused", 0, rttr::swing_result_name(result));
            continue;
        }
        wo187::note_move(eid);
        rpmeasure::note_action(eid, action.readMs);
        report(eid, body.peer, action, "queued", 0, timed ? "timed" : "missing-current-pose");
        if (watching) g_observing.emplace(token, Observation{eid, body.peer, body.entity, body.actor, now + kWatchMs, std::move(action)});
        else report(eid, body.peer, action, "unconfirmed", 0, "watch-unavailable");
    }
}

void observed(uint64_t token, bool installed) {
    if (!(scope_now() == g_scope)) return;
    const auto it = g_observing.find(token);
    if (it == g_observing.end()) return;
    auto entry = std::move(it->second);
    g_observing.erase(it);
    if (!installed || !body_matches(entry.eid, entry.peer, entry.entity, entry.actor)) {
        settle(entry.eid, entry.peer, entry.action, "unconfirmed", installed ? "body-replaced" : "not-installed");
        return;
    }
    const uint64_t shown = matched_frame(entry.eid, entry.peer).appliedHostMs;
    if (shown && (!entry.action.typed || rpcombat::uses_row(entry.action.kind)))
        rptimeline::note_action_pose(entry.action.hostMs, shown);
    if (entry.action.typed) {
        ++g_combat.actions_played;
        const auto ms = elapsed(entry.action.readMs, GetTickCount64());
        g_actionLag.add(static_cast<uint32_t>(ms));
        if (entry.action.kind == rpcombat::kKo) g_koLag.add(static_cast<uint32_t>(ms));
    }
    settle(entry.eid, entry.peer, entry.action, "installed", shown ? "-" : "missing-current-pose", shown);
}

static rpcombat::Counts combat_snapshot() {
    synchronize();
    auto counts = g_combat;
    const auto action = g_actionLag.summary();
    const auto ko = g_koLag.summary();
    counts.action_lag_ms_p95 = action.p95;
    counts.action_confirmed_n = action.observed;
    counts.ko_up_lag_ms_p95 = ko.p95;
    counts.ko_up_n = ko.observed;
    return counts;
}

} // namespace kcdmp::timelineactions

namespace kcdmp::rpcombat {

Counts snapshot() { return timelineactions::combat_snapshot(); }

} // namespace kcdmp::rpcombat
