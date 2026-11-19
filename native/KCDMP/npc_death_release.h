// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#pragma once

#include <array>
#include <cmath>
#include <cstddef>
#include <cstdint>

namespace kcdmp::npcdeath {

using Guid = std::array<uint8_t, 16>;
struct Request { Guid soul{}; uint32_t eid = 0; uint64_t lifecycle = 0, attempt = 0; };

struct Scope {
    uint64_t connection = 0, lifecycle = 0, load = 0, playerLife = 0;

    bool operator==(const Scope& other) const {
        return connection == other.connection && lifecycle == other.lifecycle &&
            load == other.load && playerLife == other.playerLife;
    }
};

enum class Intent : uint8_t { Advance, ObserveOnly, AbortBeforeApply };
enum class Receipt : uint8_t { Applied = 0, Pending = 0x70, Uncertain = 0x71, Stale = 0x72, Aborted = 0x73 };
enum class Retirement { Ready, Pending, Uncertain, Stale };
enum class Preparation { Ready, Unchanged, Uncertain };
enum class Life { Alive, Dead, Unreadable };
enum class Stage {
    ReleasePending, ReleaseUncertain, Ready, PrepareUnchanged, PrepareUncertain, PrepareObservedDead,
    DeathPending, ApplyUncertain, Applied, Uncertain, Stale, Aborted, AbortedUnadmitted
};

inline bool nonlethal_health(float requested, float observed, float& allowed) {
    if (!std::isfinite(requested) || !std::isfinite(observed) || requested < 0.0f || observed < 0.0f) return false;
    const float headroom = observed > 1.0f ? observed - 1.0f : 0.0f;
    allowed = requested < headroom ? requested : headroom;
    return true;
}

// Called on Main. One slot per recent attempt, retained across Pending polls. Old tokens cannot be admitted again
// after a slot is reused. An unresolved dispatch retains the body fence even when its receipt ages out.
template <class Body>
class Attempts {
    static constexpr std::size_t kWindow = 512;
    enum class Phase { Release, Busy, ObservePreparation, Observe, Done };

    struct Entry {
        Scope scope{};
        Request request{};
        Body body{};
        Phase phase = Phase::Release;
        Receipt receipt = Receipt::Stale;
        bool reportedPending = false;
        bool advanceBlocked = false;
    };

