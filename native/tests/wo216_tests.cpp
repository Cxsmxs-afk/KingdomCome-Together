// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
// WO-216: engine-free checks of wo216_rules.h. Linked into KCDMP_NativeTests; wo216_tests() returns the number of failures.
#include <cstdio>
#include <cmath>
#include <cstring>
#include <initializer_list>

#include "wo216_rules.h"
// Windows headers define near as an empty legacy macro. These pure headers
// are also included AFTER windows.h by pipe_server.cpp: pin that include order.
#ifndef near
#define near
#define KCDMP_TEST_UNDEF_NEAR
#endif
#include "wo216_authority_rules.h"
#include "pipe_control_reader.h"
#ifdef KCDMP_TEST_UNDEF_NEAR
#undef near
#undef KCDMP_TEST_UNDEF_NEAR
#endif
#include <string>
#include <limits>
#include "wo215_rules.h"
#include "fader_rules.h"

using namespace kcdmp::wo216rules;

namespace {
int g_fail = 0, g_pass = 0;
#define WCHECK(cond, ...) do { if (cond) ++g_pass; else { ++g_fail; std::printf("FAIL  %s:%d  ", __FILE__, __LINE__); std::printf(__VA_ARGS__); std::printf("\n"); } } while (0)
} // namespace

int wo216_tests(int* passed) {
    // ---- the switch word and the bench's twin names
    int nk = 0;
    const Name* k = switch_names(&nk);
    uint32_t all = 0;
    for (int i = 0; i < nk; ++i) all |= k[i].b;
    WCHECK(all == kBitsDefault && nk == 7, "every WO-216 native switch has a name and ships on (%d names, 0x%X)", nk, all);
    WCHECK(twin_off_bits("route_replay") == kBitRouteReplay && twin_off_bits("no_brains, poses") == (kBitNoBrains | kBitPoses), "the twins by name");
    WCHECK(twin_off_bits("all") == kBitsDefault && twin_off_bits("route") == 0 && twin_off_bits(nullptr) == 0, "all; WO-215's names are not WO-216's");

    // ---- A.0 the record on the wire: 52 bytes after the name, every field back as it went
    {
        RouteRec r;
        r.reqId = 4711; r.speed = 3; r.flags = kRecActive | kRecGoal | kRecExact; r.style = 5;
        r.goal[0] = 1.5f; r.goal[1] = -2.25f; r.goal[2] = 30.0f;
        r.exact[0] = 1.0f; r.exact[1] = 2.0f; r.exact[2] = 3.0f; r.exactYaw = 1.25f; r.tolA = 0.4f; r.tolB = -2.0f; r.seq = 9; r.pathId = 77;
        uint8_t w[kRecWire];
        rec_put(r, w);
        RouteRec b;
        WCHECK(kRecWire == 52 && rec_get(w, &b), "the record is 52 bytes and reads back");
        WCHECK(b.reqId == 4711 && b.speed == 3 && b.flags == r.flags && b.style == 5 && b.seq == 9 && b.pathId == 77 &&
               std::memcmp(b.goal, r.goal, 12) == 0 && std::memcmp(b.exact, r.exact, 12) == 0 && b.exactYaw == 1.25f && b.tolA == 0.4f,
               "every field comes back as it went");
        w[8] = 0xFF; w[9] = 0xFF; w[10] = 0xFF; w[11] = 0x7F;   // goal.x = NaN
        WCHECK(!rec_get(w, &b), "a record with a non-finite goal is refused");
    }
    WCHECK(std::fabs(yaw_of_dir(-std::sin(0.7f), std::cos(0.7f)) - 0.7f) < 1e-5f, "the facing back out of the exact end's direction (yaw 0 faces +Y)");
    WCHECK(known_speed(0) && known_speed(1) && known_speed(7) && known_speed(11) && !known_speed(12) && !known_speed(-1), "the speed enums the engine's tables answer (0..11)");

    // ---- A.0 a change worth a line
    {
        RouteRec a, b;
        a.flags = b.flags = kRecActive | kRecGoal;
        a.reqId = b.reqId = 5;
        WCHECK(!rec_changed(a, b), "the same request is no change");
        b.goal[0] = 0.2f;
        WCHECK(!rec_changed(a, b), "a goal moved 20 cm is no change (25 cm is)");
        b.goal[0] = 0.3f;
        WCHECK(rec_changed(a, b), "a goal moved 30 cm is a change");
        b = a; b.reqId = 6;
        WCHECK(rec_changed(a, b), "another request is a change");
        b = a; b.speed = 3;
        WCHECK(rec_changed(a, b), "another class is a change");
        b = a; b.flags = 0; b.reqId = kNoReq;
        WCHECK(rec_changed(a, b), "the request ended is a change");
        RouteRec i1, i2;
        i2.goal[0] = 9.0f;
        WCHECK(!rec_changed(i1, i2), "two idle lines are the same whatever their stale fields say");
        b = a; b.flags |= kRecExact; a.flags |= kRecExact; b.exactYaw = 0.2f;
        WCHECK(rec_changed(a, b), "the exact end turned 0.2 rad is a change");
    }
    WCHECK(repeat_due(true, 2.1) && !repeat_due(true, 1.5) && !repeat_due(false, 3.0) && repeat_due(false, 6.5),
           "a running request is repeated every 2 s, an idle line every 6 s");

    // ---- A.1 the replay
    {
        RouteRec L;
        L.flags = kRecActive | kRecGoal; L.reqId = 10; L.seq = 3;
        WCHECK(replay_due(false, 0, 0, L), "a copy with no replay of its host's request makes it");
        WCHECK(!replay_due(true, 10, 3, L), "the same request and seq: once per change, never renewed");
        WCHECK(replay_due(true, 10, 2, L) && replay_due(true, 9, 3, L), "re-aimed (a new seq) or a new request: made again");
        RouteRec idle;
        WCHECK(!replay_due(false, 0, 0, idle), "no request on the host: nothing to replay (WO-215's face stands it)");
        RouteRec pre; pre.flags = kRecActive | kRecPrePath; pre.reqId = 4;
        WCHECK(!replay_due(false, 0, 0, pre), "a path given with the request (no goal read): not replayed (WO-215's walk-to-sample)");
    }
    WCHECK(line_live(true, 3.0) && !line_live(true, 3.6) && line_live(false, 7.0) && !line_live(false, 8.5) && !line_live(true, -1.0),
           "a running line lives 3.5 s (the host repeats it every 2 s), an idle one 8 s");
    WCHECK(drift_cause(0.4) == Cause::Stall && drift_cause(1.5) == Cause::Drift && drift_cause(-1.0) == Cause::Drift,
           "a drift within 1 s after a link stall is the stall's");
    WCHECK(no_path(true, true, 3.0f) && !no_path(true, true, 1.0f) && !no_path(false, true, 3.0f) && !no_path(true, false, 3.0f),
           "our request ended 3 m from the goal while the host's runs: no-path (1 m: it arrived first)");
    WCHECK(cancel_on_host_end(true, 2.0f) && !cancel_on_host_end(true, 0.4f) && !cancel_on_host_end(false, 2.0f),
           "the host's request ended: ours cancelled unless it is within 0.6 m of its goal");
    WCHECK(std::strcmp(cause_name(Cause::NoPath), "no-path") == 0 && std::strcmp(cause_name(Cause::Stall), "stall") == 0, "the causes by name");
    WCHECK(parity_bin(0.0f) == 0 && parity_bin(0.25f) == 2 && parity_bin(9.0f) == kParityBins - 1 && parity_bin(-1.0f) == 0, "the parity bins (10 cm)");
    {   // the corrector's way: the host's last 1.2 s
        const float a[2] = {0, 0}, b[2] = {10, 0};
        const float p1[2] = {5, 2}, p2[2] = {-3, 4}, p3[2] = {12, 0};
        WCHECK(std::fabs(seg_dist2d(p1, a, b) - 2.0f) < 1e-4f, "beside a segment: the perpendicular");
        WCHECK(std::fabs(seg_dist2d(p2, a, b) - 5.0f) < 1e-4f && std::fabs(seg_dist2d(p3, a, b) - 2.0f) < 1e-4f, "beyond its ends: the end");
        WCHECK(std::fabs(seg_dist2d(p1, a, a) - std::sqrt(29.0f)) < 1e-4f, "a zero-length segment is its point");
        const float way[4][2] = {{0, 0}, {1.4f, 0}, {2.8f, 0}, {4.2f, 0}};   // a walker's 3 s at 1.4 m/s, the newest last
        const float behind[2] = {1.0f, 0.3f};   // 0.3 m beside the way, about 2.3 s behind the host's point now
        WCHECK(way_off(behind, way, 4) < 0.31f, "a copy behind its host on the host's own way is on its way (b6 pulled it back)");
        const float beside[2] = {3.0f, 2.0f};
        WCHECK(std::fabs(way_off(beside, way, 4) - 2.0f) < 1e-4f, "a copy 2 m beside the way is off it");
        const float one[1][2] = {{1, 1}};
        const float q[2] = {4, 5};
        WCHECK(std::fabs(way_off(q, one, 1) - 5.0f) < 1e-4f && way_off(q, one, 0) == 0.0f, "one point: the distance to it; none: nothing");
    }

    // ---- B
    WCHECK(breach(0) && !breach(1) && !breach(2) && !breach(-1), "a running brain (state 0) is a breach; suspended or unknown are not");
    WCHECK(decision_suppressed(2, true) && !decision_suppressed(1, true) && !decision_suppressed(0, true) &&
           !decision_suppressed(2, false), "Paket C: only the joiner with the installed entry gate skips the decision batch");
    WCHECK(!breach(0, true) && breach(0, false), "Paket C: a gated batch keeps node runtime untouched; state 0 alone is no executed decision");
    for (uint8_t role : {uint8_t{2}, uint8_t{0}, uint8_t{1}, uint8_t{2}})
        WCHECK(decision_suppressed(role, true) == (role == 2), "Paket C: cancel/own save/host/rejoin follows the current role");
    WCHECK(hide_unbound(false, false) && !hide_unbound(true, false) && !hide_unbound(false, true), "an unbound body is hidden; a copy or a figure is not");
    WCHECK(fighting_a_player(true, false) && fighting_a_player(false, true) && !fighting_a_player(false, false),
           "a fight with the local player or the other player's figure");
    WCHECK(!hide_unbound(false, false, true) && !hide_unbound(false, true, true) && !hide_unbound(true, false, true),
           "an NPC fighting a player is never hidden, bound or not");
    WCHECK(hide_unbound(false, false, false), "an unbound bystander is still hidden");
    WCHECK(hide_holds(false, true, 0.4) == false && hide_holds(true, false, 0.4) == true, "a hide and a show inside 2 s do not flip");
    WCHECK(hide_holds(false, true, 2.5) == true && hide_holds(true, false, 3.0) == false, "after the hold, the sweep may hide or show");
    WCHECK(streamable_name(63) && !streamable_name(65) && !streamable_name(0),
           "a name the stream cannot carry (the animal care's horses, 65) is never hidden: it can never be a copy");

    // P1: pure control runs independently of any game/main-thread queue.
    {
        using namespace kcdmp::wo216authority;
        State a;
        WCHECK(a.enabled() && !a.active(2, true, 100000), "P1: role 2 alone is pending, never an active gate");
        uint8_t wire[kRequestBytes] = {kOp, uint8_t(Phase::Load)};
        put_u64(wire + 2, 0x0102030405060708ull); put_u32(wire + 10, 0xA1B2C3D4u);
        Request decoded;
        WCHECK(kRequestBytes == 14 && kAckBytes == 19 && request(wire, sizeof wire, &decoded), "P1: opcode plus 13 request bytes, 19 ACK bytes");
        WCHECK(wire[2] == 8 && wire[9] == 1 && wire[10] == 0xD4 && wire[13] == 0xA1 && decoded.id == 0xA1B2C3D4u && decoded.generation == 0x0102030405060708ull, "P1: explicit little-endian generation/request correlation");
        WCHECK(!request(wire, sizeof wire - 1, &decoded), "P1: truncated request rejected");
        wire[1] = 4; WCHECK(!request(wire, sizeof wire, &decoded), "P1: unsupported phase rejected"); wire[1] = 1;
        WCHECK(a.apply({Phase::Query, 0, 1}, 10) && a.generation == 0 && !a.apply({Phase::Query, 1, 1}, 10), "P1: query only reads, including fresh agent generation seed");
        WCHECK(!a.apply({Phase::Gameplay, 1, 1}, 10), "P1: Gameplay cannot establish a world without Load proof");
        WCHECK(a.apply({Phase::Load, 1, 2}, 100), "P1: explicit pending accepted before ModLoad");
        a.role_changed(0, 2, true);
        WCHECK(a.loadAccepted && a.apply({Phase::Load, 1, 3}, 200), "P1: Query/Load/tagged role2/idempotent Load keeps same pending proof");
        WCHECK(a.apply({Phase::Gameplay, 1, 4}, 10000), "P1: same-load Gameplay accepted");
        unsigned blockedWhileLoading = 0;
        for (uint64_t t = 10000; t < 15000; ++t) if (a.active(2, true, t)) ++blockedWhileLoading;
        WCHECK(blockedWhileLoading == 0 && a.active(2, true, 15000), "P1: no decisions blocked during load/settle; activates at Gameplay +5000 exactly");
        WCHECK(!a.active(0, true, 15000) && !a.active(1, true, 15000) && !a.active(2, false, 15000), "P1: own world, host and missing hook never blocked");
        WCHECK(a.apply({Phase::Gameplay, 1, 5}, 16000) && a.gameplayAtMs == 10000 && a.active(2, true, 16000), "P1: Gameplay heartbeat never resets settle");
        WCHECK(!a.apply({Phase::Load, 1, 6}, 17000), "P1: late old Load cannot roll active Gameplay backward");
        uint8_t ack[kAckBytes]{}; a.ack(ack, 2, true, 16000, 0xA1B2C3D4u);
        WCHECK(ack[0] == 2 && ack[1] == 1 && ack[2] == 1 && read_u64(ack + 3) == 1 && read_u32(ack + 11) == 6000 && read_u32(ack + 15) == 0xA1B2C3D4u, "P1: ACK exact 19-byte field offsets and request id echo");
        WCHECK(a.apply({Phase::Load, 2, 7}, 20000) && !a.active(2, true, 99999), "P1: every new load immediately invalidates active gate");
        WCHECK(!a.apply({Phase::Gameplay, 1, 8}, 21000) && a.phase == Phase::Load && a.generation == 2, "P1: stale Gameplay cannot activate newer Load");
        WCHECK(a.apply({Phase::Gameplay, 2, 9}, 22000) && !a.active(2, true, 26999) && a.active(2, true, 27000), "P1: later load receives its own settle");
        a.role_changed(2, 0, true); a.role_changed(0, 2, true);
        WCHECK(!a.loadAccepted && !a.active(2, true, 99999), "P1: arbitrary role switching cannot reuse old Gameplay");
        WCHECK(a.apply({Phase::Disabled, 3, 10}, 30000) && !a.enabled() && !a.active(2, true, 99999) && !a.legacy(2), "P1: Disabled off-main immediately clears decision and legacy permission");
        a.role_changed(2, 0, true); a.role_changed(0, 2, true);
        WCHECK(a.legacy(2) && !a.legacy(1), "P1: explicit current-generation role transition restores PR14 only for joiner");
        WCHECK(a.apply({Phase::Disabled, 3, 11}, 31000) && a.legacy(2), "P1: repeated Disabled heartbeat does not disrupt legacy play");
        WCHECK(a.apply({Phase::Disabled, 4, 12}, 32000) && !a.legacy(2), "P1: Off load invalidates legacy even while Main is blocked");
        uint32_t bits = 7; uint8_t role = 0;
        if (a.role_generation(true, 3)) { a.role_changed(role, 2, true); role = 2; bits = 99; }
        WCHECK(bits == 7 && role == 0 && !a.legacy(2), "P1: queued stale tagged role cannot change role, latch or bits after newer Disabled");
        a.role_changed(0, 2, false);
        WCHECK(!a.legacy(2), "P1: old untagged role format never rearms Disabled legacy");
        State pending; pending.apply({Phase::Load, 1, 1}, 1); pending.role_changed(0, 2, false);
        WCHECK(!pending.loadAccepted, "P1: an untagged role cannot retain new pending proof");
    }
    {
        using namespace kcdmp::wo216authority;
        WCHECK(!coverage(10, 0) && !coverage(0, 0) && !coverage(10, 2) && coverage(10, 3) && coverage(10, 4) && !coverage(10, 3, false), "P1: nearby bound0/under30percent/unknown player never authorize hiding");
        WCHECK(hide_eligible(true, true, false, true, false, false, true), "P1: initialized known living nearby bystander can qualify");
        WCHECK(!hide_eligible(false, true, false, true, false, false, true) && !hide_eligible(true, false, false, true, false, false, true) && !hide_eligible(true, true, true, true, false, false, true), "P1: unready, unknown life and dead body never hidden");
        WCHECK(!hide_eligible(true, true, false, false, false, false, true) && !hide_eligible(true, true, false, true, true, false, true) && !hide_eligible(true, true, false, true, false, true, true), "P1: distant, fighting and exempt bodies stay visible");
        HideLease h;
        WCHECK(!h.desired(false, true, false, 1) && h.desired(false, true, true, 1), "P1: complete coverage required before first hide");
        h.applied(true, 100);
        WCHECK(h.desired(false, true, true, 10099) && !h.desired(false, true, true, 10100) && h.expired, "P1: an unbound hide expires at 10 seconds");
        h.applied(false, 10100);
        WCHECK(!h.desired(false, true, true, 99999), "P1: expired unbound episode cannot hide again next census");
        h.applied(true, 100000);
        WCHECK(!h.desired(true, true, true, 110000) && !h.expired, "P1: binding exactly at expiry resets the episode before expiry evaluation");
        h.applied(false, 110000);
        WCHECK(h.desired(false, true, true, 110001), "P1: later unbinding starts a new eligible episode");
        PauseLease ownPause;
        WCHECK(!ownPause.claim(10, 20, 3, 2, 8) && !ownPause.claim(10, 20, 3, 0, 8), "P1: already paused / foreign context is never claimed by legacy cleanup");
        WCHECK(ownPause.claim(10, 20, 3, 0, 0), "P1: only our PauseNPC issued from running/mask0 owns a lease");
        WCHECK(ownPause.may_resume(10, 20, 3, 2, 8), "P1: mode stop may resume the same owned entity/WUID/generation and exact debug context");
        WCHECK(!ownPause.may_resume(10, 20, 4, 2, 8) && !ownPause.may_resume(11, 20, 3, 2, 8) && !ownPause.may_resume(10, 21, 3, 2, 8), "P1: stale generation, EID reuse and a new WUID never open a pause");
        WCHECK(!ownPause.may_resume(10, 20, 3, 2, 0x18) && !ownPause.may_resume(10, 20, 3, 2, 0x10) && !ownPause.may_resume(10, 20, 3, 0, 0), "P1: foreign/additional context or already resumed state gets no ResumeNPC");
        WCHECK(ownPause.may_resume(10, 20, 3, 2, 8), "P1: retained lease can retry after the foreign context leaves");
        ownPause.forget(); WCHECK(!ownPause.may_resume(10, 20, 3, 2, 8), "P1: cleanup is impossible after ownership is released");
        Stats stats{5, 4294967295ull, 0, 300, 90, 210, 0, 20};
        WCHECK(stats.nearCount == 300, "P1: Windows near macro cannot erase the nearby-count member");
        char text[512]{}; const int n = stats_prefix(text, sizeof text, stats);
        WCHECK(n > 0 && n < 400, "P1: all eight import counters fit before first512-byte boundary");
        const char* keys[] = {"gate_armed_at_s=", "decision_blocked=", "decision_blocked_during_load=", "npc_near=", "npc_bound=", "npc_hidden=", "hide_failsafe_on=", "unhidden_unbound_10s="};
        for (const char* key : keys) {
            const char* pos = std::strstr(text, key);
            WCHECK(pos && !std::strstr(pos + std::strlen(key), key), "P1: each required numeric key occurs exactly once: %s", key);
        }
        const std::string longReason(1200, 'x'); std::snprintf(text + n, sizeof text - static_cast<size_t>(n), "decision_reason=%s", longReason.c_str());
        for (const char* key : keys) WCHECK(std::strstr(text, key), "P1: long diagnostic cannot truncate required counter: %s", key);
    }

    // P1 reader pump: partial frames and older normal work cannot delay pure
    // Load control behind the 3.5 s main wait, nor reorder reliable normal work.
    {
        namespace pc = kcdmp::pipecontrol;
        using namespace kcdmp::wo216authority;
        auto wireFrame = [](uint8_t type, const std::vector<uint8_t>& body) {
            std::vector<uint8_t> w{type, uint8_t(body.size()), uint8_t(body.size() >> 8)};
            w.insert(w.end(), body.begin(), body.end()); return w;
        };
        std::vector<uint8_t> control(kRequestBytes, 0); control[0] = kOp; control[1] = uint8_t(Phase::Load);
        put_u64(control.data() + 2, 7); put_u32(control.data() + 10, 0x1234ABCDu);
        const auto ctlWire = wireFrame(pc::kGeneric, control);
        for (size_t split = 0; split <= ctlWire.size(); ++split) {
            pc::Reader r(254); unsigned calls = 0;
            auto ack = [&](const pc::Frame& f) { ++calls; return f.sequence == 254 && f.body == control; };
            WCHECK(r.feed(ctlWire.data(), split, ack) && r.feed(ctlWire.data() + split, ctlWire.size() - split, ack) && calls == 1 && r.queued() == 0 && r.next_sequence() == 255,
                "P1: every control header/body split decodes once (%zu)", split);
        }
        pc::Reader reader(250); State state;
        bool mainDone = false, ackWhileMainPending = false, mutexReleased = false;
        unsigned ackCount = 0; long long ackMs = 9999; uint8_t ack[kAckBytes]{};
        std::mutex mainMutex; std::condition_variable mainCv;
        const auto began = std::chrono::steady_clock::now();
        auto accept = [&](const pc::Frame& f) {
            ++ackCount; ackWhileMainPending = !mainDone;
            ackMs = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - began).count();
            Request req; if (!request(f.body.data(), f.body.size(), &req) || !state.apply(req, 100)) return false;
            state.ack(ack, 2, true, 100, req.id); return true;
        };
        const auto active = wireFrame(0x0A, {}); // the original normal request is waiting on Main
        WCHECK(reader.feed(active.data(), active.size(), accept), "P1: original normal frame decoded");
        pc::Frame f; WCHECK(reader.pop(&f) && f.sequence == 250, "P1: original normal has its wire-read sequence");
        auto before = wireFrame(pc::kGeneric, {161});
        const auto batch = wireFrame(pc::kBatch, {1, 0, 0});
        before.insert(before.end(), batch.begin(), batch.end());
        std::vector<std::vector<uint8_t>> chunks{before, {ctlWire[0]}, {ctlWire[1]},
            std::vector<uint8_t>(ctlWire.begin() + 2, ctlWire.begin() + 8),
            std::vector<uint8_t>(ctlWire.begin() + 8, ctlWire.end()), wireFrame(pc::kGeneric, {171})};
        size_t next = 0;
        std::unique_lock<std::mutex> mainLock(mainMutex);
        const auto waited = pc::wait_pumped(mainCv, mainLock, std::chrono::milliseconds(500), [&] { return mainDone; }, [&] {
            if (mainMutex.try_lock()) { mutexReleased = true; mainMutex.unlock(); }
            if (next < chunks.size()) { const auto& c = chunks[next++]; return reader.feed(c.data(), c.size(), accept); }
            return true;
        });
        WCHECK(waited == pc::Wait::TimedOut && !mainDone && ackWhileMainPending && ackCount == 1 && ackMs < 1000 && mutexReleased,
            "P1: actual 5ms-slice wait ACKs split Load while Main remains unfinished (%lld ms), with mutex released", ackMs);
        std::printf("P1 control-pump ACK while Main unfinished: %lld ms (5 ms wait slices)\n", ackMs);
        WCHECK(ack[0] == 1 && read_u64(ack + 3) == 7 && read_u32(ack + 15) == 0x1234ABCDu, "P1: pump invokes real lifecycle decoder/state/ACK including id");
        WCHECK(reader.queued() == 3 && reader.pop(&f) && f.body[0] == 161 && f.sequence == 251, "P1: older normal frame stayed deferred, exact sequence once");
        WCHECK(reader.pop(&f) && f.type == pc::kBatch && reader.pop(&f) && f.body[0] == 171 && f.sequence == 253 && !reader.pop(&f) && reader.next_sequence() == 254,
            "P1: batch consumes no single sequence; later normal order/sequence retained across control bypass");
        const auto large = wireFrame(pc::kSamples, std::vector<uint8_t>(4096, 3));
        pc::Reader partial; unsigned controls = 0;
        auto noControl = [&](const pc::Frame&) { ++controls; return true; };
        bool allBytes = true;
        for (size_t i = 0; i < large.size(); ++i) allBytes = allBytes && partial.feed(large.data() + i, 1, noControl);
        WCHECK(allBytes && partial.pop(&f) && f.body.size() == 4096 && controls == 0 && partial.partial_bytes() == 0, "P1: payload larger than a small OS-buffer slice consumes bytewise without full-frame Peek");
        pc::Reader malformed;
        const uint8_t huge[] = {pc::kGeneric, 1, 16}; //4097 is never allowed
        WCHECK(!malformed.feed(huge, sizeof huge, noControl) && malformed.failed(), "P1: oversized length rejected at header, before payload allocation");
        pc::Reader longStats;
        std::vector<uint8_t> statsBody{kcdmp::t3stats::kOp, 1};
        for (unsigned i = 0; i < 75; ++i) {
            const std::string field = (i ? " " : "") + std::string("metric") + std::to_string(i) + "=1234567890";
            statsBody.insert(statsBody.end(), field.begin(), field.end());
        }
        const auto statsWire = wireFrame(pc::kGeneric, statsBody);
        WCHECK(statsBody.size() > 1300 && kcdmp::t3stats::valid(statsBody.data() + 1, statsBody.size() - 1) && longStats.feed(statsWire.data(), statsWire.size(), noControl) && longStats.pop(&f) && f.body == statsBody,
            "P1: numeric T3 mirror larger than1300B stays allowed with batching off");
        pc::Reader forbiddenGeneric; const auto oversizedNormal = wireFrame(pc::kGeneric, std::vector<uint8_t>(1025, 42));
        WCHECK(!forbiddenGeneric.feed(oversizedNormal.data(), oversizedNormal.size(), noControl) && forbiddenGeneric.failed(), "P1: generic above1024B is rejected after decode unless opcode250");
        pc::Reader bounded; bool accepted = true;
        for (size_t i = 0; i < pc::kDeferredFrames + 1; ++i) accepted = bounded.feed(active.data(), active.size(), noControl);
        WCHECK(!accepted && bounded.failed() && bounded.queued() == pc::kDeferredFrames, "P1: bounded normal-frame overflow fails connection, no silent reliable drop");
        pc::Reader byteBound; const auto medium = wireFrame(pc::kGeneric, std::vector<uint8_t>(1024, 7));
        while (byteBound.feed(medium.data(), medium.size(), noControl)) {}
        WCHECK(byteBound.failed() && byteBound.queued_bytes() <= pc::kDeferredBytes, "P1: deferred payload bytes bounded independently of frame count");
        pc::Reader wrapped(255); unsigned echoedSeq = 0;
        auto wrapAck = [&](const pc::Frame& c) { echoedSeq = c.sequence; return true; };
        WCHECK(wrapped.feed(ctlWire.data(), ctlWire.size(), wrapAck) && echoedSeq == 255 && wrapped.feed(active.data(), active.size(), wrapAck) && wrapped.pop(&f) && f.sequence == 0,
            "P1: control sequence allocated once even across255->0 normal wrap");
        pc::Reader otherOp; const auto ordinary = wireFrame(pc::kGeneric, {169});
        WCHECK(otherOp.feed(ordinary.data(), ordinary.size(), noControl) && controls == 0 && otherOp.pop(&f) && f.body[0] == 169, "P1: another off-main-capable opcode cannot bypass FIFO");
        std::unique_lock<std::mutex> finishedLock(mainMutex, std::defer_lock); mainLock.unlock(); finishedLock.lock();
        WCHECK(pc::wait_pumped(mainCv, finishedLock, std::chrono::milliseconds(20), [] { return false; }, [] { return false; }) == pc::Wait::TransportFailed,
            "P1: disconnected/generation-changed pump ends wait rather than touching next connection");
    }

    // Paket F1: no anonymous black, no other owner's release, no stale lease
    // after a load/disconnect and a new hold. Respawn remains source compatible.
    {
        using kcdmp::fader::Owner;
        kcdmp::fader::Lease lease;
        WCHECK(!lease.may_acquire(Owner::None, 1) && !lease.may_acquire(Owner::Wake, 0) &&
            !lease.may_acquire(static_cast<Owner>(99), 1), "F1: unknown reason and zero lease create no fade");
        WCHECK(lease.may_acquire(Owner::Wake, 10), "F1: a known wake owns its hold");
        lease.acquired(Owner::Wake, 10);
        WCHECK(lease.may_acquire(Owner::Wake, 10), "F1: retry of the same lease is idempotent");
        WCHECK(!lease.may_acquire(Owner::VideoLanding, 11) && !lease.may_acquire(Owner::Respawn, 1),
            "F1: concurrent owners cannot overwrite a hold");
        WCHECK(!lease.matches(Owner::VideoLanding, 10) && !lease.matches(Owner::Wake, 9) && !lease.matches(Owner::Respawn, 1),
            "F1: wrong owner, stale timer and respawn cannot lift the wake's fade");
        WCHECK(kcdmp::fader::external(lease.owner), "F1: lifecycle reset can identify an external hold");
        lease.clear();
        lease.acquired(Owner::Wake, 12);
        WCHECK(!lease.matches(Owner::Wake, 10) && lease.matches(Owner::Wake, 12), "F1: an old release cannot lift a new hold");
        lease.clear();
        WCHECK(lease.may_acquire(Owner::Respawn, 1), "F1: existing respawn calls keep their known owner");
        lease.acquired(Owner::Respawn, 1);
        WCHECK(!kcdmp::fader::external(lease.owner), "F1: external lifecycle cleanup never releases native respawn");
    }
    if (passed) *passed = g_pass;
    return g_fail;
}
