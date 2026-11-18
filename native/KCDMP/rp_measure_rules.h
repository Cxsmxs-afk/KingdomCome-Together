// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#pragma once

#include <algorithm>
#include <array>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <memory>
#include <new>
#include <string_view>

namespace kcdmp::rpmeasure {

constexpr size_t kFields = 17;
constexpr uint32_t kMaxSenderAgeMs = 30000;
using Counts = std::array<uint64_t, kFields>;

inline bool clock_config(bool environment, unsigned value, bool& enabled) {
    if (!environment || value > 1) return false;
    enabled = value == 1;
    return true;
}

struct SampleScope {
    int64_t firstByteQpc = 0;
    uint64_t generation = 0; // Zero when the reader was outside Gameplay.
};

inline bool sample_in_window(SampleScope sample, uint64_t generation, int64_t startQpc) {
    return generation != 0 && sample.generation == generation &&
           sample.firstByteQpc > 0 && sample.firstByteQpc >= startQpc;
}

inline bool partner_id(std::string_view name, uint8_t& peer) {
    constexpr std::string_view prefix = "kcd2mp_";
    if (name.substr(0, prefix.size()) != prefix) return false;
    const auto suffix = name.substr(prefix.size());
    if (suffix.empty() || suffix.size() > 3 || (suffix.size() > 1 && suffix.front() == '0')) return false;
    unsigned value = 0;
    for (char c : suffix) {
        if (c < '0' || c > '9') return false;
        value = value * 10 + static_cast<unsigned>(c - '0');
    }
    if (value > 255) return false;
    peer = static_cast<uint8_t>(value);
    return true;
}

inline bool sender_age_ms(uint32_t sender, uint32_t now, uint32_t& out) {
    const uint32_t age = now - sender;
    if (sender == 0 || age > kMaxSenderAgeMs) return false;
    out = age;
    return true;
}

namespace detail {

inline bool rounded_measurement(double value, uint32_t& out) {
    if (!std::isfinite(value) || value < 0.0) return false;
    const auto limit = (std::numeric_limits<uint32_t>::max)();
    out = value >= static_cast<double>(limit) ? limit : static_cast<uint32_t>(std::ceil(value));
    return true;
}

class Histogram {
    // Keep the entire measurement session in fixed space. Exact quarter-second
    // thresholds and ordinary correction sizes fall in the fine range. Beyond
    // that, use the upper edge of the bucket and retain the exact maximum.
    static constexpr uint32_t kFineLimit = 2000;
    static constexpr uint32_t kCoarseWidth = 16;
    static constexpr uint32_t kHistogramLimit = 65535;
    static constexpr size_t kBuckets = kFineLimit + 1 + (kHistogramLimit - kFineLimit + kCoarseWidth - 1) / kCoarseWidth;
    std::array<uint32_t, kBuckets> buckets_{};
    uint64_t observed_ = 0;
    uint32_t max_ = 0;

    static size_t bucket_for(uint32_t value) {
        const uint32_t bounded = (std::min)(value, kHistogramLimit);
        if (bounded <= kFineLimit) return bounded;
        return kFineLimit + 1 + (bounded - kFineLimit - 1) / kCoarseWidth;
    }

    uint32_t upper_bound(size_t bucket) const {
        if (bucket <= kFineLimit) return static_cast<uint32_t>(bucket);
        if (bucket + 1 == kBuckets) return max_; // Includes values past the finite histogram range.
        const auto upper = kFineLimit + static_cast<uint32_t>(bucket - kFineLimit) * kCoarseWidth;
        return (std::min)(upper, max_);
    }

public:
    struct Summary { uint32_t p50, p95, max; uint64_t observed; };

    void add(uint32_t value) {
        max_ = (std::max)(max_, value);
        auto& bucket = buckets_[bucket_for(value)];
        if (bucket != (std::numeric_limits<uint32_t>::max)()) {
            ++bucket;
            ++observed_;
        }
    }

    Summary summary() const {
        if (observed_ == 0) return {};
        const uint64_t rank50 = (observed_ * 50 + 99) / 100;
        const uint64_t rank95 = (observed_ * 95 + 99) / 100;
        uint64_t rank = 0;
        uint32_t p50 = 0;
        bool have50 = false;
        for (size_t bucket = 0; bucket < buckets_.size(); ++bucket) {
            rank += buckets_[bucket];
            if (!have50 && rank >= rank50) {
                p50 = upper_bound(bucket);
                have50 = true;
            }
            if (rank >= rank95) return {p50, upper_bound(bucket), max_, observed_};
        }
        return {max_, max_, max_, observed_};
    }

