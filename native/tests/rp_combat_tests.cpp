// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
#include "rp_combat_rules.h"

#include <array>
#include <cstdio>
#include <cstring>
#include <limits>

int rp_combat_tests(int* passed) {
    using namespace kcdmp::rpcombat;
    using kcdmp::timelineactions::Accepted;
    using kcdmp::timelineactions::Blocked;
    using kcdmp::timelineactions::kCommit;
    int ok = 0, failed = 0;
    const auto check = [&](bool pass, const char* name) {
        if (pass) ++ok;
        else { ++failed; std::printf("RP combat failed: %s\n", name); }
    };

    std::array<uint8_t, kRegistrationHeader + 5> reg{5, 0x78, 0x56, 0x34, 0x12, kSwing, 7, 0, 0, 0};
    std::memcpy(reg.data() + kRegistrationHeader, "row-a", 5);
    Registration registration;
    check(decode_registration(reg.data(), reg.size(), registration) && registration.eid == 0x12345678 &&
          registration.peer == 5 && registration.kind == kSwing && registration.rowKey == 7 && registration.spec == "row-a",
          "Bulk row registration preserves body, kind, row key and setup text");
    reg[5] = kKo;
    check(!decode_registration(reg.data(), reg.size(), registration), "KO does not use textual setup");
    reg[5] = kHit; reg[6] = 0;
    check(!decode_registration(reg.data(), reg.size(), registration), "invalid row key refused");
    reg[6] = 7; reg[kRegistrationHeader + 1] = 0;
    check(!decode_registration(reg.data(), reg.size(), registration), "embedded NUL refused at setup");

    std::array<uint8_t, kActionHeader + kRangedPayloadSize> bytes{5, 0x78, 0x56, 0x34, 0x12, 0x24, 0, 0, 0, 0x96, 1, 0, 0, 0xff, 0xff, kSwing, kCommit};
    bytes[kActionHeader] = 7;
    Event event;
    for (const uint8_t kind : {kSwing, kBlock, kDodge, kParry, kHit, kRecoil}) {
        bytes[15] = kind;
        check(decode_event(bytes.data(), kActionHeader + 4, event) && event.eid == 0x12345678 && event.peer == 5 &&
              event.kind == kind && event.rowKey == 7 && event.hostMs == 0x19600000024ULL && event.sequence == 65535,
              "Realtime row is a typed key in the 64-bit host clock");
    }
    check(!decode_event(bytes.data(), kActionHeader + 5, event), "Realtime row refuses spec or trailing bytes");
    bytes[16] = 2;
    check(!decode_event(bytes.data(), kActionHeader + 4, event), "only committed actions reach the renderer");
    bytes[16] = kCommit;
    bytes[15] = kKo; bytes[kActionHeader] = 1;
    check(decode_event(bytes.data(), kActionHeader + 1, event) && event.stage == 1, "typed KO down");
    bytes[kActionHeader] = 0;
    check(decode_event(bytes.data(), kActionHeader + 1, event) && event.stage == 0, "typed KO wake");
    bytes[kActionHeader] = 2;
    check(!decode_event(bytes.data(), kActionHeader + 1, event), "unknown KO state refused");
    bytes[15] = kRanged; bytes[kActionHeader] = kRangedProtocolVersion;
    uint8_t* payload = bytes.data() + kActionHeader;
    payload[1] = 1;
    payload[2] = 3;
    payload[3] = 0x42;
    payload[19] = 0x11;
    payload[26] = 0x80;
    payload[30] = 0x90;
    payload[38] = 0x70;
    check(decode_event(bytes.data(), bytes.size(), event) && event.stage == 1 && event.rangedPhase == 3 &&
          event.itemClass[0] == 0x42 && event.rangedHand.peerSerial == 0x8000000000000011ULL &&
          event.rangedHand.bodyGeneration == 0x90000000 && event.rangedHand.revision == 0x7000000000000000ULL,
          "typed ranged V2 preserves class and all hand scope fields");
    check(!decode_event(bytes.data(), kActionHeader + 18, event), "unfenced legacy ranged payload is refused");
    payload[0] = 1;
    check(!decode_event(bytes.data(), bytes.size(), event), "unknown ranged protocol version is refused");
    payload[0] = kRangedProtocolVersion;
    payload[38] = 0;
    check(!decode_event(bytes.data(), bytes.size(), event), "ranged authority revision is required");
    payload[38] = 0x70;
    payload[2] = 4;
    check(decode_event(bytes.data(), bytes.size(), event) && event.rangedPhase == 4, "bow load permitted");
    payload[1] = 2;
    check(!decode_event(bytes.data(), bytes.size(), event), "unverified crossbow load excluded");
    payload[2] = 5;
    check(decode_event(bytes.data(), bytes.size(), event) && event.stage == 2, "typed crossbow uncharge");
    payload[3] = 0;
    check(!decode_event(bytes.data(), bytes.size(), event), "ranged item must have nonempty class");
    using kcdmp::timelineactions::HandSlot;
    using kcdmp::timelineactions::class_in_confirmed_slot;
    check(class_in_confirmed_slot(HandSlot::Left, 1, 0x42, 0, 0x42) &&
          class_in_confirmed_slot(HandSlot::Right, 2, 0, 0x42, 0x42),
          "strict reader accepts matching known on either requested slot");
    check(!class_in_confirmed_slot(HandSlot::Right, 3, 0x42, 0, 0x42) &&
          !class_in_confirmed_slot(HandSlot::Left, 2, 0x42, 0, 0x42) &&
          !class_in_confirmed_slot(HandSlot::Unknown, 3, 0x42, 0x42, 0x42),
          "same GUID moved slots, unknown actual slot and unknown expected slot are refused");

    Event hit{5, kHit, kCommit, 0, 8, 7, 9000, 42, 0, {}};
    Event recoil = hit;
    recoil.kind = kRecoil;
    Receipts receipts;
    check(!receipts.find(key(hit)), "first hit can be admitted");
    receipts.remember(key(hit), Accepted);
    check(receipts.find(key(hit)) == Accepted, "duplicate event yields original receipt without another native request");
    check(!receipts.find(key(recoil)), "independent recoil sequence at same stamp is not a duplicate");
    receipts.remember(key(recoil), Blocked);
    check(receipts.find(key(recoil)) == Blocked, "definitively refused event remains refused");
    Event newEpoch = hit;
    newEpoch.hostMs += 0x100000000ULL;
    check(!receipts.find(key(newEpoch)), "sequence wrap on a later full host stamp does not collide");
    for (uint16_t seq = 0; seq < 254; ++seq) {
        Event other = hit;
        other.hostMs += 1;
        other.sequence = seq;
        receipts.remember(key(other), Accepted);
    }
    check(receipts.full() && receipts.remember(key(newEpoch), Accepted) == Blocked && receipts.find(key(hit)) == Accepted,
          "saturated history cannot make an already installed event reusable");
    receipts.observe_target(12002);
    check(!receipts.full() && receipts.too_old(key(hit)), "old IDs stay beyond the presentation replay watermark");
    receipts.observe_target(9000);
    check(receipts.too_old(key(hit)), "receiver target regression cannot make an old receipt replayable");

    check(schedule(100, 100, 99, 100, 101, false) == Schedule::Wait, "future action waits for host presentation");
    check(schedule(100, 100, 100, 100, 101, false) == Schedule::Run, "current displayed body can start action");
    check(schedule(100, 99, 100, 100, 849, false) == Schedule::Wait &&
          schedule(100, 99, 100, 100, 850, false) == Schedule::Run, "current target can recover after bounded transmitted-pose wait");
    check(schedule(100, 0, 99, 100, 850, false) == Schedule::Expire, "ordinary action cannot present without due host clock");
    check(schedule(100, 100, 3101, 100, 101, false) == Schedule::Expire, "replay outside bounded window refused");
    check(schedule(100, 90, 100, 100, 101, true) == Schedule::Run, "exact tracked Wake uses clock even while held Fall freezes pose");
    check(schedule(100, 0, 0, 100, 849, true) == Schedule::Wait &&
          schedule(100, 0, 0, 100, 850, true) == Schedule::Run, "exact tracked recovery can wake without timeline after bounded wait");

    check(!visible_target(0, 0) && !visible_target(10, 20), "empty and unconfirmed requests cannot pass 95 percent");
    check(visible_target(19, 20) && !visible_target(18, 20) && !visible_target(19, 21) && !visible_target(2, 1),
          "95 percent uses exact ceil threshold with explicit denominator");
    const auto max = (std::numeric_limits<uint64_t>::max)();
    check(visible_target(max, max), "visibility arithmetic does not overflow");
    if (passed) *passed = ok;
    return failed;
}
