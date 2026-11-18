// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#include "../KCDMP/t3_native_stats_rules.h"
#include "../KCDMP/p4_rules.h"
#include <array>
#include <cstdio>
#include <cstring>

int t3_native_stats_tests(int* passed) {
    int good = 0, bad = 0;
    auto check = [&](bool ok, const char* name) { if (ok) ++good; else { ++bad; std::printf("FAIL T3 native stats: %s\n", name); } };
    std::array<uint8_t, 256> b{};
    uint64_t core[19]{}; for (size_t i = 0; i < 19; ++i) core[i] = i;
    const uint64_t extra[] = {101, 102, 103, 104, 105};
    const size_t n = kcdmp::p4rules::stats(b.data(), b.size(), core);
    const auto before = b;
    check(n == 173, "actual P4 producer has the stable prefix");
    check(kcdmp::t3native::append_corpses(b.data(), n, b.size(), extra) == 218, "P5 producer stays below the existing 512-byte payload budget");
    check(b[0] == 1 && b[1] == 24, "version retained and count extended");
    check(std::memcmp(b.data() + 2, before.data() + 2, 171) == 0, "all original counter identities and values preserved");
    for (size_t i = 0; i < 5; ++i) {
        check(b[173 + 9*i] == 19 + i, "appended field identity");
        check(kcdmp::p4rules::u64(b.data() + 174 + 9*i) == extra[i], "appended exact little-endian value");
    }
    b = before;
    check(kcdmp::t3native::append_corpses(b.data(), n, 217, extra) == 0 && b == before, "short capacity never emits or modifies half a group");
    check(kcdmp::t3native::append_corpses(nullptr, n, 256, extra) == 0, "null output refused");
    check(kcdmp::t3native::append_corpses(b.data(), n, b.size(), nullptr) == 0 && b == before, "null data refused without mutation");
    b[1] = 18; auto malformed = b;
    check(kcdmp::t3native::append_corpses(b.data(), n, b.size(), extra) == 0 && b == malformed, "wrong core count refused");
    b = before; b[2 + 9*18] = 17; malformed = b;
    check(kcdmp::t3native::append_corpses(b.data(), n, b.size(), extra) == 0 && b == malformed, "noncanonical core identity refused");
    b = before; const uint64_t large[] = {UINT64_MAX, 0, 0, 0, 0};
    check(kcdmp::t3native::append_corpses(b.data(), n, b.size(), large) == 218 &&
        kcdmp::p4rules::u64(b.data() + 174) == uint64_t(INT64_MAX), "native metric saturates at signed agent counter limit");
    *passed += good; return bad;
}
