// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// Runs the production scheduler with isolated engine and clock doubles.
#include <algorithm>
#include <array>
#include <cstdarg>
#include <cstdio>
#include <string>
#include <vector>

// Preserve the normal components used by the other suites in this test executable.
#define kcdmp kcdmp_h15_scheduler_test
#define GetTickCount64 kct_h15_scheduler_test_clock
#define KCDMP_H15_SCHEDULER_TEST
#include "../KCDMP/rp_timeline_actions.cpp"
#undef KCDMP_H15_SCHEDULER_TEST

namespace {
using namespace kcdmp;
using timelineactions::Scope;

constexpr uint32_t kEid = 7, kRow = 24;
constexpr uint8_t kPeer = 2;
constexpr uint64_t kHostMs = 4000, kReadMs = 1000;
constexpr uint16_t kSequence = 17;
int body, actor;
struct Simulation {
    Scope scope{7, 3, 2, 0};
    uint64_t now = kReadMs, applied = 0, target = 0, token = 0;
    npcdrive::PartnerPose pose;
    uint8_t kind = rpcombat::kSwing;
    bool mode = true, protectedVisual = true;
    rttr::SwingResult providerResult = rttr::SwingResult::Ok;
    unsigned typedCalls = 0, legacyCalls = 0;
    std::vector<std::string> logs;
} sim;

auto row_wire() {
    std::array<uint8_t, rpcombat::kRegistrationHeader + 1> bytes{};
    bytes[0] = kPeer;
    loadsequence::write_le(bytes.data() + 1, kEid, 4);
    bytes[5] = sim.kind;
    loadsequence::write_le(bytes.data() + 6, kRow, 4);
    bytes.back() = 'a';
    return bytes;
}

auto action_wire(uint64_t hostMs, uint16_t sequence) {
    std::array<uint8_t, rpcombat::kActionHeader + 4> bytes{};
    bytes[0] = kPeer;
    loadsequence::write_le(bytes.data() + 1, kEid, 4);
    loadsequence::write_le(bytes.data() + 5, hostMs, 8);
    loadsequence::write_le(bytes.data() + 13, sequence, 2);
    bytes[15] = sim.kind;
    bytes[16] = timelineactions::kCommit;
    loadsequence::write_le(bytes.data() + rpcombat::kActionHeader, kRow, 4);
    return bytes;
}

auto submit(Scope received = sim.scope, uint64_t readMs = kReadMs, uint64_t hostMs = kHostMs,
            uint16_t sequence = kSequence) {
    const auto bytes = action_wire(hostMs, sequence);
    return timelineactions::submit_combat(bytes.data(), bytes.size(), received, readMs);
}

bool begin(bool admit = true, uint8_t kind = rpcombat::kSwing) {
    timelineactions::reset();
    sim = {};
    sim.kind = kind;
    timelineactions::configure(true);
    const auto row = row_wire();
    return timelineactions::register_combat(row.data(), row.size(), sim.scope) == timelineactions::Accepted &&
        (!admit || submit() == timelineactions::Accepted);
}

void pose_pulse(uint64_t ageMs = 0, uint64_t hostMs = kHostMs) {
    sim.pose = {sim.scope.lifecycle, sim.scope.connection, sim.scope.life, sim.scope.load, ageMs, hostMs};
}

unsigned logs_with(const char* result, const char* reason, const char* dispatch, const char* terminal = "1") {
    const auto has = [](const std::string& line, const char* key, const char* value) {
        return line.find(std::string(key) + "=" + value + " ") != std::string::npos;
    };
    return static_cast<unsigned>(std::count_if(sim.logs.begin(), sim.logs.end(), [&](const std::string& line) {
        return has(line, "seq", "17") && has(line, "event_host_ms", "4000") && has(line, "result", result) &&
            has(line, "reason", reason) && has(line, "dispatch", dispatch) && has(line, "terminal", terminal);
    }));
}

unsigned terminal_logs() {
    return static_cast<unsigned>(std::count_if(sim.logs.begin(), sim.logs.end(), [](const std::string& line) {
        return line.find(" seq=17 ") != std::string::npos && line.find(" event_host_ms=4000 ") != std::string::npos &&
            line.find(" terminal=1 ") != std::string::npos;
    }));
}
} // namespace

