// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#include "rp_timeline_actions.h"

#include "combat_swing.h"
#include "engine.h"
#include "log.h"
#include "npc_drive.h"
#include "p4.h"
#include "pipe_server.h"
#include "rp_measure.h"
#include "rp_timeline.h"
#include "wo187.h"
#include "wo208.h"
#include "wo216.h"

#include <windows.h>

#include <algorithm>
#include <deque>
#include <string>
#include <unordered_map>
#include <utility>

namespace kcdmp::timelineactions {
namespace {

constexpr size_t kMaxPending = 128, kMaxPerBody = 32, kRecentEvents = 64;

struct Pending {
    uint64_t hostMs, readMs;
    uint16_t sequence;
    uint8_t kind, phase;
    std::string spec;
};
struct Body {
    void* entity = nullptr;
    uint8_t peer = 0;
    std::deque<Pending> pending;
    std::deque<std::pair<uint16_t, uint64_t>> recent;
};
struct Observation {
    uint32_t eid;
    uint8_t peer;
    void* entity;
    Pending action;
};

bool g_enabled = false;
Scope g_scope;
size_t g_pending = 0;
uint64_t g_nextToken = 0;
std::unordered_map<uint32_t, Body> g_bodies;
std::unordered_map<uint64_t, Observation> g_observing;

Scope scope_now() {
    return {wo216::lifecycle_observation_stamp(), pipe::observation_connection(), p4::life_edge_tick()};
}

void report(uint32_t eid, uint8_t peer, const Pending& action, const char* result, uint64_t poseHostMs = 0, const char* reason = "-") {
    const uint64_t distance = poseHostMs ? skew(action.hostMs, poseHostMs) : 0;
    logf("MP-RP-TIMELINE-ACTION peer=%u eid=0x%X kind=%u phase=%u seq=%u event_host_ms=%llu pose_host_ms=%llu skew_ms=%llu result=%s "
         "reason=%s scope=%llu connection=%llu", peer, eid, action.kind, action.phase, action.sequence,
         static_cast<unsigned long long>(action.hostMs), static_cast<unsigned long long>(poseHostMs),
         static_cast<unsigned long long>(distance), result, reason, static_cast<unsigned long long>(g_scope.lifecycle),
         static_cast<unsigned long long>(g_scope.connection));
}

void forget_pending(uint32_t eid, Body& body, const char* reason) {
    for (const auto& action : body.pending) report(eid, body.peer, action, "expired", 0, reason);
    g_pending -= body.pending.size();
}

Scope synchronize() {
    const auto scope = scope_now();
    if (!(g_scope == scope)) {
        reset();
        g_scope = scope;
    }
    return scope;
}

bool entity_matches(uint32_t eid, uint8_t peer, void* entity) {
    if (!entity || entity != engine::entity_by_id(eid)) return false;
    const char* name = engine::entity_name(entity);
    uint8_t actual = 0;
    return name && rpmeasure::partner_id(name, actual) && actual == peer;
}

bool body_matches(uint32_t eid, uint8_t peer, void* entity) {
    return npcdrive::is_bound_eid(eid) && entity_matches(eid, peer, entity);
}

uint64_t matched_pose(uint32_t eid, uint8_t peer) {
    const auto frame = rptimeline::frame(eid, peer);
    return frame.lifecycle == g_scope.lifecycle && frame.connection == g_scope.connection ? frame.appliedHostMs : 0;
}

} // namespace

void configure(bool enabled) {
    // Re-enabling is the Agent's barrier for a new relay clock epoch. None of
    // its future poses can share a queued action's previous clock basis.
    if (enabled) reset();
    g_enabled = enabled;
}

void reset() {
    for (auto& body : g_bodies) forget_pending(body.first, body.second, "scope-changed");
    for (auto& observation : g_observing) {
        const auto& watched = observation.second;
        report(watched.eid, watched.peer, watched.action, "unconfirmed", 0, "scope-changed");
    }
    g_bodies.clear();
    g_observing.clear();
    g_pending = 0;
}

Receipt submit(const uint8_t* bytes, size_t size, Scope received, uint64_t readMs) {
    Action action;
    if (!decode(bytes, size, action)) return Invalid;
    const Scope current = synchronize();
    if (!current.connection || !(received == current)) return Invalid;
    if (!g_enabled || !wo216::lifecycle_gameplay()) return Unavailable;
    void* entity = engine::entity_by_id(action.eid);
    if (!entity_matches(action.eid, action.peer, entity)) return Invalid;
    if (!npcdrive::is_bound_eid(action.eid) || wo208::probe_owned(action.eid)) return Unavailable;
    Body& body = g_bodies[action.eid];
    if (body.entity != entity || body.peer != action.peer) {
        forget_pending(action.eid, body, "body-replaced");
        body = Body{};
        body.entity = entity;
        body.peer = action.peer;
    }
    const auto key = std::make_pair(action.sequence, action.hostMs);
    if (std::find(body.recent.begin(), body.recent.end(), key) != body.recent.end()) return Accepted;
    if (g_pending >= kMaxPending || body.pending.size() >= kMaxPerBody) return Unavailable;
    body.pending.push_back({action.hostMs, readMs, action.sequence, action.kind, action.phase, std::string(action.spec)});
    body.recent.push_back(key);
    if (body.recent.size() > kRecentEvents) body.recent.pop_front();
    ++g_pending;
    return Accepted;
}

void tick() {
    const Scope scope = synchronize();
    if (!scope.connection || !g_pending) return;
    const uint64_t now = GetTickCount64();
    for (auto it = g_bodies.begin(); it != g_bodies.end();) {
        const uint32_t eid = it->first;
        Body& body = it->second;
        if (!body_matches(eid, body.peer, body.entity)) {
            forget_pending(eid, body, "body-replaced");
            it = g_bodies.erase(it);
            continue;
        }
        ++it;
        if (body.pending.empty()) continue;
        const auto frame = rptimeline::frame(eid, body.peer);
        const auto& next = body.pending.front();
        const bool timed = frame.lifecycle == scope.lifecycle && frame.connection == scope.connection &&
                           due(next.hostMs, frame.appliedHostMs, frame.targetHostMs);
        if (g_enabled && !timed && !waited(next.readMs, now)) continue;
        Pending action = std::move(body.pending.front());
        body.pending.pop_front();
        --g_pending;
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
        if (watching) g_observing.emplace(token, Observation{eid, body.peer, body.entity, std::move(action)});
        else report(eid, body.peer, action, "unconfirmed", 0, "watch-unavailable");
    }
}

void observed(uint64_t token, bool installed) {
    if (!(scope_now() == g_scope)) return;
    const auto it = g_observing.find(token);
    if (it == g_observing.end()) return;
    auto entry = std::move(it->second);
    g_observing.erase(it);
    if (!installed || !body_matches(entry.eid, entry.peer, entry.entity)) {
        report(entry.eid, entry.peer, entry.action, "unconfirmed", 0, installed ? "body-replaced" : "not-installed");
        return;
    }
    const uint64_t shown = matched_pose(entry.eid, entry.peer);
    if (shown) rptimeline::note_action_pose(entry.action.hostMs, shown);
    report(entry.eid, entry.peer, entry.action, "installed", shown, shown ? "-" : "missing-current-pose");
}

} // namespace kcdmp::timelineactions
