// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#pragma once
#include "t3_native_stats_rules.h"
#include "edge_lanes.h"
#include "holders.h"
#include "p4.h"
#include "wo216.h"
#include "respawn_actions.h"
#include "rp_measure.h"
#include "rp_timeline.h"
#include "wo201.h"

namespace kcdmp::t3native {
// Called on Main with cached measurements only. No census, UI query or actor
// lookup belongs in the diagnostic request.
inline size_t stats(uint8_t* out, size_t cap) {
    if (!out || cap < kP6Bytes) return 0;
    uint64_t markers[3]{};
    actions::p5_marker_counts(markers);
    const uint64_t extra[] = {wo216::p5_corpses_visible(), wo216::p5_corpse_hidden_blocked(), markers[0], markers[1], markers[2]};
    const size_t core = p4::stats(out, cap);
    const size_t corpses = append_corpses(out, core, cap, extra);
    if (!corpses) return 0;
    uint64_t ranged[kRangedFields]{};
    wo201ranged::t3_counts(ranged);
    const size_t prior = append_ranged(out, corpses, cap, ranged);
    if (!prior || cap < kRpMeasureBytes) return prior;
    static_assert(rpmeasure::kFields == kRpMeasureFields);
    auto measure = rpmeasure::snapshot();
    const auto owners = holders::owner_stats();
    measure[kRpOwnerViolationsIndex] = owners.violations;
    measure[kRpOwnerChecksIndex] = owners.checks;
    const size_t measured = append_rp_measure(out, prior, cap, measure.data());
    if (!measured || cap < kOwnerBytes) return measured;
    const uint64_t owner_counts[] = {owners.handovers, owners.denied_sets};
    const size_t owned = append_owners(out, measured, cap, owner_counts);
    if (!owned || cap < kEdgeBytes) return owned;
    const auto lanes = edge::snapshot();
    const uint64_t edge_counts[] = {lanes.rt_age_ms_p95, lanes.rt_age_ms_max, lanes.bulk_age_ms_p95,
                                    lanes.pipe_busy_replies, lanes.rt_n, lanes.bulk_n};
    return append_edge(out, owned, cap, edge_counts);
}

inline size_t timeline_stats(uint8_t* out, size_t cap) {
    const auto timeline = rptimeline::snapshot();
    const uint64_t counts[] = {timeline.buffer_ms, timeline.extrap_ms_max, timeline.late_samples,
                               timeline.action_pos_skew_ms_p95, timeline.pose_n, timeline.action_n};
    return write_timeline(out, cap, counts);
}
} // namespace kcdmp::t3native
