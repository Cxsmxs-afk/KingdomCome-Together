// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#include "rp_timeline_actions.h"

#include <array>
#include <cstdio>
#include <cstring>
#include <limits>

int rp_timeline_action_tests(int* passed) {
    using namespace kcdmp::timelineactions;
    int ok = 0, failed = 0;
    const auto check = [&](bool pass, const char* name) {
        if (pass) ++ok;
        else { ++failed; std::printf("RP timeline action failed: %s\n", name); }
    };

    std::array<uint8_t, kHeader + 4> bytes{};
    bytes[0] = 6;
    bytes[1] = 0x78; bytes[2] = 0x56; bytes[3] = 0x34; bytes[4] = 0x12;
    bytes[5] = 0x24; bytes[9] = 0x96; bytes[10] = 0x01;
    bytes[13] = 0xff; bytes[14] = 0xff;
    bytes[15] = kAttack; bytes[16] = kCommit;
    std::memcpy(bytes.data() + kHeader, "Test", 4);
    Action a;
    check(decode(bytes.data(), bytes.size(), a) && a.peer == 6 && a.eid == 0x12345678 && a.hostMs == 0x19600000024ULL &&
          a.sequence == 65535 && a.kind == kAttack && a.phase == kCommit && a.spec == "Test", "timed attack preserves 64-bit host time and relay identity");
    bytes[15] = kBlockImpulse;
    check(decode(bytes.data(), bytes.size(), a) && a.kind == kBlockImpulse, "committed block impulse uses the same envelope");
    bytes[15] = kDodge;
    check(decode(bytes.data(), bytes.size(), a) && a.kind == kDodge, "committed dodge uses the same envelope");
    bytes[16] = 2;
    check(!decode(bytes.data(), bytes.size(), a), "cancel is never interpreted as a swing");
    bytes[16] = kCommit;
    bytes[15] = 2;
    check(!decode(bytes.data(), bytes.size(), a), "jump and other kinds keep their own reliable paths");
    bytes[15] = kAttack;
    check(!decode(bytes.data(), kHeader, a), "size must include a nonempty fragment spec");
    bytes[kHeader + 1] = 0;
    check(!decode(bytes.data(), bytes.size(), a), "embedded NUL cannot change the executed fragment");

    check(!due(246, 245, 260), "a target reaching the action doesn't prove the pose was displayed");
    check(!due(246, 246, 245), "a restored older timeline target cannot launch a future action");
    check(due(246, 246, 246) && due(246, 258, 260), "displayed matching or later pose makes the action due");
    check(!waited(100, 99) && !waited(100, 849) && waited(100, 850), "bounded grace counts real elapsed receipt time");
    const auto end = (std::numeric_limits<uint64_t>::max)();
    check(skew(end - 12, end) == 12 && skew(end, end - 12) == 12 && skew(end, end) == 0, "skew is absolute without signed overflow");
    const Scope scope{3, 5, 7};
    check(scope == Scope{3, 5, 7} && !(scope == Scope{4, 5, 7}) && !(scope == Scope{3, 6, 7}) && !(scope == Scope{3, 5, 8}),
          "world, pipe, and life all identify the action receipt");
    if (passed) *passed = ok;
    return failed;
}
