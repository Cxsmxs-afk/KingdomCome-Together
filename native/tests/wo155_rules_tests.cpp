// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
// WO-155: engine-free checks of the friendly-fire knockdown window (native/KCDMP/wo155_rules.h).
// Linked into KCDMP_NativeTests; wo155_rules_tests() returns the number of failures.
#include <cstdio>

#include "wo155_rules.h"

using namespace kcdmp::wo155rules;

namespace {
int g_fail = 0, g_pass = 0;
#define HCHECK(cond, ...) do { if (cond) ++g_pass; else { ++g_fail; std::printf("FAIL  %s:%d  ", __FILE__, __LINE__); std::printf(__VA_ARGS__); std::printf("\n"); } } while (0)
constexpr double kNever = -1e9;
} // namespace

int wo155_rules_tests(int* passed) {
    HCHECK(kWindowS == 5.0, "the window is 5 s");

    const Observation capture{0x0807060504030201, 0x1817161514131211, 0x2827262524232221, 0x3837363534333231};
    const std::array<uint8_t, 46> expectedMelee{
        0xA1, 0xA2, 0xA3, 0xA4, 0x00, 0x00, 0x80, 0x3F, 0x00, 0x00, 0x00, 0x40, 0x00, 0x51,
        0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18,
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38
    };
    HCHECK(encode_notice(0xA4A3A2A1, 1.0f, 2.0f, 0, 0x51, capture) == expectedMelee,
           "melee native notice keeps HP and stamina and serializes capture start, completion, source life and ID");
    auto expectedMissile = expectedMelee;
    expectedMissile[12] = 0x02;
    HCHECK(encode_notice(0xA4A3A2A1, 1.0f, 2.0f, 0x02, 0x51, capture) == expectedMissile,
           "avatar missile has its existing flag and the same capture evidence layout as melee");

    using kcdmp::rttr::DamageInvocation;
    const Recipient recipient{8, 10, 2, true};
    HCHECK(recipient.matches({8, 10, 2, true}, 11), "the current player life is allowed");
    HCHECK(!recipient.matches({8, 12, 2, true}, 11), "the old life cannot apply through the queue");
    HCHECK(!recipient.matches({9, 10, 2, true}, 11), "the former world cannot apply through the queue");
    HCHECK(!recipient.matches({8, 10, 3, true}, 11), "the former pipe cannot apply through the queue");
    HCHECK(!recipient.matches({8, 10, 2, false}, 11), "a stopped world cannot apply through the queue");
    HCHECK(!recipient.matches({8, 10, 2, true}, 10), "an equal tick cannot establish which life was read");
    HCHECK(!recipient.matches({8, 10, 2, true}, 9), "relay ingress before a life edge is retired even on a fresh pipe read");
    const Recipient bound{8, 10, 2, true, 51};
    HCHECK(!bound.matches({8, 10, 2, true, 52}, 12), "a packet held before the reader cannot use a new native life");
    HCHECK(!bound.matches({8, 10, 2, true, 0}, 12), "an unavailable native life proves no recipient");
    HCHECK(bound.matches({8, 10, 2, true, 51}, 12), "the exact proved life is accepted");

    struct DamageDouble {
        float hp = 95, st = 85;
        int dispatched = 0;
        DamageInvocation suppressed = DamageInvocation::Acknowledged;
        DamageInvocation operator()(bool suppress) {
            if (suppress && suppressed == DamageInvocation::NotInvoked) return suppressed;
            hp -= 10; st -= 7; ++dispatched;
            return suppress ? suppressed : DamageInvocation::Acknowledged;
        }
    };
    {
        DamageDouble damage;
        const auto protectedHit = dispatch(true, false, [&](bool suppress) { return damage(suppress); });
        HCHECK(protectedHit == HitResult::ProtectedConsumed && damage.dispatched == 0 && damage.hp == 95 && damage.st == 85,
               "protected contact is consumed with HP and stamina unchanged");
        const auto normal = dispatch(false, false, [&](bool suppress) { return damage(suppress); });
        HCHECK(normal == HitResult::Acknowledged && damage.dispatched == 1 && damage.hp == 85 && damage.st == 78,
               "allowed contact changes HP and stamina exactly once");
    }
    {
        DamageDouble damage;
        damage.suppressed = DamageInvocation::Unconfirmed;
        const auto ambiguous = dispatch(false, true, [&](bool suppress) { return damage(suppress); });
        HCHECK(ambiguous == HitResult::DispatchUnconfirmed && damage.dispatched == 1 && damage.hp == 85 && damage.st == 78,
               "an engine call can change HP and stamina before a lost receipt, so fallback would apply a second hit");
    }
    {
        DamageDouble damage;
        damage.suppressed = DamageInvocation::NotInvoked;
        const auto fallback = dispatch(false, true, [&](bool suppress) { return damage(suppress); });
        HCHECK(fallback == HitResult::Acknowledged && damage.dispatched == 1 && damage.hp == 85 && damage.st == 78,
               "fallback is safe when the first call provably never entered the engine");
    }

    // the first hit may knock him down (not suppressed); a second 2 s later, after the down edge, is suppressed
    {
        FfWindow w;
        HCHECK(!w.suppress(100.0, false, kNever), "the first hit knocks (not suppressed)");
        HCHECK(w.suppress(102.0, false, 100.3), "a second hit 2 s later, after the down edge: suppressed");
        HCHECK(w.suppress(104.9, false, 100.3), "4.9 s after the knocking hit: still inside the window");
        HCHECK(!w.suppress(105.1, false, 100.3), "5.1 s after: a new knockdown is allowed");
        // and that new knocking hit starts its own window
        HCHECK(w.suppress(107.0, false, 105.4), "the new window works the same");
    }
    // a hit while he is on the ground never knocks him again, whatever the clock says
    {
        FfWindow w;
        HCHECK(w.suppress(10.0, true, kNever), "down now: suppressed");
        HCHECK(!w.suppress(200.0, false, kNever), "no knockdown ever happened: the next hit knocks");
    }
    // a knocking hit that did NOT knock him down (a combat stance) opens no window
    {
        FfWindow w;
        HCHECK(!w.suppress(50.0, false, kNever), "hit 1 in a combat stance (no down edge)");
        HCHECK(!w.suppress(52.0, false, kNever), "hit 2 is not suppressed: hit 1 knocked nothing");
        HCHECK(!w.suppress(54.0, false, 20.0), "a down edge from long before the hit does not arm the window");
    }
    // a down edge more than kSettleS after the hit is not the hit's knockdown
    {
        FfWindow w;
        HCHECK(!w.suppress(30.0, false, kNever), "hit");
        HCHECK(!w.suppress(33.0, false, 31.8), "the edge came 1.8 s after the hit: another cause, no window");
    }
    // the window closes while he is up: 10 s later he can be knocked again, and a long fight alternates
    {
        FfWindow w;
        HCHECK(!w.suppress(0.0, false, kNever), "knock 1");
        HCHECK(w.suppress(1.0, true, 0.3), "down: suppressed");
        HCHECK(w.suppress(4.0, false, 0.3), "up again at 4 s: still inside the window");
        HCHECK(!w.suppress(8.0, false, 0.3), "8 s: window over, knock 2");
        HCHECK(w.suppress(9.0, false, 8.3), "and its window");
    }
    *passed = g_pass;
    return g_fail;
}
