// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#include "p6_missile.h"
#include "wo163.h"
#include <atomic>
#include <mutex>
#include <vector>
#include <cstring>
#include <cstdio>
#ifndef KCDMP_P6_TEST
#include <windows.h>
#include "main_thread.h"
#include "wo216.h"
#include "p5_marker.h"
#include "p4.h"
#include "hits.h"
#include "motion.h"
#include "engine.h"
#include "respawn_actions.h"
#include "rttr_abi.h"
#include "npc_drive.h"
#include "log.h"
#else
namespace kcdmp::p6 {void test_trace(const char*,...);}
#define logf test_trace
#endif
namespace kcdmp::p6 {
namespace {
using namespace p6rules;
#ifndef KCDMP_P6_TEST
Backend defaults();
Backend g_b=defaults();
#else
Backend g_b;
#endif
EventFn g_event=nullptr;
uint32_t g_refusalLogged=0;
void refused_once(unsigned reason,const char* text){const uint32_t bit=1u<<reason;if((g_refusalLogged&bit)==0){g_refusalLogged|=bit;logf("P6-MISSILE contact refused=%s no_legacy_melee_fallback=1",text);}}
std::atomic<bool> g_connected{false};std::atomic<uint64_t> g_connection{0};
std::atomic<uint32_t> g_player{0};std::atomic<uint64_t> g_physical{0};
Scope g_scope;uint64_t g_epoch=0,g_lastConnection=0,g_id=0;
Journal g_journal;
struct Contact {Victim victim;Scope scope;uint64_t epoch=0,id=0;Measure measure;};
struct Block {uint32_t eid=0;void* combat=nullptr;void* soul=nullptr;std::array<uint8_t,16> guid{};uint64_t physical=0,until=0;bool complete=false;float hp=0,st=0;};
struct Notice {bool missile=false,local=false;uint32_t attacker=0,victim=0;void* combat=nullptr;uint64_t physical=0,connection=0;};
std::vector<Contact> g_contacts;std::vector<Block> g_blocks;
std::mutex g_noticeLock;std::vector<Notice> g_notices;bool g_noticeOverflow=false;
#ifndef KCDMP_P6_TEST
uint64_t engine_now(){return GetTickCount64();}
bool engine_main(){return main_thread::main_thread_id()!=0&&GetCurrentThreadId()==main_thread::main_thread_id();}
Scope engine_scope(){
    Scope s;s.generation=wo216::lifecycle_generation();s.life=p4::life_edge_tick();s.role=wo216::lifecycle_role();s.physical=p5::physical_world();
    const bool loaded=p5::loaded_scope(&s.world);s.actor=motion::local_actor(&s.player);s.soul=rttr::read_player_soul();
    s.ready=loaded&&s.world&&wo216::lifecycle_gameplay()&&s.generation&&s.actor&&s.soul;
    bool dead=false,ko=false;npcdrive::PhysicsStatus phys{};
    const bool physicsKnown=npcdrive::physics_status(engine::entity_by_id(s.player),&phys);
    s.alive=s.ready&&rttr::soul_bool(s.soul,"IsDead",&dead)&&rttr::soul_bool(s.soul,"IsUnconscious",&ko)&&!dead&&!ko&&physicsKnown&&phys.present&&phys.living;
    if(s.generation!=wo216::lifecycle_generation()||s.life!=p4::life_edge_tick()||s.role!=wo216::lifecycle_role()||!wo216::lifecycle_gameplay())s.ready=s.alive=false;
    return s;
}
bool engine_victim(uint32_t eid,Victim* out){
    *out={};out->eid=eid;out->entity=engine::entity_by_id(eid);out->soul=hits::soul_of_eid(eid);
    if(!out->entity||!out->soul)return false;
    out->wuid=actions::entity_wuid(out->entity);out->combat=rttr::combat_soul_of(out->soul);
    const char* name=engine::entity_name(out->entity);
    out->avatar=hits::avatar_known(eid)||(name&&std::strncmp(name,"kcd2mp_",7)==0);
    return out->wuid&&out->combat&&rttr::soul_guid(out->soul,out->guid.data())&&rttr::soul_bool(out->soul,"IsDead",&out->dead);
}
bool engine_same(const Victim& v){
    if(engine::entity_by_id(v.eid)!=v.entity||hits::soul_of_eid(v.eid)!=v.soul)return false;
    return actions::entity_wuid(v.entity)==v.wuid;
}
bool engine_vitals(void* soul,float* hp,float* st){return rttr::soul_state(soul,"health",hp)&&rttr::soul_state(soul,"stamina",st)&&std::isfinite(*hp)&&std::isfinite(*st)&&*hp>=0&&*st>=0;}
bool engine_arranged(uint32_t eid){return hits::scene_owned_eid(eid,"measured_missile_accept");}
uint32_t engine_named(const char* name){return hits::eid_of_name(name);}
bool engine_avatar(uint8_t peer,uint32_t eid){char name[32]{};std::snprintf(name,sizeof name,"kcd2mp_%u",unsigned(peer));return hits::avatar_known(eid)&&hits::eid_of_name(name)==eid;}
bool engine_apply(const uint8_t* body,size_t len,uint8_t* steps){const auto r=hits::apply_attributed_ex(body,len,true,-1.0f,true);*steps=r.steps;return (r.steps&1)!=0;}
Backend defaults(){return {engine_now,engine_main,engine_scope,engine_victim,engine_same,engine_vitals,engine_arranged,engine_named,engine_avatar,engine_apply};}
#endif
void ensure(){
#ifndef KCDMP_P6_TEST
    if(!g_b.now)g_b=defaults();
#endif
}
bool same_scope(const Scope& a,const Scope& b){return a.generation==b.generation&&a.life==b.life&&a.physical==b.physical&&a.world==b.world&&a.role==b.role&&a.actor==b.actor&&a.soul==b.soul&&a.player==b.player&&a.ready==b.ready&&a.alive==b.alive;}
bool refresh(){
    ensure();if(!g_b.main||!g_b.main())return false;
    const Scope next=g_b.scope();const uint64_t conn=g_connection.load();
    if(!same_scope(next,g_scope)||conn!=g_lastConnection){
        g_contacts.clear();
        if(next.physical!=g_scope.physical||conn!=g_lastConnection)g_blocks.clear();
        g_scope=next;g_lastConnection=conn;
        if(++g_epoch==0)++g_epoch;
    }
    g_player.store(next.player);g_physical.store(next.physical);
    return g_connected.load()&&next.ready;
}
Block* block_candidate(uint32_t eid,void* combat,uint64_t now){
    for(auto& b:g_blocks)if(b.eid==eid&&b.combat==combat&&b.physical==g_scope.physical)return &b;
    g_blocks.erase(std::remove_if(g_blocks.begin(),g_blocks.end(),[&](const Block& b){return now>b.until;}),g_blocks.end());
    if(g_blocks.size()>=512)return nullptr; // bounded; no game-object reads on the producer thread
    Block b;b.eid=eid;b.combat=combat;b.physical=g_scope.physical;b.until=now+660;
    g_blocks.push_back(b);return &g_blocks.back();
}
void bind_block(Block* b,const Victim& v){if(b){b->soul=v.soul;b->guid=v.guid;}}
void ambiguous(uint32_t victim){for(auto& c:g_contacts)if(c.victim.eid==victim)c.measure.ambiguous=true;}
void emit(const Contact& c,float hp,float st,bool died){
    if(!g_event)return;
    uint8_t b[61]{};std::memcpy(b,c.victim.guid.data(),16);std::memcpy(b+16,&st,4);std::memcpy(b+20,&hp,4);
    b[24]=died?1:0;b[25]=1;b[27]=1;b[28]=7;put64(b+29,c.id);put64(b+37,c.scope.generation);put64(b+45,c.epoch);put64(b+53,c.measure.captured);
    g_event(b,sizeof b);
    // The sampler consumes exactly these owned amounts, preserving any later
    // independent melee remainder instead of suppressing a fixed660ms window.
    if(auto* owned=block_candidate(c.victim.eid,c.victim.combat,g_b.now())){bind_block(owned,c.victim);owned->complete=true;owned->hp=hp;owned->st=st;}
}
}
void set_backend(const Backend& b){g_b=b;}
void set_event(EventFn fn){g_event=fn;}
void connection(bool on){g_connected=on;g_connection.fetch_add(1);}
uint64_t capture_epoch(){return refresh()&&g_scope.alive?g_epoch:0;}
bool contact(bool missile,uint32_t attacker,uint32_t victim,void* combat,bool knownLocal){
    ensure();const bool candidate=missile&&(knownLocal||(attacker&&attacker==g_player.load()));
    if(!g_b.main||!g_b.main()){
        std::lock_guard<std::mutex> lk(g_noticeLock);
        if(g_notices.size()<32)g_notices.push_back({missile,candidate,attacker,victim,combat,g_physical.load(),g_connection.load()});else g_noticeOverflow=true;
        return candidate; // observer only; no off-main RTTR, never fall back to a melee mark
    }
    const bool ready=refresh();const bool local=missile&&(knownLocal||(attacker&&attacker==g_scope.player));
    ambiguous(victim);
    if(!local)return false;
    const uint64_t now=g_b.now();block_candidate(victim,combat,now);
    const Scope startScope=g_scope;const uint64_t startEpoch=g_epoch,startConnection=g_connection.load();
    Victim v;
    if(!g_b.victim(victim,&v)){refused_once(0,"victim_identity_unreadable");return true;}
    if(v.avatar||victim==g_scope.player){
        g_blocks.erase(std::remove_if(g_blocks.begin(),g_blocks.end(),[&](const Block& b){return b.eid==victim&&b.combat==combat;}),g_blocks.end());
        return false; // existing player/Avatar PvP path owns these contacts
    }
    refresh();
    if(startScope.physical!=g_scope.physical||startConnection!=g_connection.load())return true;
    bind_block(block_candidate(victim,combat,now),v);
    if(!ready||!g_scope.alive||(g_scope.role!=1&&g_scope.role!=2)||v.dead||v.combat!=combat){refused_once(1,"scope_physics_life_or_combat_identity");return true;}
    for(const auto& c:g_contacts)if(c.victim.eid==victim)return true;
    float hp=0,st=0;
    if(g_contacts.size()>=64||!g_b.vitals(v.soul,&hp,&st)){refused_once(2,"measurement_unavailable_or_capacity");return true;}
    if(!refresh()||!same_scope(startScope,g_scope)||startEpoch!=g_epoch||startConnection!=g_connection.load()||!g_b.same(v)){refused_once(3,"scope_changed_during_baseline");return true;}
    Contact c;c.victim=v;c.scope=startScope;c.epoch=startEpoch;c.id=++g_id;if(!c.id)c.id=++g_id;
    c.measure.captured=now;c.measure.hp0=hp;c.measure.st0=st;g_contacts.push_back(c);
    return true;
}
void tick(){
    if(!refresh())return;
    const uint64_t now=g_b.now();
    std::vector<Notice> notices;bool overflow=false;
    {std::lock_guard<std::mutex> lk(g_noticeLock);notices.swap(g_notices);overflow=g_noticeOverflow;g_noticeOverflow=false;}
    if(overflow)for(auto& c:g_contacts)c.measure.ambiguous=true;
    for(const auto& n:notices){
        if(n.physical!=g_scope.physical||n.connection!=g_connection.load())continue;
        ambiguous(n.victim);
        if(n.missile&&(n.local||n.attacker==g_scope.player)){
            block_candidate(n.victim,n.combat,now);Victim v;
            if(g_b.victim(n.victim,&v)){
                if(v.avatar||v.eid==g_scope.player)g_blocks.erase(std::remove_if(g_blocks.begin(),g_blocks.end(),[&](const Block& b){return b.eid==n.victim&&b.combat==n.combat;}),g_blocks.end());
                else bind_block(block_candidate(n.victim,n.combat,now),v);
            }
        }
    }
    for(size_t i=0;i<g_contacts.size();){
        const Contact before=g_contacts[i];float hp=0,st=0,dh=0,ds=0;
        const bool identity=same_scope(before.scope,g_scope)&&before.epoch==g_epoch&&g_b.same(before.victim);
        const bool read=identity&&g_b.vitals(before.victim.soul,&hp,&st);
        // A getter may cross a Load/life/connection edge. Refresh can clear the
        // vector, so never keep a reference across an engine call.
        if(!refresh()||before.epoch!=g_epoch||!same_scope(before.scope,g_scope))return;
        auto live=std::find_if(g_contacts.begin(),g_contacts.end(),[&](const Contact& c){return c.id==before.id;});
        if(live==g_contacts.end())return;
        const bool stillSame=identity&&g_b.same(before.victim);
        const int result=live->measure.sample(g_b.now(),stillSame,read,hp,st,&dh,&ds);
        if(!result){++i;continue;}
        Contact finished=*live;g_contacts.erase(live);
        if(result==1){
            Victim after;const bool finalIdentity=g_b.victim(finished.victim.eid,&after)&&after.eid==finished.victim.eid&&after.entity==finished.victim.entity&&after.soul==finished.victim.soul&&after.wuid==finished.victim.wuid&&after.combat==finished.victim.combat&&after.guid==finished.victim.guid&&g_b.same(finished.victim);
            if(!finalIdentity||!refresh()||finished.epoch!=g_epoch||!same_scope(finished.scope,g_scope)||g_b.now()<finished.measure.captured||g_b.now()-finished.measure.captured>kContactMs)return;
            emit(finished,dh,ds,finished.scope.role==1&&after.dead);
            logf("P6-MISSILE contact_measured id=%llu hp=%.3f st=%.3f source=hook_window role=%u",static_cast<unsigned long long>(finished.id),dh,ds,finished.scope.role);
        }
    }
    g_blocks.erase(std::remove_if(g_blocks.begin(),g_blocks.end(),[&](const Block& b){return b.physical!=g_scope.physical||now>b.until;}),g_blocks.end());
}
bool filter_sample(void* soul,const unsigned char guid[16],float* hp,float* st){
    ensure();if(!g_b.main||!g_b.main()||!guid)return false;
    refresh();const uint64_t now=g_b.now();
    for(size_t i=0;i<g_blocks.size();++i){
        const Block before=g_blocks[i];
        if(before.physical!=g_scope.physical||now>before.until)continue;
        if(!before.soul){
            Victim v;if(!g_b.victim(before.eid,&v)||v.combat!=before.combat)continue;
            refresh();if(before.physical!=g_scope.physical)return false;
            auto found=std::find_if(g_blocks.begin(),g_blocks.end(),[&](const Block& item){return item.eid==before.eid&&item.combat==before.combat&&item.physical==before.physical;});
            if(found==g_blocks.end())return false;
            i=static_cast<size_t>(found-g_blocks.begin());bind_block(&*found,v);
        }
        Block& b=g_blocks[i];
        if(b.soul!=soul||std::memcmp(b.guid.data(),guid,16)!=0)continue;
        if(!hp||!st)return true; // read-only observation used by diagnostics/tests
        if(b.complete){
            const float usedHp=(std::min)(*hp,b.hp),usedSt=(std::min)(*st,b.st);
            *hp-=usedHp;*st-=usedSt;b.hp-=usedHp;b.st-=usedSt;
            if(b.hp<=0.01f&&b.st<=0.25f)g_blocks.erase(g_blocks.begin()+static_cast<std::ptrdiff_t>(i));
            return *hp<=0.01f&&*st<=0.25f;
        }
        // Unmeasurable/competing contact: acknowledge one inseparable sample,
        // then stop holding later independent hits. No fallback to invented melee.
        ambiguous(b.eid);*hp=0;*st=0;
        g_blocks.erase(g_blocks.begin()+static_cast<std::ptrdiff_t>(i));return true;
    }
    return false;
}
bool suppress_sample(void* soul,const unsigned char guid[16]){return filter_sample(soul,guid,nullptr,nullptr);}
void remote_follow_eid(uint32_t eid){for(auto& c:g_contacts)if(c.victim.eid==eid)c.measure.ambiguous=true;}
void remote_follow(const unsigned char guid[16]){if(!guid)return;for(auto& c:g_contacts)if(std::memcmp(c.victim.guid.data(),guid,16)==0)c.measure.ambiguous=true;}
uint8_t measured_apply(const uint8_t* b,size_t len,uint8_t* out,size_t cap,size_t* used){
    *used=0;ensure();
    if(!b||len<89||cap<40||b[0]!=18||b[8]!=0x80||b[1]!=255||b[2]!=255||b[3]!=0)return wo163::kRBadRequest;
    const uint8_t* ab=b+9;const size_t an=30+ab[29];
    if(len!=9+an+50||ab[29]==0||ab[29]>63)return wo163::kRBadRequest;
    const uint8_t* t=ab+an;
    if(std::memcmp(t,"P6M1",4)!=0)return wo163::kRBadRequest;
    const Key key{u32(t+6),t[4],u64(t+18),u64(t+26),u64(t+34)};
    const uint64_t hostGen=u64(t+10),deadline=u64(t+42);
    float hp=0,st=0,strength=0;std::memcpy(&st,ab+16,4);std::memcpy(&hp,ab+20,4);std::memcpy(&strength,b+4,4);
    Ack result=ack(key,Status::Invalid);
    if(!key.valid()){std::memcpy(out,result.data(),40);*used=40;return wo163::kROk;}
    std::vector<uint8_t> fact(ab,ab+an);fact.push_back(t[5]);
    if(Entry* old=g_journal.find(key)){
        result=old->fact==fact?old->result:ack(key,Status::Conflict);
        std::memcpy(out,result.data(),40);*used=40;return wo163::kROk;
    }
    Entry* entry=g_journal.reserve(key,fact,deadline);
    if(!entry){result=ack(key,Status::Capacity);std::memcpy(out,result.data(),40);*used=40;return wo163::kROk;}
    g_journal.begin(*entry); // final uncertain BEFORE any engine side effect
    Status status=Status::Invalid;uint8_t steps=0;
    const bool validFact=t[5]==7&&strength==0&&measured(hp,st)&&(ab[24]&0x04)!=0&&(ab[24]&0x0A)==0;
    if(validFact)status=Status::Scope;
    if(validFact&&refresh()&&g_scope.role==1&&key.world==g_scope.world&&hostGen==g_scope.generation){
        const Scope startScope=g_scope;const uint64_t startConnection=g_connection.load();
        status=Status::Expired;
        if(deadline_valid(g_b.now(),entry->deadline)){
            status=Status::Victim;
            char name[64]{};std::memcpy(name,ab+30,ab[29]);
            bool validName=true;for(size_t i=0;i<ab[29];++i){const char c=name[i];if(!((c>='a'&&c<='z')||(c>='A'&&c<='Z')||(c>='0'&&c<='9')||c=='_'))validName=false;}
            Victim v;const uint32_t eid=validName?g_b.named(name):0;const uint32_t avatar=u32(ab+25);
            if(eid&&eid!=g_scope.player&&g_b.victim(eid,&v)&&!v.avatar&&!v.dead&&std::memcmp(v.guid.data(),ab,16)==0&&g_b.avatar(key.peer,avatar)){
                status=Status::Arranged;
                if(!g_b.arranged(eid)){
                    float currentHp=0,currentSt=0;
                    if(!g_b.vitals(v.soul,&currentHp,&currentSt))status=Status::Victim;
                    else {
                        Victim current;
                        if(!g_b.victim(eid,&current)||current.dead||current.avatar||current.entity!=v.entity||current.soul!=v.soul||current.combat!=v.combat||current.wuid!=v.wuid||current.guid!=v.guid)status=Status::Victim;
                        else if(!refresh()||!same_scope(startScope,g_scope)||startConnection!=g_connection.load()||g_scope.role!=1||g_scope.generation!=hostGen||g_scope.world!=key.world||!g_b.same(v))status=Status::Scope;
                        else if(!deadline_valid(g_b.now(),entry->deadline))status=Status::Expired;
                        else {status=g_b.apply(ab,an,&steps)?Status::Accepted:Status::Uncertain;}
                    }
                }
            }
        }
    }
    entry->result=ack(key,status,steps,status==Status::Accepted?hp:0,status==Status::Accepted?st:0);
    logf("P6-MISSILE host mode=measured_missile_accept status=%u peer=%u id=%llu engine_missile_rejudge=0",unsigned(status),key.peer,static_cast<unsigned long long>(key.id));
    std::memcpy(out,entry->result.data(),40);*used=40;return wo163::kROk;
}
#ifdef KCDMP_P6_TEST
void reset_for_tests(){g_refusalLogged=0;g_journal={};g_contacts.clear();g_blocks.clear();g_scope={};g_epoch=g_id=0;g_lastConnection=0;g_notices.clear();g_noticeOverflow=false;g_connected=true;g_connection=1;}
#endif
} // namespace kcdmp::p6
