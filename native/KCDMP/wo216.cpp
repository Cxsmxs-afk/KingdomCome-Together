// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
// WO-216: the rest of the body -- the native half's switch word, status and B's census (wo216.h). A's read and replay live beside the
// route they extend (wo215.cpp: the AI update's entry, the per-copy route).
#include "wo216.h"
#include "wo216_authority_rules.h"
#include "p5_corpse_rules.h"
#include "buffs.h"
#include "p4.h"
#include "wo203.h"
#include "wo163.h"
#include "wo215.h"
#include "wo208.h"
#include "anchors.h"
#include "engine.h"
#include "fault_guard.h"
#include "log.h"
#include "motion.h"
#include "npc_drive.h"
#include "npc_scan.h"
#include "respawn_actions.h"
#include "rttr_abi.h"
#include "hits.h"
#include "wo138.h"
#include "wo217.h"
#include "wo200.h"
#include "inline_hook.h"
#include "x64_len.h"

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <cstdio>
#include <cstring>
#include <deque>
#include <mutex>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace kcdmp::wo216 {

namespace {

using namespace wo216rules;

std::atomic<uint32_t> g_bits{kBitsDefault};
std::atomic<bool> g_configured{false};

uint32_t twin_off() {
    static uint32_t s = 0xFFFFFFFFu;
    if (s == 0xFFFFFFFFu) {
        char b[256] = {};
        const DWORD n = GetEnvironmentVariableA("KCDMP_TEST_W216_OFF", b, sizeof b);
        s = (n > 0 && n < sizeof b) ? twin_off_bits(b) : 0;
        if (s) logf("WO216-CFG the bench's twin: bits 0x%X off for this game (KCDMP_TEST_W216_OFF=%s)", s, b);
    }
    return s;
}

double now_s() {
    static LARGE_INTEGER f{};
    if (!f.QuadPart) QueryPerformanceFrequency(&f);
    LARGE_INTEGER c;
    QueryPerformanceCounter(&c);
    return static_cast<double>(c.QuadPart) / static_cast<double>(f.QuadPart);
}

std::atomic<uint8_t> g_role{0};   // role 2 is pending until op168 confirms Gameplay + native settle
std::mutex g_authorityMu;
wo216authority::State g_authority; // only pure control state under this mutex, never an engine call
std::atomic<uint64_t> g_activeFromMs{0}, g_authorityGeneration{0};
std::atomic<bool> g_authorityEnabled{true}, g_legacyArmed{false};
std::atomic<uint8_t> g_authorityPhase{static_cast<uint8_t>(wo216authority::Phase::Load)};
std::atomic<uint32_t> c_blockedDuringLoad{0}, c_gateArmedAtS{0};
std::atomic<uint32_t> c_npcNear{0}, c_npcBound{0}, c_npcHidden{0}, c_hideFailsafe{1}, c_unhidden10s{0};

void publish_authority() {
    g_activeFromMs.store(0, std::memory_order_release);
    g_authorityEnabled.store(g_authority.enabled(), std::memory_order_release);
    g_authorityPhase.store(static_cast<uint8_t>(g_authority.phase), std::memory_order_release);
    g_legacyArmed.store(g_authority.legacyArmed, std::memory_order_release);
    g_authorityGeneration.store(g_authority.generation, std::memory_order_release);
    g_activeFromMs.store(g_authority.active_from(), std::memory_order_release);
}
bool lifecycle_active(uint64_t now, uint64_t* observedFrom = nullptr) {
    const uint64_t from = g_activeFromMs.load(std::memory_order_acquire);
    if (observedFrom) *observedFrom = from;
    return g_role.load(std::memory_order_acquire) == 2 && g_authorityEnabled.load(std::memory_order_acquire) && from && now >= from;
}
bool legacy_active() { return g_role.load() == 2 && !g_authorityEnabled.load() && g_legacyArmed.load(); }
std::atomic<bool> g_decisionGate{false};
std::atomic<uint32_t> c_decisionBlocked{0};
const char* g_decisionWhy = "not installed";
char g_decisionReason[192]{};   // inline_hook may return a thread-local decoder diagnostic

// Paket C: stop the decision batch BEFORE it visits a brain. Suspending individual
// nodes after their scheduler began left suspended nodes in a live tree. The AI
// module's movement/navigation update is deliberately left running for host orders.
bool decision_gate(void*, void*) {
    const uint64_t now = GetTickCount64(); uint64_t from = 0;
    const bool blocked = lifecycle_active(now, &from) && g_decisionGate.load();
    if (blocked) {
        c_decisionBlocked.fetch_add(1, std::memory_order_relaxed);
        // Count actual blocked callbacks observed in pending Load as well. The
        // deadline rule should make this zero; a concurrent invalidation is
        // deliberately counted conservatively instead of reporting a constant.
        if (g_authorityPhase.load(std::memory_order_acquire) == static_cast<uint8_t>(wo216authority::Phase::Load))
            c_blockedDuringLoad.fetch_add(1, std::memory_order_relaxed);
        uint32_t zero = 0;
        c_gateArmedAtS.compare_exchange_strong(zero, static_cast<uint32_t>((now - from + wo216authority::kSettleMs) / 1000));
    }
    return blocked;
}

void install_decision_gate() {
    HMODULE xg = GetModuleHandleA("XGenAIModule.dll");
    int count = 0;
    const uint8_t* fn = xg ? anchor::function_by_string(xg, "wh::xgenaimodule::C_NPCManager::UpdateIntellects", &count) : nullptr;
    anchor::Range range{};
    if (!fn || count != 1 || !anchor::function_range(xg, fn, &range) || range.begin != fn || range.size() < 32) {
        g_decisionWhy = "UpdateIntellects has no unique profiler anchor and PE function entry";
    } else {
        // As with crash_capture's OS detour, derive only a relocation-free patch
        // length from the anchored live entry. install_gate compares this snapshot
        // again with other threads stopped; no RVA or guessed instruction length.
        uint8_t live[32]{};
        if (!fault::guarded_silent([&] { std::memcpy(live, fn, sizeof live); })) {
            g_decisionWhy = "UpdateIntellects entry unreadable";
        } else {
            size_t len = 0;
            for (size_t n = 14; n <= sizeof live && !len; ++n)
                if (x64::check_patch(live, sizeof live, n).verdict == x64::Boundary::Ok) len = n;
            if (!len) g_decisionWhy = "UpdateIntellects has no relocation-free 14-32 byte entry";
            else if (inlinehook::install_gate(const_cast<uint8_t*>(fn), live, len, &decision_gate, &g_decisionWhy)) {
                g_decisionGate.store(true, std::memory_order_release);
                g_decisionWhy = "armed";
            }
        }
    }
    std::snprintf(g_decisionReason, sizeof g_decisionReason, "%s", g_decisionWhy ? g_decisionWhy : "unknown install failure");
    g_decisionWhy = g_decisionReason;
    logf("HOST-AUTHORITY decision_gate=%s (%s); UpdateIntellects blocked only for role 2; navigation remains live",
         g_decisionGate.load() ? "armed" : "unavailable", g_decisionWhy);
}
// Pure state control is deliberately handled on the pipe reader, including load invalidation.
// The normal main-thread registry entry is also provided for older batch callers.
uint8_t lifecycle_op(const uint8_t* body, size_t len, uint8_t* out, size_t cap, size_t* outLen) {
    *outLen = 0;
    wo216authority::Request r;
    if (cap < wo216authority::kAckBytes || !wo216authority::request(body, len, &r)) return wo163::kRBadRequest;
    const uint64_t now = GetTickCount64();
    std::lock_guard<std::mutex> lock(g_authorityMu);
    const auto before = g_authority;
    const bool accepted = g_authority.apply(r, now);
    if (accepted && (before.generation != g_authority.generation || before.phase != g_authority.phase ||
        before.gameplayAtMs != g_authority.gameplayAtMs || before.loadAccepted != g_authority.loadAccepted || before.legacyArmed != g_authority.legacyArmed)) {
        if (before.generation != g_authority.generation) { c_gateArmedAtS.store(0); p4::invalidate(); }
        publish_authority();
    }
    g_authority.ack(out, g_role.load(), g_decisionGate.load(), now, r.id);
    *outLen = wo216authority::kAckBytes;
    return accepted ? wo163::kROk : wo163::kRBadRequest;
}
bool lifecycle_off_main(uint8_t opc, const uint8_t* body, size_t len, uint8_t* out, size_t cap, size_t* outLen, uint8_t* reason) {
    if (opc != kOpLifecycle) return false;
    *reason = lifecycle_op(body, len, out, cap, outLen); return true;
}
void b_exempt(const char* name, bool on, double forS);
void b_stop(const char* why);
int b_status(char* out, int n);

uint8_t d_read(uint8_t maxN, uint8_t* out, size_t cap, size_t* outLen);
uint8_t d_of(const char* name, uint8_t* out, size_t cap, size_t* outLen);

uint8_t op(uint8_t opc, const uint8_t* body, size_t len, uint8_t* out, size_t cap, size_t* outLen) {
    *outLen = 0;
    if (opc == kOpLifecycle) return lifecycle_op(body, len, out, cap, outLen);
    if (opc == kOpOutfitRead) { if (len != 2) return wo163::kRBadRequest; return d_read(body[1], out, cap, outLen); }
    if (opc == kOpOutfitOf) {
        if (len < 3 || len != 2u + body[1] || body[1] == 0 || body[1] > 63) return wo163::kRBadRequest;
        char nm[64]{};
        std::memcpy(nm, body + 2, body[1]);
        return d_of(nm, out, cap, outLen);
    }
    if (opc == kOpExempt) {   // [on:1][secs:2][nameLen:1][name]
        if (len < 5 || len != 5u + body[4] || body[4] == 0 || body[4] > 63) return wo163::kRBadRequest;
        char nm[64]{};
        std::memcpy(nm, body + 5, body[4]);
        uint16_t secs = 0;
        std::memcpy(&secs, body + 2, 2);
        b_exempt(nm, body[1] != 0, secs ? secs : 60.0);
        return wo163::kROk;
    }
    if (opc == kOpBrains) {
        char t[1200];
        int n = b_status(t, sizeof t);
        if (n < 0) n = 0;
        if (static_cast<size_t>(n) >= sizeof t) n = static_cast<int>(sizeof t) - 1;
        if (static_cast<size_t>(n) > cap) n = static_cast<int>(cap);
        std::memcpy(out, t, static_cast<size_t>(n));
        *outLen = static_cast<size_t>(n);
        return wo163::kROk;
    }
    if (opc == kOpConfig) {
        if ((len != 5 && len != 6 && len != 14) || cap < 4) return wo163::kRBadRequest;
        uint32_t bits = 0; std::memcpy(&bits, body + 1, 4);
        const bool tagged = len == 14;
        const uint64_t generation = tagged ? wo216authority::read_u64(body + 6) : 0;
        uint8_t oldRole = 0; uint32_t oldBits = 0; bool first = false;
        {
            // Short pure-state lock only. In particular, no console/engine call,
            // log write or action on a body may delay off-main Load invalidation.
            std::lock_guard<std::mutex> authorityLock(g_authorityMu);
            if (!g_authority.role_generation(tagged, generation)) return wo163::kRBadRequest;
            oldRole = g_role.load();
            if (len >= 6) {
                if (body[5] > 2) return wo163::kRBadRequest;
                if (body[5] == 2 && g_authorityEnabled.load() && (!g_decisionGate.load() || !wo200::host_authority_ready())) return wo163::kRFailed;
                g_role.store(body[5]); g_authority.role_changed(oldRole, body[5], tagged);
                if (oldRole != body[5]) publish_authority();
            }
            oldBits = g_bits.exchange(bits); first = !g_configured.exchange(true);
        }
        if (len >= 6 && oldRole != body[5]) logf("WO216-CFG role %u -> %u (0 none, 1 the host, 2 a joiner in the host's world)", oldRole, body[5]);
        if (oldBits != bits || first)
            logf("WO216-CFG bits=0x%X (was 0x%X; twin off 0x%X): route_replay=%d no_brains=%d activity_line=%d outfits=%d riders=%d poses=%d npc_fights=%d",
                 bits, oldBits, twin_off(), on(kBitRouteReplay), on(kBitNoBrains), on(kBitActivityLine), on(kBitOutfits), on(kBitRiders), on(kBitPoses),
                 on(kBitNpcFights));
        const uint32_t eff = bits & ~twin_off();
        std::memcpy(out, &eff, 4); *outLen = 4;
        return wo163::kROk;
    }
    if (opc == kOpStatus) {
        char t[2400];
        int n = status(t, sizeof t);
        if (n < 0) n = 0;
        if (static_cast<size_t>(n) >= sizeof t) n = static_cast<int>(sizeof t) - 1;
        if (static_cast<size_t>(n) > cap) n = static_cast<int>(cap);
        std::memcpy(out, t, static_cast<size_t>(n));
        *outLen = static_cast<size_t>(n);
        return wo163::kROk;
    }
    return wo163::kRBadRequest;
}

// ================================================================================================== B: no brain runs on the joiner
// Paket C gates UpdateIntellects before its decision batch. The census now
// controls visibility. Only explicit disabled authority restores the PR14 per-body pause fallback.
// CEntity::Hide stays unguarded because it may take an engine lock (WO-215).
struct Api {
    size_t conExecSlot = 0;
    bool conResolved = false;
    size_t hideSlot = 0;           // CEntity::Hide(bool)
    bool armed = false;
    const char* why = "not resolved";
    bool resolved = false;
} g_b;

void b_resolve() {
    if (g_b.resolved) return;
    g_b.resolved = true;
    // CEntity::Hide(bool) at slot 63: its own first act sets the entity's hidden bit (flags +8, bit 4):
    // mov ecx,[rsi+8]; mov r8d,r14d; shl r8d,4; mov eax,ecx; and eax,0FFFFFFEFh
    void* pe = engine::entity_by_id(0x7777);   // the local player's entity (any CEntity carries the vftable)
    void* vt = nullptr; void* fn = nullptr;
    KCDMP_FAULT_READ(vsite, "wo216::b_resolve vft");
    const size_t kHide = 63 * 8;
    if (!pe || !fault::guarded(vsite, [&] { vt = *reinterpret_cast<void**>(pe); fn = reinterpret_cast<void**>(vt)[63]; }) || !fn) { g_b.why = "no entity to read CEntity's vftable from"; g_b.resolved = false; return; }
    uint8_t hb[0x60]{};
    if (!fault::guarded(vsite, [&] { std::memcpy(hb, fn, sizeof hb); })) { g_b.why = "CEntity::Hide unreadable"; return; }
    static const uint8_t vHide[] = {0x8B, 0x4E, 0x08, 0x45, 0x8B, 0xC6, 0x41, 0xC1, 0xE0, 0x04, 0x8B, 0xC1, 0x83, 0xE0, 0xEF};
    bool hideOk = false;
    for (size_t i = 0; i + sizeof vHide <= sizeof hb && !hideOk; ++i) hideOk = std::memcmp(hb + i, vHide, sizeof vHide) == 0;
    if (!hideOk) { g_b.why = "CEntity's slot 63 does not set the hidden bit (not Hide on this build)"; return; }
    g_b.hideSlot = kHide;
    g_b.armed = true;
    g_b.why = "armed";
    logf("WO216-BRAINS visibility armed: CEntity::Hide is slot 63 (its hidden bit checked)");
}

// Legacy fallback uses the PR14 console-call resolver, only when explicitly disabled.
void b_legacy_resolve() {
    if (g_b.conResolved) return;
    g_b.conResolved = true;
    // the console's ExecuteString: CScriptBind_System::ExecuteCommand (CryScriptSystem, found by its own error text) calls
    // mov rcx,[rdi+78h]; xor r9d,r9d; mov rdx,[rsp+48h]; xor r8d,r8d; mov rax,[rcx]; call [rax+<slot>]   (bSilent = 0, bDefer = 0)
    HMODULE ss = GetModuleHandleA("CryScriptSystem.dll");
    if (!ss) { g_b.why = "CryScriptSystem.dll not loaded"; return; }
    int nf = 0;
    const uint8_t* door = anchor::function_by_string(ss, "CScriptBind_System::ExecuteCommand", &nf);
    if (!door || nf != 1) { g_b.why = "System.ExecuteCommand's binding not found as one function"; return; }
    uint8_t code[0x100]{};
    KCDMP_FAULT_READ(rsite, "wo216::b_resolve");
    if (!fault::guarded(rsite, [&] { std::memcpy(code, door, sizeof code); })) { g_b.why = "the binding unreadable"; return; }
    static const uint8_t pat[] = {0x45, 0x33, 0xC9, 0x48, 0x8B, 0x54, 0x24, 0x48, 0x45, 0x33, 0xC0, 0x48, 0x8B, 0x01, 0xFF, 0x90};
    size_t at = 0;
    for (size_t i = 0; i + sizeof pat + 4 <= sizeof code && !at; ++i) if (std::memcmp(code + i, pat, sizeof pat) == 0) at = i + sizeof pat;
    if (!at) { g_b.why = "the binding's console call not found"; return; }
    int32_t slot = 0;
    std::memcpy(&slot, code + at, 4);
    if (slot <= 0 || slot > 0x400 || (slot & 7)) { g_b.why = "the console call's slot is implausible"; return; }
    g_b.conExecSlot = static_cast<size_t>(slot);
    logf("WO216-BRAINS legacy PauseNPC console slot +0x%zX resolved from ExecuteCommand", g_b.conExecSlot);
}

bool b_exec(const char* cmd, bool ownedCleanup = false) {
    if ((!legacy_active() && !ownedCleanup) || !g_b.conExecSlot) return false;
    void* con = engine::console();
    if (!con) return false;
    void* vt = *reinterpret_cast<void**>(con);
    auto fn = reinterpret_cast<void (*)(void*, const char*, bool, bool)>(reinterpret_cast<void**>(vt)[g_b.conExecSlot / 8]);
    fn(con, cmd, false, false); return true;
}

bool p5_allow_hide(uint32_t eid);
void p5_watch(uint32_t eid);
// The engine's own Hide, unguarded (see above), on a freshly resolved entity.
bool b_hide(uint32_t eid, bool hide) {
    if (!g_b.armed || (hide && !p5_allow_hide(eid))) return false;
    void* e = engine::entity_by_id(eid);
    if (!e) return false;
    void* vt = *reinterpret_cast<void**>(e);
    auto fn = reinterpret_cast<void (*)(void*, bool)>(reinterpret_cast<void**>(vt)[g_b.hideSlot / 8]);
    fn(e, hide);
    if (hide) p5_watch(eid);
    return true;
}

bool fighting_player(uint32_t eid) {
    motion::NpcCombat c;
    if (!motion::read_npc_combat(eid, &c)) return false;
    return wo216rules::fighting_a_player(c.opponentIsPlayer != 0, c.opponentEid && motion::is_avatar_eid(c.opponentEid));
}

bool ours(const char* name) {
    if (!name || !*name || std::strncmp(name, "kcd2mp", 6) == 0 || std::strncmp(name, "kcdmp", 5) == 0) return true;
    // WO-217 0.1 (dialog_twins): the engine's dialogue twins are its conversation's actors -- never hidden, frozen or counted here
    return wo217rules::engine_dialog_twin(name) && wo217::on(wo217rules::kBitDialogTwins);
}

struct BBody {
    std::string name;
    wo216authority::HideLease visibility;
    p5corpse::State corpse;
    bool wantHide = false, bound = false, inNearRange = false, ready = false, lifeKnown = false, dead = false, exemptNow = false;
    double pauseAt = -1e9;
    wo216authority::PauseLease pause;
    void* pauseEntity = nullptr;
    uint64_t wuid = 0;
    int brainState = -1;
    bool fightPlayer = false;  // its opponent is a player this census: the hide sweep must leave it shown
    bool keptFight = false;    // already logged that this fight kept it on screen
    double spawnAt = -1;       // a local spawn: when it was seen (its brain may come a frame or two later)
    int kind = 0;
};
struct PauseCleanup { wo216authority::PauseLease lease; void* entity = nullptr; std::string name; bool waitingLogged = false; };
std::deque<PauseCleanup> g_pauseCleanup;
uint64_t g_bodyGeneration = 0;
std::unordered_map<uint32_t, BBody> g_bodies;           // main thread
std::atomic<uint64_t> g_p5Visible{0}, g_p5Blocked{0}, g_p5World{0};
std::deque<uint32_t> g_p5Watch;
std::unordered_set<uint32_t> g_p5Watching;
void p5_watch(uint32_t eid) { if (g_p5Watching.insert(eid).second) g_p5Watch.push_back(eid); }
void p5_retire(BBody& b) {
    if (b.corpse.visible && g_p5Visible.load()) g_p5Visible.fetch_sub(1);
    b.corpse = {};
}
void p5_capture(BBody& b, void* e, void* soul, uint64_t wuid, bool known, bool dead) {
    uint32_t flags = 0;
    KCDMP_FAULT_READ(site, "wo216::corpse visibility");
    const bool visibilityKnown = (dead || b.corpse.confirmed) && g_b.armed && e && fault::guarded(site, [&] { std::memcpy(&flags, static_cast<const char*>(e) + 8, 4); });
    const bool before = b.corpse.visible;
    void* canonicalSoul = soul ? buffs::as_c_soul(soul) : nullptr;
    const p5corpse::Identity identity{reinterpret_cast<uintptr_t>(e), reinterpret_cast<uintptr_t>(canonicalSoul ? canonicalSoul : soul), wuid, lifecycle_generation()};
    if (b.corpse.identity.valid() && !b.corpse.identity.same(identity)) {
        b.wantHide = false;
        if (b.visibility.hidden && c_npcHidden.load()) c_npcHidden.fetch_sub(1);
        b.visibility.applied(false, GetTickCount64()); // an old lease never shows/hides a replacement
    }
    b.corpse.observe(identity, known, dead, visibilityKnown, (flags & 0x10) != 0);
    if (before != b.corpse.visible) { if (b.corpse.visible) g_p5Visible.fetch_add(1); else if (g_p5Visible.load()) g_p5Visible.fetch_sub(1); }
    g_p5World.store(lifecycle_generation());
    b.lifeKnown = known; if (known) b.dead = dead;
}
void p5_read(uint32_t eid, BBody& b) {
    void* e = engine::entity_by_id(eid);
    void* soul = e ? hits::soul_of_eid(eid) : nullptr;
    bool dead = false; const bool known = soul && rttr::soul_bool(soul, "IsDead", &dead);
    p5_capture(b, e, soul, e ? actions::entity_wuid(e) : 0, known, dead);
}
void p5_preserve(uint32_t eid, BBody& b) {
    if (!b.corpse.needs_show()) return;
    b.wantHide = false;
    if (b.pause.owned) { g_pauseCleanup.push_back({b.pause, b.pauseEntity, b.name, false}); b.pause.forget(); }
    if (b.visibility.hidden) {
        if (g_p5Blocked.load() < uint64_t(INT64_MAX)) g_p5Blocked.fetch_add(1);
        b.visibility.applied(false, GetTickCount64());
        if (c_npcHidden.load()) c_npcHidden.fetch_sub(1);
    }
    // Cleanup uses only our exact legacy pause lease; never acquire a new pause
    // or touch foreign suspend bits. The existing death path owns its physics.
    p5corpse::reveal(b.corpse, [&] { return b_hide(eid, false); }, [&] { p5_read(eid, b); });
}
bool p5_allow_hide(uint32_t eid) {
    auto it = g_bodies.find(eid);
    if (it == g_bodies.end()) return false;
    BBody& b = it->second; p5_read(eid, b);
    uint64_t blocked = g_p5Blocked.load();
    const bool allowed = p5corpse::hide(b.corpse, [] {}, blocked);
    g_p5Blocked.store(blocked);
    if (!allowed) p5_preserve(eid, b);
    return allowed;
}
void p5_tick() {
    size_t budget = std::min<size_t>(8, g_p5Watch.size());
    while (budget--) {
        const uint32_t eid = g_p5Watch.front(); g_p5Watch.pop_front();
        auto it = g_bodies.find(eid);
        if (it == g_bodies.end()) { g_p5Watching.erase(eid); continue; }
        BBody& b = it->second; p5_read(eid, b); p5_preserve(eid, b);
        if (b.corpse.confirmed || b.visibility.hidden) g_p5Watch.push_back(eid);
        else g_p5Watching.erase(eid);
    }
}
std::unordered_map<std::string, double> g_exempt;       // name -> until (s)
std::vector<uint32_t> g_pauseQ, g_hideQ;       // main thread: the actions, at most kPerFrame a frame
constexpr int kPerFrame = 24;
std::mutex g_spawnMu;
std::vector<uint32_t> g_spawned;                        // the spawn watch's new entities (any thread -> main)
std::atomic<bool> g_spawnWatch{false};
std::atomic<void*> g_origSpawn{nullptr};
bool g_spawnHooked = false;
const char* g_spawnWhy = "not installed";
bool g_active = false;
bool g_act = false;                                     // no_brains on: the census acts (off: the twin -- it only counts)
double g_censusAt = 0, g_activeSince = 0;
bool g_swept = false;                                   // first complete census in this lifecycle
struct Census { uint32_t samples = 0, nonzero = 0, maxRunning = 0, lastRunning = 0, lastExemptRunning = 0, lastBodies = 0, lastHidden = 0, lastBound = 0;
                uint32_t nearNonzero = 0, maxNearRunning = 0, lastNearRunning = 0, lastNear = 0, lastDead = 0;
                double lastCostMs = 0, maxCostMs = 0; } g_cen;
std::atomic<uint32_t> c_bPauses{0}, c_bHides{0}, c_bShows{0}, c_bLocalSpawns{0}, c_bBreaches{0}, c_bSweepPaused{0}, c_bSweepHidden{0},
    c_bExemptRunning{0}, c_bSpawnSeen{0}, c_bFightKept{0};
std::string g_lastNames;                                // the newest non-zero sample's bodies (the briefing names them)

void* spawn_detour(void* es, void* params, bool autoInit) {
    auto fn = reinterpret_cast<void* (*)(void*, void*, bool)>(g_origSpawn.load(std::memory_order_relaxed));
    void* e = fn(es, params, autoInit);   // the engine's own spawn first, outside any guard of ours
    if (e && g_spawnWatch.load(std::memory_order_relaxed)) {
        const uint32_t id = engine::entity_id(e);   // a guarded getter on the new entity
        if (id) {
            c_bSpawnSeen.fetch_add(1, std::memory_order_relaxed);
            std::lock_guard<std::mutex> l(g_spawnMu);
            if (g_spawned.size() < 4096) g_spawned.push_back(id);
        }
    }
    return e;
}

// The entity system's interface slot SpawnEntity (+0x60, the one engine.cpp spawns with) is wrapped: the vftable's pointer swapped once
// (one aligned store; never put back -- the wrapper checks the switch). Main thread.
void b_hook_spawn() {
    if (g_spawnHooked) return;
    g_spawnHooked = true;
    void* es = engine::entity_system();
    if (!es) { g_spawnWhy = "no entity system"; g_spawnHooked = false; return; }
    void** vt = *reinterpret_cast<void***>(es);
    void** slot = vt + 0x60 / 8;
    void* orig = *slot;
    HMODULE m = nullptr;
    if (!GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, static_cast<LPCSTR>(orig), &m) ||
        m != GetModuleHandleA("CryEntitySystem.dll")) { g_spawnWhy = "SpawnEntity's slot does not point into CryEntitySystem"; return; }
    g_origSpawn.store(orig);
    DWORD old = 0;
    if (!VirtualProtect(slot, sizeof(void*), PAGE_READWRITE, &old)) { g_spawnWhy = "the vftable could not be made writable"; return; }
    InterlockedExchangePointer(reinterpret_cast<PVOID volatile*>(slot), reinterpret_cast<PVOID>(&spawn_detour));
    DWORD tmp = 0;
    VirtualProtect(slot, sizeof(void*), old, &tmp);
    g_spawnWhy = "armed";
    logf("WO216-BRAINS the spawn watch is in: the entity system's SpawnEntity (+0x60) wrapped -- a body this engine spawns on its own is "
         "hidden in the frame it appears and decisions gated before the brain update");
}

