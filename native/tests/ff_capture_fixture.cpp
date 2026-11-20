// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only.
// Build against the selected checkout's actual melee/missile hooks and update.
// Only engine state, boot clock and the final native-pipe writer are doubles.
#include <hits.cpp>
#include <string_view>

namespace {
constexpr uint64_t startTick = 10000;
constexpr uint64_t localWuid = 1777;
constexpr uint64_t initialLife = 73;
constexpr uint32_t targetEid = 0x2345;
constexpr uint32_t localEid = 0x7777;
constexpr uint64_t connection = 17;
uint64_t tickMs = startTick - 10;
uint64_t lifeProof = initialLife;
uint64_t lifeEdge = startTick - 100;

struct Vitals { float hp = 200, stamina = 200; };
Vitals target, local;
struct Contact { uint64_t at; float hp, stamina; bool missile = false; };
constexpr uint64_t effectDelayMs = 20;
size_t causes = 0;
enum class RestoreFault { None, Refused, AcknowledgedWithoutEffect, Partial, AppliedWithoutReceipt };
RestoreFault restoreFault = RestoreFault::None;
uint64_t restoreFaultAt = 0;

void* engine_cause(void*, void* out, const uint8_t*) {
    ++causes;
    return out;
}

template<class T>
void set_field(uint8_t* data, size_t offset, T value) {
    std::memcpy(data + offset, &value, sizeof value);
}

bool contact(const Contact& hit) {
    alignas(8) uint8_t data[0x90]{};
    set_field(data, 0, localWuid);
    set_field(data, 8, localEid);
    set_field(data, 0x18, targetEid);
    data[0x4C] = 3;
    auto hook = hit.missile ? kcdmp::hits::hk_missile : kcdmp::hits::hk_melee;
    return hook(nullptr, &target, data) == &target;
}

void emit_notice(uint32_t victim, float st, float hp, uint8_t flags, uint8_t material,
                 const kcdmp::wo155rules::Observation& observation, uint64_t pipeConnection) {
    if (pipeConnection != connection) std::abort();
    const auto body = kcdmp::wo155rules::encode_notice(victim, st, hp, flags, material, observation);
    std::printf("%02X%02X00", kcdmp::pipe::kPvpHitOut, static_cast<unsigned>(body.size()));
    for (uint8_t value : body) std::printf("%02X", value);
    std::puts("");
}
}

uint64_t GetTickCount64() { return tickMs; }

namespace kcdmp {
namespace engine {
void* game_iface() { return nullptr; }
void* entity_by_id(uint32_t eid) { return eid == localEid ? &local : nullptr; }
uint32_t entity_id(void* entity) { return entity == &local ? localEid : 0; }
const char* entity_name(void*) { return nullptr; }
}
namespace rttr {
void* read_player_soul() { return &local; }
bool soul_state(void* soul, const char* state, float* value) {
    const auto& vitals = *static_cast<Vitals*>(soul);
    *value = std::strcmp(state, "health") == 0 ? vitals.hp : vitals.stamina;
    return true;
}
bool soul_set_state(void* soul, const char* state, float value) {
    auto& vitals = *static_cast<Vitals*>(soul);
    float& current = std::strcmp(state, "health") == 0 ? vitals.hp : vitals.stamina;
    const auto fault = soul == &target && tickMs == restoreFaultAt ? restoreFault : RestoreFault::None;
    if (fault != RestoreFault::None)
        std::fprintf(stderr, "FF-RESTORE tick=%llu state=%s fault=%d before=%.2f target=%.2f\n",
                     static_cast<unsigned long long>(tickMs), state, static_cast<int>(fault), current, value);
    if (fault == RestoreFault::Refused) return false;
    if (fault == RestoreFault::AcknowledgedWithoutEffect) return true;
    if (fault == RestoreFault::Partial) {
        current += (value - current) / 2;
        return false;
    }
    current = value;
    if (fault == RestoreFault::AppliedWithoutReceipt) return false;
    return true;
}
bool combat_bool(void*, const char*, bool* out) { *out = true; return true; }
}
namespace fault { void on_fault(Site&) noexcept { std::abort(); } }
namespace motion {
void* actor_of(uint32_t) { return nullptr; }
void* player_combat_actor() { return nullptr; }
}
namespace actions { uint64_t entity_wuid(void*) { return localWuid; } }
namespace buffs { void* as_c_soul(void*) { return nullptr; } }
namespace pipe {
void publish_pvp_life() {}
uint64_t observation_connection() { return connection; }
#ifndef KCDMP_FF_LEGACY_SOURCE
void queue_pvp_trace(const hits::PvpTrace& trace) { hits::log_pvp_trace(trace); }
#endif
}
namespace p4 {
uint64_t generation() { return 1; }
uint64_t life_edge_tick() { return lifeEdge; }
uint64_t pvp_life_proof() { return lifeProof; }
bool gameplay() { return true; }
}
namespace loadsequence { uint64_t stamp() { return 19; } }
namespace p6 {
void tick() {}
bool contact(bool, uint32_t, uint32_t, void*, bool) { return false; }
}
namespace wo208 { bool on(uint32_t) { return false; } }
namespace wo210hit { bool visual_gate_ready() { return false; } }
namespace wo166 { void note_local_hit_dropped(uint32_t, float, float) { std::abort(); } }
namespace wo136 {
bool host_target() { return false; }
void note_threat(uint32_t, uint32_t, int, const char*, float) { std::abort(); }
}
namespace wo139 { void host_struck(uint32_t) { std::abort(); } }
}

