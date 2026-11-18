// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// Exercise the production queue, receipt ledger and contact measurement rules.
#include "p6_rules.h"
#include <cstdio>
#include <limits>
namespace {
int good=0,bad=0;
#define PCHECK(c,label) do{if(c)++good;else{++bad;std::printf("FAIL P6 rules line %d: %s\n",__LINE__,label);}}while(false)
}
int p6_rules_tests(int* passed){
    using namespace kcdmp::p6rules;good=bad=0;
    PCHECK(figure_guard(true,false),"remote figure missile stays guarded when the old melee guard switch is off");
    PCHECK(figure_guard(true,true),"remote figure missile stays guarded with the melee guard on");
    PCHECK(!figure_guard(false,false)&&figure_guard(false,true),"melee figure guard retains its existing on/off policy");
    PCHECK(!figure_relayed(true,true)&&!figure_relayed(true,false),"missile never inherits authority from a matching or missing melee ledger");
    PCHECK(figure_relayed(false,true)&&!figure_relayed(false,false),"matching relayed melee remains authorized and unmatched melee does not");
    Capture bow;bow.phase=3;bow.source=1;bow.known=3;bow.tick=1000;bow.right[0]=9;
    Queue q;q.reset(21,42);PCHECK(q.push(bow),"actual native phase can enter the bounded queue");
    bow.phase=4;bow.right[0]=10;
    PCHECK(q.rows.front().phase==3&&q.rows.front().right[0]==9,"capture owns its original phase/class even when the source hand changes");
    Capture unknown;unknown.phase=2;unknown.tick=1001;
    PCHECK(q.push(unknown),"unknown hand observation stays an explicit captured row");
    uint8_t reply[512]{};size_t n=q.drain(1001,reply,sizeof reply);
    PCHECK(n==kHeader+2*kRowBytes&&reply[0]==2&&reply[1]==2&&u64(reply+2)==21&&u64(reply+10)==42,"actual wire header preserves generation/epoch and exact record count");
    const auto* first=reply+kHeader;
    PCHECK(first[0]==3&&first[1]==1&&first[2]==3&&first[3]==0&&u32(first+4)==1&&u64(first+8)==1000&&first[32]==9,"48-byte row preserves capture phase/source/known/sequence/time and right-hand class");
    PCHECK(reply[kHeader+kRowBytes+1]==0&&reply[kHeader+kRowBytes+2]==0&&reply[kHeader+kRowBytes+32]==0,"later known class never relabels queued unknown hand data");
    PCHECK(q.rows.empty(),"drain removes exactly serialized rows");
    for(unsigned i=0;i<32;++i){bow.tick=1000+i;PCHECK(q.push(bow),"queue accepts each of its32 bounded records");}
    n=q.drain(1032,reply,sizeof reply);
    PCHECK(n==498&&reply[1]==10&&n+4<=512&&q.rows.size()==22,"ten full records plus generic reply remain inside512 bytes; remainder stays FIFO");
    PCHECK(u32(reply+kHeader+4)==3&&u32(reply+kHeader+9*kRowBytes+4)==12,"first page preserves monotonically assigned FIFO sequence");
    n=q.drain(1032,reply,kHeader+kRowBytes);
    PCHECK(n==kHeader+kRowBytes&&reply[1]==1&&u32(reply+kHeader+4)==13&&q.rows.size()==21,"small capacity emits one complete next record without splitting");
    const auto pending=q.rows.size();PCHECK(q.drain(1032,reply,kHeader-1)==0&&q.rows.size()==pending,"short header buffer never consumes records");
    PCHECK(q.drain(1032,reply,kHeader)==kHeader&&reply[1]==0&&q.rows.size()==pending,"header-only poll never consumes a partial record");
    q.reset(22,43);PCHECK(q.rows.empty()&&q.generation==22&&q.epoch==43&&q.dropped==pending,"generation/life reset retires every queued old capture");
    bow.tick=2000;for(unsigned i=0;i<32;++i)q.push(bow);
    PCHECK(!q.push(bow)&&q.rows.empty()&&q.overflow==1,"overflow explicitly fails and discards the old partial sequence");
    PCHECK(q.push(bow)&&q.rows.size()==1,"following capture can begin a fresh producer sequence");
    q.reset(23,44);bow.tick=1000;q.push(bow);bow.tick=1001;q.push(bow);
    n=q.drain(3001,reply,sizeof reply);
    PCHECK(reply[1]==1&&u64(reply+kHeader+8)==1001&&q.expired==1,"TTL retires an overage head while retaining exact2000ms boundary");
    q.reset(24,45);bow.tick=5000;q.push(bow);n=q.drain(4999,reply,sizeof reply);
    PCHECK(n==kHeader&&reply[1]==0&&q.expired==2,"future-clock row is refused rather than made fresh");
    q.sequence=(std::numeric_limits<uint32_t>::max)();q.push(bow);
    PCHECK(q.rows.front().sequence==1,"sequence rollover never emits zero");

    Key key{7,0,8,9,10};PCHECK(key.valid(),"authenticated host peer0 is a valid receipt owner");
    Key other=key;other.peer=15;PCHECK(other.valid(),"last supported peer15 is valid");
    other.peer=16;PCHECK(!other.valid(),"peer16 is outside the native receipt owner range");
    other=key;other.world=0;PCHECK(!other.valid(),"world0 cannot own a host receipt");
    other=key;other.generation=0;PCHECK(!other.valid(),"generation0 cannot own a host receipt");
    other=key;other.epoch=0;PCHECK(!other.valid(),"epoch0 cannot own a host receipt");
    other=key;other.id=0;PCHECK(!other.valid(),"contact0 cannot own a host receipt");
    const auto a=ack(key,Status::Accepted,5,12.5f,2.25f);
    float hp=0,st=0;std::memcpy(&hp,a.data()+32,4);std::memcpy(&st,a.data()+36,4);
    PCHECK(a[0]==1&&a[1]==0&&a[2]==5&&a[3]==0&&u32(a.data()+4)==7&&u64(a.data()+8)==8&&u64(a.data()+16)==9&&u64(a.data()+24)==10&&hp==12.5f&&st==2.25f,"typed40-byte ACK preserves complete receipt identity and actual applied values");
    Journal journal;const std::vector<uint8_t> fact{7,1,2,3};auto* entry=journal.reserve(key,fact,3000);
    PCHECK(entry&&!entry->final&&entry->result[1]==uint8_t(Status::Uncertain)&&entry->fact==fact,"reservation precedes side effects with an uncertain safe retry outcome");
    journal.begin(*entry);PCHECK(entry->final,"begin reserves terminal ownership before a physical write");
    for(uint64_t i=1;i<kJournalMax;++i){auto k=key;k.id+=i;journal.reserve(k,fact,3000);}
    auto lastKey=key;lastKey.id+=kJournalMax;
    PCHECK(!journal.reserve(lastKey,fact,3000)&&journal.entries.size()==kJournalMax,"full receipt journal refuses rather than evicting old exactly-once facts");
    PCHECK(journal.find(key)&&journal.find(key)->final&&journal.find(key)->fact==fact,"old terminal receipt survives capacity pressure");
    for(unsigned field=0;field<5;++field){auto k=key;if(field==0)++k.world;if(field==1)++k.peer;if(field==2)++k.generation;if(field==3)++k.epoch;if(field==4)k.id=99999;PCHECK(journal.find(k)==nullptr,"world/peer/source-generation/epoch/contact form distinct receipt identity");}

    PCHECK(measured(0,1)&&measured(1,0)&&!measured(0,0)&&!measured(-1,1),"only finite nonnegative positive measured deltas qualify");
    PCHECK(!measured((std::numeric_limits<float>::quiet_NaN)(),1)&&!measured(1,(std::numeric_limits<float>::infinity)())&&!measured(1000,1),"NaN/infinite/excess measured values are refused");
    PCHECK(deadline_valid(1000,1000)&&deadline_valid(1000,3000)&&!deadline_valid(1000,3001)&&!deadline_valid(1000,999),"host deadline never freshens expired or overbudget work");
    float dh=-1,ds=-1;Measure m{1000,100,200};
    PCHECK(m.sample(1001,true,true,100,200,&dh,&ds)==0&&!m.done&&dh==0&&ds==0,"real contact waits while neither measured value changed");
    PCHECK(m.sample(1600,true,true,95,198,&dh,&ds)==1&&m.done&&dh==5&&ds==2,"same identity measured within exact600ms produces one accepted delta");
    PCHECK(m.sample(1601,true,true,90,190,&dh,&ds)==2,"a completed contact cannot create a second measurement");
    Measure boundary{1000,100,200};PCHECK(boundary.sample(1001,true,true,100,199.75f,&dh,&ds)==0,"stamina noise at0.25 does not produce a hit");
    PCHECK(boundary.sample(1002,true,true,100,199.5f,&dh,&ds)==1&&dh==0&&ds==0.5f,"real positive stamina-only measurement remains supported");
    for(unsigned refusal=0;refusal<8;++refusal){Measure reject{1000,100,200};uint64_t now=1001;bool identity=true,read=true;float h=95,s=198;
        if(refusal==0)reject.ambiguous=true;if(refusal==1)identity=false;if(refusal==2)read=false;if(refusal==3)now=999;if(refusal==4)now=1601;
        if(refusal==5)h=(std::numeric_limits<float>::quiet_NaN)();if(refusal==6)s=(std::numeric_limits<float>::infinity)();if(refusal==7)h=-1;
        PCHECK(reject.sample(now,identity,read,h,s,&dh,&ds)==2&&reject.done,"ambiguous/replaced/unreadable/old/future/invalid contact retires without acceptance");}
    for(unsigned baseline=0;baseline<6;++baseline){Measure reject{1000,100,200};
        if(baseline==0)reject.hp0=(std::numeric_limits<float>::quiet_NaN)();if(baseline==1)reject.st0=(std::numeric_limits<float>::quiet_NaN)();
        if(baseline==2)reject.hp0=(std::numeric_limits<float>::infinity)();if(baseline==3)reject.st0=(std::numeric_limits<float>::infinity)();
        if(baseline==4)reject.hp0=-1;if(baseline==5)reject.st0=-1;
        PCHECK(reject.sample(1001,true,true,95,190,&dh,&ds)==2&&reject.done,"unreadable or invalid initial vitals never qualify through a positive other-field delta");}
    if(passed)*passed=good;return bad;
}
