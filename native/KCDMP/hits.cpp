// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
// WO-121 Phases 5 and 6 -- see hits.h.
#include "hits.h"
#include "p6_missile.h"
#include "wo208.h"
#include "wo208_rules.h"
#include "wo166.h"
#include "wo170_rules.h"
#include "wo174_rules.h"
#include "wo176.h"
#include "visual_damage_rules.h"
#include "wo210_hit.h"
#include "fault_guard.h"

#include <windows.h>
#include <array>
#include <atomic>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#include "anchors.h"
#include "buffs.h"
#include "engine.h"
#include "load_sequence.h"
#include "log.h"
#include "motion.h"
#include "npc_drive.h"
#include "respawn.h"
#include "p4.h"
#include "pipe_server.h"
#include "respawn_actions.h"
#include "rttr_abi.h"
#include "npc_death_release.h"
#include "wo136.h"
#include "wo139.h"

namespace kcdmp::hits {
namespace {

constexpr size_t kSlotMelee   = 0x150;
constexpr size_t kSlotMissile = 0x158;
constexpr size_t kSmAddSoul   = 0x10;    // I_SkirmishManager::AddSoulToSkirmish(soul, reference, override)
constexpr size_t kSmRemoveSoul = 0x18;   // I_SkirmishManager::RemoveSoulFromSkirmish(soul) (WO-132)
// WO-163 (A7): the relation object the manager carries at +0x80 (RTTI C_SkirmishSituation) and its relation call at the vtable's byte
// offset +0x08 -- relation(this, soulA, soulB, bool true) -> bool (WO-162 Q2.2/Q2.3 [disasm]: FindSkirmishOf(A), B a member of that same
// skirmish, active, not flagged, then the hostile test: an explicit pair or a negative faction value). Read-only.
constexpr size_t kSmSituation = 0x80;
constexpr size_t kSitRelation = 0x08;
constexpr size_t kActorGetSoul = 0x6E0;
constexpr size_t kSoulCombat  = 0x108;   // C_CombatSoul from the actor's soul (WO-119 s2.1, observed)
constexpr double kEngagementS = 30.0;    // one skirmish add per (victim, avatar) per this
constexpr size_t kVisualFigures = 16;

template <class T> bool rd(const void* base, size_t off, T* out) {
    KCDMP_FAULT_READ(site, "hits::rd");
    return fault::guarded(site, [&] { *out = *reinterpret_cast<const T*>(static_cast<const char*>(base) + off); });
}
void* vslot(void* obj, size_t off) {
    void* vt = nullptr; void* fn = nullptr;
    if (!obj || !rd(obj, 0, &vt) || !vt || !rd(vt, off, &fn)) return nullptr;
    return fn;
}
bool call_p0(void* fn, void* self, void** out) {
    KCDMP_FAULT_CALL(site, "hits::call_p0");
    return fault::guarded(site, [&] { *out = reinterpret_cast<void* (__fastcall*)(void*)>(fn)(self); });
}
bool call_p1u(void* fn, void* self, uint32_t a, void** out) {
    KCDMP_FAULT_CALL(site, "hits::call_p1u");
    return fault::guarded(site, [&] { *out = reinterpret_cast<void* (__fastcall*)(void*, uint32_t)>(fn)(self, a); });
}
bool call_history(void* fn, void* victimCs, void* data) {
    KCDMP_FAULT_CALL(site, "hits::call_history");
    return fault::guarded(site, [&] { reinterpret_cast<void (__fastcall*)(void*, void*)>(fn)(victimCs, data); });
}
bool call_getter(void* fn, void** out) {
    KCDMP_FAULT_CALL(site, "hits::call_getter");
    return fault::guarded(site, [&] { *out = reinterpret_cast<void* (__fastcall*)(void*)>(fn)(nullptr); });
}
bool call_add_soul(void* fn, void* mgr, void* soul, void* ref, uint8_t ovr, uint64_t* out) {
    KCDMP_FAULT_CALL(site, "hits::call_add_soul");
    return fault::guarded(site, [&] { *out = reinterpret_cast<uint64_t (__fastcall*)(void*, void*, void*, uint8_t)>(fn)(mgr, soul, ref, ovr); });
}
// WO-132: I_SkirmishManager vtable +0x18 RemoveSoulFromSkirmish(mgr, soul) (RPGModule
// 0x645970, its __FUNCTION__ string names it) -> C_Skirmish::RemoveSoul: one soul
// leaves its skirmish, the fight itself goes on.
bool call_remove_soul(void* fn, void* mgr, void* soul, uint64_t* out) {
    KCDMP_FAULT_CALL(site, "hits::call_remove_soul");
    return fault::guarded(site, [&] { *out = reinterpret_cast<uint64_t (__fastcall*)(void*, void*)>(fn)(mgr, soul); });
}
// WO-163 (A7): the skirmish relation test, guarded; true when the call completed (its answer in *out).
bool call_relation(void* fn, void* sit, void* a, void* b, bool* out) {
    KCDMP_FAULT_CALL(site, "hits::call_relation");
    return fault::guarded(site, [&] { *out = reinterpret_cast<bool (__fastcall*)(void*, void*, void*, bool)>(fn)(sit, a, b, true); });
}
bool is_a(void* obj, void* const* vft) { void* vp = nullptr; return obj && vft && rd(obj, 0, &vp) && vp == static_cast<const void*>(vft); }
bool bytes_eq(const void* p, const uint8_t* pat, size_t n) {
    KCDMP_FAULT_READ(site, "hits::bytes_eq");
    return fault::guarded_or<bool>(site, false, [&]() -> bool { return std::memcmp(p, pat, n) == 0; });
}

// ---- anchors ---------------------------------------------------------------------
void* const* g_vftCombatSoul = nullptr;
void* const* g_vftSkirmish = nullptr;
void* const* g_vftSituation = nullptr;   // WO-163: RTTI C_SkirmishSituation
void* g_fnRelation = nullptr;            // WO-163: the relation call, set only when its prologue is the verified one
void* g_fnHistory = nullptr, *g_fnSkirmishGetter = nullptr;
std::atomic<bool> g_hookArmed{false};
bool g_attribArmed = false;
std::string g_whyHook = "not installed", g_whyAttrib = "not installed";

using HitFn = void* (__fastcall*)(void* self, void* out, const uint8_t* data);
HitFn g_origMelee = nullptr, g_origMissile = nullptr;

// ---- config ----------------------------------------------------------------------
std::atomic<bool> g_ff{true}, g_attribution{true}, g_pvpHook{true};

// ---- avatars (main thread writes, any thread reads) --------------------------------
struct AvatarSlot { std::atomic<uint32_t> eid{0}; std::atomic<void*> soul{nullptr}; };
AvatarSlot g_avatars[16];

void* avatar_soul(uint32_t eid) {
    if (!eid) return nullptr;
    for (auto& a : g_avatars) if (a.eid.load(std::memory_order_relaxed) == eid) return a.soul.load(std::memory_order_relaxed);
    return nullptr;
}

// Keep a figure's damage fence past a transform unbind. A mount/respawn or
// other presentation handover can release the writer while its action still
// exists. This table retires the exact physical actor when its entity is gone.
struct VisualBody {
    visualdamage::Identity id;
    void* entity = nullptr;
    void* combatSoul = nullptr;
    bool bypassedMeleeGate = false;
};
std::mutex g_visualMu;
std::array<VisualBody, kVisualFigures> g_visualBodies{};
std::atomic<unsigned> g_visualCount{0};
std::atomic<uint64_t> c_visualBlocked{0}, c_visualEscape{0};

// ---- the hook's queue (any thread -> main thread) -----------------------------------
wo155rules::Recipient pvp_scope() {
    return {p4::generation(), p4::life_edge_tick(), pipe::observation_connection(), p4::gameplay(), p4::pvp_life_proof()};
}
bool current_pvp_capture(const wo155rules::WatchCapture& capture) {
    return capture.tick && !capture.ambiguous && capture.source.proof &&
        capture.source.matches(pvp_scope(), capture.tick) &&
        loadsequence::allowed(capture.load, loadsequence::stamp(), loadsequence::Access::Live);
}
struct PvpHit {
    float st, hp;
    wo155rules::WatchCapture capture;
    uint64_t measuredTick, measurementId;
};
uint64_t g_pvpMeasurementId = 0;  // main thread only, one ID per published measurement
struct PlayerHitMark { uint32_t victimEid; double at; bool invalidate=false; };
std::mutex g_qMutex;
std::vector<PlayerHitMark> g_marks;
// A player's hit on an avatar, watched until its queued damage has landed.
// WO-132: also an NPC's hit on an avatar (kind kWatchNpc: measured and put back
// here, forwarded by the agent as the avatar owner's damage -- a real hit, never
// a bleed tick), and an engaged copy's hit on the local player on a joiner (kind
// kWatchDiscard: put back, never forwarded -- the joiner's damage comes only
// from the host).
// WO-165: kWatchReplay -- a replayed blow (wo165.cpp) on the local player or a dummy NPC: measured, never put back, reported to
// the replay's own callback (the engine applied it; this only says how much).
// WO-170: kWatchFigure -- a blow of a partner's figure that no relayed swing of the partner's matches: measured and put back (health and
// stamina; nothing is forwarded), the victim any soul (an NPC, the local player, another figure).
// WO-208 B: kWatchDefence -- an engaged copy's blow on the local player with mp_defence_local: this machine's engine judged it (the
// verdict read from the record it built, +0x54..+0x57) and its damage is KEPT; measured from the lowest point (as a replay), reported to
// the agent (DefenceFn), which tells the host -- the host holds its own judgement of that blow for it (WO208-DEF).
enum WatchKind : uint8_t { kWatchPvp = 0, kWatchNpc = 1, kWatchDiscard = 2, kWatchReplay = 3, kWatchFigure = 4, kWatchDefence = 5 };
struct NewWatch { void* soul; uint32_t eid; float hp0, st0; uint8_t flags, material; uint8_t kind; uint32_t attackerEid; uint8_t outcome = 0;
                  wo155rules::WatchCapture pvp{}; };
std::vector<NewWatch> g_newWatches;
struct Watch { void* soul; uint32_t eid; float hp0, st0, dh = 0, ds = 0; uint8_t flags, material; double t0; int frames = 0, landedAt = -1;
               uint8_t kind = 0; uint32_t attackerEid = 0; bool fatal = false; uint8_t outcome = 0;
               wo155rules::WatchSeries pvp{}; uint64_t pvpFirstEffectTick = 0; };
std::vector<Watch> g_watches;   // main thread only
constexpr double kWatchS = 0.6;
std::atomic<uint64_t> g_playerWuid{0};
std::atomic<uint32_t> g_playerEid{0};
std::atomic<PvpFn> g_pvpFn{nullptr};
std::atomic<NpcHitFn> g_npcHitFn{nullptr};
std::atomic<DiscardFn> g_discardFn{nullptr};
std::atomic<DefenceFn> g_defenceFn{nullptr};
// WO-208 B: the last landed blow of each engaged copy on the local player (attacker eid -> QPC seconds), main thread
std::unordered_map<uint32_t, double> g_defLanded;
std::atomic<uint32_t> c_defJudged{0}, c_defOutcome[7]{};
// WO-132: attacker entity ids whose hits on the local player are discarded
// (the joiner's engaged copies of host NPCs). Main thread writes.
// WO-153 2: 256, not 64. The field's status lines read `discard_eids=64` (the cap) in four of five joiner runs against 88-229
// guarded copies, so a guarded copy past the 64th could still hurt the joiner. One entry per guarded copy's entity id;
// the scan per hit is 256 relaxed loads.
constexpr int kMaxDiscard = 256;
std::atomic<uint32_t> g_discard[kMaxDiscard]{};
bool is_discard_attacker_impl(uint32_t eid) {
    if (!eid) return false;
    for (auto& d : g_discard) if (d.load(std::memory_order_relaxed) == eid) return true;
    return false;
}
std::atomic<bool> g_npcWatch{true};

// WO-170 F1: the figure guard (any thread reads, the pipe's config op writes).
std::atomic<bool> g_figureGuard{true};
wo170rules::SwingLedger g_ledger;          // guarded by g_qMutex
std::vector<uint32_t> g_cancelFigure;      // victims another hit landed on: their open put-back watch ends (guarded by g_qMutex)
std::atomic<uint32_t> g_figureWatchesOpen{0};
std::atomic<uint32_t> c_figSeen{0}, c_figRelayed{0}, c_figDropped{0}, c_figPutBack{0}, c_figFatal{0}, c_figCancelled{0}, c_figUnread{0}, c_figMissile{0};

// WO-165: only the thread that opens the existing authority-selected replay
// may use its exact actor/victim permit. Other engine jobs see an empty window.
struct ReplayWindow {
    visualdamage::ReplayPermit permit;
    uint32_t seq = 0;
    void* victimSoul = nullptr;
};
thread_local ReplayWindow t_replay;
thread_local ReplayCapture t_replayCap;
std::atomic<ReplayDmgFn> g_replayDmgFn{nullptr};
std::atomic<uint32_t> c_replaySeen{0};

// Recently-hit souls (main thread only).
std::unordered_map<void*, double> g_hitSouls;
// Engagements (victim eid << 32 | avatar eid) -> last skirmish add time.
std::unordered_map<uint64_t, double> g_engaged;
// NPC name -> entity id (one walk per name, verified by name on use).
std::unordered_map<std::string, uint32_t> g_nameEids;

std::atomic<uint32_t> c_melee{0}, c_missile{0}, c_avatarHits{0}, c_restored{0}, c_ffQueued{0}, c_marks{0},
    c_attrib{0}, c_attribHistory{0}, c_skirmish{0}, c_pvpIn{0}, c_faults{0},
    c_npcAvatarHits{0}, c_npcHitsSent{0}, c_discardHits{0}, c_discarded{0}, c_skirmishKept{0}, c_skirmishRemove{0}, c_relationAsked{0}, c_sceneKept{0}, c_attribKept{0};
std::atomic<uint32_t> c_hostBlows[5]{}, c_hitsOnMe{0};   // WO-176 (B1a): the host's own melee blows by the engine's verdict (1 hit, 2 blocked, 3 pb, 4 broken)
// WO-174: actor eid -> when WO174-BRAWL was last said for it
std::unordered_map<uint32_t, double> g_sceneSaid;

double now_s() { LARGE_INTEGER q, f; QueryPerformanceCounter(&q); QueryPerformanceFrequency(&f); return double(q.QuadPart) / double(f.QuadPart); }

bool read_hit(const uint8_t* data, uint64_t* aw, uint32_t* aeid, uint64_t* vw, uint32_t* veid, uint8_t* material) {
    return rd(data, 0x00, aw) && rd(data, 0x08, aeid) && rd(data, 0x10, vw) && rd(data, 0x18, veid) && rd(data, 0x4C, material);
}

thread_local bool t_dryRun = false;   // WO-210 E.2: the gate's dry run on this thread
std::atomic<uint32_t> c_dryRunHits{0};

void* hit_common(bool missile, HitFn orig, void* self, void* out, const uint8_t* data) {
    if (t_dryRun) { c_dryRunHits.fetch_add(1, std::memory_order_relaxed); return orig(self, out, data); }   // WO-210 E.2: nothing of ours
    (missile ? c_missile : c_melee).fetch_add(1, std::memory_order_relaxed);
    uint64_t aw = 0, vw = 0; uint32_t aeid = 0, veid = 0; uint8_t mat = 0;
    if (!g_pvpHook.load(std::memory_order_relaxed) || !read_hit(data, &aw, &aeid, &vw, &veid, &mat)) return orig(self, out, data);
    const bool byPlayer = aw != 0 && aw == g_playerWuid.load(std::memory_order_relaxed);
    const uint64_t contactTick = byPlayer && veid ? GetTickCount64() : 0;
    const auto source = contactTick ? pvp_scope() : wo155rules::Recipient{};
    const uint64_t contactLoad = contactTick ? loadsequence::stamp() : 0;
    const bool measuredMissile = p6::contact(missile, aeid, veid, self, byPlayer); // always run original cause producer
    // WO-165: our own replay of a blow (wo165.cpp) -- on the thread that opened the window, this attacker on this victim. Exempt from
    // the discard (WO-162 Q1.3 #9: a replay from an engaged copy would be put back) and from every other branch: the record the engine
    // built is copied (the probe's reads: block +0x54/+0x55, perfect +0x56, broken +0x57, zone mismatch +0x61), the victim is watched
    // to MEASURE the damage the engine applies, and the original runs (never skipped: Q1.3).
    if (!missile && t_replay.permit.combat && aeid == t_replay.permit.attacker && veid == t_replay.permit.victim) {
        c_replaySeen.fetch_add(1, std::memory_order_relaxed);
        t_replayCap.seen = true; t_replayCap.self = self;
        KCDMP_FAULT_READ(cpSite, "hits::replay_copy");
        fault::guarded(cpSite, [&] { std::memcpy(t_replayCap.rec, data, sizeof t_replayCap.rec); });
        void* vs = t_replay.victimSoul;
        float hp0 = 0, st0 = 0;
        const bool r0 = vs && rttr::soul_state(vs, "health", &hp0) && rttr::soul_state(vs, "stamina", &st0);
        void* res = orig(self, out, data);
        if (r0) {
            std::lock_guard<std::mutex> lock(g_qMutex);
            g_newWatches.push_back({vs, veid, hp0, st0, 0, mat, static_cast<uint8_t>(kWatchReplay), t_replay.seq});
            // WO-173: a put-back watch of a dropped figure blow on this victim must not undo the engine's own blow
            if (g_figureWatchesOpen.load(std::memory_order_relaxed) > 0 && g_cancelFigure.size() < 64) g_cancelFigure.push_back(veid);
        }
        return res;
    }
    bool protectedVisual = false;
    if (aeid && self && wo210hit::visual_gate_ready() && g_visualCount.load(std::memory_order_acquire)) {
        std::lock_guard<std::mutex> lock(g_visualMu);
        for (auto& body : g_visualBodies) {
            if (body.id.eid != aeid || body.combatSoul != self) continue;
            protectedVisual = true;
            if (!missile) body.bypassedMeleeGate = true;
            break;
        }
    }
    if (protectedVisual) {
        const uint64_t n = c_visualEscape.fetch_add(1, std::memory_order_relaxed) + 1;
        if (n <= 10 || n % 50 == 0)
            logf("MP-RP-DAMAGE escaped_cause attacker=0x%X victim=0x%X missile=%d application=unconfirmed n=%llu",
                 aeid, veid, missile ? 1 : 0, static_cast<unsigned long long>(n));
    }
    // WO-170 F1: a blow of a partner's figure. The figure only ever does what its partner does: a blow that no relayed swing of his
    // matches is the figure's own (its brain, an animation's collision) and is measured and put back like the avatar's own hits above --
    // never skipped (the engine's call wants a cause back). P6: remote-figure missiles have no relayed shooting authority;
    // use the existing measured put-back watch, never the melee ledger or a synthetic cause.
    if ((protectedVisual || (p6rules::figure_guard(missile,g_figureGuard.load(std::memory_order_relaxed)) && aeid && avatar_soul(aeid))) && veid != aeid) {
        c_figSeen.fetch_add(1, std::memory_order_relaxed);
        {
            if(missile)c_figMissile.fetch_add(1,std::memory_order_relaxed);
            bool ledgerMatch=false;
            if(!missile && !protectedVisual){std::lock_guard<std::mutex> lock(g_qMutex);ledgerMatch=g_ledger.take(aeid,now_s())==wo170rules::Verdict::Relayed;}
            if (p6rules::figure_relayed(missile,ledgerMatch)) {
                const uint32_t n = c_figRelayed.fetch_add(1, std::memory_order_relaxed);
                if (n < 20 || (n % 50) == 0) logf("WO170-FIGUREHIT relayed by=0x%X victim=0x%X -- matches a swing the partner sent: applied [n=%u]", aeid, veid, n + 1);
            } else {
                void* vs = avatar_soul(veid);
                if (!vs && veid == g_playerEid.load(std::memory_order_relaxed)) vs = rttr::read_player_soul();
                if (!vs) vs = soul_of_eid(veid);
                float hp0 = 0, st0 = 0;
                const bool r0 = vs && rttr::soul_state(vs, "health", &hp0) && rttr::soul_state(vs, "stamina", &st0);
                const uint32_t n = c_figDropped.fetch_add(1, std::memory_order_relaxed);
                if (!r0) {
                    c_figUnread.fetch_add(1, std::memory_order_relaxed);
                    logf("WO170-FIGUREHIT dropped by=0x%X victim=0x%X why=not-relayed put-back=UNAVAILABLE (the victim's soul or its health could not be read) [n=%u]", aeid, veid, n + 1);
                    return orig(self, out, data);
                }
                void* res = orig(self, out, data);
                {
                    std::lock_guard<std::mutex> lock(g_qMutex);
                    g_newWatches.push_back({vs, veid, hp0, st0, static_cast<uint8_t>(missile?0x02:0), mat, static_cast<uint8_t>(kWatchFigure), aeid});
                }
                g_figureWatchesOpen.fetch_add(1, std::memory_order_relaxed);
                return res;
            }
        }
    } else if (veid && g_figureWatchesOpen.load(std::memory_order_relaxed) > 0) {
        // Any other blow on a victim with an open put-back watch: that watch cannot tell the two apart, so it ends (said when it does)
        std::lock_guard<std::mutex> lock(g_qMutex);
        if (g_cancelFigure.size() < 64) g_cancelFigure.push_back(veid);
    }
    void* vsoul = avatar_soul(veid);
    if (vsoul) {
        // A hit on a peer's avatar. NEVER skipped: the caller expects a cause
        // back (session 1: an empty one crashed the game). Measured and put back.
        const uint8_t flags = missile ? 0x02 : 0;
        const wo155rules::WatchCapture capture{contactTick, contactLoad, source, veid, flags, mat};
        const uint32_t nHit = c_avatarHits.fetch_add(1, std::memory_order_relaxed);
        if (nHit < 12) {
            // S_CombatHitData, as 0x70ce00 copies it (0x90 bytes): the damage
            // fields are identified from these dumps against the victim's
            // later stamina/health change.
            char line[1100]; int k = std::snprintf(line, sizeof(line), "WO121-HITDUMP n=%u missile=%d", nHit, missile ? 1 : 0);
            for (int o = 0x20; o < 0x90 && k < static_cast<int>(sizeof(line)) - 40; o += 4) {
                uint32_t u = 0; float f = 0;
                if (!rd(data, o, &u)) break;
                std::memcpy(&f, &u, 4);
                k += std::snprintf(line + k, sizeof(line) - k, " +%02X=%08X(%.3g)", o, u, f);
            }
            logf("%s", line);
        }
        // The damage is NOT applied inside this call: 0x70ce00 copies the hit
        // data into a cause and queues it; the victim applies it on a later
        // update (session 4: hp/stamina unchanged across the call, every
        // time). And S_CombatHitData holds no damage amount (the dump above).
        // So a player's hit is WATCHED: the avatar's health/stamina are read
        // every frame until the quiet window ends. Each drop is put back at
        // once and measured loss is forwarded at bounded intervals.
        // WO-132: an NPC's hit is watched the same way, so the owner gets the
        // hit's own damage -- never a bleed tick or any other drop between hits.
        const bool byNpc = !byPlayer && aeid != 0 && g_npcWatch.load(std::memory_order_relaxed);
        if (byNpc) c_npcAvatarHits.fetch_add(1, std::memory_order_relaxed);
        float hp0 = 0, st0 = 0;
        const bool r0 = (byPlayer || byNpc) && rttr::soul_state(vsoul, "health", &hp0) && rttr::soul_state(vsoul, "stamina", &st0);
        void* res = orig(self, out, data);
        if (r0) {
            std::lock_guard<std::mutex> lock(g_qMutex);
            g_newWatches.push_back({vsoul, veid, hp0, st0, flags, mat,
                                    static_cast<uint8_t>(byPlayer ? kWatchPvp : kWatchNpc), byPlayer ? 0u : aeid, 0, capture});
        }
        return res;
    }
    // WO-132: on a joiner, an engaged copy of a host NPC hitting the local
    // player -- the host's world decides the joiner's damage, so this local
    // blow is measured and put back (the reaction still plays).
    if (veid && veid == g_playerEid.load(std::memory_order_relaxed) && is_discard_attacker(aeid)) {
        c_discardHits.fetch_add(1, std::memory_order_relaxed);
        void* psoul = rttr::read_player_soul();
        float hp0 = 0, st0 = 0;
        const bool r0 = psoul && rttr::soul_state(psoul, "health", &hp0) && rttr::soul_state(psoul, "stamina", &st0);
        // WO-208 B (mp_defence_local): this machine's engine judges the copy's blow -- its verdict, read from the record it built (block
        // +0x54, perfect +0x56, broken +0x57: WO-176's reads), and the damage it applies is kept. Off: measured and put back (WO-132).
        const bool local = wo208::on(wo208rules::kBitDefenceLocal);
        uint8_t outcome = 0;
        if (local) {
            uint32_t v = 0;
            outcome = rd(data, 0x54, &v) ? wo208rules::def_outcome(static_cast<uint8_t>(v & 0xFF), static_cast<uint8_t>((v >> 16) & 0xFF),
                                                                   static_cast<uint8_t>((v >> 24) & 0xFF)) : wo208rules::kDefHit;
        }
        void* res = orig(self, out, data);
        if (r0) {
            std::lock_guard<std::mutex> lock(g_qMutex);
            NewWatch nw{psoul, veid, hp0, st0, static_cast<uint8_t>(missile ? 0x02 : 0), mat, static_cast<uint8_t>(local ? kWatchDefence : kWatchDiscard), aeid};
            nw.outcome = outcome;
            g_newWatches.push_back(nw);
        }
        return res;
    }
    // WO-176 (B1): an NPC's blow on the local player (the host's own fight) -- the engine's verdict, for the attack-rate comparison. Read only.
    if (!missile && veid && veid == g_playerEid.load(std::memory_order_relaxed) && aeid && aeid != veid) {
        uint32_t v = 0;
        if (rd(data, 0x54, &v)) {
            const uint8_t blk = v & 0xFF, pb = (v >> 16) & 0xFF, broken = (v >> 24) & 0xFF;
            const uint32_t n = c_hitsOnMe.fetch_add(1, std::memory_order_relaxed);
            if (n < 400) logf("WO176-HITME attacker=0x%X outcome=%s -- an NPC's blow on the local player, the engine's verdict [n=%u]", aeid,
                              broken ? "broken" : pb ? "pb" : blk ? "blocked" : "hit", n + 1);
        }
    }
    if (aw != 0 && aw == g_playerWuid.load(std::memory_order_relaxed) && veid) {
        {
            std::lock_guard<std::mutex> lock(g_qMutex);
            if (g_marks.size() < 128) g_marks.push_back({veid, 0, measuredMissile});
        }
        // WO-176 (B1a): the engine's own verdict on the host's real blow (the record it built: block +0x54, second +0x55, perfect +0x56,
        // broken +0x57) -- the baseline the joiner's real hits are compared with. Read only, never changes the blow.
        if (!missile) {
            uint32_t v = 0;
            if (rd(data, 0x54, &v)) {
                const uint8_t blk = v & 0xFF, second = (v >> 8) & 0xFF, pb = (v >> 16) & 0xFF, broken = (v >> 24) & 0xFF;
                const int k = broken ? 4 : pb ? 3 : blk ? 2 : 1;
                static const char* const kNames[] = { "-", "hit", "blocked", "pb", "broken" };
                const uint32_t n = c_hostBlows[k].fetch_add(1, std::memory_order_relaxed);
                if (n < 400) logf("WO176-HOSTHIT victim=0x%X outcome=%s rec=[blk %u second %u pb %u broken %u] -- the host's own blow, the engine's verdict [n=%u]",
                                  veid, kNames[k], blk, second, pb, broken, n + 1);
            }
        }
    }
    return orig(self, out, data);
}

void* __fastcall hk_melee(void* self, void* out, const uint8_t* data) { return hit_common(false, g_origMelee, self, out, data); }
void* __fastcall hk_missile(void* self, void* out, const uint8_t* data) { return hit_common(true, g_origMissile, self, out, data); }

bool patch_slot(void* const* vft, size_t slot, void* fn, void** orig) {
    void** at = const_cast<void**>(vft) + slot / 8;
    void* cur = nullptr;
    if (!rd(at, 0, &cur) || !cur) return false;
    DWORD old = 0;
    if (!VirtualProtect(at, 8, PAGE_READWRITE, &old)) return false;
    *orig = cur;
    InterlockedExchangePointer(at, fn);
    VirtualProtect(at, 8, old, &old);
    return true;
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
void* soul_of_actor(void* actor) {
    void* fn = actor ? vslot(actor, kActorGetSoul) : nullptr;
    void* soul = nullptr;
    return fn && call_p0(fn, actor, &soul) ? soul : nullptr;
}

void remember_visual(uint32_t eid, void* soul) {
    void* entity = engine::entity_by_id(eid);
    void* actor = motion::actor_of(eid);
    if (!entity || !actor || !soul || soul_of_actor(actor) != soul) return;
    void* combatSoul = static_cast<char*>(soul) + kSoulCombat;
    if (!is_a(combatSoul, g_vftCombatSoul)) combatSoul = nullptr;
    const visualdamage::Identity id{eid, actor};
    std::lock_guard<std::mutex> lock(g_visualMu);
    VisualBody* empty = nullptr;
    for (auto& body : g_visualBodies) {
        if (visualdamage::same(body.id, id) && body.entity == entity) {
            body.combatSoul = combatSoul;
            return;
        }
        if (!body.entity && !empty) empty = &body;
    }
    if (empty) {
        *empty = {id, entity, combatSoul, false};
        g_visualCount.fetch_add(1, std::memory_order_release);
    }
}

void retire_missing_figures() {
    std::array<VisualBody, kVisualFigures> bodies;
    {
        std::lock_guard<std::mutex> lock(g_visualMu);
        bodies = g_visualBodies;
    }
    for (const auto& known : bodies) {
        if (!known.entity || engine::entity_by_id(known.id.eid) == known.entity) continue;
        std::lock_guard<std::mutex> lock(g_visualMu);
        for (auto& body : g_visualBodies) {
            if (body.entity != known.entity || !visualdamage::same(body.id, known.id)) continue;
            body = {};
            g_visualCount.fetch_sub(1, std::memory_order_release);
        }
    }
}

struct FindName { const char* name; uint32_t eid; };
bool find_name_visit(void* e, void* ctx) {
    auto* f = static_cast<FindName*>(ctx);
    const char* n = engine::entity_name(e);
    char buf[64]{};
    if (!n) return false;
    size_t i = 0;
    KCDMP_FAULT_READ(site, "hits::find_name_visit");
    if (!fault::guarded(site, [&] { for (; i + 1 < sizeof(buf) && n[i]; ++i) buf[i] = n[i]; })) return false;
    buf[i] = 0;
    if (_stricmp(buf, f->name) == 0) { f->eid = engine::entity_id(e); return true; }
    return false;
}

// WO-132: is this NPC's combat opponent (combat model +0x1118) the local player?
bool npc_fights_local_player(void* actor) {
    void* pca = motion::player_combat_actor();
    if (!actor || !pca) return false;
    void* ca = nullptr;
    if (!rd(actor, 0x300, &ca) || !ca) return false;   // m_pCombatActor (motion.cpp kActorCombatField)
    void* model = nullptr;
    void* opp = nullptr;
    if (!rd(ca, 0x2F0, &model) || !model || !rd(model, 0x1118, &opp) || !opp) return false;
    return opp == pca || static_cast<char*>(opp) - 8 == pca;
}

void copy_name(uint32_t eid, char* out, size_t n) {
    out[0] = 0;
    void* e = eid ? engine::entity_by_id(eid) : nullptr;
    const char* s = e ? engine::entity_name(e) : nullptr;
    if (!s) return;
    size_t i = 0;
    KCDMP_FAULT_READ(site, "hits::copy_name");
    if (!fault::guarded(site, [&] { for (; i + 1 < n && s[i]; ++i) out[i] = s[i]; })) i = 0;
    out[i] = 0;
}

uint32_t eid_by_name(const std::string& name) {
    auto it = g_nameEids.find(name);
    if (it != g_nameEids.end()) {
        void* e = engine::entity_by_id(it->second);
        const char* n = e ? engine::entity_name(e) : nullptr;
        if (n && _stricmp(n, name.c_str()) == 0) return it->second;
        g_nameEids.erase(it);
    }
    FindName f{ name.c_str(), 0 };
    engine::for_each_entity(&find_name_visit, &f);
    if (f.eid) g_nameEids[name] = f.eid;
    return f.eid;
}

} // namespace

// ==================================================================================
void install() {
    HMODULE rpg = GetModuleHandleA("RPGModule.dll");
    if (!rpg) { logf("WO121-HITS DISARMED -- RPGModule not loaded"); return; }
    g_vftCombatSoul = anchor::find_vftable(rpg, ".?AVC_CombatSoul@rpgmodule@wh@@", 0);
    g_vftSkirmish = anchor::find_vftable(rpg, ".?AVC_SkirmishManager@rpgmodule@wh@@", 0);
    // WO-163 (A7): the relation call, named by RTTI (C_SkirmishSituation's vtable) and accepted only when its prologue is the one read in
    // the game's own code (a build that moved the function leaves it unarmed, never mis-called): push the callee-saved registers, rbx = this,
    // ebp = the flag argument.
    g_vftSituation = anchor::find_vftable(rpg, ".?AVC_SkirmishSituation@rpgmodule@wh@@", 0);
    if (g_vftSituation) {
        static const uint8_t kRelPro[] = {0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x6C, 0x24, 0x10, 0x48, 0x89, 0x74, 0x24, 0x18, 0x48, 0x89, 0x7C, 0x24, 0x20,
                                           0x41, 0x56, 0x48, 0x83, 0xEC, 0x20, 0x48, 0x8B, 0xD9, 0x41, 0x0F, 0xB6, 0xE9};
        void* fn = g_vftSituation[kSitRelation / 8];
        if (fn && bytes_eq(fn, kRelPro, sizeof(kRelPro))) g_fnRelation = fn;
    }
    void* melee = g_vftCombatSoul ? g_vftCombatSoul[kSlotMelee / 8] : nullptr;
    void* missile = g_vftCombatSoul ? g_vftCombatSoul[kSlotMissile / 8] : nullptr;

    // History writer: the call slot 0x150 makes that slot 0x158 does not, with
    // the disassembled prologue (push rbx/rdi/r13/r14/r15; sub rsp,0x70).
    static const uint8_t kHistPro[] = {0x40, 0x53, 0x57, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57, 0x48, 0x83, 0xEC, 0x70};
    if (melee && missile) {
        const uint8_t* t[64]{};
        int n = anchor::function_call_targets(rpg, melee, t, 64);
        for (int i = 0; i < n; ++i) {
            if (anchor::function_calls(rpg, missile, t[i])) continue;
            if (bytes_eq(t[i], kHistPro, sizeof(kHistPro))) { g_fnHistory = const_cast<uint8_t*>(t[i]); break; }
        }
    }
    // Skirmish manager getter: StopFight's call with the disassembled prologue;
    // its result's vptr is checked against RTTI C_SkirmishManager at every use.
    static const uint8_t kGetterPro[] = {0x48, 0x89, 0x4C, 0x24, 0x08, 0x53, 0x48, 0x83, 0xEC, 0x20};
    if (const void* sf = actions::stop_fight_fn()) {
        const uint8_t* t[64]{};
        int n = anchor::function_call_targets(rpg, sf, t, 64);
        int picks = 0;
        for (int i = 0; i < n; ++i) {
            if (bytes_eq(t[i], kGetterPro, sizeof(kGetterPro))) { g_fnSkirmishGetter = const_cast<uint8_t*>(t[i]); ++picks; }
        }
        if (picks != 1) g_fnSkirmishGetter = nullptr;
    }

    if (!g_vftCombatSoul || !melee || !missile) g_whyHook = "RTTI C_CombatSoul vftable not unique";
    else if (!g_fnHistory) g_whyHook = "slot 0x150 does not call the combat-history writer (not the disassembled CombatHit)";
    else {
        void* o1 = nullptr, *o2 = nullptr;
        if (!patch_slot(g_vftCombatSoul, kSlotMelee, reinterpret_cast<void*>(&hk_melee), &o1)) g_whyHook = "slot 0x150 could not be patched";
        else {
            g_origMelee = reinterpret_cast<HitFn>(o1);
            if (patch_slot(g_vftCombatSoul, kSlotMissile, reinterpret_cast<void*>(&hk_missile), &o2)) g_origMissile = reinterpret_cast<HitFn>(o2);
            if (!g_origMissile) { g_whyHook = "slot 0x158 could not be patched"; }
            else { g_hookArmed = true; g_whyHook.clear(); }
        }
    }
    if (!g_fnHistory) g_whyAttrib = "combat-history writer not anchored";
    else if (!g_fnSkirmishGetter || !g_vftSkirmish) g_whyAttrib = "skirmish manager not anchored (StopFight / RTTI C_SkirmishManager)";
    else { g_attribArmed = true; g_whyAttrib.clear(); }

    char dm[64]{}, dh[64]{}, dg[64]{};
    anchor::describe(melee, dm, sizeof dm); anchor::describe(g_fnHistory, dh, sizeof dh); anchor::describe(g_fnSkirmishGetter, dg, sizeof dg);
    logf("WO121-HITS piece=hit_slot %s%s%s", g_hookArmed ? "armed" : "NOT armed", g_whyHook.empty() ? "" : " -- ", g_whyHook.c_str());
    logf("WO121-HITS piece=attribution %s%s%s", g_attribArmed ? "armed" : "NOT armed", g_whyAttrib.empty() ? "" : " -- ", g_whyAttrib.c_str());
    logf("WO121-HITS hit_slot=%s attribution=%s combat_hit=%s history=%s skirmish_getter=%s -- local hits on an avatar are measured and put back, never skipped",
         g_hookArmed ? "armed" : "off", g_attribArmed ? "armed" : "off", dm, dh, dg);
}

uint8_t on_config(const uint8_t* body, size_t len) {
    if (len != 3) return 8;
    g_ff = body[0] != 0; g_attribution = body[1] != 0; g_pvpHook = body[2] != 0;
    logf("WO121-HITS config friendly_fire=%d attribution=%d pvp_hook=%d", body[0] != 0, body[1] != 0, body[2] != 0);
    return 0;
}

bool avatar_known(uint32_t eid){return avatar_soul(eid)!=nullptr;}
void note_avatar(uint32_t eid, void* soul, bool on) {
    if (!on) { std::lock_guard<std::mutex> lock(g_qMutex); g_ledger.forget(eid); }   // WO-170: a released body's swings are not licences
    if (on) {
        remember_visual(eid, soul);
        for (auto& a : g_avatars) if (a.eid.load() == eid) { a.soul = soul; return; }
        for (auto& a : g_avatars) if (a.eid.load() == 0) { a.soul = soul; a.eid = eid; return; }
    } else {
        for (auto& a : g_avatars) if (a.eid.load() == eid) { a.eid = 0; a.soul = nullptr; }
    }
}

bool visual_damage_ready(uint32_t figureEid) {
    if (!wo210hit::visual_gate_ready() || !g_hookArmed.load(std::memory_order_acquire)) return false;
    void* soul = avatar_soul(figureEid);
    void* actor = soul ? motion::actor_of(figureEid) : nullptr;
    void* entity = actor ? engine::entity_by_id(figureEid) : nullptr;
    if (!entity || soul_of_actor(actor) != soul) return false;
    bool currentFigure = false;
    {
        std::lock_guard<std::mutex> lock(g_visualMu);
        for (const auto& body : g_visualBodies) {
            if (body.entity != entity || !visualdamage::same(body.id, {figureEid, actor})) continue;
            currentFigure = body.combatSoul && !body.bypassedMeleeGate;
            break;
        }
    }
    if (!currentFigure) return false;

    // The same actor's combat component can be recreated by the engine after
    // streaming. Provision it only once the exact registered figure is known.
    void* combat = nullptr;
    void* model = nullptr;
    uint32_t verifiedEid = 0;
    void* verifiedActor = nullptr;
    return motion::combat_parts(figureEid, true, &combat, &model) &&
           motion::combat_actor_identity(combat, &verifiedEid, &verifiedActor) &&
           visualdamage::same({figureEid, actor}, {verifiedEid, verifiedActor});
}

bool filter_visual_contact(void* attackerCombat, uint32_t victimEid) {
    if (!g_visualCount.load(std::memory_order_acquire)) return false;
    visualdamage::Identity current;
    void* actor = nullptr;
    if (!motion::combat_actor_identity(attackerCombat, &current.eid, &actor)) return false;
    current.actor = actor;
    auto decision = visualdamage::Contact::Ordinary;
    {
        std::lock_guard<std::mutex> lock(g_visualMu);
        for (const auto& body : g_visualBodies) {
            decision = visualdamage::decide(body.id, current, victimEid, attackerCombat, t_replay.permit);
            if (decision != visualdamage::Contact::Ordinary) break;
        }
    }
    if (decision != visualdamage::Contact::Suppressed) return false;
    const uint64_t n = c_visualBlocked.fetch_add(1, std::memory_order_relaxed) + 1;
    if (n <= 20 || n % 100 == 0)
        logf("MP-RP-DAMAGE blocked attacker=0x%X victim=0x%X before_cause=1 n=%llu", current.eid, victimEid, static_cast<unsigned long long>(n));
    return true;
}

VisualDamageStats visual_damage_stats() {
    // The lower-level slot queues a cause. It has no event-bound damage
    // receipt. Escaped causes therefore remain unconfirmed rather than
    // describing every nearby health delta as a second application.
    return {c_visualBlocked.load(std::memory_order_relaxed), 0, c_visualEscape.load(std::memory_order_relaxed)};
}

AttribResult apply_attributed(const uint8_t* body, size_t len) { return apply_attributed_ex(body, len, true, -1.0f, true); }

AttribResult apply_attributed_ex(const uint8_t* body, size_t len, bool rawDamage, float stCap, bool threat,
                                 RawDamageAuthority authority) {
    AttribResult r{};
    if (len < 16 + 4 + 4 + 1 + 4 + 1 || len != static_cast<size_t>(30 + body[29])) { r.reason = 8; return r; }
    float st = 0, hp = 0; uint32_t avatarEid = 0;
    std::memcpy(&st, body + 16, 4); std::memcpy(&hp, body + 20, 4); std::memcpy(&avatarEid, body + 25, 4);
    std::string name(reinterpret_cast<const char*>(body + 30), body[29]);
    if (!std::isfinite(st) || !std::isfinite(hp) || st < 0.0f || hp < 0.0f) { r.reason = 8; return r; }
    if (stCap >= 0.0f && st > stCap) st = stCap;   // WO-173: the capped fallback
    // WO-176 (A1): a quest's arranged fight (by its context; WO-174's name list is the second net) never takes a figure's blow -- nothing of
    // it is routed into that fight (no damage, no history, no skirmish, no threat); reason 9 tells the agent, which tells the joiner once
    const uint32_t veid = name.empty() ? 0 : eid_by_name(name);
    if (veid && scene_owned_eid(veid, "wo121-attributed")) { r.reason = 9; c_attribKept.fetch_add(1); return r; }
    void* victimSoulR = rttr::find_soul_by_guid(body);   // the RTTR Soul (TakeDamage)
    void* avatarActor = actor_by_eid(avatarEid);
    void* avatarSoul = soul_of_actor(avatarActor);
    if (!victimSoulR) { r.reason = 3; return r; }
    if (!avatarSoul) { r.reason = 4; return r; }
    if (rawDamage && authority == RawDamageAuthority::ReplicatedMelee) {
        void* named = buffs::as_c_soul(soul_of_actor(veid ? actor_by_eid(veid) : nullptr));
        void* victim = buffs::as_c_soul(victimSoulR);
        float before = -1.0f;
        if (!named || named != victim || !rttr::soul_state(victim, "health", &before) ||
            !npcdeath::nonlethal_health(hp, before, hp)) {
            r.reason = 3;
            return r;
        }
    }
    void* avatarEnt = engine::entity_by_id(avatarEid);
    r.attackerWuid = avatarEnt ? actions::entity_wuid(avatarEnt) : 0;
    // 1. damage with the avatar as the attacker (WO-173: skipped when the engine's hit processor already applied this blow)
    if (!rawDamage) {
        r.steps |= 1;
        rttr::note_remote_damage(body, hp);   // the LocalHit observer must not report the engine's drop as ours either
    } else if (rttr::apply_damage_soul(victimSoulR, st, hp, buffs::as_c_soul(avatarSoul) ? buffs::as_c_soul(avatarSoul) : avatarSoul)) {
        r.steps |= 1;
        r.staminaSubmitted = st;
        r.healthSubmitted = hp;
        if (veid) note_applied_damage(veid, st, hp);   // WO-170: never put back with a dropped figure blow
        // The plain path's echo guard: without it the LocalHit observer
        // reported this remote hit as ours and sent it back out (session 7).
        rttr::note_remote_damage(body, hp);
    }
    c_attrib.fetch_add(1);
    if (!g_attribArmed) { r.ok = (r.steps & 1) != 0; r.reason = 5; return r; }
    // The victim's own actor/soul (the path the probe proved for the history
    // writer and the skirmish), by name -- one walk per NPC, cached.
    void* victimEnt = veid ? engine::entity_by_id(veid) : nullptr;
    void* victimActor = veid ? actor_by_eid(veid) : nullptr;
    void* victimSoulA = soul_of_actor(victimActor);
    r.victimWuid = victimEnt ? actions::entity_wuid(victimEnt) : 0;
    // 2. combat history: {+0x00 attacker WUID, +0x10 victim WUID}
    if (victimSoulA && r.attackerWuid && r.victimWuid) {
        void* vcs = static_cast<char*>(victimSoulA) + kSoulCombat;
        if (is_a(vcs, g_vftCombatSoul)) {
            alignas(16) uint8_t d[0x90]{};
            std::memcpy(d + 0x00, &r.attackerWuid, 8);
            std::memcpy(d + 0x10, &r.victimWuid, 8);
            if (call_history(g_fnHistory, vcs, d)) { r.steps |= 2; c_attribHistory.fetch_add(1); }
            else c_faults.fetch_add(1);
        }
    }
    // 3. skirmish, once per engagement (WO-174: never a scripted brawl's -- its actors keep their fight, the avatar stays out of it)
    if (victimSoulA && veid) {
        const uint64_t key = (static_cast<uint64_t>(veid) << 32) | avatarEid;
        const double now = now_s();
        auto it = g_engaged.find(key);
        if (it == g_engaged.end() || now - it->second > kEngagementS) {
            void* mgr = nullptr;
            if (call_getter(g_fnSkirmishGetter, &mgr) && is_a(mgr, g_vftSkirmish)) {
                // WO-132: override 1 makes the avatar the NPC's target and
                // opponent. When the NPC is already fighting the local player
                // (the host), that took the host's fight away from him (the
                // field: the guard never turned to the host); then the avatar
                // only joins the skirmish (override 0) and the NPC keeps the host.
                const bool fightsHost = npc_fights_local_player(victimActor);
                const uint8_t ovr = fightsHost ? 0 : 1;
                if (fightsHost) c_skirmishKept.fetch_add(1);
                uint64_t rv = 0;
                if (call_add_soul(g_vftSkirmish[kSmAddSoul / 8], mgr, victimSoulA, avatarSoul, ovr, &rv)) {
                    r.steps |= 4; c_skirmish.fetch_add(1);
                    logf("WO121-HITS attributed npc=%s avatar_eid=0x%X skirmish add (override %u%s) -> 0x%llX", name.c_str(), avatarEid, ovr,
                         fightsHost ? ": it fights the host -- WO-136's threat decides whether it turns" : "", static_cast<unsigned long long>(rv));
                } else c_faults.fetch_add(1);
            }
            g_engaged[key] = now;
        } else {
            it->second = now;   // a running engagement stays one engagement
            r.steps |= 4;
        }
        // WO-136: the hit the engine cannot perceive (no hit volume) counts as the
        // avatar's threat; the NPC turns to it the way it would to a real attacker.
        if (threat) kcdmp::wo136::note_threat(veid, avatarEid, 2, "avatar-hit", hp);   // WO-163 (A6): the hit's own measured damage
    }
    r.ok = (r.steps & 1) != 0;
    return r;
}

wo155rules::HitResult apply_pvp_hit(const uint8_t* body, size_t len, wo155rules::Recipient recipient,
                                  uint64_t readTick, uint8_t sequence) {
    using wo155rules::HitResult;
    if (len != 10 && len != 34 && len != 42) return HitResult::Refused;
    uint64_t contactSequence = 0, incarnation = 0;
    uint64_t ingressTick = readTick;
    if (len >= 34) {
        std::memcpy(&contactSequence, body + 10, 8);
        std::memcpy(&incarnation, body + 18, 8);
        std::memcpy(&ingressTick, body + 26, 8);
        // Environment.TickCount64 and GetTickCount64 share the Windows boot clock.
        if (!contactSequence || !incarnation || !ingressTick || ingressTick > readTick)
            return HitResult::Refused;
    }
    if (len == 42) {
        std::memcpy(&recipient.proof, body + 34, 8);
        if (!recipient.proof) return HitResult::Refused;
    }
    float st = 0, hp = 0;
    std::memcpy(&st, body, 4); std::memcpy(&hp, body + 4, 4);
    const uint8_t flags = body[8], attacker = body[9];
    constexpr uint8_t kPvpNoKnockdown = 0x80;   // WO-155: the agent's mp_ff_knockdown off (a pipe bit, not a wire flag)
    if (!std::isfinite(st) || !std::isfinite(hp) || st < 0 || hp < 0) return HitResult::Refused;
    const uint64_t nativeEntryTick = GetTickCount64();
    const auto recipientCurrent = [&] {
        return recipient.matches(pvp_scope(), ingressTick);
    };
    if (!recipientCurrent()) return HitResult::RecipientRetired;
    void* player = rttr::read_player_soul();
    if (!player) return HitResult::Refused;
    const auto identify = [] { return engine::entity_by_id(0x7777); };
    void* entity = identify();
    const uint64_t wuid = entity ? actions::entity_wuid(entity) : 0;
    const uint32_t eid = entity ? engine::entity_id(entity) : 0;
    if (recipient.proof && !p4::pvp_body_matches(player, wuid)) return HitResult::RecipientRetired;
    const auto bodyCurrent = [&] {
        return rttr::read_player_soul() == player &&
            (!recipient.proof || (entity && identify() == entity &&
                                  p4::pvp_body_matches(player, actions::entity_wuid(entity))));
    };
    float hpBefore = 0, stBefore = 0, hpAfter = 0, stAfter = 0;
    const auto readVitals = [player](float& health, float& stamina) {
        return rttr::soul_state(player, "health", &health) && rttr::soul_state(player, "stamina", &stamina);
    };
    const bool beforeKnown = wuid && readVitals(hpBefore, stBefore);
    // A stale recipient cannot consume protection or reach either of the damage overloads.
    if (!recipientCurrent() || (recipient.proof && !bodyCurrent())) return HitResult::RecipientRetired;
    const bool protectedPlayer = p4::protected_player(player);
    if (protectedPlayer) {
        logf("T3-RECOVERY hit=Pvp decision=protected_consumed applied_hp=0 applied_st=0 peer=%u", attacker);
    }
    // WO-155: friendly fire keeps knocking the victim down (the game's scripted hit), but never twice in a row:
    // while he is on the ground, and for 5 s after the hit that knocked him down, the damage goes in through the
    // four-argument call with SuppressHitReaction (health and stamina exact, no second fall). The agent's
    // kPvpNoKnockdown (mp_ff_knockdown off) suppresses every one.
    static kcdmp::wo155rules::FfWindow s_ffWin;
    static uint64_t s_ffWorld = 0, s_ffVictim = 0;
    if (s_ffWorld != recipient.generation || s_ffVictim != wuid) {
        s_ffWin = {};
        s_ffWorld = recipient.generation;
        s_ffVictim = wuid;
    }
    const bool noKnock = (flags & kPvpNoKnockdown) != 0;
    const bool downNow = kcdmp::motion::local_down_now();
    auto window = s_ffWin;
    const bool suppress = !protectedPlayer &&
        (noKnock || window.suppress(npcdrive::now_s(), downNow, kcdmp::motion::local_last_down_edge_s()));
    bool recipientRetired = false;
    bool viaOld = false;
    bool hintSent = false;
    uint64_t applyTick = 0;
    auto outcome = wo155rules::dispatch(protectedPlayer, suppress, [&](bool suppressReaction) {
        if (recipientRetired || !recipientCurrent() || !bodyCurrent()) {
            recipientRetired = true;
            return rttr::DamageInvocation::NotInvoked;
        }
        // The hint precedes damage because the engine may floor Henry within this call.
        if (!hintSent) {
            respawn::note_pvp_hit((flags & 0x01) != 0, attacker);
            hintSent = true;
        }
        viaOld = suppress && !suppressReaction;
        if (!applyTick) applyTick = GetTickCount64();
        return rttr::invoke_damage_soul(player, st, hp, nullptr, suppressReaction ? 1 : -1);
    });
    if (recipientRetired) return HitResult::RecipientRetired;
    if (outcome != HitResult::ProtectedConsumed) {
        c_pvpIn.fetch_add(1);
        if (outcome != HitResult::Refused) s_ffWin = window;
    }
    void* afterEntity = beforeKnown ? identify() : nullptr;
    // Match the recipient identity even if this hit itself changed its life state.
    const bool measured = afterEntity && afterEntity == entity && actions::entity_wuid(afterEntity) == wuid &&
        p4::generation() == recipient.generation && rttr::read_player_soul() == player &&
        readVitals(hpAfter, stAfter);
    const uint64_t measuredTick = GetTickCount64();
    const uint64_t queueEndTick = applyTick ? applyTick : nativeEntryTick;
    const uint64_t receiveQueueMs = queueEndTick >= ingressTick ? queueEndTick - ingressTick : 0;
    const uint64_t engineMs = applyTick && measuredTick >= applyTick ? measuredTick - applyTick : 0;
    const bool acknowledged = outcome == HitResult::Acknowledged;
    if (acknowledged) {
        // Preserve the existing figure-watch correction until the engine confirms
        // whether TakeDamage can complete its numeric effect on a later frame.
        note_applied_damage(eid, st, hp);
        p4::note_damage(player, hp, "Pvp", attacker);
        if (measured && (hpAfter < hpBefore || stAfter < stBefore)) outcome = HitResult::EffectObserved;
    }
    const char* status = outcome == HitResult::EffectObserved ? "observed" :
        outcome == HitResult::ProtectedConsumed ? "protected_consumed" :
        outcome == HitResult::DispatchUnconfirmed ? "dispatch_unconfirmed" :
        acknowledged ? "acknowledged" : "refused";
    char timing[192];
    std::snprintf(timing, sizeof timing, "recv_tick=%llu pipe_read_tick=%llu apply_tick=%llu receive_queue_ms=%llu engine_ms=%llu missile=%u",
                  static_cast<unsigned long long>(ingressTick), static_cast<unsigned long long>(readTick),
                  static_cast<unsigned long long>(applyTick), static_cast<unsigned long long>(receiveQueueMs),
                  static_cast<unsigned long long>(engineMs), (flags & 0x02) ? 1u : 0u);
    if (measured) {
        logf("MP-LANE2-COMBAT event=contact source=pvp outcome=%s peer=%u seq=%u scope=%llu/%llu target=0x%X hp_before=%.4f hp_after=%.4f st_before=%.4f st_after=%.4f contact=%llu/%llu recipient_life=%llu %s",
             status, attacker, sequence, static_cast<unsigned long long>(recipient.generation),
             static_cast<unsigned long long>(recipient.life), eid, hpBefore, hpAfter, stBefore, stAfter,
             static_cast<unsigned long long>(incarnation), static_cast<unsigned long long>(contactSequence),
             static_cast<unsigned long long>(recipient.proof), timing);
    } else {
        logf("MP-LANE2-COMBAT event=contact source=pvp outcome=%s peer=%u seq=%u scope=%llu/%llu target=0x%X vitals=unavailable contact=%llu/%llu recipient_life=%llu %s",
             status, attacker, sequence, static_cast<unsigned long long>(recipient.generation),
             static_cast<unsigned long long>(recipient.life), eid,
             static_cast<unsigned long long>(incarnation), static_cast<unsigned long long>(contactSequence),
             static_cast<unsigned long long>(recipient.proof), timing);
    }
    if (!protectedPlayer) {
        logf("WO121-HITS friendly-fire hit on the player: hp -%.2f st -%.2f unarmed=%d from ghost %u -> %s",
             hp, st, (flags & 1) ? 1 : 0, attacker, acknowledged ? "acknowledged" : status);
        logf("WO155-FF knockdown=%s%s", noKnock ? "off(mp_ff_knockdown)" : downNow ? "no(he is down)" : suppress ? "no(inside the 5 s window)" : "allowed",
             viaOld ? " -- fallback selected after a verified pre-invoke refusal" : "");
    }
    return outcome;
}

bool hit_by_player(void* soul, double withinS) {
    auto it = g_hitSouls.find(soul);
    return it != g_hitSouls.end() && now_s() - it->second <= withinS;
}

void set_pvp_callback(PvpFn fn) { g_pvpFn.store(fn); }
void set_npc_hit_callback(NpcHitFn fn) { g_npcHitFn.store(fn); }
void set_discard_callback(DiscardFn fn) { g_discardFn.store(fn); }
bool is_discard_attacker(uint32_t eid) { return is_discard_attacker_impl(eid); }

void note_player_damage(float st, float hp) {
    // A forwarded host hit landed on the local player: a discard watch open
    // right now must not put it back (WO-208 B: nor count it as a copy's blow judged here).
    for (auto& w : g_watches) if (w.kind == kWatchDiscard || w.kind == kWatchDefence) { w.hp0 -= hp; w.st0 -= st; }
}

void set_defence_callback(DefenceFn fn) { g_defenceFn.store(fn); }
void report_defence(uint32_t attackerEid, uint8_t outcome, float stamina, float health) {
    c_defOutcome[outcome < 7 ? outcome : 0].fetch_add(1);
    if (DefenceFn fn = g_defenceFn.load()) fn(attackerEid, outcome, stamina, health);
}
double last_local_hit_from(uint32_t attackerEid) {
    auto it = g_defLanded.find(attackerEid);
    return it == g_defLanded.end() ? -1.0 : it->second;
}

bool discard_from(uint32_t eid, bool on) {
    if (!eid) return false;
    for (auto& d : g_discard) if (d.load() == eid) { if (!on) d.store(0); return true; }
    if (!on) return true;
    for (auto& d : g_discard) { uint32_t z = 0; if (d.compare_exchange_strong(z, eid)) return true; }
    return false;
}

int discard_count() { int n = 0; for (auto& d : g_discard) if (d.load()) ++n; return n; }

bool skirmish_ready() { return g_attribArmed && g_fnSkirmishGetter && g_vftSkirmish; }

bool scene_owned_eid(uint32_t eid, const char* site) {
    if (!eid) return false;
    // WO-176 (A1): the fight's own context first (a quest's arranged fight marks its souls); WO-174's name list below is the second net
    if (kcdmp::wo176::arranged_context(eid) == 1) { c_sceneKept.fetch_add(1); return kcdmp::wo176::kept_out(eid, site); }
    char nm[64]{};
    copy_name(eid, nm, sizeof nm);
    if (!wo174rules::scene_owned(nm)) return false;
    c_sceneKept.fetch_add(1);
    const double now = now_s();
    auto it = g_sceneSaid.find(eid);
    if (it == g_sceneSaid.end() || now - it->second >= wo174rules::kBrawlLogEveryS) {
        g_sceneSaid[eid] = now;
        logf("WO174-BRAWL skirmish=scripted quest=by-actor-name npc=%s site=%s figure_kept_out=yes -- a scripted brawl is the game's own: no partner's figure joins its skirmish, no threat, no forced target, no lock", nm, site ? site : "?");
    }
    return true;
}

uint32_t scene_kept_count() { return c_sceneKept.load(); }

bool skirmish_add(void* soul, void* reference, uint8_t ovr, uint64_t* rv) {
    if (!skirmish_ready() || !soul || !reference) return false;
    void* mgr = nullptr;
    if (!call_getter(g_fnSkirmishGetter, &mgr) || !is_a(mgr, g_vftSkirmish)) return false;
    uint64_t r = 0;
    if (!call_add_soul(g_vftSkirmish[kSmAddSoul / 8], mgr, soul, reference, ovr, &r)) { c_faults.fetch_add(1); return false; }
    if (rv) *rv = r;
    return true;
}

bool skirmish_remove(void* soul, uint64_t* rv) {
    if (!skirmish_ready() || !soul) return false;
    void* mgr = nullptr;
    if (!call_getter(g_fnSkirmishGetter, &mgr) || !is_a(mgr, g_vftSkirmish)) return false;
    uint64_t r = 0;
    if (!call_remove_soul(g_vftSkirmish[kSmRemoveSoul / 8], mgr, soul, &r)) { c_faults.fetch_add(1); return false; }
    c_skirmishRemove.fetch_add(1);
    if (rv) *rv = r;
    return true;
}

bool skirmish_relation_armed() { return skirmish_ready() && g_vftSituation && g_fnRelation; }

bool skirmish_hostile(void* soulA, void* soulB, bool* answered, bool* hostile, const char** why) {
    if (answered) *answered = false;
    if (hostile) *hostile = false;
    const char* w = "";
    if (!why) why = &w;
    if (!answered || !hostile || !soulA || !soulB) { *why = "bad arguments"; return false; }
    if (!skirmish_relation_armed()) { *why = "not armed"; return false; }
    void* mgr = nullptr;
    if (!call_getter(g_fnSkirmishGetter, &mgr) || !is_a(mgr, g_vftSkirmish)) { *why = "no skirmish manager"; return false; }
    // The relation object WO-162 places at manager+0x80: it is checked by its vtable THIS frame, whether it lies in the manager (the object's
    // own vptr is at +0x80) or is held by a pointer there (the pointer's target carries it) -- the one that matches is used, anything else is refused.
    void* sit = static_cast<char*>(mgr) + kSmSituation;
    if (!is_a(sit, g_vftSituation)) {
        void* held = nullptr;
        if (rd(mgr, kSmSituation, &held) && held && is_a(held, g_vftSituation)) sit = held;
        else { *why = "manager+0x80 is not a C_SkirmishSituation"; return false; }
    }
    bool r = false;
    if (!call_relation(g_fnRelation, sit, soulA, soulB, &r)) { c_faults.fetch_add(1); *why = "the call faulted"; return false; }
    c_relationAsked.fetch_add(1);
    *answered = true; *hostile = r;
    return true;
}

void* soul_of_eid(uint32_t eid) { return soul_of_actor(actor_by_eid(eid)); }

int avatar_list(uint32_t* eids, void** souls, int max) {
    int n = 0;
    for (auto& a : g_avatars) {
        if (n >= max) break;
        const uint32_t e = a.eid.load(std::memory_order_relaxed);
        if (!e) continue;
        eids[n] = e; souls[n] = a.soul.load(std::memory_order_relaxed); ++n;
    }
    return n;
}

void set_npc_watch(bool on) { g_npcWatch = on; }

void set_figure_guard(bool on) {
    const bool was = g_figureGuard.exchange(on);
    if (was != on) logf("WO170-CFG mp_figure_guard %s", on ? "on" : "off");
}
bool figure_guard() { return g_figureGuard.load(std::memory_order_relaxed); }

void note_figure_swing(uint32_t figureEid) {
    if (!figureEid || !g_figureGuard.load(std::memory_order_relaxed) || !avatar_soul(figureEid)) return;
    std::lock_guard<std::mutex> lock(g_qMutex);
    g_ledger.note_swing(figureEid, now_s());
}

void note_applied_damage(uint32_t victimEid, float stamina, float health) {
    if (!victimEid || g_figureWatchesOpen.load(std::memory_order_relaxed) == 0) return;
    for (auto& w : g_watches) if (w.kind == kWatchFigure && w.eid == victimEid) { w.hp0 -= health; w.st0 -= stamina; }
}

void replay_begin(uint32_t attackerEid, uint32_t victimEid, uint32_t seq, void* attackerCombat, void* victimSoul, bool) {
    t_replay = {{attackerEid, victimEid, attackerCombat, false}, seq, victimSoul};
    t_replayCap = {};
}

void replay_end(ReplayCapture* out) {
    if (out) *out = t_replayCap;
    t_replay = {};
}

void set_replay_damage_callback(ReplayDmgFn fn) { g_replayDmgFn.store(fn); }
uint32_t replay_seen() { return c_replaySeen.load(); }

uint32_t eid_of_name(const char* name) { return name && *name ? eid_by_name(name) : 0; }

void dry_run_begin() { t_dryRun = true; }
void dry_run_end() { t_dryRun = false; }

void mark_player_hit(uint32_t victimEid) {
    if (!victimEid) return;
    std::lock_guard<std::mutex> lock(g_qMutex);
    if (g_marks.size() < 128) g_marks.push_back({victimEid, 0});
}

void tick() {
    pipe::publish_pvp_life();
    p6::tick(); // contact measurements precede the legacy health sampler
    // The player's WUID for the hook (a load changes the entity, not the id,
    // but it is cheap to keep fresh).
    static double s_lastWuid = 0;
    const double now = now_s();
    if (now - s_lastWuid > 1.0) {
        s_lastWuid = now;
        if (void* e = engine::entity_by_id(0x7777)) { g_playerWuid = actions::entity_wuid(e); g_playerEid = engine::entity_id(e); }
        retire_missing_figures();
    }
    std::vector<PvpHit> pvp;
    std::vector<PlayerHitMark> marks;
    std::vector<NewWatch> nw;
    std::vector<uint32_t> cancelFig;
    {
        std::lock_guard<std::mutex> lock(g_qMutex);
        marks.swap(g_marks);
        nw.swap(g_newWatches);
        cancelFig.swap(g_cancelFigure);
    }
    // WO-170: another blow landed on a victim whose dropped figure blow is still being put back: that watch ends, what it saw so far logged
    for (uint32_t victim : cancelFig) {
        for (auto it = g_watches.begin(); it != g_watches.end();) {
            if (it->kind == kWatchFigure && it->eid == victim) {
                c_figCancelled.fetch_add(1, std::memory_order_relaxed);
                g_figureWatchesOpen.fetch_sub(1, std::memory_order_relaxed);
                char vn[64]{}; copy_name(it->eid, vn, sizeof vn);
                logf("WO170-FIGUREHIT watch ended npc=%s by=0x%X -- another blow landed on it inside the window, so none of its loss is put back from here (put back so far: hp -%.2f st -%.2f)",
                     vn, it->attackerEid, it->dh, it->ds);
                it = g_watches.erase(it);
            } else ++it;
        }
    }
    for (const auto& n : nw) {
        bool merged = false;   // a second hit inside the window: same baseline, longer window
        for (auto& w : g_watches) {
            if (w.soul != n.soul || w.kind != n.kind) continue;
            // WO-208 B: another copy's blow on the player while one's measure is open -- the first ends at the second's own start (the
            // engine applies a blow a frame after its call, so the second's starting health closes the first); each is reported alone
            if (n.kind == kWatchDefence && w.attackerEid != n.attackerEid) {
                if (w.hp0 - n.hp0 > w.dh) w.dh = w.hp0 - n.hp0;
                if (w.st0 - n.st0 > w.ds) w.ds = w.st0 - n.st0;
                w.t0 = now - 10.0;   // ends this frame
                continue;
            }
            if (n.kind == kWatchPvp) w.pvp.add(n.pvp);
            else w.flags |= n.flags;
            w.t0 = now; merged = true; break;
        }
        if (merged && n.kind == kWatchFigure) g_figureWatchesOpen.fetch_sub(1, std::memory_order_relaxed);   // WO-170: the open watch carries it
        // WO-165: a second replay on the same victim inside the window shares the first one's measure (one soul, one baseline); its
        // own report says so (health -1) at once, so no caller waits for a number that cannot be separated.
        if (merged && n.kind == kWatchReplay) {
            logf("WO165-REPLAY-DMG seq=%u victim=0x%X merged into the open measure of an earlier replay on the same victim", n.attackerEid, n.eid);
            if (ReplayDmgFn fn = g_replayDmgFn.load()) fn(n.attackerEid, n.eid, -1.0f, -1.0f, true);
        }
        if (!merged) {
            Watch w{n.soul, n.eid, n.hp0, n.st0, 0, 0, n.flags, n.material, now};
            w.kind = n.kind; w.attackerEid = n.attackerEid; w.outcome = n.outcome;
            if (n.kind == kWatchPvp) w.pvp.add(n.pvp);
            if (n.kind == kWatchDefence) g_defLanded[n.attackerEid] = now;   // WO-208 B: wo166's window asks
            g_watches.push_back(w);
        }
    }
    std::vector<Watch> npcDone;
    for (auto it = g_watches.begin(); it != g_watches.end();) {
        Watch& w = *it;
        ++w.frames;
        // The soul must still be this avatar's (a release or a respawn ends the watch).
        // A discard watch is on the local player's soul (a load replaces it).
        const bool live = (w.kind == kWatchDiscard || w.kind == kWatchDefence) ? rttr::read_player_soul() == w.soul
                        : w.kind == kWatchReplay ? (rttr::read_player_soul() == w.soul || soul_of_eid(w.eid) == w.soul)
                        : w.kind == kWatchFigure ? (avatar_soul(w.eid) == w.soul || rttr::read_player_soul() == w.soul || soul_of_eid(w.eid) == w.soul)
                        : avatar_soul(w.eid) == w.soul;
        float hp = 0, st = 0;
        if (live && w.kind == kWatchFigure && rttr::soul_state(w.soul, "health", &hp) && rttr::soul_state(w.soul, "stamina", &st)) {
            // WO-170: the dropped blow's loss goes back at once, every frame of the window; a body it killed is left as the engine has it
            if (wo170rules::should_put_back(hp, w.hp0)) {
                w.dh += w.hp0 - hp; rttr::soul_set_state(w.soul, "health", w.hp0);
                if (w.landedAt < 0) w.landedAt = w.frames;
            } else if (hp <= 0.01f && w.hp0 > 0.01f) {
                w.fatal = true; if (w.landedAt < 0) w.landedAt = w.frames;
            }
            if (st < w.st0 - 0.25f) { w.ds += w.st0 - st; rttr::soul_set_state(w.soul, "stamina", w.st0); if (w.landedAt < 0) w.landedAt = w.frames; }
        } else if (live && (w.kind == kWatchReplay || w.kind == kWatchDefence) && rttr::soul_state(w.soul, "health", &hp) && rttr::soul_state(w.soul, "stamina", &st)) {
            // measured from the lowest point, nothing put back: the engine's own application is the result
            if (w.hp0 - hp > w.dh + 0.01f) { w.dh = w.hp0 - hp; if (w.landedAt < 0) w.landedAt = w.frames; }
            if (w.st0 - st > w.ds + 0.25f) { w.ds = w.st0 - st; if (w.landedAt < 0) w.landedAt = w.frames; }
        } else if (live && rttr::soul_state(w.soul, "health", &hp) && rttr::soul_state(w.soul, "stamina", &st)) {
            bool dropped = false;
            if (hp < w.hp0 - 0.01f) { w.dh += w.hp0 - hp; rttr::soul_set_state(w.soul, "health", w.hp0); dropped = true; }
            // WO-157 3b.1: an NPC's blow on an avatar keeps the stamina it cost (only the health is put back -- the joiner's
            // game takes both). Refilled after every blow, the figure never tired: its stamina soaked every blow, 14 of 16
            // measured hp 0, and the joiner was never hurt. Measured from its lowest point, so a blow is counted once.
            if (st < w.st0 - 0.25f) {
                w.ds += w.st0 - st;
                if (w.kind == kWatchNpc) w.st0 = st; else rttr::soul_set_state(w.soul, "stamina", w.st0);
                dropped = true;
            }
            if (dropped) {
                c_restored.fetch_add(1);
                if (w.landedAt < 0) w.landedAt = w.frames;
            }
        }
        const uint64_t measuredTick = w.kind == kWatchPvp ? GetTickCount64() : 0;
        if (measuredTick && w.landedAt >= 0 && !w.pvpFirstEffectTick) w.pvpFirstEffectTick = measuredTick;
        const bool keepWatching = live && now - w.t0 < (w.kind == kWatchFigure ? wo170rules::kPutBackS : kWatchS);
        if (keepWatching && (w.kind != kWatchPvp || !w.pvp.due(measuredTick))) { ++it; continue; }
        if (w.kind == kWatchFigure) {
            char vn[64]{}, an[64]{};
            copy_name(w.eid, vn, sizeof vn); copy_name(w.attackerEid, an, sizeof an);
            g_figureWatchesOpen.fetch_sub(1, std::memory_order_relaxed);
            if (w.fatal) c_figFatal.fetch_add(1, std::memory_order_relaxed);
            if (w.dh > 0 || w.ds > 0) c_figPutBack.fetch_add(1, std::memory_order_relaxed);
            logf("WO170-FIGUREHIT dropped npc=%s by=%s(0x%X) why=not-relayed -- the figure's own blow: hp -%.2f st -%.2f put back (landed at frame %d of %d)%s%s",
                 vn[0] ? vn : "?", an[0] ? an : "?", w.attackerEid, w.dh, w.ds, w.landedAt, w.frames,
                 w.fatal ? " -- it killed the body: not put back (a dead body is not raised)" : "", live ? "" : " -- victim gone");
            it = g_watches.erase(it);
            continue;
        }
        if (w.kind == kWatchDefence) {
            c_defJudged.fetch_add(1);
            c_defOutcome[w.outcome < 7 ? w.outcome : 0].fetch_add(1);
            const uint32_t k = c_defJudged.load();
            if (k <= 60 || k % 50 == 0)
                logf("WO208-DEF judged attacker=0x%X outcome=%s hp -%.2f st -%.2f (landed at frame %d of %d) -- this machine's engine; kept, to the host [n=%u]",
                     w.attackerEid, wo208rules::def_outcome_name(w.outcome), w.dh, w.ds, w.landedAt, w.frames, k);
            if (DefenceFn fn = g_defenceFn.load()) fn(w.attackerEid, w.outcome, w.ds, w.dh);
            it = g_watches.erase(it);
            continue;
        }
        if (w.kind == kWatchReplay) {
            // attackerEid carries the replay's sequence number here
            logf("WO165-REPLAY-DMG seq=%u victim=0x%X hp -%.2f st -%.2f (landed at frame %d of %d)%s", w.attackerEid, w.eid, w.dh, w.ds,
                 w.landedAt, w.frames, live ? "" : " -- victim gone");
            if (ReplayDmgFn fn = g_replayDmgFn.load()) fn(w.attackerEid, w.eid, w.dh, w.ds, live);
        } else if (w.kind == kWatchPvp) {
            const bool damage = w.dh > 0 || w.ds > 0;
            if (damage || !keepWatching) {
                const auto capture = w.pvp.evidence();
                const bool current = capture.victim == w.eid && capture.tick <= measuredTick && current_pvp_capture(capture);
                const bool send = live && damage && current && g_ff.load();
                const auto measurementId = send ? ++g_pvpMeasurementId : 0;
                const char* result = !live ? "avatar gone, dropped" : !damage ? "no measured loss in this window" :
                    !current ? "dropped (capture scope retired)" : send ? "measured for forwarding" : "dropped (friendly fire off)";
                logf("WO121-HITS player hit on avatar eid=0x%X measured hp -%.2f st -%.2f (landed at frame %d of %d) -> %s"
                     " measurement_id=%llu capture_tick=%llu first_effect_tick=%llu measured_tick=%llu native_observe_ms=%llu missile=%u publication=%s",
                     w.eid, w.dh, w.ds, w.landedAt, w.frames, result, static_cast<unsigned long long>(measurementId),
                     static_cast<unsigned long long>(capture.tick), static_cast<unsigned long long>(w.pvpFirstEffectTick),
                     static_cast<unsigned long long>(measuredTick),
                     static_cast<unsigned long long>(capture.tick && measuredTick >= capture.tick ? measuredTick - capture.tick : 0),
                     (capture.flags & 0x02) ? 1u : 0u, keepWatching ? "bounded" : "final");
                if (send) pvp.push_back({w.ds, w.dh, capture, measuredTick, measurementId});
            }
            if (keepWatching) {
                w.pvp.advance(measuredTick);
                w.dh = w.ds = 0;
                w.pvpFirstEffectTick = 0;
                w.frames = 0;
                w.landedAt = -1;
                ++it;
                continue;
            }
        } else if (w.kind == kWatchNpc) {
            logf("WO132-HITS npc hit on avatar eid=0x%X by eid=0x%X measured hp -%.2f st -%.2f (landed at frame %d of %d) -> %s", w.eid, w.attackerEid,
                 w.dh, w.ds, w.landedAt, w.frames, !live ? "avatar gone, dropped" : (w.dh > 0 || w.ds > 0) ? "to the agent" : "no damage, nothing sent");
            if (live && (w.dh > 0 || w.ds > 0)) npcDone.push_back(w);
        } else {
            c_discarded.fetch_add(1);
            logf("WO132-HITS local hit on the player by engaged copy eid=0x%X discarded: hp -%.2f st -%.2f put back (landed at frame %d of %d)",
                 w.attackerEid, w.dh, w.ds, w.landedAt, w.frames);
            kcdmp::wo166::note_local_hit_dropped(w.attackerEid, w.dh, w.ds);   // WO-166 C3: counted, the host decides
            if (DiscardFn fn = g_discardFn.load()) fn(w.attackerEid, w.ds, w.dh);
        }
        it = g_watches.erase(it);
    }
    if (NpcHitFn fn = g_npcHitFn.load()) {
        for (const auto& w : npcDone) {
            char name[64]{};
            copy_name(w.attackerEid, name, sizeof name);
            fn(w.eid, w.ds, w.dh, w.attackerEid, w.flags, name);
            c_npcHitsSent.fetch_add(1);
        }
    }
    for (const auto& m : marks) {
        if (void* soul = soul_of_actor(actor_by_eid(m.victimEid))) {
            // The pipe's LocalHit looks its soul up by guid (the RTTR Soul);
            // remember both the actor's soul and its C_Soul primary.
            if(m.invalidate){
                g_hitSouls.erase(soul);if(void* c=buffs::as_c_soul(soul))g_hitSouls.erase(c);
            }else{
                g_hitSouls[soul]=now;if(void* c=buffs::as_c_soul(soul))g_hitSouls[c]=now;
                c_marks.fetch_add(1);
            }
        }
        if (kcdmp::wo136::host_target()) kcdmp::wo139::host_struck(m.victimEid);   // WO-154 3.1: a pursuit's lock goes first
        kcdmp::wo136::note_threat(m.victimEid, 0, 2, "host-hit");   // WO-136: the host's real hit, its threat
    }
    if (g_hitSouls.size() > 256)
        for (auto it = g_hitSouls.begin(); it != g_hitSouls.end();) it = (now - it->second > 5.0) ? g_hitSouls.erase(it) : ++it;
    if (PvpFn fn = g_pvpFn.load()) {
        for (const auto& h : pvp) {
            // Unarmed = the local player had no weapon in hand when the hit landed.
            bool armed = true;
            if (void* ps = rttr::read_player_soul()) {
                bool inHand = true;
                if (rttr::combat_bool(ps, "HasWeaponInHand", &inHand)) armed = inHand;
            }
            if (g_ff.load() && current_pvp_capture(h.capture)) {
                const wo155rules::Observation observation{h.capture.tick, h.measuredTick, h.capture.source.proof, h.measurementId};
                const uint8_t flags = h.capture.flags | (armed ? 0 : 0x01);
                fn(h.capture.victim, h.st, h.hp, flags, h.capture.material, observation, h.capture.source.connection);
                c_ffQueued.fetch_add(1);
            }
        }
    }
}

int status_text(char* out, int n) {
    return std::snprintf(out, n,
        "hit_slot=%s attribution=%s ff=%d attrib_cfg=%d melee=%u missile=%u avatar_hits=%u restored=%u ff_sent=%u player_marks=%u "
        "attributed=%u history=%u skirmish=%u pvp_in=%u hit_faults=%u npc_watch=%s npc_avatar_hits=%u npc_hits_sent=%u "
        "discard_hits=%u discarded=%u discard_eids=%d skirmish_kept_host=%u skirmish_remove=%u skirmish_relation=%s relation_asked=%u "
        "scene_kept=%u blows_kept_out=%u figure_guard=%s fig_seen=%u fig_relayed=%u fig_dropped=%u fig_put_back=%u fig_fatal=%u fig_cancelled=%u fig_unread=%u fig_missile_guarded=%u",
        g_hookArmed ? "armed" : "off", g_attribArmed ? "armed" : "off", g_ff.load() ? 1 : 0, g_attribution.load() ? 1 : 0,
        c_melee.load(), c_missile.load(), c_avatarHits.load(), c_restored.load(), c_ffQueued.load(), c_marks.load(),
        c_attrib.load(), c_attribHistory.load(), c_skirmish.load(), c_pvpIn.load(), c_faults.load(),
        (g_hookArmed && g_npcWatch.load()) ? "armed" : "off", c_npcAvatarHits.load(), c_npcHitsSent.load(),
        c_discardHits.load(), c_discarded.load(), discard_count(), c_skirmishKept.load(), c_skirmishRemove.load(),
        skirmish_relation_armed() ? "armed" : "off", c_relationAsked.load(), c_sceneKept.load(), c_attribKept.load(),
        g_figureGuard.load() ? "on" : "off", c_figSeen.load(), c_figRelayed.load(), c_figDropped.load(), c_figPutBack.load(), c_figFatal.load(),
        c_figCancelled.load(), c_figUnread.load(), c_figMissile.load());
}

} // namespace kcdmp::hits
