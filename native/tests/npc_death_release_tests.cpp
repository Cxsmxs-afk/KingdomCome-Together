// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#include "npc_death_release.h"

#include <cstdio>
#include <functional>
#include <limits>
#include <vector>

namespace {
using namespace kcdmp::npcdeath;

struct Engine {
    int body = 8, resolves = 0, stops = 0, prepares = 0, writes = 0, confirmed = 0;
    int aborts = 0, holdChanges = 0, followupActions = 0;
    mutable int lifeReads = 0;
    bool gameplay = true, seenFinished = false, releaseProof = true, guardOff = true, invokeAck = true;
    bool combatCurrent = true, stopRequested = false, reusesOnInvoke = false;
    bool fence = false, mayAbort = true;
    bool bodyReadable = true, hideOnInvoke = false, refuseBeforePreparation = false;
    uint64_t nowMs = 0, finishAtMs = 0;
    Life currentLife = Life::Alive;
    std::function<void()> onStop;
    std::function<void()> onPrepare;
    std::vector<Stage> stages;

    bool scope_current() const { return gameplay; }
    int resolve(const Request& request) { ++resolves; return request.soul[0] == 7 && (!request.eid || request.eid == 17) ? body : 0; }
    bool current(int proof, const Request&, bool before) const {
        return scope_current() && bodyReadable && proof != 0 && body == proof && (!before || combatCurrent);
    }
    Retirement retire(int, uint64_t) {
        fence = true;
        if (seenFinished || (finishAtMs && nowMs >= finishAtMs)) return Retirement::Ready;
        if (!stopRequested) { stopRequested = true; ++stops; if (onStop) onStop(); }
        return Retirement::Pending;
    }
    bool ready(int, uint64_t) const { return releaseProof; }
    bool abort(int, uint64_t) {
        ++aborts;
        if (!mayAbort) return false;
        fence = false;
        return true;
    }
    Life life(int) const { ++lifeReads; return currentLife; }
    Preparation prepare(int, const Request&, uint64_t) {
        ++prepares;
        if (onPrepare) onPrepare();
        if (refuseBeforePreparation) return Preparation::Unchanged;
        ++holdChanges;
        return guardOff ? Preparation::Ready : Preparation::Uncertain;
    }
    bool queue_followup(int proof) {
        if (fence || proof != body || currentLife != Life::Alive || followupActions) return false;
        ++followupActions;
        return true;
    }
    bool apply(int, const Request&) {
        ++writes;
        if (reusesOnInvoke) ++body;
        if (hideOnInvoke) bodyReadable = false;
        return invokeAck;
    }
    void confirm(int, const Request&) { ++confirmed; }
    void report(int, const Request&, Stage stage) { stages.push_back(stage); }
};
}

