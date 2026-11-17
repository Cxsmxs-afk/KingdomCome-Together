// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
// WO-218: engine-free checks of wo218_rules.h (the pipe's batch). Linked into KCDMP_NativeTests; wo218_tests() returns the failures.
#include <cstdio>
#include "wo218_rules.h"
#include "pipe_outbox.h"
#include <future>
#include <mutex>
#include <thread>
#include <chrono>

namespace {
int g_fail = 0, g_pass = 0;
#define CHECK(c) do { if (c) ++g_pass; else { std::printf("FAIL  %s:%d  %s\n", __FILE__, __LINE__, #c); ++g_fail; } } while (0)
using namespace kcdmp::wo218rules;
namespace pipeout = kcdmp::pipeout;
}

int wo218_tests(int* passed) {
    // a batch of two: [id 0x0102][n 2] (0x2B len 2 [133 7]) (0x27 len 1 [3])
    const uint8_t b[] = {0x02, 0x01, 2, 0x2B, 2, 0, 133, 7, 0x27, 1, 0, 3};
    uint16_t id = 0;
    std::vector<Sub> subs;
    CHECK(parse_batch(b, sizeof b, &id, &subs));
    CHECK(id == 0x0102);
    CHECK(subs.size() == 2);
    CHECK(subs[0].type == 0x2B && subs[0].len == 2 && b[subs[0].off] == 133 && b[subs[0].off + 1] == 7);
    CHECK(subs[1].type == 0x27 && subs[1].len == 1 && b[subs[1].off] == 3);
    // malformed: short, a length past the end, trailing bytes, an empty sub
    CHECK(!parse_batch(b, 2, &id, &subs));
    const uint8_t past[] = {0, 0, 1, 0x2B, 5, 0, 1};
    CHECK(!parse_batch(past, sizeof past, &id, &subs));
    const uint8_t trail[] = {0, 0, 1, 0x2B, 1, 0, 1, 9};
    CHECK(!parse_batch(trail, sizeof trail, &id, &subs));
    const uint8_t empty[] = {0, 0, 1, 0x2B, 0, 0};
    CHECK(!parse_batch(empty, sizeof empty, &id, &subs));
    const uint8_t none[] = {5, 0, 0};
    CHECK(parse_batch(none, sizeof none, &id, &subs) && subs.empty() && id == 5);
    // the generic families and their reply types
    CHECK(batchable(0x21) && batchable(0x2B) && !batchable(0x2C) && !batchable(0x20) && !batchable(0x10) && !batchable(kBatch));
    CHECK(reply_type(0x2B) == 0xAC && reply_type(0x27) == 0xA6 && reply_type(0x21) == 0x98 && reply_type(0x10) == 0);
    // the reply: [1][id][n] then (type, len, body); past the cap a sub keeps only its first four bytes
    Reply r;
    r.begin(0x0102, 2);
    const uint8_t s1[] = {1, 0, 133, 0, 9};
    r.add(0xAC, s1, sizeof s1);
    CHECK(r.b.size() == 4 + 3 + 5 && r.b[0] == 1 && r.b[1] == 2 && r.b[2] == 1 && r.b[3] == 2);
    CHECK(r.b[4] == 0xAC && r.b[5] == 5 && r.b[6] == 0 && r.b[11] == 9);
    std::vector<uint8_t> big(1000, 7);
    Reply c;
    c.begin(1, 255);
    for (int i = 0; i < 70; ++i) c.add(0xAC, big.data(), big.size());
    CHECK(c.b.size() <= kReplyCap && c.cut > 0 && c.subs == 70);
    // the frame's budget: one always runs, then while under 2 ms
    CHECK(may_run(0, 9999.0) && may_run(3, 1999.0) && !may_run(1, 2000.0));
    CHECK(!may_run(8, 0));
    FrameBudget budget;
    budget.begin(1); budget.add(1, 20); // one single request
    for (int i = 0; i < 7; ++i) { CHECK(budget.may_run()); budget.add(1, 20); }
    CHECK(!budget.may_run());
    budget.begin(1); CHECK(!budget.may_run()); // another drain in the same frame has no fresh allowance
    budget.begin(2); CHECK(budget.may_run());
    budget.add(1, 2500000); CHECK(!budget.may_run()); // a nonpreemptible engine call does not admit a second call
    budget.begin(3); CHECK(budget.may_run());
    std::vector<int> applied;
    for (int frame = 4, next = 0; next < 255; ++frame) {
        budget.begin(frame);
        while (next < 255 && budget.may_run()) { applied.push_back(next++); budget.add(1, 1); }
        CHECK(budget.ran <= 8);
    }
    CHECK(applied.size() == 255);
    for (int i = 0; i < 255; ++i) CHECK(applied[i] == i); // carries preserve reliable order
    using pipeout::Outbox;
    auto row = [](uint8_t flags, uint8_t hp, uint8_t x) {
        std::vector<uint8_t> r(27, 0); // one row, name 'n'
        r[0] = 0xA0; r[1] = 24; r[3] = 1; r[4] = flags; r[5] = x; r[21] = hp; r[25] = 1; r[26] = 'n';
        return r;
    };
    Outbox tx;
    CHECK(tx.push_wire(row(0, 100, 1))); // initial life state is reliable
    CHECK(tx.push_wire(row(0, 100, 2)) && tx.push_wire(row(0, 100, 3)) && tx.sample_count() == 1);
    std::vector<uint8_t> frame;
    CHECK(tx.pop(&frame) && frame[5] == 1);
    CHECK(tx.pop(&frame) && frame[5] == 3 && tx.empty()); // replace movement, no backlog
    CHECK(tx.push_wire(row(0, 100, 4)));
    CHECK(tx.push_wire(row(1, 0, 5)) && tx.sample_count() == 0); // death erases an older alive position
    CHECK(tx.push_reliable({0x9E, 1}) && tx.push_reliable({0x90, 2}));
    CHECK(tx.pop(&frame) && frame[4] == 1 && frame[5] == 5);
    CHECK(tx.pop(&frame) && frame[0] == 0x9E);
    CHECK(tx.pop(&frame) && frame[0] == 0x90 && tx.empty()); // quest/hit order survives samples
    for (uint8_t flags : {uint8_t{2}, uint8_t{4}, uint8_t{32}, uint8_t{8}}) {
        CHECK(tx.push_wire(row(flags, 50, 6)) && tx.sample_count() == 0);
        CHECK(tx.pop(&frame) && frame[4] == flags); // KO/draw/engage/swing stay reliable
    }
    CHECK(tx.push_wire(row(0, 49, 7)) && tx.sample_count() == 0 && tx.pop(&frame)); // HP change too
    CHECK(!pipeout::same_connection(1, 2, true) && !pipeout::same_connection(2, 2, false) && pipeout::same_connection(2, 2, true));
    tx.push_sample("old", {1}); tx.clear(); CHECK(tx.empty()); // reconnect cannot reuse old handle's queued records
    CHECK(tx.push_reliable(std::vector<uint8_t>(Outbox::kReliableBytes, 1)));
    CHECK(!tx.push_reliable({2}) && tx.reliable_bytes() == Outbox::kReliableBytes); // caller must fail connection, no silent drop
    tx.clear();
    // A writer owns the popped buffer while its I/O is blocked. Producers can
    // still queue a reliable death; no queue lock crosses the simulated write.
    std::mutex mx;
    std::promise<void> blocked, release;
    auto released = release.get_future().share();
    auto blockedFuture = blocked.get_future();
    tx.push_reliable({10});
    std::thread writer([&] {
        std::vector<uint8_t> owned;
        { std::lock_guard<std::mutex> l(mx); tx.pop(&owned); }
        blocked.set_value(); released.wait();
    });
    CHECK(blockedFuture.wait_for(std::chrono::seconds(1)) == std::future_status::ready);
    auto producer = std::async(std::launch::async, [&] { std::lock_guard<std::mutex> l(mx); return tx.push_reliable({11}); });
    CHECK(producer.wait_for(std::chrono::milliseconds(250)) == std::future_status::ready);
    release.set_value(); writer.join(); CHECK(producer.get());
    CHECK(tx.pop(&frame) && frame[0] == 11);
    // what one frame served
    FrameCount f;
    f.add(10, 1); f.add(10, 2); f.add(11, 1); f.add(13, 5);
    CHECK(f.total == 9 && f.frames == 2 && f.maxPerFrame == 3 && f.cur == 5);
    uint8_t fb[4];
    CHECK(failure(fb, 133, kReasonTaskFaulted) == 4 && fb[0] == 0 && fb[2] == 133 && fb[3] == 17);
    // C.3: only the activity families, only the copy's own idle / variation / callback / transition
    CHECK(takeover_family(4) && takeover_family(5) && takeover_family(7) && !takeover_family(1) && !takeover_family(3) && !takeover_family(9));
    CHECK(takeover_class("CAnimActionAIMovement") && takeover_class("C_VariationAction") && takeover_class("C_CallbackAction"));
    CHECK(!takeover_class("C_CombatAnimAction") && !takeover_class("CAnimActionLooking") && !takeover_class(nullptr) && !takeover_class("C_Variation"));
    *passed = g_pass;
    return g_fail;
}