    std::array<Entry, kWindow> entries_{};
    Scope scope_{};
    uint64_t highest_ = 0;

public:
    template <class Backend>
    Receipt step(const Request& request, const Scope& scope, Backend& backend, Intent intent = Intent::Advance) {
        if (!request.attempt || request.soul == Guid{} || request.lifecycle != scope.lifecycle || !backend.scope_current())
            return Receipt::Stale;
        if (!(scope == scope_)) { scope_ = scope; highest_ = 0; }
        Entry& entry = entries_[request.attempt % kWindow];
        const bool sameScope = entry.scope == scope;
        if (!sameScope || entry.request.attempt != request.attempt) {
            if (highest_ >= kWindow && request.attempt <= highest_ - kWindow) return Receipt::Stale;
            if (intent == Intent::ObserveOnly || entry.phase == Phase::Busy ||
                (sameScope && request.attempt < entry.request.attempt))
                return Receipt::Uncertain;
            highest_ = request.attempt > highest_ ? request.attempt : highest_;
            if (intent == Intent::AbortBeforeApply) {
                // A request that never started owns no body fence. Seal only its scoped ID against late Advance.
                entry = {scope, request, {}, Phase::Done, Receipt::Aborted};
                backend.report(entry.body, request, Stage::AbortedUnadmitted);
                return Receipt::Aborted;
            }
            entry = {scope, request, {}, Phase::Busy};
            entry.body = backend.resolve(request);
            entry.phase = Phase::Release;
        } else if (entry.request.soul != request.soul || entry.request.eid != request.eid) return Receipt::Stale;

        auto report = [&](Stage stage) { backend.report(entry.body, request, stage); };
        auto finish = [&](Receipt value, Stage stage) {
            entry.phase = Phase::Done;
            entry.receipt = value;
            report(stage);
            return value;
        };
        if (entry.phase == Phase::Done) {
            if (entry.receipt == Receipt::Applied && !backend.current(entry.body, request, false)) return Receipt::Stale;
            return entry.receipt;
        }
        const auto phase = entry.phase;
        if (phase == Phase::Busy) return Receipt::Uncertain;
        const bool beforeApply = phase == Phase::Release;
        if (beforeApply && (intent == Intent::ObserveOnly || (intent == Intent::Advance && entry.advanceBlocked)))
            return Receipt::Uncertain;

        // Reentry during an engine read, Stop or preparation observes a busy receipt and cannot change the attempt.
        entry.phase = Phase::Busy;
        if (beforeApply && !backend.current(entry.body, request, true)) return finish(Receipt::Stale, Stage::Stale);
        auto observe_preparation = [&](bool first) {
            // Read the original actor only. No lethal call was made, so even
            // a later IsDead cannot be credited to this attempt.
            if (!first && backend.current(entry.body, request, false) && backend.life(entry.body) == Life::Dead &&
                backend.current(entry.body, request, false))
                return finish(Receipt::Uncertain, Stage::PrepareObservedDead);
            entry.phase = Phase::ObservePreparation;
            if (first) report(Stage::PrepareUncertain);
            return Receipt::Uncertain;
        };
        if (phase == Phase::ObservePreparation) return observe_preparation(false);
        auto confirm = [&] {
            entry.phase = Phase::Done; // a corpse readback/notification must not admit a second mutation
            entry.receipt = Receipt::Applied;
            backend.confirm(entry.body, request);
            report(Stage::Applied);
            return entry.receipt;
        };
        auto observe = [&](Receipt continuation, bool first) {
            if (!backend.current(entry.body, request, false)) continuation = Receipt::Uncertain;
            else if (backend.life(entry.body) == Life::Dead) {
                if (backend.current(entry.body, request, false)) return confirm();
                continuation = Receipt::Uncertain;
            }
            const bool reportChange = first || entry.receipt != continuation;
            entry.receipt = continuation;
            entry.phase = Phase::Observe;
            if (reportChange) report(continuation == Receipt::Pending ? Stage::DeathPending : Stage::ApplyUncertain);
            return intent == Intent::AbortBeforeApply ? Receipt::Uncertain : continuation;
        };
        if (phase == Phase::Observe) return observe(entry.receipt, false);

        auto unconfirmed_before_prepare = [&] {
            entry.phase = Phase::Release;
            if (!entry.advanceBlocked) report(Stage::ReleaseUncertain);
            entry.advanceBlocked = true;
            return Receipt::Uncertain;
        };
        auto abort_unmutated = [&] {
            if (!backend.current(entry.body, request, true) || backend.life(entry.body) != Life::Alive ||
                !backend.current(entry.body, request, true) || !backend.abort(entry.body, request.attempt))
                return unconfirmed_before_prepare();
            return finish(Receipt::Aborted, Stage::Aborted);
        };
        if (intent == Intent::AbortBeforeApply) return abort_unmutated();

        switch (backend.retire(entry.body, request.attempt)) {
            case Retirement::Pending:
                entry.phase = Phase::Release;
                if (!entry.reportedPending) { entry.reportedPending = true; report(Stage::ReleasePending); }
                return Receipt::Pending;
            case Retirement::Uncertain: return unconfirmed_before_prepare();
            case Retirement::Stale: return finish(Receipt::Stale, Stage::Stale);
            case Retirement::Ready: break;
        }
        if (!backend.current(entry.body, request, true))
            return finish(Receipt::Stale, Stage::Stale);
        if (!backend.ready(entry.body, request.attempt)) return unconfirmed_before_prepare();
        report(Stage::Ready);
        const auto before = backend.life(entry.body);
        if (before == Life::Dead)
            return backend.current(entry.body, request, false) ? confirm() : finish(Receipt::Uncertain, Stage::Uncertain);
        if (before != Life::Alive) return unconfirmed_before_prepare();
        switch (backend.prepare(entry.body, request, request.attempt)) {
            case Preparation::Unchanged:
                report(Stage::PrepareUnchanged);
                return abort_unmutated();
            case Preparation::Uncertain:
                return observe_preparation(true);
            case Preparation::Ready:
                break;
        }
        if (!backend.current(entry.body, request, true) || !backend.ready(entry.body, request.attempt))
            return observe_preparation(true);
        const bool admitted = backend.apply(entry.body, request);
        return observe(admitted ? Receipt::Pending : Receipt::Uncertain, true);
    }
};

} // namespace kcdmp::npcdeath
