// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#pragma once
#include <cstdint>
namespace kcdmp::wo201ranged {
// A verified empty pointer is idle. A failed guarded read supplies no phase.
template<class Read, class Resolve>
uint8_t read_step_phase(Read read, Resolve resolve) {
    void* step = nullptr;
    if (!read(&step)) return 255;
    return step ? resolve(step) : 0;
}

#ifdef KCDMP_P6_CAPTURE_TEST
struct TestBackend {
    uint64_t(*now)();uint64_t(*generation)();uint64_t(*epoch)();bool(*gameplay)();
    void*(*actor)(uint32_t*);void*(*soul)();uint8_t(*phase)(void*);uint8_t(*hands)(uint32_t,uint8_t*,uint8_t*);
};
void set_test_backend(const TestBackend&);
void reset_for_tests();
#endif
}
