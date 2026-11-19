// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#pragma once

#include "walk_holder.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <limits>

namespace kcdmp::rptimeline {

constexpr uint32_t kMinBufferMs = 60, kMaxBufferMs = 120, kMaxExtrapMs = 200;
constexpr uint64_t kClockAgeMs = 30000, kFutureToleranceMs = 50, kClockResetMs = 1000;
constexpr uint64_t kPoseHoldMs = 3000, kStateFreshMs = 1500;

inline uint64_t difference(uint64_t a, uint64_t b) { return a >= b ? a - b : b - a; }

inline bool known_stamp(uint64_t stamp, uint64_t hostAtArrival) {
    if (!stamp || !hostAtArrival) return false;
    return stamp > hostAtArrival ? stamp - hostAtArrival <= kFutureToleranceMs
                                 : hostAtArrival - stamp <= kClockAgeMs;
}

inline uint64_t advance(uint64_t host, uint64_t step) {
    const auto max = (std::numeric_limits<uint64_t>::max)();
    return step > max - host ? max : host + step;
}

struct Sample {
    uint64_t hostMs = 0, stateHostMs = 0;
    float x = 0, y = 0, z = 0, yaw = 0;
    std::array<uint8_t, 12> state{};
    uint8_t flags = 0;
};

struct Output {
    uint64_t targetHostMs = 0, appliedHostMs = 0;
    uint32_t bufferMs = 0, extrapMs = 0;
    float x = 0, y = 0, z = 0, yaw = 0, vx = 0, vy = 0, vz = 0;
    Sample source;
    bool ready() const { return appliedHostMs != 0; }
};

enum class Ingest { Rejected, Accepted, Late, Restarted };

class Track {
    static constexpr size_t kDepth = 24, kDelayCount = 16;
    std::array<Sample, kDepth> samples_{};
    size_t size_ = 0;
    std::array<uint32_t, kDelayCount> delays_{};
    size_t nDelay_ = 0, delayIndex_ = 0;
    uint64_t localAnchor_ = 0, hostAnchor_ = 0, lastArrival_ = 0, lastRender_ = 0, previousTarget_ = 0;
    double bufferMs_ = 90;

    uint64_t projected(uint64_t local) const {
        if (!hostAnchor_) return 0;
        if (local >= localAnchor_) return advance(hostAnchor_, local - localAnchor_);
        return localAnchor_ - local >= hostAnchor_ ? 0 : hostAnchor_ - (localAnchor_ - local);
    }

    uint32_t wanted_buffer() const {
        if (!nDelay_) return 90;
        auto sorted = delays_;
        std::sort(sorted.begin(), sorted.begin() + nDelay_);
        const size_t q90 = (nDelay_ * 9 + 9) / 10 - 1;
        return (std::clamp)(sorted[q90] + 24, kMinBufferMs, kMaxBufferMs);
    }

    static float angle(float from, float to, float fraction) {
        constexpr float twoPi = 6.283185307179586f;
        return from + std::remainder(to - from, twoPi) * fraction;
    }

    static bool velocity(const Sample& a, const Sample& b, Output& out) {
        const uint64_t span = b.hostMs - a.hostMs;
        if (span < 5 || span > 500) return false;
        const float dt = static_cast<float>(span) / 1000.f;
        const float vx = (b.x - a.x) / dt, vy = (b.y - a.y) / dt, vz = (b.z - a.z) / dt;
        if (!(vx * vx + vy * vy + vz * vz <= 400.f)) return false;
        out.vx = vx; out.vy = vy; out.vz = vz;
        return true;
    }

public:
    Ingest push(Sample sample, uint64_t localArrival, uint64_t hostAtArrival) {
        if (!localArrival || !known_stamp(sample.hostMs, hostAtArrival) || !std::isfinite(sample.x) ||
            !std::isfinite(sample.y) || !std::isfinite(sample.z) || !std::isfinite(sample.yaw))
            return Ingest::Rejected;
        if (lastArrival_ && localArrival < lastArrival_)
            return sample.hostMs <= previousTarget_ ? Ingest::Late : Ingest::Rejected;
        const uint64_t expected = projected(localArrival);
        const bool restarted = expected && difference(expected, hostAtArrival) > kClockResetMs;
        if (restarted) *this = {};
        const bool late = previousTarget_ && sample.hostMs <= previousTarget_;
        if (size_ && sample.hostMs <= samples_[size_ - 1].hostMs)
            return late ? Ingest::Late : Ingest::Rejected;

        // A changed clock calibration cannot move a displayed cursor back.
        // Small offset adjustments settle gradually on consecutive samples.
        if (hostAnchor_) {
            const uint64_t maxCorrection = 2;
            hostAnchor_ = hostAtArrival >= expected ? advance(expected, (std::min)(maxCorrection, hostAtArrival - expected))
                                                   : expected - (std::min)(maxCorrection, expected - hostAtArrival);
        } else hostAnchor_ = hostAtArrival;
        localAnchor_ = lastArrival_ = localArrival;
        delays_[delayIndex_] = static_cast<uint32_t>(hostAtArrival > sample.hostMs ? hostAtArrival - sample.hostMs : 0);
        delayIndex_ = (delayIndex_ + 1) % kDelayCount;
        if (nDelay_ < kDelayCount) ++nDelay_;

        if (sample.stateHostMs > sample.hostMs) sample.stateHostMs = 0;
        if (size_) {
            const auto& previous = samples_[size_ - 1];
            if (sample.stateHostMs < previous.stateHostMs) {
                sample.stateHostMs = previous.stateHostMs;
                sample.state = previous.state;
            }
            const float dx = sample.x - previous.x, dy = sample.y - previous.y;
            if (dx * dx + dy * dy > walkhold::kPlaceM * walkhold::kPlaceM) size_ = 0;
        }
        if (size_ == kDepth) {
            std::move(samples_.begin() + 1, samples_.end(), samples_.begin());
            --size_;
        }
        samples_[size_++] = sample;
        return restarted ? Ingest::Restarted : late ? Ingest::Late : Ingest::Accepted;
    }

