// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#pragma once
#include "p5_marker_rules.h"
namespace kcdmp::p5 {
void set_backend(const Backend& backend);
bool render_add(void* entity, Ref* out, int type, unsigned picture = 0, const char* label = nullptr, bool death = false);
bool render_remove(Ref* ref);
bool detach_all();
bool may_delete_source(void* entity);
bool suspended();
void set_suspended(bool on);
void physical_world_changed();
uint64_t physical_world();
bool loaded_scope(uint32_t* world); // cached real loaded-world receipt, main-thread only
void counts(uint64_t out[3]);
struct Fact { uint8_t kind=0; uint64_t id=0, revision=0; float xyz[3]{}; };
using EventFn = void(*)(const uint8_t*,size_t);
void set_event(EventFn fn);
void own_death(uint64_t id,bool grave,void* source,const float xyz[3],Ref* existing = nullptr);
bool own_removed(uint64_t id);
void own_grave_loaded(uint64_t id,void* entity,const float xyz[3],Ref* existing);
void tick();
uint8_t list(const uint8_t* body,size_t len,uint8_t* out,size_t cap,size_t* used);
uint8_t command(const uint8_t* body,size_t len,uint8_t* out,size_t cap,size_t* used);
// Engine adapters supplied by respawn_actions.cpp, all main thread.
void* fallback_anchor(uint64_t id,const float xyz[3],bool grave);
bool source_identity(void* entity, Binding* out);
bool release_anchor(const Binding& source);
bool source_alive(const Binding& source);
void poll_world();
} // namespace kcdmp::p5
