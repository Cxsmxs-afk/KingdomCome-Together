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
    bool gameplay = true, seenFinished = false, releaseProof = true, guardOff = true, invokeAck = true;
    bool combatCurrent = true, stopRequested = false, reusesOnInvoke = false;
    Life currentLife = Life::Alive;
    std::function<void()> onStop;
    std::vector<Stage> stages;

    bool scope_current() const { return gameplay; }
    int resolve(const Request& request) { ++resolves; return request.soul[0] == 7 && (!request.eid || request.eid == 17) ? body : 0; }
    bool current(int proof, const Request&, bool before) const {
        return scope_current() && proof != 0 && body == proof && (!before || combatCurrent);
    }
    Retirement retire(int, uint64_t) {
        if (seenFinished) return Retirement::Ready;
        if (!stopRequested) { stopRequested = true; ++stops; if (onStop) onStop(); }
        return Retirement::Pending;
    }
    bool ready(int, uint64_t) const { return releaseProof; }
    Life life(int) const { return currentLife; }
    bool prepare(int, const Request&, uint64_t) { ++prepares; return guardOff; }
    bool apply(int, const Request&) {
        ++writes;
        if (reusesOnInvoke) ++body;
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
        Attempts<int> attempts;
        engine.onStop = [&] {
            check(attempts.step(request, scope, engine) == Receipt::Uncertain && engine.writes == 0,
                  "reentrant observation cannot prepare or write while the engine is in Stop");
        };
        check(attempts.step(request, scope, engine) == Receipt::Pending && engine.stops == 1 && engine.prepares == 0 && engine.writes == 0,
              "named and GUID death hold one attempt until Finished is observed");
        check(attempts.step(request, scope, engine) == Receipt::Pending && engine.resolves == 1 && engine.stops == 1 && engine.writes == 0,
              "pending preserves identity and observes without a second Stop");
        engine.seenFinished = true;
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
        check(attempts.step(named, scope, engine) == Receipt::Stale && engine.prepares == 0 && engine.writes == 0,
              "release lost between Ready and the guard clearance blocks all writes");
    }
    {
        Engine engine;
        engine.seenFinished = true;
        engine.invokeAck = false;
        Attempts<int> attempts;
        check(attempts.step(guid, scope, engine) == Receipt::Uncertain && engine.writes == 1 && engine.confirmed == 0,
              "no acknowledgement from the engine leaves the dispatch uncertain");
        check(attempts.step(guid, scope, engine) == Receipt::Uncertain && engine.writes == 1,
              "a missing acknowledgement is never a new dispatch");
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
        auto wrong = named;
        wrong.soul[0] = 8;
        check(attempts.step(wrong, scope, engine) == Receipt::Stale && engine.resolves == 1,
              "same attempt can't be moved to a different GUID");
    }
    return failed;
}
