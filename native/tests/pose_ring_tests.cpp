// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <vector>

#include "pose_ring.h"

static_assert(sizeof(kcdmp::posering::Header) == 24, "header layout is the C# contract");
static_assert(sizeof(kcdmp::posering::Slot) == 52, "slot layout is the C# contract");
static_assert(offsetof(kcdmp::posering::Slot, hostMs) == 4, "hostMs offset");
static_assert(offsetof(kcdmp::posering::Slot, x) == 8, "x offset");
static_assert(offsetof(kcdmp::posering::Slot, kind) == 24, "kind offset");

using kcdmp::posering::Sample;
using kcdmp::posering::bytes_needed;
using kcdmp::posering::kCapacity;
using kcdmp::posering::kEmpty;
using kcdmp::posering::kMismatch;
using kcdmp::posering::kOk;
using kcdmp::posering::kPlayer;
using kcdmp::posering::leave_torn;
using kcdmp::posering::publish;
using kcdmp::posering::read_since;
using kcdmp::posering::view_of;

namespace {
int g_fail = 0, g_pass = 0;
#define PCHECK(cond, ...) do { if (cond) ++g_pass; else { ++g_fail; std::printf("FAIL  %s:%d  ", __FILE__, __LINE__); std::printf(__VA_ARGS__); std::printf("\n"); } } while (0)
} // namespace

int pose_ring_tests(int* passed) {
    std::vector<uint8_t> mem(bytes_needed());
    auto v = view_of(mem.data());
    v.header->magic = kcdmp::posering::kMagic;
    v.header->version = kcdmp::posering::kVersion;
    v.header->capacity = kCapacity;
    v.header->slotBytes = sizeof(kcdmp::posering::Slot);

    Sample out[8];
    uint32_t n = 0, next = 0;
    PCHECK(read_since(v, 0, out, 8, &n, &next) == kEmpty, "an empty ring reads empty");

    publish(v, 10, 1, 2, 3, 0.5f, kPlayer, 0, "henry");
    publish(v, 20, 4, 5, 6, 1.0f, kPlayer, 1, "henry");
    PCHECK(read_since(v, 0, out, 8, &n, &next) == kOk && n == 2 && next == 2, "two publishes are two samples");
    PCHECK(out[1].hostMs == 20 && out[1].x == 4.f && out[1].flags == 1, "the newer sample is intact");
    PCHECK(std::strcmp(out[0].name, "henry") == 0, "the name round-trips");

    uint32_t cursor = next;
    publish(v, 30, 7, 0, 0, 0, kPlayer, 0, "henry");
    PCHECK(read_since(v, cursor, out, 8, &n, &next) == kOk && n == 1 && out[0].hostMs == 30, "the cursor skips what was already read");

    for (uint32_t i = 0; i < kCapacity + 10; ++i)
        publish(v, 1000 + i, static_cast<float>(i), 0, 0, 0, kPlayer, 0, "n");
    PCHECK(v.header->overflow.load() >= 10, "writing past the ring counts the overflow");
    PCHECK(read_since(v, 0, out, 1, &n, &next) == kOk, "a wrapped ring still reads");
    Sample all[kCapacity];
    uint32_t got = 0;
    read_since(v, next > kCapacity ? next - kCapacity : 0, all, kCapacity, &got, &next);
    PCHECK(got == kCapacity, "a read after wrap returns one full ring, not the dropped head");
    PCHECK(all[got - 1].hostMs == 1000 + kCapacity + 10 - 1, "the newest sample survived the wrap");

    uint32_t before = v.header->writeIndex.load();
    leave_torn(v);
    PCHECK(read_since(v, before, out, 8, &n, &next) == kOk && n == 0, "a torn slot is not returned");

    v.header->version = 99;
    PCHECK(read_since(v, 0, out, 8, &n, &next) == kMismatch && n == 0, "a version mismatch yields no samples");

    if (passed) *passed = g_pass;
    return g_fail;
}

#ifdef POSE_RING_STANDALONE
int main() {
    int passed = 0;
    const int failed = pose_ring_tests(&passed);
    std::printf("%d passed, %d failed\n", passed, failed);
    return failed == 0 ? 0 : 1;
}
#endif