// WO-217 B.4 (layer_watch): the entity system's EnableLayer (the function that logs its own "EnableLayer '%s', enable:%c, serialized:%c";
// its vftable slot found at arm time, +0x250 in the bench's build) is wrapped like SpawnEntity: after the engine's own call, a layer switched
// on is noted, and the census scans every entity at once and each second for kLayerBurstS -- visibility still requires full coverage
// at its activation, not at the next 30 s net (WO-216: eight bodies of another village switched on with their layer, found by the net).
constexpr double kLayerBurstS = 30.0;   // a1-b7: a village's bodies started running 16 s after their layer came on
std::unordered_map<uint32_t, double> g_layerBorn;   // WO-217 B.4 (main thread): bodies a layer burst's scan first listed, and when
std::mutex g_layerMu;
std::vector<std::string> g_layersOn;                     // (any thread -> main) the layers switched on since the last census
std::atomic<void*> g_origLayer{nullptr};
std::atomic<bool> g_layerWatch{false};
bool g_layerHooked = false;
const char* g_layerWhy = "not installed";
uint32_t g_layerSlot = 0;
double g_layerBurstUntil = -1e9;
bool g_censusNow = false;
std::atomic<uint32_t> c_layerOn{0}, c_layerScans{0}, c_layerFound{0}, c_netFound{0}, c_netScans{0};
void layer_detour(void* es, const char* layer, bool enable, bool serialized) {
    auto fn = reinterpret_cast<void (*)(void*, const char*, bool, bool)>(g_origLayer.load(std::memory_order_relaxed));
    fn(es, layer, enable, serialized);   // the engine's own call first, outside any guard of ours
    if (enable && g_layerWatch.load(std::memory_order_relaxed)) {
        std::lock_guard<std::mutex> l(g_layerMu);
        if (g_layersOn.size() < 64) g_layersOn.emplace_back(layer ? layer : "?");
    }
}
void b_hook_layer() {
    if (g_layerHooked) return;
    g_layerHooked = true;
    void* es = engine::entity_system();
    HMODULE m = GetModuleHandleA("CryEntitySystem.dll");
    if (!es || !m) { g_layerWhy = "no entity system"; g_layerHooked = false; return; }
    int cnt = 0;
    const uint8_t* fn = anchor::function_by_string(m, "EnableLayer '%s', enable:%c, serialized:%c", &cnt);
    if (!fn || cnt != 1) { g_layerWhy = "EnableLayer (its own log line) not found once"; return; }
    void** vt = *reinterpret_cast<void***>(es);
    int slot = -1;
    for (int i = 0; i < 160; ++i) if (vt[i] == fn) { if (slot >= 0) { slot = -2; break; } slot = i; }
    if (slot < 0) { g_layerWhy = slot == -2 ? "EnableLayer sits in two slots" : "EnableLayer is not a slot of the entity system"; return; }
    void** sp = vt + slot;
    g_origLayer.store(*sp);
    DWORD old = 0;
    if (!VirtualProtect(sp, sizeof(void*), PAGE_READWRITE, &old)) { g_layerWhy = "the vftable could not be made writable"; return; }
    InterlockedExchangePointer(reinterpret_cast<PVOID volatile*>(sp), reinterpret_cast<PVOID>(&layer_detour));
    DWORD tmp = 0;
    VirtualProtect(sp, sizeof(void*), old, &tmp);
    g_layerSlot = static_cast<uint32_t>(slot) * 8;
    g_layerWhy = "armed";
    logf("WO217-LAYER the layer watch is in: the entity system's EnableLayer (+0x%X) wrapped -- a layer switched on is scanned at once and "
         "each second for %.0f s (visibility still requires initialized living bodies and complete nearby coverage)", g_layerSlot, kLayerBurstS);
}

