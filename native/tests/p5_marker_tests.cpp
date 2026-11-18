// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// Link the actual p5_marker.cpp; only verified engine adapter boundaries are mocked.
#include "p5_marker.h"
#include "wo163.h"
#include <array>
#include <vector>
#include <algorithm>
#include <cstdio>
#include <cstring>
#ifdef _WIN32
#include "fault_guard.h"
#endif
namespace {
using namespace kcdmp::p5;
struct Entity { uint32_t eid=0;uint64_t wuid=0;bool alive=false,link=true; };
struct Mark { bool present=false; };
std::array<Entity,2048> entities;
std::array<Mark,8192> marks;
int mapObject=0;size_t entityNext=0,markNext=0;
uint64_t clockMs=1000,generation=1;
bool gameplay=true,ready=true,removeFault=false,unknownPresence=false,pendingWuid=false;
int failAdds=0,identifies=0,creates=0,adds=0,removes=0,releases=0,anchors=0,deleted=0,polls=0,geometryAnchors=0;
std::vector<std::array<uint8_t,43>> events;
int good=0,bad=0;
#define MCHECK(c,label) do{if(c)++good;else{++bad;std::printf("FAIL P5 marker line %d: %s\n",__LINE__,label);}}while(false)
Entity* entity(bool link=true){auto& e=entities.at(entityNext++);e={uint32_t(entityNext),uint64_t(100+entityNext),true,link};return &e;}
bool identify(void* ptr,Binding* out){++identifies;auto* e=static_cast<Entity*>(ptr);if(!e||!e->alive)return false;*out={&mapObject,e,e->link?e:nullptr,e->eid,e->wuid,kcdmp::p5::physical_world()};out->pending=!e->link||!e->wuid;return e->link&&e->wuid!=0;}
bool backendReady(){return ready;}
bool create(const Binding&,const Spec&,Shared* out){++creates;auto& m=marks.at(markNext++);m={};out->p=&m;out->ctrl=&m;return true;}
bool add(const Binding&,const Spec&,const Shared& sp){++adds;static_cast<Mark*>(sp.p)->present=true;if(failAdds){--failAdds;return false;}return true;}
Presence contains(const Binding&,const Shared& sp){if(unknownPresence)return Presence::Unknown;return sp.p&&static_cast<Mark*>(sp.p)->present?Presence::Present:Presence::Absent;}
bool remove(const Binding&,const Shared& sp){++removes;if(removeFault)return false;static_cast<Mark*>(sp.p)->present=false;return true;}
bool release(Shared* sp){++releases;*sp={};return true;}
Backend backend{backendReady,identify,create,add,contains,remove,release};
void event(const uint8_t* p,size_t n){MCHECK(n==43,"actual native event size43");if(n==43){std::array<uint8_t,43> copy{};std::copy(p,p+n,copy.begin());events.push_back(copy);}}
struct Reply {uint8_t code=255;std::array<uint8_t,512> bytes{};size_t used=0;};
Reply command(uint8_t action,uint8_t owner=0,uint64_t id=0,uint64_t revision=0,uint32_t world=7,uint64_t gen=0,uint8_t kind=1){
    uint8_t p[45]={253,1,action,kind,owner};put64(p+5,gen?gen:generation);put32(p+13,world);put64(p+17,id);put64(p+25,revision);
    const float xyz[3]={10,20,30};std::memcpy(p+33,xyz,12);Reply r;r.code=kcdmp::p5::command(p,sizeof p,r.bytes.data(),r.bytes.size(),&r.used);return r;
}
Reply page(uint32_t token=0,uint16_t cursor=0,uint64_t gen=0,uint32_t world=7){
    uint8_t p[20]={252,1};put64(p+2,gen?gen:generation);put32(p+10,world);put32(p+14,token);p[18]=uint8_t(cursor);p[19]=uint8_t(cursor>>8);
    Reply r;r.code=kcdmp::p5::list(p,sizeof p,r.bytes.data(),r.bytes.size(),&r.used);return r;
}
void reset(){
    unknownPresence=removeFault=false;for(auto& mark:marks)mark.present=false;
    kcdmp::p5::physical_world_changed();++generation;clockMs+=10000;ready=gameplay=true;pendingWuid=false;failAdds=0;
    kcdmp::p5::set_backend(backend);kcdmp::p5::set_event(event);
    auto r=command(3);MCHECK(r.code==0&&r.used==16&&r.bytes[2]==0,"new loaded scope ACK");
    identifies=creates=adds=removes=releases=anchors=deleted=polls=geometryAnchors=0;events.clear();
}
const float death[3]={10,20,30};
}
uint64_t kcdmp_p5_test_now(){return clockMs;}
uint64_t kcdmp_p5_test_generation(){return generation;}
bool kcdmp_p5_test_gameplay(){return gameplay;}
namespace kcdmp::p5 {
void test_trace(const char*,...){}
void* fallback_anchor(uint64_t,const float*,bool grave){++anchors;if(grave)++geometryAnchors;auto* e=entity();if(pendingWuid)e->wuid=0;return e;}
bool source_identity(void* ptr,Binding* out){if(out->entity&&(out->entity!=ptr||out->physical!=physical_world()))return false;auto* e=static_cast<Entity*>(ptr);if(!e||!e->alive)return false;*out={nullptr,e,nullptr,e->eid,e->wuid,physical_world()};out->pending=e->wuid==0;return e->wuid!=0;}
bool source_alive(const Binding& b){auto* e=static_cast<Entity*>(b.entity);return e&&e->alive&&e->eid==b.eid&&e->wuid==b.wuid&&b.physical==physical_world()&&(!b.linkable||(e->link&&b.linkable==e));}
bool release_anchor(const Binding& b){if(!source_alive(b))return false;++deleted;static_cast<Entity*>(b.entity)->alive=false;return true;}
void poll_world(){++polls;}
}
int p5_marker_tests(int* passed){
    using namespace kcdmp::p5;good=bad=0;set_backend(backend);
    // Actual production handle logic, each bad marker owns its own terminal state.
    ready=false;Handle pending;auto* e=entity();
    MCHECK(!add_handle(pending,backend,e,Spec{0x2B,0,nullptr})&&pending.state==State::Empty&&creates==0&&adds==0,"not ready makes no engine create/add call");
    ready=true;failAdds=8;
    std::array<Handle,9> handles;
    for(unsigned i=0;i<8;++i){MCHECK(!add_handle(handles[i],backend,entity(),Spec{0x2B,0,nullptr}),"one failed Add is refused");MCHECK(!handles[i].mayReference&&handles[i].state==State::Retired,"successful cleanup retires just the failed handle");}
    MCHECK(add_handle(handles[8],backend,entity(),Spec{0x2B,0,nullptr})&&handles[8].state==State::Added,"good ninth marker survives eight distinct failed Adds");
    Handle quarantine;removeFault=true;failAdds=1;const int beforeRelease=releases;
    MCHECK(!add_handle(quarantine,backend,entity(),Spec{0x2B,0,nullptr})&&quarantine.mayReference&&quarantine.state==State::Quarantined,"Add partial mutation plus Remove fault retains raw-reference quarantine");
    const int attempts=removes;
    MCHECK(!remove_handle(quarantine,backend)&&removes==attempts&&releases==beforeRelease,"uncertain Remove is not repeatedly called and shared ownership is retained");
    removeFault=false;static_cast<Mark*>(quarantine.mark.p)->present=false;
    MCHECK(remove_handle(quarantine,backend)&&!quarantine.mayReference,"verified absence allows safe later retirement");

    reset();own_death(101,false,nullptr,death);
    MCHECK(anchors==1&&adds==1&&events.size()==1,"empty inventory/no grave produces one dedicated marker-only holder and event");
    MCHECK(events.back()[1]==1&&events.back()[2]==1&&u32(events.back().data()+11)==7&&u64(events.back().data()+15)==101,"marker-only event preserves action kind world id");
    const uint64_t rev=u64(events.back().data()+23);tick();own_death(101,false,nullptr,death);
    MCHECK(anchors==1&&adds==1&&events.size()==1,"death descriptor and rendered marker are idempotent");
    own_removed(101);
    MCHECK(deleted==1&&events.size()==2&&events.back()[1]==0&&u64(events.back().data()+23)>rev,"logical removal detaches map before deleting own anchor and emits newer tombstone");

    reset();auto* noLink=entity(false);own_death(102,true,noLink,death);
    MCHECK(anchors==0&&creates==0&&adds==0,"original grave with delayed linkable waits without a fault retry");
    for(int i=0;i<21;++i){clockMs+=50;tick();}
    MCHECK(anchors==1&&adds==1&&geometryAnchors==0&&events.back()[2]==0,"original grave permanently without linkable eventually uses plain fallback without duplicate geometry");
    reset();ready=false;own_death(103,false,nullptr,death);
    MCHECK(anchors==0&&creates==0&&adds==0,"UI-not-ready coalesces descriptor without spawn/create/add");
    ready=true;tick();MCHECK(anchors==1&&adds==1,"ready tick creates exactly one pending marker");

    reset();pendingWuid=true;own_death(108,false,nullptr,death);auto* pendingAnchor=&entities[entityNext-1];
    MCHECK(anchors==1&&creates==0&&adds==0,"new holder with no WUID waits before first Create/Add");
    for(int i=0;i<3;++i){clockMs+=100;tick();}
    MCHECK(anchors==1&&creates==0&&adds==0,"pending WUID uses same identity without spawn/fault retries");
    pendingAnchor->wuid=12345;tick();MCHECK(anchors==1&&adds==1,"first proven WUID resumes same pending marker once");
    reset();pendingWuid=true;own_death(109,false,nullptr,death);pendingAnchor=&entities[entityNext-1];
    for(int i=0;i<21;++i){clockMs+=10;tick();}
    pendingAnchor->wuid=12346;tick();MCHECK(anchors==1&&creates==0&&adds==0,"twenty pending ticks are terminal even if WUID appears later");
    reset();pendingWuid=true;own_death(110,false,nullptr,death);pendingAnchor=&entities[entityNext-1];clockMs+=2001;pendingAnchor->wuid=12347;tick();
    MCHECK(anchors==1&&creates==0&&adds==0,"two-second pending deadline never freshens on a late WUID");

    reset();own_death(104,false,nullptr,death);removeFault=true;
    own_removed(104);MCHECK(deleted==0,"failed map removal never deletes its owned linkable anchor");
    auto failedLoad=command(4);MCHECK(failedLoad.code!=0&&failedLoad.bytes[2]==3&&suspended(),"load barrier refuses unknown raw map ownership");
    MCHECK(deleted==0,"refused load cannot delete protected anchor");

    reset();own_death(105,false,nullptr,death);auto* gone=&entities[entityNext-1];gone->alive=false;
    const int oldRemoves=removes;tick();
    MCHECK(removes>oldRemoves,"already-added marker observes destroyed anchor and safely detaches its raw linkable");

    reset();auto* replacedLink=entity();own_death(107,true,replacedLink,death);replacedLink->link=false;
    const int beforeLinkRemove=removes;tick();MCHECK(removes>beforeLinkRemove,"same EID with changed raw linkable detaches the old map reference");
    reset();MCHECK(command(1,2,180,1,7,0,0).code==0&&geometryAnchors==1,"remote Grave kind keeps its requested mirror geometry");

    reset();own_death(106,false,nullptr,death);const int beforeReconnect=adds;const uint64_t physical=physical_world();++generation;
    auto reconnect=command(3);MCHECK(reconnect.code==0&&adds==beforeReconnect&&physical_world()==physical,"same loaded-world reconnect preserves stable physical marker without respawn");
    MCHECK(command(1,2,901,1,7,generation-1).code!=0,"stale native generation cannot add a marker");
    MCHECK(command(1,2,901,1,8).code!=0,"other world cannot add a marker");
    auto prepare=command(4);MCHECK(prepare.code==0&&suspended(),"prepare-load detaches and closes renderer before load");
    MCHECK(command(1,2,901,1).code!=0,"even matching-generation Add cannot reopen preparing scope");

    reset();MCHECK(command(1,0,200,1).code==0&&anchors==1,"authenticated host peer zero can add a death marker");
    reset();auto added=command(1,3,201,1);MCHECK(added.code==0&&anchors==1,"peer marker adds once");
    const int afterAdd=adds;MCHECK(command(1,3,201,1).code==0&&adds==afterAdd,"equal identical peer Add is idempotent");
    MCHECK(command(0,3,201,2).code==0&&deleted==1,"newer Remove creates tombstone and deletes only detached owned anchor");
    MCHECK(command(1,3,201,1).code==0&&adds==afterAdd,"old Add cannot resurrect newer tombstone");
    MCHECK(command(1,3,201,2).code!=0&&adds==afterAdd,"equal-revision conflicting action is rejected");
    MCHECK(command(1,4,201,1).code==0&&adds==afterAdd+1,"different owner's same id has independent revision");

    reset();MCHECK(command(1,0,250,1).code==0,"host marker before renderer cleanup");
    MCHECK(command(2,255).code==0&&deleted==1,"ClearOwner255 detaches renderer for all peers");
    const int clearedAdds=adds,clearedAnchors=anchors;tick();
    MCHECK(adds==clearedAdds&&anchors==clearedAnchors,"renderer cleanup stays cleared until actual peer replay");
    MCHECK(command(1,0,250,1).code==0&&adds==clearedAdds+1&&anchors==clearedAnchors+1,"same generation equal Add can restore renderer after reconnect cleanup");
    MCHECK(command(0,0,250,2).code==0&&command(2,255).code==0&&command(1,0,250,1).code==0&&adds==clearedAdds+1,"renderer cleanup never erases a true newer Remove tombstone");

    reset();bool churnAccepted=true;
    for(uint64_t first=0;first<400;first+=128){
        const uint64_t end=std::min<uint64_t>(first+128,400);
        for(uint64_t i=first;i<end;++i)churnAccepted=command(1,2,10000+i,1).code==0&&churnAccepted;
        for(uint64_t i=first;i<end;++i)churnAccepted=command(0,2,10000+i,2).code==0&&churnAccepted;
    }
    MCHECK(churnAccepted&&anchors==400&&adds==400&&deleted==400,"four hundred distinct Add/Remove facts with at most128 active never exhaust the active marker cap");
    const int afterChurn=adds;
    MCHECK(command(1,2,10000,1).code==0&&adds==afterChurn,"journal retains the first Remove tombstone across four hundred identities");
    MCHECK(command(1,2,20000,1).code==0&&adds==afterChurn+1,"a fresh marker still renders after four hundred retired marker identities");

    reset();MCHECK(command(4).code==0,"prepare precedes physical own-save purge");
    physical_world_changed();const uint64_t beforePrivate=physical_world();
    auto* savedGrave=entity();own_death(270,true,savedGrave,death);
    MCHECK(command(3,0,0,0,0).code==0&&physical_world()==beforePrivate&&adds==1,"prepared same-generation own-save scope0 restores real local grave marker");
    MCHECK(events.empty(),"private scope never emits host-world marker facts");
    gameplay=false;const int pausedAdds=adds;own_death(271,false,nullptr,death);tick();
    MCHECK(adds==pausedAdds,"missing actual Gameplay proof never creates pending marker");

    reset();for(uint64_t id=301;id<=317;++id)own_death(id,false,nullptr,death);
    auto first=page();const uint32_t token=u32(first.bytes.data()+13);
    MCHECK(first.code==0&&first.used==485&&first.bytes[19]==16&&first.bytes[20]==0,"first actual native page has16 rows and exact21+16x29 bytes");
    own_removed(317);auto last=page(token,16);
    MCHECK(last.code==0&&last.used==50&&last.bytes[19]==1&&last.bytes[20]==1&&u64(last.bytes.data()+22)==317,"paging token keeps frozen descriptor snapshot despite inter-page mutation");
    MCHECK(page(token+1,16).code!=0,"wrong snapshot token refuses");
    clockMs+=5001;MCHECK(page(token,16).code!=0,"expired snapshot cannot be refreshed by continuation");
    auto next=page();MCHECK(next.code==0&&next.bytes[19]==16&&next.bytes[20]==1,"new snapshot reflects removed descriptor");
    MCHECK(page(token,0).code!=0,"old token never attaches to new snapshot");
    uint64_t counts[3]{};kcdmp::p5::counts(counts);MCHECK(counts[2]==0,"no session-wide marker-off state is introduced");
    const auto graveCount=counts[0];Ref partner;MCHECK(render_add(entity(),&partner,0x3C),"partner icon can still render independently");
    kcdmp::p5::counts(counts);MCHECK(counts[0]==graveCount,"grave marker counter never includes partner icon renders");
#ifdef _WIN32
    // The real fault registry retains only static Sites, never per-handle addresses.
    static kcdmp::fault::Site ordinary{"test/p5-ordinary",kcdmp::fault::Kind::Call};
    static kcdmp::fault::Site isolated{"test/p5-marker",kcdmp::fault::Kind::Call,kcdmp::fault::Policy::MarkerIsolated};
    kcdmp::fault::set_switch_off(true);
    for(unsigned i=0;i<8;++i){kcdmp::fault::on_fault(ordinary);kcdmp::fault::on_fault(isolated);}
    MCHECK(!kcdmp::fault::enabled(ordinary)&&kcdmp::fault::enabled(isolated),"ordinary8-fault safety remains; marker failures never globally switch later handles off");
    bool ran=false;MCHECK(kcdmp::fault::guarded(isolated,[&]{ran=true;})&&ran,"isolated static call Site still permits an unrelated good marker after8 faults");
    const auto totals=kcdmp::fault::totals();MCHECK(totals.sites>=2,"registry can traverse static Sites after all local marker handles are retired");
#endif
    if(passed)*passed=good;return bad;
}