int main(int argc, char** argv) {
    if (argc != 2) return 2;
    const std::string_view scenario = argv[1];
    std::vector<Contact> contacts;
    if (scenario == "melee" || scenario == "missile") {
        contacts = {{0, 1, 2}, {500, 2, 3}, {1000, 3, 4}, {1500, 4, 2}, {2000, 5, 3}};
        for (auto& hit : contacts) hit.missile = scenario == "missile";
    } else if (scenario == "single") {
        contacts = {{0, 1, 2}};
    } else if (scenario == "boundary") {
        contacts = {{0, 1, 2}, {590, 2, 3, true}};
    } else if (scenario == "continuous") {
        for (unsigned i = 0; i <= 50; ++i)
            contacts.push_back({100 * i, float(1 + i % 5), float(1 + (i + 1) % 5)});
    } else if (scenario == "recovery" || scenario == "native-life") {
        contacts = {{0, 7, 5}, {3000, 1, 2}};
    } else if (scenario == "restore-refused" || scenario == "restore-no-effect" || scenario == "restore-partial") {
        contacts = {{0, 1, 2}};
        restoreFaultAt = startTick + effectDelayMs;
        restoreFault = scenario == "restore-refused" ? RestoreFault::Refused :
                       scenario == "restore-no-effect" ? RestoreFault::AcknowledgedWithoutEffect : RestoreFault::Partial;
    } else if (scenario == "restore-receipt-lost") {
        contacts = {{0, 1, 2}, {10, 2, 3}};
        restoreFaultAt = startTick + effectDelayMs;
        restoreFault = RestoreFault::AppliedWithoutReceipt;
    } else if (scenario == "restore-boundary") {
        contacts = {{0, 1, 2}, {580, 2, 3}, {590, 3, 4}};
        restoreFaultAt = startTick + 600;
        restoreFault = RestoreFault::Refused;
    } else {
        return 2;
    }

    kcdmp::hits::g_origMelee = &engine_cause;
    kcdmp::hits::g_origMissile = &engine_cause;
    kcdmp::hits::note_avatar(targetEid, &target, true);
    kcdmp::hits::set_pvp_callback(&emit_notice);
    kcdmp::hits::tick();

    size_t nextContact = 0, nextEffect = 0;
    for (uint64_t at = 0; at <= contacts.back().at + 800; at += 10) {
        tickMs = startTick + at;
        if (scenario == "native-life" && at == 100) {
            lifeEdge = tickMs;
            ++lifeProof;
        }
        if (nextContact < contacts.size() && contacts[nextContact].at == at) {
            if (!contact(contacts[nextContact++])) return 1;
        }
        if (nextEffect < contacts.size() && contacts[nextEffect].at + effectDelayMs == at) {
            const auto& hit = contacts[nextEffect++];
            target.hp -= hit.hp;
            target.stamina -= hit.stamina;
        }
        kcdmp::hits::tick();
        if (tickMs != restoreFaultAt && (target.hp != 200 || target.stamina != 200)) {
            std::fprintf(stderr, "FF-RESTORE target not restored at tick=%llu hp=%.2f stamina=%.2f\n",
                         static_cast<unsigned long long>(tickMs), target.hp, target.stamina);
            return 1;
        }
    }
    return causes == contacts.size() && nextEffect == contacts.size() ? 0 : 1;
}
