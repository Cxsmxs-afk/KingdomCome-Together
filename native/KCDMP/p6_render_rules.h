// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#pragma once
#include <cstddef>
#include <cstdint>
#include <cstring>
namespace kcdmp::p6render {
struct Stamp {uint64_t generation=0,life=0,readTick=0;bool gameplay=false;};
inline bool text(const uint8_t* b,size_t n,const char* value){return std::strlen(value)==n&&std::memcmp(b,value,n)==0;}
inline bool ranged_shot(const uint8_t* b,size_t n){
    if(!b||n<2||b[0]!=4)return false;
    size_t at=1;
    const size_t name=b[at++];if(name>n-at||name<7||std::memcmp(b+at,"kcd2mp_",7)!=0)return false;at+=name;
    if(at>=n)return false;
    const size_t fragment=b[at++];if(fragment>n-at)return false;
    const bool shot=text(b+at,fragment,"Fire")||text(b+at,fragment,"Load")||text(b+at,fragment,"Uncharge");at+=fragment;
    if(!shot||at>=n)return false;
    const size_t tags=b[at++];if(tags>n-at||n-at-tags!=9)return false;
    size_t first=0;
    for(size_t i=0;i<=tags;++i)if(i==tags||b[at+i]=='+'){
        if(text(b+at+first,i-first,"l_bow")||text(b+at+first,i-first,"l_crossbow"))return true;
        first=i+1;
    }
    return false;
}
inline bool allowed(const Stamp& s,uint64_t now,uint64_t gen,uint64_t life,bool gameplay){
    // An equal tick cannot prove ordering relative to a life edge. Initial
    // life=0 means no edge observed yet; explicit nonzero P1Gameplay is still required.
    return s.generation&&s.gameplay&&gameplay&&gen==s.generation&&life==s.life&&s.readTick>s.life&&now>=s.readTick&&now-s.readTick<=2000;
}
template<class Invoke>
bool invoke(const uint8_t* body,size_t n,const Stamp& s,uint64_t now,uint64_t gen,uint64_t life,bool gameplay,Invoke&& call){
    if(ranged_shot(body,n)&&!allowed(s,now,gen,life,gameplay))return false;
    call();return true;
}
}
