// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#include "../KCDMP/rp_timeline_rules.h"
#include "../KCDMP/pose_ring.h"

#include <cstdio>
#include <cstring>
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
    drawn.ranged = kcdmp::rangedaim::decode_state(drawn.state.data(), drawn.stateHostMs);
    check(aim.push(drawn, local + 110, base + 110) == Ingest::Accepted, "aim track accepts the drawn pose");
    (void)aim.render(local + 190);
    const auto stillDrawn = aim.render(local + 5000);
    check(!stillDrawn.source.stateHostMs && stillDrawn.ranged_held() && stillDrawn.rangedStateHostMs == drawn.hostMs &&
          stillDrawn.source.state[7] == 255,
          "a drawn bow survives a short gap without extending the crouch timestamp");
    auto ended = sample(base + 5000, 0.1f);
    ended.stateHostMs = ended.hostMs;
    ended.state[9] = ended.state[11] = 0x80; // the unavailable-angle pair marks confirmed Off
    ended.ranged = kcdmp::rangedaim::decode_state(ended.state.data(), ended.stateHostMs);
    check(aim.push(ended, local + 5010, base + 5010) == Ingest::Accepted, "the confirmed end is a newer sample");
    const auto down = aim.render(local + 5200);
    check(down.source.stateHostMs == ended.hostMs && down.rangedStateHostMs == ended.hostMs &&
          !down.ranged_held(), "the newer sample clears the draw");
    auto staleHold = drawn;
    check(aim.push(staleHold, local + 5300, base + 5300) == Ingest::Late, "an older hold does not restore the draw");
    staleHold.hostMs = base + 5350;
    staleHold.stateHostMs = ended.stateHostMs;
    staleHold.ranged.hostMs = ended.ranged.hostMs;
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

    // Send timed Ghost packets through the native decoder and the actual coalescing inbox before Track.
    namespace ranged = kcdmp::rangedaim;
    using State = std::array<uint8_t, receive::kStateBytes>;
    const auto ranged_state = [](uint8_t bits, uint8_t charge, int16_t yaw, int16_t pitch) {
        State body{};
        body[3] = bits;
        body[7] = charge;
        body[8] = static_cast<uint8_t>(yaw);
        body[9] = static_cast<uint8_t>(static_cast<uint16_t>(yaw) >> 8);
        body[10] = static_cast<uint8_t>(pitch);
        body[11] = static_cast<uint8_t>(static_cast<uint16_t>(pitch) >> 8);
        return body;
    };
    constexpr uint8_t crouch = 0x04;
    const auto active = ranged_state(ranged::kHeldBit | ranged::kChargeKnownBit | crouch, 96, -2300, 4100);
    const auto turned = ranged_state(ranged::kHeldBit | ranged::kChargeKnownBit | crouch, 161, 6200, -8300);
    const auto ordinary = ranged_state(crouch, 0, 0, 0);
    const auto off = ranged_state(0, 0, ranged::kAngleUnavailable, ranged::kAngleUnavailable);
    constexpr receive::Scope sameBody{6, 7, 101};
    receive::Ring inbox(4);
    Track timedGhost;
    const auto enqueue = [&](uint64_t when, const State* state, float x = 0, uint64_t delay = 0) {
        std::fill(wire.begin(), wire.end(), 0);
        wire[0] = receive::kGhost;
        stamp(1, local + when + delay); stamp(9, base + when); stamp(17, base + when + delay);
        wire[start] = 1;
        std::memcpy(wire.data() + start + 1, &x, sizeof x);
        if (state) {
            wire[start + 17] = 0x10;
            std::copy(state->begin(), state->end(), wire.begin() + start + receive::kGhostFixed);
        }
        receive::Pose incoming;
        const size_t bytes = start + receive::kGhostFixed + (state ? receive::kStateBytes : 0);
        return receive::decode(wire.data(), bytes, incoming, true) &&
               inbox.publish(incoming, sameBody) == receive::Put::Accepted;
    };
    const auto consume = [&](Ingest expected = Ingest::Accepted) {
        const auto rows = inbox.drain(sameBody);
        if (rows.size() != 1) return false;
        const auto& incoming = rows.front();
        auto decoded = sample(incoming.hostMs, incoming.x);
        decoded.stateHostMs = incoming.stateHostMs;
        decoded.state = incoming.state;
        decoded.ranged = incoming.ranged;
        return timedGhost.push(decoded, static_cast<uint64_t>(incoming.arrivalQpc), incoming.hostAtArrivalMs) ==
               expected;
    };
    const auto leftOfWrap = ranged_state(ranged::kHeldBit, 0, 17000, -2000);
    const auto rightOfWrap = ranged_state(ranged::kHeldBit, 0, -17000, 4000);
    check(enqueue(0, &leftOfWrap) && consume() && enqueue(100, &rightOfWrap) && consume(),
          "known camera directions survive the native Ghost reader");
    const auto halfway = timedGhost.render(local + 140);
    check(halfway.ranged_held() && std::abs(halfway.ranged.yaw) == 18000 && halfway.ranged.pitch == 1000 &&
          halfway.rangedStateHostMs == base,
          "the timed receiver smooths both measured angles along the shortest yaw arc");
    timedGhost = {};
    const auto noView = ranged_state(ranged::kHeldBit, 0, ranged::kAngleUnavailable, ranged::kAngleUnavailable);
    check(enqueue(0, &leftOfWrap) && consume() && enqueue(100, &noView) && consume() &&
          timedGhost.render(local + 140).ranged.yaw == 17000 &&
          !timedGhost.render(local + 220).ranged.angles_known(),
          "the receiver does not invent a direction between a measured and unknown observation");
    timedGhost = {};
    check(enqueue(0, &leftOfWrap) && consume() && enqueue(100, &off) && consume() &&
          timedGhost.render(local + 140).ranged.pitch == -2000 && !timedGhost.render(local + 220).ranged_held(),
          "a scheduled Off is a discrete end and camera interpolation cannot cross it");
    timedGhost = {};
    check(enqueue(0, &leftOfWrap) && consume() && enqueue(250, &rightOfWrap) && consume() &&
          timedGhost.render(local + 310).ranged.yaw == 17000,
          "a direction is held over a long gap rather than slowly panning to a later sample");
    timedGhost = {};
    check(enqueue(0, &active) && consume() && enqueue(1000, &turned) && consume(),
          "timed Ghost receiver reads active state and a later in-phase turn");
    const auto turn = timedGhost.render(local + 1120);
    const auto& turnRanged = turn.ranged;
    check(turn.ranged_held() && turnRanged.charge == 161 && turnRanged.yaw == 6200 && turnRanged.pitch == -8300,
          "timed Ghost receiver preserves measured charge and both signed angles in a turn");
    check(enqueue(1900, &turned) && consume() && enqueue(2000, &turned, 0.2f) && consume(),
          "timed Ghost receiver accepts the two movement samples before the gap");
    const auto gap = timedGhost.render(local + 5000);
    check(gap.ranged_held() && gap.rangedStateHostMs == base + 2000 &&
          gap.extrapMs == 200 && gap.vx == 0.0f && !gap.source.stateHostMs,
          "three-second delivery gap holds aim while movement exhausts and crouch expires");
    bool resumed = true;
    for (uint64_t when = 5000; when <= 10000; when += 1000) {
        const bool accepted = enqueue(when, &turned, 0.4f) && consume();
        resumed = resumed && accepted;
    }
    check(resumed && timedGhost.render(local + 10120).ranged_held(),
          "confirmed draw reaches ten seconds in the same scope");

    // Omit the first Off at 10500. A newer ordinary body update is not an Off observation.
    check(enqueue(10800, &ordinary, 0.4f) && consume(), "ordinary body update reaches the timed receiver");
    const auto beforeOff = timedGhost.render(local + 11000);
    check(beforeOff.ranged_held() && beforeOff.rangedStateHostMs == base + 10000 &&
          beforeOff.source.stateHostMs == base + 10800 && (beforeOff.source.state[3] & crouch),
          "losing the first Off and receiving an ordinary body update preserves the draw and updates crouch");

    check(enqueue(11100, &off, 0.4f) && enqueue(11130, &ordinary, 0.4f) && enqueue(11160, nullptr, 0.4f) && consume(),
          "the raw inbox coalesces a repeated Off, an ordinary state, and a newer transform");
    const auto released = timedGhost.render(local + 11300);
    check(!released.ranged_held() && released.rangedStateHostMs == base + 11100 &&
          released.source.stateHostMs == base + 11130 && (released.source.state[3] & crouch),
          "the repeated Off clears using its original stamp even through ordinary state coalescing");

    check(enqueue(10900, &turned) && consume(Ingest::Late), "older held Ghost reaches Track and is rejected as late");
    const auto afterStale = timedGhost.render(local + 11450);
    check(!afterStale.ranged_held() && afterStale.rangedStateHostMs == base + 11100,
          "the older held sample cannot restore a draw after the repeated Off");

    constexpr uint64_t newBodyLocal = local + 11600;
    check(enqueue(11500, &active, 0.4f, 200) && consume() && enqueue(11710, &ordinary, 0.4f) && consume(),
          "a held sample captured before the body replacement can arrive after it");
    check(timedGhost.render(local + 11850).ranged_held(), "the same stream without a body boundary can still hold");
    const auto rebound = timedGhost.render(local + 11850, newBodyLocal);
    check(rebound.ready() && !rebound.ranged_held() && !rebound.rangedStateHostMs &&
          rebound.source.stateHostMs == base + 11710 && (rebound.source.state[3] & crouch),
          "the body boundary fences old capture time without clearing the new body's ordinary state");
    check(enqueue(11860, &turned, 0.4f, 100) && consume() &&
          timedGhost.render(local + 12120, newBodyLocal).ranged_held(),
          "a new held sample captured after the body boundary plays in the still-bound stream");

    Track uncertainBinding;
    auto futureHold = sample(base, 0);
    futureHold.ranged = ranged::decode_state(active.data(), base);
    check(uncertainBinding.push(futureHold, local + 20, base - 40) == Ingest::Accepted &&
          uncertainBinding.render(local + 200).ranged_held() &&
          !uncertainBinding.render(local + 200, local).ranged_held(),
          "a tolerated future clock stamp just after binding cannot establish a new body hold");

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
