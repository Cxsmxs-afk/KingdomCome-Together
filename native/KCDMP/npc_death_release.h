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

enum class Receipt : uint8_t { Applied = 0, Pending = 0x70, Uncertain = 0x71, Stale = 0x72 };
enum class Retirement { Ready, Pending, Uncertain, Stale };
enum class Life { Alive, Dead, Unreadable };
enum class Stage { ReleasePending, Ready, DeathPending, Applied, Uncertain, Stale };

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
    enum class Phase { Release, Applying, Observe, Done };

    struct Entry {
        Scope scope{};
        Request request{};
        Body body{};
        Phase phase = Phase::Release;
        Receipt receipt = Receipt::Stale;
        bool reportedPending = false;
    };

    std::array<Entry, kWindow> entries_{};
    Scope scope_{};
    uint64_t highest_ = 0;

public:
    template <class Backend>
    Receipt step(const Request& request, const Scope& scope, Backend& backend) {
        if (!request.attempt || request.lifecycle != scope.lifecycle || !backend.scope_current()) return Receipt::Stale;
        if (!(scope == scope_)) { scope_ = scope; highest_ = 0; }
        Entry& entry = entries_[request.attempt % kWindow];
        const bool sameScope = entry.scope == scope;
        if (!sameScope || entry.request.attempt != request.attempt) {
            if (highest_ >= kWindow && request.attempt <= highest_ - kWindow) return Receipt::Stale;
            if (sameScope && (entry.phase == Phase::Applying || request.attempt < entry.request.attempt))
                return Receipt::Uncertain;
            highest_ = request.attempt > highest_ ? request.attempt : highest_;
            entry = {scope, request, backend.resolve(request)};
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
        if (entry.phase == Phase::Applying) return Receipt::Uncertain;
        const bool beforeApply = entry.phase == Phase::Release;
        if (!backend.current(entry.body, request, beforeApply)) {
            return beforeApply ? finish(Receipt::Stale, Stage::Stale) : finish(Receipt::Uncertain, Stage::Uncertain);
        }
        auto confirm = [&] {
            if (!backend.current(entry.body, request, false)) return finish(Receipt::Uncertain, Stage::Uncertain);
            entry.phase = Phase::Done; // a corpse readback/notification must not admit a second mutation
            entry.receipt = Receipt::Applied;
            backend.confirm(entry.body, request);
            report(Stage::Applied);
            return entry.receipt;
        };
        if (entry.phase == Phase::Observe) {
            if (backend.life(entry.body) == Life::Dead) return confirm();
            return Receipt::Pending;
        }

        // Record the in-progress state before calling Stop or any engine code that can reenter.
        entry.phase = Phase::Applying;
        switch (backend.retire(entry.body, request.attempt)) {
            case Retirement::Pending:
                entry.phase = Phase::Release;
                if (!entry.reportedPending) { entry.reportedPending = true; report(Stage::ReleasePending); }
                return Receipt::Pending;
            case Retirement::Uncertain: return finish(Receipt::Uncertain, Stage::Uncertain);
            case Retirement::Stale: return finish(Receipt::Stale, Stage::Stale);
            case Retirement::Ready: break;
        }
        if (!backend.current(entry.body, request, true) || !backend.ready(entry.body, request.attempt))
            return finish(Receipt::Stale, Stage::Stale);
        report(Stage::Ready);
        const auto before = backend.life(entry.body);
        if (before == Life::Dead) return confirm();
        if (before != Life::Alive) return finish(Receipt::Uncertain, Stage::Uncertain);
        if (!backend.prepare(entry.body, request, request.attempt)) return finish(Receipt::Uncertain, Stage::Uncertain);
        if (!backend.current(entry.body, request, true) || !backend.ready(entry.body, request.attempt))
            return finish(Receipt::Stale, Stage::Stale);
        const bool admitted = backend.apply(entry.body, request);
        if (!backend.current(entry.body, request, false)) return finish(Receipt::Uncertain, Stage::Uncertain);
        if (backend.life(entry.body) == Life::Dead) return confirm();
        if (!admitted) return finish(Receipt::Uncertain, Stage::Uncertain);
        entry.phase = Phase::Observe;
        report(Stage::DeathPending);
        return Receipt::Pending;
    }
};

} // namespace kcdmp::npcdeath
