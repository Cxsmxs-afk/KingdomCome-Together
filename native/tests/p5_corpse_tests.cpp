// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#include "p5_corpse_rules.h"
#include <cstdio>
#include <vector>
int p5_corpse_tests(int* passed) {
    using namespace kcdmp::p5corpse;
    int pass=0, fail=0;
#define CHECK5(c) do {if(c)++pass;else{++fail;std::printf("FAIL P5 corpse line %d\n",__LINE__);}}while(false)
    Identity id{0x10000,0x20000,3,7}; State body; uint64_t blocked=0; std::vector<bool> writes;
    auto hide = [&] { return kcdmp::p5corpse::hide(body,[&]{writes.push_back(true);},blocked); };
    body.observe(id,true,false,true,false); CHECK5(hide() && writes.size()==1 && blocked==0);
    // Alive census intent waits, then actual death arrives immediately before Hide.
    body.observe(id,true,true,true,true); CHECK5(!hide() && writes.size()==1 && blocked==1);
    CHECK5(body.needs_show() && !body.visible);
    writes.push_back(false); body.observe(id,true,true,true,false);
    CHECK5(body.visible && body.needs_show());
    CHECK5(!hide() && writes.size()==2 && !writes.back() && blocked==2);
    // A failed life read retains the same known-dead fact, never makes an alive body.
    body.observe(id,false,false,true,false); CHECK5(body.confirmed && !body.visible && !hide() && blocked==3);
    body.observe(id,true,true,false,false); CHECK5(!body.visible && !hide());
    // The exact same instance actually alive is a different observed life.
    body.observe(id,true,false,true,false); CHECK5(!body.confirmed && !body.visible && hide());
    for(unsigned field=0;field<4;++field){
        State s; s.observe(id,true,true,true,false); auto other=id;
        if(field==0)++other.entity; if(field==1)++other.soul; if(field==2)++other.wuid; if(field==3)++other.world;
        s.observe(other,false,false,true,false); CHECK5(!s.confirmed && !s.visible && s.refuses_hide());
        s.observe(other,true,false,true,false); CHECK5(!s.confirmed && !s.refuses_hide());
    }
    for(unsigned bits=0;bits<8;++bits){
        State s; const bool known=(bits&1)!=0, dead=(bits&2)!=0, hidden=(bits&4)!=0;
        s.observe(id,known,dead,true,hidden);
        CHECK5(s.visible==(known&&dead&&!hidden)); CHECK5(s.refuses_hide()==(!known||dead));
    }
    State pending;pending.observe(id,true,false,true,true);
    CHECK5(!pending.confirmed && !pending.visible && !pending.needs_show()); // alive-pending is never shown as a corpse
    for(unsigned field=0;field<3;++field){
        State s; auto unknown=id; if(field==0)unknown.entity=0;if(field==1)unknown.soul=0;if(field==2)unknown.world=0;
        s.observe(unknown,true,true,true,false);CHECK5(!s.confirmed&&!s.visible&&!s.needs_show()&&s.refuses_hide());
    }
    blocked=uint64_t(INT64_MAX);body.observe(id,true,true,true,false);CHECK5(!hide()&&blocked==uint64_t(INT64_MAX));
    int shows=0, reads=0; State view; view.observe(id,true,false,true,true);
    auto write=[&]{++shows;return true;};
    auto verify=[&]{++reads;view.observe(id,true,true,true,false);};
    CHECK5(!reveal(view,write,verify)&&shows==0&&reads==0);
    view.observe(id,true,true,true,true); CHECK5(reveal(view,write,verify)&&shows==1&&reads==1&&view.visible);
    CHECK5(reveal(view,write,verify)&&shows==1&&reads==1); // already observed visible: no repeated writer
    view.observe(id,true,true,true,true);
    CHECK5(!reveal(view,[]{return false;},verify)&&reads==1&&!view.visible);
    CHECK5(!reveal(view,write,[&]{++reads;view.observe(id,false,false,false,false);})&&!view.visible);
    auto replacement=id;++replacement.entity;
    CHECK5(!reveal(view,write,[&]{view.observe(replacement,true,false,true,false);})&&!view.confirmed&&!view.visible);
    if(passed)*passed=pass;return fail;
}
