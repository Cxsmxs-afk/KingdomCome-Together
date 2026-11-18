// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#pragma once

#include <cstdint>

namespace kcdmp::edge {

struct Counts {
    uint64_t rt_age_ms_p95 = 0, rt_age_ms_max = 0, bulk_age_ms_p95 = 0;
    uint64_t pipe_busy_replies = 0, rt_n = 0, bulk_n = 0;
};

// Read on the game thread; the pipe thread independently counts Busy replies.
Counts snapshot();
// Raw samples record when the native writer actually inserts them on Main.
void observe_rt(int64_t firstByteQpc);

} // namespace kcdmp::edge
