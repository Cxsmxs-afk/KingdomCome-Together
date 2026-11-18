// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#include "p5_marker.h"
#include "wo216.h"
#include "wo163.h"
#include "wo203.h"
#include <array>
#include <vector>
#include <algorithm>
#include <cstring>
#include <limits>
#ifndef KCDMP_P5_TEST
#include <windows.h>
#include "log.h"
#else
uint64_t kcdmp_p5_test_now();
uint64_t kcdmp_p5_test_generation();
bool kcdmp_p5_test_gameplay();
namespace kcdmp::p5 { void test_trace(const char*, ...); }
#define logf test_trace
#endif
namespace kcdmp::p5 {
namespace {
uint64_t now_ms() {
#ifdef KCDMP_P5_TEST
    return kcdmp_p5_test_now();
#else
    return GetTickCount64();
#endif
}
uint64_t generation(){
#ifdef KCDMP_P5_TEST
    return kcdmp_p5_test_generation();
#else
    return wo216::lifecycle_generation();
#endif
}
bool gameplay(){
#ifdef KCDMP_P5_TEST
    return kcdmp_p5_test_gameplay();
#else
    return wo216::lifecycle_gameplay();
#endif
}
Backend g_backend;
constexpr size_t kHandles=1024,kOwnMax=128,kMirrorMax=384,kRevisionsMax=4096,kPageRows=16;
std::array<Handle,kHandles> g_handles;
uint64_t g_token=0,g_physical=1,g_revision=0,g_added=0,g_fallbacks=0;
uint32_t g_world=0;uint64_t g_resetGeneration=0;
bool g_preparing=false,g_scopeKnown=false;
EventFn g_event=nullptr;
struct Row { Fact fact; Ref mark; Binding source; bool remote=false,owned=false,fallback=false,terminal=false; uint64_t pendingUntil=0;unsigned pendingAttempts=0; };
struct Remote { uint8_t owner=0; Row row; bool removed=false; uint64_t clearedGeneration=0; };
std::vector<Row> g_own;
std::vector<Remote> g_remote;
struct Page { uint32_t token=0,world=0;uint64_t generation=0,until=0;std::vector<Fact> rows; } g_page;
uint32_t g_pageToken=0;
void bump(uint64_t& n){if(n<uint64_t(INT64_MAX))++n;}
Handle* find(uint64_t token){for(auto& h:g_handles)if(h.token==token&&token)return &h;return nullptr;}
Handle* allocate(){for(auto& h:g_handles)if(h.state==State::Empty||h.state==State::Retired){h=Handle{};h.token=++g_token;if(!h.token)h.token=++g_token;return &h;}return nullptr;}
void emit(uint8_t action,const Fact& f){
    if(!g_event||!g_world)return;
    uint8_t b[43]{};b[0]=1;b[1]=action;b[2]=f.kind;put64(b+3,generation());put32(b+11,g_world);
    put64(b+15,f.id);put64(b+23,f.revision);std::memcpy(b+31,f.xyz,12);g_event(b,sizeof b);
}
bool release_row(Row& r){
    if(!render_remove(&r.mark))return false;
    if(r.owned&&r.source.entity&&source_alive(r.source)&&!release_anchor(r.source))return false;
    r.source=Binding{};r.owned=r.fallback=r.terminal=false;r.pendingUntil=0;r.pendingAttempts=0;return true;
}
bool pending_identity(Row& r){
    if(!r.pendingUntil)r.pendingUntil=now_ms()+2000;
    if(now_ms()>=r.pendingUntil||++r.pendingAttempts>20){
        logf("P5-MARKER pending identity expired id=%llu attempts=%u",static_cast<unsigned long long>(r.fact.id),r.pendingAttempts);
        if(!r.owned&&r.source.entity&&render_remove(&r.mark)){
            // An original grave can have no usable linkable. After a bounded
            // normal-ready wait, try one separate plain holder on the next tick.
            r.source={};r.pendingUntil=0;r.pendingAttempts=0;
        }else r.terminal=true;
        return false;
    }
    return true;
}
void render_row(Row& r){
    if(g_preparing||!g_scopeKnown||r.terminal||!gameplay())return;
    if(r.mark.token){
        Handle* h=find(r.mark.token);
        if(h&&h->state==State::Added){
            // Query the current EID table before touching an anchor. Holding a
            // C++ record does not keep an Engine-owned NO_SAVE entity alive.
            if(source_alive(h->binding))return;
            if(!render_remove(&r.mark)){r.terminal=true;return;}
            r.source={};r.owned=false;r.fallback=false;
        }
        if(h&&h->state==State::Quarantined){
            if(h->mayReference||!render_remove(&r.mark)){r.terminal=true;return;}
            if(!r.owned)r.source={}; // failed non-linkable grave may use plain fallback
        }
    }
    if(!g_backend.ready||!g_backend.ready())return;
    if(r.source.entity&&!r.source.wuid){
        if(!pending_identity(r))return;
        if(!source_identity(r.source.entity,&r.source)){
            if(!r.source.pending)r.terminal=true;
            return;
        }
    }
    if(r.source.entity&&source_alive(r.source)){
        if(render_add(r.source.entity,&r.mark,0x2B,0,nullptr,true))return;
        Handle* h=find(r.mark.token);
        if(h&&h->state==State::Waiting){pending_identity(r);return;}
    }
    if(!r.fallback){
        if(!render_remove(&r.mark)){r.terminal=true;return;}
        void* e=fallback_anchor(r.fact.id,r.fact.xyz,r.remote&&r.fact.kind==0);Binding b;
        const bool identified=e&&source_identity(e,&b);
        if(!e||(!identified&&!b.pending)){r.terminal=true;return;}
        r.source=b;r.owned=true;r.fallback=true;bump(g_fallbacks);
        if(!identified){pending_identity(r);return;}
        if(!render_add(e,&r.mark,0x2B,0,nullptr,true)){
            Handle* h=find(r.mark.token);
            if(h&&h->state==State::Waiting)pending_identity(r);
            else r.terminal=true;
        }
    }else r.terminal=true;
}
bool request_fence(uint64_t gen){return gen&&gen==generation();}
[[maybe_unused]] uint8_t list_op(uint8_t,const uint8_t* b,size_t n,uint8_t* o,size_t cap,size_t* used){return list(b,n,o,cap,used);}
[[maybe_unused]] uint8_t command_op(uint8_t,const uint8_t* b,size_t n,uint8_t* o,size_t cap,size_t* used){return command(b,n,o,cap,used);}
#ifndef KCDMP_P5_TEST
struct Register{Register(){wo203::reg_op(252,list_op);wo203::reg_op(253,command_op);}}g_register;
#endif
} // namespace
void set_backend(const Backend& b){g_backend=b;}
void set_event(EventFn fn){g_event=fn;}
bool suspended(){return g_preparing;}
void set_suspended(bool on){g_preparing=on;}
uint64_t physical_world(){return g_physical;}
bool loaded_scope(uint32_t* world){if(world)*world=g_world;return g_scopeKnown&&!g_preparing;}
void counts(uint64_t out[3]){if(out){out[0]=g_added;out[1]=g_fallbacks;out[2]=0;}}
bool render_add(void* entity,Ref* out,int type,unsigned picture,const char* label,bool death){
    if(!out||g_preparing||!g_scopeKnown||!gameplay()||!g_backend.ready||!g_backend.ready())return false;
    Handle* h=out->token?find(out->token):nullptr;
    if(!h&&!out->token)for(auto& existing:g_handles)if(existing.state==State::Added&&existing.binding.entity==entity&&existing.binding.physical==g_physical&&existing.type==type&&existing.picture==picture&&g_backend.contains(existing.binding,existing.mark)==Presence::Present){h=&existing;break;}
    if(!h)h=allocate();
    if(!h)return false;
    h->type=type;h->picture=picture;h->death=death;
    out->token=h->token;
    if(h->state==State::Added){out->p=h->mark.p;out->ctrl=h->mark.ctrl;return true;}
    if(h->state!=State::Empty&&h->state!=State::Waiting)return false;
    const bool ok=add_handle(*h,g_backend,entity,Spec{type,picture,label});
    if(ok){out->p=h->mark.p;out->ctrl=h->mark.ctrl;if(h->death)bump(g_added);}
    else logf("P5-MARKER skipped token=%llu state=%u map_reference_unknown=%d",static_cast<unsigned long long>(h->token),unsigned(h->state),h->mayReference?1:0);
    return ok;
}
bool render_remove(Ref* ref){
    if(!ref)return true;
    Handle* h=find(ref->token);
    if(!h){*ref={};return true;}
    if(!remove_handle(*h,g_backend)){logf("P5-MARKER quarantine token=%llu cleanup=unconfirmed anchor_delete=forbidden engine_retained=unproven",static_cast<unsigned long long>(h->token));return false;}
    *ref={};return true;
}
bool may_delete_source(void* entity){for(const auto& h:g_handles)if(h.binding.entity==entity&&h.mayReference&&h.state!=State::Retired&&h.state!=State::Empty)return false;return true;}
bool detach_all(){
    bool safe=true;
    for(auto& h:g_handles)if(h.state!=State::Empty&&h.state!=State::Retired&&!remove_handle(h,g_backend))safe=false;
    return safe;
}
void physical_world_changed(){
    // Engine may already have purged NO_SAVE entities. Quarantine cannot retain
    // them. Only the validated Remove path may touch old map marks, never old sources.
    const bool detached=detach_all();
    ++g_physical;g_resetGeneration=g_preparing?0:generation();g_world=0;g_scopeKnown=false;g_preparing=true;
    g_own.clear();g_remote.clear();g_page={};
    logf("P5-WORLD changed physical=%llu old_marks_detached=%d uncontrolled_load_safety=unverified",static_cast<unsigned long long>(g_physical),detached?1:0);
}
void own_death(uint64_t id,bool grave,void* source,const float xyz[3],Ref* existing){
    if(!id||!finite3(xyz))return;
    for(const auto& r:g_own)if(r.fact.id==id)return;
    if(g_own.size()>=kOwnMax){
        // Saved loot graves (<=64) are never evicted. Rotate the oldest plain
        // death-location fact; quarantined renderer ownership stays in Handles.
        auto old=std::find_if(g_own.begin(),g_own.end(),[](const Row& item){return item.fact.kind==1;});
        if(old==g_own.end()){logf("P5-MARKER invalid capacity: only loot graves, refusing inventory deletion");return;}
        const bool detached=release_row(*old);old->fact.revision=++g_revision;emit(0,old->fact);
        logf("P5-MARKER oldest plain location retired capacity=128 detached=%d",detached?1:0);
        g_own.erase(old);
    }
    Row r;r.fact.kind=grave?0:1;r.fact.id=id;r.fact.revision=++g_revision;std::memcpy(r.fact.xyz,xyz,12);
    if(source)source_identity(source,&r.source);
    if(existing)r.mark=*existing;
    g_own.push_back(r);render_row(g_own.back());emit(1,r.fact);
}
void own_grave_loaded(uint64_t id,void* entity,const float xyz[3],Ref* existing){own_death(id,true,entity,xyz,existing);}
bool own_removed(uint64_t id){
    for(size_t i=0;i<g_own.size();++i){
        if(g_own[i].fact.id!=id)continue;
        auto& r=g_own[i];
        if(!release_row(r))return false;
        r.fact.revision=++g_revision;emit(0,r.fact);
        g_own.erase(g_own.begin()+static_cast<std::ptrdiff_t>(i));
        return true;
    }
    return true;
}
void tick(){
    if(g_preparing||!g_scopeKnown||!gameplay())return;
    for(auto& r:g_own)render_row(r);
    for(auto& r:g_remote)if(!r.removed&&!r.clearedGeneration)render_row(r.row);
}
uint8_t list(const uint8_t* b,size_t n,uint8_t* out,size_t cap,size_t* used){
    *used=0;if(!b||n!=20||b[0]!=252||b[1]!=1||cap<21)return wo163::kRBadRequest;
    const uint64_t gen=u64(b+2);const uint32_t world=u32(b+10),token=u32(b+14);const uint16_t cursor=uint16_t(b[18]|b[19]<<8);
    poll_world();
    if(!request_fence(gen)||!g_scopeKnown||world!=g_world||g_preparing||!gameplay())return wo163::kRFailed;
    if(!token&&!cursor){g_page={};g_page.token=++g_pageToken;if(!g_page.token)g_page.token=++g_pageToken;g_page.world=world;g_page.generation=gen;g_page.until=now_ms()+5000;for(const auto& r:g_own)g_page.rows.push_back(r.fact);}
    else if(token!=g_page.token||world!=g_page.world||gen!=g_page.generation||now_ms()>g_page.until)return wo163::kRBadRequest;
    if(cursor>g_page.rows.size())return wo163::kRBadRequest;
    const size_t count=(std::min)({kPageRows,g_page.rows.size()-cursor,(cap-21)/29});
    out[0]=1;put64(out+1,gen);put32(out+9,world);put32(out+13,g_page.token);
    const size_t next=cursor+count;out[17]=uint8_t(next);out[18]=uint8_t(next>>8);out[19]=uint8_t(count);out[20]=next==g_page.rows.size()?1:0;
    for(size_t i=0;i<count;++i){const auto& f=g_page.rows[cursor+i];uint8_t* p=out+21+i*29;p[0]=f.kind;put64(p+1,f.id);put64(p+9,f.revision);std::memcpy(p+17,f.xyz,12);}
    *used=21+count*29;return wo163::kROk;
}
uint8_t command(const uint8_t* b,size_t n,uint8_t* out,size_t cap,size_t* used){
    *used=0;if(!b||n!=45||b[0]!=253||b[1]!=1||cap<16)return wo163::kRBadRequest;
    const uint8_t action=b[2],kind=b[3],owner=b[4];const uint64_t gen=u64(b+5),id=u64(b+17),revision=u64(b+25);const uint32_t world=u32(b+13);float xyz[3]{};std::memcpy(xyz,b+33,12);
    uint8_t status=0;
    poll_world();
    if(action>4||kind>1||!finite3(xyz))status=4;
    else if(!request_fence(gen))status=2;
    else if(action==4){
        if(owner||id||revision||(g_world&&world!=g_world))status=2;
        else {g_preparing=true;if(!detach_all())status=3;}
    }else if(action==3){
        if(owner||id||revision||(!world&&!g_preparing&&(!g_scopeKnown||g_world)))status=2;
        else if(!gameplay()||(g_resetGeneration&&gen<=g_resetGeneration))status=1;
        else if(g_world&&g_world!=world)status=2;
        else {g_world=world;g_scopeKnown=true;g_preparing=false;g_resetGeneration=0;for(auto& r:g_own){if(r.mark.token){Handle* h=find(r.mark.token);if(!h||h->state==State::Retired)r.mark={};}render_row(r);emit(1,r.fact);}}
    }else if(!g_scopeKnown||!world||world!=g_world||g_preparing||!gameplay())status=2;
    else if((owner>16&&!(action==2&&owner==255))||(!id&&action!=2)||(!revision&&action!=2))status=4;
    else {
        auto it=std::find_if(g_remote.begin(),g_remote.end(),[&](const Remote& r){return r.owner==owner&&r.row.fact.id==id;});
        if(action==2){
            for(auto& r:g_remote)if((r.owner==owner||owner==255)&&!r.removed){
                if(!release_row(r.row))status=3;
                else r.clearedGeneration=gen; // local renderer cleanup, not a game-fact tombstone
            }
        }
        else if(it!=g_remote.end()&&revision<it->row.fact.revision){} // tombstone wins
        else if(it!=g_remote.end()&&revision==it->row.fact.revision){
            const auto& f=it->row.fact;
            if(it->removed!=(action==0)||f.kind!=kind||std::memcmp(f.xyz,xyz,12)!=0)status=4;
            else if(action==1&&it->clearedGeneration){
                const size_t active=static_cast<size_t>(std::count_if(g_remote.begin(),g_remote.end(),[](const Remote& r){return !r.removed&&!r.clearedGeneration;}));
                if(active>=kMirrorMax)status=1;
                else{it->clearedGeneration=0;it->row.terminal=false;render_row(it->row);}
            }
        }
        else {
            const bool wasActive=it!=g_remote.end()&&!it->removed&&!it->clearedGeneration;
            if(action==1&&!wasActive){
                const size_t active=static_cast<size_t>(std::count_if(g_remote.begin(),g_remote.end(),[](const Remote& r){return !r.removed&&!r.clearedGeneration;}));
                if(active>=kMirrorMax)status=1;
            }
            // Tombstones retain revisions independently of the active render
            // cap. The bounded 4096-entry journal matches the peer protocol.
            if(!status&&it==g_remote.end()){
                if(g_remote.size()>=kRevisionsMax)status=1;
                else{g_remote.push_back(Remote{});it=g_remote.end()-1;it->owner=owner;}
            }
            if(!status){if(!release_row(it->row))status=3;else{it->row={};it->row.remote=true;it->row.fact.kind=kind;it->row.fact.id=id;it->row.fact.revision=revision;std::memcpy(it->row.fact.xyz,xyz,12);it->removed=action==0;it->clearedGeneration=0;if(action==1)render_row(it->row);}}
        }
    }
    out[0]=1;out[1]=action;out[2]=status;out[3]=0;put64(out+4,generation());put32(out+12,g_world);*used=16;
    return status==0?wo163::kROk:status==4?wo163::kRBadRequest:wo163::kRFailed;
}
} // namespace kcdmp::p5
