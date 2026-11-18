// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// Actual p4.cpp linked against deterministic engine mocks, not a second policy implementation.
#include "p4.h"
#include "cvar_lease_rules.h"
#include "wo216.h"
#include "wo163.h"
#include "wo203.h"
#include "respawn_actions.h"
#include "rttr_abi.h"
#include "buffs.h"
#include "engine.h"
#include "motion.h"
#include "npc_drive.h"
#include "hangover.h"
#include <array>
#include <cstdio>
#include <cstring>

namespace {
uint64_t clockMs = 10000, worldGen = 1, playerWuid = 100;
int playerObject = 0, otherObject = 0, entityObject = 0;
bool worldReady = true, flagsKnown = true, combat = false, dead = false, ko = false;
bool physicsKnown = true, physicalPresent = true, living = true, flyingKnown = true, bodyDown = false;
int flying = 0, writes = 0, soulScans = 0, positionReads = 0, flagReads = 0, probes = 0, fallOff = 0;
kcdmp::p4::Source writeSource = kcdmp::p4::Source::Unknown;
float pos[3] = {0,0,10}, groundZ = 10;
bool writeResult = true, writeMoves = true, writeFar = false, unknownAfter = false, positionKnown = true;
bool groundKnown = true, changeGenAtProbe = false, lifeAtProbe = false, combatAtProbe = false;
uint64_t delayAtProbe = 0, delayAtFlag = 0;
struct Body { float xyz[3]; };
std::array<Body,8198> world;
int pass = 0, fail = 0;
#define PCHECK(c, label) do { if (c) ++pass; else { ++fail; std::printf("FAIL p4 flow line %d: %s\n", __LINE__, label); } } while (0)
void reset() {
    clockMs += 30000; ++worldGen; kcdmp::p4::invalidate();
    worldReady = true; flagsKnown = true; combat = false; dead = false; ko = false;
    physicsKnown = true; physicalPresent = true; living = true; flyingKnown = true; bodyDown = false; flying = 0;
    writeResult = true; writeMoves = true; writeFar = false; unknownAfter = false; positionKnown = true; groundKnown = true; groundZ = 10; changeGenAtProbe = false; lifeAtProbe = false; combatAtProbe = false; delayAtProbe = 0; delayAtFlag = 0;
    pos[0] = 0; pos[1] = 0; pos[2] = 10; playerWuid = 100;
    kcdmp::p4::tick(); ++clockMs; // retire the preceding world's lease before measuring this fixture
    writes = 0; soulScans = 0; positionReads = 0; flagReads = 0; probes = 0; fallOff = 0;
    writeSource = kcdmp::p4::Source::Unknown;
}
kcdmp::p4::Context ctx(kcdmp::p4::Source source = kcdmp::p4::Source::Join) { return {source,clockMs+2000,worldGen}; }
const float partner[3] = {0,0,10};
}
uint64_t kcdmp_p4_test_now() { return clockMs; }
namespace kcdmp::p4 { void test_trace(const char*, ...) {} }
namespace kcdmp::wo216 { uint64_t lifecycle_generation() { return worldGen; } bool lifecycle_gameplay() { return worldReady; } }
namespace kcdmp::wo203 { bool reg_op(uint8_t,OpFn) { return true; } }
namespace kcdmp::buffs { void* as_c_soul(void* p) { return p; } }
namespace kcdmp::engine { void* entity_by_id(uint32_t eid) { return eid==0x7777 ? &entityObject : nullptr; } }
namespace kcdmp::actions {
uint64_t entity_wuid(void*) { return playerWuid; }
bool player_position(float* out) { ++positionReads; std::memcpy(out,pos,12); return positionKnown; }
bool suppress_fall_damage(bool on) { if (!on) ++fallOff; return true; }
bool teleport_player(float x, float y, float z, p4rules::Source source) {
    writeSource = source;
    ++writes;
    if (writeMoves) { pos[0] = writeFar ? x + 100 : x; pos[1] = y; pos[2] = z; }
    if (unknownAfter) positionKnown = false;
    return writeResult;
}
}
namespace kcdmp::motion { bool combat_mode_of(uint32_t,bool* out) { *out=combat;return flagsKnown; } bool local_down_now() { return bodyDown; } }
namespace kcdmp::npcdrive {
bool living_flying(void*,int* out) { *out=flying;return physicsKnown && physicalPresent && living && flyingKnown; }
bool physics_status(void*,PhysicsStatus* out) { *out={};out->present=physicalPresent?1:0;out->living=living?1:0;return physicsKnown; }
}
namespace kcdmp::hangover {
bool snap_to_ground(const float* in,float* out,bool) {
    ++probes;clockMs+=delayAtProbe;delayAtProbe=0;
    if (changeGenAtProbe) { ++worldGen;changeGenAtProbe=false; }
    if (lifeAtProbe) { p4::note_life_edge("fixture-life");lifeAtProbe=false; }
    if (combatAtProbe) { combat=true;combatAtProbe=false; }
    out[0]=in[0];out[1]=in[1];out[2]=groundZ;return groundKnown;
}
}
namespace kcdmp::rttr {
void* read_player_soul() { return &playerObject; }
bool soul_bool(void*,const char* name,bool* out) {
    ++flagReads;clockMs+=delayAtFlag;delayAtFlag=0;*out=std::strcmp(name,"IsDead")==0?dead:std::strcmp(name,"IsUnconscious")==0?ko:false;return flagsKnown;
}
bool soul_state(void*,const char*,float* out) { *out=100;return true; }
int for_each_soul(bool(*visit)(void*,void*),void* arg) {
    ++soulScans;for(auto& b:world)if(visit(&b,arg))break;return static_cast<int>(world.size());
}
bool soul_position(void* soul,float* out) { auto* b=static_cast<Body*>(soul);std::memcpy(out,b->xyz,12);return true; }
}
int p4_flow_tests(int* passed) {
    for(size_t i=0;i<world.size();++i) { world[i].xyz[0]=i<8195?10000.0f+static_cast<float>(i):3.0f;world[i].xyz[1]=0;world[i].xyz[2]=10; }
    reset();auto r=kcdmp::p4::place(partner,3,ctx());
    PCHECK(r.ok && writes==1,"fresh grounded placement actually writes");
    PCHECK(world.size()==8198 && soulScans==0,"8198-soul world fixture never causes a teleport-frame world scan");
    PCHECK(positionReads<=3 && flagReads<=6 && probes<=8,"normal placement has bounded direct reads and at most8 ground probes");
    using kcdmp::p4::Source;
    const Source sources[] = {Source::Join, Source::Leash, Source::LeashSettle, Source::Respawn, Source::Unstuck,
                              Source::VideoLanding, Source::VideoRollback, Source::SceneNudge};
    for (const Source source : sources) {
        reset();
        r = kcdmp::p4::place(partner, 3, ctx(source), source == Source::Respawn);
        PCHECK(r.ok && writes == 1 && writeSource == source, "placement forwards the exact validated source to its owner guard");
    }
    reset();uint8_t beforeStats[173]{},afterStats[173]{};kcdmp::p4::stats(beforeStats,sizeof beforeStats);
    writeResult=false;writeMoves=false;r=kcdmp::p4::place(partner,3,ctx());kcdmp::p4::stats(afterStats,sizeof afterStats);
    PCHECK(!r.ok && kcdmp::p4rules::u64(beforeStats+3)==kcdmp::p4rules::u64(afterStats+3),"failed engine call never counts as actual teleport");
    PCHECK(fallOff == 1, "a refused write relinquishes the newly acquired fall-damage lease");
    reset();kcdmp::p4::stats(beforeStats,sizeof beforeStats);writeMoves=false;r=kcdmp::p4::place(partner,3,ctx());kcdmp::p4::stats(afterStats,sizeof afterStats);
    PCHECK(!r.ok && kcdmp::p4rules::u64(beforeStats+3)==kcdmp::p4rules::u64(afterStats+3),"successful void return without observed movement is not a fake teleport count");
    reset();kcdmp::p4::stats(beforeStats,sizeof beforeStats);writeFar=true;r=kcdmp::p4::place(partner,3,ctx(kcdmp::p4::Source::Respawn),true);kcdmp::p4::stats(afterStats,sizeof afterStats);
    PCHECK(!r.ok && kcdmp::p4rules::u64(beforeStats+3+9*5)==kcdmp::p4rules::u64(afterStats+3+9*5) && kcdmp::p4rules::u64(beforeStats+3+9*6)==kcdmp::p4rules::u64(afterStats+3+9*6),"engine call with far after-position never fabricates safe wake maxima");
    reset();kcdmp::p4::stats(beforeStats,sizeof beforeStats);unknownAfter=true;r=kcdmp::p4::place(partner,3,ctx(kcdmp::p4::Source::Respawn),true);kcdmp::p4::stats(afterStats,sizeof afterStats);
    PCHECK(!r.ok && kcdmp::p4rules::u64(beforeStats+3+9*5)==kcdmp::p4rules::u64(afterStats+3+9*5) && kcdmp::p4rules::u64(beforeStats+3+9*6)==kcdmp::p4rules::u64(afterStats+3+9*6),"unknown after-position never fabricates safe wake maxima");
    reset();auto c=ctx();delayAtProbe=2001;r=kcdmp::p4::place(partner,3,c);
    PCHECK(!r.ok && writes==0,"slow ground probe cannot refresh the request deadline");
    reset();changeGenAtProbe=true;r=kcdmp::p4::place(partner,3,ctx());
    PCHECK(!r.ok && writes==0,"generation change during a successful probe refuses write");
    reset();lifeAtProbe=true;r=kcdmp::p4::place(partner,3,ctx());
    PCHECK(!r.ok && writes==0,"life edge during a successful probe refuses write");
    reset();combatAtProbe=true;r=kcdmp::p4::place(partner,3,ctx());
    PCHECK(!r.ok && writes==0,"combat beginning after first check refuses write");
    reset();combat=true;r=kcdmp::p4::place(partner,3,ctx());
    PCHECK(!r.ok && writes==0 && probes==0,"combat blocks before any ground work");
    reset();living=false;r=kcdmp::p4::place(partner,3,ctx());
    PCHECK(!r.ok && writes==0,"nonunconscious physical ragdoll still blocks automatic placement");
    reset();flagsKnown=false;r=kcdmp::p4::place(partner,3,ctx());
    PCHECK(!r.ok && writes==0,"unknown engine flags never imply safe movement");
    reset();physicsKnown=false;r=kcdmp::p4::place(partner,3,ctx());
    PCHECK(!r.ok && writes==0,"unknown current physics refuses placement");
    reset();groundZ=7;r=kcdmp::p4::place(partner,0,ctx());
    PCHECK(!r.ok && writes==0 && probes==1,"exact target never skips the3m drop rejection");
    reset();const float originalDeath[3]={0,0,26.4f};r=kcdmp::p4::place(partner,3,ctx(kcdmp::p4::Source::Respawn),true,originalDeath);
    PCHECK(!r.ok && writes==0 && probes<=8,"original death26.4 toground10 rejects even with placementDrop0");
    reset();c=ctx();kcdmp::p4::note_life_edge("fixture-down-wake");r=kcdmp::p4::place(partner,3,c);
    PCHECK(!r.ok && writes==0,"queued prelife challenge cannot act after wake");
    ++clockMs;r=kcdmp::p4::place(partner,3,ctx());
    PCHECK(r.ok && writes==1,"fresh challenge started after life edge works");
    reset();c=ctx(kcdmp::p4::Source::Unknown);r=kcdmp::p4::place(partner,3,c);
    PCHECK(!r.ok && writes==0,"legacy unknown source never auto-writes");

    reset();uint8_t req[9]={251},out[40]{};size_t used=0;kcdmp::p4rules::put64(req+1,worldGen);
    auto code=kcdmp::p4::snapshot(req,sizeof req,out,sizeof out,&used);float x=0;std::memcpy(&x,out+4,4);
    PCHECK(code==0 && used==32 && out[0]==1 && out[1]==15 && x==0,"snapshot251 has exact fresh32B v1 schema");
    pos[0]=19;code=kcdmp::p4::snapshot(req,sizeof req,out,sizeof out,&used);std::memcpy(&x,out+4,4);
    PCHECK(code==0 && x==19 && positionReads==2,"next snapshot reads actual changed position rather than cache");
    const uint64_t readStarted=clockMs;delayAtFlag=25;code=kcdmp::p4::snapshot(req,sizeof req,out,sizeof out,&used);
    PCHECK(code==0 && kcdmp::p4rules::u64(out+24)==readStarted,"snapshot capture is stamped before reflected reads, never after their delay");
    delayAtFlag=2001;code=kcdmp::p4::snapshot(req,sizeof req,out,sizeof out,&used);
    PCHECK(code!=0 && used==0,"slow snapshot read cannot report itself newly fresh");
    living=false;ko=false;code=kcdmp::p4::snapshot(req,sizeof req,out,sizeof out,&used);
    PCHECK(code==0 && (out[2]&4)!=0,"snapshot marks physical ragdoll as KO even with IsUnconscious=false");
    living=true;physicsKnown=false;code=kcdmp::p4::snapshot(req,sizeof req,out,sizeof out,&used);
    PCHECK(code!=0 && used==0,"snapshot refuses missing physical evidence");
    physicsKnown=true;++worldGen;code=kcdmp::p4::snapshot(req,sizeof req,out,sizeof out,&used);
    PCHECK(code!=0 && used==0,"snapshot refuses stale expected generation");

    reset();kcdmp::p4::arm_recovery(&playerObject);
    PCHECK(kcdmp::p4::protected_player(&playerObject),"exact live player receives recovery protection");
    PCHECK(!kcdmp::p4::protected_player(&otherObject),"other soul never inherits recovery");
    ++playerWuid;PCHECK(!kcdmp::p4::protected_player(&playerObject),"same pointer different WUID never inherits recovery");--playerWuid;
    kcdmp::p4::note_life_edge("physics-up");PCHECK(kcdmp::p4::protected_player(&playerObject),"physical-up debounce does not truncate newly armed recovery");
    clockMs+=4999;PCHECK(kcdmp::p4::protected_player(&playerObject),"recovery still applies at4999ms");
    ++clockMs;PCHECK(!kcdmp::p4::protected_player(&playerObject),"recovery expires at5000ms without refresh");
    reset();kcdmp::p4::arm_recovery(&playerObject);kcdmp::p4::invalidate();
    PCHECK(!kcdmp::p4::recovery_policy(),"world/session invalidation clears any-thread recovery policy");

    reset();r=kcdmp::p4::place(partner,3,ctx());const uint64_t first=clockMs;
    clockMs=first+1;kcdmp::p4::tick();clockMs=first+251;kcdmp::p4::tick();
    clockMs=first+900;r=kcdmp::p4::place(partner,3,ctx());PCHECK(r.ok,"second placement is valid before first landing completes");
    clockMs=first+901;kcdmp::p4::tick();clockMs=first+1151;kcdmp::p4::tick();
    clockMs=first+1251;kcdmp::p4::tick();PCHECK(fallOff==0,"new placement cannot inherit old point's continuous ground age");
    for(uint64_t t=first+1451;t<first+2151;t+=200){clockMs=t;kcdmp::p4::tick();}
    clockMs=first+2151;kcdmp::p4::tick();PCHECK(fallOff==1,"new point releases only after its own complete1s grounded proxy");
    reset();r=kcdmp::p4::place(partner,3,ctx());const uint64_t began=clockMs;flying=1;
    clockMs=began+19000;r=kcdmp::p4::place(partner,3,ctx());PCHECK(r.ok,"later placement can reuse shared fall lease");
    clockMs=began+20000;kcdmp::p4::tick();PCHECK(fallOff==1,"repeated placement cannot extend shared20s hard limit");
    uint8_t stats[175]{};stats[174]=0xA5;size_t n=kcdmp::p4::stats(stats+1,173);
    PCHECK(n==173 && stats[1]==1 && stats[2]==19 && stats[174]==0xA5,"actual status exports bounded173B TLV without touching canary");
    for(uint64_t age : {uint64_t(9999),uint64_t(10000),uint64_t(10001)}) {
        reset();r=kcdmp::p4::place(partner,3,ctx());const uint64_t at=clockMs;
        kcdmp::p4::stats(beforeStats,sizeof beforeStats);clockMs=at+age;
        kcdmp::p4::note_downed(&playerObject,pos,0,-1);kcdmp::p4::stats(afterStats,sizeof afterStats);
        const uint64_t delta=kcdmp::p4rules::u64(afterStats+3+9*14)-kcdmp::p4rules::u64(beforeStats+3+9*14);
        PCHECK(r.ok && delta==(age<=10000?1u:0u),"death marker honours inclusive10s boundary from confirmed mod teleport");
    }
    using namespace kcdmp::cvarlease;
    Lease lease;int cvar=0,changes=0;bool readable=true;
    auto read=[&](int* value){*value=cvar;return readable;};auto write=[&](int value){++changes;cvar=value;return true;};
    readable=false;PCHECK(change(lease,true,1,read,write)==Result::Unknown && changes==0 && !lease.held,"unknown original CVar never creates lease or write");
    readable=true;PCHECK(change(lease,true,1,read,write)==Result::Held && cvar==1 && lease.previous==0,"first fall lease remembers verified original integer");
    PCHECK(change(lease,true,1,read,write)==Result::Held && changes==1 && lease.previous==0,"repeat placement shares original CVar ownership without rebasing");
    cvar=2;PCHECK(change(lease,false,1,read,write)==Result::Foreign && cvar==2 && changes==1 && !lease.held,"foreign2 is never overwritten on release");
    cvar=0;change(lease,true,1,read,write);PCHECK(change(lease,false,1,read,write)==Result::Restored && cvar==0,"still-owned value restores verified original");
    if(passed)*passed=pass;return fail;
}
