// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#include "rp_controller.h"
#include "log.h"
#include "pipe_server.h"
#include "wo216.h"
#include "wo221.h"

#include <windows.h>

namespace kcdmp::rpctrl {
namespace {

struct Body {
    uint32_t eid = 0;
    uint64_t lastReport = 0;
    Fallback previous = Fallback::None;
    Counts counts;
    std::array<uint64_t, 4> gait{};
};

std::array<Body, 256> g_bodies;
Counts g_total;
rpmeasure::ReceiverScope g_scope;
bool g_on = false;

bool current() {
    return g_on && g_scope.connected() && wo221::on(wo221::kBitRemoteController) &&
           g_scope == rpmeasure::ReceiverScope{wo216::lifecycle_observation_stamp(), pipe::observation_connection()};
}

void report(const Body& b, unsigned peer) {
    const auto& c = b.counts;
    using U = unsigned long long;
    logf("MP-RP-CTRL figure=kcd2mp_%u eid=0x%X scope=%llu connection=%llu ctrl_frames=%llu req_ok=%llu req_fail=%llu "
         "fallback_jump=%llu fallback_mount=%llu fallback_ragdoll=%llu fallback_air=%llu hard_tp_join=%llu hard_tp_respawn=%llu "
         "hard_tp_travel=%llu hard_tp_unapproved=%llu pulls=%llu pull_observed_ms=%llu pulls_per_min=%llu "
         "gait_idle=%llu gait_walk=%llu gait_run=%llu gait_sprint=%llu",
         peer, b.eid, static_cast<U>(g_scope.lifecycle), static_cast<U>(g_scope.connection),
         static_cast<U>(c.ctrl_frames), static_cast<U>(c.req_ok), static_cast<U>(c.req_fail), static_cast<U>(c.fallback_jump),
         static_cast<U>(c.fallback_mount), static_cast<U>(c.fallback_ragdoll), static_cast<U>(c.fallback_air),
         static_cast<U>(c.hard_tp_join), static_cast<U>(c.hard_tp_respawn), static_cast<U>(c.hard_tp_travel),
         static_cast<U>(c.hard_tp_unapproved), static_cast<U>(c.pulls), static_cast<U>(c.pull_observed_ms),
         static_cast<U>(pulls_per_min(c.pulls, c.pull_observed_ms)),
         static_cast<U>(b.gait[0]), static_cast<U>(b.gait[1]), static_cast<U>(b.gait[2]), static_cast<U>(b.gait[3]));
}

Body& body(uint32_t eid, uint8_t peer) {
    auto& row = g_bodies[peer];
    if (row.eid != eid) { row = {}; row.eid = eid; }
    return row;
}

void maybe_report(Body& row, uint8_t peer) {
    const uint64_t now = GetTickCount64();
    if (!row.lastReport || now - row.lastReport >= 4000) {
        row.lastReport = now;
        report(row, peer);
    }
}

} // namespace

void synchronize(bool on, rpmeasure::ReceiverScope scope) {
    if (g_on == on && g_scope == scope) return;
    g_on = on;
    g_scope = scope;
    g_bodies.fill({});
    g_total = {};
}

void request(uint32_t eid, uint8_t peer, bool accepted, unsigned gait) {
    if (!eid || !current()) return;
    auto& row = body(eid, peer);
    if (accepted) {
        ++row.counts.ctrl_frames; ++g_total.ctrl_frames;
        ++row.counts.req_ok; ++g_total.req_ok;
        ++row.gait[(std::min)(gait, 3u)];
        row.previous = Fallback::None;
    } else {
        ++row.counts.req_fail; ++g_total.req_fail;
    }
    maybe_report(row, peer);
}

void fallback(uint32_t eid, uint8_t peer, Fallback why) {
    if (!eid || !current()) return;
    auto& row = body(eid, peer);
    if (row.previous == why) return;
    row.previous = why;
    switch (why) {
        case Fallback::Jump: ++row.counts.fallback_jump; ++g_total.fallback_jump; break;
        case Fallback::Mount: ++row.counts.fallback_mount; ++g_total.fallback_mount; break;
        case Fallback::Ragdoll: ++row.counts.fallback_ragdoll; ++g_total.fallback_ragdoll; break;
        case Fallback::Air: ++row.counts.fallback_air; ++g_total.fallback_air; break;
        case Fallback::None: return;
    }
    maybe_report(row, peer);
}

void place(uint32_t eid, uint8_t peer, Relocation reason) {
    if (!eid || !current()) return;
    auto& row = body(eid, peer);
    switch (reason) {
        case Relocation::Join: ++row.counts.hard_tp_join; ++g_total.hard_tp_join; break;
        case Relocation::Respawn: ++row.counts.hard_tp_respawn; ++g_total.hard_tp_respawn; break;
        case Relocation::Travel: ++row.counts.hard_tp_travel; ++g_total.hard_tp_travel; break;
        case Relocation::None: ++row.counts.hard_tp_unapproved; ++g_total.hard_tp_unapproved; break;
    }
    report(row, peer);
}

void observed(uint32_t eid, uint8_t peer, uint64_t elapsedMs, bool pulled) {
    if (!eid || !elapsedMs || !current()) return;
    auto& row = body(eid, peer);
    row.counts.pull_observed_ms += elapsedMs; g_total.pull_observed_ms += elapsedMs;
    if (pulled) { ++row.counts.pulls; ++g_total.pulls; }
}

void forget(uint32_t eid) {
    if (!eid) return;
    for (unsigned peer = 0; peer < g_bodies.size(); ++peer) {
        auto& row = g_bodies[peer];
        if (row.eid != eid) continue;
        if (row.lastReport && current()) report(row, peer);
        row = {};
    }
}

Counts snapshot() {
    if (!current()) return {};
    auto result = g_total;
    result.pulls_per_min = pulls_per_min(result.pulls, result.pull_observed_ms);
    return result;
}

} // namespace kcdmp::rpctrl