    bool live(uint64_t local) const { return size_ && local >= lastArrival_; }
    bool fresh(uint64_t local) const { return live(local) && local - lastArrival_ <= kPoseHoldMs; }

    Output render(uint64_t local) {
        Output out;
        if (!live(local)) return out;
        const uint32_t desired = wanted_buffer();
        const double step = lastRender_ && local >= lastRender_ ? static_cast<double>(local - lastRender_) * 0.08 : 0.0;
        bufferMs_ += (std::clamp)(static_cast<double>(desired) - bufferMs_, -step, step);
        lastRender_ = local;
        out.bufferMs = static_cast<uint32_t>(std::round(bufferMs_));
        const uint64_t host = projected(local);
        const uint64_t candidate = host > out.bufferMs ? host - out.bufferMs : 0;
        out.targetHostMs = previousTarget_ = (std::max)(candidate, previousTarget_);
        if (out.targetHostMs < samples_[0].hostMs) return out;

        const Sample* before = &samples_[0];
        size_t next = 1;
        for (; next < size_ && samples_[next].hostMs <= out.targetHostMs; ++next) before = &samples_[next];
        out.source = *before;
        out.x = before->x; out.y = before->y; out.z = before->z; out.yaw = before->yaw;
        out.appliedHostMs = before->hostMs;
        if (next < size_) {
            const auto& after = samples_[next];
            const float fraction = static_cast<float>(out.targetHostMs - before->hostMs) /
                                   static_cast<float>(after.hostMs - before->hostMs);
            const float dx = after.x - before->x, dy = after.y - before->y;
            out.x += dx * fraction;
            out.y += dy * fraction;
            out.z = walkhold::segment_z(before->z, after.z, std::sqrt(dx * dx + dy * dy), fraction);
            out.yaw = angle(before->yaw, after.yaw, fraction);
            velocity(*before, after, out);
            out.appliedHostMs = out.targetHostMs;
        } else if (size_ >= 2 && velocity(samples_[size_ - 2], *before, out)) {
            const uint64_t past = out.targetHostMs - before->hostMs;
            const float speedSquared = out.vx * out.vx + out.vy * out.vy + out.vz * out.vz;
            if (speedSquared > 0.0001f) {
                out.extrapMs = static_cast<uint32_t>((std::min)(past, static_cast<uint64_t>(kMaxExtrapMs)));
                const float fraction = static_cast<float>(out.extrapMs) / kMaxExtrapMs;
                const float displacementTime = (out.extrapMs / 1000.f) * (1.f - fraction * 0.5f);
                out.x += out.vx * displacementTime;
                out.y += out.vy * displacementTime;
                out.z += out.vz * displacementTime;
                out.vx *= 1.f - fraction; out.vy *= 1.f - fraction; out.vz *= 1.f - fraction;
                out.appliedHostMs = advance(before->hostMs, out.extrapMs);
            }
        }
        // A drawn bow stays until a newer sample confirms it is down. Movement freshness still drops crouch and the other bits.
        const bool rangedHeld = out.source.stateHostMs != 0 && (out.source.state[3] & 0x08) != 0;
        if (!rangedHeld && (out.source.stateHostMs > out.appliedHostMs || out.targetHostMs - out.source.stateHostMs >= kStateFreshMs))
            out.source.stateHostMs = 0;
        return out;
    }
};

} // namespace kcdmp::rptimeline
