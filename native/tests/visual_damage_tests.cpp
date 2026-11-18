// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#include "visual_damage_rules.h"

#include <cstdio>

using namespace kcdmp::visualdamage;

int visual_damage_tests(int* passed) {
    int pass = 0, fail = 0;
#define CHECK(condition) do { \
    if (condition) ++pass; \
    else { ++fail; std::printf("FAIL visual damage line %d: %s\n", __LINE__, #condition); } \
} while (0)
    int avatarActor = 0, avatarCombat = 0, replaced = 0;
    constexpr uint32_t avatar = 7, npc = 8, player = 0x7777;
    const Identity registered{avatar, &avatarActor};
    ReplayPermit noReplay;

    int originalCalls = 0;
    auto engineGate = [&](Identity source, uint32_t target, const void* combat, ReplayPermit& replay) {
        const auto decision = decide(registered, source, target, combat, replay);
        if (decision != Contact::Suppressed) ++originalCalls;
        return decision;
    };
    CHECK(engineGate(registered, npc, &avatarCombat, noReplay) == Contact::Suppressed);
    CHECK(engineGate(registered, player, &avatarCombat, noReplay) == Contact::Suppressed);
    CHECK(engineGate(registered, 0, &avatarCombat, noReplay) == Contact::Suppressed);
    CHECK(originalCalls == 0);
    CHECK(engineGate({npc, &replaced}, player, &replaced, noReplay) == Contact::Ordinary);
    CHECK(engineGate({player, &replaced}, avatar, &replaced, noReplay) == Contact::Ordinary);
    CHECK(engineGate({avatar, &replaced}, npc, &replaced, noReplay) == Contact::Ordinary);
    CHECK(engineGate({npc, &avatarActor}, avatar, &avatarCombat, noReplay) == Contact::Ordinary);
    CHECK(engineGate({0, &avatarActor}, npc, &avatarCombat, noReplay) == Contact::Ordinary);
    CHECK(engineGate({avatar, nullptr}, npc, &avatarCombat, noReplay) == Contact::Ordinary);
    CHECK(originalCalls == 6);

    // An authoritative replay allows exactly its original actor and victim.
    // A cosmetic collision nearby must not consume its unrelated permit.
    ReplayPermit hostReplay{avatar, npc, &avatarCombat};
    CHECK(engineGate(registered, player, &avatarCombat, hostReplay) == Contact::Suppressed);
    CHECK(engineGate(registered, npc, &replaced, hostReplay) == Contact::Suppressed);
    CHECK(!hostReplay.used);
    CHECK(engineGate(registered, npc, &avatarCombat, noReplay) == Contact::Suppressed);
    CHECK(engineGate(registered, npc, &avatarCombat, hostReplay) == Contact::Authorized);
    CHECK(hostReplay.used);
    CHECK(engineGate(registered, npc, &avatarCombat, hostReplay) == Contact::Suppressed);
    CHECK(originalCalls == 7);

    ReplayPermit victimReplay{avatar, player, &avatarCombat};
    CHECK(engineGate(registered, player, &avatarCombat, victimReplay) == Contact::Authorized);
    CHECK(engineGate(registered, player, &avatarCombat, victimReplay) == Contact::Suppressed);
    CHECK(originalCalls == 8);
    *passed = pass;
    return fail;
#undef CHECK
}
