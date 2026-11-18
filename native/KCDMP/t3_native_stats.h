// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#pragma once
#include "t3_native_stats_rules.h"
#include "p4.h"
#include "wo216.h"
#include "respawn_actions.h"
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
    return append_ranged(out, corpses, cap, ranged);
}
} // namespace kcdmp::t3native
