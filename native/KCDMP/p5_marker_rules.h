// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#pragma once
#include <cstddef>
#include <cstdint>
#include <cmath>
namespace kcdmp::p5 {
struct Shared { void* p = nullptr; void* ctrl = nullptr; };
static_assert(sizeof(Shared) == 2 * sizeof(void*), "existing shared_ptr ABI prefix");
struct Ref { void* p = nullptr; void* ctrl = nullptr; uint64_t token = 0; };
struct Binding { void* map = nullptr; void* entity = nullptr; void* linkable = nullptr; uint32_t eid = 0; uint64_t wuid = 0, physical = 0; bool pending = false; };
struct Spec { int type = 0; unsigned picture = 0; const char* label = nullptr; };
enum class Presence : uint8_t { Unknown, Absent, Present };
enum class State : uint8_t { Empty, Waiting, Created, Added, Quarantined, Retired };
struct Handle { uint64_t token = 0; int type = 0; unsigned picture = 0; State state = State::Empty; Binding binding; Shared mark; bool death = false, mayReference = false, cleanupAttempted = false, ownershipKnown = false; };
// Backend operations are existing verified engine methods, each with a distinct
// static isolated fault Site. A Ref is an ownership token, not an Engine retain.
struct Backend {
    bool (*ready)() = nullptr;
    bool (*identify)(void*, Binding*) = nullptr;
    bool (*create)(const Binding&, const Spec&, Shared*) = nullptr;
    bool (*add)(const Binding&, const Spec&, const Shared&) = nullptr;
    Presence (*contains)(const Binding&, const Shared&) = nullptr;
    bool (*remove)(const Binding&, const Shared&) = nullptr;
    bool (*release)(Shared*) = nullptr;
};
inline bool detached(Handle& h, const Backend& b) {
    if (!h.mayReference) return true;
    const Presence before = b.contains(h.binding,h.mark);
    if (before == Presence::Absent) { h.mayReference = false; return true; }
    if (before == Presence::Unknown || h.cleanupAttempted) return false;
    h.cleanupAttempted = true;
    // Success means exact absence after the call, never merely a void return.
    b.remove(h.binding,h.mark);
    if (b.contains(h.binding,h.mark) != Presence::Absent) return false;
    h.mayReference = false; return true;
}
inline bool remove_handle(Handle& h, const Backend& b) {
    if (h.state == State::Empty || h.state == State::Retired) return true;
    if (!detached(h,b)) { h.state = State::Quarantined; return false; }
    // Once absent from the map it is safe to retire this ownership token. A
    // failed destructor is never retried (unknown partial refcount consumption).
    if (h.ownershipKnown && h.mark.ctrl) b.release(&h.mark);
    h.state = State::Retired; return true;
}
inline bool add_handle(Handle& h, const Backend& b, void* entity, const Spec& spec) {
    if (h.state != State::Empty && h.state != State::Waiting) return h.state == State::Added;
    if (!b.ready()) return false;
    if (!b.identify(entity,&h.binding)) { h.state = h.binding.pending ? State::Waiting : State::Quarantined; return false; }
    if (!b.create(h.binding,spec,&h.mark) || !h.mark.p || !h.mark.ctrl) { h.state = State::Quarantined; return false; }
    h.ownershipKnown = true; h.state = State::Created; h.mayReference = true; // Add can mutate before it faults.
    const bool added = b.add(h.binding,spec,h.mark);
    if (!added || b.contains(h.binding,h.mark) != Presence::Present) {
        if (!remove_handle(h,b)) h.state = State::Quarantined;
        else h.state = State::Retired; // failed Add is terminal, not a timer retry
        return false;
    }
    h.state = State::Added; return true;
}
inline bool finite3(const float* p) { return p && std::isfinite(p[0]) && std::isfinite(p[1]) && std::isfinite(p[2]); }
inline uint64_t u64(const uint8_t* p) { uint64_t v=0;for(unsigned i=0;i<8;++i)v|=uint64_t(p[i])<<(8*i);return v; }
inline uint32_t u32(const uint8_t* p) { uint32_t v=0;for(unsigned i=0;i<4;++i)v|=uint32_t(p[i])<<(8*i);return v; }
inline void put64(uint8_t* p,uint64_t v){for(unsigned i=0;i<8;++i)p[i]=uint8_t(v>>(8*i));}
inline void put32(uint8_t* p,uint32_t v){for(unsigned i=0;i<4;++i)p[i]=uint8_t(v>>(8*i));}
} // namespace kcdmp::p5
