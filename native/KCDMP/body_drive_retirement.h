// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#pragma once
#include <cstdint>

namespace kcdmp::bodydrive {

struct ControllerBinding {
    const void* entity = nullptr;
    const void* actor = nullptr;
    void* controller = nullptr;

    bool complete() const { return entity && actor && controller; }
    bool matches(ControllerBinding other) const {
        return entity == other.entity && actor == other.actor && controller == other.controller;
    }
};

enum class RetirementCheck { Wait, Stop, Removed, Replaced };

struct ControllerRequest {
    static constexpr unsigned kMaxStops = 2;
    static constexpr double kRetryS = 0.10;
    static constexpr double kQuarantineProbeS = 0.50;

    ControllerBinding binding;
    uint64_t loadStamp = 0;
    double nextCheck = 0;
    unsigned stopCalls = 0;
    bool draining = false;
    bool ownerFenced = false;
    bool reported = false;

    bool active() const { return binding.complete(); }
    bool may_drive(ControllerBinding current, uint64_t stamp) const {
        return current.complete() && !draining && (!active() || (loadStamp == stamp && binding.matches(current)));
    }

    RetirementCheck observe(bool readable, ControllerBinding current, bool live) const {
        if (!readable || !active()) return RetirementCheck::Wait;
        if (!current.entity) return live ? RetirementCheck::Removed : RetirementCheck::Wait;
        if (!current.complete()) return RetirementCheck::Wait;
        // A distinct verified controller can take the next pose. A reused
        // address or a temporary missing read says nothing about the old request.
        if (current.controller != binding.controller) return live ? RetirementCheck::Replaced : RetirementCheck::Wait;
        if (binding.matches(current) && stopCalls < kMaxStops) return RetirementCheck::Stop;
        return RetirementCheck::Wait;
    }

    void defer(double now, RetirementCheck check) {
        nextCheck = now + (check != RetirementCheck::Wait && stopCalls < kMaxStops ? kRetryS : kQuarantineProbeS);
    }
};

} // namespace kcdmp::bodydrive
