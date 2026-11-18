// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#pragma once

#include "load_sequence_rules.h"

namespace kcdmp::loadsequence {

// Pure local pipe control. It closes admission without posting to the game
// thread, including when a save load temporarily stops game frames.
bool control(const uint8_t* body, size_t size, uint64_t connection, uint8_t* ack);
void disconnected(uint64_t connection);
void tick();
uint64_t stamp();
bool allows(uint64_t received, Access access);
// The verified pre-load calendar follower is prepared for the offered
// world before Play. Keep it closed for a private save, menu or exit.
bool calendar_allowed();
void note_stale(uint64_t n = 1);
Counts snapshot();

} // namespace kcdmp::loadsequence
