// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#include "edge_lanes_rules.h"
#include "load_sequence_rules.h"
#include "pipe_control_reader.h"
#include "pose_ring.h"

#include <array>
#include <cstdio>

int load_sequence_tests(int* passed) {
    using namespace kcdmp::loadsequence;
    int ok = 0, failed = 0;
    const auto check = [&](bool pass, const char* name) {
        if (pass) ++ok;
        else { ++failed; std::printf("Load sequence failed: %s\n", name); }
    };

    std::array<uint8_t, kRequestBytes> request{kOp, static_cast<uint8_t>(Phase::Play), kLinkHeld | kHostOfferHeld | kPrivateHold};
    constexpr uint64_t gen = 0xDE000000010ULL;
    write_le(request.data() + 3, 900, 2);
    write_le(request.data() + 5, gen, 8);
    write_le(request.data() + 13, 0x12345678, 4);
    Request decoded;
    check(decode(request.data(), request.size(), decoded) && decoded.phase == Phase::Play &&
          decoded.flags == kFlags && decoded.holdSeconds == 900 && decoded.generation == gen && decoded.id == 0x12345678,
          "control carries full generation and three independent hold flags");
    request[2] |= kMainApplied;
    check(!decode(request.data(), request.size(), decoded), "client cannot assert Main effect");
    request[2] = kLinkHeld;
    check(!decode(request.data(), request.size(), decoded), "link actuator has bounded deadline");
    write_le(request.data() + 3, 80, 2);
    check(decode(request.data(), request.size(), decoded), "link hold uses its verified timeout policy");
    request[2] = kPrivateHold;
    check(!decode(request.data(), request.size(), decoded), "private state cannot specify an actuator");
    request[2] = 0;
    write_le(request.data() + 3, 0, 2);
    request[1] = static_cast<uint8_t>(Phase::Query);
    check(!decode(request.data(), request.size(), decoded), "query cannot carry a world mutation");
    write_le(request.data() + 5, 0, 8);
    check(decode(request.data(), request.size(), decoded), "query has no requested generation");
    std::array<uint8_t, kAckBytes> ack{};
    encode_ack(ack.data(), Phase::Play, kMainApplied | kHostOfferHeld, gen, 42);
    check(ack[0] == 5 && ack[1] == 0x82 && read_le(ack.data() + 2, 8) == gen && read_le(ack.data() + 10, 4) == 42,
          "ack flags preserve requested hold and separate Main proof");

    check(calendar_allowed(Phase::Offer, 0) && calendar_allowed(Phase::Load, 0) &&
          !calendar_allowed(Phase::Load, kPrivateHold) && !calendar_allowed(Phase::Menu, 0) &&
          !calendar_allowed(Phase::Exit, 0), "calendar can prepare the offered save without overwriting a private world");

    State state;
    check(gate(state.stamp()) == Gate::Legacy && allowed(0, state.stamp(), Access::Live),
          "older Agent pairs until optional load control is negotiated");
    check(state.apply({Phase::Menu, 0, 0, 1}, 1, 100) && gate(state.stamp()) == Gate::Hold,
          "unbound menu can be adopted closed before the first world");
    const auto menu = state.stamp();
    check(!allowed(menu, menu, Access::Setup) && allowed(menu, menu, Access::Control) && !allowed(menu, menu, Access::Live),
          "menu leaves control available without enabling body setup");
    check(state.apply({Phase::Offer, 0, gen, 2}, 1, 150) && state.apply({Phase::Load, 0, gen, 3}, 1, 200),
          "offer and loading close both participants' stream gates");
    const auto loading = state.stamp();
    check(!state.apply({Phase::Play, 0, gen - 1, 4}, 1, 230) && state.stamp() == loading,
          "older generation cannot trigger Play");
    check(state.apply({Phase::Reconstruct, 0, gen, 4}, 1, 300) && gate(state.stamp()) == Gate::Setup &&
          allowed(state.stamp(), state.stamp(), Access::Setup) && !allowed(state.stamp(), state.stamp(), Access::Live),
          "reconstruction permits scoped setup and holds realtime writes");
    const auto reconstruct = state.stamp();
    check(state.apply({Phase::Settle, 0, gen, 5}, 1, 350) && state.apply({Phase::Play, 0, gen, 6}, 1, 360),
          "settled generation opens");
    const auto play = state.stamp();
    check(allowed(play, play, Access::Live) && !allowed(reconstruct, play, Access::Setup) &&
          !allowed(loading, play, Access::Live) && !old_generation(loading, play) && old_generation(menu, play),
          "same-generation phase drops are distinct from stale old world");
    const auto metrics = state.snapshot(380);
    check(metrics.world_gen == gen && metrics.load_phase == 5 && metrics.observations == 5 &&
          metrics.phase_ms[1] == 50 && metrics.phase_ms[2] == 100 && metrics.phase_ms[3] == 50 &&
          metrics.phase_ms[4] == 10 && metrics.phase_ms[5] == 20,
          "elapsed phases use only the active world's monotonic observations");

    check(state.apply({Phase::Play, kLinkHeld | kHostOfferHeld, gen, 7, 900}, 1, 380) && gate(state.stamp()) == Gate::Hold &&
          !old_generation(play, state.stamp()), "same-gen link or offer hold closes without a stale-generation label");
    check(state.apply({Phase::Play, kHostOfferHeld, gen, 8, 900}, 1, 390) && gate(state.stamp()) == Gate::Hold,
          "releasing one reason cannot open another hold");
    check(!state.apply({Phase::Play, 0, gen, 7}, 1, 391), "old same-generation resume cannot undo a newer hold");
    check(state.apply({Phase::Play, kPrivateHold, gen, 9}, 1, 392) && gate(state.stamp()) == Gate::Hold,
          "private save grace is a separate hold reason");
    check(state.apply({Phase::Play, 0, gen, 10}, 1, 393) && gate(state.stamp()) == Gate::Play, "fresh release opens the same world");
    check(!allowed(play, state.stamp(), Access::Live), "pre-hold frames cannot reappear after resume");
    const auto fresh = state.stamp();
    check(state.apply({Phase::Play, 0, gen, 10}, 1, 396) && fresh == state.stamp(), "exact duplicate control is idempotent");
    check(!state.apply({Phase::Play, kLinkHeld, gen, 10, 80}, 1, 397), "same request ID cannot change a control");
    check(state.apply({Phase::Disabled, 0, gen, 11}, 1, 398) && gate(state.stamp()) == Gate::Legacy &&
          !allowed(fresh, state.stamp(), Access::Live) && allowed(state.stamp(), state.stamp(), Access::Live),
          "switching off reopens only fresh legacy work");
    check(state.apply({Phase::Play, 0, gen, 12}, 1, 399) && gate(state.stamp()) == Gate::Play,
          "re-enable adopts current same-world play with a fresh receipt");
    check(state.apply({Phase::Load, 0, gen + 1, 13}, 1, 410) && old_generation(fresh, state.stamp()),
          "new world load retires all prior-world first-byte stamps");
    check(state.apply({Phase::Menu, 0, gen + 1, 14}, 1, 420) &&
          !state.apply({Phase::Play, 0, gen + 1, 15}, 1, 430),
          "failed load retires same-generation delayed playback");
    check(state.apply({Phase::Offer, 0, gen + 2, 15}, 1, 440) &&
          state.apply({Phase::Play, 0, gen + 2, 16}, 1, 450), "latest forward receipt can coalesce verified intermediate phases");
    const auto beforeDisconnect = state.stamp();
    state.disconnect(1, 460);
    check(gate(state.stamp()) == Gate::Hold && !allowed(beforeDisconnect, state.stamp(), Access::Live),
          "pipe loss after explicit on remains held");
    check(state.apply({Phase::Play, 0, gen + 2, 1}, 2, 470) && gate(state.stamp()) == Gate::Play &&
          !old_generation(beforeDisconnect, state.stamp()), "new link to the same world opens only after a fresh receipt");

    State wrapped;
    check(wrapped.apply({Phase::Load, 0, 1, UINT32_MAX}, 3, 10) &&
          wrapped.apply({Phase::Play, 0, 1, 1}, 3, 20), "nonzero control ID wraps modularly");

    using namespace kcdmp::pipecontrol;
    Reader reader;
    unsigned immediate = 0;
    const auto stamp = [&](Frame& frame) { frame.loadStamp = state.stamp(); };
    const auto control = [&](const Frame& frame) {
        if (frame.body[0] != kOp) return false;
        ++immediate;
        return true;
    };
    constexpr uint8_t poseHeader[]{kSamples, 1, 0};
    constexpr uint8_t poseCount[]{1};
    const auto oldStamp = state.stamp();
    check(reader.feed(poseHeader, sizeof poseHeader, control, stamp), "partial legacy pose reaches first-byte stamp");
    check(state.apply({Phase::Load, 0, gen + 3, 2}, 2, 500), "next load advances while old pose incomplete");
    check(reader.feed(poseCount, sizeof poseCount, control, stamp), "old partial pose completes after new world");
    Frame queued;
    check(reader.pop(&queued) && queued.loadStamp == oldStamp && !allowed(queued.loadStamp, state.stamp(), Access::Live),
          "late completion cannot refresh its ingress generation");
    std::array<uint8_t, 3 + kRequestBytes> wire{kGeneric, kRequestBytes, 0, kOp};
    check(reader.feed(wire.data(), wire.size(), control, stamp) && immediate == 1 && reader.queued() == 0,
          "new phase control takes the fast reader path while Main queues can stall");

    using namespace kcdmp::posering::receive;
    Ring raw(2);
    Pose pose;
    pose.body[0] = 'H';
    const Scope oldest{1, 1, oldStamp};
    const Scope current{1, 1, state.stamp()};
    check(raw.publish(pose, oldest) == Put::Accepted && raw.retire(current) == 1 && raw.drain(current).empty(),
          "old newest-wins pose slots are retired and counted as old generation");
    const auto holding = state.stamp();
    check(state.apply({Phase::Reconstruct, 0, gen + 3, 3}, 2, 530), "next phase in same world is a new fence");
    check(raw.publish(pose, {1, 1, holding}) == Put::Accepted && raw.retire({1, 1, state.stamp()}) == 0 &&
          raw.drain({1, 1, state.stamp()}).empty(), "obsolete same-world pose cannot apply yet doesn't increment stale generation");

    if (passed) *passed = ok;
    return failed;
}