bool exempt(const std::string& name, double now) {
    auto it = g_exempt.find(name);
    if (it == g_exempt.end()) return false;
    if (now > it->second) { g_exempt.erase(it); return false; }
    return true;
}

// These leases can outlive the active legacy mode; cleanup remains main-thread
// only, lookup by EID then validate entity identity/WUID, never dereference an old
// saved entity pointer. A foreign suspend mask is left untouched and retried.
void b_cleanup_pauses() {
    size_t budget = g_pauseCleanup.size() < 8 ? g_pauseCleanup.size() : 8;
    while (budget--) {
        PauseCleanup p = std::move(g_pauseCleanup.front()); g_pauseCleanup.pop_front();
        void* e = engine::entity_by_id(p.lease.eid);
        const char* name = e ? engine::entity_name(e) : nullptr;
        if (!e || e != p.entity || !name || p.name != name || actions::entity_wuid(e) != p.lease.wuid) continue;
        uint64_t w = 0; int st = -1, mask = -1;
        if (!actions::brain_state(e, &w, &st, &mask) || st < 0 || mask < 0) { g_pauseCleanup.push_back(std::move(p)); continue; }
        if (!p.lease.same_owner(p.lease.eid, w, p.lease.generation) || (mask & 0x08) == 0) continue;
        if (!p.lease.may_resume(p.lease.eid, w, p.lease.generation, st, mask)) {
            if (!p.waitingLogged) { p.waitingLogged = true; logf("WO216-LEGACY cleanup waits npc=%s generation=%llu mask=0x%X: another context remains, no ResumeNPC", p.name.c_str(), static_cast<unsigned long long>(p.lease.generation), mask); }
            g_pauseCleanup.push_back(std::move(p)); continue;
        }
        char cmd[96]; std::snprintf(cmd, sizeof cmd, "wh_ai_ResumeNPC %s", p.name.c_str());
        if (b_exec(cmd, true)) logf("WO216-LEGACY owned pause released npc=%s generation=%llu eid=0x%X", p.name.c_str(), static_cast<unsigned long long>(p.lease.generation), p.lease.eid);
        else g_pauseCleanup.push_back(std::move(p));
    }
}

