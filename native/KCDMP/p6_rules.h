// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#pragma once
#include <array>
#include <vector>
#include <deque>
#include <cstdint>
#include <cstddef>
#include <cstring>
#include <cmath>
#include <algorithm>
namespace kcdmp::p6rules {
inline uint64_t u64(const uint8_t* p){uint64_t v=0;for(unsigned i=0;i<8;++i)v|=uint64_t(p[i])<<(i*8);return v;}
inline uint32_t u32(const uint8_t* p){uint32_t v=0;for(unsigned i=0;i<4;++i)v|=uint32_t(p[i])<<(i*8);return v;}
inline void put64(uint8_t* p,uint64_t v){for(unsigned i=0;i<8;++i)p[i]=uint8_t(v>>(8*i));}
inline void put32(uint8_t* p,uint32_t v){for(unsigned i=0;i<4;++i)p[i]=uint8_t(v>>(8*i));}
constexpr size_t kHeader=18,kRowBytes=48,kQueueMax=32,kRowsPerReply=10,kJournalMax=4096;
inline bool figure_guard(bool missile,bool meleeGuard){return missile||meleeGuard;}
inline bool figure_relayed(bool missile,bool ledgerMatch){return !missile&&ledgerMatch;}
constexpr uint64_t kRowTtlMs=2000,kContactMs=600,kHostDeadlineMs=2000;
struct Capture { uint8_t phase=0,source=0,known=0;uint32_t sequence=0;uint64_t tick=0;std::array<uint8_t,16> left{},right{}; };
inline bool class_empty(const std::array<uint8_t,16>& b){return std::all_of(b.begin(),b.end(),[](uint8_t v){return v==0;});}
inline void encode(const Capture& r,uint8_t* out){out[0]=r.phase;out[1]=r.source;out[2]=r.known;out[3]=0;put32(out+4,r.sequence);put64(out+8,r.tick);std::memcpy(out+16,r.left.data(),16);std::memcpy(out+32,r.right.data(),16);}
struct Queue {
    std::deque<Capture> rows;uint64_t generation=0,epoch=0;uint32_t sequence=0;
    uint64_t overflow=0,expired=0,dropped=0;
    void reset(uint64_t gen,uint64_t life){dropped+=rows.size();rows.clear();generation=gen;epoch=life;}
    bool push(Capture row){
        if(rows.size()>=kQueueMax){++overflow;dropped+=rows.size();rows.clear();return false;}
        row.sequence=++sequence;if(!row.sequence)row.sequence=++sequence;
        rows.push_back(row);return true;
    }
    size_t drain(uint64_t now,uint8_t* out,size_t cap){
        if(cap<kHeader)return 0;
        while(!rows.empty()&&(rows.front().tick>now||now-rows.front().tick>kRowTtlMs)){rows.pop_front();++expired;}
        const size_t count=(std::min)({rows.size(),kRowsPerReply,(cap-kHeader)/kRowBytes});
        out[0]=2;out[1]=uint8_t(count);put64(out+2,generation);put64(out+10,epoch);
        for(size_t i=0;i<count;++i){encode(rows.front(),out+kHeader+i*kRowBytes);rows.pop_front();}
        return kHeader+count*kRowBytes;
    }
};
enum class Status:uint8_t{Accepted=0,Scope=1,Victim=2,Arranged=3,Invalid=4,Uncertain=5,Capacity=6,Conflict=7,Expired=8};
struct Key {
    uint32_t world=0;uint8_t peer=0;uint64_t generation=0,epoch=0,id=0;
    bool operator==(const Key& b)const{return world==b.world&&peer==b.peer&&generation==b.generation&&epoch==b.epoch&&id==b.id;}
    bool valid()const{return world&&generation&&epoch&&id&&peer<16;}
};
using Ack=std::array<uint8_t,40>;
inline Ack ack(const Key& k,Status s,uint8_t steps=0,float hp=0,float st=0){Ack a{};a[0]=1;a[1]=uint8_t(s);a[2]=steps;a[3]=k.peer;put32(a.data()+4,k.world);put64(a.data()+8,k.generation);put64(a.data()+16,k.epoch);put64(a.data()+24,k.id);std::memcpy(a.data()+32,&hp,4);std::memcpy(a.data()+36,&st,4);return a;}
inline bool measured(float hp,float st){return std::isfinite(hp)&&std::isfinite(st)&&hp>=0&&st>=0&&hp<=999.9f&&st<=999.9f&&(hp>0||st>0);}
inline bool deadline_valid(uint64_t now,uint64_t deadline){return deadline>=now&&deadline-now<=kHostDeadlineMs;}
struct Entry {Key key;std::vector<uint8_t> fact;uint64_t deadline=0;Ack result{};bool final=false;};
struct Journal {
    std::deque<Entry> entries;
    Entry* find(const Key& k){for(auto& e:entries)if(e.key==k)return &e;return nullptr;}
    Entry* reserve(const Key& k,const std::vector<uint8_t>& f,uint64_t deadline){
        if(entries.size()>=kJournalMax)return nullptr;
        Entry e;e.key=k;e.fact=f;e.deadline=deadline;e.result=ack(k,Status::Uncertain);entries.push_back(e);return &entries.back();
    }
    // Reservation has an uncertain final result before any side effect. The
    // caller may replace it once with the actual outcome, never apply again.
    void begin(Entry& e){e.final=true;}
};
struct Measure {
    uint64_t captured=0;float hp0=0,st0=0;bool ambiguous=false,done=false;
    // 0 waiting, 1 actual positive measured delta, 2 refusal/expired.
    int sample(uint64_t now,bool identity,bool read,float hp,float st,float* dh,float* ds){
        if(done)return 2;
        if(ambiguous||!identity||!read||!std::isfinite(hp0)||!std::isfinite(st0)||hp0<0||st0<0||now<captured||now-captured>kContactMs||!std::isfinite(hp)||!std::isfinite(st)||hp<0||st<0){done=true;return 2;}
        *dh=(std::max)(0.0f,hp0-hp);*ds=(std::max)(0.0f,st0-st);
        if(*dh>0.01f||*ds>0.25f){done=true;return measured(*dh,*ds)?1:2;}
        return 0;
    }
};
} // namespace kcdmp::p6rules