    void reset() {
        buckets_.fill(0);
        observed_ = 0;
        max_ = 0;
    }
};

struct Observations {
    Histogram e2e, hop, correction, action;
    uint64_t poses = 0;
    uint64_t hard = 0;

    Counts counts() const {
        const auto latencies = e2e.summary();
        const auto agent = hop.summary();
        const auto corrections = correction.summary();
        const auto actions = action.summary();
        // Step and owner fields have no independent first-stage native observation.
        return {latencies.p50, latencies.p95, latencies.max, agent.p95,
            corrections.observed, corrections.p95, corrections.max, hard, 0, actions.p95, 0,
            latencies.observed, agent.observed, actions.observed, 0, poses, 0};
    }

    void reset() {
        e2e.reset();
        hop.reset();
        correction.reset();
        action.reset();
        poses = 0;
        hard = 0;
    }
};

struct Peer {
    Observations observations;
    uint32_t eid = 0;
    uint32_t lastSender = 0;
};

} // namespace detail

// Aggregates cover one measurement session. Per-peer data begins anew when a
// figure is rebound. Callers serialize access.
class Ledger {
    detail::Observations aggregate_;
    std::array<std::unique_ptr<detail::Peer>, 256> peers_{};

    detail::Peer* peer_at(uint8_t peer) {
        auto& entry = peers_[peer];
        if (!entry) entry.reset(new (std::nothrow) detail::Peer());
        return entry.get();
    }

public:
    void hop(uint8_t peer, double ms) {
        uint32_t measured = 0;
        if (!detail::rounded_measurement(ms, measured)) return;
        auto* body = peer_at(peer);
        if (!body) return;
        body->observations.hop.add(measured);
        aggregate_.hop.add(measured);
    }

    void pose(uint8_t peer, uint32_t eid, uint32_t sender, uint32_t now, bool sharedClock, double correctionCm, bool hard) {
        if (eid == 0) return;
        auto* entry = peer_at(peer);
        if (!entry) return;
        auto& body = *entry;
        if (body.eid != eid) {
            // An entity identity can belong to only one measured peer at a time.
            forget(eid);
            if (body.eid != 0) {
                body.observations.reset();
                body.lastSender = 0;
            }
            body.eid = eid;
        }

        auto& local = body.observations;
        ++local.poses;
        ++aggregate_.poses;
        if (hard) {
            ++local.hard;
            ++aggregate_.hard;
        }
        uint32_t correction = 0;
        if (correctionCm > 0.0 && detail::rounded_measurement(correctionCm, correction)) {
            local.correction.add(correction);
            aggregate_.correction.add(correction);
        }

        uint32_t age = 0;
        if (!sharedClock || !sender_age_ms(sender, now, age)) return;
        const uint32_t advance = sender - body.lastSender;
        if (body.lastSender != 0 && (advance == 0 || advance >= uint32_t{0x80000000})) return;
        body.lastSender = sender;
        local.e2e.add(age);
        aggregate_.e2e.add(age);
    }

    void action(uint8_t peer, double ms) {
        uint32_t measured = 0;
        if (!detail::rounded_measurement(ms, measured)) return;
        auto* body = peer_at(peer);
        if (!body) return;
        body->observations.action.add(measured);
        aggregate_.action.add(measured);
    }

    bool peer_of(uint32_t eid, uint8_t& peer) const {
        if (eid == 0) return false;
        for (size_t i = 0; i < peers_.size(); ++i) {
            if (peers_[i] && peers_[i]->eid == eid) {
                peer = static_cast<uint8_t>(i);
                return true;
            }
        }
        return false;
    }

    void forget(uint32_t eid) {
        uint8_t peer = 0;
        if (peer_of(eid, peer)) peers_[peer].reset();
    }

    void reset() {
        for (auto& entry : peers_) entry.reset();
        aggregate_.reset();
    }

    Counts counts() const { return aggregate_.counts(); }

    Counts peer_counts(uint8_t peer) const {
        return peers_[peer] ? peers_[peer]->observations.counts() : Counts{};
    }
};

} // namespace kcdmp::rpmeasure
