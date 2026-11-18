// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#pragma once

#include "rp_measure_rules.h"

namespace kcdmp::rpmeasure {

// A control byte distinguishable from Op250's role values 1 and 2.
constexpr uint8_t kClockControl = 3;

// Main thread only. This diagnostics state cannot drive a body.
bool configure_clock(uint8_t value);
bool shared_clock();
void forget(uint32_t eid);
void note_hop(uint8_t peer, SampleScope source, int64_t agentArrivalQpc, double milliseconds);
void note_pose(uint8_t peer, uint32_t eid, SampleScope source, uint32_t senderMs, uint32_t appliedMs,
               double correctionCm, bool hardPlacement);
void note_action(uint32_t eid, uint64_t pipeReadMs);
void note_action(std::string_view name, uint64_t pipeReadMs);
Counts snapshot();

} // namespace kcdmp::rpmeasure
