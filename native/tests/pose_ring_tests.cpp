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

void put32(std::vector<uint8_t>& bytes, size_t offset, uint32_t value) {
    for (unsigned i = 0; i != 4; ++i) bytes[offset + i] = static_cast<uint8_t>(value >> (i * 8));
}

void receive_tests() {
    using namespace kcdmp::posering::receive;
    // Wire fixture for relay Ghost id 7, a ridden pose with State2 and a sender stamp.
    auto ghost = std::vector<uint8_t>(kEnvelope + 34);
    ghost[0] = 0x02;
    put32(ghost, 1, 250);
    ghost[kEnvelope] = 7;
    put32(ghost, kEnvelope + 1, 0x3FC00000); // x = 1.5
    ghost[kEnvelope + 17] = 0x19;
    ghost[kEnvelope + 18] = 42;
    ghost[kEnvelope + 21] = 0x40;
    put32(ghost, kEnvelope + 30, 207);
    Pose a;
    PCHECK(decode(ghost.data(), ghost.size(), a) && a.kind == kGhost && std::strcmp(a.body, "kcd2mp_7") == 0 &&
           a.x == 1.5f && a.flags == 0x10 && a.state[0] == 42 && a.state[3] == 0x40 && a.senderMs == 207 && a.stateQpc == 250,
           "Ghost v8 wire position, ride, State2 and sender stamp agree");
    for (size_t length : {18u, 22u, 30u, 34u}) {
        Pose variant;
        PCHECK(decode(ghost.data(), kEnvelope + length, variant) && (variant.stateQpc != 0) == (length >= 30),
               "Ghost version %zu permits the same optional tail as the Agent", length);
    }
    PCHECK(!decode(ghost.data(), ghost.size() - 1, a), "mis-sized Ghost tail is rejected");
    PCHECK(decode(ghost.data(), ghost.size(), a), "a valid pose decodes after invalid length");

    auto npc = std::vector<uint8_t>(kEnvelope + 2 + 3 + kNpcTail);
    npc[0] = 0x27;
    put32(npc, 1, 260);
    npc[kEnvelope] = 1;
    npc[kEnvelope + 1] = 3;
    std::memcpy(npc.data() + kEnvelope + 2, "NPC", 3);
    const size_t values = kEnvelope + 2 + 3;
    put32(npc, values + 4, 0xC0000000); // y = -2
    npc[values + 20] = 0x93; // pipe marker stripped, dead/KO/carried preserved
    npc[values + 21] = 0xFF;
    npc[values + 22] = 0xFF;
    put32(npc, values + 23, 1);
    Pose b;
    PCHECK(decode(npc.data(), npc.size(), b) && std::strcmp(b.body, "NPC") == 0 && b.y == -2.0f && b.flags == 0x13 &&
           b.sequence == 65535 && b.senderMs == 1, "NPC relay layout preserves flags, sequence and sender stamp");
    npc[kEnvelope + 2] = '"';
    PCHECK(!decode(npc.data(), npc.size(), b), "unsafe NPC name does not reach a body lookup");
    npc[kEnvelope + 2] = 'N';
    put32(npc, values + 4, 0x7F800000);
    PCHECK(!decode(npc.data(), npc.size(), b), "nonfinite body pose is rejected");
    put32(npc, values + 4, 0xC0000000);
    PCHECK(decode(npc.data(), npc.size(), b), "valid NPC data restored");

    Ring pending(2);
    const Scope first{1, 5}, second{3, 5};
    PCHECK(pending.publish(a, first) == Put::Accepted && pending.publish(b, first) == Put::Accepted, "independent bodies have slots");
    auto later = a;
    later.arrivalQpc = 300; later.senderMs = 221; later.stateQpc = 0; later.state = {}; later.x = 3.0f;
    PCHECK(pending.publish(later, first) == Put::Accepted && pending.publish(a, first) == Put::Superseded,
           "late copies cannot replace newest pose; occupied bodies can still update a full ring");
    auto wrapped = b;
    wrapped.arrivalQpc = 270; wrapped.sequence = 0; wrapped.senderMs = 2; wrapped.flags = 0;
    std::memcpy(wrapped.body, "npc", 4);
    PCHECK(pending.publish(wrapped, first) == Put::Accepted && pending.publish(b, first) == Put::Superseded,
           "same NPC key ignores casing and uses wrap-safe sequence order");
    auto third = b;
    std::memcpy(third.body, "cat", 4);
    PCHECK(pending.publish(third, first) == Put::Full, "new body overflow fails visibly without evicting other bodies");
    const auto frames = pending.drain(first);
    PCHECK(frames.size() == 2 && frames[0].sequence == 0 && frames[0].flags == 0 && frames[1].x == 3.0f &&
           frames[1].stateQpc == 250 && frames[1].state[0] == 42, "newest poses drain in arrival order with omitted State2 retained at real age");
    PCHECK(pending.drain(first).empty(), "drain consumes the current ring once");

    pending.publish(a, first);
    pending.publish(b, second);
    PCHECK(pending.drain(first).empty(), "a stale scope drain never extracts or erases a newer world");
    const auto fresh = pending.drain(second);
    PCHECK(fresh.size() == 1 && fresh[0].kind == kNpcStateDown, "a scope transition discards the prior world's pending body");
    pending.publish(a, second);
    auto lateSender = later;
    lateSender.senderMs = 200;
    PCHECK(pending.publish(lateSender, second) == Put::Superseded && pending.publish(lateSender, second, false) == Put::Accepted,
           "sender order prevents rollback and obeys the compatibility clock toggle");
}
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

    receive_tests();

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