uint64_t GetTickCount64() { return sim.now; }

namespace kcdmp {
void logf(const char* format, ...) {
    char line[1024];
    va_list args;
    va_start(args, format);
    std::vsnprintf(line, sizeof line, format, args);
    va_end(args);
    sim.logs.emplace_back(std::string(line) + " ");
}
}
namespace kcdmp::engine {
void* entity_by_id(uint32_t eid) { return eid == kEid ? &body : nullptr; }
const char* entity_name(void* entity) { return entity == &body ? "kcd2mp_2" : nullptr; }
}
namespace kcdmp::hits {
bool visual_damage_ready(uint32_t) { return sim.protectedVisual; }
}
namespace kcdmp::motion {
void* actor_of(uint32_t eid) { return eid == kEid ? &actor : nullptr; }
}
namespace kcdmp::npcdrive {
bool is_bound_eid(uint32_t eid) { return eid == kEid; }
PartnerPose partner_pose(uint32_t eid, uint8_t peer, void* entity) {
    return eid == kEid && peer == kPeer && entity == &body ? sim.pose : PartnerPose{};
}
}
namespace kcdmp::loadsequence {
uint64_t stamp() { return sim.scope.load; }
bool allows(uint64_t received, Access access) { return allowed(received, sim.scope.load, access); }
void note_stale(uint64_t) {}
}
namespace kcdmp::p4 {
uint64_t life_edge_tick() { return sim.scope.life; }
}
namespace kcdmp::pipe {
uint64_t observation_connection() { return sim.scope.connection; }
}
namespace kcdmp::rptimeline {
Frame frame(uint32_t, uint8_t) { return {sim.target, sim.applied, sim.scope.lifecycle, sim.scope.connection}; }
void note_action_pose(uint64_t, uint64_t) {}
}
namespace kcdmp::rpmeasure {
void note_action(uint32_t, uint64_t) {}
}
namespace kcdmp::wo131 {
bool rp_knockout_available(uint32_t, bool) { return false; }
bool rp_knockout_wake_pending(uint32_t) { return false; }
bool rp_knockout(uint32_t, bool, uint64_t) { return false; }
void rp_knockout_tick() {}
}
namespace kcdmp::wo143 {
bool rp_ranged_available(uint32_t, const uint8_t[16], timelineactions::HandSlot) { return false; }
bool rp_ranged(uint32_t, uint8_t, uint8_t, const uint8_t[16], timelineactions::HandSlot, uint64_t) { return false; }
uint8_t rp_observe_hands(uint32_t, std::array<uint8_t, 16>&, std::array<uint8_t, 16>&) { return 0; }
}
namespace kcdmp::edgeconsole {
Result present_ranged_hand(const timelineactions::RangedHandRequest&, const uint8_t[16]) { return {}; }
}
namespace kcdmp::wo187 {
void note_move(uint32_t) {}
}
namespace kcdmp::wo208 {
bool probe_owned(uint32_t) { return false; }
}
namespace kcdmp::wo216 {
bool lifecycle_gameplay() { return true; }
uint64_t lifecycle_observation_stamp() { return sim.scope.lifecycle; }
}
namespace kcdmp::wo221 {
bool on(uint32_t) { return sim.mode; }
}
namespace kcdmp::rttr {
SwingResult ghost_swing(uint32_t, const char*, uint64_t, bool*) {
    ++sim.legacyCalls;
    return SwingResult::QueueFaulted;
}
SwingResult prepare_ghost_row(uint32_t, const char*, CombatRow*) { return SwingResult::Ok; }
bool combat_row_current(uint32_t, const CombatRow&) { return true; }
SwingResult queue_ghost_row(uint32_t, const CombatRow&, uint64_t token, bool* watching) {
    ++sim.typedCalls;
    sim.token = token;
    *watching = sim.providerResult == SwingResult::Ok;
    return sim.providerResult;
}
const char* swing_result_name(SwingResult) { return "queue-faulted"; }
}

