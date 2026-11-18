// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#pragma once
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>

namespace kcdmp::p4rules {
enum class Source : uint8_t { Unknown, Leash, Respawn, Unstuck, Join, LeashSettle, VideoLanding, VideoRollback, SceneNudge };
constexpr size_t kFields = 19, kStatsBytes = 2 + 9 * kFields;
constexpr uint64_t kFreshMs = 2000, kRecoveryMs = 5000, kFallMaxMs = 20000, kGroundedMs = 1000;
struct Context { Source source = Source::Unknown; uint64_t deadline = 0, generation = 0; };
inline uint64_t u64(const uint8_t* p) { uint64_t v = 0; for (unsigned i = 0; i < 8; ++i) v |= uint64_t(p[i]) << (8 * i); return v; }
inline void put64(uint8_t* p, uint64_t v) { for (unsigned i = 0; i < 8; ++i) p[i] = uint8_t(v >> (8 * i)); }
inline bool context(const uint8_t* b, size_t n, Context* out) {
    if (!b || !out || n != 17 || b[0] > uint8_t(Source::SceneNudge)) return false;
    *out = {static_cast<Source>(b[0]), u64(b + 1), u64(b + 9)}; return true;
}
inline bool fresh(const Context& c, uint64_t now, uint64_t gen, bool gameplay, uint64_t lastLifeEdge = 0) {
    return c.deadline >= kFreshMs && (!lastLifeEdge || c.deadline - kFreshMs > lastLifeEdge) && c.source != Source::Unknown && c.generation && c.generation == gen && gameplay &&
        c.deadline >= now && c.deadline - now <= kFreshMs;
}
inline bool finite3(const float* p) { return p && std::isfinite(p[0]) && std::isfinite(p[1]) && std::isfinite(p[2]); }
inline float distance(const float* a, const float* b) {
    const float x = a[0] - b[0], y = a[1] - b[1], z = a[2] - b[2]; return std::sqrt(x*x + y*y + z*z);
}
struct Drops { float source = 0, placement = 0, sourceDz = 0, placementDz = 0; };
inline bool ground_ok(const float* origin, const float* target, const float* ground, Drops* d) {
    if (!finite3(origin) || !finite3(target) || !finite3(ground) || !d) return false;
    d->sourceDz = origin[2] - ground[2]; d->placementDz = target[2] - ground[2];
    d->source = d->sourceDz > 0 ? d->sourceDz : 0; d->placement = d->placementDz > 0 ? d->placementDz : 0;
    return d->source <= 2.0f && std::fabs(d->placementDz) <= 2.0f;
}
inline bool state_safe(bool combatKnown, bool combat, bool deadKnown, bool dead, bool koKnown, bool ko, bool recovery) {
    return combatKnown && deadKnown && koKnown && !dead && (recovery || (!combat && !ko));
}
struct Landing {
    bool held = false; uint64_t began = 0, generation = 0, groundedAt = 0; bool haveGrounded = false;
    void arm(uint64_t now, uint64_t gen) {
        // A repeated placement cannot extend the hard lease forever.
        if (!held || generation != gen) began = now;
        held = true; generation = gen; haveGrounded = false; groundedAt = 0;
    }
    bool release(uint64_t now, uint64_t gen, bool knownGrounded) {
        if (!held) return false;
        if (gen != generation || now < began || now - began >= kFallMaxMs) return true;
        if (!knownGrounded) { haveGrounded = false; return false; }
        if (!haveGrounded) { groundedAt = now; haveGrounded = true; }
        return now >= groundedAt && now - groundedAt >= kGroundedMs;
    }
};
struct Recovery {
    uint64_t until = 0, generation = 0, wuid = 0; uintptr_t soul = 0;
    void arm(uint64_t now, uint64_t gen, uintptr_t who, uint64_t id) { until = now + kRecoveryMs; generation = gen; soul = who; wuid = id; }
    bool active(uint64_t now, uint64_t gen, uintptr_t who, uint64_t id) const {
        return until && now < until && gen && gen == generation && who && who == soul && id && id == wuid;
    }
};
inline size_t stats(uint8_t* out, size_t cap, const uint64_t* values) {
    if (!out || !values || cap < kStatsBytes) return 0;
    out[0] = 1; out[1] = uint8_t(kFields);
    for (size_t i = 0; i < kFields; ++i) { out[2 + 9*i] = uint8_t(i); put64(out + 3 + 9*i, values[i]); }
    return kStatsBytes;
}
inline uint64_t metres(float v) {
    if (!std::isfinite(v) || v <= 0) return 0;
    const double d = std::ceil(static_cast<double>(v));
    return d >= static_cast<double>((std::numeric_limits<int64_t>::max)()) ? uint64_t((std::numeric_limits<int64_t>::max)()) : static_cast<uint64_t>(d);
}
inline const char* source_name(Source s) {
    constexpr const char* names[] = {"unknown", "leash", "respawn", "unstuck", "join", "leash_settle", "video_landing", "video_rollback", "scene_nudge"};
    const unsigned i = static_cast<unsigned>(s); return i < 9 ? names[i] : "unknown";
}
} // namespace kcdmp::p4rules
