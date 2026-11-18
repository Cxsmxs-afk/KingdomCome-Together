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
    std::array<uint8_t, 512> p6{};
    size_t used = kcdmp::p4rules::stats(p6.data(), p6.size(), core);
    used = kcdmp::t3native::append_corpses(p6.data(), used, p6.size(), extra);
    const auto prefix = p6;
    const uint64_t ranged[] = {201, 202, 203, 204, 205, 206};
    check(kcdmp::t3native::append_ranged(p6.data(), used, p6.size(), ranged) == 272 && p6[1] == 30, "P6 stays at272 bytes in the existing reply budget");
    check(std::memcmp(p6.data() + 2, prefix.data() + 2, 216) == 0, "P4 and P5 field identities and values remain byte-identical");
    for (size_t i = 0; i < 6; ++i) {
        check(p6[218 + 9*i] == 24 + i, "P6 appended field identity");
        check(kcdmp::p4rules::u64(p6.data() + 219 + 9*i) == ranged[i], "P6 exact native little-endian value");
    }
    const auto base = p6;
    uint64_t measure[kcdmp::t3native::kRpMeasureFields]{};
    for (size_t i = 0; i < kcdmp::t3native::kRpMeasureFields; ++i) measure[i] = 300 + i;
    check(kcdmp::t3native::append_rp_measure(p6.data(), 272, p6.size(), measure) == 425 && p6[1] == 47,
        "measurement group retains the existing 512-byte reply cap");
    check(std::memcmp(p6.data() + 2, base.data() + 2, 270) == 0, "measurement group preserves the entire P6 prefix");
    for (size_t i = 0; i < kcdmp::t3native::kRpMeasureFields; ++i) {
        check(p6[272 + 9*i] == 30 + i, "measurement field identity is append-only");
        check(kcdmp::p4rules::u64(p6.data() + 273 + 9*i) == measure[i], "measurement field retains the full integer value");
    }
    check(p6[272 + 9*kcdmp::t3native::kRpOwnerViolationsIndex] == 40 &&
          p6[272 + 9*kcdmp::t3native::kRpOwnerChecksIndex] == 46, "owner violations and checks keep their measurement IDs");
    const auto measured = p6;
    const uint64_t owner_counts[] = {7, 42};
    check(kcdmp::t3native::append_owners(p6.data(), 425, 442, owner_counts) == 0 && p6 == measured,
        "insufficient capacity cannot modify or emit part of the owner group");
    check(kcdmp::t3native::append_owners(p6.data(), 425, p6.size(), owner_counts) == 443 && p6[1] == 49 &&
          std::memcmp(p6.data() + 2, measured.data() + 2, 423) == 0, "owner group keeps the complete measurement prefix and stays under the reply budget");
    check(p6[425] == 47 && kcdmp::p4rules::u64(p6.data() + 426) == 7 &&
          p6[434] == 48 && kcdmp::p4rules::u64(p6.data() + 435) == 42, "owner handover and denial IDs carry exact counts");
    const auto owned = p6;
    const uint64_t edges[] = {21, 35, 74, 8, 200, 400};
    check(kcdmp::t3native::append_edge(p6.data(), 443, 496, edges) == 0 && p6 == owned,
        "short capacity leaves the complete owner snapshot untouched");
    check(kcdmp::t3native::append_edge(p6.data(), 443, p6.size(), edges) == 497 && p6[1] == 55 &&
          std::memcmp(p6.data() + 2, owned.data() + 2, 441) == 0, "edge group stays in the 512-byte budget and preserves owner data");
    for (size_t i = 0; i < kcdmp::t3native::kEdgeFields; ++i) {
        check(p6[443 + 9*i] == 49 + i, "edge field identity is append-only");
        check(kcdmp::p4rules::u64(p6.data() + 444 + 9*i) == edges[i], "edge age, busy replies and sample counts retain their exact values");
    }
    std::array<uint8_t, 64> timeline;
    timeline.fill(0xAD);
    const auto untouchedTimeline = timeline;
    const uint64_t timelineCounts[] = {90, 200, 15, 19, 1234, 20};
    check(kcdmp::t3native::write_timeline(timeline.data(), 55, timelineCounts) == 0 && timeline == untouchedTimeline,
        "timeline short reply is untouched");
    check(kcdmp::t3native::write_timeline(timeline.data(), timeline.size(), timelineCounts) == 56 &&
        timeline[0] == 1 && timeline[1] == 6 && timeline[56] == 0xAD, "timeline is a separate 56-byte versioned page");
    for (size_t i = 0; i < kcdmp::t3native::kTimelineFields; ++i) {
        check(timeline[2 + 9*i] == i, "timeline field has its own page ID");
        check(kcdmp::p4rules::u64(timeline.data() + 3 + 9*i) == timelineCounts[i], "timeline values and matching observation counts retain precise encoding");
    }
    p6 = base;
    check(kcdmp::t3native::append_rp_measure(p6.data(), 272, 424, measure) == 0 && p6 == base,
        "short replies cannot contain partial measurement coverage");
    p6 = prefix;
    check(kcdmp::t3native::append_ranged(p6.data(), used, 271, ranged) == 0 && p6 == prefix, "no partial P6 group on short capacity");
    p6[2 + 9*23] = 22; const auto badPrefix = p6;
    check(kcdmp::t3native::append_ranged(p6.data(), used, p6.size(), ranged) == 0 && p6 == badPrefix, "invalid P5 prefix never gets ranged diagnostics attached");
    *passed += good; return bad;
}