int rp_timeline_outcome_integration(int* passed) {
    using namespace kcdmp::timelineactions;
    int ok_count = 0, failed = 0;
    const auto check = [&](bool ok, const char* name) {
        if (ok) ++ok_count;
        else { ++failed; std::printf("H15 FAIL %s\n", name); }
    };

    check(begin(), "admit ordinary action");
    check(kcdmp::rpcombat::snapshot().actions_in == 1 && kcdmp::rpcombat::snapshot().actions_played == 0 &&
          sim.typedCalls == 0 && terminal_logs() == 0, "acceptance proves queue entry only");
    sim.now += kPoseWaitMs - 1;
    tick();
    check(sim.typedCalls == 0 && kcdmp::rpcombat::snapshot().actions_pending == 1, "wait holds accepted action");
    ++sim.now;
    tick();
    tick();
    check(sim.typedCalls == 0 && logs_with("expired", "missing-current-clock", "not-started") == 1 && terminal_logs() == 1,
          "missing clock has one pre-dispatch terminal outcome");
    check(submit() == Accepted && sim.typedCalls == 0, "lost accept ACK cannot replay an expired action");

    check(begin(), "admit action for a live pose without a target");
    sim.now += kPoseWaitMs - 1;
    pose_pulse();
    tick();
    check(sim.typedCalls == 0, "live pose keeps the normal target wait");
    ++sim.now;
    pose_pulse(1);
    tick();
    check(sim.typedCalls == 1 && logs_with("queued", "guarded-live-pose", "submitted", "0") == 1 && terminal_logs() == 0,
          "missing target plus current pose submits exactly one typed request");
    observed(sim.token, true);
    observed(sim.token, true);
    check(submit() == Accepted && sim.typedCalls == 1 && sim.legacyCalls == 0 &&
          logs_with("installed", "guarded-live-pose", "installed") == 1 && terminal_logs() == 1,
          "lost ACK and duplicate confirmation cannot replay the no-target request");

    check(begin(), "admit action for a current untimed pose");
    sim.now += kPoseWaitMs;
    pose_pulse(0, 0);
    tick();
    check(sim.typedCalls == 1 && logs_with("queued", "guarded-live-pose", "submitted", "0") == 1,
          "unstamped movement can prove a live pose without inventing a target clock");

    check(begin(), "admit action with only an old pose");
    sim.now += kPoseWaitMs;
    pose_pulse(kPoseWaitMs + 1);
    tick();
    check(sim.typedCalls == 0 && logs_with("expired", "missing-current-clock", "not-started") == 1,
          "old pose does not authorize no-target playback");

    check(begin(), "admit action with a known behind target");
    sim.now += kPoseWaitMs;
    sim.target = kHostMs - 1;
    pose_pulse();
    tick();
    check(sim.typedCalls == 0 && logs_with("expired", "missing-current-clock", "not-started") == 1,
          "live pose does not override a known target that has not reached the action");

    check(begin(), "admit action with a replay-stale stamp and no target");
    sim.now += kPoseWaitMs;
    pose_pulse(0, kHostMs + kcdmp::rpcombat::kReplayMs + 1);
    tick();
    check(sim.typedCalls == 0 && logs_with("expired", "stale-replay", "not-started") == 1,
          "a recent stamped pose rejects stale replay even without a target");

    check(begin(false), "register row for repeated live poses without a target");
    bool admitted = true;
    constexpr uint16_t actionCount = 300;
    for (uint16_t i = 0; i < actionCount; ++i) {
        const uint64_t hostMs = kHostMs + static_cast<uint64_t>(i) * kPoseWaitMs;
        pose_pulse(0, hostMs);
        admitted &= submit(sim.scope, sim.now, hostMs, kSequence + i) == Accepted;
        sim.now += kPoseWaitMs;
        pose_pulse(0, hostMs);
        tick();
        observed(sim.token, true);
    }
    check(admitted && sim.typedCalls == actionCount && sim.legacyCalls == 0 &&
          kcdmp::rpcombat::snapshot().actions_played == actionCount,
          "stamped ingress advances bounded receipts with no target over 300 actions");
    pose_pulse(0, 0);
    check(submit(sim.scope, sim.now) == Blocked && sim.typedCalls == actionCount,
          "target and pose stamp loss cannot reopen a retired action ID");

    check(begin(false), "register row for prolonged loss of both presentation clocks");
    constexpr uint16_t unclockedCapacity = 256;
    bool unclockedAdmitted = true, unclockedSettled = true;
    for (uint16_t i = 0; i < unclockedCapacity; ++i) {
        const uint64_t hostMs = kHostMs + static_cast<uint64_t>(i) * kPoseWaitMs;
        pose_pulse(0, 0);
        unclockedAdmitted &= submit(sim.scope, sim.now, hostMs, kSequence + i) == Accepted;
        sim.now += kPoseWaitMs;
        pose_pulse(0, 0);
        tick();
        observed(sim.token, true);
        const auto counts = kcdmp::rpcombat::snapshot();
        unclockedSettled &= counts.actions_pending == 0 && counts.actions_played == static_cast<unsigned>(i) + 1;
    }
    check(unclockedAdmitted && unclockedSettled && sim.typedCalls == unclockedCapacity && sim.legacyCalls == 0,
          "256 unstamped live poses each complete one action without retaining pending work");

    const uint16_t nextSequence = kSequence + unclockedCapacity;
    const uint64_t nextHostMs = kHostMs + unclockedCapacity * kPoseWaitMs;
    pose_pulse(0, 0);
    const auto saturated = submit(sim.scope, sim.now, nextHostMs, nextSequence);
    const auto knownDuplicate = submit(sim.scope, sim.now);
    tick();
    check(saturated == Blocked && knownDuplicate == Accepted && sim.typedCalls == unclockedCapacity &&
          kcdmp::rpcombat::snapshot().actions_in == unclockedCapacity && kcdmp::rpcombat::snapshot().actions_pending == 0,
          "receipt capacity rejects action 257 and duplicate lookup creates no new work");

    pose_pulse(0, nextHostMs);
    --sim.pose.life;
    check(submit(sim.scope, sim.now, nextHostMs, nextSequence) == Blocked && sim.typedCalls == unclockedCapacity,
          "a stamp from another life cannot reopen receipt capacity");

    // Use a new sequence for the recovery input. The earlier saturation refusal was never queued.
    pose_pulse(0, nextHostMs);
    const auto recovered = submit(sim.scope, sim.now, nextHostMs, nextSequence + 1);
    check(recovered == Accepted && submit(sim.scope, sim.now) == Blocked,
          "a live stamp in the current scope reopens admission and leaves retired IDs stale");
    sim.now += kPoseWaitMs;
    pose_pulse(0, nextHostMs);
    tick();
    observed(sim.token, true);
    sim.target = sim.applied = nextHostMs + kPoseWaitMs;
    const auto timedAfterRecovery = submit(sim.scope, sim.now, sim.target, nextSequence + 2);
    tick();
    observed(sim.token, true);
    const auto recoveredCounts = kcdmp::rpcombat::snapshot();
    check(timedAfterRecovery == Accepted && sim.typedCalls == unclockedCapacity + 2 && sim.legacyCalls == 0 &&
          recoveredCounts.actions_in == unclockedCapacity + 2 && recoveredCounts.actions_played == unclockedCapacity + 2 &&
          recoveredCounts.actions_pending == 0 && submit(sim.scope, sim.now) == Blocked,
          "guarded and timed work both resume once without replay or backlog");
    std::printf("H15 unstamped_capacity=%u blocked_257=%u cached_duplicate=%u recovery=%u timed_recovery=%u "
                "typed_calls=%u legacy_calls=%u admitted=%llu installed=%llu pending=%llu\n",
                static_cast<unsigned>(unclockedCapacity), static_cast<unsigned>(saturated), static_cast<unsigned>(knownDuplicate),
                static_cast<unsigned>(recovered), static_cast<unsigned>(timedAfterRecovery), sim.typedCalls, sim.legacyCalls,
                static_cast<unsigned long long>(recoveredCounts.actions_in),
                static_cast<unsigned long long>(recoveredCounts.actions_played),
                static_cast<unsigned long long>(recoveredCounts.actions_pending));

    check(begin(), "admit action with an old-life pose");
    sim.now += kPoseWaitMs;
    pose_pulse();
    --sim.pose.life;
    tick();
    check(sim.typedCalls == 0 && logs_with("expired", "missing-current-clock", "not-started") == 1,
          "pose from an old local life cannot authorize playback in the current scope");

    check(begin(), "admit action before life change without a target");
    sim.now += kPoseWaitMs;
    ++sim.scope.life;
    pose_pulse();
    tick();
    check(sim.typedCalls == 0 && logs_with("expired", "life-edge-changed", "not-started") == 1 && terminal_logs() == 1,
          "new life pose does not release the old scope's action");

    check(begin(), "admit action before protection is lost without a target");
    sim.now += kPoseWaitMs;
    pose_pulse();
    sim.protectedVisual = false;
    tick();
    check(sim.typedCalls == 0 && sim.legacyCalls == 0 && logs_with("refused", "runtime-gate", "not-started") == 1,
          "no-target playback rechecks visual damage protection before provider entry");

    check(begin(), "admit action before a long scheduler stall without a target");
    sim.now += kcdmp::rpcombat::kReplayMs + 1;
    pose_pulse();
    tick();
    check(sim.typedCalls == 0 && logs_with("expired", "stale-reader-age", "not-started") == 1,
          "a current pose after a stall does not revive an old pending action");

    check(begin(), "admit action for a provider fault without a target");
    sim.now += kPoseWaitMs;
    pose_pulse();
    sim.providerResult = kcdmp::rttr::SwingResult::QueueFaulted;
    tick();
    sim.target = sim.applied = kHostMs;
    check(submit() == Accepted, "ambiguous no-target dispatch keeps its receipt when target returns");
    tick();
    observed(sim.token, true);
    check(sim.typedCalls == 1 && sim.legacyCalls == 0 &&
          logs_with("unconfirmed", "queue-faulted", "uncertain") == 1 && terminal_logs() == 1,
          "uncertain no-target provider result permits no second playback");

    check(begin(true, kcdmp::rpcombat::kBlock), "admit mapped ordinary block for H16 regression");
    sim.now += kPoseWaitMs;
    pose_pulse();
    tick();
    observed(sim.token, true);
    check(sim.typedCalls == 1 && sim.legacyCalls == 0 &&
          logs_with("queued", "guarded-live-pose", "submitted", "0") == 1 &&
          logs_with("installed", "guarded-live-pose", "installed") == 1 && terminal_logs() == 1,
          "ordinary block uses the same protected no-target provider exactly once");

    check(begin(), "admit action for watch");
    sim.target = sim.applied = kHostMs;
    check(submit() == Accepted, "duplicate before dispatch returns its receipt");
    tick();
    check(sim.typedCalls == 1 && kcdmp::rpcombat::snapshot().actions_played == 0 && terminal_logs() == 0,
          "provider entry without confirmation is not installation");
    sim.now += 3501;
    tick();
    observed(sim.token, true);
    check(sim.typedCalls == 1 && sim.legacyCalls == 0 && logs_with("unconfirmed", "watch-timeout", "uncertain") == 1 &&
          terminal_logs() == 1,
          "accepted action times out after dispatch and late observation cannot reinstall it");
    check(submit() == Accepted && sim.typedCalls == 1, "duplicate after provider timeout remains one call");

    check(begin(), "admit action for one guarded current-target attempt");
    sim.target = kHostMs;
    sim.applied = kHostMs - 20;
    sim.now += kPoseWaitMs;
    tick();
    observed(sim.token, true);
    observed(sim.token, true);
    check(sim.typedCalls == 1 && sim.legacyCalls == 0 &&
          logs_with("queued", "guarded-current-target", "submitted", "0") == 1 &&
          logs_with("installed", "-", "installed") == 1 && terminal_logs() == 1 &&
          kcdmp::rpcombat::snapshot().actions_played == 1, "same action gets one guarded attempt and one install");
    check(submit() == Accepted && sim.typedCalls == 1, "lost guarded ACK cannot submit another engine request");

    check(begin(), "admit action before visual shield changes");
    sim.target = sim.applied = kHostMs;
    sim.protectedVisual = false;
    tick();
    check(sim.typedCalls == 0 && sim.legacyCalls == 0 && logs_with("refused", "runtime-gate", "not-started") == 1,
          "shield is checked again and refusal cannot call the provider");

    check(begin(), "admit before provider fault");
    sim.target = sim.applied = kHostMs;
    sim.providerResult = kcdmp::rttr::SwingResult::QueueFaulted;
    tick();
    check(sim.typedCalls == 1 && logs_with("unconfirmed", "queue-faulted", "uncertain") == 1,
          "queue fault may have installed and must not authorize replay");

    check(begin(), "admit before replay watermark advances");
    sim.target = sim.applied = kHostMs + kcdmp::rpcombat::kReplayMs + 1;
    tick();
    check(sim.typedCalls == 0 && logs_with("expired", "stale-replay", "not-started") == 1,
          "replay expiry has its own reason even when clock is current");

    check(begin(false), "register a row before a stale event arrives");
    sim.target = sim.applied = kHostMs + kcdmp::rpcombat::kReplayMs + 1;
    check(submit() == Blocked && submit() == Blocked && sim.typedCalls == 0 &&
          logs_with("refused", "stale-replay", "not-started", "0") == 1 &&
          kcdmp::rpcombat::snapshot().actions_pending == 0, "old admission is throttled without creating a terminal action");

    check(begin(), "admit before world replacement");
    const auto retiredWorld = sim.scope;
    sim.scope.load = kcdmp::loadsequence::advance(sim.scope.load, kcdmp::loadsequence::Gate::Play, true);
    check(submit(retiredWorld) == Invalid && sim.typedCalls == 0 && logs_with("expired", "stale-world", "not-started") == 1,
          "old world cannot dispatch and pending action has one terminal outcome");

    check(begin(), "admit before pipe replacement");
    sim.target = sim.applied = kHostMs;
    tick();
    const auto retiredPipe = sim.scope;
    ++sim.scope.connection;
    check(submit(retiredPipe) == Invalid, "old pipe is invalid");
    observed(sim.token, true);
    check(sim.typedCalls == 1 && logs_with("unconfirmed", "stale-pipe", "uncertain") == 1,
          "pipe replacement after provider entry cannot prove no dispatch");

    check(begin(), "admit an action before disconnecting without more pipe traffic");
    sim.target = sim.applied = kHostMs;
    tick();
    sim.scope.connection = 0;
    observed(sim.token, true);
    tick();
    observed(sim.token, true);
    check(sim.typedCalls == 1 && sim.legacyCalls == 0 && terminal_logs() == 1 &&
          logs_with("unconfirmed", "stale-pipe", "uncertain") == 1,
          "tick settles a changed scope even when only an old provider callback arrives");

    check(begin(), "admit before combat mode switch");
    sim.mode = false;
    tick();
    check(sim.typedCalls == 0 && logs_with("expired", "combat-mode-changed", "not-started") == 1,
          "mode reset before dispatch yields exactly one terminal outcome");

    if (passed) *passed = ok_count;
    return failed;
}

#undef GetTickCount64
#undef kcdmp
