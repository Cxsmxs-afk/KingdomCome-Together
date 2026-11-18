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

void* const* g_vftExp = nullptr;
void* const* g_vftMain = nullptr;
struct StepClass { const char* rtti; const char* name; uint8_t phase; void* const* vft; };
StepClass g_steps[] = {
    {".?AVC_ActorActionShootingCharging@entitymodule@wh@@", "charging", 1, nullptr},
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
uint64_t g_lastEpoch=0,g_streamEpoch=0,g_lastRead=0;
std::atomic<uint32_t> c_reads{0},c_changes{0},c_unreadable{0};
std::atomic<uint64_t> c_missingActor{0},c_unknownHand{0};
std::atomic<bool> g_reset{false};
std::mutex g_lock;
p6rules::Queue g_q;
void stream_reset(uint64_t gen,uint64_t scope,bool identity){
    if(++g_streamEpoch==0)++g_streamEpoch;
    g_q.reset(gen,g_streamEpoch);g_haveLast=false;g_lastEpoch=scope;
    if(identity){g_lastSoul=nullptr;g_lastActor=nullptr;g_holderOk=false;}
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
uint8_t read_phase(void* actor) {
    if (!actor) return 255;
    void* hf = vslot(actor, kActorExpHolder);
    if (!hf) return 255;
    if (!g_holderOk) {
        static const uint8_t movHolder[] = {0x48, 0x8B, 0x83, 0x08, 0x03, 0x00, 0x00};   // mov rax,[rbx+0x308]
        static const uint8_t leaHolder[] = {0x48, 0x8D, 0x81, 0x08, 0x03, 0x00, 0x00};   // lea rax,[rcx+0x308]
        g_holderOk = body_has(hf, movHolder, sizeof movHolder, 48) || body_has(hf, leaHolder, sizeof leaHolder, 48);
        if (!g_holderOk) { g_why = "the actor's expansion-holder slot does not return the holder"; return 255; }
    }
    void* holder = nullptr;
    KCDMP_FAULT_CALL(hsite, "wo201ranged::holder");
    if (!fault::guarded(hsite, [&] { holder = reinterpret_cast<void* (*)(void*)>(hf)(actor); }) || !holder) return 255;
    void* get = vslot(holder, kHolderGetExp);
    void* exp = nullptr;
    KCDMP_FAULT_CALL(gsite, "wo201ranged::expansion");
    if (!get || !fault::guarded(gsite, [&] { exp = reinterpret_cast<void* (*)(void*, uint8_t)>(get)(holder, kShootingId); })) return 255;
    if (!exp) return 0;                        // never shot this session: no expansion yet
    if (!is_a(exp, g_vftExp)) return 255;
    void* main = nullptr;
    if (!rd(exp, kExpMain, &main)) return 255;
    if (!main) return 0;
    if (!is_a(main, g_vftMain)) return 255;
    void* step = nullptr;
    if (!rd(main, kMainStep, &step) || !step) return 0;
    void* vp = nullptr;
    if (!rd(step, 0, &vp)) return 255;
    for (const auto& s : g_steps) if (s.vft && vp == static_cast<const void*>(s.vft)) return s.phase;
    static uint32_t s_unknown = 0;
    if (vp && s_unknown++ < 5)
        logf("WO201-RANGED step class not in the bow/crossbow list (vt=%p) -- a bow step this build names differently [n=%u]", vp, s_unknown);
    return 255; // unknown RTTI is unreadable, never a proved idle/none
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
uint8_t phase_of(void* actor){return g_test.phase(actor);}
uint8_t hand_guids(uint32_t eid,p6rules::Capture* row){return g_test.hands(eid,row->left.data(),row->right.data());}
#else
uint64_t now_ms(){return GetTickCount64();}
uint64_t generation(){return wo216::lifecycle_generation();}
uint64_t scope_epoch(){return p6::capture_epoch();}
bool gameplay(){return wo216::lifecycle_gameplay();}
void* local_actor(uint32_t* eid){return motion::local_actor(eid);}
void* player_soul(){return rttr::read_player_soul();}
uint8_t phase_of(void* actor){return read_phase(actor);}
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
void reset_for_tests(){std::lock_guard<std::mutex> lk(g_lock);g_on=false;g_reset=false;g_q={};g_haveLast=false;g_last=0;g_lastSoul=g_lastActor=nullptr;g_lastEpoch=g_streamEpoch=g_lastRead=0;g_holderOk=false;c_reads=0;c_changes=0;c_unreadable=0;c_missingActor=0;c_unknownHand=0;g_why="resolved";}
#endif

void tick() {
    std::lock_guard<std::mutex> lk(g_lock);
    const uint64_t gen=generation();
    const uint64_t epoch=scope_epoch();
    if(g_reset.exchange(false)||gen!=g_q.generation||epoch!=g_lastEpoch){
        stream_reset(gen,epoch,true);
    }
    if(!g_on.load()||!gameplay()||std::strcmp(g_why,"resolved")!=0)return;
    const uint64_t t=now_ms();
    uint32_t eid=0;void* actor=local_actor(&eid);void* ps=player_soul();
    if(!actor||!eid){c_missingActor.fetch_add(1);return;}
    if(!ps){c_unreadable.fetch_add(1);return;}
    if(!epoch){c_unreadable.fetch_add(1);return;}
    if(actor!=g_lastActor||ps!=g_lastSoul){g_lastActor=actor;g_lastSoul=ps;g_haveLast=false;g_holderOk=false;}
    if(t<g_lastRead||t-g_lastRead<50)return;
    g_lastRead=t;
    const uint8_t phase=phase_of(actor);c_reads.fetch_add(1);
    if(phase==255){c_unreadable.fetch_add(1);return;}
    p6rules::Capture row;row.phase=phase;row.tick=t;
    const uint8_t known=hand_guids(eid,&row);row.known=known;row.source=known==3?1:0;
    // No later poll enriches these GUIDs. Unknown remains unknown forever.
    // A weapon change inside the same Firing clip is not a second shot.
    if(g_haveLast&&phase==g_last)return;
    if(phase&&(!known||known!=3||(p6rules::class_empty(row.left)&&p6rules::class_empty(row.right))))c_unknownHand.fetch_add(1);
    if(gen!=generation()||epoch!=scope_epoch()||actor!=local_actor(&eid)||ps!=player_soul()){
        stream_reset(generation(),0,true);return;
    }
    const uint8_t was=g_last;g_last=phase;g_haveLast=true;c_changes.fetch_add(1);
    if(!g_q.push(row)){stream_reset(gen,epoch,false);return;} // explicit resync; never silently drop only Fire/Leaving
    if(phase==3||phase!=was)logf("WO201-RANGED local %s -> %s capture=%llu source=%u known=%u epoch=%llu",phase_name(was),phase_name(phase),static_cast<unsigned long long>(t),row.source,row.known,static_cast<unsigned long long>(epoch));
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
    if(g_on.load()!=on){stream_reset(gen,epoch,false);g_on=on;}
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
