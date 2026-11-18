// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#include "p4.h"
#include "wo216.h"
#include "wo163.h"
#include "wo203.h"
#include "respawn_actions.h"
#include "rttr_abi.h"
#include "buffs.h"
#include "engine.h"
#include "motion.h"
#include "npc_drive.h"
#include "hangover.h"
#include "hits.h"
#ifndef KCDMP_P4_TEST
#include "log.h"
#else
namespace kcdmp::p4 { void test_trace(const char*, ...); }
#define logf test_trace
uint64_t kcdmp_p4_test_now();
#endif
#include <windows.h>
#include <atomic>
#include <cstring>

namespace kcdmp::p4 {
namespace {
uint64_t now_ms() {
#ifdef KCDMP_P4_TEST
    return kcdmp_p4_test_now();
#else
    return GetTickCount64();
#endif
}
uint64_t g_values[p4rules::kFields]{};
std::atomic<uint64_t> g_until{0}, g_recGen{0}, g_recWuid{0}, g_reset{0}, g_lifeEdge{0};
std::atomic<uintptr_t> g_recSoul{0};
p4rules::Landing g_landing;
uint64_t g_tickReset = 0, g_lastTpAt = 0, g_lastTpGen = 0, g_hitAt = 0;
Source g_lastSource = Source::Unknown;
float g_lastTp[3]{}, g_lastHitHp = 0, g_prev[3]{};
uint8_t g_lastPeer = 0;
const char* g_hitSource = "none";
uint64_t g_prevAt = 0, g_fastAt = 0, g_groundAt = 0, g_monitorAt = 0, g_airAt = 0;
float g_airHigh = 0;
void* g_observedSoul = nullptr; uint64_t g_observedWuid = 0; bool g_deadSeen = false;
bool g_havePrevious = false, g_ground = false, g_forceGround = true;
void* canonical(void* soul) { void* s = soul ? buffs::as_c_soul(soul) : nullptr; return s ? s : soul; }
uint64_t own_wuid() { return actions::entity_wuid(engine::entity_by_id(0x7777)); }
void bump(size_t i) { if (g_values[i] < uint64_t(INT64_MAX)) ++g_values[i]; }
void maximum(size_t i, float v) { const uint64_t n = p4rules::metres(v); if (n > g_values[i]) g_values[i] = n; }
size_t source_field(Source s) {
    const unsigned i = static_cast<unsigned>(s);
    return i >= 1 && i <= 4 ? i : i >= 5 && i <= 8 ? i + 10 : 0;
}
bool player_flags(bool recovery) {
    void* soul = rttr::read_player_soul();
    bool combat = false, dead = false, ko = false;
    const bool ck = motion::combat_mode_of(0x7777, &combat);
    const bool dk = soul && rttr::soul_bool(soul, "IsDead", &dead);
    const bool kk = soul && rttr::soul_bool(soul, "IsUnconscious", &ko);
    npcdrive::PhysicsStatus ps{};
    const bool pk = npcdrive::physics_status(engine::entity_by_id(0x7777), &ps) && ps.present;
    if (!pk || (!recovery && (!ps.living || motion::local_down_now()))) return false;
    return p4rules::state_safe(ck, combat, dk, dead, kk, ko, recovery);
}
uint8_t snapshot_op(uint8_t, const uint8_t* body, size_t len, uint8_t* out, size_t cap, size_t* used) {
    return snapshot(body, len, out, cap, used);
}
struct Register { Register() { wo203::reg_op(251, snapshot_op); } } g_register;
} // namespace
uint64_t generation() { return wo216::lifecycle_generation(); }
bool gameplay() { return wo216::lifecycle_gameplay(); }
bool ready_context(Context c) { return p4rules::fresh(c, now_ms(), generation(), gameplay(), g_lifeEdge.load()); }
void note_life_edge(const char* why) { g_lifeEdge.store(now_ms()); logf("T3-LIFE-EDGE source=%s tick=%llu", why, static_cast<unsigned long long>(g_lifeEdge.load())); }
void invalidate() { g_until.store(0, std::memory_order_release); g_reset.fetch_add(1); g_lifeEdge.store(now_ms()); }
bool recovery_policy() {
    const uint64_t until = g_until.load(std::memory_order_acquire);
    return until && now_ms() < until && g_recGen.load() == generation() && gameplay() && g_until.load() == until;
}
bool protected_player(void* soul) {
    return recovery_policy() && canonical(soul) == reinterpret_cast<void*>(g_recSoul.load()) && own_wuid() == g_recWuid.load() &&
        canonical(rttr::read_player_soul()) == canonical(soul);
}
void arm_recovery(void* soul) {
    g_until.store(0, std::memory_order_release);
    const uint64_t wuid = own_wuid(), gen = generation();
    if (!soul || !wuid || !gen || !gameplay()) return;
    g_recSoul.store(reinterpret_cast<uintptr_t>(canonical(soul))); g_recWuid.store(wuid); g_recGen.store(gen);
    g_until.store(now_ms() + p4rules::kRecoveryMs, std::memory_order_release);
    logf("T3-RECOVERY armed duration_ms=5000 generation=%llu hp_guard=survival mod_damage=blocked vanilla_zero_damage=unverified", static_cast<unsigned long long>(gen));
}
Placement place(const float partner[3], float dist, Context c, bool recovery, const float* death) {
    Placement r{};
    if (!ready_context(c) || !p4rules::finite3(partner) || !std::isfinite(dist) || dist < 0 || dist > 30) {
        bump(8); logf("T3-TP refused=stale_or_context source=%s generation=%llu deadline=%llu", p4rules::source_name(c.source), static_cast<unsigned long long>(c.generation), static_cast<unsigned long long>(c.deadline)); return r;
    }
    if (!actions::player_position(r.before) || !p4rules::finite3(r.before) || !player_flags(recovery)) { bump(7); return r; }
    const float* origin = death ? death : r.before;
    if (!p4rules::finite3(origin)) { bump(9); return r; }
    // Target combat is proved by the nonce-bound peer Snapshot contract in the
    // agent. There is no verified cheap native spatial hostility query here.
    // Do not turn 8k campaign souls into a blocking teleport-frame world scan.
    p4rules::Drops drops; bool refusedDrop = false;
    float originalTarget[3]{};
    for (int k = 0; k < (dist > 0 ? 8 : 1); ++k) {
        const float a = 0.785398163f * static_cast<float>(k);
        const float input[3] = {partner[0] + dist * std::cos(a), partner[1] + dist * std::sin(a), partner[2]};
        float ground[3]{}; ++r.tried;
        if (!hangover::snap_to_ground(input, ground)) continue;
        if (!p4rules::ground_ok(origin, input, ground, &drops)) {
            refusedDrop = true; logf("T3-TP refused=drop source=%s source_drop_m=%.3f placement_drop_m=%.3f source_dz=%.3f placement_dz=%.3f", p4rules::source_name(c.source), drops.source, drops.placement, drops.sourceDz, drops.placementDz); continue;
        }
        if (p4rules::distance(ground, partner) > 30.0f) continue;
        std::memcpy(r.target, ground, 12); std::memcpy(originalTarget, input, 12); r.snapped = true; break;
    }
    if (!r.snapped) { if (refusedDrop) bump(9); logf("T3-TP refused=no_safe_ground source=%s candidates=%d", p4rules::source_name(c.source), r.tried); return r; }
    // All checks remain immediately adjacent to the actual write. No accepted
    // old deadline or generation is reset by the main queue or ground search.
    if (!ready_context(c)) { bump(8); return r; }
    if (!player_flags(recovery)) { bump(7); return r; }
    int beforeFlying = 1;
    if (!npcdrive::living_flying(engine::entity_by_id(0x7777), &beforeFlying)) {
        logf("T3-TP refused=player_physics_unknown source=%s", p4rules::source_name(c.source)); return r;
    }
    const bool previousLease = g_landing.held;
    r.fallHeld = actions::suppress_fall_damage(true);
    if (!r.fallHeld) { logf("T3-TP refused=fall_guard_unavailable source=%s", p4rules::source_name(c.source)); return r; }
    if (!ready_context(c)) { if (!previousLease) actions::suppress_fall_damage(false); bump(8); return r; }
    const bool ran = actions::teleport_player(r.target[0], r.target[1], r.target[2]);
    const bool afterKnown = actions::player_position(r.after);
    r.residual = afterKnown ? p4rules::distance(r.target, r.after) : -1;
    r.ok = ran && afterKnown && r.residual < 1.5f;
    if (ran) {
        g_landing.arm(now_ms(), c.generation);
        g_ground = false; g_groundAt = 0; g_forceGround = true; g_havePrevious = false;
        const bool confirmed = afterKnown && (r.ok || p4rules::distance(r.before, r.after) > 0.01f);
        if (confirmed) {
            g_lastTpAt = now_ms(); g_lastTpGen = c.generation; g_lastSource = c.source;
            std::memcpy(g_lastTp, r.target, 12); bump(0); bump(source_field(c.source));
            if (c.source == Source::Respawn && r.ok) { maximum(5, p4rules::distance(r.after, partner)); maximum(6, drops.source); }
        }
        else logf("T3-TP call_returned_without_confirmed_movement source=%s; not counted as actual teleport", p4rules::source_name(c.source));
    } else if (!previousLease) actions::suppress_fall_damage(false);
    const uint64_t executed = now_ms();
    const uint64_t age = executed >= c.deadline - p4rules::kFreshMs ? executed - (c.deadline - p4rules::kFreshMs) : 0;
    logf("T3-TP source=%s generation=%llu write=%d ok=%d dist_m=%.3f partner_dist_m=%.3f source_drop_m=%.3f placement_drop_m=%.3f source_dz=%.3f placement_dz=%.3f age_upper_ms=%llu original_target=(%.3f,%.3f,%.3f) target=(%.3f,%.3f,%.3f) residual_3d=%.3f landing=pending_proxy target_combat=peer_snapshot_contract nearby_hostility=unverified",
        p4rules::source_name(c.source), static_cast<unsigned long long>(c.generation), ran ? 1 : 0, r.ok ? 1 : 0,
        p4rules::distance(r.before, r.target), p4rules::distance(partner, r.target), drops.source, drops.placement, drops.sourceDz, drops.placementDz,
        static_cast<unsigned long long>(age), originalTarget[0], originalTarget[1], originalTarget[2], r.target[0], r.target[1], r.target[2], r.residual);
    return r;
}
void note_damage(void* soul, float hp, const char* source, uint8_t peer) {
    if (hp <= 0 || canonical(soul) != canonical(rttr::read_player_soul())) return;
    g_hitAt = now_ms(); g_lastHitHp = hp; g_hitSource = source; g_lastPeer = peer;
}
void note_downed(void* soul, const float death[3], int kind, int gameOverId) {
    const uint64_t now = now_ms();
    bool bleeding = false; const bool bk = soul && rttr::soul_bool(soul, "IsBleeding", &bleeding);
    float hp = -1; if (soul) rttr::soul_state(soul, "health", &hp);
    const bool hit = g_hitAt && now >= g_hitAt && now - g_hitAt <= 3000;
    const bool fall = !hit && !(bk && bleeding) && g_airAt && now >= g_airAt && now - g_airAt <= 2000 && g_airHigh - death[2] > 2.0f;
    const char* cause = hit ? "hit" : fall ? "fall" : bk && bleeding ? "bleed" : "other";
    if (kind != 1 && kind >= 0) bump(hit ? 10 : fall ? 11 : bk && bleeding ? 12 : 13);
    logf("T3-DEATHCAUSE cause=%s evidence=%s cause_inferred=1 kind=%d gameover=%d health=%.3f bleeding_known=%d bleeding=%d last_mod_hit=%s hit_age_ms=%llu hit_requested_hp=%.3f peer=%u death=(%.3f,%.3f,%.3f)",
        cause, hit ? "recent_successful_mod_damage_call" : fall ? "physical_fall_inference" : bk && bleeding ? "bleeding_inference" : "unknown", kind, gameOverId, hp, bk ? 1 : 0, bleeding ? 1 : 0,
        g_hitSource, static_cast<unsigned long long>(g_hitAt && now >= g_hitAt ? now - g_hitAt : UINT64_MAX), g_lastHitHp, g_lastPeer, death[0], death[1], death[2]);
    if (kind != 1 && kind >= 0 && g_lastTpAt && g_lastTpGen == generation() && now >= g_lastTpAt && now - g_lastTpAt <= 10000) {
        bump(14); logf("T3-FALLDEATH-AFTER-TP cause=%s fall=inference_not_proven source=%s after_ms=%llu target=(%.3f,%.3f,%.3f)", cause, p4rules::source_name(g_lastSource), static_cast<unsigned long long>(now - g_lastTpAt), g_lastTp[0], g_lastTp[1], g_lastTp[2]);
    }
}
void tick() {
    const uint64_t now = now_ms(), reset = g_reset.load();
    if (reset != g_tickReset) {
        g_tickReset = reset; if (g_lastTpGen != generation()) g_lastTpAt = 0;
        g_hitAt = 0; g_havePrevious = false; g_airAt = 0; g_forceGround = true;
    }
    if (!gameplay()) {
        if (g_landing.held) {
            logf("T3-LANDING release=local_load lease_end_handled=%d", actions::suppress_fall_damage(false) ? 1 : 0);
            g_landing.held = false;
        }
        g_havePrevious = false; g_observedSoul = nullptr; return;
    }
    if (!g_landing.held && now - g_monitorAt < 100) return;
    g_monitorAt = now;
    void* soul = canonical(rttr::read_player_soul()); const uint64_t wuid = own_wuid();
    if (g_observedSoul && (soul != g_observedSoul || wuid != g_observedWuid)) { g_until.store(0); note_life_edge("player-identity"); g_hitAt = 0; }
    g_observedSoul = soul; g_observedWuid = wuid;
    float pos[3]{}; const bool posKnown = actions::player_position(pos);
    bool dead = false; const bool deadKnown = soul && rttr::soul_bool(soul, "IsDead", &dead);
    if (deadKnown && dead && !g_deadSeen && posKnown) { note_downed(soul, pos, 0, -1); note_life_edge("engine-dead"); }
    if (deadKnown) g_deadSeen = dead;
    int flying = 1;
    const bool physicsKnown = npcdrive::living_flying(engine::entity_by_id(0x7777), &flying);
    if (physicsKnown && flying == 1 && posKnown) { if (!g_airAt || now - g_airAt > 2000) g_airHigh = pos[2]; else if (pos[2] > g_airHigh) g_airHigh = pos[2]; g_airAt = now; }
    const bool verticalKnown = posKnown && g_havePrevious && now > g_prevAt && now - g_prevAt <= 500;
    bool falling = false;
    if (verticalKnown) {
        falling = g_prev[2] - pos[2] > 0.03f;
        const float xy = std::hypot(pos[0] - g_prev[0], pos[1] - g_prev[1]) * 1000.0f / static_cast<float>(now - g_prevAt);
        if (xy > 50 && now - g_fastAt >= 1000) { g_fastAt = now; logf("T3-FAST-MOTION xy_m_s=%.3f dz=%.3f recent_tp_source=%s tp_age_ms=%llu cause=unverified", xy, pos[2] - g_prev[2], p4rules::source_name(g_lastSource), static_cast<unsigned long long>(g_lastTpAt ? now - g_lastTpAt : UINT64_MAX)); }
    }
    if (posKnown) { std::memcpy(g_prev, pos, 12); g_prevAt = now; } g_havePrevious = posKnown;
    if (!g_landing.held) return;
    if (g_forceGround || now - g_groundAt >= 250) {
        g_forceGround = false; g_groundAt = now; float ground[3]{};
        g_ground = posKnown && hangover::snap_to_ground(pos, ground) && std::fabs(pos[2] - ground[2]) <= 0.5f;
    }
    const bool standing = verticalKnown && physicsKnown && flying == 0 && !falling && g_ground;
    if (g_landing.release(now, generation(), standing) || (reset != 0 && !gameplay())) {
        const bool timedOut = now >= g_landing.began && now - g_landing.began >= p4rules::kFallMaxMs;
        logf("T3-LANDING release=%s elapsed_ms=%llu physics_known=%d flying=%d ground_proxy=%d lease_end_handled=%d", timedOut ? "hard_timeout" : generation() != g_landing.generation ? "world_reset" : "grounded_proxy_1s", static_cast<unsigned long long>(now - g_landing.began), physicsKnown ? 1 : 0, flying, g_ground ? 1 : 0, actions::suppress_fall_damage(false) ? 1 : 0);
        g_landing.held = false;
    }
}
void refused_stale(Source source) { bump(8); logf("T3-TP refused=missing_fresh_snapshot source=%s", p4rules::source_name(source)); }
size_t stats(uint8_t* out, size_t cap) { return p4rules::stats(out, cap, g_values); }
uint8_t snapshot(const uint8_t* body, size_t len, uint8_t* out, size_t cap, size_t* used) {
    *used = 0;
    if (!body || len != 9 || body[0] != 251 || cap < 32) return wo163::kRBadRequest;
    const uint64_t gen = p4rules::u64(body + 1), life = g_lifeEdge.load();
    if (!gen || gen != generation() || !gameplay()) return wo163::kRFailed;
    const uint64_t capturedAt = now_ms(); // before any reflected read; never freshen an old position at the end
    float p[3]{}; bool combat = false, dead = false, ko = false; uint8_t known = 0;
    if (actions::player_position(p) && p4rules::finite3(p)) known |= 1;
    if (motion::combat_mode_of(0x7777, &combat)) known |= 2;
    void* soul = rttr::read_player_soul();
    if (soul && rttr::soul_bool(soul, "IsDead", &dead)) known |= 4;
    npcdrive::PhysicsStatus ps{};
    const bool pk = npcdrive::physics_status(engine::entity_by_id(0x7777), &ps) && ps.present;
    if (soul && rttr::soul_bool(soul, "IsUnconscious", &ko) && pk) {
        ko = ko || !ps.living || motion::local_down_now(); known |= 8;
    }
    if (known != 15 || gen != generation() || life != g_lifeEdge.load() || !gameplay() || now_ms() < capturedAt || now_ms() - capturedAt > p4rules::kFreshMs) return wo163::kRFailed;
    out[0] = 1; out[1] = known; out[2] = uint8_t((combat ? 1 : 0) | (dead ? 2 : 0) | (ko ? 4 : 0)); out[3] = 0;
    std::memcpy(out + 4, p, 12); p4rules::put64(out + 16, gen); p4rules::put64(out + 24, capturedAt); *used = 32;
    return wo163::kROk;
}
} // namespace kcdmp::p4
