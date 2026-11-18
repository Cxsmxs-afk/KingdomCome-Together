// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#include "rp_measure.h"
#include "log.h"
#include "wo216.h"

#include <windows.h>

namespace kcdmp::rpmeasure {
namespace {

Ledger g_ledger;
uint64_t g_generation = 0;
uint64_t g_last_report = 0;
bool g_shared_clock = false;
int64_t g_window_start_qpc = 0;
uint64_t g_window_start_ms = 0;

bool current_gameplay() {
    const uint64_t generation = wo216::lifecycle_generation();
    if (generation != g_generation) {
        g_ledger.reset();
        g_generation = generation;
        g_last_report = 0;
    }
    return generation != 0 && wo216::lifecycle_gameplay();
}

void action_latency(uint8_t peer, uint64_t pipeReadMs) {
    const uint64_t now = GetTickCount64();
    if (pipeReadMs != 0 && pipeReadMs >= g_window_start_ms && now >= pipeReadMs)
        g_ledger.action(peer, static_cast<double>(now - pipeReadMs));
}

} // namespace

bool configure_clock(uint8_t value) {
    char setting[2]{};
    const bool allowed = GetEnvironmentVariableA("KCDMP_TEST_RP_MEASURE", setting, sizeof setting) == 1 && setting[0] == '1';
    const bool previous = g_shared_clock;
    if (!clock_config(allowed, value, g_shared_clock)) return false;
    if (g_shared_clock || g_shared_clock != previous) {
        g_ledger.reset();
        g_last_report = 0;
        LARGE_INTEGER start{};
        g_window_start_qpc = QueryPerformanceCounter(&start) ? start.QuadPart : (std::numeric_limits<int64_t>::max)();
        g_window_start_ms = GetTickCount64();
    }
    logf("MP-RP-CONFIG clock=%s source=same_pc_qpc", g_shared_clock ? "on" : "off");
    return true;
}

bool shared_clock() { return g_shared_clock; }

void on_pipe_closed() {
    g_ledger.reset();
    g_generation = 0;
    g_last_report = 0;
    g_shared_clock = false;
    g_window_start_qpc = 0;
    g_window_start_ms = 0;
}

void forget(uint32_t eid) { g_ledger.forget(eid); }

void note_hop(uint8_t peer, SampleScope source, int64_t agentArrivalQpc, double milliseconds) {
    if (current_gameplay() && sample_in_window(source, g_generation, g_window_start_qpc) &&
        agentArrivalQpc >= g_window_start_qpc)
        g_ledger.hop(peer, milliseconds);
}

void note_pose(uint8_t peer, uint32_t eid, SampleScope source, uint32_t senderMs, uint32_t appliedMs,
               double correctionCm, bool hardPlacement) {
    if (current_gameplay())
        g_ledger.pose(peer, eid, senderMs, appliedMs,
                      g_shared_clock && sample_in_window(source, g_generation, g_window_start_qpc),
                      correctionCm, hardPlacement);
}

void note_action(uint32_t eid, uint64_t pipeReadMs) {
    uint8_t peer = 0;
    if (current_gameplay() && g_ledger.peer_of(eid, peer)) action_latency(peer, pipeReadMs);
}

void note_action(std::string_view name, uint64_t pipeReadMs) {
    uint8_t peer = 0;
    if (current_gameplay() && partner_id(name, peer)) action_latency(peer, pipeReadMs);
}

Counts snapshot() {
    if (!current_gameplay()) return {};
    const uint64_t now = GetTickCount64();
    if (g_last_report == 0 || now - g_last_report >= 60000) {
        g_last_report = now;
        for (unsigned peer = 0; peer < 256; ++peer) {
            const auto values = g_ledger.peer_counts(static_cast<uint8_t>(peer));
            if (!values[11] && !values[12] && !values[13] && !values[15]) continue;
            logf("MP-RP-MEASURE figure=kcd2mp_%u gen=%llu clock=%s action_origin=dll_receipt_to_request "
                 "rp_e2e_ms_p50=%llu rp_e2e_ms_p95=%llu rp_e2e_ms_max=%llu rp_agent_hop_ms_p95=%llu "
                 "rp_corr_n=%llu rp_corr_cm_p95=%llu rp_corr_cm_max=%llu rp_hard_place=%llu "
                 "rp_action_lag_ms_p95=%llu rp_e2e_n=%llu rp_agent_hop_n=%llu rp_action_n=%llu rp_pose_n=%llu",
                 peer, static_cast<unsigned long long>(g_generation), g_shared_clock ? "same_pc_qpc" : "unknown",
                 static_cast<unsigned long long>(values[0]), static_cast<unsigned long long>(values[1]),
                 static_cast<unsigned long long>(values[2]), static_cast<unsigned long long>(values[3]),
                 static_cast<unsigned long long>(values[4]), static_cast<unsigned long long>(values[5]),
                 static_cast<unsigned long long>(values[6]), static_cast<unsigned long long>(values[7]),
                 static_cast<unsigned long long>(values[9]), static_cast<unsigned long long>(values[11]),
                 static_cast<unsigned long long>(values[12]), static_cast<unsigned long long>(values[13]),
                 static_cast<unsigned long long>(values[15]));
        }
    }
    return g_ledger.counts();
}

} // namespace kcdmp::rpmeasure
