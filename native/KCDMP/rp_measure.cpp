// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#include "rp_measure.h"
#include "log.h"
#include "pipe_server.h"
#include "wo216.h"

#include <windows.h>

namespace kcdmp::rpmeasure {
namespace {

Ledger g_ledger;
Window g_window;
uint64_t g_last_report = 0;
int64_t g_window_start_qpc = 0;
uint64_t g_window_start_ms = 0;

void clear_observations() {
    g_ledger.reset();
    g_last_report = 0;
}

ReceiverScope synchronize() {
    const ReceiverScope receiver{wo216::lifecycle_observation_stamp(), pipe::observation_connection()};
    if (g_window.synchronize(receiver)) {
        clear_observations();
        g_window_start_qpc = 0;
        g_window_start_ms = 0;
    }
    return receiver;
}

void action_latency(uint8_t peer, uint64_t pipeReadMs) {
    const uint64_t now = GetTickCount64();
    if (pipeReadMs != 0 && pipeReadMs >= g_window_start_ms && now >= pipeReadMs)
        g_ledger.action(peer, static_cast<double>(now - pipeReadMs));
}

void report_figure(unsigned peer, bool final) {
    const auto values = g_ledger.peer_counts(static_cast<uint8_t>(peer));
    if (final ? !values[11] : !values[11] && !values[12] && !values[13] && !values[15]) return;
    logf("MP-RP-MEASURE figure=kcd2mp_%u gen=%llu clock=%s proof=%s final=%u action_origin=dll_receipt_to_request "
         "rp_e2e_ms_p50=%llu rp_e2e_ms_p95=%llu rp_e2e_ms_max=%llu rp_agent_hop_ms_p95=%llu "
         "rp_corr_n=%llu rp_corr_cm_p95=%llu rp_corr_cm_max=%llu rp_hard_place=%llu "
         "rp_action_lag_ms_p95=%llu rp_e2e_n=%llu rp_agent_hop_n=%llu rp_action_n=%llu rp_pose_n=%llu",
         peer, static_cast<unsigned long long>(wo216::lifecycle_generation()),
         g_window.shared_clock() ? "same_pc_qpc" : "unknown", g_window.gameplay() ? "lifecycle" : "writer", final ? 1u : 0u,
         static_cast<unsigned long long>(values[0]), static_cast<unsigned long long>(values[1]),
         static_cast<unsigned long long>(values[2]), static_cast<unsigned long long>(values[3]),
         static_cast<unsigned long long>(values[4]), static_cast<unsigned long long>(values[5]),
         static_cast<unsigned long long>(values[6]), static_cast<unsigned long long>(values[7]),
         static_cast<unsigned long long>(values[9]), static_cast<unsigned long long>(values[11]),
         static_cast<unsigned long long>(values[12]), static_cast<unsigned long long>(values[13]),
         static_cast<unsigned long long>(values[15]));
}

} // namespace

bool configure_clock(uint8_t value) {
    char setting[2]{};
    const bool allowed = GetEnvironmentVariableA("KCDMP_TEST_RP_MEASURE", setting, sizeof setting) == 1 && setting[0] == '1';
    bool enabled = false;
    if (!clock_config(allowed, value, enabled)) return false;
    const auto receiver = synchronize();
    if (enabled && !receiver.connected()) return false;
    if (enabled || g_window.shared_clock()) {
        clear_observations();
        LARGE_INTEGER start{};
        g_window_start_qpc = QueryPerformanceCounter(&start) ? start.QuadPart : (std::numeric_limits<int64_t>::max)();
        g_window_start_ms = GetTickCount64();
    }
    g_window.configure(enabled);
    logf("MP-RP-CONFIG clock=%s source=same_pc_qpc", enabled ? "on" : "off");
    return true;
}

bool shared_clock() {
    synchronize();
    return g_window.shared_clock();
}

void forget(uint32_t eid) {
    synchronize();
    uint8_t peer = 0;
    if (g_window.shared_clock() && g_ledger.peer_of(eid, peer)) report_figure(peer, true);
    g_ledger.forget(eid);
}

void note_hop(uint8_t peer, SampleScope source, int64_t agentArrivalQpc, double milliseconds) {
    const auto receiver = synchronize();
    if (g_window.can_report() && (g_window.gameplay() || g_ledger.has_body(peer)) &&
        sample_in_window(source, receiver, g_window_start_qpc) &&
        agentArrivalQpc >= g_window_start_qpc)
        g_ledger.hop(peer, milliseconds);
}

void note_pose(uint8_t peer, uint32_t eid, SampleScope source, uint32_t senderMs, uint32_t appliedMs,
               double correctionCm, bool hardPlacement) {
    const auto receiver = synchronize();
    uint32_t age = 0;
    const bool validWriterSource = eid != 0 && g_window.shared_clock() &&
                                   sample_in_window(source, receiver, g_window_start_qpc) &&
                                   sender_age_ms(senderMs, appliedMs, age);
    if (g_window.observe_pose(g_ledger.has_body(peer, eid), validWriterSource))
        g_ledger.pose(peer, eid, senderMs, appliedMs, validWriterSource, correctionCm, hardPlacement);
}

void note_action(uint32_t eid, uint64_t pipeReadMs) {
    synchronize();
    uint8_t peer = 0;
    if (g_window.gameplay() && g_ledger.peer_of(eid, peer)) action_latency(peer, pipeReadMs);
}

void note_action(std::string_view name, uint64_t pipeReadMs) {
    synchronize();
    uint8_t peer = 0;
    if (g_window.gameplay() && partner_id(name, peer)) action_latency(peer, pipeReadMs);
}

Counts snapshot() {
    synchronize();
    if (!g_window.can_report()) return {};
    const uint64_t now = GetTickCount64();
    if (g_last_report == 0 || now - g_last_report >= 60000) {
        g_last_report = now;
        for (unsigned peer = 0; peer < 256; ++peer) report_figure(peer, false);
    }
    return g_ledger.counts();
}

} // namespace kcdmp::rpmeasure