void b_queue_pause(uint32_t eid, BBody& b, double now) {
    if (!legacy_active() || now - b.pauseAt < 2.0) return;
    b.pauseAt = now; g_pauseQ.push_back(eid);
}

struct CensusCtx { double now; bool layerOn = false; uint32_t layerCaught = 0; std::string layerNames; uint32_t bodies = 0, running = 0, exemptRunning = 0, hidden = 0, bound = 0, nearRunning = 0, nearN = 0, nearBound = 0, deadRunning = 0; std::string names;
                   const std::unordered_set<uint32_t>* fresh = nullptr; uint32_t freshRunning = 0; std::string freshNames;
                   std::unordered_set<uint32_t> seen; bool havePlayer = false; float player[3]{}; };
bool census_visit(void* e, void* ctx) {
    auto* c = static_cast<CensusCtx*>(ctx);
    const int kind = npcscan::body_kind(e);
    if (kind < 0) return false;
    const char* nm = engine::entity_name(e);
    if (ours(nm)) return false;
    const uint32_t eid = engine::entity_id(e);
    if (!eid) return false;
    c->seen.insert(eid);
    BBody& b = g_bodies[eid];
    uint64_t w = 0; int st = -1, mask = -1;
    const bool brain = actions::brain_state(e, &w, &st, &mask) && w && st >= 0 && mask >= 0;
    // EIDs can be reused. An expired hide lease never migrates to a different body.
    if ((!b.name.empty() && b.name != nm) || (b.wuid && w && b.wuid != w)) { p5_retire(b); b = BBody{}; }
    b.name = nm; b.kind = kind; b.wuid = w; b.brainState = st;
    b.bound = npcdrive::is_bound_eid(eid);
    b.exemptNow = exempt(b.name, c->now);
    b.inNearRange = false;
    if (c->havePlayer) {
        float q[3];
        if (engine::entity_world_pos(e, q)) {
            const float dx = q[0] - c->player[0], dy = q[1] - c->player[1], dz = q[2] - c->player[2];
            b.inNearRange = dx * dx + dy * dy + dz * dz <= 150.0f * 150.0f;
        }
    }
    b.dead = false;
    void* soul = hits::soul_of_eid(eid);
    b.lifeKnown = soul && rttr::soul_bool(soul, "IsDead", &b.dead);
    // Reuse this census' existing life/WUID reads. No second per-world soul pass.
    p5_capture(b, e, soul, w, b.lifeKnown, b.dead); p5_preserve(eid, b);
    if (b.corpse.confirmed) p5_watch(eid);
    // No verified reconstruction sub-entry exists in UpdateIntellects. A complete
    // brain/life read plus the global post-load settle is the conservative proxy;
    // a newly observed spawn also receives its own five-second initialization window.
    b.ready = brain && (b.spawnAt < 0 || c->now - b.spawnAt >= 5.0);
    if (b.ready && b.lifeKnown && !b.dead) {
        if (b.bound) ++c->bound;
        if (b.inNearRange) { ++c->nearN; if (b.bound) ++c->nearBound; }
    }
    b.fightPlayer = fighting_player(eid);
    if (!b.fightPlayer) b.keptFight = false;
    if (brain) ++c->bodies;
    if (!brain || (b.lifeKnown && b.dead)) { if (brain && st == 0 && b.dead) ++c->deadRunning; return false; }
    if (wo216rules::breach(st, decisions_blocked())) {
        if (b.exemptNow) ++c->exemptRunning;
        else if (c->layerOn && g_layerBorn.count(eid) && c->now - g_layerBorn[eid] <= kLayerBurstS) {
            ++c->layerCaught;
            if (c->layerNames.size() < 300) { if (!c->layerNames.empty()) c->layerNames += ","; c->layerNames += b.name; }
            if (g_act && b.ready && b.lifeKnown) b_queue_pause(eid, b, c->now);
        } else {
            ++c->running;
            if (b.inNearRange) ++c->nearRunning;
            if (c->names.size() < 600) { if (!c->names.empty()) c->names += ","; c->names += b.name + (b.bound ? "(bound)" : "(unbound)"); }
            if (c->fresh && c->fresh->count(eid)) { ++c->freshRunning; if (c->freshNames.size() < 300) { if (!c->freshNames.empty()) c->freshNames += ","; c->freshNames += b.name; } }
            if (g_act && b.ready && b.lifeKnown) b_queue_pause(eid, b, c->now);
        }
    }
    return false;
}

