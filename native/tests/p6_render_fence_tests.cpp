// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// The actual incremental reader, pumped wait, batch parser and final invoke gate.
#include "p6_render_rules.h"
#include "pipe_control_reader.h"
#include "wo218_rules.h"
#include <cstdio>
#include <string>
#include <vector>
namespace {
int good=0,bad=0;
#define RCHECK(c,label) do{if(c)++good;else{++bad;std::printf("FAIL P6 render fence line %d: %s\n",__LINE__,label);}}while(false)
namespace pc=kcdmp::pipecontrol;namespace render=kcdmp::p6render;namespace batch=kcdmp::wo218rules;
struct Context{uint64_t now=1000,gen=7,life=900;bool gameplay=true;};
std::vector<uint8_t> shot(const char* fragment="Fire",const char* tags="l_bow",const char* name="kcd2mp_1"){
    std::vector<uint8_t> b{4};for(const char* s:{name,fragment,tags}){const size_t n=std::strlen(s);b.push_back(uint8_t(n));b.insert(b.end(),s,s+n);}b.insert(b.end(),9,0);return b;
}
std::vector<uint8_t> wire(uint8_t type,const std::vector<uint8_t>& b){std::vector<uint8_t> p{type,uint8_t(b.size()),uint8_t(b.size()>>8)};p.insert(p.end(),b.begin(),b.end());return p;}
void stamp(pc::Frame& f,const Context& c){f.readerTick=c.now;f.readerGeneration=c.gen;f.readerLife=c.life;f.readerGameplay=c.gameplay;}
render::Stamp proof(const pc::Frame& f){return {f.readerGeneration,f.readerLife,f.readerTick,f.readerGameplay};}
bool invoke(const pc::Frame& f,const Context& c,int* calls){return render::invoke(f.body.data(),f.body.size(),proof(f),c.now,c.gen,c.life,c.gameplay,[&]{++*calls;});}
pc::Frame decode(const std::vector<uint8_t>& body,const Context& c){pc::Reader r;auto p=wire(0x28,body);const bool fed=r.feed(p.data(),p.size(),[](const pc::Frame&){return true;},[&](pc::Frame& f){stamp(f,c);});pc::Frame f;RCHECK(fed&&r.pop(&f),"actual Reader accepts the complete normal frame");return f;}
}
int p6_render_fence_tests(int* passed){
    good=bad=0;const auto bow=shot(),crossbow=shot("Uncharge","medium+l_crossbow");
    RCHECK(render::ranged_shot(bow.data(),bow.size())&&render::ranged_shot(crossbow.data(),crossbow.size()),"only actual avatar shooting fragments with exact ranged tag components are selected");
    for(const char* fragment:{"Fire","Load","Uncharge"})for(const char* tags:{"l_bow","l_crossbow","medium+l_bow+charged","l_crossbow+medium+goatsfoot"}){
        const auto b=shot(fragment,tags);RCHECK(render::ranged_shot(b.data(),b.size()),"all existing ranged one-shot forms share the final gate");}
    for(const auto& b:{shot("Fire","my_l_bow"),shot("Fire","l_bowExtra"),shot("Fire","r_l_crossbow"),shot("Wave","l_bow"),shot("Fire","l_bow","Dude"),shot("Fire","l_bow","probe_npc")}){
        int calls=0;const render::Stamp absent{};
        RCHECK(!render::ranged_shot(b.data(),b.size())&&render::invoke(b.data(),b.size(),absent,9999,0,0,false,[&]{++calls;})&&calls==1,"unrelated probes/fragments and substring tag collisions retain their existing path");}
    RCHECK(!render::ranged_shot(nullptr,0),"missing body is not misclassified as a ranged action");
    for(size_t n=0;n<bow.size();++n)RCHECK(!render::ranged_shot(bow.data(),n),"truncated one-shot is never parsed past its supplied body");

    Context fresh;auto frame=decode(bow,fresh);int calls=0;
    RCHECK(invoke(frame,fresh,&calls)&&calls==1,"fresh same-generation/life gameplay frame invokes exactly once");
    Context exact=fresh;exact.now+=2000;calls=0;RCHECK(invoke(frame,exact,&calls)&&calls==1,"exact2000ms original reader age remains the accepted boundary");
    for(unsigned stale=0;stale<8;++stale){Context c=fresh;auto old=frame;
        if(stale==0)++c.gen;if(stale==1)++c.life;if(stale==2)c.now+=2001;if(stale==3)c.gameplay=false;
        if(stale==4)old.readerGameplay=false;if(stale==5)old.readerGeneration=0;if(stale==6)c.now=old.readerTick-1;if(stale==7){old.readerLife=old.readerTick;c.life=old.readerTick;}
        calls=0;RCHECK(!invoke(old,c,&calls)&&calls==0,"generation/life/old/future/pending/unknown/equal-edge frame never reaches actual invoke");}
    Context initial;initial.life=0;auto noEdge=decode(bow,initial);calls=0;RCHECK(invoke(noEdge,initial,&calls)&&calls==1,"initial life0 needs no invented life edge but does require real gameplay");

    // First byte arrives under one life; completing header/body after a load cannot make it fresh.
    const auto packet=wire(0x28,bow);
    for(size_t split=1;split<packet.size();++split){pc::Reader r;Context c;unsigned stamps=0;auto tag=[&](pc::Frame& f){++stamps;stamp(f,c);};
        RCHECK(r.feed(packet.data(),split,[](const pc::Frame&){return true;},tag),"every partial header/body prefix is consumed by the actual Reader");
        c.now+=100;++c.life;
        RCHECK(r.feed(packet.data()+split,packet.size()-split,[](const pc::Frame&){return true;},tag),"every remaining header/body suffix completes under the new life");
        pc::Frame f;calls=0;RCHECK(r.pop(&f)&&stamps==1&&f.readerTick==1000&&f.readerLife==900&&!invoke(f,c,&calls)&&calls==0,"partial completion preserves original first-byte proof and refuses the old life");}

    // Real pumped wait receives complete/partial normal work while a Main task stays unfinished.
    {
        pc::Reader r;Context c;bool done=false;unsigned pumps=0,controls=0;std::mutex mutex;std::condition_variable cv;std::unique_lock<std::mutex> lock(mutex);
        const auto normal=wire(0x28,bow);std::vector<uint8_t> control(kcdmp::wo216authority::kRequestBytes,0);control[0]=kcdmp::wo216authority::kOp;
        const auto ctl=wire(pc::kGeneric,control);
        const auto status=pc::wait_pumped(cv,lock,std::chrono::milliseconds(25),[&]{return done;},[&]{
            ++pumps;if(pumps==1)return r.feed(normal.data(),1,[&](const pc::Frame&){++controls;return true;},[&](pc::Frame& f){stamp(f,c);});
            if(pumps==2){c.now+=2001;return r.feed(normal.data()+1,normal.size()-1,[&](const pc::Frame&){++controls;return true;},[&](pc::Frame& f){stamp(f,c);});}
            if(pumps==3)return r.feed(ctl.data(),ctl.size(),[&](const pc::Frame&){++controls;return true;},[&](pc::Frame& f){stamp(f,c);});return true;
        });
        pc::Frame old;calls=0;RCHECK(status==pc::Wait::TimedOut&&!done&&controls==1&&r.pop(&old)&&!invoke(old,c,&calls)&&calls==0,"control pump while Main unfinished never freshens deferred ranged work before its later invoke");
    }
    // A batch is one reader receipt. Later subs/frames inherit its stamp, never their own dispatch time.
    {
        std::vector<uint8_t> b{0x34,0x12,9};for(unsigned i=0;i<9;++i){b.push_back(0x28);b.push_back(uint8_t(bow.size()));b.push_back(0);b.insert(b.end(),bow.begin(),bow.end());}
        pc::Reader r;Context c;auto p=wire(pc::kBatch,b);
        RCHECK(r.feed(p.data(),p.size(),[](const pc::Frame&){return true;},[&](pc::Frame& f){stamp(f,c);}),"real Reader preserves one batch frame's original proof");
        pc::Frame f;r.pop(&f);uint16_t id=0;std::vector<batch::Sub> subs;
        RCHECK(batch::parse_batch(f.body.data(),f.body.size(),&id,&subs)&&id==0x1234&&subs.size()==9,"real batch parser retains all nine original sub-body offsets");
        batch::FrameBudget budget;budget.begin(1);calls=0;size_t at=0;
        while(at<subs.size()&&budget.may_run()){const auto& sub=subs[at++];render::invoke(f.body.data()+sub.off,sub.len,proof(f),c.now,c.gen,c.life,c.gameplay,[&]{++calls;});budget.add(1,1);}
        RCHECK(at==8&&calls==8,"first main frame can invoke its eight fresh original stamped subs");
        c.now+=2001;budget.begin(2);const auto& last=subs[at];
        RCHECK(!render::invoke(f.body.data()+last.off,last.len,proof(f),c.now,c.gen,c.life,c.gameplay,[&]{++calls;})&&calls==8&&f.readerTick==1000,"carried ninth sub expires against batch read time rather than gaining a fresh per-frame deadline");
        ++c.gen;calls=0;for(const auto& sub:subs)render::invoke(f.body.data()+sub.off,sub.len,proof(f),c.now,c.gen,c.life,c.gameplay,[&]{++calls;});
        RCHECK(calls==0,"load generation after batch decode rejects every remaining ranged sub");
    }
    if(passed)*passed=good;return bad;
}
