// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#pragma once
#include "p4_rules.h"
namespace kcdmp::p4 {
using p4rules::Context;
using p4rules::Source;
struct Placement {
    bool ok = false, snapped = false, fallHeld = false; int tried = 0;
    float target[3]{}, before[3]{}, after[3]{}, residual = -1;
};
// All player teleport policies and writes use this main-thread entry.
Placement place(const float partner[3], float dist, Context c, bool recovery = false, const float* death = nullptr);
void tick();
void note_life_edge(const char* why); // main-thread actual down/up edges fence stale challenges
void invalidate(); // off-main safe: invalidate protection; no engine call
uint64_t generation();
uint64_t life_edge_tick(); // cached actual local life edge; no engine read
bool gameplay();
bool ready_context(Context c);
void arm_recovery(void* soul); // after full heal/readback, before wake events
bool protected_player(void* soul); // main thread, exact live identity
bool recovery_policy(); // any thread; pure published identity + generation + time
void note_damage(void* soul, float hp, const char* source, uint8_t peer = 0);
void note_downed(void* soul, const float death[3], int kind, int gameOverId);
void refused_stale(Source source);
size_t stats(uint8_t* out, size_t cap);
uint8_t snapshot(const uint8_t* body, size_t len, uint8_t* out, size_t cap, size_t* used);
} // namespace kcdmp::p4
