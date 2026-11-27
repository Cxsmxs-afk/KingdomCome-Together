// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
// Who holds a copy's body (holders.h).
#include "holders.h"
#include "log.h"
#include "main_thread.h"
#include "pipe_server.h"
#include "wo216.h"
#include "wo221.h"

#include <cassert>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <utility>

namespace kcdmp::holders {

namespace {

std::mutex g_mutex;
Table g_table;
uint32_t g_handover[static_cast<int>(Holder::Count)][static_cast<int>(Holder::Count)] = {};
uint32_t g_back = 0;
uint32_t g_frames[static_cast<int>(Holder::Count)] = {};

void synchronize() {
    g_table.synchronize({wo216::lifecycle_observation_stamp(), pipe::observation_connection()},
                        main_thread::frame_count());
}

int status(char* out, int n) {
    std::lock_guard<std::mutex> lock(g_mutex);
    synchronize();
    const auto stats = g_table.stats();
    int k = std::snprintf(out, static_cast<size_t>(n), "on=%d bodies=%zu handovers=%llu back_within_1s=%u frames:",
                          on() ? 1 : 0, g_table.size(), static_cast<unsigned long long>(stats.handovers), g_back);
    for (int i = 1; i < static_cast<int>(Holder::Count) && k > 0 && k < n; ++i)
        k += std::snprintf(out + k, static_cast<size_t>(n - k), " %s=%u", name(static_cast<Holder>(i)), g_frames[i]);
    // the commonest hand-overs
    for (int a = 1; a < static_cast<int>(Holder::Count); ++a)
        for (int b = 1; b < static_cast<int>(Holder::Count); ++b)
            if (g_handover[a][b] && k > 0 && k < n)
                k += std::snprintf(out + k, static_cast<size_t>(n - k), " %s>%s=%u", name(static_cast<Holder>(a)), name(static_cast<Holder>(b)), g_handover[a][b]);
    return k;
}
const bool kReg = wo221::add_status("holders", &status);

Selection select(uint32_t eid, const char* npc, Holder who, double now) {
    Selection selection;
    uint64_t handovers = 0;
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        synchronize();
        selection = g_table.select(eid, who, now);
        if (!selection.selected) return selection;
        ++g_frames[static_cast<int>(who)];
        if (!selection.changed || selection.previous == Holder::None) return selection;
        ++g_handover[static_cast<int>(selection.previous)][static_cast<int>(who)];
        handovers = g_table.stats().handovers;
        if (selection.heldSeconds < 1.0) ++g_back;
    }
    const bool figure = npc && std::strncmp(npc, "kcd2mp_", 7) == 0;
    const bool quick = selection.heldSeconds < 1.0;
    if (figure || handovers <= 300 || handovers % 1000 == 0 || (quick && selection.bodyHandovers % 50 == 1))
        logf("WO221-HOLD npc=%s %s -> %s (held %.1f s; its hand-over #%u)%s [n=%llu] eid=0x%X frame=%llu",
             npc ? npc : "?", name(selection.previous), name(who), selection.heldSeconds, selection.bodyHandovers,
             quick ? " QUICK" : "", static_cast<unsigned long long>(handovers), eid, main_thread::frame_count());
    return selection;
}

} // namespace

const char* name(Holder h) {
    switch (h) {
        case Holder::Writer: return "writer";
        case Holder::Driver: return "driver";
        case Holder::Seat: return "seat";
        case Holder::Activity: return "activity";
        case Holder::Fall: return "fall";
        case Holder::Mount: return "mount";
        case Holder::OneShot: return "one-shot";
        case Holder::Route: return "route";
        case Holder::Respawn: return "respawn";
        case Holder::Leash: return "leash";
        case Holder::Unstuck: return "unstuck";
        case Holder::Figure: return "figure";
        case Holder::Cart: return "cart";
        case Holder::Probe: return "probe";
        case Holder::Escort: return "escort";
        default: return "none";
    }
}

bool on() { return wo221::on(wo221::kBitHolders); }

Holder set(uint32_t eid, const char* npc, Holder h, double now) {
    return select(eid, npc, h, now).previous;
}

bool handover(uint32_t eid, const char* npc, Holder h, double now) {
    return select(eid, npc, h, now).selected;
}

Holder of(uint32_t eid) {
    std::lock_guard<std::mutex> lock(g_mutex);
    synchronize();
    return g_table.owner(eid);
}

bool forget(uint32_t eid) {
    std::lock_guard<std::mutex> lock(g_mutex);
    synchronize();
    return g_table.forget(eid);
}

bool begin_drain(uint32_t eid, Holder holder) {
    std::lock_guard<std::mutex> lock(g_mutex);
    synchronize();
    return g_table.begin_drain(eid, holder);
}

bool engine_stopped(uint32_t eid, Holder holder) {
    std::lock_guard<std::mutex> lock(g_mutex);
    synchronize();
    return g_table.engine_stopped(eid, holder);
}

OwnerStats owner_stats() {
    std::lock_guard<std::mutex> lock(g_mutex);
    synchronize();
    return g_table.stats();
}

Permit may_place(uint32_t eid, Holder who, const char* path, Claim claim) {
    std::lock_guard<std::mutex> lock(g_mutex);
    synchronize();
    return Permit(eid, who, path, g_table.acquire(eid, who, path ? path : "", claim));
}

Permit::Permit(Permit&& other) noexcept
    : eid_(other.eid_), holder_(other.holder_), path_(other.path_), lease_(std::exchange(other.lease_, 0)),
      written_(std::exchange(other.written_, false)) {}

Permit::~Permit() {
    if (!lease_) return;
    std::lock_guard<std::mutex> lock(g_mutex);
    g_table.finish(eid_, lease_);
}

void Permit::wrote() {
    if (written_) return;
    written_ = true;
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        synchronize();
        if (g_table.wrote(eid_, holder_, path_ ? path_ : "", lease_)) return;
    }
    logf("WO221-OWNER-VIOLATION eid=0x%X holder=%s path=%s frame=%llu",
         eid_, name(holder_), path_ ? path_ : "?", main_thread::frame_count());
    assert(false && "a physical body setter wrote without its holder permit");
}

} // namespace kcdmp::holders
