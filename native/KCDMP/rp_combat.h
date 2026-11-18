// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#pragma once

#include <cstdint>

namespace kcdmp::rpcombat {

struct Counts {
    uint64_t actions_in = 0, actions_played = 0, actions_pending = 0;
    uint64_t action_lag_ms_p95 = 0, action_confirmed_n = 0, ko_up_lag_ms_p95 = 0, ko_up_n = 0;
};

// Native scheduled requests only, with lag to confirmed installation/readback.
Counts snapshot();

} // namespace kcdmp::rpcombat
