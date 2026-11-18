// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#pragma once

#include "rp_measure_rules.h"
#include "rp_timeline_rules.h"

namespace kcdmp::rptimeline {

struct Frame {
    uint64_t targetHostMs = 0, appliedHostMs = 0;
    uint64_t lifecycle = 0, connection = 0;
};

struct Counts {
    uint64_t buffer_ms = 0, extrap_ms_max = 0, late_samples = 0;
    uint64_t action_pos_skew_ms_p95 = 0, pose_n = 0, action_n = 0;
};

// Main thread only. Renderer and action lane share the receiver's exact scope.
bool enabled();
void configure(bool on);
bool synchronize(rpmeasure::ReceiverScope receiver);
void note_received(uint8_t peer, uint64_t hostMs);
void note_target(uint32_t eid, uint8_t peer, uint64_t targetHostMs);
void note_pose(uint32_t eid, uint8_t peer, const Output& sample);
void note_action_pose(uint64_t actionHostMs, uint64_t appliedPoseHostMs);
void forget(uint32_t eid);
Frame frame(uint32_t eid, uint8_t peer);
Counts snapshot();

} // namespace kcdmp::rptimeline
