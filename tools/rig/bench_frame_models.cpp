// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#include "frame_snapshot.h"

#include <algorithm>
#include <array>
#include <chrono>
#include <cstdio>
#include <numeric>
#include <vector>

using namespace kcdmp::motion;
using Clock = std::chrono::steady_clock;
constexpr unsigned kBodies = 20;
constexpr unsigned kFrames = 20000;
struct Actor {
    uint32_t eid = 0;
    std::array<uint32_t, 32> fields{};
};

#ifdef _MSC_VER
#define KCT_BENCH_NOINLINE __declspec(noinline)
#else
#define KCT_BENCH_NOINLINE __attribute__((noinline))
#endif

KCT_BENCH_NOINLINE Actor* find_actor(std::array<Actor, kBodies>& roster, uint32_t eid, unsigned& lookups) {
    ++lookups;
    for (auto& actor : roster) if (actor.eid == eid) return &actor;
    return nullptr;
}

KCT_BENCH_NOINLINE bool read_actor(void* pointer, ModelRead* model, unsigned& reads) {
    ++reads;
    auto& actor = *static_cast<Actor*>(pointer);
    model->opponentEid = std::accumulate(actor.fields.begin(), actor.fields.end(), actor.eid);
    return true;
}

int main() {
    for (unsigned players : {2u, 3u, 4u}) for (unsigned trial = 1; trial <= 5; ++trial) {
        std::array<Actor, kBodies> roster{};
        for (unsigned i = 0; i < kBodies; ++i) {
            roster[i].eid = 300 + i;
            roster[i].fields.fill(i);
        }
        FrameModels window;
        std::vector<double> costs(kFrames);
        unsigned actors = 0, reads = 0;
        uint64_t result = 0;
        for (unsigned frame = 0; frame < kFrames; ++frame) {
            auto start = Clock::now();
            window.begin();
            for (unsigned observer = 0; observer < players; ++observer) for (auto& actor : roster) {
                ModelRead sample{};
                auto raw = [&](void* p, ModelRead* o) { return read_actor(p, o, reads); };
#ifdef KCT_PART3_BASELINE
                window.read(actor.eid, find_actor(roster, actor.eid, actors), 7, &sample, raw);
#else
                window.read(actor.eid, &actor, {7, 2, 3}, &sample, [&] { return find_actor(roster, actor.eid, actors); }, raw);
#endif
                result += sample.opponentEid;
            }
            // The contact read after mutation bypasses the observer table in both builds.
            ++roster[0].fields[frame % roster[0].fields.size()];
            ModelRead live{};
            read_actor(find_actor(roster, 300, actors), &live, reads);
            result += live.opponentEid;
            costs[frame] = std::chrono::duration<double, std::micro>(Clock::now() - start).count();
        }
        double mean = std::accumulate(costs.begin(), costs.end(), 0.0) / costs.size();
        const size_t rank = costs.size() * 95 / 100 - 1;
        std::nth_element(costs.begin(), costs.begin() + rank, costs.end());
        std::printf("players=%u trial=%u frames=%u us_p95=%.3f mean_us=%.3f lookups_per_frame=%u reads_per_frame=%u checksum=%llu\n",
            players, trial, kFrames, costs[rank], mean, actors / kFrames, reads / kFrames, static_cast<unsigned long long>(result));
    }
}
