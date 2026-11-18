// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#pragma once

#include "load_sequence_rules.h"

#include <array>
#include <limits>

namespace kcdmp::t3recovery {

constexpr uint8_t kOp = 248, kVersion = 1;
constexpr size_t kAreas = 6, kReconnectAt = 2 * kAreas, kReconnectFields = 5,
                 kCheckedAt = kReconnectAt + kReconnectFields, kFailedAt = kCheckedAt + kAreas,
                 kFields = kFailedAt + kAreas;
constexpr size_t kHeaderBytes = 1 + 1 + 8 + 4 + 8, kRequestBytes = kHeaderBytes + 8 * kFields,
                 kAckBytes = kRequestBytes - 1;
static_assert(kRequestBytes == 254 && kAckBytes <= 512);

constexpr std::array<const char*, kFields> kNames{
    "digest_mismatch_body", "digest_mismatch_npc", "digest_mismatch_corpse",
    "digest_mismatch_loot", "digest_mismatch_clock", "digest_mismatch_weather",
    "resync_body", "resync_npc", "resync_corpse", "resync_loot", "resync_clock", "resync_weather",
    "reconnect_ms", "reconnect_same_world", "reconnect_success", "reconnect_timeout", "reconnect_reload",
    "digest_checked_body", "digest_checked_npc", "digest_checked_corpse",
    "digest_checked_loot", "digest_checked_clock", "digest_checked_weather",
    "resync_failed_body", "resync_failed_npc", "resync_failed_corpse",
    "resync_failed_loot", "resync_failed_clock", "resync_failed_weather",
};

struct Report {
    uint64_t generation = 0;
    uint32_t world = 0;
    uint64_t run = 0;
    std::array<uint64_t, kFields> fields{};
};

inline bool decode(const uint8_t* body, size_t size, Report& report) {
    using loadsequence::read_le;
    if (!body || size != kRequestBytes || body[0] != kOp || body[1] != kVersion) return false;
    Report parsed{};
    parsed.generation = read_le(body + 2, 8);
    parsed.world = static_cast<uint32_t>(read_le(body + 10, 4));
    parsed.run = read_le(body + 14, 8);
    if (!parsed.generation || !parsed.world || !parsed.run) return false;
    for (size_t i = 0; i < kFields; ++i) {
        parsed.fields[i] = read_le(body + kHeaderBytes + 8 * i, 8);
        if (parsed.fields[i] > uint64_t((std::numeric_limits<int64_t>::max)())) return false;
    }
    for (size_t i = kReconnectAt + 1; i <= kReconnectAt + 3; ++i)
        if (parsed.fields[i] > 1) return false;
    for (size_t i = 0; i < kAreas; ++i)
        if (parsed.fields[i] > parsed.fields[kCheckedAt + i]) return false;
    report = parsed;
    return true;
}

struct Receiver {
    uint64_t generation = 0, connection = 0;
    uint8_t role = 0;
    bool play = false;
};

// The Agent owns comparisons, actual repair confirmations and the network
// reconnect timeline. Native validates the mirror's current local scope.
// A freshly attached pipe can report an already-running managed recovery.
class Mirror {
    Receiver receiver_{};
    Report prior_{};

public:
    bool accept(const Receiver& receiver, const Report& report) {
        if (!receiver.play || !receiver.connection || (receiver.role != 1 && receiver.role != 2) ||
            !report.generation || receiver.generation != report.generation || !report.world || !report.run)
            return false;
        if (receiver.connection == receiver_.connection && receiver.role == receiver_.role &&
            report.generation == prior_.generation && report.world == prior_.world && report.run == prior_.run) {
            for (size_t i = 0; i < kFields; ++i) {
                const bool reconnect = i >= kReconnectAt && i < kCheckedAt;
                if (!reconnect && report.fields[i] < prior_.fields[i]) return false;
            }
        }
        receiver_ = receiver;
        prior_ = report;
        return true;
    }
};

} // namespace kcdmp::t3recovery
