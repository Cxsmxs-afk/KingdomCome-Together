// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
// The lifetime of the combat animation actions this mod queues (combat_actions.h).
#include "combat_actions.h"
#include "combat_swing.h"
#include "crash_capture.h"
#include "load_sequence.h"
#include "motion.h"
#include "wo221.h"
#include "wo203.h"
#include "wo216.h"
#include "anchors.h"
#include "engine.h"
#include "fault_guard.h"
#include "log.h"

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <array>
#include <atomic>
#include <cstdio>
#include <cstring>
#include <limits>
#include <mutex>

namespace kcdmp::combatactions {

namespace {

// ---- anchors (read out of this build's files; every one is re-checked by its bytes at install, none is a fixed address) ---------
// C_Actor (EntityModule, RTTI .?AVC_Actor@entitymodule@wh@@): vftable slot 0xB60 = ReleaseCombatActor -- the one place a body's
// combat part (+0x300) is given back to the combat module: called (virtually only) by SetMonsterLOD, by the actor's reset / death
// path (slot 0x528 and its two overrides) and by the actor's shutdown.
constexpr size_t kActorVtReleaseCombat = 0xB60;
constexpr size_t kActorCombatActor = 0x300;
constexpr size_t kActorEid = 0x30;         // C_Actor::entity id, checked in motion.cpp
constexpr size_t kActorEntity = 0x38;      // the actor's entity (read in SetMonsterLOD: mov rdx,[r14+38h])
// C_CombatAnimAction (CombatModule, RTTI .?AVC_CombatAnimAction@combatmodule@wh@@): +0x28 status (0 none, 1 pending, 2 installed,
// 3 exiting, 4 finished), +0x58 reference count, +0x9C its body's entity id; vftable slot 14 (+0x70, "fragment started"), slot 17
// (+0x88, "the fragment is about to end"), slot 18 (+0x90): the three that reach for the combat part; slot 23 (+0xB8) deletes.
constexpr size_t kActStatus = 0x28, kActRefs = 0x58;
constexpr size_t kSlotStarted = 14, kSlotEnding = 17, kSlotEvent18 = 18, kSlotDelete = 23;
constexpr size_t kVtCopy = 48;          // slots copied into the quiet vftable (the class has 29; the rest is never called)
constexpr size_t kBuckets = 256, kBucketSize = 64, kSweepReads = 8, kReleaseStops = 8;
constexpr uint64_t kDoneKeepMs = 30000;
constexpr uint64_t kWatchMs = static_cast<uint64_t>(wo167rules::kWatchS * 1000);
using detail::Retirement;
enum class QueueStage : uint8_t { Reserved, InFlight, Settled };
enum class ActionAccess : uint8_t { CurrentBody, EngineRelease, RetiredQueue };

struct Entry {
    BodyProof body;
    void* action = nullptr;
    uint64_t trackedAt = 0, epoch = 0;
    Retirement retirement = Retirement::Active;
    bool movesBody = false;
    uint64_t serial = 0;
    QueueStage queue = QueueStage::Reserved;
};

// The engine may reenter a release while Stop is on the stack. Never wait and never recursively acquire this gate.
struct BucketGate {
    std::atomic_flag busy = ATOMIC_FLAG_INIT;
    bool try_lock() { return !busy.test_and_set(std::memory_order_acquire); }
    void unlock() { busy.clear(std::memory_order_release); }
};

struct ActorLife {
    std::atomic<void*> actor{nullptr};
    std::atomic<uint64_t> revision{0};
    BodyProof fenced;
    uint64_t attempt = 0;
    bool ready = false;
    ReleaseReceipt receipt = ReleaseReceipt::Stale;
};

struct Bucket {
    BucketGate gate;
    std::array<Entry, kBucketSize> entries;
    std::array<ActorLife, kBucketSize> lives;
    std::atomic<size_t> lifeCount{0};
    std::atomic<unsigned> releaseDepth{0};
    std::atomic<uintptr_t> untrackedActor{0};
};

std::array<Bucket, kBuckets> g_buckets;
std::atomic<bool> g_armed{false}, g_releaseOn{false}, g_unscopedGap{false};
std::atomic<uint64_t> g_epoch{1};
uint64_t g_nextQueue = 0;  // Main only
void* const* g_actionVft = nullptr;      // the engine's own
void* g_quiet[1 + kVtCopy] = {};         // [0] = the engine's RTTI locator (vftable[-1]); [1..] = the quiet copy
using ReleaseFn = void (*)(void*);
ReleaseFn g_origRelease = nullptr;
std::atomic<uint32_t> c_withPart{0}, c_tracked{0}, c_live{0}, c_hook{0}, c_stopped{0}, c_ended{0}, c_quieted{0}, c_explicit{0},
    c_forgotten{0}, c_offMain{0}, c_quarantined{0}, c_hookBusy{0}, c_noProof{0}, c_full{0}, c_retireScopes{0}, c_quietOnly{0},
    c_trackingGap{0}, c_finishPending{0}, c_queueRefused{0}, c_queueUncertain{0}, c_queueMissingHook{0};
std::atomic<DWORD> g_mainTid{0};

Bucket& bucket_for(const void* actor) {
    const auto address = reinterpret_cast<uintptr_t>(actor);
    return g_buckets[((address >> 4) ^ (address >> 12) ^ (address >> 24)) & (kBuckets - 1)];
}

ActorLife* find_life(Bucket& bucket, void* actor) {
    const auto count = bucket.lifeCount.load(std::memory_order_acquire);
    for (size_t i = 0; i < count; ++i)
        if (bucket.lives[i].actor.load(std::memory_order_relaxed) == actor) return &bucket.lives[i];
    return nullptr;
}

ActorLife* ensure_life(Bucket& bucket, void* actor) {
    if (auto* existing = find_life(bucket, actor)) return existing;
    std::unique_lock lock(bucket.gate, std::try_to_lock);
    if (!lock || bucket.releaseDepth.load(std::memory_order_acquire)) return nullptr;
    if (auto* existing = find_life(bucket, actor)) return existing;
    const auto index = bucket.lifeCount.load(std::memory_order_relaxed);
    if (index == bucket.lives.size()) return nullptr;
    // A slot is never recycled for another pointer. The hook can use its revision without a lock or ABA.
    auto& life = bucket.lives[index];
    life.actor.store(actor, std::memory_order_relaxed);
    bucket.lifeCount.store(index + 1, std::memory_order_release);
    return &life;
}

constexpr uintptr_t kUntrackedCollision = 1;

void note_tracking_gap(void* actor) {
    c_trackingGap.fetch_add(1, std::memory_order_relaxed);
    if (!actor) { g_unscopedGap.store(true, std::memory_order_release); return; }
    auto& suspect = bucket_for(actor).untrackedActor;
    const auto address = reinterpret_cast<uintptr_t>(actor);
    uintptr_t empty = 0;
    // Keep evidence of an untracked action until process exit. If two failed actors share a bucket the bucket is unconfirmed.
    if (!suspect.compare_exchange_strong(empty, address, std::memory_order_acq_rel) && empty != address)
        suspect.store(kUntrackedCollision, std::memory_order_release);
}

bool has_tracking_gap(const Bucket& bucket, void* actor) {
    const auto suspect = bucket.untrackedActor.load(std::memory_order_acquire);
    return g_unscopedGap.load(std::memory_order_acquire) || suspect == kUntrackedCollision ||
        suspect == reinterpret_cast<uintptr_t>(actor);
}

bool on_main() { return GetCurrentThreadId() == g_mainTid.load(std::memory_order_relaxed); }

void note_main_thread() {
    DWORD unknown = 0;
    g_mainTid.compare_exchange_strong(unknown, GetCurrentThreadId(), std::memory_order_relaxed);
}

void quiet_slot(void*) {}   // what the three callbacks become: nothing is read, nothing is called

bool rd_u32(const void* p, size_t off, uint32_t* out) {
    KCDMP_FAULT_READ(site, "combat_actions::rd_u32");
    return fault::guarded(site, [&] { *out = *reinterpret_cast<const uint32_t*>(static_cast<const char*>(p) + off); });
}
bool rd_ptr(const void* p, size_t off, void** out) {
    KCDMP_FAULT_READ(site, "combat_actions::rd_ptr");
    return fault::guarded(site, [&] { *out = *reinterpret_cast<void* const*>(static_cast<const char*>(p) + off); });
}

BodyProof observe_actor_raw(void* actor, uint64_t revision) {
    if (!actor) return {};
    BodyProof proof{};
    proof.actor = actor;
    proof.lifecycle = wo216::lifecycle_observation_stamp();
    proof.load = loadsequence::stamp();
    proof.combatRevision = revision;
    proof.queueEpoch = g_epoch.load(std::memory_order_acquire);
    if (!rd_u32(actor, kActorEid, &proof.eid) || !rd_ptr(actor, kActorEntity, &proof.entity) ||
        !rd_ptr(actor, kActorCombatActor, &proof.combat) || !detail::known_body(proof) ||
        proof.lifecycle != wo216::lifecycle_observation_stamp() || proof.load != loadsequence::stamp() ||
        proof.queueEpoch != g_epoch.load(std::memory_order_acquire)) return {};
    return proof;
}

BodyProof observe_actor(void* actor) {
    if (!actor) return {};
    auto& bucket = bucket_for(actor);
    if (bucket.releaseDepth.load(std::memory_order_acquire)) return {};
    auto* life = find_life(bucket, actor);
    if (!life) return {};
    const auto revision = life->revision.load(std::memory_order_acquire);
    const BodyProof proof = observe_actor_raw(actor, revision);
    return !bucket.releaseDepth.load(std::memory_order_acquire) && revision == life->revision.load(std::memory_order_acquire)
        ? proof : BodyProof{};
}
bool add_ref(void* action) {
    KCDMP_FAULT_READ(site, "combat_actions::add_ref");
    bool added = false;
    return fault::guarded(site, [&] {
        const void* table = *reinterpret_cast<void**>(action);
        if (!g_actionVft || (table != g_actionVft && table != &g_quiet[1])) return;
        auto* refs = reinterpret_cast<volatile LONG*>(static_cast<char*>(action) + kActRefs);
        LONG count = InterlockedCompareExchange(refs, 0, 0);
        for (int attempt = 0; attempt < 4 && count > 0 && count < (std::numeric_limits<LONG>::max)(); ++attempt) {
            const LONG previous = InterlockedCompareExchange(refs, count + 1, count);
            if (previous == count) { added = true; return; }
            count = previous;
        }
    }) && added;
}
// the controller's own way of giving a reference back (CryAction: lock xadd [+0x58], -1; at zero the action's slot 23)
bool drop_ref(void* action, bool unsubmitted = false) {
    KCDMP_FAULT_READ(site, "combat_actions::drop_ref");
    LONG left = 1;
    void** vt = nullptr;
    bool released = false;
    if (!fault::guarded(site, [&] {
            vt = *reinterpret_cast<void***>(action);
            if (vt != g_actionVft && vt != &g_quiet[1]) return;
            const auto state = *reinterpret_cast<const uint32_t*>(static_cast<const char*>(action) + kActStatus);
            if (state != wo167rules::kStatusFinished && !(unsubmitted && state == wo167rules::kStatusNone)) return;
            auto* refs = reinterpret_cast<volatile LONG*>(static_cast<char*>(action) + kActRefs);
            const LONG count = InterlockedCompareExchange(refs, 0, 0);
            if (count <= 0 || InterlockedCompareExchange(refs, count - 1, count) != count) return;
            left = count - 1;
            released = true;
        }) || !released) return false;
    // the action's own delete frees memory (the allocator's lock): a plain call, never under the fault guard
    if (left == 0) reinterpret_cast<void (*)(void*)>(vt[kSlotDelete])(action);
    return true;
}
bool set_vft(void* action) {
    KCDMP_FAULT_READ(site, "combat_actions::set_vft");
    bool done = false;
    fault::guarded(site, [&] {
        void** slot = reinterpret_cast<void**>(action);
        // only an object that still carries the engine's own vftable (never a freed or re-used block)
        void* quiet = &g_quiet[1];
        void* original = const_cast<void*>(static_cast<const void*>(g_actionVft));
        const auto previous = InterlockedCompareExchangePointer(slot, quiet, original);
        done = previous == original || previous == quiet;
        if (previous == original) c_quieted.fetch_add(1, std::memory_order_relaxed);
    });
    return done;
}

bool read_status(void* action, uint32_t* status, void** observedTable = nullptr) {
    void* table = nullptr;
    const bool ok = g_armed.load(std::memory_order_acquire) && rd_ptr(action, 0, &table) &&
        (table == g_actionVft || table == &g_quiet[1]) && rd_u32(action, kActStatus, status) && *status <= 4;
    if (observedTable) *observedTable = table;
    return ok;
}

struct ActionReadback { void* table = nullptr; uint32_t status = UINT32_MAX, refs = 0; };

bool may_write(Bucket& bucket, const Entry& entry, ActionAccess access) {
    if (!entry.serial || entry.queue == QueueStage::Reserved ||
        entry.body.lifecycle != wo216::lifecycle_observation_stamp() || entry.body.load != loadsequence::stamp()) return false;
    const auto* life = find_life(bucket, entry.body.actor);
    if (!life) return false;
    const auto revision = life->revision.load(std::memory_order_acquire);
    const auto releases = bucket.releaseDepth.load(std::memory_order_acquire);
    if (access == ActionAccess::EngineRelease)
        return releases == 1 && revision == entry.body.combatRevision + 1;
    return releases == 0 && revision == entry.body.combatRevision &&
        (access == ActionAccess::RetiredQueue || g_epoch.load(std::memory_order_acquire) == entry.body.queueEpoch);
}

bool read_owned_action(const Entry& entry, ActionReadback& seen) {
    // reserve_queue published a separate, counted reference before admission.
    // The observed table alone does not establish that ownership or its lifetime.
    return entry.serial && read_status(entry.action, &seen.status, &seen.table) && rd_u32(entry.action, kActRefs, &seen.refs) &&
        seen.refs > 0 && seen.refs <= static_cast<uint32_t>((std::numeric_limits<LONG>::max)());
}

void quarantine(Entry& entry) {
    if (entry.retirement == Retirement::Quarantined) return;
    entry.retirement = Retirement::Quarantined;
    c_quarantined.fetch_add(1, std::memory_order_relaxed);
}

void trace(const Entry& entry, const char* stage, const ActionReadback& seen = {}) {
    if (!wo221::on(wo221::kBitDeathTrace)) return;
    crash::note_action({entry.serial, reinterpret_cast<uintptr_t>(entry.action),
        reinterpret_cast<uintptr_t>(seen.table), reinterpret_cast<uintptr_t>(entry.body.actor),
        entry.body.eid, seen.status, seen.refs, stage});
}

bool note_finished(Entry& entry, bool actionKnown, const ActionReadback& seen) {
    if (entry.retirement == Retirement::Finished) return true;
    if (!detail::observe_finished(entry.retirement, actionKnown, seen.status)) return false;
    trace(entry, "finished", seen);
    c_ended.fetch_add(1, std::memory_order_relaxed);
    return true;
}

// Called only for the recorded physical body and owned action. A verified quiet table closes callbacks before Stop,
// but quiet alone is no stop receipt. An unreadable status or unexpected table is never written through.
bool end_entry(Bucket& bucket, Entry& entry, ActionAccess access) {
    if (entry.retirement == Retirement::Finished) return true;
    const auto current = [&] { return may_write(bucket, entry, access); };
    if (!current()) {
        if (entry.retirement != Retirement::Quarantined) trace(entry, "proof-lost");
        quarantine(entry);
        return false;
    }
    ActionReadback seen;
    const bool actionKnown = read_owned_action(entry, seen);
    if (note_finished(entry, actionKnown && current(), seen)) return true;
    if (entry.retirement != Retirement::Active) return false;
    const bool canStop = access != ActionAccess::EngineRelease;
    const bool requested = detail::retire(entry.retirement, actionKnown, seen.status, canStop,
        [&] {
            if (seen.table != &g_quiet[1]) trace(entry, "before-quiet", seen);
            if (!current()) return false;
            const bool quiet = seen.table == &g_quiet[1] || set_vft(entry.action);
            if (quiet && !canStop) c_quietOnly.fetch_add(1, std::memory_order_relaxed);
            return quiet;
        }, [&] {
            trace(entry, "before-stop", seen);
            if (!current()) return false;
            if (!rttr::stop_action(entry.action)) return false;
            c_stopped.fetch_add(1, std::memory_order_relaxed);
            return true;
        }, current);
    if (!requested) c_quarantined.fetch_add(1, std::memory_order_relaxed);
    else c_finishPending.fetch_add(1, std::memory_order_relaxed);
    return false;
}

struct ReleaseEvidence { unsigned waiting = 0, active = 0; };

ReleaseReceipt end_matching(Bucket& bucket, const BodyProof& proof, bool canStop, ReleaseEvidence* evidence = nullptr) {
    bool pending = false, uncertain = false;
    size_t stops = 0;
    const auto access = canStop ? ActionAccess::CurrentBody : ActionAccess::EngineRelease;
    // Stop can reenter the release hook. Before the first engine call, silence every exact unfinished action in this batch.
    if (canStop) for (auto& entry : bucket.entries) {
        if (!entry.action || !detail::same_body(entry.body, proof) || entry.retirement == Retirement::Finished) continue;
        if (entry.queue != QueueStage::Settled) continue;
        if (!may_write(bucket, entry, access)) return ReleaseReceipt::Stale;
        ActionReadback seen;
        const bool readable = read_owned_action(entry, seen);
        if (evidence && readable) {
            if (seen.status == wo167rules::kStatusPending || seen.status == wo167rules::kStatusNone) ++evidence->waiting;
            else if (seen.status == wo167rules::kStatusInstalled || seen.status == wo167rules::kStatusExiting) ++evidence->active;
        }
        if (entry.retirement != Retirement::Active) continue;
        if (!may_write(bucket, entry, access)) return ReleaseReceipt::Stale;
        if (!readable) {
            trace(entry, "unreadable", seen);
            quarantine(entry);
        } else if (seen.status == wo167rules::kStatusFinished) {
            note_finished(entry, true, seen);
        } else if (seen.table != &g_quiet[1]) {
            trace(entry, "before-quiet", seen);
            if (!may_write(bucket, entry, access)) return ReleaseReceipt::Stale;
            if (!set_vft(entry.action)) quarantine(entry);
        }
    }
    for (auto& entry : bucket.entries) {
        if (!entry.action || entry.body.actor != proof.actor) continue;
        BodyProof matching = proof;
        if (!canStop) matching.queueEpoch = entry.body.queueEpoch;  // engine hook owns the physical release, even on disconnect
        if (!detail::same_body(entry.body, matching)) {
            // An older incarnation on this address is untouched, but it prevents claiming that teardown is complete.
            if (entry.retirement != Retirement::Finished) uncertain = true;
            continue;
        }
        if (entry.queue == QueueStage::Reserved || (canStop && entry.queue != QueueStage::Settled && entry.retirement == Retirement::Active)) {
            if (canStop) pending = true;
            continue;
        }
        if (canStop && entry.retirement != Retirement::Finished && !may_write(bucket, entry, access)) {
            quarantine(entry);
            return ReleaseReceipt::Stale;
        }
        if (canStop && entry.retirement == Retirement::Active) {
            if (stops == kReleaseStops) { pending = true; continue; }
            ++stops;
        }
        if (end_entry(bucket, entry, access)) continue;
        if (entry.retirement == Retirement::StopRequested) pending = true;
        else uncertain = true;
    }
    return uncertain ? ReleaseReceipt::Uncertain : pending ? ReleaseReceipt::Pending : ReleaseReceipt::Ready;
}

// C_Actor::ReleaseCombatActor, every vftable's slot: ours first (while the combat part is still there), then the engine's own.
void hk_release_combat_actor(void* actor) {
    c_hook.fetch_add(1, std::memory_order_relaxed);
    const bool main = on_main();
    if (!main) c_offMain.fetch_add(1, std::memory_order_relaxed);
    if (!actor || !g_armed.load(std::memory_order_acquire)) { g_origRelease(actor); return; }
    {
        auto& bucket = bucket_for(actor);
        bucket.releaseDepth.fetch_add(1, std::memory_order_acq_rel);
        if (auto* life = find_life(bucket, actor)) {
            const auto oldRevision = life->revision.fetch_add(1, std::memory_order_acq_rel);
            // Already-owned actions still need protection after a diagnostic switch changes.
            if (c_live.load(std::memory_order_relaxed)) {
                std::unique_lock lock(bucket.gate, std::try_to_lock);
                if (!lock) c_hookBusy.fetch_add(1, std::memory_order_relaxed);
                else {
                    const BodyProof proof = observe_actor_raw(actor, oldRevision);
                    if (proof.combat) {
                        c_withPart.fetch_add(1, std::memory_order_relaxed);
                        // The hook suppresses verified callbacks only. It calls neither Stop nor a blocking registry operation.
                        end_matching(bucket, proof, false);
                    } else if (!detail::known_body(proof)) c_noProof.fetch_add(1, std::memory_order_relaxed);
                }
            }
        }
        g_origRelease(actor);
        bucket.releaseDepth.fetch_sub(1, std::memory_order_release);
    }
}

bool actor_release_hooked(void* actor) {
    void* table = nullptr;
    void* release = nullptr;
    return actor && rd_ptr(actor, 0, &table) && table && rd_ptr(table, kActorVtReleaseCombat, &release) &&
        release == reinterpret_cast<void*>(&hk_release_combat_actor);
}

void fail(const char* why) { logf("WO221-ACTEND NOT ARMED: %s -- 0.65.0's behaviour (an action of ours can outlive its body's combat part)", why); }

void install() {
    HMODULE em = GetModuleHandleA("EntityModule.dll");
    HMODULE cm = GetModuleHandleA("CombatModule.dll");
    if (!em || !cm) { fail("EntityModule.dll or CombatModule.dll not loaded"); return; }
    void* const* actorVft = anchor::find_vftable(em, ".?AVC_Actor@entitymodule@wh@@", 0);
    g_actionVft = anchor::find_vftable(cm, ".?AVC_CombatAnimAction@combatmodule@wh@@", 0);
    if (!actorVft || !g_actionVft) { fail("RTTI C_Actor / C_CombatAnimAction vftable not unique"); return; }

    // the release: reads +0x300, calls the combat part's own shutdown (slot 0x2F0), hands it to the module (slot 0x68), clears +0x300
    const void* rel = actorVft[kActorVtReleaseCombat / 8];
    static const uint8_t pRead[] = {0x48, 0x8B, 0x97, 0x00, 0x03, 0x00, 0x00};                                  // mov rdx,[rdi+300h]
    static const uint8_t pShut[] = {0xFF, 0x90, 0xF0, 0x02, 0x00, 0x00};                                        // call [rax+2F0h]
    static const uint8_t pClear[] = {0x48, 0xC7, 0x87, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00};         // mov qword [rdi+300h],0
    if (!anchor::function_has_bytes(em, rel, pRead, sizeof pRead) || !anchor::function_has_bytes(em, rel, pShut, sizeof pShut) ||
        !anchor::function_has_bytes(em, rel, pClear, sizeof pClear)) { fail("C_Actor slot 0xB60 is not ReleaseCombatActor by its bytes"); return; }
    // the two unguarded callbacks: each reads the entity id (+0x9C) and the actor's combat part (+0x300)
    static const uint8_t pEid14[] = {0x8B, 0x97, 0x9C, 0x00, 0x00, 0x00};                                       // mov edx,[rdi+9Ch]
    static const uint8_t pMgr14[] = {0x48, 0x8B, 0x81, 0x90, 0x04, 0x00, 0x00};                                 // mov rax,[rcx+490h]
    static const uint8_t pEid17[] = {0x8B, 0x95, 0x9C, 0x00, 0x00, 0x00};                                       // mov edx,[rbp+9Ch]
    static const uint8_t pCa17[] = {0x48, 0x8B, 0xB8, 0x00, 0x03, 0x00, 0x00};                                  // mov rdi,[rax+300h]
    if (!anchor::function_has_bytes(cm, g_actionVft[kSlotStarted], pEid14, sizeof pEid14) || !anchor::function_has_bytes(cm, g_actionVft[kSlotStarted], pMgr14, sizeof pMgr14) ||
        !anchor::function_has_bytes(cm, g_actionVft[kSlotEnding], pEid17, sizeof pEid17) || !anchor::function_has_bytes(cm, g_actionVft[kSlotEnding], pCa17, sizeof pCa17)) {
        fail("C_CombatAnimAction slots 14 / 17 are not the callbacks read in this build");
        return;
    }
    // the quiet copy: the engine's own table (its RTTI locator kept, so the object still names its class), three slots silenced
    g_quiet[0] = const_cast<void*>(static_cast<const void*>(g_actionVft[-1]));
    for (size_t i = 0; i < kVtCopy; ++i) g_quiet[1 + i] = const_cast<void*>(g_actionVft[i]);
    g_quiet[1 + kSlotStarted] = reinterpret_cast<void*>(&quiet_slot);
    g_quiet[1 + kSlotEnding] = reinterpret_cast<void*>(&quiet_slot);
    g_quiet[1 + kSlotEvent18] = reinterpret_cast<void*>(&quiet_slot);
    // rm6: an ended action is never deleted either -- a death takes the body's combat part (and its action manager) apart while the
    // controller's queue still holds the action; the block stays allocated (0x1A8 bytes) and the queue's walk finds a quiet action
    g_quiet[1 + kSlotDelete] = reinterpret_cast<void*>(&quiet_slot);

    // every vftable that carries the release (C_Actor and the classes derived from it) gets ours in that slot
    anchor::Range rdata{};
    if (!anchor::section(em, ".rdata", &rdata)) { fail("EntityModule .rdata not found"); return; }
    g_releaseOn.store(wo221::on(wo221::kBitCombatActionEnd), std::memory_order_relaxed);
    g_origRelease = reinterpret_cast<ReleaseFn>(const_cast<void*>(rel));
    int slots = 0, failed = 0;
    for (const uint8_t* p = rdata.begin; p + 8 <= rdata.end; p += 8) {
        void* v = nullptr;
        std::memcpy(&v, p, 8);
        if (v != rel) continue;
        DWORD old = 0;
        void** slot = reinterpret_cast<void**>(const_cast<uint8_t*>(p));
        if (!VirtualProtect(slot, sizeof(void*), PAGE_READWRITE, &old)) { ++failed; continue; }
        InterlockedExchangePointer(slot, reinterpret_cast<void*>(&hk_release_combat_actor));
        DWORD ignored = 0;
        VirtualProtect(slot, sizeof(void*), old, &ignored);
        ++slots;
    }
    if (!slots) { fail("no vftable slot holds ReleaseCombatActor"); return; }
    g_armed = true;
    char d[128]{};
    anchor::describe(rel, d, sizeof d);
    logf("WO221-ACTEND armed: ReleaseCombatActor=%s in %d vftable slot(s)%s; hooks silence verified callbacks 14, 17, 18 before release; "
         "uncertain actions remain referenced (mp_combat_action_end %s)",
         d, slots, failed ? " (some slots could not be written)" : "", wo221::on(wo221::kBitCombatActionEnd) ? "on" : "off");
}

bool scope_current(const BodyProof& body, uint64_t lifecycle, uint64_t load) {
    return body.lifecycle == lifecycle && body.load == load;
}

void forget(Entry& entry) {
    entry = {};
    c_live.fetch_sub(1, std::memory_order_relaxed);
    c_forgotten.fetch_add(1, std::memory_order_relaxed);
}

void forget_unconfirmed(Entry& entry) {
    if (entry.retirement != Retirement::Finished) {
        trace(entry, "scope-retired");
        quarantine(entry);
        note_tracking_gap(entry.body.actor);
    }
    forget(entry);  // retain the reference when the previous world's queue is not known to be done
}

Entry* ticket_entry(Bucket& bucket, const QueueTicket& ticket) {
    for (auto& entry : bucket.entries)
        if (entry.serial == ticket.serial && entry.action == ticket.action) return &entry;
    return nullptr;
}

bool body_fenced(const ActorLife& life, const BodyProof& body) {
    return life.attempt && detail::same_body(life.fenced, body);
}

// Main: one fixed bucket per frame, with a separate ceiling for guarded engine lookups and Stop calls.
// Session/load edges only advance a cutoff; they never iterate tracked actions from the pipe thread.
void tick() {
    note_main_thread();
    g_releaseOn.store(wo221::on(wo221::kBitCombatActionEnd), std::memory_order_relaxed);
    if (!c_live.load(std::memory_order_relaxed)) return;
    const uint64_t lifecycle = wo216::lifecycle_observation_stamp(), load = loadsequence::stamp();
    const uint64_t epoch = g_epoch.load(std::memory_order_acquire), now = GetTickCount64();
    static size_t next = 0;
    auto& bucket = g_buckets[next++ & (kBuckets - 1)];
    struct FinishedAction { void* actor; void* action; };
    std::array<FinishedAction, kSweepReads> done{};
    size_t reads = 0, finished = 0;
    {
        std::unique_lock lock(bucket.gate, std::try_to_lock);
        if (!lock) return;
        for (auto& entry : bucket.entries) {
            if (!entry.action) continue;
            if (!scope_current(entry.body, lifecycle, load)) {
                forget_unconfirmed(entry);
                continue;
            }
            if (reads == kSweepReads || (entry.epoch == epoch && now - entry.trackedAt < kDoneKeepMs)) continue;
            ++reads;
            BodyProof current = observe_body(entry.body.eid);
            current.queueEpoch = entry.body.queueEpoch; // disconnect blocks admission, even while the old bodies are being swept
            if (!detail::same_body(entry.body, current)) {
                forget_unconfirmed(entry);
                continue;
            }
            if (entry.epoch != epoch) {
                end_entry(bucket, entry, ActionAccess::RetiredQueue);
                entry.epoch = epoch;
            }
            if (now - entry.trackedAt < kDoneKeepMs) continue;
            ActionReadback seen;
            if (!read_owned_action(entry, seen)) quarantine(entry);
            else if (seen.status == wo167rules::kStatusFinished && may_write(bucket, entry, ActionAccess::RetiredQueue)) {
                note_finished(entry, true, seen);
                // Status None can still belong to the controller's queue. Only its explicit Finished status can give our ref back.
                done[finished++] = {entry.body.actor, entry.action};
                forget(entry);
            }
        }
    }
    for (size_t i = 0; i < finished; ++i) {
        if (drop_ref(done[i].action)) continue;
        c_quarantined.fetch_add(1, std::memory_order_relaxed);
        note_tracking_gap(done[i].actor);
    }
}

int status(char* out, int n) {
    return std::snprintf(out, static_cast<size_t>(n), "MP-FIX ACT armed=%d on=%d recorded=%u live=%u forgotten=%u release_calls=%u with_part=%u "
                         "off_main=%u stopped=%u ended=%u quiet=%u explicit=%u quarantined=%u hook_busy=%u no_body_proof=%u bucket_full=%u "
                         "retire_scopes=%u quiet_without_stop=%u tracking_gap=%u finish_pending=%u queue_refused=%u queue_uncertain=%u queue_no_release_hook=%u",
                         g_armed.load() ? 1 : 0, g_releaseOn.load() ? 1 : 0, c_tracked.load(), c_live.load(), c_forgotten.load(), c_hook.load(),
                         c_withPart.load(), c_offMain.load(), c_stopped.load(), c_ended.load(), c_quieted.load(), c_explicit.load(), c_quarantined.load(),
                         c_hookBusy.load(), c_noProof.load(), c_full.load(), c_retireScopes.load(), c_quietOnly.load(),
                         c_trackingGap.load(), c_finishPending.load(), c_queueRefused.load(), c_queueUncertain.load(), c_queueMissingHook.load());
}

const bool kReg = wo203::reg_install("combat_actions", &install) && wo203::reg_tick("combat_actions", &tick) && wo221::add_status("combat_actions", &status);

} // namespace

bool armed() { return g_armed.load(); }

BodyProof observe_body(uint32_t eid) {
    if (!eid) return {};
    void* actor = motion::actor_of(eid);
    if (!actor || !ensure_life(bucket_for(actor), actor)) return {};
    const BodyProof body = observe_actor(actor);
    if (body.eid != eid || engine::entity_by_id(eid) != body.entity ||
        !scope_current(body, wo216::lifecycle_observation_stamp(), loadsequence::stamp())) return {};
    return body;
}

QueueTicket reserve_queue(const BodyProof& body, void* action, bool movesBody) {
    const auto refuse = [] { c_queueRefused.fetch_add(1, std::memory_order_relaxed); return QueueTicket{}; };
    if (!action || !body.combat || !g_armed.load(std::memory_order_acquire) ||
        !detail::same_body(body, observe_body(body.eid))) return refuse();
    note_main_thread();
    if (!on_main()) return refuse();
    auto& bucket = bucket_for(body.actor);
    std::unique_lock lock(bucket.gate, std::try_to_lock);
    if (!lock) return refuse();
    const auto* life = find_life(bucket, body.actor);
    if (!life || !detail::same_body(body, observe_actor(body.actor)) || body_fenced(*life, body)) return refuse();
    if (!actor_release_hooked(body.actor)) {
        c_queueMissingHook.fetch_add(1, std::memory_order_relaxed);
        return refuse();
    }
    Entry* free = nullptr;
    for (auto& entry : bucket.entries) {
        if (entry.action == action) return refuse();
        if (!entry.action && !free) free = &entry;
    }
    if (!free) {
        c_full.fetch_add(1, std::memory_order_relaxed);
        return refuse();
    }
    if (!add_ref(action)) return refuse();
    const auto serial = ++g_nextQueue;
    *free = {body, action, GetTickCount64(), body.queueEpoch, Retirement::Active, movesBody, serial};
    c_tracked.fetch_add(1, std::memory_order_relaxed);
    c_live.fetch_add(1, std::memory_order_relaxed);
    return {body, action, serial};
}

bool begin_queue(const QueueTicket& ticket) {
    auto& bucket = bucket_for(ticket.body.actor);
    std::unique_lock lock(bucket.gate, std::try_to_lock);
    if (!lock) return false;
    const auto* life = find_life(bucket, ticket.body.actor);
    if (!life || !detail::same_body(ticket.body, observe_actor(ticket.body.actor)) || body_fenced(*life, ticket.body) ||
        !actor_release_hooked(ticket.body.actor))
        return false;
    auto* entry = ticket_entry(bucket, ticket);
    if (!entry || entry->queue != QueueStage::Reserved || entry->retirement != Retirement::Active) return false;
    entry->queue = QueueStage::InFlight;
    return true;
}

QueueResult settle_queue(const QueueTicket& ticket, QueueResult result) {
    auto& bucket = bucket_for(ticket.body.actor);
    bool release = false;
    {
        std::unique_lock lock(bucket.gate, std::try_to_lock);
        auto* entry = lock ? ticket_entry(bucket, ticket) : nullptr;
        if (!entry) {
            if (result == QueueResult::Refused) {
                c_queueRefused.fetch_add(1, std::memory_order_relaxed);
                return result;
            }
            note_tracking_gap(ticket.body.actor);
            c_queueUncertain.fetch_add(1, std::memory_order_relaxed);
            return QueueResult::Uncertain;
        }
        if (result == QueueResult::Refused && entry->queue == QueueStage::Reserved) {
            c_queueRefused.fetch_add(1, std::memory_order_relaxed);
            forget(*entry);
            release = true;
        } else if (result == QueueResult::Submitted && entry->queue == QueueStage::InFlight &&
                   entry->retirement == Retirement::Active && detail::same_body(ticket.body, observe_actor(ticket.body.actor))) {
            entry->queue = QueueStage::Settled;
            return result;
        } else {
            // QueueAction may have installed before reentry or a fault. The ledger's reference stays and the event is never retried.
            entry->queue = QueueStage::Settled;
            trace(*entry, "queue-uncertain");
            quarantine(*entry);
            c_queueUncertain.fetch_add(1, std::memory_order_relaxed);
            return QueueResult::Uncertain;
        }
    }
    if (release && !drop_ref(ticket.action, true)) c_quarantined.fetch_add(1, std::memory_order_relaxed);
    return result;
}

bool body_pose_holds(uint32_t eid) {
    if (!c_live.load(std::memory_order_relaxed)) return false;
    const BodyProof body = observe_body(eid);
    if (!body.combat) return false;
    auto& bucket = bucket_for(body.actor);
    std::unique_lock lock(bucket.gate, std::try_to_lock);
    if (!lock) return true;  // the current holder retries when the engine's release observation is through
    if (!detail::same_body(body, observe_actor(body.actor))) return false;
    const auto now = GetTickCount64();
    for (auto& entry : bucket.entries) {
        if (!entry.action || !entry.movesBody || !detail::same_body(entry.body, body)) continue;
        uint32_t status = 0;
        if (!read_status(entry.action, &status)) { quarantine(entry); return true; }
        if (status == wo167rules::kStatusInstalled || status == wo167rules::kStatusExiting) return true;
        if (status == wo167rules::kStatusPending || status == wo167rules::kStatusNone) {
            if (now - entry.trackedAt < kWatchMs) return true;
            if (entry.retirement == Retirement::Active) end_entry(bucket, entry, ActionAccess::CurrentBody);
            // A queued action may already be entering the controller when Stop arrives. Hold until it is observed out.
            return true;
        }
        entry.movesBody = false;
    }
    return false;
}

ReleaseReceipt retire_for_body(const BodyProof& body, const char* why, uint64_t attempt) {
    if (!on_main() || !detail::known_body(body) || !detail::same_body(body, observe_body(body.eid))) {
        const auto count = c_noProof.fetch_add(1, std::memory_order_relaxed) + 1;
        if (count <= 4 || count % 256 == 0) logf("WO221-ACTEND eid=0x%X result=quarantined reason=body-identity-unproven why=%s", body.eid, why);
        return ReleaseReceipt::Stale;
    }
    auto& bucket = bucket_for(body.actor);
    ReleaseEvidence evidence;
    bool report = false;
    ReleaseReceipt receipt = ReleaseReceipt::Uncertain;
    {
        std::unique_lock lock(bucket.gate, std::try_to_lock);
        if (!lock) return ReleaseReceipt::Uncertain;
        auto* life = find_life(bucket, body.actor);
        if (!life) return ReleaseReceipt::Uncertain;
        if (!detail::same_body(body, observe_actor(body.actor))) return ReleaseReceipt::Stale;
        if (attempt) {
            if (body_fenced(*life, body) && life->attempt != attempt) return ReleaseReceipt::Uncertain;
            if (!body_fenced(*life, body)) {
                life->fenced = body;
                life->attempt = attempt;
                life->ready = false;
                life->receipt = ReleaseReceipt::Stale;
                report = true;
            }
        }
        receipt = end_matching(bucket, body, true, report ? &evidence : nullptr);
        if (has_tracking_gap(bucket, body.actor)) receipt = ReleaseReceipt::Uncertain;
        if (!detail::same_body(body, observe_actor(body.actor))) receipt = ReleaseReceipt::Stale;
        if (attempt) {
            report = report || life->receipt != receipt;
            if (!life->ready && receipt == ReleaseReceipt::Ready) c_explicit.fetch_add(1, std::memory_order_relaxed);
            life->ready = receipt == ReleaseReceipt::Ready;
            life->receipt = receipt;
        }
    }
    if (report) logf("WO221-ACTEND eid=0x%X attempt=%llu receipt=%u waiting=%u active=%u why=%s", body.eid,
                     static_cast<unsigned long long>(attempt), static_cast<unsigned>(receipt), evidence.waiting, evidence.active, why);
    return receipt;
}

bool release_ready(const BodyProof& body, uint64_t attempt) {
    if (!attempt || !on_main() || !detail::known_body(body) || !detail::same_body(body, observe_body(body.eid))) return false;
    auto& bucket = bucket_for(body.actor);
    std::unique_lock lock(bucket.gate, std::try_to_lock);
    if (!lock) return false;
    const auto* life = find_life(bucket, body.actor);
    return life && life->ready && life->attempt == attempt && body_fenced(*life, body) &&
        !has_tracking_gap(bucket, body.actor) && detail::same_body(body, observe_actor(body.actor));
}

bool abort_release(const BodyProof& body, uint64_t attempt) {
    if (!attempt || !on_main() || !detail::same_body(body, observe_body(body.eid))) return false;
    auto& bucket = bucket_for(body.actor);
    std::unique_lock lock(bucket.gate, std::try_to_lock);
    if (!lock) return false;
    auto* life = find_life(bucket, body.actor);
    if (!life || !detail::same_body(body, observe_actor(body.actor))) return false;
    if (!body_fenced(*life, body)) return true;
    if (life->attempt != attempt) return false;
    life->fenced = {};
    life->attempt = 0;
    life->ready = false;
    life->receipt = ReleaseReceipt::Stale;
    return true;
}

int end_for_body(const BodyProof& body, const char* why) {
    if (!wo221::on(wo221::kBitCombatActionEnd)) return 0;
    return retire_for_body(body, why) == ReleaseReceipt::Ready ? 0 : -1;
}

int end_for_entity(uint32_t eid, const char* why) {
    if (!eid || !wo221::on(wo221::kBitCombatActionEnd)) return 0;
    const auto count = c_noProof.fetch_add(1, std::memory_order_relaxed) + 1;
    if (count <= 4 || count % 256 == 0) logf("WO221-ACTEND eid=0x%X result=refused reason=eid-without-body-proof why=%s", eid, why);
    return -1;
}

int end_all(const char*) {
    g_epoch.fetch_add(1, std::memory_order_acq_rel);
    c_retireScopes.fetch_add(1, std::memory_order_relaxed);
    return 0;
}

} // namespace kcdmp::combatactions
