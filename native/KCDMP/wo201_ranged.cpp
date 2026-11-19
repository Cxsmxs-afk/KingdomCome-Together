// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
// WO-201 B11 figure_ranged (both): this player's crossbow and bow, observed. Ranged shooting is not combat-director work (WO-121's attack
// capture never sees it -- the field: 0 attack rows while the joiner shot a crossbow); every actor runs it as its own shooting
// expansion (C_ActorShootingExpansion, EntityModule), whose running main action holds the current step: one of
// C_ActorActionShooting{Charging, InstantCharging, Aiming, Firing, Reloading, Uncharging, Unloading, TransitioningToIdle}.
// Read once a frame for the local player only, no hook: the actor's expansion holder (motion.cpp's slot, checked by what its body
// returns), the expansion with id 4 by the holder's own lookup (it never creates one), its main action, the step -- every pointer
// checked against its RTTI vtable that frame. A change of step is queued for the agent (op 86), which shows it on this player's figure
// on the partner's screen (an animation of the game's own fragments: never a shot of the figure's own -- no projectile).  WO201-RANGED.
#include "wo201.h"
#include "ranged_aim.h"
#include "p6_capture.h"
#include "p6_rules.h"
#include "wo163.h"
#ifndef KCDMP_P6_CAPTURE_TEST
#include "npcstate.h"
#include "wo216.h"
#include "p6_missile.h"
#include "p6_rules.h"
#include "wo163.h"
#include "anchors.h"
#include "engine.h"
#include "fault_guard.h"
#include "log.h"
#include "motion.h"
#include "rttr_abi.h"
#include <windows.h>
#else
namespace kcdmp::wo201ranged {void test_trace(const char*,...);}
#define logf test_trace
#endif
#include <atomic>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <vector>

