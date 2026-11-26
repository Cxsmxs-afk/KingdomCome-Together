// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
// WO-201 V.2 nearest_player_triggers (the host), rebuilt by WO-204 Act I (nearest_player_v2): a quest's distance trigger on "the
// player" measures from the nearest of the host's Henry and the bound partner figures. "Came near" fires when any does; "is far"
// (the escort's "player out from the tour") only once all of those figures are far.
//
// The engine's C_DistanceTrigger (XGenAIModule) reads its Origin and Target ports when it becomes active, keeps the two objects on the
// node, registers the node as a move listener on BOTH objects, and evaluates on every move of either: Evaluate reads each one's
// position through its own virtual, takes the distance, compares it with the node's threshold by the node's compare kind and hands
// the bool to SetResult, which stores the match state and fires OnMatch / OnUnmatch on a change. Deactivation unregisters the node
// from the two objects then in its fields and clears them.
//
// WO-204 I.1 -- why the WO-201 design crashed after a sleep and a wait: it wrote the figure's C_NPC into the node's player field for
// one evaluation and wrote Henry back after it. SetResult fires the quest's flow inside that evaluation; when the flow deactivated the
// node there, the deactivation unregistered the FIGURE (the object then in the field) and cleared both fields, and the restore wrote
// Henry back into the dead node. Henry kept a move listener on a node with no target: his next move after the time skip evaluated it
// on an AI worker and read the null target (both crash dumps: active byte 0, origin = Henry's C_NPC, target 0, threshold 0).
//
// v2 writes nothing into any engine object. The detour reads the node's two objects and their positions exactly as Evaluate does,
// on the thread the engine evaluates on; when the node is bound to Henry and the figure (its position from the mod's own main-thread
// snapshot, never its object) is nearer to the other end, it computes Evaluate's own compare from that distance and calls the
// engine's own SetResult. Otherwise the engine's Evaluate runs unchanged. There is no forced re-evaluation: the figure's own moves do
// not wake a node (accepted), the node answers whenever the engine evaluates it. Quiet (the engine's own Evaluate, exactly as without
// the mod) from a time skip's start until 5 s after it ends, after a load or a new player soul, and while the agent says a join or a
// load runs.
//
// Every piece is read out of the engine's code, never by a fixed address:
//   * the activation function: by its trace text; its tail "mov byte [rdi+X],1; ...; jmp Evaluate" gives Evaluate and the active byte;
//   * Evaluate's whole body must match wo204rules::kEvalBody (displacements masked): the node's origin/target/kind/threshold fields,
//     the jump table (its six entries must land on the six compares) and SetResult come out of it;
//   * Evaluate's prologue is checked against the hook-prologue registry before the detour goes in (the first time the switch is on).
// WO201-NEAR / WO204-NEAR.
#include "wo201.h"
#include "wo201_rules.h"
#include "wo204_rules.h"
#include "wo140.h"
#include "wo163.h"
#include "anchors.h"
#include "engine.h"
#include "fault_guard.h"
#include "log.h"
#include "npcstate.h"
#include "rttr_abi.h"
#include "inline_hook.h"
#include "hook_prologues.h"
#include "load_sequence.h"
#include "main_thread.h"
#include "wo202.h"
#include "wo202_rules.h"
#include <windows.h>
#include <array>
#include <atomic>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <vector>

