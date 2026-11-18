// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#include "../KCDMP/edge_lanes_rules.h"
#include "../KCDMP/wo218_rules.h"

#include <cstdio>

int edge_lanes_tests(int* passed) {
    using namespace kcdmp::edgelanes;
    int failures = 0;
    const auto check = [&](bool result, const char* label) {
        if (result) ++*passed;
        else { ++failures; std::fprintf(stderr, "edge lanes: %s\n", label); }
    };
    const auto request = [](uint32_t id, Lane lane, uint32_t key = 0, uint8_t type = 0x20) {
        Request out;
        out.id = id;
        out.lane = lane;
        out.key = key;
        out.frame.type = type;
        return out;
    };

    kcdmp::pipecontrol::Frame outer;
    outer.body.assign(kRequestHeader + 1, 0);
    put_u32(outer.body.data(), 0x87654321);
    outer.body[4] = uint8_t(Lane::Realtime);
    outer.body[5] = kRawPose;
    outer.body.back() = 15;
    outer.readerTick = 200;
    outer.readerGeneration = 7;
    outer.readerLife = 100;
    outer.readerGameplay = true;
    outer.readerQpc = 999;
    outer.readerObservation = 51;
    Request parsed;
    check(decode(outer, parsed) && parsed.id == 0x87654321 && parsed.lane == Lane::Realtime &&
          parsed.frame.type == kRawPose && parsed.frame.body == std::vector<uint8_t>{15}, "decodes ID and payload");
    check(parsed.frame.sequence == 0 && parsed.frame.readerTick == 200 && parsed.frame.readerGeneration == 7 &&
          parsed.frame.readerLife == 100 && parsed.frame.readerGameplay && parsed.frame.readerQpc == 999 &&
          parsed.frame.readerObservation == 51, "inner request retains physical first-byte scope and no legacy sequence");
    outer.body[6] = 1;
    check(!decode(outer, parsed), "ordered requests cannot carry a coalescing key");
    outer.body[4] = uint8_t(Lane::Latest);
    check(decode(outer, parsed) && parsed.key == 1, "latest key is recognized");
    outer.body[4] = 99;
    check(!decode(outer, parsed), "unknown lane rejected");
    outer.body.resize(kRequestHeader - 1);
    check(!decode(outer, parsed), "truncated envelope rejected");
    outer.body.resize(kcdmp::pipecontrol::kEdgePayload + 1);
    check(!decode(outer, parsed), "oversized envelope rejected");

    Queue queue;
    uint32_t superseded = 0;
    check(queue.push(request(1, Lane::Latest, 41), superseded) && superseded == 0, "first state admitted");
    check(queue.push(request(2, Lane::Realtime), superseded) && superseded == 0, "reliable event admitted");
    check(queue.push(request(3, Lane::Latest, 42), superseded) && superseded == 0, "different body not replaced");
    check(queue.push(request(4, Lane::Latest, 41, 0x09), superseded) && superseded == 0, "different state type not replaced");
    check(queue.push(request(5, Lane::Latest, 41), superseded) && superseded == 1, "newest state supersedes exact slot");
    for (uint32_t id = 2; id <= 5; ++id)
        check(queue.pop(true, parsed) && parsed.id == id, "replacement goes after earlier reliable work");
    check(!queue.pop(true, parsed), "superseded request cannot be delivered");

    bool admitted = true;
    for (uint32_t id = 1; id <= kBulkLimit; ++id)
        admitted &= queue.push(request(id, Lane::Bulk), superseded);
    check(admitted && !queue.push(request(1000, Lane::Bulk), superseded), "bulk has bounded admission");
    check(queue.push(request(1001, Lane::Realtime), superseded) && queue.pop(true, parsed) && parsed.id == 1001,
          "backlogged bulk cannot block realtime");
    bool finished = true;
    for (uint32_t id = 1; id <= kBulkLimit; ++id)
        finished &= queue.pop(false, parsed) && parsed.id == id;
    check(finished && !queue.pop(false, parsed), "all accepted reliable bulk requests stay FIFO");
    for (uint32_t id = 1; id <= kRtLimit; ++id)
        admitted &= queue.push(request(id, Lane::Realtime), superseded);
    check(admitted && !queue.push(request(1000, Lane::Realtime), superseded) &&
          queue.push(request(1001, Lane::Bulk), superseded) && queue.pop(false, parsed) && parsed.id == 1001,
          "both lanes admit independently");
    queue.clear();
    auto probe = request(8, Lane::Realtime);
    probe.probe = true;
    check(queue.push(std::move(probe), superseded) && queue.push(request(9, Lane::Realtime), superseded), "mixed probe and real jobs queued");
    queue.clear_probe();
    check(queue.pop(true, parsed) && parsed.id == 9 && !queue.pop(true, parsed), "ending probe keeps accepted real work");

    check(may_work(true, 0, kRtBudgetUs + 1) && may_work(false, 0, kBulkBudgetUs + 1), "each nonempty lane gets one turn");
    check(!may_work(true, 1, kRtBudgetUs) && !may_work(false, 1, kBulkBudgetUs), "elapsed budget stops further work");
    check(may_work(true, 29, 1) && !may_work(true, 30, 1) && may_work(false, 49, 1) && !may_work(false, 50, 1),
          "realtime and bulk caps are applied independently");

    PendingNotices notices;
    constexpr auto entered = NoticeClock::time_point{};
    constexpr auto deadline = entered + kPendingAfter;
    constexpr auto oneMs = std::chrono::milliseconds(1);
    check(kPendingAfter + kNoticeWaitSlice < std::chrono::milliseconds(50), "notification leaves scheduling headroom");
    check(notices.admit({7, 11}, deadline) && notices.next() == deadline &&
          !notices.admit({7, 11}, entered), "only one timer per accepted ID");
    check(!notices.take_due(deadline - oneMs), "no notice before deadline");
    auto pending = notices.take_due(deadline);
    check(pending && pending->connection == 7 && pending->id == 11 && notices.contains(11) &&
          !notices.next() && !notices.take_due(deadline + oneMs), "advisory leaves the same ID admitted and does not repeat");
    check(!notices.finish({6, 11}) && notices.finish({7, 11}) && !notices.contains(11) &&
          !notices.take_due(deadline + oneMs), "terminal is generation-scoped and prevents later notice");
    check(notices.admit({7, 12}, deadline) && notices.finish({7, 12}) &&
          !notices.take_due(deadline), "fast terminal cancels its notice before scheduling");
    check(notices.admit({7, 15}, deadline) && notices.admit({7, 16}, deadline - oneMs),
          "multiple independent timers accepted");
    pending = notices.take_due(deadline - oneMs);
    check(pending && pending->id == 16 && notices.next() == deadline, "notifier selects earliest deadline");
    notices.clear();
    check(notices.admit({8, 15}, deadline) && !notices.finish({7, 15}) && notices.next() == deadline,
          "disconnect cancels old timers and isolates reused IDs");
    notices.clear();

    Queue stalled;
    bool queued = true;
    for (uint32_t id = 1; id <= 3; ++id)
        queued &= stalled.push(request(id, Lane::Realtime), superseded) && notices.admit({9, id}, deadline);
    check(queued && stalled.pop(true, parsed) && parsed.id == 1, "first request can stall after leaving queue");
    auto first = notices.take_due(deadline);
    auto second = notices.take_due(deadline);
    auto third = notices.take_due(deadline);
    check(first && second && third && first->id == 1 && second->id == 2 && third->id == 3,
          "running and queued requests both receive a notice without game progress");
    check(notices.finish({9, 1}) && stalled.pop(true, parsed) && parsed.id == 2 && notices.finish({9, 2}) &&
          stalled.pop(true, parsed) && parsed.id == 3 && notices.finish({9, 3}) && !stalled.pop(true, parsed) &&
          !notices.next(), "resumed work still finishes once and in FIFO order");

    struct Run {
        uint32_t rt = 0, bulk = 0, busy = 0;
        int64_t p95Us = 0;
        bool fifo = true;
    };
    const auto modeled = [&](unsigned offeredFrames, bool isolated) {
        Queue load;
        Run run;
        std::vector<int64_t> rtAge;
        uint32_t rtOffer = 0, bulkOffer = 0, old = 0;
        constexpr int64_t kFrameUs = 16667;
        for (unsigned frame = 0; frame < offeredFrames + 64; ++frame) {
            const int64_t at = 1 + frame * kFrameUs;
            if (frame < offeredFrames) {
                const auto offer = [&](Lane lane, unsigned count, uint32_t& number) {
                    for (unsigned i = 0; i < count; ++i) {
                        auto item = request(++number, lane);
                        item.frame.readerQpc = at;
                        if (!load.push(std::move(item), old)) ++run.busy;
                    }
                };
                offer(Lane::Realtime, kRtPerFrame, rtOffer);
                offer(Lane::Bulk, kBulkPerFrame, bulkOffer);
            }
            int64_t now = at + 1000; // A deterministic share of engine work before the lane starts.
            unsigned shared = 0;
            const auto drain = [&](bool rt) {
                const int64_t started = now;
                for (unsigned served = 0; isolated ? may_work(rt, served, static_cast<double>(now - started))
                     : kcdmp::wo218rules::may_run(static_cast<int>(shared), static_cast<double>(now - at - 1000)); ++served) {
                    Request item;
                    if (!load.pop(rt, item)) break;
                    if (rt) {
                        run.fifo &= item.id == ++run.rt;
                        rtAge.push_back(now - item.frame.readerQpc);
                        now += 6;
                    } else {
                        run.fifo &= item.id == ++run.bulk;
                        now += 9;
                    }
                    ++shared;
                }
            };
            if (isolated) { drain(true); drain(false); }
            else { drain(false); drain(true); }
            if (frame >= offeredFrames && run.rt == rtOffer && run.bulk == bulkOffer) break;
        }
        std::sort(rtAge.begin(), rtAge.end());
        if (!rtAge.empty()) run.p95Us = rtAge[(rtAge.size() * 95 + 99) / 100 - 1];
        return run;
    };
    const auto separated = modeled(120, true);
    char label[200];
    std::snprintf(label, sizeof label, "virtual dedicated lanes at 30+50/frame (RT p95 %lld us)", static_cast<long long>(separated.p95Us));
    check(separated.fifo && separated.busy == 0 && separated.rt == 120 * kRtPerFrame &&
          separated.bulk == 120 * kBulkPerFrame && separated.p95Us > 0 && separated.p95Us < 34000, label);
    const auto shared = modeled(4, false);
    std::snprintf(label, sizeof label, "virtual shared budget detects head-of-line blocking (RT p95 %lld us)", static_cast<long long>(shared.p95Us));
    check(shared.fifo && shared.busy == 0 && shared.rt == 4 * kRtPerFrame &&
          shared.bulk == 4 * kBulkPerFrame && shared.p95Us >= 34000, label);
    return failures;
}
