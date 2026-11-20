// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#include "load_sequence.h"
#include "log.h"
#include "wo138.h"

#include <windows.h>
#include <atomic>
#include <mutex>

namespace kcdmp::loadsequence {
namespace {
State g_state;
std::mutex g_stateMutex;
std::atomic<uint64_t> g_stamp{0}, g_stale{0}, g_applied{0};
bool g_hostHoldOwned = false; // Main only
uint64_t g_hostHoldAttempt = 0, g_hostHoldConnection = 0;

}

bool control(const uint8_t* body, size_t size, uint64_t connection, uint8_t* ack) {
    Request request;
    const bool decoded = decode(body, size, request);
    std::lock_guard<std::mutex> lock(g_stateMutex);
    const auto previous = g_state.stamp();
    const bool accepted = decoded && g_state.apply(request, connection, GetTickCount64());
    const auto next = g_state.stamp();
    if (previous != next) {
        g_stamp.store(next, std::memory_order_release);
        logf("MP-LOAD-NATIVE phase=%u flags=%u world_gen=%llu revision=%llu", static_cast<unsigned>(g_state.phase()), static_cast<unsigned>(g_state.flags()),
             static_cast<unsigned long long>(g_state.generation()), static_cast<unsigned long long>(next >> 2));
    }
    uint8_t flags = g_state.flags();
    const bool effectCurrent = wo138::join_held() == (g_state.hold_seconds() != 0);
    if (next && effectCurrent && g_applied.load(std::memory_order_acquire) == next) flags |= kMainApplied;
    encode_ack(ack, g_state.phase(), flags, g_state.generation(), request.id);
    return accepted;
}

void disconnected(uint64_t connection) {
    std::lock_guard<std::mutex> lock(g_stateMutex);
    g_state.disconnect(connection, GetTickCount64());
    g_stamp.store(g_state.stamp(), std::memory_order_release);
}

void tick() {
    uint64_t revision, connection;
    uint16_t holdSeconds;
    {
        std::lock_guard<std::mutex> lock(g_stateMutex);
        revision = g_state.stamp();
        connection = g_state.connection();
        holdSeconds = g_state.hold_seconds();
    }
    if (g_hostHoldOwned && (!holdSeconds || g_hostHoldConnection != connection)) {
        if (!wo138::join_hold(false, 0)) return;
        g_hostHoldOwned = false;
        g_hostHoldConnection = 0;
        logf("MP-LOAD-NATIVE host_offer=release actuator=ok");
    }
    if (!holdSeconds) {
        if (gate(revision) != Gate::Legacy && wo138::join_held() && !wo138::join_hold(false, 0)) return;
        g_applied.store(revision, std::memory_order_release);
        return;
    }
    if (!g_hostHoldOwned && g_hostHoldAttempt != revision) {
        g_hostHoldAttempt = revision;
        g_hostHoldOwned = wo138::join_hold(true, holdSeconds);
        if (g_hostHoldOwned) g_hostHoldConnection = connection;
        logf("MP-LOAD-NATIVE host_offer=hold actuator=%s", g_hostHoldOwned ? "ok" : "refused");
    }
    // The inherited actuator has its own game-thread deadline. Never
    // confirm a timed-out hold, and do not silently reacquire it.
    g_applied.store(g_hostHoldOwned && wo138::join_held() ? revision : 0, std::memory_order_release);
}

uint64_t stamp() { return g_stamp.load(std::memory_order_acquire); }

bool allows(uint64_t received, Access access) {
    const auto current = stamp();
    if (allowed(received, current, access)) return true;
    if (old_generation(received, current)) note_stale();
    return false;
}

bool calendar_allowed() {
    std::lock_guard<std::mutex> lock(g_stateMutex);
    return calendar_allowed(g_state.phase(), g_state.flags());
}

bool snapshot_allowed(uint64_t received, uint64_t generation, bool final) {
    std::lock_guard<std::mutex> lock(g_stateMutex);
    return g_state.snapshot_allowed(received, generation, final);
}

void note_stale(uint64_t n) { if (n) g_stale.fetch_add(n, std::memory_order_relaxed); }

Counts snapshot() {
    std::lock_guard<std::mutex> lock(g_stateMutex);
    auto out = g_state.snapshot(GetTickCount64());
    out.stale_gen_dropped = g_stale.load(std::memory_order_relaxed);
    return out;
}

} // namespace kcdmp::loadsequence