namespace kcdmp::wo201ranged {

namespace {

#ifndef KCDMP_P6_CAPTURE_TEST
constexpr size_t kActorExpHolder = 0x980;   // motion.cpp: returns actor+0x308, the expansion holder (checked below by its body)
constexpr size_t kHolderGetExp = 0x70;      // motion.cpp: holder->vtbl[0x70](holder, id) = the expansion of that id, never created
constexpr uint8_t kShootingId = 4;          // the id the expansion factory registers C_ActorShootingExpansion under
constexpr ptrdiff_t kExpMain = 0x68;        // the expansion's running main action (checked by its class)
constexpr ptrdiff_t kMainStep = 0xA0;       // the main action's current step (checked by its class)
constexpr ptrdiff_t kBowCharge = 0xB0, kBowMinCharge = 0xC0, kBowMaxCharge = 0xC4; // WO-119 §3.2, Charging only

void* const* g_vftExp = nullptr;
void* const* g_vftMain = nullptr;
struct StepClass { const char* rtti; const char* name; uint8_t phase; void* const* vft; bool measuresCharge = false; };
StepClass g_steps[] = {
    {".?AVC_ActorActionShootingCharging@entitymodule@wh@@", "charging", 1, nullptr, true},
    {".?AVC_ActorActionShootingInstantCharging@entitymodule@wh@@", "charging", 1, nullptr},
    {".?AVC_ActorActionShootingAiming@entitymodule@wh@@", "aiming", 2, nullptr},
    {".?AVC_ActorActionShootingFiring@entitymodule@wh@@", "firing", 3, nullptr},
    {".?AVC_ActorActionShootingReloading@entitymodule@wh@@", "reloading", 4, nullptr},
    {".?AVC_ActorActionShootingUncharging@entitymodule@wh@@", "uncharging", 5, nullptr},
    {".?AVC_ActorActionShootingUnloading@entitymodule@wh@@", "leaving", 6, nullptr},
    {".?AVC_ActorActionShootingTransitioningToIdle@entitymodule@wh@@", "leaving", 6, nullptr},
};
#endif
const char* g_why="not resolved";
bool g_holderOk = false;
std::atomic<bool> g_on{false};
uint8_t g_last=0;bool g_haveLast=false;
void* g_lastSoul=nullptr;void* g_lastActor=nullptr;
rangedaim::Block g_aim{};
bool g_waitForPhaseAfterReplacement = false;
uint64_t g_lastEpoch=0,g_streamEpoch=0,g_lastRead=0;
std::atomic<uint32_t> c_reads{0},c_changes{0},c_unreadable{0};
std::atomic<uint64_t> c_missingActor{0},c_unknownHand{0};
std::atomic<bool> g_reset{false};
std::mutex g_lock;
p6rules::Queue g_q;
void stream_reset(uint64_t gen,uint64_t scope,bool identity){
    if(++g_streamEpoch==0)++g_streamEpoch;
    g_q.reset(gen,g_streamEpoch);g_haveLast=false;g_lastEpoch=scope;
    if(identity){g_lastSoul=nullptr;g_lastActor=nullptr;g_holderOk=false;g_aim={};g_waitForPhaseAfterReplacement=false;}
}

#ifndef KCDMP_P6_CAPTURE_TEST
template <class T> bool rd(const void* base, ptrdiff_t off, T* out) {
    if (!base) return false;
    KCDMP_FAULT_READ(site, "wo201ranged::read");
    if (!fault::enabled(site)) return false;
    const uintptr_t a = reinterpret_cast<uintptr_t>(base) + static_cast<uintptr_t>(off);
    if (!fault::plausible_address(a)) return false;
    return fault::guarded(site, [&] { std::memcpy(out, reinterpret_cast<const void*>(a), sizeof(T)); });
}
bool is_a(const void* obj, void* const* vft) {
    void* vp = nullptr;
    return obj && vft && rd(obj, 0, &vp) && vp == static_cast<const void*>(vft);
}
void* vslot(const void* obj, size_t off) {
    void* vt = nullptr; void* fn = nullptr;
    return obj && rd(obj, 0, &vt) && vt && rd(vt, static_cast<ptrdiff_t>(off), &fn) ? fn : nullptr;
}
bool body_has(const void* fn, const uint8_t* pat, size_t n, size_t within) {
    uint8_t b[64]{};
    if (within > sizeof b) within = sizeof b;
    KCDMP_FAULT_READ(site, "wo201ranged::body");
    if (!fn || !fault::guarded(site, [&] { std::memcpy(b, fn, within); })) return false;
    for (size_t i = 0; i + n <= within; ++i) if (std::memcmp(b + i, pat, n) == 0) return true;
    return false;
}

// The player's current shooting phase: 0 none, 1 charging, 2 aiming, 3 firing, 4 reloading, 5 uncharging, 6 leaving; 255 unreadable.
rangedaim::Sample read_step(void* actor) {
    if (!actor) return {};
    void* hf = vslot(actor, kActorExpHolder);
    if (!hf) return {};
    if (!g_holderOk) {
        static const uint8_t movHolder[] = {0x48, 0x8B, 0x83, 0x08, 0x03, 0x00, 0x00};   // mov rax,[rbx+0x308]
        static const uint8_t leaHolder[] = {0x48, 0x8D, 0x81, 0x08, 0x03, 0x00, 0x00};   // lea rax,[rcx+0x308]
        g_holderOk = body_has(hf, movHolder, sizeof movHolder, 48) || body_has(hf, leaHolder, sizeof leaHolder, 48);
        if (!g_holderOk) { g_why = "the actor's expansion-holder slot does not return the holder"; return {}; }
    }
    void* holder = nullptr;
    KCDMP_FAULT_CALL(hsite, "wo201ranged::holder");
    if (!fault::guarded(hsite, [&] { holder = reinterpret_cast<void* (*)(void*)>(hf)(actor); }) || !holder) return {};
    void* get = vslot(holder, kHolderGetExp);
    void* exp = nullptr;
    KCDMP_FAULT_CALL(gsite, "wo201ranged::expansion");
    if (!get || !fault::guarded(gsite, [&] { exp = reinterpret_cast<void* (*)(void*, uint8_t)>(get)(holder, kShootingId); })) return {};
    if (!exp) return {rangedaim::None, {}};    // no expansion yet
    if (!is_a(exp, g_vftExp)) return {};
    void* main = nullptr;
    if (!rd(exp, kExpMain, &main)) return {};
    if (!main) return {rangedaim::None, {}};
    if (!is_a(main, g_vftMain)) return {};
    void* step = nullptr;
    if (!rd(main, kMainStep, &step)) return {};
    if (!step) return {rangedaim::None, {}};
    void* vp = nullptr;
    if (!rd(step, 0, &vp)) return {};
    for (const auto& s : g_steps) {
        if (!s.vft || vp != static_cast<const void*>(s.vft)) continue;
        float charge = 0, min = 0, max = 0;
        const auto measured = s.measuresCharge &&
            rd(step, kBowCharge, &charge) && rd(step, kBowMinCharge, &min) && rd(step, kBowMaxCharge, &max)
            ? rangedaim::quantize_charge(charge, min, max) : std::nullopt;
        return {s.phase, measured};
    }
    static uint32_t s_unknown = 0;
    if (vp && s_unknown++ < 5)
        logf("WO201-RANGED step class not in the bow/crossbow list (vt=%p) -- a bow step this build names differently [n=%u]", vp, s_unknown);
    return {}; // unknown RTTI is unreadable
}

#endif

const char* phase_name(uint8_t p) {
    switch (p) {
        case 0: return "none"; case 1: return "charging"; case 2: return "aiming"; case 3: return "firing";
        case 4: return "reloading"; case 5: return "uncharging"; case 6: return "leaving";
    }
    return "?";
}

#ifdef KCDMP_P6_CAPTURE_TEST
TestBackend g_test{};
uint64_t now_ms(){return g_test.now();}
uint64_t generation(){return g_test.generation();}
uint64_t scope_epoch(){return g_test.epoch();}
bool gameplay(){return g_test.gameplay();}
void* local_actor(uint32_t* eid){return g_test.actor(eid);}
void* player_soul(){return g_test.soul();}
rangedaim::Sample step_of(void* actor) {
    const auto phase = g_test.phase(actor);
    float charge = 0, min = 0, max = 0;
    const auto measured = phase == rangedaim::Charging && g_test.charge && g_test.charge(actor, &charge, &min, &max)
        ? rangedaim::quantize_charge(charge, min, max) : std::nullopt;
    return {phase, measured};
}
uint8_t hand_guids(uint32_t eid,p6rules::Capture* row){return g_test.hands(eid,row->left.data(),row->right.data());}
#else
uint64_t now_ms(){return GetTickCount64();}
uint64_t generation(){return wo216::lifecycle_generation();}
uint64_t scope_epoch(){return p6::capture_epoch();}
bool gameplay(){return wo216::lifecycle_gameplay();}
void* local_actor(uint32_t* eid){return motion::local_actor(eid);}
void* player_soul(){return rttr::read_player_soul();}
rangedaim::Sample step_of(void* actor){return read_step(actor);}
uint8_t hand_guids(uint32_t eid,p6rules::Capture* row){wo143rules::Hands h{};const uint8_t known=wo141::x::read_body_hands_strict(eid,&h);std::memcpy(row->left.data(),h.left.b,16);std::memcpy(row->right.data(),h.right.b,16);return known;}
#endif
} // namespace

#ifndef KCDMP_P6_CAPTURE_TEST
void install() {
    HMODULE em = GetModuleHandleA("EntityModule.dll");
    if (!em) { g_why = "EntityModule.dll not loaded"; return; }
    g_vftExp = anchor::find_vftable(em, ".?AVC_ActorShootingExpansion@entitymodule@wh@@", 0);
    g_vftMain = anchor::find_vftable(em, ".?AVC_ActorActionShootingMain@entitymodule@wh@@", 0);
    int found = 0;
    for (auto& s : g_steps) { s.vft = anchor::find_vftable(em, s.rtti, 0); if (s.vft) ++found; }
    if (!g_vftExp || !g_vftMain || found < 6) { g_why = "the shooting classes not found by RTTI"; return; }
    g_why = "resolved";
    logf("WO201-RANGED resolver: the shooting expansion, its main action and %d of %zu step classes by RTTI -- the local player's crossbow and "
         "bow are read once a frame (no hook)", found, sizeof g_steps / sizeof g_steps[0]);
}

#else
void install(){g_why="resolved";}
void set_test_backend(const TestBackend& b){g_test=b;}
void reset_for_tests(){std::lock_guard<std::mutex> lk(g_lock);g_on=false;g_reset=false;g_q={};g_haveLast=false;g_last=0;g_lastSoul=g_lastActor=nullptr;g_aim={};g_waitForPhaseAfterReplacement=false;g_lastEpoch=g_streamEpoch=g_lastRead=0;g_holderOk=false;c_reads=0;c_changes=0;c_unreadable=0;c_missingActor=0;c_unknownHand=0;g_why="resolved";}
#endif

rangedaim::Block sampled_aim(const void* actor) {
    if (!actor || actor != g_lastActor || g_q.generation != generation() ||
        !g_on.load(std::memory_order_relaxed) || g_reset.load(std::memory_order_relaxed)) return {};
    return g_aim;
}

void tick() {
    std::lock_guard<std::mutex> lk(g_lock);
    const uint64_t gen=generation();
    const uint64_t epoch=scope_epoch();
    if(g_reset.exchange(false)||gen!=g_q.generation||epoch!=g_lastEpoch){
        stream_reset(gen,epoch,true);
    }
    if(!g_on.load()||!gameplay()||std::strcmp(g_why,"resolved")!=0)return;
    const uint64_t t=now_ms();
    if(t<g_lastRead||t-g_lastRead<50)return;
    g_lastRead=t;
    uint32_t eid=0;void* actor=local_actor(&eid);void* ps=player_soul();
    if(!actor||!eid){c_missingActor.fetch_add(1);return;}
    if(!ps){c_unreadable.fetch_add(1);return;}
    if(!epoch){c_unreadable.fetch_add(1);return;}
    if (actor != g_lastActor || ps != g_lastSoul) {
        if (g_lastActor || g_lastSoul) {
            stream_reset(gen, epoch, true);
            g_waitForPhaseAfterReplacement = true;
        }
        g_lastActor = actor;
        g_lastSoul = ps;
    }
    const auto sample = step_of(actor);
    const uint8_t phase = sample.phase;
    c_reads.fetch_add(1);
    if (phase == rangedaim::Unreadable) { c_unreadable.fetch_add(1); return; }
    if (g_waitForPhaseAfterReplacement) {
        if (phase == rangedaim::Firing) return; // an in-flight phase of the retired body cannot start a shot
        g_waitForPhaseAfterReplacement = false;
    }

    auto draw = g_aim;
    const auto edge = rangedaim::apply(sample, draw);
    const bool newPhase = !g_haveLast || phase != g_last;
    if (!newPhase && edge == rangedaim::Edge::Same) return;

    p6rules::Capture row;
    if (newPhase) {
        // The reliable action carries the two hands at the phase edge; later polls do not relabel it.
        row.phase = phase;
        row.tick = t;
        row.known = hand_guids(eid, &row);
        row.source = row.known == 3 ? 1 : 0;
        if (phase && (row.known != 3 || (p6rules::class_empty(row.left) && p6rules::class_empty(row.right))))
            c_unknownHand.fetch_add(1);
    }
    uint32_t verifiedEid = 0;
    if(gen!=generation()||epoch!=scope_epoch()||actor!=local_actor(&verifiedEid)||eid!=verifiedEid||ps!=player_soul()){
        stream_reset(generation(),0,true);return;
    }
    g_aim = draw;
    if (!newPhase) return;
    const uint8_t was=g_last;g_last=phase;g_haveLast=true;c_changes.fetch_add(1);
    if(!g_q.push(row)){stream_reset(gen,epoch,false);return;} // explicit resync; never silently drop only Fire/Leaving
    if (phase == rangedaim::Firing || phase != was)
        logf("WO201-RANGED local %s -> %s capture=%llu source=%u known=%u epoch=%llu held=%d charge_known=%d charge=%u angles=unverified",
             phase_name(was), phase_name(phase), static_cast<unsigned long long>(t), row.source, row.known,
             static_cast<unsigned long long>(epoch), draw.aim ? 1 : 0, draw.charge ? 1 : 0,
             static_cast<unsigned>(draw.charge.value_or(0)));
}

// v2 only: [op86][version2][on1][expected P1gen8] -> header18 + <=10 rows48.
uint8_t handle(const uint8_t* body,size_t len,uint8_t* out,size_t cap,size_t* outLen){
    *outLen=0;
    if(!body||len!=11||body[1]!=2||body[2]>1||cap<p6rules::kHeader)return wo163::kRBadRequest;
    const uint64_t gen=p6rules::u64(body+3);
    if(!gen||gen!=generation())return wo163::kRFailed;
    std::lock_guard<std::mutex> lk(g_lock);
    const uint64_t epoch=scope_epoch();
    if(g_reset.exchange(false)||g_q.generation!=gen||g_lastEpoch!=epoch){stream_reset(gen,epoch,true);}
    const bool on=body[2]!=0;
    if(g_on.load()!=on){stream_reset(gen,epoch,true);g_on=on;}
    if(!g_on){*outLen=g_q.drain(now_ms(),out,cap);return wo163::kROk;}
    if(!epoch||!gameplay()||std::strcmp(g_why,"resolved")!=0)return wo163::kRFailed;
    *outLen=g_q.drain(now_ms(),out,cap);return wo163::kROk;
}
void on_pipe_closed(){g_on=false;g_reset=true;}
void t3_counts(uint64_t out[6]){
    if(!out)return;
    std::lock_guard<std::mutex> lk(g_lock);
    out[0]=c_missingActor.load();out[1]=c_unreadable.load();out[2]=c_unknownHand.load();out[3]=g_q.overflow;out[4]=g_q.expired;out[5]=g_q.dropped;
}
int status_text(char* out,int n){
    std::lock_guard<std::mutex> lk(g_lock);
    return std::snprintf(out,static_cast<size_t>(n),"ranged=%s on=%d reads=%u changes=%u unreadable=%u now=%s queue=%zu overflow=%llu expired=%llu lifecycle_dropped=%llu",g_why,g_on.load()?1:0,c_reads.load(),c_changes.load(),c_unreadable.load(),phase_name(g_last),g_q.rows.size(),static_cast<unsigned long long>(g_q.overflow),static_cast<unsigned long long>(g_q.expired),static_cast<unsigned long long>(g_q.dropped));
}
} // namespace kcdmp::wo201ranged
