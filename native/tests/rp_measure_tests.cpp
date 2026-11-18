// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#include "../KCDMP/rp_measure_rules.h"
#include "../KCDMP/pipe_control_reader.h"

#include <cstdio>

int rp_measure_tests(int* passed) {
    using namespace kcdmp::rpmeasure;
    int good = 0, bad = 0;
    auto check = [&](bool ok, const char* name) {
        if (ok) ++good;
        else { ++bad; std::printf("FAIL partner measurement: %s\n", name); }
    };

    bool clock = false;
    check(!clock_config(false, 1, clock) && !clock, "shared clock requires the environment opt-in");
    check(clock_config(true, 1, clock) && clock, "explicit enabled configuration applied");
    check(!clock_config(true, 2, clock) && clock, "unknown configuration leaves state untouched");
    check(!clock_config(false, 0, clock) && clock, "unauthorized off leaves state untouched");
    check(clock_config(true, 0, clock) && !clock, "explicit authorized off applied");
    const ReceiverScope boot{2, 1};
    const ReceiverScope gameplay{5, 1};
    const ReceiverScope nextLoad{6, 1};
    const ReceiverScope nextGameplay{9, 1};
    const ReceiverScope reconnected{9, 2};
    check(sample_in_window({100, boot}, boot, 100), "first byte from the armed boot scope at window start is eligible");
    check(!sample_in_window({99, boot}, boot, 100), "queued receipt preceding a reset is outside the window");
    check(!sample_in_window({100, boot}, gameplay, 100) && !sample_in_window({100, gameplay}, nextLoad, 100) &&
        !sample_in_window({100, nextGameplay}, reconnected, 100), "phase, generation and pipe edges discard queued receipts");
    check(!sample_in_window({0, boot}, boot, 0) && !sample_in_window({100, {0, 0}}, {0, 0}, 100),
        "missing timestamp or transport scope is ineligible");

    Window window;
    check(window.synchronize(boot) && !window.shared_clock() && !window.can_report() && !window.observe_pose(false, true),
        "ordinary inactive phase cannot be opted in by a writer alone");
    window.configure(true);
    check(window.shared_clock() && !window.can_report() && !window.observe_pose(true, false),
        "same-PC command promises a clock and waits for a writer with a fresh source");
    check(window.observe_pose(false, true) && window.can_report() && window.observe_pose(true, false)
        && !window.observe_pose(false, false) && !window.synchronize(boot),
        "live writer-source proof allows the loaded-host window and applies to the observed body");
    check(window.synchronize(gameplay) && window.can_report() && !window.shared_clock() && window.observe_pose(false, false),
        "a real gameplay transition disables the test assertion and leaves ordinary coverage");
    window.configure(true);
    check(window.synchronize(nextLoad) && !window.shared_clock() && !window.can_report() && !window.observe_pose(true, true),
        "the next load hard-disables the test window including previously known actors");
    window.configure(true);
    check(window.synchronize(nextGameplay) && !window.shared_clock(), "a new Gameplay phase demands a separate explicit assertion");
    window.configure(true);
    check(window.synchronize({nextGameplay.lifecycle, 0}) && !window.shared_clock() && !window.can_report() &&
        window.synchronize(reconnected) && !window.shared_clock(),
        "pipe disconnect clears the assertion and reconnect cannot reuse it");

    namespace reader = kcdmp::pipecontrol;
    reader::Reader staged;
    reader::Frame deferred;
    const uint8_t sampleFrame[] = {reader::kSamples, 1, 0, 0};
    const auto noControl = [](const reader::Frame&) { return true; };
    check(staged.feed(sampleFrame, 1, noControl, [&](reader::Frame& f) {
            f.readerQpc = 99;
            f.readerObservation = boot.lifecycle;
        }) && staged.feed(sampleFrame + 1, sizeof(sampleFrame) - 1, noControl, [&](reader::Frame& f) {
            f.readerQpc = 101;
            f.readerObservation = gameplay.lifecycle;
        }) && staged.pop(&deferred) && deferred.readerQpc == 99 &&
        !sample_in_window({deferred.readerQpc, {deferred.readerObservation, 1}}, boot, 100),
        "actual pipe decoder keeps the first-byte cutoff across delayed completion");

    uint8_t peer = 17;
    check(partner_id("kcd2mp_0", peer) && peer == 0, "peer zero is a canonical figure");
    check(partner_id("kcd2mp_255", peer) && peer == 255, "peer 255 is a canonical figure");
    check(!partner_id("kcd2mp_", peer) && !partner_id("kcd2mp_01", peer) && !partner_id("kcd2mp_256", peer)
        && !partner_id("kcd2mp_-1", peer) && !partner_id("kcd2mp_2_clone", peer) && !partner_id("other_2", peer)
        && peer == 255, "noncanonical figures leave peer unchanged");

    uint32_t age = 23;
    check(!sender_age_ms(0, 1, age) && !sender_age_ms(101, 100, age) && !sender_age_ms(1, 30002, age) && age == 23,
        "no timestamp, future, and too-old stamps are unmeasured");
    check(sender_age_ms(12, 12, age) && age == 0, "valid shared tick can be measured as zero");
    check(sender_age_ms(1, 30001, age) && age == 30000, "upper freshness boundary is inclusive");
    check(sender_age_ms(UINT32_MAX - 5, 4, age) && age == 10, "sender tick rollover preserves modular age");

    Ledger ledger;
    check(ledger.counts() == Counts{} && ledger.peer_counts(1) == Counts{}, "empty ledger has zero coverage");
    ledger.hop(1, 0.0);
    ledger.hop(1, 8.1);
    ledger.hop(2, 3.9);
    ledger.hop(1, -1.0);
    ledger.hop(1, (std::numeric_limits<double>::quiet_NaN)());
    ledger.action(1, 0.0);
    ledger.action(2, 14.25);
    ledger.action(2, (std::numeric_limits<double>::infinity)());
    auto total = ledger.counts();
    check(total[3] == 9 && total[12] == 3 && total[9] == 15 && total[13] == 2,
        "hop and action coverage accepts zero, rounds up, and rejects invalid values");
    check(ledger.peer_counts(1)[9] == 0 && ledger.peer_counts(1)[13] == 1 && ledger.peer_counts(2)[9] == 15,
        "peer measurements are separate and true zero remains distinguishable");

    ledger.pose(1, 100, 900, 1000, false, 0.0, false);
    ledger.pose(1, 100, 1000, 1080, true, 10.01, false);
    ledger.pose(1, 100, 1000, 1090, true, 0.0, false);
    ledger.pose(1, 100, 990, 1091, true, 0.0, false);
    ledger.pose(1, 100, 1010, 1100, true, 21.1, true);
    ledger.pose(2, 200, 0, 1100, true, -3.0, false);
    ledger.pose(2, 0, 1009, 1100, true, 100.0, true);
    total = ledger.counts();
    check(total[0] == 80 && total[1] == 90 && total[2] == 90 && total[11] == 2,
        "only distinct forward valid shared-clock stamps measure pose age");
    check(total[4] == 2 && total[5] == 22 && total[6] == 22 && total[7] == 1 && total[15] == 6,
        "only valid entity IDs count as poses, with independent correction and hard counts");
    check(total[8] == 0 && total[10] == 0 && total[14] == 0 && total[16] == 0,
        "step and owner stay uncovered without an independent native witness");
    check(ledger.peer_counts(1)[12] == 2 && ledger.peer_counts(2)[15] == 1, "first binding retains existing pipeline samples");
    check(ledger.peer_of(100, peer) && peer == 1 && ledger.peer_of(200, peer) && peer == 2,
        "applied entity identities resolve to the measured peer");
    check(ledger.has_body(1) && ledger.has_body(1, 100) && !ledger.has_body(1, 200) && !ledger.has_body(3),
        "only the verified bound figure is eligible for loaded-host follow-up");

    ledger.pose(1, 101, 100, 120, true, 0.0, false);
    check(!ledger.peer_of(100, peer) && ledger.peer_of(101, peer) && peer == 1 && ledger.peer_counts(1)[11] == 1
        && ledger.peer_counts(1)[0] == 20 && ledger.peer_counts(1)[12] == 0, "entity rebinding starts fresh per-peer windows");
    ledger.pose(3, 101, 101, 121, true, 0.0, false);
    check(ledger.peer_of(101, peer) && peer == 3 && ledger.peer_counts(1) == Counts{}, "reused entity identity clears the prior peer");
    ledger.forget(101);
    check(!ledger.peer_of(101, peer) && ledger.peer_counts(3) == Counts{} && ledger.counts()[11] == 4,
        "forget clears the entity and leaves interval aggregates intact");
    ledger.reset();
    check(ledger.counts() == Counts{} && ledger.peer_counts(2) == Counts{} && !ledger.peer_of(200, peer),
        "explicit session reset clears all metrics and figure associations");

    ledger.pose(7, 88, UINT32_MAX - 5, UINT32_MAX - 3, true, 0.0, false);
    ledger.pose(7, 88, 3, 4, true, 0.0, false);
    check(ledger.counts()[11] == 2 && ledger.counts()[0] == 1 && ledger.counts()[2] == 2,
        "forward arrival across stamp rollover is measured in the same window");

    ledger.reset();
    ledger.hop(2, 90000.0);
    for (size_t i = 0; i < 100; ++i) ledger.hop(2, 2100.25);
    for (size_t i = 0; i < 4000; ++i) ledger.hop(2, 15.2);
    check(ledger.counts()[12] == 4101 && ledger.counts()[3] == 16 && ledger.peer_counts(2)[3] == 16,
        "bounded nearest-rank histogram preserves full-run coverage");
    ledger.pose(2, 200, 1, 2, true, 2001.0, false);
    ledger.pose(2, 200, 2, 3, true, 5021.0, false);
    check(ledger.counts()[5] == 5021 && ledger.counts()[6] == 5021,
        "coarse percentile never exceeds the exact maximum");
    ledger.pose(2, 200, 1, 2, true, 1e40, true);
    check(ledger.counts()[5] == UINT32_MAX && ledger.counts()[6] == UINT32_MAX,
        "oversized finite distance saturates without undefined conversion");

    *passed += good;
    return bad;
}
