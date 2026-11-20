// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#include "load_sequence.h"
#include "log.h"
#include "wo138.h"

#include <chrono>
#include <future>
#include <thread>

#ifndef _WIN32
uint64_t GetTickCount64() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count();
}
#endif

// No world pause is requested in these transitions. Keep the engine boundary visible.
bool kcdmp::wo138::join_held() { return false; }
bool kcdmp::wo138::join_hold(bool, double) { std::abort(); }

int load_sequence_log_stall_tests(int* passed) {
    using namespace kcdmp::loadsequence;
    using namespace std::chrono_literals;
    int ok = 0, failed = 0;
    const auto test = [&](const char* role, Phase phase, Gate expected, uint64_t generation) {
        constexpr uint64_t connection = 91;
        std::array<uint8_t, kRequestBytes> body{kOp, static_cast<uint8_t>(phase)};
        write_le(body.data() + 5, generation, 8);
        write_le(body.data() + 13, 1, 4);
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
        blockedSink.unlock();
        const bool accepted = pipe.get();
        const auto readback = frame.get();
        const bool scopeMatches = readback.world_gen == generation && readback.load_phase == static_cast<uint8_t>(phase) &&
            gate(stamp()) == expected && ack[0] == static_cast<uint8_t>(phase) &&
            read_le(ack.data() + 2, 8) == generation && read_le(ack.data() + 10, 4) == 1;
        const bool pass = published && frameReady && accepted && scopeMatches;
        std::printf("LOAD-LOG-STALL role=%s gate_published=%d frame_before_sink_release=%d ack_and_scope=%d\n",
                    role, published, frameReady, accepted && scopeMatches);
        if (pass) ++ok;
        else ++failed;
        disconnected(connection);
    };
    test("host", Phase::Play, Gate::Play, 782);
    test("joiner", Phase::Offer, Gate::Hold, 783);
    *passed = ok;
    return failed;
}
