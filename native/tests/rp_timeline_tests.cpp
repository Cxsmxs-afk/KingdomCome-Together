// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#include "../KCDMP/rp_timeline_rules.h"
#include "../KCDMP/pose_ring.h"

#include <cstdio>
#include <vector>

int rp_timeline_tests(int* passed) {
    using namespace kcdmp::rptimeline;
    int failures = 0;
    const auto check = [&](bool result, const char* label) {
        if (result) ++*passed;
        else { ++failures; std::fprintf(stderr, "partner timeline: %s\n", label); }
    };
    constexpr uint64_t base = 900000, local = 10000;
    auto sample = [](uint64_t host, float x) {
        Sample row;
        row.hostMs = host;
        row.x = x;
        return row;
    };
    Track track;
    check(!track.render(local).ready(), "no host proof means no timed render");
    check(!known_stamp(0, base) && !known_stamp(base + 51, base) && !known_stamp(base - 30001, base) &&
          known_stamp(base + 50, base) && known_stamp(base - 30000, base), "bounded clock proof");
    check(track.push(sample(base, 0), local, base) == Ingest::Accepted &&
          !track.render(local + 1).ready(), "first pose waits on its host deadline");
    auto one = sample(base + 100, 0.2f);
    one.stateHostMs = one.hostMs;
    one.state[3] = 0x04;
    check(track.push(one, local + 110, base + 110) == Ingest::Accepted, "second pose and state share capture stamp");
    const auto between = track.render(local + 130);
    check(between.ready() && between.appliedHostMs == between.targetHostMs && between.targetHostMs < one.hostMs &&
          between.x > 0 && between.x < 0.2f && between.vx > 1.9f && !between.source.stateHostMs,
          "position and velocity interpolate while future crouch remains pending");
    const auto shown = track.render(local + 190);
    check(shown.ready() && shown.source.stateHostMs == one.hostMs && shown.source.state[3] == 0x04,
          "posture applies with the scheduled pose");
    check(track.push(sample(base + 90, 100), local + 191, base + 191) == Ingest::Late,
          "already passed out-of-order pose is late");
    const auto eased = track.render(local + 270);
    const auto held = track.render(local + 900);
    const auto longHeld = track.render(local + 5000);
    check(eased.extrapMs > 0 && eased.extrapMs < 200 && eased.vx > 0 && eased.vx < 2.0f, "extrapolator slows smoothly");
    check(held.extrapMs == 200 && held.vx == 0 && held.appliedHostMs == one.hostMs + 200 &&
          held.x == longHeld.x && held.z == longHeld.z && longHeld.appliedHostMs == held.appliedHostMs,
          "endpoint is stationary beyond 200 ms");
    check(!longHeld.source.stateHostMs, "old stance inputs age out while position holds");
    Track aim;
    check(aim.push(sample(base, 0), local, base) == Ingest::Accepted, "aim track accepts its first pose");
    auto drawn = sample(base + 100, 0.2f);
    drawn.stateHostMs = drawn.hostMs;
    drawn.state[3] = 0x08 | 0x04;
    drawn.state[7] = 255;
    check(aim.push(drawn, local + 110, base + 110) == Ingest::Accepted, "aim track accepts the drawn pose");
    (void)aim.render(local + 190);
    const auto stillDrawn = aim.render(local + 5000);
    check(!stillDrawn.source.stateHostMs && stillDrawn.ranged_held() && stillDrawn.rangedStateHostMs == drawn.hostMs &&
          stillDrawn.source.state[7] == 255,
          "a drawn bow survives a short gap without extending the crouch timestamp");
    auto ended = sample(base + 5000, 0.1f);
    ended.stateHostMs = ended.hostMs;
    check(aim.push(ended, local + 5010, base + 5010) == Ingest::Accepted, "the confirmed end is a newer sample");
    const auto down = aim.render(local + 5200);
    check(down.source.stateHostMs == ended.hostMs && down.rangedStateHostMs == ended.hostMs &&
          !down.ranged_held(), "the newer sample clears the draw");
    auto staleHold = drawn;
    check(aim.push(staleHold, local + 5300, base + 5300) == Ingest::Late, "an older hold does not restore the draw");
    staleHold.hostMs = base + 5350;
    staleHold.stateHostMs = ended.stateHostMs;
    check(aim.push(staleHold, local + 5360, base + 5360) == Ingest::Accepted &&
          !aim.render(local + 5550).ranged_held(),
          "a later position cannot replace a confirmed end with a different block at the same state stamp");
    check(track.push(sample(base + 150, 0.3f), local + 5010, base + 5010) == Ingest::Late &&
          track.render(local + 5015).targetHostMs >= longHeld.targetHostMs, "late correction never rewinds the host cursor");
    check(track.push(sample(base * 2, 4), local + 5100, base * 2) == Ingest::Restarted &&
          !track.render(local + 5101).ready() && track.render(local + 5221).source.hostMs == base * 2,
          "verified clock discontinuity cannot reuse prior motion");

    Track slow, fast;
    for (uint64_t t = 0; t < 2000; t += 20) {
        slow.push(sample(base + t, t / 1000.0f), local + t + 175, base + t + 175);
        fast.push(sample(base + t, t / 1000.0f), local + t + 3, base + t + 3);
        slow.render(local + t + 175);
        fast.render(local + t + 3);
    }
    check(fast.render(local + 2020).bufferMs == 60 && slow.render(local + 2200).bufferMs == 120,
          "adaptive buffer reaches 60 ms and 120 ms bounds");

    constexpr uint64_t max = (std::numeric_limits<uint64_t>::max)();
    Track boundary;
    check(boundary.push(sample(max - 170, 1), local, max - 140) == Ingest::Accepted &&
          boundary.render(local + 100).ready() && advance(max - 2, 7) == max,
          "shared uint64 projection saturates rather than wrapping");
    const auto cursor = boundary.render(local + 110).targetHostMs;
    check(boundary.push(sample(max - 100, 1), local + 110, max - 80) != Ingest::Rejected &&
          boundary.render(local + 111).targetHostMs >= cursor, "small calibration change cannot move cursor backward");

    namespace receive = kcdmp::posering::receive;
    std::vector<uint8_t> wire(receive::kEnvelope + receive::kClockBytes + receive::kGhostFixed + receive::kStateBytes, 0);
    const auto stamp = [&](size_t offset, uint64_t value) {
        for (unsigned i = 0; i < 8; ++i) wire[offset + i] = static_cast<uint8_t>(value >> (8 * i));
    };
    wire[0] = receive::kGhost;
    stamp(1, 500); stamp(9, uint64_t{0x1ffffffff}); stamp(17, uint64_t{0x200000009});
    const size_t start = receive::kEnvelope + receive::kClockBytes;
    wire[start] = 42; wire[start + 17] = 0x10; wire[start + 21] = 4;
    receive::Pose pose;
    check(receive::decode(wire.data(), wire.size(), pose, true) && pose.ghost() && pose.source == 42 &&
          pose.hostMs == uint64_t{0x1ffffffff} && pose.stateHostMs == pose.hostMs,
          "actual typed pose decoder keeps 64-bit clock and captures State2's tick");
    check(!receive::decode(wire.data(), wire.size(), pose), "legacy raw length does not accept stamped metadata");
    check(!receive::decode(wire.data(), start - 1, pose, true), "truncated timed input refused");
    stamp(9, 0);
    check(!receive::decode(wire.data(), wire.size(), pose, true), "no fabricated timestamp enters timed playback");

    Track route;
    std::vector<uint64_t> actions, coordinatedSkew, untimedSkew;
    constexpr uint64_t actionEvery = 800;
    for (uint64_t t = 0; t <= 8000; t += 8) {
        auto direct = route.render(local + t);
        if (t >= 32 && t % 32 == 0) route.push(sample(base + t - 32, (t - 32) / 1000.0f), local + t, base + t);
        if (t >= actionEvery && t % actionEvery == 0 && direct.ready()) {
            actions.push_back(base + t - 16);
            untimedSkew.push_back(difference(actions.back(), direct.appliedHostMs));
        }
        const auto timed = route.render(local + t);
        if (!actions.empty() && timed.ready() && timed.appliedHostMs >= actions.front()) {
            coordinatedSkew.push_back(difference(timed.appliedHostMs, actions.front()));
            actions.erase(actions.begin());
        }
    }
    const auto p95 = [](std::vector<uint64_t> values) {
        if (values.empty()) return uint64_t{0};
        std::sort(values.begin(), values.end());
        return values[(values.size() * 95 + 99) / 100 - 1];
    };
    char label[160]{};
    std::snprintf(label, sizeof label, "virtual action alignment p95=%llu ms with untimed twin p95=%llu ms",
                  static_cast<unsigned long long>(p95(coordinatedSkew)), static_cast<unsigned long long>(p95(untimedSkew)));
    check(coordinatedSkew.size() >= 9 && p95(coordinatedSkew) < 20 && p95(untimedSkew) >= 34, label);
    return failures;
}
