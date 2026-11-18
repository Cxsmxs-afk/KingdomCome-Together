// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#include "join_native.h"
#include "engine.h"
#include "hangover.h"
#include "npc_drive.h"
#include "respawn_actions.h"
#include "log.h"

#include <windows.h>
#include <cmath>
#include <cstring>
#include <string>

namespace kcdmp::joinnative {
bool find_beside(float hx, float hy, float hz, float dist, float out[3], int* tried) {
    int n = 0;
    for (int k = 0; k < 8; ++k) {
        const float a = 0.785398163f * static_cast<float>(k);
        const float in[3] = {hx + dist * std::cos(a), hy + dist * std::sin(a), hz};
        ++n;
        if (hangover::snap_to_ground(in, out) && std::fabs(out[2] - hz) <= 2.0f) { if (tried) *tried = n; return true; }
    }
    if (tried) *tried = n;
    return false;
}
PlaceReport place(float hx, float hy, float hz, float dist, p4::Context context) {
    const float partner[3] = {hx, hy, hz};
    const auto p = p4::place(partner, dist, context);
    PlaceReport r{}; r.ok = p.ok; r.snapped = p.snapped; r.fallHeld = p.fallHeld; r.tried = p.tried;
    std::memcpy(r.target, p.target, 12); std::memcpy(r.before, p.before, 12); std::memcpy(r.after, p.after, 12); r.residual = p.residual;
    return r;
}
void tick() { p4::tick(); }
} // namespace kcdmp::joinnative
