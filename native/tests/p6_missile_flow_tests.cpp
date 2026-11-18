// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// Link actual p6_missile.cpp; only its engine boundary is replaced.
#include "p6_missile.h"
#include "wo163.h"
#include <vector>
#include <array>
#include <cstdio>
#include <cstring>
#include <limits>
namespace {
using namespace kcdmp::p6;using namespace kcdmp::p6rules;
int good=0,bad=0;
#define FCHECK(c,label) do{if(c)++good;else{++bad;std::printf("FAIL P6 missile flow line %d: %s\n",__LINE__,label);}}while(false)
int actorTag=0,playerSoulTag=0,entityTag=0,soulTag=0,combatTag=0,replacementTag=0;
uint64_t clockMs=1000;Scope world;Victim current;
bool isMain=true,readOkay=true,sameOkay=true,arranged=false,avatarOkay=true,applyOkay=true;
bool replaceOnFinalVictim=false;
int victimReads=0,vitalReads=0,applies=0;uint8_t applySteps=1;
float health=100,stamina=200;std::vector<std::array<uint8_t,61>> events;
enum class AfterRead{None,Generation,Life,Physical,World,Entity,Clock,Disconnect,VictimDead,VictimAvatar};AfterRead afterRead=AfterRead::None;
uint64_t now(){return clockMs;}bool mainThread(){return isMain;}Scope scope(){return world;}
bool victim(uint32_t id,Victim* out){++victimReads;if(id!=current.eid)return false;if(replaceOnFinalVictim&&victimReads>=2)current.entity=&replacementTag;*out=current;return true;}
bool same(const Victim& v){return sameOkay&&v.eid==current.eid&&v.wuid==current.wuid&&v.entity==current.entity&&v.soul==current.soul&&v.combat==current.combat&&v.guid==current.guid;}
bool vitals(void* s,float* hp,float* st){++vitalReads;if(!readOkay||s!=current.soul)return false;*hp=health;*st=stamina;
    const auto change=afterRead;afterRead=AfterRead::None;
    if(change==AfterRead::Generation)++world.generation;if(change==AfterRead::Life)++world.life;if(change==AfterRead::Physical)++world.physical;
    if(change==AfterRead::World)++world.world;if(change==AfterRead::Entity)current.entity=&replacementTag;
    if(change==AfterRead::Clock)clockMs+=2001;if(change==AfterRead::Disconnect)connection(false);
    if(change==AfterRead::VictimDead)current.dead=true;if(change==AfterRead::VictimAvatar)current.avatar=true;
    return true;
}
bool isArranged(uint32_t id){return id==current.eid&&arranged;}
uint32_t named(const char* name){return std::strcmp(name,"npc_test")==0?current.eid:0;}
bool avatar(uint8_t peer,uint32_t id){return avatarOkay&&peer<16&&id==99;}
bool apply(const uint8_t*,size_t,uint8_t* steps){++applies;*steps=applySteps;return applyOkay;}
void event(const uint8_t* p,size_t n){FCHECK(n==61,"real contact event retains61-byte extension");if(n==61){std::array<uint8_t,61> b{};std::memcpy(b.data(),p,n);events.push_back(b);}}
void reset(uint8_t role=2){
    reset_for_tests();set_backend({now,mainThread,scope,victim,same,vitals,isArranged,named,avatar,apply});set_event(event);
    clockMs=1000;world={10,20,30,7,77,role,&actorTag,&playerSoulTag,true,true};
    current={11,222,&entityTag,&soulTag,&combatTag,{},false,false};current.guid[0]=42;
    isMain=readOkay=sameOkay=avatarOkay=applyOkay=true;arranged=replaceOnFinalVictim=false;afterRead=AfterRead::None;
    health=100;stamina=200;applySteps=1;victimReads=vitalReads=applies=0;events.clear();capture_epoch();
}
void hit(bool missile=true){contact(missile,world.player,current.eid,current.combat);}
std::vector<uint8_t> request(uint64_t id=1,uint8_t peer=0,uint64_t deadline=0){
    const char* name="npc_test";const size_t nn=std::strlen(name),an=30+nn;std::vector<uint8_t> b(9+an+50,0);
    b[0]=18;b[1]=255;b[2]=255;b[8]=128;auto* ab=b.data()+9;
    std::memcpy(ab,current.guid.data(),16);float hp=5,st=2;std::memcpy(ab+16,&st,4);std::memcpy(ab+20,&hp,4);ab[24]=5;put32(ab+25,99);ab[29]=uint8_t(nn);std::memcpy(ab+30,name,nn);
    auto* t=ab+an;std::memcpy(t,"P6M1",4);t[4]=peer;t[5]=7;put32(t+6,world.world);put64(t+10,world.generation);
    put64(t+18,100);put64(t+26,200);put64(t+34,id);put64(t+42,deadline?deadline:clockMs+2000);return b;
}
struct Result{uint8_t reason=255;size_t used=0;std::array<uint8_t,40> bytes{};Status status()const{return Status(bytes[1]);}};
Result run(const std::vector<uint8_t>& b){Result r;r.reason=measured_apply(b.data(),b.size(),r.bytes.data(),r.bytes.size(),&r.used);return r;}
}
namespace kcdmp::p6 {void test_trace(const char*,...) {}}
int p6_missile_flow_tests(int* passed){
    using namespace kcdmp::p6;using namespace kcdmp::p6rules;good=bad=0;
    reset();const uint64_t epoch=capture_epoch();hit();
    FCHECK(events.empty()&&applies==0&&suppress_sample(current.soul,current.guid.data()),"real hook contact waits for measurement and suppresses the second generic sampler path");
    health=95;stamina=198;clockMs=1100;tick();
    FCHECK(events.size()==1&&events[0][25]==1&&events[0][26]==0&&events[0][27]==1&&events[0][28]==7,"one joiner measurement emits attributed missile proof without overwriting judgeByte26");
    float dh=0,ds=0;std::memcpy(&ds,events[0].data()+16,4);std::memcpy(&dh,events[0].data()+20,4);
    FCHECK(dh==5&&ds==2&&u64(events[0].data()+37)==10&&u64(events[0].data()+45)==epoch&&u64(events[0].data()+53)==1000,"event carries actual delta and original generation/epoch/contact time");
    tick();FCHECK(events.size()==1&&applies==0,"source observer never writes damage or emits a contact twice");
    clockMs=1661;FCHECK(!suppress_sample(current.soul,current.guid.data()),"generic sample suppression has a bounded660ms lifetime");
    for(unsigned unavailable=0;unavailable<2;++unavailable){reset();world.alive=false;
        if(unavailable){world.ready=false;world.player=0;world.actor=nullptr;world.soul=nullptr;}
        const bool consumed=contact(true,unavailable?0:world.player,current.eid,current.combat,unavailable!=0);
        health=90;tick();float genericHp=10,genericSt=5;
        FCHECK(consumed&&events.empty()&&filter_sample(current.soul,current.guid.data(),&genericHp,&genericSt)&&genericHp==0&&genericSt==0,"unknown/down local-player proof emits no receipt and consumes one legacy missile sample instead of a fake melee mark");
        genericHp=4;genericSt=2;
        FCHECK(!filter_sample(current.soul,current.guid.data(),&genericHp,&genericSt)&&genericHp==4&&genericSt==2,"unknown-contact quarantine retires after one inseparable sample and does not eat later independent melee");}
    reset();hit();health=90;stamina=195;clockMs+=50;tick();float combinedHp=17,combinedSt=8;
    FCHECK(events.size()==1&&!filter_sample(current.soul,current.guid.data(),&combinedHp,&combinedSt)&&combinedHp==7&&combinedSt==3,"completed HP10/ST5 missile credit is consumed exactly, preserving combined HP7/ST3 independent remainder");
    combinedHp=4;combinedSt=2;
    FCHECK(!filter_sample(current.soul,current.guid.data(),&combinedHp,&combinedSt)&&combinedHp==4&&combinedSt==2&&!suppress_sample(current.soul,current.guid.data()),"spent missile credit retires its block so the later melee sample remains untouched");
    reset(1);hit();health=0;current.dead=true;tick();
    FCHECK(events.size()==1&&events[0][24]==1&&suppress_sample(current.soul,current.guid.data()),"host local missile reports actual death once while suppressing its generic duplicate sampler");
    reset();hit();health=0;current.dead=true;tick();
    FCHECK(events.size()==1&&events[0][24]==0,"joiner provisional copy death never invents the host fatal fact");

    for(unsigned why=0;why<5;++why){reset();if(why==0)hit(false);if(why==1)contact(true,999,current.eid,current.combat);
        if(why==2){current.avatar=true;hit();}if(why==3){current.dead=true;hit();}if(why==4)contact(true,world.player,current.eid,&replacementTag);
        health=90;tick();FCHECK(events.empty()&&applies==0,"nonmissile/otherattacker/avatar/dead/wrongcombat contact never creates measured evidence");}
    reset();isMain=false;hit();FCHECK(victimReads==0&&vitalReads==0&&events.empty(),"off-main contact makes no engine/RTTR-affine read");
    isMain=true;health=90;tick();FCHECK(events.empty()&&suppress_sample(current.soul,current.guid.data()),"off-main notice only quarantines generic attribution, never fabricates missing baseline");
    for(unsigned mixed=0;mixed<4;++mixed){reset();hit();if(mixed==0)hit(false);if(mixed==1)hit();if(mixed==2)remote_follow(current.guid.data());if(mixed==3){isMain=false;hit(false);isMain=true;}
        health=90;tick();FCHECK(events.empty(),"mixed/overlapping/remote-follow/off-main overlap cannot attribute a shared health window to one missile");}
    for(unsigned stale=0;stale<8;++stale){reset();hit();if(stale==0)++world.generation;if(stale==1)++world.life;if(stale==2)++world.physical;if(stale==3)++world.world;
        if(stale==4)world.actor=&replacementTag;if(stale==5)world.soul=&replacementTag;if(stale==6)connection(false);if(stale==7)world.alive=false;
        health=90;tick();FCHECK(events.empty(),"load/life/physical/world/actor/soul/disconnect/down edge retires pending source evidence");}
    for(unsigned badRead=0;badRead<5;++badRead){reset();hit();health=90;if(badRead==0)sameOkay=false;if(badRead==1)readOkay=false;if(badRead==2)clockMs=1601;if(badRead==3)health=(std::numeric_limits<float>::quiet_NaN)();if(badRead==4)current.entity=&replacementTag;
        tick();health=80;readOkay=sameOkay=true;tick();FCHECK(events.empty(),"identity/read/expiry/nonfinite/replacement refusal never retries a later unrelated health delta");}
    reset();health=(std::numeric_limits<float>::quiet_NaN)();hit();health=90;stamina=190;tick();FCHECK(events.empty(),"unknown initial health cannot be certified through a positive stamina delta");
    reset();hit();health=90;replaceOnFinalVictim=true;tick();
    FCHECK(events.empty(),"last victim getter cannot rebind the old contact to a new entity sharing its soul/WUID");
    for(auto change:{AfterRead::Generation,AfterRead::Life,AfterRead::Physical,AfterRead::World,AfterRead::Entity,AfterRead::Clock,AfterRead::Disconnect}){
        reset();hit();health=90;afterRead=change;tick();char label[128]{};
        std::snprintf(label,sizeof label,"source engine-read continuation fences original context/identity/deadline/session change=%u",unsigned(change));
        FCHECK(events.empty(),label);}

    reset(1);const auto body=request();auto accepted=run(body);
    FCHECK(accepted.reason==kcdmp::wo163::kROk&&accepted.used==40&&accepted.status()==Status::Accepted&&applies==1&&accepted.bytes[2]==1,"host explicit measured missile accept performs one existing attributed write");
    clockMs+=10000;auto retry=run(body);
    FCHECK(retry.bytes==accepted.bytes&&applies==1,"lost ACK retry returns cached original outcome without deadline refresh or second damage");
    auto conflict=body;float changed=7;std::memcpy(conflict.data()+9+20,&changed,4);auto refused=run(conflict);
    FCHECK(refused.status()==Status::Conflict&&applies==1,"same receipt with different measured fact never writes");
    reset(1);auto peer15=run(request(1,15));FCHECK(peer15.status()==Status::Accepted&&applies==1,"last supported peer15 has an independent valid host receipt");
    reset(1);auto peer16=run(request(1,16));FCHECK(peer16.status()==Status::Invalid&&applies==0,"unsupported peer16 never reaches physical apply");
    reset(1);applyOkay=false;applySteps=0;const auto uncertainBody=request();auto uncertain=run(uncertainBody);applyOkay=true;applySteps=1;
    FCHECK(uncertain.status()==Status::Uncertain&&run(uncertainBody).status()==Status::Uncertain&&applies==1,"uncertain first physical outcome is terminal and cannot be retried into duplicate damage");
    for(unsigned invalid=0;invalid<8;++invalid){reset(1);auto b=request();
        if(invalid==0)world.role=2;if(invalid==1)world.ready=false;if(invalid==2)arranged=true;if(invalid==3)current.dead=true;if(invalid==4)current.avatar=true;
        if(invalid==5)avatarOkay=false;if(invalid==6)readOkay=false;if(invalid==7)clockMs+=2001;
        auto result=run(b);FCHECK(result.status()!=Status::Accepted&&applies==0,"wrong role/loading/arranged/dead/avatar/unknownavatar/unreadable/expired host receipt is failclosed");}
    for(auto change:{AfterRead::Generation,AfterRead::Life,AfterRead::Physical,AfterRead::World,AfterRead::Entity,AfterRead::Clock,AfterRead::Disconnect}){
        reset(1);auto b=request();afterRead=change;auto result=run(b);char label[128]{};
        std::snprintf(label,sizeof label,"host physical write fences original context/identity/deadline/session change=%u",unsigned(change));
        FCHECK(result.status()!=Status::Accepted&&applies==0,label);}
    for(auto change:{AfterRead::VictimDead,AfterRead::VictimAvatar}){
        reset(1);auto b=request();afterRead=change;auto result=run(b);
        FCHECK(result.status()==Status::Victim&&applies==0,"fresh victim read after vitals refuses a body that died or became an avatar before apply");}
    reset(1);auto badHeader=request();badHeader[8]=0;FCHECK(run(badHeader).reason==kcdmp::wo163::kRBadRequest&&applies==0,"ordinary melee discriminator cannot enter the measured-only path");
    reset(1);auto finite=request();float nan=(std::numeric_limits<float>::quiet_NaN)();std::memcpy(finite.data()+9+16,&nan,4);
    FCHECK(run(finite).status()==Status::Invalid&&applies==0,"nonfinite wire damage is refused before any engine mutation");
    reset(1);auto malformed=request();malformed.push_back(0);FCHECK(run(malformed).reason==kcdmp::wo163::kRBadRequest&&applies==0,"trailing extension data never masquerades as a complete receipt");
    reset(1);auto oldScope=request();put64(oldScope.data()+oldScope.size()-40,999);FCHECK(run(oldScope).status()==Status::Scope&&applies==0,"old host generation never applies despite a valid sender receipt key");
    if(passed)*passed=good;return bad;
}