int npc_death_release_tests(int* passed) {
    int failed = 0;
    auto check = [&](bool result, const char* name) {
        if (result) ++*passed;
        else { ++failed; std::printf("FAIL H6 death boundary: %s\n", name); }
    };
    const Scope scope{3, 4, 5, 6};
    const Request named{{7}, 17, scope.lifecycle, 1};
    const Request guid{{7}, 0, scope.lifecycle, 2};

    float allowed = -1.0f;
    check(nonlethal_health(9.0f, 4.8f, allowed) && allowed > 3.79f && allowed < 3.81f,
          "a replicated numeric hit cannot apply its lethal requested amount to the measured NPC");
    check(nonlethal_health(2.0f, 1.0f, allowed) && allowed == 0.0f, "one HP leaves no room for numeric health damage");
    check(!nonlethal_health(2.0f, std::numeric_limits<float>::quiet_NaN(), allowed) &&
          !nonlethal_health(2.0f, -1.0f, allowed), "unreadable health never defaults to the full delta");

    for (const auto& request : {named, guid}) {
        Engine engine;
        engine.finishAtMs = 1200;
        Attempts<int> attempts;
        engine.onStop = [&] {
            check(attempts.step(request, scope, engine) == Receipt::Uncertain && engine.writes == 0,
                  "reentrant observation cannot prepare or write while the engine is in Stop");
        };
        check(attempts.step(request, scope, engine) == Receipt::Pending && engine.stops == 1 && engine.prepares == 0 && engine.writes == 0,
              "named and GUID death hold one attempt until Finished is observed");
        for (auto when : {1000u, 1199u}) {
            engine.nowMs = when;
            check(attempts.step(request, scope, engine) == Receipt::Pending && engine.resolves == 1 &&
                  engine.stops == 1 && engine.writes == 0,
                  "pending survives more than one second without another Stop or a write");
        }
        engine.nowMs = 1200;
        check(attempts.step(request, scope, engine) == Receipt::Pending && engine.prepares == 1 && engine.writes == 1 && engine.confirmed == 0,
              "Ready admits one mutation and a damage acknowledgement cannot confirm death");
        engine.combatCurrent = false;
        check(attempts.step(request, scope, engine) == Receipt::Pending && engine.writes == 1,
              "the corpse readback survives the death's own combat release and never writes again");
        engine.currentLife = Life::Dead;
        check(attempts.step(request, scope, engine) == Receipt::Applied && engine.confirmed == 1,
              "same-body IsDead readback is the only successful death receipt");
        check(attempts.step(request, scope, engine) == Receipt::Applied && engine.writes == 1 && engine.confirmed == 1 &&
              engine.stages == std::vector<Stage>{Stage::ReleasePending, Stage::Ready, Stage::DeathPending, Stage::Applied},
              "a duplicate observes the original single terminal release and engine outcome");
    }

    for (int transition : {0, 1, 2, 3}) {
        Engine engine;
        Attempts<int> attempts;
        check(attempts.step(named, scope, engine) == Receipt::Pending, "retirement is outstanding before a scope edge");
        if (transition == 0) ++engine.body; // despawn/reused EID
        if (transition == 1) engine.combatCurrent = false; // LOD release
        if (transition >= 2) engine.gameplay = false; // load or pipe/connection retired
        engine.seenFinished = true;
        check(attempts.step(named, scope, engine) == Receipt::Stale && engine.writes == 0 && engine.prepares == 0,
              "late Finished after reuse, LOD, Load or Disconnect cannot prepare or mutate the body");
    }

    {
        Engine engine;
        engine.seenFinished = true;
        engine.releaseProof = false;
        Attempts<int> attempts;
        check(attempts.step(named, scope, engine) == Receipt::Uncertain && engine.prepares == 0 && engine.writes == 0,
              "release lost between Ready and the guard clearance blocks all writes");
    }
    {
        Engine engine;
        engine.seenFinished = true;
        engine.invokeAck = false;
        Attempts<int> attempts;
        check(attempts.step(guid, scope, engine) == Receipt::Uncertain && engine.writes == 1 && engine.confirmed == 0,
              "no acknowledgement from the engine leaves the dispatch uncertain");
        check(attempts.step(guid, scope, engine, Intent::ObserveOnly) == Receipt::Uncertain && engine.writes == 1,
              "a missing acknowledgement is never a new dispatch");
        check(attempts.step(guid, scope, engine, Intent::AbortBeforeApply) == Receipt::Uncertain && engine.fence && engine.aborts == 0,
              "abort cannot relinquish the fence after an uncertain invocation");
        engine.currentLife = Life::Dead;
        check(attempts.step(guid, scope, engine, Intent::ObserveOnly) == Receipt::Applied && engine.writes == 1 && engine.confirmed == 1,
              "an uncertain invoke can later confirm death under the original attempt without a second write");
    }
    {
        Engine engine;
        engine.seenFinished = true;
        engine.reusesOnInvoke = true;
        engine.currentLife = Life::Alive;
        Attempts<int> attempts;
        check(attempts.step(named, scope, engine) == Receipt::Uncertain && engine.writes == 1 && engine.confirmed == 0,
              "reentrant body replacement during lethal invoke is not success");
        check(attempts.step(named, scope, engine) == Receipt::Uncertain && engine.writes == 1,
              "later duplicate cannot mutate the replacement");
    }
    {
        Engine engine;
        engine.seenFinished = true;
        engine.hideOnInvoke = true;
        Attempts<int> attempts;
        check(attempts.step(named, scope, engine) == Receipt::Uncertain && engine.writes == 1,
              "a temporarily unreadable body after invoke keeps the same observation pending");
        engine.bodyReadable = true;
        engine.currentLife = Life::Dead;
        check(attempts.step(named, scope, engine, Intent::ObserveOnly) == Receipt::Applied &&
              engine.writes == 1 && engine.confirmed == 1, "the original body can confirm after temporary unreadability");
    }
    {
        Engine engine;
        engine.seenFinished = true;
        engine.currentLife = Life::Dead;
        Attempts<int> attempts;
        check(attempts.step(guid, scope, engine) == Receipt::Applied && engine.prepares == 0 && engine.writes == 0,
              "already dead is idempotent, without guard clearance or lethal write");
        Request replacement = guid;
        replacement.attempt += 512;
        check(attempts.step(replacement, scope, engine) == Receipt::Applied && attempts.step(guid, scope, engine) == Receipt::Stale && engine.writes == 0,
              "aged-out attempts cannot reenter the bounded receipt table");
        ++engine.body;
        check(attempts.step(replacement, scope, engine) == Receipt::Stale && engine.confirmed == 2 && engine.writes == 0,
              "a cached success is never proof of a replacement body's death");
    }
    {
        Engine engine;
        engine.seenFinished = true;
        engine.guardOff = false;
        Attempts<int> attempts;
        check(attempts.step(named, scope, engine) == Receipt::Uncertain && engine.prepares == 1 && engine.writes == 0,
              "a refused or unreadable guard result leaves the bound body protected");
        check(attempts.step(named, scope, engine, Intent::AbortBeforeApply) == Receipt::Uncertain && engine.aborts == 0 && engine.fence,
              "abort is not confirmed after guard preparation may have changed combat holds");
        auto wrong = named;
        wrong.soul[0] = 8;
        check(attempts.step(wrong, scope, engine) == Receipt::Stale && engine.resolves == 1,
              "same attempt can't be moved to a different GUID");
    }
    {
        Engine engine;
        engine.seenFinished = true;
        engine.refuseBeforePreparation = true;
        engine.mayAbort = false;
        Attempts<int> attempts;
        check(attempts.step(named, scope, engine) == Receipt::Uncertain && engine.prepares == 1 &&
              engine.fence && engine.writes == 0, "loss of the pre-mutation preparation proof holds the attempt");
        engine.refuseBeforePreparation = false;
        check(attempts.step(named, scope, engine) == Receipt::Uncertain &&
              engine.prepares == 1 && engine.writes == 0, "a late Advance cannot restart failed preparation");
        check(attempts.step(named, scope, engine, Intent::AbortBeforeApply) == Receipt::Uncertain &&
              engine.fence, "missing release ownership cannot clear the fence");
        engine.mayAbort = true;
        check(attempts.step(named, scope, engine, Intent::AbortBeforeApply) == Receipt::Aborted &&
              engine.aborts == 3 && !engine.fence && engine.writes == 0,
              "proved unchanged body and attempt can abort when no preparation mutation began");
        check(attempts.step(named, scope, engine) == Receipt::Aborted && engine.prepares == 1 &&
              engine.writes == 0, "a proved abort seals the original attempt against late Advance");
        auto later = named;
        later.attempt += 2;
        check(attempts.step(later, scope, engine) == Receipt::Pending && engine.prepares == 2 &&
              engine.writes == 1, "the new attempt can perform the sole lethal dispatch after a proved abort");
    }
    {
        Engine engine;
        Attempts<int> attempts;
        check(attempts.step(named, scope, engine, Intent::ObserveOnly) == Receipt::Uncertain &&
              engine.resolves == 0 && engine.stops == 0, "an observation cannot admit a previously unseen attempt");
        check(attempts.step(named, scope, engine) == Receipt::Pending && engine.fence && engine.stops == 1,
              "a pending action holds its death attempt fence");
        engine.seenFinished = true;
        check(attempts.step(named, scope, engine, Intent::ObserveOnly) == Receipt::Uncertain && engine.prepares == 0 &&
              engine.writes == 0, "observation after a lost reply does not cross the mutation boundary when Finished arrives");
        check(attempts.step(named, scope, engine, Intent::AbortBeforeApply) == Receipt::Aborted &&
              engine.aborts == 1 && !engine.fence && engine.writes == 0, "confirmed living-body abort releases the attempt fence");
        check(attempts.step(named, scope, engine) == Receipt::Aborted && engine.writes == 0 && !engine.fence,
              "a late Advance sees the abort tombstone and cannot restore the fence");
        check(attempts.step(guid, scope, engine, Intent::AbortBeforeApply) == Receipt::Aborted &&
              attempts.step(guid, scope, engine) == Receipt::Aborted && engine.writes == 0 &&
              engine.resolves == 1 && engine.aborts == 1 && engine.stages.back() == Stage::AbortedUnadmitted,
              "an abort before first admission seals its ID without resolving or touching any body");
    }
    {
        Engine engine;
        Attempts<int> attempts;
        check(attempts.step(named, scope, engine) == Receipt::Pending, "the action is awaiting retirement before abort");
        engine.currentLife = Life::Unreadable;
        check(attempts.step(named, scope, engine, Intent::AbortBeforeApply) == Receipt::Uncertain &&
              engine.aborts == 0 && engine.fence, "unreadable life cannot confirm cancellation or reopen enqueue");
        engine.currentLife = Life::Alive;
        ++engine.body;
        check(attempts.step(named, scope, engine, Intent::AbortBeforeApply) == Receipt::Stale &&
              engine.aborts == 0 && engine.fence, "an EID reused before abort does not touch another body's fence");
    }
    {
        Engine engine;
        engine.seenFinished = true;
        engine.refuseBeforePreparation = true;
        Attempts<int> attempts;
        const auto result = attempts.step(named, scope, engine);
        std::printf("H6 prep unchanged: receipt=%u fence=%d lethal_calls=%d abort_calls=%d\n",
            static_cast<unsigned>(result), engine.fence, engine.writes, engine.aborts);
        check(result == Receipt::Aborted && !engine.fence && engine.holdChanges == 0 &&
              engine.aborts == 1 && engine.writes == 0,
              "a refusal before the first prepare writer reopens only the proven live original binding");
        check(engine.queue_followup(engine.body) && !engine.queue_followup(engine.body) && engine.followupActions == 1,
              "same verified living body admits one new action after its fence was lifted");
        check(attempts.step(named, scope, engine, Intent::AbortBeforeApply) == Receipt::Aborted &&
              attempts.step(named, scope, engine) == Receipt::Aborted && engine.aborts == 1 &&
              !engine.fence && engine.writes == 0,
              "late commands cannot reopen the aborted death attempt or clear a fence a second time");
    }
    {
        Engine engine;
        engine.seenFinished = true;
        engine.refuseBeforePreparation = true;
        engine.onPrepare = [&] { engine.currentLife = Life::Unreadable; };
        Attempts<int> attempts;
        check(attempts.step(guid, scope, engine) == Receipt::Uncertain && engine.fence &&
              engine.aborts == 0 && engine.holdChanges == 0,
              "preparation did not write but an unreadable body cannot yet be reopened");
        engine.currentLife = Life::Alive;
        check(attempts.step(guid, scope, engine) == Receipt::Uncertain &&
              attempts.step(guid, scope, engine, Intent::ObserveOnly) == Receipt::Uncertain &&
              engine.prepares == 1 && engine.writes == 0,
              "a late Advance and an observation cannot retry the failed preparation");
        check(attempts.step(guid, scope, engine, Intent::AbortBeforeApply) == Receipt::Aborted &&
              engine.aborts == 1 && engine.queue_followup(engine.body) && engine.writes == 0,
              "a later explicit abort can prove recovery after an earlier read failure before mutation");
    }
    {
        Engine engine;
        engine.seenFinished = true;
        engine.refuseBeforePreparation = true;
        engine.onPrepare = [&] { ++engine.body; };
        Attempts<int> attempts;
        check(attempts.step(named, scope, engine) == Receipt::Uncertain && engine.fence && engine.aborts == 0,
              "pre-writer recovery cannot relax the fence when the original body has already changed");
        check(attempts.step(named, scope, engine, Intent::AbortBeforeApply) == Receipt::Stale &&
              engine.aborts == 0 && !engine.queue_followup(engine.body) && engine.writes == 0,
              "late explicit abort with reused EID cannot reopen the replacement or invoke lethal damage");
    }
    {
        Engine engine;
        engine.seenFinished = true;
        engine.guardOff = false;
        Attempts<int> attempts;
        check(attempts.step(named, scope, engine) == Receipt::Uncertain && engine.holdChanges == 1 &&
              engine.fence && engine.writes == 0, "guard refusal after an engagement mutation stays fenced");
        const int before = engine.lifeReads;
        check(attempts.step(named, scope, engine, Intent::ObserveOnly) == Receipt::Uncertain &&
              engine.lifeReads == before + 1 && engine.prepares == 1 && engine.writes == 0,
              "partially mutated preparation still allows read-only observation of the original body");
        engine.currentLife = Life::Dead;
        check(attempts.step(named, scope, engine, Intent::ObserveOnly) == Receipt::Uncertain &&
              engine.confirmed == 0 && engine.writes == 0 && engine.fence &&
              engine.stages.back() == Stage::PrepareObservedDead,
              "a later death after a failed preparation is not credited to an uninvoked lethal mutation");
        check(attempts.step(named, scope, engine, Intent::AbortBeforeApply) == Receipt::Uncertain &&
              engine.aborts == 0 && engine.prepares == 1 && !engine.queue_followup(engine.body),
              "a late abort does not reopen a potentially partially modified body");
    }
    {
        Engine engine;
        engine.seenFinished = true;
        engine.onPrepare = [&] { engine.releaseProof = false; };
        Attempts<int> attempts;
        check(attempts.step(guid, scope, engine) == Receipt::Uncertain && engine.holdChanges == 1 &&
              engine.fence && engine.writes == 0,
              "losing retirement proof after successful preparation is still potentially mutated");
        const int before = engine.lifeReads;
        engine.gameplay = false;
        check(attempts.step(guid, scope, engine, Intent::ObserveOnly) == Receipt::Stale &&
              attempts.step(guid, scope, engine, Intent::AbortBeforeApply) == Receipt::Stale &&
              engine.lifeReads == before && engine.aborts == 0 && engine.writes == 0,
              "scope retirement stops reads and writes from delayed observer or abort requests");
    }
    return failed;
}
