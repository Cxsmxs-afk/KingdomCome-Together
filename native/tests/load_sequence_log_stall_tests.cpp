// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#include "load_sequence.h"
#include "log.h"
#include "wo138.h"

#include <atomic>
#include <chrono>
#include <cstring>
#include <future>
#include <thread>

#ifndef _WIN32
uint64_t GetTickCount64() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count();
}
#endif

namespace {
std::atomic<bool> held{false};
std::atomic<unsigned> actuations{0};

void enqueue(kcdmp::deferred_log::Queue& queue, const char* fmt, ...) {
    va_list args;
    va_start(args, fmt);
    queue.push(fmt, args);
    va_end(args);
}
}

// The actual PauseGame hook and engine are doubles. Its first operation also
// logs synchronously, so removing only LoadSequence's own logs is insufficient.
bool kcdmp::wo138::join_held() { return held; }
bool kcdmp::wo138::join_hold(bool on, double) {
    ++actuations;
    kcdmp::logf("LOAD-LOG-STALL-ACTUATOR hold=%d", static_cast<int>(on));
    held = on;
    return true;
}

int load_sequence_log_stall_tests(int* passed) {
    using namespace kcdmp::loadsequence;
    using namespace std::chrono_literals;
    int ok = 0, failed = 0;
    const auto test = [&](const char* role, Phase phase, Gate expected, uint64_t generation,
                          uint8_t flags, uint32_t request, bool shouldHold, unsigned expectedCalls) {
        constexpr uint64_t connection = 91;
        std::array<uint8_t, kRequestBytes> body{kOp, static_cast<uint8_t>(phase), flags};
        write_le(body.data() + 3, flags & kHostOfferHeld ? 60 : 0, 2);
        write_le(body.data() + 5, generation, 8);
        write_le(body.data() + 13, request, 4);
        std::array<uint8_t, kAckBytes> ack{};
        const auto previous = stamp();
        std::unique_lock blockedSink(kcdmp::log_mutex());
        auto pipe = std::async(std::launch::async, [&] { return control(body.data(), body.size(), connection, ack.data()); });
        const auto deadline = std::chrono::steady_clock::now() + 1s;
        while (stamp() == previous && std::chrono::steady_clock::now() < deadline)
            std::this_thread::yield();
        const bool published = stamp() != previous;
        std::promise<void> entered;
        auto frame = std::async(std::launch::async, [&] {
            entered.set_value();
            tick();
            return snapshot();
        });
        entered.get_future().wait();
        const bool frameReady = frame.wait_for(1s) == std::future_status::ready;
        const bool controlReady = pipe.wait_for(1s) == std::future_status::ready;
        std::array<uint8_t, kRequestBytes> query{kOp, static_cast<uint8_t>(Phase::Query)};
        std::array<uint8_t, kAckBytes> applied{};
        write_le(query.data() + 13, request, 4);
        const bool mainApplied = frameReady && controlReady &&
            control(query.data(), query.size(), connection, applied.data()) && (applied[1] & kMainApplied);
        blockedSink.unlock();
        const bool accepted = pipe.get();
        const auto readback = frame.get();
        const bool scopeMatches = readback.world_gen == generation && readback.load_phase == static_cast<uint8_t>(phase) &&
            gate(stamp()) == expected && ack[0] == static_cast<uint8_t>(phase) &&
            read_le(ack.data() + 2, 8) == generation && read_le(ack.data() + 10, 4) == request &&
            held == shouldHold && actuations == expectedCalls;
        const bool pass = published && frameReady && controlReady && accepted && scopeMatches && mainApplied;
        std::printf("LOAD-LOG-STALL role=%s gate_published=%d frame_before_sink_release=%d control_before_sink_release=%d ack_and_scope=%d main_applied=%d\n",
                    role, published, frameReady, controlReady, accepted && scopeMatches, mainApplied);
        if (pass) ++ok;
        else ++failed;
    };
    test("host", Phase::Play, Gate::Play, 782, 0, 1, false, 0);
    test("joiner", Phase::Offer, Gate::Hold, 783, 0, 2, false, 0);
    bool observedWake = true, checkedWake = false;
#ifdef _WIN32
    checkedWake = true;
    kcdmp::deferred_log::prepare();
    const auto event = kcdmp::deferred_log::wake.load();
    {
        kcdmp::deferred_log::Scope diagnostic;
        kcdmp::logf("LOAD-LOG-STALL-FIXTURE wake inside active scope");
        observedWake = event && WaitForSingleObject(event, 0) == WAIT_OBJECT_0;
    }
#endif
    test("host-offer-hold", Phase::Offer, Gate::Hold, 784, kHostOfferHeld, 3, true, 1);
    test("host-offer-release", Phase::Play, Gate::Play, 784, 0, 4, false, 2);
    disconnected(91);

#ifdef _WIN32
    kcdmp::deferred_log::wake.store(nullptr);
    if (event) CloseHandle(event);
#endif
    kcdmp::deferred_log::Record record;
    unsigned heldLog = 0, releaseLog = 0, lifecycleLog = 0;
    while (kcdmp::deferred_log::queue.pop(record)) {
        if (std::strcmp(record.text, "LOAD-LOG-STALL-ACTUATOR hold=1") == 0) ++heldLog;
        if (std::strcmp(record.text, "LOAD-LOG-STALL-ACTUATOR hold=0") == 0) ++releaseLog;
        if (std::strstr(record.text, "MP-LOAD-NATIVE ") == record.text) ++lifecycleLog;
    }
    if (heldLog == 1 && releaseLog == 1 && lifecycleLog == 6 && observedWake) ++ok;
    else ++failed;
    std::printf("LOAD-LOG-STALL retained lifecycle=%u nested_hold=%u nested_release=%u wake_checked=%d wake_in_scope=%d\n",
                lifecycleLog, heldLog, releaseLog, checkedWake, checkedWake && observedWake);

    kcdmp::deferred_log::Queue bounded;
    for (unsigned index = 0; index < 80; ++index)
        enqueue(bounded, "transition %u", index);
    unsigned emitted = 0;
    bool ordered = true;
    while (bounded.pop(record)) {
        char expected[32]{};
        std::snprintf(expected, sizeof expected, "transition %u", emitted++);
        ordered &= std::strcmp(record.text, expected) == 0;
    }
    const unsigned dropped = bounded.take_dropped();
    enqueue(bounded, "%s", "recovered");
    const bool recovered = bounded.pop(record) && std::strcmp(record.text, "recovered") == 0;
    if (emitted == 64 && dropped == 16 && ordered && recovered) ++ok;
    else ++failed;
    std::printf("LOAD-LOG-STALL queue emitted=%u overflow=%u ordered=%d recovered=%d\n", emitted, dropped, ordered, recovered);
    *passed = ok;
    return failed;
}
