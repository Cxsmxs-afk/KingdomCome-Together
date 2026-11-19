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
    bool mode = true, protectedVisual = true;
    rttr::SwingResult providerResult = rttr::SwingResult::Ok;
    unsigned typedCalls = 0, legacyCalls = 0;
    std::vector<std::string> logs;
} sim;

auto row_wire() {
    std::array<uint8_t, rpcombat::kRegistrationHeader + 1> bytes{};
    bytes[0] = kPeer;
    loadsequence::write_le(bytes.data() + 1, kEid, 4);
    bytes[5] = rpcombat::kSwing;
    loadsequence::write_le(bytes.data() + 6, kRow, 4);
    bytes.back() = 'a';
    return bytes;
}

auto action_wire() {
    std::array<uint8_t, rpcombat::kActionHeader + 4> bytes{};
    bytes[0] = kPeer;
    loadsequence::write_le(bytes.data() + 1, kEid, 4);
    loadsequence::write_le(bytes.data() + 5, kHostMs, 8);
    loadsequence::write_le(bytes.data() + 13, kSequence, 2);
    bytes[15] = rpcombat::kSwing;
    bytes[16] = timelineactions::kCommit;
    loadsequence::write_le(bytes.data() + rpcombat::kActionHeader, kRow, 4);
    return bytes;
}

auto submit(Scope received = sim.scope) {
    const auto bytes = action_wire();
    return timelineactions::submit_combat(bytes.data(), bytes.size(), received, kReadMs);
}

bool begin(bool admit = true) {
    timelineactions::reset();
    sim = {};
    timelineactions::configure(true);
    const auto row = row_wire();
    return timelineactions::register_combat(row.data(), row.size(), sim.scope) == timelineactions::Accepted &&
        (!admit || submit() == timelineactions::Accepted);
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
bool rp_ranged_available(uint32_t, const uint8_t[16]) { return false; }
bool rp_ranged(uint32_t, uint8_t, uint8_t, const uint8_t[16], uint64_t) { return false; }
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
