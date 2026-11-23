// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#include "wo201.h"
#include "p6_capture.h"
#include "p6_rules.h"
#include "ranged_aim.h"
#include <array>
#include <cstdio>
#include <cstring>
namespace {
uint64_t clockMs=1000,gen=1,life=11;
bool playable=true,hasActor=true,hasSoul=true,changeDuringHand=false,changedBody=false;
bool chargeReadable=false;
float playerCharge=0;
bool viewReadable = false, changeDuringView = false;
bool viewEngineReady = true, viewResolved = true;
unsigned viewReads = 0, viewPrepares = 0;
float viewForward[3]{}, cameraPos[3]{};
uint8_t phase=0,known=3,left=0x11,right=0;
int actorObject=0,otherActor=0,soulObject=0,passedCount=0,failedCount=0;
#define C6(c,label) do{if(c)++passedCount;else{++failedCount;std::printf("FAIL P6 capture %d: %s\n",__LINE__,label);}}while(false)
uint64_t now(){return clockMs;}uint64_t generation(){return gen;}uint64_t epoch(){return life;}bool gameplay(){return playable;}
void* actor(uint32_t* eid){*eid=hasActor?44:0;return hasActor?(changedBody?&otherActor:&actorObject):nullptr;}
void* soul(){return hasSoul?&soulObject:nullptr;}
uint8_t readPhase(void*){return phase;}
bool readCharge(void*,float* charge,float* min,float* max){*charge=playerCharge;*min=0;*max=1;return chargeReadable;}
bool readView(uint32_t eid, float matrix[12], float feet[3]) {
    ++viewReads;
    if (changeDuringView) ++life;
    for (int row = 0; row < 3; ++row) { matrix[row * 4 + 1] = viewForward[row]; matrix[row * 4 + 3] = cameraPos[row]; feet[row] = 0; }
    return eid == 44 && viewReadable && viewResolved;
}
bool prepareView() { ++viewPrepares; return viewResolved = viewEngineReady; }
uint8_t hands(uint32_t,uint8_t* l,uint8_t* r){std::memset(l,0,16);std::memset(r,0,16);l[0]=left;r[0]=right;if(changeDuringHand)++life;return known;}
struct Reply {uint8_t code=255;size_t size=0;std::array<uint8_t,512> b{};};
Reply poll(bool on=true,uint64_t expected=0){uint8_t b[11]={86,2,uint8_t(on?1:0)};kcdmp::p6rules::put64(b+3,expected?expected:gen);Reply r;r.code=kcdmp::wo201ranged::handle(b,sizeof b,r.b.data(),r.b.size(),&r.size);return r;}
void step(uint8_t p,uint64_t ms=50){phase=p;clockMs+=ms;kcdmp::wo201ranged::tick();}
void reset(){clockMs=1000;gen=1;life=11;playable=hasActor=hasSoul=true;changeDuringHand=changedBody=chargeReadable=false;playerCharge=0;phase=0;known=3;left=0x11;right=0;viewReads=viewPrepares=0;viewReadable=changeDuringView=false;viewEngineReady=viewResolved=true;viewForward[0]=viewForward[2]=cameraPos[0]=cameraPos[1]=0;viewForward[1]=1;cameraPos[2]=1.6f;kcdmp::wo201ranged::set_test_backend({now,generation,epoch,gameplay,actor,soul,readPhase,hands,readCharge,readView,prepareView});kcdmp::wo201ranged::reset_for_tests();C6(poll().code==0,"current gameplay enables native v2 capture");}
}
namespace kcdmp::wo201ranged {void test_trace(const char*,...){} }
int p6_capture_flow_tests(int* passed){
    using namespace kcdmp::wo201ranged;using namespace kcdmp::p6rules;
    passedCount=failedCount=0;
    reset();step(1);step(2);step(3);left=0x22;step(4);
    auto r=poll();C6(r.code==0&&r.size==18+4*48&&r.b[1]==4,"actual tick and poll emits1,2,3,4 in one typed batch");
    C6(r.b[18]==1&&r.b[66]==2&&r.b[114]==3&&r.b[162]==4,"actual phase FIFO");
    C6(r.b[18+16]==0x11&&r.b[114+16]==0x11&&r.b[162+16]==0x22,"queued Bow Fire is never relabeled after hand changes to Crossbow");
    C6(r.b[19]==1&&r.b[20]==3&&u64(r.b.data()+2)==gen,"strict native source and known mask are carried");
    reset();step(3);left=0x22;step(3);r=poll();C6(r.b[1]==1&&r.b[18+16]==0x11,"same firing clip plus later hand change cannot invent a second shot or relabel the first");
    reset();known=0;left=right=0;step(3);known=3;left=0x44;r=poll();
    C6(r.b[18+1]==0&&r.b[18+2]==0&&r.b[18+16]==0,"unreadable capture stays unknown despite fresh hand before poll");
    uint64_t counts[6]{};t3_counts(counts);C6(counts[2]==1,"unknown hand diagnostic counts actual captured phase");
    reset();step(1,600);step(2,600);step(3,600);r=poll();C6(r.b[1]==3,"long but stable gameplay frames do not restart an endless5second settle");
    reset();hasActor=false;step(1);t3_counts(counts);C6(counts[0]==1&&poll().b[1]==0,"missing actor is distinct from a proved none phase");
    hasActor=true;phase=255;clockMs+=50;tick();t3_counts(counts);C6(counts[1]==1,"unreadable phase has separate diagnostic and no idle fabrication");
    reset();step(1);++life;r=poll();C6(r.b[1]==0,"life change drops old queued phase before drain");t3_counts(counts);C6(counts[5]==1,"life drop is visible in cached lifecycle counter");
    reset();step(3);clockMs+=2001;r=poll();C6(r.b[1]==0,"old capture is expired rather than restamped at poll");t3_counts(counts);C6(counts[4]==1,"expiry counter counts discarded row");
    reset();const auto firstEpoch=u64(poll().b.data()+10);for(unsigned i=0;i<33;++i)step(uint8_t(i%2+1));r=poll();t3_counts(counts);
    C6(r.b[1]==0&&u64(r.b.data()+10)!=firstEpoch&&counts[3]==1,"overflow drops an unsafe sequence and changes only visual stream epoch");
    step(3);r=poll();C6(r.b[1]==1&&r.b[18]==3,"next real phase resynchronizes after overflow");C6(life==11,"visual overflow does not mutate contact or player lifecycle epoch");
    reset();for(unsigned i=0;i<25;++i)step(uint8_t(i%2+1));auto a=poll(),b=poll(),c=poll();
    C6(a.size==498&&a.b[1]==10&&b.size==498&&b.b[1]==10&&c.b[1]==5,"Queue32 drains whole48byte rows in <=512byte pages");
    C6(u32(a.b.data()+18+4)==1&&u32(b.b.data()+18+4)==11&&u32(c.b.data()+18+4)==21,"paged sequence stays ordered without duplicates");
    reset();step(1);const auto beforeOff=u64(poll().b.data()+10);step(3);r=poll(false);C6(r.b[1]==0&&u64(r.b.data()+10)!=beforeOff,"visual disable clears queue and resets only its stream");
    C6(life==11,"visual disable leaves independent contact lifecycle unchanged");
    reset();step(1);C6(poll(false,gen+1).code!=0,"stale generation disable changes no capture config");step(2);r=poll();C6(r.b[1]==2,"valid capture still runs after refused stale disable");
    reset();changeDuringHand=true;step(3);r=poll();C6(r.b[1]==0,"life change during engine hand read cannot publish stale capture");
    reset();step(3);playable=false;r=poll();C6(r.code!=0,"no gameplay proof cannot drain old capture");
    on_pipe_closed();playable=true;tick();r=poll();C6(r.b[1]==0,"pipe close invalidates prior presentation queue");
    namespace aim = kcdmp::rangedaim;
    reset();step(aim::Charging);step(aim::Unreadable);
    C6(sampled_aim(&actorObject).observed && sampled_aim(&actorObject).aim,
       "unreadable step on same body keeps the last draw");
    changedBody=true;step(aim::Unreadable);
    C6(!sampled_aim(&otherActor).observed&&!sampled_aim(&otherActor).aim&&poll().b[1]==0,
       "an unreadable replacement actor cannot publish the retired body's draw or an unconfirmed end");
    reset();for(unsigned i=0;i<33;++i)step(uint8_t(i%2+1));
    C6(sampled_aim(&actorObject).aim,"event queue overflow is not a confirmed draw end");
    reset();step(aim::Firing);changedBody=true;step(aim::Firing);r=poll();
    C6(r.b[1]==0,"a replacement discards queued fire and does not restart its inherited firing phase");
    step(aim::None);step(aim::Firing);r=poll();
    C6(r.b[1]==2 && r.b[18]==aim::None && r.b[66]==aim::Firing,"a new shot after replacement and confirmed idle is captured once");
    reset();chargeReadable=true;playerCharge=0;step(aim::Charging);
    C6(sampled_aim(&actorObject).charge==0,"zero is exported when actually measured");
    playerCharge=.75f;step(aim::Charging,25);
    C6(sampled_aim(&actorObject).charge==0,"measurement cadence remains 50 ms");
    step(aim::Charging,25);
    C6(sampled_aim(&actorObject).charge==191&&poll().b[1]==1,"in-phase measured charge updates without queuing a second phase");
    chargeReadable=false;step(aim::Charging);step(aim::Aiming);
    C6(sampled_aim(&actorObject).charge==191,"unreadable charge and subsequent aim retain the measured value for this draw");
    step(aim::Uncharging);
    r=poll();
    C6(sampled_aim(&actorObject).observed&&!sampled_aim(&actorObject).aim&&!sampled_aim(&actorObject).charge&&
       r.b[1]==2&&r.b[18]==aim::Aiming&&r.b[66]==aim::Uncharging,"abort clears the draw and has no Fire phase");

    reset(); viewReadable = true; step(aim::None);
    C6(viewReads == 0, "camera reader stays idle outside a held phase");
    step(aim::Charging);
    auto view = sampled_aim(&actorObject).angles;
    C6(viewReads == 1 && view.known() && view.yaw == 0 && view.pitch == 0,
       "the held production sampler measures a true zero in the local first-person view");
    viewForward[0] = -.8660254f; viewForward[1] = 0; viewForward[2] = .5f;
    step(aim::Charging, 25);
    C6(viewReads == 1 && sampled_aim(&actorObject).angles.yaw == 0, "aim has the same 50 ms sampling cadence");
    step(aim::Charging, 25);
    view = sampled_aim(&actorObject).angles;
    C6(viewReads == 2 && view.yaw == 9000 && view.pitch == 3000 && poll().b[1] == 2,
       "in-phase camera changes publish yaw and pitch without a new reliable phase");
    cameraPos[0] = 2; viewForward[0] = .8660254f; step(aim::Aiming);
    C6(sampled_aim(&actorObject).angles.yaw == 9000, "a camera away from the player cannot replace the confirmed direction");
    changedBody = true; step(aim::Aiming);
    C6(!sampled_aim(&otherActor).angles.known(), "a replacement actor cannot inherit the retired body's direction");
    cameraPos[0] = 0; step(aim::Aiming);
    C6(sampled_aim(&otherActor).angles.yaw == -9000 && sampled_aim(&otherActor).angles.pitch == 3000,
       "the replacement can establish its own direction and height");
    step(aim::Uncharging);
    C6(!sampled_aim(&otherActor).angles.known() && viewReads == 5, "confirmed release drops the angle pair and does not read the camera");
    reset(); viewReadable = changeDuringView = true; step(aim::Aiming);
    C6(!sampled_aim(&actorObject).observed && !sampled_aim(&actorObject).angles.known(),
       "a lifecycle change during camera read cannot admit the old view");
    reset(); viewReadable = true; step(aim::Aiming);
    changeDuringView = true; step(aim::Aiming);
    C6(!sampled_aim(&actorObject).observed && !sampled_aim(&actorObject).angles.known(),
       "the same angles still retire if camera read crosses a lifecycle boundary");
    reset(); viewReadable = true; viewEngineReady = viewResolved = false;
    step(aim::None);
    C6(viewPrepares == 0, "late camera resolution does no work outside a held phase");
    step(aim::Aiming);
    C6(viewPrepares == 1 && viewReads == 0 && !sampled_aim(&actorObject).angles.known(),
       "a cold first held sample tries camera resolution and leaves its angles unknown");
    viewEngineReady = true;
    for (int i = 0; i < 9; ++i) step(aim::Aiming);
    C6(viewPrepares == 1 && viewReads == 0 && !sampled_aim(&actorObject).angles.known(),
       "late camera readiness retries at most once every 500 ms");
    viewForward[0] = -.8660254f; viewForward[1] = 0; viewForward[2] = .5f;
    step(aim::Aiming);
    view = sampled_aim(&actorObject).angles;
    C6(viewPrepares == 2 && viewReads == 1 && view.yaw == 9000 && view.pitch == 3000,
       "held native sampler recovers both camera angles without starting a dialogue");
    step(aim::Aiming); step(aim::Aiming);
    C6(viewPrepares == 2 && viewReads == 3 && poll().b[1] == 2,
       "successful late camera resolution stays prepared and adds no reliable shooting phase");
    if(passed)*passed=passedCount;
    return failedCount;
}
