// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
#pragma once
#include <cstdint>
namespace kcdmp::p5corpse {
struct Identity {
    uintptr_t entity = 0, soul = 0; uint64_t wuid = 0, world = 0;
    bool valid() const { return entity && soul && world; }
    bool same(const Identity& other) const { return valid() && other.valid() && entity == other.entity && soul == other.soul && wuid == other.wuid && world == other.world; }
};
struct State {
    Identity identity;
    bool confirmed = false, lifeKnown = false, visible = false;
    void observe(Identity current, bool known, bool dead, bool visibilityKnown, bool hidden) {
        if (!identity.same(current)) { confirmed = false; visible = false; }
        identity = current;
        lifeKnown = current.valid() && known;
        if (lifeKnown) confirmed = dead; // a real alive observation retires this instance's old life
        visible = lifeKnown && dead && visibilityKnown && !hidden;
    }
    bool refuses_hide() const { return !lifeKnown || confirmed; }
    bool needs_show() const { return identity.valid() && confirmed; }
};
// The same final decision is used immediately before the native Hide writer.
// Unknown is kept visible without manufacturing a death fact.
template<class Write>
bool hide(State& state, Write&& write, uint64_t& corpseBlocked) {
    if (state.refuses_hide()) { if (state.confirmed && corpseBlocked < uint64_t(INT64_MAX)) ++corpseBlocked; return false; }
    write(); return true;
}
// Show is credited only by a fresh visibility/life/identity readback.
template<class Write, class Verify>
bool reveal(State& state, Write&& write, Verify&& verify) {
    if (!state.needs_show()) return false;
    if (state.visible) return true;
    if (!write()) return false;
    verify(); return state.visible;
}
} // namespace kcdmp::p5corpse
