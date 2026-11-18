// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#include "holders.h"

#include <cstdio>

using namespace kcdmp::holders;

namespace {

constexpr uint32_t kBody = 7;
constexpr uint32_t kOther = 8;
constexpr Scope kSession{2, 3};

#define HCHECK(condition) do { \
    if (condition) ++pass; \
    else { ++fail; std::printf("FAIL holder contract line %d: %s\n", __LINE__, #condition); } \
} while (0)

bool write_once(Table& table, uint32_t eid, Holder owner, const char* path, Claim claim = Claim::Available) {
    const auto lease = table.acquire(eid, owner, path, claim);
    if (!lease) return false;
    const bool wrote = table.wrote(eid, owner, path, lease);
    table.finish(eid, lease);
    return wrote;
}

} // namespace

int holder_contract_tests(int* passed) {
    int pass = 0, fail = 0;
    Claims claims;
    HCHECK(decide(claims) == Holder::Writer);
    claims.activity = true;
    HCHECK(decide(claims) == Holder::Activity);
    claims.seat = true;
    HCHECK(decide(claims) == Holder::Seat);
    claims.probe = true;
    HCHECK(decide(claims) == Holder::Probe);
    claims.oneShot = true;
    HCHECK(decide(claims) == Holder::OneShot);
    claims.mount = true;
    HCHECK(decide(claims) == Holder::Mount);
    claims.fall = true;
    HCHECK(decide(claims) == Holder::Fall);

    {
        // Walking to sitting. A lease protects the synchronous call and the
        // held movement request remains active until engine stop is confirmed.
        Table table;
        table.synchronize(kSession, 1);
        HCHECK(table.select(kBody, Holder::Driver, 1).changed);
        const auto walk = table.acquire(kBody, Holder::Driver, "walk", Claim::Held);
        HCHECK(walk != 0);
        HCHECK(!table.select(kBody, Holder::Seat, 1).selected);
        HCHECK(!table.acquire(kBody, Holder::Driver, "walk", Claim::Held));
        HCHECK(!table.forget(kBody));
        HCHECK(write_once(table, kOther, Holder::Figure, "other-body"));
        HCHECK(table.wrote(kBody, Holder::Driver, "walk", walk));
        table.finish(kBody, walk);
        table.synchronize(kSession, 2);
        HCHECK(!table.select(kBody, Holder::Seat, 2).selected);
        HCHECK(!table.acquire(kBody, Holder::Writer, "legacy-stream", Claim::Available));
        HCHECK(!table.forget(kBody));
        table.engine_stopped(kBody, Holder::Route);
        HCHECK(!table.select(kBody, Holder::Seat, 2).selected);
        const auto stop = table.acquire(kBody, Holder::Driver, "walk", Claim::Release);
        HCHECK(stop != 0 && table.wrote(kBody, Holder::Driver, "walk", stop));
        table.engine_stopped(kBody, Holder::Driver);
        HCHECK(!table.select(kBody, Holder::Seat, 2).selected);
        table.finish(kBody, stop);
        table.engine_stopped(kBody, Holder::Driver);
        const auto seated = table.select(kBody, Holder::Seat, 2);
        HCHECK(seated.selected && seated.changed && seated.previous == Holder::Driver);
        HCHECK(write_once(table, kBody, Holder::Seat, "seat-align"));
        HCHECK(!table.acquire(kBody, Holder::Writer, "legacy-stream", Claim::Available));
        HCHECK(table.stats().handovers == 1 && table.stats().violations == 0);
    }

    {
        // Fight to KO/death, ground to standing, and mounting to dismounting.
        Table table;
        table.synchronize(kSession, 1);
        HCHECK(table.select(kBody, Holder::OneShot, 1).selected);
        HCHECK(write_once(table, kBody, Holder::OneShot, "combat"));
        HCHECK(table.select(kBody, Holder::Fall, 1).previous == Holder::OneShot);
        HCHECK(write_once(table, kBody, Holder::Fall, "ragdoll"));
        HCHECK(!table.acquire(kBody, Holder::Figure, "figure", Claim::Available));
        table.synchronize(kSession, 2);
        HCHECK(write_once(table, kBody, Holder::Fall, "stand-up"));
        HCHECK(table.select(kBody, Holder::Figure, 2).previous == Holder::Fall);
        HCHECK(write_once(table, kBody, Holder::Figure, "figure"));
        HCHECK(table.select(kBody, Holder::Mount, 3).previous == Holder::Figure);
        HCHECK(write_once(table, kBody, Holder::Mount, "mount"));
        HCHECK(!table.acquire(kBody, Holder::Figure, "figure", Claim::Available));
        table.synchronize(kSession, 3);
        HCHECK(write_once(table, kBody, Holder::Mount, "dismount"));
        HCHECK(table.select(kBody, Holder::Writer, 3).previous == Holder::Mount);
        HCHECK(write_once(table, kBody, Holder::Writer, "stream"));
        HCHECK(table.stats().handovers == 4 && table.stats().violations == 0);
    }

    {
        // Same-class candidates cannot evade the fence by reselecting the
        // holder. A local setter's matching follow-up can use its own path ID.
        Table table;
        table.synchronize(kSession, 1);
        HCHECK(table.select(kBody, Holder::Figure, 1).changed);
        HCHECK(write_once(table, kBody, Holder::Figure, "new-figure"));
        HCHECK(!table.select(kBody, Holder::Figure, 1).changed);
        HCHECK(!table.acquire(kBody, Holder::Figure, "legacy-figure", Claim::Available));
        HCHECK(table.forget(kBody));
        HCHECK(!table.acquire(kBody, Holder::Figure, "legacy-figure", Claim::Available));
        HCHECK(write_once(table, kOther, Holder::Respawn, "respawn"));
        HCHECK(write_once(table, kOther, Holder::Respawn, "respawn"));
        HCHECK(!table.acquire(kOther, Holder::Leash, "joinplace", Claim::Available));
        HCHECK(!table.acquire(kOther + 1, Holder::Route, "route", Claim::Held));
        HCHECK(table.size() == 2);
        table.synchronize(kSession, 2);
        HCHECK(table.size() == 0);
        HCHECK(write_once(table, kBody, Holder::Figure, "legacy-figure"));
        HCHECK(write_once(table, kOther, Holder::Leash, "joinplace"));
        HCHECK(table.stats().denied_sets == 4 && table.stats().violations == 0);
    }

    {
        // Scope changes retire in-flight writes and long-lived movement until
        // its old engine owner has evidence of cancellation or disappearance.
        Table table;
        table.synchronize(kSession, 1);
        HCHECK(table.select(kBody, Holder::Route, 1).selected);
        HCHECK(write_once(table, kBody, Holder::Route, "route", Claim::Held));
        const auto local = table.acquire(kOther, Holder::Unstuck, "unstuck", Claim::Available);
        HCHECK(local != 0);
        table.synchronize({kSession.lifecycle + 1, kSession.connection}, 2);
        HCHECK(!table.select(kBody, Holder::Writer, 2).selected);
        HCHECK(!table.acquire(kBody, Holder::Route, "route", Claim::Held));
        HCHECK(!table.acquire(kOther, Holder::Leash, "joinplace", Claim::Available));
        HCHECK(table.wrote(kOther, Holder::Unstuck, "unstuck", local));
        table.finish(kOther, local);
        HCHECK(write_once(table, kOther, Holder::Leash, "joinplace"));
        table.synchronize({kSession.lifecycle + 1, kSession.connection + 1}, 3);
        HCHECK(table.size() == 1 && table.owner(kBody) == Holder::Route);
        HCHECK(write_once(table, kBody, Holder::Route, "route", Claim::Release));
        HCHECK(!table.select(kBody, Holder::Writer, 3).selected);
        table.engine_stopped(kBody, Holder::Route);
        HCHECK(table.select(kBody, Holder::Writer, 3).selected);
        HCHECK(write_once(table, kBody, Holder::Writer, "stream"));
        HCHECK(table.stats().violations == 0);
    }

    {
        // A denial ends before any effect. Only a write reported outside the
        // admitted entity, path and lease is an owner violation.
        Table table;
        table.synchronize(kSession, 1);
        const auto lease = table.acquire(kBody, Holder::Figure, "current", Claim::Available);
        HCHECK(lease != 0 && !table.acquire(kBody, Holder::Writer, "legacy", Claim::Available));
        HCHECK(table.stats().denied_sets == 1 && table.stats().violations == 0);
        HCHECK(!table.wrote(kBody, Holder::Figure, "legacy", lease));
        HCHECK(!table.wrote(kBody, Holder::Writer, "current", lease));
        HCHECK(!table.wrote(kOther, Holder::Figure, "current", lease));
        HCHECK(table.wrote(kBody, Holder::Figure, "current", lease));
        table.finish(kBody, lease);
        HCHECK(!table.wrote(kBody, Holder::Figure, "current", lease));
        HCHECK(table.stats().denied_sets == 1 && table.stats().violations == 4);
    }
    *passed = pass;
    return fail;
}
