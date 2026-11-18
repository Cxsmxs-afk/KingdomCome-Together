// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#pragma once
#include "p6_rules.h"
namespace kcdmp::p6 {
struct Scope { uint64_t generation=0,life=0,physical=0;uint32_t world=0,player=0;uint8_t role=0;void* actor=nullptr;void* soul=nullptr;bool ready=false,alive=false; };
struct Victim { uint32_t eid=0;uint64_t wuid=0;void* entity=nullptr;void* soul=nullptr;void* combat=nullptr;std::array<uint8_t,16> guid{};bool avatar=false,dead=false; };
// All engine-affine adapter calls run only on the proven main thread.
struct Backend {
    uint64_t(*now)()=nullptr;bool(*main)()=nullptr;Scope(*scope)()=nullptr;
    bool(*victim)(uint32_t,Victim*)=nullptr;bool(*same)(const Victim&)=nullptr;
    bool(*vitals)(void*,float*,float*)=nullptr;bool(*arranged)(uint32_t)=nullptr;
    uint32_t(*named)(const char*)=nullptr;bool(*avatar)(uint8_t,uint32_t)=nullptr;
    bool(*apply)(const uint8_t*,size_t,uint8_t*)=nullptr;
};
void set_backend(const Backend&);
using EventFn=void(*)(const uint8_t*,size_t);
void set_event(EventFn);
void connection(bool connected);
// Always call the engine's original cause producer, regardless of this observer.
bool contact(bool missile,uint32_t attacker,uint32_t victim,void* combatSoul,bool knownLocal=false);
void tick();
bool suppress_sample(void* soul,const unsigned char guid[16]);
bool filter_sample(void* soul,const unsigned char guid[16],float* hp,float* st);
void remote_follow(const unsigned char guid[16]);
void remote_follow_eid(uint32_t eid);
uint64_t capture_epoch();
uint8_t measured_apply(const uint8_t*,size_t,uint8_t*,size_t,size_t*);
#ifdef KCDMP_P6_TEST
void reset_for_tests();
#endif
}
