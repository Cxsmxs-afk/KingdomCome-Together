// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#pragma once

#include <array>
#include <cstddef>
#include <cstdint>

namespace kcdmp::loadsequence {

enum class Phase : uint8_t { Menu, Offer, Load, Reconstruct, Settle, Play, Exit, Disabled, Query };
enum class Gate : uint8_t { Legacy, Hold, Setup, Play };
enum class Access : uint8_t { Read, Control, Setup, Live };
constexpr uint8_t kOp = 249, kLinkHeld = 1, kHostOfferHeld = 2, kPrivateHold = 4,
                  kFlags = kLinkHeld | kHostOfferHeld | kPrivateHold, kMainApplied = 0x80;
constexpr size_t kRequestBytes = 17, kAckBytes = 14, kPhases = 7;

struct Request {
    Phase phase = Phase::Query;
    uint8_t flags = 0;
    uint64_t generation = 0;
    uint32_t id = 0;
    uint16_t holdSeconds = 0;
};

inline uint64_t read_le(const uint8_t* p, size_t n) {
    uint64_t v = 0;
    for (size_t i = 0; i < n; ++i) v |= uint64_t(p[i]) << (i * 8);
    return v;
}

inline void write_le(uint8_t* p, uint64_t v, size_t n) {
    for (size_t i = 0; i < n; ++i) p[i] = static_cast<uint8_t>(v >> (i * 8));
}

inline bool decode(const uint8_t* bytes, size_t size, Request& out) {
    if (!bytes || size != kRequestBytes || bytes[0] != kOp || bytes[1] > static_cast<uint8_t>(Phase::Query)) return false;
    out = {static_cast<Phase>(bytes[1]), bytes[2], read_le(bytes + 5, 8),
           static_cast<uint32_t>(read_le(bytes + 13, 4)), static_cast<uint16_t>(read_le(bytes + 3, 2))};
    if (!out.id || (out.flags & ~kFlags) || (out.phase == Phase::Query && out.generation) ||
        ((out.phase == Phase::Query || out.phase == Phase::Disabled) && out.flags)) return false;
    if (out.flags & kHostOfferHeld) return out.holdSeconds >= 60 && out.holdSeconds <= 900;
    if (out.flags & kLinkHeld) return out.holdSeconds == 80;
    return out.holdSeconds == 0;
}

inline void encode_ack(uint8_t* out, Phase phase, uint8_t flags, uint64_t generation, uint32_t id) {
    out[0] = static_cast<uint8_t>(phase);
    out[1] = flags;
    write_le(out + 2, generation, 8);
    write_le(out + 10, id, 4);
}

// The atomic ingress stamp includes the permission class and a revision
// shared by the first pipe byte and the Main application. A held or
// superseded packet never becomes eligible when Play opens later.
constexpr Gate gate(uint64_t stamp) { return static_cast<Gate>(stamp & 3u); }
constexpr uint64_t generation_stamp(uint64_t stamp) { return stamp >> 32; }
constexpr bool old_generation(uint64_t received, uint64_t current) {
    return generation_stamp(received) != generation_stamp(current);
}
constexpr uint64_t advance(uint64_t stamp, Gate next, bool generationChanged = false) {
    const uint64_t generation = generation_stamp(stamp) + static_cast<uint64_t>(generationChanged);
    const auto revision = static_cast<uint32_t>((stamp & ~uint64_t{3}) + 4);
    return (generation << 32) | revision | static_cast<uint8_t>(next);
}
constexpr bool allowed(uint64_t received, uint64_t current, Access access) {
    if (access == Access::Read) return true;
    if (received != current) return false;
    if (access == Access::Control || gate(current) == Gate::Legacy || gate(current) == Gate::Play) return true;
    return access == Access::Setup && gate(current) == Gate::Setup;
}

constexpr bool calendar_allowed(Phase phase, uint8_t flags) {
    return phase != Phase::Menu && phase != Phase::Exit && !(flags & kPrivateHold);
}

struct Counts {
    uint64_t world_gen = 0, load_phase = static_cast<uint8_t>(Phase::Disabled), stale_gen_dropped = 0, observations = 0;
    std::array<uint64_t, kPhases> phase_ms{};
};

class State {
    Counts counts_{};
    uint64_t connection_ = 0, stamp_ = 0, since_ = 0;
    uint32_t lastId_ = 0;
    uint16_t holdSeconds_ = 0;
    uint8_t flags_ = 0;
    Phase phase_ = Phase::Disabled;
    bool fenced_ = false, exited_ = false;

