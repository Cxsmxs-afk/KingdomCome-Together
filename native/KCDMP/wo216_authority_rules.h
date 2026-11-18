// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#pragma once
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <limits>

namespace kcdmp::wo216authority {
constexpr uint8_t kOp = 168;
constexpr uint64_t kSettleMs = 5000, kUnboundHideMs = 10000;
constexpr size_t kRequestBytes = 14, kAckBytes = 19; // request includes opcode; ACK excludes generic header
// Disabled keeps the old per-body path available after an explicit role 0 -> 2.
enum class Phase : uint8_t { Disabled = 0, Load = 1, Gameplay = 2, Query = 3 };
inline uint64_t read_u64(const uint8_t* p) { uint64_t v = 0; for (unsigned i = 0; i < 8; ++i) v |= uint64_t(p[i]) << (8 * i); return v; }
inline uint32_t read_u32(const uint8_t* p) { uint32_t v = 0; for (unsigned i = 0; i < 4; ++i) v |= uint32_t(p[i]) << (8 * i); return v; }
inline void put_u64(uint8_t* p, uint64_t v) { for (unsigned i = 0; i < 8; ++i) p[i] = uint8_t(v >> (8 * i)); }
inline void put_u32(uint8_t* p, uint32_t v) { for (unsigned i = 0; i < 4; ++i) p[i] = uint8_t(v >> (8 * i)); }
struct Request { Phase phase = Phase::Query; uint64_t generation = 0; uint32_t id = 0; };
inline bool request(const uint8_t* b, size_t n, Request* out) {
    if (!b || !out || n != kRequestBytes || b[0] != kOp || b[1] > uint8_t(Phase::Query)) return false;
    out->phase = static_cast<Phase>(b[1]); out->generation = read_u64(b + 2); out->id = read_u32(b + 10); return true;
}
struct State {
    Phase phase = Phase::Load;
    uint64_t generation = 0, gameplayAtMs = 0;
    bool loadAccepted = false, legacyArmed = false;
    bool enabled() const { return phase != Phase::Disabled; }
    bool active(uint8_t role, bool hooked, uint64_t now) const {
        return role == 2 && hooked && phase == Phase::Gameplay && loadAccepted && now >= gameplayAtMs && now - gameplayAtMs >= kSettleMs;
    }
    bool legacy(uint8_t role) const { return role == 2 && !enabled() && legacyArmed; }
    uint64_t active_from() const {
        if (phase != Phase::Gameplay || !loadAccepted || gameplayAtMs > (std::numeric_limits<uint64_t>::max)() - kSettleMs) return 0;
        return gameplayAtMs + kSettleMs;
    }
    uint32_t age(uint64_t now) const {
        if (phase != Phase::Gameplay || now < gameplayAtMs) return 0;
        const uint64_t d = now - gameplayAtMs;
        return d > (std::numeric_limits<uint32_t>::max)() ? (std::numeric_limits<uint32_t>::max)() : static_cast<uint32_t>(d);
    }
    bool role_generation(bool bound, uint64_t gen) const { return !bound || gen == generation; }
    void role_changed(uint8_t oldRole, uint8_t role, bool generationBound = false) {
        if (oldRole == role) return;
        legacyArmed = role == 2 && !enabled() && generationBound;
        // An explicit Load may precede role 2 preparation. Preserve that same
        // pending generation, while an arbitrary role switch cannot reuse Gameplay.
        if (!(generationBound && role == 2 && phase == Phase::Load && loadAccepted)) {
            loadAccepted = false; gameplayAtMs = 0;
            if (enabled()) phase = Phase::Load;
        }
    }
    bool apply(const Request& r, uint64_t now) {
        if (r.phase == Phase::Query) return r.generation == 0;
        if (r.generation == 0 || r.generation < generation) return false;
        if (r.phase == Phase::Disabled || r.phase == Phase::Load) {
            if (r.generation == generation) return r.phase == phase && (r.phase == Phase::Disabled || loadAccepted);
            generation = r.generation; phase = r.phase; gameplayAtMs = 0; legacyArmed = false;
            loadAccepted = phase == Phase::Load; return true;
        }
        if (r.phase != Phase::Gameplay || r.generation != generation || !loadAccepted || !enabled()) return false;
        if (phase != Phase::Gameplay) { phase = Phase::Gameplay; gameplayAtMs = now; }
        return true;
    }
    void ack(uint8_t* b, uint8_t role, bool hooked, uint64_t now, uint32_t id) const {
        b[0] = uint8_t(phase); b[1] = enabled() ? 1 : 0; b[2] = active(role, hooked, now) ? 1 : 0;
        put_u64(b + 3, generation); put_u32(b + 11, age(now)); put_u32(b + 15, id);
    }
};
// WO-127/WO-107: the debug PauseNPC context is 0x08. Claim only a body
// observed running with no suspend reason. Cleanup is deliberately stricter
// than the engine's multi-context Resume: never call it while another bit exists.
struct PauseLease {
    uint32_t eid = 0; uint64_t wuid = 0, generation = 0; bool owned = false;
    bool claim(uint32_t e, uint64_t w, uint64_t gen, int beforeState, int beforeMask) {
        if (!e || !w || !gen || beforeState != 0 || beforeMask != 0) return false;
        eid = e; wuid = w; generation = gen; owned = true; return true;
    }
    bool same_owner(uint32_t e, uint64_t w, uint64_t gen) const { return owned && eid == e && wuid == w && generation == gen; }
    bool may_resume(uint32_t e, uint64_t w, uint64_t gen, int state, int mask) const {
        return same_owner(e, w, gen) && (state == 1 || state == 2) && mask == 0x08;
    }
    void forget() { owned = false; }
};
struct Stats {
    uint64_t gateArmedAtS = 0, decisions = 0, duringLoad = 0, near = 0, bound = 0, hidden = 0, failsafe = 0, expired = 0;
};
inline int stats_prefix(char* out, size_t cap, const Stats& s) {
    return std::snprintf(out, cap, "gate_armed_at_s=%llu decision_blocked=%llu decision_blocked_during_load=%llu npc_near=%llu npc_bound=%llu npc_hidden=%llu hide_failsafe_on=%llu unhidden_unbound_10s=%llu ",
        static_cast<unsigned long long>(s.gateArmedAtS), static_cast<unsigned long long>(s.decisions), static_cast<unsigned long long>(s.duringLoad),
        static_cast<unsigned long long>(s.near), static_cast<unsigned long long>(s.bound), static_cast<unsigned long long>(s.hidden),
        static_cast<unsigned long long>(s.failsafe), static_cast<unsigned long long>(s.expired));
}
// Only a complete census can authorize hiding. Integer comparison keeps the
// exact 30 percent boundary and avoids division by zero / partial-pass ratios.
inline bool coverage(unsigned near, unsigned nearBound, bool havePlayer = true) {
    return havePlayer && near && nearBound && uint64_t(nearBound) * 10 >= uint64_t(near) * 3;
}
struct HideLease {
    bool hidden = false, expired = false;
    uint64_t hiddenAtMs = 0;
    // expiry belongs to an unbound episode: never re-hide it the next census.
    bool desired(bool bound, bool eligible, bool covered, uint64_t now) {
        if (bound) { expired = false; return false; }
        if (hidden && now >= hiddenAtMs && now - hiddenAtMs >= kUnboundHideMs) expired = true;
        return !bound && eligible && covered && !expired;
    }
    void applied(bool hide, uint64_t now) { if (hide && !hidden) hiddenAtMs = now; hidden = hide; }
};
inline bool hide_eligible(bool ready, bool lifeKnown, bool dead, bool near, bool fighting, bool exempt, bool streamable) {
    return ready && lifeKnown && !dead && near && !fighting && !exempt && streamable;
}
} // namespace kcdmp::wo216authority
