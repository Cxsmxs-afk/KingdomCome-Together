// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
// WO-131 -- see wo131.h.
#include "wo131.h"
#include "p6_missile.h"
#include "fault_guard.h"

#include <windows.h>
#include <atomic>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <unordered_map>

#include "buffs.h"
#include "engine.h"
#include "holders.h"
#include "log.h"
#include "npc_drive.h"
#include "p4.h"
#include "pipe_server.h"
#include "respawn_actions.h"
#include "rp_body_action_rules.h"
#include "rp_timeline_actions.h"
#include "rttr_abi.h"
#include "script_context.h"
#include "motion.h"
#include "wo187.h"
#include "wo210_kd.h"
#include "wo216.h"

namespace kcdmp::wo131 {
namespace {

constexpr size_t kActorGetSoul = 0x6E0;   // hits.cpp / motion.cpp
constexpr const char* kGuardGuidText = "4b43444d-7121-4d67-b1a5-9e2f6d8c0a15";   // kcdmp_avatar_guard (buff__kcdmp.xml)
// WO-135: a knocked-out copy. kcdmp_knockout_guard is buff__kcdmp.xml's imm-only
// guard (WO-113); the other three are the game's own (Tables :: rpg/buff.xml).
constexpr const char* kKoGuardGuidText  = "4b43444d-7113-4d67-b1a5-9e2f6d8c0a14";   // kcdmp_knockout_guard
constexpr const char* kUnconsciousText  = "f8d60fe4-e2c1-420a-946a-213e1cd09265";   // unconscious_nonpersistend (Cpp:Unconscious)
constexpr const char* kInfiniteUncText  = "74cf0c29-d03e-4233-9352-b91ca5ea69ea";   // infinite_unconsciousness_nonpersistent (ufo*0)
constexpr const char* kRemoveUncText    = "bd22f98a-e61f-4d83-b39c-79d1d85b6b91";   // remove_unconsciousness
constexpr char kKoPoseSetter[] = "wo131.unconscious_pose";
constexpr char kHolsterContext[] = "UnconsciousHolsterInsteadDropWeapons";   // shipped rule, WO-68 control read
constexpr double kWakeSettleS = 1.2;   // WO-197/201: let the full-body get-up blend finish before writing the body again

struct RemoteKo {
    void* entity = nullptr;
    void* soul = nullptr;
    timelineactions::Scope scope{};
    uint64_t token = 0;
    double deadline = 0, stableSince = 0;
    bool down = true, standRequested = false, ownsHolster = false;
};
std::unordered_map<uint32_t, RemoteKo> g_remoteKo;

template <class T> bool rd(const void* base, size_t off, T* out) {
    KCDMP_FAULT_READ(site, "wo131::rd");
    return fault::guarded(site, [&] { *out = *reinterpret_cast<const T*>(static_cast<const char*>(base) + off); });
}
void* vslot(void* obj, size_t off) {
    void* vt = nullptr; void* fn = nullptr;
    if (!obj || !rd(obj, 0, &vt) || !vt || !rd(vt, off, &fn)) return nullptr;
    return fn;
}
bool call_p0(void* fn, void* self, void** out) {
    KCDMP_FAULT_CALL(site, "wo131::call_p0");
    return fault::guarded(site, [&] { *out = reinterpret_cast<void* (__fastcall*)(void*)>(fn)(self); });
}
bool call_p1u(void* fn, void* self, uint32_t a, void** out) {
    KCDMP_FAULT_CALL(site, "wo131::call_p1u");
    return fault::guarded(site, [&] { *out = reinterpret_cast<void* (__fastcall*)(void*, uint32_t)>(fn)(self, a); });
}
void* actor_by_eid(uint32_t eid) {
    void* gi = engine::game_iface();
    void* am = nullptr;
    if (!gi || !rd(gi, 0x188, &am) || !am) return nullptr;
    void* fn = vslot(am, 0x18);
    void* actor = nullptr;
    if (!fn || !call_p1u(fn, am, eid, &actor)) return nullptr;
    return actor;
}
void* soul_of_eid(uint32_t eid) {
    void* actor = actor_by_eid(eid);
    void* fn = actor ? vslot(actor, kActorGetSoul) : nullptr;
    void* soul = nullptr;
    return fn && call_p0(fn, actor, &soul) ? soul : nullptr;
}

unsigned char g_guard[16]{};
bool g_guardParsed = false;
const unsigned char* guard_guid() {
    if (!g_guardParsed) g_guardParsed = buffs::parse_guid(kGuardGuidText, g_guard);
    return g_guardParsed ? g_guard : nullptr;
}

struct KoGuids { unsigned char ko[16], unc[16], inf[16], rem[16]; bool ok = false, tried = false; } g_ko;
const KoGuids* ko_guids() {
    if (!g_ko.tried) {
        g_ko.tried = true;
        g_ko.ok = buffs::parse_guid(kKoGuardGuidText, g_ko.ko) && buffs::parse_guid(kUnconsciousText, g_ko.unc)
               && buffs::parse_guid(kInfiniteUncText, g_ko.inf) && buffs::parse_guid(kRemoveUncText, g_ko.rem);
    }
    return g_ko.ok ? &g_ko : nullptr;
}

std::atomic<uint32_t> c_checks{0}, c_guardOn{0}, c_guardOff{0}, c_follow{0}, c_followCredit{0}, c_followStamina{0},
                      c_stopFight{0}, c_restore{0}, c_faction{0}, c_fail{0}, c_knockouts{0}, c_wakes{0};

void* current_soul(uint32_t eid) {
    void* soul = soul_of_eid(eid);
    return soul && buffs::as_c_soul(soul) ? buffs::as_c_soul(soul) : soul;
}

timelineactions::Scope scope_now() {
    return {wo216::lifecycle_observation_stamp(), pipe::observation_connection(), p4::life_edge_tick()};
}

bool active_ko(const RemoteKo& ko, uint32_t eid, void* soul) {
    return ko.soul == soul && ko.scope == scope_now() && ko.entity == engine::entity_by_id(eid);
}

void report_ko(uint32_t eid, RemoteKo& ko, bool confirmed) {
    if (!ko.token) return;
    timelineactions::observed(ko.token, confirmed);
    logf("MP-RP-KO eid=0x%X token=%llu down=%u result=%s", eid, static_cast<unsigned long long>(ko.token),
         ko.down ? 1u : 0u, confirmed ? "confirmed" : "unconfirmed");
    ko.token = 0;
}

bool toggle_holster(uint32_t eid, void* soul, bool on, bool* owned) {
    auto permit = holders::may_place(eid, holders::Holder::Fall, kKoPoseSetter, holders::Claim::Held);
    if (!permit) return false;
    const int result = sctx::set_soul_context_quiet(soul, kHolsterContext, on);
    if (result < 0) return false;
    if (result > 0) permit.wrote();
    if (owned) *owned = result > 0;
    return true;
}

bool set_ko(uint32_t eid, void* cs, bool down, bool& present, bool typed = false) {
    const KoGuids& k = *ko_guids();
    auto permit = holders::may_place(eid, holders::Holder::Fall, kKoPoseSetter, holders::Claim::Held);
    if (!permit) return false;
    if (down) {
        // The imm-only guard precedes the full guard's removal. Its upr=1 blocks unconsciousness.
        if (buffs::has(cs, k.ko) <= 0 && !buffs::add(cs, k.ko)) return false;
        if (!typed) motion::set_ko_hold(eid, true);
        buffs::remove_all(cs, guard_guid());
        if (buffs::has(cs, k.inf) <= 0) buffs::add(cs, k.inf);
        if (buffs::has(cs, k.unc) <= 0) {
            if (!buffs::add(cs, k.unc)) {
                if (typed) {
                    buffs::remove_all(cs, k.inf);
                    if (buffs::has(cs, guard_guid()) > 0 || buffs::add(cs, guard_guid())) buffs::remove_all(cs, k.ko);
                }
                return false;
            }
            permit.wrote();
        }
        if (!typed) wo210kd::note_buff_pose(eid, true);
        c_knockouts.fetch_add(1);
        present = buffs::has(cs, k.unc) > 0;
        logf("WO135-KO copy eid=0x%X knocked out: unconscious=%d imm-only guard=%d", eid, present ? 1 : 0, buffs::has(cs, k.ko));
    } else {
        if (!typed) motion::set_ko_hold(eid, false);
        buffs::remove_all(cs, k.inf);
        const int removed = buffs::remove_all(cs, k.unc);
        if (buffs::add(cs, k.rem) || removed > 0) permit.wrote();
        if (buffs::has(cs, guard_guid()) <= 0) buffs::add(cs, guard_guid());
        buffs::remove_all(cs, k.ko);
        if (!typed) wo210kd::note_buff_pose(eid, false);
        c_wakes.fetch_add(1);
        present = buffs::has(cs, guard_guid()) > 0;
        logf("WO135-KO copy eid=0x%X woken: unconscious=%d guard=%d", eid, buffs::has(cs, k.unc), present ? 1 : 0);
    }
    return true;
}

bool standing_idle(uint32_t eid) {
    wo187::ScopeInfo scopes[12];
    const int n = wo187::read_all_scopes(eid, scopes, 12);
    for (int i = 0; i < n; ++i) {
        const auto& s = scopes[i];
        if (!s.ok || s.empty || std::strcmp(s.name, "FullBody")) continue;
        char fragment[48]{};
        return wo187::fragment_name(eid, s.frag, fragment, sizeof fragment) && std::strncmp(fragment, "MotionIdle", 10) == 0;
    }
    return false;
}

void put_f(uint8_t* p, float v) { std::memcpy(p, &v, 4); }
float get_f(const uint8_t* p) { float v; std::memcpy(&v, p, 4); return v; }
uint32_t get_u32(const uint8_t* p) { uint32_t v; std::memcpy(&v, p, 4); return v; }

} // namespace

bool rp_knockout_available(uint32_t eid, bool down) {
    void* entity = engine::entity_by_id(eid);
    const char* name = entity ? engine::entity_name(entity) : nullptr;
    if (!name || std::strncmp(name, "kcd2mp_", 7) || !npcdrive::is_bound_eid(eid) ||
        !npcdrive::unparented_body(eid) || !buffs::ready() || !guard_guid() || !ko_guids()) return false;
    using holders::Holder;
    const Holder owner = holders::of(eid);
    if (owner != Holder::Writer && owner != Holder::Figure && owner != Holder::Driver &&
        owner != Holder::OneShot && owner != Holder::Fall) return false;
    void* soul = current_soul(eid);
    bool dead = false, unconscious = false, ragdoll = false;
    if (!soul || !rttr::soul_bool(soul, "IsDead", &dead) || dead ||
        !rttr::soul_bool(soul, "IsUnconscious", &unconscious) || !wo210kd::rp_ragdoll_state(eid, &ragdoll)) return false;
    if (!down) return true;   // a queued down event may still be waiting for the host clock
    if (unconscious || ragdoll) return false;
    // Rivals cannot use the ordinary knockout's weapon-drop path on a figure.
    // Use the game's pre-existing player holster context, verified by readback.
    return sctx::has_soul_context(soul, kHolsterContext) >= 0;
}

bool rp_knockout_holds(uint32_t eid) {
    const auto it = g_remoteKo.find(eid);
    return it != g_remoteKo.end() && active_ko(it->second, eid, current_soul(eid));
}

bool rp_knockout_wake_pending(uint32_t eid) {
    const auto it = g_remoteKo.find(eid);
    return it != g_remoteKo.end() && it->second.down && active_ko(it->second, eid, current_soul(eid));
}

bool rp_knockout(uint32_t eid, bool down, uint64_t token) {
    if (!token || !rp_knockout_available(eid, down)) return false;
    const double now = npcdrive::now_s();
    const auto scope = scope_now();
    void* entity = engine::entity_by_id(eid);
    void* soul = current_soul(eid);
    if (!scope.connection || !entity || !soul) return false;
    auto old = g_remoteKo.find(eid);
    if (!down && !rp_knockout_wake_pending(eid)) return false;
    if (down && old != g_remoteKo.end()) return false;
    if (!wo210kd::take_fall(eid, engine::entity_name(entity), now)) return false;
    bool present = false;
    if (down) {
        bool ownsHolster = false;
        if (!toggle_holster(eid, soul, true, &ownsHolster)) return false;
        if (!set_ko(eid, soul, true, present, true)) {
            if (ownsHolster) toggle_holster(eid, soul, false, nullptr);
            return false;
        }
        g_remoteKo.emplace(eid, RemoteKo{entity, soul, scope, token, now + 6.0, 0, true, false, ownsHolster});
    } else {
        if (!set_ko(eid, soul, false, present, true)) return false;
        auto& ko = old->second;
        report_ko(eid, ko, false);   // a wake cannot confirm a down that was never seen
        ko.down = false;
        ko.token = token;
        ko.deadline = now + 25.0;
    }
    return true;
}

void rp_knockout_tick() {
    if (g_remoteKo.empty()) return;
    const auto scope = scope_now();
    const double now = npcdrive::now_s();
    for (auto it = g_remoteKo.begin(); it != g_remoteKo.end();) {
        const uint32_t eid = it->first;
        auto& ko = it->second;
        if (!(ko.scope == scope) || ko.entity != engine::entity_by_id(eid) || ko.soul != current_soul(eid) ||
            !npcdrive::is_bound_eid(eid)) {
            report_ko(eid, ko, false);
            it = g_remoteKo.erase(it);
            continue;
        }
        ++it;
        bool dead = false, unc = false;
        if (now >= ko.deadline) report_ko(eid, ko, false);
        if (!rttr::soul_bool(ko.soul, "IsDead", &dead)) { ko.stableSince = 0; continue; }
        if (dead) {
            report_ko(eid, ko, false);
            if (ko.ownsHolster) toggle_holster(eid, ko.soul, false, nullptr);
            g_remoteKo.erase(eid);
            continue;
        }
        if (!rttr::soul_bool(ko.soul, "IsUnconscious", &unc) || holders::of(eid) != holders::Holder::Fall) {
            ko.stableSince = 0;
            continue;
        }
        npcdrive::PhysicsStatus ps{};
        if (!npcdrive::physics_status(ko.entity, &ps)) { ko.stableSince = 0; continue; }
        if (ko.down) {
            if (rpbodyactions::down_confirmed(unc, ps.present != 0, ps.living != 0)) report_ko(eid, ko, true);
            continue;
        }
        bool rag = false;
        if (unc || !wo210kd::rp_ragdoll_state(eid, &rag)) { ko.stableSince = 0; continue; }
        int flying = 1;
        const bool grounded = ps.living && npcdrive::living_flying(ko.entity, &flying) && !flying;
        if (!rpbodyactions::up_confirmed(unc, ps.present != 0, ps.living != 0, grounded, rag)) {
            ko.stableSince = 0;
            if (!ko.standRequested && ps.present && (rag || !ps.living)) ko.standRequested = wo210kd::rp_stand_up(eid, kKoPoseSetter);
            continue;
        }
        if (!standing_idle(eid)) { ko.stableSince = 0; continue; }
        report_ko(eid, ko, true);
        if (!ko.stableSince) ko.stableSince = now;
        if (now - ko.stableSince < kWakeSettleS) continue;
        if (ko.ownsHolster && !toggle_holster(eid, ko.soul, false, nullptr)) continue;
        g_remoteKo.erase(eid);
    }
}

uint8_t handle(const uint8_t* body, size_t len, uint8_t* out, size_t cap, size_t* outLen) {
    *outLen = 0;
    if (len < 1 || cap < 32) return kRBadRequest;
    const uint8_t op = body[0];
    const uint8_t* a = body + 1;
    const size_t n = len - 1;
    switch (op) {
    case kOpHitCheck: {
        if (n < 17 || n != static_cast<size_t>(17 + a[16]) || a[16] > 63) return kRBadRequest;
        char name[64]{};
        std::memcpy(name, a + 17, a[16]);
        npcdrive::HitCheck hc{};
        npcdrive::hit_check(name, &hc);
        // Everything by the bound body's entity id: rttr::find_soul_by_guid is a
        // full reflection walk of the soul list (seconds on a live world --
        // the first live run timed the agent out), the actor's soul is two calls.
        float hp = -1;
        int guarded = -1;
        if (hc.eid) {
            void* s0 = soul_of_eid(hc.eid);
            void* cs = s0 && buffs::as_c_soul(s0) ? buffs::as_c_soul(s0) : s0;
            if (cs) {
                rttr::soul_state(cs, "health", &hp);
                if (guard_guid() && buffs::ready()) guarded = buffs::has(cs, guard_guid());
                // WO-135: a knocked-out copy carries the imm-only guard instead.
                if (guarded == 0 && ko_guids() && buffs::ready()) guarded = buffs::has(cs, ko_guids()->ko);
            }
        }
        c_checks.fetch_add(1);
        const double ageMs = hc.ageS < 0 ? 65535.0 : hc.ageS * 1000.0;
        const uint16_t age = static_cast<uint16_t>(ageMs > 65535.0 ? 65535.0 : ageMs);
        out[0] = hc.bound ? 1 : 0;
        std::memcpy(out + 1, &age, 2);
        put_f(out + 3, hc.distM);
        out[7] = hc.flags;
        put_f(out + 8, hp);
        out[12] = static_cast<uint8_t>(guarded < 0 ? 0xFF : guarded);
        *outLen = 13;
        return kROk;
    }
    case kOpCopyGuard: {
        if (n != 5) return kRBadRequest;
        const uint8_t mode = a[0];
        const uint32_t eid = get_u32(a + 1);
        if (mode > kGuardWake) return kRBadRequest;
        if (!guard_guid() || !buffs::ready()) { c_fail.fetch_add(1); return kRUnarmed; }
        const KoGuids* k = ko_guids();
        if (mode >= kGuardKnockout && !k) { c_fail.fetch_add(1); return kRUnarmed; }
        void* soul = soul_of_eid(eid);
        if (!soul) { c_fail.fetch_add(1); return kRNoActor; }
        void* cs = buffs::as_c_soul(soul) ? buffs::as_c_soul(soul) : soul;
        auto typed = g_remoteKo.find(eid);
        const bool tracked = typed != g_remoteKo.end() && active_ko(typed->second, eid, cs);
        bool present = false;
        switch (mode) {
        case kGuardOn:
            if (tracked && typed->second.down) { present = k && buffs::has(cs, k->ko) > 0; break; }
            if (k) buffs::remove_all(cs, k->ko);
            if (buffs::has(cs, guard_guid()) <= 0 && !buffs::add(cs, guard_guid())) { c_fail.fetch_add(1); return kRFailed; }
            c_guardOn.fetch_add(1);
            present = buffs::has(cs, guard_guid()) > 0;
            break;
        case kGuardOff: {
            if (tracked) {
                report_ko(eid, typed->second, false);
                if (typed->second.ownsHolster) toggle_holster(eid, cs, false, nullptr);
                g_remoteKo.erase(typed);
            }
            motion::set_ko_hold(eid, false);
            wo210kd::clear_buff_pose(eid);
            buffs::remove_all(cs, guard_guid());
            if (k) buffs::remove_all(cs, k->ko);
            c_guardOff.fetch_add(1);
            const int guard = buffs::has(cs, guard_guid());
            const int knockout = k ? buffs::has(cs, k->ko) : 0;
            if (guard < 0 || knockout < 0) { c_fail.fetch_add(1); return kRFailed; }
            present = guard == 0 && knockout == 0;
            break;
        }
        case kGuardKnockout:
        case kGuardWake: {
            if (tracked) return kRFailed;  // a typed KO owns the guard swap through physical get-up
            if (!wo210kd::take_fall(eid, engine::entity_name(engine::entity_by_id(eid)), npcdrive::now_s()))
                return kRFailed;
            if (!set_ko(eid, cs, mode == kGuardKnockout, present)) { c_fail.fetch_add(1); return kRFailed; }
            break;
        }
        }
        out[0] = static_cast<uint8_t>(present ? 1 : 0);
        *outLen = 1;
        return kROk;
    }
    case kOpFollowHp: {
        // [guid:16][eid:4][hp:4f]: the soul by entity id (cheap); the GUID only
        // keys the sampler's credit. WO-200 II.1: [stamina:4f] after it -- the host's own stamina of its NPC (n == 28).
        if (n != 24 && n != 28) return kRBadRequest;
        const uint32_t eid = get_u32(a + 16);
        const float want = get_f(a + 20);
        const float wantSt = n == 28 ? get_f(a + 24) : -1.0f;
        if (!std::isfinite(want) || want < 0) return kRBadRequest;
        void* s0 = soul_of_eid(eid);
        void* soul = s0 && buffs::as_c_soul(s0) ? buffs::as_c_soul(s0) : s0;
        if (!soul) { c_fail.fetch_add(1); return kRNoSoul; }
        float before = -1, after = -1;
        if (!rttr::soul_state(soul, "health", &before)) { c_fail.fetch_add(1); return kRFailed; }
        // Never below 1: the host's death comes as its own order (op 2 off, then
        // ApplyDeath); a follow is never the thing that kills a copy.
        const float target = want < 1.0f ? 1.0f : want;
        if (std::isfinite(wantSt) && wantSt >= 0.0f) {   // WO-200 II.1: the host's stamina of its NPC (never set from anything local)
            float st = -1;
            if (rttr::soul_state(soul, "stamina", &st) && std::fabs(st - wantSt) >= 1.0f) {
                p6::remote_follow_eid(eid);
                if (rttr::soul_set_state(soul, "stamina", wantSt)) c_followStamina.fetch_add(1);
            }
        }
        if (std::fabs(target - before) < 0.5f) { put_f(out, before); put_f(out + 4, before); *outLen = 8; return kROk; }
        p6::remote_follow_eid(eid); // includes a host follow that raises health
        if (target < before) { rttr::note_remote_damage(a, before - target); c_followCredit.fetch_add(1); }
        if (!rttr::soul_set_state(soul, "health", target)) { c_fail.fetch_add(1); return kRFailed; }
        rttr::soul_state(soul, "health", &after);
        c_follow.fetch_add(1);
        put_f(out, before); put_f(out + 4, after);
        *outLen = 8;
        return kROk;
    }
    case kOpStopFight: {
        if (n != 4) return kRBadRequest;
        const uint32_t eid = get_u32(a);
        if (!actions::stop_fight_available()) { c_fail.fetch_add(1); return kRUnarmed; }
        void* soul = eid == 0 ? rttr::read_player_soul() : soul_of_eid(eid);
        if (!soul) { c_fail.fetch_add(1); return kRNoActor; }
        void* cs = buffs::as_c_soul(soul) ? buffs::as_c_soul(soul) : soul;
        const bool ok = actions::stop_fight(cs);
        logf("WO131-STOPFIGHT eid=0x%X (%s) -> %s", eid, eid == 0 ? "the local player" : "a body", ok ? "called" : "FAILED");
        if (!ok) { c_fail.fetch_add(1); return kRFailed; }
        c_stopFight.fetch_add(1);
        return kROk;
    }
    case kOpRestoreHp: {
        if (n != 8) return kRBadRequest;
        const uint32_t eid = get_u32(a);
        const float want = get_f(a + 4);
        if (!std::isfinite(want) || want <= 0) return kRBadRequest;
        void* soul = soul_of_eid(eid);
        if (!soul) { c_fail.fetch_add(1); return kRNoActor; }
        void* cs = buffs::as_c_soul(soul) ? buffs::as_c_soul(soul) : soul;
        float before = -1, after = -1;
        rttr::soul_state(cs, "health", &before);
        p6::remote_follow_eid(eid);
        if (!rttr::soul_set_state(cs, "health", want)) { c_fail.fetch_add(1); return kRFailed; }
        rttr::soul_state(cs, "health", &after);
        c_restore.fetch_add(1);
        put_f(out, before); put_f(out + 4, after);
        *outLen = 8;
        return kROk;
    }
    case kOpFaction: {
        if (n != 17) return kRBadRequest;
        const uint8_t mode = a[16];
        bool ok = false;
        if (mode == 2) ok = rttr::set_ghost_faction_player(a);
        else ok = rttr::set_ghost_faction_hostile(a, mode == 1);
        if (!ok) { c_fail.fetch_add(1); return kRFailed; }
        c_faction.fetch_add(1);
        return kROk;
    }
    case kOpPlayerCrouch: {
        if (n != 1) return kRBadRequest;
        const bool ok = motion::player_set_crouch(a[0] != 0);
        logf("WO135-CROUCH check: the player's own SetCrouch(%d) -> %s (no input)", a[0] != 0 ? 1 : 0, ok ? "called" : "FAILED");
        return ok ? kROk : kRFailed;
    }
    case kOpStatus: {
        char t[256];
        int m = std::snprintf(t, sizeof(t),
            "wo131 checks=%u guard_on=%u guard_off=%u follow=%u follow_credit=%u stopfight=%u restore=%u faction=%u fail=%u buffs=%s stopfight_armed=%s knockouts=%u wakes=%u",
            c_checks.load(), c_guardOn.load(), c_guardOff.load(), c_follow.load(), c_followCredit.load(), c_stopFight.load(),
            c_restore.load(), c_faction.load(), c_fail.load(), buffs::ready() ? "ready" : "no", actions::stop_fight_available() ? "yes" : "no",
            c_knockouts.load(), c_wakes.load());
        if (m < 0) m = 0;
        if (static_cast<size_t>(m) > cap) m = static_cast<int>(cap);
        std::memcpy(out, t, m);
        *outLen = static_cast<size_t>(m);
        return kROk;
    }
    default:
        return kRBadRequest;
    }
}

} // namespace kcdmp::wo131