    void accrue(uint64_t now) {
        const auto phase = static_cast<size_t>(phase_);
        if (phase < kPhases && now >= since_) counts_.phase_ms[phase] += now - since_;
        since_ = now;
    }

public:
    uint64_t stamp() const { return stamp_; }
    Phase phase() const { return phase_; }
    uint8_t flags() const { return flags_; }
    uint16_t hold_seconds() const { return holdSeconds_; }
    uint64_t generation() const { return counts_.world_gen; }
    uint64_t connection() const { return connection_; }

    bool snapshot_allowed(uint64_t received, uint64_t expectedGeneration, bool final) const {
        if (!connection_ || !expectedGeneration || expectedGeneration != generation() ||
            received != stamp_ || flags_) return false;
        // On an explicit leave only the final save may cross the Exit fence.
        return phase_ == Phase::Play || phase_ == Phase::Settle || (final && phase_ == Phase::Exit);
    }

    bool apply(const Request& request, uint64_t connection, uint64_t now) {
        if (!connection) return false;
        if (request.phase == Phase::Query) return true;
        if (!request.generation && request.phase != Phase::Menu && request.phase != Phase::Disabled) return false;

        const bool newConnection = connection != connection_;
        if (!newConnection && request.generation < counts_.world_gen) return false;
        if (!newConnection && lastId_) {
            const auto delta = request.id - lastId_;
            if (!delta) return request.phase == phase_ && request.flags == flags_ && request.holdSeconds == holdSeconds_ &&
                               request.generation == counts_.world_gen;
            if (delta >= uint32_t{0x80000000}) return false;
        }
        const bool generationChanged = request.generation != counts_.world_gen;
        const bool fresh = newConnection || generationChanged;
        const bool abort = request.phase == Phase::Menu && phase_ >= Phase::Offer && phase_ <= Phase::Play;
        if (!fresh && request.phase != Phase::Disabled) {
            if (exited_ && request.phase != Phase::Exit && request.phase != Phase::Menu) return false;
            if (phase_ != Phase::Disabled && request.phase < phase_ &&
                !(request.phase == Phase::Menu && (abort || phase_ == Phase::Exit))) return false;
        }
        lastId_ = request.id;
        if (!fresh && request.phase == phase_ && request.flags == flags_ && request.holdSeconds == holdSeconds_) return true;
        if (fresh) {
            exited_ = false;
            counts_.world_gen = request.generation;
            counts_.phase_ms = {};
            counts_.observations = 0;
        } else accrue(now);
        since_ = now;
        connection_ = connection;
        phase_ = request.phase;
        flags_ = request.flags;
        holdSeconds_ = request.holdSeconds;
        counts_.load_phase = static_cast<uint8_t>(phase_);
        fenced_ = phase_ != Phase::Disabled;
        if (phase_ == Phase::Exit || (!fresh && abort)) exited_ = true;
        if (fenced_) ++counts_.observations;
        Gate next = Gate::Hold;
        if (!fenced_) next = Gate::Legacy;
        else if (!flags_ && phase_ == Phase::Play) next = Gate::Play;
        else if (!flags_ && (phase_ == Phase::Reconstruct || phase_ == Phase::Settle)) next = Gate::Setup;
        stamp_ = advance(stamp_, next, generationChanged);
        return true;
    }

    void disconnect(uint64_t connection, uint64_t now) {
        if (connection != connection_ || !fenced_) return;
        accrue(now);
        phase_ = Phase::Exit;
        flags_ = 0;
        holdSeconds_ = 0;
        counts_.load_phase = static_cast<uint8_t>(phase_);
        exited_ = true;
        connection_ = 0;
        ++counts_.observations;
        stamp_ = advance(stamp_, Gate::Hold);
    }

    Counts snapshot(uint64_t now) const {
        auto out = counts_;
        auto phase = static_cast<size_t>(phase_);
        if (phase < kPhases && now >= since_) out.phase_ms[phase] += now - since_;
        return out;
    }
};

} // namespace kcdmp::loadsequence