namespace kcdmp::wo201near {

namespace {

using namespace wo201rules;
namespace r4 = wo204rules;

constexpr uint32_t kPlayerEntity = 0x7777;
constexpr size_t kMgrNpcOf = 0x20;   // C_NPCManager vtable: the C_NPC of an entity id (WO-189's read)
constexpr char kActivateText[] = "no origin or target on input";
constexpr size_t kMaxGuests = 3;
constexpr uint64_t kSampleIntervalMs = 33;
using Roster = std::array<uint32_t, kMaxGuests>;

const char* g_why = "not resolved";
void* g_eval = nullptr;
void* g_setResult = nullptr;
void* const* g_vftNpc = nullptr;
void* const* g_vftNpcMgr = nullptr;
int64_t g_offO = -1, g_offT = -1, g_offActive = -1, g_offKind = -1, g_offThr = -1;
size_t g_posSlot = 0;
using EvalFn = void (*)(void* node);
using SetResultFn = void (*)(void* node, bool match);
EvalFn g_evalOrig = nullptr;
bool g_hooked = false;
std::atomic<bool> g_on{false}, g_agentQuiet{false};
std::atomic<uint64_t> g_quietUntil{0}, g_rosterRevision{0}, g_lastLog{0};
std::atomic<uint32_t> c_evals{0}, c_bound{0}, c_overrides{0}, c_overridesOffMain{0}, c_quiet{0}, c_stale{0}, c_unsettled{0}, c_kindUnknown{0};
std::atomic<uint32_t> c_snapOk{0}, c_figNone{0}, c_figChanged{0}, c_noHenry{0};   // the main thread's snapshot, per frame

// Partner C_NPC pointers are identity only on the workers. All partner positions are sampled on the main thread.
struct Figure { void* npc = nullptr; float pos[3]{}; bool ready = false; };
struct Snap {
    void* P = nullptr;
    std::array<Figure, kMaxGuests> figures{};
    uint64_t at = 0, revision = 0, load = 0;
    size_t expected = 0;
};
SRWLOCK g_snapLock = SRWLOCK_INIT;
Roster g_roster{}; // under g_snapLock
Snap g_snap;   // under g_snapLock
struct PreviousFigure { void* npc = nullptr; uint64_t since = 0; };
std::array<PreviousFigure, kMaxGuests> g_previous{};
uint64_t g_lastTick = 0, g_lastSample = 0, g_lastRevision = 0, g_lastLoad = 0;
void* g_lastSoul = nullptr;
bool g_wasSkipping = false;

template <class T> bool rd(const void* base, ptrdiff_t off, T* out) {
    if (!base) return false;
    KCDMP_FAULT_READ(site, "wo201near::read");
    if (!fault::enabled(site)) return false;
    const uintptr_t a = reinterpret_cast<uintptr_t>(base) + static_cast<uintptr_t>(off);
    if (!fault::plausible_address(a)) return false;
    return fault::guarded(site, [&] { std::memcpy(out, reinterpret_cast<const void*>(a), sizeof(T)); });
}
bool rd_bytes(const void* p, uint8_t* out, size_t n) {
    KCDMP_FAULT_READ(site, "wo201near::read_bytes");
    if (!p || !fault::enabled(site) || !fault::plausible_address(reinterpret_cast<uintptr_t>(p))) return false;
    return fault::guarded(site, [&] { std::memcpy(out, p, n); });
}
bool is_a(const void* obj, void* const* vft) {
    void* vp = nullptr;
    return obj && vft && rd(obj, 0, &vp) && vp == static_cast<const void*>(vft);
}
void* vslot(const void* obj, size_t off) {
    void* vt = nullptr; void* fn = nullptr;
    return obj && rd(obj, 0, &vt) && vt && rd(vt, static_cast<ptrdiff_t>(off), &fn) ? fn : nullptr;
}

// An object's position through the slot Evaluate itself calls (it returns a pointer to x,y,z) -- the same call the engine makes on
// this thread for this node.
bool pos_of(void* obj, float out[3]) {
    void* fn = vslot(obj, g_posSlot);
    if (!fn) return false;
    const float* p = nullptr;
    KCDMP_FAULT_CALL(site, "wo201near::position");
    if (!fault::guarded(site, [&] { p = reinterpret_cast<const float* (*)(void*)>(fn)(obj); }) || !p) return false;
    return rd_bytes(p, reinterpret_cast<uint8_t*>(out), 12);
}

void* npc_of(void* mgr, void* get, uint32_t eid) {
    void* npc = nullptr;
    uint32_t id = eid;
    KCDMP_FAULT_CALL(site, "wo201near::npc_of");
    if (!fault::guarded(site, [&] { npc = reinterpret_cast<void* (*)(void*, const uint32_t*)>(get)(mgr, &id); })) return nullptr;
    return is_a(npc, g_vftNpc) ? npc : nullptr;
}

Snap read_snap() {
    AcquireSRWLockShared(&g_snapLock);
    const Snap s = g_snap;
    ReleaseSRWLockShared(&g_snapLock);
    return s;
}
void write_snap(const Snap& s) {
    AcquireSRWLockExclusive(&g_snapLock);
    if (g_rosterRevision.load() == s.revision) g_snap = s;
    ReleaseSRWLockExclusive(&g_snapLock);
}

// Any thread the engine evaluates on. Reads only; the one call into the engine besides the positions is its own SetResult.
void eval_detour(void* node) {
    c_evals.fetch_add(1, std::memory_order_relaxed);
    if (!g_on.load(std::memory_order_relaxed) || !g_setResult) { g_evalOrig(node); return; }
    const uint64_t now = GetTickCount64();
    if (r4::near_quiet(now, g_quietUntil.load(std::memory_order_relaxed), g_agentQuiet.load(std::memory_order_relaxed))) {
        c_quiet.fetch_add(1, std::memory_order_relaxed); g_evalOrig(node); return;
    }
    const Snap s = read_snap();
    if (!s.P || s.revision != g_rosterRevision.load() || s.load != loadsequence::stamp()) { g_evalOrig(node); return; }
    void* o = nullptr; void* t = nullptr;
    if (!rd(node, static_cast<ptrdiff_t>(g_offO), &o) || !rd(node, static_cast<ptrdiff_t>(g_offT), &t) || !o || !t || o == t
        || (o != s.P && t != s.P)) {
        g_evalOrig(node); return;
    }
    c_bound.fetch_add(1, std::memory_order_relaxed);
    void* other = o == s.P ? t : o;
    // A trigger whose other end is a player retains its own engine semantics.
    for (const auto& figure : s.figures) if (other == figure.npc) { g_evalOrig(node); return; }
    if (!r4::snapshot_fresh(now, s.at)) { c_stale.fetch_add(1, std::memory_order_relaxed); g_evalOrig(node); return; }
    float pp[3], po[3];
    if (!pos_of(s.P, pp) || !pos_of(other, po)) { g_evalOrig(node); return; }
    std::array<float, kMaxGuests> distances{};
    size_t known = 0;
    for (const auto& figure : s.figures) {
        if (!figure.ready) continue;
        const float distance = r4::distance3(figure.pos, po);
        if (std::isfinite(distance)) distances[known++] = distance;
    }
    const float dHenry = r4::distance3(pp, po), nearest = nearest_player(dHenry, distances.data(), known);
    const bool partnerNearer = nearest < dHenry;
    if (!partnerNearer && known == s.expected) { g_evalOrig(node); return; }
    int32_t kind = -1; float thr = 0;
    bool match = false;
    if (!rd(node, static_cast<ptrdiff_t>(g_offKind), &kind) || !rd(node, static_cast<ptrdiff_t>(g_offThr), &thr)) { g_evalOrig(node); return; }
    if (!r4::trigger_result(kind, nearest, thr, &match)) { c_kindUnknown.fetch_add(1, std::memory_order_relaxed); g_evalOrig(node); return; }
    if (s.revision != g_rosterRevision.load() || s.load != loadsequence::stamp()) { g_evalOrig(node); return; }
    if (match && (kind == 2 || kind == 3) && known < s.expected) return; // a missing position cannot prove everyone is far
    if (!partnerNearer) { g_evalOrig(node); return; }
    KCDMP_FAULT_CALL(site, "wo201near::set_result");
    if (!fault::guarded(site, [&] { reinterpret_cast<SetResultFn>(g_setResult)(node, match); })) return;
    c_overrides.fetch_add(1, std::memory_order_relaxed);
    const bool offMain = GetCurrentThreadId() != main_thread::main_thread_id();
    if (offMain) c_overridesOffMain.fetch_add(1, std::memory_order_relaxed);
    auto last = g_lastLog.load(std::memory_order_relaxed);
    if (now >= last && now - last >= 10000 && g_lastLog.compare_exchange_strong(last, now, std::memory_order_relaxed)) {
        deferred_log::Scope defer;
        logf("MP-FIX ESC distance=%.1f host=%.1f players=%zu/%zu reason=near-trigger kind=%d threshold=%.1f match=%d [overrides=%u bound=%u quiet=%u] thread=%lu",
             nearest, dHenry, known + 1, s.expected + 1, kind, thr, match ? 1 : 0, c_overrides.load(), c_bound.load(), c_quiet.load(),
             static_cast<unsigned long>(GetCurrentThreadId()));
    }
}

bool arm(const char** why) {
    if (g_hooked) return true;
    if (!g_eval || !g_setResult) { *why = g_why; return false; }
    void* orig = nullptr;
    if (!inlinehook::install_detour(g_eval, hookpro::kDistEvaluate, sizeof hookpro::kDistEvaluate, reinterpret_cast<void*>(&eval_detour), &orig, why))
        return false;
    g_evalOrig = reinterpret_cast<EvalFn>(orig);
    g_hooked = true;
    logf("WO204-NEAR armed: C_DistanceTrigger's Evaluate detoured (origin +0x%llX, target +0x%llX, kind +0x%llX, threshold +0x%llX, position slot +0x%zX) -- the node is read, never written",
         static_cast<long long>(g_offO), static_cast<long long>(g_offT), static_cast<long long>(g_offKind), static_cast<long long>(g_offThr), g_posSlot);
    return true;
}

} // namespace

void install_resolve();

void install() {
    install_resolve();
    if (std::strcmp(g_why, "resolved") != 0) logf("WO201-NEAR resolver: NOT resolved (%s) -- the player's distance triggers stay the host's Henry's", g_why);
}

void install_resolve() {
    HMODULE xg = GetModuleHandleA("XGenAIModule.dll");
    if (!xg) { g_why = "XGenAIModule.dll not loaded"; return; }
    g_vftNpc = anchor::find_vftable(xg, ".?AVC_NPC@xgenaimodule@wh@@", 0);
    g_vftNpcMgr = anchor::find_vftable(xg, ".?AVC_NPCManager@xgenaimodule@wh@@", 0);
    if (!anchor::find_vftable(xg, ".?AVC_DistanceTrigger@xgenaimodule@wh@@", 0) || !g_vftNpc || !g_vftNpcMgr) {
        g_why = "a class (C_DistanceTrigger, C_NPC, C_NPCManager) not found by RTTI"; return;
    }
    int n = 0;
    const uint8_t* act = anchor::function_by_string(xg, kActivateText, &n);
    if (!act || n != 1) { g_why = "the activation text is referenced by no (or more than one) function"; return; }
    // The compiler splits this function into contiguous chunks (its unwind data names only the first): its code runs from the start
    // to the first int3 padding run, which ends a function
    std::vector<uint8_t> code(0x600);
    if (!rd_bytes(act, code.data(), code.size())) { g_why = "the activation function unreadable"; return; }
    size_t len = code.size();
    for (size_t i = 0; i + 3 <= code.size(); ++i)
        if (code[i] == 0xCC && code[i + 1] == 0xCC && code[i + 2] == 0xCC) { len = i; break; }
    const TailJump tj = active_tail_jump(code.data(), len);
    if (tj.jmpAt < 0) { g_why = "its tail (mov byte [rdi+X],1 ... jmp Evaluate) not found"; return; }
    int32_t rel = 0; std::memcpy(&rel, code.data() + tj.jmpAt + 1, 4);
    void* ev = const_cast<uint8_t*>(act + tj.jmpAt + 5 + rel);
    g_offActive = tj.activeOff;
    uint8_t body[sizeof r4::kEvalBody]{};
    if (!rd_bytes(ev, body, sizeof body)) { g_why = "Evaluate unreadable"; return; }
    if (std::memcmp(body, hookpro::kDistEvaluate, sizeof hookpro::kDistEvaluate) != 0) { g_why = "Evaluate's prologue is not the registry's"; return; }
    const NodeReads nr = evaluate_reads(body, sizeof body);
    const r4::EvalShape sh = r4::evaluate_shape(body, sizeof body);
    if (!sh.ok) { g_why = "Evaluate's body is not the shape WO-204 read (position reads, compare, SetResult)"; return; }
    if (nr.origin != sh.origin || nr.target != sh.target || nr.slot <= 0) { g_why = "Evaluate's two position reads disagree with its shape"; return; }
    int32_t entries[6]{};
    const uint8_t* table = static_cast<const uint8_t*>(ev) + sh.baseRel + sh.tableRva;
    if (!rd_bytes(table, reinterpret_cast<uint8_t*>(entries), sizeof entries) || !r4::table_matches(entries, sh.baseRel)) {
        g_why = "Evaluate's compare table does not land on its six compares"; return;
    }
    g_offO = sh.origin; g_offT = sh.target; g_offKind = sh.kind; g_offThr = sh.threshold; g_posSlot = static_cast<size_t>(nr.slot);
    g_setResult = static_cast<uint8_t*>(ev) + sh.setResultAt;
    g_eval = ev;
    g_why = "resolved";
    char a[96]; anchor::describe(g_eval, a, sizeof a);
    char b[96]; anchor::describe(g_setResult, b, sizeof b);
    logf("WO201-NEAR resolver: Evaluate %s, SetResult %s (origin +0x%llX target +0x%llX kind +0x%llX threshold +0x%llX active +0x%llX position slot +0x%zX) -- %s the build the research read",
         a, b, static_cast<long long>(g_offO), static_cast<long long>(g_offT), static_cast<long long>(g_offKind), static_cast<long long>(g_offThr),
         static_cast<long long>(g_offActive), g_posSlot,
         (g_offO == kKnownOriginOff && g_offT == kKnownTargetOff && g_offActive == kKnownActiveOff) ? "the same as" : "DIFFERENT from (the resolved values are used)");
}

// Main thread: quiet on a load and publish a bounded roster snapshot at most 30 times a second.
void tick() {
    if (!g_on.load() || !g_hooked) return;
    const uint64_t now = GetTickCount64();
    const uint64_t gap = now - g_lastTick;
    g_lastTick = now;
    const uint64_t load = loadsequence::stamp();
    if (gap > 500 || load != g_lastLoad) {
        g_quietUntil = now + r4::kQuietAfterMs;
        g_previous = {};
        g_lastLoad = load;
    }
    if (now - g_lastSample < kSampleIntervalMs) return;
    g_lastSample = now;
    // I.3: a load (a long frame), a new player soul, a time skip -- quiet until kQuietAfterMs after it
    void* ps = rttr::read_player_soul();
    const int skip = wo140::skip_state_now();
    const bool skipping = skip > 0 && r4::skip_quiet(static_cast<uint32_t>(skip));
    if (!ps || ps != g_lastSoul || skipping) {
        if (skipping && !g_wasSkipping) logf("WO204-NEAR quiet: a time skip (state %d) -- the triggers are the host's Henry's until %llu ms after it ends", skip,
                                             static_cast<unsigned long long>(r4::kQuietAfterMs));
        g_lastSoul = ps;
        g_quietUntil = now + r4::kQuietAfterMs;
        g_previous = {};
    }
    g_wasSkipping = skipping;
    if (r4::near_quiet(now, g_quietUntil.load(), g_agentQuiet.load())) return;
    Snap s;
    s.at = now;
    s.load = load;
    AcquireSRWLockShared(&g_snapLock);
    const Roster roster = g_roster;
    s.revision = g_rosterRevision.load();
    ReleaseSRWLockShared(&g_snapLock);
    if (s.revision != g_lastRevision) {
        g_lastRevision = s.revision;
        g_previous = {};
    }
    void* mgr = wo141::x::npc_manager();
    void* get = is_a(mgr, g_vftNpcMgr) ? vslot(mgr, kMgrNpcOf) : nullptr;
    s.P = get ? npc_of(mgr, get, kPlayerEntity) : nullptr;
    if (!s.P) c_noHenry.fetch_add(1, std::memory_order_relaxed);
    for (size_t i = 0; s.P && i < roster.size() && roster[i]; ++i) {
        ++s.expected;
        auto& fig = s.figures[i];
        auto& prev = g_previous[i];
        fig.npc = engine::entity_by_id(roster[i]) ? npc_of(mgr, get, roster[i]) : nullptr;
        if (fig.npc == s.P) fig.npc = nullptr;
        if (!fig.npc) c_figNone.fetch_add(1, std::memory_order_relaxed);
        if (fig.npc != prev.npc) {
            if (fig.npc && prev.npc) c_figChanged.fetch_add(1, std::memory_order_relaxed);
            prev = {fig.npc, now};
        }
        fig.ready = wo202rules::figure_usable(fig.npc, prev.npc, prev.since, now) && pos_of(fig.npc, fig.pos);
        if (!fig.ready) c_unsettled.fetch_add(1, std::memory_order_relaxed);
    }
    write_snap(s);
    c_snapOk.fetch_add(1, std::memory_order_relaxed);
}

uint8_t handle(const uint8_t* body, size_t len, uint8_t* out, size_t cap, size_t* outLen) {
    // [op][on:1][first:4][quiet?:1][second?:4][third?:4] -> [armed:1][overrides:4][bound:4]
    if ((len != 6 && len != 7 && len != 15) || cap < 9) return wo163::kRBadRequest;
    const bool on = body[1] != 0;
    Roster roster{};
    const bool quiet = on && len >= 7 && body[6] != 0;
    if (on) {
        std::memcpy(&roster[0], body + 2, 4);
        if (len == 15) std::memcpy(&roster[1], body + 7, 8);
        if (!roster[0] || (roster[2] && !roster[1])) return wo163::kRBadRequest;
        for (size_t i = 0; i < roster.size() && roster[i]; ++i) {
            if (roster[i] == kPlayerEntity) return wo163::kRBadRequest;
            for (size_t j = 0; j < i; ++j) if (roster[i] == roster[j]) return wo163::kRBadRequest;
        }
        const char* why = "";
        if (!arm(&why)) { logf("WO201-NEAR NOT armed (%s) -- the player's distance triggers stay the host's Henry's", why); return wo163::kRFailed; }
    }
    AcquireSRWLockExclusive(&g_snapLock);
    const bool changed = roster != g_roster || quiet != g_agentQuiet.load() || on != g_on.load();
    if (changed) {
        g_roster = roster;
        g_snap = {};
        g_rosterRevision.fetch_add(1);
        g_agentQuiet = quiet;
        g_on = on;
    }
    ReleaseSRWLockExclusive(&g_snapLock);
    if (changed) logf("WO204-NEAR figures=%u quiet=%d", (roster[0] ? 1u : 0u) + (roster[1] ? 1u : 0u) + (roster[2] ? 1u : 0u), quiet ? 1 : 0);
    out[0] = g_hooked ? 1 : 0;
    const uint32_t ov = c_overrides.load(), bd = c_bound.load();
    std::memcpy(out + 1, &ov, 4); std::memcpy(out + 5, &bd, 4);
    *outLen = 9;
    return wo163::kROk;
}

int status_text(char* out, int n) {
    const auto snap = read_snap();
    return std::snprintf(out, static_cast<size_t>(n),
                         "near=%s v2 on=%d quiet_agent=%d figures=%zu evals=%u bound=%u overrides=%u off_main=%u quiet=%u stale=%u unsettled=%u kind_unknown=%u "
                         "snap_ok=%u fig_none=%u fig_changed=%u no_henry=%u",
                         g_why, g_on.load() ? 1 : 0, g_agentQuiet.load() ? 1 : 0, snap.expected, c_evals.load(), c_bound.load(), c_overrides.load(),
                         c_overridesOffMain.load(), c_quiet.load(), c_stale.load(), c_unsettled.load(), c_kindUnknown.load(),
                         c_snapOk.load(), c_figNone.load(), c_figChanged.load(), c_noHenry.load());
}

} // namespace kcdmp::wo201near
