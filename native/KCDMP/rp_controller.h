// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#pragma once

#include "rp_controller_rules.h"
#include "rp_measure_rules.h"

namespace kcdmp::rpctrl {

struct Counts {
    uint64_t ctrl_frames = 0, req_ok = 0, req_fail = 0;
    uint64_t fallback_jump = 0, fallback_mount = 0, fallback_ragdoll = 0, fallback_air = 0;
    uint64_t hard_tp_join = 0, hard_tp_respawn = 0, hard_tp_travel = 0, hard_tp_unapproved = 0;
    uint64_t pulls = 0, pull_observed_ms = 0, pulls_per_min = 0; // thousandths of a pull per body-minute
};

// Main thread only. Counters follow the current lifecycle, pipe and mode.
void synchronize(bool on, rpmeasure::ReceiverScope scope);
void request(uint32_t eid, uint8_t peer, bool accepted, unsigned gait);
void fallback(uint32_t eid, uint8_t peer, Fallback why);
void place(uint32_t eid, uint8_t peer, Relocation reason);
void observed(uint32_t eid, uint8_t peer, uint64_t elapsedMs, bool pulled);
void forget(uint32_t eid);
Counts snapshot();

} // namespace kcdmp::rpctrl