// Second pass only: the ratio must describe the complete nearby census, never
// the order in which its entities happened to be visited.
void b_visibility(CensusCtx& c) {
    const bool covered = wo216authority::coverage(c.nearN, c.nearBound, c.havePlayer);
    c_npcNear.store(c.nearN); c_npcBound.store(c.nearBound); c_hideFailsafe.store(covered ? 0 : 1);
    const uint64_t ms = GetTickCount64();
    for (uint32_t eid : c.seen) {
        BBody& b = g_bodies[eid];
        const bool eligible = g_act && wo216authority::hide_eligible(b.ready, b.lifeKnown, b.dead, b.inNearRange,
            b.fightPlayer, b.exemptNow, wo216rules::streamable_name(b.name.size()));
        const bool wasExpired = b.visibility.expired;
        b.wantHide = b.visibility.desired(b.bound, eligible, covered, ms);
        if (!b.wantHide && b.visibility.hidden) {
            if (b_hide(eid, false)) { if (!wasExpired && b.visibility.expired) c_unhidden10s.fetch_add(1); b.visibility.applied(false, ms); c_bShows.fetch_add(1); if (c_npcHidden.load()) c_npcHidden.fetch_sub(1); }
        } else if (b.wantHide && !b.visibility.hidden) g_hideQ.push_back(eid);
        if (b.visibility.hidden) ++c.hidden;
    }
}

std::vector<uint32_t> g_npcList;   // every NPC body with a brain here (the full scan's; local spawns added as they come)
double g_listAt = -1e9;
bool list_visit(void* e, void* ctx) {
    if (npcscan::body_kind(e) < 0) return false;
    const char* nm = engine::entity_name(e);
    if (ours(nm)) return false;
    const uint32_t eid = engine::entity_id(e);
    if (eid) static_cast<std::vector<uint32_t>*>(ctx)->push_back(eid);
    return false;
}

