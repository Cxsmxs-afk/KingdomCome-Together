// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#pragma once

#include "pipe_control_reader.h"

#include <algorithm>
#include <utility>

namespace kcdmp::edgelanes {

constexpr uint8_t kReply = 0xB8, kRawPose = 0x39, kProbe = 0x3A, kProbeReply = 0xBA;
constexpr uint8_t kVersion = 1;
constexpr size_t kRequestHeader = 10, kReplyHeader = 6;
constexpr size_t kRtLimit = 192, kBulkLimit = 256;
constexpr unsigned kRtPerFrame = 30, kBulkPerFrame = 50;
constexpr double kRtBudgetUs = 1250, kBulkBudgetUs = 750;
enum class Lane : uint8_t { Realtime = 1, Bulk = 2, Latest = 3 };
enum class Status : uint8_t { Completed = 0, Busy = 1, Unsupported = 2, Superseded = 3, Invalid = 4, Faulted = 5 };

inline uint32_t read_u32(const uint8_t* b) {
    return uint32_t(b[0]) | (uint32_t(b[1]) << 8) | (uint32_t(b[2]) << 16) | (uint32_t(b[3]) << 24);
}
inline void put_u32(uint8_t* b, uint32_t n) {
    for (unsigned i = 0; i < 4; ++i) b[i] = static_cast<uint8_t>(n >> (8 * i));
}

struct Request {
    uint32_t id = 0, key = 0;
    Lane lane = Lane::Realtime;
    pipecontrol::Frame frame;
    uint64_t connection = 0;
    bool probe = false;
};

inline bool ordered(Lane lane) { return lane == Lane::Realtime || lane == Lane::Bulk; }
inline bool realtime(Lane lane) { return lane != Lane::Bulk; }

inline bool decode(const pipecontrol::Frame& outer, Request& out) {
    const auto& bytes = outer.body;
    if (bytes.size() < kRequestHeader || bytes.size() > pipecontrol::kEdgePayload) return false;
    out.id = read_u32(bytes.data());
    out.lane = static_cast<Lane>(bytes[4]);
    out.key = read_u32(bytes.data() + 6);
    if (!out.id || bytes[5] == 0 || (!ordered(out.lane) && out.lane != Lane::Latest) ||
        (ordered(out.lane) && out.key != 0)) return false;
    out.frame.type = bytes[5];
    out.frame.sequence = 0;
    out.frame.readerTick = outer.readerTick;
    out.frame.readerQpc = outer.readerQpc;
    out.frame.readerGeneration = outer.readerGeneration;
    out.frame.readerLife = outer.readerLife;
    out.frame.readerGameplay = outer.readerGameplay;
    out.frame.readerObservation = outer.readerObservation;
    out.frame.body.assign(bytes.begin() + kRequestHeader, bytes.end());
    return true;
}

// FIFO within each lane. Replacing an explicitly coalescible state puts the
// latest value at its new place after all preceding reliable events.
class Queue {
    std::deque<Request> rt_, bulk_;

public:
    bool push(Request&& request, uint32_t& superseded) {
        auto& queue = realtime(request.lane) ? rt_ : bulk_;
        const size_t limit = realtime(request.lane) ? kRtLimit : kBulkLimit;
        superseded = 0;
        if (request.lane == Lane::Latest) {
            auto found = std::find_if(queue.begin(), queue.end(), [&](const Request& old) {
                return old.lane == Lane::Latest && old.frame.type == request.frame.type && old.key == request.key;
            });
            if (found != queue.end()) {
                superseded = found->id;
                queue.erase(found);
            }
        }
        if (queue.size() >= limit) return false;
        queue.push_back(std::move(request));
        return true;
    }

    bool pop(bool rt, Request& out) {
        auto& queue = rt ? rt_ : bulk_;
        if (queue.empty()) return false;
        out = std::move(queue.front());
        queue.pop_front();
        return true;
    }

    void clear() { rt_.clear(); bulk_.clear(); }
    void clear_probe() {
        const auto probe = [](const Request& request) { return request.probe; };
        rt_.erase(std::remove_if(rt_.begin(), rt_.end(), probe), rt_.end());
        bulk_.erase(std::remove_if(bulk_.begin(), bulk_.end(), probe), bulk_.end());
    }
};

inline bool may_work(bool rt, unsigned served, double elapsedUs) {
    return served < (rt ? kRtPerFrame : kBulkPerFrame) &&
           (served == 0 || elapsedUs < (rt ? kRtBudgetUs : kBulkBudgetUs));
}

} // namespace kcdmp::edgelanes
