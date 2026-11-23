// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#include "rp_timeline.h"
#include "log.h"
#include "pipe_server.h"
#include "wo216.h"

#include <windows.h>

namespace kcdmp::rptimeline {
namespace {

struct Body {
    uint32_t eid = 0;
    Frame display{};
    uint64_t poseCount = 0, late = 0, lastReport = 0;
    uint64_t rangedHostMs = 0;
    uint32_t buffer = 0, extrap = 0;
    uint8_t rangedBits = 0, rangedCharge = 0;
    rangedaim::Angles rangedAngles{};
};

bool g_enabled = true;
rpmeasure::ReceiverScope g_receiver;
std::array<Body, 256> g_bodies;
Counts g_total;
rpmeasure::detail::Histogram g_skew;

void clear() {
    g_bodies.fill({});
    g_total = {};
    g_skew.reset();
}

Body& body_for(uint32_t eid, uint8_t peer) {
    auto& row = g_bodies[peer];
    if (row.eid == eid) return row;
    const auto previousLate = row.eid == 0 ? row.late : 0;
    row = {};
    row.eid = eid;
    row.late = previousLate;
    row.display.lifecycle = g_receiver.lifecycle;
    row.display.connection = g_receiver.connection;
    return row;
}

void report(const Body& row, uint8_t peer) {
    logf("MP-RP-TIMELINE figure=kcd2mp_%u eid=0x%X scope=%llu connection=%llu buffer_ms=%u extrap_ms_max=%u "
         "late_samples=%llu pose_n=%llu applied_host_ms=%llu target_host_ms=%llu "
         "ranged_held=%u ranged_state_ms=%llu ranged_charge_known=%u ranged_charge=%u aim_yaw_cd=%d aim_pitch_cd=%d",
         static_cast<unsigned>(peer), row.eid, static_cast<unsigned long long>(row.display.lifecycle),
         static_cast<unsigned long long>(row.display.connection), row.buffer, row.extrap,
         static_cast<unsigned long long>(row.late), static_cast<unsigned long long>(row.poseCount),
         static_cast<unsigned long long>(row.display.appliedHostMs), static_cast<unsigned long long>(row.display.targetHostMs),
         (row.rangedBits & rangedaim::kHeldBit) ? 1u : 0u, static_cast<unsigned long long>(row.rangedHostMs),
         (row.rangedBits & rangedaim::kChargeKnownBit) ? 1u : 0u, static_cast<unsigned>(row.rangedCharge),
         row.rangedAngles.yaw, row.rangedAngles.pitch);
}

bool same_receiver() {
    return g_receiver == rpmeasure::ReceiverScope{wo216::lifecycle_observation_stamp(), pipe::observation_connection()};
}

} // namespace

bool enabled() { return g_enabled; }

void configure(bool on) {
    g_enabled = on;
    clear();
}

bool synchronize(rpmeasure::ReceiverScope receiver) {
    if (receiver == g_receiver) return false;
    g_receiver = receiver;
    clear();
    return true;
}

void note_received(uint8_t peer, uint64_t hostMs) {
    if (!g_enabled || !hostMs || !same_receiver() || hostMs > g_bodies[peer].display.targetHostMs) return;
    ++g_total.late_samples;
    ++g_bodies[peer].late;
}

void note_target(uint32_t eid, uint8_t peer, uint64_t targetHostMs) {
    if (!g_enabled || !eid || !targetHostMs) return;
    body_for(eid, peer).display.targetHostMs = targetHostMs;
}

void note_pose(uint32_t eid, uint8_t peer, const Output& sample) {
    if (!g_enabled || !eid || !sample.ready() || !same_receiver()) return;
    auto& row = body_for(eid, peer);
    row.display.targetHostMs = sample.targetHostMs;
    g_total.buffer_ms = row.buffer = sample.bufferMs;
    row.extrap = (std::max)(row.extrap, sample.extrapMs);
    g_total.extrap_ms_max = (std::max)(g_total.extrap_ms_max, static_cast<uint64_t>(sample.extrapMs));
    if (sample.appliedHostMs != row.display.appliedHostMs) {
        ++row.poseCount;
        ++g_total.pose_n;
        row.display.appliedHostMs = sample.appliedHostMs;
    }
    if (row.rangedHostMs != sample.rangedStateHostMs) {
        row.rangedHostMs = sample.rangedStateHostMs;
        row.rangedBits = sample.ranged.bits;
        row.rangedCharge = sample.ranged.charge;
    }
    row.rangedAngles = sample.ranged.angles_known() ? rangedaim::Angles{sample.ranged.yaw, sample.ranged.pitch} : rangedaim::Angles{};
    const uint64_t now = GetTickCount64();
    if (row.lastReport == 0 || now - row.lastReport >= 4000) {
        row.lastReport = now;
        report(row, peer);
    }
}

void note_action_pose(uint64_t actionHostMs, uint64_t appliedPoseHostMs) {
    if (!g_enabled || !actionHostMs || !appliedPoseHostMs || !same_receiver()) return;
    const auto skew = (std::min)(difference(actionHostMs, appliedPoseHostMs),
                                 static_cast<uint64_t>((std::numeric_limits<uint32_t>::max)()));
    g_skew.add(static_cast<uint32_t>(skew));
}

void forget(uint32_t eid) {
    if (!eid) return;
    for (size_t i = 0; i < g_bodies.size(); ++i) {
        auto& row = g_bodies[i];
        if (row.eid != eid) continue;
        if (row.poseCount && same_receiver()) report(row, static_cast<uint8_t>(i));
        row = {};
    }
}

Frame frame(uint32_t eid, uint8_t peer) {
    if (!g_enabled || !eid || !same_receiver()) return {};
    const auto& row = g_bodies[peer];
    return row.eid == eid ? row.display : Frame{};
}

Counts snapshot() {
    if (!g_enabled || !same_receiver()) return {};
    auto out = g_total;
    const auto summary = g_skew.summary();
    out.action_n = summary.observed;
    out.action_pos_skew_ms_p95 = summary.p95;
    return out;
}

} // namespace kcdmp::rptimeline