void b_census(double now) {
    CensusCtx c;
    c.now = now;
    if (void* pe = engine::entity_by_id(0x7777)) c.havePlayer = engine::entity_world_pos(pe, c.player);
    const double t0 = now_s();
    // the full entity scan (~9 ms on the bench) at the join's sweep and then every 30 s as a net; between, the spawn watch adds every
    // body this engine spawns and a body gone drops out by itself (its id no longer answers)
    // WO-217 B.4: the layers switched on since the last census start a burst of full scans (this one and each second for kLayerBurstS)
    {
        std::vector<std::string> on;
        { std::lock_guard<std::mutex> l(g_layerMu); on.swap(g_layersOn); }
        for (const auto& ln : on) {
            g_layerBurstUntil = now + kLayerBurstS;
            const uint32_t k = c_layerOn.fetch_add(1, std::memory_order_relaxed) + 1;
            if (k <= 200 || k % 50 == 0) logf("WO217-LAYER on layer=%s -- its bodies scanned now and each second for %.0f s [n=%u]", ln.c_str(), kLayerBurstS, k);
        }
    }
    const bool layerScan = wo217::on(wo217rules::kBitLayerWatch) && now < g_layerBurstUntil && g_swept;
    const bool netScan = now - g_listAt >= 30.0 || !g_swept;
    std::unordered_set<uint32_t> fresh;
    if (netScan || layerScan) {
        if (netScan) g_listAt = now;
        std::vector<uint32_t> v;
        engine::for_each_entity(&list_visit, &v);
        if (g_swept) {
            std::unordered_set<uint32_t> was(g_npcList.begin(), g_npcList.end());
            for (uint32_t id : v) if (!was.count(id)) { fresh.insert(id); if (layerScan) g_layerBorn[id] = now; }
        }
        g_npcList.swap(v);
        (layerScan ? c_layerScans : c_netScans).fetch_add(1, std::memory_order_relaxed);
    }
    c.fresh = fresh.empty() ? nullptr : &fresh;
    c.layerOn = layerScan || now < g_layerBurstUntil + kLayerBurstS;
    for (auto it = g_layerBorn.begin(); it != g_layerBorn.end();) { if (now - it->second > kLayerBurstS) it = g_layerBorn.erase(it); else ++it; }
    for (uint32_t eid : g_npcList) if (void* e = engine::entity_by_id(eid)) census_visit(e, &c);
    b_visibility(c);
    // WO-217 B.4: bodies a layer switched on whose brains run inside the layer's watch -- caught at the activation (not the census's misses)
    if (c.layerCaught) {
        const uint32_t k = c_layerFound.fetch_add(c.layerCaught, std::memory_order_relaxed) + c.layerCaught;
        logf("WO217-LAYER found %u running at a layer's activation: %s -- %s [n=%u]", c.layerCaught, c.layerNames.c_str(),
             legacy_active() && g_act ? "legacy pause requested" : decisions_blocked() ? "decisions gated" : "left running", k);
    }
    // ... and bodies running that the list did not hold before this scan (a layer scan's, else the 30 s net's: missed)
    if (c.freshRunning) {
        if (layerScan) {
            const uint32_t k = c_layerFound.fetch_add(c.freshRunning, std::memory_order_relaxed) + c.freshRunning;
            logf("WO217-LAYER found %u running at a layer's activation: %s -- %s [n=%u]", c.freshRunning, c.freshNames.c_str(),
                 legacy_active() && g_act ? "legacy pause requested" : decisions_blocked() ? "decisions gated" : "left running", k);
        } else {
            const uint32_t k = c_netFound.fetch_add(c.freshRunning, std::memory_order_relaxed) + c.freshRunning;
            logf("WO217-LAYER the 30 s net found %u running the list did not hold: %s [n=%u]", c.freshRunning, c.freshNames.c_str(), k);
        }
    }
    const double ms = (now_s() - t0) * 1000.0;
    // bodies gone since the last census: forgotten
    for (auto it = g_bodies.begin(); it != g_bodies.end();) {
        if (!c.seen.count(it->first)) {
            BBody& gone = it->second;
            if (gone.pause.owned && gone.pause.generation == g_bodyGeneration) g_pauseCleanup.push_back({gone.pause, gone.pauseEntity, gone.name, false});
            if (it->second.visibility.hidden) { if (b_hide(it->first, false)) c_bShows.fetch_add(1); if (c_npcHidden.load()) c_npcHidden.fetch_sub(1); }
            p5_retire(gone);
            it = g_bodies.erase(it);
        } else ++it;
    }
    if (!g_swept) {
        g_swept = true;
        c_bSweepPaused.store(static_cast<uint32_t>(g_pauseQ.size()));
        c_bSweepHidden.store(static_cast<uint32_t>(g_hideQ.size()));
        logf("WO216-BRAINS the join's sweep: %u bodies with a brain here, %u running (%s), %u not streamed by the host (%s), %u bound, census %.1f ms",
             c.bodies, c.running, g_act ? "decision batch gated" : "left running: no_brains off", static_cast<uint32_t>(g_hideQ.size()),
             g_act ? "hidden" : "left shown", c.bound, ms);
        return;   // the contract's samples start after the sweep
    }
    ++g_cen.samples;
    g_cen.lastRunning = c.running; g_cen.lastExemptRunning = c.exemptRunning; g_cen.lastBodies = c.bodies; g_cen.lastHidden = c.hidden; g_cen.lastBound = c.bound;
    g_cen.lastCostMs = ms;
    if (ms > g_cen.maxCostMs) g_cen.maxCostMs = ms;
    g_cen.lastNearRunning = c.nearRunning; g_cen.lastNear = c.nearN; g_cen.lastDead = c.deadRunning;
    if (c.nearRunning) { ++g_cen.nearNonzero; if (c.nearRunning > g_cen.maxNearRunning) g_cen.maxNearRunning = c.nearRunning; }
    c_bExemptRunning.fetch_add(c.exemptRunning, std::memory_order_relaxed);
    if (c.running) {
        ++g_cen.nonzero;
        c_bBreaches.fetch_add(c.running, std::memory_order_relaxed);
        if (c.running > g_cen.maxRunning) g_cen.maxRunning = c.running;
        g_lastNames = c.names;
        if (g_cen.nonzero <= 200 || g_cen.nonzero % 50 == 0)
            logf("WO216-BRAINS sample=%u running=%u (not exempt; within 150 m %u of %u) exempt_running=%u bodies=%u hidden=%u bound=%u: %s -- %s [nonzero n=%u]",
                 g_cen.samples, c.running, c.nearRunning, c.nearN, c.exemptRunning, c.bodies, c.hidden, c.bound, c.names.c_str(),
                 g_act ? "decision gate unavailable" : "left running (no_brains off: the twin)", g_cen.nonzero);
    }
}

void b_spawns(double now) {
    std::vector<uint32_t> in;
    {
        std::lock_guard<std::mutex> l(g_spawnMu);
        in.swap(g_spawned);
    }
    for (uint32_t eid : in) {
        void* e = engine::entity_by_id(eid);
        if (!e) continue;
        const int kind = npcscan::body_kind(e);
        if (kind < 0) continue;
        const char* nm = engine::entity_name(e);
        if (ours(nm)) continue;
        BBody& b = g_bodies[eid];
        if (b.visibility.hidden && c_npcHidden.load()) c_npcHidden.fetch_sub(1);
        p5_retire(b); b = BBody{};
        b.name = nm ? nm : "?";
        b.kind = kind;
        b.spawnAt = now;
        if (std::find(g_npcList.begin(), g_npcList.end(), eid) == g_npcList.end()) g_npcList.push_back(eid);
        const uint32_t k = c_bLocalSpawns.fetch_add(1, std::memory_order_relaxed) + 1;
        if (k <= 200 || k % 50 == 0)
            logf("WO216-LOCALSPAWN npc=%s eid=0x%X: stays shown until initialization and a complete coverage census [n=%u]", b.name.c_str(), eid, k);
    }
    // Every hidden lease is checked each frame; its release cannot wait for the
    // next one-second census or sit behind the hide-action budget.
    const uint64_t ms = GetTickCount64();
    for (auto& kv : g_bodies) {
        BBody& b = kv.second;
        if (b.visibility.hidden) {
            const bool boundNow = npcdrive::is_bound_eid(kv.first);
            const bool timedOut = ms >= b.visibility.hiddenAtMs && ms - b.visibility.hiddenAtMs >= wo216authority::kUnboundHideMs;
            if (boundNow || timedOut) {
                b.visibility.expired = !boundNow; b.wantHide = false;
                if (b_hide(kv.first, false)) {
                    b.visibility.applied(false, ms); if (!boundNow) c_unhidden10s.fetch_add(1); c_bShows.fetch_add(1); if (c_npcHidden.load()) c_npcHidden.fetch_sub(1);
                }
            }
        }
        if (b.spawnAt < 0) continue;
        if (now - b.spawnAt >= 5.0) { b.spawnAt = -1; continue; }
        void* e = engine::entity_by_id(kv.first);
        if (!e) { b.spawnAt = -1; continue; }
        uint64_t w = 0; int st = -1, mask = -1;
        if (!actions::brain_state(e, &w, &st, &mask) || st < 0) continue;
        if (g_act && st == 0 && !exempt(b.name, now)) b_queue_pause(kv.first, b, now);
    }
}

void b_actions() {
    int budget = kPerFrame;
    char cmd[96];
    p5_tick();
    while (budget > 0 && !g_pauseQ.empty()) {
        const uint32_t eid = g_pauseQ.back(); g_pauseQ.pop_back();
        auto it = g_bodies.find(eid);
        if (!legacy_active() || it == g_bodies.end() || !it->second.ready || !it->second.lifeKnown || it->second.dead || it->second.exemptNow) continue;
        BBody& b = it->second; void* e = engine::entity_by_id(eid);
        const char* liveName = e ? engine::entity_name(e) : nullptr;
        uint64_t w = 0; int st = -1, mask = -1;
        wo216authority::PauseLease lease;
        p5_read(eid, b); p5_preserve(eid, b);
        if (b.corpse.refuses_hide()) continue;
        if (!e || !liveName || b.name != liveName || !actions::brain_state(e, &w, &st, &mask) ||
            !lease.claim(eid, w, g_bodyGeneration, st, mask)) continue;
        std::snprintf(cmd, sizeof cmd, "wh_ai_PauseNPC %s", b.name.c_str());
        if (b_exec(cmd)) { b.pause = lease; b.pauseEntity = e; c_bPauses.fetch_add(1, std::memory_order_relaxed); }
        --budget;
    }
    while (budget > 0 && !g_hideQ.empty()) {
        const uint32_t eid = g_hideQ.back(); g_hideQ.pop_back();
        auto it = g_bodies.find(eid);
        if (it == g_bodies.end()) continue;
        BBody& b = it->second;
        if ((!authority_active() && !legacy_active()) || c_hideFailsafe.load() || !b.wantHide || b.visibility.hidden || npcdrive::is_bound_eid(eid)) continue;
        // A queued intent can outlive a death or unfinished initialization. Re-read
        // those facts immediately before Hide; unknown means leave it visible.
        void* e = engine::entity_by_id(eid); bool dead = false; void* soul = hits::soul_of_eid(eid);
        uint64_t w = 0; int st = -1, mask = -1;
        const char* liveName = e ? engine::entity_name(e) : nullptr;
        if (!p5_allow_hide(eid)) continue;
        if (!e || !liveName || b.name != liveName || !soul || !rttr::soul_bool(soul, "IsDead", &dead) || dead ||
            !actions::brain_state(e, &w, &st, &mask) || !w || st < 0 || mask < 0 || w != b.wuid || fighting_player(eid)) continue;
        if (b_hide(eid, true)) { b.visibility.applied(true, GetTickCount64()); c_bHides.fetch_add(1); c_npcHidden.fetch_add(1); }
        --budget;
    }
}

