// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#pragma once
#include <cstddef>
#include <cstdint>
#include <chrono>
#include <condition_variable>
#include <mutex>
#include <deque>
#include <utility>
#include <vector>
#include "wo216_authority_rules.h"
#include "t3_stats_rules.h"

namespace kcdmp::pipecontrol {
constexpr uint8_t kGeneric = 0x2B, kBatch = 0x2D, kSamples = 0x10;
constexpr size_t kNormalPayload = 1024, kLargePayload = 4096;
constexpr size_t kDeferredFrames = 128, kDeferredBytes = 64 * 1024;
struct Frame { uint8_t type = 0, sequence = 0; std::vector<uint8_t> body; uint64_t readerTick=0,readerGeneration=0,readerLife=0;bool readerGameplay=false; };
inline bool header_payload_allowed(uint8_t type, size_t n) {
    return n <= kNormalPayload || ((type == kBatch || type == kSamples || type == kGeneric) && n <= kLargePayload);
}
inline bool complete_payload_allowed(const Frame& f) {
    return f.body.size() <= kNormalPayload || ((f.type == kBatch || f.type == kSamples) && f.body.size() <= kLargePayload) ||
        (f.type == kGeneric && f.body.size() <= kLargePayload && !f.body.empty() && f.body[0] == t3stats::kOp);
}
inline bool lifecycle(const Frame& f) { return f.type == kGeneric && !f.body.empty() && f.body[0] == wo216authority::kOp; }

// One decoder is shared by the ordinary serve loop and its short control pump.
// It consumes every available byte, including an incomplete header/body, so a
// full-frame Peek requirement can never deadlock behind the OS pipe buffer.
// Only complete standalone lifecycle frames bypass the deferred normal FIFO.
class Reader {
    uint8_t head_[3]{}; size_t header_ = 0, wanted_ = 0;
    std::vector<uint8_t> body_;Frame reading_;
    std::deque<Frame> deferred_;
    size_t bytes_ = 0; uint8_t sequence_ = 0; bool failed_ = false;
public:
    explicit Reader(uint8_t firstSequence = 0) : sequence_(firstSequence) {}
    bool failed() const { return failed_; }
    uint8_t next_sequence() const { return sequence_; }
    size_t queued() const { return deferred_.size(); }
    size_t queued_bytes() const { return bytes_; }
    size_t partial_bytes() const { return header_ + body_.size(); }
    template<class Control,class Stamp>
    bool feed(const uint8_t* data, size_t n, Control&& control,Stamp&& stamp) {
        if (failed_ || (!data && n)) return false;
        size_t at = 0;
        while (at < n) {
            if(header_==0){reading_=Frame{};reading_.type=data[at];stamp(reading_);} // first byte, never freshen a partial frame
            while (header_ < 3 && at < n) head_[header_++] = data[at++];
            if (header_ < 3) break;
            if (body_.empty()) {
                wanted_ = size_t(head_[1]) | (size_t(head_[2]) << 8);
                if (!header_payload_allowed(head_[0], wanted_)) { failed_ = true; return false; }
            }
            const size_t left = wanted_ - body_.size();
            const size_t take = left < n - at ? left : n - at;
            if (take) body_.insert(body_.end(), data + at, data + at + take);
            at += take;
            if (body_.size() != wanted_) break;
            Frame f=std::move(reading_); f.type = head_[0]; f.body = std::move(body_);
            if (!complete_payload_allowed(f)) { failed_ = true; return false; }
            // Assigned exactly once in wire-read order, not when normal work is
            // eventually dispatched. Batches never consume the single sequence.
            if (f.type != kBatch) f.sequence = sequence_++;
            header_ = 0; wanted_ = 0; body_.clear();
            if (lifecycle(f)) {
                if (!control(f)) { failed_ = true; return false; }
            } else {
                const size_t cost = 3 + f.body.size();
                if (deferred_.size() >= kDeferredFrames || cost > kDeferredBytes - bytes_) { failed_ = true; return false; }
                bytes_ += cost; deferred_.push_back(std::move(f));
            }
        }
        return true;
    }
    template<class Control>
    bool feed(const uint8_t* data,size_t n,Control&& control){return feed(data,n,std::forward<Control>(control),[](Frame&){});}
    bool pop(Frame* out) {
        if (!out || deferred_.empty()) return false;
        *out = std::move(deferred_.front()); deferred_.pop_front(); bytes_ -= 3 + out->body.size(); return true;
    }
};
enum class Wait { Done, TimedOut, TransportFailed };
// Shared by the real pipe wait and portable tests. Pump never runs under the
// result mutex, and the original timeout is absolute, not extended per byte.
template<class Ready, class Pump>
Wait wait_pumped(std::condition_variable& cv, std::unique_lock<std::mutex>& lock,
                 std::chrono::milliseconds timeout, Ready&& ready, Pump&& pump) {
    const auto deadline = std::chrono::steady_clock::now() + timeout;
    while (!ready()) {
        const auto now = std::chrono::steady_clock::now();
        if (now >= deadline) return Wait::TimedOut;
        const auto slice = now + std::chrono::milliseconds(5);
        cv.wait_until(lock, slice < deadline ? slice : deadline, ready);
        if (ready()) break;
        lock.unlock();
        const bool healthy = pump();
        lock.lock();
        if (!healthy) return Wait::TransportFailed;
    }
    return Wait::Done;
}
} // namespace kcdmp::pipecontrol