// A world/role switch releases visibility; the decision gate reads role atomically.
void b_stop(const char* why) {
    uint32_t shown = 0;
    for (auto& kv : g_bodies) {
        BBody& b = kv.second;
        if (b.pause.owned && b.pause.generation == g_bodyGeneration) g_pauseCleanup.push_back({b.pause, b.pauseEntity, b.name, false});
        if (b.visibility.hidden) { if (b_hide(kv.first, false)) ++shown; b.visibility.hidden = false; }
    }
    b_cleanup_pauses();
    g_bodies.clear();
    g_p5Visible.store(0); g_p5Watch.clear(); g_p5Watching.clear();
    c_npcHidden.store(0); c_npcNear.store(0); c_npcBound.store(0); c_hideFailsafe.store(1);
    g_pauseQ.clear(); g_hideQ.clear();
    g_exempt.clear();
    { std::lock_guard<std::mutex> l(g_spawnMu); g_spawned.clear(); }
    { std::lock_guard<std::mutex> l(g_layerMu); g_layersOn.clear(); }
    g_layerBorn.clear();
    g_layerBurstUntil = 0;
    g_lastNames.clear();
    g_spawnWatch.store(false);
    g_layerWatch.store(false);
    g_swept = false;
    g_cen = Census{};
    g_npcList.clear();
    g_listAt = -1e9;
    logf("WO216-BRAINS off (%s): %u hidden bodies shown again", why, shown);
}

void b_tick(double now) {
    b_cleanup_pauses();
    const bool want = authority_active() || legacy_active();
    static uint64_t censusGeneration = 0;
    const uint64_t generation = g_authorityGeneration.load(std::memory_order_acquire);
    if (generation != censusGeneration) {
        if (g_active) { b_stop("new lifecycle generation"); g_active = false; }
        censusGeneration = generation;
    }
    if (!want) {
        if (g_active) { g_active = false; b_stop("not a joiner in the host's world now"); }
        return;
    }
    const bool act = on(kBitNoBrains);
    if (g_active && g_act && !act) { b_stop("the switch"); g_active = false; }
    g_act = act;
    if (!g_active) {
        b_resolve();
        if (legacy_active()) b_legacy_resolve();
        if (!g_b.armed) {
            static bool s_told = false;
            if (!s_told) { s_told = true; logf("WO216-BRAINS visibility NOT armed: %s -- decision gate remains independent", g_b.why); }
            return;
        }
        g_active = true;
        g_bodyGeneration = g_authorityGeneration.load(std::memory_order_acquire);
        g_activeSince = now;
        g_censusAt = 0;
        b_hook_spawn();
        g_spawnWatch.store(true);
        b_hook_layer();
        g_layerWatch.store(true);
    }
    b_spawns(now);
    bool layerNow = false;
    if (wo217::on(wo217rules::kBitLayerWatch)) { std::lock_guard<std::mutex> l(g_layerMu); layerNow = !g_layersOn.empty(); }
    if (now - g_censusAt >= wo216rules::kBrainCensusS || layerNow) { g_censusAt = now; b_census(now); }   // WO-217 B.4: a layer on: at once
    b_actions();
}

void b_exempt(const char* name, bool on, double forS) {
    // the pipe's main-thread path runs this (op 165 is a main-thread op)
    if (!name || !*name) return;
    if (on) g_exempt[name] = now_s() + forS; else g_exempt.erase(name);
    static uint32_t s_n = 0;
    if (++s_n <= 100 || s_n % 100 == 0) logf("WO216-BRAINS exempt npc=%s %s%s [n=%u]", name, on ? "on for " : "off", on ? std::to_string(static_cast<int>(forS)).append(" s").c_str() : "", s_n);
}

int b_status(char* out, int n) {
    if (!out || n <= 0) return 0;
    const wo216authority::Stats stats{c_gateArmedAtS.load(), c_decisionBlocked.load(), c_blockedDuringLoad.load(), c_npcNear.load(), c_npcBound.load(), c_npcHidden.load(), c_hideFailsafe.load(), c_unhidden10s.load()};
    const int prefix = wo216authority::stats_prefix(out, static_cast<size_t>(n), stats);
    if (prefix < 0 || prefix >= n) return n > 0 ? n - 1 : 0;
    return prefix + std::snprintf(out + prefix, static_cast<size_t>(n - prefix),
        "B decision_gate=%s story_gate=%s authority_active=%d authority_enabled=%d authority_generation=%llu authority_phase=%u decision_reason=%s no_brains=%s role=%u active=%d (%s; spawn watch %s) samples=%u nonzero=%u max_running=%u last_running=%u near_nonzero=%u max_near_running=%u "
        "last_near_running=%u last_near=%u last_dead_with_brain=%u last_exempt_running=%u bodies=%u list=%zu "
        "hidden=%u bound=%u census_ms=%.1f max_ms=%.1f sweep_paused=%u sweep_hidden=%u pauses=%u hides=%u shows=%u fight_kept=%u local_spawns=%u spawns_seen=%u "
        "breaches=%u exempt_running=%u exempt_names=%zu layer_watch=%s(%s) layers_on=%u layer_scans=%u layer_found=%u net_scans=%u net_found=%u last=%s",
        g_decisionGate.load() ? "armed" : "unavailable", wo200::host_authority_ready() ? "armed" : "unavailable", authority_active() ? 1 : 0, authority_enabled() ? 1 : 0, static_cast<unsigned long long>(g_authorityGeneration.load()), g_authorityPhase.load(), g_decisionWhy,
        on(kBitNoBrains) ? "on" : "off", g_role.load(), g_active ? 1 : 0, g_b.why, g_spawnWhy, g_cen.samples, g_cen.nonzero, g_cen.maxRunning,
        g_cen.lastRunning, g_cen.nearNonzero, g_cen.maxNearRunning, g_cen.lastNearRunning, g_cen.lastNear, g_cen.lastDead, g_cen.lastExemptRunning, g_cen.lastBodies, g_npcList.size(), g_cen.lastHidden, g_cen.lastBound, g_cen.lastCostMs, g_cen.maxCostMs,
        c_bSweepPaused.load(), c_bSweepHidden.load(), c_bPauses.load(), c_bHides.load(), c_bShows.load(), c_bFightKept.load(), c_bLocalSpawns.load(), c_bSpawnSeen.load(),
        c_bBreaches.load(), c_bExemptRunning.load(), g_exempt.size(), wo217::on(wo217rules::kBitLayerWatch) ? "on" : "off", g_layerWhy, c_layerOn.load(),
        c_layerScans.load(), c_layerFound.load(), c_netScans.load(), c_netFound.load(), g_lastNames.empty() ? "-" : g_lastNames.substr(0, 300).c_str());
}

// ================================================================================================== D: outfits
// The host reads each streamed body's worn items (its soul's EquipmentManager: armour and weapons by class) round-robin, ~10 bodies a
// second, and keeps the bodies whose set changed (or that are new to the stream) for the agent (op 166): the copy is dressed with the
// spawn and on a change. The joiner reads its copy's own set (op 167) for the agent's diff. Reads only (the reflection's own getters).
constexpr int kMaxWorn = 40;
struct Worn { uint8_t n = 0; uint8_t cls[kMaxWorn][16]{}; };
std::unordered_map<uint32_t, std::pair<std::string, Worn>> g_wornSent;   // eid -> (name, the set last handed out)
std::unordered_map<uint32_t, std::pair<std::string, Worn>> g_wornOut;    // eid -> (name, the set to hand out)
std::vector<std::pair<uint32_t, std::string>> g_wornList;
size_t g_wornRR = 0;
double g_wornListAt = -1e9, g_wornAt = 0;
std::atomic<uint32_t> c_dReads{0}, c_dChanged{0}, c_dTaken{0}, c_dUnread{0}, c_dOfReads{0};

int worn_of_eid(uint32_t eid, Worn* w) {
    void* soul = hits::soul_of_eid(eid);
    if (!soul) return -1;
    unsigned char tmp[kMaxWorn][16];
    const int n = rttr::soul_equipped(soul, tmp, kMaxWorn);
    if (n < 0) return -1;
    // sorted, so a set compares as a set
    std::vector<std::array<uint8_t, 16>> v(static_cast<size_t>(n));
    for (int i = 0; i < n; ++i) std::memcpy(v[static_cast<size_t>(i)].data(), tmp[i], 16);
    std::sort(v.begin(), v.end());
    w->n = static_cast<uint8_t>(n);
    for (int i = 0; i < n; ++i) std::memcpy(w->cls[i], v[static_cast<size_t>(i)].data(), 16);
    return n;
}
bool same(const Worn& a, const Worn& b) { return a.n == b.n && std::memcmp(a.cls, b.cls, static_cast<size_t>(a.n) * 16) == 0; }

void streamed_list_cb(const char* name, uint32_t eid, bool notHuman, void* ctx) {
    if (notHuman || !name) return;
    static_cast<std::vector<std::pair<uint32_t, std::string>>*>(ctx)->emplace_back(eid, name);
}

void d_tick(double now) {
    if (!on(kBitOutfits) || g_role.load() != 1) return;
    if (now - g_wornListAt >= 2.0) {
        g_wornListAt = now;
        g_wornList.clear();
        wo138::for_each_streamed(&streamed_list_cb, &g_wornList);
        // bodies no longer streamed: forgotten (a body streamed again is dressed again)
        for (auto it = g_wornSent.begin(); it != g_wornSent.end();) {
            bool in = false;
            for (const auto& b : g_wornList) if (b.first == it->first) { in = true; break; }
            if (!in) it = g_wornSent.erase(it); else ++it;
        }
    }
    if (g_wornList.empty() || now - g_wornAt < 0.1) return;
    g_wornAt = now;
    const auto& b = g_wornList[g_wornRR++ % g_wornList.size()];
    Worn w;
    if (worn_of_eid(b.first, &w) < 0) { c_dUnread.fetch_add(1, std::memory_order_relaxed); return; }
    c_dReads.fetch_add(1, std::memory_order_relaxed);
    auto it = g_wornSent.find(b.first);
    if (it != g_wornSent.end() && same(it->second.second, w)) return;
    g_wornSent[b.first] = {b.second, w};
    g_wornOut[b.first] = {b.second, w};
    c_dChanged.fetch_add(1, std::memory_order_relaxed);
}

uint8_t d_read(uint8_t maxN, uint8_t* out, size_t cap, size_t* outLen) {
    size_t k = 1;
    uint8_t cnt = 0;
    for (auto it = g_wornOut.begin(); it != g_wornOut.end() && cnt < maxN;) {
        const std::string& nm = it->second.first;
        const Worn& w = it->second.second;
        const size_t need = 1 + nm.size() + 1 + static_cast<size_t>(w.n) * 16;
        if (nm.empty() || nm.size() > 63) { it = g_wornOut.erase(it); continue; }
        if (k + need > cap) break;
        out[k] = static_cast<uint8_t>(nm.size()); std::memcpy(out + k + 1, nm.data(), nm.size()); k += 1 + nm.size();
        out[k++] = w.n;
        std::memcpy(out + k, w.cls, static_cast<size_t>(w.n) * 16); k += static_cast<size_t>(w.n) * 16;
        ++cnt;
        it = g_wornOut.erase(it);
    }
    out[0] = cnt;
    *outLen = k;
    c_dTaken.fetch_add(cnt, std::memory_order_relaxed);
    return wo163::kROk;
}

uint8_t d_of(const char* name, uint8_t* out, size_t cap, size_t* outLen) {
    if (cap < 2) return wo163::kRBadRequest;
    void* e = nullptr;
    // the copy by its name: the entity system's own lookup through the engine's iterator would be slow; the writer binds every copy
    // by name, and engine::entity_by_name is not exposed -- the soul by name instead (the copy's soul is its host NPC's)
    struct Ctx { const char* n; void* e; } cx{name, nullptr};
    engine::for_each_entity([](void* ent, void* p) {
        auto* c = static_cast<Ctx*>(p);
        const char* nm = engine::entity_name(ent);
        if (nm && _stricmp(nm, c->n) == 0) { c->e = ent; return true; }
        return false;
    }, &cx);
    e = cx.e;
    c_dOfReads.fetch_add(1, std::memory_order_relaxed);
    Worn w;
    if (!e || worn_of_eid(engine::entity_id(e), &w) < 0) { out[0] = 0; out[1] = 0; *outLen = 2; return wo163::kROk; }
    const size_t n = (std::min)(static_cast<size_t>(w.n), (cap - 2) / 16);
    out[0] = 1; out[1] = static_cast<uint8_t>(n);
    std::memcpy(out + 2, w.cls, n * 16);
    *outLen = 2 + n * 16;
    return wo163::kROk;
}

int d_status(char* out, int n) {
    return std::snprintf(out, static_cast<size_t>(n), "D outfits=%s reads=%u unread=%u changed=%u taken=%u pending=%zu streamed=%zu joiner_reads=%u",
                         on(kBitOutfits) ? "on" : "off", c_dReads.load(), c_dUnread.load(), c_dChanged.load(), c_dTaken.load(), g_wornOut.size(),
                         g_wornList.size(), c_dOfReads.load());
}

double g_lastLine = 0;
void tick_reg() {
    const double now = now_s();
    b_tick(now);
    d_tick(now);
    if (now - g_lastLine >= 30.0) {
        g_lastLine = now;
        char t[2400];
        if (status(t, sizeof t) > 0 && g_configured.load()) logf("WO216-STATUS %s", t);
    }
}
int status_reg(char* out, int n) { return status(out, n); }
const bool kReg = wo203::reg_op(kOpConfig, &op) && wo203::reg_op(kOpStatus, &op) && wo203::reg_op(kOpBrains, &op) &&
                  wo203::reg_op(kOpLifecycle, &op) && wo203::reg_op_off_main(kOpLifecycle, &lifecycle_off_main) &&
                  wo203::reg_op(kOpExempt, &op) && wo203::reg_op(kOpOutfitRead, &op) && wo203::reg_op(kOpOutfitOf, &op) &&
                  wo203::reg_install("host_authority", &install_decision_gate) && wo203::reg_tick("w216", &tick_reg) && wo203::reg_status("w216", &status_reg);

} // namespace

bool on(uint32_t bit) { return (g_bits.load(std::memory_order_relaxed) & ~twin_off() & bit) != 0; }
bool configured() { return g_configured.load(std::memory_order_relaxed); }
bool authority_active() { return lifecycle_active(GetTickCount64()); }
bool authority_enabled() { return g_authorityEnabled.load(std::memory_order_acquire); }
uint64_t lifecycle_generation() { return g_authorityGeneration.load(std::memory_order_acquire); }
bool lifecycle_gameplay() { return g_authorityPhase.load(std::memory_order_acquire) == uint8_t(wo216authority::Phase::Gameplay); }
bool decisions_blocked() { return authority_active() && g_decisionGate.load(); }
uint64_t p5_corpses_visible() { return g_p5World.load() == lifecycle_generation() ? g_p5Visible.load() : 0; }
uint64_t p5_corpse_hidden_blocked() { return g_p5Blocked.load(); }
void p5_copy_dead_soul(void* soul) {
    void* canonicalSoul = soul ? buffs::as_c_soul(soul) : nullptr;
    const uintptr_t wanted = reinterpret_cast<uintptr_t>(canonicalSoul ? canonicalSoul : soul);
    if (!wanted) return;
    // Cached tracked identities only: never enumerate or query every world soul.
    for (const auto& item : g_bodies) if (item.second.corpse.identity.soul == wanted) { p5_copy_dead(item.first); return; }
}
void p5_copy_dead(uint32_t eid) {
    void* e = engine::entity_by_id(eid);
    const char* name = e ? engine::entity_name(e) : nullptr;
    if (!e || !name || ours(name) || npcscan::body_kind(e) < 0) return;
    if (!g_b.armed) b_resolve();
    BBody& b = g_bodies[eid];
    if (b.name.empty()) b.name = name;
    p5_read(eid, b); p5_preserve(eid, b);
    if (b.corpse.confirmed) p5_watch(eid);
}


int status(char* out, int n) {
    if (!out || n <= 0) return 0;
    int k = std::snprintf(out, static_cast<size_t>(n), "w216 bits=0x%X twin_off=0x%X configured=%d | ", g_bits.load(), twin_off(), g_configured.load() ? 1 : 0);
    if (k < 0 || k >= n) return n - 1;
    k += wo215::replay_status(out + k, n - k);
    if (k < n - 2) { k += std::snprintf(out + k, static_cast<size_t>(n - k), " | "); if (k < n - 2) k += b_status(out + k, n - k); }
    if (k < n - 2) { k += std::snprintf(out + k, static_cast<size_t>(n - k), " | "); if (k < n - 2) k += d_status(out + k, n - k); }
    if (k < n - 2) { k += std::snprintf(out + k, static_cast<size_t>(n - k), " | "); if (k < n - 2) k += wo208::owner_rows_status(out + k, n - k); }
    return k < n ? k : n - 1;
}

} // namespace kcdmp::wo216
